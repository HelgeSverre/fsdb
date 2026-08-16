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

                testCase "UPDATE reports 0 affected for a no-op write to an already-matching row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, v INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 5)" |> ignore

                    match runDefault store "UPDATE t SET v = v WHERE id = 1" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected 0 rows affected (matched but unchanged), got %A" other

                testCase "UPDATE SET assignments evaluate left-to-right: a later one sees an earlier one's new value"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE s1 (id INT, a INT, b INT)" |> ignore
                    runDefault store "INSERT INTO s1 VALUES (1, 1, 2)" |> ignore

                    match runDefault store "UPDATE s1 SET a = 10, b = a" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other

                    match runDefault store "SELECT a, b FROM s1" with
                    | ResultSet(_, [ [ Some "10"; Some "10" ] ]) -> ()
                    | other -> failtestf "expected b to see a's new value (10), matching MySQL's left-to-right evaluation, got %A" other

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
                    | other -> failtestf "expected only alice left, got %A" other

                testCase "DELETE ... LIMIT n caps how many matching rows are removed"
                <| fun _ ->
                    // `DELETE FROM t WHERE ... LIMIT n` is a batch-cleanup-job
                    // staple, capping how many stale rows one run removes.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3), (4)" |> ignore

                    match runDefault store "DELETE FROM t WHERE n >= 2 LIMIT 2" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected exactly 2 rows deleted, got %A" other

                    match runDefault store "SELECT COUNT(*) AS c FROM t" with
                    | ResultSet([ "c" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected 2 rows left (4 - 2 deleted), got %A" other

                testCase "UPDATE ... ORDER BY ... LIMIT mutates only the first n rows in that order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (3), (1), (4), (2)" |> ignore

                    match runDefault store "UPDATE t SET n = n + 100 ORDER BY n LIMIT 2" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows affected, got %A" other

                    match runDefault store "SELECT n FROM t ORDER BY n" with
                    | ResultSet(_, rows) ->
                        // 1 and 2 were the two lowest, so they're the ones
                        // that got +100'd; 3 and 4 are untouched.
                        Expect.equal rows [ [ Some "3" ]; [ Some "4" ]; [ Some "101" ]; [ Some "102" ] ] "only the two smallest rows changed"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "DELETE ... ORDER BY ... LIMIT removes the first n rows in that order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (3), (1), (4), (2)" |> ignore

                    match runDefault store "DELETE FROM t ORDER BY n DESC LIMIT 2" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows affected, got %A" other

                    match runDefault store "SELECT n FROM t ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "the two largest were removed"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "UPDATE t1 JOIN t2 ON ... SET updates matched rows in both tables"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE accounts (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE balances (account_id INT, cents INT)" |> ignore
                    runDefault store "INSERT INTO accounts VALUES (1, 'old'), (2, 'other')" |> ignore
                    runDefault store "INSERT INTO balances VALUES (1, 100)" |> ignore

                    match
                        runDefault
                            store
                            "UPDATE accounts a JOIN balances b ON a.id = b.account_id SET a.name = 'renamed', b.cents = b.cents + 1 WHERE a.id = 1"
                    with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows affected (one per table), got %A" other

                    match runDefault store "SELECT name FROM accounts WHERE id = 1" with
                    | ResultSet(_, [ [ Some "renamed" ] ]) -> ()
                    | other -> failtestf "expected the account renamed, got %A" other

                    match runDefault store "SELECT cents FROM balances WHERE account_id = 1" with
                    | ResultSet(_, [ [ Some "101" ] ]) -> ()
                    | other -> failtestf "expected the balance incremented, got %A" other

                    match runDefault store "SELECT name FROM accounts WHERE id = 2" with
                    | ResultSet(_, [ [ Some "other" ] ]) -> ()
                    | other -> failtestf "expected the unmatched account untouched, got %A" other

                testCase "UPDATE JOIN updates a physical row at most once even when the join matches it multiple times"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT, hits INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1, 0)" |> ignore
                    // Two t2 rows both join to the same t1 row.
                    runDefault store "INSERT INTO t2 VALUES (1), (1)" |> ignore

                    match runDefault store "UPDATE t1 JOIN t2 ON t1.id = t2.t1_id SET t1.hits = t1.hits + 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected exactly 1 row affected despite two join matches, got %A" other

                    match runDefault store "SELECT hits FROM t1" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected hits incremented exactly once, got %A" other

                testCase "UPDATE self-join through two aliases writes both sides of every matched row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE sj2 (id INT, v INT, w INT, nxt INT)" |> ignore
                    runDefault store "INSERT INTO sj2 VALUES (1, 0, 0, 2), (2, 0, 0, 3), (3, 0, 0, NULL)" |> ignore

                    match runDefault store "UPDATE sj2 a JOIN sj2 b ON a.nxt = b.id SET a.v = 111, b.w = 222" with
                    | Affected _ -> ()
                    | other -> failtestf "expected the statement to succeed, got %A" other

                    match runDefault store "SELECT id, v, w FROM sj2 ORDER BY id" with
                    | ResultSet(_, rows) ->
                        // Two join matches: (a=1,b=2) and (a=2,b=3). Row 2
                        // is reached both as a 'b' (match 1, w=222) and as
                        // an 'a' (match 2, v=111) — both must land, since
                        // `a` and `b` are independent roles even though
                        // they're the same table.
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "111"; Some "0" ]; [ Some "2"; Some "111"; Some "222" ]; [ Some "3"; Some "0"; Some "222" ] ]
                            "row 1: a's v=111 only. row 2: both a's v=111 and b's w=222. row 3: b's w=222 only"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "UPDATE JOIN across tables is statement-atomic: a later table's constraint violation leaves the earlier table untouched"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE p (id INT PRIMARY KEY, x INT)" |> ignore
                    runDefault store "CREATE TABLE q (id INT PRIMARY KEY, pid INT, u INT UNIQUE)" |> ignore
                    runDefault store "INSERT INTO p VALUES (1, 10), (2, 20)" |> ignore
                    runDefault store "INSERT INTO q VALUES (100, 1, 1), (101, 2, 2)" |> ignore

                    match runDefault store "UPDATE p JOIN q ON p.id = q.pid SET p.x = 999, q.u = 7" with
                    | Err(1062, _) -> ()
                    | other -> failtestf "expected a 1062 duplicate-key error (q.u = 7 collides for both matched rows), got %A" other

                    match runDefault store "SELECT x FROM p ORDER BY id" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "10" ]; [ Some "20" ] ] "p's rows must be untouched — the whole statement rolled back"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "UPDATE JOIN SET assignments evaluate left-to-right across tables too"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u1 (id INT, a INT)" |> ignore
                    runDefault store "CREATE TABLE u2 (id INT, b INT)" |> ignore
                    runDefault store "INSERT INTO u1 VALUES (1, 1)" |> ignore
                    runDefault store "INSERT INTO u2 VALUES (1, 0)" |> ignore

                    match runDefault store "UPDATE u1 JOIN u2 ON u1.id = u2.id SET u1.a = 42, u2.b = u1.a" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows affected, got %A" other

                    match runDefault store "SELECT b FROM u2" with
                    | ResultSet(_, [ [ Some "42" ] ]) -> ()
                    | other -> failtestf "expected u2.b to see u1.a's new value (42), got %A" other

                testCase "DELETE t1 FROM t1 JOIN t2 ON ... removes only t1's matched rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT, flag INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1, 1), (2, 0)" |> ignore

                    match runDefault store "DELETE t1 FROM t1 JOIN t2 ON t1.id = t2.t1_id WHERE t2.flag = 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row deleted, got %A" other

                    match runDefault store "SELECT id FROM t1 ORDER BY id" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected only id=2 left, got %A" other

                    match runDefault store "SELECT COUNT(*) AS c FROM t2" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected t2 untouched (not a delete target), got %A" other

                testCase "DELETE FROM t1 USING t1 JOIN t2 ON ... deletes the same way as the named-target form"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT, flag INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1, 1), (2, 0)" |> ignore

                    match runDefault store "DELETE FROM t1 USING t1 JOIN t2 ON t1.id = t2.t1_id WHERE t2.flag = 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row deleted, got %A" other

                    match runDefault store "SELECT id FROM t1 ORDER BY id" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected only id=2 left, got %A" other

                testCase "DELETE t1, t2 FROM t1 JOIN t2 ON ... deletes matched rows from both target tables"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1)" |> ignore

                    match runDefault store "DELETE t1, t2 FROM t1 JOIN t2 ON t1.id = t2.t1_id" with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows deleted total (one per table), got %A" other

                    match runDefault store "SELECT id FROM t1 ORDER BY id" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected t1's matched row gone, got %A" other

                    match runDefault store "SELECT COUNT(*) AS c FROM t2" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected t2 emptied, got %A" other ]

          testList
              "ALTER TABLE ... AFTER / FIRST"
              [ testCase "ADD COLUMN ... FIRST repositions in SELECT * order and ordinal_position"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'x')" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN flag INT FIRST" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "flag"; "id"; "name" ], [ [ None; Some "1"; Some "x" ] ]) -> ()
                    | other -> failtestf "expected flag first, got %A" other

                testCase "ADD COLUMN ... AFTER col repositions in SELECT * order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'x')" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN flag INT AFTER id" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "id"; "flag"; "name" ], [ [ Some "1"; None; Some "x" ] ]) -> ()
                    | other -> failtestf "expected flag right after id, got %A" other

                testCase "CHANGE COLUMN ... FIRST renames, redefines, and repositions in one statement"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(20), age INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 'x', 30)" |> ignore
                    runDefault store "ALTER TABLE t CHANGE age years INT FIRST" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "years"; "id"; "name" ], [ [ Some "30"; Some "1"; Some "x" ] ]) -> ()
                    | other -> failtestf "expected years first with its value moved along, got %A" other

                testCase "ADD COLUMN ... FIRST updates information_schema.columns ordinal_position"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN flag INT FIRST" |> ignore

                    match
                        runDefault
                            store
                            "SELECT column_name FROM information_schema.columns WHERE table_name = 't' ORDER BY ordinal_position"
                    with
                    | ResultSet(_, [ [ Some "flag" ]; [ Some "id" ]; [ Some "name" ] ]) -> ()
                    | other -> failtestf "expected flag/id/name ordinal order, got %A" other ]

          testList
              "EXPLAIN"
              [ testCase "EXPLAIN SELECT with a WHERE and JOIN describes both tables in FROM order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM t1 JOIN t2 ON t1.id = t2.t1_id WHERE t1.id = 1" with
                    | ResultSet(cols,
                                [ [ Some "1"; Some "SIMPLE"; Some "t1"; _; Some "ALL"; _; _; _; _; Some "2"; Some "100.00"; None ]
                                  [ Some "1"; Some "SIMPLE"; Some "t2"; _; Some "system"; _; _; _; _; Some "1"; Some "100.00"; Some extra ] ]) ->
                        Expect.equal
                            cols
                            [ "id"; "select_type"; "table"; "partitions"; "type"; "possible_keys"; "key"; "key_len"; "ref"; "rows"; "filtered"; "Extra" ]
                            "the classic 12 columns"

                        Expect.stringContains extra "Using where" "WHERE noted on the last table"
                    | other -> failtestf "expected a two-row join plan, got %A" other

                testCase "EXPLAIN SELECT with a correlated EXISTS subquery is DEPENDENT SUBQUERY"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1_id INT)" |> ignore

                    match
                        runDefault
                            store
                            "EXPLAIN SELECT * FROM t1 WHERE EXISTS (SELECT 1 FROM t2 WHERE t2.t1_id = t1.id)"
                    with
                    | ResultSet(_, rows) ->
                        let selectTypes = rows |> List.map (fun r -> r.[1])
                        Expect.contains selectTypes (Some "DEPENDENT SUBQUERY") "the correlated subquery is flagged dependent"
                        let ids = rows |> List.map (fun r -> r.[0])
                        Expect.equal (ids |> List.distinct |> List.length) 2 "outer and subquery are different id blocks"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "EXPLAIN SELECT with an uncorrelated subquery is plain SUBQUERY"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (id INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM t1 WHERE id IN (SELECT id FROM t2)" with
                    | ResultSet(_, rows) ->
                        let selectTypes = rows |> List.map (fun r -> r.[1])
                        Expect.contains selectTypes (Some "SUBQUERY") "the uncorrelated subquery is plain SUBQUERY"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "EXPLAIN SELECT with a derived table is DERIVED"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM (SELECT n FROM t) AS d" with
                    | ResultSet(_, rows) ->
                        let selectTypes = rows |> List.map (fun r -> r.[1])
                        Expect.contains selectTypes (Some "DERIVED") "the derived table gets its own DERIVED block"
                        let tables = rows |> List.map (fun r -> r.[2])
                        Expect.contains tables (Some "<derived2>") "the outer row references it by its derived id"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "EXPLAIN SELECT with GROUP BY notes Using temporary"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (g INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT g, COUNT(*) FROM t GROUP BY g" with
                    | ResultSet(_, [ row ]) -> Expect.stringContains (row.[11] |> Option.defaultValue "") "Using temporary" "GROUP BY notes Using temporary"
                    | other -> failtestf "expected one row, got %A" other

                testCase "EXPLAIN SELECT with ORDER BY notes Using filesort"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT n FROM t ORDER BY n" with
                    | ResultSet(_, [ row ]) -> Expect.stringContains (row.[11] |> Option.defaultValue "") "Using filesort" "ORDER BY notes Using filesort"
                    | other -> failtestf "expected one row, got %A" other

                testCase "EXPLAIN on a UNION includes PRIMARY, UNION, and a UNION RESULT row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT n FROM t UNION SELECT n FROM t" with
                    | ResultSet(_, rows) ->
                        let selectTypes = rows |> List.choose (fun r -> r.[1])
                        Expect.contains selectTypes "PRIMARY" "first branch is PRIMARY"
                        Expect.contains selectTypes "UNION" "second branch is UNION"
                        Expect.contains selectTypes "UNION RESULT" "a UNION RESULT row combines them"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "EXPLAIN UPDATE describes the target table with select_type UPDATE"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 1), (2, 2)" |> ignore

                    match runDefault store "EXPLAIN UPDATE t SET n = 1 WHERE id = 1" with
                    | ResultSet(_, [ [ Some "1"; Some "UPDATE"; Some "t"; _; Some "ALL"; _; _; _; _; Some "2"; Some "100.00"; Some extra ] ]) ->
                        Expect.stringContains extra "Using where" "WHERE noted"
                    | other -> failtestf "expected one UPDATE-typed row, got %A" other

                testCase "EXPLAIN DELETE describes the target table with select_type DELETE"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore

                    match runDefault store "EXPLAIN DELETE FROM t WHERE id = 1" with
                    | ResultSet(_, [ [ Some "1"; Some "DELETE"; Some "t"; _; _; _; _; _; _; _; _; _ ] ]) -> ()
                    | other -> failtestf "expected one DELETE-typed row, got %A" other

                testCase "EXPLAIN INSERT works without error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore

                    match runDefault store "EXPLAIN INSERT INTO t VALUES (1)" with
                    | ResultSet(_, [ [ Some "1"; Some "INSERT"; Some "t"; _; _; _; _; _; _; _; _; _ ] ]) -> ()
                    | other -> failtestf "expected one INSERT-typed row, got %A" other

                testCase "EXPLAIN FORMAT=TRADITIONAL parses the same as bare EXPLAIN"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore

                    match runDefault store "EXPLAIN FORMAT=TRADITIONAL SELECT * FROM t" with
                    | ResultSet(_, [ [ Some "1"; Some "SIMPLE"; Some "t"; _; _; _; _; _; _; _; _; _ ] ]) -> ()
                    | other -> failtestf "expected the same shape as bare EXPLAIN, got %A" other

                testCase "EXPLAIN on a 1-row table reports type system"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM t" with
                    | ResultSet(_, [ [ _; _; _; _; Some "system"; _; _; _; _; Some "1"; _; _ ] ]) -> ()
                    | other -> failtestf "expected type=system for a 1-row table, got %A" other

                testCase "EXPLAIN validates the statement it describes: a missing table is 1146, not a fake plan"
                <| fun _ ->
                    let store = newStore ()

                    for sql in
                        [ "EXPLAIN SELECT * FROM nosuchtable"
                          "EXPLAIN UPDATE nosuchtable SET x = 1"
                          "EXPLAIN INSERT INTO nosuchtable VALUES (1)" ] do
                        match runDefault store sql with
                        | Err(1146, _) -> ()
                        | other -> failtestf "expected 1146 for %s, got %A" sql other

                testCase "EXPLAIN on a JOIN against a missing table is 1146"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT * FROM t1 JOIN nosuch2 ON t1.id = nosuch2.id" with
                    | Err(1146, _) -> ()
                    | other -> failtestf "expected 1146, got %A" other

                testCase "EXPLAIN validates unknown columns: 1054, not a fake plan"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore

                    match runDefault store "EXPLAIN SELECT nosuchcol FROM t1" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected 1054, got %A" other

                    match runDefault store "EXPLAIN DELETE FROM t1 WHERE nosuchcol = 1" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected 1054, got %A" other ]

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

                testCase "IF() works even though IF is also a reserved keyword"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT IF(1, 'yes', 'no')" with
                    | ResultSet(_, [ [ Some "yes" ] ]) -> ()
                    | other -> failtestf "expected yes, got %A" other

                testCase "ABS and ROUND"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT ABS(-5), ROUND(3.7)" with
                    | ResultSet(_, [ [ Some "5"; Some "4" ] ]) -> ()
                    | other -> failtestf "expected 5/4, got %A" other ]

          testList
              "three-valued logic"
              [ testCase "WHERE x = NULL matches no rows (NULL never equals anything)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (NULL)" |> ignore

                    match runDefault store "SELECT n FROM t WHERE n = NULL" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected zero rows, got %A" other

                testCase "NULL AND FALSE is FALSE, not NULL (short-circuits to false either way)"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT NULL AND FALSE" with
                    | ResultSet(_, [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected 0, got %A" other

                testCase "NULL AND TRUE is NULL (unknown, neither true nor false)"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT NULL AND TRUE" with
                    | ResultSet(_, [ [ None ] ]) -> ()
                    | other -> failtestf "expected NULL, got %A" other

                testCase "ORDER BY sorts NULLs first"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (2), (NULL), (1)" |> ignore

                    match runDefault store "SELECT n FROM t ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ None ]; [ Some "1" ]; [ Some "2" ] ] "NULL sorts first"
                    | other -> failtestf "expected a resultset, got %A" other ]

          testList
              "LIKE / IN / BETWEEN"
              [ testCase "IN matches any candidate, including a mix of types"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

                    match runDefault store "SELECT n FROM t WHERE n IN (1, 3) ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "3" ] ] "matches 1 and 3 only"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "NOT IN excludes the candidates"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

                    match runDefault store "SELECT n FROM t WHERE n NOT IN (2) ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "3" ] ] "excludes 2"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "BETWEEN is inclusive on both ends"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3), (4)" |> ignore

                    match runDefault store "SELECT n FROM t WHERE n BETWEEN 2 AND 3 ORDER BY n" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "2" ]; [ Some "3" ] ] "includes both endpoints"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "LIKE with % and _ wildcards"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (name VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('alice'), ('bob'), ('alan')" |> ignore

                    match runDefault store "SELECT name FROM t WHERE name LIKE 'al_ce' ORDER BY name" with
                    | ResultSet(_, [ [ Some "alice" ] ]) -> ()
                    | other -> failtestf "expected only alice, got %A" other

                    match runDefault store "SELECT name FROM t WHERE name LIKE 'a%' ORDER BY name" with
                    | ResultSet(_, rows) -> Expect.equal rows [ [ Some "alan" ]; [ Some "alice" ] ] "both a-names"
                    | other -> failtestf "expected a resultset, got %A" other ]

          testList
              "storage errors reaching QueryResult"
              [ testCase "INSERT into an unknown table is a 1146 error"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "INSERT INTO ghost VALUES (1)" with
                    | Err(1146, _) -> ()
                    | other -> failtestf "expected a 1146 error, got %A" other

                testCase "omitting a NOT NULL column with no default is a 1048 error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, name VARCHAR(10) NOT NULL)" |> ignore

                    match runDefault store "INSERT INTO t (id) VALUES (1)" with
                    | Err(1048, _) -> ()
                    | other -> failtestf "expected a 1048 error, got %A" other

                testCase "an uncoercible value for a column's type is a 1366 error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('not a number')" with
                    | Err(1366, _) -> ()
                    | other -> failtestf "expected a 1366 error, got %A" other ]

          testList
              "ALTER TABLE / RENAME TABLE / CREATE INDEX / DROP INDEX end to end"
              [ testCase "ADD COLUMN then SELECT sees the new column with default values on old rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1)" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN active INT DEFAULT 1" |> ignore

                    match runDefault store "SELECT id, active FROM t" with
                    | ResultSet([ "id"; "active" ], [ [ Some "1"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected the new column filled with its default, got %A" other

                testCase "DROP COLUMN then SELECT * no longer sees it"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, junk INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1, 2)" |> ignore
                    runDefault store "ALTER TABLE t DROP COLUMN junk" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "id" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected only id left, got %A" other

                testCase "CHANGE COLUMN renames and SELECT sees the new name"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (old_name VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('x')" |> ignore
                    runDefault store "ALTER TABLE t CHANGE old_name new_name VARCHAR(20)" |> ignore

                    match runDefault store "SELECT new_name FROM t" with
                    | ResultSet([ "new_name" ], [ [ Some "x" ] ]) -> ()
                    | other -> failtestf "expected the renamed column, got %A" other

                testCase "ALTER TABLE ... RENAME TO makes the table reachable under the new name only"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore
                    runDefault store "ALTER TABLE t RENAME TO u" |> ignore

                    match runDefault store "SELECT * FROM u" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected the renamed table to be queryable, got %A" other

                    match runDefault store "SELECT * FROM t" with
                    | Err(1146, _) -> ()
                    | other -> failtestf "expected the old name to be gone, got %A" other

                testCase "RENAME TABLE a TO b"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (id INT)" |> ignore

                    match runDefault store "RENAME TABLE a TO b" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected Affected 0, got %A" other

                    match runDefault store "SELECT * FROM b" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected b to exist, got %A" other

                testCase "CREATE INDEX / DROP INDEX round-trip without error"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (a INT)" |> ignore

                    match runDefault store "CREATE INDEX idx_a ON t (a)" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected Affected 0, got %A" other

                    match runDefault store "DROP INDEX idx_a ON t" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected Affected 0, got %A" other

                testCase "several ALTER TABLE actions comma-separated in one statement all apply"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT, junk INT)" |> ignore
                    runDefault store "ALTER TABLE t ADD COLUMN extra INT, DROP COLUMN junk" |> ignore

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet([ "id"; "extra" ], _) -> ()
                    | other -> failtestf "expected both actions applied, got %A" other ]

          testList
              "INSERT ... ON DUPLICATE KEY UPDATE / INSERT IGNORE"
              [ testCase "no collision inserts a fresh row"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT)" |> ignore

                    match runDefault store "INSERT INTO t (id, n) VALUES (1, 10) ON DUPLICATE KEY UPDATE n = n + 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row inserted, got %A" other

                    match runDefault store "SELECT n FROM t WHERE id = 1" with
                    | ResultSet(_, [ [ Some "10" ] ]) -> ()
                    | other -> failtestf "expected n = 10, got %A" other

                testCase "a primary key collision runs the UPDATE clause instead of erroring"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT)" |> ignore
                    runDefault store "INSERT INTO t (id, n) VALUES (1, 10)" |> ignore

                    runDefault store "INSERT INTO t (id, n) VALUES (1, 999) ON DUPLICATE KEY UPDATE n = n + 1"
                    |> ignore

                    match runDefault store "SELECT n FROM t WHERE id = 1" with
                    | ResultSet(_, [ [ Some "11" ] ]) -> ()
                    | other -> failtestf "expected n incremented from the existing row, not overwritten by 999, got %A" other

                testCase "VALUES(col) inside ON DUPLICATE KEY UPDATE resolves to the incoming row's value"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT)" |> ignore
                    runDefault store "INSERT INTO t (id, n) VALUES (1, 10)" |> ignore

                    runDefault store "INSERT INTO t (id, n) VALUES (1, 999) ON DUPLICATE KEY UPDATE n = VALUES(n)"
                    |> ignore

                    match runDefault store "SELECT n FROM t WHERE id = 1" with
                    | ResultSet(_, [ [ Some "999" ] ]) -> ()
                    | other -> failtestf "expected n replaced with the incoming value 999, got %A" other

                testCase "a unique-index collision (not the primary key) also triggers the UPDATE clause"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, email VARCHAR(255) UNIQUE, hits INT)" |> ignore
                    runDefault store "INSERT INTO t (id, email, hits) VALUES (1, 'a@x.com', 1)" |> ignore

                    runDefault
                        store
                        "INSERT INTO t (id, email, hits) VALUES (2, 'a@x.com', 1) ON DUPLICATE KEY UPDATE hits = hits + 1"
                    |> ignore

                    match runDefault store "SELECT id, hits FROM t" with
                    | ResultSet(_, [ [ Some "1"; Some "2" ] ]) -> ()
                    | other -> failtestf "expected the existing row bumped, not a second row, got %A" other

                testCase "a collision on a unique index over a VIRTUAL generated column runs the UPDATE clause, not error 1062"
                <| fun _ ->
                    let store = newStore ()

                    runDefault store "CREATE TABLE t (id INT PRIMARY KEY, k VARCHAR(50), k_hash VARCHAR(64) AS (MD5(k)), UNIQUE KEY uq_hash (k_hash))"
                    |> ignore

                    runDefault store "INSERT INTO t (id, k) VALUES (1, 'x') ON DUPLICATE KEY UPDATE id = id"
                    |> ignore

                    match runDefault store "INSERT INTO t (id, k) VALUES (2, 'x') ON DUPLICATE KEY UPDATE id = id" with
                    | Affected _ -> ()
                    | other -> failtestf "expected the ODKU clause to run instead of a 1062 error, got %A" other

                    match runDefault store "SELECT COUNT(*) FROM t" with
                    | ResultSet(_, [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected one row (updated, not duplicated), got %A" other

                testCase "INSERT IGNORE parses and executes like a plain INSERT"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "INSERT IGNORE INTO t VALUES (1)" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other ]

          testList
              "INSERT ... SELECT"
              [ testCase "inserts every row a SELECT (with WHERE/GROUP BY/aggregates) produces"
                <| fun _ ->
                    // `INSERT INTO t (cols) SELECT ...` is a Laravel
                    // reporting-job staple: roll a detail table up into a
                    // daily summary table.
                    let store = newStore ()
                    runDefault store "CREATE TABLE sales (region VARCHAR(10), amount INT)" |> ignore
                    runDefault store "CREATE TABLE region_totals (region VARCHAR(10), total INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO sales VALUES ('east', 10), ('east', 20), ('west', 5)"
                    |> ignore

                    match
                        runDefault
                            store
                            "INSERT INTO region_totals (region, total) SELECT region, SUM(amount) FROM sales GROUP BY region ORDER BY region"
                    with
                    | Affected 2UL -> ()
                    | other -> failtestf "expected 2 rows inserted, got %A" other

                    match runDefault store "SELECT region, total FROM region_totals ORDER BY region" with
                    | ResultSet([ "region"; "total" ], rows) ->
                        Expect.equal rows [ [ Some "east"; Some "30" ]; [ Some "west"; Some "5" ] ] "one summary row per region"
                    | other -> failtestf "expected the grouped totals, got %A" other

                testCase "INSERT IGNORE ... SELECT skips rows that violate a unique constraint instead of failing"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE src (id INT)" |> ignore
                    runDefault store "CREATE TABLE dst (id INT UNIQUE)" |> ignore
                    runDefault store "INSERT INTO src VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO dst VALUES (1)" |> ignore

                    match runDefault store "INSERT IGNORE INTO dst (id) SELECT id FROM src ORDER BY id" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected only id=2 inserted (id=1 collides), got %A" other ]

          testList
              "CAST and new column types"
              [ testCase "CAST(x AS UNSIGNED)/CAST(x AS SIGNED) coerce to an integer"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT CAST('42' AS UNSIGNED), CAST(3.9 AS SIGNED)" with
                    | ResultSet(_, [ [ Some "42"; Some "3" ] ]) -> ()
                    | other -> failtestf "expected 42/3, got %A" other

                testCase "CAST(x AS CHAR) stringifies"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT CAST(42 AS CHAR)" with
                    | ResultSet(_, [ [ Some "42" ] ]) -> ()
                    | other -> failtestf "expected '42', got %A" other

                testCase "CAST never raises 1366, even under the session's default strict sql_mode"
                <| fun _ ->
                    // Oracle (real MySQL 8, default sql_mode): CAST('abc' AS
                    // SIGNED) = 0, CAST('12abc' AS SIGNED) = 12 (the leading
                    // numeric run, not the non-strict-fallback 0),
                    // CAST('abc' AS DATE) = NULL — all three with only a
                    // truncation *warning*, never error 1366.
                    let store = newStore ()

                    match runDefault store "SELECT CAST('abc' AS SIGNED), CAST('12abc' AS SIGNED), CAST('abc' AS DATE)" with
                    | ResultSet(_, [ [ Some "0"; Some "12"; None ] ]) -> ()
                    | other -> failtestf "expected 0, 12, NULL, got %A" other

                testCase "CAST(x AS SIGNED) stops at the exponent, unlike a DECIMAL/float target"
                <| fun _ ->
                    // Oracle: CAST('1e3' AS SIGNED) = 1 (string-to-integer
                    // conversion stops at the first non-digit); CAST('1e3' AS
                    // DECIMAL(10,2)) = 1000 (the float grammar applies).
                    let store = newStore ()

                    match runDefault store "SELECT CAST('1e3' AS SIGNED), CAST('1e3' AS DECIMAL(10,2))" with
                    | ResultSet(_, [ [ Some "1"; Some "1000" ] ]) -> ()
                    | other -> failtestf "expected 1, 1000, got %A" other

                testCase "ENUM column accepts a listed value and rejects one outside the set"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (status ENUM('open', 'closed'))" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('open')" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the valid enum value to insert, got %A" other

                    match runDefault store "INSERT INTO t VALUES ('bogus')" with
                    | Err(1366, _) -> ()
                    | other -> failtestf "expected a 1366 error for an unlisted enum value, got %A" other

                testCase "SET column accepts any string without validating against the declared set"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (flags SET('a', 'b'))" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('a,b')" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected SET to accept any string, got %A" other

                testCase "the full new column-type surface creates and round-trips through INSERT/SELECT"
                <| fun _ ->
                    let store = newStore ()

                    runDefault
                        store
                        "CREATE TABLE t (a CHAR(3), b TINYTEXT, c BLOB, d SMALLINT, e MEDIUMINT UNSIGNED, f TIME, g YEAR, h FLOAT, i BOOLEAN)"
                    |> ignore

                    match runDefault store "INSERT INTO t VALUES ('x', 'y', 'z', 1, 2, '10:00:00', 2024, 1.5, 1)" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the row to insert, got %A" other

                    match runDefault store "SELECT * FROM t" with
                    | ResultSet(_, [ row ]) -> Expect.equal (List.length row) 9 "every column round-trips"
                    | other -> failtestf "expected one row back, got %A" other ]

          testList
              "CREATE/DROP DATABASE and db.table-qualified names"
              [ testCase "CREATE DATABASE then a qualified CREATE TABLE/INSERT/SELECT round-trip"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "CREATE DATABASE app" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected CREATE DATABASE to succeed, got %A" other

                    match runDefault store "CREATE DATABASE app" with
                    | Err(1007, _) -> ()
                    | other -> failtestf "expected 1007 for a duplicate CREATE DATABASE, got %A" other

                    runDefault store "CREATE TABLE app.widgets (id INT, name VARCHAR(20))" |> ignore

                    match runDefault store "INSERT INTO app.widgets VALUES (1, 'cog')" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the qualified INSERT to affect one row, got %A" other

                    match runDefault store "SELECT name FROM app.widgets WHERE id = 1" with
                    | ResultSet(_, [ [ Some "cog" ] ]) -> ()
                    | other -> failtestf "expected the qualified SELECT to find the row, got %A" other

                    match runDefault store "UPDATE app.widgets SET name = 'gear' WHERE id = 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the qualified UPDATE to affect one row, got %A" other

                    match runDefault store "DELETE FROM app.widgets WHERE id = 1" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the qualified DELETE to affect one row, got %A" other

                testCase "DROP DATABASE removes it; IF EXISTS/IF NOT EXISTS are no-ops"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE DATABASE IF NOT EXISTS app" |> ignore

                    match runDefault store "DROP DATABASE app" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected DROP DATABASE to succeed, got %A" other

                    match runDefault store "DROP DATABASE app" with
                    | Err(1049, _) -> ()
                    | other -> failtestf "expected 1049 for a missing DROP DATABASE, got %A" other

                    match runDefault store "DROP DATABASE IF EXISTS app" with
                    | Affected 0UL -> ()
                    | other -> failtestf "expected DROP DATABASE IF EXISTS to be a no-op, got %A" other ]

          testList
              "EXISTS (subquery)"
              [ testCase "EXISTS is true when the subquery has rows, false when it doesn't"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (id INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1)" |> ignore

                    match runDefault store "SELECT EXISTS (SELECT 1 FROM t WHERE id = 1) AS `exists`" with
                    | ResultSet([ "exists" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected exists=1, got %A" other

                    match runDefault store "SELECT EXISTS (SELECT 1 FROM t WHERE id = 2) AS `exists`" with
                    | ResultSet([ "exists" ], [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected exists=0, got %A" other

                testCase "EXISTS reaches information_schema, the shape Laravel's hasTable() uses"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE widgets (id INT)" |> ignore

                    let sql =
                        "select exists (select 1 from information_schema.tables where table_schema = 'fsdb' "
                        + "and table_name = 'widgets' and table_type in ('BASE TABLE', 'SYSTEM VERSIONED')) as `exists`"

                    match runDefault store sql with
                    | ResultSet([ "exists" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected exists=1, got %A" other ]

          testList
              "ungrouped aggregates (COUNT/SUM/AVG/MIN/MAX, no GROUP BY)"
              [ testCase "MAX(batch) is what Laravel's migration repository runs to pick the next batch number"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE migrations (id INT, batch INT)" |> ignore
                    runDefault store "INSERT INTO migrations VALUES (1, 1), (2, 1), (3, 2)" |> ignore

                    match runDefault store "SELECT MAX(batch) AS aggregate FROM migrations" with
                    | ResultSet([ "aggregate" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected aggregate=2, got %A" other

                testCase "COUNT(*)/SUM/AVG/MIN over an empty table and a NULL-containing one"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "SELECT COUNT(*) AS c, SUM(n) AS s, MIN(n) AS mn FROM t" with
                    | ResultSet([ "c"; "s"; "mn" ], [ [ Some "0"; None; None ] ]) -> ()
                    | other -> failtestf "expected count=0 and sum/min NULL on an empty table, got %A" other

                    runDefault store "INSERT INTO t VALUES (10), (NULL), (20)" |> ignore

                    match runDefault store "SELECT COUNT(*) AS c, COUNT(n) AS cn, SUM(n) AS s, AVG(n) AS a FROM t" with
                    | ResultSet([ "c"; "cn"; "s"; "a" ], [ [ Some "3"; Some "2"; Some "30"; Some "15" ] ]) -> ()
                    | other -> failtestf "expected NULLs to drop out of COUNT(n)/SUM/AVG, got %A" other

                testCase "an aggregate nested inside an expression is detected and evaluated, not just a bare top-level call"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (10), (20)" |> ignore

                    match runDefault store "SELECT COUNT(*) + 1 AS c FROM t" with
                    | ResultSet([ "c" ], [ [ Some "3" ] ]) -> ()
                    | other -> failtestf "expected COUNT(*) + 1 = 3, got %A" other

                    match runDefault store "SELECT ROUND(AVG(n), 1) AS a FROM t" with
                    | ResultSet([ "a" ], [ [ Some "15" ] ]) -> ()
                    | other -> failtestf "expected a scalar function wrapping an aggregate to work, got %A" other ]

          testList
              "table-qualified columns are checked against the FROM's alias-or-table, not silently accepted from anywhere"
              [ testCase "a qualifier that doesn't match the table in scope is a 1054 unknown-column error, not a resultset"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (id INT)" |> ignore
                    runDefault store "INSERT INTO u VALUES (1)" |> ignore

                    match runDefault store "SELECT p.id FROM u" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected a 1054 unknown-column error, got %A" other

                testCase "a qualifier matching the table's alias resolves correctly"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (id INT)" |> ignore
                    runDefault store "INSERT INTO u VALUES (1)" |> ignore

                    match runDefault store "SELECT x.id FROM u AS x" with
                    | ResultSet([ "id" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected the alias-qualified column to resolve, got %A" other

                testCase "a qualifier matching the bare table name still resolves once it's aliased away"
                <| fun _ ->
                    // Once a table has an alias, real MySQL only accepts the
                    // alias as the qualifier, not the original table name —
                    // `u.id` must be unknown the same way `p.id` is.
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (id INT)" |> ignore
                    runDefault store "INSERT INTO u VALUES (1)" |> ignore

                    match runDefault store "SELECT u.id FROM u AS x" with
                    | Err(1054, _) -> ()
                    | other -> failtestf "expected the pre-alias table name to no longer resolve, got %A" other ]

          testList
              "SELECT DISTINCT"
              [ testCase "SELECT DISTINCT dedupes on the projected columns, preserving first-occurrence order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (name VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO u VALUES ('bob'), ('alice'), ('bob'), ('alice'), ('carol')" |> ignore

                    match runDefault store "SELECT DISTINCT name FROM u ORDER BY name" with
                    | ResultSet([ "name" ], rows) ->
                        Expect.equal rows [ [ Some "alice" ]; [ Some "bob" ]; [ Some "carol" ] ] "three distinct names, sorted"
                    | other -> failtestf "expected a deduped resultset, got %A" other

                testCase "SELECT DISTINCT applies LIMIT to the deduped set, not the raw row count"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (name VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO u VALUES ('a'), ('a'), ('a'), ('b')" |> ignore

                    match runDefault store "SELECT DISTINCT name FROM u ORDER BY name LIMIT 1" with
                    | ResultSet([ "name" ], [ [ Some "a" ] ]) -> ()
                    | other -> failtestf "expected LIMIT 1 to keep only the first distinct row, got %A" other ]

          testList
              "JOIN"
              [ testCase "INNER JOIN matches rows across two aliased instances of the same table"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE crud (id INT, name VARCHAR(10), qty INT)" |> ignore
                    runDefault store "INSERT INTO crud VALUES (1, 'widget', 5), (2, 'gadget', 9)" |> ignore

                    match runDefault store "SELECT c.name, d.qty FROM crud AS c INNER JOIN crud AS d ON c.id = d.id ORDER BY c.id" with
                    | ResultSet([ "name"; "qty" ], rows) ->
                        Expect.equal rows [ [ Some "widget"; Some "5" ]; [ Some "gadget"; Some "9" ] ] "each row joined to itself"
                    | other -> failtestf "expected a joined resultset, got %A" other

                testCase "INNER JOIN between two different tables, and it drops unmatched rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT, title VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    // bob (user_id 2) has no post; alice has two.
                    runDefault store "INSERT INTO posts VALUES (1, 1, 'first'), (2, 1, 'second')" |> ignore

                    match runDefault store "SELECT users.name, posts.title FROM users JOIN posts ON users.id = posts.user_id ORDER BY posts.id" with
                    | ResultSet([ "name"; "title" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "first" ]; [ Some "alice"; Some "second" ] ] "only alice's matching posts"
                    | other -> failtestf "expected a joined resultset, got %A" other

                testCase "LEFT JOIN keeps an unmatched left row, padding the right side with NULL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT, title VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO posts VALUES (1, 1, 'first')" |> ignore

                    match runDefault store "SELECT users.name, posts.title FROM users LEFT JOIN posts ON users.id = posts.user_id ORDER BY users.id" with
                    | ResultSet([ "name"; "title" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "first" ]; [ Some "bob"; None ] ] "bob survives with a NULL title"
                    | other -> failtestf "expected a joined resultset, got %A" other

                testCase "RIGHT JOIN keeps an unmatched right row, padding the left side with NULL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT, title VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice')" |> ignore
                    // post 2's user_id (99) matches no user.
                    runDefault store "INSERT INTO posts VALUES (1, 1, 'first'), (2, 99, 'orphan')" |> ignore

                    match runDefault store "SELECT users.name, posts.title FROM users RIGHT JOIN posts ON users.id = posts.user_id ORDER BY posts.id" with
                    | ResultSet([ "name"; "title" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "first" ]; [ None; Some "orphan" ] ] "the orphaned post survives with a NULL name"
                    | other -> failtestf "expected a joined resultset, got %A" other

                testCase "CROSS JOIN produces the full Cartesian product"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (x INT)" |> ignore
                    runDefault store "CREATE TABLE b (y INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10), (20)" |> ignore

                    match runDefault store "SELECT x, y FROM a CROSS JOIN b ORDER BY x, y" with
                    | ResultSet([ "x"; "y" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "10" ]; [ Some "1"; Some "20" ]; [ Some "2"; Some "10" ]; [ Some "2"; Some "20" ] ]
                            "every combination"
                    | other -> failtestf "expected a 2x2 Cartesian product, got %A" other

                testCase "FROM t1, t2 (comma/implicit join) works the same as an explicit CROSS JOIN"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (x INT)" |> ignore
                    runDefault store "CREATE TABLE b (y INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO b VALUES (10), (20)" |> ignore

                    match runDefault store "SELECT x, y FROM a, b ORDER BY x, y" with
                    | ResultSet([ "x"; "y" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "10" ]; [ Some "1"; Some "20" ]; [ Some "2"; Some "10" ]; [ Some "2"; Some "20" ] ]
                            "every combination, same as CROSS JOIN"
                    | other -> failtestf "expected a 2x2 Cartesian product, got %A" other

                testCase "UPDATE t1, t2 SET ... WHERE ... (comma join) updates matched rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT, x INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1id INT, y INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1, 0)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1, 5)" |> ignore

                    match runDefault store "UPDATE t1, t2 SET t1.x = t2.y WHERE t1.id = t2.t1id" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row affected, got %A" other

                    match runDefault store "SELECT x FROM t1" with
                    | ResultSet(_, [ [ Some "5" ] ]) -> ()
                    | other -> failtestf "expected t1.x set from t2.y, got %A" other

                testCase "DELETE t1 FROM t1, t2 WHERE ... (comma join) deletes matched rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT)" |> ignore
                    runDefault store "CREATE TABLE t2 (t1id INT, y INT)" |> ignore
                    runDefault store "INSERT INTO t1 VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO t2 VALUES (1, 7)" |> ignore

                    match runDefault store "DELETE t1 FROM t1, t2 WHERE t1.id = t2.t1id AND t2.y > 6" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected 1 row deleted, got %A" other

                    match runDefault store "SELECT id FROM t1 ORDER BY id" with
                    | ResultSet(_, [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected only id=2 left, got %A" other

                testCase "qualified t.* in a JOIN expands only that table's own columns, not every joined column"
                <| fun _ ->
                    // This is the shape of a Laravel `belongsToMany` pivot
                    // query: `SELECT teams.*, team_user.role AS pivot_role
                    // FROM teams JOIN team_user ...`.
                    let store = newStore ()
                    runDefault store "CREATE TABLE teams (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE team_user (id INT, team_id INT, role VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO teams VALUES (1, 'acme')" |> ignore
                    // team_user.id (99) differs from teams.id (1), so a
                    // qualifier-dropping expansion is visible in the result.
                    runDefault store "INSERT INTO team_user VALUES (99, 1, 'admin')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT teams.*, team_user.role AS pivot_role FROM teams JOIN team_user ON teams.id = team_user.team_id"
                    with
                    | ResultSet([ "id"; "name"; "pivot_role" ], [ [ Some "1"; Some "acme"; Some "admin" ] ]) -> ()
                    | other -> failtestf "expected teams' own id (1), not team_user's (99), got %A" other

                testCase "an unqualified column present in two joined tables is error 1052, not a silent pick"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE u (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE p (id INT, uid INT, title VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO u VALUES (1, 'alice')" |> ignore
                    runDefault store "INSERT INTO p VALUES (10, 1, 'first')" |> ignore

                    match runDefault store "SELECT id FROM u JOIN p ON p.uid = u.id" with
                    | Err(1052, _) -> ()
                    | other -> failtestf "expected error 1052 (ambiguous column), got %A" other

                testCase "ORDER BY resolves a bare name against SELECT's output columns before FROM-tables (Laravel's belongsToMany-through shape)"
                <| fun _ ->
                    // Both `chat_sessions` and `chatbots` have `created_at`,
                    // but the projection only outputs one of them (via
                    // `chat_sessions.*`) — real MySQL resolves the bare
                    // `ORDER BY created_at` against that single output
                    // column, not against the two ambiguous FROM-table ones
                    // (verified against a live MySQL 8 instance).
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1, '2020-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, '2021-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (2, 1, '2019-01-01')" |> ignore

                    match
                        runDefault
                            store
                            "select chat_sessions.*, chatbots.team_id as laravel_through_key from chat_sessions inner join chatbots on chatbots.id = chat_sessions.chatbot_id where chatbots.team_id = 1 order by created_at desc limit 5"
                    with
                    | ResultSet([ "id"; "chatbot_id"; "created_at"; "laravel_through_key" ], rows) ->
                        Expect.equal
                            (rows |> List.map (fun r -> r.[0]))
                            [ Some "1"; Some "2" ]
                            "expected chat_sessions sorted by its own created_at, descending"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "SELECT * FROM two joined tables, ORDER BY a column both have, is error 1052 in the order clause"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1, '2020-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, '2021-01-01')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT * FROM chat_sessions INNER JOIN chatbots ON chatbots.id = chat_sessions.chatbot_id ORDER BY created_at"
                    with
                    | Err(1052, msg) -> Expect.stringContains msg "order clause" "expected the order-clause wording"
                    | other -> failtestf "expected error 1052 (ambiguous ORDER BY), got %A" other

                testCase "ORDER BY a name that's a duplicate SELECT alias is error 1052 in the order clause"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1, '2020-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, '2021-01-01')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT chat_sessions.created_at AS x, chatbots.created_at AS x FROM chat_sessions JOIN chatbots ON chatbots.id = chat_sessions.chatbot_id ORDER BY x"
                    with
                    | Err(1052, msg) -> Expect.stringContains msg "order clause" "expected the order-clause wording"
                    | other -> failtestf "expected error 1052 (ambiguous duplicate alias), got %A" other

                testCase "WHERE a bare ambiguous column is error 1052 in the where clause"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1, '2020-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, '2021-01-01')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT chat_sessions.* FROM chat_sessions INNER JOIN chatbots ON chatbots.id = chat_sessions.chatbot_id WHERE created_at > '2020-01-01'"
                    with
                    | Err(1052, msg) -> Expect.stringContains msg "where clause" "expected the where-clause wording"
                    | other -> failtestf "expected error 1052 (ambiguous WHERE), got %A" other

                testCase "ORDER BY a name absent from the output but unique across FROM-tables still resolves"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT)" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1)" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (2, 1, 'b')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, 'a')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT chat_sessions.id FROM chat_sessions JOIN chatbots ON chatbots.id = chat_sessions.chatbot_id ORDER BY chatbot_id"
                    with
                    | ResultSet([ "id" ], rows) -> Expect.equal rows [ [ Some "2" ]; [ Some "1" ] ] "expected both rows, chatbot_id ties broken by (stable) scan/insertion order"
                    | other -> failtestf "expected a resultset, got %A" other

                testCase "ORDER BY a name absent from the output and ambiguous across FROM-tables is error 1052"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE chat_sessions (id INT, chatbot_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE chatbots (id INT, team_id INT, created_at VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO chatbots VALUES (1, 1, '2020-01-01')" |> ignore
                    runDefault store "INSERT INTO chat_sessions VALUES (1, 1, '2021-01-01')" |> ignore

                    match
                        runDefault
                            store
                            "SELECT chat_sessions.id FROM chat_sessions JOIN chatbots ON chatbots.id = chat_sessions.chatbot_id ORDER BY created_at"
                    with
                    | Err(1052, msg) -> Expect.stringContains msg "order clause" "expected the order-clause wording"
                    | other -> failtestf "expected error 1052 (ambiguous ORDER BY fallback), got %A" other ]

          testList
              "real GROUP BY / HAVING / grouped aggregates"
              [ testCase "GROUP BY groups rows and aggregates per group"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE sales (region VARCHAR(10), amount INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO sales VALUES ('east', 10), ('east', 20), ('west', 5), ('west', 15), ('west', 25)"
                    |> ignore

                    match runDefault store "SELECT region, SUM(amount) AS total, COUNT(*) AS n FROM sales GROUP BY region ORDER BY region" with
                    | ResultSet([ "region"; "total"; "n" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "east"; Some "30"; Some "2" ]; [ Some "west"; Some "45"; Some "3" ] ]
                            "one row per region, aggregated over just that region's rows"
                    | other -> failtestf "expected two grouped rows, got %A" other

                testCase "HAVING filters grouped rows, including referencing an aggregate not in the SELECT list"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE sales (region VARCHAR(10), amount INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO sales VALUES ('east', 10), ('east', 20), ('west', 5), ('south', 100)"
                    |> ignore

                    match runDefault store "SELECT region FROM sales GROUP BY region HAVING COUNT(*) > 1 ORDER BY region" with
                    | ResultSet([ "region" ], rows) -> Expect.equal rows [ [ Some "east" ] ] "only east has more than one row"
                    | other -> failtestf "expected only 'east' to survive HAVING, got %A" other

                testCase "HAVING can reference a SELECT list alias, not just a bare aggregate call"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE sales (region VARCHAR(10), amount INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO sales VALUES ('east', 10), ('east', 20), ('west', 5), ('south', 100)"
                    |> ignore

                    match runDefault store "SELECT region, COUNT(*) AS c FROM sales GROUP BY region HAVING c > 1 ORDER BY region" with
                    | ResultSet([ "region"; "c" ], rows) -> Expect.equal rows [ [ Some "east"; Some "2" ] ] "only east has c > 1"
                    | other -> failtestf "expected the alias 'c' to resolve inside HAVING, got %A" other

                testCase "GROUP BY with a NULL-valued key groups every NULL together, same as MySQL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (NULL, 1), (NULL, 2), ('a', 3)" |> ignore

                    match runDefault store "SELECT grp, COUNT(*) AS c FROM t GROUP BY grp ORDER BY grp" with
                    | ResultSet([ "grp"; "c" ], rows) ->
                        Expect.equal rows [ [ None; Some "2" ]; [ Some "a"; Some "1" ] ] "both NULL rows land in one group"
                    | other -> failtestf "expected NULLs to group together, got %A" other

                testCase "COUNT ignores NULL, SUM of an all-NULL group is NULL, AVG propagates NULL the same way"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', NULL), ('a', NULL), ('b', 10), ('b', NULL)" |> ignore

                    match runDefault store "SELECT grp, COUNT(n) AS cn, SUM(n) AS s, AVG(n) AS a FROM t GROUP BY grp ORDER BY grp" with
                    | ResultSet([ "grp"; "cn"; "s"; "a" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "a"; Some "0"; None; None ]; [ Some "b"; Some "1"; Some "10"; Some "10" ] ]
                            "group 'a' is all-NULL (COUNT 0, SUM/AVG NULL), group 'b' has one real value"
                    | other -> failtestf "expected NULL-aware aggregates per group, got %A" other

                testCase "a bare non-aggregated column picks the first row of its group (ANY_VALUE-style, ONLY_FULL_GROUP_BY off)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), tag VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 'first'), ('a', 'second')" |> ignore

                    match runDefault store "SELECT grp, tag FROM t GROUP BY grp" with
                    | ResultSet([ "grp"; "tag" ], [ [ Some "a"; Some "first" ] ]) -> ()
                    | other -> failtestf "expected the first row's tag, got %A" other

                testCase "GROUP BY a positional projection reference and an alias"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 1), ('a', 2), ('b', 3)" |> ignore

                    match runDefault store "SELECT grp AS g, COUNT(*) AS c FROM t GROUP BY 1 ORDER BY g" with
                    | ResultSet([ "g"; "c" ], rows) -> Expect.equal rows [ [ Some "a"; Some "2" ]; [ Some "b"; Some "1" ] ] "GROUP BY 1"
                    | other -> failtestf "expected GROUP BY 1 to group by the first projection, got %A" other

                    match runDefault store "SELECT grp AS g, COUNT(*) AS c FROM t GROUP BY g ORDER BY g" with
                    | ResultSet([ "g"; "c" ], rows) -> Expect.equal rows [ [ Some "a"; Some "2" ]; [ Some "b"; Some "1" ] ] "GROUP BY alias"
                    | other -> failtestf "expected GROUP BY alias to group by the aliased projection, got %A" other

                testCase "GROUP BY with no ORDER BY sorts by the group key ascending, matching MySQL's indexed-grouping default"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (code VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('GB'), ('NO'), ('DE'), ('NL')" |> ignore

                    match runDefault store "SELECT code, COUNT(*) AS c FROM t GROUP BY code" with
                    | ResultSet([ "code"; "c" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "DE"; Some "1" ]; [ Some "GB"; Some "1" ]; [ Some "NL"; Some "1" ]; [ Some "NO"; Some "1" ] ]
                            "grouped rows come back sorted by code, not insertion order"
                    | other -> failtestf "expected code-sorted groups, got %A" other

                testCase "ORDER BY an aggregate not in the SELECT list, over a grouped query"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 1), ('b', 1), ('b', 2), ('b', 3)" |> ignore

                    match runDefault store "SELECT grp FROM t GROUP BY grp ORDER BY COUNT(*) DESC" with
                    | ResultSet([ "grp" ], rows) -> Expect.equal rows [ [ Some "b" ]; [ Some "a" ] ] "'b' has more rows, sorts first descending"
                    | other -> failtestf "expected ORDER BY COUNT(*) DESC to work, got %A" other

                testCase "COUNT(DISTINCT x) counts only distinct non-NULL values"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 1), ('a', 1), ('a', 2), ('a', NULL)" |> ignore

                    match runDefault store "SELECT COUNT(DISTINCT n) AS c FROM t GROUP BY grp" with
                    | ResultSet([ "c" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected COUNT(DISTINCT n) = 2, got %A" other

                testCase "COUNT(DISTINCT a, b) counts distinct tuples, dropping any tuple with a NULL"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (g VARCHAR(10), n INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO t VALUES ('a', 1), ('a', NULL), ('b', 3), ('b', 3), (NULL, 5)"
                    |> ignore

                    match runDefault store "SELECT COUNT(DISTINCT g, n) AS c FROM t" with
                    | ResultSet([ "c" ], [ [ Some "2" ] ]) -> ()
                    | other -> failtestf "expected 2 distinct (g, n) tuples ('a',1) and ('b',3), got %A" other

                testCase "GROUP_CONCAT joins group members with the default comma separator, and a custom SEPARATOR"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), tag VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('a', 'x'), ('a', 'y'), ('a', 'x')" |> ignore

                    match runDefault store "SELECT GROUP_CONCAT(tag) AS c FROM t GROUP BY grp" with
                    | ResultSet([ "c" ], [ [ Some "x,y,x" ] ]) -> ()
                    | other -> failtestf "expected the default comma separator, got %A" other

                    match runDefault store "SELECT GROUP_CONCAT(DISTINCT tag SEPARATOR '|') AS c FROM t GROUP BY grp" with
                    | ResultSet([ "c" ], [ [ Some "x|y" ] ]) -> ()
                    | other -> failtestf "expected DISTINCT deduping and a custom separator, got %A" other

                testCase "SELECT COUNT(*) FROM an empty table still returns one row with 0, not zero rows"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore

                    match runDefault store "SELECT COUNT(*) AS c FROM t" with
                    | ResultSet([ "c" ], [ [ Some "0" ] ]) -> ()
                    | other -> failtestf "expected one row with COUNT(*) = 0 on an empty table, got %A" other

                testCase "a real GROUP BY over an empty table produces zero rows, unlike the whole-table aggregate case above"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (grp VARCHAR(10), n INT)" |> ignore

                    match runDefault store "SELECT grp, COUNT(*) AS c FROM t GROUP BY grp" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected zero grouped rows on an empty table, got %A" other ]

          testList
              "correlated subqueries"
              [ testCase "correlated EXISTS: WHERE EXISTS (... referencing the outer row) — the Eloquent whereHas() shape"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    // only alice has a post.
                    runDefault store "INSERT INTO posts VALUES (1, 1)" |> ignore

                    let sql = "SELECT name FROM users WHERE EXISTS (SELECT 1 FROM posts WHERE posts.user_id = users.id)"

                    match runDefault store sql with
                    | ResultSet([ "name" ], [ [ Some "alice" ] ]) -> ()
                    | other -> failtestf "expected only alice (who has a post), got %A" other

                testCase "correlated NOT EXISTS"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO posts VALUES (1, 1)" |> ignore

                    let sql = "SELECT name FROM users WHERE NOT EXISTS (SELECT 1 FROM posts WHERE posts.user_id = users.id)"

                    match runDefault store sql with
                    | ResultSet([ "name" ], [ [ Some "bob" ] ]) -> ()
                    | other -> failtestf "expected only bob (who has no post), got %A" other

                testCase "IN (SELECT ...): a non-correlated subquery's first column is the candidate set"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO posts VALUES (1)" |> ignore

                    match runDefault store "SELECT name FROM users WHERE id IN (SELECT user_id FROM posts)" with
                    | ResultSet([ "name" ], [ [ Some "alice" ] ]) -> ()
                    | other -> failtestf "expected only alice, got %A" other

                testCase "NOT IN (SELECT ...)"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO posts VALUES (1)" |> ignore

                    match runDefault store "SELECT name FROM users WHERE id NOT IN (SELECT user_id FROM posts)" with
                    | ResultSet([ "name" ], [ [ Some "bob" ] ]) -> ()
                    | other -> failtestf "expected only bob, got %A" other

                testCase "scalar subquery: (SELECT ...) used as a value, zero rows is NULL, one row is that value"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
                    runDefault store "CREATE TABLE posts (id INT, user_id INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO posts VALUES (1, 1), (2, 1)" |> ignore

                    let sql = "SELECT name, (SELECT COUNT(*) FROM posts WHERE posts.user_id = users.id) AS n FROM users ORDER BY name"

                    match runDefault store sql with
                    | ResultSet([ "name"; "n" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "2" ]; [ Some "bob"; Some "0" ] ] "correlated scalar subquery per row"
                    | other -> failtestf "expected a per-row correlated count, got %A" other

                testCase "scalar subquery returning more than one row is MySQL error 1242"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2)" |> ignore

                    match runDefault store "SELECT (SELECT n FROM t) AS x" with
                    | Err(1242, _) -> ()
                    | other -> failtestf "expected error 1242, got %A" other

                testCase "derived table: FROM (SELECT ...) AS t"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

                    match runDefault store "SELECT doubled FROM (SELECT n * 2 AS doubled FROM t) AS d WHERE doubled > 2 ORDER BY doubled" with
                    | ResultSet([ "doubled" ], rows) -> Expect.equal rows [ [ Some "4" ]; [ Some "6" ] ] "derived table filtered and projected"
                    | other -> failtestf "expected a derived-table resultset, got %A" other

                testCase "a derived table's columns compare numerically, not as re-wrapped text"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE nums (n INT)" |> ignore
                    runDefault store "INSERT INTO nums VALUES (2), (10), (9)" |> ignore

                    match runDefault store "SELECT MAX(y.n) AS m FROM (SELECT n FROM nums) y" with
                    | ResultSet([ "m" ], [ [ Some "10" ] ]) -> ()
                    | other -> failtestf "expected MAX to compare numerically (10), got %A" other

                    match runDefault store "SELECT y.n FROM (SELECT n FROM nums) y ORDER BY y.n" with
                    | ResultSet([ "n" ], rows) ->
                        Expect.equal rows [ [ Some "2" ]; [ Some "9" ]; [ Some "10" ] ] "ORDER BY sorts numerically, not lexicographically"
                    | other -> failtestf "expected a numerically-sorted resultset, got %A" other

                testCase "LEFT JOIN (SELECT ...) AS t ON ... — a derived table as a JOIN target, not just the leading FROM"
                <| fun _ ->
                    // Eloquent's leftJoinSub/joinSub send exactly this shape.
                    let store = newStore ()
                    runDefault store "CREATE TABLE users (id INT, name VARCHAR(20))" |> ignore
                    runDefault store "CREATE TABLE orders (user_id INT, total INT)" |> ignore
                    runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
                    runDefault store "INSERT INTO orders VALUES (1, 10), (1, 5)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT users.name, o.total_spent FROM users LEFT JOIN (SELECT user_id, SUM(total) AS total_spent FROM orders GROUP BY user_id) AS o ON users.id = o.user_id ORDER BY users.id"
                    with
                    | ResultSet([ "name"; "total_spent" ], rows) ->
                        Expect.equal rows [ [ Some "alice"; Some "15" ]; [ Some "bob"; None ] ] "bob has no orders, padded with NULL"
                    | other -> failtestf "expected a joined-against-subquery resultset, got %A" other

                testCase "UPDATE t1 JOIN (SELECT ...) dt ON ... is a 1064 error, not a crash"
                <| fun _ ->
                    // `Executor.applyMutationJoin`'s documented real-tables-only
                    // simplification — a clean error, not a wrong result.
                    let store = newStore ()
                    runDefault store "CREATE TABLE t1 (id INT, n INT)" |> ignore

                    match runDefault store "UPDATE t1 JOIN (SELECT 1 AS id) dt ON t1.id = dt.id SET t1.n = 1" with
                    | Err(1064, _) -> ()
                    | other -> failtestf "expected a 1064 error, got %A" other

                testCase "a scalar subquery's comparison is numeric, not lexicographic text"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE nums (n INT)" |> ignore
                    runDefault store "INSERT INTO nums VALUES (2), (10), (9)" |> ignore

                    match runDefault store "SELECT (SELECT MAX(n) FROM nums) > (SELECT MIN(n) FROM nums) AS r" with
                    | ResultSet([ "r" ], [ [ Some "1" ] ]) -> ()
                    | other -> failtestf "expected 10 > 2 to be true, got %A" other ]

          testList
              "CASE / UNION / <=> / IS TRUE-FALSE / REGEXP / LIKE BINARY"
              [ testCase "searched CASE WHEN ... THEN ... ELSE ... END"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (-1), (0)" |> ignore

                    let sql = "SELECT CASE WHEN n > 0 THEN 'pos' WHEN n < 0 THEN 'neg' ELSE 'zero' END AS sign FROM t ORDER BY n"

                    match runDefault store sql with
                    | ResultSet([ "sign" ], rows) ->
                        Expect.equal rows [ [ Some "neg" ]; [ Some "zero" ]; [ Some "pos" ] ] "one branch per row"
                    | other -> failtestf "expected a CASE per row, got %A" other

                testCase "simple CASE subject WHEN value THEN ... END, falling through to NULL with no ELSE"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT)" |> ignore
                    runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

                    match runDefault store "SELECT CASE n WHEN 1 THEN 'one' WHEN 2 THEN 'two' END AS label FROM t ORDER BY n" with
                    | ResultSet([ "label" ], rows) -> Expect.equal rows [ [ Some "one" ]; [ Some "two" ]; [ None ] ] "3 falls through to NULL"
                    | other -> failtestf "expected a simple CASE with an implicit NULL else, got %A" other

                testCase "UNION dedupes, UNION ALL keeps duplicates"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE a (n INT)" |> ignore
                    runDefault store "CREATE TABLE b (n INT)" |> ignore
                    runDefault store "INSERT INTO a VALUES (1), (2)" |> ignore
                    runDefault store "INSERT INTO b VALUES (2), (3)" |> ignore

                    match runDefault store "SELECT n FROM a UNION SELECT n FROM b ORDER BY n" with
                    | ResultSet([ "n" ], rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ] "deduped"
                    | other -> failtestf "expected UNION to dedupe, got %A" other

                    match runDefault store "SELECT n FROM a UNION ALL SELECT n FROM b ORDER BY n" with
                    | ResultSet([ "n" ], rows) ->
                        Expect.equal rows [ [ Some "1" ]; [ Some "2" ]; [ Some "2" ]; [ Some "3" ] ] "duplicates kept"
                    | other -> failtestf "expected UNION ALL to keep duplicates, got %A" other

                testCase "UNION's ORDER BY sorts numerically, not as re-wrapped text"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE nums (n INT)" |> ignore
                    runDefault store "INSERT INTO nums VALUES (2), (10), (9)" |> ignore

                    match runDefault store "SELECT n FROM nums UNION SELECT n FROM nums ORDER BY n" with
                    | ResultSet([ "n" ], rows) ->
                        Expect.equal rows [ [ Some "2" ]; [ Some "9" ]; [ Some "10" ] ] "sorted numerically, not lexicographically"
                    | other -> failtestf "expected a numerically-sorted UNION resultset, got %A" other

                testCase "<=> is a null-safe equals: NULL <=> NULL is true, unlike ="
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT NULL <=> NULL AS a, NULL = NULL AS b, 1 <=> 1 AS c, 1 <=> 2 AS d" with
                    | ResultSet([ "a"; "b"; "c"; "d" ], [ [ Some "1"; None; Some "1"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected <=> to never be NULL, got %A" other

                testCase "IS TRUE / IS FALSE are never NULL, even for a NULL operand"
                <| fun _ ->
                    let store = newStore ()

                    match runDefault store "SELECT (1 IS TRUE) AS a, (0 IS FALSE) AS b, (NULL IS TRUE) AS c, (NULL IS FALSE) AS d" with
                    | ResultSet([ "a"; "b"; "c"; "d" ], [ [ Some "1"; Some "1"; Some "0"; Some "0" ] ]) -> ()
                    | other -> failtestf "expected IS TRUE/FALSE to be plain booleans, got %A" other

                testCase "REGEXP matches a real regex pattern, case-insensitively"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (s VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('Hello'), ('world')" |> ignore

                    match runDefault store "SELECT s FROM t WHERE s REGEXP '^h' ORDER BY s" with
                    | ResultSet([ "s" ], [ [ Some "Hello" ] ]) -> ()
                    | other -> failtestf "expected REGEXP to case-insensitively match 'Hello', got %A" other

                testCase "LIKE BINARY is case-sensitive, unlike plain LIKE"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (s VARCHAR(10))" |> ignore
                    runDefault store "INSERT INTO t VALUES ('Hello')" |> ignore

                    match runDefault store "SELECT s FROM t WHERE s LIKE BINARY 'hello'" with
                    | ResultSet(_, []) -> ()
                    | other -> failtestf "expected LIKE BINARY 'hello' not to match 'Hello', got %A" other

                    match runDefault store "SELECT s FROM t WHERE s LIKE 'hello'" with
                    | ResultSet([ "s" ], [ [ Some "Hello" ] ]) -> ()
                    | other -> failtestf "expected plain LIKE to still match case-insensitively, got %A" other ]

          testList
              "generated columns"
              [ testCase "a STORED generated column computes on INSERT"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT, doubled INT AS (n * 2) STORED)" |> ignore
                    runDefault store "INSERT INTO t (n) VALUES (3), (5)" |> ignore

                    match runDefault store "SELECT n, doubled FROM t ORDER BY n" with
                    | ResultSet([ "n"; "doubled" ], rows) ->
                        Expect.equal rows [ [ Some "3"; Some "6" ]; [ Some "5"; Some "10" ] ] "computed from n"
                    | other -> failtestf "expected doubled to be computed, got %A" other

                testCase "a generated column recomputes after UPDATE of a column it depends on"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (n INT, doubled INT AS (n * 2))" |> ignore
                    runDefault store "INSERT INTO t (n) VALUES (3)" |> ignore
                    runDefault store "UPDATE t SET n = 10" |> ignore

                    match runDefault store "SELECT doubled FROM t" with
                    | ResultSet([ "doubled" ], [ [ Some "20" ] ]) -> ()
                    | other -> failtestf "expected doubled to recompute to 20, got %A" other ]

          testList
              "DATE column coercion"
              [ testCase "a full datetime string into a DATE column keeps just the date part, like real MySQL"
                <| fun _ ->
                    // Real MySQL silently truncates a full datetime string
                    // into just its date part on insert into a DATE column
                    // — this is exactly what Eloquent's `date` cast sends
                    // (Carbon's full `'Y-m-d H:i:s'` string form).
                    let store = newStore ()
                    runDefault store "CREATE TABLE t (d DATE)" |> ignore

                    match runDefault store "INSERT INTO t VALUES ('2024-03-05 13:45:09')" with
                    | Affected 1UL -> ()
                    | other -> failtestf "expected the datetime string to coerce into DATE, got %A" other

                    match runDefault store "SELECT d FROM t" with
                    | ResultSet([ "d" ], [ [ Some "2024-03-05" ] ]) -> ()
                    | other -> failtestf "expected the date part only, got %A" other ]

          testList
              "ROW_NUMBER() OVER (PARTITION BY ... ORDER BY ...)"
              [ testCase "numbers rows 1.. per partition, in the window's own ORDER BY order"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE msgs (session_id INT, created_at INT, body VARCHAR(20))" |> ignore

                    runDefault
                        store
                        "INSERT INTO msgs VALUES (1, 1, 'a'), (1, 2, 'b'), (1, 3, 'c'), (2, 1, 'x')"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT body, ROW_NUMBER() OVER (PARTITION BY session_id ORDER BY created_at DESC) AS rn FROM msgs ORDER BY session_id, rn"
                    with
                    | ResultSet([ "body"; "rn" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "c"; Some "1" ]; [ Some "b"; Some "2" ]; [ Some "a"; Some "3" ]; [ Some "x"; Some "1" ] ]
                            "each session's messages numbered newest-first, restarting per session"
                    | other -> failtestf "expected numbered rows, got %A" other

                testCase "SELECT * alongside ROW_NUMBER() OVER (...) doesn't leak the synthetic column into *"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE msgs (session_id INT, body VARCHAR(20))" |> ignore
                    runDefault store "INSERT INTO msgs VALUES (1, 'a')" |> ignore

                    match
                        runDefault store "SELECT *, ROW_NUMBER() OVER (PARTITION BY session_id ORDER BY body) AS rn FROM msgs"
                    with
                    | ResultSet([ "session_id"; "body"; "rn" ], [ [ Some "1"; Some "a"; Some "1" ] ]) -> ()
                    | other -> failtestf "expected exactly session_id, body, rn (no duplicate/leaked column), got %A" other

                testCase "the Laravel 'limit per group' shape: a derived table filtered on the window alias"
                <| fun _ ->
                    // Verbatim repro of the pattern Eloquent's constrained
                    // eager loading (`->with(['msgs' => fn ($q) =>
                    // $q->orderBy('created_at', 'desc')->limit(1)])`)
                    // compiles a relation query's `->limit()` into.
                    let store = newStore ()
                    runDefault store "CREATE TABLE msgs (session_id INT, created_at INT, body VARCHAR(20))" |> ignore

                    runDefault
                        store
                        "INSERT INTO msgs VALUES (1, 1, 'old'), (1, 2, 'newest'), (2, 1, 'only')"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT * FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY session_id ORDER BY created_at DESC) AS laravel_row FROM msgs) AS t WHERE laravel_row <= 1 ORDER BY session_id"
                    with
                    | ResultSet([ "session_id"; "created_at"; "body"; "laravel_row" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "1"; Some "2"; Some "newest"; Some "1" ]; [ Some "2"; Some "1"; Some "only"; Some "1" ] ]
                            "only the latest message per session survives"
                    | other -> failtestf "expected one row per session, got %A" other ]

          testList
              "LAG(expr) OVER (PARTITION BY ... ORDER BY ...)"
              [ testCase "yields the previous row's value per partition, NULL for the first"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE readings (sensor VARCHAR(10), created_at INT, value INT)" |> ignore

                    runDefault
                        store
                        "INSERT INTO readings VALUES ('a', 1, 10), ('a', 2, 14), ('a', 3, 9), ('b', 1, 100)"
                    |> ignore

                    match
                        runDefault
                            store
                            "SELECT sensor, value, LAG(value) OVER (PARTITION BY sensor ORDER BY created_at) AS prev FROM readings ORDER BY sensor, created_at"
                    with
                    | ResultSet([ "sensor"; "value"; "prev" ], rows) ->
                        Expect.equal
                            rows
                            [ [ Some "a"; Some "10"; None ]
                              [ Some "a"; Some "14"; Some "10" ]
                              [ Some "a"; Some "9"; Some "14" ]
                              [ Some "b"; Some "100"; None ] ]
                            "each partition's first row has no predecessor, later rows see the prior one"
                    | other -> failtestf "expected lagged values, got %A" other

                testCase "usable nested inside arithmetic, not just as a bare projection"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE readings (sensor VARCHAR(10), created_at INT, value INT)" |> ignore
                    runDefault store "INSERT INTO readings VALUES ('a', 1, 10), ('a', 2, 14)" |> ignore

                    match
                        runDefault
                            store
                            "SELECT value, value - LAG(value) OVER (PARTITION BY sensor ORDER BY created_at) AS diff FROM readings ORDER BY created_at"
                    with
                    | ResultSet([ "value"; "diff" ], [ [ Some "10"; None ]; [ Some "14"; Some "4" ] ]) -> ()
                    | other -> failtestf "expected value - LAG(value) computed per row, got %A" other

                testCase "an explicit offset skips back that many rows within the partition"
                <| fun _ ->
                    let store = newStore ()
                    runDefault store "CREATE TABLE readings (created_at INT, value INT)" |> ignore
                    runDefault store "INSERT INTO readings VALUES (1, 10), (2, 20), (3, 30)" |> ignore

                    match
                        runDefault store "SELECT value, LAG(value, 2) OVER (ORDER BY created_at) AS prev2 FROM readings ORDER BY created_at"
                    with
                    | ResultSet([ "value"; "prev2" ], [ [ Some "10"; None ]; [ Some "20"; None ]; [ Some "30"; Some "10" ] ]) -> ()
                    | other -> failtestf "expected offset-2 lag, got %A" other ] ]
