module Fsdb.GeometryOperations

open System
open System.Buffers.Binary
open NetTopologySuite.IO
open NetTopologySuite.Operation.Buffer
open NetTopologySuite.Operation.Overlay
open NetTopologySuite.Operation.OverlayNG
open Fsdb.SpatialReferenceSystems
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
    | OperationFailed of detail: string

let private defaultPointsPerCircle = 32.0
let private strategyCodeLength = sizeof<int32>
let private encodedStrategyLength = strategyCodeLength + sizeof<double>

let private radians degrees = degrees * Math.PI / 180.0

/// MySQL delegates geographic point distance to Boost.Geometry's first-order
/// Andoyer strategy; more exact geodesic formulae produce observably different
/// results and therefore are not interchangeable here.
let internal geographicPointDistance referenceSystem first second =
    match referenceSystem.SemiMajorAxis, referenceSystem.InverseFlattening with
    | Some semiMajorAxis, Some inverseFlattening ->
        let flattening = 1.0 / inverseFlattening
        let firstLatitude, firstLongitude = first
        let secondLatitude, secondLongitude = second
        let firstLatitude = radians firstLatitude
        let secondLatitude = radians secondLatitude
        let longitudeDelta = radians (secondLongitude - firstLongitude)
        let sinFirst, cosFirst = sin firstLatitude, cos firstLatitude
        let sinSecond, cosSecond = sin secondLatitude, cos secondLatitude

        let cosineDistance =
            sinFirst * sinSecond + cosFirst * cosSecond * cos longitudeDelta
            |> max -1.0
            |> min 1.0

        let angularDistance = acos cosineDistance
        let sineDistance = sin angularDistance
        let squaredDifference = (sinFirst - sinSecond) ** 2.0
        let squaredSum = (sinFirst + sinSecond) ** 2.0
        let oneMinusCosine = 1.0 - cosineDistance
        let onePlusCosine = 1.0 + cosineDistance

        let firstCorrection =
            if abs oneMinusCosine < 1e-15 then
                0.0
            else
                (angularDistance + 3.0 * sineDistance) / oneMinusCosine

        let secondCorrection =
            if abs onePlusCosine < 1e-15 then
                0.0
            else
                (angularDistance - 3.0 * sineDistance) / onePlusCosine

        let flatteningCorrection =
            -flattening / 4.0 * (firstCorrection * squaredDifference + secondCorrection * squaredSum)

        semiMajorAxis * (angularDistance + flatteningCorrection)
    | _ -> invalidArg (nameof referenceSystem) "a geographic reference system requires an ellipsoid"

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
        let positive = Double.IsFinite points && points > 0.0
        let validPoints = positive && points <= float Limits.maxPointsInGeometryLimit

        match code with
        | 1 when validPoints -> Some(BufferStrategy.EndRound points)
        | 2 when points = 0.0 -> Some BufferStrategy.EndFlat
        | 3 when validPoints -> Some(BufferStrategy.JoinRound points)
        | 4 when positive -> Some(BufferStrategy.JoinMiter points)
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

type private BufferConfiguration =
    { Point: BufferStrategy
      Join: BufferStrategy
      End: BufferStrategy
      HasPoint: bool
      HasLine: bool
      HasArea: bool }

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

        Ok
            { Point = tryFind Point |> Option.defaultValue (BufferStrategy.PointCircle defaultPointsPerCircle)
              Join = tryFind Join |> Option.defaultValue (BufferStrategy.JoinRound defaultPointsPerCircle)
              End = tryFind End |> Option.defaultValue (BufferStrategy.EndRound defaultPointsPerCircle)
              HasPoint = hasPoint
              HasLine = hasLine
              HasArea = hasArea }

