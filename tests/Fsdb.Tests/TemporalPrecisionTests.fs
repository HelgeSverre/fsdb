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
      Charset = None
      OnUpdateCurrentTimestamp = false }

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

let private allRows (statements: string list) : string option list list =
    match runSql statements with
    | Fsdb.Executor.ResultSet(_, rows) -> rows
    | other -> failtestf "expected a resultset, got %A" other

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
                    | Ok(CreateTable(_, [ { Type = TDateTime 6 }; { Type = TTimestamp 3 }; { Type = TTime 2 }; { Type = TDateTime 0 } ], _, _, _, _, _, _)) -> ()
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
                    Expect.equal (s.LastResultColumnMetadata |> List.map _.TypeId) [ Fsdb.Value.TypeTime ] "TIME wire type override" ]

          testList
              "UNION reconciles fsp across branches"
              // MySQL 8.0 oracle: the reconciled column's fsp is the max
              // declared fsp among branches, and every row renders exactly
              // that many digits — a whole-second DATETIME row unioned with
              // a DATETIME(6) one shows .000000.
              [ let setup =
                    [ "CREATE TABLE a (c DATETIME)"
                      "CREATE TABLE b (c DATETIME(6))"
                      "INSERT INTO a VALUES ('2024-01-01 10:00:00')"
                      "INSERT INTO b VALUES ('2024-01-01 10:00:00.123456')" ]

                testCase "DATETIME UNION DATETIME(6) pads every row to six digits"
                <| fun _ ->
                    Expect.equal
                        (allRows (setup @ [ "SELECT c FROM a UNION SELECT c FROM b" ]))
                        [ [ Some "2024-01-01 10:00:00.000000" ]; [ Some "2024-01-01 10:00:00.123456" ] ]
                        "union pads to max fsp"

                testCase "UNION ALL renders the same six digits"
                <| fun _ ->
                    Expect.equal
                        (allRows (setup @ [ "SELECT c FROM a UNION ALL SELECT c FROM b" ]))
                        [ [ Some "2024-01-01 10:00:00.000000" ]; [ Some "2024-01-01 10:00:00.123456" ] ]
                        "union all pads to max fsp"

                testCase "reversed branch order flips the rows, not the digit count"
                <| fun _ ->
                    Expect.equal
                        (allRows (setup @ [ "SELECT c FROM b UNION SELECT c FROM a" ]))
                        [ [ Some "2024-01-01 10:00:00.123456" ]; [ Some "2024-01-01 10:00:00.000000" ] ]
                        "b-first union"

                    Expect.equal
                        (allRows (setup @ [ "SELECT c FROM b UNION ALL SELECT c FROM a" ]))
                        [ [ Some "2024-01-01 10:00:00.123456" ]; [ Some "2024-01-01 10:00:00.000000" ] ]
                        "b-first union all"

                testCase "CAST branches reconcile to the max declared cast fsp"
                <| fun _ ->
                    // MySQL: '2024-01-01 10:00:00.0' / '2024-01-01 10:00:00.5'
                    // — both exactly one digit, DATETIME(1) winning over the
                    // bare DATETIME cast.
                    Expect.equal
                        (allRows
                            [ "SELECT CAST('2024-01-01 10:00:00' AS DATETIME) UNION SELECT CAST('2024-01-01 10:00:00.5' AS DATETIME(1))" ])
                        [ [ Some "2024-01-01 10:00:00.0" ]; [ Some "2024-01-01 10:00:00.5" ] ]
                        "cast branches at fsp 1" ]

          testList
              "generated columns over fsp sources"
              [ testCase "DATE(created_at) STORED over a DATETIME(6) source keeps the fraction on the source only"
                <| fun _ ->
                    // MySQL 8.4.11 oracle: no error; source keeps all six
                    // digits, the generated column holds the date part.
                    Expect.equal
                        (oneRow
                            [ "CREATE TABLE g (created_at DATETIME(6), d DATE GENERATED ALWAYS AS (DATE(created_at)) STORED)"
                              "INSERT INTO g (created_at) VALUES ('2024-03-05 13:45:09.123456')"
                              "SELECT created_at, d FROM g" ])
                        [ Some "2024-03-05 13:45:09.123456"; Some "2024-03-05" ]
                        "full fraction on the source, date-only on the generated column" ]

          testList
              "microsecond distinctness"
              // Values one microsecond apart are distinct everywhere:
              // GROUP BY, DISTINCT, and UNIQUE keys (MySQL 8.4.11 oracle).
              [ let setup =
                    [ "CREATE TABLE t (dt DATETIME(6) UNIQUE)"
                      "INSERT INTO t VALUES ('2024-01-01 00:00:00.000001')"
                      "INSERT INTO t VALUES ('2024-01-01 00:00:00.000002')" ]

                testCase "GROUP BY separates values one microsecond apart"
                <| fun _ ->
                    Expect.equal
                        (allRows (setup @ [ "SELECT dt, COUNT(*) FROM t GROUP BY dt ORDER BY dt" ]))
                        [ [ Some "2024-01-01 00:00:00.000001"; Some "1" ]
                          [ Some "2024-01-01 00:00:00.000002"; Some "1" ] ]
                        "two groups, one row each"

                testCase "DISTINCT keeps both values"
                <| fun _ ->
                    Expect.equal
                        (allRows (setup @ [ "SELECT DISTINCT dt FROM t ORDER BY dt" ]))
                        [ [ Some "2024-01-01 00:00:00.000001" ]; [ Some "2024-01-01 00:00:00.000002" ] ]
                        "both microsecond-distinct rows survive DISTINCT"

                testCase "UNIQUE rejects only the exact duplicate, not the microsecond neighbour"
                <| fun _ ->
                    match runSql (setup @ [ "INSERT INTO t VALUES ('2024-01-01 00:00:00.000001')" ]) with
                    | Fsdb.Executor.Err(1062, _) -> ()
                    | other -> failtestf "expected duplicate-key error 1062, got %A" other ]

          testList
              "scalar functions stringify temporals without the expression's fsp (known divergence)"
              [ testCase "SHA2 over an int matches MySQL; over a CAST DATETIME(3) it hashes six digits, not fsp-padded three"
                <| fun _ ->
                    // SHA2(123, 256) hashes the decimal string '123' — same
                    // as MySQL.
                    Expect.equal
                        (oneRow [ "SELECT SHA2(123, 256)" ])
                        [ Some "a665a45920422f9d417e4867efdc4fb8a04a1f3fff1fa07e998e86f7f7a27ae3" ]
                        "SHA2 of an int hashes its decimal string"

                    // Known divergence: MySQL renders the CAST at its
                    // declared fsp=3 and hashes '2024-01-01 00:00:00.500'
                    // (-> cf0c35779c80fb3d9ca4fc0c138402f1e0eb7417bcf07f49f98f26c3a798e165);
                    // fsdb's function-argument stringification has no fsp,
                    // so `Value.toText` renders six digits and it hashes
                    // '2024-01-01 00:00:00.500000' instead. Fixing this
                    // means fsp-aware temporal arg rendering across all
                    // string-taking scalars (MD5/CONCAT/... share it), not a
                    // SHA2-local patch — this pins the current behavior.
                    Expect.equal
                        (oneRow [ "SELECT SHA2(CAST('2024-01-01 00:00:00.5' AS DATETIME(3)), 256)" ])
                        [ Some "a575d69101492a18a6de9a6104f8e4abaa0306ab1c464b89eb6a5c9f3896a304" ]
                        "current behavior: hashes the fsp-6 rendering" ]

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
                    | Error e -> failtestf "scan after reload failed: %A" e

                testCase "a WAL-tail-only reload (no snapshot) keeps both the microsecond ticks and the column's fsp"
                <| fun _ ->
                    // A decode bug dropping the CreateTable entry's fsp byte
                    // would still pass a ticks-only assertion yet render
                    // zero fractional digits — so this asserts the reloaded
                    // column type AND the SQL-level rendering.
                    let dir = tempDataDir ()
                    let store = load dir
                    attach dir store
                    let session = create 1 store

                    handle session "CREATE TABLE w (c DATETIME(6))" |> ignore
                    handle session "INSERT INTO w VALUES ('2024-03-05 13:45:09.123456')" |> ignore
                    // Deliberately no snapshot: everything replays from the
                    // WAL tail alone.

                    let reloaded = load dir

                    match scan reloaded defaultDatabase "w" with
                    | Ok(columns, _) -> Expect.equal (columns |> List.map (fun c -> c.Type)) [ TDateTime 6 ] "fsp survives a WAL-only reload"
                    | Error e -> failtestf "scan after reload failed: %A" e

                    match handle (create 2 reloaded) "SELECT c FROM w" |> snd with
                    | Fsdb.Executor.ResultSet(_, [ [ Some "2024-03-05 13:45:09.123456" ] ]) -> ()
                    | other -> failtestf "expected all six microsecond digits after a WAL-only reload, got %A" other ] ]
