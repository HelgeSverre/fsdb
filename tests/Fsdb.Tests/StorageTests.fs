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
      PrimaryKey = false }

let private idCol =
    { (col "id" TInt false) with
        AutoIncrement = true
        PrimaryKey = true }

let private usersColumns =
    [ idCol
      col "name" (TVarchar 255) false
      { (col "age" TInt true) with Default = Some(VInt 0L) } ]

/// A store with an empty `users` table, ready to insert into.
let private withUsersTable () =
    let store = create ()
    createTable store defaultDatabase "users" usersColumns |> ignore
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

                    match createTable store defaultDatabase "users" usersColumns with
                    | Error(TableExists "users") -> ()
                    | other -> failtestf "expected TableExists, got %A" other

                testCase "createTable is case-insensitive against an existing table"
                <| fun _ ->
                    let store = withUsersTable ()

                    match createTable store defaultDatabase "USERS" usersColumns with
                    | Error(TableExists _) -> ()
                    | other -> failtestf "expected TableExists, got %A" other

                testCase "createTable auto-creates the database on first use"
                <| fun _ ->
                    let store = create ()

                    match createTable store "newdb" "users" usersColumns with
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

                testCase "missing nullable column with no default becomes NULL"
                <| fun _ ->
                    let store = create ()

                    let columns =
                        [ col "id" TInt false; col "nickname" (TVarchar 50) true ]

                    createTable store defaultDatabase "t" columns |> ignore

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
                    | other -> failtestf "expected InvalidValueForColumn, got %A" other ]

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

                    let predicate (row: Value[]) = row.[1] = VString "alice"
                    let updater (row: Value[]) = [| row.[0]; row.[1]; VString "31" |]

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

                    let updater (row: Value[]) = [| row.[0]; VNull; row.[2] |]

                    match updateRows store defaultDatabase "users" (fun _ -> true) updater with
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

                    match deleteRows store defaultDatabase "users" (fun row -> row.[1] = VString "alice") with
                    | Ok affected ->
                        Expect.equal affected 1 "one row deleted"

                        match scan store defaultDatabase "users" with
                        | Ok(_, rows) ->
                            Expect.equal
                                (rows |> Seq.map (fun r -> r.[1]) |> List.ofSeq)
                                [ VString "bob" ]
                                "only bob remains"
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
                    | Error e -> failtestf "expected Ok, got %A" e ] ]