let private tryNtsParameters configuration =
    let relevantRoundSegments =
        [ if configuration.HasPoint then roundSegments configuration.Point
          if configuration.HasLine || configuration.HasArea then roundSegments configuration.Join
          if configuration.HasLine then roundSegments configuration.End ]
        |> List.choose id

    let quadrants = relevantRoundSegments |> List.map (fun segments -> segments / 4, segments % 4)

    match
        configuration.HasPoint && (configuration.HasLine || configuration.HasArea),
        configuration.Join,
        quadrants |> List.map fst |> List.distinct,
        quadrants |> List.forall (snd >> (=) 0)
    with
    | true, _, _, _ -> None
    | _, BufferStrategy.JoinMiter _, _, _ -> None
    | _, _, _ :: _ :: _, _
    | _, _, _, false -> None
    | _, _, values, true ->
        let parameters = BufferParameters()
        parameters.QuadrantSegments <- values |> List.tryHead |> Option.defaultValue 8

        parameters.EndCapStyle <-
            match configuration.End, configuration.Point with
            | BufferStrategy.EndFlat, _ -> EndCapStyle.Flat
            | _, BufferStrategy.PointSquare -> EndCapStyle.Square
            | _ -> EndCapStyle.Round

        parameters.JoinStyle <- JoinStyle.Round

        Some parameters

let private closeRing points =
    match points with
    | [] -> []
    | first :: _ when List.last points = first -> points
    | first :: _ -> points @ [ first ]

let private polygon srid points =
    { Srid = srid
      Shape = GPolygon [ closeRing points ] }
    |> toNts

let private union geometries =
    match geometries with
    | [] -> None
    | first :: rest ->
        rest
        |> List.fold (fun current geometry -> OverlayNGRobust.Overlay(current, geometry, SpatialFunction.Union)) first
        |> Some

let private circle srid segments distance (x, y) =
    let segments = max 3 segments

    [ 0 .. segments - 1 ]
    |> List.map (fun index ->
        Limits.checkQueryCancellation index
        let angle = 2.0 * Math.PI * float index / float segments
        x + distance * Math.Cos angle, y + distance * Math.Sin angle)
    |> polygon srid

let private square srid distance (x, y) =
    [ x - distance, y - distance
      x + distance, y - distance
      x + distance, y + distance
      x - distance, y + distance ]
    |> polygon srid

type private JoinGeometry =
    { Vertex: float * float
      PreviousDirection: float * float
      NextDirection: float * float
      PreviousOffset: float * float
      NextOffset: float * float
      ArcStart: float * float
      ArcEnd: float * float
      StartAngle: float
      EndAngle: float }

let private tryJoinGeometry distance previous vertex next =
    let x, y = vertex
    let px, py = previous
    let nx, ny = next
    let previousDx, previousDy = x - px, y - py
    let nextDx, nextDy = nx - x, ny - y
    let previousLength = Math.Sqrt(previousDx * previousDx + previousDy * previousDy)
    let nextLength = Math.Sqrt(nextDx * nextDx + nextDy * nextDy)
    let previousAngle = Math.Atan2(previousDy, previousDx)
    let nextAngle = Math.Atan2(nextDy, nextDx)
    let cross = previousDx * nextDy - previousDy * nextDx

    if cross = 0.0 || previousLength = 0.0 || nextLength = 0.0 then
        None
    else
        let left dx dy length = x - distance * dy / length, y + distance * dx / length
        let right dx dy length = x + distance * dy / length, y - distance * dx / length

        let previousOffset, nextOffset, startAngle, endAngle, startPoint, endPoint =
            if cross < 0.0 then
                let previousOffset = left previousDx previousDy previousLength
                let nextOffset = left nextDx nextDy nextLength

                previousOffset,
                nextOffset,
                previousAngle + Math.PI / 2.0,
                nextAngle + Math.PI / 2.0,
                previousOffset,
                nextOffset
            else
                let previousOffset = right previousDx previousDy previousLength
                let nextOffset = right nextDx nextDy nextLength

                previousOffset,
                nextOffset,
                nextAngle - Math.PI / 2.0,
                previousAngle - Math.PI / 2.0,
                nextOffset,
                previousOffset

        let mutable endAngle = endAngle

        while endAngle > startAngle do
            endAngle <- endAngle - 2.0 * Math.PI

        Some
            { Vertex = vertex
              PreviousDirection = previousDx, previousDy
              NextDirection = nextDx, nextDy
              PreviousOffset = previousOffset
              NextOffset = nextOffset
              ArcStart = startPoint
              ArcEnd = endPoint
              StartAngle = startAngle
              EndAngle = endAngle }

