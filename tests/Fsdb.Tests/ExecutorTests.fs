module Fsdb.Tests.ExecutorTests

open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor

/// Parses `sql` and runs it against a fresh in-memory store, failing the
/// test with the parse error if the SQL itself doesn't parse — end-to-end
/// statement tests read as plain "run this SQL, check this QueryResult".
let private run (store: Store) (registry: Registry) (sql: string) : QueryResult =
    match Fsdb.Parser.parse sql with
    | Error msg -> failtestf "expected %s to parse, got error: %s" sql msg
    | Ok stmt -> execute store registry defaultDatabase 0L stmt |> snd

let private runDefault (store: Store) (sql: string) : QueryResult = run store builtins sql

let private newStore () = create ()

let tests =
    testList
        "executor"
        [ testList
              "CREATE / INSERT / SELECT with WHERE, ORDER BY, LIMIT"
              [ testCase "create, insert, and select round-trip with where + order + limit"
                <| fun _ ->
                    let store = newStore ()

                    runDefault store "CREATE TABLE users (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50), age INT)"
                    |> ignore

                    match runDefault store "INSERT INTO users (name, age) VALUES ('alice', 30), ('bob', 25), ('carol', 40)" with
                    | Affected 3UL -> ()
                    | other -> failtestf "expected 3 rows affected, got %A" other

                    match runDefault store "SELECT name, age FROM users WHERE age > 26 ORDER BY name LIMIT 10" with
                    | ResultSet([ "name"; "age" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "30" ]; [ Some "carol"; Some "40" ] ] "filtered, sorted rows"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "SELECT * expands every column"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'x')" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "id"; "name" ], [ [ Some "1"; Some "x" ] ]) -> ()
                    | other -> failtestf "expected id/name columns, got %A" other

                testCase "SELECT without FROM returns a single row"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT 1 + 1 AS two" with
                    | ResultSet([ "two" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected a single computed row, got %A" other

                testCase "ORDER BY DESC reverses the sort"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (3), (2)" |> ignore

                    match runDefault store "SELECT n FROM t ORDER BY n DESC" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "3" ]; [ Some "2" ]; [ Some "1" ] ] "descending order"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "LIMIT with OFFSET pages through rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3), (4)" |> ignore

                    match runDefault store "SELECT n FROM t ORDER BY n LIMIT 2 OFFSET 1" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "2" ]; [ Some "3" ] ] "page of two starting at offset 1"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "SELECT * with no FROM is a 1096 error, not a 0-column resultset"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT *" with
                    | Err(1096, _) -> ()
                    | other -> failtestf "expected a 1096 error, got %A" other

                testCase "ORDER BY resolves a SELECT alias, not just a table column"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (3), (1), (2)" |> ignore

                    match runDefault store "SELECT n AS x FROM t ORDER BY x" with
                    | ResultSet([ "x" ], rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ] "sorted by alias"
                    | other -> failtestf "expected a resultset sorted by alias, got %A" other

                testCase "LIKE treats a backslash-escaped %/_ as a literal character, not a wildcard"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT 'axb' LIKE 'a\\%b'" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected no match (escaped %% is literal), got %A" other

                    match runDefault store "SELECT 'a%b' LIKE 'a\\%b'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected a literal %% match, got %A" other

                testCase "LIKE matches across embedded newlines"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT 'line1\nline2' LIKE '%line2%'" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected a match across the newline, got %A" other

                testCase "LIKE does not match past a trailing newline for an unqualified pattern"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT 'ab\n' LIKE 'ab'" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected no match, got %A" other

                testCase "an unknown column in WHERE is a 1054 error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "SELECT n FROM t WHERE ghost = 1" with
                    | Err(1054, msg) -> Expect.stringContains msg "ghost" "message names the column"
                    | other -> failtestf "expected a 1054 error, got %A" other

                testCase "an unknown function is a 1305 error"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT NOPE(1)" with
                    | Err(1305, msg) -> Expect.stringContains msg "NOPE" "message names the function"
                    | other -> failtestf "expected a 1305 error, got %A" other ]

          testList
              "UPDATE / DELETE"
              [ testCase "UPDATE changes only matching rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (name VARCHAR(20), age INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES ('alice', 30), ('bob', 25)" |> ignore

                    match runDefault store "UPDATE users SET age = 31 WHERE name = 'alice'" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other

                    match runDefault store "SELECT name, age FROM users ORDER BY name" with
                    | ResultSet(_, rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "31" ]; [ Some "bob"; Some "25" ] ] "only alice's age changed"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "DELETE removes only matching rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO users VALUES ('alice'), ('bob')" |> ignore

                    match runDefault store "DELETE FROM users WHERE name = 'bob'" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other

                    match runDefault store "SELECT name FROM users" with
                    | ResultSet(_, [ [ Some "alice" ] ]) -> ()
                    | other -> failtestf "expected only alice left, got %A" other ]

          testList
              "functions"
              [ testCase "UPPER, LOWER, CONCAT, and arithmetic compose in one projection"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (name VARCHAR(20), age INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES ('alice', 30)" |> ignore

                    match runDefault store "SELECT UPPER(name), age + 1 FROM users" with
                    | ResultSet(_, [ [ Some "ALICE"; Some "31" ] ]) -> ()
                    | other -> failtestf "expected ALICE/31, got %A" other

                testCase "COALESCE and IFNULL skip past NULLs"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT COALESCE(NULL, NULL, 5), IFNULL(NULL, 7)" with
                    | ResultSet(_, [ [ Some "5"; Some "7" ] ]) -> ()
                    | other -> failtestf "expected 5/7, got %A" other

                testCase "a custom registerScalar function is callable through the same registry"
                <| fun _ ->
                    // Proves the extensibility API end to end: a function
                    // registered exactly the way user code would, resolving
                    // through the same registry the built-ins use.
                    let store = newStore ()

                    let registry =
                        builtins
                        |> registerScalar "SHOUT" (function
                            | [ VString s ] -> VString(s.ToUpperInvariant() + "!")
                            | _ -> VNull)

                    match run store registry "SELECT SHOUT('hello')" with
                    | ResultSet(_, [ [ Some "HELLO!" ] ]) -> ()
                    | other -> failtestf "expected HELLO!, got %A" other

                testCase "ABS and ROUND"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT ABS(-5), ROUND(3.7)" with
                    | ResultSet(_, [ [ Some "5"; Some "4" ] ]) -> ()
                    | other -> failtestf "expected 5/4, got %A" other ] ]
