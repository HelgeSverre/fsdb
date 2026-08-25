module Fsdb.Tests.ViewTests

open System.Threading
open System.Threading.Tasks
open Expecto
open Fsdb.Executor

let private run = TestSupport.Sql.executeDefault
let private expectOk = TestSupport.Sql.expectOk
let private rows = TestSupport.Sql.rows

let private setup () =
    let store = Fsdb.Storage.create ()
    expectOk (run store "CREATE TABLE vendors (id INT PRIMARY KEY, name VARCHAR(100))") "create vendors"
    expectOk (run store "CREATE TABLE receipts (id INT PRIMARY KEY, vendor_id INT, total DECIMAL(10,2), confidence DOUBLE)") "create receipts"
    expectOk (run store "INSERT INTO vendors VALUES (1, 'Acme'), (2, 'Nordic')") "seed vendors"
    expectOk (run store "INSERT INTO receipts VALUES (1, 1, 42.50, 0.97), (2, 2, 8.40, 0.94)") "seed receipts"
    store

let tests =
    testList
        "views"
        [ testCase "a grouped join view is live and supports an outer filter"
          <| fun _ ->
              let store = setup ()

              expectOk
                  (run
                      store
                      "CREATE VIEW vendor_stats AS SELECT v.id AS vendor_id, v.name AS vendor, COUNT(r.id) AS receipt_count, SUM(r.total) AS total_spend, AVG(r.confidence) AS avg_confidence FROM vendors v LEFT JOIN receipts r ON r.vendor_id = v.id GROUP BY v.id, v.name")
                  "create view"

              Expect.equal
                  (rows store "SELECT vendor, receipt_count, total_spend FROM vendor_stats WHERE vendor_id = 1")
                  [ [ Some "Acme"; Some "1"; Some "42.50" ] ]
                  "initial aggregate"

              expectOk (run store "INSERT INTO receipts VALUES (3, 1, 7.50, 0.91)") "insert after create"

              Expect.equal
                  (rows store "SELECT vendor, receipt_count, total_spend FROM vendor_stats WHERE vendor_id = 1")
                  [ [ Some "Acme"; Some "2"; Some "50.00" ] ]
                  "the view reevaluates its stored SELECT"

          testCase "a direct single-table view accepts UPDATE and DELETE"
          <| fun _ ->
              let store = setup ()
              expectOk (run store "CREATE VIEW vendor_names AS SELECT id, name FROM vendors") "create view"
              expectOk (run store "UPDATE vendor_names SET name = 'Updated' WHERE id = 1") "update through view"
              expectOk (run store "DELETE FROM vendor_names WHERE id = 2") "delete through view"
              expectOk (run store "INSERT INTO vendor_names VALUES (3, 'Inserted')") "insert through view"

              Expect.equal
                  (rows store "SELECT id, name FROM vendors ORDER BY id")
                  [ [ Some "1"; Some "Updated" ]; [ Some "3"; Some "Inserted" ] ]
                  "writes reach the base table"

              Expect.equal
                  (rows store "SELECT IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA = 'fsdb' AND TABLE_NAME = 'vendor_names'")
                  [ [ Some "YES" ] ]
                  "metadata reports the supported writable shape"

          testCase "a direct view streams an ordered limit from its base table"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE source (id INT PRIMARY KEY, visible INT, hidden INT)") "create source"

              let values =
                  [ 1 .. 100 ]
                  |> List.map (fun id -> sprintf "(%d,%d,%d)" id (id % 2) (id * 10))
                  |> String.concat ","

              expectOk (run store ("INSERT INTO source VALUES " + values)) "seed source"
              expectOk (run store "CREATE VIEW visible_rows AS SELECT id, visible FROM source WHERE visible = 1") "create view"

              let mutable touches = 0
              let registry =
                  Fsdb.Functions.builtins
                  |> Fsdb.Functions.registerScalar "TOUCH" (fun _ ->
                      touches <- touches + 1
                      Fsdb.Value.VInt 1L)

              match TestSupport.Sql.execute store registry "SELECT id FROM visible_rows WHERE TOUCH(id) ORDER BY id LIMIT 3" with
              | ResultSet(_, result) ->
                  Expect.equal result [ [ Some "1" ]; [ Some "3" ]; [ Some "5" ] ] "ordered rows"
                  Expect.isLessThanOrEqual touches 6 "the base index stops after enough visible rows"
              | other -> failtestf "expected streamed view rows, got %A" other

              expectOk (run store "CREATE VIEW renamed_rows AS SELECT id AS public_id, visible AS score FROM source") "create renamed view"

              Expect.equal
                  (rows store "SELECT public_id, score FROM renamed_rows ORDER BY public_id LIMIT 1")
                  [ [ Some "1"; Some "1" ] ]
                  "view aliases survive merging"

              match run store "SELECT hidden FROM visible_rows" with
              | Err(1054, _) -> ()
              | other -> failtestf "expected hidden-column rejection, got %A" other

          testCase "a grouped view rejects UPDATE"
          <| fun _ ->
              let store = setup ()
              expectOk (run store "CREATE VIEW totals AS SELECT vendor_id, SUM(total) AS total FROM receipts GROUP BY vendor_id") "create view"

              match run store "UPDATE totals SET total = 0" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected non-updatable view error, got %A" other

          testCase "a direct view predicate limits UPDATE and DELETE but not INSERT"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE accounts (id INT PRIMARY KEY, name VARCHAR(20), score INT)") "create accounts"
              expectOk (run store "INSERT INTO accounts VALUES (1, 'visible', 10), (2, 'hidden', 1)") "seed accounts"
              expectOk (run store "CREATE VIEW visible_accounts AS SELECT id, name FROM accounts WHERE score >= 5") "create view"

              match run store "UPDATE visible_accounts SET name = 'hidden' WHERE visible_accounts.score = 10" with
              | Err(1054, _) -> ()
              | other -> failtestf "expected hidden predicate-column rejection, got %A" other

              expectOk (run store "UPDATE visible_accounts SET name = 'updated'") "update view"
              expectOk (run store "DELETE FROM visible_accounts WHERE id = 1") "delete view"
              expectOk (run store "INSERT INTO visible_accounts VALUES (3, 'invisible')") "insert view"

              Expect.equal
                  (rows store "SELECT id, name, score FROM accounts ORDER BY id")
                  [ [ Some "2"; Some "hidden"; Some "1" ]; [ Some "3"; Some "invisible"; None ] ]
                  "only rows selected by the view predicate are updateable or deletable"

          testCase "WITH CHECK OPTION validates direct view writes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE guarded_rows (id INT PRIMARY KEY, n INT)") "create table"
              expectOk
                  (run store "CREATE VIEW guarded AS SELECT id, n FROM guarded_rows WHERE n > 0 WITH CHECK OPTION")
                  "create guarded view"

              expectOk (run store "INSERT INTO guarded VALUES (1, 1)") "insert visible row"

              [ "INSERT INTO guarded VALUES (2, 0)"
                "UPDATE guarded SET n = 0 WHERE id = 1"
                "REPLACE INTO guarded VALUES (2, 0)"
                "INSERT INTO guarded VALUES (1, 2) ON DUPLICATE KEY UPDATE n = 0" ]
              |> List.iter (fun sql ->
                  match run store sql with
                  | Err(1369, "CHECK OPTION failed 'fsdb.guarded'") -> ()
                  | other -> failtestf "expected CHECK OPTION failure for %s, got %A" sql other)

              expectOk (run store "UPDATE IGNORE guarded SET n = 0 WHERE id = 1") "ignore guarded update"

              Expect.equal (rows store "SELECT * FROM guarded_rows") [ [ Some "1"; Some "1" ] ] "failed writes are atomic"

              match run store "SELECT CHECK_OPTION, IS_UPDATABLE FROM information_schema.views WHERE table_name = 'guarded'" with
              | ResultSet(_, [ [ Some "CASCADED"; Some "YES" ] ]) -> ()
              | other -> failtestf "expected guarded view metadata, got %A" other

              match Fsdb.InformationSchema.showCreateView store.Catalog "fsdb" "guarded" with
              | Ok(_, [ [ _; Some ddl; _; _ ] ]) ->
                  Expect.stringContains ddl "WITH CASCADED CHECK OPTION" "SHOW CREATE retains the option"
              | other -> failtestf "expected SHOW CREATE VIEW, got %A" other

              match run store "CREATE VIEW grouped AS SELECT n, COUNT(*) AS c FROM guarded_rows GROUP BY n WITH CHECK OPTION" with
              | Err(1368, "CHECK OPTION on non-updatable view 'fsdb.grouped'") -> ()
              | other -> failtestf "expected non-updatable CHECK OPTION failure, got %A" other

              expectOk (run store "CREATE VIEW check_text AS SELECT 'WITH CHECK OPTION' AS phrase") "create literal view"
              Expect.equal (rows store "SELECT * FROM check_text") [ [ Some "WITH CHECK OPTION" ] ] "literal tail is not a view clause"

          testCase "a view predicate with a subquery is not writable"
          <| fun _ ->
              let store = setup ()
              expectOk (run store "CREATE VIEW filtered AS SELECT id, name FROM vendors WHERE id IN (SELECT vendor_id FROM receipts)") "create view"

              match run store "UPDATE filtered SET name = 'blocked'" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected complex predicate refusal, got %A" other

          testCase "writable views do not expose unprojected base columns"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE accounts (id INT PRIMARY KEY, name VARCHAR(20), secret INT)") "create accounts"
              expectOk (run store "INSERT INTO accounts VALUES (1, 'Visible', 7)") "seed accounts"
              expectOk (run store "CREATE VIEW visible_accounts AS SELECT id, name FROM accounts") "create view"

              [ "UPDATE visible_accounts SET secret = 8"
                "UPDATE visible_accounts SET name = 'Hidden' WHERE secret = 7"
                "UPDATE visible_accounts SET name = 'Hidden' WHERE accounts.secret = 7"
                "UPDATE visible_accounts SET name = 'Hidden' ORDER BY secret LIMIT 1"
                "INSERT INTO visible_accounts(secret) VALUES (8)" ]
              |> List.iter (fun sql ->
                  match run store sql with
                  | Err(1054, _) -> ()
                  | other -> failtestf "expected hidden-column rejection for %s, got %A" sql other)

              expectOk
                  (run store "INSERT INTO visible_accounts VALUES (1, 'Duplicate') ON DUPLICATE KEY UPDATE name = VALUES(name)")
                  "upsert through view"

              expectOk (run store "REPLACE INTO visible_accounts VALUES (1, 'Replaced')") "replace values through view"
              expectOk (run store "REPLACE INTO visible_accounts SET id = 2, name = 'Set'") "replace set through view"
              expectOk (run store "CREATE TABLE incoming (id INT, name VARCHAR(20))") "create replace source"
              expectOk (run store "INSERT INTO incoming VALUES (3, 'Selected')") "seed replace source"
              expectOk (run store "REPLACE INTO visible_accounts SELECT id, name FROM incoming") "replace select through view"

              Expect.equal
                  (rows store "SELECT id, name, secret FROM accounts ORDER BY id")
                  [ [ Some "1"; Some "Replaced"; None ]; [ Some "2"; Some "Set"; None ]; [ Some "3"; Some "Selected"; None ] ]
                  "view upserts and replace forms map only exposed columns"

          testCase "view writes recheck the definer's base-table privileges"
          <| fun _ ->
              let store = setup ()
              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let root = Fsdb.Session.create 1 store
              let root = apply root "CREATE USER owner"
              let root = apply root "CREATE USER writer"
              let root = apply root "GRANT SELECT, UPDATE, INSERT ON fsdb.vendors TO owner"
              let root = apply root "GRANT CREATE VIEW ON fsdb.* TO owner"
              let owner = { Fsdb.Session.create 2 store with User = "owner" }
              let _owner = apply owner "CREATE VIEW writable_vendors AS SELECT id, name FROM vendors"
              let root = apply root "GRANT UPDATE, INSERT ON fsdb.writable_vendors TO writer"
              let writer = { Fsdb.Session.create 3 store with User = "writer" }
              let _root = apply root "REVOKE UPDATE, INSERT ON fsdb.vendors FROM owner"

              match Fsdb.QueryHandler.handle writer "UPDATE writable_vendors SET name = 'Blocked' WHERE id = 1" |> snd with
              | Err(1142, message) -> Expect.stringContains message "vendors" "revoked base table named"
              | other -> failtestf "expected definer privilege failure, got %A" other

              match Fsdb.QueryHandler.handle writer "INSERT INTO writable_vendors VALUES (3, 'Blocked')" |> snd with
              | Err(1142, message) -> Expect.stringContains message "vendors" "revoked base table named"
              | other -> failtestf "expected definer privilege failure, got %A" other

          testCase "view definitions reject user and system variables"
          <| fun _ ->
              let store = setup ()

              [ "CREATE VIEW user_variable AS SELECT @value"
                "CREATE VIEW system_variable AS SELECT @@max_connections" ]
              |> List.iter (fun sql ->
                  match run store sql with
                  | Err(1351, "View's SELECT contains a variable or parameter") -> ()
                  | other -> failtestf "expected view variable rejection, got %A" other)

          testCase "explicit view columns, nested views, replacement, and drop work"
          <| fun _ ->
              let store = setup ()
              expectOk (run store "CREATE VIEW amounts (receipt_id, amount) AS SELECT id, total FROM receipts") "create columns"
              expectOk (run store "CREATE VIEW large_amounts AS SELECT receipt_id, amount FROM amounts WHERE amount > 10") "create nested"

              Expect.equal (rows store "SELECT * FROM large_amounts") [ [ Some "1"; Some "42.50" ] ] "nested view"

              expectOk (run store "CREATE OR REPLACE VIEW large_amounts AS SELECT receipt_id, amount FROM amounts WHERE amount > 100") "replace"
              Expect.equal (rows store "SELECT * FROM large_amounts") [] "replacement definition"
              expectOk (run store "DROP VIEW amounts, large_amounts") "drop views"

              match run store "SELECT * FROM amounts" with
              | Err(1146, _) -> ()
              | other -> failtestf "expected missing view after DROP, got %A" other

          testCase "recursive view references fail cleanly"
          <| fun _ ->
              let store = setup ()
              expectOk (run store "CREATE VIEW looped AS SELECT * FROM looped") "create recursive definition"

              match run store "SELECT * FROM looped" with
              | Err(1462, message) -> Expect.stringContains message "recursive reference" "clear recursion error"
              | other -> failtestf "expected 1462, got %A" other

          testCase "view definitions persist through the WAL"
          <| fun _ ->
              TestSupport.withDirectory "view" (fun dir ->
                  let store = Fsdb.Storage.create ()
                  Fsdb.Persistence.attach dir store
                  expectOk (run store "CREATE TABLE t (id INT PRIMARY KEY)") "create table"
                  expectOk (run store "INSERT INTO t VALUES (1), (2)") "seed"
                  expectOk (run store "CREATE VIEW doubled AS SELECT id, id * 2 AS n FROM t") "create view"
                  expectOk (run store "CREATE VIEW positive AS SELECT id FROM t WHERE id > 0 WITH LOCAL CHECK OPTION") "create guarded view"

                  let reloaded = Fsdb.Persistence.load dir
                  Expect.equal
                      (rows reloaded "SELECT * FROM doubled ORDER BY id")
                      [ [ Some "1"; Some "2" ]; [ Some "2"; Some "4" ] ]
                      "reloaded view"

                  match run reloaded "INSERT INTO positive VALUES (0)" with
                  | Err(1369, "CHECK OPTION failed 'fsdb.positive'") -> ()
                  | other -> failtestf "expected persisted CHECK OPTION, got %A" other)

          testCase "SHOW and information_schema expose stored views"
          <| fun _ ->
              let store = setup ()
              let session = Fsdb.Session.create 1 store

              let session, created =
                  Fsdb.QueryHandler.handle session "CREATE VIEW totals AS SELECT vendor_id, SUM(total) AS total FROM receipts GROUP BY vendor_id"

              expectOk created "create through handler"

              match Fsdb.QueryHandler.handle session "SHOW FULL TABLES WHERE Table_type = 'VIEW'" |> snd with
              | ResultSet(_, [ [ Some "totals"; Some "VIEW" ] ]) -> ()
              | other -> failtestf "expected SHOW FULL TABLES view row, got %A" other

              Expect.equal
                  (rows store "SELECT TABLE_SCHEMA, TABLE_NAME, IS_UPDATABLE FROM information_schema.VIEWS WHERE TABLE_SCHEMA = 'fsdb'")
                  [ [ Some "fsdb"; Some "totals"; Some "NO" ] ]
                  "VIEWS row"

              Expect.equal
                  (rows store "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'fsdb' AND TABLE_NAME = 'totals'")
                  [ [ Some "totals"; Some "VIEW" ] ]
                  "TABLES row survives information_schema narrowing"

              match Fsdb.QueryHandler.handle session "SHOW CREATE VIEW totals" |> snd with
              | ResultSet(columns, [ row ]) ->
                  Expect.equal columns [ "View"; "Create View"; "character_set_client"; "collation_connection" ] "SHOW columns"
                  Expect.stringContains (row.[1] |> Option.defaultValue "") "CREATE VIEW `totals` AS" "SHOW statement"
              | other -> failtestf "expected SHOW CREATE VIEW row, got %A" other

          testCase "view projections retain introspection metadata without evaluating rows"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let session =
                  apply
                      session
                      "CREATE TABLE source (id INT AUTO_INCREMENT PRIMARY KEY, label VARCHAR(12) COLLATE utf8mb4_bin NOT NULL DEFAULT 'x', amount DECIMAL(8,2) DEFAULT 1.25)"

              let session = apply session "CREATE VIEW direct_meta AS SELECT id, label, amount FROM source"
              let session = apply session "CREATE VIEW computed_meta AS SELECT id AS item_id, label AS caption, amount + 1 AS adjusted, CONCAT(label, '!') AS tagged FROM source"
              let session = apply session "CREATE VIEW nested_meta AS SELECT item_id, caption, adjusted, tagged FROM computed_meta"
              let session = apply session "CREATE VIEW empty_meta AS SELECT id, label, amount + 1 AS adjusted FROM source WHERE 1 = 0"

              match
                  Fsdb.QueryHandler.handle
                      session
                      "SELECT table_name, column_name, column_default, is_nullable, column_type, collation_name FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name IN ('direct_meta', 'computed_meta', 'nested_meta', 'empty_meta') ORDER BY table_name, ordinal_position"
                  |> snd
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "computed_meta"; Some "item_id"; Some "0"; Some "NO"; Some "int"; None ]
                        [ Some "computed_meta"; Some "caption"; Some "x"; Some "NO"; Some "varchar(12)"; Some "utf8mb4_bin" ]
                        [ Some "computed_meta"; Some "adjusted"; None; Some "YES"; Some "decimal(9,2)"; None ]
                        [ Some "computed_meta"; Some "tagged"; None; Some "YES"; Some "varchar(13)"; Some "utf8mb4_bin" ]
                        [ Some "direct_meta"; Some "id"; Some "0"; Some "NO"; Some "int"; None ]
                        [ Some "direct_meta"; Some "label"; Some "x"; Some "NO"; Some "varchar(12)"; Some "utf8mb4_bin" ]
                        [ Some "direct_meta"; Some "amount"; Some "1.25"; Some "YES"; Some "decimal(8,2)"; None ]
                        [ Some "empty_meta"; Some "id"; Some "0"; Some "NO"; Some "int"; None ]
                        [ Some "empty_meta"; Some "label"; Some "x"; Some "NO"; Some "varchar(12)"; Some "utf8mb4_bin" ]
                        [ Some "empty_meta"; Some "adjusted"; None; Some "YES"; Some "decimal(9,2)"; None ]
                        [ Some "nested_meta"; Some "item_id"; Some "0"; Some "NO"; Some "int"; None ]
                        [ Some "nested_meta"; Some "caption"; Some "x"; Some "NO"; Some "varchar(12)"; Some "utf8mb4_bin" ]
                        [ Some "nested_meta"; Some "adjusted"; None; Some "YES"; Some "decimal(9,2)"; None ]
                        [ Some "nested_meta"; Some "tagged"; None; Some "YES"; Some "varchar(13)"; Some "utf8mb4_bin" ] ]
                      "direct, computed, nested, and empty views retain their projection shapes"
              | other -> failtestf "expected view column metadata, got %A" other

              match Fsdb.QueryHandler.handle session "SHOW COLUMNS FROM computed_meta" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "item_id"; Some "int"; Some "NO"; Some ""; Some "0"; Some "" ]
                        [ Some "caption"; Some "varchar(12)"; Some "NO"; Some ""; Some "x"; Some "" ]
                        [ Some "adjusted"; Some "decimal(9,2)"; Some "YES"; Some ""; None; Some "" ]
                        [ Some "tagged"; Some "varchar(13)"; Some "YES"; Some ""; None; Some "" ] ]
                      "SHOW COLUMNS uses the same projection metadata"
              | other -> failtestf "expected view columns, got %A" other

              match Fsdb.QueryHandler.handle session "DESCRIBE computed_meta" |> snd with
              | ResultSet(_, rows) -> Expect.equal (List.length rows) 4 "DESCRIBE uses the view projection shape"
              | other -> failtestf "expected described view columns, got %A" other

              match Fsdb.QueryHandler.handle session "SHOW TABLE STATUS LIKE 'direct_meta'" |> snd with
              | ResultSet(columns, [ row ]) ->
                  let value name = List.item (List.findIndex ((=) name) columns) row
                  Expect.equal (value "Name") (Some "direct_meta") "view name"
                  Expect.isNone (value "Engine") "views have no storage engine"
                  Expect.equal (value "Comment") (Some "VIEW") "view marker"
              | other -> failtestf "expected view table status, got %A" other

              let root = apply session "CREATE USER metadata_reader"
              let reader = { Fsdb.Session.create 2 store with User = "metadata_reader" }

              match Fsdb.QueryHandler.handle reader "SELECT column_name FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'direct_meta'" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected hidden view columns, got %A" other

              let _root = apply root "GRANT SELECT ON fsdb.direct_meta TO metadata_reader"

              match Fsdb.QueryHandler.handle reader "SELECT column_name FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'direct_meta' ORDER BY ordinal_position" |> snd with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "id" ]; [ Some "label" ]; [ Some "amount" ] ] "granted view columns become visible"
              | other -> failtestf "expected visible view columns, got %A" other

          testCase "CTE-backed views retain the CTE projection shape"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let session = apply session "CREATE TABLE source (amount DECIMAL(8,2), label VARCHAR(12) COLLATE utf8mb4_bin NOT NULL DEFAULT 'x')"
              let session = apply session "CREATE VIEW cte_meta AS WITH shaped AS (SELECT amount, label FROM source) SELECT amount, label FROM shaped"

              match Fsdb.QueryHandler.handle session "SELECT column_name, column_default, is_nullable, column_type, collation_name FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'cte_meta' ORDER BY ordinal_position" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "amount"; None; Some "YES"; Some "decimal(8,2)"; None ]
                        [ Some "label"; Some "x"; Some "NO"; Some "varchar(12)"; Some "utf8mb4_bin" ] ]
                      "CTE source columns remain declarative"
              | other -> failtestf "expected CTE view metadata, got %A" other

          testCase "union-backed views reconcile every branch's metadata"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let session = apply session "CREATE TABLE source (amount DECIMAL(8,2), label VARCHAR(12) COLLATE utf8mb4_bin NOT NULL DEFAULT 'x')"
              let session = apply session "CREATE VIEW union_meta AS SELECT amount, label FROM source UNION ALL SELECT 1, 'a'"

              match Fsdb.QueryHandler.handle session "SELECT column_name, column_default, is_nullable, column_type, collation_name FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'union_meta' ORDER BY ordinal_position" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "amount"; None; Some "YES"; Some "decimal(8,2)"; None ]
                        [ Some "label"; Some ""; Some "NO"; Some "varchar(12)"; Some "utf8mb4_bin" ] ]
                      "union metadata combines type, nullability, and collation without retaining source defaults"
              | other -> failtestf "expected union view metadata, got %A" other

          testCase "computed view projections retain MySQL result types"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let session = apply session "CREATE TABLE source (amount DECIMAL(8,2), label VARCHAR(12) COLLATE utf8mb4_bin NOT NULL DEFAULT 'x')"
              let session = apply session "CREATE VIEW computed_types AS SELECT 1 AS one, 'x' AS text_value, 1 = 1 AS int_cmp, 'x' = 'x' AS text_cmp, amount * 2 AS multiplied, amount / 2 AS divided, COUNT(*) AS counted, MIN(label) AS minimum FROM source"

              match Fsdb.QueryHandler.handle session "SELECT column_name, column_default, is_nullable, column_type, collation_name FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'computed_types' ORDER BY ordinal_position" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "one"; Some "0"; Some "NO"; Some "int"; None ]
                        [ Some "text_value"; Some ""; Some "NO"; Some "varchar(1)"; Some "utf8mb4_0900_ai_ci" ]
                        [ Some "int_cmp"; Some "0"; Some "NO"; Some "int"; None ]
                        [ Some "text_cmp"; Some "0"; Some "NO"; Some "int"; None ]
                        [ Some "multiplied"; None; Some "YES"; Some "decimal(9,2)"; None ]
                        [ Some "divided"; None; Some "YES"; Some "decimal(12,6)"; None ]
                        [ Some "counted"; Some "0"; Some "NO"; Some "bigint"; None ]
                        [ Some "minimum"; None; Some "YES"; Some "varchar(12)"; Some "utf8mb4_bin" ] ]
                      "computed expressions keep their declared result metadata"
              | other -> failtestf "expected computed view metadata, got %A" other

          testCase "integer literal widths retain MySQL view metadata"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store
              let session, result =
                  Fsdb.QueryHandler.handle
                      session
                      "CREATE VIEW literal_width AS SELECT 99999999 AS small_signed, 100000000 AS large_signed, 2147483647 AS int32_max, 2147483648 AS positive_large, -2147483649 AS negative_large, 9223372036854775807 AS max_signed, 9223372036854775808 AS unsigned_large, 18446744073709551615 AS max_unsigned"

              expectOk result "CREATE VIEW literal_width"

              match Fsdb.QueryHandler.handle session "SELECT column_name, column_default, is_nullable, column_type FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name = 'literal_width' ORDER BY ordinal_position" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "small_signed"; Some "0"; Some "NO"; Some "int" ]
                        [ Some "large_signed"; Some "0"; Some "NO"; Some "bigint" ]
                        [ Some "int32_max"; Some "0"; Some "NO"; Some "bigint" ]
                        [ Some "positive_large"; Some "0"; Some "NO"; Some "bigint" ]
                        [ Some "negative_large"; Some "0"; Some "NO"; Some "bigint" ]
                        [ Some "max_signed"; Some "0"; Some "NO"; Some "bigint" ]
                        [ Some "unsigned_large"; Some "0"; Some "NO"; Some "bigint unsigned" ]
                        [ Some "max_unsigned"; Some "0"; Some "NO"; Some "bigint unsigned" ] ]
                      "integer literals use MySQL view metadata"
              | other -> failtestf "expected integer literal view metadata, got %A" other

          testCase "recursive, union, and decimal aggregate views retain MySQL metadata"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let session = apply session "CREATE TABLE source (amount DECIMAL(5,2))"
              let session = apply session "CREATE VIEW recursive_meta AS WITH RECURSIVE seq (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 2) SELECT n FROM seq"
              let session = apply session "CREATE VIEW numeric_union_meta AS SELECT amount AS value FROM source UNION ALL SELECT 1234567890"
              let session = apply session "CREATE VIEW mixed_union_meta AS SELECT amount AS value FROM source UNION ALL SELECT 1234567890 UNION ALL SELECT 'abc'"
              let session = apply session "CREATE VIEW aggregate_meta AS SELECT SUM(amount) AS total, AVG(amount) AS average FROM source"

              match Fsdb.QueryHandler.handle session "SELECT table_name, column_name, column_default, is_nullable, column_type, collation_name FROM information_schema.columns WHERE table_schema = 'fsdb' AND table_name IN ('recursive_meta', 'numeric_union_meta', 'mixed_union_meta', 'aggregate_meta') ORDER BY table_name, ordinal_position" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "aggregate_meta"; Some "total"; None; Some "YES"; Some "decimal(27,2)"; None ]
                        [ Some "aggregate_meta"; Some "average"; None; Some "YES"; Some "decimal(9,6)"; None ]
                        [ Some "mixed_union_meta"; Some "value"; None; Some "YES"; Some "varchar(14)"; Some "utf8mb4_0900_ai_ci" ]
                        [ Some "numeric_union_meta"; Some "value"; None; Some "YES"; Some "decimal(12,2)"; None ]
                        [ Some "recursive_meta"; Some "n"; None; Some "YES"; Some "bigint"; None ] ]
                      "recursive, union, and aggregate descriptors retain their MySQL shapes"
              | other -> failtestf "expected view metadata, got %A" other

          testCase "a view reads with its definer privileges and observes later revokes"
          <| fun _ ->
              let store = setup ()
              let root = Fsdb.Session.create 1 store

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let root = apply root "CREATE USER owner"
              let root = apply root "CREATE USER reader"
              let root = apply root "GRANT SELECT ON fsdb.receipts TO owner"
              let root = apply root "GRANT CREATE VIEW ON fsdb.* TO owner"
              let owner = { Fsdb.Session.create 2 store with User = "owner" }
              let _owner = apply owner "CREATE VIEW owner_totals AS SELECT vendor_id, SUM(total) AS total FROM receipts GROUP BY vendor_id"
              let root = apply root "GRANT SELECT ON fsdb.owner_totals TO reader"
              let reader = { Fsdb.Session.create 3 store with User = "reader" }

              match Fsdb.QueryHandler.handle reader "SELECT total FROM owner_totals WHERE vendor_id = 1" |> snd with
              | ResultSet(_, [ [ Some "42.50" ] ]) -> ()
              | other -> failtestf "expected definer-backed read, got %A" other

              let _root = apply root "REVOKE SELECT ON fsdb.receipts FROM owner"

              match Fsdb.QueryHandler.handle reader "SELECT total FROM owner_totals WHERE vendor_id = 1" |> snd with
              | Err(1142, message) -> Expect.stringContains message "receipts" "revoked table named"
              | other -> failtestf "expected definer privilege failure after revoke, got %A" other

          testCase "a view retains its host-qualified definer"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = Fsdb.Session.create 1 store

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let root = apply root "CREATE TABLE source (id INT PRIMARY KEY)"
              let root = apply root "INSERT INTO source VALUES (1)"
              let root = apply root "CREATE USER 'owner'@'%'"
              let root = apply root "CREATE USER 'owner'@'localhost'"
              let root = apply root "CREATE USER reader"
              let root = apply root "GRANT CREATE VIEW ON fsdb.* TO 'owner'@'localhost'"
              let root = apply root "GRANT SELECT ON fsdb.source TO 'owner'@'localhost'"
              let root = apply root "GRANT SELECT ON fsdb.hosted_view TO reader"
              let owner = { Fsdb.Session.create 2 store with User = "owner"; AccountHost = "localhost" }
              let _owner = apply owner "CREATE VIEW hosted_view AS SELECT id FROM source"
              let reader = { Fsdb.Session.create 3 store with User = "reader" }

              match Fsdb.QueryHandler.handle reader "SELECT id FROM hosted_view" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the localhost definer's SELECT privilege, got %A" other

              Expect.equal
                  (rows store "SELECT definer FROM mysql.views WHERE view_name = 'hosted_view'")
                  [ [ Some "owner@localhost" ] ]
                  "stored full definer"

          testCase "a view evaluates CURRENT_USER as its definer"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = Fsdb.Session.create 1 store

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let root = apply root "CREATE USER owner"
              let root = apply root "CREATE USER writer"
              let root = apply root "GRANT CREATE VIEW ON fsdb.* TO owner"
              let root = apply root "GRANT SELECT ON fsdb.identity_view TO writer"
              let owner = { Fsdb.Session.create 2 store with User = "owner" }
              let writer = { Fsdb.Session.create 3 store with User = "writer" }
              let _owner =
                  apply
                      owner
                      "CREATE VIEW identity_view AS SELECT CURRENT_USER() AS definer_identity, USER() AS invoker_identity"

              match Fsdb.QueryHandler.handle writer "SELECT definer_identity, invoker_identity FROM identity_view" |> snd with
              | ResultSet(_, [ [ Some currentUser; Some invokingUser ] ]) ->
                  Expect.equal currentUser "owner@%" "definer identity"
                  Expect.equal invokingUser "writer@localhost" "invoker identity"
              | other -> failtestf "expected view identity row, got %A" other

          testCase "concurrent view creation stamps each account's definer"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = Fsdb.Session.create 1 store

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let root = apply root "CREATE TABLE source (id INT PRIMARY KEY)"
              let root = apply root "CREATE USER 'first'@'%'"
              let root = apply root "CREATE USER 'second'@'localhost'"
              let root = apply root "GRANT CREATE VIEW, SELECT ON fsdb.* TO 'first'@'%'"
              let _root = apply root "GRANT CREATE VIEW, SELECT ON fsdb.* TO 'second'@'localhost'"
              let first = { Fsdb.Session.create 2 store with User = "first" }
              let second = { Fsdb.Session.create 3 store with User = "second"; AccountHost = "localhost" }
              use ready = new CountdownEvent 32
              use start = new ManualResetEventSlim false

              [ 1 .. 32 ]
              |> List.map (fun index ->
                  let session = if index % 2 = 0 then first else second

                  Task.Factory.StartNew(
                      (fun () ->
                          ready.Signal() |> ignore
                          start.Wait()
                          let _, result = Fsdb.QueryHandler.handle session (sprintf "CREATE VIEW concurrent_%d AS SELECT id FROM source" index)
                          expectOk result (sprintf "create concurrent_%d" index)),
                      TaskCreationOptions.LongRunning
                  ))
              |> List.toArray
              |> fun tasks ->
                  ready.Wait()
                  start.Set()
                  Task.WaitAll tasks

              let expected =
                  [ 1 .. 32 ]
                  |> List.map (fun index -> sprintf "concurrent_%d" index, if index % 2 = 0 then "first@%" else "second@localhost")

              let actual =
                  rows store "SELECT view_name, definer FROM mysql.views WHERE view_name LIKE 'concurrent_%' ORDER BY view_name"
                  |> List.map (function
                      | [ Some name; Some definer ] -> name, definer
                      | row -> failtestf "expected view definer row, got %A" row)
                  |> List.sort

              Expect.equal actual (expected |> List.sort) "each concurrent view retains its creator"

          testCase "dropping a database removes its stored-object catalog rows"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE DATABASE discarded") "create database"
              expectOk (run store "CREATE TABLE discarded.source (id INT PRIMARY KEY)") "create source"
              expectOk (run store "CREATE TABLE discarded.audit (id INT PRIMARY KEY)") "create audit"
              expectOk (run store "CREATE VIEW discarded.ids AS SELECT id FROM discarded.source") "create view"

              expectOk
                  (run
                      store
                      "CREATE TRIGGER remember AFTER INSERT ON discarded.source FOR EACH ROW INSERT INTO discarded.audit VALUES (NEW.id)")
                  "create trigger"

              expectOk (run store "DROP DATABASE discarded") "drop database"
              Expect.equal (rows store "SELECT COUNT(*) FROM mysql.views WHERE view_schema = 'discarded'") [ [ Some "0" ] ] "view row removed"
              Expect.equal
                  (rows store "SELECT COUNT(*) FROM mysql.triggers WHERE trigger_schema = 'discarded'")
                  [ [ Some "0" ] ]
                  "trigger row removed" ]