let private roundJoin srid pointsPerCircle distance previous vertex next =
    tryJoinGeometry distance previous vertex next
    |> Option.map (fun join ->
        let x, y = join.Vertex
        let difference = join.StartAngle - join.EndAngle
        let segments = max 4 (int pointsPerCircle)
        let steps = max 1 (int (Math.Ceiling(float segments * difference / (2.0 * Math.PI))))

        [ 1 .. steps - 1 ]
        |> List.map (fun index ->
            Limits.checkQueryCancellation index
            let angle = join.StartAngle - difference * float index / float steps
            x + distance * Math.Cos angle, y + distance * Math.Sin angle)
        |> fun middle -> polygon srid (join.Vertex :: join.ArcStart :: middle @ [ join.ArcEnd ]))

let private miterJoin srid limit distance previous vertex next =
    let cross (ax, ay) (bx, by) = ax * by - ay * bx

    tryJoinGeometry distance previous vertex next
    |> Option.bind (fun join ->
        let px, py = join.PreviousOffset
        let nx, ny = join.NextOffset
        let denominator = cross join.PreviousDirection join.NextDirection

        if denominator = 0.0 then
            None
        else
            let t = cross (nx - px, ny - py) join.NextDirection / denominator
            let dx, dy = join.PreviousDirection
            let intersection = px + t * dx, py + t * dy
            let vx, vy = join.Vertex
            let ix, iy = intersection
            let miterDx, miterDy = ix - vx, iy - vy
            let miterDistance = Math.Sqrt(miterDx * miterDx + miterDy * miterDy)
            let maximumDistance = max 1.0 limit * distance

            let tip =
                if miterDistance > maximumDistance then
                    let scale = maximumDistance / miterDistance
                    vx + miterDx * scale, vy + miterDy * scale
                else
                    intersection

            Some(polygon srid [ join.Vertex; join.ArcStart; tip; join.ArcEnd ]))

let private roundEnd srid pointsPerCircle distance penultimate ultimate =
    let ux, uy = ultimate
    let px, py = penultimate
    let dx, dy = ux - px, uy - py
    let length = Math.Sqrt(dx * dx + dy * dy)
    let points = max 4 (int pointsPerCircle)
    let startAngle = Math.Atan2(py - uy, px - ux) - Math.PI / 2.0
    let step = 2.0 * Math.PI / float points
    let left = ux - distance * dy / length, uy + distance * dx / length
    let right = ux + distance * dy / length, uy - distance * dx / length

    let middle =
        [ 1 .. points / 2 - (if points % 2 = 0 then 1 else 0) ]
        |> List.map (fun index ->
            Limits.checkQueryCancellation index
            let angle = startAngle - float index * step
            ux + distance * Math.Cos angle, uy + distance * Math.Sin angle)

    polygon srid (ultimate :: left :: middle @ [ right ])

let private bevelCore distance geometry =
    let parameters = BufferParameters()
    parameters.EndCapStyle <- EndCapStyle.Flat
    parameters.JoinStyle <- JoinStyle.Bevel

    BufferOp.Buffer(geometry, distance, parameters)

