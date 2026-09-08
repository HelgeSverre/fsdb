module Fsdb.GeometryOperations

open System
open System.Buffers.Binary
open NetTopologySuite.IO
open NetTopologySuite.Operation.Buffer
open NetTopologySuite.Operation.Overlay
open NetTopologySuite.Operation.OverlayNG
open Fsdb.Value

[<RequireQualifiedAccess>]
type internal OverlayKind =
    | Intersection
    | Union
    | Difference
    | SymmetricDifference

[<RequireQualifiedAccess>]
type internal BufferStrategy =
    | EndRound of pointsPerCircle: float
    | EndFlat
    | JoinRound of pointsPerCircle: float
    | JoinMiter of pointsPerCircle: float
    | PointCircle of pointsPerCircle: float
    | PointSquare

[<RequireQualifiedAccess>]
type internal BufferError =
    | InvalidStrategy
    | UnsupportedRoundResolution
    | OperationFailed of detail: string

let internal maxBufferPointsPerCircle = 65_536
let private defaultPointsPerCircle = 32.0
let private strategyCodeLength = sizeof<int32>
let private encodedStrategyLength = strategyCodeLength + sizeof<double>

let private strategyCodeAndPoints = function
    | BufferStrategy.EndRound points -> 1, points
    | BufferStrategy.EndFlat -> 2, 0.0
    | BufferStrategy.JoinRound points -> 3, points
    | BufferStrategy.JoinMiter points -> 4, points
    | BufferStrategy.PointCircle points -> 5, points
    | BufferStrategy.PointSquare -> 6, 0.0

let internal encodeBufferStrategy strategy =
    let code, points = strategyCodeAndPoints strategy
    let bytes = Array.zeroCreate<byte> encodedStrategyLength
    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, strategyCodeLength), code)
    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(strategyCodeLength, sizeof<double>), BitConverter.DoubleToInt64Bits points)
    bytes

let internal tryDecodeBufferStrategy (bytes: byte[]) =
    if bytes.Length <> encodedStrategyLength then
        None
    else
        let code = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, strategyCodeLength))

        let points =
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(strategyCodeLength, sizeof<double>))
            |> BitConverter.Int64BitsToDouble
        let validPoints = Double.IsFinite points && points > 0.0 && points <= float maxBufferPointsPerCircle

        match code with
        | 1 when validPoints -> Some(BufferStrategy.EndRound points)
        | 2 when points = 0.0 -> Some BufferStrategy.EndFlat
        | 3 when validPoints -> Some(BufferStrategy.JoinRound points)
        | 4 when validPoints -> Some(BufferStrategy.JoinMiter points)
        | 5 when validPoints -> Some(BufferStrategy.PointCircle points)
        | 6 when points = 0.0 -> Some BufferStrategy.PointSquare
        | _ -> None

let private toNts (geometry: Geometry) =
    let value = WKBReader().Read(geometryToWkb geometry)
    value.SRID <- geometry.Srid
    value

let private fromNts srid (geometry: NetTopologySuite.Geometries.Geometry) =
    if geometry.IsEmpty then
        Ok { Srid = srid; Shape = GEmpty }
    else
        match geometry |> WKBWriter().Write |> tryGeometryFromWkb srid with
        | Some result -> Ok result
        | None -> Error "the result cannot be represented as a MySQL geometry"

let private attempt srid operation =
    try
        operation () |> fromNts srid
    with
    | :? NetTopologySuite.Geometries.TopologyException as error -> Error error.Message
    | :? ParseException as error -> Error error.Message
    | :? ArgumentException as error -> Error error.Message

let internal overlay kind (first: Geometry) (second: Geometry) =
    let spatialFunction =
        match kind with
        | OverlayKind.Intersection -> SpatialFunction.Intersection
        | OverlayKind.Union -> SpatialFunction.Union
        | OverlayKind.Difference -> SpatialFunction.Difference
        | OverlayKind.SymmetricDifference -> SpatialFunction.SymDifference

    attempt first.Srid (fun () -> OverlayNGRobust.Overlay(toNts first, toNts second, spatialFunction))

