module Fsdb.Tests.StorageTests

open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage

let private col name ty nullable =
    { Name = name
      Type = ty
      Nullable = nullable
      Default = None
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      Generated = None }

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
    createTable store defaultDatabase "users" usersColumns [] [] |> ignore
    store

let tests =
    testList
        "storage"
        [ testList
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

                    match createTable store defaultDatabase "users" usersColumns [] [] with
                    | Error(TableExists "users") -> ()
                    | other -> failtestf "expected TableExists, got %A" other

                testCase "createTable is case-insensitive against an existing table"
                <| fun _ ->
                    let store = withUsersTable ()

                    match createTable store defaultDatabase "USERS" usersColumns [] [] with
                    | Error(TableExists _) -> ()
                    | other -> failtestf "expected TableExists, got %A" other

                testCase "createTable auto-creates the database on first use"
                <| fun _ ->
                    let store = create ()

                    match createTable store "newdb" "users" usersColumns [] [] with
                    | Ok() ->
                        match scan store "newdb" "users" with
                        | Ok _ -> ()
                        | Error e -> failtestf "expected the table to exist, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

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
                    | Ok(lastId, _) -> Expect.equal lastId 1L "AUTO_INCREMENT restarts at 1 after truncate"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "truncate on an unknown table returns NoSuchTable"
                <| fun _ ->
                    let store = create ()

                    match truncate store defaultDatabase "ghosts" with
                    | Error(NoSuchTable "ghosts") -> ()
                    | other -> failtestf "expected NoSuchTable, got %A" other ]

          testList
              "insertRows"
              [ testCase "inserting all columns by position returns lastInsertId 1 and affected 1"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] with
                    | Ok(lastId, affected) ->
                        Expect.equal lastId 1L "first assigned id"
                        Expect.equal affected 1 "one row affected"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AUTO_INCREMENT assigns sequential ids across inserts"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ]
                    |> ignore

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "bob"; VInt 25L ] ] with
                    | Ok(lastId, _) -> Expect.equal lastId 2L "second row gets id 2"
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
                    | Ok(lastId, affected) ->
                        Expect.equal lastId 1L "lastInsertId is the first row's id"
                        Expect.equal affected 2 "two rows affected"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "an explicit id bumps the AUTO_INCREMENT counter past it"
                <| fun _ ->
                    let store = withUsersTable ()

                    insertRows store defaultDatabase "users" None [ [ VInt 100L; VString "alice"; VInt 30L ] ]
                    |> ignore

                    match insertRows store defaultDatabase "users" None [ [ VNull; VString "bob"; VInt 25L ] ] with
                    | Ok(lastId, _) -> Expect.equal lastId 101L "counter continues past the explicit id"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "inserting by explicit column list fills the rest from defaults"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRows store defaultDatabase "users" (Some [ "name" ]) [ [ VString "alice" ] ] with
                    | Ok(lastId, affected) ->
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
                          { (col "created_at" TTimestamp true) with Default = Some DCurrentTimestamp } ]

                    createTable store defaultDatabase "posts" columns [] [] |> ignore

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

                    createTable store defaultDatabase "t" columns [] [] |> ignore

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
                    match coerceValue true (col "established" TDateTime true) (VString "") with
                    | Error(InvalidValueForColumn("established", "")) -> ()
                    | other -> failtestf "expected InvalidValueForColumn, got %A" other

                testCase "non-strict mode coerces an unparseable datetime string on a nullable column to NULL"
                <| fun _ ->
                    match coerceValue false (col "established" TDateTime true) (VString "") with
                    | Ok VNull -> ()
                    | other -> failtestf "expected Ok VNull, got %A" other

                testCase "non-strict mode still rejects an unparseable datetime string on a NOT NULL column"
                <| fun _ ->
                    match coerceValue false (col "established" TDateTime false) (VString "") with
                    | Error(InvalidValueForColumn("established", "")) -> ()
                    | other -> failtestf "expected InvalidValueForColumn, got %A" other ]

          testList
              "unique constraints"
              [ let emailsTable store =
                    createTable
                        store
                        defaultDatabase
                        "emails"
                        [ col "id" (TInt false) false; col "email" (TVarchar 255) false ]
                        [ { Name = "uq_email"; Columns = [ "email" ]; Unique = true } ]
                        []
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

                testCase "the unique check is collation-aware: case and trailing spaces don't dodge it"
                <| fun _ ->
                    let store = create ()
                    emailsTable store
                    insertRows store defaultDatabase "emails" None [ [ VInt 1L; VString "a@x.com" ] ] |> ignore

                    match insertRows store defaultDatabase "emails" None [ [ VInt 2L; VString "A@X.COM " ] ] with
                    | Error(DuplicateKey("uq_email", _)) -> ()
                    | other -> failtestf "expected DuplicateKey, got %A" other

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

                    match updateRows store defaultDatabase "emails" (fun row -> Ok(row.[0] = VInt 2L)) updater with
                    | Error(DuplicateKey("uq_email", "a@x.com")) -> ()
                    | other -> failtestf "expected DuplicateKey, got %A" other

                testCase "UPDATE that leaves a row's own unique value unchanged doesn't collide with itself"
                <| fun _ ->
                    let store = create ()
                    emailsTable store
                    insertRows store defaultDatabase "emails" None [ [ VInt 1L; VString "a@x.com" ] ] |> ignore

                    let updater (row: Value[]) = Ok row

                    match updateRows store defaultDatabase "emails" (fun _ -> Ok true) updater with
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

                    match updateRows store defaultDatabase "emails" (fun _ -> Ok true) (fun row -> Ok [| row.[0]; VString "same@x.com" |]) with
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
                    | Ok(_, affected) ->
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
                        [ { Name = "uq_email"; Columns = [ "email" ]; Unique = true } ]
                        []
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
                    | Ok(_, affected) ->
                        Expect.equal affected 1 "only the non-colliding row counted"

                        match scan store defaultDatabase "emails" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.length) 2 "the original row plus the one good new row"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "insertRowsIgnore skips a row whose foreign key parent is missing"
                <| fun _ ->
                    let store = create ()

                    createTable store defaultDatabase "departments" [ col "id" (TInt false) false ] [] []
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
                    | Ok(_, affected) ->
                        Expect.equal affected 1 "only the row with no dangling FK counted"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "insertRowsIgnore returns lastInsertId 0 when every row is skipped"
                <| fun _ ->
                    let store = withUsersTable ()

                    match insertRowsIgnore store defaultDatabase "users" (Some [ "id"; "age" ]) [ [ VInt 1L; VInt 30L ] ] with
                    | Ok(lastId, affected) ->
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
                    | Ok(lastId, _) -> Expect.equal lastId 2L "the counter kept climbing across the delete"
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

                    match updateRows store defaultDatabase "users" predicate updater with
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

                    match updateRows store defaultDatabase "users" (fun _ -> Ok true) updater with
                    | Error(NotNullViolation "name") -> ()
                    | other -> failtestf "expected NotNullViolation, got %A" other

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
                    createTable store defaultDatabase "dup" [ col "v" (TInt false) true ] [] [] |> ignore

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
                    let ix = { Name = "idx_name"; Columns = [ "name" ]; Unique = false }

                    match alterTable store defaultDatabase "users" [ AddIndex ix ] with
                    | Ok() ->
                        match scan store defaultDatabase "users" with
                        | Ok _ -> ()
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                    match alterTable store defaultDatabase "users" [ DropIndexAction "idx_name" ] with
                    | Ok() -> ()
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "AddForeignKey / DropForeignKey manage the table's FK metadata"
                <| fun _ ->
                    let store = withUsersTable ()

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
                    createTable store defaultDatabase "t" [ col "a" (TInt false) true; col "b" (TInt false) true ] [] [] |> ignore

                    match alterTable store defaultDatabase "t" [ AddPrimaryKey [ "a"; "b" ] ] with
                    | Ok() ->
                        match scan store defaultDatabase "t" with
                        | Ok(columns, _) -> Expect.isTrue (columns |> List.forall (fun c -> c.PrimaryKey)) "both columns are PK"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "multiple actions in one call apply in order"
                <| fun _ ->
                    let store = withUsersTable ()

                    match alterTable store defaultDatabase "users" [ DropColumn "age"; RenameTo "people" ] with
                    | Ok() ->
                        match scan store defaultDatabase "people" with
                        | Ok(columns, _) -> Expect.equal (columns |> List.map (fun c -> c.Name)) [ "id"; "name" ] "both actions applied"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | Error e -> failtestf "expected Ok, got %A" e ]

          testList
              "upsertRows"
              [ testCase "no collision: behaves like a plain insert"
                <| fun _ ->
                    let store = withUsersTable ()
                    let applyUpdate (_: Value[]) (candidate: Value[]) = Ok candidate

                    match upsertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VInt 30L ] ] Ok applyUpdate with
                    | Ok(lastId, affected) ->
                        Expect.equal lastId 1L "inserted with a fresh id"
                        Expect.equal affected 1 "one row"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a primary-key collision calls applyUpdate instead of inserting a second row"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore

                    let applyUpdate (existing: Value[]) (_candidate: Value[]) =
                        Ok [| existing.[0]; existing.[1]; VInt 31L |]

                    match upsertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 999L ] ] Ok applyUpdate with
                    | Ok(_, affected) ->
                        Expect.equal affected 1 "one row affected (the update, not an insert)"

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
                        [ { Name = "uq_email"; Columns = [ "email" ]; Unique = true } ]
                        []
                    |> ignore

                    insertRows store defaultDatabase "emails" None [ [ VInt 1L; VString "a@x.com" ] ] |> ignore

                    let applyUpdate (existing: Value[]) (_candidate: Value[]) = Ok existing

                    match upsertRows store defaultDatabase "emails" None [ [ VInt 2L; VString "a@x.com" ] ] Ok applyUpdate with
                    | Ok(_, affected) ->
                        Expect.equal affected 1 "matched via the unique index"

                        match scan store defaultDatabase "emails" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows |> List.length) 1 "no duplicate row inserted"
                        | Error e -> failtestf "expected Ok, got %A" e
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

                    createTable store defaultDatabase "departments" [ idCol; col "name" (TVarchar 255) false ] [] []
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
                    |> ignore

                    insertRows store defaultDatabase "departments" None [ [ VInt 1L; VString "eng" ] ]
                    |> ignore

                    store

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

                    match updateRows store defaultDatabase "employees" (fun _ -> Ok true) updater with
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

                testCase "ON DELETE SET NULL against a NOT NULL foreign key column fails the delete instead of blanking it"
                <| fun _ ->
                    let store = create ()

                    createTable store defaultDatabase "pa" [ idCol ] [] [] |> ignore

                    let fk =
                        { Name = "fk_ch"
                          Columns = [ "pid" ]
                          RefTable = "pa"
                          RefColumns = [ "id" ]
                          OnDelete = Some "SET NULL"
                          OnUpdate = None }

                    createTable store defaultDatabase "ch" [ idCol; col "pid" (TInt false) false ] [] [ fk ]
                    |> ignore

                    insertRows store defaultDatabase "pa" None [ [ VInt 1L ] ] |> ignore
                    insertRows store defaultDatabase "ch" None [ [ VInt 5L; VInt 1L ] ] |> ignore

                    match deleteRows store defaultDatabase "pa" (fun _ -> Ok true) with
                    | Error(NotNullViolation "pid") ->
                        match scan store defaultDatabase "ch" with
                        | Ok(_, rows) -> Expect.equal (List.ofSeq rows) [ [| VInt 5L; VInt 1L |] ] "the child row is untouched"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | other -> failtestf "expected NotNullViolation, got %A" other

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

                    createTable store defaultDatabase "node" [ idCol; col "parent_id" (TInt false) true ] [] [ selfFk ]
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

                    // employees.dept_id = 1 is now dangling; also verify a
                    // fresh insert to a non-existent parent is allowed too.
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

                    createTable store defaultDatabase "node" [ idCol; col "parent_id" (TInt false) true ] [] [ selfFk ]
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
                    | Ok(_, affected) -> Expect.equal affected 4 "every row's parent was already inserted earlier in the same statement"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "UPDATE of a referenced parent key with an existing child row returns error 1451"
                <| fun _ ->
                    let store = withDeptEmployees None

                    insertRows store defaultDatabase "employees" None [ [ VInt 1L; VInt 1L; VString "alice" ] ]
                    |> ignore

                    let updater (row: Value[]) = Ok [| VInt 99L; row.[1] |]

                    match updateRows store defaultDatabase "departments" (fun _ -> Ok true) updater with
                    | Error(ForeignKeyRestrict "fk_dept") ->
                        match scan store defaultDatabase "departments" with
                        | Ok(_, rows) -> Expect.equal (rows |> Seq.map (fun r -> r.[0]) |> List.ofSeq) [ VInt 1L ] "the parent row is untouched"
                        | Error e -> failtestf "expected Ok, got %A" e
                    | other -> failtestf "expected ForeignKeyRestrict, got %A" other ]

          testList
              "OnCommit notification hook"
              [ testCase "insertRows fires RowsInserted with the physically-coerced row (defaults/autoincrement resolved)"
                <| fun _ ->
                    let store = withUsersTable ()
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit <- Some events.Add

                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VNull ] ] |> ignore

                    Expect.equal
                        (List.ofSeq events)
                        [ RowsInserted(defaultDatabase, "users", [ [| VInt 1L; VString "alice"; VNull |] ]) ]
                        "one RowsInserted with the assigned auto-increment id"

                testCase "insertRows with no subscriber fires nothing (no OnCommit set)"
                <| fun _ ->
                    let store = withUsersTable ()
                    Expect.isNone store.OnCommit "no subscriber by default"
                    // Just proving this doesn't throw with OnCommit = None.
                    insertRows store defaultDatabase "users" None [ [ VNull; VString "alice"; VNull ] ] |> ignore

                testCase "an INSERT that inserts zero rows (INSERT IGNORE, every row skipped) fires nothing"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VNull ] ] |> ignore
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit <- Some events.Add

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
                    store.OnCommit <- Some events.Add

                    // A no-op SET (age stays 40) on bob's row must not appear
                    // in the event's changes, matching "Changed: n" semantics.
                    updateRows
                        store
                        defaultDatabase
                        "users"
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
                    store.OnCommit <- Some events.Add

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
                    store.OnCommit <- Some events.Add

                    let applyUpdate (existing: Value[]) (_candidate: Value[]) = Ok [| existing.[0]; existing.[1]; VInt 99L |]

                    upsertRows
                        store
                        defaultDatabase
                        "users"
                        None
                        [ [ VInt 1L; VString "alice"; VInt 1L ]; [ VInt 2L; VString "carol"; VInt 20L ] ]
                        Ok
                        applyUpdate
                    |> ignore

                    Expect.contains
                        (List.ofSeq events)
                        (RowsInserted(defaultDatabase, "users", [ [| VInt 2L; VString "carol"; VInt 20L |] ]))
                        "carol was a fresh insert"

                    Expect.contains
                        (List.ofSeq events)
                        (RowsUpdated(defaultDatabase, "users", [ [| VInt 1L; VString "alice"; VInt 30L |], [| VInt 1L; VString "alice"; VInt 99L |] ]))
                        "alice's row collided and was updated"

                testCase "createTable/dropTable/alterTable/truncate/createDatabase/dropDatabase all fire SchemaChanged, logically (the DDL statement, not row data)"
                <| fun _ ->
                    let store = create ()
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit <- Some events.Add

                    createDatabase store "shop" |> ignore
                    createTable store "shop" "widgets" usersColumns [] [] |> ignore
                    alterTable store "shop" "widgets" [ AddColumn(col "sku" (TVarchar 64) true, PositionDefault) ] |> ignore
                    truncate store "shop" "widgets" |> ignore
                    dropTable store "shop" "widgets" |> ignore
                    dropDatabase store "shop" |> ignore

                    Expect.equal (List.ofSeq events |> List.length) 6 "one SchemaChanged per DDL statement"

                    events
                    |> Seq.iter (function
                        | SchemaChanged("shop", _) -> ()
                        | other -> failtestf "expected a SchemaChanged in db 'shop', got %A" other)

                testCase "a failed write (e.g. duplicate key) fires nothing"
                <| fun _ ->
                    let store = withUsersTable ()
                    insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore

                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit <- Some events.Add

                    match insertRows store defaultDatabase "users" None [ [ VInt 1L; VString "bob"; VInt 40L ] ] with
                    | Error(DuplicateKey _) -> ()
                    | other -> failtestf "expected DuplicateKey, got %A" other

                    Expect.isEmpty events "the failed insert wrote nothing, so nothing fired"

                testCase "a transaction snapshot buffers its writes and only emits a single TransactionCommitted, on commit"
                <| fun _ ->
                    let store = withUsersTable ()
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit <- Some events.Add

                    let snapshot = beginTransactionSnapshot store
                    insertRows snapshot defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore
                    insertRows snapshot defaultDatabase "users" None [ [ VInt 2L; VString "bob"; VInt 40L ] ] |> ignore

                    Expect.isEmpty events "nothing visible on the real store until commit"

                    // Merge the snapshot's catalog back in (what
                    // QueryHandler.commitSession does) and flush its buffer.
                    store.Catalog <- snapshot.Catalog
                    commitTransactionEvents store snapshot

                    match List.ofSeq events with
                    | [ TransactionCommitted evs ] ->
                        Expect.equal
                            evs
                            [ RowsInserted(defaultDatabase, "users", [ [| VInt 1L; VString "alice"; VInt 30L |] ])
                              RowsInserted(defaultDatabase, "users", [ [| VInt 2L; VString "bob"; VInt 40L |] ]) ]
                            "both buffered inserts, in order"
                    | other -> failtestf "expected exactly one TransactionCommitted, got %A" other

                testCase "a rolled-back transaction snapshot's buffered events are simply discarded"
                <| fun _ ->
                    let store = withUsersTable ()
                    let events = ResizeArray<CommitEvent>()
                    store.OnCommit <- Some events.Add

                    let snapshot = beginTransactionSnapshot store
                    insertRows snapshot defaultDatabase "users" None [ [ VInt 1L; VString "alice"; VInt 30L ] ] |> ignore

                    // ROLLBACK: just drop the snapshot — never call
                    // commitTransactionEvents, never merge its catalog.
                    Expect.isEmpty events "rollback never touched the real store"

                    match scan store defaultDatabase "users" with
                    | Ok(_, rows) -> Expect.isEmpty (List.ofSeq rows) "the real store's data is untouched too"
                    | Error e -> failtestf "expected Ok, got %A" e

                testCase "a transaction snapshot doesn't buffer at all when the real store has no subscriber"
                <| fun _ ->
                    let store = withUsersTable ()
                    let snapshot = beginTransactionSnapshot store
                    Expect.isNone snapshot.PendingEvents "no subscriber on the real store means nothing to buffer" ] ]