let private nondegenerateLineBuffer configuration srid distance closed points =
    let source =
        { Srid = srid
          Shape = GLineString points }
        |> toNts

    let core = bevelCore distance source
    let distinctPoints = if closed then points |> List.take (points.Length - 1) else points

    let joins =
        let joinAt previous vertex next =
            match configuration.Join with
            | BufferStrategy.JoinRound resolution -> roundJoin srid resolution distance previous vertex next
            | BufferStrategy.JoinMiter limit -> miterJoin srid limit distance previous vertex next
            | _ -> None

        if closed then
            let values = List.toArray distinctPoints

            [ 0 .. values.Length - 1 ]
            |> List.choose (fun index ->
                joinAt
                    values.[(index + values.Length - 1) % values.Length]
                    values.[index]
                    values.[(index + 1) % values.Length])
        else
            points
            |> List.windowed 3
            |> List.choose (function
                | [ previous; vertex; next ] -> joinAt previous vertex next
                | _ -> None)

    let ends =
        match closed, configuration.End, points with
        | false, BufferStrategy.EndRound resolution, first :: second :: _ ->
            let penultimate, last = points.[points.Length - 2], List.last points
            [ roundEnd srid resolution distance second first
              roundEnd srid resolution distance penultimate last ]
        | _ -> []

    union (core :: joins @ ends) |> Option.defaultValue core

let private customLineBuffer configuration srid distance closed points =
    let prependDistinct points point =
        match points with
        | head :: _ when head = point -> points
        | _ -> point :: points

    let points =
        points
        |> List.fold prependDistinct []
        |> List.rev
        |> fun values -> if closed then closeRing values else values

    match points with
    | [ point ] -> circle srid (int defaultPointsPerCircle) distance point
    | _ -> nondegenerateLineBuffer configuration srid distance closed points

let rec private customBuffer configuration distance (geometry: Geometry) =
    let srid = geometry.Srid

    let rec bufferShape shape =
        match shape with
        | GEmpty -> Some(toNts { geometry with Shape = GEmpty })
        | GPoint(x, y) ->
            match configuration.Point with
            | BufferStrategy.PointCircle resolution -> Some(circle srid (int resolution) distance (x, y))
            | BufferStrategy.PointSquare -> Some(square srid distance (x, y))
            | _ -> None
        | GMultiPoint points ->
            points
            |> List.map (fun point ->
                match configuration.Point with
                | BufferStrategy.PointCircle resolution -> circle srid (int resolution) distance point
                | BufferStrategy.PointSquare -> square srid distance point
                | _ -> invalidOp "invalid point buffer strategy")
            |> union
        | GLineString points -> Some(customLineBuffer configuration srid distance false points)
        | GMultiLineString lines -> lines |> List.map (customLineBuffer configuration srid distance false) |> union
        | GPolygon rings ->
            let original = toNts { geometry with Shape = GPolygon rings }

            rings
            |> List.map (customLineBuffer configuration srid (abs distance) true)
            |> union
            |> Option.map (fun boundary ->
                let operation = if distance > 0.0 then SpatialFunction.Union else SpatialFunction.Difference
                OverlayNGRobust.Overlay(original, boundary, operation))
        | GMultiPolygon polygons ->
            polygons
            |> List.choose (fun rings -> bufferShape (GPolygon rings))
            |> union
        | GGeometryCollection geometries ->
            geometries
            |> List.choose (fun item -> customBuffer configuration distance item |> Result.toOption)
            |> union

    match bufferShape geometry.Shape with
    | Some result -> Ok result
    | None -> Error "the result cannot be represented as a MySQL geometry"

let internal buffer strategies distance (geometry: Geometry) =
    configureBuffer geometry strategies
    |> Result.bind (fun configuration ->
        attempt geometry.Srid (fun () ->
            match tryNtsParameters configuration with
            | Some parameters -> BufferOp.Buffer(toNts geometry, distance, parameters)
            | None ->
                match customBuffer configuration distance geometry with
                | Ok result -> result
                | Error detail -> invalidArg "geometry" detail)
        |> Result.mapError BufferError.OperationFailed)
