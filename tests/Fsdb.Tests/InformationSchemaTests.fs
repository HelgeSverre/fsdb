module Fsdb.Tests.InformationSchemaTests

open Expecto
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor

/// Parses `sql` and runs it against `store`, failing the test with the
/// parse error if the SQL itself doesn't parse — same helper shape as
/// `ExecutorTests.run`, kept local since this file only ever runs SELECTs
/// against `information_schema`.
let private run (store: Store) (sql: string) : QueryResult =
    match Fsdb.Parser.parse sql with
    | Error msg -> failtestf "expected %s to parse, got error: %s" sql msg
    | Ok stmt -> execute store builtins defaultDatabase 0L stmt |> snd

let private setup () : Store =
    let store = create ()

    run store "CREATE TABLE users (id INT AUTO_INCREMENT PRIMARY KEY, email VARCHAR(255) NOT NULL UNIQUE, name VARCHAR(100))"
    |> ignore

    run
        store
        "CREATE TABLE posts (id INT AUTO_INCREMENT PRIMARY KEY, user_id INT, title VARCHAR(200), CONSTRAINT posts_user_id_foreign FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE)"
    |> ignore

    run store "INSERT INTO users (email, name) VALUES ('a@b.com', 'alice')" |> ignore
    store

let tests =
    testList
        "information_schema"
        [ testCase "TABLES lists every real table with its row count, case-insensitive db/table names"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT table_name, table_type, engine, table_rows FROM information_schema.TABLES WHERE table_schema = 'fsdb' ORDER BY table_name" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "posts"; Some "BASE TABLE"; Some "InnoDB"; Some "0" ]
                        [ Some "users"; Some "BASE TABLE"; Some "InnoDB"; Some "1" ] ]
                      "both tables, correctly typed and counted"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "COLUMNS projects declared columns with type/nullability/key metadata"
          <| fun _ ->
              let store = setup ()

              match
                  run
                      store
                      "SELECT column_name, column_type, is_nullable, column_key, extra FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'users' ORDER BY ordinal_position"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "id"; Some "int"; Some "NO"; Some "PRI"; Some "auto_increment" ]
                        [ Some "email"; Some "varchar(255)"; Some "NO"; Some "UNI"; Some "" ]
                        [ Some "name"; Some "varchar(100)"; Some "YES"; Some ""; Some "" ] ]
                      "columns in declared order with the right metadata"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "STATISTICS has one row per index column, including a synthesized PRIMARY"
          <| fun _ ->
              let store = setup ()

              match
                  run
                      store
                      "SELECT index_name, non_unique, seq_in_index, column_name FROM information_schema.statistics WHERE table_schema = 'fsdb' AND table_name = 'users' ORDER BY index_name, seq_in_index"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "email"; Some "0"; Some "1"; Some "email" ]
                        [ Some "PRIMARY"; Some "0"; Some "1"; Some "id" ] ]
                      "primary key and the column-level unique index both show up"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "KEY_COLUMN_USAGE and REFERENTIAL_CONSTRAINTS surface the foreign key"
          <| fun _ ->
              let store = setup ()

              match
                  run
                      store
                      "SELECT column_name, referenced_table_name, referenced_column_name FROM information_schema.key_column_usage WHERE table_schema = 'fsdb' AND table_name = 'posts' AND referenced_table_name IS NOT NULL"
              with
              | ResultSet(_, [ [ Some "user_id"; Some "users"; Some "id" ] ]) -> ()
              | other -> failtestf "expected the fk column usage row, got %A" other

              match
                  run
                      store
                      "SELECT delete_rule, table_name, referenced_table_name FROM information_schema.referential_constraints WHERE constraint_schema = 'fsdb' AND table_name = 'posts'"
              with
              | ResultSet(_, [ [ Some "CASCADE"; Some "posts"; Some "users" ] ]) -> ()
              | other -> failtestf "expected the referential constraint row, got %A" other

          testCase "SCHEMATA lists every real database plus information_schema itself"
          <| fun _ ->
              let store = setup ()
              run store "CREATE DATABASE app" |> ignore

              match run store "SELECT schema_name FROM information_schema.schemata ORDER BY schema_name" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "app" ]; [ Some "fsdb" ]; [ Some "information_schema" ] ] "every schema present"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "an unknown information_schema table is a plain 1146"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT * FROM information_schema.nope" with
              | Err(1146, _) -> ()
              | other -> failtestf "expected 1146 for an unknown virtual table, got %A" other

          testCase "USE information_schema (as the statement db) resolves an unqualified table name"
          <| fun _ ->
              let store = setup ()

              match Fsdb.Parser.parse "SELECT table_name FROM tables WHERE table_schema = 'fsdb' AND table_name = 'users'" with
              | Error msg -> failtestf "expected the query to parse, got error: %s" msg
              | Ok stmt ->
                  match execute store builtins "information_schema" 0L stmt |> snd with
                  | ResultSet(_, [ [ Some "users" ] ]) -> ()
                  | other -> failtestf "expected the unqualified lookup to still resolve, got %A" other ]
