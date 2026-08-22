module Fsdb.Tests.ValueTests

open System
open System.Text
open Expecto
open Fsdb.Collation
open Fsdb.Value
open Fsdb.Functions
open Fsdb.Temporal

/// Looks up a builtin by name and applies it — the same path
/// `Executor.evalExpr`'s `FuncCall` case takes, minus the `Result` plumbing
/// around an unknown-function name that isn't this module's concern.
let private call (name: string) (args: Value list) : Value =
    match lookup name builtins with
    | Some fn -> fn args
    | None -> failwithf "no such builtin: %s" name

/// Same as `call`, against the aggregate half of the registry — the
/// `Value list` here plays the already-NULL-filtered, already-nonempty
/// per-group values `Executor.evalAggregate` would hand an `Aggregate`.
let private callAgg (name: string) (args: Value list) : Value =
    match lookupAggregate name builtins with
    | Some fn -> fn args
    | None -> failwithf "no such aggregate: %s" name

let tests =
    testList
        "Value"
        [ testList
              "toText"
              [ testCase "VNull renders as None (the lenenc-null wire marker)"
                <| fun _ -> Expect.equal (toText VNull) None "null"

                testCase "VInt renders as a plain integer string"
                <| fun _ -> Expect.equal (toText (VInt 42L)) (Some "42") "int"

                testCase "VDouble renders without float noise"
                <| fun _ -> Expect.equal (toText (VDouble 1.5)) (Some "1.5") "double"

                testCase "VDecimal renders without trailing exponent notation"
                <| fun _ -> Expect.equal (toText (VDecimal 12.50M)) (Some "12.50") "decimal"

                testCase "VString renders unchanged"
                <| fun _ -> Expect.equal (toText (VString "hi")) (Some "hi") "string"

                testCase "VDate renders as yyyy-MM-dd"
                <| fun _ ->
                    Expect.equal (toText (VDate(DateOnly(2024, 3, 5)))) (Some "2024-03-05") "date"

                testCase "VDateTime renders as yyyy-MM-dd HH:mm:ss"
                <| fun _ ->
                    Expect.equal
                        (toText (VDateTime(DateTime(2024, 3, 5, 13, 45, 9))))
                        (Some "2024-03-05 13:45:09")
                        "datetime"

                testCase "VDateTime with a sub-second component renders 6 fractional digits"
                <| fun _ ->
                    // 1_234_560 ticks past the second = 123456 microseconds
                    // (ticks are 100 ns). MySQL's DATETIME(6) keeps these and
                    // renders exactly six digits.
                    Expect.equal
                        (toText (VDateTime(DateTime(2024, 3, 5, 13, 45, 9).AddTicks 1_234_560L)))
                        (Some "2024-03-05 13:45:09.123456")
                        "datetime with microseconds"

                testCase "VDateTime with an exactly-half-second component pads to 6 digits"
                <| fun _ ->
                    Expect.equal
                        (toText (VDateTime(DateTime(2024, 3, 5, 13, 45, 9, 500))))
                        (Some "2024-03-05 13:45:09.500000")
                        "datetime half-second pads trailing zeros like MySQL DATETIME(6)"

                testCase "VDateTime with tiny microseconds pads LEADING zeros to 6 digits"
                <| fun _ ->
                    // 50 ticks = 5 microseconds — must render `.000005`,
                    // never an unpadded `.5` that reads as half a second.
                    Expect.equal
                        (toText (VDateTime(DateTime(2024, 3, 5, 13, 45, 9).AddTicks 50L)))
                        (Some "2024-03-05 13:45:09.000005")
                        "leading zeros are significant in the fraction"

                testCase "VTime renders signed hours beyond one day"
                <| fun _ ->
                    let time = tryParseTimeValue "-838:59:58.123456" |> Option.get
                    Expect.equal (toText (VTime time)) (Some "-838:59:58.123456") "time"
                    Expect.equal (toTextFsp 2 (VTime time)) (Some "-838:59:58.12") "declared precision"
                    Expect.equal (toText (VTime(timeValueOrClamp (12L * TimeSpan.TicksPerHour)))) (Some "12:00:00") "whole seconds"

                testCase "TIME parsing rejects oversized fields without overflowing"
                <| fun _ ->
                    Expect.isNone (tryParseTimeTicks "999999999999999999999999 00:00:00") "too many days"
                    Expect.isNone (tryTimeValue Int64.MinValue) "minimum int64"
                    Expect.equal (timeMagnitude Int64.MinValue) 9_223_372_036_854_775_808UL "safe magnitude"
                    Expect.equal (timeTicks (timeValueOrClamp Int64.MinValue)) -maxTimeTicks "clamped minimum"

                testCase "zero dates preserve zero components"
                <| fun _ ->
                    let date = tryZeroDate 2020 0 1 |> Option.get
                    Expect.equal (toText (VZeroDate date)) (Some "2020-00-01") "partial zero date"

                testCase "zero datetimes preserve their time component"
                <| fun _ ->
                    let date = tryZeroDate 0 0 0 |> Option.get
                    let dateTime = tryZeroDateTime date 12 34 56 123_000 |> Option.get
                    Expect.equal (toText (VZeroDateTime dateTime)) (Some "0000-00-00 12:34:56.123000") "zero datetime"

                testCase "VJson renders the raw text unchanged"
                <| fun _ -> Expect.equal (toText (VJson "{\"a\":1}")) (Some "{\"a\":1}") "json" ]

          testList
              "toWire/ofWire round-trip"
              [ testCase "every case round-trips, including sub-second precision toText would drop"
                <| fun _ ->
                    let values =
                        [ VNull
                          VInt -42L
                          VUInt UInt64.MaxValue
                          VUInt 0UL
                          VBit(9, 257UL)
                          VDouble 1.5
                          VDecimal 12.50M
                          VString "hi | there\nwith \"quotes\""
                          VBytes [| 0uy; 255uy; 1uy |]
                          VDate(DateOnly(2024, 3, 5))
                          VDateTime(DateTime(2024, 3, 5, 13, 45, 9, 123))
                          VTime(tryParseTimeValue "-838:59:58.123456" |> Option.get)
                          VZeroDate(tryZeroDate 2020 0 1 |> Option.get)
                          VZeroDateTime(tryZeroDateTime (tryZeroDate 0 0 0 |> Option.get) 0 0 0 0 |> Option.get)
                          VJson "{\"a\":1}"
                          VGeometry(tryGeometryFromText 4326 "POINT(1.5 -2)" |> Option.get) ]

                    for v in values do
                        Expect.equal (ofWire (toWire v)) v (sprintf "round-trip of %A" v)

                testCase "ofWire throws on an unrecognized tag"
                <| fun _ -> Expect.throws (fun () -> ofWire "?garbage" |> ignore) "bad tag" ]

          testList
              "geometry"
              [ testCase "WKT constructors preserve type, coordinates, and SRID through WKB"
                <| fun _ ->
                    let geometry = call "ST_GeomFromText" [ VString "POINT(1.5 -2)"; VInt 4326L ]

                    Expect.equal (call "ST_AsText" [ geometry ]) (VString "POINT(1.5 -2)") "canonical WKT"
                    Expect.equal (call "ST_SRID" [ geometry ]) (VInt 4326L) "SRID"
                    Expect.equal (call "ST_GeometryType" [ geometry ]) (VString "POINT") "type"
                    Expect.equal (call "ST_X" [ geometry ]) (VDouble 1.5) "x"
                    Expect.equal (call "ST_Y" [ geometry ]) (VDouble -2.0) "y"
                    Expect.equal
                        (call "ST_AsWKB" [ geometry ])
                        (VBytes(Convert.FromHexString "0101000000000000000000F83F00000000000000C0"))
                        "WKB excludes the SRID"
                    Expect.equal
                        (call "ST_AsText" [ call "ST_GeomFromWKB" [ call "ST_AsWKB" [ geometry ]; VInt 4326L ] ])
                        (VString "POINT(1.5 -2)")
                        "WKB round-trip"

                testCase "geometry collections retain nested shapes and dimensions"
                <| fun _ ->
                    let geometry = call "ST_GeomFromText" [ VString "GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1))" ]

                    Expect.equal (call "ST_GeometryType" [ geometry ]) (VString "GEOMCOLLECTION") "collection spelling"
                    Expect.equal (call "ST_Dimension" [ geometry ]) (VInt 1L) "highest member dimension"
                    Expect.equal (call "ST_IsEmpty" [ geometry ]) (VInt 0L) "not empty"

                testCase "an empty geometry collection retains its type"
                <| fun _ ->
                    let geometry = call "ST_GeomFromText" [ VString "GEOMETRYCOLLECTION EMPTY" ]

                    Expect.equal (call "ST_AsText" [ geometry ]) (VString "GEOMETRYCOLLECTION EMPTY") "empty WKT"
                    Expect.equal (call "ST_Dimension" [ geometry ]) VNull "empty dimension"
                    Expect.equal (call "ST_IsEmpty" [ geometry ]) (VInt 1L) "empty flag"
                    Expect.isNone (tryGeometryFromText 0 "POINT EMPTY") "MySQL rejects empty points"
                    Expect.isNone (tryGeometryFromText 0 "LINESTRING EMPTY") "MySQL rejects empty lines"
                    Expect.isNone (tryGeometryFromText 0 "POLYGON EMPTY") "MySQL rejects empty polygons"
                    Expect.isNone (tryGeometryFromText 0 "MULTIPOINT EMPTY") "MySQL rejects empty multipoints"
                    Expect.isNone (tryGeometryFromText 0 "MULTILINESTRING EMPTY") "MySQL rejects empty multilines"
                    Expect.isNone (tryGeometryFromText 0 "MULTIPOLYGON EMPTY") "MySQL rejects empty multipolygons"

                testCase "WKT rejects incomplete lines and open polygon rings"
                <| fun _ ->
                    Expect.isNone (tryGeometryFromText 0 "LINESTRING(1 2)") "line needs two points"
                    Expect.isNone (tryGeometryFromText 0 "POLYGON((0 0,0 1,1 1,2 2))") "ring must close"

                testCase "WKB rejects malformed concrete and empty shapes"
                <| fun _ ->
                    let parse (hex: string) = Convert.FromHexString hex |> tryGeometryFromWkb 0

                    Expect.isNone (parse "01020000000100000000000000000000000000000000000000") "line needs two points"
                    Expect.isNone (parse "0103000000010000000300000000000000000000000000000000000000000000000000F03F000000000000F03F00000000000000000000000000000000") "polygon ring needs four points"
                    Expect.isNone (parse "0103000000010000000400000000000000000000000000000000000000000000000000F03F0000000000000000000000000000F03F000000000000F03F00000000000000000000000000000040") "polygon ring must close"
                    Expect.isNone (parse "010200000000000000") "empty line"
                    Expect.isNone (parse "010300000000000000") "empty polygon"
                    Expect.isNone (parse "010400000000000000") "empty multipoint"
                    Expect.isNone (parse "010500000000000000") "empty multiline"
                    Expect.isNone (parse "010600000000000000") "empty multipolygon"
                    Expect.isNone (parse "010500000001000000010200000000000000") "empty multiline child"
                    Expect.isNone (parse "010600000001000000010300000000000000") "empty multipolygon child"
                    Expect.equal
                        (parse "010700000000000000" |> Option.map geometryToText)
                        (Some "GEOMETRYCOLLECTION EMPTY")
                        "empty collection"

                testCase "planar distance covers points, boundaries, holes, and collections"
                <| fun _ ->
                    let geometry text = call "ST_GeomFromText" [ VString text ]

                    Expect.equal
                        (call "ST_Distance" [ geometry "POINT(0 0)"; geometry "POINT(3 4)" ])
                        (VDouble 5.0)
                        "point distance"
                    Expect.equal
                        (call "ST_Distance" [ geometry "POINT(0 0)"; geometry "LINESTRING(3 0,3 4)" ])
                        (VDouble 3.0)
                        "point to line distance"
                    Expect.equal
                        (call "ST_Distance" [ geometry "POINT(1 0)"; geometry "LINESTRING(0 0,2 0)" ])
                        (VDouble 0.0)
                        "point on line distance"
                    Expect.equal
                        (call "ST_Distance" [ geometry "POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,3 1,3 3,1 3,1 1))"; geometry "POINT(2 2)" ])
                        (VDouble 1.0)
                        "hole boundary distance"
                    Expect.equal
                        (call "ST_Distance" [ geometry "GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(2 0,2 2))"; geometry "POINT(1 0)" ])
                        (VDouble 1.0)
                        "collection distance"
                    Expect.equal
                        (call "ST_Distance" [ geometry "GEOMETRYCOLLECTION EMPTY"; geometry "POINT(1 1)" ])
                        VNull
                        "empty distance"

                testCase "planar intersections distinguish contacts, holes, and empty shapes"
                <| fun _ ->
                    let geometry text = call "ST_GeomFromText" [ VString text ]
                    let intersects first second = call "ST_Intersects" [ geometry first; geometry second ]
                    let disjoint first second = call "ST_Disjoint" [ geometry first; geometry second ]
                    let holedPolygon = "POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,3 1,3 3,1 3,1 1))"

                    Expect.equal (intersects "POINT(1 1)" "LINESTRING(0 0,2 2)") (VInt 1L) "point on line"
                    Expect.equal (intersects "POINT(2 2)" holedPolygon) (VInt 0L) "point in hole"
                    Expect.equal (disjoint "POINT(2 2)" holedPolygon) (VInt 1L) "hole is disjoint"
                    Expect.equal (intersects "LINESTRING(0 0,4 0)" "LINESTRING(4 0,5 0)") (VInt 1L) "endpoint contact"
                    Expect.equal (disjoint "GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(2 0,2 2))" "POINT(1 0)") (VInt 1L) "collection is disjoint"
                    Expect.equal (intersects "GEOMETRYCOLLECTION EMPTY" "POINT(1 1)") VNull "empty intersection"
                    Expect.equal (disjoint "GEOMETRYCOLLECTION EMPTY" "POINT(1 1)") VNull "empty disjoint"

                testCase "planar topology predicates distinguish interiors, boundaries, and holes"
                <| fun _ ->
                    let geometry text = call "ST_GeomFromText" [ VString text ]
                    let contains first second = call "ST_Contains" [ geometry first; geometry second ]
                    let within first second = call "ST_Within" [ geometry first; geometry second ]
                    let touches first second = call "ST_Touches" [ geometry first; geometry second ]
                    let rectangle = "POLYGON((0 0,4 0,4 4,0 4,0 0))"
                    let donut = "POLYGON((0 0,5 0,5 5,0 5,0 0),(1 1,4 1,4 4,1 4,1 1))"

                    Expect.equal (contains rectangle "POINT(2 2)") (VInt 1L) "interior point"
                    Expect.equal (contains rectangle "POINT(0 2)") (VInt 0L) "boundary point"
                    Expect.equal (contains rectangle "LINESTRING(0 1,2 1)") (VInt 1L) "line may begin on a boundary"
                    Expect.equal (contains rectangle "LINESTRING(0 1,0 3)") (VInt 0L) "boundary line"
                    Expect.equal (contains donut "POLYGON((2 2,3 2,3 3,2 3,2 2))") (VInt 0L) "polygon in hole"
                    Expect.equal (within "POINT(2 2)" rectangle) (VInt 1L) "inverse predicate"
                    Expect.equal (contains "LINESTRING(0 0,4 0)" "POINT(2 0)") (VInt 1L) "line interior"
                    Expect.equal (contains "LINESTRING(0 0,4 0)" "POINT(0 0)") (VInt 0L) "line endpoint"
                    Expect.equal (contains "MULTIPOINT((1 1),(2 2))" "POINT(1 1)") (VInt 1L) "multipoint member"
                    Expect.equal (contains "GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(0 0,1 0))" "POINT(0 0)") (VInt 1L) "collection point at line endpoint"
                    Expect.equal (contains "GEOMETRYCOLLECTION(POINT(0 0),POLYGON((0 0,2 0,2 2,0 2,0 0)))" "POINT(0 0)") (VInt 1L) "collection point at polygon boundary"
                    Expect.equal (within "POINT(0 0)" "GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(0 0,1 0))") (VInt 1L) "collection point inverse predicate"
                    Expect.equal (touches rectangle "POINT(0 2)") (VInt 1L) "point on polygon boundary"
                    Expect.equal (touches "POINT(0 0)" "POINT(0 0)") VNull "point pair"
                    Expect.equal (touches "MULTIPOINT((0 0),(1 0))" "POINT(0 0)") VNull "multipoint pair"
                    Expect.equal (touches "GEOMETRYCOLLECTION(POINT(0 0))" "POINT(0 0)") VNull "point collection pair"
                    Expect.equal (touches "GEOMETRYCOLLECTION(GEOMETRYCOLLECTION EMPTY,POINT(0 0))" "POINT(0 0)") VNull "nested empty point collection pair"
                    Expect.equal (touches rectangle "LINESTRING(0 1,0 3)") (VInt 1L) "line on polygon boundary"
                    Expect.equal (touches "LINESTRING(0 0,2 0)" "LINESTRING(2 0,3 0)") (VInt 1L) "line endpoint"
                    Expect.equal (touches "LINESTRING(0 0,2 0)" "LINESTRING(1 0,3 0)") (VInt 0L) "line overlap"
                    Expect.equal (touches "MULTILINESTRING((0 0,1 0),(1 0,2 0))" "POINT(0 0)") (VInt 1L) "multiline odd endpoint"
                    Expect.equal (touches "MULTILINESTRING((0 0,1 0),(1 0,2 0))" "POINT(1 0)") (VInt 0L) "multiline even endpoint"
                    Expect.equal (touches rectangle "POLYGON((4 1,6 1,6 3,4 3,4 1))") (VInt 1L) "shared edge"
                    Expect.equal (touches rectangle "POLYGON((2 2,6 2,6 6,2 6,2 2))") (VInt 0L) "overlapping area"
                    Expect.equal (touches "GEOMETRYCOLLECTION(POINT(0 2),POINT(5 5))" rectangle) (VInt 1L) "collection boundary contact"
                    Expect.equal (touches "GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(0 0,1 0))" "POINT(0 0)") (VInt 1L) "collection point at line endpoint"
                    Expect.equal (touches "GEOMETRYCOLLECTION(LINESTRING(0 0,1 0),LINESTRING(1 0,2 0))" "POINT(1 0)") (VInt 0L) "collection shared line endpoint"
                    Expect.equal (contains "GEOMETRYCOLLECTION EMPTY" "POINT(1 1)") VNull "empty contains"
                    Expect.equal (touches "GEOMETRYCOLLECTION EMPTY" "POINT(1 1)") VNull "empty touches"

                testCase "planar intersections reject nonzero and mismatched SRIDs"
                <| fun _ ->
                    let expectError code invoke =
                        Expect.throwsC
                            (invoke >> ignore)
                            (function
                            | Fsdb.Functions.SqlError(actual, _) when actual = code -> ()
                            | error -> failtestf "expected %d, got %A" code error)

                    let planar = call "ST_GeomFromText" [ VString "POINT(0 0)" ]
                    let geographic = call "ST_GeomFromText" [ VString "POINT(0 0)"; VInt 4326L ]

                    expectError 3033 (fun () -> call "ST_Intersects" [ planar; geographic ])
                    expectError 1235 (fun () -> call "ST_Disjoint" [ geographic; geographic ])
                    expectError 3033 (fun () -> call "ST_Contains" [ planar; geographic ])
                    expectError 1235 (fun () -> call "ST_Within" [ geographic; geographic ])
                    expectError 1235 (fun () -> call "ST_Touches" [ geographic; geographic ])

                testCase "envelopes and MBR predicates preserve planar bounds"
                <| fun _ ->
                    let geometry text = call "ST_GeomFromText" [ VString text ]
                    let asText value = call "ST_AsText" [ value ]

                    Expect.equal (asText (call "ST_Envelope" [ geometry "POINT(1 2)" ])) (VString "POINT(1 2)") "point envelope"
                    Expect.equal (asText (call "ST_Envelope" [ geometry "LINESTRING(1 2,1 4)" ])) (VString "LINESTRING(1 2,1 4)") "line envelope"
                    Expect.equal (asText (call "ST_Envelope" [ geometry "MULTIPOINT(2 3,1 4)" ])) (VString "POLYGON((1 3,2 3,2 4,1 4,1 3))") "area envelope"
                    Expect.equal (asText (call "ST_Envelope" [ geometry "GEOMETRYCOLLECTION EMPTY" ])) (VString "GEOMETRYCOLLECTION EMPTY") "empty envelope"

                    let rectangle = geometry "POLYGON((0 0,4 0,4 4,0 4,0 0))"
                    let inside = geometry "POINT(2 2)"
                    let boundary = geometry "POINT(4 4)"

                    Expect.equal (call "MBRContains" [ rectangle; inside ]) (VInt 1L) "strictly inside"
                    Expect.equal (call "MBRWithin" [ inside; rectangle ]) (VInt 1L) "inverse predicate"
                    Expect.equal (call "MBRContains" [ rectangle; boundary ]) (VInt 0L) "boundary is excluded"
                    Expect.equal (call "MBRIntersects" [ rectangle; boundary ]) (VInt 1L) "boundary intersects"
                    Expect.equal (call "MBRIntersects" [ rectangle; geometry "POINT(5 4)" ]) (VInt 0L) "disjoint bounds"
                    Expect.equal (call "MBRContains" [ rectangle; geometry "POLYGON((0 1,2 1,2 3,0 3,0 1))" ]) (VInt 1L) "area may touch a boundary"

                testCase "planar geometry functions reject nonzero and mismatched SRIDs"
                <| fun _ ->
                    let expectError code invoke =
                        Expect.throwsC
                            (invoke >> ignore)
                            (function
                            | Fsdb.Functions.SqlError(actual, _) when actual = code -> ()
                            | error -> failtestf "expected %d, got %A" code error)

                    let planar = call "ST_GeomFromText" [ VString "POINT(0 0)" ]
                    let geographic = call "ST_GeomFromText" [ VString "POINT(0 0)"; VInt 4326L ]

                    expectError 3033 (fun () -> call "ST_Distance" [ planar; geographic ])
                    expectError 1235 (fun () -> call "ST_Distance" [ geographic; geographic ])
                    expectError 1235 (fun () -> call "ST_Envelope" [ geographic ])
                    expectError 1235 (fun () -> call "ST_IsValid" [ geographic ])

                testCase "ST_IsValid distinguishes simple planar geometry from invalid topology"
                <| fun _ ->
                    let geometry text = call "ST_GeomFromText" [ VString text ]
                    let valid text = call "ST_IsValid" [ geometry text ]

                    Expect.equal (valid "POINT(0 0)") (VInt 1L) "point"
                    Expect.equal (valid "GEOMETRYCOLLECTION EMPTY") (VInt 1L) "empty collection"
                    Expect.equal (valid "POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,3 1,3 3,1 3,1 1))") (VInt 1L) "interior hole"
                    Expect.equal (valid "POLYGON((0 0,2 2,0 2,2 0,0 0))") (VInt 0L) "self crossing ring"
                    Expect.equal (valid "POLYGON((0 0,4 0,4 4,0 4,0 0),(5 5,6 5,6 6,5 6,5 5))") (VInt 0L) "exterior hole"
                    Expect.equal (valid "POLYGON((0 0,4 0,4 4,0 4,0 0),(0 1,2 1,2 2,0 2,0 1))") (VInt 0L) "touching hole"
                    Expect.equal (valid "MULTIPOLYGON(((0 0,4 0,4 4,0 4,0 0)),((4 4,8 4,8 8,4 8,4 4)))") (VInt 1L) "vertex-touching components"
                    Expect.equal (valid "MULTIPOLYGON(((0 0,4 0,4 4,0 4,0 0)),((2 2,6 2,6 6,2 6,2 2)))") (VInt 0L) "overlapping components"
                    Expect.equal (valid "MULTIPOINT((0 0),(0 0))") (VInt 1L) "duplicate point"
                    Expect.equal (valid "LINESTRING(0 0,-0.00 0,0.0 0)") (VInt 0L) "line without distinct points"

                testCase "geometry validity rejects malformed in-memory component shapes"
                <| fun _ ->
                    let geometry shape = { Srid = 0; Shape = shape }

                    Expect.isFalse (geometry (GLineString [ (0.0, 0.0) ]) |> geometryIsValidPlanar) "short line"
                    Expect.isFalse (geometry (GLineString [ (0.0, 0.0); (0.0, 0.0) ]) |> geometryIsValidPlanar) "repeated line point"
                    Expect.isFalse (geometry (GMultiLineString [ [ (0.0, 0.0) ] ]) |> geometryIsValidPlanar) "short line"
                    Expect.isFalse (geometry (GMultiLineString [ [ (0.0, 0.0); (0.0, 0.0) ] ]) |> geometryIsValidPlanar) "repeated line point"
                    Expect.isFalse (geometry (GMultiPolygon [ [] ]) |> geometryIsValidPlanar) "missing shell" ]

          testList
              "mysqlTypeOf"
              [ testCase "VInt reports LONGLONG, so mysqlnd converts it to a native PHP int"
                <| fun _ -> Expect.equal (mysqlTypeOf (VInt 1L)) TypeLongLong "int"

                testCase "VDouble reports DOUBLE"
                <| fun _ -> Expect.equal (mysqlTypeOf (VDouble 1.5)) TypeDouble "double"

                testCase "VDecimal reports NEWDECIMAL"
                <| fun _ -> Expect.equal (mysqlTypeOf (VDecimal 12.5M)) TypeNewDecimal "decimal"

                testCase "VString reports VAR_STRING"
                <| fun _ -> Expect.equal (mysqlTypeOf (VString "hi")) TypeVarString "string"

                testCase "VBytes reports BLOB so clients preserve raw bytes"
                <| fun _ -> Expect.equal (mysqlTypeOf (VBytes [| 0uy; 255uy |])) TypeBlob "bytes"

                testCase "VDate reports DATE"
                <| fun _ -> Expect.equal (mysqlTypeOf (VDate(DateOnly(2024, 3, 5)))) TypeDate "date"

                testCase "VDateTime reports DATETIME"
                <| fun _ ->
                    Expect.equal (mysqlTypeOf (VDateTime(DateTime(2024, 3, 5, 13, 45, 9)))) TypeDateTime "datetime"

                testCase "VTime reports TIME"
                <| fun _ ->
                    let time = tryParseTimeValue "01:02:03" |> Option.get
                    Expect.equal (mysqlTypeOf (VTime time)) TypeTime "time"

                testCase "VNull falls back to VAR_STRING — NULL round-trips regardless of declared type"
                <| fun _ -> Expect.equal (mysqlTypeOf VNull) TypeVarString "null" ]

          testList
              "compare"
              [ testCase "NULL sorts before every other value"
                <| fun _ ->
                    Expect.isLessThan (compare VNull (VInt 0L)) 0 "null < int"
                    Expect.isLessThan (compare VNull (VString "")) 0 "null < string"

                testCase "NULL equals NULL under compare"
                <| fun _ -> Expect.equal (compare VNull VNull) 0 "null = null"

                testCase "two ints compare numerically"
                <| fun _ -> Expect.isLessThan (compare (VInt 2L) (VInt 10L)) 0 "2 < 10"

                testCase "a number vs. a numeric string coerces the string to a double"
                <| fun _ -> Expect.equal (compare (VInt 1L) (VString "1")) 0 "1 = '1'"

                testCase "numeric-string coercion beats lexical string ordering"
                <| fun _ ->
                    // '9' < '10' lexically but 9 > 10 is false numerically.
                    Expect.isLessThan (compare (VString "9") (VInt 10L)) 0 "'9' < 10 numerically"

                testCase "two strings compare lexically, not numerically"
                <| fun _ -> Expect.isGreaterThan (compare (VString "9") (VString "10")) 0 "'9' > '10' lexically"

                testCase "decimals compare exactly, without float rounding"
                <| fun _ -> Expect.equal (compare (VDecimal 1.10M) (VDecimal 1.1M)) 0 "1.10 = 1.1"

                testCase "BIT values compare exactly across the signed and unsigned boundary"
                <| fun _ ->
                    let highBit = VBit(64, 0x8000000000000000UL)
                    Expect.isGreaterThan (compare highBit (VInt Int64.MaxValue)) 0 "BIT value exceeds signed max"
                    Expect.isLessThan (compare highBit (VUInt 0x8000000000000001UL)) 0 "BIT value precedes the next unsigned integer"
                    Expect.equal (compare highBit (VDecimal 9223372036854775808m)) 0 "BIT value matches its exact decimal"
                    Expect.equal (compare highBit (VString "9223372036854775808")) 0 "BIT value matches its exact numeric string"
                    Expect.equal (compare highBit (VBytes [| 0x80uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 1uy |])) 0 "BIT value follows MySQL binary numeric coercion"
                    Expect.equal (compare (VBit(1, 1UL)) (VBytes [| 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 1uy |])) 0 "BIT value accepts a wide binary literal"
                    Expect.equal (compare (VBit(8, 0x41UL)) (VBytes [| 0x41uy |])) 0 "BIT value matches identical bytes"
                    Expect.equal (compare (VBit(8, 0x41UL)) (VBytes [| 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0x41uy |])) 0 "BIT byte coercion ignores leading zeroes"
                    Expect.isLessThan (compare (VBit(8, 0x41UL)) (VBytes [| 0x61uy |])) 0 "BIT bytes compare exactly"

                testCase "dates compare chronologically"
                <| fun _ ->
                    Expect.isLessThan
                        (compare (VDate(DateOnly(2024, 1, 1))) (VDate(DateOnly(2024, 6, 1))))
                        0
                        "jan < jun"

                testCase "times compare by signed duration"
                <| fun _ ->
                    let negative = tryParseTimeValue "-01:02:03" |> Option.get |> VTime
                    let long = tryParseTimeValue "25:00:00" |> Option.get |> VTime
                    Expect.isLessThan (compare negative long) 0 "negative precedes positive"
                    Expect.isGreaterThan (compare long (VString "02:00:00")) 0 "time strings are temporal bounds"

                testCase "partial zero dates sort before complete dates"
                <| fun _ ->
                    let zero = VZeroDate(tryZeroDate 2020 0 1 |> Option.get)
                    Expect.isLessThan (compare zero (VDate(DateOnly(2020, 1, 1)))) 0 "zero month precedes January"

                testCase "zero dates compare with zero datetimes as their midnight values"
                <| fun _ ->
                    let date = tryZeroDate 2020 1 0 |> Option.get
                    let midnight = tryZeroDateTime date 0 0 0 0 |> Option.get
                    let noon = tryZeroDateTime date 12 0 0 0 |> Option.get
                    Expect.equal (compare (VZeroDate date) (VZeroDateTime midnight)) 0 "midnight matches the date"
                    Expect.isLessThan (compare (VZeroDate date) (VZeroDateTime noon)) 0 "date precedes noon"

                testCase "zero components do not permit impossible calendar days"
                <| fun _ ->
                    Expect.isNone (tryZeroDate 0 2 31) "zero year still has February's calendar limit"
                    Expect.isSome (tryZeroDate 2020 0 31) "zero month permits day 31"
                    Expect.isNone (tryZeroDate 2020 0 32) "day 32 is never valid"

                testCase "a partial zero date compares with text without converting through DateTime"
                <| fun _ ->
                    let date = VZeroDate(tryZeroDate 2020 0 1 |> Option.get)
                    Expect.isLessThan (compare date (VString "2020-01-01")) 0 "zero month precedes January"

                testCase "string comparison is case-insensitive, matching utf8mb4_0900_ai_ci"
                <| fun _ -> Expect.equal (compare (VString "a") (VString "A")) 0 "'a' = 'A'"

                testCase "string comparison treats trailing spaces as significant, matching utf8mb4_0900_ai_ci's NO PAD"
                <| fun _ ->
                    Expect.equal (compare (VString "a") (VString "a ")) -1 "'a' < 'a '"
                    Expect.equal (compare (VString "a ") (VString "a")) 1 "'a ' > 'a'"

                // Every expectation below was verified against a live MySQL
                // 8.4 (utf8mb4_0900_ai_ci, the server default) — equality is
                // primary-weight based (accents and case fold; digraphs
                // expand), so indexes/unique keys/joins match MySQL exactly.
                testTheory
                    "the registry resolves every utf8mb4 collation MySQL 8.4 ships"
                    [ "utf8mb4_0900_ai_ci"; "utf8mb4_0900_as_ci"; "utf8mb4_0900_as_cs"; "utf8mb4_0900_bin"
                      "utf8mb4_bin"; "utf8mb4_general_ci"; "utf8mb4_unicode_ci"; "utf8mb4_unicode_520_ci"
                      "utf8mb4_da_0900_ai_ci"; "utf8mb4_da_0900_as_cs"
                      "utf8mb4_nb_0900_ai_ci"; "utf8mb4_nb_0900_as_cs"
                      "utf8mb4_nn_0900_ai_ci"; "utf8mb4_nn_0900_as_cs"
                      "utf8mb4_sv_0900_ai_ci"; "utf8mb4_sv_0900_as_cs"
                      "utf8mb4_de_pb_0900_ai_ci"; "utf8mb4_de_pb_0900_as_cs"
                      "utf8mb4_tr_0900_ai_ci"; "utf8mb4_tr_0900_as_cs"
                      "utf8mb4_es_0900_ai_ci"; "utf8mb4_es_0900_as_cs"; "utf8mb4_es_trad_0900_ai_ci"; "utf8mb4_es_trad_0900_as_cs"
                      "utf8mb4_is_0900_ai_ci"; "utf8mb4_is_0900_as_cs"
                      "utf8mb4_et_0900_ai_ci"; "utf8mb4_et_0900_as_cs"
                      "utf8mb4_pl_0900_ai_ci"; "utf8mb4_pl_0900_as_cs"
                      "utf8mb4_ro_0900_ai_ci"; "utf8mb4_ro_0900_as_cs"
                      "utf8mb4_ru_0900_ai_ci"; "utf8mb4_ru_0900_as_cs"
                      "utf8mb4_sk_0900_ai_ci"; "utf8mb4_sk_0900_as_cs"
                      "utf8mb4_sl_0900_ai_ci"; "utf8mb4_sl_0900_as_cs"
                      "utf8mb4_vi_0900_ai_ci"; "utf8mb4_vi_0900_as_cs"
                      "utf8mb4_bg_0900_ai_ci"; "utf8mb4_bg_0900_as_cs"
                      "utf8mb4_bs_0900_ai_ci"; "utf8mb4_bs_0900_as_cs"
                      "utf8mb4_cs_0900_ai_ci"; "utf8mb4_cs_0900_as_cs"
                      "utf8mb4_eo_0900_ai_ci"; "utf8mb4_eo_0900_as_cs"
                      "utf8mb4_gl_0900_ai_ci"; "utf8mb4_gl_0900_as_cs"
                      "utf8mb4_hr_0900_ai_ci"; "utf8mb4_hr_0900_as_cs"
                      "utf8mb4_hu_0900_ai_ci"; "utf8mb4_hu_0900_as_cs"
                      "utf8mb4_la_0900_ai_ci"; "utf8mb4_la_0900_as_cs"
                      "utf8mb4_lt_0900_ai_ci"; "utf8mb4_lt_0900_as_cs"
                      "utf8mb4_lv_0900_ai_ci"; "utf8mb4_lv_0900_as_cs"
                      "utf8mb4_mn_cyrl_0900_ai_ci"; "utf8mb4_mn_cyrl_0900_as_cs"
                      "utf8mb4_sr_latn_0900_ai_ci"; "utf8mb4_sr_latn_0900_as_cs"
                      "utf8mb4_ja_0900_as_cs"; "utf8mb4_ja_0900_as_cs_ks"; "utf8mb4_zh_0900_as_cs"
                      "utf8mb4_danish_ci"; "utf8mb4_swedish_ci"; "utf8mb4_german2_ci"
                      "utf8mb4_spanish_ci"; "utf8mb4_spanish2_ci"; "utf8mb4_turkish_ci"; "utf8mb4_icelandic_ci"
                      "utf8mb4_estonian_ci"; "utf8mb4_polish_ci"; "utf8mb4_romanian_ci"; "utf8mb4_roman_ci"
                      "utf8mb4_croatian_ci"; "utf8mb4_czech_ci"; "utf8mb4_esperanto_ci"; "utf8mb4_hungarian_ci"
                      "utf8mb4_latvian_ci"; "utf8mb4_lithuanian_ci"; "utf8mb4_persian_ci"; "utf8mb4_sinhala_ci"
                      "utf8mb4_slovak_ci"; "utf8mb4_slovenian_ci"; "utf8mb4_vietnamese_ci" ]
                    <| fun name ->
                        Expect.isTrue (Fsdb.Collation.tryFind name |> Option.isSome) (sprintf "registry resolves %s" name)

                testTheory
                    "the 0900 sensitivity matrix folds exactly what its name says"
                    [ "utf8mb4_0900_ai_ci", "å", "a", true
                      "utf8mb4_0900_as_ci", "å", "a", false
                      "utf8mb4_0900_as_cs", "å", "a", false
                      "utf8mb4_0900_as_ci", "A", "a", true
                      "utf8mb4_0900_as_cs", "A", "a", false
                      "utf8mb4_0900_ai_ci", "A", "a", true
                      "utf8mb4_0900_bin", "a", "A", false
                      "utf8mb4_0900_bin", "å", "a", false ]
                    <| fun (name, x, y, expectedEqual) ->
                        let col = Fsdb.Collation.tryFind name |> Option.get
                        Expect.equal (col.Equals x y) expectedEqual (sprintf "%s: %s = %s" name x y)

                testTheory
                    "pad attributes: PAD SPACE folds trailing spaces in equality, NO PAD keeps them"
                    [ "utf8mb4_unicode_ci", true; "utf8mb4_general_ci", true; "utf8mb4_bin", true
                      "utf8mb4_0900_ai_ci", false; "utf8mb4_0900_bin", false; "utf8mb4_da_0900_ai_ci", false ]
                    <| fun (name, expectedEqual) ->
                        let col = Fsdb.Collation.tryFind name |> Option.get
                        Expect.equal (col.Equals "a" "a ") expectedEqual (sprintf "%s: 'a' = 'a '" name)

                testCase "utf8mb4_general_ci gives sharp s one s weight"
                <| fun _ ->
                    let col = Fsdb.Collation.tryFind "utf8mb4_general_ci" |> Option.get
                    Expect.isTrue (col.Equals "ß" "s") "sharp s equals s"
                    Expect.isFalse (col.Equals "ß" "ss") "sharp s does not expand"
                    Expect.equal (col.KeyOf "ß") (col.KeyOf "s") "index key"
                    Expect.equal (col.HashOf "ß") (col.HashOf "s") "hash key"
                    Expect.isTrue (col.CharEquals 'ß' 's') "LIKE character"

                testTheory
                    "HashOf agrees with ComparePrimary: strings equal under the folded order hash equal"
                    [ "utf8mb4_0900_ai_ci", "åge", "age"
                      "utf8mb4_0900_ai_ci", "ÅGE", "age"
                      "utf8mb4_0900_ai_ci", "ß", "ss"
                      "utf8mb4_0900_ai_ci", "Céline", "celine"
                      "utf8mb4_unicode_ci", "a", "a "
                      "utf8mb4_unicode_ci", "ÅGE", "age"
                      "utf8mb4_0900_bin", "a", "a"
                      "utf8mb4_bin", "x", "x " ]
                    <| fun (name, a, b) ->
                        let col = Fsdb.Collation.tryFind name |> Option.get
                        Expect.isTrue (col.ComparePrimary a b = 0) (sprintf "%s: precondition %s = %s" name a b)
                        Expect.equal (col.HashOf a) (col.HashOf b) (sprintf "%s: HashOf %s = HashOf %s" name a b)

                testCase "PAD SPACE sorting puts the extra-space side first (MySQL-verified)"
                <| fun _ ->
                    let col = Fsdb.Collation.tryFind "utf8mb4_unicode_ci" |> Option.get
                    let sorted = [ "ab"; "a"; "a " ] |> List.sortWith col.Compare
                    Expect.equal sorted [ "a "; "a"; "ab" ] "['a '] < ['a'] < ['ab']"

                testTheory
                    "utf8mb4_0900_ai_ci folds case and accents: accented variant equals its base letter"
                    [ "å", "a"; "ä", "a"; "à", "a"; "á", "a"; "â", "a"; "ã", "a"; "ā", "a"; "ǎ", "a"
                      "ø", "o"; "ö", "o"; "ò", "o"; "ó", "o"
                      "ü", "u"; "ù", "u"; "ú", "u"
                      "ñ", "n"; "ç", "c"
                      "ì", "i"; "í", "i"; "ï", "i" ]
                    <| fun (accented, plain) ->
                        Expect.equal (compare (VString accented) (VString plain)) 0 (sprintf "%s = %s" accented plain)

                testTheory
                    "utf8mb4_0900_ai_ci expands digraphs and ligatures"
                    [ "ß", "ss"; "æ", "ae"; "œ", "oe"; "ﬀ", "ff"; "ǅ", "dž" ]
                    <| fun (a, b) -> Expect.equal (compare (VString a) (VString b)) 0 (sprintf "%s = %s" a b)

                testTheory
                    "utf8mb4_0900_ai_ci folds whole words, not just single letters"
                    [ "åge", "age"; "ÅGE", "age"; "ØL", "ol"; "ÆRA", "aera"; "Céline", "celine" ]
                    <| fun (a, b) -> Expect.equal (compare (VString a) (VString b)) 0 (sprintf "%s = %s" a b)

                testTheory
                    "NO PAD still holds under accent folding: trailing spaces stay significant"
                    [ "a", "a "; "åge", "age "; "b", "b  "; "ß", "ss " ]
                    <| fun (a, b) ->
                        Expect.notEqual (compare (VString a) (VString b)) 0 (sprintf "%s <> %s" a b)

                testCase "primary-weight ordering matches MySQL: expanded letters sit with their base-letter family"
                <| fun _ ->
                    // MySQL 8.4 orders these identically: æ expands to "ae"
                    // so it sorts after the a-family; ø is o-family.
                    let words = [ "ab"; "æb"; "øb"; "zb" ]
                    let sorted = words |> List.sortWith (fun a b -> compare (VString a) (VString b))
                    Expect.equal sorted [ "ab"; "æb"; "øb"; "zb" ] "ab < æb < øb < zb, as verified against MySQL"

                testCase "tie-breaks among primary-equal strings are stable (ICU CLDR order; documented divergence from MySQL's weight table)"
                <| fun _ ->
                    // MySQL's own UCA 9.0 table orders these accent variants
                    // ǎ|ā|ã|â|á|à|ä|å|ab (accented first, plain last); ICU's
                    // CLDR reverses that — the *equality* never differs, only
                    // the ORDER BY tie-break among equal-primary strings.
                    // Pin ICU's order so a future ICU/CLDR bump shows up as a
                    // deliberate change, not an accident.
                    let variants = [ "ab"; "åb"; "äb"; "àb"; "áb"; "âb"; "ãb"; "āb"; "ǎb" ]
                    let sorted = variants |> List.sortWith (fun a b -> compare (VString a) (VString b))
                    Expect.equal sorted [ "ab"; "åb"; "äb"; "àb"; "áb"; "âb"; "ãb"; "āb"; "ǎb" ] "ICU CLDR tie-break order"

                testCase "case folds in equality, and ORDER BY's tie-break is ICU's lowercase-first order"
                <| fun _ ->
                    Expect.equal (compare (VString "a") (VString "A")) 0 "'a' = 'A'"
                    // compareTotal (the ORDER BY comparator) breaks case
                    // ties via the full collation: lowercase first — ICU's
                    // CLDR order, not MySQL's (A|a|...); pinned for the same
                    // stability reason as the accent tie-breaks above.
                    let sorted = [ "B"; "b"; "a"; "A" ] |> List.sortWith (fun a b -> compareTotal (VString a) (VString b))
                    Expect.equal sorted [ "a"; "A"; "b"; "B" ] "lowercase-first tie-break"

                testCase "a DATE column value against a DATETIME-shaped string bound compares as a real instant, not text"
                <| fun _ ->
                    // A `VDate` against a DATETIME-shaped string must compare
                    // as a real instant, not as text — lexical order puts
                    // "2024-01-01" *before* "2024-01-01 00:00:00" and would
                    // exclude same-day rows at a BETWEEN lower bound.
                    Expect.equal (compare (VDate(DateOnly(2024, 1, 1))) (VString "2024-01-01 00:00:00")) 0 "midnight on the same day"
                    Expect.isGreaterThan (compare (VDate(DateOnly(2024, 1, 1))) (VString "2023-12-31 23:59:59")) 0 "a day after the bound"
                    Expect.isLessThan (compare (VString "2024-01-01 00:00:00") (VDate(DateOnly(2024, 1, 2)))) 0 "symmetric: string on the left"

                testCase "a DATETIME value against an unparseable string bound falls back to a text compare"
                <| fun _ ->
                    Expect.equal
                        (compare (VDateTime(DateTime(2024, 1, 1))) (VString "not a date"))
                        (compare (VString "2024-01-01 00:00:00") (VString "not a date"))
                        "text fallback" ]

          testList
              "equals"
              [ testCase "1 = '1' is true (WHERE-style implicit coercion)"
                <| fun _ -> Expect.equal (equals (VInt 1L) (VString "1")) (Some true) "1 = '1'"

                testCase "NULL = NULL is unknown, not true"
                <| fun _ -> Expect.equal (equals VNull VNull) None "null = null is unknown"

                testCase "NULL = anything is unknown"
                <| fun _ -> Expect.equal (equals VNull (VInt 1L)) None "null = 1 is unknown"

                testCase "1 = '2' is false"
                <| fun _ -> Expect.equal (equals (VInt 1L) (VString "2")) (Some false) "1 <> '2'" ]

          testList
              "truthy"
              [ testCase "NULL is unknown"
                <| fun _ -> Expect.equal (truthy VNull) None "null truthiness"

                testCase "zero is false"
                <| fun _ -> Expect.equal (truthy (VInt 0L)) (Some false) "0 is false"

                testCase "nonzero is true"
                <| fun _ -> Expect.equal (truthy (VInt 1L)) (Some true) "1 is true"

                testCase "a non-numeric string coerces to zero, so it's false"
                <| fun _ -> Expect.equal (truthy (VString "abc")) (Some false) "'abc' is false"

                testCase "a leading-numeric string is true"
                <| fun _ -> Expect.equal (truthy (VString "3abc")) (Some true) "'3abc' is true" ]

          testList
              "arithmetic"
              [ testCase "int + int stays int"
                <| fun _ -> Expect.equal (add (VInt 2L) (VInt 3L)) (VInt 5L) "2 + 3"

                testCase "int - int stays int"
                <| fun _ -> Expect.equal (sub (VInt 5L) (VInt 3L)) (VInt 2L) "5 - 3"

                testCase "int * int stays int"
                <| fun _ -> Expect.equal (mul (VInt 4L) (VInt 3L)) (VInt 12L) "4 * 3"

                testCase "decimal + int promotes to decimal"
                <| fun _ -> Expect.equal (add (VDecimal 1.5M) (VInt 1L)) (VDecimal 2.5M) "1.5 + 1"

                testCase "double + int promotes to double"
                <| fun _ -> Expect.equal (add (VDouble 1.5) (VInt 1L)) (VDouble 2.5) "1.5 + 1 (double)"

                testCase "any operand NULL propagates NULL"
                <| fun _ ->
                    Expect.equal (add VNull (VInt 1L)) VNull "null + 1"
                    Expect.equal (mul (VInt 1L) VNull) VNull "1 * null"

                testCase "int / int divides to decimal, scaled to the dividend's scale plus 4 (MySQL's div_precision_increment)"
                <| fun _ -> Expect.equal (div (VInt 5L) (VInt 2L)) (VDecimal 2.5000M) "5 / 2"

                testCase "a repeating quotient truncates at the same 4 extra digits, not a double's shortest round-trip"
                <| fun _ -> Expect.equal (div (VInt 1L) (VInt 3L)) (VDecimal 0.3333M) "1 / 3"

                testCase "a negative dividend keeps the sign through the decimal scale bump"
                <| fun _ -> Expect.equal (div (VInt -7L) (VInt 2L)) (VDecimal -3.5000M) "-7 / 2"

                testCase "the dividend's own scale carries into the +4, not just its type"
                <| fun _ -> Expect.equal (div (VDecimal 10.00M) (VInt 3L)) (VDecimal 3.333333M) "10.00 / 3"

                testCase "an INT dividend against a DECIMAL divisor still scales off the INT's own (zero) scale"
                <| fun _ -> Expect.equal (div (VInt 1L) (VDecimal 3.00M)) (VDecimal 0.3333M) "1 / 3.00"

                testCase "a DOUBLE operand taints the result to DOUBLE instead of DECIMAL"
                <| fun _ -> Expect.equal (div (VDouble 1.0) (VInt 3L)) (VDouble(1.0 / 3.0)) "1e0 / 3"

                testCase "division by zero yields NULL, not an exception, for both the decimal and double paths"
                <| fun _ ->
                    Expect.equal (div (VInt 1L) (VInt 0L)) VNull "1 / 0"
                    Expect.equal (div (VDouble 1.0) (VInt 0L)) VNull "1e0 / 0" ]

          testList
              "modulo"
              [ testCase "int MOD int stays int"
                <| fun _ -> Expect.equal (modulo (VInt 8L) (VInt 3L)) (VInt 2L) "8 MOD 3"

                testCase "a decimal operand keeps DECIMAL (and its own scale), unlike / — no div_precision_increment bump"
                <| fun _ -> Expect.equal (modulo (VDecimal 5.0M) (VInt 2L)) (VDecimal 1.0M) "5.0 MOD 2"

                testCase "a double operand taints the result to DOUBLE"
                <| fun _ -> Expect.equal (modulo (VDouble 5.5) (VInt 2L)) (VDouble 1.5) "5.5 MOD 2"

                testCase "modulo by zero yields NULL, not an exception"
                <| fun _ ->
                    Expect.equal (modulo (VInt 5L) (VInt 0L)) VNull "5 MOD 0"
                    Expect.equal (modulo (VDecimal 5.0M) (VInt 0L)) VNull "5.0 MOD 0" ]

          testList
              "intDiv"
              [ testCase "DIV always yields an int, truncated toward zero"
                <| fun _ -> Expect.equal (intDiv (VInt 5L) (VInt 2L)) (VInt 2L) "5 DIV 2"

                testCase "a negative dividend truncates toward zero rather than flooring"
                <| fun _ -> Expect.equal (intDiv (VInt -7L) (VInt 2L)) (VInt -3L) "-7 DIV 2"

                testCase "a float operand doesn't pre-round — DIV truncates the true quotient, not the rounded operands"
                <| fun _ ->
                    Expect.equal (intDiv (VDecimal 7.5M) (VInt 2L)) (VInt 3L) "7.5 DIV 2"
                    Expect.equal (intDiv (VInt 7L) (VDecimal 2.5M)) (VInt 2L) "7 DIV 2.5"

                testCase "DIV by zero yields NULL, not an exception"
                <| fun _ -> Expect.equal (intDiv (VInt 5L) (VInt 0L)) VNull "5 DIV 0" ]

          // `Functions.fs` has no dedicated `FunctionsTests.fs` — its home
          // in the `.fsproj`'s `<Compile>` list isn't this module's file to
          // add (see `Fsdb.Tests.fsproj`), so the registry's builtins are
          // exercised here instead, through `call` above.
          testList
              "Functions"
              [ testList
                    "AES"
                    [ testCase "AES_ENCRYPT uses MySQL's default aes-128-ecb key folding and binary output"
                      <| fun _ ->
                          Expect.equal
                              (call "AES_ENCRYPT" [ VString "hello"; VString "secret" ])
                              (VBytes(Convert.FromHexString "C290E8B6DE4EA5773414E019FE7F17A3"))
                              "ciphertext"

                          Expect.equal
                              (call "AES_ENCRYPT" [ VString "hello"; VString "1234567890123456x" ])
                              (VBytes(Convert.FromHexString "B91F9F7279D55B4275255DF933CEE9F6"))
                              "wrapped key"

                      testCase "AES_DECRYPT round-trips and rejects malformed or incorrectly padded ciphertext"
                      <| fun _ ->
                          let ciphertext = call "AES_ENCRYPT" [ VBytes [| 0uy; 255uy; 128uy |]; VString "secret" ]
                          Expect.equal (call "AES_DECRYPT" [ ciphertext; VString "secret" ]) (VBytes [| 0uy; 255uy; 128uy |]) "round-trip"
                          Expect.equal (call "AES_DECRYPT" [ ciphertext; VString "wrong" ]) VNull "wrong key"
                          Expect.equal (call "AES_DECRYPT" [ VBytes [| 0uy |]; VString "secret" ]) VNull "malformed block"
                          Expect.equal (call "AES_ENCRYPT" [ VNull; VString "secret" ]) VNull "null data"
                          Expect.equal (call "AES_ENCRYPT" [ VString "hello"; VNull ]) VNull "null key"

                      testCase "AES modes agree with MySQL's ECB, CBC, CFB, and OFB vectors"
                      <| fun _ ->
                          let plain, key, vector = VString "hello", VString "secret", VString "1234567890123456"
                          let binaryPlain = VBytes(Encoding.UTF8.GetBytes "hello")
                          let vectors =
                              [ "aes-128-ecb", [ plain; key ], "C290E8B6DE4EA5773414E019FE7F17A3"
                                "aes-128-cbc", [ plain; key; vector ], "E15CF4E4A472AAD6F4C09DA3FB305FE2"
                                "aes-128-cfb1", [ plain; key; vector ], "FE00C0BBC1"
                                "aes-128-cfb8", [ plain; key; vector ], "E5B3EACD01"
                                "aes-128-cfb128", [ plain; key; vector ], "E5A8898691"
                                "aes-128-ofb", [ plain; key; vector ], "E5A8898691"
                                "aes-192-cbc", [ plain; key; vector ], "B7E5A75343B842252A844B3D2F065348"
                                "aes-256-cbc", [ plain; key; vector ], "2D2DA42E9EDBB5A009EB79D0594F7A92" ]

                          for mode, args, expected in vectors do
                              let encrypt = Fsdb.Functions.aesEncrypt mode
                              let decrypt = Fsdb.Functions.aesDecrypt mode
                              let ciphertext = encrypt args
                              Expect.equal ciphertext (VBytes(Convert.FromHexString expected)) mode
                              Expect.equal (decrypt (ciphertext :: List.tail args)) binaryPlain mode

                      testCase "AES feedback modes preserve MySQL's state across multiple blocks"
                      <| fun _ ->
                          let plain = VString "the quick brown fox jumps over"
                          let key = VString "secret"
                          let vector = VString "1234567890123456"
                          let vectors =
                              [ "aes-128-cfb1", "EA5E39AB8B23B3F70DC86C20E7FB81CF5B64ABD8CB5811F7FB31D4F34A4A"
                                "aes-128-cfb8", "F9F45C432803549DDDDDC49B99C55B9651B8BB8E02999DE0168EF49F26B8"
                                "aes-128-cfb128", "F9A580CA8FCB3EDE61FD8D2EFFD2944A2D55F08A0495AC3DBAC7FFDC2593"
                                "aes-128-ofb", "F9A580CA8FCB3EDE61FD8D2EFFD2944A879CE7BF5D42C6EFB0D3CBAD529E" ]

                          for mode, expected in vectors do
                              Expect.equal
                                  (Fsdb.Functions.aesEncrypt mode [ plain; key; vector ])
                                  (VBytes(Convert.FromHexString expected))
                                  mode

                      testCase "AES KDF output agrees with MySQL's HKDF and PBKDF2-HMAC vectors"
                      <| fun _ ->
                          Expect.equal
                              (call "AES_ENCRYPT" [ VString "hello"; VString "secret"; VString ""; VString "hkdf"; VString "salt"; VString "info" ])
                              (VBytes(Convert.FromHexString "8D5A2A4C69CA7DBA564BCDFAA5C5EA35"))
                              "hkdf"

                          Expect.equal
                              (call "AES_ENCRYPT" [ VString "hello"; VString "secret"; VString ""; VString "pbkdf2_hmac"; VString "salt"; VInt 1000L ])
                              (VBytes(Convert.FromHexString "B02BD357E6A07064764E1EA45A9544C3"))
                              "pbkdf2"

                      testCase "AES reports MySQL's invalid KDF, IV, and arity errors"
                      <| fun _ ->
                          let expectError code invoke =
                              Expect.throwsC
                                  (invoke >> ignore)
                                  (function
                                  | Fsdb.Functions.SqlError(actual, _) when actual = code -> ()
                                  | error -> failtestf "expected %d, got %A" code error)

                          expectError 1582 (fun () -> call "AES_ENCRYPT" [ VString "hello" ])
                          expectError 3235 (fun () -> call "AES_ENCRYPT" [ VString "hello"; VString "secret"; VString ""; VString "unknown" ])
                          expectError 3236 (fun () -> call "AES_ENCRYPT" [ VString "hello"; VString "secret"; VString ""; VString "pbkdf2_hmac"; VString "salt"; VInt 999L ])
                          expectError 1882 (fun () -> Fsdb.Functions.aesEncrypt "aes-128-cbc" [ VString "hello"; VString "secret"; VString "short" ]) ]

                testList
                    "JSON"
                    [ testCase "JSON_EXTRACT walks $.a.b, $[0], and $.a[2].b"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_EXTRACT" [ VJson """{"a": {"b": 1}}"""; VString "$.a.b" ])
                              (VJson "1")
                              "$.a.b"

                          Expect.equal (call "JSON_EXTRACT" [ VJson "[10, 20, 30]"; VString "$[0]" ]) (VJson "10") "$[0]"

                          Expect.equal
                              (call "JSON_EXTRACT" [ VJson """{"a": [0, 1, {"b": 5}]}"""; VString "$.a[2].b" ])
                              (VJson "5")
                              "$.a[2].b"

                      testCase "JSON_EXTRACT quotes string results (json out, not unquoted text)"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_EXTRACT" [ VJson """{"a": "hi"}"""; VString "$.a" ])
                              (VJson "\"hi\"")
                              "quoted"

                      testCase "JSON_EXTRACT $.* returns every top-level value as an array"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_EXTRACT" [ VJson """{"a": 1, "b": 2}"""; VString "$.*" ])
                              (VJson "[1, 2]")
                              "$.*"

                      testCase "JSON_EXTRACT on a missing path is NULL, not an error"
                      <| fun _ -> Expect.equal (call "JSON_EXTRACT" [ VJson "{}"; VString "$.missing" ]) VNull "missing path"

                      testCase "JSON_EXTRACT on invalid JSON text is NULL"
                      <| fun _ -> Expect.equal (call "JSON_EXTRACT" [ VString "{not json"; VString "$.a" ]) VNull "invalid json"

                      testCase "JSON_EXTRACT on NULL doc is NULL"
                      <| fun _ -> Expect.equal (call "JSON_EXTRACT" [ VNull; VString "$.a" ]) VNull "null doc"

                      testCase "JSON_VALUE returns unquoted scalar text and NULL for a missing path"
                      <| fun _ ->
                          let document = VJson """{"s":"hi","n":2,"b":true,"z":null,"a":[1]}"""
                          Expect.equal (call "JSON_VALUE" [ document; VString "$.s" ]) (VString "hi") "string"
                          Expect.equal (call "JSON_VALUE" [ document; VString "$.n" ]) (VString "2") "number"
                          Expect.equal (call "JSON_VALUE" [ document; VString "$.b" ]) (VString "true") "boolean"
                          Expect.equal (call "JSON_VALUE" [ document; VString "$.z" ]) VNull "JSON null"
                          Expect.equal (call "JSON_VALUE" [ document; VString "$.a" ]) (VString "[1]") "array"
                          Expect.equal (call "JSON_VALUE" [ document; VString "$.missing" ]) VNull "missing"

                      testCase "JSON_UNQUOTE strips the quotes from a JSON string"
                      <| fun _ -> Expect.equal (call "JSON_UNQUOTE" [ VJson "\"hi\"" ]) (VString "hi") "unquote"

                      testCase "JSON_UNQUOTE on a non-string JSON value renders its text unchanged"
                      <| fun _ -> Expect.equal (call "JSON_UNQUOTE" [ VJson "1" ]) (VString "1") "non-string"

                      testCase "JSON null-argument and malformed-path edge cases return NULL"
                      <| fun _ ->
                          Expect.equal (call "JSON_UNQUOTE" [ VNull ]) VNull "unquote null"
                          Expect.equal (call "JSON_VALID" [ VNull ]) VNull "valid null"
                          Expect.equal (call "JSON_EXTRACT" [ VJson "\"scalar\""; VString "$.*" ]) VNull "wildcard over a scalar"
                          Expect.equal (call "JSON_EXTRACT" [ VJson """{"a": 1}"""; VString "$.a.\"unterminated" ]) VNull "unterminated quoted path"
                          // array-index insert never overwrites an existing element
                          Expect.equal (call "JSON_INSERT" [ VJson "[1, 2]"; VString "$[0]"; VInt 9L ]) (VJson "[1, 2]") "array insert refuses existing index"

                      testCase "JSON_CONTAINS finds an object subset"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_CONTAINS" [ VJson """{"a": 1, "b": 2}"""; VJson """{"a": 1}""" ])
                              (VInt 1L)
                              "subset"

                          Expect.equal
                              (call "JSON_CONTAINS" [ VJson """{"a": 1, "b": 2}"""; VJson """{"a": 2}""" ])
                              (VInt 0L)
                              "not a subset"

                      testCase "JSON_CONTAINS finds an array element"
                      <| fun _ -> Expect.equal (call "JSON_CONTAINS" [ VJson "[1, 2, 3]"; VJson "2" ]) (VInt 1L) "array membership"

                      testCase "JSON_SET overwrites an existing key and adds a new one"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_SET" [ VJson """{"a": 1}"""; VString "$.a"; VInt 2L; VString "$.b"; VInt 3L ])
                              (VJson """{"a": 2, "b": 3}""")
                              "set"

                      testCase "JSON_INSERT never overwrites an existing key"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_INSERT" [ VJson """{"a": 1}"""; VString "$.a"; VInt 99L ])
                              (VJson """{"a": 1}""")
                              "insert no-op on existing key"

                      testCase "JSON_REPLACE never creates a missing key"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_REPLACE" [ VJson """{"a": 1}"""; VString "$.b"; VInt 99L ])
                              (VJson """{"a": 1}""")
                              "replace no-op on missing key"

                      testCase "JSON_REMOVE deletes a key"
                      <| fun _ ->
                          Expect.equal (call "JSON_REMOVE" [ VJson """{"a": 1, "b": 2}"""; VString "$.a" ]) (VJson """{"b": 2}""") "remove"

                      testCase "JSON_ARRAY builds a JSON array from mixed values, NULL included"
                      <| fun _ ->
                          Expect.equal (call "JSON_ARRAY" [ VInt 1L; VString "x"; VNull ]) (VJson """[1, "x", null]""") "array"

                      testCase "JSON_OBJECT builds a JSON object from key/value pairs"
                      <| fun _ -> Expect.equal (call "JSON_OBJECT" [ VString "a"; VInt 1L ]) (VJson """{"a": 1}""") "object"

                      testCase "JSON_LENGTH counts object members, array elements, and scalars"
                      <| fun _ ->
                          Expect.equal (call "JSON_LENGTH" [ VJson """{"a": 1, "b": 2}""" ]) (VInt 2L) "object"
                          Expect.equal (call "JSON_LENGTH" [ VJson "[1, 2, 3]" ]) (VInt 3L) "array"
                          Expect.equal (call "JSON_LENGTH" [ VJson "1" ]) (VInt 1L) "scalar"

                      testCase "JSON_VALID distinguishes valid from invalid JSON text"
                      <| fun _ ->
                          Expect.equal (call "JSON_VALID" [ VString """{"a": 1}""" ]) (VInt 1L) "valid"
                          Expect.equal (call "JSON_VALID" [ VString "{not json" ]) (VInt 0L) "invalid"

                      testCase "JSON_SCHEMA_VALID applies Draft 4 object, number, and required constraints"
                      <| fun _ ->
                          let schema =
                              VString
                                  """{"type":"object","properties":{"latitude":{"type":"number","minimum":-90,"maximum":90},"longitude":{"type":"number","minimum":-180,"maximum":180}},"required":["latitude","longitude"]}"""

                          Expect.equal
                              (call "JSON_SCHEMA_VALID" [ schema; VString """{"latitude":63.444697,"longitude":10.445118}""" ])
                              (VInt 1L)
                              "valid coordinate"

                          Expect.equal
                              (call "JSON_SCHEMA_VALID" [ schema; VString """{"latitude":91,"longitude":0}""" ])
                              (VInt 0L)
                              "out-of-range latitude"

                          Expect.equal (call "JSON_SCHEMA_VALID" [ schema; VString "{}" ]) (VInt 0L) "missing required members"

                      testCase "JSON_SCHEMA_VALIDATION_REPORT gives MySQL's first-failure shape"
                      <| fun _ ->
                          let schema = VString """{"type":"object","properties":{"longitude":{"maximum":180}}}"""

                          Expect.equal
                              (call "JSON_SCHEMA_VALIDATION_REPORT" [ schema; VString """{"longitude":310}""" ])
                              (VJson
                                  """{"valid": false, "reason": "The JSON document location '#/longitude' failed requirement 'maximum' at JSON Schema location '#/properties/longitude'", "schema-location": "#/properties/longitude", "document-location": "#/longitude", "schema-failed-keyword": "maximum"}""")
                              "range failure"

                          Expect.equal
                              (call "JSON_SCHEMA_VALIDATION_REPORT" [ schema; VString """{"longitude":10}""" ])
                              (VJson """{"valid": true}""")
                              "valid report"

                          let required = VString """{"type":"object","required":["latitude"]}"""

                          Expect.equal
                              (call "JSON_SCHEMA_VALIDATION_REPORT" [ required; VString "{}" ])
                              (VJson
                                  """{"valid": false, "reason": "The JSON document location '#' failed requirement 'required' at JSON Schema location '#'", "schema-location": "#", "document-location": "#", "schema-failed-keyword": "required"}""")
                              "required report"

                      testCase "JSON schema ignores format assertions like MySQL"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_SCHEMA_VALID" [ VString """{"type":"string","format":"email"}"""; VString "\"not-an-email\"" ])
                              (VInt 1L)
                              "format annotations do not reject an otherwise valid string"

                      testCase "JSON schema resolves local references and refuses remote references"
                      <| fun _ ->
                          let local = VString """{"$ref":"#/definitions/integer","definitions":{"integer":{"type":"integer"}}}"""
                          Expect.equal (call "JSON_SCHEMA_VALID" [ local; VString "1" ]) (VInt 1L) "local reference"
                          Expect.equal (call "JSON_SCHEMA_VALID" [ local; VString "\"x\"" ]) (VInt 0L) "local reference violation"

                          Expect.throwsC
                              (fun () -> call "JSON_SCHEMA_VALID" [ VString """{"$ref":"https://example.invalid/schema"}"""; VString "{}" ] |> ignore)
                              (fun error ->
                                  match error with
                                  | Fsdb.Functions.SqlError(1235, _) -> ()
                                  | other -> failtestf "expected 1235, got %A" other)

                      testCase "JSON schema enforces property dependencies"
                      <| fun _ ->
                          let schema = VString """{"type":"object","dependencies":{"credit_card":["billing_address"]}}"""
                          Expect.equal (call "JSON_SCHEMA_VALID" [ schema; VString """{"credit_card":1234}""" ]) (VInt 0L) "missing dependency"
                          Expect.equal
                              (call "JSON_SCHEMA_VALIDATION_REPORT" [ schema; VString """{"credit_card":1234}""" ])
                              (VJson
                                  """{"valid": false, "reason": "The JSON document location '#' failed requirement 'dependencies' at JSON Schema location '#'", "schema-location": "#", "document-location": "#", "schema-failed-keyword": "dependencies"}""")
                              "dependency report"

                          Expect.equal
                              (call "JSON_SCHEMA_VALID" [ schema; VString """{"credit_card":1234,"billing_address":"x"}""" ])
                              (VInt 1L)
                              "dependency present"

                      testCase "JSON schema applies bounded string patterns"
                      <| fun _ ->
                          let schema = VString """{"type":"string","pattern":"^[A-Z]{2}-[0-9]{3}$"}"""
                          Expect.equal (call "JSON_SCHEMA_VALID" [ schema; VString "\"NO-123\"" ]) (VInt 1L) "matching pattern"
                          Expect.equal (call "JSON_SCHEMA_VALID" [ schema; VString "\"no-123\"" ]) (VInt 0L) "non-matching pattern"

                          Expect.equal
                              (call "JSON_SCHEMA_VALIDATION_REPORT" [ schema; VString "\"no-123\"" ])
                              (VJson
                                  """{"valid": false, "reason": "The JSON document location '#' failed requirement 'pattern' at JSON Schema location '#'", "schema-location": "#", "document-location": "#", "schema-failed-keyword": "pattern"}""")
                              "pattern report"

                          let digit = VString """{"type":"string","pattern":"^\\d$"}"""
                          Expect.equal (call "JSON_SCHEMA_VALID" [ digit; VString "\"1\"" ]) (VInt 1L) "ASCII digit"
                          Expect.equal (call "JSON_SCHEMA_VALID" [ digit; VString "\"١\"" ]) (VInt 0L) "non-ASCII digit"

                      testCase "JSON schema applies pattern properties to matching member values"
                      <| fun _ ->
                          let schema =
                              VString
                                  """{"type":"object","patternProperties":{"^S_":{"type":"string"}},"additionalProperties":false}"""

                          Expect.equal (call "JSON_SCHEMA_VALID" [ schema; VString """{"S_name":"Ada"}""" ]) (VInt 1L) "matching member"
                          Expect.equal (call "JSON_SCHEMA_VALID" [ schema; VString """{"S_name":1}""" ]) (VInt 0L) "member constraint"
                          Expect.equal (call "JSON_SCHEMA_VALID" [ schema; VString """{"name":"Ada"}""" ]) (VInt 0L) "additional property"

                          Expect.equal
                              (call "JSON_SCHEMA_VALIDATION_REPORT" [ schema; VString """{"S_name":1}""" ])
                              (VJson
                                  """{"valid": false, "reason": "The JSON document location '#/S_name' failed requirement 'patternProperties' at JSON Schema location '#'", "schema-location": "#", "document-location": "#/S_name", "schema-failed-keyword": "patternProperties"}""")
                              "pattern property report"

                      testCase "JSON schema ignores invalid patterns and bounds catastrophic ones"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_SCHEMA_VALID" [ VString """{"type":"string","pattern":"["}"""; VString "\"x\"" ])
                              (VInt 1L)
                              "invalid pattern"

                          let schema = VString """{"type":"string","pattern":"(a+)+$"}"""
                          let document = VString ("\"" + String.replicate 100_000 "a" + "!\"")

                          Expect.throwsC
                              (fun () -> call "JSON_SCHEMA_VALID" [ schema; document ] |> ignore)
                              (fun error ->
                                  match error with
                                  | Fsdb.Functions.SqlError(1235, _) -> ()
                                  | other -> failtestf "expected 1235, got %A" other)

                      testCase "JSON schema bounds pattern-property matching across a document"
                      <| fun _ ->
                          let document =
                              [ 0 .. 1_024 ]
                              |> List.map (fun index -> sprintf "\"p%d\":0" index)
                              |> String.concat ","
                              |> sprintf "{%s}"
                              |> VString

                          let schema = VString """{"type":"object","patternProperties":{"^p":{}}}"""

                          Expect.throwsC
                              (fun () -> call "JSON_SCHEMA_VALID" [ schema; document ] |> ignore)
                              (fun error ->
                                  match error with
                                  | Fsdb.Functions.SqlError(1235, _) -> ()
                                  | other -> failtestf "expected 1235, got %A" other)

                      testCase "JSON schema ignores siblings of a local reference"
                      <| fun _ ->
                          let schema =
                              VString
                                  """{"$ref":"#/definitions/letters","pattern":"^b+$","definitions":{"letters":{"type":"string","pattern":"^a+$"}}}"""

                          Expect.equal (call "JSON_SCHEMA_VALID" [ schema; VString "\"aaa\"" ]) (VInt 1L) "reference target"
                          Expect.equal (call "JSON_SCHEMA_VALID" [ schema; VString "\"bbb\"" ]) (VInt 0L) "reference target violation"

                      testCase "JSON schema refuses recursive local references containing patterns"
                      <| fun _ ->
                          let schema =
                              VString
                                  """{"$ref":"#/definitions/node","definitions":{"node":{"type":"object","properties":{"value":{"type":"string","pattern":"^a+$"},"next":{"$ref":"#/definitions/node"}}}}}"""

                          Expect.throwsC
                              (fun () -> call "JSON_SCHEMA_VALID" [ schema; VString """{"value":"a","next":{"value":"a"}}""" ] |> ignore)
                              (fun error ->
                                  match error with
                                  | Fsdb.Functions.SqlError(1235, _) -> ()
                                  | other -> failtestf "expected 1235, got %A" other)

                      testCase "JSON schema functions return NULL for a SQL NULL argument and reject invalid JSON"
                      <| fun _ ->
                          let schema = VString """{"type":"object"}"""
                          Expect.equal (call "JSON_SCHEMA_VALID" [ VNull; VString "{}" ]) VNull "null schema"
                          Expect.equal (call "JSON_SCHEMA_VALIDATION_REPORT" [ schema; VNull ]) VNull "null document"

                          Expect.throwsC
                              (fun () -> call "JSON_SCHEMA_VALID" [ schema; VString "not json" ] |> ignore)
                              (fun error ->
                                  match error with
                                  | Fsdb.Functions.SqlError(3141, _) -> ()
                                  | other -> failtestf "expected 3141, got %A" other)

                          Expect.throwsC
                              (fun () -> call "JSON_SCHEMA_VALID" [ VString """{"required":1}"""; VString "{}" ] |> ignore)
                              (fun error ->
                                  match error with
                                  | Fsdb.Functions.SqlError(3853, _) -> ()
                                  | other -> failtestf "expected 3853, got %A" other)

                      testCase "JSON_KEYS lists an object's top-level keys"
                      <| fun _ -> Expect.equal (call "JSON_KEYS" [ VJson """{"a": 1, "b": 2}""" ]) (VJson """["a", "b"]""") "keys"

                      testCase "JSON_TYPE names each JSON value's kind"
                      <| fun _ ->
                          Expect.equal (call "JSON_TYPE" [ VJson """{"a": 1}""" ]) (VString "OBJECT") "object"
                          Expect.equal (call "JSON_TYPE" [ VJson "[1, 2]" ]) (VString "ARRAY") "array"
                          Expect.equal (call "JSON_TYPE" [ VJson "\"hi\"" ]) (VString "STRING") "string"
                          Expect.equal (call "JSON_TYPE" [ VJson "1" ]) (VString "INTEGER") "integer"
                          Expect.equal (call "JSON_TYPE" [ VJson "1.5" ]) (VString "DOUBLE") "double"
                          Expect.equal (call "JSON_TYPE" [ VJson "true" ]) (VString "BOOLEAN") "boolean"
                          Expect.equal (call "JSON_TYPE" [ VJson "null" ]) (VString "NULL") "json null"
                          Expect.equal (call "JSON_TYPE" [ VNull ]) VNull "sql null"

                      testCase "JSON_CONTAINS with a path searches that subtree, and scalar/null equality works"
                      <| fun _ ->
                          Expect.equal (call "JSON_CONTAINS" [ VJson """{"a": [1, 2, 3]}"""; VJson "2"; VString "$.a" ]) (VInt 1L) "path hit"
                          Expect.equal (call "JSON_CONTAINS" [ VJson """{"a": [1, 2, 3]}"""; VJson "9"; VString "$.a" ]) (VInt 0L) "path miss"
                          Expect.equal (call "JSON_CONTAINS" [ VJson """{"a": 1}"""; VJson "1"; VString "$.b" ]) VNull "missing path is NULL"
                          Expect.equal (call "JSON_CONTAINS" [ VJson "1"; VJson "1" ]) (VInt 1L) "scalar equality"
                          Expect.equal (call "JSON_CONTAINS" [ VJson "1"; VJson "2" ]) (VInt 0L) "scalar inequality"
                          Expect.equal (call "JSON_CONTAINS" [ VJson "null"; VJson "null" ]) (VInt 1L) "json null contains json null"
                          Expect.equal (call "JSON_CONTAINS" [ VJson """{"a": 1}"""; VJson "null" ]) (VInt 0L) "non-null does not contain null"

                      testCase "JSON_LENGTH with a path measures that subtree"
                      <| fun _ ->
                          Expect.equal (call "JSON_LENGTH" [ VJson """{"a": [1, 2, 3]}"""; VString "$.a" ]) (VInt 3L) "array subtree"
                          Expect.equal (call "JSON_LENGTH" [ VJson """{"a": {"x": 1, "y": 2}}"""; VString "$.a" ]) (VInt 2L) "object subtree"

                      testCase "JSON_KEYS with a path lists that object's keys"
                      <| fun _ ->
                          Expect.equal (call "JSON_KEYS" [ VJson """{"a": {"x": 1, "y": 2}}"""; VString "$.a" ]) (VJson """["x", "y"]""") "nested keys"

                      testCase "JSON_SET/INSERT/REPLACE/REMOVE walk nested object and array paths"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_SET" [ VJson """{"a": {"b": 1}}"""; VString "$.a.b"; VInt 2L ])
                              (VJson """{"a": {"b": 2}}""")
                              "set through a nested object key"
                          Expect.equal
                              (call "JSON_SET" [ VJson """[{"x": 1}]"""; VString "$[0].x"; VInt 9L ])
                              (VJson """[{"x": 9}]""")
                              "set through array index then object key"
                          Expect.equal
                              (call "JSON_INSERT" [ VJson """{"a": {"b": 1}}"""; VString "$.a.b"; VInt 99L ])
                              (VJson """{"a": {"b": 1}}""")
                              "insert never overwrites a nested existing key"
                          Expect.equal
                              (call "JSON_REPLACE" [ VJson """{"a": {"b": 1}}"""; VString "$.a.c"; VInt 99L ])
                              (VJson """{"a": {"b": 1}}""")
                              "replace never creates a nested missing key"
                          Expect.equal
                              (call "JSON_REMOVE" [ VJson """{"a": {"b": 1, "c": 2}}"""; VString "$.a.b" ])
                              (VJson """{"a": {"c": 2}}""")
                              "remove a nested object key"
                          Expect.equal (call "JSON_REMOVE" [ VJson "[1, 2, 3]"; VString "$[0]" ]) (VJson "[2, 3]") "remove an array element"

                      testCase "JSON_SEARCH finds string leaves by pattern in one/all modes"
                      <| fun _ ->
                          Expect.equal
                              (call "JSON_SEARCH" [ VJson """{"a": ["x", "y"], "b": "y"}"""; VString "one"; VString "y" ])
                              (VJson "\"$.a[1]\"")
                              "one returns the first matching path"
                          Expect.equal
                              (call "JSON_SEARCH" [ VJson """{"a": "y", "b": ["z", "y"]}"""; VString "all"; VString "y" ])
                              (VJson """["$.a", "$.b[1]"]""")
                              "all returns every matching path"
                          Expect.equal
                              (call "JSON_SEARCH" [ VJson """{"a": "x"}"""; VString "one"; VString "zzz" ])
                              VNull
                              "no match is NULL"
                          Expect.equal
                              (call "JSON_SEARCH" [ VJson """{"a": "10%", "b": "100"}"""; VString "one"; VString "10\\%"; VString "\\" ])
                              (VJson "\"$.a\"")
                              "escape_char makes a wildcard literal"
                          Expect.equal
                              (call "JSON_SEARCH" [ VJson """{"a": {"x": "hit"}, "b": "hit"}"""; VString "all"; VString "hit"; VString "\\"; VString "$.a" ])
                              (VJson "\"$.a.x\"")
                              "path arguments restrict the search"
                          Expect.equal
                              (call "JSON_SEARCH" [ VJson """{"a": "hit", "b": "hit"}"""; VString "all"; VString "hit"; VString "\\"; VString "$.*" ])
                              (VJson """["$.a", "$.b"]""")
                              "wildcard paths retain concrete result paths"

                          let adversarial = String.replicate 20_000 "%" + "b"
                          let timer = Diagnostics.Stopwatch.StartNew()
                          let result = call "JSON_SEARCH" [ VJson("\"" + String.replicate 20_000 "." + "\""); VString "one"; VString adversarial ]
                          timer.Stop()
                          Expect.equal result VNull "a wildcard-heavy non-match stays a non-match"
                          Expect.isLessThan timer.Elapsed (TimeSpan.FromSeconds 1.0) "wildcards are matched iteratively" ]

                testCase "WEIGHT_STRING exposes the default collation key"
                <| fun _ ->
                    let weight value = call "WEIGHT_STRING" [ VString value ]
                    let utf8Bin = tryFind "utf8mb4_bin" |> Option.get
                    let latin1Bin = tryFind "latin1_bin" |> Option.get
                    Expect.equal (weight "Åge") (weight "age") "default collation folds case and accents"
                    Expect.notEqual (weight "age") (weight "axe") "different primary weights remain distinct"
                    Expect.notEqual (weight "a") (weight "a ") "trailing spaces retain their weight"
                    Expect.equal (call "WEIGHT_STRING" [ VNull ]) VNull "NULL propagates"
                    Expect.equal (call "WEIGHT_STRING" [ VBytes [| 0x61uy; 0x00uy |] ]) (VBytes [| 0x61uy; 0x00uy |]) "binary input stays byte-exact"
                    Expect.equal (call "WEIGHT_STRING" [ VBit(8, 128UL) ]) (VBytes [| 0x80uy |]) "BIT input stays byte-exact"
                    Expect.equal (weightStringBinary 2 (VBit(8, 128UL))) (VBytes [| 0x80uy; 0x00uy |]) "BINARY width pads BIT bytes"
                    Expect.equal (weightString utf8Bin (VString "a")) (VBytes [| 0x00uy; 0x00uy; 0x61uy |]) "utf8mb4 binary collation uses code points"
                    Expect.equal (weightString utf8Bin (VString "a ")) (VBytes [| 0x00uy; 0x00uy; 0x61uy; 0x00uy; 0x00uy; 0x20uy |]) "utf8mb4 binary collation retains trailing spaces"
                    Expect.equal (weightStringChar utf8Bin 3 (VString "a")) (VBytes [| 0x00uy; 0x00uy; 0x61uy; 0x00uy; 0x00uy; 0x20uy; 0x00uy; 0x00uy; 0x20uy |]) "CHAR width pads text before weighting"
                    Expect.equal (weightStringChar latin1Bin 3 (VString "a")) (VBytes [| 0x61uy; 0x20uy; 0x20uy |]) "latin1 CHAR width pads bytes"
                    Expect.equal (weightStringChar utf8Bin 2 (VBytes [| 0x61uy; 0x00uy |])) (VBytes [| 0x61uy; 0x00uy |]) "CHAR preserves raw bytes"
                    Expect.equal (weightStringChar utf8Bin 3 (VBit(8, 128UL))) (VBytes [| 0x80uy |]) "CHAR preserves BIT bytes"

                testList
                    "Dates"
                    [ testCase "component and calendar functions distinguish zero dates"
                      <| fun _ ->
                          let partial = VZeroDate(tryZeroDate 2020 1 0 |> Option.get)
                          let allZero = VZeroDate(tryZeroDate 0 0 0 |> Option.get)
                          Expect.equal (call "YEAR" [ partial ]) (VInt 2020L) "year survives"
                          Expect.equal (call "MONTH" [ partial ]) (VInt 1L) "month survives"
                          Expect.equal (call "DAYOFMONTH" [ partial ]) (VInt 0L) "zero day survives"
                          Expect.equal (call "DAY" [ partial ]) (VInt 0L) "DAY matches DAYOFMONTH"
                          Expect.equal (call "DATE_FORMAT" [ partial; VString "%Y-%m-%d" ]) (VString "2020-01-00") "format preserves components"
                          Expect.equal (call "LAST_DAY" [ partial ]) (VDate(DateOnly(2020, 1, 31))) "calendar month remains known"
                          let zeroDateTime = VZeroDateTime(tryZeroDateTime (tryZeroDate 2020 0 1 |> Option.get) 12 34 56 0 |> Option.get)
                          Expect.equal (call "DATE_FORMAT" [ zeroDateTime; VString "%Y-%m-%d %H:%i:%s" ]) (VString "2020-00-01 12:34:56") "format preserves time"
                          Expect.equal (call "HOUR" [ zeroDateTime ]) (VInt 12L) "hour survives"
                          Expect.equal (call "MINUTE" [ zeroDateTime ]) (VInt 34L) "minute survives"
                          Expect.equal (call "SECOND" [ zeroDateTime ]) (VInt 56L) "second survives"
                          Expect.equal (call "EXTRACT" [ VString "YEAR_MONTH"; partial ]) (VInt 202001L) "year and month survive extraction"
                          Expect.equal (call "EXTRACT" [ VString "DAY"; partial ]) (VInt 0L) "zero day survives extraction"
                          Expect.equal (call "EXTRACT" [ VString "HOUR"; zeroDateTime ]) (VInt 12L) "hour survives extraction"
                          Expect.equal (call "DATE_FORMAT" [ allZero; VString "%Y-%m-%d" ]) VNull "all-zero format is null"

                      testCase "NOW truncates to whole seconds (MySQL NOW() has precision 0)"
                      <| fun _ ->
                          // `DateTime.Now` carries 100 ns ticks; MySQL's NOW()
                          // has no fractional part, so NOW() must truncate at
                          // the source or `toText` would render spurious
                          // microseconds real MySQL never shows.
                          match call "NOW" [] with
                          | VDateTime dt ->
                              Expect.equal (dt.Ticks % TimeSpan.TicksPerSecond) 0L "NOW() has no sub-second component"
                          | other -> failtestf "expected VDateTime from NOW(), got %A" other

                      testCase "NOW(N) rounds the clock to N fractional digits (MySQL NOW(6) has microseconds)"
                      <| fun _ ->
                          // Can't assert an exact time, but the tick granularity
                          // is deterministic: fsp 6 rounds to whole microseconds
                          // (10-tick units), fsp 3 to milliseconds (10_000-tick
                          // units), fsp 0 to whole seconds.
                          let ticksOf args =
                              match call "NOW" args with
                              | VDateTime dt -> dt.Ticks
                              | other -> failtestf "expected VDateTime, got %A" other
                          Expect.equal (ticksOf [ VInt 6L ] % 10L) 0L "NOW(6) rounded to microseconds"
                          Expect.equal (ticksOf [ VInt 3L ] % 10_000L) 0L "NOW(3) rounded to milliseconds"
                          Expect.equal (ticksOf [ VInt 0L ] % TimeSpan.TicksPerSecond) 0L "NOW(0) rounded to seconds"

                      testCase "TIMESTAMP coerces a date or string to a datetime"
                      <| fun _ ->
                          Expect.equal (call "TIMESTAMP" [ VDate(DateOnly(2024, 1, 1)) ]) (VDateTime(DateTime(2024, 1, 1, 0, 0, 0))) "date gains midnight"
                          Expect.equal (call "TIMESTAMP" [ VString "2024-03-05 13:45:09" ]) (VDateTime(DateTime(2024, 3, 5, 13, 45, 9))) "string parses"
                          Expect.equal (call "TIMESTAMP" [ VNull ]) VNull "null"

                      testCase "DATE_ADD/DATE_SUB apply an INTERVAL-encoded argument"
                      <| fun _ ->
                          let interval = call "INTERVAL" [ VInt 3L; VString "DAY" ]

                          Expect.equal
                              (call "DATE_ADD" [ VDate(DateOnly(2024, 1, 1)); interval ])
                              (VDate(DateOnly(2024, 1, 4)))
                              "date_add 3 days"

                          Expect.equal
                              (call "DATE_SUB" [ VDate(DateOnly(2024, 1, 4)); interval ])
                              (VDate(DateOnly(2024, 1, 1)))
                              "date_sub 3 days"

                      testCase "DATE_ADD on a datetime with a time-bearing unit stays a datetime"
                      <| fun _ ->
                          Expect.equal
                              (call "DATE_ADD" [ VDateTime(DateTime(2024, 1, 1, 10, 0, 0)); call "INTERVAL" [ VInt 90L; VString "MINUTE" ] ])
                              (VDateTime(DateTime(2024, 1, 1, 11, 30, 0)))
                              "date_add 90 minutes"

                      testCase "DATE_ADD tolerates a plain 'N UNIT' string interval"
                      <| fun _ ->
                          Expect.equal
                              (call "DATE_ADD" [ VDate(DateOnly(2024, 1, 1)); VString "1 MONTH" ])
                              (VDate(DateOnly(2024, 2, 1)))
                              "date_add '1 MONTH'"

                      testCase "DATE_ADD/DATE_SUB propagate NULL"
                      <| fun _ -> Expect.equal (call "DATE_ADD" [ VNull; VString "1 DAY" ]) VNull "null date"

                      testCase "DATE_ADD on a date-only VString stays a date, not a midnight datetime"
                      <| fun _ ->
                          // The parser never types a SQL string literal as
                          // `VDate` — `DATE_ADD('2024-01-15', INTERVAL 10
                          // DAY)` still has to answer with a plain date.
                          Expect.equal
                              (call "DATE_ADD" [ VString "2024-01-15"; call "INTERVAL" [ VInt 10L; VString "DAY" ] ])
                              (VDate(DateOnly(2024, 1, 25)))
                              "date-only string stays a date"

                          Expect.equal
                              (call "DATE_ADD" [ VString "2024-01-15 10:00:00"; call "INTERVAL" [ VInt 1L; VString "HOUR" ] ])
                              (VDateTime(DateTime(2024, 1, 15, 11, 0, 0)))
                              "a time-bearing string still becomes a datetime"

                      testCase "DATEDIFF counts whole days, ignoring time"
                      <| fun _ ->
                          Expect.equal
                              (call "DATEDIFF" [ VDateTime(DateTime(2024, 1, 10, 23, 0, 0)); VDateTime(DateTime(2024, 1, 1, 1, 0, 0)) ])
                              (VInt 9L)
                              "datediff"

                      testCase "DATE_FORMAT renders the common specifiers"
                      <| fun _ ->
                          Expect.equal
                              (call "DATE_FORMAT" [ VDateTime(DateTime(2024, 3, 5, 13, 45, 9)); VString "%Y-%m-%d %H:%i:%s" ])
                              (VString "2024-03-05 13:45:09")
                              "date_format"

                      testCase "DATE_FORMAT works on a VString datetime, not just VDateTime"
                      <| fun _ ->
                          Expect.equal
                              (call "DATE_FORMAT" [ VString "2024-03-05 13:45:09"; VString "%Y-%m-%d" ])
                              (VString "2024-03-05")
                              "date_format on string"

                      testCase "DATE truncates to the date part"
                      <| fun _ -> Expect.equal (call "DATE" [ VDateTime(DateTime(2024, 3, 5, 13, 45, 9)) ]) (VDate(DateOnly(2024, 3, 5))) "date"

                      testCase "YEAR/MONTH/DAY/HOUR/MINUTE/SECOND extract their date part"
                      <| fun _ ->
                          let dt = VDateTime(DateTime(2024, 3, 5, 13, 45, 9))
                          Expect.equal (call "YEAR" [ dt ]) (VInt 2024L) "year"
                          Expect.equal (call "MONTH" [ dt ]) (VInt 3L) "month"
                          Expect.equal (call "DAY" [ dt ]) (VInt 5L) "day"
                          Expect.equal (call "HOUR" [ dt ]) (VInt 13L) "hour"
                          Expect.equal (call "MINUTE" [ dt ]) (VInt 45L) "minute"
                          Expect.equal (call "SECOND" [ dt ]) (VInt 9L) "second"

                      testCase "DAYOFWEEK/DAYNAME/MONTHNAME match MySQL (1 = Sunday)"
                      <| fun _ ->
                          let sunday = VDate(DateOnly(2024, 3, 3))
                          Expect.equal (call "DAYOFWEEK" [ sunday ]) (VInt 1L) "sunday is 1"
                          Expect.equal (call "DAYNAME" [ sunday ]) (VString "Sunday") "dayname"
                          Expect.equal (call "MONTHNAME" [ sunday ]) (VString "March") "monthname"

                      testCase "QUARTER buckets the month into 1-4"
                      <| fun _ -> Expect.equal (call "QUARTER" [ VDate(DateOnly(2024, 8, 1)) ]) (VInt 3L) "august is q3"

                      testCase "UNIX_TIMESTAMP/FROM_UNIXTIME round-trip"
                      <| fun _ ->
                          let dt = VDateTime(DateTime(2024, 1, 1, 0, 0, 0))
                          let ts = call "UNIX_TIMESTAMP" [ dt ]
                          Expect.equal (call "FROM_UNIXTIME" [ ts ]) dt "round trip"

                      testCase "UNIX_TIMESTAMP() agrees with NOW() (both read the same clock)"
                      <| fun _ ->
                          let nowTs = call "UNIX_TIMESTAMP" [ call "NOW" [] ]
                          let bareTs = call "UNIX_TIMESTAMP" []

                          match nowTs, bareTs with
                          | VInt a, VInt b -> Expect.isTrue (abs (a - b) <= 1L) "UNIX_TIMESTAMP() and UNIX_TIMESTAMP(NOW()) read the same clock, not one UTC and one local"
                          | other -> failtestf "expected two VInt timestamps, got %A" other

                      testCase "TIMESTAMPDIFF computes whole units between two datetimes"
                      <| fun _ ->
                          Expect.equal
                              (call
                                  "TIMESTAMPDIFF"
                                  [ VString "DAY"; VDate(DateOnly(2024, 1, 1)); VDate(DateOnly(2024, 1, 11)) ])
                              (VInt 10L)
                              "timestampdiff days"

                      testCase "TIMESTAMPDIFF MONTH/YEAR overshoot by one when the diff is negative"
                      <| fun _ ->
                          Expect.equal
                              (call
                                  "TIMESTAMPDIFF"
                                  [ VString "MONTH"; VDate(DateOnly(2024, 3, 31)); VDate(DateOnly(2024, 1, 1)) ])
                              (VInt -2L)
                              "backwards month diff"

                          Expect.equal
                              (call
                                  "TIMESTAMPDIFF"
                                  [ VString "YEAR"; VDate(DateOnly(2024, 6, 1)); VDate(DateOnly(2023, 1, 1)) ])
                              (VInt -1L)
                              "backwards year diff"

                      testCase "TIMESTAMPDIFF QUARTER applies the same whole-unit day guard as MONTH/YEAR"
                      <| fun _ ->
                          Expect.equal
                              (call "TIMESTAMPDIFF" [ VString "QUARTER"; VDate(DateOnly(2024, 1, 31)); VDate(DateOnly(2024, 4, 30)) ])
                              (VInt 0L)
                              "not a whole quarter yet — day 30 < day 31"

                          Expect.equal
                              (call "TIMESTAMPDIFF" [ VString "QUARTER"; VDate(DateOnly(2024, 1, 30)); VDate(DateOnly(2024, 4, 30)) ])
                              (VInt 1L)
                              "exactly 3 whole months is a whole quarter"

                      testCase "LAST_DAY finds the month's final day"
                      <| fun _ -> Expect.equal (call "LAST_DAY" [ VDate(DateOnly(2024, 2, 15)) ]) (VDate(DateOnly(2024, 2, 29))) "leap february"

                      testCase "STR_TO_DATE parses a common format"
                      <| fun _ ->
                          Expect.equal (call "STR_TO_DATE" [ VString "2024-03-05"; VString "%Y-%m-%d" ]) (VDate(DateOnly(2024, 3, 5))) "str_to_date"

                      testCase "STR_TO_DATE parses time parts, abbreviated and full month names"
                      <| fun _ ->
                          Expect.equal
                              (call "STR_TO_DATE" [ VString "2024-03-05 13:45:09"; VString "%Y-%m-%d %H:%i:%s" ])
                              (VDateTime(DateTime(2024, 3, 5, 13, 45, 9)))
                              "date plus time"
                          Expect.equal
                              (call "STR_TO_DATE" [ VString "05-Mar-2024"; VString "%d-%b-%Y" ])
                              (VDate(DateOnly(2024, 3, 5)))
                              "abbreviated month"
                          Expect.equal
                              (call "STR_TO_DATE" [ VString "March 5, 2024"; VString "%M %e, %Y" ])
                              (VDate(DateOnly(2024, 3, 5)))
                              "full month and unpadded day"

                      testCase "DATE_ADD applies every interval unit and the three-argument form"
                      <| fun _ ->
                          let d = VDate(DateOnly(2024, 1, 1))
                          Expect.equal (call "DATE_ADD" [ d; VInt 3L; VString "DAY" ]) (VDate(DateOnly(2024, 1, 4))) "three-arg day"
                          Expect.equal (call "DATE_ADD" [ d; call "INTERVAL" [ VInt 1L; VString "WEEK" ] ]) (VDate(DateOnly(2024, 1, 8))) "week"
                          Expect.equal (call "DATE_ADD" [ d; call "INTERVAL" [ VInt 1L; VString "QUARTER" ] ]) (VDate(DateOnly(2024, 4, 1))) "quarter"
                          Expect.equal (call "DATE_ADD" [ d; call "INTERVAL" [ VInt 1L; VString "YEAR" ] ]) (VDate(DateOnly(2025, 1, 1))) "year"
                          // non-date-only units promote a VDate to VDateTime
                          Expect.equal
                              (call "DATE_ADD" [ d; call "INTERVAL" [ VInt 2L; VString "HOUR" ] ])
                              (VDateTime(DateTime(2024, 1, 1, 2, 0, 0)))
                              "hour promotes to datetime"
                          Expect.equal
                              (call "DATE_ADD" [ d; call "INTERVAL" [ VInt 30L; VString "SECOND" ] ])
                              (VDateTime(DateTime(2024, 1, 1, 0, 0, 30)))
                              "second promotes to datetime"

                      testCase "DATE_FORMAT renders every supported specifier, ordinal suffixes, and 12-hour wrap"
                      <| fun _ ->
                          let dt = VDateTime(DateTime(2024, 3, 5, 13, 45, 9))

                          Expect.equal
                              (call "DATE_FORMAT" [ dt; VString "%Y-%y-%m-%c-%d-%e|%H-%h-%I-%i-%s-%S-%p|%W-%a-%M-%b|%j-%D-%w|%%|%x" ])
                              (VString "2024-24-03-3-05-5|13-01-01-45-09-09-PM|Tuesday-Tue-March-Mar|065-5th-2|%|%x")
                              "every specifier"

                          // each ordinal suffix shape: st/nd/rd/th
                          Expect.equal (call "DATE_FORMAT" [ VDate(DateOnly(2024, 1, 1)); VString "%D" ]) (VString "1st") "1st"
                          Expect.equal (call "DATE_FORMAT" [ VDate(DateOnly(2024, 1, 2)); VString "%D" ]) (VString "2nd") "2nd"
                          Expect.equal (call "DATE_FORMAT" [ VDate(DateOnly(2024, 1, 3)); VString "%D" ]) (VString "3rd") "3rd"
                          Expect.equal (call "DATE_FORMAT" [ VDate(DateOnly(2024, 1, 21)); VString "%D" ]) (VString "21st") "21st"
                          Expect.equal (call "DATE_FORMAT" [ VDate(DateOnly(2024, 1, 22)); VString "%D" ]) (VString "22nd") "22nd"
                          Expect.equal (call "DATE_FORMAT" [ VDate(DateOnly(2024, 1, 23)); VString "%D" ]) (VString "23rd") "23rd"
                          Expect.equal (call "DATE_FORMAT" [ VDate(DateOnly(2024, 1, 31)); VString "%D" ]) (VString "31st") "31st"
                          Expect.equal (call "DATE_FORMAT" [ VDate(DateOnly(2024, 1, 4)); VString "%D" ]) (VString "4th") "4th"

                          // 12-hour wrap: midnight is 12 AM, noon is 12 PM
                          Expect.equal (call "DATE_FORMAT" [ VDateTime(DateTime(2024, 1, 1, 0, 0, 0)); VString "%h %p" ]) (VString "12 AM") "midnight"
                          Expect.equal (call "DATE_FORMAT" [ VDateTime(DateTime(2024, 1, 1, 12, 0, 0)); VString "%h %p" ]) (VString "12 PM") "noon"

                      testCase "TIME extracts the clock part; WEEK counts Sunday-first weeks"
                      <| fun _ ->
                          Expect.equal (call "TIME" [ VString "2024-03-05 13:45:09" ]) (VTime(tryParseTimeValue "13:45:09" |> Option.get)) "time"
                          Expect.equal (call "WEEK" [ VDate(DateOnly(2024, 1, 7)) ]) (VInt 1L) "first sunday of 2024 starts week 1"
                          Expect.equal (call "WEEK" [ VDate(DateOnly(2024, 1, 1)) ]) (VInt 0L) "days before the first sunday are week 0"

                      testCase "FROM_UNIXTIME formats with a specifier"
                      <| fun _ ->
                          Expect.equal (call "FROM_UNIXTIME" [ VInt 0L; VString "%Y-%m-%d" ]) (VString "1970-01-01") "epoch formatted" ]

                testList
                    "Strings"
                    [ testCase "SUBSTRING is 1-indexed and supports negative positions"
                      <| fun _ ->
                          Expect.equal (call "SUBSTRING" [ VString "Hello world"; VInt 7L ]) (VString "world") "positive pos"
                          Expect.equal (call "SUBSTRING" [ VString "Hello world"; VInt 1L; VInt 5L ]) (VString "Hello") "with length"
                          Expect.equal (call "SUBSTRING" [ VString "Hello"; VInt(-3L) ]) (VString "llo") "negative pos"
                          Expect.equal (call "SUBSTRING" [ VString "hello"; VInt(-10L) ]) (VString "") "negative pos past the start is empty, not the whole string"

                      testCase "LOCATE/INSTR/POSITION find a 1-indexed offset, or 0"
                      <| fun _ ->
                          Expect.equal (call "LOCATE" [ VString "lo"; VString "Hello" ]) (VInt 4L) "locate"
                          Expect.equal (call "INSTR" [ VString "Hello"; VString "lo" ]) (VInt 4L) "instr"
                          Expect.equal (call "POSITION" [ VString "z"; VString "Hello" ]) (VInt 0L) "not found"
                          // The three-argument LOCATE(sub, str, pos) starts
                          // searching at an explicit 1-indexed offset, a
                          // separate match branch from the two-argument form:
                          // starting at position 4 skips the first 'l' at
                          // position 3 and lands on the second one at 4.
                          Expect.equal (call "LOCATE" [ VString "l"; VString "Hello"; VInt 4L ]) (VInt 4L) "three-arg locate from offset"
                          Expect.equal (call "LOCATE" [ VString "l"; VString "Hello"; VInt 5L ]) (VInt 0L) "three-arg locate not found past offset"
                          Expect.equal (call "LOCATE" [ VString "l"; VString "Hello"; VInt 0L ]) (VInt 0L) "start position below 1 yields 0, not a search from the beginning"

                      testCase "REPLACE substitutes every occurrence"
                      <| fun _ -> Expect.equal (call "REPLACE" [ VString "a-b-c"; VString "-"; VString "+" ]) (VString "a+b+c") "replace"

                      testCase "TRIM/LTRIM/RTRIM strip whitespace"
                      <| fun _ ->
                          Expect.equal (call "TRIM" [ VString "  hi  " ]) (VString "hi") "trim"
                          Expect.equal (call "LTRIM" [ VString "  hi  " ]) (VString "hi  ") "ltrim"
                          Expect.equal (call "RTRIM" [ VString "  hi  " ]) (VString "  hi") "rtrim"

                      testCase "LPAD/RPAD pad to length with a repeating pad string"
                      <| fun _ ->
                          Expect.equal (call "LPAD" [ VString "5"; VInt 3L; VString "0" ]) (VString "005") "lpad"
                          Expect.equal (call "RPAD" [ VString "5"; VInt 3L; VString "0" ]) (VString "500") "rpad"
                          Expect.equal (call "LPAD" [ VString "12345"; VInt 3L; VString "0" ]) (VString "123") "lpad truncates"
                          Expect.equal (call "LPAD" [ VString "5"; VInt(-1L); VString "0" ]) VNull "lpad negative length is null, not empty"
                          Expect.equal (call "LPAD" [ VString "5"; VInt 0L; VString "0" ]) (VString "") "lpad zero length is still empty"
                          Expect.equal (call "LPAD" [ VString "5"; VInt 100_000_000L; VString "0" ]) VNull "lpad past max_allowed_packet is null"
                          Expect.equal (call "RPAD" [ VString "5"; VInt 100_000_000L; VString "0" ]) VNull "rpad past max_allowed_packet is null"

                      testCase "LEFT/RIGHT take from either end"
                      <| fun _ ->
                          Expect.equal (call "LEFT" [ VString "Hello"; VInt 3L ]) (VString "Hel") "left"
                          Expect.equal (call "RIGHT" [ VString "Hello"; VInt 3L ]) (VString "llo") "right"

                      testCase "REVERSE/REPEAT/SPACE build strings"
                      <| fun _ ->
                          Expect.equal (call "REVERSE" [ VString "abc" ]) (VString "cba") "reverse"
                          Expect.equal (call "REPEAT" [ VString "ab"; VInt 3L ]) (VString "ababab") "repeat"
                          Expect.equal (call "SPACE" [ VInt 3L ]) (VString "   ") "space"
                          Expect.equal (call "REPEAT" [ VString "ab"; VInt 100_000_000L ]) VNull "repeat past max_allowed_packet is null, not an OOM"
                          Expect.equal (call "SPACE" [ VInt 100_000_000L ]) VNull "space past max_allowed_packet is null"

                      testCase "ASCII returns the first UTF-8 byte's code, 0 for empty"
                      <| fun _ ->
                          Expect.equal (call "ASCII" [ VString "A" ]) (VInt 65L) "ascii"
                          Expect.equal (call "ASCII" [ VString "" ]) (VInt 0L) "empty"
                          Expect.equal (call "ASCII" [ VString "é" ]) (VInt 195L) "e-acute's lead UTF-8 byte, not its UTF-16 codepoint"

                      testCase "HEX/UNHEX round-trip a string"
                      <| fun _ ->
                          Expect.equal (call "HEX" [ VString "AB" ]) (VString "4142") "hex"
                          Expect.equal (call "HEX" [ VString "é" ]) (VString "C3A9") "hex over utf-8 bytes, not utf-16 code units"
                          // UNHEX produces raw bytes (VBytes), not text — MySQL
                          // treats its result as a binary string.
                          Expect.equal (call "UNHEX" [ VString "4142" ]) (VBytes [| 0x41uy; 0x42uy |]) "unhex"

                      testCase "LENGTH counts UTF-8 bytes; CHAR_LENGTH counts code points"
                      <| fun _ ->
                          Expect.equal (call "LENGTH" [ VString "é" ]) (VInt 2L) "e-acute is 2 UTF-8 bytes"
                          Expect.equal (call "CHAR_LENGTH" [ VString "é" ]) (VInt 1L) "e-acute is 1 code point"
                          Expect.equal (call "CHAR_LENGTH" [ VString "\U0001F600" ]) (VInt 1L) "an astral emoji is 1 code point despite being a UTF-16 surrogate pair"

                      testCase "MD5/SHA1 produce lowercase hex digests of the known length"
                      <| fun _ ->
                          match call "MD5" [ VString "hello" ] with
                          | VString s -> Expect.equal s.Length 32 "md5 length"
                          | v -> failwithf "expected VString, got %A" v

                          match call "SHA1" [ VString "hello" ] with
                          | VString s -> Expect.equal s.Length 40 "sha1 length"
                          | v -> failwithf "expected VString, got %A" v

                      testCase "SHA2 picks a hash by requested length and nulls unsupported/NULL input"
                      <| fun _ ->
                          Expect.equal (call "SHA2" [ VString "hello"; VInt 224L ]) (VString "ea09ae9cc6768c50fcee903ed054556e5bfc8347907f12598aa24193") "sha2-224"
                          // 56 bytes: exercises the padding path that spills into a second block.
                          Expect.equal (call "SHA2" [ VString (String.replicate 56 "a"); VInt 224L ]) (VString "d40854fc9caf172067136f2e29e1380b14626bf6f0dd06779f820dcd") "sha2-224 two-block padding"
                          Expect.equal (call "SHA2" [ VString "hello"; VInt 256L ]) (VString "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824") "sha2-256"
                          Expect.equal (call "SHA2" [ VString "hello"; VInt 384L ]) (VString "59e1748777448c69de6b800d7a33bbfb9ff1b463e44354c3553bcdb9c666fa90125a3c79f90397bdf5f6a13de828684f") "sha2-384"
                          Expect.equal (call "SHA2" [ VString "hello"; VInt 512L ]) (VString "9b71d224bd62f3785d96d46ad3ea3d73319bfbc2890caadae2dff72519673ca72323c3d99ba5c11d7c7acc6e14b8c5da0c4663475c2e5c3adef46f73bcdec043") "sha2-512"
                          Expect.equal (call "SHA2" [ VString "hello"; VInt 7L ]) VNull "unsupported length"
                          Expect.equal (call "SHA2" [ VNull; VInt 256L ]) VNull "null input"
                          Expect.equal (call "SHA2" [ VString ""; VInt 224L ]) (VString "d14a028c2a3a2bc9476102bb288234c415a2b01f828ea62ac5b3e42f") "sha2-224 of the empty string"
                          // 120 bytes: exercises the three-block padding path.
                          Expect.equal
                              (call "SHA2" [ VString(String.replicate 120 "a"); VInt 224L ])
                              (VString "66924e30a9929327e7a6cf03747397226ed2efc180ebe3dea7132a79")
                              "sha2-224 three-block padding"

                      testCase "FORMAT adds thousands separators and fixes decimal places"
                      <| fun _ -> Expect.equal (call "FORMAT" [ VDouble 1234.5; VInt 2L ]) (VString "1,234.50") "format"

                      testCase "SUBSTRING_INDEX slices before/after the Nth delimiter"
                      <| fun _ ->
                          Expect.equal (call "SUBSTRING_INDEX" [ VString "a.b.c"; VString "."; VInt 2L ]) (VString "a.b") "positive count"
                          Expect.equal (call "SUBSTRING_INDEX" [ VString "a.b.c"; VString "."; VInt(-2L) ]) (VString "b.c") "negative count"

                      testCase "CONCAT_WS skips NULL arguments but a NULL separator nulls the result"
                      <| fun _ ->
                          Expect.equal (call "CONCAT_WS" [ VString ","; VString "a"; VNull; VString "b" ]) (VString "a,b") "skips null"
                          Expect.equal (call "CONCAT_WS" [ VNull; VString "a"; VString "b" ]) VNull "null separator"

                      testCase "CONCAT joins non-NULL arguments and returns NULL if any argument is NULL"
                      <| fun _ ->
                          Expect.equal (call "CONCAT" [ VString "a"; VString "b" ]) (VString "ab") "joins two strings"
                          Expect.equal (call "CONCAT" [ VString "a"; VNull; VString "b" ]) VNull "any NULL nulls the whole result"
                          Expect.equal (call "CONCAT" [ VNull ]) VNull "single NULL argument"

                      testCase "ELT/FIELD/FIND_IN_SET are all 1-indexed, 0/NULL when not found"
                      <| fun _ ->
                          Expect.equal (call "ELT" [ VInt 2L; VString "a"; VString "b"; VString "c" ]) (VString "b") "elt"
                          Expect.equal (call "FIELD" [ VString "b"; VString "a"; VString "b"; VString "c" ]) (VInt 2L) "field"
                          Expect.equal (call "FIELD" [ VNull; VString "a"; VString "b" ]) (VInt 0L) "field(NULL, ...) is 0, not NULL"
                          Expect.equal (call "FIND_IN_SET" [ VString "b"; VString "a,b,c" ]) (VInt 2L) "find_in_set"

                      testCase "QUOTE wraps and escapes for a SQL literal"
                      <| fun _ ->
                          Expect.equal (call "QUOTE" [ VString "it's" ]) (VString "'it\\'s'") "quote"
                          Expect.equal (call "QUOTE" [ VNull ]) (VString "NULL") "null renders as unquoted NULL"
                          Expect.equal (call "QUOTE" [ VString "a\000b" ]) (VString "'a\\0b'") "escapes NUL"
                          Expect.equal (call "QUOTE" [ VString "a\nb" ]) (VString "'a\\nb'") "escapes newline"
                          Expect.equal (call "QUOTE" [ VString "a\rb" ]) (VString "'a\\rb'") "escapes carriage return"
                          Expect.equal (call "QUOTE" [ VString "a\\b" ]) (VString "'a\\\\b'") "escapes a bare backslash"
                          Expect.equal (call "QUOTE" [ VString "a\026b" ]) (VString "'a\\Zb'") "escapes Ctrl-Z"

                      testCase "STRCMP returns -1/0/1"
                      <| fun _ ->
                          Expect.equal (call "STRCMP" [ VString "a"; VString "b" ]) (VInt(-1L)) "a < b"
                          Expect.equal (call "STRCMP" [ VString "a"; VString "a" ]) (VInt 0L) "a = a" ]

                testList
                    "Math and misc"
                    [ testCase "CEIL/FLOOR round toward +/- infinity"
                      <| fun _ ->
                          Expect.equal (call "CEIL" [ VDouble 1.1 ]) (VInt 2L) "ceil"
                          Expect.equal (call "FLOOR" [ VDouble 1.9 ]) (VInt 1L) "floor"
                          // An integer passes through unchanged, and a
                          // numeric string is coerced the same way a plain
                          // value would be.
                          Expect.equal (call "CEIL" [ VInt 3L ]) (VInt 3L) "int ceil passthrough"
                          Expect.equal (call "FLOOR" [ VInt 3L ]) (VInt 3L) "int floor passthrough"
                          Expect.equal (call "CEIL" [ VString "1.1" ]) (VInt 2L) "string ceil coerced"
                          Expect.equal (call "FLOOR" [ VString "1.9" ]) (VInt 1L) "string floor coerced"
                          Expect.equal (call "CEIL" [ VNull ]) VNull "null ceil"
                          Expect.equal (call "FLOOR" [ VNull ]) VNull "null floor"

                      testCase "POW/SQRT compute doubles, SQRT of a negative is NULL"
                      <| fun _ ->
                          Expect.equal (call "POW" [ VInt 2L; VInt 10L ]) (VDouble 1024.0) "pow"
                          Expect.equal (call "SQRT" [ VInt 9L ]) (VDouble 3.0) "sqrt"
                          Expect.equal (call "SQRT" [ VInt(-1L) ]) VNull "sqrt of negative"

                      testCase "SIGN returns -1/0/1"
                      <| fun _ ->
                          Expect.equal (call "SIGN" [ VInt(-5L) ]) (VInt(-1L)) "negative"
                          Expect.equal (call "SIGN" [ VInt 0L ]) (VInt 0L) "zero"
                          Expect.equal (call "SIGN" [ VInt 5L ]) (VInt 1L) "positive"

                      testCase "TRUNCATE cuts toward zero without rounding"
                      <| fun _ ->
                          Expect.equal (call "TRUNCATE" [ VDouble 1.999; VInt 2L ]) (VDouble 1.99) "double truncate"
                          // A DECIMAL first operand stays DECIMAL (keeps its
                          // scale) via a separate, dedicated match branch.
                          Expect.equal (call "TRUNCATE" [ VDecimal 1.999M; VInt 2L ]) (VDecimal 1.99M) "decimal truncate"
                          Expect.equal (call "TRUNCATE" [ VNull; VInt 2L ]) VNull "null truncate"

                      testCase "ABS handles every numeric kind and NULL"
                      <| fun _ ->
                          Expect.equal (call "ABS" [ VInt(-5L) ]) (VInt 5L) "int"
                          Expect.equal (call "ABS" [ VDouble(-5.5) ]) (VDouble 5.5) "double"
                          Expect.equal (call "ABS" [ VDecimal(-5.5M) ]) (VDecimal 5.5M) "decimal"
                          Expect.equal (call "ABS" [ VNull ]) VNull "null"

                      testCase "ROUND rounds per kind, honours a digit arg, and nulls NULL"
                      <| fun _ ->
                          Expect.equal (call "ROUND" [ VInt 5L ]) (VInt 5L) "int passthrough"
                          Expect.equal (call "ROUND" [ VInt 5L; VInt 2L ]) (VInt 5L) "int with digits"
                          Expect.equal (call "ROUND" [ VDouble 3.7 ]) (VDouble 4.0) "double nearest"
                          Expect.equal (call "ROUND" [ VDouble 3.14159; VInt 2L ]) (VDouble 3.14) "double with digits"
                          Expect.equal (call "ROUND" [ VDecimal 3.5M ]) (VDecimal 4M) "decimal half away from zero"
                          Expect.equal (call "ROUND" [ VNull ]) VNull "single null"
                          Expect.equal (call "ROUND" [ VNull; VInt 2L ]) VNull "null with digits"
                          Expect.equal (call "ROUND" [ VDouble 1.5; VNull ]) VNull "value with null digits"
                          Expect.equal (call "ROUND" [ VDecimal 123.456M; VInt(-1L) ]) (VDecimal 120M) "decimal negative digits rounds left of the point"
                          Expect.equal (call "ROUND" [ VInt 15L; VInt(-1L) ]) (VInt 20L) "int negative digits rounds left of the point"
                          Expect.equal (call "ROUND" [ VDouble 123.456; VInt(-1L) ]) (VDouble 120.0) "double negative digits doesn't throw"

                      testCase "MOD divides modulo across int/double/decimal and nulls a zero divisor"
                      <| fun _ ->
                          Expect.equal (call "MOD" [ VInt 7L; VInt 3L ]) (VInt 1L) "int mod"
                          Expect.equal (call "MOD" [ VInt 7L; VInt 0L ]) VNull "int mod by zero"
                          Expect.equal (call "MOD" [ VDouble 7.5; VDouble 2.0 ]) (VDouble 1.5) "double mod"
                          Expect.equal (call "MOD" [ VDouble 7.5; VDouble 0.0 ]) VNull "double mod by zero"
                          Expect.equal (call "MOD" [ VDecimal 7.5M; VDecimal 2M ]) (VDecimal 1.5M) "decimal mod"
                          Expect.equal (call "MOD" [ VDecimal 7.5M; VDecimal 0M ]) VNull "decimal mod by zero"
                          Expect.equal (call "MOD" [ VInt 7L; VNull ]) VNull "null divisor"
                          Expect.equal (call "MOD" [ VNull; VInt 3L ]) VNull "null dividend"

                      testCase "GREATEST/LEAST propagate NULL and otherwise compare like ORDER BY"
                      <| fun _ ->
                          Expect.equal (call "GREATEST" [ VInt 1L; VInt 5L; VInt 3L ]) (VInt 5L) "greatest"
                          Expect.equal (call "LEAST" [ VInt 1L; VInt 5L; VInt 3L ]) (VInt 1L) "least"
                          Expect.equal (call "GREATEST" [ VInt 1L; VNull ]) VNull "null propagates greatest"
                          Expect.equal (call "LEAST" [ VInt 1L; VNull ]) VNull "null propagates least"
                          Expect.equal (call "GREATEST" [ VNull ]) VNull "single null greatest"
                          Expect.equal (call "LEAST" [ VNull ]) VNull "single null least"

                      testCase "NULLIF nulls out equal arguments, passes through unequal ones"
                      <| fun _ ->
                          Expect.equal (call "NULLIF" [ VInt 1L; VInt 1L ]) VNull "equal"
                          Expect.equal (call "NULLIF" [ VInt 1L; VInt 2L ]) (VInt 1L) "unequal"
                          Expect.equal (call "NULLIF" [ VInt 1L; VNull ]) (VInt 1L) "vs null is not equal"

                      testCase "ISNULL is 1 for NULL, 0 otherwise"
                      <| fun _ ->
                          Expect.equal (call "ISNULL" [ VNull ]) (VInt 1L) "null"
                          Expect.equal (call "ISNULL" [ VInt 0L ]) (VInt 0L) "zero is not null"

                      testCase "CONV converts a number between bases"
                      <| fun _ -> Expect.equal (call "CONV" [ VString "ff"; VInt 16L; VInt 10L ]) (VString "255") "hex to decimal"

                      testCase "BIN/OCT render base 2/8"
                      <| fun _ ->
                          Expect.equal (call "BIN" [ VInt 5L ]) (VString "101") "bin"
                          Expect.equal (call "OCT" [ VInt 8L ]) (VString "10") "oct"

                      testCase "CRC32 matches the standard zlib checksum"
                      <| fun _ -> Expect.equal (call "CRC32" [ VString "123456789" ]) (VInt 0x0CBF43926L) "crc32 check value"

                      testCase "UUID produces a well-formed v4-shaped string"
                      <| fun _ ->
                          match call "UUID" [] with
                          | VString s -> Expect.isTrue (Guid.TryParse(s) |> fst) "parses as a guid"
                          | v -> failwithf "expected VString, got %A" v

                      testCase "INET_ATON/INET_NTOA round-trip an IPv4 address"
                      <| fun _ ->
                          Expect.equal (call "INET_ATON" [ VString "192.168.1.1" ]) (VInt 3232235777L) "aton"
                          Expect.equal (call "INET_NTOA" [ VInt 3232235777L ]) (VString "192.168.1.1") "ntoa" ]

                testList
                    "ADDDATE/SUBDATE bare-number form"
                    [ testCase "a bare numeric second argument means that many DAYs"
                      <| fun _ ->
                          Expect.equal
                              (call "ADDDATE" [ VDate(DateOnly(2024, 1, 1)); VInt 3L ])
                              (VDate(DateOnly(2024, 1, 4)))
                              "adddate"

                          Expect.equal
                              (call "SUBDATE" [ VDate(DateOnly(2024, 1, 4)); VInt 3L ])
                              (VDate(DateOnly(2024, 1, 1)))
                              "subdate"

                      testCase "ADDDATE/SUBDATE still accept the INTERVAL form"
                      <| fun _ ->
                          Expect.equal
                              (call "ADDDATE" [ VDate(DateOnly(2024, 1, 1)); call "INTERVAL" [ VInt 1L; VString "MONTH" ] ])
                              (VDate(DateOnly(2024, 2, 1)))
                              "adddate interval"

                      testCase "ADDDATE/SUBDATE propagate NULL"
                      <| fun _ ->
                          Expect.equal (call "ADDDATE" [ VNull; VInt 3L ]) VNull "null date"
                          Expect.equal (call "ADDDATE" [ VDate(DateOnly(2024, 1, 1)); VNull ]) VNull "null amount" ]

                testList
                    "REGEXP_LIKE/REPLACE/SUBSTR/INSTR"
                    [ testCase "REGEXP_LIKE matches case-insensitively by default"
                      <| fun _ ->
                          Expect.equal (call "REGEXP_LIKE" [ VString "Hello"; VString "^hello$" ]) (VInt 1L) "ci match"
                          Expect.equal (call "REGEXP_LIKE" [ VString "Hello"; VString "^hello$"; VString "c" ]) (VInt 0L) "c flag forces case-sensitive"
                          Expect.equal (call "REGEXP_LIKE" [ VString "abc"; VString "^z" ]) (VInt 0L) "no match"

                      testCase "REGEXP_LIKE propagates NULL"
                      <| fun _ -> Expect.equal (call "REGEXP_LIKE" [ VNull; VString "a" ]) VNull "null subject"

                      testCase "REGEXP functions report malformed patterns and match types"
                      <| fun _ ->
                          let expects code arguments =
                              Expect.throwsC
                                  (fun () -> call "REGEXP_LIKE" arguments |> ignore)
                                  (function
                                  | Fsdb.Functions.SqlError(actual, _) when actual = code -> ()
                                  | other -> failtestf "expected %d, got %A" code other)

                          expects 3691 [ VString "abc"; VString "(" ]
                          expects 1210 [ VString "abc"; VString "a"; VString "z" ]

                      testCase "REGEXP_SUBSTR returns the matched substring, or NULL if none"
                      <| fun _ ->
                          Expect.equal (call "REGEXP_SUBSTR" [ VString "abc123def"; VString "[0-9]+" ]) (VString "123") "found"
                          Expect.equal (call "REGEXP_SUBSTR" [ VString "abcdef"; VString "[0-9]+" ]) VNull "not found"

                      testCase "REGEXP_SUBSTR honors pos and occurrence"
                      <| fun _ ->
                          Expect.equal (call "REGEXP_SUBSTR" [ VString "a1b2c3"; VString "[0-9]"; VInt 1L; VInt 2L ]) (VString "2") "second digit"
                          Expect.equal (call "REGEXP_SUBSTR" [ VString "a1b2c3"; VString "[0-9]"; VInt 4L ]) (VString "2") "starting past the first digit"

                      testCase "REGEXP_INSTR returns a 1-based match start, or the end position past the match"
                      <| fun _ ->
                          Expect.equal (call "REGEXP_INSTR" [ VString "abc123"; VString "[0-9]+" ]) (VInt 4L) "start"
                          Expect.equal (call "REGEXP_INSTR" [ VString "abc123"; VString "[0-9]+"; VInt 1L; VInt 1L; VInt 1L ]) (VInt 7L) "end"
                          Expect.equal (call "REGEXP_INSTR" [ VString "abcdef"; VString "[0-9]+" ]) (VInt 0L) "not found"

                      testCase "REGEXP_REPLACE replaces every match by default, or just one occurrence"
                      <| fun _ ->
                          Expect.equal (call "REGEXP_REPLACE" [ VString "a1b2c3"; VString "[0-9]"; VString "#" ]) (VString "a#b#c#") "all"

                          Expect.equal
                              (call "REGEXP_REPLACE" [ VString "a1b2c3"; VString "[0-9]"; VString "#"; VInt 1L; VInt 2L ])
                              (VString "a1b#c3")
                              "second occurrence only"

                      testCase "REGEXP_REPLACE uses $N for backreferences; \\N is a literal digit"
                      <| fun _ ->
                          Expect.equal
                              (call "REGEXP_REPLACE" [ VString "2024-01-15"; VString "(\\d+)-(\\d+)-(\\d+)"; VString "$3/$2/$1" ])
                              (VString "15/01/2024")
                              "backreference reorder"

                          Expect.equal
                              (call "REGEXP_REPLACE" [ VString "2024-01-15"; VString "(\\d+)-(\\d+)-(\\d+)"; VString "\\3/\\2/\\1" ])
                              (VString "3/2/1")
                              "\\N is a literal digit, not a backreference"

                      testCase "REGEXP_REPLACE rejects .NET-only $ tokens with MySQL's 3887"
                      <| fun _ ->
                          // A `$` not followed by a digit isn't a valid MySQL
                          // backreference — MySQL errors 3887 rather than run
                          // .NET's own `$``/`$&`/`$'` substitutions (which also
                          // amplify output to O(n^2)). Oracle-verified 8.4.
                          let rejects (repl: string) =
                              Expect.throwsC
                                  (fun () -> call "REGEXP_REPLACE" [ VString "x"; VString "."; VString repl ] |> ignore)
                                  (fun e ->
                                      match e with
                                      | Fsdb.Functions.SqlError(3887, _) -> ()
                                      | other -> failtestf "expected 3887, got %A" other)

                          rejects "$`"
                          rejects "$&"
                          rejects "$'"
                          rejects "a$" ]

                testList
                    "UUID_TO_BIN/BIN_TO_UUID/IS_UUID"
                    [ testCase "UUID_TO_BIN matches the oracle's byte order, swapped and not"
                      <| fun _ ->
                          let uuid = VString "6ccd780c-baba-1026-9564-5b8c656024db"

                          Expect.equal
                              (call "UUID_TO_BIN" [ uuid; VInt 0L ])
                              (VBytes(Convert.FromHexString "6CCD780CBABA102695645B8C656024DB"))
                              "no swap"

                          Expect.equal
                              (call "UUID_TO_BIN" [ uuid; VInt 1L ])
                              (VBytes(Convert.FromHexString "1026BABA6CCD780C95645B8C656024DB"))
                              "swap"

                      testCase "UUID_TO_BIN/BIN_TO_UUID round-trip, swapped and not"
                      <| fun _ ->
                          let uuid = VString "6ccd780c-baba-1026-9564-5b8c656024db"

                          Expect.equal (call "BIN_TO_UUID" [ call "UUID_TO_BIN" [ uuid ] ]) uuid "no swap round-trip"
                          Expect.equal (call "BIN_TO_UUID" [ call "UUID_TO_BIN" [ uuid; VInt 1L ]; VInt 1L ]) uuid "swap round-trip"

                      testCase "UUID_TO_BIN/BIN_TO_UUID propagate NULL and reject malformed input"
                      <| fun _ ->
                          Expect.equal (call "UUID_TO_BIN" [ VNull ]) VNull "null in"
                          Expect.equal (call "UUID_TO_BIN" [ VString "not-a-uuid" ]) VNull "malformed"
                          Expect.equal (call "BIN_TO_UUID" [ VBytes [| 1uy; 2uy |] ]) VNull "wrong length"

                      testCase "IS_UUID accepts dashed, undashed, and braced forms"
                      <| fun _ ->
                          Expect.equal (call "IS_UUID" [ VString "6ccd780c-baba-1026-9564-5b8c656024db" ]) (VInt 1L) "dashed"
                          Expect.equal (call "IS_UUID" [ VString "6ccd780cbaba10269564 5b8c656024db" ]) (VInt 0L) "embedded space is not a valid form"
                          Expect.equal (call "IS_UUID" [ VString "6ccd780cbaba102695645b8c656024db" ]) (VInt 1L) "undashed"
                          Expect.equal (call "IS_UUID" [ VString "{6ccd780c-baba-1026-9564-5b8c656024db}" ]) (VInt 1L) "braced"
                          Expect.equal (call "IS_UUID" [ VString "not-a-uuid" ]) (VInt 0L) "malformed"
                          Expect.equal (call "IS_UUID" [ VNull ]) VNull "null" ]

                testList
                    "WEEK/WEEKDAY/WEEKOFYEAR/YEARWEEK"
                    [ testCase "WEEK covers all 8 MySQL modes at a mode-0-first-Sunday boundary"
                      <| fun _ ->
                          let d2000 = VDate(DateOnly(2000, 1, 1))
                          let expected = [ 0L; 0L; 52L; 52L; 0L; 0L; 52L; 52L ]

                          for mode in 0..7 do
                              Expect.equal (call "WEEK" [ d2000; VInt(int64 mode) ]) (VInt expected.[mode]) (sprintf "mode %d" mode)

                      testCase "WEEK covers all 8 modes at a leap-year Jan-1-is-Monday boundary"
                      <| fun _ ->
                          let d2024 = VDate(DateOnly(2024, 1, 1))
                          let expected = [ 0L; 1L; 53L; 1L; 1L; 1L; 1L; 1L ]

                          for mode in 0..7 do
                              Expect.equal (call "WEEK" [ d2024; VInt(int64 mode) ]) (VInt expected.[mode]) (sprintf "mode %d" mode)

                      testCase "YEARWEEK forces the 1-53 rollover range regardless of mode"
                      <| fun _ ->
                          Expect.equal (call "YEARWEEK" [ VDate(DateOnly(2000, 1, 1)) ]) (VInt 199952L) "default mode 0"
                          Expect.equal (call "YEARWEEK" [ VDate(DateOnly(2024, 1, 1)); VInt 3L ]) (VInt 202401L) "mode 3"
                          Expect.equal (call "YEARWEEK" [ VDate(DateOnly(2024, 12, 31)); VInt 1L ]) (VInt 202501L) "rolls into next year's week 1"

                      testCase "WEEKDAY is 0-indexed from Monday"
                      <| fun _ ->
                          Expect.equal (call "WEEKDAY" [ VDate(DateOnly(2024, 1, 1)) ]) (VInt 0L) "monday"
                          Expect.equal (call "WEEKDAY" [ VDate(DateOnly(2024, 1, 7)) ]) (VInt 6L) "sunday"

                      testCase "WEEKOFYEAR is WEEK(d, 3)"
                      <| fun _ -> Expect.equal (call "WEEKOFYEAR" [ VDate(DateOnly(2024, 1, 1)) ]) (call "WEEK" [ VDate(DateOnly(2024, 1, 1)); VInt 3L ]) "matches mode 3"

                      testCase "WEEK/WEEKDAY/WEEKOFYEAR/YEARWEEK propagate NULL"
                      <| fun _ ->
                          Expect.equal (call "WEEK" [ VNull ]) VNull "week"
                          Expect.equal (call "WEEKDAY" [ VNull ]) VNull "weekday"
                          Expect.equal (call "WEEKOFYEAR" [ VNull ]) VNull "weekofyear"
                          Expect.equal (call "YEARWEEK" [ VNull ]) VNull "yearweek" ]

                testList
                    "Aggregates"
                    [ testCase "STDDEV_POP/STDDEV_SAMP/VAR_POP/VAR_SAMP over 1,2,3,4"
                      <| fun _ ->
                          let xs = [ VInt 1L; VInt 2L; VInt 3L; VInt 4L ]

                          Expect.equal (callAgg "STDDEV_POP" xs) (VDouble 1.118033988749895) "stddev_pop"
                          Expect.equal (callAgg "STDDEV" xs) (VDouble 1.118033988749895) "stddev alias"
                          Expect.equal (callAgg "STD" xs) (VDouble 1.118033988749895) "std alias"
                          Expect.equal (callAgg "STDDEV_SAMP" xs) (VDouble 1.2909944487358056) "stddev_samp"
                          Expect.equal (callAgg "VAR_POP" xs) (VDouble 1.25) "var_pop"
                          Expect.equal (callAgg "VARIANCE" xs) (VDouble 1.25) "variance alias"
                          Expect.equal (callAgg "VAR_SAMP" xs) (VDouble 1.6666666666666667) "var_samp"

                      testCase "STDDEV_SAMP/VAR_SAMP over a single value is NULL (n - 1 = 0), STDDEV_POP/VAR_POP is 0"
                      <| fun _ ->
                          Expect.equal (callAgg "STDDEV_POP" [ VInt 1L ]) (VDouble 0.0) "pop"
                          Expect.equal (callAgg "STDDEV_SAMP" [ VInt 1L ]) VNull "samp"
                          Expect.equal (callAgg "VAR_SAMP" [ VInt 1L ]) VNull "var samp"

                      testCase "BIT_AND/BIT_OR/BIT_XOR fold bitwise over a group"
                      <| fun _ ->
                          let ys = [ VInt 1L; VInt 2L; VInt 3L; VInt 4L ]

                          Expect.equal (callAgg "BIT_AND" ys) (VInt 0L) "bit_and"
                          Expect.equal (callAgg "BIT_OR" ys) (VInt 7L) "bit_or"
                          Expect.equal (callAgg "BIT_XOR" ys) (VInt 4L) "bit_xor" ] ] ]
