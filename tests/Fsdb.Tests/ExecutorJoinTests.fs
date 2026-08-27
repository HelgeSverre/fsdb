module Fsdb.Tests.ExecutorJoinTests

open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor
open TestSupport.SqlCommentMutation

let private run = TestSupport.Sql.execute
let private runDefault = TestSupport.Sql.executeDefault
let private newStore () = create ()

let tests =
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

          testCase "inner joins choose the smallest ready indexed source unless STRAIGHT_JOIN pins order"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE base_rows (id INT PRIMARY KEY)" |> ignore
              runDefault store "CREATE TABLE large_rows (id INT PRIMARY KEY, base_id INT, INDEX ix_large_base (base_id))" |> ignore
              runDefault store "CREATE TABLE small_rows (id INT PRIMARY KEY, base_id INT, INDEX ix_small_base (base_id))" |> ignore
              runDefault store "INSERT INTO base_rows VALUES (1), (2)" |> ignore

              [ 1..100 ]
              |> List.map (fun id -> sprintf "(%d, %d)" id (if id = 1 then 1 else 2))
              |> String.concat ","
              |> sprintf "INSERT INTO large_rows VALUES %s"
              |> runDefault store
              |> ignore

              runDefault store "INSERT INTO small_rows VALUES (1, 1)" |> ignore

              let sql =
                  "SELECT base_rows.id FROM base_rows "
                  + "JOIN large_rows ON large_rows.base_id = base_rows.id "
                  + "JOIN small_rows ON small_rows.base_id = base_rows.id"

              let explainedTables query =
                  match runDefault store ("EXPLAIN " + query) with
                  | ResultSet(_, rows) -> rows |> List.choose (List.item 2)
                  | other -> failtestf "expected EXPLAIN rows, got %A" other

              Expect.equal
                  (explainedTables sql)
                  [ "base_rows"; "small_rows"; "large_rows" ]
                  "the smaller ready indexed source runs first"

              Expect.equal
                  (explainedTables (sql.Replace("SELECT ", "SELECT STRAIGHT_JOIN ")))
                  [ "base_rows"; "large_rows"; "small_rows" ]
                  "STRAIGHT_JOIN preserves source order"

              match runDefault store (sql + " ORDER BY base_rows.id") with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ] ] "reordering preserves join results"
              | other -> failtestf "expected joined rows, got %A" other

          testCase "a qualified base-table range narrows before joining"
          <| fun _ ->
              let mutable calls = 0

              let registry =
                  builtins
                  |> registerScalar "TOUCH" (fun values ->
                      calls <- calls + 1
                      values.Head)

              let store = newStore ()
              runDefault store "CREATE TABLE base_rows (id INT PRIMARY KEY)" |> ignore
              runDefault store "CREATE TABLE singleton (id INT PRIMARY KEY)" |> ignore
              runDefault store ("INSERT INTO base_rows VALUES " + ([ 1..1000 ] |> List.map (sprintf "(%d)") |> String.concat ",")) |> ignore
              runDefault store "INSERT INTO singleton VALUES (1)" |> ignore

              match
                  run
                      store
                      registry
                      "SELECT base_rows.id FROM base_rows JOIN singleton ON singleton.id = singleton.id WHERE base_rows.id >= 995 AND TOUCH(base_rows.id) = base_rows.id ORDER BY base_rows.id"
              with
              | ResultSet(_, rows) -> Expect.equal rows.Length 6 "the range returns the final six rows"
              | other -> failtestf "expected joined rows, got %A" other

              Expect.isLessThan calls 20 "the residual sees the qualified range candidates, not the full base table"

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

          testCase "integer equality hash join preserves duplicate matches and never matches NULL keys"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE lhs (id INT NULL)" |> ignore
              runDefault store "CREATE TABLE rhs (owner_id INT NULL, label VARCHAR(10))" |> ignore
              runDefault store "INSERT INTO lhs VALUES (1), (NULL), (2)" |> ignore
              runDefault store "INSERT INTO rhs VALUES (1, 'a'), (1, 'b'), (NULL, 'null-key')" |> ignore

              match runDefault store "SELECT lhs.id, rhs.label FROM lhs LEFT JOIN rhs ON lhs.id = rhs.owner_id ORDER BY lhs.id, rhs.label" with
              | ResultSet([ "id"; "label" ], rows) ->
                  Expect.equal
                      rows
                      [ [ None; None ]; [ Some "1"; Some "a" ]; [ Some "1"; Some "b" ]; [ Some "2"; None ] ]
                      "NULL does not equal NULL, duplicate right keys preserve both matches, and unmatched left rows are padded"
              | other -> failtestf "expected a duplicate-preserving left join resultset, got %A" other

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

          testCase "INNER JOIN without a condition produces the full Cartesian product"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE a (x INT)" |> ignore
              runDefault store "CREATE TABLE b (y INT)" |> ignore
              runDefault store "INSERT INTO a VALUES (1), (2)" |> ignore
              runDefault store "INSERT INTO b VALUES (10), (20)" |> ignore

              match runDefault store "SELECT x, y FROM a JOIN b ORDER BY x, y" with
              | ResultSet([ "x"; "y" ], rows) -> Expect.equal rows.Length 4 "every combination"
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

          // Every expectation below was verified against a real MySQL
          // 8.4 with the identical schema/data (torture-style
          // differential probe) — the exact rows, not just shapes.
          testCase "self-join through two aliases pairs rows with a shared key"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
              runDefault store "INSERT INTO b VALUES (10, 1, 'x'), (11, 1, 'y'), (12, 2, 'z'), (13, 4, 'w')" |> ignore

              match
                  runDefault store "SELECT x.id, y.id, y.tag FROM b x INNER JOIN b y ON y.aid = x.aid AND y.id <> x.id ORDER BY x.id, y.id"
              with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "10"; Some "11"; Some "y" ]; [ Some "11"; Some "10"; Some "x" ] ] "aid=1 pairs, each excluding itself"
              | other -> failtestf "expected the self-join pairs, got %A" other

          testCase "a three-table INNER chain keeps only rows matching through every link"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE a (id INT, name VARCHAR(10))" |> ignore
              runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
              runDefault store "CREATE TABLE c (id INT, bid INT, note VARCHAR(10))" |> ignore
              runDefault store "INSERT INTO a VALUES (1,'alice'),(2,'bob'),(3,'carol')" |> ignore
              runDefault store "INSERT INTO b VALUES (10,1,'x'),(11,1,'y'),(12,2,'z'),(13,4,'w')" |> ignore
              runDefault store "INSERT INTO c VALUES (100,10,'n1'),(101,12,'n2'),(102,99,'n3')" |> ignore

              match
                  runDefault store "SELECT a.id, b.id, c.id, c.note FROM a INNER JOIN b ON b.aid = a.id INNER JOIN c ON c.bid = b.id ORDER BY a.id, b.id, c.id"
              with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "10"; Some "100"; Some "n1" ]; [ Some "2"; Some "12"; Some "101"; Some "n2" ] ] "rows surviving both links"
              | other -> failtestf "expected the chained matches, got %A" other

          testCase "a LEFT chain pads NULLs at whichever link first fails"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE a (id INT)" |> ignore
              runDefault store "CREATE TABLE b (id INT, aid INT)" |> ignore
              runDefault store "CREATE TABLE c (id INT, bid INT)" |> ignore
              runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO b VALUES (10, 1), (11, 1), (12, 2), (13, 4)" |> ignore
              runDefault store "INSERT INTO c VALUES (100, 10), (101, 12), (102, 99)" |> ignore

              match runDefault store "SELECT a.id, b.id, c.id FROM a LEFT JOIN b ON b.aid = a.id LEFT JOIN c ON c.bid = b.id ORDER BY a.id, b.id, c.id" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "1"; Some "10"; Some "100" ]
                        [ Some "1"; Some "11"; None ]
                        [ Some "2"; Some "12"; Some "101" ]
                        [ Some "3"; None; None ] ]
                      "each link pads only the side past its failure point"
              | other -> failtestf "expected the chained LEFT matches, got %A" other

          testCase "WHERE on the joined table filters LEFT JOIN rows exactly like MySQL (matched vs padded)"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE a (id INT)" |> ignore
              runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
              runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO b VALUES (10, 1, 'x'), (11, 1, 'y'), (12, 2, 'z'), (13, 4, 'w')" |> ignore

              match runDefault store "SELECT a.id, b.id, b.tag FROM a LEFT JOIN b ON b.aid = a.id WHERE b.tag IS NOT NULL ORDER BY a.id, b.id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "10"; Some "x" ]; [ Some "1"; Some "11"; Some "y" ]; [ Some "2"; Some "12"; Some "z" ] ] "WHERE on the right side drops padded rows"
              | other -> failtestf "expected only matched rows, got %A" other

              match runDefault store "SELECT a.id, b.id, b.tag FROM a LEFT JOIN b ON b.aid = a.id WHERE b.tag IS NULL ORDER BY a.id" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "3"; None; None ] ] "only the unmatched left row survives"
              | other -> failtestf "expected only the padded row, got %A" other

          testCase "aggregates over a LEFT JOIN count/SUM per group, NULLs included"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE a (id INT)" |> ignore
              runDefault store "CREATE TABLE b (id INT, aid INT)" |> ignore
              runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO b VALUES (10, 1), (11, 1), (12, 2), (13, 4)" |> ignore

              match
                  runDefault
                      store
                      "SELECT a.id, COUNT(b.id), COALESCE(SUM(b.id), 0) FROM a LEFT JOIN b ON b.aid = a.id GROUP BY a.id ORDER BY a.id"
              with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "2"; Some "21" ]; [ Some "2"; Some "1"; Some "12" ]; [ Some "3"; Some "0"; Some "0" ] ] "per-group counts and sums"
              | other -> failtestf "expected grouped aggregates, got %A" other

          testCase "a multi-key ON keeps equi-key matches and filters by the residual conjunct"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE a (id INT)" |> ignore
              runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
              runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO b VALUES (10, 1, 'x'), (11, 1, 'y'), (12, 2, 'z'), (13, 4, 'w')" |> ignore

              match runDefault store "SELECT a.id, b.id, b.tag FROM a LEFT JOIN b ON b.aid = a.id AND b.tag = 'x' ORDER BY a.id, b.id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "10"; Some "x" ]; [ Some "2"; None; None ]; [ Some "3"; None; None ] ] "only the x-tagged b row matches a=1"
              | other -> failtestf "expected the filtered multi-key matches, got %A" other

          testCase "a non-equi ON falls back to nested loop, same matches as MySQL"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE a (id INT)" |> ignore
              runDefault store "CREATE TABLE b (id INT, aid INT)" |> ignore
              runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO b VALUES (10, 1), (11, 1), (12, 2), (13, 4)" |> ignore

              match runDefault store "SELECT a.id, b.id FROM a INNER JOIN b ON a.id < b.aid ORDER BY a.id, b.id" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "1"; Some "12" ]
                        [ Some "1"; Some "13" ]
                        [ Some "2"; Some "13" ]
                        [ Some "3"; Some "13" ] ]
                      "every a.id < b.aid pair"
              | other -> failtestf "expected the range-join pairs, got %A" other

          testCase "an equi-key on a string-cast column still hash-matches (key class coercion)"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE a (id INT, name VARCHAR(10))" |> ignore
              runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
              runDefault store "INSERT INTO a VALUES (1,'alice'),(2,'bob'),(3,'carol')" |> ignore
              runDefault store "INSERT INTO b VALUES (10,1,'x'),(11,1,'y'),(12,2,'z'),(13,4,'w')" |> ignore

              match runDefault store "SELECT a.id, b.id, b.tag FROM a INNER JOIN b ON b.aid = CAST(a.id AS CHAR) ORDER BY a.id, b.id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "10"; Some "x" ]; [ Some "1"; Some "11"; Some "y" ]; [ Some "2"; Some "12"; Some "z" ] ] "same matches as the uncast equi-join"
              | other -> failtestf "expected the coerced-key matches, got %A" other

          testCase "chaining RIGHT JOIN then LEFT JOIN pads each outer side independently"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE a (id INT)" |> ignore
              runDefault store "CREATE TABLE b (id INT, aid INT)" |> ignore
              runDefault store "CREATE TABLE c (id INT, bid INT)" |> ignore
              runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO b VALUES (10, 1), (11, 1), (12, 2), (13, 4)" |> ignore
              runDefault store "INSERT INTO c VALUES (100, 10), (101, 12), (102, 99)" |> ignore

              match runDefault store "SELECT a.id, b.id, c.id FROM a RIGHT JOIN b ON b.aid = a.id LEFT JOIN c ON c.bid = b.id ORDER BY b.id, a.id, c.id" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "1"; Some "10"; Some "100" ]
                        [ Some "1"; Some "11"; None ]
                        [ Some "2"; Some "12"; Some "101" ]
                        [ None; Some "13"; None ] ]
                      "b=13 is RIGHT-padded on a, then LEFT-padded on c"
              | other -> failtestf "expected the mixed RIGHT+LEFT chain, got %A" other

          testCase "joining against an aggregate derived table matches MySQL's grouped results"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE a (id INT)" |> ignore
              runDefault store "CREATE TABLE b (id INT, aid INT, tag VARCHAR(10))" |> ignore
              runDefault store "INSERT INTO a VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO b VALUES (10, 1, 'x'), (11, 1, 'y'), (12, 2, 'z'), (13, 4, 'w')" |> ignore

              match
                  runDefault
                      store
                      "SELECT a.id, q2.tag FROM a INNER JOIN (SELECT aid, MAX(tag) tag FROM b GROUP BY aid) q2 ON q2.aid = a.id ORDER BY a.id"
              with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1"; Some "y" ]; [ Some "2"; Some "z" ] ] "max tag per joined group"
              | other -> failtestf "expected the derived-table join, got %A" other

          testCase "nested CTEs and mixed comments preserve a composed join result"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE customers (id INT PRIMARY KEY, name VARCHAR(10), active INT)" |> ignore
              runDefault store "CREATE TABLE orders (id INT PRIMARY KEY, customer_id INT, amount INT)" |> ignore
              runDefault store "INSERT INTO customers VALUES (1,'alice',1),(2,'bob',1),(3,'carol',0),(4,'dana',1)" |> ignore
              runDefault store "INSERT INTO orders VALUES (1,1,10),(2,1,15),(3,2,7),(4,3,99)" |> ignore

              let baseline =
                  "WITH RECURSIVE limits(n) AS ("
                  + "SELECT 1 UNION ALL SELECT n + 1 FROM limits WHERE n < 4), "
                  + "filtered AS ("
                  + "SELECT o.id, o.customer_id, o.amount FROM orders AS o "
                  + "JOIN limits AS l ON l.n = o.id "
                  + "WHERE EXISTS (SELECT 1 FROM customers AS present WHERE present.id = o.customer_id)), "
                  + "totals AS ("
                  + "SELECT customer_id, SUM(amount) AS total, COUNT(*) AS count FROM filtered GROUP BY customer_id), "
                  + "ranked AS ("
                  + "SELECT customer_id, total, count, "
                  + "ROW_NUMBER() OVER (ORDER BY total DESC, customer_id) AS row_rank FROM totals) "
                  + "SELECT c.id, c.name, COALESCE(r.total, 0), r.row_rank, "
                  + "(SELECT COUNT(*) FROM filtered AS again WHERE again.customer_id = c.id) "
                  + "FROM customers AS c LEFT JOIN ranked AS r ON r.customer_id = c.id "
                  + "LEFT JOIN (SELECT customer_id, MAX(amount) AS biggest FROM filtered GROUP BY customer_id) AS maxima "
                  + "ON maxima.customer_id = c.id "
                  + "WHERE c.active = 1 AND (r.customer_id IS NULL OR maxima.biggest >= 10) ORDER BY c.id"

              let commented =
                  "WITH/* lead */RECURSIVE limits/* name */(n) AS ("
                  + "SELECT /*!80400 SQL_NO_CACHE */ 1 UNION/* branch */ALL SELECT n/* lhs */+/* rhs */1 "
                  + "FROM limits WHERE n < 4),# next cte\n"
                  + "filtered AS (SELECT o/* qualifier */.id, o.customer_id, o.amount FROM orders AS o "
                  + "JOIN/* source */limits AS l ON l.n = o.id -- correlated filter\n"
                  + "WHERE EXISTS/* call */(SELECT 1 FROM customers AS present WHERE present.id = o.customer_id)), "
                  + "totals AS (SELECT customer_id, SUM(/* argument */amount) AS total, COUNT(/* star */*) AS count "
                  + "FROM filtered GROUP BY customer_id),/* ranked rows */"
                  + "ranked AS (SELECT customer_id, total, count, ROW_NUMBER(/* empty */) OVER ("
                  + "ORDER BY total DESC, customer_id) AS row_rank FROM totals) "
                  + "SELECT c.id, c.name, COALESCE(/* argument */r.total, 0), r.row_rank, "
                  + "(SELECT COUNT(*) FROM filtered AS again WHERE again.customer_id = c.id) "
                  + "FROM customers AS c LEFT/* outer */JOIN ranked AS r ON r.customer_id = c.id "
                  + "LEFT JOIN (SELECT customer_id, MAX(amount) AS biggest FROM filtered GROUP BY customer_id) AS maxima "
                  + "ON maxima.customer_id = c.id WHERE c.active = 1 "
                  + "AND (r.customer_id IS NULL OR maxima.biggest >= 10) ORDER/* final */BY c.id"

              let expected =
                  [ [ Some "1"; Some "alice"; Some "25"; Some "2"; Some "2" ]
                    [ Some "4"; Some "dana"; Some "0"; None; Some "0" ] ]

              for sql in [ baseline; commented ] do
                  match runDefault store sql with
                  | ResultSet(_, rows) -> Expect.equal rows expected sql
                  | other -> failtestf "expected the composed resultset, got %A from %s" other sql

          testCase "a branching CTE graph preserves every path through dense comments"
          <| fun _ ->
              let definitions =
                  "c0 AS (SELECT 1 AS n)"
                  :: [ for level in 1..12 ->
                           sprintf
                               "c%d/* name */AS (SELECT n/* left */+/* one */1 AS n FROM c%d UNION/* branch */ALL SELECT n + 2 FROM c%d)"
                               level
                               (level - 1)
                               (level - 1) ]

              let sql =
                  "WITH/* graph */"
                  + String.concat ",# next level\n" definitions
                  + " SELECT COUNT(/* paths */*), MIN(n), MAX(n), SUM(n) FROM c12"

              match runDefault (newStore ()) sql with
              | ResultSet(_, [ [ Some "4096"; Some "13"; Some "25"; Some "77824" ] ]) -> ()
              | other -> failtestf "expected all branching CTE paths, got %A" other

          testCase "a long commented join chain preserves dependency order and outer padding"
          <| fun _ ->
              let joins =
                  [ 1..23 ]
                  |> List.map (fun level ->
                      sprintf
                          "JOIN/* edge %d */tagged AS t%d ON t%d.n = t%d.n + 1"
                          level
                          level
                          level
                          (level - 1))
                  |> String.concat " "

              let sql =
                  "WITH/* seed */RECURSIVE nums(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM nums WHERE n < 32), "
                  + "tagged AS (SELECT n, n MOD 3 AS bucket FROM nums) "
                  + "SELECT COUNT(*), SUM(t0/* qualifier */.n), COUNT(t23.n), COUNT(missing.n) "
                  + "FROM tagged AS t0 "
                  + joins
                  + " LEFT/* padded */JOIN tagged AS missing ON missing.n = t0.n + 100"

              match runDefault (newStore ()) sql with
              | ResultSet(_, [ [ Some "9"; Some "45"; Some "9"; Some "0" ] ]) -> ()
              | other -> failtestf "expected the long join chain aggregate, got %A" other

          testCase "deep correlated and derived subqueries retain every enclosing scope"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE numbers (n INT PRIMARY KEY)" |> ignore
              runDefault store "INSERT INTO numbers VALUES (1),(2),(3),(4)" |> ignore

              let correlated =
                  [ 12 .. -1 .. 1 ]
                  |> List.fold
                      (fun inner level ->
                          sprintf
                              "EXISTS/* level %d */(SELECT 1 FROM numbers AS e%d WHERE e%d/* qualifier */.n = root.n AND %s)"
                              level
                              level
                              level
                              inner)
                      "TRUE"

              let correlatedSql = sprintf "SELECT root.n FROM numbers AS root WHERE %s ORDER BY root.n" correlated

              match runDefault store correlatedSql with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ]; [ Some "4" ] ] correlatedSql
              | other -> failtestf "expected deeply correlated rows, got %A" other

              let derived =
                  [ 1..28 ]
                  |> List.fold
                      (fun inner level ->
                          sprintf
                              "SELECT d%d.n/* arithmetic */+1 AS n FROM (/* layer %d */%s) AS d%d"
                              (level - 1)
                              level
                              inner
                              (level - 1))
                      "SELECT 1 AS n"

              let derivedSql = sprintf "WITH seed AS (SELECT 1) SELECT final.n FROM (/* outer */%s) AS final" derived

              match runDefault store derivedSql with
              | ResultSet(_, [ [ Some "29" ] ]) -> ()
              | other -> failtestf "expected the deeply derived value, got %A" other

          testCase "recursive, lateral, JSON, values, window, and quantified sources compose"
          <| fun _ ->
              let sql =
                  "WITH RECURSIVE nums(n) AS ("
                  + "SELECT 1 UNION ALL SELECT n + 1 FROM nums WHERE n < 4), "
                  + "windowed AS (SELECT n, SUM(n) OVER (ORDER BY n) AS running FROM nums) "
                  + "SELECT labels.label, source.n, doubled.twice, jt.v, windowed.running, "
                  + "(SELECT COUNT(*) FROM nums AS probe WHERE probe.n <= source.n) "
                  + "FROM nums AS source "
                  + "JOIN (VALUES ROW(1,'one'),ROW(2,'two'),ROW(3,'three'),ROW(4,'four')) AS labels(id,label) "
                  + "ON labels.id = source.n "
                  + "JOIN LATERAL (SELECT source.n * 2 AS twice WHERE source.n MOD 2 = 0) AS doubled ON TRUE "
                  + "JOIN JSON_TABLE('[4,8]', '$[*]' COLUMNS(v INT PATH '$')) AS jt ON jt.v = doubled.twice "
                  + "LEFT JOIN windowed ON windowed.n = source.n "
                  + "WHERE source.n = ANY (SELECT n FROM nums WHERE n >= 2) ORDER BY source.n"

              let expected =
                  [ [ Some "two"; Some "2"; Some "4"; Some "4"; Some "3"; Some "2" ]
                    [ Some "four"; Some "4"; Some "8"; Some "8"; Some "10"; Some "4" ] ]

              let mutations =
                  whitespaceRuns sql
                  |> Array.indexed
                  |> Array.collect (fun (index, run) ->
                      let block = injectAt sql run "/* execution boundary */"

                      if index % 4 = 0 then
                          [| block
                             injectAt sql run "# execution boundary\n"
                             injectAt sql run "-- execution boundary\n"
                             injectAt sql run " /*!99999 ignored_tokens */ " |]
                      else
                          [| block |])
                  |> Array.toList

              Expect.isGreaterThan mutations.Length 100 "many independent comment placements execute"

              let store = newStore ()

              for candidate in sql :: mutations do
                  match runDefault store candidate with
                  | ResultSet(_, rows) -> Expect.equal rows expected candidate
                  | other -> failtestf "expected every composed source to agree, got %A from %s" other candidate

          // NATURAL/USING expectations below were verified against a
          // real MySQL 8.4 (same schema/data, differential probe) —
          // column order, coalesced values, and padding all match.
          testCase "NATURAL JOIN matches on every common column and coalesces SELECT *"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t1 (i INT, j INT, m INT)" |> ignore
              runDefault store "CREATE TABLE t2 (k INT, j INT, m INT)" |> ignore
              runDefault store "INSERT INTO t1 VALUES (1,10,100),(2,20,200),(3,30,300)" |> ignore
              runDefault store "INSERT INTO t2 VALUES (11,10,100),(22,20,999),(33,99,333)" |> ignore

              // only the row where BOTH j and m match survives; SELECT
              // * order is the coalesced commons, then left-rest, then
              // right-rest.
              match runDefault store "SELECT * FROM t1 NATURAL JOIN t2" with
              | ResultSet([ "j"; "m"; "i"; "k" ], [ [ Some "10"; Some "100"; Some "1"; Some "11" ] ]) -> ()
              | other -> failtestf "expected the coalesced commons-first row, got %A" other

          testCase "NATURAL LEFT/RIGHT JOIN pad with the preserved side's coalesced values"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t1 (i INT, j INT, m INT)" |> ignore
              runDefault store "CREATE TABLE t2 (k INT, j INT, m INT)" |> ignore
              runDefault store "INSERT INTO t1 VALUES (1,10,100),(2,20,200),(3,30,300)" |> ignore
              runDefault store "INSERT INTO t2 VALUES (11,10,100),(22,20,999),(33,99,333)" |> ignore

              match runDefault store "SELECT j, m, i, k FROM t1 NATURAL LEFT JOIN t2 ORDER BY i" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "10"; Some "100"; Some "1"; Some "11" ]
                        [ Some "20"; Some "200"; Some "2"; None ]
                        [ Some "30"; Some "300"; Some "3"; None ] ]
                      "left rows keep their own j/m when unmatched"
              | other -> failtestf "expected LEFT-padded coalesced rows, got %A" other

              // RIGHT puts the right table's remaining columns before
              // the left's, and unmatched right rows keep their values.
              match runDefault store "SELECT j, m, k, i FROM t1 NATURAL RIGHT JOIN t2 ORDER BY k" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "10"; Some "100"; Some "11"; Some "1" ]
                        [ Some "20"; Some "999"; Some "22"; None ]
                        [ Some "99"; Some "333"; Some "33"; None ] ]
                      "right rows keep their own j/m when unmatched"
              | other -> failtestf "expected RIGHT-padded coalesced rows, got %A" other

          testCase "JOIN ... USING coalesces the listed column and keeps both sides' other columns"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t1 (i INT, j INT, m INT)" |> ignore
              runDefault store "CREATE TABLE t2 (k INT, j INT, m INT)" |> ignore
              runDefault store "INSERT INTO t1 VALUES (1,10,100),(2,20,200),(3,30,300)" |> ignore
              runDefault store "INSERT INTO t2 VALUES (11,10,100),(22,20,999),(33,99,333)" |> ignore

              // j coalesced once, both m's survive (t1.m then t2.m).
              match runDefault store "SELECT j, i, t1.m, k, t2.m FROM t1 JOIN t2 USING (j) ORDER BY j" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "10"; Some "1"; Some "100"; Some "11"; Some "100" ]
                        [ Some "20"; Some "2"; Some "200"; Some "22"; Some "999" ] ]
                      "coalesced j, both m's in place"
              | other -> failtestf "expected the USING-joined rows, got %A" other

          testCase "a USING column missing from either side is MySQL's 1054 from-clause error"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t1 (i INT)" |> ignore
              runDefault store "CREATE TABLE t2 (k INT)" |> ignore

              match runDefault store "SELECT * FROM t1 JOIN t2 USING (nope)" with
              | Err(1054, msg) -> Expect.stringContains msg "from clause" "message names the from clause"
              | other -> failtestf "expected 1054 for a missing USING column, got %A" other

          testCase "NATURAL JOIN with no common columns is the Cartesian product"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE n1 (a INT, b INT)" |> ignore
              runDefault store "CREATE TABLE n2 (c INT, d INT)" |> ignore
              runDefault store "INSERT INTO n1 VALUES (1,2)" |> ignore
              runDefault store "INSERT INTO n2 VALUES (3,4)" |> ignore

              match runDefault store "SELECT a, b, c, d FROM n1 NATURAL JOIN n2" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1"; Some "2"; Some "3"; Some "4" ] ] "every pair, no coalescing"
              | other -> failtestf "expected the Cartesian product, got %A" other

          testCase "chained NATURAL/USING joins fold left to right, commons first each step"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t1 (i INT, j INT, m INT)" |> ignore
              runDefault store "CREATE TABLE t2 (k INT, j INT, m INT)" |> ignore
              runDefault store "CREATE TABLE t3 (j INT, n INT)" |> ignore
              runDefault store "INSERT INTO t1 VALUES (1,10,100),(2,20,200),(3,30,300)" |> ignore
              runDefault store "INSERT INTO t2 VALUES (11,10,100),(22,20,999),(33,99,333)" |> ignore
              runDefault store "INSERT INTO t3 VALUES (10, 7), (20, 8)" |> ignore

              // t1 NATURAL t2 coalesces (j, m); then NATURAL t3 moves
              // j to the front again: j, m, i, k, n.
              match runDefault store "SELECT j, m, i, k, n FROM t1 NATURAL JOIN t2 NATURAL JOIN t3 ORDER BY j" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "10"; Some "100"; Some "1"; Some "11"; Some "7" ] ] "second join's common j first, rest preserved"
              | other -> failtestf "expected the chained natural join, got %A" other

              // t1 JOIN t2 USING (j) coalesces j; NATURAL t3 finds only
              // j again: j, i, t1.m, k, t2.m, n.
              match runDefault store "SELECT j, i, t1.m, k, t2.m, n FROM t1 JOIN t2 USING (j) NATURAL JOIN t3 ORDER BY j" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "10"; Some "1"; Some "100"; Some "11"; Some "100"; Some "7" ]
                        [ Some "20"; Some "2"; Some "200"; Some "22"; Some "999"; Some "8" ] ]
                      "mixed USING-then-NATURAL chain"
              | other -> failtestf "expected the mixed chain, got %A" other

          testCase "unqualified references to a coalesced column see the COALESCE, and qualified stars stay untouched"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t1 (i INT, j INT, m INT)" |> ignore
              runDefault store "CREATE TABLE t2 (k INT, j INT, m INT)" |> ignore
              runDefault store "INSERT INTO t1 VALUES (1,10,100),(2,20,200),(3,30,300)" |> ignore
              runDefault store "INSERT INTO t2 VALUES (11,10,100),(22,20,999),(33,99,333)" |> ignore

              // unqualified j in WHERE resolves to the coalesced column
              match runDefault store "SELECT i FROM t1 NATURAL LEFT JOIN t2 WHERE j IS NOT NULL ORDER BY i" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ] "every left row has a coalesced j"
              | other -> failtestf "expected unqualified j to resolve, got %A" other

              // t1.* keeps t1's own columns, common ones included
              match runDefault store "SELECT t1.* FROM t1 NATURAL JOIN t2" with
              | ResultSet([ "i"; "j"; "m" ], [ [ Some "1"; Some "10"; Some "100" ] ]) -> ()
              | other -> failtestf "expected t1's own full column list, got %A" other

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
              | other -> failtestf "expected error 1052 (ambiguous ORDER BY fallback), got %A" other

          testCase "hash join set-equals a reference nested-loop join, over randomized tables (pure equi, equi+residual, and a non-extractable arithmetic ON that must fall back)"
          <| fun _ ->
              let rnd = System.Random(20260817)

              let readRows (store: Store) (sql: string) : (int64 * int64 * int64 * int64) list =
                  match runDefault store sql with
                  | ResultSet(_, rows) ->
                      rows
                      |> List.map (function
                          | [ Some a; Some b; Some c; Some d ] -> int64 a, int64 b, int64 c, int64 d
                          | other -> failtestf "expected 4 non-null columns, got %A" other)
                  | other -> failtestf "expected a resultset for %s, got %A" sql other

              for _ in 1 .. 30 do
                  let store = newStore ()
                  runDefault store "CREATE TABLE l (k INT, x INT)" |> ignore
                  runDefault store "CREATE TABLE r (k INT, y INT)" |> ignore

                  // Small key range (0..3) on purpose, so most runs
                  // produce duplicate keys on both sides — exercising
                  // the hash join's one-key-to-many-rows bucket, not
                  // just a 1:1 lookup.
                  let leftRows = [ for _ in 1 .. rnd.Next(1, 8) -> int64 (rnd.Next(0, 4)), int64 (rnd.Next(0, 10)) ]
                  let rightRows = [ for _ in 1 .. rnd.Next(1, 8) -> int64 (rnd.Next(0, 4)), int64 (rnd.Next(0, 10)) ]
                  let valuesOf (rows: (int64 * int64) list) = rows |> List.map (fun (a, b) -> sprintf "(%d, %d)" a b) |> String.concat ", "

                  runDefault store (sprintf "INSERT INTO l VALUES %s" (valuesOf leftRows)) |> ignore
                  runDefault store (sprintf "INSERT INTO r VALUES %s" (valuesOf rightRows)) |> ignore

                  // Pure equi ON — extractEquiKeys finds one key pair
                  // and no residual, so this is the hash path end to
                  // end.
                  let referenceEqui = [ for lk, lx in leftRows do for rk, ry in rightRows do if lk = rk then yield lk, lx, rk, ry ]

                  Expect.equal
                      (readRows store "SELECT l.k, l.x, r.k, r.y FROM l JOIN r ON l.k = r.k" |> List.sort)
                      (referenceEqui |> List.sort)
                      "pure equi-join hash path"

                  // Equi + residual ON — the equi key still drives the
                  // hash probe; `l.x > 1` is the leftover conjunct
                  // applied per matched candidate.
                  let referenceResidual = referenceEqui |> List.filter (fun (_, lx, _, _) -> lx > 1L)

                  Expect.equal
                      (readRows store "SELECT l.k, l.x, r.k, r.y FROM l JOIN r ON l.k = r.k AND l.x > 1" |> List.sort)
                      (referenceResidual |> List.sort)
                      "equi-plus-residual ON"

                  // `l.k + 1 = r.k` has no `QualifiedCol = QualifiedCol`
                  // conjunct at all, so `extractEquiKeys` reports no
                  // keys and this falls back to the lazy nested loop
                  // entirely — still has to come out correct.
                  let referenceArithmetic = [ for lk, lx in leftRows do for rk, ry in rightRows do if lk + 1L = rk then yield lk, lx, rk, ry ]

                  Expect.equal
                      (readRows store "SELECT l.k, l.x, r.k, r.y FROM l JOIN r ON l.k + 1 = r.k" |> List.sort)
                      (referenceArithmetic |> List.sort)
                      "non-extractable arithmetic ON falls back to the nested loop and stays correct"

          testCase "a low-selectivity non-equi JOIN returns only matched rows"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE l (n INT)" |> ignore
              runDefault store "CREATE TABLE r (n INT)" |> ignore

              let n = 800
              let values = [ for i in 1 .. n -> sprintf "(%d)" i ] |> String.concat ", "
              runDefault store (sprintf "INSERT INTO l VALUES %s" values) |> ignore
              runDefault store (sprintf "INSERT INTO r VALUES %s" values) |> ignore

              let sql = "SELECT l.n, r.n FROM l JOIN r ON l.n + 1 = r.n"
              let result = runDefault store sql

              match result with
              | ResultSet(_, rows) -> Expect.equal (List.length rows) (n - 1) "one match per row except the last"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "a non-equi JOIN rejects more than one million candidate pairs"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE numbers (n INT)" |> ignore
              let values = [ 1..1_001 ] |> List.map (sprintf "(%d)") |> String.concat ","
              runDefault store ("INSERT INTO numbers VALUES " + values) |> ignore

              match runDefault store "SELECT a.n FROM numbers a JOIN numbers b ON a.n + b.n > 0 LIMIT 1" with
              | Err(1105, _) -> ()
              | other -> failtestf "expected 1105 for the oversized join, got %A" other

          testCase "a chain of Cartesian joins streams into LIMIT"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE numbers (n INT)" |> ignore
              let values = [ 1..100 ] |> List.map (sprintf "(%d)") |> String.concat ","
              runDefault store ("INSERT INTO numbers VALUES " + values) |> ignore

              match runDefault store "SELECT a.n FROM numbers a JOIN numbers b ON 1=1 JOIN numbers c ON 1=1 JOIN numbers d ON 1=1 LIMIT 1" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected LIMIT to consume one Cartesian row, got %A" other

          testCase "hash join on string keys matches case-insensitively, and trailing spaces stay distinct (NO PAD)"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE l (name VARCHAR(20))" |> ignore
              runDefault store "CREATE TABLE r (name VARCHAR(20), tag VARCHAR(20))" |> ignore
              runDefault store "INSERT INTO l VALUES ('Alice'), ('bob  '), ('Carol')" |> ignore
              runDefault store "INSERT INTO r VALUES ('alice', 'x'), ('BOB', 'y'), ('dave', 'z')" |> ignore

              match runDefault store "SELECT l.name, r.tag FROM l JOIN r ON l.name = r.name ORDER BY l.name" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "Alice"; Some "x" ] ]
                      "utf8mb4_0900_ai_ci folds case but not trailing spaces: 'Alice'/'alice' join, 'bob  '/'BOB' don't"
              | other -> failtestf "expected case-insensitive-only matches, got %A" other

          testCase "UPDATE ... JOIN hash-matches string keys case-insensitively, trailing spaces distinct"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE accounts2 (name VARCHAR(20), balance INT)" |> ignore
              runDefault store "CREATE TABLE rates (name VARCHAR(20), bonus INT)" |> ignore
              runDefault store "INSERT INTO accounts2 VALUES ('Alice', 0), ('bob  ', 0)" |> ignore
              runDefault store "INSERT INTO rates VALUES ('alice', 10), ('BOB', 20)" |> ignore

              match runDefault store "UPDATE accounts2 a JOIN rates r ON a.name = r.name SET a.balance = r.bonus" with
              | Affected 1UL -> ()
              | other -> failtestf "expected only 'Alice'/'alice' to match, got %A" other

              match runDefault store "SELECT name, balance FROM accounts2 ORDER BY name" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "Alice"; Some "10" ]; [ Some "bob  "; Some "0" ] ] "'bob  ' vs 'BOB' doesn't match under NO PAD"
              | other -> failtestf "expected a resultset, got %A" other ]
