module Fsdb.Tests.ExecutorDmlTests

open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor

let private run = TestSupport.Sql.execute
let private runDefault = TestSupport.Sql.executeDefault
let private newStore () = create ()

let tests =
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

          testCase "UPDATE JOIN with multiple SET assignments on the same target table applies all of them"
          <| fun _ ->
              // `claims` must be checked once per (source, physical
              // row), not per assignment, or only the first SET lands.
              let store = newStore ()
              runDefault store "CREATE TABLE mt1 (id INT, a INT, b INT)" |> ignore
              runDefault store "CREATE TABLE mt2 (id INT, x INT)" |> ignore
              runDefault store "INSERT INTO mt1 VALUES (1, 0, 0)" |> ignore
              runDefault store "INSERT INTO mt2 VALUES (1, 5)" |> ignore

              match runDefault store "UPDATE mt1 JOIN mt2 ON mt1.id = mt2.id SET mt1.a = 10, mt1.b = 20" with
              | Affected 1UL -> ()
              | other -> failtestf "expected 1 row affected, got %A" other

              match runDefault store "SELECT a, b FROM mt1" with
              | ResultSet(_, [ [ Some "10"; Some "20" ] ]) -> ()
              | other -> failtestf "expected both a and b written, got %A" other

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
