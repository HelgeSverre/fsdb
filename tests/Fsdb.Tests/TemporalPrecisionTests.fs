module Fsdb.Tests.TemporalPrecisionTests

// DATETIME(N)/TIMESTAMP(N)/TIME(N) fractional-second precision (fsp 0-6).
// Every expected value here was verified against a real MySQL 8.0.33 oracle
// (see the feature's differential checks).

open System
open System.IO
open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Persistence
open Fsdb.Session
open Fsdb.QueryHandler

let private col name ty : ColumnDef =
    { Name = name
      Type = ty
      Nullable = true
      Default = None
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      Generated = None
      Collation = None
      Charset = None }

/// Runs `sql` on a fresh in-memory session, threading each statement's
/// session forward, and returns the last statement's `QueryResult`.
let private runSql (statements: string list) : Fsdb.Executor.QueryResult =
    let mutable session = create 1 (Fsdb.Storage.create ())
    let mutable result = Fsdb.Executor.Affected 0UL

    for sql in statements do
        let s, r = handle session sql
        session <- s
        result <- r

    result

let private oneRow (statements: string list) : string option list =
    match runSql statements with
    | Fsdb.Executor.ResultSet(_, [ row ]) -> row
    | other -> failtestf "expected exactly one row, got %A" other

let private tempDataDir () =
    let dir = Path.Combine(Path.GetTempPath(), "fsdb-fsp-tests", Guid.NewGuid().ToString "N")
    Directory.CreateDirectory dir |> ignore
    dir

