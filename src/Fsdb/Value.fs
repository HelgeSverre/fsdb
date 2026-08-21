/// The runtime value type flowing through expression evaluation, storage,
/// and wire encoding, plus MySQL's (famously loose) comparison, coercion,
/// truthiness, and arithmetic rules as pure functions.
module Fsdb.Value

open System
open System.Buffers.Binary
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Fsdb.Binary
open Fsdb.Temporal

type GeometryKind =
    | Geometry
    | Point
    | LineString
    | Polygon
    | MultiPoint
    | MultiLineString
    | MultiPolygon
    | GeometryCollection

type GeometryShape =
    | GEmpty
    | GPoint of float * float
    | GLineString of (float * float) list
    | GPolygon of (float * float) list list
    | GMultiPoint of (float * float) list
    | GMultiLineString of ((float * float) list) list
    | GMultiPolygon of ((float * float) list list) list
    | GGeometryCollection of Geometry list

and Geometry =
    { Srid: int
      Shape: GeometryShape }

exception GeometryError of string

let geometryKind = function
    | GEmpty -> GeometryCollection
    | GPoint _ -> Point
    | GLineString _ -> LineString
    | GPolygon _ -> Polygon
    | GMultiPoint _ -> MultiPoint
    | GMultiLineString _ -> MultiLineString
    | GMultiPolygon _ -> MultiPolygon
    | GGeometryCollection _ -> GeometryCollection

let geometryTypeCode = function
    | Point -> 1
    | LineString -> 2
    | Polygon -> 3
    | MultiPoint -> 4
    | MultiLineString -> 5
    | MultiPolygon -> 6
    | GeometryCollection -> 7
    | Geometry -> invalidArg "kind" "GEOMETRY is a column supertype, not a WKB shape"

let geometryTypeName = function
    | Geometry -> "GEOMETRY"
    | Point -> "POINT"
    | LineString -> "LINESTRING"
    | Polygon -> "POLYGON"
    | MultiPoint -> "MULTIPOINT"
    | MultiLineString -> "MULTILINESTRING"
    | MultiPolygon -> "MULTIPOLYGON"
    | GeometryCollection -> "GEOMETRYCOLLECTION"

let private formatCoordinate (value: float) = value.ToString("G17", CultureInfo.InvariantCulture)

let rec geometryToText (geometry: Geometry) : string =
    let pair (x, y) = formatCoordinate x + " " + formatCoordinate y
    let pairs points = points |> List.map pair |> String.concat ","
    let rings polygon = polygon |> List.map (fun ring -> "(" + pairs ring + ")") |> String.concat ","

    match geometry.Shape with
    | GEmpty -> "GEOMETRYCOLLECTION EMPTY"
    | GPoint(x, y) -> "POINT(" + pair (x, y) + ")"
    | GLineString points -> "LINESTRING(" + pairs points + ")"
    | GPolygon polygon -> "POLYGON(" + rings polygon + ")"
    | GMultiPoint points -> "MULTIPOINT(" + (points |> List.map (pair >> fun point -> "(" + point + ")") |> String.concat ",") + ")"
    | GMultiLineString lines -> "MULTILINESTRING(" + (lines |> List.map (fun line -> "(" + pairs line + ")") |> String.concat ",") + ")"
    | GMultiPolygon polygons -> "MULTIPOLYGON(" + (polygons |> List.map (fun polygon -> "(" + rings polygon + ")") |> String.concat ",") + ")"
    | GGeometryCollection geometries -> "GEOMETRYCOLLECTION(" + (geometries |> List.map geometryToText |> String.concat ",") + ")"

type private WktToken =
    | WktWord of string
    | WktNumber of float
    | WktOpen
    | WktClose
    | WktComma