type private BufferStrategyCategory =
    | End
    | Join
    | Point

let private strategyCategory = function
    | BufferStrategy.EndRound _
    | BufferStrategy.EndFlat -> End
    | BufferStrategy.JoinRound _
    | BufferStrategy.JoinMiter _ -> Join
    | BufferStrategy.PointCircle _
    | BufferStrategy.PointSquare -> Point

let rec private geometryContents = function
    | GEmpty -> false, false, false
    | GPoint _
    | GMultiPoint _ -> true, false, false
    | GLineString _
    | GMultiLineString _ -> false, true, false
    | GPolygon _
    | GMultiPolygon _ -> false, false, true
    | GGeometryCollection geometries ->
        geometries
        |> List.map (fun geometry -> geometryContents geometry.Shape)
        |> List.fold (fun (hasPoint, hasLine, hasArea) (point, line, area) -> hasPoint || point, hasLine || line, hasArea || area) (false, false, false)

let private roundSegments = function
    | BufferStrategy.EndRound points
    | BufferStrategy.JoinRound points
    | BufferStrategy.PointCircle points -> Some(max 3 (int points))
    | BufferStrategy.EndFlat
    | BufferStrategy.JoinMiter _
    | BufferStrategy.PointSquare -> None

let private configureBuffer geometry strategies =
    let categories = strategies |> List.map strategyCategory
    let hasPoint, hasLine, hasArea = geometryContents geometry.Shape
    let hasLinearOrArea = hasLine || hasArea

    let applicable =
        strategies
        |> List.forall (function
            | BufferStrategy.PointCircle _
            | BufferStrategy.PointSquare -> hasPoint
            | _ -> hasLinearOrArea)

    if not applicable || categories.Length <> (categories |> List.distinct |> List.length) then
        Error BufferError.InvalidStrategy
    else
        let tryFind category = strategies |> List.tryFind (strategyCategory >> (=) category)

        let pointStrategy = tryFind Point |> Option.defaultValue (BufferStrategy.PointCircle defaultPointsPerCircle)
        let joinStrategy = tryFind Join |> Option.defaultValue (BufferStrategy.JoinRound defaultPointsPerCircle)
        let endStrategy = tryFind End |> Option.defaultValue (BufferStrategy.EndRound defaultPointsPerCircle)

        let relevantRoundSegments =
            [ if hasPoint then roundSegments pointStrategy
              if hasLinearOrArea then roundSegments joinStrategy
              if hasLine then roundSegments endStrategy ]
            |> List.choose id

        let exactQuadrants =
            relevantRoundSegments
            |> List.map (fun segments -> segments / 4, segments % 4)

        match exactQuadrants |> List.map fst |> List.distinct, exactQuadrants |> List.forall (snd >> (=) 0) with
        | _ :: _ :: _, _
        | _, false -> Error BufferError.UnsupportedRoundResolution
        | quadrants, true ->
            let parameters = BufferParameters()
            parameters.QuadrantSegments <- quadrants |> List.tryHead |> Option.defaultValue 8

            parameters.EndCapStyle <-
                match endStrategy, pointStrategy with
                | BufferStrategy.EndFlat, _ -> EndCapStyle.Flat
                | _, BufferStrategy.PointSquare -> EndCapStyle.Square
                | _ -> EndCapStyle.Round

            parameters.JoinStyle <-
                match joinStrategy with
                | BufferStrategy.JoinMiter _ -> JoinStyle.Mitre
                | _ -> JoinStyle.Round

            Ok parameters

let internal buffer strategies distance (geometry: Geometry) =
    configureBuffer geometry strategies
    |> Result.bind (fun parameters ->
        attempt geometry.Srid (fun () -> BufferOp.Buffer(toNts geometry, distance, parameters))
        |> Result.mapError BufferError.OperationFailed)
