module Fsdb.Tests.ExecutorSubqueryTests

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

          testCase "IN rejects a subquery that returns more than one column"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t (a INT, b INT)" |> ignore
              runDefault store "INSERT INTO t VALUES (1, 2)" |> ignore

              match runDefault store "SELECT 1 IN (SELECT a, b FROM t)" with
              | Err(1241, "Operand should contain 1 column(s)") -> ()
              | other -> failtestf "expected MySQL error 1241, got %A" other

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

          testCase "NULL NOT IN (SELECT ...) survives when the subquery has no rows"
          <| fun _ ->
              // `NULL IN (<empty set>)` is FALSE, not UNKNOWN, so
              // `NOT IN` against an empty subquery must be TRUE
              // regardless of `v`'s own NULL-ness — the subquery has
              // to run before any NULL short-circuit.
              let store = newStore ()
              runDefault store "CREATE TABLE t (id INT, v INT)" |> ignore
              runDefault store "CREATE TABLE empty_t (id INT)" |> ignore
              runDefault store "INSERT INTO t VALUES (1, NULL), (2, 5)" |> ignore

              match runDefault store "SELECT id FROM t WHERE v NOT IN (SELECT id FROM empty_t) ORDER BY id" with
              | ResultSet([ "id" ], rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "both rows survive against an empty candidate set"
              | other -> failtestf "expected both rows to survive NOT IN against an empty subquery, got %A" other

          testCase "quantified comparisons fold empty inputs and NULLs with MySQL's three-valued logic"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE empty_values (n INT)" |> ignore
              runDefault store "CREATE TABLE values_with_null (n INT)" |> ignore
              runDefault store "INSERT INTO values_with_null VALUES (NULL), (5)" |> ignore

              match
                  runDefault
                      store
                      "SELECT 5 = ANY (SELECT n FROM empty_values), 5 = ALL (SELECT n FROM empty_values), NULL = ANY (SELECT n FROM empty_values), NULL = ALL (SELECT n FROM empty_values)"
              with
              | ResultSet(_, [ [ Some "0"; Some "1"; Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected quantified empty-set identities, got %A" other

              match
                  runDefault
                      store
                      "SELECT 5 = SOME (SELECT n FROM values_with_null), 5 = ALL (SELECT n FROM values_with_null), 5 <> ANY (SELECT n FROM values_with_null), 5 <> ALL (SELECT n FROM values_with_null)"
              with
              | ResultSet(_, [ [ Some "1"; None; None; Some "0" ] ]) -> ()
              | other -> failtestf "expected quantified NULL propagation, got %A" other

          testCase "quantified comparisons preserve correlation, coercion, collation, and the one-column requirement"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE outer_rows (id INT)" |> ignore
              runDefault store "CREATE TABLE inner_rows (owner_id INT, value INT)" |> ignore
              runDefault store "INSERT INTO outer_rows VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO inner_rows VALUES (1, 1), (1, 2), (2, NULL), (2, 3)" |> ignore
              runDefault store "CREATE TABLE binary_text (s VARCHAR(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin)" |> ignore
              runDefault store "INSERT INTO binary_text VALUES ('A'), ('a')" |> ignore
              runDefault store "CREATE TABLE enum_text (s ENUM('red', 'blue'))" |> ignore
              runDefault store "INSERT INTO enum_text VALUES ('red')" |> ignore
              runDefault store "CREATE TABLE pairs (a INT, b INT)" |> ignore
              runDefault store "INSERT INTO pairs VALUES (1, 2)" |> ignore

              match
                  runDefault
                      store
                      "SELECT id, id = ANY (SELECT value FROM inner_rows WHERE owner_id = outer_rows.id), id < ALL (SELECT value FROM inner_rows WHERE owner_id = outer_rows.id) FROM outer_rows ORDER BY id"
              with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "1"; Some "0" ]; [ Some "2"; None; None ]; [ Some "3"; Some "0"; Some "1" ] ] "each outer row evaluates its own candidate set"
              | other -> failtestf "expected correlated quantified results, got %A" other

              match runDefault store "SELECT s, s = ANY (SELECT 'a') FROM binary_text ORDER BY s" with
              | ResultSet(_, [ [ Some "A"; Some "0" ]; [ Some "a"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the left column collation to govern equality, got %A" other

              match runDefault store "SELECT 'A' = ANY (SELECT s FROM binary_text WHERE s = 'a'), 'A' < ANY (SELECT s FROM binary_text WHERE s = 'a')" with
              | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the subquery column collation to govern comparison, got %A" other

              match runDefault store "SELECT 'A' = ANY (SELECT s COLLATE utf8mb4_0900_ai_ci FROM binary_text WHERE s = 'a')" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the projected COLLATE to override the subquery column collation, got %A" other

              match runDefault store "SELECT 1 = ANY (SELECT s FROM enum_text)" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected an ENUM subquery result to retain its ordinal comparison, got %A" other

              match runDefault store "SELECT 'A' = ANY (SELECT s FROM (SELECT s FROM binary_text WHERE s = 'a') AS derived_binary), 1 = ANY (SELECT s FROM (SELECT s FROM enum_text) AS derived_enum)" with
              | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected derived subquery columns to retain collation and ENUM metadata, got %A" other

              match runDefault store "SELECT 'A' = ANY (SELECT d FROM (SELECT s COLLATE utf8mb4_0900_ai_ci AS d FROM binary_text WHERE s = 'a') AS derived_collated)" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected a derived projected COLLATE to govern comparison, got %A" other

              runDefault store "CREATE VIEW binary_view AS SELECT s FROM binary_text WHERE s = 'a'" |> ignore
              runDefault store "CREATE VIEW enum_view AS SELECT s FROM enum_text" |> ignore
              runDefault store "CREATE VIEW collated_binary_view AS SELECT s COLLATE utf8mb4_0900_ai_ci AS d FROM binary_text WHERE s = 'a'" |> ignore

              match runDefault store "SELECT 'A' = ANY (SELECT s FROM binary_view), 1 = ANY (SELECT s FROM enum_view)" with
              | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected view subquery columns to retain collation and ENUM metadata, got %A" other

              match runDefault store "SELECT 'A' = ANY (SELECT d FROM collated_binary_view)" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected a view projected COLLATE to govern comparison, got %A" other

              match runDefault store "WITH binary_cte AS (SELECT s FROM binary_text WHERE s = 'a'), enum_cte AS (SELECT s FROM enum_text) SELECT 'A' = ANY (SELECT s FROM binary_cte), 1 = ANY (SELECT s FROM enum_cte)" with
              | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected CTE subquery columns to retain collation and ENUM metadata, got %A" other

              match runDefault store "WITH collated_cte AS (SELECT s COLLATE utf8mb4_0900_ai_ci AS d FROM binary_text WHERE s = 'a') SELECT 'A' = ANY (SELECT d FROM collated_cte)" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected a CTE projected COLLATE to govern comparison, got %A" other

              match runDefault store "SELECT 2 = ANY (SELECT '2')" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected numeric coercion to match, got %A" other

              match runDefault store "SELECT 1 = ANY (SELECT a, b FROM pairs)" with
              | Err(1241, "Operand should contain 1 column(s)") -> ()
              | other -> failtestf "expected MySQL error 1241, got %A" other

              match runDefault store "CREATE TABLE generated_quantified (n INT, q INT GENERATED ALWAYS AS (n = ANY (SELECT n FROM outer_rows)))" with
              | Err(3102, "Expression of generated column 'q' contains a disallowed function.") -> ()
              | other -> failtestf "expected MySQL error 3102, got %A" other

          testCase "quantified comparisons support every ordered comparison operator"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE values_to_compare (n INT)" |> ignore
              runDefault store "INSERT INTO values_to_compare VALUES (1), (2), (3)" |> ignore

              match
                  runDefault
                      store
                      "SELECT 2 = ANY (SELECT n FROM values_to_compare), 2 <> ALL (SELECT n FROM values_to_compare), 2 < ANY (SELECT n FROM values_to_compare), 2 <= ALL (SELECT n FROM values_to_compare WHERE n >= 2), 2 > SOME (SELECT n FROM values_to_compare), 2 >= ALL (SELECT n FROM values_to_compare WHERE n <= 2)"
              with
              | ResultSet(_, [ [ Some "1"; Some "0"; Some "1"; Some "1"; Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected every quantified comparison operator to agree with MySQL, got %A" other

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

          testCase "statement-stable IN and scalar subqueries run once across outer rows"
          <| fun _ ->
              let mutable touches = 0

              let touch (values: Value list) : Value =
                  touches <- touches + 1
                  List.head values

              let registry = registerScalar "TOUCH" touch builtins
              let store = newStore ()
              runDefault store "CREATE TABLE outer_rows (id INT)" |> ignore
              runDefault store "CREATE TABLE inner_rows (id INT)" |> ignore
              runDefault store "INSERT INTO outer_rows VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO inner_rows VALUES (1), (2)" |> ignore

              touches <- 0

              match run store registry "SELECT id FROM outer_rows WHERE id IN (SELECT TOUCH(id) FROM inner_rows) ORDER BY id" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "IN retains matching rows"
              | other -> failtestf "expected a resultset, got %A" other

              let inTouches = touches
              Expect.isLessThan inTouches 5 "the IN subquery is evaluated once, not once per outer row"

              touches <- 0

              match run store registry "SELECT id, (SELECT TOUCH(7)) AS probe FROM outer_rows ORDER BY id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "7" ]; [ Some "2"; Some "7" ]; [ Some "3"; Some "7" ] ] "scalar result is shared"
              | other -> failtestf "expected a resultset, got %A" other

              let scalarTouches = touches
              Expect.isLessThan scalarTouches 3 "the scalar subquery is evaluated once, not once per outer row"

              touches <- 0

              match run store registry "SELECT id, (SELECT TOUCH(outer_rows.id)) AS probe FROM outer_rows ORDER BY id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "1" ]; [ Some "2"; Some "2" ]; [ Some "3"; Some "3" ] ] "correlation stays per-row"
              | other -> failtestf "expected a resultset, got %A" other

              Expect.isGreaterThan touches scalarTouches "a correlated scalar subquery remains per-row"

              touches <- 0

              match run store registry "SELECT id FROM outer_rows WHERE id = ANY (SELECT TOUCH(id) FROM inner_rows) ORDER BY id" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "ANY retains matching rows"
              | other -> failtestf "expected a resultset, got %A" other

              let anyTouches = touches
              Expect.isLessThan anyTouches 5 "an uncorrelated ANY subquery runs once"

              touches <- 0

              match run store registry "SELECT id FROM outer_rows WHERE id <= ALL (SELECT TOUCH(id) FROM inner_rows) ORDER BY id" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ] ] "ALL retains only the universal match"
              | other -> failtestf "expected a resultset, got %A" other

              let allTouches = touches
              Expect.isLessThan allTouches 5 "an uncorrelated ALL subquery runs once"

              touches <- 0

              match
                  run
                      store
                      registry
                      "SELECT id FROM outer_rows WHERE id = ANY (SELECT TOUCH(id) FROM inner_rows WHERE outer_rows.id >= 0) ORDER BY id"
              with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "the correlated comparison still matches"
              | other -> failtestf "expected a resultset, got %A" other

              Expect.isGreaterThan touches allTouches "a correlated quantified subquery runs for every outer row"

          testCase "memoized integer IN preserves NULL and empty-set semantics"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE outer_rows (id INT)" |> ignore
              runDefault store "CREATE TABLE inner_rows (id INT)" |> ignore
              runDefault store "INSERT INTO outer_rows VALUES (1), (2), (NULL)" |> ignore
              runDefault store "INSERT INTO inner_rows VALUES (2), (NULL)" |> ignore

              match
                  runDefault
                      store
                      "SELECT id, id IN (SELECT id FROM inner_rows), id IN (SELECT id FROM inner_rows WHERE FALSE) FROM outer_rows ORDER BY id"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ None; None; Some "0" ]; [ Some "1"; None; Some "0" ]; [ Some "2"; Some "1"; Some "0" ] ]
                      "memoized membership retains three-valued IN behavior"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "stable integer IN narrows an indexed outer table"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE outer_rows (id INT PRIMARY KEY, label VARCHAR(10))" |> ignore
              runDefault store "CREATE TABLE inner_rows (id INT)" |> ignore
              runDefault store "INSERT INTO outer_rows VALUES (1, 'one'), (2, 'two'), (3, 'three')" |> ignore
              runDefault store "INSERT INTO inner_rows VALUES (3), (1), (1), (NULL)" |> ignore

              match runDefault store "SELECT id, label FROM outer_rows WHERE id IN (SELECT id FROM inner_rows) ORDER BY id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "one" ]; [ Some "3"; Some "three" ] ] "outer index candidates retain set semantics"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "EXISTS stops after its first matching row"
          <| fun _ ->
              let mutable touches = 0

              let touch (values: Value list) : Value =
                  touches <- touches + 1
                  List.head values

              let registry = registerScalar "TOUCH" touch builtins
              let store = newStore ()
              runDefault store "CREATE TABLE inner_rows (id INT)" |> ignore
              let values = [ for id in 1 .. 100 -> sprintf "(%d)" id ] |> String.concat ", "
              runDefault store (sprintf "INSERT INTO inner_rows VALUES %s" values) |> ignore

              match run store registry "SELECT EXISTS (SELECT 1 FROM inner_rows WHERE TOUCH(id) > 0) AS present" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected exists=1, got %A" other

              Expect.isLessThan touches 5 "EXISTS stops after its first matching row"

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

          testCase "derived joins filter multi-table UPDATE and DELETE targets"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t1 (id INT, n INT)" |> ignore
              runDefault store "CREATE TABLE source (id INT, n INT)" |> ignore
              runDefault store "INSERT INTO t1 VALUES (1, 10), (2, 20), (3, 30)" |> ignore
              runDefault store "INSERT INTO source VALUES (1, 5), (2, 0), (3, 7)" |> ignore

              match runDefault store "UPDATE t1 JOIN (SELECT id, n FROM source WHERE n > 0) dt ON t1.id = dt.id SET t1.n = dt.n" with
              | Affected 2UL -> ()
              | other -> failtestf "expected two updated rows, got %A" other

              match runDefault store "SELECT id, n FROM t1 ORDER BY id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "5" ]; [ Some "2"; Some "20" ]; [ Some "3"; Some "7" ] ] "the derived rows supply update values"
              | other -> failtestf "expected updated rows, got %A" other

              match runDefault store "DELETE t1 FROM t1 JOIN (SELECT id FROM source WHERE n = 0) dt ON t1.id = dt.id" with
              | Affected 1UL -> ()
              | other -> failtestf "expected one deleted row, got %A" other

              match runDefault store "SELECT id FROM t1 ORDER BY id" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "3" ] ] "only the physical target is deleted"
              | other -> failtestf "expected surviving rows, got %A" other

              match runDefault store "UPDATE t1 JOIN (SELECT id FROM source) dt ON t1.id = dt.id SET dt.id = 9" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected a non-updatable derived target error, got %A" other

              match runDefault store "EXPLAIN UPDATE t1 JOIN (SELECT id FROM source) dt ON t1.id = dt.id SET t1.n = 1" with
              | ResultSet _ -> ()
              | other -> failtestf "expected a derived UPDATE plan, got %A" other

          testCase "a scalar subquery's comparison is numeric, not lexicographic text"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE nums (n INT)" |> ignore
              runDefault store "INSERT INTO nums VALUES (2), (10), (9)" |> ignore

              match runDefault store "SELECT (SELECT MAX(n) FROM nums) > (SELECT MIN(n) FROM nums) AS r" with
              | ResultSet([ "r" ], [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected 10 > 2 to be true, got %A" other ]