let tests =
    testList
        "temporal precision (fsp)"
        [ testList
              "parser captures fsp"
              [ testCase "DATETIME(6)/TIMESTAMP(3)/TIME(2) carry their precision; a bare DATETIME is fsp 0"
                <| fun _ ->
                    match Fsdb.Parser.parse "CREATE TABLE t (a DATETIME(6), b TIMESTAMP(3), c TIME(2), d DATETIME)" with
                    | Ok(CreateTable(_, [ { Type = TDateTime 6 }; { Type = TTimestamp 3 }; { Type = TTime 2 }; { Type = TDateTime 0 } ], _, _, _, _, _)) -> ()
                    | other -> failtestf "unexpected parse: %A" other ]

          testList
              "coercion rounds the fraction to fsp (half up, like MySQL)"
              [ testCase "DATETIME(0) of .6 rounds up to the next second"
                <| fun _ ->
                    match coerceValue true (col "a" (TDateTime 0)) (VString "2024-01-01 00:00:00.6") with
                    | Ok(VDateTime dt) -> Expect.equal dt (DateTime(2024, 1, 1, 0, 0, 1)) "rounded to :01"
                    | other -> failtestf "got %A" other

                testCase "DATETIME(3) of .1234565 rounds to .123"
                <| fun _ ->
                    match coerceValue true (col "a" (TDateTime 3)) (VString "2024-01-01 00:00:00.1234565") with
                    | Ok(VDateTime dt) -> Expect.equal (dt.Ticks % TimeSpan.TicksPerSecond) (123L * TimeSpan.TicksPerMillisecond) ".123"
                    | other -> failtestf "got %A" other

                testCase "DATETIME(6) of .9999995 carries all the way to the next day"
                <| fun _ ->
                    match coerceValue true (col "a" (TDateTime 6)) (VString "2024-01-01 23:59:59.9999995") with
                    | Ok(VDateTime dt) -> Expect.equal dt (DateTime(2024, 1, 2, 0, 0, 0)) "carried to next day"
                    | other -> failtestf "got %A" other

                testCase "TIME(2) of .126 rounds to .13 and stays a formatted string"
                <| fun _ ->
                    match coerceValue true (col "a" (TTime 2)) (VString "12:00:00.126") with
                    | Ok(VString s) -> Expect.equal s "12:00:00.13" "rounded, 2 digits"
                    | other -> failtestf "got %A" other ]

          testList
              "text resultset renders exactly fsp digits"
              [ testCase "DATETIME(6) on an exact second still shows .000000"
                <| fun _ ->
                    let row = oneRow [ "CREATE TABLE t (a DATETIME(6))"; "INSERT INTO t VALUES ('2024-01-01 00:00:00')"; "SELECT a FROM t" ]
                    Expect.equal row [ Some "2024-01-01 00:00:00.000000" ] "six trailing zeros"

                testCase "DATETIME(3) renders exactly three digits"
                <| fun _ ->
                    let row = oneRow [ "CREATE TABLE t (a DATETIME(3))"; "INSERT INTO t VALUES ('2024-01-01 00:00:00.5')"; "SELECT a FROM t" ]
                    Expect.equal row [ Some "2024-01-01 00:00:00.500" ] "three digits"

                testCase "DATETIME(0) renders no fraction (and rounds .6 up)"
                <| fun _ ->
                    let row = oneRow [ "CREATE TABLE t (a DATETIME(0))"; "INSERT INTO t VALUES ('2024-01-01 00:00:00.6')"; "SELECT a FROM t" ]
                    Expect.equal row [ Some "2024-01-01 00:00:01" ] "no fraction, rounded"

                testCase "SELECT * threads each column's declared fsp"
                <| fun _ ->
                    let row =
                        oneRow
                            [ "CREATE TABLE t (a DATETIME(0), b DATETIME(3), c DATETIME(6))"
                              "INSERT INTO t VALUES ('2024-01-01 00:00:00', '2024-01-01 00:00:00', '2024-01-01 00:00:00')"
                              "SELECT * FROM t" ]

                    Expect.equal row [ Some "2024-01-01 00:00:00"; Some "2024-01-01 00:00:00.000"; Some "2024-01-01 00:00:00.000000" ] "per-column fsp"

                testCase "a NOW()-less expression column (no declared type) keeps toText's actual-digit rendering"
                <| fun _ ->
                    // A literal cast into DATETIME(6) is a declared cast type, but a
                    // bare datetime literal has none — it must not gain a fraction.
                    let row = oneRow [ "SELECT CAST('2024-01-01 00:00:00' AS DATETIME) AS x" ]
                    Expect.equal row [ Some "2024-01-01 00:00:00" ] "no spurious fraction" ]

          testList
              "information_schema / SHOW"
              [ testCase "column_type shows datetime(N) for fsp>0 and datetime for fsp 0; data_type stays bare; datetime_precision carries fsp"
                <| fun _ ->
                    match
                        runSql
                            [ "CREATE TABLE t (a DATETIME, b DATETIME(6), c TIME(2))"
                              "SELECT column_type, data_type, datetime_precision FROM information_schema.columns WHERE table_name='t' ORDER BY ordinal_position" ]
                    with
                    | Fsdb.Executor.ResultSet(_, rows) ->
                        Expect.equal
                            rows
                            [ [ Some "datetime"; Some "datetime"; Some "0" ]
                              [ Some "datetime(6)"; Some "datetime"; Some "6" ]
                              [ Some "time(2)"; Some "time"; Some "2" ] ]
                            "column_type/data_type/datetime_precision"
                    | other -> failtestf "got %A" other ]

          testList
              "fsp > 6 is rejected with MySQL's 1426"
              [ testCase "CREATE TABLE with DATETIME(7)"
                <| fun _ ->
                    match runSql [ "CREATE TABLE t (a DATETIME(7))" ] with
                    | Fsdb.Executor.Err(1426, msg) -> Expect.equal msg "Too-big precision 7 specified for 'a'. Maximum is 6." "1426 message"
                    | other -> failtestf "expected 1426, got %A" other ]

          testList
              "binary column-definition decimals"
              [ testCase "decimalsOfColumnType carries fsp for the temporal types, 0 otherwise"
                <| fun _ ->
                    Expect.equal (Fsdb.Protocol.decimalsOfColumnType (TDateTime 6)) 6uy "datetime(6)"
                    Expect.equal (Fsdb.Protocol.decimalsOfColumnType (TTimestamp 3)) 3uy "timestamp(3)"
                    Expect.equal (Fsdb.Protocol.decimalsOfColumnType (TTime 2)) 2uy "time(2)"
                    Expect.equal (Fsdb.Protocol.decimalsOfColumnType (TDateTime 0)) 0uy "datetime(0)"
                    Expect.equal (Fsdb.Protocol.decimalsOfColumnType (TInt false)) 0uy "non-temporal"

                testCase "fractionalDigitsOf recovers fsp from a rendered exact-second DATETIME(6) row"
                <| fun _ ->
                    // The wire `decimals` is read back off the fsp-rendered
                    // text (Server derives it this way), so an exact second
                    // still reports 6 because the renderer emitted .000000.
                    Expect.equal (Fsdb.Protocol.fractionalDigitsOf [ Some "2024-01-01 00:00:00.000000" ]) 6uy "six"
                    Expect.equal (Fsdb.Protocol.fractionalDigitsOf [ Some "2024-01-01 00:00:00.500" ]) 3uy "three"
                    Expect.equal (Fsdb.Protocol.fractionalDigitsOf [ Some "2024-01-01 00:00:00" ]) 0uy "none"
                    Expect.equal (Fsdb.Protocol.fractionalDigitsOf [ None; Some "2024-01-01 00:00:00.000000" ]) 6uy "skips leading NULL" ]

          testList
              "expression results render their declared fsp"
              [ testCase "CAST(x AS DATETIME(6)) on an exact second pads to .000000"
                <| fun _ ->
                    // The cast rounds the value to fsp; its result must also
                    // render six digits, the same as a declared DATETIME(6)
                    // column would (MySQL: 2024-01-03 00:00:00.000000).
                    Expect.equal
                        (oneRow [ "SELECT CAST('2024-01-02 23:59:59.9999995' AS DATETIME(6))" ])
                        [ Some "2024-01-03 00:00:00.000000" ]
                        "cast to (6) pads exact second"

                testCase "CAST(x AS DATETIME(3)) renders exactly three digits"
                <| fun _ ->
                    Expect.equal
                        (oneRow [ "SELECT CAST('2024-01-02 10:00:00.126' AS DATETIME(3))" ])
                        [ Some "2024-01-02 10:00:00.126" ]
                        "cast to (3)"

                testCase "MAX/MIN of a DATETIME(6) column inherit its fsp, including an exact second"
                <| fun _ ->
                    Expect.equal
                        (oneRow
                            [ "CREATE TABLE t (c DATETIME(6))"
                              "INSERT INTO t VALUES ('2024-01-01 00:00:00'), ('2024-01-01 00:00:05.250000')"
                              "SELECT MAX(c) FROM t WHERE c < '2024-01-01 00:00:01'" ])
                        [ Some "2024-01-01 00:00:00.000000" ]
                        "MAX exact-second keeps (6) precision"

                testCase "MIN of a DATETIME(6) column inherits its fsp, including an exact second"
                <| fun _ ->
                    Expect.equal
                        (oneRow
                            [ "CREATE TABLE t (c DATETIME(6))"
                              "INSERT INTO t VALUES ('2024-01-01 00:00:00'), ('2024-01-01 00:00:05.250000')"
                              "SELECT MIN(c) FROM t WHERE c < '2024-01-01 00:00:01'" ])
                        [ Some "2024-01-01 00:00:00.000000" ]
                        "MIN exact-second keeps (6) precision"

                testCase "CAST(x AS TIME(3)) renders exactly three digits"
                <| fun _ ->
                    Expect.equal
                        (oneRow [ "SELECT CAST('10:00:00.126' AS TIME(3))" ])
                        [ Some "10:00:00.126" ]
                        "cast to TIME(3)" ]

          testList
              "TIME columns report the TIME wire type"
              [ testCase "a declared TIME(N) column advertises TypeTime, not VAR_STRING"
                <| fun _ ->
                    let mutable session = create 1 (Fsdb.Storage.create ())

                    for sql in [ "CREATE TABLE t (tm TIME(3))"; "INSERT INTO t VALUES ('10:20:30.126')" ] do
                        let s, _ = handle session sql
                        session <- s

                    let s, _ = handle session "SELECT tm FROM t"
                    // fsdb stores TIME as a VString, so the data-driven type
                    // would be VAR_STRING; the declared-type override restores
                    // the real TIME wire type a MySQL client expects.
                    Expect.equal s.LastResultColumnTypes [ Fsdb.Value.TypeTime ] "TIME wire type override" ]

          testList
              "persistence round-trips fsp"
              [ testCase "a DATETIME(6)/TIME(3) column keeps its precision across a snapshot+reload"
                <| fun _ ->
                    let dir = tempDataDir ()
                    let store = load dir
                    attach dir store
                    createTable store defaultDatabase "t" [ col "a" (TDateTime 6); col "b" (TTime 3) ] [] [] None None |> ignore
                    snapshotNow dir store

                    let reloaded = load dir

                    match scan reloaded defaultDatabase "t" with
                    | Ok(columns, _) -> Expect.equal (columns |> List.map (fun c -> c.Type)) [ TDateTime 6; TTime 3 ] "fsp survives reload"
                    | Error e -> failtestf "scan after reload failed: %A" e ] ]
