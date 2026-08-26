module Fsdb.Tests.StorageTests

open Expecto
open Fsdb
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Temporal
open Fsdb.Engine

let private prepareRow _ row = Ok row

let private col name ty nullable =
    { Name = name
      Type = ty
      Nullable = nullable
      Default = None
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      Generated = None
      Comment = ""
      Collation = None
      Charset = None
      OnUpdateCurrentTimestamp = false }

let private idCol =
    { (col "id" (TInt false) false) with
        AutoIncrement = true
        PrimaryKey = true }

let private usersColumns =
    [ idCol
      col "name" (TVarchar 255) false
      { (col "age" (TInt false) true) with Default = Some(DConst(VInt 0L)) } ]

/// A store with an empty `users` table, ready to insert into.
let private withUsersTable () =
    let store = create ()
    createTable store defaultDatabase "users" usersColumns [] [] None None |> ignore
    store

let tests =
    testList
        "storage"
        [ testList
              "system catalog rows"
              [ testCase "typed trigger updates preserve legacy rows"
                <| fun _ ->
                    let legacy =
                        [| VString "audit"
                           VString "app"
                           VString "users"
                           VString "AFTER"
                           VString "INSERT"
                           VString "SET @seen = 1" |]

                    let updated = legacy |> SystemCatalog.Trigger.withActionOrder 4L

                    match SystemCatalog.Trigger.tryRead updated with
                    | Some trigger ->
                        Expect.equal trigger.Name "audit" "name"
                        Expect.equal trigger.Table "users" "table"
                        Expect.equal trigger.Order 4L "order"
                    | None -> failtest "expected trigger row"

                testCase "typed check rows reject missing required fields"
                <| fun _ ->
                    Expect.isNone (SystemCatalog.Check.tryRead [| VString "incomplete" |]) "incomplete row"

                    let row =
                        [| VString "positive"
                           VString "app"
                           VString "items"
                           VString "amount > 0"
                           VString "YES"
                           VNull
                           VString "NO"
                           VInt 2L |]

                    match SystemCatalog.Check.tryRead row with
                    | Some check ->
                        Expect.equal check.Name "positive" "name"
                        Expect.isTrue check.Enforced "enforced"
                        Expect.equal check.Ordinal 2 "ordinal"
                    | None -> failtest "expected check row"

                testCase "typed routine and event rows expose named fields"
                <| fun _ ->
                    let created = System.DateTime(2026, 8, 25, 10, 0, 0)

                    let routineRow =
                        [| VString "app"; VString "refresh"; VString "SELECT 1"; VDateTime created; VString "owner@%" |]

                    let eventRow =
                        [| VString "app"; VString "nightly"; VString "EVERY 1 DAY"; VString "CALL refresh()"
                           VDateTime created; VString "owner@%"; VString "ENABLED" |]

                    match SystemCatalog.Routine.tryRead routineRow, SystemCatalog.Event.tryRead eventRow with
                    | Some routine, Some event ->
                        Expect.equal routine.Definition "SELECT 1" "routine definition"
                        Expect.equal event.Schedule "EVERY 1 DAY" "event schedule"
                        Expect.isTrue (SystemCatalog.Routine.matches "APP" "REFRESH" routine) "routine identity"
                        Expect.isTrue (SystemCatalog.Event.rowMatches "APP" "NIGHTLY" eventRow) "event row identity"
                    | _ -> failtest "expected routine and event rows" ]

          testList
              "createTable / dropTable / truncate"
              [ testCase "createTable then scan sees the table with no rows"
                <| fun _ ->
                    let store = withUsersTable ()

                    match scan store defaultDatabase "users" with
                    | Ok(columns, rows) ->
                        Expect.equal (List.length columns) 3 "column count"
                        Expect.isEmpty (List.ofSeq rows) "no rows yet"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "createTable twice returns TableExists"
                <| fun _ ->
                    let store = withUsersTable ()

                    match createTable store defaultDatabase "users" usersColumns [] [] None None with
                    | Error(TableExists "users") -> ()
                    | other -> failtestf "expected TableExists, got %A" other

                testCase "createTable is case-insensitive against an existing table"
                <| fun _ ->
                    let store = withUsersTable ()

                    match createTable store defaultDatabase "USERS" usersColumns [] [] None None with
                    | Error(TableExists _) -> ()
                    | other -> failtestf "expected TableExists, got %A" other

                testCase "createTable auto-creates the database on first use"
                <| fun _ ->
                    let store = create ()

                    match createTable store "newdb" "users" usersColumns [] [] None None with
                    | Ok() ->
                        match scan store "newdb" "users" with
                        | Ok _ -> ()
                        | Error e -> failtestf "expected the table to exist, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "BIT width is constrained to MySQL's one-to-sixty-four range"
                <| fun _ ->
                    let store = create ()

                    match createTable store defaultDatabase "zero_bits" [ col "bits" (TBit 0) true ] [] [] None None with
                    | Error(ExpressionError(3013, _)) -> ()
                    | other -> failtestf "expected invalid BIT size, got %A" other

                    match createTable store defaultDatabase "wide_bits" [ col "bits" (TBit 65) true ] [] [] None None with
                    | Error(ExpressionError(1439, _)) -> ()
                    | other -> failtestf "expected oversized BIT width, got %A" other

                testCase "column comments allow 1024 Unicode scalars and reject longer text"
                <| fun _ ->
                    let store = create ()
                    let atLimit = { col "ok" (TInt false) true with Comment = String.replicate 1024 "😀" }
                    let tooLong = { col "too_long" (TInt false) true with Comment = String.replicate 1025 "x" }

                    Expect.equal (createTable store defaultDatabase "comment_limit" [ atLimit ] [] [] None None) (Ok()) "1024 scalars are valid"

                    match createTable store defaultDatabase "comment_too_long" [ tooLong ] [] [] None None with
                    | Error(ExpressionError(1629, "Comment for field 'too_long' is too long (max = 1024)")) -> ()
                    | other -> failtestf "expected comment-length error, got %A" other

                testCase "scan on an unknown table returns NoSuchTable"
                <| fun _ ->
                    let store = create ()

                    match scan store defaultDatabase "ghosts" with
                    | Error(NoSuchTable "ghosts") -> ()
                    | other -> failtestf "expected NoSuchTable, got %A" other

                testCase "scan on an unknown database returns NoSuchDatabase"
                <| fun _ ->
                    let store = create ()

                    match scan store "ghostdb" "ghosts" with
                    | Error(NoSuchDatabase "ghostdb") -> ()
                    | other -> failtestf "expected NoSuchDatabase, got %A" other

                testCase "dropTable removes the table"
                <| fun _ ->
                    let store = withUsersTable ()
                    dropTable store defaultDatabase "users" |> ignore

                    match scan store defaultDatabase "users" with
                    | Error(NoSuchTable _) -> ()
                    | other -> failtestf "expected NoSuchTable after drop, got %A" other

                testCase "dropTable on an unknown table returns NoSuchTable"
                <| fun _ ->
                    let store = create ()

                    match dropTable store defaultDatabase "ghosts" with
                    | Error(NoSuchTable "ghosts") -> ()
                    | other -> failtestf "expected NoSuchTable, got %A" other

                testCase "truncate clears rows and resets the AUTO_INCREMENT counter"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ]
                    |> ignore

                    truncate store defaultDatabase "users" |> ignore

                    match scan store defaultDatabase "users" with
                    | Ok(_, rows) -> Expect.isEmpty (List.ofSeq rows) "rows cleared"
                    | Error e -> failtestf "expected Ok, got %A" e

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "bob"; VInt 40L ] ] with
                    | Ok { LastInsertId = lastId } -> Expect.equal lastId 1L "AUTO_INCREMENT restarts at 1 after truncate"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "truncate on an unknown table returns NoSuchTable"
                <| fun _ ->
                    let store = create ()

                    match truncate store defaultDatabase "ghosts" with
                    | Error(NoSuchTable "ghosts") -> ()
                    | other -> failtestf "expected NoSuchTable, got %A" other

                testCase "truncate resets the PRIMARY KEY index too, not just NextAutoId — re-inserting the same id after a truncate isn't a stale duplicate"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore
                    truncate store defaultDatabase "users" |> ignore

                    match insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "bob"; VInt 40L ] ] with
                    | Ok { LastInsertId = 1L; Affected = 1 } -> ()
                    | other -> failtestf "expected id 1 to be free again after truncate, got %A" other ]

          testList
              "insertRows"
              [ testCase "inserting all columns by position returns lastInsertId 1 and affected 1"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] with
                    | Ok { LastInsertId = lastId; GeneratedId = generatedId; Affected = affected } ->
                        Expect.equal lastId 1L "first assigned id"
                        Expect.equal generatedId (Some 1L) "the id was actually generated, so LAST_INSERT_ID() sees it too"
                        Expect.equal affected 1 "one row affected"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AUTO_INCREMENT assigns sequential ids across inserts"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ]
                    |> ignore

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "bob"; VInt 25L ] ] with
                    | Ok { LastInsertId = lastId } -> Expect.equal lastId 2L "second row gets id 2"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AUTO_INCREMENT assigns lastInsertId to the first row of a multi-row insert"
                <| fun _ ->
                    let store = withUsersTable ()

                    match
                        insertRows
                            store
                            defaultDatabase
                            "users"
                            None
                            [ [ VNull; VString "alice"; VInt 30L ]
                              [ VNull; VString "bob"; VInt 25L ] ]
                    with
                    | Ok { LastInsertId = lastId; Affected = affected } ->
                        Expect.equal lastId 1L "lastInsertId is the first row's id"
                        Expect.equal affected 2 "two rows affected"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "an explicit id bumps the AUTO_INCREMENT counter past it"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows store defaultDatabase "users" None [ [ VInt 100L; VString "alice"; VInt 30L ] ]
                    |> ignore

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "bob"; VInt 25L ] ] with
                    | Ok { LastInsertId = lastId } -> Expect.equal lastId 101L "counter continues past the explicit id"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "an explicit unsigned AUTO_INCREMENT value is preserved"
                <| fun _ ->
                    let store = create ()

                    let id =
                        { (col "id" (TBigInt true) false) with
                            AutoIncrement = true
                            PrimaryKey = true }

                    createTable store defaultDatabase "unsigned_ids" [ id ] [] [] None None |> ignore

                    match insertRows store defaultDatabase "unsigned_ids" None [ [ VUInt 4UL ] ] with
                    | Ok { LastInsertId = 4L; Affected = 1 } ->
                        match scan store defaultDatabase "unsigned_ids" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows) [ [| VUInt 4UL |] ] "stored value"
                        | Error e -> failtestf "expected Ok scan, got %A" e
                    | other -> failtestf "expected explicit unsigned id 4, got %A" other

                testCase "a single explicit-id insert reports that id as lastInsertId, matching real MySQL's OK packet (not the SQL LAST_INSERT_ID() function's 0)"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRows store defaultDatabase "users" None [ [ VInt 800L; VString "alice"; VInt 30L ] ] with
                    | Ok { LastInsertId = lastId; GeneratedId = generatedId; Affected = affected } ->
                        Expect.equal lastId 800L "lastInsertId is the row's explicit id, like PDO::lastInsertId()"
                        Expect.equal generatedId None "no id was actually generated, so LAST_INSERT_ID() must not see 800"
                        Expect.equal affected 1 "one row affected"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a generated id anywhere in a multi-row insert wins over an explicit id, even one earlier in the batch"
                <| fun _ ->
                    let store = withUsersTable ()

                    match
                        insertRows
                            store
                            defaultDatabase
                            "users"
                            None
                            [ [ VInt 500L; VString "alice"; VInt 30L ]
                              [ VNull; VString "bob"; VInt 25L ] ]
                    with
                    | Ok { LastInsertId = lastId } -> Expect.equal lastId 501L "the generated second row's id wins over the first row's explicit one"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "an all-explicit multi-row insert reports the last row's id, not the first"
                <| fun _ ->
                    let store = withUsersTable ()

                    match
                        insertRows
                            store
                            defaultDatabase
                            "users"
                            None
                            [ [ VInt 700L; VString "alice"; VInt 30L ]
                              [ VInt 701L; VString "bob"; VInt 25L ] ]
                    with
                    | Ok { LastInsertId = lastId } -> Expect.equal lastId 701L "the last row's explicit id, matching real MySQL"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "inserting by explicit column list fills the rest from defaults"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRows store defaultDatabase "users" (Some [ "name" ]) [ [ VString "alice" ] ] with
                    | Ok { LastInsertId = lastId; Affected = affected } ->
                        Expect.equal lastId 1L "AUTO_INCREMENT still assigned"
                        Expect.equal affected 1 "one row"

                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) ->
                            match List.ofSeq rows with
                            | [ row ] -> Expect.equal row.[2] (VInt 0L) "age falls back to its default"
                            | other -> failtestf "expected one row, got %A" other
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "DEFAULT CURRENT_TIMESTAMP evaluates to a real VDateTime, not the marker text"
                <| fun _ ->
                    let store = create ()

                    let columns =
                        [ col "id" (TInt false) false
                          { (col "created_at" (TTimestamp 0) true) with Default = Some DCurrentTimestamp } ]

                    createTable store defaultDatabase "posts" columns [] [] None None |> ignore

                    match insertRows store defaultDatabase "posts" (Some [ "id" ]) [ [ VInt 1L ] ] with
                    | Ok _ ->
                        match scan store defaultDatabase "posts" with
                        | Ok(_, rows) ->
                            match List.ofSeq rows with
                            | [ row ] ->
                                match row.[1] with
                                | VDateTime _ -> ()
                                | other -> failtestf "expected a VDateTime default, got %A" other
                            | other -> failtestf "expected one row, got %A" other
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "missing nullable column with no default becomes NULL"
                <| fun _ ->
                    let store = create ()

                    let columns =
                        [ col "id" (TInt false) false; col "nickname" (TVarchar 50) true ]

                    createTable store defaultDatabase "t" columns [] [] None None |> ignore

                    match insertRows store defaultDatabase "t" (Some [ "id" ]) [ [ VInt 1L ] ] with
                    | Ok _ ->
                        match scan store defaultDatabase "t" with
                        | Ok(_, rows) ->
                            match List.ofSeq rows with
                            | [ row ] -> Expect.equal row.[1] VNull "nickname is NULL"
                            | other -> failtestf "expected one row, got %A" other
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "inserting an unknown column returns UnknownColumn"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRows store defaultDatabase "users" (Some [ "nope" ]) [ [ VString "x" ] ] with
                    | Error(UnknownColumn "nope") -> ()
                    | other -> failtestf "expected UnknownColumn, got %A" other

                testCase "a row with too few values returns ColumnCountMismatch"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "alice" ] ] with
                    | Error(ColumnCountMismatch(3, 2)) -> ()
                    | other -> failtestf "expected ColumnCountMismatch(3, 2), got %A" other

                testCase "omitting a NOT NULL column with no default returns NotNullViolation"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRows store defaultDatabase "users" (Some [ "id" ]) [ [ VNull ] ] with
                    | Error(NotNullViolation "name") -> ()
                    | other -> failtestf "expected NotNullViolation on 'name', got %A" other

                testCase "explicit NULL for a NOT NULL column returns NotNullViolation"
                <| fun _ ->
                    let store = withUsersTable ()

                    match
                        insertRows
                            store
                            defaultDatabase
                            "users"
                            None
                            [ [ VNull; VNull; VInt 30L ] ]
                    with
                    | Error(NotNullViolation "name") -> ()
                    | other -> failtestf "expected NotNullViolation on 'name', got %A" other

                testCase "a PRIMARY KEY is implicitly NOT NULL"
                <| fun _ ->
                    let store = create ()
                    let primary = { (col "id" (TInt false) true) with PrimaryKey = true }
                    Expect.isOk (createTable store defaultDatabase "primary_values" [ primary ] [] [] None None) "table created"

                    match scan store defaultDatabase "primary_values" with
                    | Ok([ column ], _) -> Expect.isFalse column.Nullable "stored primary-key metadata is non-nullable"
                    | other -> failtestf "expected one primary-key column, got %A" other

                    let catalog = store.Catalog
                    let database = catalog.[defaultDatabase]
                    let legacyTable = { database.["primary_values"] with Columns = [ primary ] }
                    setCatalog store (Map.add defaultDatabase (Map.add "primary_values" legacyTable database) catalog)

                    match insertRows store defaultDatabase "primary_values" None [ [ VNull ] ] with
                    | Error(NotNullViolation "id") -> ()
                    | other -> failtestf "expected implicit primary-key NOT NULL enforcement, got %A" other

                testCase "a numeric string coerces into an INT column"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VString "42" ] ] with
                    | Ok _ ->
                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) ->
                            match List.ofSeq rows with
                            | [ row ] -> Expect.equal row.[2] (VInt 42L) "age coerced from string"
                            | other -> failtestf "expected one row, got %A" other
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a non-numeric string into an INT column returns error 1366"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VString "not-a-number" ] ] with
                    | Error(InvalidValueForColumn("age", "not-a-number")) ->
                        let code, _ = toMySqlError (InvalidValueForColumn("age", "not-a-number"))
                        Expect.equal code 1366 "MySQL error code"
                    | other -> failtestf "expected InvalidValueForColumn, got %A" other

                testCase "outside STRICT_TRANS_TABLES, a non-numeric string into an INT column coerces to 0"
                <| fun _ ->
                    let store = withUsersTable ()
                    setStrictMode store false

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VString "not-a-number" ] ] with
                    | Ok _ ->
                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) ->
                            match List.ofSeq rows with
                            | [ row ] -> Expect.equal row.[2] (VInt 0L) "age coerced to 0"
                            | other -> failtestf "expected one row, got %A" other
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e ]

          testList
              "coerceValue non-strict fallback"
              [ testCase "strict mode rejects an unparseable datetime string"
                <| fun _ ->
                    match coerceValue true (col "established" (TDateTime 0) true) (VString "") with
                    | Error(InvalidValueForColumn("established", "")) -> ()
                    | other -> failtestf "expected InvalidValueForColumn, got %A" other

                testCase "non-strict mode coerces an unparseable datetime string on a nullable column to a zero datetime"
                <| fun _ ->
                    match coerceValue false (col "established" (TDateTime 0) true) (VString "") with
                    | Ok(VZeroDateTime dateTime) ->
                        Expect.equal (toText (VZeroDateTime dateTime)) (Some "0000-00-00 00:00:00") "zero datetime fallback"
                    | other -> failtestf "expected a zero datetime, got %A" other

                testCase "non-strict mode coerces an unparseable datetime string on a NOT NULL column to a zero datetime"
                <| fun _ ->
                    match coerceValue false (col "established" (TDateTime 0) false) (VString "") with
                    | Ok(VZeroDateTime dateTime) ->
                        Expect.equal (toText (VZeroDateTime dateTime)) (Some "0000-00-00 00:00:00") "zero datetime fallback"
                    | other -> failtestf "expected a zero datetime, got %A" other

                testCase "non-strict mode preserves a partial zero date"
                <| fun _ ->
                    match
                        coerceValueWithMode
                            { Strict = false
                              NoZeroDate = false
                              NoZeroInDate = false }
                            (col "established" TDate false)
                            (VString "2020-00-01")
                    with
                    | Ok(VZeroDate date) -> Expect.equal (zeroDateParts date) (2020, 0, 1) "zero month survives"
                    | other -> failtestf "expected a zero date, got %A" other

                testCase "strict mode rejects a zero datetime"
                <| fun _ ->
                    match coerceValue true (col "established" (TDateTime 0) false) (VString "0000-00-00 00:00:00") with
                    | Error(ZeroTemporalForColumn _) -> ()
                    | other -> failtestf "expected ZeroTemporalForColumn, got %A" other ]

          testList
              "coerceValue SET columns"
              [ let tagsCol = col "tags" (TSet [ "a"; "b"; "c" ]) true

                testCase "members land in declaration order, deduplicated, regardless of input order"
                <| fun _ ->
                    match coerceValue true tagsCol (VString "b,a") with
                    | Ok(VString "a,b") -> ()
                    | other -> failtestf "expected 'a,b', got %A" other

                    match coerceValue true tagsCol (VString "a,a,b") with
                    | Ok(VString "a,b") -> ()
                    | other -> failtestf "expected 'a,b', got %A" other

                testCase "the empty string is always a legal SET value"
                <| fun _ ->
                    match coerceValue true tagsCol (VString "") with
                    | Ok(VString "") -> ()
                    | other -> failtestf "expected Ok(VString \"\"), got %A" other

                testCase "a numeric value is a bitmask over the declared members"
                <| fun _ ->
                    match coerceValue true tagsCol (VInt 5L) with
                    | Ok(VString "a,c") -> ()
                    | other -> failtestf "expected 'a,c' (bits 0 and 2), got %A" other

                testCase "strict mode rejects a member that isn't declared"
                <| fun _ ->
                    match coerceValue true tagsCol (VString "a,x") with
                    | Error(DataTruncatedForColumn "tags") ->
                        let code, _ = toMySqlError (DataTruncatedForColumn "tags")
                        Expect.equal code 1265 "MySQL error code"
                    | other -> failtestf "expected DataTruncatedForColumn, got %A" other

                testCase "non-strict mode silently drops undeclared members instead of failing"
                <| fun _ ->
                    match coerceValue false tagsCol (VString "a,x") with
                    | Ok(VString "a") -> ()
                    | other -> failtestf "expected 'a' (x dropped), got %A" other

                testCase "non-strict mode masks an out-of-range numeric bitmask down to the valid bits"
                <| fun _ ->
                    match coerceValue false tagsCol (VInt 999L) with
                    | Ok(VString "a,b,c") -> ()
                    | other -> failtestf "expected 'a,b,c' (999 masked to the 3 declared bits), got %A" other

                testCase "strict mode rejects an out-of-range numeric bitmask"
                <| fun _ ->
                    match coerceValue true tagsCol (VInt 999L) with
                    | Error(DataTruncatedForColumn "tags") -> ()
                    | other -> failtestf "expected DataTruncatedForColumn, got %A" other ]

          testList
              "coerceValue TIME columns"
              [ let time = col "elapsed" (TTime 2) true

                testCase "rounds fractions and preserves signed hours beyond one day"
                <| fun _ ->
                    match coerceValue true time (VString "-25:02:03.455") with
                    | Ok(VTime value) -> Expect.equal (formatTimeValueFsp 2 value) "-25:02:03.46" "stored time"
                    | other -> failtestf "expected VTime, got %A" other

                testCase "accepts MySQL's compact numeric time form"
                <| fun _ ->
                    match coerceValue true (col "elapsed" (TTime 6) true) (VDecimal 10203.5m) with
                    | Ok(VTime value) -> Expect.equal (formatTimeValueFsp 6 value) "01:02:03.500000" "numeric time"
                    | other -> failtestf "expected VTime, got %A" other

                testCase "rounds fractions longer than six digits"
                <| fun _ ->
                    match coerceValue true (col "elapsed" (TTime 6) true) (VString "01:02:03.12345678") with
                    | Ok(VTime value) -> Expect.equal (formatTimeValueFsp 6 value) "01:02:03.123457" "fraction"
                    | other -> failtestf "expected VTime, got %A" other

                testCase "strict mode rejects values beyond the TIME range"
                <| fun _ ->
                    let time = col "elapsed" (TTime 6) true

                    for value in [ "838:59:59.000001"; "-838:59:59.000001" ] do
                        match coerceValue true time (VString value) with
                        | Error(ExpressionError(1292, _)) -> ()
                        | other -> failtestf "expected time range rejection for %s, got %A" value other

                testCase "non-strict mode clamps values beyond the TIME range"
                <| fun _ ->
                    for input, expected in [ "838:59:59.000001", "838:59:59.000000"; "-838:59:59.000001", "-838:59:59.000000" ] do
                        match coerceValue false (col "elapsed" (TTime 6) true) (VString input) with
                        | Ok(VTime value) -> Expect.equal (formatTimeValueFsp 6 value) expected "clamped time"
                        | other -> failtestf "expected clamped VTime, got %A" other

                testCase "non-strict mode clamps oversized hour fields"
                <| fun _ ->
                    match coerceValue false (col "elapsed" (TTime 6) true) (VString "1000:00:00") with
                    | Ok(VTime value) -> Expect.equal (formatTimeValueFsp 6 value) "838:59:59.000000" "clamped time"
                    | other -> failtestf "expected clamped VTime, got %A" other

                testCase "non-strict mode distinguishes invalid TIME components from text"
                <| fun _ ->
                    let time = col "elapsed" (TTime 6) true

                    for input, code in [ "10:60:00", 1264; "10:20:60", 1264; "not a time", 1265 ] do
                        let expectedParse = if code = 1264 then TimeComponentsOutOfRange else NotATime
                        Expect.equal (parseTimeInput input) expectedParse input
                        let result, diagnostics = Diagnostics.capture (fun () -> coerceValue false time (VString input))
                        Expect.equal result (Ok(VTime(timeValueOrClamp 0L))) input
                        Expect.equal (diagnostics |> List.map _.Code) [ code ] input

                testCase "strict mode rejects invalid TIME components"
                <| fun _ ->
                    match coerceValue true (col "elapsed" (TTime 6) true) (VString "10:60:00") with
                    | Error(ExpressionError(1292, _)) -> ()
                    | other -> failtestf "expected time component rejection, got %A" other

                testCase "TIME rejects CURRENT_TIMESTAMP defaults"
                <| fun _ ->
                    let store = create ()
                    let time = { (col "elapsed" (TTime 6) true) with Default = Some DCurrentTimestamp }

                    match createTable store defaultDatabase "times" [ time ] [] [] None None with
                    | Error(InvalidDefaultValue "elapsed") -> ()
                    | other -> failtestf "expected invalid TIME default, got %A" other ]

          testList
              "coerceValue BIT columns"
              [ let bits = col "bits" (TBit 3) true

                testCase "bit and numeric literals retain an exact width and numeric value"
                <| fun _ ->
                    Expect.equal (coerceValue true bits (VBytes [| 0b00000101uy |])) (Ok(VBit(3, 5UL))) "binary literal"
                    Expect.equal (coerceValue true bits (VInt 2L)) (Ok(VBit(3, 2UL))) "numeric literal"
                    Expect.equal (coerceValue true bits (VDecimal 1.5m)) (Ok(VBit(3, 2UL))) "numeric rounding"

                testCase "strict mode rejects a value outside the bit width"
                <| fun _ ->
                    match coerceValue true bits (VInt 8L) with
                    | Error(DataTooLongForColumn("bits", _)) -> ()
                    | other -> failtestf "expected DataTooLongForColumn, got %A" other

                testCase "non-strict mode clamps an out-of-range bit value"
                <| fun _ ->
                    Expect.equal (coerceValue false bits (VInt 8L)) (Ok(VBit(3, 7UL))) "max bit value"

                testCase "BIT values coerce through numeric targets"
                <| fun _ ->
                    let value = VBit(3, 5UL)
                    Expect.equal (coerceValue true (col "integer" (TInt false) true) value) (Ok(VInt 5L)) "integer"
                    Expect.equal (coerceValue true (col "unsigned" (TBigInt true) true) value) (Ok(VUInt 5UL)) "unsigned"
                    Expect.equal (coerceValue true (col "decimal" (TDecimal(10, 0)) true) value) (Ok(VDecimal 5m)) "decimal"
                    Expect.equal (coerceValue true (col "double" TDouble true) value) (Ok(VDouble 5.0)) "double"
                    Expect.equal (coerceValue true (col "year" TYear true) value) (Ok(VInt 5L)) "year"
                    Expect.equal (coerceValue true (col "set" (TSet [ "a"; "b"; "c" ]) true) value) (Ok(VString "a,c")) "set bitmask"
                    Expect.equal (coerceValue true (col "enum" (TEnum [ "a"; "b"; "c" ]) true) (VBit(3, 2UL))) (Ok(VString "b")) "enum index" ]

          testList
              "coerceValue spatial columns"
              [ testCase "a POINT column accepts points and rejects another geometry type"
                <| fun _ ->
                    let point = VGeometry(tryGeometryFromText 4326 "POINT(1 2)" |> Option.get)
                    let line = VGeometry(tryGeometryFromText 4326 "LINESTRING(0 0,1 1)" |> Option.get)
                    let column = col "location" (TGeometry Point) true

                    Expect.equal (coerceValue true column point) (Ok point) "point retained"

                    match coerceValue true column line with
                    | Error(InvalidValueForColumn("location", _)) -> ()
                    | other -> failtestf "expected geometry type rejection, got %A" other ]

          testList
              "unique constraints"
              [ let emailsTable store =
                    createTable
                        store
                        defaultDatabase
                        "emails"
                        [ col "id" (TInt false) false; col "email" (TVarchar 255) false ]
                        [ { Name = "uq_email"; Columns = [ "email" ]; Unique = true; Kind = BTree } ]
                        []
                        None
                        None
                    |> ignore

                testCase "a plain INSERT violating a UNIQUE index returns error 1062"
                <| fun _ ->
                    let store = create ()
                    emailsTable store
                    insertRows store defaultDatabase "emails" None [ [ VInt 1L; VString "a@x.com" ] ] |> ignore

                    match insertRows store defaultDatabase "emails" None [ [ VInt 2L; VString "a@x.com" ] ] with
                    | Error(DuplicateKey("uq_email", "a@x.com")) ->
                        let code, _ = toMySqlError (DuplicateKey("uq_email", "a@x.com"))
                        Expect.equal code 1062 "MySQL error code"
                    | other -> failtestf "expected DuplicateKey, got %A" other

                testCase "a plain INSERT violating the primary key returns error 1062 for key PRIMARY"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ]
                    |> ignore

                    match insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "bob"; VInt 25L ] ] with
                    | Error(DuplicateKey("PRIMARY", "1")) -> ()
                    | other -> failtestf "expected DuplicateKey on PRIMARY, got %A" other

                testCase "the unique check is collation-aware: case folds, trailing spaces stay distinct (NO PAD)"
                <| fun _ ->
                    let store = create ()
                    emailsTable store
                    insertRows store defaultDatabase "emails" None [ [ VInt 1L; VString "a@x.com" ] ] |> ignore

                    // case-insensitive: 'A@X.COM' collides with 'a@x.com'
                    match insertRows store defaultDatabase "emails" None [ [ VInt 2L; VString "A@X.COM" ] ] with
                    | Error(DuplicateKey("uq_email", _)) -> ()
                    | other -> failtestf "expected DuplicateKey, got %A" other

                    // NO PAD: a trailing space makes it a different value,
                    // exactly as MySQL 8's utf8mb4_0900_ai_ci treats it
                    match insertRows store defaultDatabase "emails" None [ [ VInt 3L; VString "a@x.com " ] ] with
                    | Ok _ -> ()
                    | other -> failtestf "expected the space-suffixed email to insert under NO PAD, got %A" other

                testCase "two colliding rows within the same multi-row INSERT also return error 1062"
                <| fun _ ->
                    let store = create ()
                    emailsTable store

                    match
                        insertRows
                            store
                            defaultDatabase
                            "emails"
                            None
                            [ [ VInt 1L; VString "a@x.com" ]
                              [ VInt 2L; VString "a@x.com" ] ]
                    with
                    | Error(DuplicateKey("uq_email", "a@x.com")) -> ()
                    | other -> failtestf "expected DuplicateKey, got %A" other

                testCase "UPDATE colliding with another row's unique value returns error 1062"
                <| fun _ ->
                    let store = create ()
                    emailsTable store

                    insertRows
                        store
                        defaultDatabase
                        "emails"
                        None
                        [ [ VInt 1L; VString "a@x.com" ]
                          [ VInt 2L; VString "b@x.com" ] ]
                    |> ignore

                    let updater (row: Value[]) = Ok [| row.[0]; VString "a@x.com" |]

                    match updateRows store defaultDatabase "emails" None (fun row -> Ok(row.[0] = VInt 2L)) updater with
                    | Error(DuplicateKey("uq_email", "a@x.com")) -> ()
                    | other -> failtestf "expected DuplicateKey, got %A" other

                testCase "UPDATE that leaves a row's own unique value unchanged doesn't collide with itself"
                <| fun _ ->
                    let store = create ()
                    emailsTable store
                    insertRows store defaultDatabase "emails" None [ [ VInt 1L; VString "a@x.com" ] ] |> ignore

                    let updater (row: Value[]) = Ok row

                    match updateRows store defaultDatabase "emails" None (fun _ -> Ok true) updater with
                    | Ok _ -> ()
                    | Error e -> failtestf "expected Ok (no self-collision), got %A" e

                testCase "a composite primary key rejects a second row with the same column combination"
                <| fun _ ->
                    let store = create ()

                    createTable
                        store
                        defaultDatabase
                        "role_user"
                        [ { (col "role_id" (TInt false) false) with PrimaryKey = true }
                          { (col "user_id" (TInt false) false) with PrimaryKey = true } ]
                        []
                        []
                        None
                        None
                    |> ignore

                    insertRows store defaultDatabase "role_user" None [ [ VInt 1L; VInt 2L ] ] |> ignore

                    match insertRows store defaultDatabase "role_user" None [ [ VInt 1L; VInt 2L ] ] with
                    | Error(DuplicateKey("PRIMARY", _)) -> ()
                    | other -> failtestf "expected DuplicateKey on the composite PRIMARY, got %A" other

                testCase "a composite primary key allows rows that differ in either column"
                <| fun _ ->
                    let store = create ()

                    createTable
                        store
                        defaultDatabase
                        "role_user"
                        [ { (col "role_id" (TInt false) false) with PrimaryKey = true }
                          { (col "user_id" (TInt false) false) with PrimaryKey = true } ]
                        []
                        []
                        None
                        None
                    |> ignore

                    insertRows store defaultDatabase "role_user" None [ [ VInt 1L; VInt 2L ] ] |> ignore

                    match insertRows store defaultDatabase "role_user" None [ [ VInt 1L; VInt 3L ] ] with
                    | Ok _ -> ()
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a multi-row UPDATE that moves two rows onto the same UNIQUE value returns error 1062"
                <| fun _ ->
                    let store = create ()
                    emailsTable store

                    insertRows
                        store
                        defaultDatabase
                        "emails"
                        None
                        [ [ VInt 1L; VString "a@x.com" ]
                          [ VInt 2L; VString "b@x.com" ] ]
                    |> ignore

                    match updateRows store defaultDatabase "emails" None (fun _ -> Ok true) (fun row -> Ok [| row.[0]; VString "same@x.com" |]) with
                    | Error(DuplicateKey("uq_email", "same@x.com")) -> ()
                    | other -> failtestf "expected DuplicateKey, got %A" other ]

          testList
              "INSERT IGNORE"
              [ testCase "insertRowsIgnore skips a NOT NULL violation and inserts the rest"
                <| fun _ ->
                    let store = withUsersTable ()

                    match
                        insertRowsIgnore
                            store
                            defaultDatabase
                            "users"
                            None
                            [ [ VNull; VNull; VInt 30L ] // violates NOT NULL on name
                              [ VNull; VString "bob"; VInt 25L ] ]
                    with
                    | Ok { Affected = affected } ->
                        Expect.equal affected 1 "only the good row counted"

                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.map (fun r -> r.[1])) [ VString "bob" ] "only bob got in"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "insertRowsIgnore skips a unique-key violation and inserts the rest"
                <| fun _ ->
                    let store = create ()

                    createTable
                        store
                        defaultDatabase
                        "emails"
                        [ col "id" (TInt false) false; col "email" (TVarchar 255) false ]
                        [ { Name = "uq_email"; Columns = [ "email" ]; Unique = true; Kind = BTree } ]
                        []
                        None
                        None
                    |> ignore

                    insertRows store defaultDatabase "emails" None [ [ VInt 1L; VString "a@x.com" ] ] |> ignore

                    match
                        insertRowsIgnore
                            store
                            defaultDatabase
                            "emails"
                            None
                            [ [ VInt 2L; VString "a@x.com" ] // dup, skipped
                              [ VInt 3L; VString "b@x.com" ] ]
                    with
                    | Ok { Affected = affected } ->
                        Expect.equal affected 1 "only the non-colliding row counted"

                        match scan store defaultDatabase "emails" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.length) 2 "the original row plus the one good new row"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "insertRowsIgnore skips a row whose foreign key parent is missing"
                <| fun _ ->
                    let store = create ()

                    createTable
                        store
                        defaultDatabase
                        "departments"
                        [ { (col "id" (TInt false) false) with PrimaryKey = true } ]
                        []
                        []
                        None
                        None
                    |> ignore

                    let fk =
                        { Name = "fk_dept"
                          Columns = [ "dept_id" ]
                          RefTable = "departments"
                          RefColumns = [ "id" ]
                          OnDelete = None
                          OnUpdate = None }

                    createTable
                        store
                        defaultDatabase
                        "employees"
                        [ col "id" (TInt false) false; col "dept_id" (TInt false) true ]
                        []
                        [ fk ]
                        None
                        None
                    |> ignore

                    match
                        insertRowsIgnore
                            store
                            defaultDatabase
                            "employees"
                            None
                            [ [ VInt 1L; VInt 999L ] // no such department, skipped
                              [ VInt 2L; VNull ] ]
                    with
                    | Ok { Affected = affected } ->
                        Expect.equal affected 1 "only the row with no dangling FK counted"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "insertRowsIgnore returns lastInsertId 0 when every row is skipped"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRowsIgnore store defaultDatabase "users" (Some [ "id"; "age" ]) [ [ VInt 1L; VInt 30L ] ] with
                    | Ok { LastInsertId = lastId; Affected = affected } ->
                        Expect.equal lastId 0L "nothing was assigned"
                        Expect.equal affected 0 "nothing was inserted"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "insertRowsIgnore still errors on a genuine column-count mismatch"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRowsIgnore store defaultDatabase "users" None [ [ VNull; VString "alice" ] ] with
                    | Error(ColumnCountMismatch(3, 2)) -> ()
                    | other -> failtestf "expected ColumnCountMismatch, got %A" other ]

          testList
              "AUTO_INCREMENT vs. DELETE"
              [ testCase "DELETE does not reset the AUTO_INCREMENT counter (unlike TRUNCATE)"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ]
                    |> ignore

                    deleteRows store defaultDatabase "users" (fun _ -> Ok true) |> ignore

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "bob"; VInt 25L ] ] with
                    | Ok { LastInsertId = lastId } -> Expect.equal lastId 2L "the counter kept climbing across the delete"
                    | Error e -> failtestf "expected Ok, got %A" e ]

          testList
              "resolveColumn"
              [ testCase "resolveColumn finds a column case-insensitively"
                <| fun _ ->
                    match resolveColumn usersColumns "NAME" with
                    | Ok 1 -> ()
                    | other -> failtestf "expected index 1, got %A" other

                testCase "resolveColumn returns UnknownColumn for a missing name"
                <| fun _ ->
                    match resolveColumn usersColumns "missing" with
                    | Error(UnknownColumn "missing") -> ()
                    | other -> failtestf "expected UnknownColumn, got %A" other ]

          testList
              "updateRows / deleteRows"
              [ testCase "updateRows rewrites matching rows and coerces the new values"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VNull; VString "alice"; VInt 30L ]
                          [ VNull; VString "bob"; VInt 25L ] ]
                    |> ignore

                    let predicate (row: Value[]) = Ok(row.[1] = VString "alice")
                    let updater (row: Value[]) = Ok [| row.[0]; row.[1]; VString "31" |]

                    match updateRows store defaultDatabase "users" None predicate updater with
                    | Ok affected ->
                        Expect.equal affected 1 "one row updated"

                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) ->
                            let ages =
                                rows |> Seq.map (fun r -> r.[1], r.[2]) |> List.ofSeq

                            Expect.contains ages (VString "alice", VInt 31L) "alice's age updated and coerced"
                            Expect.contains ages (VString "bob", VInt 25L) "bob untouched"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "updateRows setting a NOT NULL column to NULL returns NotNullViolation"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ]
                    |> ignore

                    let updater (row: Value[]) = Ok [| row.[0]; VNull; row.[2] |]

                    match updateRows store defaultDatabase "users" None (fun _ -> Ok true) updater with
                    | Error(NotNullViolation "name") -> ()
                    | other -> failtestf "expected NotNullViolation, got %A" other

                testCase "a point-update candidate keeps its row identity after an earlier row is deleted"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VNull; VString "alice"; VInt 30L ]
                          [ VNull; VString "bob"; VInt 25L ] ]
                    |> ignore

                    let staleRowId, staleRow =
                        match tryUniqueLookup store defaultDatabase "users" "id" (VInt 2L) with
                        | Some(_, [ candidate ]) -> candidate
                        | other -> failtestf "expected bob's indexed row, got %A" other

                    deleteRows store defaultDatabase "users" (fun row -> Ok(row.[1] = VString "alice")) |> ignore

                    let updater (row: Value[]) = Ok [| row.[0]; row.[1]; VInt 99L |]

                    match updateRows store defaultDatabase "users" (Some [ staleRowId, staleRow ]) (fun _ -> Ok true) updater with
                    | Ok affected ->
                        Expect.equal affected 1 "bob still gets updated"

                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) ->
                            Expect.equal
                                (List.ofSeq rows |> List.map (fun r -> r.[1], r.[2]))
                                [ VString "bob", VInt 99L ]
                                "bob's row was updated in place — no crash, no unrelated row clobbered"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a candidate delete evaluates the current row after the row changes"
                <| fun _ ->
                    let store = create ()
                    let index = { Name = "ix_category"; Columns = [ "category" ]; Unique = false; Kind = BTree }

                    createTable
                        store
                        defaultDatabase
                        "items"
                        [ col "id" (TInt false) false; col "category" (TVarchar 20) false ]
                        [ index ]
                        []
                        None
                        None
                    |> ignore

                    insertRows store defaultDatabase "items" None [ [ VInt 1L; VString "books" ] ] |> ignore

                    let candidates =
                        match trySecondaryLookup store defaultDatabase "items" "category" (VString "books") with
                        | Some(_, candidates) -> candidates
                        | None -> failtest "expected an indexed candidate"

                    updateRows
                        store
                        defaultDatabase
                        "items"
                        None
                        (fun row -> Ok(row.[0] = VInt 1L))
                        (fun row -> Ok [| row.[0]; VString "archived" |])
                    |> ignore

                    match deleteRowsCandidates store defaultDatabase "items" candidates (fun row -> Ok(row.[1] = VString "books")) with
                    | Ok 0 ->
                        match scan store defaultDatabase "items" with
                        | Ok(_, rows) ->
                            Expect.equal (List.ofSeq rows) [ [| VInt 1L; VString "archived" |] ] "the changed row remains"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | other -> failtestf "expected Ok 0, got %A" other

                testCase "deleteRows removes matching rows and returns the count"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VNull; VString "alice"; VInt 30L ]
                          [ VNull; VString "bob"; VInt 25L ] ]
                    |> ignore

                    match deleteRows store defaultDatabase "users" (fun row -> Ok(row.[1] = VString "alice")) with
                    | Ok affected ->
                        Expect.equal affected 1 "one row deleted"

                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) ->
                            Expect.equal
                                (rows |> Seq.map (fun r -> r.[1]) |> List.ofSeq)
                                [ VString "bob" ]
                                "only bob remains"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "deleteRows removes only the rows the predicate matched, not every byte-identical row (DELETE ... LIMIT semantics)"
                <| fun _ ->
                    let store = create ()
                    createTable store defaultDatabase "dup" [ col "v" (TInt false) true ] [] [] None None |> ignore

                    insertRows store defaultDatabase "dup" None [ [ VInt 1L ]; [ VInt 1L ]; [ VInt 2L ] ]
                    |> ignore

                    // Mimics `DELETE ... LIMIT 1`: the predicate only accepts
                    // the first row it sees, even though two rows are
                    // structurally identical.
                    let remaining = ref 1

                    let predicate (row: Value[]) =
                        Ok(
                            row.[0] = VInt 1L
                            && remaining.Value > 0
                            && (remaining.Value <- remaining.Value - 1
                                true)
                        )

                    match deleteRows store defaultDatabase "dup" predicate with
                    | Ok affected ->
                        Expect.equal affected 1 "only one row reported deleted"

                        match scan store defaultDatabase "dup" with
                        | Ok(_, rows) ->
                            Expect.equal
                                (rows |> Seq.map (fun r -> r.[0]) |> List.ofSeq |> List.sortBy (function VInt i -> i | _ -> 0L))
                                [ VInt 1L; VInt 2L ]
                                "one of the two duplicate 1-rows survives"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "UPDATE against an empty table succeeds with zero rows changed"
                <| fun _ ->
                    let store = withUsersTable ()

                    match updateRows store defaultDatabase "users" None (fun _ -> Ok true) Ok with
                    | Ok 0 -> ()
                    | other -> failtestf "expected Ok 0, got %A" other

                testCase "a no-op UPDATE that writes a row's PRIMARY KEY back to its own value never collides with itself"
                <| fun _ ->
                    // Collision checks exclude the row by stable identity;
                    // structural equality cannot distinguish every rewrite.
                    let store = withUsersTable ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VInt 1L; VString "alice"; VInt 30L ]
                          [ VInt 2L; VString "bob"; VInt 25L ] ]
                    |> ignore

                    let predicate (row: Value[]) = Ok(row.[0] = VInt 1L)
                    let updater (row: Value[]) = Ok [| row.[0]; row.[1]; VInt 31L |]

                    match updateRows store defaultDatabase "users" None predicate updater with
                    | Ok affected -> Expect.equal affected 1 "alice's age changed, id rewritten to itself is not a collision"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "insertion order survives an INSERT/UPDATE/DELETE/INSERT interleaving"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VNull; VString "alice"; VInt 30L ]
                          [ VNull; VString "bob"; VInt 25L ]
                          [ VNull; VString "carol"; VInt 40L ] ]
                    |> ignore

                    updateRows store defaultDatabase "users" None (fun row -> Ok(row.[1] = VString "bob")) (fun row -> Ok [| row.[0]; row.[1]; VInt 26L |])
                    |> ignore

                    deleteRows store defaultDatabase "users" (fun row -> Ok(row.[1] = VString "alice"))
                    |> ignore

                    insertRows store defaultDatabase "users" None [ [ VNull; VString "dave"; VInt 22L ] ]
                    |> ignore

                    match scan store defaultDatabase "users" with
                    | Ok(_, rows) ->
                        Expect.equal
                            (rows |> Seq.map (fun r -> r.[1]) |> List.ofSeq)
                            [ VString "bob"; VString "carol"; VString "dave" ]
                            "scan order is bob, carol, dave — insertion order minus the deleted row, dave appended last"
                    | Error e -> failtestf "expected Ok, got %A" e ]

          testList
              "snapshot isolation"
              [ testCase "a seq obtained before a write still yields the old rows"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ]
                    |> ignore

                    let before =
                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) -> rows
                        | Error e -> failtestf "expected Ok, got %A" e

                    insertRows store defaultDatabase "users" None [ [ VNull; VString "bob"; VInt 25L ] ]
                    |> ignore

                    Expect.equal (List.ofSeq before |> List.length) 1 "the old snapshot still has one row"

                    match scan store defaultDatabase "users" with
                    | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.length) 2 "a fresh scan sees both rows"
                    | Error e -> failtestf "expected Ok, got %A" e ]

          testList
              "alterTable"
              [ testCase "AddColumn appends the column and fills existing rows with its default"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] |> ignore

                    let newCol = { (col "active" (TInt false) true) with Default = Some(DConst(VInt 1L)) }

                    match alterTable store defaultDatabase "users" [ AddColumn(newCol, PositionDefault) ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(columns, rows) ->
                            Expect.equal (List.length columns) 4 "one more column"
                            Expect.equal (List.ofSeq rows |> List.map (fun r -> r.[3])) [ VInt 1L ] "filled with the new column's default"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AddColumn NOT NULL with no DEFAULT on a non-empty table fills the type's implicit zero value"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] |> ignore

                    match alterTable store defaultDatabase "users" [ AddColumn(col "score" (TInt false) false, PositionDefault) ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.map (fun r -> r.[3])) [ VInt 0L ] "implicit zero, not NULL"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AddColumn NOT NULL with no DEFAULT on an empty table succeeds without needing a fill value"
                <| fun _ ->
                    let store = withUsersTable ()

                    match alterTable store defaultDatabase "users" [ AddColumn(col "score" (TInt false) false, PositionDefault) ] with
                    | Ok() -> ()
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AddColumn NOT NULL DATE with no DEFAULT on a non-empty table fails with 1292 (implicit zero date under NO_ZERO_DATE)"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] |> ignore

                    match alterTable store defaultDatabase "users" [ AddColumn(col "born" TDate false, PositionDefault) ] with
                    | Error(ZeroTemporalForColumn("date", "0000-00-00", "born")) ->
                        let code, _ = toMySqlError (ZeroTemporalForColumn("date", "0000-00-00", "born"))
                        Expect.equal code 1292 "MySQL error code"
                    | other -> failtestf "expected ZeroTemporalForColumn, got %A" other

                testCase "AddColumn NOT NULL DATE with no DEFAULT preserves an implicit zero date when zero modes are absent"
                <| fun _ ->
                    let store = withUsersTable ()
                    setZeroDateModes store false false
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] |> ignore

                    match alterTable store defaultDatabase "users" [ AddColumn(col "born" TDate false, PositionDefault) ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) ->
                            Expect.equal
                                (List.ofSeq rows |> List.map (fun row -> row.[3]))
                                [ VZeroDate(tryZeroDate 0 0 0 |> Option.get) ]
                                "implicit zero date"
                        | Error error -> failtestf "expected Ok, got %A" error
                    | Error error -> failtestf "expected Ok, got %A" error

                testCase "AddColumn NOT NULL with no DEFAULT fills empty string for a text column and the first member for an ENUM"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] |> ignore

                    match
                        alterTable
                            store
                            defaultDatabase
                            "users"
                            [ AddColumn(col "bio" (TVarchar 100) false, PositionDefault)
                              AddColumn(col "role" (TEnum [ "admin"; "member" ]) false, PositionDefault) ]
                    with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) ->
                            match List.ofSeq rows with
                            | [ row ] ->
                                Expect.equal row.[3] (VString "") "text column implicit default is ''"
                                Expect.equal row.[4] (VString "admin") "ENUM implicit default is its first declared member"
                            | other -> failtestf "expected one row, got %A" other
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "DropColumn removes the column from schema and every row"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] |> ignore

                    match alterTable store defaultDatabase "users" [ DropColumn "age" ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(columns, rows) ->
                            Expect.equal (columns |> List.map (fun c -> c.Name)) [ "id"; "name" ] "age column gone"
                            Expect.equal (List.ofSeq rows |> List.map Array.length) [ 2 ] "row shrunk too"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "DropColumn on an unknown column returns UnknownColumn"
                <| fun _ ->
                    let store = withUsersTable ()

                    match alterTable store defaultDatabase "users" [ DropColumn "ghost" ] with
                    | Error(UnknownColumn "ghost") -> ()
                    | other -> failtestf "expected UnknownColumn, got %A" other

                testCase "ModifyColumn replaces the column's definition in place"
                <| fun _ ->
                    let store = withUsersTable ()
                    let widened = col "name" (TVarchar 500) false

                    match alterTable store defaultDatabase "users" [ ModifyColumn(widened, PositionDefault) ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(columns, _) ->
                            match columns |> List.find (fun c -> c.Name = "name") with
                            | { Type = TVarchar 500 } -> ()
                            | other -> failtestf "expected the widened type, got %A" other
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "ChangeColumn renames and redefines a column"
                <| fun _ ->
                    let store = withUsersTable ()
                    let renamed = col "full_name" (TVarchar 255) false

                    match alterTable store defaultDatabase "users" [ ChangeColumn("name", renamed, PositionDefault) ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(columns, _) -> Expect.equal (columns |> List.map (fun c -> c.Name)) [ "id"; "full_name"; "age" ] "renamed"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AddColumn ... FIRST inserts at the front, in schema and every row"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] |> ignore

                    match alterTable store defaultDatabase "users" [ AddColumn(col "flag" (TInt false) true, PositionFirst) ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(columns, rows) ->
                            Expect.equal (columns |> List.map (fun c -> c.Name)) [ "flag"; "id"; "name"; "age" ] "flag is now first"
                            Expect.equal (List.ofSeq rows |> List.map (fun r -> r.[0])) [ VNull ] "row's first value is the new column's"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AddColumn ... AFTER col inserts right after that column, in schema and every row"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] |> ignore

                    match alterTable store defaultDatabase "users" [ AddColumn(col "flag" (TInt false) true, PositionAfter "id") ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(columns, rows) ->
                            Expect.equal (columns |> List.map (fun c -> c.Name)) [ "id"; "flag"; "name"; "age" ] "flag right after id"
                            Expect.equal (List.ofSeq rows |> List.map (fun r -> r.[1])) [ VNull ] "row's second value is the new column's"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "ModifyColumn with no position leaves the column exactly where it was"
                <| fun _ ->
                    let store = withUsersTable ()

                    match alterTable store defaultDatabase "users" [ ModifyColumn(col "name" (TVarchar 500) false, PositionDefault) ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(columns, _) -> Expect.equal (columns |> List.map (fun c -> c.Name)) [ "id"; "name"; "age" ] "order unchanged"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "ModifyColumn ... FIRST moves the column and its row values to the front"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] |> ignore

                    match alterTable store defaultDatabase "users" [ ModifyColumn(col "age" (TInt false) true, PositionFirst) ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(columns, rows) ->
                            Expect.equal (columns |> List.map (fun c -> c.Name)) [ "age"; "id"; "name" ] "age moved to the front"
                            Expect.equal (List.ofSeq rows |> List.map (fun r -> r.[0])) [ VInt 30L ] "age's own value moved with it"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "ChangeColumn ... AFTER col renames, redefines, and repositions in one action"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] |> ignore

                    match alterTable store defaultDatabase "users" [ ChangeColumn("age", col "years" (TInt false) true, PositionFirst) ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(columns, rows) ->
                            Expect.equal (columns |> List.map (fun c -> c.Name)) [ "years"; "id"; "name" ] "renamed and moved to the front"
                            Expect.equal (List.ofSeq rows |> List.map (fun r -> r.[0])) [ VInt 30L ] "row value moved with it"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "RenameTo re-files the table under its new name"
                <| fun _ ->
                    let store = withUsersTable ()

                    match alterTable store defaultDatabase "users" [ RenameTo "people" ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Error(NoSuchTable _) -> ()
                        | other -> failtestf "expected the old name to be gone, got %A" other

                        match scan store defaultDatabase "people" with
                        | Ok _ -> ()
                        | Error e -> failtestf "expected the new name to exist, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "renameTable is the same rename as an ALTER TABLE ... RENAME TO"
                <| fun _ ->
                    let store = withUsersTable ()

                    match renameTable store defaultDatabase "users" "people" with
                    | Ok() ->
                        match scan store defaultDatabase "people" with
                        | Ok _ -> ()
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "RenameColumnTo renames just the column"
                <| fun _ ->
                    let store = withUsersTable ()

                    match alterTable store defaultDatabase "users" [ RenameColumnTo("age", "years_old") ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok(columns, _) -> Expect.equal (columns |> List.map (fun c -> c.Name)) [ "id"; "name"; "years_old" ] "renamed"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AddIndex / DropIndexAction manage the table's index metadata"
                <| fun _ ->
                    let store = withUsersTable ()
                    let ix = { Name = "idx_name"; Columns = [ "name" ]; Unique = false; Kind = BTree }

                    match alterTable store defaultDatabase "users" [ AddIndex ix ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok _ -> ()
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                    match alterTable store defaultDatabase "users" [ DropIndexAction "idx_name" ] with
                    | Ok() -> ()
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a secondary ordered index follows inserted, updated, deleted, and replaced row identities"
                <| fun _ ->
                    let store = withUsersTable ()
                    let index = { Name = "idx_age"; Columns = [ "age" ]; Unique = false; Kind = BTree }

                    match alterTable store defaultDatabase "users" [ AddIndex index ] with
                    | Ok() -> ()
                    | Error e -> failtestf "expected Ok, got %A" e

                    let reindexesBefore = reindexCallCount ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VInt 1L; VString "alice"; VInt 30L ]
                          [ VInt 2L; VString "bob"; VInt 25L ]
                          [ VInt 3L; VString "carol"; VInt 30L ] ]
                    |> function
                        | Ok _ -> ()
                        | Error e -> failtestf "expected Ok, got %A" e

                    updateRows
                        store
                        defaultDatabase
                        "users"
                        None
                        (fun row -> Ok(row.[0] = VInt 2L))
                        (fun row -> Ok [| row.[0]; row.[1]; VInt 26L |])
                    |> function
                        | Ok _ -> ()
                        | Error e -> failtestf "expected Ok, got %A" e

                    deleteRows store defaultDatabase "users" (fun row -> Ok(row.[0] = VInt 1L))
                    |> function
                        | Ok _ -> ()
                        | Error e -> failtestf "expected Ok, got %A" e

                    replaceRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VInt 3L; VString "carol"; VInt 28L ] ]
                        prepareRow
                    |> function
                        | Ok _ -> ()
                        | Error e -> failtestf "expected Ok, got %A" e

                    let entries =
                        match trySecondaryRangeLookup store defaultDatabase "users" "age" (Some(VInt 0L, true)) None with
                        | Some(_, _, _, rows) -> rows |> List.map (fun (_, row) -> row.[2], row.[0])
                        | None -> failtest "expected an ordered secondary range lookup"

                    Expect.equal entries [ VInt 26L, VInt 2L; VInt 28L, VInt 3L ] "ordered entries reflect the live rows"

                    let descending =
                        match trySecondaryOrderedLookup store defaultDatabase "users" "age" None None Desc with
                        | Some(_, _, _, _, rows) -> rows |> Seq.map (fun row -> row.[2], row.[0]) |> List.ofSeq
                        | None -> failtest "expected an ordered secondary lookup"

                    Expect.equal descending [ VInt 28L, VInt 3L; VInt 26L, VInt 2L ] "descending entries retain index-key order"

                    let primaryRange =
                        match trySecondaryRangeLookup store defaultDatabase "users" "id" (Some(VInt 2L, true)) None with
                        | Some(name, _, _, rows) -> name, rows |> List.map (fun (_, row) -> row.[0])
                        | None -> failtest "expected an ordered primary-key range lookup"

                    Expect.equal primaryRange ("PRIMARY", [ VInt 2L; VInt 3L ]) "primary keys share the ordered access path"
                    Expect.equal (reindexCallCount ()) reindexesBefore "point writes preserve ordered buckets incrementally"

                testCase "a composite secondary index follows row mutations incrementally"
                <| fun _ ->
                    let store = withUsersTable ()
                    let index = { Name = "idx_name_age"; Columns = [ "name"; "age" ]; Unique = false; Kind = BTree }
                    alterTable store defaultDatabase "users" [ AddIndex index ] |> Result.defaultWith (failtestf "add index failed: %A")
                    let reindexesBefore = reindexCallCount ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VInt 1L; VString "alice"; VInt 30L ]
                          [ VInt 2L; VString "bob"; VInt 25L ] ]
                    |> Result.defaultWith (failtestf "insert failed: %A")
                    |> ignore

                    let ids name age =
                        match tryCompositeEqualityLookup store defaultDatabase "users" [ "name", VString name; "age", VInt age ] with
                        | Some lookup -> lookup.LookupRows |> List.map (fun (_, row) -> row.[0])
                        | None -> failtest "expected a composite lookup"

                    Expect.equal (ids "alice" 30L) [ VInt 1L ] "inserted rows enter the composite bucket"

                    updateRows
                        store
                        defaultDatabase
                        "users"
                        None
                        (fun row -> Ok(row.[0] = VInt 1L))
                        (fun row -> Ok [| row.[0]; row.[1]; VInt 31L |])
                    |> Result.defaultWith (failtestf "update failed: %A")
                    |> ignore

                    Expect.equal (ids "alice" 30L) [] "updated rows leave the old composite bucket"
                    Expect.equal (ids "alice" 31L) [ VInt 1L ] "updated rows enter the new composite bucket"

                    let ordered =
                        match tryCompositeOrderedLookup store defaultDatabase "users" [ "name"; "age" ] Asc with
                        | Some lookup -> lookup.OrderedRows |> Seq.map (fun row -> row.[1], row.[2]) |> List.ofSeq
                        | None -> failtest "expected composite ordered rows"

                    Expect.equal ordered [ VString "alice", VInt 31L; VString "bob", VInt 25L ] "composite ordering reflects the update"

                    deleteRows store defaultDatabase "users" (fun row -> Ok(row.[0] = VInt 1L))
                    |> Result.defaultWith (failtestf "delete failed: %A")
                    |> ignore

                    Expect.equal (ids "alice" 31L) [] "deleted rows leave the composite bucket"
                    Expect.equal (reindexCallCount ()) reindexesBefore "row mutations avoid a full index rebuild"

                testCase "AddIndex with Unique = true rejects existing duplicates instead of silently dropping rows from the index"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VNull; VString "dup"; VInt 1L ]; [ VNull; VString "dup"; VInt 2L ]; [ VNull; VString "unique"; VInt 3L ] ]
                    |> ignore

                    let ix = { Name = "uq_name"; Columns = [ "name" ]; Unique = true; Kind = BTree }

                    match alterTable store defaultDatabase "users" [ AddIndex ix ] with
                    | Error(DuplicateKey("uq_name", _)) -> ()
                    | other -> failtestf "expected DuplicateKey for the two 'dup' rows, got %A" other

                    // Rejected DDL must leave the table exactly as it was —
                    // no half-applied index, all three original rows intact.
                    match scan store defaultDatabase "users" with
                    | Ok(_, rows) -> Expect.equal (List.length (List.ofSeq rows)) 3 "all rows survive a rejected ADD UNIQUE"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AddForeignKey / DropForeignKey manage the table's FK metadata"
                <| fun _ ->
                    let store = withUsersTable ()
                    createTable store defaultDatabase "other" [ { (col "id" (TInt false) false) with PrimaryKey = true } ] [] [] None None |> ignore

                    let fk =
                        { Name = "fk_x"; Columns = [ "id" ]; RefTable = "other"; RefColumns = [ "id" ]; OnDelete = None; OnUpdate = None }

                    match alterTable store defaultDatabase "users" [ AddForeignKey fk ] with
                    | Ok() -> ()
                    | Error e -> failtestf "expected Ok, got %A" e

                    match alterTable store defaultDatabase "users" [ DropForeignKey "fk_x" ] with
                    | Ok() -> ()
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AddPrimaryKey marks the named columns as primary key"
                <| fun _ ->
                    let store = create ()
                    createTable store defaultDatabase "t" [ col "a" (TInt false) true; col "b" (TInt false) true ] [] [] None None |> ignore

                    match alterTable store defaultDatabase "t" [ AddPrimaryKey [ "a"; "b" ] ] with
                    | Ok() ->
                        match scan store defaultDatabase "t" with
                        | Ok(columns, _) ->
                            Expect.isTrue (columns |> List.forall (fun c -> c.PrimaryKey)) "both columns are PK"
                            Expect.isTrue (columns |> List.forall (fun c -> not c.Nullable)) "primary-key columns become non-nullable"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AddPrimaryKey rejects NULL and duplicate existing values"
                <| fun _ ->
                    let withValues name values =
                        let store = create ()
                        Expect.isOk (createTable store defaultDatabase name [ col "id" (TInt false) true ] [] [] None None) "table created"
                        Expect.isOk (insertRows store defaultDatabase name None (values |> List.map List.singleton)) "rows inserted"
                        store

                    let withNull = withValues "nullable_key" [ VNull; VInt 1L ]

                    match alterTable withNull defaultDatabase "nullable_key" [ AddPrimaryKey [ "id" ] ] with
                    | Error(ExpressionError(1138, "Invalid use of NULL value")) -> ()
                    | other -> failtestf "expected 1138 for a NULL primary-key candidate, got %A" other

                    let withDuplicate = withValues "duplicate_key" [ VInt 1L; VInt 1L ]

                    match alterTable withDuplicate defaultDatabase "duplicate_key" [ AddPrimaryKey [ "id" ] ] with
                    | Error(DuplicateKey("PRIMARY", "1")) -> ()
                    | other -> failtestf "expected a duplicate PRIMARY rejection, got %A" other

                testCase "multiple actions in one call apply in order"
                <| fun _ ->
                    let store = withUsersTable ()

                    match alterTable store defaultDatabase "users" [ DropColumn "age"; RenameTo "people" ] with
                    | Ok() ->
                        match scan store defaultDatabase "people" with
                        | Ok(columns, _) -> Expect.equal (columns |> List.map (fun c -> c.Name)) [ "id"; "name" ] "both actions applied"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "the PRIMARY KEY's index is rebuilt at its new column position after an ALTER shifts it, not stale at the old one"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore

                    // Moving a key column must rebuild its index against the
                    // new row layout before later uniqueness checks.
                    match alterTable store defaultDatabase "users" [ AddColumn(col "flag" (TInt false) true, PositionFirst) ] with
                    | Error e -> failtestf "expected Ok, got %A" e
                    | Ok() ->
                        match insertRows store defaultDatabase "users" None [ [ VNull; VInt 1L; VString "bob"; VInt 40L ] ] with
                        | Error(DuplicateKey("PRIMARY", _)) -> ()
                        | other -> failtestf "expected id 1 to still be rejected as a duplicate after the shift, got %A" other

                        match insertRows store defaultDatabase "users" None [ [ VNull; VInt 2L; VString "carol"; VInt 50L ] ] with
                        | Ok { LastInsertId = 2L; Affected = 1 } -> ()
                        | other -> failtestf "expected a distinct id to still succeed after the shift, got %A" other ]

          testList
              "upsertRows"
              [ testCase "no collision: behaves like a plain insert"
                <| fun _ ->
                    let store = withUsersTable ()
                    let applyUpdate (_: Value[]) (candidate: Value[]) = Ok candidate

                    match upsertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] prepareRow applyUpdate false with
                    | Ok { LastInsertId = lastId; Affected = affected } ->
                        Expect.equal lastId 1L "inserted with a fresh id"
                        Expect.equal affected 1 "one row"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a primary-key collision calls applyUpdate instead of inserting a second row"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore

                    let applyUpdate (existing: Value[]) (_candidate: Value[]) =
                        Ok [| existing.[0]; existing.[1]; VInt 31L |]

                    match upsertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 999L ] ] prepareRow applyUpdate false with
                    | Ok { Affected = affected } ->
                        // MySQL counts a changed ON DUPLICATE KEY UPDATE match as 2
                        // (the attempted insert plus the update), not 1.
                        Expect.equal affected 2 "two: the attempted insert plus the update that changed the row"

                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.map (fun r -> r.[2])) [ VInt 31L ] "existing row updated, not duplicated"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a unique-index collision (not the primary key) also triggers applyUpdate"
                <| fun _ ->
                    let store = create ()

                    createTable
                        store
                        defaultDatabase
                        "emails"
                        [ col "id" (TInt false) false; col "email" (TVarchar 255) false ]
                        [ { Name = "uq_email"; Columns = [ "email" ]; Unique = true; Kind = BTree } ]
                        []
                        None
                        None
                    |> ignore

                    insertRows store defaultDatabase "emails" None [ [ VInt 1L; VString "a@x.com" ] ] |> ignore

                    let applyUpdate (existing: Value[]) (_candidate: Value[]) = Ok existing

                    // `applyUpdate` here is a genuine no-op (every column
                    // stays exactly what it already held) — MySQL's own
                    // `affected_rows` rule for a matched `ON DUPLICATE KEY
                    // UPDATE` row: 0 without `CLIENT_FOUND_ROWS`, 1 with it.
                    match upsertRows store defaultDatabase "emails" None [ [ VInt 2L; VString "a@x.com" ] ] prepareRow applyUpdate false with
                    | Ok { Affected = affected } ->
                        Expect.equal affected 0 "matched via the unique index, but no-op: not counted without CLIENT_FOUND_ROWS"

                        match scan store defaultDatabase "emails" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.length) 1 "no duplicate row inserted"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                    match upsertRows store defaultDatabase "emails" None [ [ VInt 3L; VString "a@x.com" ] ] prepareRow applyUpdate true with
                    | Ok { Affected = affected } -> Expect.equal affected 1 "the same no-op match counts once CLIENT_FOUND_ROWS is negotiated"
                    | Error e -> failtestf "expected Ok, got %A" e ]

          testList
              "foreign keys"
              [ let idCol =
                    { (col "id" (TInt false) false) with
                        PrimaryKey = true }

                /// A `departments`/`employees` pair: `employees.dept_id`
                /// references `departments(id)` under the given `onDelete`
                /// action (`None` = no `ON DELETE` clause, MySQL's default —
                /// behaves like `RESTRICT`).
                let withDeptEmployees (onDelete: string option) =
                    let store = create ()

                    createTable store defaultDatabase "departments" [ idCol; col "name" (TVarchar 255) false ] [] [] None None
                    |> ignore

                    let fk =
                        { Name = "fk_dept"
                          Columns = [ "dept_id" ]
                          RefTable = "departments"
                          RefColumns = [ "id" ]
                          OnDelete = onDelete
                          OnUpdate = None }

                    createTable
                        store
                        defaultDatabase
                        "employees"
                        [ idCol; col "dept_id" (TInt false) true; col "name" (TVarchar 255) false ]
                        []
                        [ fk ]
                        None
                        None
                    |> ignore

                    insertRows store defaultDatabase "departments" None [ [ VInt 1L; VString "eng" ] ]
                    |> ignore

                    store

                testCase "CREATE rejects SET NULL on a NOT NULL child column"
                <| fun _ ->
                    let store = create ()
                    createTable store defaultDatabase "parents" [ idCol ] [] [] None None |> ignore

                    let foreignKey =
                        { Name = "fk_parent"
                          Columns = [ "parent_id" ]
                          RefTable = "parents"
                          RefColumns = [ "id" ]
                          OnDelete = Some "SET NULL"
                          OnUpdate = None }

                    match
                        createTable
                            store
                            defaultDatabase
                            "children"
                            [ idCol; col "parent_id" (TInt false) false ]
                            []
                            [ foreignKey ]
                            None
                            None
                    with
                    | Error error ->
                        Expect.equal
                            (toMySqlError error)
                            (1830, "Column 'parent_id' cannot be NOT NULL: needed in a foreign key constraint 'fk_parent' SET NULL")
                            "DDL error"
                    | Ok() -> failtest "expected SET NULL over NOT NULL to fail"

                testCase "CREATE rejects a foreign key that references a non-unique key"
                <| fun _ ->
                    let store = create ()
                    createTable store defaultDatabase "parents" [ col "id" (TInt false) false ] [] [] None None |> ignore

                    let foreignKey =
                        { Name = "fk_parent"
                          Columns = [ "parent_id" ]
                          RefTable = "parents"
                          RefColumns = [ "id" ]
                          OnDelete = None
                          OnUpdate = None }

                    match
                        createTable
                            store
                            defaultDatabase
                            "children"
                            [ idCol; col "parent_id" (TInt false) true ]
                            []
                            [ foreignKey ]
                            None
                            None
                    with
                    | Error error ->
                        Expect.equal
                            (toMySqlError error)
                            (6125,
                             "Failed to add the foreign key constraint. Missing unique key for constraint 'fk_parent' in the referenced table 'parents'")
                            "DDL error"
                    | Ok() -> failtest "expected a non-unique referenced key to fail"

                testCase "CREATE validates foreign key column lists when foreign key checks are disabled"
                <| fun _ ->
                    let store = create ()

                    createTable
                        store
                        defaultDatabase
                        "parents"
                        [ idCol; col "other" (TInt false) false ]
                        [ { Name = "uq_pair"
                            Columns = [ "id"; "other" ]
                            Unique = true
                            Kind = BTree } ]
                        []
                        None
                        None
                    |> ignore

                    setForeignKeyChecks store false

                    let cases =
                        [ ("missing_child",
                           [ idCol ],
                           { Name = "fk_child_missing"
                             Columns = [ "missing" ]
                             RefTable = "parents"
                             RefColumns = [ "id" ]
                             OnDelete = None
                             OnUpdate = None },
                           (1072, "Key column 'missing' doesn't exist in table"))
                          ("missing_parent",
                           [ idCol ],
                           { Name = "fk_parent_missing"
                             Columns = [ "id" ]
                             RefTable = "parents"
                             RefColumns = [ "missing" ]
                             OnDelete = None
                             OnUpdate = None },
                           (3734,
                            "Failed to add the foreign key constraint. Missing column 'missing' for constraint 'fk_parent_missing' in the referenced table 'parents'"))
                          ("arity_mismatch",
                           [ idCol ],
                           { Name = "fk_arity"
                             Columns = [ "id" ]
                             RefTable = "parents"
                             RefColumns = [ "id"; "other" ]
                             OnDelete = None
                             OnUpdate = None },
                           (1239,
                            "Incorrect foreign key definition for 'fk_arity': Key reference and table reference don't match")) ]

                    for tableName, columns, foreignKey, expected in cases do
                        match createTable store defaultDatabase tableName columns [] [ foreignKey ] None None with
                        | Error error -> Expect.equal (toMySqlError error) expected tableName
                        | Ok() -> failtestf "expected %s to fail" tableName

                testCase "INSERT of a child row with no matching parent returns error 1452"
                <| fun _ ->
                    let store = withDeptEmployees None

                    match insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 999L; VString "alice" ] ] with
                    | Error(ForeignKeyParentMissing "fk_dept") ->
                        let code, _ = toMySqlError (ForeignKeyParentMissing "fk_dept")
                        Expect.equal code 1452 "MySQL error code"
                    | other -> failtestf "expected ForeignKeyParentMissing, got %A" other

                testCase "INSERT of a child row with a NULL foreign key column is allowed"
                <| fun _ ->
                    let store = withDeptEmployees None

                    match insertRows store defaultDatabase "employees" None [ [ VInt 1L; VNull; VString "alice" ] ] with
                    | Ok _ -> ()
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "INSERT of a child row with a matching parent succeeds"
                <| fun _ ->
                    let store = withDeptEmployees None

                    match insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ] with
                    | Ok _ -> ()
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "UPDATE of a child row's foreign key to a non-existent parent returns error 1452"
                <| fun _ ->
                    let store = withDeptEmployees None
                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    let updater (row: Value[]) = Ok [| row.[0]; VInt 999L; row.[2] |]

                    match updateRows store defaultDatabase "employees" None (fun _ -> Ok true) updater with
                    | Error(ForeignKeyParentMissing "fk_dept") -> ()
                    | other -> failtestf "expected ForeignKeyParentMissing, got %A" other

                testCase "upsertRows' insert branch (no collision) checks foreign keys"
                <| fun _ ->
                    let store = withDeptEmployees None
                    let applyUpdate (_: Value[]) (candidate: Value[]) = Ok candidate

                    match upsertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 999L; VString "alice" ] ] prepareRow applyUpdate false with
                    | Error(ForeignKeyParentMissing "fk_dept") -> ()
                    | other -> failtestf "expected ForeignKeyParentMissing, got %A" other

                testCase "upsertRows' update branch (ON DUPLICATE KEY UPDATE) checks foreign keys"
                <| fun _ ->
                    let store = withDeptEmployees None
                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    // Collides on the primary key (id = 1), so this goes
                    // through `applyUpdate` rather than the insert path —
                    // rewriting `dept_id` to a department that doesn't
                    // exist must still be rejected the same way a plain
                    // `UPDATE` already is.
                    let applyUpdate (existing: Value[]) (_candidate: Value[]) = Ok [| existing.[0]; VInt 999L; existing.[2] |]

                    match upsertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ] prepareRow applyUpdate false with
                    | Error(ForeignKeyParentMissing "fk_dept") -> ()
                    | other -> failtestf "expected ForeignKeyParentMissing, got %A" other

                testCase "DELETE of a parent row with children and no ON DELETE clause returns error 1451 (RESTRICT default)"
                <| fun _ ->
                    let store = withDeptEmployees None
                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    match deleteRows store defaultDatabase "departments" (fun _ -> Ok true) with
                    | Error(ForeignKeyRestrict "fk_dept") ->
                        let code, _ = toMySqlError (ForeignKeyRestrict "fk_dept")
                        Expect.equal code 1451 "MySQL error code"
                    | other -> failtestf "expected ForeignKeyRestrict, got %A" other

                    match scan store defaultDatabase "departments" with
                    | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.length) 1 "the parent row survives the failed delete"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "DELETE of a parent row with ON DELETE CASCADE deletes its children too"
                <| fun _ ->
                    let store = withDeptEmployees (Some "CASCADE")
                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    match deleteRows store defaultDatabase "departments" (fun _ -> Ok true) with
                    | Ok affected ->
                        Expect.equal affected 1 "one department deleted"

                        match scan store defaultDatabase "employees" with
                        | Ok(_, rows) -> Expect.isEmpty (List.ofSeq rows) "the cascaded child row is gone too"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "DELETE of a parent row with ON DELETE SET NULL blanks the children's foreign key"
                <| fun _ ->
                    let store = withDeptEmployees (Some "SET NULL")
                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    match deleteRows store defaultDatabase "departments" (fun _ -> Ok true) with
                    | Ok _ ->
                        match scan store defaultDatabase "employees" with
                        | Ok(_, rows) ->
                            match List.ofSeq rows with
                            | [ row ] -> Expect.equal row.[1] VNull "dept_id blanked, row otherwise survives"
                            | other -> failtestf "expected the child row to survive, got %A" other
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "ON DELETE SET NULL blanking fires a RowsUpdated event too, not just the parent's RowsDeleted"
                <| fun _ ->
                    let store = withDeptEmployees (Some "SET NULL")
                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    match deleteRows store defaultDatabase "departments" (fun _ -> Ok true) with
                    | Ok _ ->
                        match List.ofSeq events with
                        | [ RowsDeleted(_, "departments", _); RowsUpdated(_, "employees", [ (oldRow, newRow) ]) ] ->
                            Expect.equal oldRow.[1] (VInt 1L) "before value still holds the pre-blank dept_id"
                            Expect.equal newRow.[1] VNull "after value is the blanked dept_id"
                        | other -> failtestf "expected a RowsDeleted for departments and a RowsUpdated for employees, got %A" other
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "ON DELETE SET NULL blanking replays correctly (WAL round-trip via the emitted events)"
                <| fun _ ->
                    // Guards the same gap as the event-shape test above, but
                    // from the replay side: apply the emitted events to a
                    // second, fresh store the way `Persistence.applyEvent`
                    // would, and confirm it lands on the blanked value
                    // instead of resurrecting the pre-delete FK.
                    let store = withDeptEmployees (Some "SET NULL")
                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add
                    deleteRows store defaultDatabase "departments" (fun _ -> Ok true) |> ignore

                    let updated =
                        events
                        |> Seq.tryPick (function
                            | RowsUpdated(_, "employees", changes) -> Some changes
                            | _ -> None)

                    match updated with
                    | Some [ (_, newRow) ] -> Expect.equal newRow.[1] VNull "the row the WAL would replay already has the FK blanked"
                    | other -> failtestf "expected exactly one RowsUpdated change for employees, got %A" other

                testCase "ON DELETE CASCADE recurses through a grandchild table"
                <| fun _ ->
                    let store = withDeptEmployees (Some "CASCADE")
                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    let projFk =
                        { Name = "fk_owner"
                          Columns = [ "owner_id" ]
                          RefTable = "employees"
                          RefColumns = [ "id" ]
                          OnDelete = Some "CASCADE"
                          OnUpdate = None }

                    createTable
                        store
                        defaultDatabase
                        "projects"
                        [ idCol; col "owner_id" (TInt false) true; col "title" (TVarchar 255) false ]
                        []
                        [ projFk ]
                        None
                        None
                    |> ignore

                    insertRows store defaultDatabase "projects" None [ [ VInt 1L; VInt 1L; VString "roadmap" ] ]
                    |> ignore

                    match deleteRows store defaultDatabase "departments" (fun _ -> Ok true) with
                    | Ok _ ->
                        match scan store defaultDatabase "projects" with
                        | Ok(_, rows) -> Expect.isEmpty (List.ofSeq rows) "the grandchild row cascaded away too"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "ON DELETE CASCADE on a mutually-referencing cycle terminates instead of stack-overflowing"
                <| fun _ ->
                    let store = create ()

                    let selfFk =
                        { Name = "fk_node"
                          Columns = [ "parent_id" ]
                          RefTable = "node"
                          RefColumns = [ "id" ]
                          OnDelete = Some "CASCADE"
                          OnUpdate = None }

                    createTable store defaultDatabase "node" [ idCol; col "parent_id" (TInt false) true ] [] [ selfFk ] None None
                    |> ignore

                    // Two rows that reference each other — needs the checks
                    // disabled to insert at all, same as a real client's
                    // `SET FOREIGN_KEY_CHECKS=0` around a cyclic seed.
                    setForeignKeyChecks store false

                    insertRows store defaultDatabase "node" None [ [ VInt 6L; VInt 7L ]; [ VInt 7L; VInt 6L ] ]
                    |> ignore

                    setForeignKeyChecks store true

                    match deleteRows store defaultDatabase "node" (fun row -> Ok(row.[0] = VInt 6L)) with
                    | Ok _ ->
                        match scan store defaultDatabase "node" with
                        | Ok(_, rows) -> Expect.isEmpty (List.ofSeq rows) "the mutually-referencing pair is fully deleted, not stuck in infinite recursion"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "setForeignKeyChecks false allows a blocked delete and a dangling child insert through"
                <| fun _ ->
                    let store = withDeptEmployees None
                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    setForeignKeyChecks store false

                    match deleteRows store defaultDatabase "departments" (fun _ -> Ok true) with
                    | Ok affected -> Expect.equal affected 1 "delete goes through with checks disabled"
                    | Error e -> failtestf "expected Ok, got %A" e

                    match insertRows store defaultDatabase "employees" None [ [ VInt 2L; VInt 12345L; VString "bob" ] ] with
                    | Ok _ -> ()
                    | Error e -> failtestf "expected Ok with checks disabled, got %A" e

                testCase "setForeignKeyChecks true (the default) is the store's starting state"
                <| fun _ ->
                    let store = create ()
                    Expect.isTrue store.ForeignKeyChecks "FK checks are on by default"

                testCase "a multi-row INSERT into a self-referencing table sees its own earlier rows as valid parents"
                <| fun _ ->
                    let store = create ()

                    let selfFk =
                        { Name = "fk_node"
                          Columns = [ "parent_id" ]
                          RefTable = "node"
                          RefColumns = [ "id" ]
                          OnDelete = None
                          OnUpdate = None }

                    createTable store defaultDatabase "node" [ idCol; col "parent_id" (TInt false) true ] [] [ selfFk ] None None
                    |> ignore

                    match
                        insertRows
                            store
                            defaultDatabase
                            "node"
                            None
                            [ [ VInt 1L; VNull ]
                              [ VInt 2L; VInt 1L ]
                              [ VInt 3L; VInt 2L ]
                              [ VInt 4L; VInt 3L ] ]
                    with
                    | Ok { Affected = affected } -> Expect.equal affected 4 "every row's parent was already inserted earlier in the same statement"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "UPDATE of a referenced parent key with an existing child row returns error 1451"
                <| fun _ ->
                    let store = withDeptEmployees None

                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    let updater (row: Value[]) = Ok [| VInt 99L; row.[1] |]

                    match updateRows store defaultDatabase "departments" None (fun _ -> Ok true) updater with
                    | Error(ForeignKeyRestrict "fk_dept") ->
                        match scan store defaultDatabase "departments" with
                        | Ok(_, rows) -> Expect.equal (rows |> Seq.map (fun r -> r.[0]) |> List.ofSeq) [ VInt 1L ] "the parent row is untouched"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | other -> failtestf "expected ForeignKeyRestrict, got %A" other

                /// As `withDeptEmployees`, but the FK's `ON UPDATE` action is
                /// the one under test rather than `ON DELETE`.
                let withDeptEmployeesOnUpdate (onUpdate: string) =
                    let store = create ()

                    createTable store defaultDatabase "departments" [ idCol; col "name" (TVarchar 255) false ] [] [] None None
                    |> ignore

                    let fk =
                        { Name = "fk_dept"
                          Columns = [ "dept_id" ]
                          RefTable = "departments"
                          RefColumns = [ "id" ]
                          OnDelete = None
                          OnUpdate = Some onUpdate }

                    createTable
                        store
                        defaultDatabase
                        "employees"
                        [ idCol; col "dept_id" (TInt false) true; col "name" (TVarchar 255) false ]
                        []
                        [ fk ]
                        None
                        None
                    |> ignore

                    insertRows store defaultDatabase "departments" None [ [ VInt 1L; VString "eng" ] ]
                    |> ignore

                    store

                testCase "UPDATE of a referenced parent key with ON UPDATE CASCADE rewrites the children's foreign key"
                <| fun _ ->
                    let store = withDeptEmployeesOnUpdate "CASCADE"

                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    let updater (row: Value[]) = Ok [| VInt 99L; row.[1] |]

                    match updateRows store defaultDatabase "departments" None (fun _ -> Ok true) updater with
                    | Ok _ ->
                        match scan store defaultDatabase "employees" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.map (fun r -> r.[1])) [ VInt 99L ] "dept_id follows the parent's new key"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "UPDATE of a referenced parent key with ON UPDATE SET NULL blanks the children's foreign key"
                <| fun _ ->
                    let store = withDeptEmployeesOnUpdate "SET NULL"

                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    let updater (row: Value[]) = Ok [| VInt 99L; row.[1] |]

                    match updateRows store defaultDatabase "departments" None (fun _ -> Ok true) updater with
                    | Ok _ ->
                        match scan store defaultDatabase "employees" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.map (fun r -> r.[1])) [ VNull ] "dept_id blanked, row otherwise survives"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "ON UPDATE CASCADE fires a RowsUpdated event for the cascaded child too"
                <| fun _ ->
                    let store = withDeptEmployeesOnUpdate "CASCADE"

                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    let updater (row: Value[]) = Ok [| VInt 99L; row.[1] |]

                    match updateRows store defaultDatabase "departments" None (fun _ -> Ok true) updater with
                    | Ok _ ->
                        match List.ofSeq events with
                        | [ RowsUpdated(_, "departments", _); RowsUpdated(_, "employees", [ (oldRow, newRow) ]) ] ->
                            Expect.equal oldRow.[1] (VInt 1L) "before value still holds the pre-cascade dept_id"
                            Expect.equal newRow.[1] (VInt 99L) "after value follows the parent's new key"
                        | other -> failtestf "expected a RowsUpdated for departments and a RowsUpdated for employees, got %A" other
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a self-referencing ON UPDATE CASCADE fails 1451 instead of silently half-applying"
                <| fun _ ->
                    let store = create ()

                    let fk =
                        { Name = "fk_parent"
                          Columns = [ "parent_id" ]
                          RefTable = "cat"
                          RefColumns = [ "id" ]
                          OnDelete = None
                          OnUpdate = Some "CASCADE" }

                    createTable store defaultDatabase "cat" [ idCol; col "parent_id" (TInt false) true ] [] [ fk ] None None
                    |> ignore

                    insertRows store defaultDatabase "cat" None [ [ VInt 1L; VNull ]; [ VInt 2L; VInt 1L ] ]
                    |> ignore

                    let updater (row: Value[]) = Ok [| VInt 100L; row.[1] |]

                    match updateRows store defaultDatabase "cat" None (fun row -> Ok(row.[0] = VInt 1L)) updater with
                    | Error(ForeignKeyRestrict "fk_parent") ->
                        match scan store defaultDatabase "cat" with
                        | Ok(_, rows) ->
                            Expect.equal
                                (List.ofSeq rows |> List.sortBy (fun r -> r.[0]))
                                [ [| VInt 1L; VNull |]; [| VInt 2L; VInt 1L |] ]
                                "rejected before any row (self or child) was rewritten"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | other -> failtestf "expected ForeignKeyRestrict, got %A" other

                testCase "a two-table ON UPDATE CASCADE cycle (A -> B -> A) fails 1451 instead of corrupting the root"
                <| fun _ ->
                    let store = create ()

                    let fkAB =
                        { Name = "fk_a_b"
                          Columns = [ "bid" ]
                          RefTable = "b"
                          RefColumns = [ "id" ]
                          OnDelete = None
                          OnUpdate = Some "CASCADE" }

                    let fkBA =
                        { Name = "fk_b_a"
                          Columns = [ "id" ]
                          RefTable = "a"
                          RefColumns = [ "id" ]
                          OnDelete = None
                          OnUpdate = Some "CASCADE" }

                    (match createTable store defaultDatabase "b" [ idCol ] [] [] None None with
                     | Ok _ -> ()
                     | Error e -> failtestf "createTable b: %A" e)

                    (match createTable store defaultDatabase "a" [ idCol; col "bid" (TInt false) true ] [] [ fkAB ] None None with
                     | Ok _ -> ()
                     | Error e -> failtestf "createTable a: %A" e)
                    // `b.id` also references `a.id`, closing the cycle A -> B -> A.
                    (match alterTable store defaultDatabase "b" [ AddForeignKey fkBA ] with
                     | Ok _ -> ()
                     | Error e -> failtestf "alterTable b: %A" e)

                    // Cyclic foreign keys require disabled checks while the
                    // mutually dependent seed rows are incomplete.
                    store.ForeignKeyChecks <- false

                    (match insertRows store defaultDatabase "b" None [ [ VInt 1L ] ] with
                     | Ok _ -> ()
                     | Error e -> failtestf "insert b: %A" e)

                    (match insertRows store defaultDatabase "a" None [ [ VInt 1L; VInt 1L ] ] with
                     | Ok _ -> ()
                     | Error e -> failtestf "insert a: %A" e)

                    store.ForeignKeyChecks <- true

                    let updater (row: Value[]) = Ok [| VInt 100L; row.[1] |]

                    match updateRows store defaultDatabase "a" None (fun _ -> Ok true) updater with
                    | Error(ForeignKeyRestrict _) ->
                        match scan store defaultDatabase "a", scan store defaultDatabase "b" with
                        | Ok(_, aRows), Ok(_, bRows) ->
                            Expect.equal (List.ofSeq aRows) [ [| VInt 1L; VInt 1L |] ] "a untouched"
                            Expect.equal (List.ofSeq bRows) [ [| VInt 1L |] ] "b untouched"
                        | other -> failtestf "expected Ok scans, got %A" other
                    | other -> failtestf "expected ForeignKeyRestrict, got %A" other

                testCase "a self-referencing ON UPDATE CASCADE nested below another cascade fails 1451"
                <| fun _ ->
                    let store = create ()

                    let fkBA =
                        { Name = "fk_b_a"
                          Columns = [ "k" ]
                          RefTable = "a"
                          RefColumns = [ "id" ]
                          OnDelete = None
                          OnUpdate = Some "CASCADE" }

                    let fkBB =
                        { Name = "fk_b_b"
                          Columns = [ "pk" ]
                          RefTable = "b"
                          RefColumns = [ "k" ]
                          OnDelete = None
                          OnUpdate = Some "CASCADE" }

                    (match createTable store defaultDatabase "a" [ idCol ] [] [] None None with
                     | Ok _ -> ()
                     | Error e -> failtestf "createTable a: %A" e)

                    let kCol =
                        { (col "k" (TInt false) false) with
                            PrimaryKey = true }

                    (match createTable store defaultDatabase "b" [ kCol; col "pk" (TInt false) true ] [] [ fkBA; fkBB ] None None with
                     | Ok _ -> ()
                     | Error e -> failtestf "createTable b: %A" e)

                    (match insertRows store defaultDatabase "a" None [ [ VInt 1L ]; [ VInt 2L ] ] with
                     | Ok _ -> ()
                     | Error e -> failtestf "insert a: %A" e)

                    (match insertRows store defaultDatabase "b" None [ [ VInt 1L; VNull ]; [ VInt 2L; VInt 1L ] ] with
                     | Ok _ -> ()
                     | Error e -> failtestf "insert b: %A" e)

                    let updater (_: Value[]) = Ok [| VInt 100L |]

                    match updateRows store defaultDatabase "a" None (fun row -> Ok(row.[0] = VInt 1L)) updater with
                    | Error(ForeignKeyRestrict "fk_b_b") ->
                        match scan store defaultDatabase "a", scan store defaultDatabase "b" with
                        | Ok(_, aRows), Ok(_, bRows) ->
                            Expect.equal
                                (List.ofSeq aRows |> List.sortBy (fun r -> r.[0]))
                                [ [| VInt 1L |]; [| VInt 2L |] ]
                                "a untouched"

                            Expect.equal
                                (List.ofSeq bRows |> List.sortBy (fun r -> r.[0]))
                                [ [| VInt 1L; VNull |]; [| VInt 2L; VInt 1L |] ]
                                "b untouched"
                        | other -> failtestf "expected Ok scans, got %A" other
                    | other -> failtestf "expected ForeignKeyRestrict fk_b_b, got %A" other ]

          testList
              "truncate"
              [ testCase "TRUNCATE TABLE re-stamps CreateTime, matching MySQL's drop-and-recreate"
                <| fun _ ->
                    let store = create ()

                    createTable store defaultDatabase "t" [ { (col "id" (TInt false) false) with PrimaryKey = true } ] [] [] None None
                    |> ignore

                    let createTimeOf () =
                        match Map.tryFind defaultDatabase store.Catalog |> Option.bind (Map.tryFind "t") with
                        | Some table -> table.CreateTime
                        | None -> failtest "table missing"

                    let before = createTimeOf ()
                    System.Threading.Thread.Sleep 20

                    (match truncate store defaultDatabase "t" with
                     | Ok() -> ()
                     | Error e -> failtestf "truncate: %A" e)

                    Expect.isGreaterThan (createTimeOf ()) before "recreated, not preserved" ]

          testList
              "OnCommit notification hook"
              [ testCase "durability waits outside commit ordering and database locks"
                <| fun _ ->
                    let store = withUsersTable ()
                    use firstWaitStarted = new System.Threading.ManualResetEventSlim()
                    use releaseFirst = new System.Threading.ManualResetEventSlim()
                    let mutable enqueueCount = 0

                    store.Durability.Sink <-
                        Some
                            { DataDirectory = "test"
                              Enqueue =
                                fun _ ->
                                    let ordinal = System.Threading.Interlocked.Increment(&enqueueCount)

                                    if ordinal = 1 then
                                        fun () ->
                                            firstWaitStarted.Set()
                                            releaseFirst.Wait()
                                    else
                                        ignore
                              EnqueueCheckpoint = fun () -> ignore }

                    let insert id name =
                        insertRows store defaultDatabase "users" None [ [ VInt id; VString name; VInt 30L ] ]
                        |> ignore

                    let first = System.Threading.Tasks.Task.Run(fun () -> insert 1L "alice")
                    Expect.isTrue (firstWaitStarted.Wait(System.TimeSpan.FromSeconds 5.)) "the first commit reaches its durability wait"

                    try
                        let second = System.Threading.Tasks.Task.Run(fun () -> insert 2L "bob")
                        Expect.isTrue (second.Wait(System.TimeSpan.FromSeconds 5.)) "the second writer publishes while the first waits"

                        let acquired = System.Threading.Monitor.TryEnter store.CommitLock
                        Expect.isTrue acquired "the durability wait does not hold the ordering lock"

                        if acquired then
                            System.Threading.Monitor.Exit store.CommitLock
                    finally
                        releaseFirst.Set()

                    Expect.isTrue (first.Wait(System.TimeSpan.FromSeconds 5.)) "the first writer resumes after acknowledgement"

                testCase "transaction events enter durability before a later database publication"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 0L ] ] |> ignore
                    use enqueueStarted = new System.Threading.ManualResetEventSlim()
                    use releaseEnqueue = new System.Threading.ManualResetEventSlim()
                    let batches = ResizeArray<CommitEvent list>()

                    store.Durability.Sink <-
                        Some
                            { DataDirectory = "test"
                              Enqueue =
                                fun events ->
                                    lock batches (fun () -> batches.Add events)

                                    if events |> List.exists (function TransactionCommitted _ -> true | _ -> false) then
                                        enqueueStarted.Set()
                                        releaseEnqueue.Wait()

                                    ignore
                              EnqueueCheckpoint = fun () -> ignore }

                    let baseCatalog, snapshot = beginTransactionSnapshotWithBase store

                    updateRows snapshot defaultDatabase "users" None (fun _ -> Ok true) (fun row -> Ok [| row.[0]; row.[1]; VInt 1L |])
                    |> ignore

                    let commit = System.Threading.Tasks.Task.Run(fun () -> commitCatalogInto store baseCatalog snapshot)
                    Expect.isTrue (enqueueStarted.Wait(System.TimeSpan.FromSeconds 5.)) "transaction reaches durable ordering"

                    let later =
                        System.Threading.Tasks.Task.Run(fun () ->
                            updateRows store defaultDatabase "users" None (fun _ -> Ok true) (fun row -> Ok [| row.[0]; row.[1]; VInt 2L |])
                            |> ignore)

                    try
                        Expect.isFalse (later.Wait(System.TimeSpan.FromMilliseconds 100.)) "later publication waits for transaction enqueue"
                    finally
                        releaseEnqueue.Set()
                        System.Threading.Tasks.Task.WaitAll [| commit; later |]

                    let ordered = lock batches (fun () -> List.ofSeq batches)

                    Expect.isTrue
                        (match ordered with
                         | [ [ TransactionCommitted _ ]; [ RowsUpdated _ ] ] -> true
                         | _ -> false)
                        "durable batches follow publication order"

                testCase "insertRows fires RowsInserted with the physically-coerced row (defaults/autoincrement resolved)"
                <| fun _ ->
                    let store = withUsersTable ()
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VNull ] ] |> ignore

                    Expect.equal
                        (List.ofSeq events)
                        [ RowsInserted(defaultDatabase, "users", [ [| VInt 1L; VString "alice"; VNull |] ]) ]
                        "one RowsInserted with the assigned auto-increment id"

                testCase "insertRows with no subscriber fires nothing (no OnCommit set)"
                <| fun _ ->
                    let store = withUsersTable ()
                    Expect.isEmpty store.OnCommit "no subscriber by default"
                    // Just proving this doesn't throw with OnCommit = None.
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VNull ] ] |> ignore

                testCase "an INSERT that inserts zero rows (INSERT IGNORE, every row skipped) fires nothing"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VNull ] ] |> ignore
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    insertRowsIgnore store defaultDatabase "users" None [ [ VInt 1L; VString "bob"; VNull ] ] |> ignore

                    Expect.isEmpty events "duplicate PK row was skipped, not inserted"

                testCase "updateRows fires RowsUpdated with (before, after) pairs for changed rows only"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VInt 1L; VString "alice"; VInt 30L ]; [ VInt 2L; VString "bob"; VInt 40L ] ]
                    |> ignore

                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    // A no-op SET (age stays 40) on bob's row must not appear
                    // in the event's changes, matching "Changed: n" semantics.
                    updateRows
                        store
                        defaultDatabase
                        "users"
                        None
                        (fun _ -> Ok true)
                        (fun row -> if row.[0] = VInt 1L then Ok [| row.[0]; row.[1]; VInt 31L |] else Ok row)
                    |> ignore

                    Expect.equal
                        (List.ofSeq events)
                        [ RowsUpdated(defaultDatabase, "users", [ [| VInt 1L; VString "alice"; VInt 30L |], [| VInt 1L; VString "alice"; VInt 31L |] ]) ]
                        "only alice's row actually changed"

                testCase "deleteRows fires RowsDeleted with the removed rows"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore

                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    deleteRows store defaultDatabase "users" (fun _ -> Ok true) |> ignore

                    Expect.equal
                        (List.ofSeq events)
                        [ RowsDeleted(defaultDatabase, "users", [ [| VInt 1L; VString "alice"; VInt 30L |] ]) ]
                        "the deleted row"

                testCase "upsertRows fires RowsInserted for appended rows and RowsUpdated for collided rows in one call"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore

                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    let applyUpdate (existing: Value[]) (_candidate: Value[]) = Ok [| existing.[0]; existing.[1]; VInt 99L |]

                    upsertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VInt 1L; VString "alice"; VInt 1L ]; [ VInt 2L; VString "carol"; VInt 20L ] ]
                        prepareRow
                        applyUpdate
                        false
                    |> ignore

                    Expect.contains
                        (List.ofSeq events)
                        (RowsInserted(defaultDatabase, "users", [ [| VInt 2L; VString "carol"; VInt 20L |] ]))
                        "carol was a fresh insert"

                    Expect.contains
                        (List.ofSeq events)
                        (RowsUpdated(defaultDatabase, "users", [ [| VInt 1L; VString "alice"; VInt 30L |], [| VInt 1L; VString "alice"; VInt 99L |] ]))
                        "alice's row collided and was updated"

                testCase "upsertRows fires no RowsUpdated for a no-op ON DUPLICATE KEY UPDATE match"
                <| fun _ ->
                    // Probed against real MySQL 8 (row-based binlog): an ODKU
                    // match that leaves every column unchanged logs nothing.
                    // Emitting a before=after RowsUpdated here would let an
                    // onCommit-driven pipeline whose drain upsert no-ops
                    // re-trigger itself on every pass.
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore

                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    // "Update" that reasserts the stored row verbatim.
                    let applyUpdate (existing: Value[]) (_candidate: Value[]) = Ok(Array.copy existing)

                    upsertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 99L ] ] prepareRow applyUpdate false
                    |> ignore

                    Expect.equal (List.ofSeq events) [] "a no-op ODKU match commits nothing"

                testCase "schema changes emit logical DDL events"
                <| fun _ ->
                    let store = create ()
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    createDatabase store "shop" |> ignore
                    createTable store "shop" "widgets" usersColumns [] [] None None |> ignore
                    alterTable store "shop" "widgets" [ AddColumn(col "sku" (TVarchar 64) true, PositionDefault) ] |> ignore
                    truncate store "shop" "widgets" |> ignore
                    dropTable store "shop" "widgets" |> ignore
                    dropDatabase store "shop" |> ignore

                    Expect.equal (List.ofSeq events |> List.length) 6 "one SchemaChanged per DDL statement"

                    events
                    |> Seq.iter (function
                        | SchemaChanged("shop", _) -> ()
                        | SchemaChangedAt("shop", CreateTable _, _) -> ()
                        | SchemaChangedAt("shop", Truncate _, _) -> ()
                        | other -> failtestf "expected a schema event in db 'shop', got %A" other)

                testCase "a failed write (e.g. duplicate key) fires nothing"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore

                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    match insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "bob"; VInt 40L ] ] with
                    | Error(DuplicateKey _) -> ()
                    | other -> failtestf "expected DuplicateKey, got %A" other

                    Expect.isEmpty events "the failed insert wrote nothing, so nothing fired"

                testCase "a transaction snapshot buffers its writes and only emits a single TransactionCommitted, on commit"
                <| fun _ ->
                    let store = withUsersTable ()
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    let baseCatalog, snapshot = beginTransactionSnapshotWithBase store
                    insertRows snapshot defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore
                    insertRows snapshot defaultDatabase "users" None [ [ VInt 2L; VString "bob"; VInt 40L ] ] |> ignore

                    Expect.isEmpty events "nothing visible on the real store until commit"

                    commitCatalogInto store baseCatalog snapshot

                    match List.ofSeq events with
                    | [ TransactionCommitted evs ] ->
                        Expect.equal
                            evs
                            [ RowsInserted(defaultDatabase, "users", [ [| VInt 1L; VString "alice"; VInt 30L |] ])
                              RowsInserted(defaultDatabase, "users", [ [| VInt 2L; VString "bob"; VInt 40L |] ]) ]
                            "both buffered inserts, in order"
                    | other -> failtestf "expected exactly one TransactionCommitted, got %A" other

                testCase "a transaction snapshot and its merge base share one catalog capture"
                <| fun _ ->
                    let store = withUsersTable ()
                    createDatabase store "other" |> ignore
                    createTable store "other" "users" usersColumns [] [] None None |> ignore

                    let baseCatalog, snapshot = beginTransactionSnapshotWithBase store
                    let snapshotCatalog = snapshot.Catalog

                    Expect.equal (Map.keys snapshotCatalog |> Set.ofSeq) (Map.keys baseCatalog |> Set.ofSeq) "both views contain the same databases"

                    for KeyValue(databaseName, database) in baseCatalog do
                        Expect.isTrue
                            (obj.ReferenceEquals(database, Map.find databaseName snapshotCatalog))
                            $"{databaseName} starts from the exact merge-base root"

                testCase "a rolled-back transaction snapshot's buffered events are simply discarded"
                <| fun _ ->
                    let store = withUsersTable ()
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    let snapshot = beginTransactionSnapshot store
                    insertRows snapshot defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore

                    // Private snapshots have no live side effects until publication.
                    Expect.isEmpty events "rollback never touched the real store"

                    match scan store defaultDatabase "users" with
                    | Ok(_, rows) -> Expect.isEmpty (List.ofSeq rows) "the real store's data is untouched too"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a transaction snapshot doesn't buffer at all when the real store has no subscriber"
                <| fun _ ->
                    let store = withUsersTable ()
                    let snapshot = beginTransactionSnapshot store
                    Expect.isNone snapshot.PendingEvents "no subscriber on the real store means nothing to buffer"

                testCase "a snapshot of a transaction's own snapshot (Executor's multi-table UPDATE/DELETE) reaches the WAL flat, not dropped or double-wrapped"
                <| fun _ ->
                    // Mirrors `Executor`'s multi-table `UPDATE`/`DELETE`
                    // path exactly: it opens its own private snapshot of
                    // the *transaction's own* snapshot store (not the real
                    // store) to isolate one statement's writes, then commits
                    // that nested snapshot back onto the outer one. Only the
                    // outer transaction's COMMIT reaches the live store.
                    let store = withUsersTable ()
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit.Add events.Add

                    let txBase, txSnapshot = beginTransactionSnapshotWithBase store
                    Expect.isSome txSnapshot.PendingEvents "the transaction's own snapshot must buffer — the real store has a subscriber"

                    let nestedBase, nested = beginTransactionSnapshotWithBase txSnapshot
                    Expect.isSome nested.PendingEvents "nested snapshots inherit commit buffering from their parent"

                    insertRows nested defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ]
                    |> ignore

                    commitCatalogInto txSnapshot nestedBase nested

                    Expect.isEmpty events "still invisible to the real store — only the outer transaction's own COMMIT reaches it"

                    commitCatalogInto store txBase txSnapshot

                    match List.ofSeq events with
                    | [ TransactionCommitted [ RowsInserted(_, "users", _) ] ] -> ()
                    | other ->
                        failtestf
                            "expected exactly one flat TransactionCommitted wrapping the single RowsInserted (not dropped, not double-wrapped in a nested TransactionCommitted), got %A"
                            other

                    match scan store defaultDatabase "users" with
                    | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.length) 1 "the write also actually reached the real store's memory"
                    | Error e -> failtestf "expected Ok, got %A" e ]

          testList
              "concurrent writers"
              [ testCase "N threads writing to N different databases finish in roughly one thread's time, not N times"
                <| fun _ ->
                    let threadCount = 4
                    let opsPerThread = 4000

                    // One counter row per database — cheap enough per-op
                    // that any accidental store-wide serialization (the
                    // blocker this guards against) shows up as a multiple
                    // of wall time, not just noise.
                    let asInt =
                        function
                        | VInt i -> i
                        | v -> failwithf "expected VInt, got %A" v

                    let workload (store: Store) (dbName: string) =
                        createDatabase store dbName |> ignore
                        createTable store dbName "counters" [ col "n" (TInt false) false ] [] [] None None |> ignore
                        insertRows store dbName "counters" None [ [ VInt 0L ] ] |> ignore

                        for _ in 1 .. opsPerThread do
                            updateRows store dbName "counters" None (fun _ -> Ok true) (fun row -> Ok [| VInt(asInt row.[0] + 1L) |])
                            |> ignore

                    let time f =
                        let sw = System.Diagnostics.Stopwatch.StartNew()
                        f ()
                        sw.Elapsed

                    let baselineStore = create ()
                    let baseline = time (fun () -> workload baselineStore "solo")

                    let store = create ()

                    let parallelElapsed =
                        time (fun () ->
                            [ 0 .. threadCount - 1 ]
                            |> List.map (fun i -> System.Threading.Tasks.Task.Run(fun () -> workload store (sprintf "db%d" i)))
                            |> System.Threading.Tasks.Task.WaitAll)

                    // A generous bound — this only needs to catch the
                    // pathological case (global serialization, where
                    // `threadCount` databases' worth of writes takes
                    // roughly `threadCount` times as long as one), not hold
                    // parallel writers to near-perfect scaling.
                    if not (TestSupport.skipTimingAssertions ()) then
                        Expect.isLessThan
                            parallelElapsed.TotalMilliseconds
                            (baseline.TotalMilliseconds * 2.0 + 200.0)
                            (sprintf "4 databases in parallel (%A) vs 1 database serially (%A) — writers to different databases shouldn't serialize" parallelElapsed baseline)

                testCase "two threads incrementing the same row in the same database serialize correctly (no lost updates)"
                <| fun _ ->
                    let store = create ()
                    createTable store defaultDatabase "counters" [ col "n" (TInt false) false ] [] [] None None |> ignore
                    insertRows store defaultDatabase "counters" None [ [ VInt 0L ] ] |> ignore

                    let threadCount = 8
                    let incrementsPerThread = 500

                    let asInt =
                        function
                        | VInt i -> i
                        | v -> failwithf "expected VInt, got %A" v

                    let increment () =
                        for _ in 1 .. incrementsPerThread do
                            updateRows store defaultDatabase "counters" None (fun _ -> Ok true) (fun row -> Ok [| VInt(asInt row.[0] + 1L) |])
                            |> ignore

                    [ 1 .. threadCount ]
                    |> List.map (fun _ -> System.Threading.Tasks.Task.Run increment)
                    |> System.Threading.Tasks.Task.WaitAll

                    match scan store defaultDatabase "counters" with
                    | Ok(_, rows) ->
                        match List.ofSeq rows with
                        | [ row ] -> Expect.equal (asInt row.[0]) (int64 (threadCount * incrementsPerThread)) "every increment landed, none lost to a race"
                        | other -> failtestf "expected exactly one row, got %A" other
                    | Error e -> failtestf "expected Ok, got %A" e ]

          testList
              "row write concurrency"
              [ testCase "indexed updates to distinct rows execute concurrently"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VNull; VString "alice"; VInt 30L ]
                          [ VNull; VString "bob"; VInt 40L ] ]
                    |> ignore

                    let candidate id =
                        match tryUniqueLookup store defaultDatabase "users" "id" (VInt id) with
                        | Some(_, [ row ]) -> row
                        | other -> failtestf "expected an indexed row, got %A" other

                    use bothEntered = new System.Threading.ManualResetEventSlim(false)
                    let mutable entered = 0

                    let update indexedRow =
                        updateRows
                            store
                            defaultDatabase
                            "users"
                            (Some [ indexedRow ])
                            (fun _ -> Ok true)
                            (fun row ->
                                if System.Threading.Interlocked.Increment(&entered) = 2 then
                                    bothEntered.Set()

                                if not (bothEntered.Wait(System.TimeSpan.FromSeconds 5.0)) then
                                    failtest "distinct row updates did not overlap"

                                Ok [| row.[0]; row.[1]; VInt 99L |])

                    let first = System.Threading.Tasks.Task.Run(fun () -> update (candidate 1L))
                    let second = System.Threading.Tasks.Task.Run(fun () -> update (candidate 2L))
                    System.Threading.Tasks.Task.WaitAll [| first :> System.Threading.Tasks.Task; second :> System.Threading.Tasks.Task |]
                    Expect.equal first.Result (Ok 1) "the first row changed"
                    Expect.equal second.Result (Ok 1) "the second row changed"

                testCase "indexed updates to the same row observe the preceding write"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 0L ] ]
                    |> ignore

                    let candidate =
                        match tryUniqueLookup store defaultDatabase "users" "id" (VInt 1L) with
                        | Some(_, [ row ]) -> row
                        | other -> failtestf "expected an indexed row, got %A" other

                    use start = new System.Threading.ManualResetEventSlim(false)

                    let asInt =
                        function
                        | VInt value -> value
                        | value -> failtestf "expected an integer, got %A" value

                    let increment () =
                        start.Wait()

                        updateRows
                            store
                            defaultDatabase
                            "users"
                            (Some [ candidate ])
                            (fun _ -> Ok true)
                            (fun row -> Ok [| row.[0]; row.[1]; VInt(asInt row.[2] + 1L) |])

                    let first = System.Threading.Tasks.Task.Run increment
                    let second = System.Threading.Tasks.Task.Run increment
                    start.Set()
                    System.Threading.Tasks.Task.WaitAll [| first :> System.Threading.Tasks.Task; second :> System.Threading.Tasks.Task |]
                    Expect.equal first.Result (Ok 1) "the first increment changed the row"
                    Expect.equal second.Result (Ok 1) "the second increment changed the row"

                    match scan store defaultDatabase "users" with
                    | Ok(_, rows) ->
                        let age = rows |> Seq.exactlyOne |> fun row -> row.[2]
                        Expect.equal age (VInt 2L) "both increments are visible"
                    | Error error -> failtestf "expected Ok, got %A" error ]

          testList
              "paged vector"
              [ testCase "updates preserve earlier snapshots across page boundaries"
                <| fun _ ->
                    let original = PagedVector.ofSeq [ 0 .. 599 ]
                    let builder = original.ToBuilder()
                    builder.[0] <- -1
                    builder.[255] <- -2
                    builder.[256] <- -3
                    builder.[599] <- -4
                    builder.Add 600
                    let updated = builder.DrainToImmutable()

                    Expect.sequenceEqual original [ 0 .. 599 ] "published pages remain unchanged"
                    Expect.equal updated.[0] -1 "first page changed"
                    Expect.equal updated.[255] -2 "first page boundary changed"
                    Expect.equal updated.[256] -3 "second page changed"
                    Expect.equal updated.[599] -4 "last existing value changed"
                    Expect.equal updated.[600] 600 "append crosses the final page"
                    Expect.throws (fun () -> builder.[0] <- 42) "a drained builder cannot mutate published pages"

                testCase "empty vectors enumerate no values"
                <| fun _ ->
                    let rows = PagedVector.empty<int>
                    Expect.isEmpty (List.ofSeq rows) "empty vector" ]

          testList
              "row store"
              [ testCase "deletion preserves surviving identities and scan order"
                <| fun _ ->
                    let original = RowStore.ofSeq [ "a"; "b"; "c" ]
                    let indexed = List.ofSeq original.Indexed
                    let aId, _ = indexed.[0]
                    let bId, _ = indexed.[1]
                    let cId, _ = indexed.[2]
                    let afterDelete = original.Remove bId
                    let dId, updated = afterDelete.Append "d"

                    Expect.sequenceEqual original [ "a"; "b"; "c" ] "the original snapshot remains visible"
                    Expect.sequenceEqual updated [ "a"; "c"; "d" ] "tombstones are absent from scans"
                    Expect.equal updated.[aId] "a" "the first row keeps its identity"
                    Expect.equal updated.[cId] "c" "the last surviving row keeps its identity"
                    Expect.notEqual dId bId "deleted identities are not reused"

                testCase "builders publish updates and expose tombstones"
                <| fun _ ->
                    let exactlyTwo =
                        function
                        | [ first; second ] -> first, second
                        | values -> failtestf "expected two values, got %d" values.Length

                    let original = RowStore.ofSeq [ "a"; "b" ]
                    let (aId, _), (bId, _) = original.Indexed |> List.ofSeq |> exactlyTwo
                    let builder = original.ToBuilder()

                    builder.[aId] <- "A"
                    Expect.isTrue (builder.Remove bId) "the live row is removed"
                    Expect.isFalse (builder.Remove bId) "the tombstone is not removed twice"
                    let cId, dId = builder.AddRange [ "c"; "d" ] |> exactlyTwo
                    let updated = builder.DrainToImmutable()

                    Expect.equal updated.Count 3 "the published live count excludes the tombstone"
                    Expect.equal updated.TombstoneCount 1 "the removed slot remains measurable"
                    Expect.equal (updated.TryFind bId) None "the removed identity resolves to no row"

                    Expect.throwsT<System.Collections.Generic.KeyNotFoundException>
                        (fun () -> updated.[bId] |> ignore)
                        "the removed identity has no value"

                    Expect.sequenceEqual updated [ "A"; "c"; "d" ] "the builder publishes updates and appends in slot order"
                    Expect.equal updated.[cId] "c" "the first appended identity resolves"
                    Expect.equal updated.[dId] "d" "the second appended identity resolves"

                testCase "compaction preserves logical identities and earlier roots"
                <| fun _ ->
                    let original = RowStore.ofSeq [ 0 .. 599 ]
                    let indexed = original.Indexed |> Array.ofSeq
                    let retainedId, _ = indexed.[500]
                    let removed =
                        indexed.[0..299]
                        |> Array.fold (fun (rows: RowStore<int>) (rowId, _) -> rows.Remove rowId) original
                    let compacted = removed.Compact()

                    Expect.equal compacted.TombstoneCount 0 "the dense root has no deleted slots"
                    Expect.equal compacted.[retainedId] 500 "logical ids survive physical relocation"
                    Expect.equal original.[retainedId] 500 "the earlier root remains readable"
                    Expect.equal (compacted.ChangesFrom removed |> Seq.length) 0 "layout changes are not row changes"

                    let appendedId, appended = compacted.Append 600
                    Expect.notEqual appendedId (fst indexed.[0]) "compaction does not recycle deleted identities"
                    Expect.sequenceEqual appended [ 300 .. 600 ] "scan order survives compaction and append"

                testCase "delete-heavy tables compact without invalidating snapshots or indexes"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ for id in 1L .. 1000L -> [ VInt id; VString(sprintf "user-%d" id); VInt 0L ] ]
                    |> ignore

                    let before = store.Catalog.[defaultDatabase].["users"]

                    let retainedId =
                        before.RowsArray.Indexed
                        |> Seq.find (fun (_, row) -> row.[0] = VInt 500L)
                        |> fst

                    deleteRows store defaultDatabase "users" (fun row ->
                        match row.[0] with
                        | VInt id -> Ok(id <= 300L)
                        | _ -> Ok false)
                    |> function
                        | Ok 300 -> ()
                        | result -> failtestf "expected 300 deleted rows, got %A" result

                    let after = store.Catalog.[defaultDatabase].["users"]
                    Expect.equal after.RowsArray.TombstoneCount 0 "the published root is dense"
                    Expect.equal after.RowsArray.[retainedId].[0] (VInt 500L) "the retained row keeps its identity"
                    Expect.equal before.RowsArray.Count 1000 "the captured root retains every row"

                    match insertRows store defaultDatabase "users" None [ [ VInt 500L; VString "duplicate"; VInt 0L ] ] with
                    | Error(DuplicateKey("PRIMARY", _)) -> ()
                    | result -> failtestf "expected the compacted primary index to reject a duplicate, got %A" result ]

          testList
              "performance canary"
              [ testCase "a single-row UPDATE against a 50,000-row table stays roughly linear, not quadratic"
                <| fun _ ->
                    // The generous bound distinguishes quadratic growth from
                    // ordinary machine variance; it is not a latency target.
                    let store = withUsersTable ()

                    let rows = [ for i in 1 .. 50_000 -> [ VString(sprintf "name%d" i); VInt(int64 (i % 100)) ] ]
                    insertRows store defaultDatabase "users" (Some [ "name"; "age" ]) rows |> ignore

                    let sw = System.Diagnostics.Stopwatch.StartNew()

                    let result = updateRows store defaultDatabase "users" None (fun row -> Ok(row.[0] = VInt 25_000L)) (fun row -> Ok [| row.[0]; row.[1]; VInt 999L |])

                    sw.Stop()

                    match result with
                    | Ok 1 -> ()
                    | other -> failtestf "expected Ok 1, got %A" other

                    if not (TestSupport.skipTimingAssertions ()) then
                        Expect.isLessThan
                            sw.Elapsed.TotalMilliseconds
                            500.0
                            (sprintf "single-row UPDATE against 50,000 rows took %A — looks quadratic again" sw.Elapsed) ] ]