let private tokenizeWkt (text: string) : WktToken list option =
    let tokens = ResizeArray<WktToken>()
    let mutable index = 0
    let mutable valid = true

    while valid && index < text.Length do
        match text.[index] with
        | c when Char.IsWhiteSpace c -> index <- index + 1
        | '(' -> tokens.Add WktOpen; index <- index + 1
        | ')' -> tokens.Add WktClose; index <- index + 1
        | ',' -> tokens.Add WktComma; index <- index + 1
        | c when Char.IsLetter c ->
            let start = index

            while index < text.Length && Char.IsLetter text.[index] do
                index <- index + 1

            tokens.Add(WktWord(text.Substring(start, index - start).ToUpperInvariant()))
        | c when Char.IsDigit c || c = '+' || c = '-' || c = '.' ->
            let start = index

            while index < text.Length &&
                  (Char.IsDigit text.[index] || text.[index] = '+' || text.[index] = '-' || text.[index] = '.' || text.[index] = 'e' || text.[index] = 'E') do
                index <- index + 1

            match Double.TryParse(text.Substring(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value when Double.IsFinite value -> tokens.Add(WktNumber value)
            | _ -> valid <- false
        | _ -> valid <- false

    if valid then Some(List.ofSeq tokens) else None

let tryGeometryFromText (srid: int) (text: string) : Geometry option =
    let samePoint (x1, y1) (x2, y2) = x1 = x2 && y1 = y2

    let validLine points = List.length points >= 2
    let validPolygon rings =
        not (List.isEmpty rings)
        && rings |> List.forall (fun ring -> List.length ring >= 4 && samePoint (List.head ring) (List.last ring))

    let rec isValidWktShape = function
        | GEmpty -> true
        | GPoint _ -> true
        | GLineString points -> validLine points
        | GPolygon rings -> validPolygon rings
        | GMultiPoint points -> not (List.isEmpty points)
        | GMultiLineString lines -> not (List.isEmpty lines) && lines |> List.forall validLine
        | GMultiPolygon polygons -> not (List.isEmpty polygons) && polygons |> List.forall validPolygon
        | GGeometryCollection geometries -> geometries |> List.forall (fun geometry -> isValidWktShape geometry.Shape)

    let rec parseShape (tokens: WktToken array) depth index : (GeometryShape * int) option =
        let readPair index =
            if index + 1 < tokens.Length then
                match tokens.[index], tokens.[index + 1] with
                | WktNumber x, WktNumber y -> Some((x, y), index + 2)
                | _ -> None
            else
                None

        let parseDelimited parseOne index =
            if index >= tokens.Length || tokens.[index] <> WktOpen then
                None
            else
                let rec loop values cursor =
                    if cursor >= tokens.Length then
                        None
                    elif tokens.[cursor] = WktClose then
                        Some(List.rev values, cursor + 1)
                    else
                        match parseOne cursor with
                        | Some(value, next) when next < tokens.Length && tokens.[next] = WktComma -> loop (value :: values) (next + 1)
                        | Some(value, next) when next < tokens.Length && tokens.[next] = WktClose -> Some(List.rev (value :: values), next + 1)
                        | _ -> None

                loop [] (index + 1)

        let parsePoint index = readPair index
        let parseSinglePoint index =
            parseDelimited parsePoint index
            |> Option.bind (function
                | [ point ], next -> Some(point, next)
                | _ -> None)

        let parseLine index = parseDelimited parsePoint index
        let parsePolygon index = parseDelimited parseLine index

        if depth > 64 || index >= tokens.Length then
            None
        else
            match tokens.[index] with
            | WktWord name when index + 1 < tokens.Length && tokens.[index + 1] = WktWord "EMPTY" ->
                match name with
                | "GEOMETRYCOLLECTION" -> Some(GEmpty, index + 2)
                | _ -> None
            | WktWord "POINT" -> parseSinglePoint (index + 1) |> Option.map (fun ((x, y), next) -> GPoint(x, y), next)
            | WktWord "LINESTRING" -> parseLine (index + 1) |> Option.map (fun (line, next) -> GLineString line, next)
            | WktWord "POLYGON" -> parsePolygon (index + 1) |> Option.map (fun (polygon, next) -> GPolygon polygon, next)
            | WktWord "MULTIPOINT" ->
                parseDelimited (fun cursor ->
                    if cursor < tokens.Length && tokens.[cursor] = WktOpen then
                        parsePoint (cursor + 1)
                        |> Option.bind (fun (point, next) -> if next < tokens.Length && tokens.[next] = WktClose then Some(point, next + 1) else None)
                    else
                        parsePoint cursor) (index + 1)
                |> Option.map (fun (points, next) -> GMultiPoint points, next)
            | WktWord "MULTILINESTRING" ->
                parseDelimited parseLine (index + 1) |> Option.map (fun (lines, next) -> GMultiLineString lines, next)
            | WktWord "MULTIPOLYGON" ->
                parseDelimited parsePolygon (index + 1) |> Option.map (fun (polygons, next) -> GMultiPolygon polygons, next)
            | WktWord "GEOMETRYCOLLECTION" ->
                parseDelimited (fun cursor -> parseShape tokens (depth + 1) cursor |> Option.map (fun (shape, next) -> { Srid = srid; Shape = shape }, next)) (index + 1)
                |> Option.map (fun (geometries, next) -> GGeometryCollection geometries, next)
            | _ -> None

    tokenizeWkt text
    |> Option.bind (fun tokens ->
        let tokens = List.toArray tokens

        match parseShape tokens 0 0 with
        | Some(shape, next) when next = tokens.Length && isValidWktShape shape -> Some { Srid = srid; Shape = shape }
        | _ -> None)

let geometryToWkb (geometry: Geometry) : byte[] =
    let writer = Writer()

    let rec writeGeometry shape =
        let writePair (x, y) = writer.WriteDoubleLE x; writer.WriteDoubleLE y
        let writePairs pairs = writer.WriteInt32LE(List.length pairs); pairs |> List.iter writePair
        let writeHeader kind = writer.WriteByte 1uy; writer.WriteInt32LE(geometryTypeCode kind)

        match shape with
        | GEmpty -> writeHeader GeometryCollection; writer.WriteInt32LE 0
        | GPoint(x, y) -> writeHeader Point; writePair (x, y)
        | GLineString points -> writeHeader LineString; writePairs points
        | GPolygon polygon ->
            writeHeader Polygon
            writer.WriteInt32LE(List.length polygon)
            polygon |> List.iter writePairs
        | GMultiPoint points ->
            writeHeader MultiPoint
            writer.WriteInt32LE(List.length points)
            points |> List.iter (fun (x, y) -> writeGeometry (GPoint(x, y)))
        | GMultiLineString lines ->
            writeHeader MultiLineString
            writer.WriteInt32LE(List.length lines)
            lines |> List.iter (fun line -> writeGeometry (GLineString line))
        | GMultiPolygon polygons ->
            writeHeader MultiPolygon
            writer.WriteInt32LE(List.length polygons)
            polygons |> List.iter (fun polygon -> writeGeometry (GPolygon polygon))
        | GGeometryCollection geometries ->
            writeHeader GeometryCollection
            writer.WriteInt32LE(List.length geometries)
            geometries |> List.iter (fun nested -> writeGeometry nested.Shape)

    writeGeometry geometry.Shape
    writer.ToArray()

let geometryToMySqlBinary (geometry: Geometry) : byte[] =
    let writer = Writer()
    writer.WriteInt32LE geometry.Srid
    writer.WriteBytes(geometryToWkb geometry)
    writer.ToArray()

let tryGeometryFromWkb (srid: int) (bytes: byte[]) : Geometry option =
    let mutable position = 0

    let take count =
        if count < 0 || position + count > bytes.Length then
            None
        else
            let start = position
            position <- position + count
            Some(start, count)

    let readByte () = take 1 |> Option.map (fun (start, _) -> bytes.[start])

    let readInt32 littleEndian =
        take 4
        |> Option.map (fun (start, _) ->
            let span = ReadOnlySpan(bytes, start, 4)
            if littleEndian then BinaryPrimitives.ReadInt32LittleEndian span else BinaryPrimitives.ReadInt32BigEndian span)

    let readDouble littleEndian =
        take 8
        |> Option.map (fun (start, _) ->
            let span = ReadOnlySpan(bytes, start, 8)
            let bits = if littleEndian then BinaryPrimitives.ReadInt64LittleEndian span else BinaryPrimitives.ReadInt64BigEndian span
            BitConverter.Int64BitsToDouble bits)

    let finitePair (x, y) = Double.IsFinite x && Double.IsFinite y

    let rec readGeometry depth : GeometryShape option =
        match readByte () with
        | Some 0uy
        | Some 1uy as order ->
            let littleEndian = order = Some 1uy

            match readInt32 littleEndian with
            | Some typeCode when typeCode >= 1 && typeCode <= 7 ->
                let readPair () =
                    match readDouble littleEndian, readDouble littleEndian with
                    | Some x, Some y -> Some(x, y)
                    | _ -> None

                let readMany readOne =
                    match readInt32 littleEndian with
                    | Some count when count >= 0 && count <= (bytes.Length - position) ->
                        let rec loop remaining values =
                            if remaining = 0 then Some(List.rev values)
                            else readOne () |> Option.bind (fun value -> loop (remaining - 1) (value :: values))

                        loop count []
                    | _ -> None

                if depth > 64 then
                    None
                else
                    match typeCode with
                    | 1 ->
                        readPair ()
                        |> Option.bind (fun (x, y) ->
                            if finitePair (x, y) then Some(GPoint(x, y))
                            else None)
                    | 2 ->
                        readMany readPair
                        |> Option.bind (fun points ->
                            if List.length points >= 2 && points |> List.forall finitePair then Some(GLineString points)
                            else None)
                    | 3 ->
                        readMany (fun () -> readMany readPair)
                        |> Option.bind (fun rings ->
                            let closed ring = List.length ring >= 4 && List.head ring = List.last ring

                            if not (List.isEmpty rings) && rings |> List.forall (fun ring -> closed ring && ring |> List.forall finitePair) then
                                Some(GPolygon rings)
                            else
                                None)
                    | 4 ->
                        readMany (fun () -> readGeometry (depth + 1))
                        |> Option.bind (fun shapes ->
                            shapes
                            |> List.fold (fun points shape ->
                                match points, shape with
                                | Some values, GPoint(x, y) -> Some((x, y) :: values)
                                | _ -> None) (Some [])
                            |> Option.bind (fun points -> if List.isEmpty points then None else Some(GMultiPoint(List.rev points))))
                    | 5 ->
                        readMany (fun () -> readGeometry (depth + 1))
                        |> Option.bind (fun shapes ->
                            shapes
                            |> List.fold (fun lines shape ->
                                match lines, shape with
                                | Some values, GLineString line -> Some(line :: values)
                                | _ -> None) (Some [])
                            |> Option.bind (fun lines -> if List.isEmpty lines || lines |> List.exists (fun line -> List.length line < 2) then None else Some(GMultiLineString(List.rev lines))))
                    | 6 ->
                        readMany (fun () -> readGeometry (depth + 1))
                        |> Option.bind (fun shapes ->
                            shapes
                            |> List.fold (fun polygons shape ->
                                match polygons, shape with
                                | Some values, GPolygon polygon -> Some(polygon :: values)
                                | _ -> None) (Some [])
                            |> Option.bind (fun polygons ->
                                let closed ring = List.length ring >= 4 && List.head ring = List.last ring

                                if List.isEmpty polygons || polygons |> List.exists (fun polygon -> List.isEmpty polygon || polygon |> List.exists (closed >> not)) then
                                    None
                                else
                                    Some(GMultiPolygon(List.rev polygons))))
                    | 7 ->
                        readMany (fun () -> readGeometry (depth + 1))
                        |> Option.map (fun shapes ->
                            if List.isEmpty shapes then GEmpty
                            else shapes |> List.map (fun shape -> { Srid = srid; Shape = shape }) |> GGeometryCollection)
                    | _ -> None
            | _ -> None
        | _ -> None

    readGeometry 0
    |> Option.bind (fun shape -> if position = bytes.Length then Some { Srid = srid; Shape = shape } else None)

let tryGeometryFromMySqlBinary (bytes: byte[]) : Geometry option =
    if bytes.Length < 5 then
        None
    else
        let srid = BinaryPrimitives.ReadInt32LittleEndian(ReadOnlySpan(bytes, 0, 4))
        tryGeometryFromWkb srid bytes.[4..]

type GeometryBounds =
    { MinX: float
      MinY: float
      MaxX: float
      MaxY: float }

let rec private shapeCoordinates = function
    | GEmpty -> []
    | GPoint(x, y) -> [ x, y ]
    | GLineString points -> points
    | GPolygon rings -> List.collect id rings
    | GMultiPoint points -> points
    | GMultiLineString lines -> List.collect id lines
    | GMultiPolygon polygons -> polygons |> List.collect (List.collect id)
    | GGeometryCollection geometries -> geometries |> List.collect (fun geometry -> shapeCoordinates geometry.Shape)

let geometryBounds (geometry: Geometry) : GeometryBounds option =
    match shapeCoordinates geometry.Shape with
    | [] -> None
    | (x, y) :: points ->
        let minX, minY, maxX, maxY =
            points
            |> List.fold (fun (minX, minY, maxX, maxY) (x, y) -> min minX x, min minY y, max maxX x, max maxY y) (x, y, x, y)

        Some { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY }

let geometryEnvelope (geometry: Geometry) : Geometry =
    let shape =
        match geometryBounds geometry with
        | None -> GEmpty
        | Some bounds when bounds.MinX = bounds.MaxX && bounds.MinY = bounds.MaxY -> GPoint(bounds.MinX, bounds.MinY)
        | Some bounds when bounds.MinX = bounds.MaxX || bounds.MinY = bounds.MaxY ->
            GLineString [ (bounds.MinX, bounds.MinY); (bounds.MaxX, bounds.MaxY) ]
        | Some bounds ->
            GPolygon
                [ [ (bounds.MinX, bounds.MinY)
                    (bounds.MaxX, bounds.MinY)
                    (bounds.MaxX, bounds.MaxY)
                    (bounds.MinX, bounds.MaxY)
                    (bounds.MinX, bounds.MinY) ] ]

    { geometry with Shape = shape }

let private pointOnSegment (x, y) ((x1, y1), (x2, y2)) =
    let cross = (x - x1) * (y2 - y1) - (y - y1) * (x2 - x1)
    let within value lower upper = min lower upper <= value && value <= max lower upper
    cross = 0.0 && within x x1 x2 && within y y1 y2

let private segments points = points |> List.pairwise

let private euclidean x y = sqrt (x * x + y * y)

let private orientation (x1, y1) (x2, y2) (x3, y3) = (x2 - x1) * (y3 - y1) - (y2 - y1) * (x3 - x1)

let private segmentsIntersect ((a, b) as first) ((c, d) as second) =
    let abC = orientation a b c
    let abD = orientation a b d
    let cdA = orientation c d a
    let cdB = orientation c d b

    (abC = 0.0 && pointOnSegment c first)
    || (abD = 0.0 && pointOnSegment d first)
    || (cdA = 0.0 && pointOnSegment a second)
    || (cdB = 0.0 && pointOnSegment b second)
    || ((abC < 0.0) <> (abD < 0.0) && (cdA < 0.0) <> (cdB < 0.0))

let private pointSegmentDistance (x, y) ((x1, y1), (x2, y2)) =
    let dx = x2 - x1
    let dy = y2 - y1
    let lengthSquared = dx * dx + dy * dy

    if lengthSquared = 0.0 then
        euclidean (x - x1) (y - y1)
    else
        let t = max 0.0 (min 1.0 (((x - x1) * dx + (y - y1) * dy) / lengthSquared))
        euclidean (x - (x1 + t * dx)) (y - (y1 + t * dy))

let private ringContains point ring =
    let edges = segments ring

    if edges |> List.exists (pointOnSegment point) then
        true
    else
        let x, y = point

        edges
        |> List.fold (fun inside ((x1, y1), (x2, y2)) ->
            if (y1 > y) <> (y2 > y) && x < (x2 - x1) * (y - y1) / (y2 - y1) + x1 then
                not inside
            else
                inside) false

let private polygonContains point = function
    | shell :: holes -> ringContains point shell && not (holes |> List.exists (ringContains point))
    | [] -> false

let rec private shapePolygons = function
    | GPolygon polygon -> [ polygon ]
    | GMultiPolygon polygons -> polygons
    | GGeometryCollection geometries -> geometries |> List.collect (fun geometry -> shapePolygons geometry.Shape)
    | _ -> []

let rec private shapeSegments = function
    | GLineString points -> segments points
    | GPolygon polygon -> polygon |> List.collect segments
    | GMultiLineString lines -> lines |> List.collect segments
    | GMultiPolygon polygons -> polygons |> List.collect (List.collect segments)
    | GGeometryCollection geometries -> geometries |> List.collect (fun geometry -> shapeSegments geometry.Shape)
    | _ -> []

let private ringIsSimple ring =
    let edges = ring |> List.pairwise |> List.indexed
    let lastEdge = List.length edges - 1

    edges
    |> List.forall (fun (firstIndex, first) ->
        edges
        |> List.skip (firstIndex + 1)
        |> List.forall (fun (secondIndex, second) ->
            let adjacent = secondIndex = firstIndex + 1 || firstIndex = 0 && secondIndex = lastEdge
            adjacent || not (segmentsIntersect first second)))

let private pointStrictlyInRing point ring =
    ringContains point ring && not (ring |> segments |> List.exists (pointOnSegment point))

let private ringsIntersect first second =
    first
    |> segments
    |> List.exists (fun firstSegment -> second |> segments |> List.exists (segmentsIntersect firstSegment))

let private ringsCrossOrOverlap first second =
    let endpointIsInterior point (startPoint, endPoint) =
        pointOnSegment point (startPoint, endPoint) && point <> startPoint && point <> endPoint

    first
    |> segments
    |> List.exists (fun ((firstStart, firstEnd) as firstSegment) ->
        second
        |> segments
        |> List.exists (fun ((secondStart, secondEnd) as secondSegment) ->
            let sharedEndpoints =
                [ firstStart; firstEnd ]
                |> List.filter (fun point -> point = secondStart || point = secondEnd)
                |> List.distinct

            segmentsIntersect firstSegment secondSegment
            && (sharedEndpoints.Length <> 1
                || endpointIsInterior firstStart secondSegment
                || endpointIsInterior firstEnd secondSegment
                || endpointIsInterior secondStart firstSegment
                || endpointIsInterior secondEnd firstSegment)))

let private hasDistinctPoints = function
    | first :: points -> points |> List.exists ((<>) first)
    | [] -> false

let private polygonIsValid rings =
    match rings with
    | shell :: holes ->
        let ringIsValid ring =
            match ring with
            | first :: _ :: _ :: _ :: _ when List.last ring = first ->
                ringIsSimple ring
                && ring
                   |> List.forall (fun (x, y) -> Double.IsFinite x && Double.IsFinite y)
            | _ -> false

        let holeIsValid hole =
            ringIsValid hole
            && hole |> List.take (List.length hole - 1) |> List.forall (fun point -> pointStrictlyInRing point shell)
            && not (ringsIntersect shell hole)

        let holesDoNotOverlap =
            holes
            |> List.indexed
            |> List.forall (fun (firstIndex, first) ->
                holes
                |> List.skip (firstIndex + 1)
                |> List.forall (fun second ->
                    not (ringsIntersect first second)
                    && not (pointStrictlyInRing first.Head second)
                    && not (pointStrictlyInRing second.Head first)))

        ringIsValid shell && holes |> List.forall holeIsValid && holesDoNotOverlap
    | [] -> false

let rec private shapeIsValid = function
    | GEmpty -> true
    | GPoint(x, y) -> Double.IsFinite x && Double.IsFinite y
    | GLineString points ->
        points
        |> List.forall (fun (x, y) -> Double.IsFinite x && Double.IsFinite y)
        && hasDistinctPoints points
    | GMultiPoint points ->
        not (List.isEmpty points)
        && points |> List.forall (fun (x, y) -> Double.IsFinite x && Double.IsFinite y)
    | GPolygon rings -> polygonIsValid rings
    | GMultiLineString lines ->
        not (List.isEmpty lines)
        && lines
           |> List.forall (fun points ->
               hasDistinctPoints points
               && points |> List.forall (fun (x, y) -> Double.IsFinite x && Double.IsFinite y))
    | GMultiPolygon polygons ->
        let polygonsAreSeparate =
            polygons
            |> List.indexed
            |> List.forall (fun (firstIndex, first) ->
                polygons
                |> List.skip (firstIndex + 1)
                |> List.forall (fun second ->
                    match first, second with
                    | firstShell :: _, secondShell :: _ ->
                        not (ringsCrossOrOverlap firstShell secondShell)
                        && not (pointStrictlyInRing firstShell.Head secondShell)
                        && not (pointStrictlyInRing secondShell.Head firstShell)
                    | _ -> false))

        not (List.isEmpty polygons) && polygons |> List.forall polygonIsValid && polygonsAreSeparate
    | GGeometryCollection geometries -> geometries |> List.forall (fun geometry -> shapeIsValid geometry.Shape)

let geometryIsValidPlanar (geometry: Geometry) = shapeIsValid geometry.Shape

let geometryDistancePlanar (first: Geometry) (second: Geometry) : float option =
    let firstPoints = shapeCoordinates first.Shape
    let secondPoints = shapeCoordinates second.Shape

    if List.isEmpty firstPoints || List.isEmpty secondPoints then
        None
    else
        let firstSegments = shapeSegments first.Shape
        let secondSegments = shapeSegments second.Shape
        let firstPolygons = shapePolygons first.Shape
        let secondPolygons = shapePolygons second.Shape
        let firstInsideSecond = firstPoints |> List.exists (fun point -> secondPolygons |> List.exists (polygonContains point))
        let secondInsideFirst = secondPoints |> List.exists (fun point -> firstPolygons |> List.exists (polygonContains point))
        let pointTouchesSegment =
            (firstPoints |> List.exists (fun point -> secondSegments |> List.exists (pointOnSegment point)))
            || (secondPoints |> List.exists (fun point -> firstSegments |> List.exists (pointOnSegment point)))
        let intersects =
            firstSegments
            |> List.exists (fun firstSegment -> secondSegments |> List.exists (segmentsIntersect firstSegment))

        if firstInsideSecond || secondInsideFirst || pointTouchesSegment || intersects then
            Some 0.0
        else
            let pointDistances =
                [ for firstPoint in firstPoints do
                      for secondPoint in secondPoints do
                          euclidean (fst firstPoint - fst secondPoint) (snd firstPoint - snd secondPoint)
                  for point in firstPoints do
                      for segment in secondSegments do
                          pointSegmentDistance point segment
                  for point in secondPoints do
                      for segment in firstSegments do
                          pointSegmentDistance point segment ]

            pointDistances |> List.min |> Some

let geometryIntersectsPlanar (first: Geometry) (second: Geometry) =
    geometryDistancePlanar first second |> Option.map ((=) 0.0)

type Value =
    | VNull
    | VInt of int64
    /// MySQL's `BIGINT UNSIGNED` domain, whose top half (2^63 .. 2^64-1)
    /// has no `int64` representation — `CAST(-1 AS UNSIGNED)` is
    /// 18446744073709551615, not -1. Only 64-bit unsigned needs its own
    /// case: every narrower unsigned type (`INT UNSIGNED`'s 4294967295 and
    /// down) fits `VInt` exactly.
    | VUInt of uint64
    | VBit of width: int * value: uint64
    | VDouble of float
    | VDecimal of decimal
    | VString of string
    | VBytes of byte[]
    | VDate of DateOnly
    | VDateTime of DateTime
    | VZeroDate of ZeroDate
    | VZeroDateTime of ZeroDateTime
    /// Raw JSON text — ponytail: no parsed representation yet, add one
    /// (JVal DU or similar) when JSON_EXTRACT-style path queries land.
    | VJson of string
    | VGeometry of Geometry

// MySQL wire protocol column type ids — shared by `Protocol`'s column
// definition packets (a resultset's declared type) and its binary-protocol
// parameter decoding (COM_STMT_EXECUTE's per-param type array). Kept here,
// ahead of both `Executor` and `Protocol` in build order, so `mysqlTypeOf`
// below (used by `Executor` to type a resultset's columns) and `Protocol`
// (used to read/write them on the wire) share one definition instead of
// two copies of the same numeric constants drifting apart.
// https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_basic_dt_types.html
let TypeTiny = 0x01uy
let TypeShort = 0x02uy
let TypeLong = 0x03uy
let TypeFloat = 0x04uy
let TypeDouble = 0x05uy
let TypeNull = 0x06uy
let TypeTimestamp = 0x07uy
let TypeLongLong = 0x08uy
let TypeDate = 0x0auy
let TypeTime = 0x0buy
let TypeDateTime = 0x0cuy
let TypeYear = 0x0duy
let TypeVarchar = 0x0fuy
let TypeBit = 0x10uy
let TypeNewDecimal = 0xf6uy
let TypeBlob = 0xfcuy
let TypeVarString = 0xfduy
let TypeString = 0xfeuy
let TypeGeometry = 0xffuy

let NotNullFlag = 0x0001us
let PrimaryKeyFlag = 0x0002us
let UniqueKeyFlag = 0x0004us
let BlobFlag = 0x0010us
let UnsignedFlag = 0x0020us
let BinaryFlag = 0x0080us
let EnumFlag = 0x0100us
let AutoIncrementFlag = 0x0200us
let SetFlag = 0x0800us

/// The result-column metadata consumed by both the definition packet and
/// binary-row encoder. Keeping these fields together prevents the encoder
/// from disagreeing with the type and flags advertised to the client.
type ColumnMetadata =
    { TypeId: byte
      ColumnLength: uint32
      Flags: uint16
      Decimals: byte }

let columnMetadata typeId =
    { TypeId = typeId
      ColumnLength = 0u
      Flags = 0us
      Decimals = 0uy }

let bitBytes (width: int) (value: uint64) : byte[] =
    let byteCount = (width + 7) / 8
    let bytes = Array.zeroCreate<byte> byteCount

    for index in 0 .. byteCount - 1 do
        let shift = (byteCount - index - 1) * 8
        bytes.[index] <- byte (value >>> shift)

    bytes

let bitValue (bytes: byte[]) : uint64 option =
    if bytes.Length > 8 then
        None
    else
        bytes
        |> Array.fold (fun value next -> (value <<< 8) ||| uint64 next) 0UL
        |> Some

let tryRawBytes =
    function
    | VBit(width, value) -> Some(bitBytes width value)
    | VBytes bytes -> Some bytes
    | _ -> None

/// .NET's shortest-round-trip double formatting agrees with MySQL on the
/// mantissa but not the exponent: "1E+20" vs MySQL's "1e20" (lowercase,
/// no '+', no zero-padding). Reshapes just the exponent part when present.
let private formatDouble (d: float) : string =
    let s = d.ToString(CultureInfo.InvariantCulture)

    match s.IndexOf 'E' with
    | -1 -> s
    | i ->
        let mantissa = s.Substring(0, i)
        let exp = s.Substring(i + 1)

        let sign, digits =
            if exp.StartsWith "-" then "-", exp.Substring(1) else "", exp.TrimStart '+'

        let digits =
            match digits.TrimStart '0' with
            | "" -> "0"
            | d -> d

        mantissa + "e" + sign + digits

/// Renders a value the way the text resultset protocol does: NULL becomes
/// the lenenc-null marker (`None`), everything else its textual form.
let toText (v: Value) : string option =
    match v with
    | VNull -> None
    | VInt i -> Some(string i)
    | VUInt u -> Some(string u)
    | VBit(width, value) -> Some(Text.Encoding.Latin1.GetString(bitBytes width value))
    // .NET Core's default double ToString is already the shortest
    // round-trippable representation (no "0.1000000000000001" noise); only
    // the exponent's shape needs reworking to match MySQL's rendering.
    | VDouble d -> Some(formatDouble d)
    | VDecimal d -> Some(d.ToString(CultureInfo.InvariantCulture))
    | VString s -> Some s
    | VBytes b -> Some(Text.Encoding.Latin1.GetString b)
    | VDate d -> Some(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
    | VDateTime dt ->
        // Render sub-second precision (MySQL DATETIME(6)) as exactly six
        // fractional digits when present, none when the value lands on a whole
        // second. Ticks are 100 ns, so the sub-second remainder / 10 is
        // microseconds. The binary protocol encoder keys off this rendering:
        // it only emits its 11-byte, microsecond-bearing form when the
        // rendered string carries the fraction.
        // Current-time sources (`NOW()`, `DEFAULT CURRENT_TIMESTAMP`)
        // truncate to whole seconds so they don't sprout a fraction MySQL's
        // precision-0 NOW() never shows.
        let micros = (dt.Ticks % TimeSpan.TicksPerSecond) / 10L
        let baseStr = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        Some(if micros = 0L then baseStr else sprintf "%s.%06d" baseStr micros)
    | VZeroDate d -> Some(formatZeroDate d)
    | VZeroDateTime dt -> Some(formatZeroDateTime dt)
    | VJson j -> Some j
    | VGeometry geometry -> Some(Text.Encoding.Latin1.GetString(geometryToMySqlBinary geometry))

/// Renders a value at a declared fractional-seconds precision (fsp 0-6),
/// for a column whose schema says `DATETIME(fsp)`/`TIMESTAMP(fsp)` — exactly
/// `fsp` fractional digits, trailing zeros included, so a `DATETIME(6)` on an
/// exact second shows `.000000` and a `DATETIME(0)` shows none (where the
/// bare `VDateTime` alone, via `toText`, can't say how many digits the column
/// wants). The stored value is already rounded to `fsp` at coercion
/// (`Storage.coerceValue`), so the top `fsp` micro-digits are exact — this
/// only chooses how many to show. Any non-`VDateTime` value (a `TIME` column
/// is stored pre-formatted as `VString`; every other type) falls through to
/// `toText`.
let toTextFsp (fsp: int) (v: Value) : string option =
    match v with
    | VDateTime dt ->
        let baseStr = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)

        if fsp <= 0 then
            Some baseStr
        else
            let micros = (dt.Ticks % TimeSpan.TicksPerSecond) / 10L
            Some(sprintf "%s.%s" baseStr ((sprintf "%06d" micros).Substring(0, min fsp 6)))
    | VZeroDateTime dt -> Some(formatZeroDateTimeFsp fsp dt)
    | _ -> toText v

/// A round-trippable tagged-text encoding of a `Value` — `ofWire (toWire v) = v`
/// for every case. Binary persistence uses `encodeValue`; this survives as the
/// human-readable, one-line ASCII form the torture harness hashes rows with
/// (`Harness.rowHash`) and `ValueTests` pins. Strings/bytes/JSON are
/// base64-encoded so the result is safe regardless of what delimiters the
/// caller puts around it.
let toWire (v: Value) : string =
    let b64 (s: string) = Convert.ToBase64String(Text.Encoding.UTF8.GetBytes s)

    match v with
    | VNull -> "N"
    | VInt i -> "I" + string i
    | VUInt u -> "U" + string u
    | VBit(width, value) -> sprintf "Q%d:%s" width (Convert.ToBase64String(bitBytes width value))
    | VDouble d -> "D" + d.ToString("R", CultureInfo.InvariantCulture)
    | VDecimal d -> "M" + d.ToString(CultureInfo.InvariantCulture)
    | VString s -> "S" + b64 s
    | VBytes b -> "B" + Convert.ToBase64String b
    | VDate d -> "T" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    // "O" (round-trip) format, not `toText`'s display format — keeps
    // sub-second precision.
    | VDateTime dt -> "V" + dt.ToString("O", CultureInfo.InvariantCulture)
    | VZeroDate d -> "Z" + formatZeroDate d
    | VZeroDateTime dt -> "W" + formatZeroDateTime dt
    | VJson j -> "J" + b64 j
    | VGeometry geometry -> "G" + Convert.ToBase64String(geometryToMySqlBinary geometry)

/// Inverse of `toWire`. Throws on malformed input rather than coercing.
let ofWire (s: string) : Value =
    let unb64 (payload: string) = Text.Encoding.UTF8.GetString(Convert.FromBase64String payload)

    if s = "N" then
        VNull
    else
        let payload = s.Substring(1)

        match s.[0] with
        | 'I' -> VInt(Int64.Parse(payload, CultureInfo.InvariantCulture))
        | 'U' -> VUInt(UInt64.Parse(payload, CultureInfo.InvariantCulture))
        | 'D' -> VDouble(Double.Parse(payload, NumberStyles.Float, CultureInfo.InvariantCulture))
        | 'M' -> VDecimal(Decimal.Parse(payload, CultureInfo.InvariantCulture))
        | 'Q' ->
            match payload.IndexOf ':' with
            | index when index > 0 ->
                let width = Int32.Parse(payload.Substring(0, index), CultureInfo.InvariantCulture)
                let bytes = Convert.FromBase64String(payload.Substring(index + 1))

                match bitValue bytes with
                | Some value -> VBit(width, value)
                | None -> failwithf "Value.ofWire: invalid bit payload %s" payload
            | _ -> failwithf "Value.ofWire: invalid bit payload %s" payload
        | 'S' -> VString(unb64 payload)
        | 'B' -> VBytes(Convert.FromBase64String payload)
        | 'T' -> VDate(DateOnly.Parse(payload, CultureInfo.InvariantCulture))
        | 'V' -> VDateTime(DateTime.Parse(payload, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
        | 'Z' -> tryParseZeroDate payload |> Option.map VZeroDate |> Option.defaultWith (fun () -> failwithf "Value.ofWire: invalid zero date %s" payload)
        | 'W' -> tryParseZeroDateTime payload |> Option.map VZeroDateTime |> Option.defaultWith (fun () -> failwithf "Value.ofWire: invalid zero datetime %s" payload)
        | 'J' -> VJson(unb64 payload)
        | 'G' ->
            let bytes = Convert.FromBase64String payload

            match tryGeometryFromMySqlBinary bytes with
            | Some geometry -> VGeometry geometry
            | None -> failwith "Value.ofWire: invalid geometry payload"
        | tag -> failwithf "Value.ofWire: unknown tag '%c' in %s" tag s

/// Binary encoding of a `Value`, mirroring `toWire`'s tag scheme but
/// length-prefixed rather than base64-encoded — `decodeValue (encodeValue v) = v`
/// for every case. The WAL and the snapshot both use this; `toWire` stays as
/// the human-readable tagged-text rendering (round-trip-tested in `ValueTests`).
let encodeValue (w: Writer) (v: Value) : unit =
    match v with
    | VNull -> w.WriteByte 0x00uy
    | VInt i ->
        w.WriteByte 0x01uy
        w.WriteInt64LE i
    // Same eight bytes as `VInt`, a distinct tag: the payload's *signedness*
    // is what the tag carries, and reinterpreting the bit pattern on the way
    // back is exact for the whole 64-bit range.
    | VUInt u ->
        w.WriteByte 0x09uy
        w.WriteInt64LE(int64 u)
    | VBit(width, value) ->
        w.WriteByte 0x0duy
        w.WriteInt32LE width
        w.WriteInt64LE(int64 value)
    | VDouble d ->
        w.WriteByte 0x02uy
        w.WriteDoubleLE d
    | VDecimal d ->
        w.WriteByte 0x03uy
        let bits = Decimal.GetBits d
        for i in 0..3 do
            w.WriteInt32LE bits.[i]
    | VString s ->
        w.WriteByte 0x04uy
        w.WriteLenEncString s
    | VBytes b ->
        w.WriteByte 0x05uy
        w.WriteLenEncBytes b
    | VDate d ->
        w.WriteByte 0x06uy
        w.WriteInt32LE d.DayNumber
    | VDateTime dt ->
        w.WriteByte 0x07uy
        w.WriteInt64LE dt.Ticks
        w.WriteByte(byte (int dt.Kind))
    | VZeroDate d ->
        let year, month, day = zeroDateParts d
        w.WriteByte 0x0buy
        w.WriteInt32LE year
        w.WriteInt32LE month
        w.WriteInt32LE day
    | VZeroDateTime dt ->
        let date, hour, minute, second, microseconds = zeroDateTimeParts dt
        let year, month, day = zeroDateParts date
        w.WriteByte 0x0cuy
        w.WriteInt32LE year
        w.WriteInt32LE month
        w.WriteInt32LE day
        w.WriteInt32LE hour
        w.WriteInt32LE minute
        w.WriteInt32LE second
        w.WriteInt32LE microseconds
    | VJson j ->
        w.WriteByte 0x08uy
        w.WriteLenEncString j
    | VGeometry geometry ->
        w.WriteByte 0x0Auy
        w.WriteLenEncBytes(geometryToMySqlBinary geometry)

/// Inverse of `encodeValue`. Throws on malformed input, same contract as
/// `ofWire`.
let decodeValue (r: #IReader) : Value =
    match r.ReadByte() with
    | 0x00uy -> VNull
    | 0x01uy -> VInt(r.ReadInt64LE())
    | 0x09uy -> VUInt(uint64 (r.ReadInt64LE()))
    | 0x0duy -> VBit(r.ReadInt32LE(), uint64 (r.ReadInt64LE()))
    | 0x02uy -> VDouble(BitConverter.Int64BitsToDouble(r.ReadInt64LE()))
    | 0x03uy ->
        let bits = [| for _ in 0..3 -> r.ReadInt32LE() |]
        VDecimal(new decimal (bits))
    | 0x04uy -> VString(r.ReadLenEncString() |> Option.defaultValue "")
    | 0x05uy ->
        r.ReadLenEncInt()
        |> Option.map (fun n -> r.ReadBytes(int n))
        |> Option.defaultValue [||]
        |> VBytes
    | 0x06uy -> VDate(DateOnly.FromDayNumber(r.ReadInt32LE()))
    | 0x07uy ->
        let ticks = r.ReadInt64LE()

        let kind =
            match r.ReadByte() with
            | 0uy -> DateTimeKind.Unspecified
            | 1uy -> DateTimeKind.Utc
            | _ -> DateTimeKind.Local

        VDateTime(new DateTime(ticks, kind))
    | 0x08uy -> VJson(r.ReadLenEncString() |> Option.defaultValue "")
    | 0x0Auy ->
        let bytes =
            r.ReadLenEncInt()
            |> Option.map (fun length -> r.ReadBytes(int length))
            |> Option.defaultValue [||]

        match tryGeometryFromMySqlBinary bytes with
        | Some geometry -> VGeometry geometry
        | None -> failwith "Value.decodeValue: invalid geometry payload"
    | 0x0buy ->
        let year, month, day = r.ReadInt32LE(), r.ReadInt32LE(), r.ReadInt32LE()
        tryZeroDate year month day |> Option.map VZeroDate |> Option.defaultWith (fun () -> failwith "Value.decodeValue: invalid zero date")
    | 0x0cuy ->
        let year, month, day = r.ReadInt32LE(), r.ReadInt32LE(), r.ReadInt32LE()
        let hour, minute, second, microseconds = r.ReadInt32LE(), r.ReadInt32LE(), r.ReadInt32LE(), r.ReadInt32LE()
        tryZeroDate year month day
        |> Option.bind (fun date -> tryZeroDateTime date hour minute second microseconds)
        |> Option.map VZeroDateTime
        |> Option.defaultWith (fun () -> failwith "Value.decodeValue: invalid zero datetime")
    | tag -> failwithf "Value.decodeValue: unknown tag 0x%02x" tag

/// The MySQL wire type this value's runtime shape reports as, so a
/// resultset's column definition can match the value instead of a blanket
/// VAR_STRING (see the `ponytail` history on `Protocol.columnDefPayload`).
/// This matters for real client drivers: PHP's mysqlnd, in particular,
/// auto-converts a LONGLONG/DOUBLE/DATE/DATETIME-typed column to a native
/// int/float/string even over the text protocol, based on this byte —
/// app code that does `$model->foo_id === $other->id` only gets the
/// native-int conversion real MySQL gives it if fsdb reports the same
/// type real MySQL would, instead of leaving every column a string for
/// the client to coerce.
let mysqlMetadataOf (v: Value) : ColumnMetadata =
    match v with
    // No data to type; NULL round-trips the same regardless of the
    // declared column type, so the caller's fallback (typically
    // VAR_STRING) is as good as anything else here.
    | VNull -> columnMetadata TypeVarString
    | VInt _ -> columnMetadata TypeLongLong
    | VUInt _ -> { columnMetadata TypeLongLong with Flags = UnsignedFlag }
    | VBit(width, _) -> { columnMetadata TypeBit with ColumnLength = uint32 width }
    | VDouble _ -> columnMetadata TypeDouble
    | VDecimal _ -> columnMetadata TypeNewDecimal
    | VString _
    | VJson _ -> columnMetadata TypeVarString
    | VGeometry _ -> { columnMetadata TypeGeometry with Flags = BlobFlag ||| BinaryFlag }
    | VBytes _ -> { columnMetadata TypeBlob with Flags = BlobFlag ||| BinaryFlag }
    | VDate _ -> columnMetadata TypeDate
    | VDateTime _ -> columnMetadata TypeDateTime
    | VZeroDate _ -> columnMetadata TypeDate
    | VZeroDateTime _ -> columnMetadata TypeDateTime

let mysqlTypeOf (v: Value) : byte = (mysqlMetadataOf v).TypeId

/// Matches the leading numeric prefix of a string the way MySQL's
/// string-to-number cast does (`'12abc' + 0` = 12, `'abc' + 0` = 0),
/// scanning as much of an optionally-signed float as it can rather than
/// requiring the whole string to be numeric.
let private leadingNumeric =
    Regex(@"^\s*[-+]?(\d+\.\d*|\.\d+|\d+)([eE][-+]?\d+)?")

let private parseLeadingNumeric (s: string) : float =
    let m = leadingNumeric.Match s

    if m.Success then
        match Double.TryParse(m.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, d -> d
        | false, _ -> 0.0
    else
        0.0

/// MySQL's implicit numeric coercion, used by both comparison and
/// arithmetic: numeric types convert directly, strings parse their leading
/// numeric prefix, and anything else coerces through its text rendering.
let toDouble (v: Value) : float =
    match v with
    | VNull -> 0.0
    | VInt i -> float i
    | VUInt u -> float u
    | VBit(_, value) -> float value
    | VDouble d -> d
    | VDecimal d -> float d
    | VString s -> parseLeadingNumeric s
    | VBytes _
    | VDate _
    | VDateTime _
    | VZeroDate _
    | VZeroDateTime _
    | VJson _
    | VGeometry _ -> v |> toText |> Option.map parseLeadingNumeric |> Option.defaultValue 0.0

/// String comparison matching MySQL 8's default collation,
/// utf8mb4_0900_ai_ci: case-insensitive ("ai" is accent-insensitive, which
/// .NET's OrdinalIgnoreCase doesn't model — ponytail: ASCII/Latin case
/// folding only, add real accent folding if a _ai collation edge case
/// actually matters) and PAD SPACE-insensitive, so `'a' = 'a '` is true the
/// MySQL 8's default collation (utf8mb4_0900_ai_ci) — accent- and
/// case-insensitive, NO PAD (trailing spaces significant) — delegated to
/// the one home for those rules, `Collation`. This is the *folded* order
/// (accent/case-only differences compare equal, returning 0) because
/// `compare` drives equality everywhere — `equals`, hash-join keys, unique
/// lookups. `ORDER BY`'s tie-breaks among equal-primary strings use
/// `compareTotal` instead.
let private compareStrings (x: string) (y: string) : int =
    Collation.defaultCollation.ComparePrimary x y |> sign

/// Byte-lexicographic (memcmp-style) order for `VARBINARY`/`BLOB` content:
/// compare byte-by-byte, shorter-is-less only once every shared prefix byte
/// ties. F#'s structural `compare` on `byte[]` compares length first, which
/// puts `UNHEX('02')` (length 1) before `UNHEX('0101')` (length 2) even
/// though byte 0x02 is greater than byte 0x01 — wrong versus MySQL's binary
/// collation.
let private compareBytesLex (x: byte[]) (y: byte[]) : int =
    let len = min x.Length y.Length
    let mutable i = 0
    let mutable result = 0

    while result = 0 && i < len do
        result <- Operators.compare x.[i] y.[i]
        i <- i + 1

    if result <> 0 then result else Operators.compare x.Length y.Length

/// `VDate`/`VDateTime` as one .NET `DateTime`, midnight for the date-only
/// case — the shared instant `compare`'s VDate/VDateTime-vs-VString branch
/// parses a string bound against.
let private asDateTime (v: Value) : DateTime =
    match v with
    | VDate d -> d.ToDateTime(TimeOnly.MinValue)
    | VDateTime dt -> dt
    | _ -> invalidArg "v" "asDateTime expects VDate or VDateTime"

/// MySQL's JSON comparison precedence, ascending (the manual lists it
/// descending, highest first): JSON NULL < number < string < object <
/// array < boolean < date < time < datetime < opaque < blob. The *type*
/// decides the order before the content does, and comparing a JSON value
/// against a non-JSON one converts the non-JSON side to JSON first — which
/// is why `JSON_EXTRACT('{"n":1}','$.n') = '1'` is FALSE (JSON number vs
/// JSON string) while `= 1` is TRUE, and why the rendered-text comparison
/// this replaced got `'{"s":"abc"}'->'$.s' = 'abc'` wrong (it compared the
/// quoted `"abc"` against the bare `abc`).
/// https://dev.mysql.com/doc/refman/8.4/en/json.html#json-comparison
///
/// ponytail: TIME and OPAQUE have no `Value` case to reach them, and
/// fsdb has no BIT type, so those ranks are unreachable placeholders —
/// widen when those types land.
let private jsonRankOfNode (node: JsonNode) : int =
    if isNull (box node) then
        0
    else
        match node.GetValueKind() with
        | JsonValueKind.Null -> 0
        | JsonValueKind.Number -> 1
        | JsonValueKind.String -> 2
        | JsonValueKind.Object -> 3
        | JsonValueKind.Array -> 4
        | JsonValueKind.True
        | JsonValueKind.False -> 5
        | _ -> 2

/// A `Value` as the (rank, node) pair `compareJson` orders by. Types with
/// no JSON scalar shape (dates, binary) keep their SQL rank and compare
/// against their own kind through `compare`'s ordinary rules, so the node
/// is unused for them.
let private asJsonOperand (v: Value) : int * JsonNode =
    let node (s: string) =
        try
            JsonNode.Parse s
        with _ ->
            JsonValue.Create s

    match v with
    | VJson j -> let n = node j in jsonRankOfNode n, n
    | VInt i -> 1, JsonValue.Create i
    | VUInt u -> 1, JsonValue.Create u
    | VBit(_, value) -> 1, JsonValue.Create value
    | VDouble d -> 1, JsonValue.Create d
    | VDecimal d -> 1, JsonValue.Create d
    | VString s -> 2, JsonValue.Create s
    | VDate _ -> 6, null
    | VZeroDate _ -> 6, null
    | VDateTime _ -> 8, null
    | VZeroDateTime _ -> 8, null
    | VBytes _ -> 11, null
    | VGeometry _ -> 11, null
    | VNull -> 0, null

/// A JSON number's exact value where `decimal` can hold it (so two BIGINTs
/// past 2^53 stay distinct), its `double` otherwise.
let private jsonNumber (node: JsonNode) : Choice<decimal, float> =
    let text = node.ToJsonString()

    match Decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, d -> Choice1Of2 d
    | false, _ ->
        match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, d -> Choice2Of2 d
        | false, _ -> Choice2Of2 0.0

/// Orders two same-ranked JSON nodes. JSON strings compare by code unit
/// (binary, NOT the ai_ci collation SQL strings use — the oracle says
/// `CAST('"a"' AS JSON) = CAST('"A"' AS JSON)` is 0), arrays compare
/// element-wise then by length, and objects compare by their sorted keys
/// then the values under them.
let rec private compareJsonNodes (x: JsonNode) (y: JsonNode) : int =
    match jsonRankOfNode x with
    | 0 -> 0
    | 1 ->
        match jsonNumber x, jsonNumber y with
        | Choice1Of2 a, Choice1Of2 b -> Decimal.Compare(a, b)
        | a, b ->
            let asFloat = function
                | Choice1Of2 (d: decimal) -> float d
                | Choice2Of2 f -> f

            Operators.compare (asFloat a) (asFloat b)
    | 2 -> String.CompareOrdinal(x.GetValue<string>(), y.GetValue<string>()) |> sign
    | 5 -> Operators.compare (x.GetValue<bool>()) (y.GetValue<bool>())
    | 4 ->
        let a = x :?> JsonArray
        let b = y :?> JsonArray

        Seq.zip a b
        |> Seq.tryPick (fun (l, r) -> match compareJsonNodes l r with 0 -> None | c -> Some c)
        |> Option.defaultWith (fun () -> Operators.compare a.Count b.Count)
    | 3 ->
        let keys (o: JsonObject) = o |> Seq.map _.Key |> Seq.sortWith (fun l r -> String.CompareOrdinal(l, r)) |> List.ofSeq
        let a = x :?> JsonObject
        let b = y :?> JsonObject
        let ka, kb = keys a, keys b

        match Operators.compare ka kb with
        | 0 ->
            ka
            |> List.tryPick (fun k -> match compareJsonNodes a.[k] b.[k] with 0 -> None | c -> Some c)
            |> Option.defaultValue 0
        | c -> c
    | _ -> 0

/// Total order over values for ORDER BY: NULL sorts first, numbers compare
/// numerically (a number vs. a string coerces the string to a double, so
/// `'10' < '9'` numerically even though it's false as a string compare),
/// same-typed values compare natively (strings per `compareStrings`'s
/// collation), and anything else falls back to a text compare.
let rec compare (a: Value) (b: Value) : int =
    match a, b with
    | VNull, VNull -> 0
    | VNull, _ -> -1
    | _, VNull -> 1
    // A JSON operand pulls the whole comparison into the JSON domain (see
    // `jsonRankOfNode`): type precedence first, content second.
    | VJson _, _
    | _, VJson _ ->
        let ra, na = asJsonOperand a
        let rb, nb = asJsonOperand b

        match Operators.compare ra rb with
        // Ranks that carry no JSON node (dates, binary) still have to order
        // among themselves; their SQL comparison is the same order.
        | 0 when ra >= 6 -> compareStrings (toText a |> Option.defaultValue "") (toText b |> Option.defaultValue "")
        | 0 -> compareJsonNodes na nb
        | c -> c
    | VDecimal x, VDecimal y -> Decimal.Compare(x, y)
    | VInt x, VInt y -> Operators.compare x y
    // The unsigned 64-bit domain and the signed one only overlap on
    // [0, 2^63); `decimal` holds both exactly, so promoting is the one
    // comparison that stays right at both ends (`toDouble` would merge
    // distinct values past 2^53, and a naive `int64`/`uint64` cast would
    // make -1 the largest value there is).
    | VUInt x, VUInt y -> Operators.compare x y
    | VBit(_, x), VBit(_, y) -> Operators.compare x y
    | VUInt x, VInt y -> Decimal.Compare(decimal x, decimal y)
    | VInt x, VUInt y -> Decimal.Compare(decimal x, decimal y)
    | VUInt x, VDecimal y -> Decimal.Compare(decimal x, y)
    | VDecimal x, VUInt y -> Decimal.Compare(x, decimal y)
    | VString x, VString y -> compareStrings x y
    | VBytes x, VBytes y -> compareBytesLex x y
    // A binary string against a character string compares byte-for-byte
    // (MySQL: `CONVERT('abc' USING binary) = 'ABC'` is false), not via the
    // character collation the generic text fallback below would apply.
    | VBytes x, VString s -> compareBytesLex x (Text.Encoding.UTF8.GetBytes s)
    | VString s, VBytes y -> compareBytesLex (Text.Encoding.UTF8.GetBytes s) y
    | VGeometry x, VGeometry y -> compareBytesLex (geometryToMySqlBinary x) (geometryToMySqlBinary y)
    | VDate x, VDate y -> Operators.compare x y
    | VDateTime x, VDateTime y -> Operators.compare x y
    | VZeroDate x, VZeroDate y -> compareZeroDates x y
    | VZeroDateTime x, VZeroDateTime y -> compareZeroDateTimes x y
    | VDate x, VDateTime y -> Operators.compare (x.ToDateTime(TimeOnly.MinValue)) y
    | VDateTime x, VDate y -> Operators.compare x (y.ToDateTime(TimeOnly.MinValue))
    | VZeroDate x, VDate y -> compareZeroDateToDateTime x (y.ToDateTime TimeOnly.MinValue)
    | VDate y, VZeroDate x -> -(compareZeroDateToDateTime x (y.ToDateTime TimeOnly.MinValue))
    | VZeroDate x, VZeroDateTime y -> compareZeroDateToZeroDateTime x y
    | VZeroDateTime y, VZeroDate x -> -(compareZeroDateToZeroDateTime x y)
    | VZeroDate x, VDateTime y -> compareZeroDateToDateTime x y
    | VDateTime y, VZeroDate x -> -(compareZeroDateToDateTime x y)
    | VZeroDateTime x, VDate y -> compareZeroDateTimeToDateTime x (y.ToDateTime TimeOnly.MinValue)
    | VDate y, VZeroDateTime x -> -(compareZeroDateTimeToDateTime x (y.ToDateTime TimeOnly.MinValue))
    | VZeroDateTime x, VDateTime y -> compareZeroDateTimeToDateTime x y
    | VDateTime y, VZeroDateTime x -> -(compareZeroDateTimeToDateTime x y)
    | (VDate _ | VDateTime _ | VZeroDate _ | VZeroDateTime _), VString s ->
        // A literal like a `WHERE date BETWEEN '2024-01-01 00:00:00' AND
        // ...` bound is still a bare VString here (nothing coerces it to the
        // column's type ahead of the comparison) — parsed as a real instant
        // and compared temporally when it looks like one, the same as real
        // MySQL's DATE/DATETIME-vs-string coercion. Unparseable text falls
        // back to a text compare rather than erroring, matching every other
        // case here. Without this, `VDate "2024-01-01"`.`toText` ("2024-01-01",
        // no time part) sorted *before* "2024-01-01 00:00:00" as plain text —
        // a same-day BETWEEN lower bound excluded rows it should include.
        match a, DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | VZeroDate _, _
        | VZeroDateTime _, _ -> compareStrings (toText a |> Option.defaultValue "") s
        | _, (true, dt) -> Operators.compare (asDateTime a) dt
        | _, (false, _) -> compareStrings (toText a |> Option.defaultValue "") s
    | VString _, (VDate _ | VDateTime _ | VZeroDate _ | VZeroDateTime _) -> -(compare b a)
    // BIGINT vs DECIMAL with neither side a DOUBLE: promote both to
    // `decimal` and compare exactly. Routing this through `toDouble`
    // (float has only 53 bits of mantissa) silently merges distinct
    // int64/decimal values above 2^53 — wrong for equality, hash-join
    // keys, and unique-index lookups alike.
    | VInt x, VDecimal y -> Decimal.Compare(decimal x, y)
    | VDecimal x, VInt y -> Decimal.Compare(x, decimal y)
    | VBit _, (VInt _ | VUInt _ | VDouble _ | VDecimal _)
    | (VInt _ | VUInt _ | VDouble _ | VDecimal _), VBit _ -> Operators.compare (toDouble a) (toDouble b)
    | (VInt _ | VUInt _ | VDouble _ | VDecimal _), _
    | _, (VInt _ | VUInt _ | VDouble _ | VDecimal _) -> Operators.compare (toDouble a) (toDouble b)
    | _ -> compareStrings (toText a |> Option.defaultValue "") (toText b |> Option.defaultValue "")

/// The `ORDER BY` total order: `compare` first (folded — accent/case-only
/// differences tie at 0), then the collation's full secondary/tertiary
/// weights break the tie, exactly as MySQL's ai_ci sorts equal-primary
/// strings. Only the sort sites use this; equality/joins/keys keep `compare`.
let compareTotal (a: Value) (b: Value) : int =
    match a, b with
    | VString x, VString y ->
        match Collation.defaultCollation.ComparePrimary x y with
        | 0 -> Collation.defaultCollation.Compare x y |> sign
        | c -> sign c
    | _ -> compare a b

/// Translates a SQL LIKE pattern to a .NET regex source: `%` -> `.*`, `_` ->
/// `.`, and an escape-prefixed `%`/`_` (backslash by default, or whatever an
/// `ESCAPE '<c>'` clause names — see `escapeChar`) matches that character
/// literally instead. Anchored with `\A`/`\z` rather than `^`/`$` — `$`
/// alone matches before a trailing newline, which would let `'ab\n' LIKE
/// 'ab'` falsely match — and callers must pass `RegexOptions.Singleline` so
/// `.` spans newlines too (`%`/`_` are unqualified wildcards in MySQL, not
/// "everything but a newline"). One definition shared by `Executor`'s `LIKE`
/// operator, `QueryHandler`'s `SHOW ... LIKE`, and `Functions`'s
/// `JSON_SEARCH`, instead of three copies of the same escaping rules.
let likeToRegexWith (escapeChar: char) (pattern: string) : string =
    let sb = Text.StringBuilder()
    let mutable i = 0

    while i < pattern.Length do
        match pattern.[i] with
        // An escape-prefixed char matches literally, whatever it is. This
        // covers the escape char escaping itself (`\\` in the pattern
        // means one literal backslash) and `\<ordinary char>`, both of
        // which MySQL's LIKE also treats as that literal char.
        | c when c = escapeChar && i + 1 < pattern.Length ->
            sb.Append(Regex.Escape(string pattern.[i + 1])) |> ignore
            i <- i + 2
        | '%' ->
            sb.Append(".*") |> ignore
            i <- i + 1
        | '_' ->
            sb.Append(".") |> ignore
            i <- i + 1
        | c ->
            sb.Append(Regex.Escape(string c)) |> ignore
            i <- i + 1

    @"\A" + sb.ToString() + @"\z"

/// `LIKE` without an `ESCAPE` clause: MySQL's default escape is backslash,
/// which is also what `Parser.stringChar` leaves unresolved in the pattern
/// string for this purpose.
let likeToRegex (pattern: string) : string = likeToRegexWith '\\' pattern

/// WHERE-style equality with MySQL's implicit coercion (`1 = '1'` is true).
/// Three-valued logic: NULL never equals anything, including another NULL.
let equals (a: Value) (b: Value) : bool option =
    match a, b with
    | VNull, _
    | _, VNull -> None
    | _ -> Some(compare a b = 0)

/// A value's truth in a boolean context (WHERE/IF/AND/OR): NULL is unknown,
/// otherwise MySQL treats "coerces to zero" as false.
let truthy (v: Value) : bool option =
    match v with
    | VNull -> None
    | _ -> Some(toDouble v <> 0.0)

/// The three numeric kinds arithmetic promotes between; anything
/// non-numeric coerces through `toDouble` like MySQL's implicit cast.
type private NumKind =
    | KInt of int64
    /// `BIGINT UNSIGNED`. Kept apart from `KInt` because MySQL's promotion
    /// rules make an unsigned operand win over a signed one (`+`/`-`/`*`/
    /// `MOD` on unsigned-and-signed yield unsigned), not because the
    /// arithmetic itself differs — that runs in `decimal` either way.
    | KUInt of uint64
    | KDecimal of decimal
    | KDouble of float

let private classify (v: Value) : NumKind option =
    match v with
    | VNull -> None
    | VInt i -> Some(KInt i)
    | VUInt u -> Some(KUInt u)
    | VBit(_, value) -> Some(KUInt value)
    | VDecimal d -> Some(KDecimal d)
    | VDouble d -> Some(KDouble d)
    | VString _
    | VBytes _
    | VDate _
    | VDateTime _
    | VZeroDate _
    | VZeroDateTime _
    | VJson _
    | VGeometry _ -> Some(KDouble(toDouble v))

let private asDouble =
    function
    | KInt i -> float i
    | KUInt u -> float u
    | KDecimal d -> float d
    | KDouble d -> d

let private asDecimal =
    function
    | KInt i -> decimal i
    | KUInt u -> decimal u
    | KDecimal d -> d
    | KDouble d -> decimal d

/// The largest `BIGINT UNSIGNED`, as a `decimal` — the ceiling the exact
/// integral operations narrow back through.
let private maxUInt64 = decimal UInt64.MaxValue

/// Narrows an exact `decimal` result of unsigned-domain arithmetic back to
/// `VUInt` when it lands inside `BIGINT UNSIGNED`, keeping the operation's
/// unsigned result type the way MySQL does (`unsigned - signed` is
/// unsigned), and refusing outright when it doesn't: MySQL raises 1690 for
/// `CAST(1 AS UNSIGNED) - 2` and `CAST(-1 AS UNSIGNED) * 2` rather than
/// answering in a wider type, and a returned `DECIMAL` there is a wrong
/// answer a caller cannot tell from a right one.
///
/// An exception, not a `Result`: `Value`'s arithmetic has no error channel
/// and every operator plus every aggregate would have to grow one.
/// `QueryHandler.handle` turns this into the ERR packet.
///
/// A non-integral `decimal` (a fractional intermediate inside a still-exact
/// promotion) is in-domain and just stays a `DECIMAL`.
exception UnsignedOutOfRange

let narrowUnsigned (d: decimal) : Value =
    if d >= 0m && d <= maxUInt64 then
        if Decimal.Truncate d = d then VUInt(uint64 d) else VDecimal d
    else
        raise UnsignedOutOfRange

/// MySQL arithmetic type promotion: int op int stays int; decimal involved
/// (with no double operand) promotes to decimal; a double operand (or a
/// non-numeric one, which coerces through double) promotes to double.
/// NULL propagates through any operand, per SQL's `NULL + 1 = NULL`.
let private arith
    (opInt: int64 -> int64 -> int64)
    (opDec: decimal -> decimal -> decimal)
    (opDbl: float -> float -> float)
    (a: Value)
    (b: Value)
    : Value =
    match classify a, classify b with
    | None, _
    | _, None -> VNull
    | Some(KInt x), Some(KInt y) ->
        // MySQL errors (1690) on int64 overflow rather than wrapping to a
        // bogus negative; there's no error channel to `Value` arithmetic
        // here, so the safe compat move is to promote to `decimal` (exact,
        // just like MySQL's own DECIMAL fallback for oversized literals).
        try
            VInt(opInt x y)
        with :? OverflowException ->
            VDecimal(opDec (decimal x) (decimal y))
    // An unsigned operand against any exact integral one keeps MySQL's
    // unsigned result type. The arithmetic itself runs in `decimal`, which
    // covers the whole [0, 2^64) domain exactly and — unlike `uint64` —
    // survives a negative intermediate without wrapping.
    | Some(KUInt _ as ka), Some((KInt _ | KUInt _) as kb)
    | Some((KInt _) as ka), Some((KUInt _) as kb) ->
        try
            narrowUnsigned (opDec (asDecimal ka) (asDecimal kb))
        with :? OverflowException ->
            VDouble(opDbl (asDouble ka) (asDouble kb))
    | Some ka, Some kb ->
        match ka, kb with
        | KDouble _, _
        | _, KDouble _ -> VDouble(opDbl (asDouble ka) (asDouble kb))
        | _ -> VDecimal(opDec (asDecimal ka) (asDecimal kb))

let add = arith (Checked.(+)) (+) (+)
let sub = arith (Checked.(-)) (-) (-)
let mul = arith (Checked.( * )) ( * ) ( * )

/// A `decimal`'s own scale (digits after the point) as constructed —
/// `10.00m` reports 2, `5m` reports 0 — read straight out of its bit
/// representation the way `System.Decimal` stores it (bits 16-23 of the
/// fourth `int32`), since the BCL exposes no `.Scale` property.
let private scaleOf (d: decimal) : int = (Decimal.GetBits d).[3] >>> 16 &&& 0xFF

/// `/`'s `div_precision_increment`, MySQL's fixed default (`SELECT
/// @@div_precision_increment` is 4 on a stock install, and MySQL doesn't
/// let a plain expression see a session override reflected in its own
/// scale math the way this constant does) — the extra fractional digits
/// `/` adds on top of the dividend's own scale.
let private divPrecisionIncrement = 4

/// Rounds to `scale` fractional digits *and* pads short results back out to
/// it (`Math.Round(5m, 4)` is still `5m`, not `5.0000m`) — `decimal`
/// remembers trailing zeros baked into its own scale, so round-tripping
/// through a fixed-point format string is what actually forces it.
let private withScale (scale: int) (d: decimal) : decimal =
    // `decimal` only carries 28-29 significant digits; a dividend with a
    // large scale plus `divPrecisionIncrement` can ask for more fractional
    // digits than fit alongside its integer part. Clamp to `decimal`'s max
    // scale rather than let `Math.Round`/`Decimal.Parse` throw (MySQL error
    // 1105) — callers treat this as best-effort formatting, not validation.
    let scale = max 0 (min 28 scale)
    let rounded = Math.Round(d, scale, MidpointRounding.AwayFromZero)
    Decimal.Parse(rounded.ToString("F" + string scale, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)

/// `/`: MySQL divides exact-value operands (INT/DECIMAL) to a `DECIMAL`
/// whose scale is the *dividend's* scale plus `div_precision_increment`
/// — `5/2` is `2.5000`, `10.00/3` is `3.333333` (2 + 4) — rather than
/// truncating to either input's scale the way `+`/`-`/`*` do. A `DOUBLE`
/// operand anywhere taints the result to `DOUBLE` instead (no fixed
/// scale to pad), and division by zero is `NULL`, not an exception, for
/// either shape.
let div (a: Value) (b: Value) : Value =
    match classify a, classify b with
    | None, _
    | _, None -> VNull
    | Some ka, Some kb ->
        match ka, kb with
        | KDouble _, _
        | _, KDouble _ ->
            let y = asDouble kb
            if y = 0.0 then VNull else VDouble(asDouble ka / y)
        | _ ->
            let y = asDecimal kb
            if y = 0m then
                VNull
            else
                try
                    let dividendScale = match ka with KDecimal d -> scaleOf d | _ -> 0
                    VDecimal(withScale (dividendScale + divPrecisionIncrement) (asDecimal ka / y))
                with :? OverflowException ->
                    VNull

/// `MOD`/`%`: ordinary MySQL numeric promotion, same as `+`/`-`/`*` (int
/// op int stays int; a `DECIMAL` operand with no `DOUBLE` promotes to
/// `DECIMAL`, keeping `%`'s natural scale rather than `/`'s
/// `div_precision_increment` bump; a `DOUBLE` operand taints to
/// `DOUBLE`), except a zero divisor is `NULL` rather than a `DivideByZeroException`.
let modulo (a: Value) (b: Value) : Value =
    match classify a, classify b with
    | None, _
    | _, None -> VNull
    | Some ka, Some kb ->
        match ka, kb with
        | KInt x, KInt y -> if y = 0L then VNull else VInt(x % y)
        // Unsigned wins the same way `+`/`-`/`*` promote it.
        | (KUInt _, (KInt _ | KUInt _) | KInt _, KUInt _) ->
            let y = asDecimal kb
            if y = 0m then VNull else narrowUnsigned (asDecimal ka % y)
        | KDouble _, _
        | _, KDouble _ ->
            let y = asDouble kb
            if y = 0.0 then VNull else VDouble(asDouble ka % y)
        | _ ->
            let y = asDecimal kb
            if y = 0m then VNull else VDecimal(asDecimal ka % y)

/// `DIV`: MySQL's integer-division operator — always an `INT` (or `NULL`),
/// truncated toward zero, regardless of whether either operand is a
/// float/decimal (`7.5 DIV 2` is `3`, not `4`; MySQL doesn't pre-round the
/// operands, it truncates the quotient).
let intDiv (a: Value) (b: Value) : Value =
    match classify a, classify b with
    | None, _
    | _, None -> VNull
    | Some ka, Some kb ->
        let y = asDecimal kb

        if y = 0m then
            VNull
        else
            // Both the decimal division and the narrowing to int64 can
            // overflow (a huge dividend, or a divisor near zero); MySQL
            // errors (1105/1690) here, and the domain-appropriate stand-in
            // with no error channel is NULL rather than an internal crash.
            try
                let quotient = Math.Truncate(asDecimal ka / y)

                // `BIGINT UNSIGNED DIV` stays unsigned, so a quotient in the
                // top half of the range (`CAST(-1 AS UNSIGNED) DIV 1`) has
                // to survive rather than overflow `int64` into NULL.
                match ka, kb with
                | (KUInt _, _ | _, KUInt _) -> narrowUnsigned quotient
                | _ -> VInt(int64 quotient)
            with :? OverflowException ->
                VNull
