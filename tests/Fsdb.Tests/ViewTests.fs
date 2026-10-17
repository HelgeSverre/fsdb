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

          testCase "computed projections leave their base columns updatable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE measurements (id INT PRIMARY KEY, n INT NOT NULL, hidden INT DEFAULT 7)") "create table"
              expectOk (run store "INSERT INTO measurements VALUES (1, 10, 7), (2, -10, 7)") "seed table"
              expectOk (run store "CREATE VIEW measured AS SELECT id, n, n * 2 AS doubled FROM measurements") "create view"

              Expect.equal
                  (rows store "SELECT IS_UPDATABLE FROM information_schema.views WHERE table_schema = 'fsdb' AND table_name = 'measured'")
                  [ [ Some "YES" ] ]
                  "computed columns do not make the whole view read-only"

              expectOk (run store "UPDATE measured SET n = 11 WHERE doubled = 20") "update a base column through a computed predicate"
              expectOk (run store "DELETE FROM measured WHERE doubled = -20") "delete through a computed predicate"

              match run store "UPDATE measured SET doubled = 22 WHERE id = 1" with
              | Err(1348, "Column 'doubled' is not updatable") -> ()
              | other -> failtestf "expected computed-column update rejection, got %A" other

              match run store "INSERT INTO measured(id, n) VALUES (3, 30)" with
              | Err(1471, _) -> ()
              | other -> failtestf "expected the computed view not to be insertable, got %A" other

              Expect.equal
                  (rows store "SELECT id, n, hidden FROM measurements ORDER BY id")
                  [ [ Some "1"; Some "11"; Some "7" ] ]
                  "legal writes reach the base table"

          testCase "nested updatable views compose predicates and check options"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE nested_rows (id INT PRIMARY KEY, n INT NOT NULL)") "create table"
              expectOk (run store "INSERT INTO nested_rows VALUES (1, 10), (2, 30)") "seed table"
              expectOk (run store "CREATE VIEW positive_rows AS SELECT id, n FROM nested_rows WHERE n > 0") "create base view"

              expectOk
                  (run store "CREATE VIEW local_rows AS SELECT id, n FROM positive_rows WHERE n < 20 WITH LOCAL CHECK OPTION")
                  "create local view"

              expectOk
                  (run store "CREATE VIEW cascaded_rows AS SELECT id, n FROM positive_rows WHERE n < 20 WITH CASCADED CHECK OPTION")
                  "create cascaded view"

              expectOk (run store "UPDATE local_rows SET n = -1 WHERE id = 1") "LOCAL checks only its own predicate"
              expectOk (run store "INSERT INTO local_rows VALUES (3, -1)") "LOCAL insert checks only its own predicate"

              expectOk (run store "UPDATE nested_rows SET n = 10 WHERE id = 1") "restore row"

              [ "UPDATE cascaded_rows SET n = -1 WHERE id = 1"
                "INSERT INTO cascaded_rows VALUES (4, -1)" ]
              |> List.iter (fun sql ->
                  match run store sql with
                  | Err(1369, "CHECK OPTION failed 'fsdb.cascaded_rows'") -> ()
                  | other -> failtestf "expected cascaded check failure for %s, got %A" sql other)

              expectOk (run store "UPDATE local_rows SET n = 15 WHERE id = 1") "update nested view"
              expectOk (run store "DELETE FROM local_rows WHERE id = 1") "delete nested view"

              Expect.equal
                  (rows store "SELECT id, n FROM nested_rows ORDER BY id")
                  [ [ Some "2"; Some "30" ]; [ Some "3"; Some "-1" ] ]
                  "nested writes preserve both visibility predicates"

              Expect.equal
                  (rows store "SELECT table_name, is_updatable FROM information_schema.views WHERE table_name IN ('positive_rows', 'local_rows', 'cascaded_rows') ORDER BY table_name")
                  [ [ Some "cascaded_rows"; Some "YES" ]; [ Some "local_rows"; Some "YES" ]; [ Some "positive_rows"; Some "YES" ] ]
                  "nested direct views remain updatable"

          testCase "nested LOCAL checks retain an underlying view check"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE checked_rows (id INT PRIMARY KEY, n INT NOT NULL)") "create table"
              expectOk (run store "INSERT INTO checked_rows VALUES (1, 10)") "seed table"

              expectOk
                  (run store "CREATE VIEW checked_positive AS SELECT id, n FROM checked_rows WHERE n > 0 WITH CASCADED CHECK OPTION")
                  "create checked base view"

              expectOk
                  (run store "CREATE VIEW checked_local AS SELECT id, n FROM checked_positive WHERE n < 20 WITH LOCAL CHECK OPTION")
                  "create local nested view"

              match run store "UPDATE checked_local SET n = -1 WHERE id = 1" with
              | Err(1369, "CHECK OPTION failed 'fsdb.checked_local'") -> ()
              | other -> failtestf "expected the underlying check to remain active, got %A" other

          testCase "view updateability and insertability are independent"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE required_rows (id INT PRIMARY KEY, required_value INT NOT NULL)") "create table"
              expectOk (run store "INSERT INTO required_rows VALUES (1, 10)") "seed table"
              expectOk (run store "CREATE VIEW partial_rows AS SELECT id FROM required_rows") "create partial view"
              expectOk (run store "CREATE VIEW repeated_rows AS SELECT id, required_value AS first_copy, required_value AS second_copy FROM required_rows") "create repeated view"
              expectOk (run store "CREATE VIEW all_required_rows AS SELECT * FROM required_rows") "create star view"

              Expect.equal
                  (rows store "SELECT table_name, is_updatable FROM information_schema.views WHERE table_name IN ('partial_rows', 'repeated_rows') ORDER BY table_name")
                  [ [ Some "partial_rows"; Some "YES" ]; [ Some "repeated_rows"; Some "YES" ] ]
                  "both views permit updates"

              expectOk (run store "UPDATE partial_rows SET id = 2 WHERE id = 1") "update a partial view"
              expectOk (run store "UPDATE repeated_rows SET first_copy = 11 WHERE id = 2") "update one repeated projection"
              expectOk (run store "INSERT INTO all_required_rows VALUES (3, 30)") "insert through a star view"
              expectOk (run store "UPDATE all_required_rows SET required_value = 31 WHERE id = 3") "update through a star view"

              [ "INSERT INTO partial_rows VALUES (3)"
                "INSERT INTO repeated_rows(id, first_copy) VALUES (3, 30)" ]
              |> List.iter (fun sql ->
                  match run store sql with
                  | Err(1471, _) -> ()
                  | other -> failtestf "expected noninsertable view rejection for %s, got %A" sql other)

              Expect.equal
                  (rows store "SELECT * FROM required_rows ORDER BY id")
                  [ [ Some "2"; Some "11" ]; [ Some "3"; Some "31" ] ]
                  "legal writes persist"

          testCase "joined views write one base table and reject outer joins"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE joined_left (id INT PRIMARY KEY, n INT, hidden INT DEFAULT 7)") "create left"
              expectOk (run store "CREATE TABLE joined_right (id INT PRIMARY KEY, note VARCHAR(20))") "create right"
              expectOk (run store "CREATE TABLE joined_many (left_id INT, marker VARCHAR(20))") "create many"
              expectOk (run store "INSERT INTO joined_left VALUES (1, 10, 7), (2, 20, 7)") "seed left"
              expectOk (run store "INSERT INTO joined_right VALUES (1, 'one'), (2, 'two')") "seed right"
              expectOk (run store "INSERT INTO joined_many VALUES (1, 'a'), (1, 'b'), (2, 'c')") "seed many"

              expectOk
                  (run store "CREATE VIEW joined_rows AS SELECT l.id, l.n, r.note FROM joined_left l JOIN joined_right r ON r.id = l.id")
                  "create join view"

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name = 'joined_rows'")
                  [ [ Some "YES" ] ]
                  "inner join view is updatable"

              expectOk (run store "UPDATE joined_rows SET n = 11 WHERE id = 1") "update left table"
              expectOk (run store "UPDATE joined_rows SET note = 'changed' WHERE id = 1") "update right table"
              expectOk (run store "INSERT INTO joined_rows(id, n) VALUES (3, 30)") "insert into one join component"

              expectOk
                  (run store "CREATE VIEW joined_right_rows AS SELECT r.id AS right_id, r.note, l.n FROM joined_left l JOIN joined_right r ON r.id = l.id")
                  "create right-insert view"

              expectOk (run store "INSERT INTO joined_right_rows(right_id, note) VALUES (4, 'four')") "insert into right table"

              expectOk
                  (run store "CREATE VIEW joined_filtered AS SELECT l.id, l.n, r.note, l.n * 2 AS doubled FROM joined_left l JOIN joined_right r ON r.id = l.id WHERE l.n < 20")
                  "create filtered join view"

              expectOk (run store "UPDATE joined_filtered SET n = 12 WHERE doubled = 22") "rewrite computed predicate"

              expectOk
                  (run store "CREATE VIEW layered_join AS SELECT id, n, note, n * 3 AS tripled FROM joined_rows WHERE n < 30")
                  "create outer join layer"

              expectOk (run store "UPDATE layered_join SET note = 'layered' WHERE tripled = 60") "update through outer join layer"

              expectOk
                  (run store "CREATE VIEW joined_duplicates AS SELECT l.id, l.n, m.marker FROM joined_left l JOIN joined_many m ON m.left_id = l.id")
                  "create duplicate-match view"

              expectOk (run store "UPDATE joined_duplicates SET n = n + 1 WHERE id = 1") "update a duplicated target once"

              match run store "UPDATE joined_rows SET n = 12, note = 'both' WHERE id = 1" with
              | Err(1393, "Can not modify more than one base table through a join view 'fsdb.joined_rows'") -> ()
              | other -> failtestf "expected cross-table update rejection, got %A" other

              match run store "DELETE FROM joined_rows WHERE id = 1" with
              | Err(1395, "Can not delete from join view 'fsdb.joined_rows'") -> ()
              | other -> failtestf "expected join-view delete rejection, got %A" other

              match run store "INSERT INTO joined_rows VALUES (5, 50, 'five')" with
              | Err(1394, "Can not insert into join view 'fsdb.joined_rows' without fields list") -> ()
              | other -> failtestf "expected implicit multi-table insert rejection, got %A" other

              match run store "INSERT INTO joined_rows(id, n) VALUES (1, 99) ON DUPLICATE KEY UPDATE note = 'bad'" with
              | Err(1393, _) -> ()
              | other -> failtestf "expected cross-table duplicate update rejection, got %A" other

              match run store "REPLACE INTO joined_rows(id, n) VALUES (5, 50)" with
              | Err(1395, "Can not delete from join view 'fsdb.joined_rows'") -> ()
              | other -> failtestf "expected join-view replace rejection, got %A" other

              match run store "UPDATE joined_rows SET n = 0 ORDER BY id LIMIT 1" with
              | Err(1221, "Incorrect usage of UPDATE and ORDER BY") -> ()
              | other -> failtestf "expected join-view ordered update rejection, got %A" other

              expectOk
                  (run store "CREATE VIEW outer_joined_rows AS SELECT l.id, l.n, r.note FROM joined_left l LEFT JOIN joined_right r ON r.id = l.id")
                  "create outer join view"

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name = 'outer_joined_rows'")
                  [ [ Some "NO" ] ]
                  "outer join view is not updatable"

              match run store "UPDATE outer_joined_rows SET n = 0" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected outer join update rejection, got %A" other

              expectOk
                  (run store "CREATE VIEW right_joined_rows AS SELECT l.id, l.n, r.note FROM joined_right r RIGHT JOIN joined_left l ON r.id = l.id")
                  "create right join view"

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name = 'right_joined_rows'")
                  [ [ Some "NO" ] ]
                  "right join view is not updatable"

              match run store "UPDATE right_joined_rows SET n = 0" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected right join update rejection, got %A" other

              Expect.equal
                  (rows store "SELECT id, n, hidden FROM joined_left ORDER BY id")
                  [ [ Some "1"; Some "13"; Some "7" ]; [ Some "2"; Some "20"; Some "7" ]; [ Some "3"; Some "30"; Some "7" ] ]
                  "left-table writes persist"

              Expect.equal
                  (rows store "SELECT id, note FROM joined_right ORDER BY id")
                  [ [ Some "1"; Some "changed" ]; [ Some "2"; Some "layered" ]; [ Some "4"; Some "four" ] ]
                  "right-table update persists"

          testCase "joined view INSERT SET targets one physical component"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE set_left (id INT PRIMARY KEY, n INT)") "create left"
              expectOk (run store "CREATE TABLE set_right (id INT PRIMARY KEY, left_id INT)") "create right"
              expectOk (run store "INSERT INTO set_right VALUES (1, 1)") "seed right"
              expectOk
                  (run store "CREATE VIEW set_joined AS SELECT l.id AS aid, l.n, r.id AS bid FROM set_left l JOIN set_right r ON r.left_id=l.id")
                  "create join view"

              Expect.equal (run store "INSERT INTO set_joined SET aid=1,n=10") (Affected 1UL) "SET inserts the left component"
              Expect.equal
                  (run store "INSERT IGNORE INTO set_joined SET aid=1,n=30")
                  (Affected 0UL)
                  "IGNORE skips a duplicate"
              Expect.equal
                  (run store "INSERT INTO set_joined SET aid=1,n=22 ON DUPLICATE KEY UPDATE n=VALUES(n)")
                  (Affected 2UL)
                  "ODKU updates the same component"
              Expect.equal (rows store "SELECT id,n FROM set_left") [ [ Some "1"; Some "22" ] ] "the physical row matches MySQL"

          testCase "distinct-definer component CHECK OPTION survives a joined write"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE DATABASE probe"
                    "CREATE TABLE probe.check_left (id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE probe.check_right (id INT PRIMARY KEY,left_id INT)"
                    "INSERT INTO probe.check_left VALUES(1,10)"
                    "INSERT INTO probe.check_right VALUES(1,1)"
                    "CREATE USER filter_owner"
                    "CREATE USER join_owner"
                    "GRANT SELECT,INSERT,UPDATE ON probe.check_left TO filter_owner"
                    "GRANT SELECT,INSERT,UPDATE ON probe.check_right TO join_owner"
                    "GRANT CREATE VIEW ON probe.* TO filter_owner,join_owner"
                    "CREATE DEFINER='filter_owner'@'%' VIEW probe.filtered AS SELECT id,n FROM probe.check_left WHERE n>0 WITH CASCADED CHECK OPTION"
                    "GRANT SELECT,INSERT,UPDATE ON probe.filtered TO join_owner"
                    "CREATE DEFINER='join_owner'@'%' VIEW probe.joined AS SELECT f.id AS aid,f.n,b.id AS bid FROM probe.filtered f JOIN probe.check_right b ON b.left_id=f.id" ] do
                  expectOk (run store sql) sql

              match run store "UPDATE probe.joined SET n=-1 WHERE aid=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected CHECK OPTION rejection for UPDATE, got %A" other

              match run store "INSERT INTO probe.joined SET aid=2,n=-1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected CHECK OPTION rejection for INSERT SET, got %A" other

              Expect.equal (run store "INSERT INTO probe.joined SET aid=2,n=20") (Affected 1UL) "legal insert succeeds"
              Expect.equal
                  (rows store "SELECT id,n FROM probe.check_left ORDER BY id")
                  [ [ Some "1"; Some "10" ]; [ Some "2"; Some "20" ] ]
                  "the physical rows match MySQL"

          testCase "ordered join view remains insertable but refuses updates"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE ordered_left (id INT PRIMARY KEY, n INT)") "create left"
              expectOk (run store "CREATE TABLE ordered_right (id INT PRIMARY KEY, v INT)") "create right"
              expectOk (run store "INSERT INTO ordered_left VALUES (1, 10), (2, 20)") "seed left"
              expectOk (run store "INSERT INTO ordered_right VALUES (1, 100), (2, 200), (3, 300)") "seed right"
              expectOk
                  (run store "CREATE VIEW ordered_join AS SELECT l.id, l.n, r.v FROM ordered_left l JOIN ordered_right r ON r.id=l.id ORDER BY l.id DESC")
                  "create ordered join view"

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='ordered_join'")
                  [ [ Some "YES" ] ]
                  "MySQL reports the ordered join view as updatable"
              Expect.equal
                  (rows store "SELECT id,n,v FROM ordered_join")
                  [ [ Some "2"; Some "20"; Some "200" ]; [ Some "1"; Some "10"; Some "100" ] ]
                  "the view retains its saved order"

              for sql in
                  [ "UPDATE ordered_join SET n=11 WHERE id=1"
                    "UPDATE ordered_join SET n=n+1 LIMIT 1"
                    "UPDATE ordered_join SET n=n+1 ORDER BY id DESC LIMIT 1" ] do
                  match run store sql with
                  | Err(1221, "Incorrect usage of UPDATE and ORDER BY") -> ()
                  | other -> failtestf "expected the saved order to reject %s, got %A" sql other

              expectOk (run store "INSERT INTO ordered_join(id,n) VALUES (3,30)") "insert through ordered join"
              expectOk (run store "CREATE VIEW outer_ordered AS SELECT id,n,v FROM ordered_join") "create outer view"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='outer_ordered'")
                  [ [ Some "YES" ] ]
                  "the outer layer remains updatable"
              match run store "UPDATE outer_ordered SET n=12 WHERE id=1" with
              | Err(1221, "Incorrect usage of UPDATE and ORDER BY") -> ()
              | other -> failtestf "expected the inherited order to reject UPDATE, got %A" other
              expectOk (run store "INSERT INTO ordered_right VALUES (4,400)") "seed fourth join partner"
              expectOk (run store "INSERT INTO outer_ordered(id,n) VALUES (4,40)") "insert through outer layer"
              expectOk
                  (run store "CREATE VIEW checked_ordered AS SELECT l.id,l.n,r.v FROM ordered_left l JOIN ordered_right r ON r.id=l.id ORDER BY l.id DESC WITH CHECK OPTION")
                  "create checked ordered join view"
              Expect.equal
                  (rows store "SELECT id,n FROM ordered_left ORDER BY id")
                  [ [ Some "1"; Some "10" ]; [ Some "2"; Some "20" ]; [ Some "3"; Some "30" ]; [ Some "4"; Some "40" ] ]
                  "the insert reaches the left component"

          testCase "direct checked join view enforces post-write visibility"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE checked_join_left (id INT PRIMARY KEY, n INT)") "create left"
              expectOk (run store "CREATE TABLE checked_join_right (id INT PRIMARY KEY, v INT)") "create right"
              expectOk (run store "INSERT INTO checked_join_left VALUES (1, 10), (2, 20)") "seed left"
              expectOk (run store "INSERT INTO checked_join_right VALUES (1, 100), (2, 200), (3, 300)") "seed right"
              expectOk
                  (run store "CREATE VIEW checked_join AS SELECT l.id, l.n, r.v FROM checked_join_left l JOIN checked_join_right r ON r.id = l.id WHERE l.n > 0 AND r.v > 0 WITH CASCADED CHECK OPTION")
                  "create checked join view"

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name = 'checked_join'")
                  [ [ Some "YES" ] ]
                  "MySQL marks the checked inner join updatable"

              expectOk (run store "UPDATE checked_join SET n = 11 WHERE id = 1") "visible update"

              expectOk
                  (run store "CREATE VIEW outer_checked_join AS SELECT id, n, v FROM checked_join")
                  "create outer layer"

              match run store "UPDATE outer_checked_join SET n = 0 WHERE id = 1" with
              | Err(1369, "CHECK OPTION failed 'fsdb.outer_checked_join'") -> ()
              | other -> failtestf "expected inherited join check, got %A" other

              for sql in
                  [ "UPDATE checked_join SET n = 0 WHERE id = 1"
                    "UPDATE checked_join SET id = 3 WHERE id = 1"
                    "UPDATE checked_join SET v = 0 WHERE id = 1"
                    "INSERT INTO checked_join(id, n) VALUES (4, 40)"
                    "INSERT INTO checked_join(id, n) VALUES (1, 11) ON DUPLICATE KEY UPDATE n = 11" ] do
                  match run store sql with
                  | Err(1369, "CHECK OPTION failed 'fsdb.checked_join'") -> ()
                  | other -> failtestf "expected CHECK OPTION failure for %s, got %A" sql other

              expectOk (run store "INSERT INTO checked_join(id, n) VALUES (3, 30)") "insert with matching partner"
              Expect.equal
                  (rows store "SELECT id, n FROM checked_join_left ORDER BY id")
                  [ [ Some "1"; Some "11" ]; [ Some "2"; Some "20" ]; [ Some "3"; Some "30" ] ]
                  "rejected writes do not change the base table"

          testCase "cross using and natural join views retain writable columns"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE join_form_left (id INT PRIMARY KEY, n INT)") "create left"
              expectOk (run store "CREATE TABLE join_form_right (id INT PRIMARY KEY, v INT)") "create right"
              expectOk (run store "INSERT INTO join_form_left VALUES (1, 10), (2, 20)") "seed left"
              expectOk (run store "INSERT INTO join_form_right VALUES (1, 100), (2, 200)") "seed right"

              for name, definition, value in
                  [ "cross_form", "SELECT l.id,l.n,r.v FROM join_form_left l CROSS JOIN join_form_right r WHERE l.id=r.id", 11
                    "using_form", "SELECT l.id,l.n,r.v FROM join_form_left l JOIN join_form_right r USING(id)", 12
                    "natural_form", "SELECT id,n,v FROM join_form_left NATURAL JOIN join_form_right", 13 ] do
                  expectOk (run store (sprintf "CREATE VIEW %s AS %s" name definition)) (sprintf "create %s" name)
                  Expect.equal
                      (rows store (sprintf "SELECT is_updatable FROM information_schema.views WHERE table_name = '%s'" name))
                      [ [ Some "YES" ] ]
                      (sprintf "%s is updatable" name)
                  expectOk
                      (run store (sprintf "UPDATE %s SET n = %d WHERE id = 1" name value))
                      (sprintf "update %s" name)

              Expect.equal
                  (rows store "SELECT id,n FROM join_form_left ORDER BY id")
                  [ [ Some "1"; Some "13" ]; [ Some "2"; Some "20" ] ]
                  "each join form writes the left table"

          testCase "subquery views follow MySQL updateability boundaries"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE subquery_rows (id INT PRIMARY KEY, n INT NOT NULL)") "create rows"
              expectOk (run store "CREATE TABLE subquery_lookup (id INT PRIMARY KEY, v INT NOT NULL)") "create lookup"
              expectOk (run store "INSERT INTO subquery_rows VALUES (1, 10), (2, 20)") "seed rows"
              expectOk (run store "INSERT INTO subquery_lookup VALUES (1, 100), (2, 200)") "seed lookup"

              expectOk
                  (run store "CREATE VIEW projection_uncorrelated AS SELECT r.id, r.n, (SELECT MAX(v) FROM subquery_lookup) AS maximum FROM subquery_rows r")
                  "create uncorrelated projection"

              expectOk
                  (run store "CREATE VIEW projection_correlated AS SELECT r.id, r.n, (SELECT v FROM subquery_lookup WHERE subquery_lookup.id = r.id) AS found FROM subquery_rows r")
                  "create correlated projection"

              expectOk
                  (run store "CREATE VIEW projection_bare_correlated AS SELECT r.id, r.n, (SELECT v FROM subquery_lookup WHERE v = n) AS found FROM subquery_rows r")
                  "create bare correlated projection"

              expectOk
                  (run store "CREATE VIEW predicate_uncorrelated AS SELECT r.id, r.n FROM subquery_rows r WHERE r.n < (SELECT MAX(v) FROM subquery_lookup)")
                  "create uncorrelated predicate"

              expectOk
                  (run store "CREATE VIEW predicate_correlated AS SELECT r.id, r.n FROM subquery_rows r WHERE EXISTS (SELECT 1 FROM subquery_lookup WHERE subquery_lookup.id = r.id)")
                  "create correlated predicate"

              Expect.equal
                  (rows store "SELECT table_name, is_updatable FROM information_schema.views WHERE table_name IN ('projection_uncorrelated', 'projection_correlated', 'projection_bare_correlated', 'predicate_uncorrelated', 'predicate_correlated') ORDER BY table_name")
                  [ [ Some "predicate_correlated"; Some "YES" ]
                    [ Some "predicate_uncorrelated"; Some "YES" ]
                    [ Some "projection_bare_correlated"; Some "YES" ]
                    [ Some "projection_correlated"; Some "YES" ]
                    [ Some "projection_uncorrelated"; Some "YES" ] ]
                  "metadata matches MySQL's creation-time flag"

              expectOk (run store "UPDATE projection_uncorrelated SET n = 11 WHERE id = 1") "update through uncorrelated projection"
              expectOk (run store "DELETE FROM projection_uncorrelated WHERE id = 2") "delete through uncorrelated projection"

              match run store "INSERT INTO projection_uncorrelated(id, n) VALUES (3, 30)" with
              | Err(1471, _) -> ()
              | other -> failtestf "expected expression projection insert rejection, got %A" other

              [ "UPDATE projection_correlated SET n = 12 WHERE id = 1"
                "DELETE FROM projection_correlated WHERE id = 1"
                "UPDATE projection_bare_correlated SET n = 12 WHERE id = 1" ]
              |> List.iter (fun sql ->
                  match run store sql with
                  | Err(1288, _) -> ()
                  | other -> failtestf "expected dependent projection rejection for %s, got %A" sql other)

              match run store "INSERT INTO projection_correlated(id, n) VALUES (4, 40)" with
              | Err(1471, _) -> ()
              | other -> failtestf "expected dependent projection insert rejection, got %A" other

              expectOk (run store "UPDATE predicate_uncorrelated SET n = 13 WHERE id = 1") "update through uncorrelated predicate"
              expectOk (run store "INSERT INTO predicate_uncorrelated VALUES (5, 50)") "insert through uncorrelated predicate"
              expectOk (run store "UPDATE predicate_correlated SET n = 14 WHERE id = 1") "update through correlated predicate"
              expectOk (run store "INSERT INTO predicate_correlated VALUES (6, 60)") "insert through correlated predicate"

              Expect.equal
                  (rows store "SELECT id, n FROM subquery_rows ORDER BY id")
                  [ [ Some "1"; Some "14" ]; [ Some "5"; Some "50" ]; [ Some "6"; Some "60" ] ]
                  "legal subquery-view writes persist"

          testCase "nested join views preserve component writes and checks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE nested_join_left (id INT PRIMARY KEY, n INT NOT NULL)") "create left"
              expectOk (run store "CREATE TABLE nested_join_right (id INT PRIMARY KEY, note VARCHAR(20) NOT NULL)") "create right"
              expectOk (run store "INSERT INTO nested_join_left VALUES (1, 10), (2, 20)") "seed left"
              expectOk (run store "INSERT INTO nested_join_right VALUES (1, 'one'), (2, 'two')") "seed right"

              expectOk
                  (run store "CREATE VIEW positive_join_left AS SELECT id, n FROM nested_join_left WHERE n > 0 WITH CASCADED CHECK OPTION")
                  "create checked component"

              expectOk (run store "CREATE VIEW visible_join_right AS SELECT id, note FROM nested_join_right") "create right component"

              expectOk
                  (run store "CREATE VIEW nested_join_rows AS SELECT l.id, l.n, r.note FROM positive_join_left l JOIN visible_join_right r ON r.id = l.id")
                  "create nested join"

              expectOk
                  (run store "CREATE VIEW filtered_nested_join AS SELECT id, n, note, n * 2 AS doubled FROM nested_join_rows WHERE n < 20")
                  "create outer join view"

              Expect.equal
                  (rows store "SELECT table_name, is_updatable FROM information_schema.views WHERE table_name IN ('nested_join_rows', 'filtered_nested_join') ORDER BY table_name")
                  [ [ Some "filtered_nested_join"; Some "YES" ]; [ Some "nested_join_rows"; Some "YES" ] ]
                  "nested join layers remain updatable"

              expectOk (run store "UPDATE nested_join_rows SET n = 11 WHERE id = 1") "update through component views"
              expectOk (run store "UPDATE filtered_nested_join SET n = 12 WHERE doubled = 22") "update through outer view"
              expectOk
                  (run store "INSERT INTO nested_join_rows(id, n) VALUES (1, 14) ON DUPLICATE KEY UPDATE n = values(n)")
                  "upsert through nested component"
              expectOk (run store "INSERT INTO nested_join_rows(id, n) VALUES (3, 30)") "insert into one nested component"

              [ "UPDATE nested_join_rows SET n = -1 WHERE id = 1"
                "INSERT INTO nested_join_rows(id, n) VALUES (4, -1)" ]
              |> List.iter (fun sql ->
                  match run store sql with
                  | Err(1369, "CHECK OPTION failed 'fsdb.nested_join_rows'") -> ()
                  | other -> failtestf "expected nested component check failure for %s, got %A" sql other)

              match run store "DELETE FROM filtered_nested_join WHERE id = 1" with
              | Err(1395, "Can not delete from join view 'fsdb.filtered_nested_join'") -> ()
              | other -> failtestf "expected nested join delete rejection, got %A" other

              Expect.equal
                  (rows store "SELECT id, n FROM nested_join_left ORDER BY id")
                  [ [ Some "1"; Some "14" ]; [ Some "2"; Some "20" ]; [ Some "3"; Some "30" ] ]
                  "nested join writes persist"

          testCase "a checked single-source outer view can update a joined view"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE layer_target(id INT PRIMARY KEY,n INT NOT NULL)") "create target"
              expectOk (run store "CREATE TABLE layer_aux(id INT PRIMARY KEY,v INT NOT NULL)") "create aux"
              expectOk (run store "INSERT INTO layer_target VALUES (1,10)") "seed target"
              expectOk (run store "INSERT INTO layer_aux VALUES (1,1)") "seed aux"
              expectOk
                  (run store "CREATE VIEW layer_base AS SELECT t.id,t.n,a.v FROM layer_target t JOIN layer_aux a ON a.id=t.id WHERE t.n>a.v WITH CASCADED CHECK OPTION")
                  "create inner joined view"
              expectOk
                  (run store "CREATE VIEW layer_outer AS SELECT id,n FROM layer_base WHERE n>v+1 WITH CASCADED CHECK OPTION")
                  "create checked outer view"
              expectOk (run store "UPDATE layer_outer SET n=11 WHERE id=1") "update nested target"
              match run store "UPDATE layer_outer SET n=2 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected outer CHECK OPTION rejection, got %A" other
              Expect.equal (rows store "SELECT n FROM layer_target WHERE id=1") [ [ Some "11" ] ] "rejected write is atomic"

          testCase "checked joined layers can update a target inside a joined view"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE layered_target(id INT PRIMARY KEY,n INT NOT NULL)") "create target"
              expectOk (run store "CREATE TABLE layered_aux(id INT PRIMARY KEY,v INT NOT NULL)") "create aux"
              expectOk (run store "INSERT INTO layered_target VALUES (1,10)") "seed target"
              expectOk (run store "INSERT INTO layered_aux VALUES (1,1)") "seed aux"
              expectOk
                  (run store "CREATE VIEW layered_base AS SELECT t.id,t.n,a.v FROM layered_target t JOIN layered_aux a ON a.id=t.id WHERE t.n>a.v WITH CASCADED CHECK OPTION")
                  "create inner joined view"
              let cases =
                  [ "layered_lateral",
                    "SELECT b.id,b.n,x.min_n FROM layered_base b JOIN LATERAL (SELECT b.v+1 AS min_n) x ON TRUE WHERE b.n>x.min_n"
                    "layered_json",
                    "SELECT b.id,b.n,j.jv FROM layered_base b JOIN JSON_TABLE(JSON_ARRAY(b.v),'$[*]' COLUMNS(jv INT PATH '$')) j WHERE b.n>j.jv"
                    "layered_derived",
                    "SELECT b.id,b.n,y.v AS aux_v FROM layered_base b JOIN (SELECT id,v FROM layered_aux) y ON y.id=b.id WHERE b.n>y.v" ]
              for name, definition in cases do
                  expectOk
                      (run store $"CREATE VIEW {name} AS {definition} WITH CASCADED CHECK OPTION")
                      $"create {name}"
                  Expect.equal
                      (rows store $"SELECT is_updatable FROM information_schema.views WHERE table_schema='fsdb' AND table_name='{name}'")
                      [ [ Some "YES" ] ]
                      $"{name} is updatable"
                  expectOk (run store $"UPDATE {name} SET n=n+1 WHERE id=1") $"update through {name}"
                  match run store $"UPDATE {name} SET n=0 WHERE id=1" with
                  | Err(1369, _) -> ()
                  | other -> failtestf "expected CHECK OPTION rejection through %s, got %A" name other
                  Expect.equal (rows store "SELECT n FROM layered_target WHERE id=1") [ [ Some "11" ] ] "rejected write is atomic"
                  expectOk (run store "UPDATE layered_target SET n=10 WHERE id=1") "reset target"

              expectOk
                  (run store "CREATE VIEW layered_component AS SELECT b.id,b.n,b.v,y.v AS marker FROM layered_base b JOIN (SELECT id,v FROM layered_aux) y ON y.id=b.id WHERE b.n>y.v WITH CASCADED CHECK OPTION")
                  "create view exposing the joined component"
              expectOk (run store "UPDATE layered_component SET v=2 WHERE id=1") "update the joined component"
              match run store "UPDATE layered_component SET v=12 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected joined component CHECK OPTION rejection, got %A" other
              Expect.equal (rows store "SELECT v FROM layered_aux WHERE id=1") [ [ Some "2" ] ] "joined component write and rejection persist"

              expectOk
                  (run store "CREATE VIEW layered_aggregate_base AS SELECT t.id,t.n,x.v FROM (SELECT id,v FROM layered_aux GROUP BY id,v) x JOIN layered_target t ON t.id=x.id WHERE t.n>x.v WITH CASCADED CHECK OPTION")
                  "create inner view with a derived source"
              expectOk
                  (run store "CREATE VIEW layered_aggregate_outer AS SELECT b.id,b.n,y.v AS aux_v FROM layered_aggregate_base b JOIN (SELECT id,v FROM layered_aux) y ON y.id=b.id WHERE b.n>y.v WITH CASCADED CHECK OPTION")
                  "create outer view with a derived source"
              expectOk (run store "UPDATE layered_aggregate_outer SET n=n+1 WHERE id=1") "update through derived layers"
              match run store "UPDATE layered_aggregate_outer SET n=0 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected CHECK OPTION rejection through derived layers, got %A" other
              Expect.equal (rows store "SELECT n FROM layered_target WHERE id=1") [ [ Some "11" ] ] "derived-layer rejection is atomic"

          testCase "joined view layers keep their inner aliases private"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE alias_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE alias_companion(id INT PRIMARY KEY,v INT)"
                    "CREATE TABLE alias_extra(id INT PRIMARY KEY,z INT)"
                    "INSERT INTO alias_target VALUES(1,10)"
                    "INSERT INTO alias_companion VALUES(1,1)"
                    "INSERT INTO alias_extra VALUES(1,1)" ] do
                  expectOk (run store sql) sql

              expectOk
                  (run store "CREATE VIEW alias_inner AS SELECT t.id,t.n,a.v FROM alias_target t JOIN alias_companion a ON a.id=t.id WHERE t.n>a.v WITH CASCADED CHECK OPTION")
                  "create inner joined view"
              expectOk
                  (run store "CREATE VIEW alias_outer AS SELECT iv.id,iv.n,iv.v FROM alias_inner iv JOIN alias_extra a ON a.id=iv.id")
                  "create outer view reusing the hidden inner alias"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_schema='fsdb' AND table_name='alias_outer'")
                  [ [ Some "YES" ] ]
                  "MySQL 8.4 reports the outer view as updatable"
              expectOk (run store "UPDATE alias_outer SET n=11 WHERE id=1") "update through alias collision"
              expectOk (run store "UPDATE alias_outer SET v=2 WHERE id=1") "update the inner joined component"
              match run store "UPDATE alias_outer SET v=12 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected inner CHECK OPTION rejection, got %A" other
              Expect.equal (rows store "SELECT n FROM alias_target") [ [ Some "11" ] ] "the physical target changes"
              Expect.equal (rows store "SELECT v FROM alias_companion") [ [ Some "2" ] ] "the rejected component write is atomic"

          testCase "a derived source can precede a joined writable view"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE using_target(id INT PRIMARY KEY,n INT NOT NULL)") "create target"
              expectOk (run store "CREATE TABLE using_aux(id INT PRIMARY KEY,v INT NOT NULL)") "create aux"
              expectOk (run store "INSERT INTO using_target VALUES (1,10)") "seed target"
              expectOk (run store "INSERT INTO using_aux VALUES (1,1)") "seed aux"
              expectOk
                  (run store "CREATE VIEW using_base AS SELECT t.id,t.n,a.v FROM using_target t JOIN using_aux a ON a.id=t.id WHERE t.n>a.v WITH CASCADED CHECK OPTION")
                  "create inner joined view"
              expectOk
                  (run store "CREATE VIEW using_outer AS SELECT b.id,b.n,y.v AS aux_v FROM (SELECT id,v FROM using_aux GROUP BY id,v) y JOIN using_base b USING(id) WHERE b.n>y.v WITH CASCADED CHECK OPTION")
                  "create outer joined view"
              expectOk (run store "UPDATE using_outer SET n=n+1 WHERE id=1") "update nested target"
              match run store "UPDATE using_outer SET n=0 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected CHECK OPTION rejection, got %A" other
              Expect.equal (rows store "SELECT n FROM using_target WHERE id=1") [ [ Some "11" ] ] "rejected write is atomic"

          testCase "two read-only sources can precede a joined writable view"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE two_source_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE two_source_aux(id INT PRIMARY KEY,v INT)"
                    "INSERT INTO two_source_target VALUES(1,10)"
                    "INSERT INTO two_source_aux VALUES(1,1)" ] do
                  expectOk (run store sql) sql
              expectOk
                  (run store "CREATE VIEW two_source_inner AS SELECT t.id,t.n,a.v FROM two_source_target t JOIN two_source_aux a ON a.id=t.id WHERE t.n>a.v WITH CASCADED CHECK OPTION")
                  "create inner joined view"
              expectOk
                  (run store "CREATE VIEW two_source_outer AS SELECT iv.id,iv.n,x.k AS xk,y.k AS yk FROM (SELECT 1 AS k) x JOIN (SELECT 1 AS k) y ON y.k=x.k JOIN two_source_inner iv ON iv.id=x.k AND iv.id=y.k WITH CASCADED CHECK OPTION")
                  "create outer view after two read-only sources"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_schema='fsdb' AND table_name='two_source_outer'")
                  [ [ Some "YES" ] ]
                  "MySQL 8.4 reports the outer view as updatable"
              expectOk (run store "UPDATE two_source_outer SET n=11 WHERE id=1") "update the nested physical target"
              match run store "UPDATE two_source_outer SET n=0 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected outer CHECK OPTION rejection, got %A" other
              Expect.equal (rows store "SELECT n FROM two_source_target") [ [ Some "11" ] ] "the rejected write is atomic"

          testCase "an unchecked outer join enforces only the inner view check"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE inner_check_target(id INT PRIMARY KEY,n INT NOT NULL)") "create target"
              expectOk (run store "CREATE TABLE inner_check_aux(id INT PRIMARY KEY,v INT NOT NULL)") "create aux"
              expectOk (run store "INSERT INTO inner_check_target VALUES (1,10)") "seed target"
              expectOk (run store "INSERT INTO inner_check_aux VALUES (1,1)") "seed aux"
              expectOk
                  (run store "CREATE VIEW inner_checked AS SELECT t.id,t.n,a.v FROM inner_check_target t JOIN inner_check_aux a ON a.id=t.id WHERE t.n>a.v WITH CASCADED CHECK OPTION")
                  "create checked inner view"
              expectOk
                  (run store "CREATE VIEW outer_unchecked AS SELECT b.id,b.n FROM inner_checked b JOIN (SELECT 1 AS k) x ON x.k=b.id WHERE b.n>5")
                  "create unchecked outer view"
              expectOk (run store "UPDATE outer_unchecked SET n=3 WHERE id=1") "leave the unchecked outer view"
              Expect.equal (rows store "SELECT n FROM inner_check_target WHERE id=1") [ [ Some "3" ] ] "outer predicate does not constrain the new value"
              expectOk (run store "UPDATE inner_check_target SET n=10 WHERE id=1") "reset target"
              match run store "UPDATE outer_unchecked SET n=1 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected inner CHECK OPTION rejection, got %A" other
              Expect.equal (rows store "SELECT n FROM inner_check_target WHERE id=1") [ [ Some "10" ] ] "inner rejection is atomic"

          testCase "joined layers retain a component view's definer grant boundary"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE layered_grant_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE layered_grant_aux(id INT PRIMARY KEY,v INT)"
                    "INSERT INTO layered_grant_target VALUES(1,10)"
                    "INSERT INTO layered_grant_aux VALUES(1,1)"
                    "CREATE USER layered_inner_owner"
                    "CREATE USER layered_outer_owner"
                    "GRANT SELECT,UPDATE ON fsdb.layered_grant_target TO layered_inner_owner"
                    "GRANT SELECT,UPDATE ON fsdb.layered_grant_aux TO layered_inner_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO layered_inner_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO layered_outer_owner"
                    "CREATE DEFINER='layered_inner_owner'@'%' SQL SECURITY DEFINER VIEW layered_grant_inner AS SELECT t.id,t.n AS metric,a.v FROM layered_grant_target t JOIN layered_grant_aux a ON a.id=t.id"
                    "GRANT SELECT,UPDATE ON fsdb.layered_grant_inner TO layered_outer_owner"
                    "CREATE DEFINER='layered_outer_owner'@'%' SQL SECURITY DEFINER VIEW layered_grant_outer AS SELECT b.id,b.metric AS n FROM layered_grant_inner b JOIN (SELECT 1 AS k) x ON x.k=b.id" ] do
                  expectOk (run store sql) sql
              expectOk (run store "UPDATE layered_grant_outer SET n=n+1 WHERE id=1") "write through two definers"
              expectOk (run store "REVOKE UPDATE ON fsdb.layered_grant_inner FROM layered_outer_owner") "revoke component write grant"
              match run store "UPDATE layered_grant_outer SET n=n+1 WHERE id=1" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected revoked inner-view grant to block the outer write, got %A" other
              Expect.equal (rows store "SELECT n FROM layered_grant_target") [ [ Some "11" ] ] "denied write changes nothing"
              expectOk (run store "GRANT UPDATE(metric) ON fsdb.layered_grant_inner TO layered_outer_owner") "grant the exposed column"
              expectOk (run store "UPDATE layered_grant_outer SET n=n+1 WHERE id=1") "column grant permits the renamed write"
              Expect.equal (rows store "SELECT n FROM layered_grant_target") [ [ Some "12" ] ] "column-authorized write persists"

          testCase "joined view outer layers preserve invoker and definer grants"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE security_mode_left(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE security_mode_right(id INT PRIMARY KEY,v INT)"
                    "INSERT INTO security_mode_left VALUES(1,10)"
                    "INSERT INTO security_mode_right VALUES(1,100)"
                    "CREATE ALGORITHM=MERGE SQL SECURITY DEFINER VIEW security_mode_join AS SELECT a.id,a.n,b.v FROM security_mode_left a JOIN security_mode_right b ON a.id=b.id"
                    "CREATE ALGORITHM=MERGE SQL SECURITY INVOKER VIEW security_mode_outer AS SELECT id,n,v FROM security_mode_join" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='security_mode_outer'")
                  [ [ Some "YES" ] ]
                  "MySQL marks the invoker layer updatable"
              expectOk (run store "UPDATE security_mode_outer SET n=n+1 WHERE id=1") "update the left component"
              expectOk (run store "UPDATE security_mode_outer SET v=v+1 WHERE id=1") "update the right component"
              Expect.equal (rows store "SELECT n FROM security_mode_left") [ [ Some "11" ] ] "left row changes"
              Expect.equal (rows store "SELECT v FROM security_mode_right") [ [ Some "101" ] ] "right row changes"

              for sql in
                  [ "CREATE USER mode_inner_owner"
                    "CREATE USER mode_outer_owner"
                    "GRANT SELECT,UPDATE ON fsdb.security_mode_left TO mode_inner_owner"
                    "GRANT SELECT,UPDATE ON fsdb.security_mode_right TO mode_inner_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO mode_inner_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO mode_outer_owner"
                    "CREATE ALGORITHM=MERGE DEFINER='mode_inner_owner'@'%' SQL SECURITY DEFINER VIEW mode_owned_join AS SELECT a.id,a.n AS metric,b.v FROM security_mode_left a JOIN security_mode_right b ON a.id=b.id"
                    "GRANT SELECT,UPDATE ON fsdb.mode_owned_join TO mode_outer_owner"
                    "CREATE ALGORITHM=MERGE DEFINER='mode_outer_owner'@'%' SQL SECURITY DEFINER VIEW mode_owned_outer AS SELECT id,metric AS n,v FROM mode_owned_join" ] do
                  expectOk (run store sql) sql

              expectOk (run store "UPDATE mode_owned_outer SET n=n+1 WHERE id=1") "write through both definers"
              expectOk (run store "UPDATE mode_owned_outer SET v=v+1 WHERE id=1") "write the second joined component"
              Expect.equal (rows store "SELECT n FROM security_mode_left") [ [ Some "12" ] ] "owned left write persists"
              Expect.equal (rows store "SELECT v FROM security_mode_right") [ [ Some "102" ] ] "owned right write persists"
              expectOk (run store "REVOKE UPDATE ON fsdb.mode_owned_join FROM mode_outer_owner") "revoke intermediate write grant"
              for sql in
                  [ "UPDATE mode_owned_outer SET n=n+1 WHERE id=1"
                    "UPDATE mode_owned_outer SET v=v+1 WHERE id=1" ] do
                  match run store sql with
                  | Err(1356, _) -> ()
                  | result -> failtestf "expected revoked intermediate grant to invalidate %s, got %A" sql result

              expectOk (run store "GRANT UPDATE(metric) ON fsdb.mode_owned_join TO mode_outer_owner") "grant the intermediate exposed column"
              expectOk (run store "UPDATE mode_owned_outer SET n=n+1 WHERE id=1") "the metric grant permits its renamed outer column"
              match run store "UPDATE mode_owned_outer SET v=v+1 WHERE id=1" with
              | Err(1356, _) -> ()
              | result -> failtestf "expected the ungranted intermediate v column to remain protected, got %A" result
              Expect.equal (rows store "SELECT n FROM security_mode_left") [ [ Some "13" ] ] "the authorized metric write persists"
              Expect.equal (rows store "SELECT v FROM security_mode_right") [ [ Some "102" ] ] "the denied right write changes nothing"

              for sql in
                  [ "CREATE USER mode_third_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO mode_third_owner"
                    "GRANT SELECT,UPDATE(n) ON fsdb.mode_owned_outer TO mode_third_owner"
                    "CREATE ALGORITHM=MERGE DEFINER='mode_third_owner'@'%' SQL SECURITY DEFINER VIEW mode_owned_third AS SELECT id,n,v FROM mode_owned_outer" ] do
                  expectOk (run store sql) sql

              expectOk (run store "UPDATE mode_owned_third SET n=n+1 WHERE id=1") "three definer layers preserve the exposed n grant"
              match run store "UPDATE mode_owned_third SET v=v+1 WHERE id=1" with
              | Err(1356, _) -> ()
              | result -> failtestf "expected the ungranted third-layer v column to remain protected, got %A" result
              Expect.equal (rows store "SELECT n FROM security_mode_left") [ [ Some "14" ] ] "the third-layer write persists"

              expectOk (run store "INSERT INTO security_mode_right VALUES(2,200),(3,300)") "seed joined insert partners"
              expectOk (run store "GRANT INSERT ON fsdb.mode_owned_join TO mode_outer_owner") "grant the outer insert path"
              match run store "INSERT INTO mode_owned_outer(id,n) VALUES(2,20)" with
              | Err(1356, _) -> ()
              | result -> failtestf "expected the inner definer's missing base INSERT to deny the write, got %A" result
              expectOk (run store "GRANT INSERT ON fsdb.security_mode_left TO mode_inner_owner") "grant inner left insert"
              expectOk (run store "GRANT INSERT ON fsdb.security_mode_right TO mode_inner_owner") "grant inner right insert"
              expectOk (run store "INSERT INTO mode_owned_outer(id,n) VALUES(2,20)") "insert through distinct definers"
              expectOk (run store "REVOKE INSERT ON fsdb.mode_owned_join FROM mode_outer_owner") "revoke outer insert grant"
              match run store "INSERT INTO mode_owned_outer(id,n) VALUES(3,30)" with
              | Err(1356, _) -> ()
              | result -> failtestf "expected the revoked intermediate INSERT grant to deny the write, got %A" result
              Expect.equal
                  (rows store "SELECT id,n FROM security_mode_left ORDER BY id")
                  [ [ Some "1"; Some "14" ]; [ Some "2"; Some "20" ] ]
                  "the denied inserts leave the base table unchanged"

          testCase "layered direct-view writes report revoked grants as invalid views"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE layered_target(id INT PRIMARY KEY,n INT)"
                    "INSERT INTO layered_target VALUES(1,0)"
                    "CREATE USER layered_inner_owner"
                    "CREATE USER layered_outer_owner"
                    "GRANT SELECT,UPDATE ON fsdb.layered_target TO layered_inner_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO layered_inner_owner,layered_outer_owner"
                    "CREATE DEFINER='layered_inner_owner'@'%' SQL SECURITY DEFINER VIEW layered_inner AS SELECT id,n FROM layered_target"
                    "GRANT SELECT,UPDATE ON fsdb.layered_inner TO layered_outer_owner"
                    "CREATE DEFINER='layered_outer_owner'@'%' SQL SECURITY DEFINER VIEW layered_outer AS SELECT id,n AS metric FROM layered_inner" ] do
                  expectOk (run store sql) sql

              let update view = run store $"UPDATE {view} SET metric=metric+1 WHERE id=1"
              Expect.equal (update "layered_outer") (Affected 1UL) "the complete grant chain permits the update"
              expectOk (run store "REVOKE UPDATE ON fsdb.layered_inner FROM layered_outer_owner") "revoke the exposed view grant"
              match update "layered_outer" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the missing intermediate grant to invalidate the view, got %A" other
              expectOk (run store "GRANT UPDATE(n) ON fsdb.layered_inner TO layered_outer_owner") "restore the exposed column grant"
              expectOk (run store "REVOKE SELECT ON fsdb.layered_target FROM layered_inner_owner") "revoke the base read grant"
              match update "layered_outer" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the direct inner view's missing SELECT to return 1356, got %A" other
              expectOk (run store "GRANT SELECT ON fsdb.layered_target TO layered_inner_owner") "restore the base read grant"
              expectOk (run store "REVOKE UPDATE ON fsdb.layered_target FROM layered_inner_owner") "revoke the base write grant"
              match update "layered_outer" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the direct inner view's missing UPDATE to return 1356, got %A" other
              Expect.equal (rows store "SELECT n FROM layered_target") [ [ Some "1" ] ] "denied writes leave the base row unchanged"

          testCase "layered derived join grants invalidate the view before physical join denials"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE derived_grant_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE derived_grant_flags(id INT PRIMARY KEY,flag INT)"
                    "CREATE TABLE derived_grant_aux(id INT PRIMARY KEY,aux INT)"
                    "INSERT INTO derived_grant_target VALUES(1,0),(2,0)"
                    "INSERT INTO derived_grant_flags VALUES(1,7)"
                    "INSERT INTO derived_grant_aux VALUES(1,9)"
                    "CREATE USER derived_grant_inner_owner"
                    "CREATE USER derived_grant_outer_owner"
                    "GRANT SELECT,UPDATE ON fsdb.derived_grant_target TO derived_grant_inner_owner"
                    "GRANT SELECT ON fsdb.derived_grant_flags TO derived_grant_inner_owner"
                    "GRANT SELECT ON fsdb.derived_grant_aux TO derived_grant_inner_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO derived_grant_inner_owner,derived_grant_outer_owner"
                    "CREATE DEFINER='derived_grant_inner_owner'@'%' SQL SECURITY DEFINER VIEW derived_grant_inner AS SELECT t.id,t.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) flag FROM derived_grant_flags GROUP BY id) d1 JOIN (SELECT id,SUM(aux) aux FROM derived_grant_aux GROUP BY id) d2 ON d2.id=d1.id JOIN derived_grant_target t ON t.id=d1.id AND t.n<d2.aux"
                    "GRANT SELECT,UPDATE ON fsdb.derived_grant_inner TO derived_grant_outer_owner"
                    "CREATE DEFINER='derived_grant_outer_owner'@'%' SQL SECURITY DEFINER VIEW derived_grant_outer AS SELECT id,n AS metric FROM derived_grant_inner" ] do
                  expectOk (run store sql) sql

              let update () = run store "UPDATE derived_grant_outer SET metric=metric+1 WHERE id=1"
              Expect.equal (update ()) (Affected 1UL) "complete grant chain permits the update"
              for table in [ "derived_grant_flags"; "derived_grant_aux" ] do
                  expectOk (run store $"REVOKE SELECT ON fsdb.{table} FROM derived_grant_inner_owner") "revoke derived read grant"
                  match update () with
                  | Err(1356, _) -> ()
                  | other -> failtestf "expected revoked derived source %s to invalidate the view, got %A" table other
                  expectOk (run store $"GRANT SELECT ON fsdb.{table} TO derived_grant_inner_owner") "restore derived read grant"

              expectOk (run store "REVOKE SELECT ON fsdb.derived_grant_target FROM derived_grant_inner_owner") "revoke target read grant"
              match update () with
              | Err(1143, _) -> ()
              | other -> failtestf "expected the physical join-key denial, got %A" other
              expectOk (run store "REVOKE SELECT ON fsdb.derived_grant_flags FROM derived_grant_inner_owner") "revoke derived read grant again"
              match update () with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the derived failure to precede the target denial, got %A" other
              Expect.equal
                  (rows store "SELECT id,n FROM derived_grant_target ORDER BY id")
                  [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ]
                  "denied updates leave the physical rows unchanged"

              expectOk (run store "GRANT SELECT ON fsdb.derived_grant_flags TO derived_grant_inner_owner") "restore grouped derived read"
              expectOk (run store "GRANT SELECT ON fsdb.derived_grant_target TO derived_grant_inner_owner") "restore grouped target read"
              for sql in
                  [ "CREATE DEFINER='derived_grant_inner_owner'@'%' SQL SECURITY DEFINER VIEW grouped_grant_inner AS SELECT t.id,t.n,d.flag FROM derived_grant_target t JOIN ((SELECT id,SUM(flag) flag FROM derived_grant_flags GROUP BY id) d JOIN derived_grant_aux a ON a.id=d.id) ON t.id=d.id"
                    "GRANT SELECT,UPDATE ON fsdb.grouped_grant_inner TO derived_grant_outer_owner"
                    "CREATE DEFINER='derived_grant_outer_owner'@'%' SQL SECURITY DEFINER VIEW grouped_grant_outer AS SELECT id,n AS metric FROM grouped_grant_inner" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='grouped_grant_outer'")
                  [ [ Some "YES" ] ] "a grouped derived source leaves the physical target writable"
              let groupedUpdate () = run store "UPDATE grouped_grant_outer SET metric=metric+1 WHERE id=1"
              Expect.equal (groupedUpdate ()) (Affected 1UL) "grouped join update reaches the target"
              for table, code in [ "derived_grant_flags", 1356; "derived_grant_aux", 1143; "derived_grant_target", 1143 ] do
                  expectOk (run store $"REVOKE SELECT ON fsdb.{table} FROM derived_grant_inner_owner") "revoke grouped source read"
                  match groupedUpdate () with
                  | Err(actual, _) when actual = code -> ()
                  | other -> failtestf "expected grouped source %s to return %d, got %A" table code other
                  expectOk (run store $"GRANT SELECT ON fsdb.{table} TO derived_grant_inner_owner") "restore grouped source read"
              expectOk (run store "REVOKE SELECT ON fsdb.derived_grant_flags FROM derived_grant_inner_owner") "revoke grouped derived read again"
              expectOk (run store "REVOKE SELECT ON fsdb.derived_grant_aux FROM derived_grant_inner_owner") "revoke grouped physical read again"
              match groupedUpdate () with
              | Err(1356, _) -> ()
              | other -> failtestf "expected grouped derived denial to precede physical join denial, got %A" other
              Expect.equal
                  (rows store "SELECT id,n FROM derived_grant_target ORDER BY id")
                  [ [ Some "1"; Some "2" ]; [ Some "2"; Some "0" ] ]
                  "grouped denied updates leave the physical rows unchanged"

              for sql in
                  [ "GRANT SELECT ON fsdb.derived_grant_flags TO derived_grant_inner_owner"
                    "GRANT SELECT ON fsdb.derived_grant_aux TO derived_grant_inner_owner"
                    "CREATE TABLE derived_grant_aux_n(id INT PRIMARY KEY,n INT)"
                    "INSERT INTO derived_grant_aux_n VALUES(1,7)"
                    "GRANT SELECT ON fsdb.derived_grant_aux_n TO derived_grant_inner_owner"
                    "CREATE DEFINER='derived_grant_inner_owner'@'%' SQL SECURITY DEFINER VIEW bare_grant_inner AS SELECT t.id,t.n,d.flag FROM derived_grant_target t JOIN ((SELECT id,SUM(flag) flag FROM derived_grant_flags GROUP BY id) d JOIN derived_grant_aux_n a ON n=d.flag) ON t.id=d.id"
                    "GRANT SELECT,UPDATE ON fsdb.bare_grant_inner TO derived_grant_outer_owner"
                    "CREATE DEFINER='derived_grant_outer_owner'@'%' SQL SECURITY DEFINER VIEW bare_grant_outer AS SELECT id,n AS metric FROM bare_grant_inner" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='bare_grant_outer'")
                  [ [ Some "YES" ] ] "the bare inner ON binds to the group's source"
              Expect.equal
                  (run store "UPDATE bare_grant_outer SET metric=metric+1 WHERE id=1")
                  (Affected 1UL)
                  "the bound grouped write reaches the target"
              Expect.equal (rows store "SELECT n FROM derived_grant_target WHERE id=1") [ [ Some "3" ] ] "the matched target changes"

          testCase "layered JSON_TABLE view checks join columns before view projection grants"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE json_layer_target(id INT PRIMARY KEY,n INT)"
                    "INSERT INTO json_layer_target VALUES(1,0)"
                    "CREATE USER json_layer_inner_owner"
                    "CREATE USER json_layer_outer_owner"
                    "GRANT SELECT,UPDATE ON fsdb.json_layer_target TO json_layer_inner_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO json_layer_inner_owner,json_layer_outer_owner"
                    "CREATE DEFINER='json_layer_inner_owner'@'%' SQL SECURITY DEFINER VIEW json_layer_inner AS SELECT a.id,a.n,j.x FROM json_layer_target a JOIN JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id"
                    "GRANT SELECT,UPDATE ON fsdb.json_layer_inner TO json_layer_outer_owner"
                    "CREATE DEFINER='json_layer_outer_owner'@'%' SQL SECURITY DEFINER VIEW json_layer_outer AS SELECT id,n AS metric,x FROM json_layer_inner" ] do
                  expectOk (run store sql) sql

              let update () = run store "UPDATE json_layer_outer SET metric=metric WHERE id=1"
              Expect.equal (update ()) (Affected 0UL) "the complete grant chain permits the update"
              expectOk (run store "REVOKE SELECT ON fsdb.json_layer_target FROM json_layer_inner_owner") "revoke the base read grant"
              match update () with
              | Err(1143, message) -> Expect.stringContains message "column 'id'" "join key is denied first"
              | other -> failtestf "expected the missing join-key grant to return 1143, got %A" other
              expectOk (run store "GRANT SELECT(id) ON fsdb.json_layer_target TO json_layer_inner_owner") "grant the join key"
              match update () with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the missing projected-column grant to invalidate the view, got %A" other
              expectOk (run store "GRANT SELECT(n) ON fsdb.json_layer_target TO json_layer_inner_owner") "grant the projected column"
              Expect.equal (update ()) (Affected 0UL) "the two column grants permit the update"
              Expect.equal (rows store "SELECT n FROM json_layer_target") [ [ Some "0" ] ] "denied writes leave the base row unchanged"

              for sql in
                  [ "CREATE DEFINER='json_layer_inner_owner'@'%' SQL SECURITY DEFINER VIEW dependent_json_inner AS SELECT a.id,a.n,j.x FROM json_layer_target a JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id"
                    "GRANT SELECT,UPDATE ON fsdb.dependent_json_inner TO json_layer_outer_owner"
                    "CREATE DEFINER='json_layer_outer_owner'@'%' SQL SECURITY DEFINER VIEW dependent_json_outer AS SELECT id,n AS metric,x FROM dependent_json_inner" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (run store "UPDATE dependent_json_outer SET metric=metric WHERE id=1")
                  (Affected 0UL)
                  "the dependent table function permits the update"
              expectOk (run store "REVOKE SELECT(id,n) ON fsdb.json_layer_target FROM json_layer_inner_owner") "revoke dependent source reads"
              match run store "UPDATE dependent_json_outer SET metric=metric WHERE id=1" with
              | Err(1143, message) -> Expect.stringContains message "column 'id'" "dependent table-function read is a direct join denial"
              | other -> failtestf "expected a dependent table-function read denial, got %A" other
              expectOk (run store "GRANT SELECT(id,n) ON fsdb.json_layer_target TO json_layer_inner_owner") "restore dependent source reads"

              for sql in
                  [ "CREATE TABLE json_layer_companion(id INT PRIMARY KEY)"
                    "INSERT INTO json_layer_companion VALUES(1)"
                    "GRANT SELECT ON fsdb.json_layer_companion TO json_layer_inner_owner"
                    "CREATE DEFINER='json_layer_inner_owner'@'%' SQL SECURITY DEFINER VIEW regular_join_inner AS SELECT a.id,a.n FROM json_layer_target a JOIN json_layer_companion b ON b.id=a.id"
                    "GRANT SELECT,UPDATE ON fsdb.regular_join_inner TO json_layer_outer_owner"
                    "CREATE DEFINER='json_layer_outer_owner'@'%' SQL SECURITY DEFINER VIEW regular_join_outer AS SELECT id,n AS metric FROM regular_join_inner" ] do
                  expectOk (run store sql) sql
              expectOk (run store "REVOKE SELECT(id,n) ON fsdb.json_layer_target FROM json_layer_inner_owner") "revoke the base columns"
              match run store "UPDATE regular_join_outer SET metric=metric WHERE id=1" with
              | Err(1143, message) -> Expect.stringContains message "column 'id'" "ordinary join key is denied first"
              | other -> failtestf "expected an ordinary join-key denial, got %A" other
              expectOk (run store "GRANT SELECT ON fsdb.json_layer_target TO json_layer_inner_owner") "restore the target read grant"
              expectOk (run store "REVOKE SELECT ON fsdb.json_layer_companion FROM json_layer_inner_owner") "revoke the joined source grant"
              match run store "UPDATE regular_join_outer SET metric=metric WHERE id=1" with
              | Err(1143, message) -> Expect.stringContains message "table 'json_layer_companion'" "joined source key is denied"
              | other -> failtestf "expected the other join-key denial, got %A" other

              for sql in
                  [ "GRANT SELECT,UPDATE ON fsdb.json_layer_target TO json_layer_inner_owner,json_layer_outer_owner"
                    "CREATE DEFINER='json_layer_inner_owner'@'%' SQL SECURITY INVOKER VIEW invoker_join_inner AS SELECT a.id,a.n,j.x FROM json_layer_target a JOIN JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id"
                    "GRANT SELECT,UPDATE ON fsdb.invoker_join_inner TO json_layer_outer_owner"
                    "CREATE DEFINER='json_layer_outer_owner'@'%' SQL SECURITY DEFINER VIEW invoker_join_outer AS SELECT id,n AS metric,x FROM invoker_join_inner" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (run store "UPDATE invoker_join_outer SET metric=metric WHERE id=1")
                  (Affected 0UL)
                  "the inner invoker view can update through the outer definer"
              expectOk (run store "REVOKE SELECT ON fsdb.json_layer_target FROM json_layer_outer_owner") "revoke the effective invoker's read grant"
              match run store "UPDATE invoker_join_outer SET metric=metric WHERE id=1" with
              | Err(1143, message) ->
                  Expect.stringContains message "json_layer_outer_owner" "the outer definer is the effective invoker"
                  Expect.stringContains message "column 'id'" "the join key is denied first"
              | other -> failtestf "expected the inner view's effective invoker to lose its join grant, got %A" other

          testCase "joined outer views inline a qualified inner JSON_TABLE source"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              for sql in
                  [ "CREATE TABLE nested_json_target(id INT PRIMARY KEY,n INT)"
                    "INSERT INTO nested_json_target VALUES(1,0),(2,0)"
                    "CREATE VIEW nested_json_inner AS SELECT a.id,a.n,j.x FROM nested_json_target a JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id WHERE a.n>=0 WITH CASCADED CHECK OPTION"
                    "CREATE VIEW nested_json_outer AS SELECT v.id,v.n,v.x,j.k FROM nested_json_inner v JOIN (SELECT 1 AS k) j ON j.k=v.id WHERE v.n<10 WITH CASCADED CHECK OPTION" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='nested_json_outer'")
                  [ [ Some "YES" ] ]
                  "the layered JSON_TABLE target remains updatable despite the private alias collision"
              Expect.equal (run store "UPDATE nested_json_outer SET n=1 WHERE id=1") (Affected 1UL) "the joined layer updates the physical row"

              for rejected in [ -1; 10 ] do
                  match run store (sprintf "UPDATE nested_json_outer SET n=%d WHERE id=1" rejected) with
                  | Err(1369, _) -> ()
                  | other -> failtestf "expected CHECK OPTION rejection for %d, got %A" rejected other

              Expect.equal
                  (rows store "SELECT id,n FROM nested_json_target ORDER BY id")
                  [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ]
                  "both rejected writes leave the accepted value intact"

          testCase "joined outer views rebind qualified inner LATERAL references"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              for sql in
                  [ "CREATE TABLE nested_lateral_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE nested_lateral_aux(id INT PRIMARY KEY,aux INT)"
                    "INSERT INTO nested_lateral_target VALUES(1,0),(2,0)"
                    "INSERT INTO nested_lateral_aux VALUES(1,7)"
                    "CREATE VIEW nested_lateral_inner AS SELECT t.id,t.n,d.aux FROM nested_lateral_target t JOIN LATERAL (SELECT a.aux FROM nested_lateral_aux a WHERE a.id=t.id) d ON d.aux>0 WHERE t.n>=0 WITH CASCADED CHECK OPTION"
                    "CREATE VIEW nested_lateral_outer AS SELECT v.id,v.n,v.aux,q.mark FROM nested_lateral_inner v JOIN (SELECT 1 AS mark) q ON q.mark=v.id WHERE v.n<10 WITH CASCADED CHECK OPTION" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='nested_lateral_outer'")
                  [ [ Some "YES" ] ]
                  "the qualified dependent source leaves the joined layer writable"
              Expect.equal (run store "UPDATE nested_lateral_outer SET n=7 WHERE id=1") (Affected 1UL) "the layer updates its matched row"

              for rejected in [ -1; 10 ] do
                  match run store (sprintf "UPDATE nested_lateral_outer SET n=%d WHERE id=1" rejected) with
                  | Err(1369, _) -> ()
                  | other -> failtestf "expected CHECK OPTION rejection for %d, got %A" rejected other

              Expect.equal
                  (rows store "SELECT id,n FROM nested_lateral_target ORDER BY id")
                  [ [ Some "1"; Some "7" ]; [ Some "2"; Some "0" ] ]
                  "the accepted value survives both rejected writes"

          testCase "joined outer views retain bare local columns in inner LATERAL sources"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              for sql in
                  [ "CREATE TABLE nested_lateral_bare_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE nested_lateral_bare_aux(id INT PRIMARY KEY,aux INT)"
                    "INSERT INTO nested_lateral_bare_target VALUES(1,0)"
                    "INSERT INTO nested_lateral_bare_aux VALUES(1,7)"
                    "CREATE VIEW nested_lateral_bare_inner AS SELECT t.id,t.n,d.aux FROM nested_lateral_bare_target t JOIN LATERAL (SELECT aux FROM nested_lateral_bare_aux a WHERE a.id=t.id) d ON d.aux>0 WHERE t.n>=0 WITH CASCADED CHECK OPTION"
                    "CREATE VIEW nested_lateral_bare_outer AS SELECT v.id,v.n,v.aux,q.mark FROM nested_lateral_bare_inner v JOIN (SELECT 1 AS mark) q ON q.mark=v.id WHERE v.n<10 WITH CASCADED CHECK OPTION" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='nested_lateral_bare_outer'")
                  [ [ Some "YES" ] ]
                  "a bare local LATERAL column does not make the outer view read-only"
              Expect.equal
                  (run store "UPDATE nested_lateral_bare_outer SET n=7 WHERE id=1")
                  (Affected 1UL)
                  "the outer layer updates its target"

              match run store "UPDATE nested_lateral_bare_outer SET n=10 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected outer CHECK OPTION rejection, got %A" other

              Expect.equal
                  (rows store "SELECT id,n FROM nested_lateral_bare_target")
                  [ [ Some "1"; Some "7" ] ]
                  "the rejected write preserves the accepted value"

              for sql in
                  [ "CREATE TABLE nested_lateral_bare_shadow(id INT PRIMARY KEY,aux INT,n INT)"
                    "INSERT INTO nested_lateral_bare_shadow VALUES(1,7,-1)"
                    "CREATE VIEW nested_lateral_shadow_inner AS SELECT t.id,t.n,d.aux FROM nested_lateral_bare_target t JOIN LATERAL (SELECT a.aux FROM nested_lateral_bare_shadow a WHERE a.id=t.id AND n>=0) d ON d.aux>0 WHERE t.n>=0 WITH CASCADED CHECK OPTION"
                    "CREATE VIEW nested_lateral_shadow_outer AS SELECT v.id,v.n,v.aux,q.mark FROM nested_lateral_shadow_inner v JOIN (SELECT 1 AS mark) q ON q.mark=v.id WHERE v.n<10 WITH CASCADED CHECK OPTION" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='nested_lateral_shadow_outer'")
                  [ [ Some "YES" ] ]
                  "the local column shadows the preceding target column"
              Expect.equal
                  (run store "UPDATE nested_lateral_shadow_outer SET n=8 WHERE id=1")
                  (Affected 0UL)
                  "the local negative value excludes the row despite the target's positive value"

          testCase "joined outer views rebind bare outer columns in inner LATERAL sources"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              for sql in
                  [ "CREATE TABLE nested_lateral_outer_bare_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE nested_lateral_outer_bare_aux(id INT PRIMARY KEY,aux INT)"
                    "INSERT INTO nested_lateral_outer_bare_target VALUES(1,0)"
                    "INSERT INTO nested_lateral_outer_bare_aux VALUES(1,7)"
                    "CREATE VIEW nested_lateral_outer_bare_inner AS SELECT t.id,t.n,d.aux FROM nested_lateral_outer_bare_target t JOIN LATERAL (SELECT a.aux FROM nested_lateral_outer_bare_aux a WHERE a.id=t.id AND n>=0) d ON d.aux>0 WHERE t.n>=0 WITH CASCADED CHECK OPTION"
                    "CREATE VIEW nested_lateral_outer_bare_outer AS SELECT v.id,v.n,v.aux,q.mark FROM nested_lateral_outer_bare_inner v JOIN (SELECT 1 AS mark) q ON q.mark=v.id WHERE v.n<10 WITH CASCADED CHECK OPTION" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='nested_lateral_outer_bare_outer'")
                  [ [ Some "YES" ] ]
                  "the bare outer target reference is rebound under the joined layer"
              Expect.equal
                  (run store "UPDATE nested_lateral_outer_bare_outer SET n=7 WHERE id=1")
                  (Affected 1UL)
                  "the outer layer updates the target"

              match run store "UPDATE nested_lateral_outer_bare_outer SET n=10 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected outer CHECK OPTION rejection, got %A" other

              Expect.equal
                  (rows store "SELECT id,n FROM nested_lateral_outer_bare_target")
                  [ [ Some "1"; Some "7" ] ]
                  "the rejected write preserves the accepted value"

          testCase "joined outer views rebind bare references to preceding LATERAL columns"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              for sql in
                  [ "CREATE TABLE nested_lateral_chain_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE nested_lateral_chain_aux(id INT PRIMARY KEY,aux INT)"
                    "INSERT INTO nested_lateral_chain_target VALUES(1,0)"
                    "INSERT INTO nested_lateral_chain_aux VALUES(1,7)"
                    "CREATE VIEW nested_lateral_chain_inner AS SELECT t.id,t.n,d.aux,e.bump FROM nested_lateral_chain_target t JOIN LATERAL (SELECT a.aux FROM nested_lateral_chain_aux a WHERE a.id=t.id) d ON d.aux>0 JOIN LATERAL (SELECT aux+1 AS bump) e ON e.bump>0 WHERE t.n>=0 WITH CASCADED CHECK OPTION"
                    "CREATE VIEW nested_lateral_chain_outer AS SELECT v.id,v.n,v.aux,v.bump,d.mark FROM nested_lateral_chain_inner v JOIN (SELECT 1 AS mark) d ON d.mark=v.id WHERE v.n<10 WITH CASCADED CHECK OPTION" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='nested_lateral_chain_outer'")
                  [ [ Some "YES" ] ]
                  "a preceding LATERAL output can own the bare column"
              Expect.equal
                  (run store "UPDATE nested_lateral_chain_outer SET n=7 WHERE id=1")
                  (Affected 1UL)
                  "the joined layer updates its target after alias rebinding"

              match run store "UPDATE nested_lateral_chain_outer SET n=10 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected outer CHECK OPTION rejection, got %A" other

              Expect.equal
                  (rows store "SELECT id,n FROM nested_lateral_chain_target")
                  [ [ Some "1"; Some "7" ] ]
                  "the rejected write preserves the accepted value"

          testCase "layered JSON_TABLE chains retain writable target grants across source orders"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE json_chain_target(id INT PRIMARY KEY,n INT)"
                    "INSERT INTO json_chain_target VALUES(1,0),(2,0)"
                    "CREATE USER json_chain_inner_owner"
                    "CREATE USER json_chain_outer_owner"
                    "GRANT SELECT,UPDATE ON fsdb.json_chain_target TO json_chain_inner_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO json_chain_inner_owner,json_chain_outer_owner" ] do
                  expectOk (run store sql) sql

              let sourceCases =
                  [ "json_chain_inner", "json_chain_outer",
                    "FROM JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j JOIN JSON_TABLE(JSON_ARRAY(j.x),'$[*]' COLUMNS(y INT PATH '$')) k ON k.y=j.x JOIN json_chain_target t ON t.id=k.y"
                    "json_sandwich_inner", "json_sandwich_outer",
                    "FROM JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j JOIN json_chain_target t ON t.id=j.x JOIN JSON_TABLE(JSON_ARRAY(t.id),'$[*]' COLUMNS(y INT PATH '$')) k ON k.y=t.id" ]

              for innerName, outerName, source in sourceCases do
                  for sql in
                      [ sprintf "CREATE DEFINER='json_chain_inner_owner'@'%%' SQL SECURITY DEFINER VIEW %s AS SELECT t.id,t.n,j.x,k.y %s" innerName source
                        sprintf "GRANT SELECT,UPDATE ON fsdb.%s TO json_chain_outer_owner" innerName
                        sprintf "CREATE DEFINER='json_chain_outer_owner'@'%%' SQL SECURITY DEFINER VIEW %s AS SELECT id,n AS metric FROM %s" outerName innerName ] do
                      expectOk (run store sql) sql

                  Expect.equal
                      (rows store (sprintf "SELECT is_updatable FROM information_schema.views WHERE table_name='%s'" outerName))
                      [ [ Some "YES" ] ]
                      "the dependent table-function chain leaves the physical target writable"
                  let update () = run store (sprintf "UPDATE %s SET metric=metric+1 WHERE id=1" outerName)
                  Expect.equal (update ()) (Affected 1UL) "the layered chain updates its matched target"
                  expectOk (run store "REVOKE SELECT ON fsdb.json_chain_target FROM json_chain_inner_owner") "revoke the target read"
                  match update () with
                  | Err(1143, message) -> Expect.stringContains message "column 'id'" "the target join key is denied"
                  | other -> failtestf "expected a target join-key denial, got %A" other
                  expectOk (run store "GRANT SELECT ON fsdb.json_chain_target TO json_chain_inner_owner") "restore the target read"
                  expectOk (run store "REVOKE UPDATE ON fsdb.json_chain_target FROM json_chain_inner_owner") "revoke the target write"
                  match update () with
                  | Err(1356, _) -> ()
                  | other -> failtestf "expected the inner definer's missing UPDATE to invalidate the view, got %A" other
                  expectOk (run store "GRANT UPDATE ON fsdb.json_chain_target TO json_chain_inner_owner") "restore the target write"
                  expectOk (run store (sprintf "REVOKE UPDATE ON fsdb.%s FROM json_chain_outer_owner" innerName)) "revoke the component write"
                  match update () with
                  | Err(1356, _) -> ()
                  | other -> failtestf "expected the outer definer's missing component UPDATE to invalidate the view, got %A" other
              Expect.equal
                  (rows store "SELECT id,n FROM json_chain_target ORDER BY id")
                  [ [ Some "1"; Some "2" ]; [ Some "2"; Some "0" ] ]
                  "denied writes do not alter the target"

          testCase "join view writes honor a component view's distinct definer"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE security_left(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE security_right(id INT PRIMARY KEY,left_id INT,n INT)"
                    "INSERT INTO security_left VALUES(1,10)"
                    "INSERT INTO security_right VALUES(1,1,100)"
                    "CREATE USER inner_join_owner"
                    "CREATE USER outer_join_owner"
                    "GRANT SELECT,UPDATE ON fsdb.security_left TO inner_join_owner"
                    "GRANT SELECT,UPDATE ON fsdb.security_right TO outer_join_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO inner_join_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO outer_join_owner"
                    "CREATE DEFINER='inner_join_owner'@'%' VIEW secured_component AS SELECT id,n FROM security_left"
                    "GRANT SELECT,UPDATE ON fsdb.secured_component TO outer_join_owner"
                    "CREATE DEFINER='outer_join_owner'@'%' VIEW secured_join AS SELECT i.id,i.n,r.n AS right_n FROM secured_component i JOIN security_right r ON r.left_id=i.id" ] do
                  expectOk (run store sql) sql

              expectOk (run store "UPDATE secured_join SET n=n+1 WHERE id=1") "update through both definers"
              Expect.equal (rows store "SELECT n FROM security_left") [ [ Some "11" ] ] "the base row changes"
              expectOk (run store "UPDATE secured_join SET right_n=right_n+1 WHERE id=1") "update outer owner's table"
              Expect.equal (rows store "SELECT n FROM security_right") [ [ Some "101" ] ] "the other base row changes"
              expectOk (run store "REVOKE UPDATE ON fsdb.security_left FROM inner_join_owner") "revoke inner write grant"
              match run store "UPDATE secured_join SET n=n+1 WHERE id=1" with
              | Err(1142, message) -> Expect.stringContains message "security_left" "the inner definer needs base-table UPDATE"
              | other -> failtestf "expected the inner definer's revoked grant to block the write, got %A" other
              expectOk (run store "UPDATE secured_join SET right_n=right_n+1 WHERE id=1") "inner write revocation does not block the other table"
              Expect.equal (rows store "SELECT n FROM security_right") [ [ Some "102" ] ] "the other row changes again"
              expectOk (run store "REVOKE SELECT ON fsdb.security_left FROM inner_join_owner") "revoke inner read grant"
              match run store "UPDATE secured_join SET right_n=right_n+1 WHERE id=1" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the invalid inner view to block the outer table's write, got %A" other
              Expect.equal (rows store "SELECT n FROM security_right") [ [ Some "102" ] ] "the denied write changes nothing"
              expectOk (run store "GRANT SELECT ON fsdb.security_left TO inner_join_owner") "restore inner read grant"
              expectOk (run store "REVOKE UPDATE ON fsdb.secured_component FROM outer_join_owner") "revoke outer view write grant"
              match run store "UPDATE secured_join SET right_n=right_n+1 WHERE id=1" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the invalid component grant to block the other table's write, got %A" other
              match run store "UPDATE secured_join SET n=n+1 WHERE id=1" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the invalid component grant to block its own table's write, got %A" other
              Expect.equal (rows store "SELECT n FROM security_right") [ [ Some "102" ] ] "the denied outer write changes nothing"
              expectOk (run store "GRANT UPDATE(n) ON fsdb.secured_component TO outer_join_owner") "grant one writable view column"
              expectOk (run store "UPDATE secured_join SET right_n=right_n+1 WHERE id=1") "a column-level component grant restores validity"
              Expect.equal (rows store "SELECT n FROM security_right") [ [ Some "103" ] ] "the column grant allows the outer write"
              expectOk (run store "REVOKE UPDATE ON fsdb.security_right FROM outer_join_owner") "revoke outer base write grant"
              match run store "UPDATE secured_join SET right_n=right_n+1 WHERE id=1" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the outer view to become invalid after base grant revocation, got %A" other
              Expect.equal (rows store "SELECT n FROM security_right") [ [ Some "103" ] ] "the revoked base grant prevents writes"

          testCase "joined view update authorizes a renamed component column"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE alias_left(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE alias_right(id INT PRIMARY KEY,left_id INT,n INT)"
                    "INSERT INTO alias_left VALUES(1,10)"
                    "INSERT INTO alias_right VALUES(1,1,100)"
                    "CREATE USER alias_inner_owner"
                    "CREATE USER alias_outer_owner"
                    "GRANT SELECT,UPDATE ON fsdb.alias_left TO alias_inner_owner"
                    "GRANT SELECT,UPDATE ON fsdb.alias_right TO alias_outer_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO alias_inner_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO alias_outer_owner"
                    "CREATE DEFINER='alias_inner_owner'@'%' VIEW alias_component AS SELECT id,n AS metric,n AS other FROM alias_left"
                    "GRANT SELECT,UPDATE(metric) ON fsdb.alias_component TO alias_outer_owner"
                    "CREATE DEFINER='alias_outer_owner'@'%' VIEW alias_join AS SELECT i.id,i.metric AS label,i.other AS other_label,r.n AS right_n FROM alias_component i JOIN alias_right r ON r.left_id=i.id" ] do
                  expectOk (run store sql) sql

              expectOk (run store "UPDATE alias_join SET label=label+1 WHERE id=1") "write the twice-renamed component column"
              Expect.equal (rows store "SELECT n FROM alias_left") [ [ Some "11" ] ] "the physical column changes"
              match run store "UPDATE alias_join SET other_label=other_label+1 WHERE id=1" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the ungranted alias of the same physical column to be denied, got %A" other
              expectOk (run store "UPDATE alias_join SET right_n=right_n+1 WHERE id=1") "write the physical component"
              Expect.equal (rows store "SELECT n FROM alias_right") [ [ Some "101" ] ] "the other component changes"
              expectOk (run store "UPDATE alias_join SET label=right_n+1 WHERE id=1") "read the other component while writing the alias"
              Expect.equal (rows store "SELECT n FROM alias_left") [ [ Some "102" ] ] "the cross-source expression uses the joined row"

          testCase "joined view insert follows a component view's distinct definer"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE insert_left(id INT PRIMARY KEY AUTO_INCREMENT,n INT)"
                    "CREATE TABLE insert_right(id INT PRIMARY KEY,left_id INT,n INT)"
                    "INSERT INTO insert_right VALUES(1,1,100)"
                    "CREATE USER insert_inner_owner"
                    "CREATE USER insert_outer_owner"
                    "GRANT SELECT,INSERT,UPDATE ON fsdb.insert_left TO insert_inner_owner"
                    "GRANT SELECT,INSERT,UPDATE ON fsdb.insert_right TO insert_outer_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO insert_inner_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO insert_outer_owner"
                    "CREATE DEFINER='insert_inner_owner'@'%' VIEW insert_component AS SELECT id,n FROM insert_left"
                    "GRANT SELECT,INSERT,UPDATE ON fsdb.insert_component TO insert_outer_owner"
                    "CREATE DEFINER='insert_outer_owner'@'%' VIEW insert_join AS SELECT i.id,i.n,r.n AS right_n FROM insert_component i JOIN insert_right r ON r.left_id=i.id" ] do
                  expectOk (run store sql) sql

              expectOk (run store "INSERT INTO insert_join(id,n) VALUES(2,20)") "insert into the inner component"
              Expect.equal (rows store "SELECT id,n FROM insert_left") [ [ Some "2"; Some "20" ] ] "the component row is stored"
              expectOk (run store "REVOKE UPDATE ON fsdb.insert_component FROM insert_outer_owner") "revoke outer component update grant"
              match run store "INSERT INTO insert_join(id,n) VALUES(2,30) ON DUPLICATE KEY UPDATE n=VALUES(n)" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected ODKU to require component UPDATE even on a duplicate, got %A" other
              match run store "INSERT INTO insert_join(id,n) VALUES(5,50) ON DUPLICATE KEY UPDATE n=VALUES(n)" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected ODKU to require component UPDATE on a fresh key, got %A" other
              Expect.equal (rows store "SELECT id,n FROM insert_left") [ [ Some "2"; Some "20" ] ] "denied ODKU changes nothing"
              expectOk (run store "GRANT UPDATE ON fsdb.insert_component TO insert_outer_owner") "restore outer update grant"
              expectOk (run store "INSERT INTO insert_join(id,n) VALUES(2,21) ON DUPLICATE KEY UPDATE n=VALUES(n)") "ODKU with both component grants"
              Expect.equal (rows store "SELECT id,n FROM insert_left") [ [ Some "2"; Some "21" ] ] "the authorized duplicate updates"
              expectOk (run store "REVOKE UPDATE ON fsdb.insert_left FROM insert_inner_owner") "revoke inner update grant"
              match run store "INSERT INTO insert_join(id,n) VALUES(2,22) ON DUPLICATE KEY UPDATE n=VALUES(n)" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected ODKU to require inner base UPDATE on a duplicate, got %A" other
              match run store "INSERT INTO insert_join(id,n) VALUES(5,50) ON DUPLICATE KEY UPDATE n=VALUES(n)" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected ODKU to require inner base UPDATE on a fresh key, got %A" other
              expectOk (run store "GRANT UPDATE ON fsdb.insert_left TO insert_inner_owner") "restore inner update grant"
              expectOk (run store "REVOKE INSERT ON fsdb.insert_left FROM insert_inner_owner") "revoke inner insert grant"
              match run store "INSERT INTO insert_join(id,n) VALUES(3,30)" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the inner definer's revoked INSERT to invalidate the joined view, got %A" other
              expectOk (run store "GRANT INSERT ON fsdb.insert_left TO insert_inner_owner") "restore inner insert grant"
              expectOk (run store "REVOKE INSERT ON fsdb.insert_component FROM insert_outer_owner") "revoke outer component insert grant"
              match run store "INSERT INTO insert_join(id,n) VALUES(4,40)" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the outer definer's revoked component INSERT to invalidate the joined view, got %A" other
              Expect.equal (rows store "SELECT id,n FROM insert_left") [ [ Some "2"; Some "21" ] ] "denied inserts change nothing"
              expectOk (run store "GRANT INSERT(n) ON fsdb.insert_component TO insert_outer_owner") "grant one insertable component column"
              expectOk (run store "INSERT INTO insert_join(n) VALUES(50)") "insert the granted column"
              match run store "INSERT INTO insert_join(id,n) VALUES(4,40)" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the ungranted component id to block the insert, got %A" other
              expectOk (run store "INSERT INTO insert_join(n) SELECT 60") "insert-select the granted column"
              match run store "INSERT INTO insert_join(id,n) SELECT 6,70" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the ungranted component id to block insert-select, got %A" other
              Expect.equal
                  (rows store "SELECT id,n FROM insert_left ORDER BY id")
                  [ [ Some "2"; Some "21" ]; [ Some "3"; Some "50" ]; [ Some "4"; Some "60" ] ]
                  "column grants protect the nested insert"

          testCase "joined view insert follows two distinct definer layers"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE deep_a(id INT PRIMARY KEY AUTO_INCREMENT,n INT)"
                    "CREATE TABLE deep_b(id INT PRIMARY KEY,left_id INT)"
                    "INSERT INTO deep_b VALUES(1,1)"
                    "CREATE USER deep_inner_owner"
                    "CREATE USER deep_middle_owner"
                    "CREATE USER deep_outer_owner"
                    "GRANT SELECT,INSERT,UPDATE ON fsdb.deep_a TO deep_inner_owner"
                    "GRANT SELECT,INSERT,UPDATE ON fsdb.deep_b TO deep_outer_owner"
                    "GRANT CREATE VIEW ON fsdb.* TO deep_inner_owner,deep_middle_owner,deep_outer_owner"
                    "CREATE DEFINER='deep_inner_owner'@'%' VIEW deep_inner AS SELECT id,n AS value FROM deep_a"
                    "GRANT SELECT,INSERT,UPDATE ON fsdb.deep_inner TO deep_middle_owner"
                    "CREATE DEFINER='deep_middle_owner'@'%' VIEW deep_middle AS SELECT id,value AS metric FROM deep_inner"
                    "GRANT SELECT,INSERT,UPDATE ON fsdb.deep_middle TO deep_outer_owner"
                    "CREATE DEFINER='deep_outer_owner'@'%' VIEW deep_outer AS SELECT m.id,m.metric,b.id AS b_id FROM deep_middle m JOIN deep_b b ON b.left_id=m.id" ] do
                  expectOk (run store sql) sql

              expectOk (run store "INSERT INTO deep_outer(metric) VALUES(10)") "insert through two view layers"
              expectOk (run store "INSERT INTO deep_outer(id,metric) VALUES(2,20)") "insert both exposed columns"
              expectOk (run store "REVOKE INSERT ON fsdb.deep_inner FROM deep_middle_owner") "revoke middle-layer INSERT"
              match run store "INSERT INTO deep_outer(metric) VALUES(30)" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the revoked middle grant to invalidate the outer view, got %A" other
              expectOk (run store "GRANT INSERT(value) ON fsdb.deep_inner TO deep_middle_owner") "grant the middle layer's written column"
              expectOk (run store "INSERT INTO deep_outer(metric) VALUES(40)") "insert with middle-layer column grant"
              expectOk (run store "INSERT INTO deep_outer(metric) SELECT 45") "insert-select with middle-layer column grant"
              match run store "INSERT INTO deep_outer(id,metric) VALUES(5,50)" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the ungranted id through the middle layer to be denied, got %A" other
              Expect.equal
                  (rows store "SELECT id,n FROM deep_a ORDER BY id")
                  [ [ Some "1"; Some "10" ]; [ Some "2"; Some "20" ]; [ Some "3"; Some "40" ]; [ Some "4"; Some "45" ] ]
                  "the authorized inserts reach the base table"

              expectOk (run store "GRANT INSERT ON fsdb.deep_inner TO deep_middle_owner") "restore the middle insert grant"
              expectOk (run store "REVOKE UPDATE ON fsdb.deep_inner FROM deep_middle_owner") "revoke the middle update grant"
              match run store "INSERT INTO deep_outer(id,metric) VALUES(2,25) ON DUPLICATE KEY UPDATE id=id+10" with
              | Err(1356, _) -> ()
              | other -> failtestf "expected missing middle-layer UPDATE to invalidate ODKU, got %A" other
              expectOk (run store "GRANT UPDATE(value) ON fsdb.deep_inner TO deep_middle_owner") "grant one middle update column"
              expectOk
                  (run store "INSERT INTO deep_outer(id,metric) VALUES(2,25) ON DUPLICATE KEY UPDATE metric=VALUES(metric)")
                  "update the granted column through two view layers"
              expectOk
                  (run store "INSERT INTO deep_outer(id,metric) VALUES(2,30) ON DUPLICATE KEY UPDATE id=id+10")
                  "MySQL validates the middle view with any update grant"
              expectOk
                  (run store "INSERT INTO deep_outer(id,metric) VALUES(5,50) ON DUPLICATE KEY UPDATE id=id+10")
                  "fresh ODKU insert through two view layers"
              expectOk (run store "REVOKE UPDATE ON fsdb.deep_middle FROM deep_outer_owner") "revoke the outer component update grant"
              expectOk (run store "GRANT UPDATE(metric) ON fsdb.deep_middle TO deep_outer_owner") "grant one outer component update column"
              expectOk
                  (run store "INSERT INTO deep_outer(id,metric) VALUES(5,55) ON DUPLICATE KEY UPDATE id=id+10")
                  "MySQL validates the outer component with any update grant"
              Expect.equal
                  (rows store "SELECT id,n FROM deep_a ORDER BY id")
                  [ [ Some "1"; Some "10" ]; [ Some "3"; Some "40" ]; [ Some "4"; Some "45" ]; [ Some "12"; Some "25" ]; [ Some "15"; Some "50" ] ]
                  "the ODKU rows follow MySQL's definer-view authorization"

          testCase "join views update mergeable components beside materialized views"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE view_amounts (group_id INT, amount INT)") "create amounts"
              expectOk (run store "INSERT INTO view_amounts VALUES (1, 4), (1, 6), (2, 20)") "seed amounts"

              expectOk
                  (run store "CREATE TABLE view_targets (id INT PRIMARY KEY, group_id INT NOT NULL DEFAULT 1, n INT NOT NULL)")
                  "create targets"

              expectOk (run store "INSERT INTO view_targets VALUES (1, 1, 10), (2, 2, 20)") "seed targets"

              expectOk
                  (run store "CREATE VIEW materialized_totals AS SELECT group_id, SUM(amount) AS total FROM view_amounts GROUP BY group_id")
                  "create materialized component"

              expectOk
                  (run store "CREATE VIEW targets_with_totals AS SELECT t.id, t.n, x.total FROM view_targets t JOIN materialized_totals x ON x.group_id = t.group_id")
                  "create joined view"

              expectOk
                  (run store "CREATE VIEW reversed_targets_with_totals AS SELECT t.id, t.n, x.total FROM materialized_totals x JOIN view_targets t ON x.group_id = t.group_id")
                  "create reversed joined view"

              Expect.equal
                  (rows store "SELECT table_name, is_updatable FROM information_schema.views WHERE table_name IN ('targets_with_totals', 'reversed_targets_with_totals') ORDER BY table_name")
                  [ [ Some "reversed_targets_with_totals"; Some "YES" ]; [ Some "targets_with_totals"; Some "YES" ] ]
                  "one mergeable component makes the join view updatable"

              expectOk (run store "UPDATE targets_with_totals SET n = 11 WHERE id = 1") "update physical leading component"
              expectOk (run store "UPDATE reversed_targets_with_totals SET n = 21 WHERE id = 2") "update physical joined component"

              match run store "UPDATE targets_with_totals SET total = 99 WHERE id = 1" with
              | Err(1348, "Column 'total' is not updatable") -> ()
              | other -> failtestf "expected materialized-column refusal, got %A" other

              match run store "INSERT INTO targets_with_totals(id, n) VALUES (3, 30)" with
              | Err(1471, "The target table targets_with_totals of the INSERT is not insertable-into") -> ()
              | other -> failtestf "expected materialized join insert refusal, got %A" other

              Expect.equal
                  (rows store "SELECT id, n FROM view_targets ORDER BY id")
                  [ [ Some "1"; Some "11" ]; [ Some "2"; Some "21" ] ]
                  "only the mergeable component changes"

          testCase "join views update a physical table beside a derived aggregate"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE derived_view_a(id INT PRIMARY KEY,n INT)") "create target"
              expectOk (run store "CREATE TABLE derived_view_b(id INT,marker INT)") "create source"
              expectOk (run store "INSERT INTO derived_view_a VALUES(1,10),(2,20)") "seed targets"
              expectOk (run store "INSERT INTO derived_view_b VALUES(1,100),(2,200)") "seed source"
              let aggregate = "(SELECT id,SUM(marker) AS marker FROM derived_view_b GROUP BY id)"
              expectOk
                  (run store ("CREATE VIEW derived_forward AS SELECT a.id,a.n,a.n*2 AS doubled,d.marker FROM derived_view_a a JOIN " + aggregate + " d ON d.id=a.id"))
                  "create forward view"
              expectOk
                  (run store ("CREATE VIEW derived_reverse AS SELECT a.id,a.n,d.marker FROM " + aggregate + " d JOIN derived_view_a a ON d.id=a.id"))
                  "create reverse view"
              expectOk
                  (run store ("CREATE VIEW derived_extra AS SELECT a.id,a.n,d.marker FROM " + aggregate + " d JOIN derived_view_a a ON d.id=a.id JOIN derived_view_b b ON b.id=a.id"))
                  "create three-source view"
              expectOk
                  (run store ("CREATE VIEW derived_straight AS SELECT a.id,a.n,d.marker FROM " + aggregate + " d STRAIGHT_JOIN derived_view_a a ON d.id=a.id"))
                  "create straight join view"
              Expect.equal
                  (rows store "SELECT table_name,is_updatable FROM information_schema.views WHERE table_name IN ('derived_extra','derived_forward','derived_reverse','derived_straight') ORDER BY table_name")
                  [ [ Some "derived_extra"; Some "YES" ]; [ Some "derived_forward"; Some "YES" ]; [ Some "derived_reverse"; Some "YES" ]; [ Some "derived_straight"; Some "YES" ] ]
                  "derived joins retain their writable physical component"
              expectOk (run store "UPDATE derived_forward SET n=11 WHERE id=1") "update forward view"
              expectOk (run store "UPDATE derived_reverse SET n=21 WHERE id=2") "update reverse view"
              expectOk (run store "UPDATE derived_extra SET n=12 WHERE id=1") "update three-source view"
              expectOk (run store "UPDATE derived_straight SET n=13 WHERE id=1") "update straight join view"
              Expect.equal
                  (rows store "SELECT id,n FROM derived_view_a ORDER BY id")
                  [ [ Some "1"; Some "13" ]; [ Some "2"; Some "21" ] ]
                  "only physical target rows change"
              match run store "UPDATE derived_forward SET doubled=24 WHERE id=1" with
              | Err(1348, _) -> ()
              | other -> failtestf "expected computed-column refusal, got %A" other
              match run store "UPDATE derived_forward SET marker=300 WHERE id=1" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected derived column refusal, got %A" other
              match run store "INSERT INTO derived_forward(id,n) VALUES(3,30)" with
              | Err(1471, _) -> ()
              | other -> failtestf "expected noninsertable derived join, got %A" other

          testCase "invoker update through a derived join requires its source read grant"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE derived_grant_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE derived_grant_source(id INT,marker INT)"
                    "INSERT INTO derived_grant_target VALUES(1,10)"
                    "INSERT INTO derived_grant_source VALUES(1,100)"
                    "CREATE SQL SECURITY INVOKER VIEW derived_grant_view AS SELECT a.id,a.n,d.marker FROM derived_grant_target a JOIN (SELECT id,SUM(marker) AS marker FROM derived_grant_source GROUP BY id) d ON d.id=a.id"
                    "CREATE USER derived_grant_reader"
                    "GRANT SELECT,UPDATE ON fsdb.derived_grant_target TO derived_grant_reader"
                    "GRANT SELECT,UPDATE ON fsdb.derived_grant_view TO derived_grant_reader" ] do
                  expectOk (run store sql) sql

              let reader = { Fsdb.Session.create 1 store with User = "derived_grant_reader" }
              let update () = Fsdb.QueryHandler.handle reader "UPDATE derived_grant_view SET n=11 WHERE id=1" |> snd

              match update () with
              | Err(1356, _) -> ()
              | other -> failtestf "expected the missing derived-source grant to deny the update, got %A" other

              Expect.equal (rows store "SELECT n FROM derived_grant_target") [ [ Some "10" ] ] "denied update leaves the target unchanged"
              expectOk (run store "GRANT SELECT ON fsdb.derived_grant_source TO derived_grant_reader") "grant derived-source read"
              expectOk (update ()) "authorized update through derived join"
              Expect.equal (rows store "SELECT n FROM derived_grant_target") [ [ Some "11" ] ] "authorized update reaches the physical target"

          testCase "derived-first USING join views update their physical component"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE derived_using_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE derived_using_source(id INT PRIMARY KEY,flag INT)"
                    "CREATE TABLE derived_using_second(id INT PRIMARY KEY,aux INT)"
                    "CREATE TABLE derived_using_third(id INT PRIMARY KEY,extra INT)"
                    "INSERT INTO derived_using_target VALUES(1,4),(2,20)"
                    "INSERT INTO derived_using_source VALUES(1,7),(3,9)"
                    "INSERT INTO derived_using_second VALUES(1,8),(3,10)"
                    "INSERT INTO derived_using_third VALUES(1,11),(3,12)"
                    "CREATE VIEW derived_using_qualified AS SELECT a.id AS aid,a.n,d.id AS did FROM (SELECT id FROM derived_using_source GROUP BY id) d JOIN derived_using_target a USING(id)"
                    "CREATE VIEW derived_using_merged AS SELECT id,n FROM (SELECT id FROM derived_using_source GROUP BY id) d JOIN derived_using_target a USING(id)"
                    "CREATE VIEW derived_using_two AS SELECT a.id AS aid,a.n,d1.id AS first_id,d2.id AS second_id FROM (SELECT id FROM derived_using_source GROUP BY id) d1 JOIN (SELECT id FROM derived_using_second GROUP BY id) d2 USING(id) JOIN derived_using_target a USING(id)"
                    "CREATE VIEW derived_using_three AS SELECT a.id AS aid,a.n FROM (SELECT id FROM derived_using_source GROUP BY id) d1 JOIN (SELECT id FROM derived_using_second GROUP BY id) d2 USING(id) JOIN (SELECT id FROM derived_using_third GROUP BY id) d3 USING(id) JOIN derived_using_target a USING(id)" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT table_name,is_updatable FROM information_schema.views WHERE table_name LIKE 'derived_using_%' ORDER BY table_name")
                  [ [ Some "derived_using_merged"; Some "YES" ]; [ Some "derived_using_qualified"; Some "YES" ]; [ Some "derived_using_three"; Some "YES" ]; [ Some "derived_using_two"; Some "YES" ] ]
                  "each view retains the writable physical component"
              expectOk (run store "UPDATE derived_using_qualified SET n=n+1 WHERE aid=1") "qualified update"
              expectOk (run store "UPDATE derived_using_merged SET n=n+1 WHERE id=1") "merged-column update"
              Expect.equal
                  (run store "UPDATE derived_using_qualified SET n=n+1 WHERE aid=2")
                  (Affected 0UL) "the USING key excludes unmatched target rows"
              expectOk (run store "UPDATE derived_using_two SET n=n+1 WHERE aid=1") "two derived sources update"
              expectOk (run store "UPDATE derived_using_three SET n=n+1 WHERE aid=1") "three derived sources update"
              Expect.equal
                  (rows store "SELECT id,n FROM derived_using_target ORDER BY id")
                  [ [ Some "1"; Some "8" ]; [ Some "2"; Some "20" ] ]
                  "USING still filters by the shared key"

          testCase "derived-first mixed USING keys retain their original left operand"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE mixed_using_target(id INT PRIMARY KEY,x INT,n INT)"
                    "CREATE TABLE mixed_using_source(id INT PRIMARY KEY,x INT)"
                    "CREATE TABLE mixed_using_key(x INT PRIMARY KEY)"
                    "INSERT INTO mixed_using_target VALUES(1,999,100),(2,20,200)"
                    "INSERT INTO mixed_using_source VALUES(1,10),(2,20)"
                    "INSERT INTO mixed_using_key VALUES(10),(20)"
                    "CREATE VIEW mixed_using_view AS SELECT a.id AS aid,a.n,d1.x AS bx FROM (SELECT id,x FROM mixed_using_source) d1 JOIN (SELECT x FROM mixed_using_key) d2 USING(x) JOIN mixed_using_target a USING(id)"
                    "CREATE VIEW mixed_using_second AS SELECT a.id AS aid,a.n,d2.x AS bx FROM (SELECT x FROM mixed_using_key) d1 JOIN (SELECT id,x FROM mixed_using_source) d2 USING(x) JOIN mixed_using_target a USING(id)" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT table_name,is_updatable FROM information_schema.views WHERE table_name LIKE 'mixed_using_%' ORDER BY table_name")
                  [ [ Some "mixed_using_second"; Some "YES" ]; [ Some "mixed_using_view"; Some "YES" ] ]
                  "MySQL 8.4 reports both mixed-key views as updatable"
              expectOk (run store "UPDATE mixed_using_view SET n=101 WHERE aid=1") "update through mixed-key view"
              expectOk (run store "UPDATE mixed_using_second SET n=102 WHERE aid=1") "update when only the later derived source has the target key"
              Expect.equal
                  (rows store "SELECT id,x,n FROM mixed_using_target ORDER BY id")
                  [ [ Some "1"; Some "999"; Some "102" ]; [ Some "2"; Some "20"; Some "200" ] ]
                  "the x match belongs to the derived sources, not the target"

          testCase "derived-first NATURAL joins update their physical component"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE natural_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE natural_extra_target(id INT PRIMARY KEY,x INT,n INT)"
                    "CREATE TABLE natural_source(id INT PRIMARY KEY,x INT)"
                    "CREATE TABLE natural_key(x INT PRIMARY KEY)"
                    "INSERT INTO natural_target VALUES(1,10),(2,20)"
                    "INSERT INTO natural_extra_target VALUES(1,999,10),(2,888,20)"
                    "INSERT INTO natural_source VALUES(1,7),(2,8)"
                    "INSERT INTO natural_key VALUES(7),(8)"
                    "CREATE VIEW natural_one AS SELECT a.id AS aid,a.n FROM (SELECT id,x FROM natural_source) d NATURAL JOIN natural_target a"
                    "CREATE VIEW natural_first AS SELECT a.id AS aid,a.n FROM (SELECT id,x FROM natural_source) d1 NATURAL JOIN (SELECT x FROM natural_key) d2 JOIN natural_target a USING(id)"
                    "CREATE VIEW natural_extra AS SELECT a.id AS aid,a.n FROM (SELECT id,x FROM natural_source) d1 NATURAL JOIN (SELECT x FROM natural_key) d2 JOIN natural_extra_target a USING(id)"
                    "CREATE VIEW natural_last AS SELECT a.id AS aid,a.n FROM (SELECT id,x FROM natural_source) d1 JOIN (SELECT x FROM natural_key) d2 USING(x) NATURAL JOIN natural_target a" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT table_name,is_updatable FROM information_schema.views WHERE table_name IN ('natural_extra','natural_first','natural_last','natural_one') ORDER BY table_name")
                  [ [ Some "natural_extra"; Some "YES" ]; [ Some "natural_first"; Some "YES" ]; [ Some "natural_last"; Some "YES" ]; [ Some "natural_one"; Some "YES" ] ]
                  "MySQL 8.4 reports all derived-first NATURAL join views as updatable"
              expectOk (run store "UPDATE natural_first SET n=11 WHERE aid=1") "update with leading NATURAL join"
              expectOk (run store "UPDATE natural_last SET n=12 WHERE aid=1") "update with target NATURAL join"
              expectOk (run store "UPDATE natural_one SET n=13 WHERE aid=1") "update with one derived NATURAL join"
              expectOk (run store "UPDATE natural_extra SET n=11 WHERE aid=1") "the moved target does not add a NATURAL x key"
              Expect.equal
                  (rows store "SELECT id,n FROM natural_target ORDER BY id")
                  [ [ Some "1"; Some "13" ]; [ Some "2"; Some "20" ] ]
                  "only the matched physical row is updated"
              Expect.equal
                  (rows store "SELECT id,x,n FROM natural_extra_target ORDER BY id")
                  [ [ Some "1"; Some "999"; Some "11" ]; [ Some "2"; Some "888"; Some "20" ] ]
                  "NATURAL key ownership stays with the original derived pair"

          testCase "join views update beside derived star projections"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE derived_star_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE derived_star_source(id INT,marker INT)"
                    "INSERT INTO derived_star_target VALUES(1,10)"
                    "INSERT INTO derived_star_source VALUES(1,100)"
                    "CREATE VIEW derived_star_forward AS SELECT a.id,a.n,d.marker FROM derived_star_target a JOIN (SELECT * FROM derived_star_source) d ON d.id=a.id"
                    "CREATE VIEW derived_star_reverse AS SELECT a.id,a.n,d.marker FROM (SELECT * FROM derived_star_source) d JOIN derived_star_target a ON d.id=a.id"
                    "CREATE VIEW derived_star_qualified AS SELECT a.id,a.n,d.marker FROM derived_star_target a JOIN (SELECT b.* FROM derived_star_source b) d ON d.id=a.id" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT table_name,is_updatable FROM information_schema.views WHERE table_name LIKE 'derived_star_%' ORDER BY table_name")
                  [ [ Some "derived_star_forward"; Some "YES" ]; [ Some "derived_star_qualified"; Some "YES" ]; [ Some "derived_star_reverse"; Some "YES" ] ]
                  "star-derived joins are updatable"
              expectOk (run store "UPDATE derived_star_forward SET n=11 WHERE id=1") "update beside unqualified star"
              expectOk (run store "UPDATE derived_star_reverse SET n=12 WHERE id=1") "update with derived source first"
              expectOk (run store "UPDATE derived_star_qualified SET n=13 WHERE id=1") "update beside qualified star"
              Expect.equal (rows store "SELECT n FROM derived_star_target") [ [ Some "13" ] ] "physical updates persist"

          testCase "join view updates beside a grouped derived star"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE grouped_star_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE grouped_star_left(id INT PRIMARY KEY,flag INT)"
                    "CREATE TABLE grouped_star_right(id INT PRIMARY KEY,aux INT)"
                    "INSERT INTO grouped_star_target VALUES(1,0)"
                    "INSERT INTO grouped_star_left VALUES(1,7)"
                    "INSERT INTO grouped_star_right VALUES(1,9)"
                    "CREATE VIEW grouped_star_view AS SELECT a.id AS aid,a.n,d.flag FROM grouped_star_target a JOIN (SELECT * FROM (grouped_star_left b JOIN grouped_star_right c USING(id))) d ON d.id=a.id"
                    "CREATE VIEW grouped_qualified_view AS SELECT a.id AS aid,a.n,d.flag FROM grouped_star_target a JOIN (SELECT b.* FROM (grouped_star_left b JOIN grouped_star_right c USING(id))) d ON d.id=a.id"
                    "CREATE VIEW grouped_natural_view AS SELECT a.id AS aid,a.n,d.flag FROM grouped_star_target a JOIN (SELECT * FROM (grouped_star_left b NATURAL JOIN grouped_star_right c)) d ON d.id=a.id" ] do
                  expectOk (run store sql) sql
              for view in [ "grouped_star_view"; "grouped_qualified_view"; "grouped_natural_view" ] do
                  Expect.equal
                      (rows store ("SELECT is_updatable FROM information_schema.views WHERE table_name='" + view + "'"))
                      [ [ Some "YES" ] ] "the grouped source does not hide the physical write target"
                  expectOk (run store ("UPDATE " + view + " SET n=n+1 WHERE aid=1")) view
              Expect.equal (rows store "SELECT id,n FROM grouped_star_target") [ [ Some "1"; Some "3" ] ] "base row changed"

          testCase "duplicate join partners enforce view write checks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE duplicate_view_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE duplicate_view_source(id INT,flag INT)"
                    "INSERT INTO duplicate_view_target VALUES(1,0),(2,0)"
                    "INSERT INTO duplicate_view_source VALUES(1,7),(1,8)"
                    "CREATE VIEW duplicate_derived_write AS SELECT a.id,a.n,d.flag FROM (SELECT id,flag FROM duplicate_view_source LIMIT 100) d JOIN duplicate_view_target a ON a.id=d.id"
                    "CREATE VIEW duplicate_derived_checked AS SELECT a.id,a.n,d.flag FROM (SELECT id,flag FROM duplicate_view_source LIMIT 100) d JOIN duplicate_view_target a ON a.id=d.id WHERE a.n>=0 WITH CASCADED CHECK OPTION"
                    "CREATE VIEW duplicate_derived_partner AS SELECT a.id,a.n,d.flag FROM (SELECT id,flag FROM duplicate_view_source LIMIT 100) d JOIN duplicate_view_target a ON a.id=d.id WHERE d.flag>a.n WITH CASCADED CHECK OPTION"
                    "CREATE VIEW duplicate_derived_join_check AS SELECT a.id,a.n,d.flag FROM (SELECT id,flag FROM duplicate_view_source LIMIT 100) d JOIN duplicate_view_target a ON a.id=d.id AND d.flag>a.n WITH CASCADED CHECK OPTION" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT table_name,is_updatable FROM information_schema.views WHERE table_name LIKE 'duplicate_derived_%' ORDER BY table_name")
                  [ [ Some "duplicate_derived_checked"; Some "YES" ]; [ Some "duplicate_derived_join_check"; Some "YES" ]; [ Some "duplicate_derived_partner"; Some "YES" ]; [ Some "duplicate_derived_write"; Some "YES" ] ]
                  "a duplicate read-only source retains a writable physical component"
              Expect.equal (run store "UPDATE duplicate_derived_write SET n=n+1 WHERE id=1") (Affected 1UL) "update the target once"
              Expect.equal (rows store "SELECT id,n FROM duplicate_view_target ORDER BY id") [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ] "duplicate matches do not repeat the write"
              match run store "UPDATE duplicate_derived_checked SET n=-1 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected duplicate joined-view check option rejection, got %A" other
              match run store "UPDATE duplicate_derived_partner SET n=7 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected every original join partner to satisfy the checked write, got %A" other
              match run store "UPDATE duplicate_derived_join_check SET n=7 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected a lost original join partner to reject the checked write, got %A" other
              Expect.equal (rows store "SELECT n FROM duplicate_view_target WHERE id=1") [ [ Some "1" ] ] "rejected write leaves the target unchanged"

              expectOk (run store "CREATE TABLE duplicate_insert_target(id INT PRIMARY KEY,n INT)") "create insert target"
              expectOk
                  (run store "CREATE VIEW duplicate_insert_checked AS SELECT a.id,a.n,b.flag FROM duplicate_insert_target a JOIN duplicate_view_source b ON b.id=a.id WHERE b.flag>a.n WITH CASCADED CHECK OPTION")
                  "create checked join insert view"
              match run store "INSERT INTO duplicate_insert_checked(id,n) VALUES(1,7)" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected every inserted join partner to satisfy the check, got %A" other
              Expect.equal (rows store "SELECT id,n FROM duplicate_insert_target") [] "rejected insert leaves no target row"
              Expect.equal (run store "INSERT INTO duplicate_insert_checked(id,n) VALUES(1,6)") (Affected 1UL) "all joined rows satisfy the valid insert"
              Expect.equal (rows store "SELECT id,n FROM duplicate_insert_target") [ [ Some "1"; Some "6" ] ] "valid insert reaches the target"
              expectOk (run store "INSERT INTO duplicate_view_source VALUES(2,7),(2,8)") "seed another join key"
              expectOk
                  (run store "CREATE VIEW duplicate_insert_on_checked AS SELECT a.id,a.n,b.flag FROM duplicate_insert_target a JOIN duplicate_view_source b ON b.id=a.id AND b.flag>a.n WITH CASCADED CHECK OPTION")
                  "create checked join-condition insert view"
              Expect.equal (run store "INSERT INTO duplicate_insert_on_checked(id,n) VALUES(2,7)") (Affected 1UL) "ON removes the failing partner before the check"
              Expect.equal
                  (rows store "SELECT id,n FROM duplicate_insert_target ORDER BY id")
                  [ [ Some "1"; Some "6" ]; [ Some "2"; Some "7" ] ]
                  "both valid insert forms reach the target"

          testCase "join views update a target inside a grouped source"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE grouped_target_a(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE grouped_target_b(id INT PRIMARY KEY,flag INT)"
                    "CREATE TABLE grouped_target_c(id INT PRIMARY KEY,aux INT)"
                    "INSERT INTO grouped_target_a VALUES(1,0)"
                    "INSERT INTO grouped_target_b VALUES(1,7)"
                    "INSERT INTO grouped_target_c VALUES(1,9)"
                    "CREATE VIEW grouped_target AS SELECT a.id AS aid,a.n,b.flag,c.aux FROM (grouped_target_b b JOIN grouped_target_a a ON b.id=a.id) JOIN grouped_target_c c ON c.id=a.id"
                    "CREATE VIEW grouped_derived_target AS SELECT a.id AS aid,a.n,d.flag,c.aux FROM ((SELECT id,flag FROM grouped_target_b) d JOIN grouped_target_a a ON d.id=a.id) JOIN grouped_target_c c ON c.id=a.id"
                    "CREATE VIEW two_derived_first AS SELECT a.id AS aid,a.n,d1.flag,d2.aux FROM (SELECT id,flag FROM grouped_target_b) d1 JOIN (SELECT id,aux FROM grouped_target_c) d2 ON d2.id=d1.id JOIN grouped_target_a a ON a.id=d1.id AND a.id=d2.id"
                    "CREATE VIEW two_aggregate_first AS SELECT a.id AS aid,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) AS flag FROM grouped_target_b GROUP BY id) d1 JOIN (SELECT id,SUM(aux) AS aux FROM grouped_target_c GROUP BY id) d2 ON d2.id=d1.id JOIN grouped_target_a a ON a.id=d1.id AND a.id=d2.id"
                    "CREATE VIEW two_aggregate_filtered AS SELECT a.id AS aid,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) AS flag FROM grouped_target_b GROUP BY id) d1 JOIN (SELECT id,SUM(aux) AS aux FROM grouped_target_c GROUP BY id) d2 ON d2.id=d1.id JOIN grouped_target_a a ON a.id=d1.id AND a.n<d2.aux" ] do
                  expectOk (run store sql) sql

              for view in [ "grouped_target"; "grouped_derived_target"; "two_derived_first"; "two_aggregate_first" ] do
                  Expect.equal
                      (rows store ("SELECT is_updatable FROM information_schema.views WHERE table_name='" + view + "'"))
                      [ [ Some "YES" ] ] (view + " remains updatable")
                  expectOk (run store ("UPDATE " + view + " SET n=n+1 WHERE aid=1")) view

              Expect.equal (rows store "SELECT n FROM grouped_target_a") [ [ Some "4" ] ] "each update reaches the grouped target"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='two_aggregate_filtered'")
                  [ [ Some "YES" ] ] "a moved join predicate can refer to the second derived source"
              expectOk (run store "UPDATE two_aggregate_filtered SET n=n+1 WHERE aid=1") "update through the filtered view"
              Expect.equal (rows store "SELECT n FROM grouped_target_a") [ [ Some "5" ] ] "visible row updated"
              expectOk (run store "UPDATE grouped_target_a SET n=20 WHERE id=1") "move the row outside the join predicate"
              Expect.equal
                  (run store "UPDATE two_aggregate_filtered SET n=n+1 WHERE aid=1")
                  (Affected 0UL) "the moved join predicate still filters the write"
              Expect.equal (rows store "SELECT n FROM grouped_target_a") [ [ Some "20" ] ] "invisible row is unchanged"

              for sql in
                  [ "CREATE SQL SECURITY INVOKER VIEW multiple_derived_invoker AS SELECT a.id AS aid,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) AS flag FROM grouped_target_b GROUP BY id) d1 JOIN (SELECT id,SUM(aux) AS aux FROM grouped_target_c GROUP BY id) d2 ON d2.id=d1.id JOIN grouped_target_a a ON a.id=d1.id AND a.id=d2.id"
                    "CREATE USER grouped_view_reader"
                    "GRANT SELECT,UPDATE ON fsdb.grouped_target_a TO grouped_view_reader"
                    "GRANT SELECT,UPDATE ON fsdb.multiple_derived_invoker TO grouped_view_reader" ] do
                  expectOk (run store sql) sql

              let reader = { Fsdb.Session.create 1 store with User = "grouped_view_reader" }
              let update () =
                  Fsdb.QueryHandler.handle reader "UPDATE multiple_derived_invoker SET n=n+1 WHERE aid=1" |> snd

              match update () with
              | Err(1356, _) -> ()
              | other -> failtestf "missing source grants must invalidate the view, got %A" other

              expectOk (run store "GRANT SELECT ON fsdb.grouped_target_b TO grouped_view_reader") "grant first source read"
              match update () with
              | Err(1356, _) -> ()
              | other -> failtestf "the second missing source grant must still deny the update, got %A" other

              expectOk (run store "GRANT SELECT ON fsdb.grouped_target_c TO grouped_view_reader") "grant second source read"
              Expect.equal (update ()) (Affected 1UL) "both source grants permit the update"
              Expect.equal (rows store "SELECT n FROM grouped_target_a") [ [ Some "21" ] ] "authorized update reaches the base row"

          testCase "multiple derived sources preserve writable targets across STRAIGHT_JOIN edges"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE straight_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE straight_left(id INT PRIMARY KEY,flag INT)"
                    "CREATE TABLE straight_right(id INT PRIMARY KEY,aux INT)"
                    "INSERT INTO straight_target VALUES(1,0)"
                    "INSERT INTO straight_left VALUES(1,7)"
                    "INSERT INTO straight_right VALUES(1,9)"
                    "CREATE VIEW straight_first AS SELECT a.id AS aid,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) AS flag FROM straight_left GROUP BY id) d1 STRAIGHT_JOIN (SELECT id,SUM(aux) AS aux FROM straight_right GROUP BY id) d2 ON d2.id=d1.id JOIN straight_target a ON a.id=d1.id AND a.id=d2.id"
                    "CREATE VIEW straight_target_join AS SELECT a.id AS aid,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) AS flag FROM straight_left GROUP BY id) d1 JOIN (SELECT id,SUM(aux) AS aux FROM straight_right GROUP BY id) d2 ON d2.id=d1.id STRAIGHT_JOIN straight_target a ON a.id=d1.id AND a.n<d2.aux"
                    "CREATE VIEW straight_both AS SELECT a.id AS aid,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) AS flag FROM straight_left GROUP BY id) d1 STRAIGHT_JOIN (SELECT id,SUM(aux) AS aux FROM straight_right GROUP BY id) d2 ON d2.id=d1.id STRAIGHT_JOIN straight_target a ON a.id=d1.id AND a.id=d2.id" ] do
                  expectOk (run store sql) sql

              for view in [ "straight_first"; "straight_target_join"; "straight_both" ] do
                  Expect.equal
                      (rows store ("SELECT is_updatable FROM information_schema.views WHERE table_name='" + view + "'"))
                      [ [ Some "YES" ] ] "MySQL 8.4 marks the target writable"
                  expectOk (run store ("UPDATE " + view + " SET n=n+1 WHERE aid=1")) view
              Expect.equal (rows store "SELECT n FROM straight_target") [ [ Some "3" ] ] "each view updates the target"
              expectOk (run store "UPDATE straight_target SET n=20 WHERE id=1") "exclude the target row"
              Expect.equal
                  (run store "UPDATE straight_target_join SET n=n+1 WHERE aid=1")
                  (Affected 0UL) "the moved target predicate still filters the write"

          testCase "join view updates a physical target beside a dependent lateral source"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE lateral_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE lateral_source(id INT PRIMARY KEY,flag INT)"
                    "INSERT INTO lateral_target VALUES(1,0),(2,0)"
                    "INSERT INTO lateral_source VALUES(1,7)"
                    "CREATE VIEW lateral_write AS SELECT a.id,a.n,d.flag FROM lateral_target a JOIN LATERAL (SELECT flag FROM lateral_source b WHERE b.id=a.id LIMIT 1) d ON TRUE" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='lateral_write'")
                  [ [ Some "YES" ] ] "MySQL 8.4 keeps the physical target writable"
              Expect.equal
                  (run store "UPDATE lateral_write SET n=n+1 WHERE id=1")
                  (Affected 1UL) "the dependent source permits the matched target write"
              Expect.equal
                  (run store "UPDATE lateral_write SET n=n+1 WHERE id=2")
                  (Affected 0UL) "the lateral source still filters unmatched targets"
              Expect.equal
                  (rows store "SELECT id,n FROM lateral_target ORDER BY id")
                  [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ] "only the matched physical row changes"

              for sql in
                  [ "CREATE SQL SECURITY INVOKER VIEW lateral_invoker AS SELECT a.id,a.n,d.flag FROM lateral_target a JOIN LATERAL (SELECT flag FROM lateral_source b WHERE b.id=a.id LIMIT 1) d ON TRUE"
                    "CREATE USER lateral_reader"
                    "GRANT SELECT,UPDATE ON fsdb.lateral_target TO lateral_reader"
                    "GRANT SELECT,UPDATE ON fsdb.lateral_invoker TO lateral_reader" ] do
                  expectOk (run store sql) sql
              let reader = { Fsdb.Session.create 1 store with User = "lateral_reader" }
              let update () = Fsdb.QueryHandler.handle reader "UPDATE lateral_invoker SET n=n+1 WHERE id=1" |> snd
              match update () with
              | Err(1356, _) -> ()
              | other -> failtestf "missing lateral-source grant must invalidate the view, got %A" other
              expectOk (run store "GRANT SELECT ON fsdb.lateral_source TO lateral_reader") "grant lateral read"
              Expect.equal (update ()) (Affected 1UL) "the granted lateral source permits the write"
              Expect.equal (rows store "SELECT n FROM lateral_target WHERE id=1") [ [ Some "2" ] ] "authorized write reaches the target"

          testCase "join view updates a physical target after a dependent lateral source"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE lateral_anchor(id INT PRIMARY KEY)"
                    "CREATE TABLE lateral_filter(id INT PRIMARY KEY,flag INT)"
                    "CREATE TABLE lateral_later_target(id INT PRIMARY KEY,n INT)"
                    "INSERT INTO lateral_anchor VALUES(1),(2)"
                    "INSERT INTO lateral_filter VALUES(1,7)"
                    "INSERT INTO lateral_later_target VALUES(1,0),(2,0)"
                    "CREATE VIEW lateral_before_target AS SELECT t.id,t.n,d.flag FROM lateral_anchor a JOIN LATERAL (SELECT b.id,b.flag FROM lateral_filter b WHERE b.id=a.id LIMIT 1) d ON TRUE JOIN lateral_later_target t ON t.id=d.id" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='lateral_before_target'")
                  [ [ Some "YES" ] ] "MySQL 8.4 marks the later physical target writable"
              Expect.equal
                  (run store "UPDATE lateral_before_target SET n=n+1 WHERE id=1")
                  (Affected 1UL) "the matched dependent row permits the target write"
              Expect.equal
                  (run store "UPDATE lateral_before_target SET n=n+1 WHERE id=2")
                  (Affected 0UL) "the missing dependent row filters the target write"
              Expect.equal
                  (rows store "SELECT id,n FROM lateral_later_target ORDER BY id")
                  [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ] "only the joined target row changes"

              for sql in
                  [ "CREATE TABLE lateral_filter2(id INT PRIMARY KEY,flag INT)"
                    "INSERT INTO lateral_filter2 VALUES(1,8)"
                    "CREATE VIEW lateral_chain_target AS SELECT t.id,t.n,d.flag,e.flag AS second_flag FROM lateral_anchor a JOIN LATERAL (SELECT b.id,b.flag FROM lateral_filter b WHERE b.id=a.id LIMIT 1) d ON TRUE JOIN LATERAL (SELECT c.id,c.flag FROM lateral_filter2 c WHERE c.id=d.id LIMIT 1) e ON TRUE JOIN lateral_later_target t ON t.id=e.id" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='lateral_chain_target'")
                  [ [ Some "YES" ] ] "MySQL 8.4 also marks the dependent chain writable"
              Expect.equal
                  (run store "UPDATE lateral_chain_target SET n=n+1 WHERE id=1")
                  (Affected 1UL) "the second dependent source preserves target identity"
              Expect.equal
                  (run store "UPDATE lateral_chain_target SET n=n+1 WHERE id=2")
                  (Affected 0UL) "the dependent chain still filters unmatched rows"
              Expect.equal
                  (rows store "SELECT id,n FROM lateral_later_target ORDER BY id")
                  [ [ Some "1"; Some "2" ]; [ Some "2"; Some "0" ] ] "only the chain's matched target row changes"

          testCase "join views update a physical target beside JSON_TABLE"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE json_view_target(id INT PRIMARY KEY,n INT)"
                    "INSERT INTO json_view_target VALUES(1,0),(2,0)"
                    "CREATE VIEW json_view_forward AS SELECT a.id,a.n,j.x FROM json_view_target a JOIN JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id"
                    "CREATE VIEW json_view_reverse AS SELECT a.id,a.n,j.x FROM JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j JOIN json_view_target a ON j.x=a.id"
                    "CREATE VIEW json_view_dependent AS SELECT a.id,a.n,j.x FROM json_view_target a JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id"
                    "CREATE VIEW json_view_chain AS SELECT a.id,a.n,j.x,k.y FROM JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j JOIN JSON_TABLE(JSON_ARRAY(j.x),'$[*]' COLUMNS(y INT PATH '$')) k ON k.y=j.x JOIN json_view_target a ON a.id=k.y" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT table_name,is_updatable FROM information_schema.views WHERE table_name LIKE 'json_view_%' ORDER BY table_name")
                  [ [ Some "json_view_chain"; Some "YES" ]
                    [ Some "json_view_dependent"; Some "YES" ]
                    [ Some "json_view_forward"; Some "YES" ]
                    [ Some "json_view_reverse"; Some "YES" ] ]
                  "MySQL 8.4 marks each physical target writable"
              Expect.equal (run store "UPDATE json_view_forward SET n=n+1 WHERE id=1") (Affected 1UL) "forward join updates a matched row"
              Expect.equal (run store "UPDATE json_view_reverse SET n=n+1 WHERE id=2") (Affected 0UL) "reverse join filters an unmatched row"
              Expect.equal (run store "UPDATE json_view_reverse SET n=n+1 WHERE id=1") (Affected 1UL) "reverse join retains target identity"
              Expect.equal (run store "UPDATE json_view_dependent SET n=n+1 WHERE id=2") (Affected 1UL) "dependent JSON_TABLE follows the target row"
              Expect.equal (run store "UPDATE json_view_chain SET n=n+1 WHERE id=1") (Affected 1UL) "dependent sources before the target retain its identity"
              match run store "UPDATE json_view_forward SET x=2 WHERE id=1" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected the JSON_TABLE column to refuse UPDATE, got %A" other
              match run store "INSERT INTO json_view_forward(id,n) VALUES(3,0)" with
              | Err(1471, _) -> ()
              | other -> failtestf "expected the join view to refuse INSERT, got %A" other
              Expect.equal
                  (rows store "SELECT id,n FROM json_view_target ORDER BY id")
                  [ [ Some "1"; Some "3" ]; [ Some "2"; Some "1" ] ]
                  "only matched physical rows change"

          testCase "aggregate-derived JSON_TABLE chain keeps a later view target writable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE derived_json_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE derived_json_source(id INT PRIMARY KEY,flag INT)"
                    "INSERT INTO derived_json_target VALUES(1,0),(2,0)"
                    "INSERT INTO derived_json_source VALUES(1,7)"
                    "CREATE VIEW derived_json_write AS SELECT a.id,a.n,d.flag,j.x FROM (SELECT id,SUM(flag) flag FROM derived_json_source GROUP BY id) d JOIN JSON_TABLE(JSON_ARRAY(d.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=d.id JOIN derived_json_target a ON a.id=j.x" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='derived_json_write'")
                  [ [ Some "YES" ] ] "MySQL 8.4 marks the later physical target writable"
              Expect.equal (run store "UPDATE derived_json_write SET n=n+1 WHERE id=1") (Affected 1UL) "the matched row changes"
              Expect.equal (run store "UPDATE derived_json_write SET n=n+1 WHERE id=2") (Affected 0UL) "the missing aggregate row filters the target"
              Expect.equal
                  (rows store "SELECT id,n FROM derived_json_target ORDER BY id")
                  [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ] "only the matched physical row changes"
              expectOk
                  (run store "CREATE VIEW derived_json_checked AS SELECT a.id,a.n,d.flag,j.x FROM (SELECT id,SUM(flag) flag FROM derived_json_source GROUP BY id) d JOIN JSON_TABLE(JSON_ARRAY(d.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=d.id JOIN derived_json_target a ON a.id=j.x WHERE a.n >= 0 WITH CASCADED CHECK OPTION")
                  "create checked composed view"
              match run store "UPDATE derived_json_checked SET n=-1 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected the checked view to reject an invalid target row, got %A" other
              Expect.equal (rows store "SELECT n FROM derived_json_target WHERE id=1") [ [ Some "1" ] ] "rejected write leaves the target unchanged"
              match run store "INSERT INTO derived_json_write(id,n) VALUES(3,0)" with
              | Err(1471, _) -> ()
              | other -> failtestf "expected joined-view INSERT to fail, got %A" other
              match run store "DELETE FROM derived_json_write WHERE id=1" with
              | Err(1395, _) -> ()
              | other -> failtestf "expected joined-view DELETE to fail, got %A" other

              expectOk
                  (run store "CREATE VIEW grouped_dependent_json_write AS SELECT a.id,a.n,d.flag,j.x FROM ((SELECT id,SUM(flag) flag FROM derived_json_source GROUP BY id) d JOIN JSON_TABLE(JSON_ARRAY(d.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=d.id) JOIN derived_json_target a ON a.id=j.x")
                  "create grouped dependent source view"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='grouped_dependent_json_write'")
                  [ [ Some "YES" ] ] "the grouped dependency chain leaves the physical target writable"
              Expect.equal
                  (run store "UPDATE grouped_dependent_json_write SET n=n+1 WHERE id=1")
                  (Affected 1UL) "the grouped dependency chain reaches the matched target"
              Expect.equal
                  (rows store "SELECT id,n FROM derived_json_target ORDER BY id")
                  [ [ Some "1"; Some "2" ]; [ Some "2"; Some "0" ] ] "the unmatched target remains unchanged"
              expectOk
                  (run store "CREATE VIEW grouped_dependent_json_checked AS SELECT a.id,a.n,d.flag,j.x FROM ((SELECT id,SUM(flag) flag FROM derived_json_source GROUP BY id) d JOIN JSON_TABLE(JSON_ARRAY(d.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=d.id) JOIN derived_json_target a ON a.id=j.x WHERE a.n >= 0 WITH CASCADED CHECK OPTION")
                  "create checked grouped dependency view"
              match run store "UPDATE grouped_dependent_json_checked SET n=-1 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected grouped dependency check option to reject the write, got %A" other
              match run store "INSERT INTO grouped_dependent_json_write(id,n) VALUES(3,0)" with
              | Err(1471, _) -> ()
              | other -> failtestf "expected grouped joined-view INSERT refusal, got %A" other
              match run store "DELETE FROM grouped_dependent_json_write WHERE id=1" with
              | Err(1395, _) -> ()
              | other -> failtestf "expected grouped joined-view DELETE refusal, got %A" other
              Expect.equal
                  (rows store "SELECT id,n FROM derived_json_target ORDER BY id")
                  [ [ Some "1"; Some "2" ]; [ Some "2"; Some "0" ] ] "rejected grouped writes leave the target unchanged"

          testCase "aggregate-derived lateral chain keeps a later view target writable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE lateral_aggregate_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE lateral_aggregate_source(id INT,flag INT)"
                    "INSERT INTO lateral_aggregate_target VALUES(1,0),(2,0)"
                    "INSERT INTO lateral_aggregate_source VALUES(1,7),(1,8)"
                    "CREATE VIEW lateral_aggregate_write AS SELECT a.id,a.n,d.flag,e.extra FROM (SELECT id,SUM(flag) flag FROM lateral_aggregate_source GROUP BY id) d JOIN LATERAL (SELECT d.flag+1 extra) e ON TRUE JOIN lateral_aggregate_target a ON a.id=d.id" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='lateral_aggregate_write'")
                  [ [ Some "YES" ] ] "MySQL 8.4 marks the later physical target writable"
              Expect.equal (run store "UPDATE lateral_aggregate_write SET n=n+1 WHERE id=1") (Affected 1UL) "the matched row changes"
              Expect.equal (run store "UPDATE lateral_aggregate_write SET n=n+1 WHERE id=2") (Affected 0UL) "the missing aggregate row filters the write"
              Expect.equal
                  (rows store "SELECT id,n FROM lateral_aggregate_target ORDER BY id")
                  [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ] "the unmatched target remains unchanged"
              match run store "INSERT INTO lateral_aggregate_write(id,n) VALUES(3,0)" with
              | Err(1471, _) -> ()
              | other -> failtestf "expected MySQL's non-insertable join-view error, got %A" other
              match run store "DELETE FROM lateral_aggregate_write WHERE id=1" with
              | Err(1395, _) -> ()
              | other -> failtestf "expected MySQL's joined-view DELETE error, got %A" other
              expectOk
                  (run store "CREATE VIEW lateral_aggregate_checked AS SELECT a.id,a.n,d.flag,e.extra FROM (SELECT id,SUM(flag) flag FROM lateral_aggregate_source GROUP BY id) d JOIN LATERAL (SELECT d.flag+1 extra) e ON TRUE JOIN lateral_aggregate_target a ON a.id=d.id WHERE a.n>=0 WITH CASCADED CHECK OPTION")
                  "create checked lateral chain"
              match run store "UPDATE lateral_aggregate_checked SET n=-1 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected CHECK OPTION rejection, got %A" other
              Expect.equal (rows store "SELECT n FROM lateral_aggregate_target WHERE id=1") [ [ Some "1" ] ] "rejected update leaves target unchanged"

              expectOk
                  (run store "CREATE VIEW lateral_aggregate_chain AS SELECT a.id,a.n,d.flag,e.extra,f.next_extra FROM (SELECT id,SUM(flag) flag FROM lateral_aggregate_source GROUP BY id) d JOIN LATERAL (SELECT d.flag+1 extra) e ON TRUE JOIN LATERAL (SELECT e.extra+1 next_extra) f ON TRUE JOIN lateral_aggregate_target a ON a.id=d.id")
                  "create two-step lateral chain"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='lateral_aggregate_chain'")
                  [ [ Some "YES" ] ] "the second dependent source preserves writable metadata"
              Expect.equal (run store "UPDATE lateral_aggregate_chain SET n=n+1 WHERE id=1") (Affected 1UL) "the second dependent source reaches the target"
              Expect.equal (rows store "SELECT n FROM lateral_aggregate_target WHERE id=1") [ [ Some "2" ] ] "the later target changes once"

          testCase "aggregate lateral and JSON chains keep a later view target writable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE composed_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE composed_source(id INT,flag INT)"
                    "INSERT INTO composed_target VALUES(1,0),(2,0)"
                    "INSERT INTO composed_source VALUES(1,7),(1,8)" ] do
                  expectOk (run store sql) sql
              let shapes =
                  [ "SELECT a.id,a.n,d.flag,e.x,j.y FROM (SELECT id,SUM(flag) flag FROM composed_source GROUP BY id) d JOIN LATERAL (SELECT d.id x) e ON TRUE JOIN JSON_TABLE(JSON_ARRAY(e.x),'$[*]' COLUMNS(y INT PATH '$')) j ON j.y=e.x JOIN composed_target a ON a.id=j.y"
                    "SELECT a.id,a.n,d.flag,j.x,e.y FROM (SELECT id,SUM(flag) flag FROM composed_source GROUP BY id) d JOIN JSON_TABLE(JSON_ARRAY(d.id),'$[*]' COLUMNS(x INT PATH '$')) j ON j.x=d.id JOIN LATERAL (SELECT j.x y) e ON TRUE JOIN composed_target a ON a.id=e.y"
                    "SELECT a.id,a.n,d.flag,e.x,j.y FROM ((SELECT id,SUM(flag) flag FROM composed_source GROUP BY id) d JOIN LATERAL (SELECT d.id x) e ON TRUE JOIN JSON_TABLE(JSON_ARRAY(e.x),'$[*]' COLUMNS(y INT PATH '$')) j ON j.y=e.x) JOIN composed_target a ON a.id=j.y" ]
              for definition in shapes do
                  expectOk (run store "UPDATE composed_target SET n=0") "reset target"
                  expectOk (run store ("CREATE VIEW composed_write AS " + definition)) "create composed view"
                  Expect.equal
                      (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='composed_write'")
                      [ [ Some "YES" ] ] "MySQL marks the target writable"
                  Expect.equal (run store "UPDATE composed_write SET n=n+1 WHERE id=1") (Affected 1UL) "matched target changes once"
                  Expect.equal (run store "UPDATE composed_write SET n=n+1 WHERE id=2") (Affected 0UL) "unmatched target stays unchanged"
                  Expect.equal
                      (rows store "SELECT id,n FROM composed_target ORDER BY id")
                      [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ] "only the matched target changes"
                  match run store "INSERT INTO composed_write(id,n) VALUES(3,0)" with
                  | Err(1471, _) -> ()
                  | other -> failtestf "expected joined-view INSERT refusal, got %A" other
                  match run store "DELETE FROM composed_write WHERE id=1" with
                  | Err(1395, _) -> ()
                  | other -> failtestf "expected joined-view DELETE refusal, got %A" other
                  expectOk (run store "DROP VIEW composed_write") "drop composed view"

                  expectOk
                      (run store ("CREATE VIEW composed_checked AS " + definition + " WHERE a.n>=0 WITH CASCADED CHECK OPTION"))
                      "create checked composed view"
                  match run store "UPDATE composed_checked SET n=-1 WHERE id=1" with
                  | Err(1369, _) -> ()
                  | other -> failtestf "expected CHECK OPTION rejection, got %A" other
                  Expect.equal (rows store "SELECT n FROM composed_target WHERE id=1") [ [ Some "1" ] ] "rejected update leaves target unchanged"
                  expectOk (run store "DROP VIEW composed_checked") "drop checked composed view"

          testCase "layered lateral and aggregate view checks retain the renamed target"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE layered_lateral_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE layered_lateral_source(id INT,flag INT)"
                    "CREATE TABLE layered_lateral_aux(id INT PRIMARY KEY,aux INT)"
                    "INSERT INTO layered_lateral_target VALUES(1,0),(2,0)"
                    "INSERT INTO layered_lateral_source VALUES(1,7),(1,8)"
                    "INSERT INTO layered_lateral_aux VALUES(1,9)"
                    "CREATE VIEW lateral_checked_inner AS SELECT a.id AS target_id,a.n AS metric,d.flag,e.x FROM (SELECT id,SUM(flag) flag FROM layered_lateral_source GROUP BY id) d JOIN LATERAL (SELECT d.id x) e ON TRUE JOIN layered_lateral_target a ON a.id=e.x WHERE a.n>=0 WITH CASCADED CHECK OPTION"
                    "CREATE VIEW lateral_checked_outer AS SELECT iv.target_id AS id,iv.metric AS n,s.aux FROM lateral_checked_inner iv JOIN (SELECT id,SUM(aux) aux FROM layered_lateral_aux GROUP BY id) s ON s.id=iv.target_id WHERE iv.metric<s.aux WITH CASCADED CHECK OPTION" ] do
                  expectOk (run store sql) sql
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='lateral_checked_outer'")
                  [ [ Some "YES" ] ]
                  "the layered composed view remains updatable"
              Expect.equal (run store "UPDATE lateral_checked_outer SET n=1 WHERE id=1") (Affected 1UL) "renamed target updates"
              for value in [ -1; 10 ] do
                  match run store (sprintf "UPDATE lateral_checked_outer SET n=%d WHERE id=1" value) with
                  | Err(1369, _) -> ()
                  | other -> failtestf "expected checked update %d to fail with 1369, got %A" value other
              Expect.equal
                  (rows store "SELECT id,n FROM layered_lateral_target ORDER BY id")
                  [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ]
                  "both rejected writes leave the target unchanged"

          testCase "independent lateral source before a view target remains writable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE first_lateral_target(id INT PRIMARY KEY,n INT)") "create target"
              expectOk (run store "INSERT INTO first_lateral_target VALUES(1,0),(2,0)") "seed target"
              expectOk
                  (run store "CREATE VIEW first_lateral_write AS SELECT a.id,a.n,e.x FROM LATERAL (SELECT 1 x) e JOIN first_lateral_target a ON a.id=e.x")
                  "create lateral-first view"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='first_lateral_write'")
                  [ [ Some "YES" ] ] "MySQL marks the physical target writable"
              Expect.equal (run store "UPDATE first_lateral_write SET n=n+1 WHERE id=1") (Affected 1UL) "matched target changes"
              Expect.equal (run store "UPDATE first_lateral_write SET n=n+1 WHERE id=2") (Affected 0UL) "unmatched target stays unchanged"
              Expect.equal
                  (rows store "SELECT id,n FROM first_lateral_target ORDER BY id")
                  [ [ Some "1"; Some "1" ]; [ Some "2"; Some "0" ] ] "only matched row changes"
              match run store "INSERT INTO first_lateral_write(id,n) VALUES(3,0)" with
              | Err(1471, _) -> ()
              | other -> failtestf "expected joined-view INSERT refusal, got %A" other
              match run store "DELETE FROM first_lateral_write WHERE id=1" with
              | Err(1395, _) -> ()
              | other -> failtestf "expected joined-view DELETE refusal, got %A" other
              expectOk
                  (run store "CREATE VIEW first_lateral_checked AS SELECT a.id,a.n,e.x FROM LATERAL (SELECT 1 x) e JOIN first_lateral_target a ON a.id=e.x WHERE a.n>=0 WITH CASCADED CHECK OPTION")
                  "create checked lateral-first view"
              match run store "UPDATE first_lateral_checked SET n=-1 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected CHECK OPTION rejection, got %A" other
              Expect.equal (rows store "SELECT n FROM first_lateral_target WHERE id=1") [ [ Some "1" ] ] "rejected write leaves target unchanged"

              expectOk
                  (run store "CREATE VIEW first_lateral_json AS SELECT a.id,a.n,e.x,j.y FROM LATERAL (SELECT 1 x) e JOIN JSON_TABLE(JSON_ARRAY(e.x),'$[*]' COLUMNS(y INT PATH '$')) j ON j.y=e.x JOIN first_lateral_target a ON a.id=j.y")
                  "create dependent JSON chain"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='first_lateral_json'")
                  [ [ Some "YES" ] ] "a second read-only source preserves writable metadata"
              Expect.equal (run store "UPDATE first_lateral_json SET n=n+1 WHERE id=1") (Affected 1UL) "the later target changes once"
              Expect.equal (rows store "SELECT n FROM first_lateral_target WHERE id=1") [ [ Some "2" ] ] "the chain reaches the physical target"

          testCase "checked merged joins keep a physical view target writable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              for sql in
                  [ "CREATE TABLE merged_target(id INT PRIMARY KEY,n INT)"
                    "CREATE TABLE merged_left(id INT PRIMARY KEY,flag INT)"
                    "CREATE TABLE merged_right(id INT PRIMARY KEY,aux INT)"
                    "INSERT INTO merged_target VALUES(1,0),(2,0)"
                    "INSERT INTO merged_left VALUES(1,7)"
                    "INSERT INTO merged_right VALUES(1,9)"
                    "CREATE VIEW merged_aggregate AS SELECT a.id,a.n,d1.flag,d2.aux FROM (SELECT id,SUM(flag) flag FROM merged_left GROUP BY id) d1 NATURAL JOIN merged_target a NATURAL JOIN (SELECT id,SUM(aux) aux FROM merged_right GROUP BY id) d2 WHERE a.n >= 0 WITH CASCADED CHECK OPTION"
                    "CREATE VIEW merged_json AS SELECT a.id,a.n,j.id AS jid FROM JSON_TABLE('[1]','$[*]' COLUMNS(id INT PATH '$')) j JOIN merged_target a USING(id) WHERE a.n >= 0 WITH CASCADED CHECK OPTION" ] do
                  expectOk (run store sql) sql

              Expect.equal
                  (rows store "SELECT table_name,is_updatable FROM information_schema.views WHERE table_name IN ('merged_aggregate','merged_json') ORDER BY table_name")
                  [ [ Some "merged_aggregate"; Some "YES" ]; [ Some "merged_json"; Some "YES" ] ]
                  "MySQL 8.4 marks both merged-column joins updatable"

              for view in [ "merged_aggregate"; "merged_json" ] do
                  Expect.equal (run store $"UPDATE {view} SET n=n+1 WHERE id=1") (Affected 1UL) "the matched target row changes"
                  match run store $"UPDATE {view} SET n=-1 WHERE id=1" with
                  | Err(1369, _) -> ()
                  | other -> failtestf "expected the checked %s view to reject an invalid row, got %A" view other
                  match run store $"INSERT INTO {view}(id,n) VALUES(3,0)" with
                  | Err(1471, _) -> ()
                  | other -> failtestf "expected the %s view to refuse INSERT, got %A" view other
                  match run store $"DELETE FROM {view} WHERE id=1" with
                  | Err(1395, _) -> ()
                  | other -> failtestf "expected the %s view to refuse DELETE, got %A" view other

              Expect.equal
                  (rows store "SELECT id,n FROM merged_target ORDER BY id")
                  [ [ Some "1"; Some "2" ]; [ Some "2"; Some "0" ] ]
                  "failed writes leave the physical target unchanged"

          testCase "join views update mergeable components beside union views"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE union_view_values (id INT, marker INT)") "create union values"
              expectOk (run store "INSERT INTO union_view_values VALUES (1, 10), (2, 20)") "seed union values"
              expectOk (run store "CREATE TABLE union_view_targets (id INT PRIMARY KEY, n INT NOT NULL)") "create union targets"
              expectOk (run store "INSERT INTO union_view_targets VALUES (1, 1), (2, 2)") "seed union targets"

              expectOk
                  (run store "CREATE VIEW union_markers AS SELECT id, marker FROM union_view_values WHERE id = 1 UNION ALL SELECT id, marker FROM union_view_values WHERE id = 2")
                  "create union component"

              expectOk
                  (run store "CREATE VIEW targets_with_union AS SELECT t.id, t.n, u.marker FROM union_view_targets t JOIN union_markers u ON u.id = t.id")
                  "create union join"

              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name = 'targets_with_union'")
                  [ [ Some "YES" ] ]
                  "a mergeable component makes the union join updatable"

              expectOk (run store "UPDATE targets_with_union SET n = 11 WHERE id = 1 AND marker = 10") "update beside union component"

              match run store "UPDATE targets_with_union SET marker = 99 WHERE id = 1" with
              | Err(1348, "Column 'marker' is not updatable") -> ()
              | other -> failtestf "expected union-column refusal, got %A" other

              match run store "INSERT INTO targets_with_union(id, n) VALUES (3, 3)" with
              | Err(1471, "The target table targets_with_union of the INSERT is not insertable-into") -> ()
              | other -> failtestf "expected union join insert refusal, got %A" other

              Expect.equal
                  (rows store "SELECT id, n FROM union_view_targets ORDER BY id")
                  [ [ Some "1"; Some "11" ]; [ Some "2"; Some "2" ] ]
                  "the mergeable component stores the union-selected update"

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

              match run store "SELECT id, (SELECT visible_rows.hidden) FROM visible_rows" with
              | Err(1054, _) -> ()
              | other -> failtestf "expected correlated hidden-column rejection, got %A" other

          testCase "ordered views guide limited writes and inherit through nesting"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE order_rows (id INT PRIMARY KEY, n INT NOT NULL, note VARCHAR(20))") "create rows"
              expectOk (run store "INSERT INTO order_rows VALUES (1, 10, 'a'), (2, 30, 'b'), (3, 20, 'c')") "seed rows"

              expectOk
                  (run store "CREATE VIEW ordered_rows AS SELECT id, n, note, n * 2 AS doubled FROM order_rows ORDER BY doubled DESC")
                  "create ordered view"

              expectOk
                  (run store "CREATE VIEW inherited_order AS SELECT id, n, note FROM ordered_rows")
                  "create inheriting view"

              expectOk
                  (run store "CREATE VIEW insertable_order AS SELECT id, n, note FROM order_rows ORDER BY n DESC")
                  "create insertable ordered view"

              Expect.equal
                  (rows store "SELECT table_name, is_updatable FROM information_schema.views WHERE table_name IN ('inherited_order', 'ordered_rows') ORDER BY table_name")
                  [ [ Some "inherited_order"; Some "YES" ]; [ Some "ordered_rows"; Some "YES" ] ]
                  "ordering does not make a view read-only"

              Expect.equal
                  (rows store "SELECT id FROM inherited_order")
                  [ [ Some "2" ]; [ Some "3" ]; [ Some "1" ] ]
                  "the nested view inherits its default read order"

              expectOk (run store "UPDATE ordered_rows SET note = 'view-order' LIMIT 1") "use the view order"
              expectOk (run store "UPDATE ordered_rows SET note = 'outer-order' ORDER BY id ASC LIMIT 1") "override the view order"

              expectOk
                  (run store "UPDATE inherited_order SET note = 'nested-order' WHERE id <> 2 LIMIT 1")
                  "inherit the inner order"

              Expect.equal
                  (rows store "SELECT id, note FROM order_rows ORDER BY id")
                  [ [ Some "1"; Some "outer-order" ]
                    [ Some "2"; Some "view-order" ]
                    [ Some "3"; Some "nested-order" ] ]
                  "limited updates choose the same rows as MySQL"

              expectOk (run store "INSERT INTO insertable_order VALUES (4, 5, 'inserted')") "insert through ordered view"

              expectOk (run store "DELETE FROM inherited_order WHERE id <> 2 LIMIT 1") "use inherited order for delete"

              Expect.equal
                  (rows store "SELECT id FROM order_rows ORDER BY id")
                  [ [ Some "1" ]; [ Some "2" ]; [ Some "4" ] ]
                  "limited delete chooses the highest remaining row"

          testCase "a grouped view rejects UPDATE"
          <| fun _ ->
              let store = setup ()
              expectOk (run store "CREATE VIEW totals AS SELECT vendor_id, SUM(total) AS total FROM receipts GROUP BY vendor_id") "create view"

              match run store "UPDATE totals SET total = 0" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected non-updatable view error, got %A" other

          testCase "joined views update a physical target after a read-only CTE"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE cte_target(id INT PRIMARY KEY,n INT)") "create target"
              expectOk (run store "CREATE TABLE cte_source(id INT,flag INT)") "create source"
              expectOk (run store "INSERT INTO cte_target VALUES(1,0),(2,0)") "seed target"
              expectOk (run store "INSERT INTO cte_source VALUES(1,7),(1,8),(2,9)") "seed source"

              for name, body in
                  [ "aggregate", "SELECT id,SUM(flag) flag FROM cte_source GROUP BY id"
                    "simple", "SELECT id,flag FROM cte_source"
                    "limited", "SELECT id,flag FROM cte_source LIMIT 100" ] do
                  let view = "cte_" + name
                  let sql = $"CREATE VIEW {view} AS WITH d AS ({body}) SELECT a.id,a.n,d.flag FROM d JOIN cte_target a ON a.id=d.id"
                  expectOk (run store sql) ("create " + view)
                  Expect.equal
                      (rows store ($"SELECT is_updatable FROM information_schema.views WHERE table_name='{view}'"))
                      [ [ Some "YES" ] ]
                      (view + " is updatable in MySQL 8.4")
                  expectOk (run store ($"UPDATE {view} SET n=n+1 WHERE id=1")) ("update " + view)

                  let checkedView = view + "_checked"
                  let checkedSql =
                      $"CREATE VIEW {checkedView} AS WITH d AS ({body}) SELECT a.id,a.n,d.flag FROM d JOIN cte_target a ON a.id=d.id WHERE a.n>=0 WITH CASCADED CHECK OPTION"
                  expectOk (run store checkedSql) ("create " + checkedView)
                  match run store ($"UPDATE {checkedView} SET n=-1 WHERE id=1") with
                  | Err(1369, _) -> ()
                  | other -> failtestf "expected MySQL CHECK OPTION rejection for %s, got %A" checkedView other
                  match run store ($"INSERT INTO {checkedView}(id,n) VALUES(3,0)") with
                  | Err(1471, _) -> ()
                  | other -> failtestf "expected MySQL non-insertable join view for %s, got %A" checkedView other

              Expect.equal
                  (rows store "SELECT id,n FROM cte_target ORDER BY id")
                  [ [ Some "1"; Some "3" ]; [ Some "2"; Some "0" ] ]
                  "each CTE-backed view updates the target once"

              expectOk (run store "CREATE TABLE cte_aux(id INT,aux INT)") "create second source"
              expectOk (run store "INSERT INTO cte_aux VALUES(1,9)") "seed second source"
              expectOk
                  (run store
                      "CREATE VIEW cte_pair AS WITH d1 AS (SELECT id,SUM(flag) flag FROM cte_source GROUP BY id), d2 AS (SELECT id,SUM(aux) aux FROM cte_aux GROUP BY id) SELECT a.id,a.n,d1.flag,d2.aux FROM d1 JOIN d2 ON d2.id=d1.id JOIN cte_target a ON a.id=d1.id")
                  "create two-source CTE view"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='cte_pair'")
                  [ [ Some "YES" ] ]
                  "two read-only CTE sources leave the target writable"
              expectOk (run store "UPDATE cte_pair SET n=n+1 WHERE id=1") "update through both CTE sources"
              Expect.equal
                  (rows store "SELECT id,n FROM cte_target ORDER BY id")
                  [ [ Some "1"; Some "4" ]; [ Some "2"; Some "0" ] ]
                  "the physical target is updated once after both CTE joins"

              for name, cte in
                  [ "renamed", "d(k,f) AS (SELECT id,flag FROM cte_source)"
                    "joined_body", "d AS (SELECT s.id,s.flag FROM cte_source s JOIN cte_aux x ON x.id=s.id)"
                    "joined_renamed", "d(k,f) AS (SELECT s.id,s.flag FROM cte_source s JOIN cte_aux x ON x.id=s.id)" ] do
                  let view = "cte_" + name
                  let key = if name = "joined_body" then "id" else "k"
                  let flag = if name = "joined_body" then "flag" else "f"
                  let sql = $"CREATE VIEW {view} AS WITH {cte} SELECT a.id,a.n,d.{flag} FROM d JOIN cte_target a ON a.id=d.{key}"
                  expectOk (run store sql) ("create " + view)
                  Expect.equal
                      (rows store ($"SELECT is_updatable FROM information_schema.views WHERE table_name='{view}'"))
                      [ [ Some "YES" ] ]
                      (view + " is updatable in MySQL 8.4")
                  expectOk (run store ($"UPDATE {view} SET n=n+1 WHERE id=1")) ("update " + view)

              Expect.equal
                  (rows store "SELECT id,n FROM cte_target ORDER BY id")
                  [ [ Some "1"; Some "7" ]; [ Some "2"; Some "0" ] ]
                  "renamed and joined CTE bodies update the target once"

          testCase "recursive CTE join views update only their physical target"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE recursive_target(id INT PRIMARY KEY,n INT)") "create target"
              expectOk (run store "INSERT INTO recursive_target VALUES(1,10),(2,20),(3,30),(4,40)") "seed target"

              for name, cte, source in
                  [ "first", "seq(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3)",
                    "seq JOIN recursive_target t ON t.id=seq.k"
                    "last", "seq(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3)",
                    "recursive_target t JOIN seq ON t.id=seq.k"
                    "group", "seq(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3)",
                    "(seq JOIN recursive_target t ON t.id=seq.k)"
                    "inferred", "seq AS (SELECT 1 AS k UNION ALL SELECT k+1 FROM seq WHERE k<3)",
                    "seq JOIN recursive_target t ON t.id=seq.k" ] do
                  let view = "recursive_" + name
                  expectOk
                      (run store ($"CREATE VIEW {view} AS WITH RECURSIVE {cte} SELECT t.id,t.n FROM {source}"))
                      ("create " + view)
                  Expect.equal
                      (rows store ($"SELECT is_updatable FROM information_schema.views WHERE table_name='{view}'"))
                      [ [ Some "YES" ] ]
                      (view + " metadata")
                  expectOk (run store ($"UPDATE {view} SET n=n+1 WHERE id=2")) ("update " + view)

              Expect.equal
                  (rows store "SELECT id,n FROM recursive_target ORDER BY id")
                  [ [ Some "1"; Some "10" ]; [ Some "2"; Some "24" ]; [ Some "3"; Some "30" ]; [ Some "4"; Some "40" ] ]
                  "each recursive read source leaves one physical row update"

              let recursive =
                  "WITH RECURSIVE seq(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3)"
              for name, body in
                  [ "dependent",
                    recursive + ", doubled AS (SELECT k,k*2 AS v FROM seq) "
                    + "SELECT t.id,t.n FROM doubled d JOIN recursive_target t ON t.id=d.k"
                    "grouped",
                    recursive + ", grouped AS (SELECT k,COUNT(*) AS c FROM seq GROUP BY k) "
                    + "SELECT t.id,t.n FROM grouped g JOIN recursive_target t ON t.id=g.k"
                    "repeated",
                    recursive + " SELECT t.id,t.n FROM seq a JOIN seq b ON b.k=a.k "
                    + "JOIN recursive_target t ON t.id=a.k"
                    "star",
                    recursive + ", copied AS (SELECT * FROM seq) "
                    + "SELECT t.id,t.n FROM copied c JOIN recursive_target t ON t.id=c.k"
                    "nested_star",
                    recursive + ", copied AS (SELECT k FROM seq), again AS (SELECT * FROM copied) "
                    + "SELECT t.id,t.n FROM again a JOIN recursive_target t ON t.id=a.k"
                    "nested_derived",
                    recursive + ", copied AS (SELECT d.k FROM (SELECT k FROM seq) d) "
                    + "SELECT t.id,t.n FROM copied c JOIN recursive_target t ON t.id=c.k"
                    "nested_join",
                    recursive + ", copied AS (SELECT s.k FROM seq s JOIN (SELECT 2 AS anchor) d ON d.anchor=s.k) "
                    + "SELECT t.id,t.n FROM copied c JOIN recursive_target t ON t.id=c.k"
                    "lateral_after_seq",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT s.k AS lookup_key) l ON TRUE JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_nested_after_seq",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT s.k AS lookup_key FROM (SELECT 1 AS marker) d) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_cte_reread",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT k AS lookup_key FROM seq WHERE k=s.k) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_volatile_reread",
                    "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                    + "SELECT k+1,r FROM seq WHERE k<3) "
                    + "SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT k AS lookup_key FROM seq WHERE k=s.k AND r=s.r) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_nested_cte_reread",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT d.k AS lookup_key FROM (SELECT k FROM seq) d WHERE d.k=s.k) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_nested_volatile_reread",
                    "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                    + "SELECT k+1,r FROM seq WHERE k<3) "
                    + "SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT d.k AS lookup_key FROM (SELECT k,r FROM seq) d "
                    + "WHERE d.k=s.k AND d.r=s.r) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_independent_scalar",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT (SELECT s.k) AS lookup_key) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_independent_exists",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT s.k AS lookup_key WHERE EXISTS (SELECT 1 WHERE s.k>0)) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_scalar_cte_reread",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT (SELECT k FROM seq WHERE k=s.k) AS lookup_key) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_exists_cte_reread",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT s.k AS lookup_key WHERE EXISTS "
                    + "(SELECT 1 FROM seq WHERE k=s.k)) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_volatile_scalar_cte_reread",
                    "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                    + "SELECT k+1,r FROM seq WHERE k<3) "
                    + "SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT (SELECT k FROM seq WHERE k=s.k AND r=s.r) AS lookup_key) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_in_cte_reread",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT s.k AS lookup_key WHERE s.k IN "
                    + "(SELECT k FROM seq WHERE k=s.k)) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_any_cte_reread",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(SELECT s.k AS lookup_key WHERE s.k = ANY "
                    + "(SELECT k FROM seq WHERE k=s.k)) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_local_cte_reread",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH local_cte AS (SELECT k FROM seq WHERE k=s.k) "
                    + "SELECT k AS lookup_key FROM local_cte) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_local_cte_volatile_reread",
                    "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                    + "SELECT k+1,r FROM seq WHERE k<3) "
                    + "SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH local_cte AS (SELECT k FROM seq WHERE k=s.k AND r=s.r) "
                    + "SELECT k AS lookup_key FROM local_cte) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_two_local_ctes",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH local_cte AS (SELECT k FROM seq WHERE k=s.k), "
                    + "again AS (SELECT * FROM local_cte) "
                    + "SELECT k AS lookup_key FROM again) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_local_cte_scalar_reread",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH local_cte AS (SELECT (SELECT k FROM seq WHERE k=s.k) AS lookup_key) "
                    + "SELECT lookup_key FROM local_cte) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_local_cte_volatile_scalar_reread",
                    "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                    + "SELECT k+1,r FROM seq WHERE k<3) "
                    + "SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH local_cte AS (SELECT (SELECT k FROM seq WHERE k=s.k AND r=s.r) "
                    + "AS lookup_key) SELECT lookup_key FROM local_cte) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_shadowed_local_cte",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH seq AS (SELECT s.k AS k) SELECT k AS lookup_key FROM seq) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_late_shadowed_local_cte",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH copied AS (SELECT k FROM seq WHERE k=s.k), "
                    + "seq AS (SELECT s.k AS k) "
                    + "SELECT copied.k AS lookup_key FROM copied JOIN seq ON seq.k=copied.k) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_late_shadowed_volatile_cte",
                    "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                    + "SELECT k+1,r FROM seq WHERE k<3) "
                    + "SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH copied AS (SELECT k FROM seq WHERE k=s.k AND r=s.r), "
                    + "seq AS (SELECT s.k AS k) "
                    + "SELECT copied.k AS lookup_key FROM copied JOIN seq ON seq.k=copied.k) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_local_union_cte",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH copied AS (SELECT k FROM seq WHERE k=s.k "
                    + "UNION ALL SELECT k FROM seq WHERE k=s.k AND FALSE) "
                    + "SELECT k AS lookup_key FROM copied) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_volatile_union_cte",
                    "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                    + "SELECT k+1,r FROM seq WHERE k<3) "
                    + "SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH copied AS (SELECT k FROM seq WHERE k=s.k AND r=s.r "
                    + "UNION SELECT k FROM seq WHERE k=s.k AND r=s.r) "
                    + "SELECT k AS lookup_key FROM copied) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_ordered_union_cte",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH copied AS (SELECT k FROM seq WHERE k=s.k "
                    + "UNION ALL SELECT k FROM seq WHERE k=s.k ORDER BY k LIMIT 1) "
                    + "SELECT k AS lookup_key FROM copied) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "lateral_volatile_limited_union_cte",
                    "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL "
                    + "SELECT k+1,r FROM seq WHERE k<3) "
                    + "SELECT t.id,t.n FROM seq s JOIN LATERAL "
                    + "(WITH copied AS (SELECT k FROM seq WHERE k=s.k AND r=s.r "
                    + "UNION SELECT k FROM seq WHERE k=s.k AND r=s.r LIMIT 1 OFFSET 0) "
                    + "SELECT k AS lookup_key FROM copied) l ON TRUE "
                    + "JOIN recursive_target t ON t.id=l.lookup_key"
                    "json_after_seq",
                    recursive + " SELECT t.id,t.n FROM seq s JOIN "
                    + "JSON_TABLE(JSON_ARRAY(s.k),'$[*]' COLUMNS(k INT PATH '$')) j ON j.k=s.k "
                    + "JOIN recursive_target t ON t.id=j.k"
                    "shared_volatile",
                    "WITH RECURSIVE seq(k,r) AS (SELECT 1,RAND() UNION ALL SELECT k+1,r FROM seq WHERE k<3) "
                    + "SELECT t.id,t.n FROM seq a JOIN seq b ON b.k=a.k AND b.r=a.r "
                    + "JOIN recursive_target t ON t.id=a.k" ] do
                  let view = "recursive_" + name
                  expectOk (run store ($"CREATE VIEW {view} AS {body}")) ("create " + view)
                  Expect.equal
                      (rows store ($"SELECT is_updatable FROM information_schema.views WHERE table_name='{view}'"))
                      [ [ Some "YES" ] ]
                      (view + " metadata")
                  expectOk (run store ($"UPDATE {view} SET n=n+1 WHERE id=2")) ("update " + view)

              Expect.equal
                  (rows store "SELECT n FROM recursive_target WHERE id=2")
                  [ [ Some "58" ] ]
                  "dependent, derived, lateral, JSON, and repeated recursive rows each update once"

              expectOk
                  (run store
                      "CREATE VIEW recursive_checked AS WITH RECURSIVE seq(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3) SELECT t.id,t.n FROM seq JOIN recursive_target t ON t.id=seq.k WHERE t.n>=0 WITH CASCADED CHECK OPTION")
                  "create checked recursive view"
              match run store "UPDATE recursive_checked SET n=-1 WHERE id=2" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected recursive view CHECK OPTION rejection, got %A" other

              expectOk
                  (run store
                      "CREATE VIEW recursive_only AS WITH RECURSIVE seq(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3) SELECT k FROM seq")
                  "create recursive-only view"
              Expect.equal
                  (rows store "SELECT IS_UPDATABLE FROM information_schema.views WHERE table_name='recursive_only'")
                  [ [ Some "YES" ] ]
                  "MySQL reports recursive-only view metadata as updatable"
              for suffix, body, expected in
                  [ "expression", "SELECT k+1 AS x FROM seq", "NO"
                    "distinct", "SELECT DISTINCT k FROM seq", "NO"
                    "group", "SELECT k,COUNT(*) AS c FROM seq GROUP BY k", "NO"
                    "join", "SELECT a.k FROM seq a JOIN seq b ON a.k=b.k", "YES"
                    "derived", "SELECT d.k FROM (SELECT k FROM seq) d", "YES"
                    "derived_group", "SELECT d.k FROM (SELECT COUNT(*) AS k FROM seq) d", "YES"
                    "derived_distinct", "SELECT d.k FROM (SELECT DISTINCT k FROM seq) d", "YES" ] do
                  let name = "recursive_only_" + suffix
                  expectOk
                      (run store ($"CREATE VIEW {name} AS WITH RECURSIVE seq(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3) {body}"))
                      ("create " + name)
                  Expect.equal
                      (rows store ($"SELECT IS_UPDATABLE FROM information_schema.views WHERE table_name='{name}'"))
                      [ [ Some expected ] ]
                      (name + " metadata matches MySQL")
                  if expected = "YES" then
                      match run store ($"UPDATE {name} SET k=4") with
                      | Err(1288, _) -> ()
                      | other -> failtestf "expected %s UPDATE rejection, got %A" name other
              match run store "UPDATE recursive_only SET k=4" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected recursive-only UPDATE rejection, got %A" other

          testCase "joined views update a target after read-only CTE derived sources"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE derived_cte_target(id INT PRIMARY KEY,n INT)") "create target"
              expectOk (run store "CREATE TABLE derived_cte_source(id INT,flag INT)") "create source"
              expectOk (run store "CREATE TABLE derived_cte_aux(id INT,marker INT)") "create aux"
              expectOk (run store "INSERT INTO derived_cte_target VALUES(1,10),(2,20)") "seed target"
              expectOk (run store "INSERT INTO derived_cte_source VALUES(1,1),(1,2),(2,3)") "seed source"
              expectOk (run store "INSERT INTO derived_cte_aux VALUES(1,9),(2,8)") "seed aux"

              for name, columns, body in
                  [ "simple", "k,f", "SELECT x.id,x.flag FROM (SELECT id,flag FROM derived_cte_source) x"
                    "aggregate", "k,f", "SELECT x.id,SUM(x.flag) AS flag FROM (SELECT id,flag FROM derived_cte_source) x GROUP BY x.id"
                    "nested", "k,f", "SELECT x.id,x.flag FROM (SELECT y.id,y.flag FROM (SELECT id,flag FROM derived_cte_source) y) x"
                    "star", "k,f", "SELECT * FROM derived_cte_source"
                    "qualified_star", "k,f", "SELECT s.* FROM derived_cte_source s"
                    "derived_star", "k,f", "SELECT * FROM (SELECT id,flag FROM derived_cte_source) x"
                    "mixed_star", "k,f,extra", "SELECT s.*,s.flag+1 FROM derived_cte_source s"
                    "joined_star", "k,f,aux_id,marker", "SELECT * FROM derived_cte_source s JOIN derived_cte_aux q ON q.id=s.id"
                    "joined_qualified_star", "k,f", "SELECT s.* FROM derived_cte_source s JOIN derived_cte_aux q ON q.id=s.id"
                    "using_star", "k,f,marker", "SELECT * FROM derived_cte_source s JOIN derived_cte_aux q USING(id)"
                    "natural_star", "k,f,marker", "SELECT * FROM derived_cte_source s NATURAL JOIN derived_cte_aux q"
                    "using_qualified_star", "k,f", "SELECT s.* FROM derived_cte_source s JOIN derived_cte_aux q USING(id)"
                    "using_right_star", "k,f,marker", "SELECT * FROM derived_cte_aux q RIGHT JOIN derived_cte_source s USING(id)" ] do
                  let view = "derived_cte_" + name
                  let sql =
                      $"CREATE VIEW {view} AS WITH d({columns}) AS ({body}) SELECT a.id,a.n,d.f FROM d JOIN derived_cte_target a ON a.id=d.k WHERE a.n>=0 WITH CASCADED CHECK OPTION"
                  expectOk (run store sql) ("create " + view)
                  Expect.equal
                      (rows store ($"SELECT is_updatable FROM information_schema.views WHERE table_name='{view}'"))
                      [ [ Some "YES" ] ]
                      (view + " is updatable in MySQL 8.4")
                  expectOk (run store ($"UPDATE {view} SET n=n+1 WHERE id=1")) ("update " + view)
                  match run store ($"UPDATE {view} SET n=-1 WHERE id=1") with
                  | Err(1369, _) -> ()
                  | other -> failtestf "expected CHECK OPTION rejection for %s, got %A" view other
                  match run store ($"INSERT INTO {view}(id,n) VALUES(3,0)") with
                  | Err(1471, _) -> ()
                  | other -> failtestf "expected joined-view INSERT rejection for %s, got %A" view other

              Expect.equal
                  (rows store "SELECT id,n FROM derived_cte_target ORDER BY id")
                  [ [ Some "1"; Some "23" ]; [ Some "2"; Some "20" ] ]
                  "duplicate CTE rows update the target once per statement"

          testCase "joined views update a target after two references to one read-only CTE"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE repeated_target(id INT PRIMARY KEY,n INT)") "create target"
              expectOk (run store "CREATE TABLE repeated_source(id INT,flag INT)") "create source"
              expectOk (run store "INSERT INTO repeated_target VALUES(1,10),(2,20)") "seed target"
              expectOk (run store "INSERT INTO repeated_source VALUES(1,1),(1,2),(2,3)") "seed source"
              expectOk
                  (run store
                      "CREATE VIEW repeated_cte_view AS WITH d AS (SELECT id,flag FROM repeated_source) SELECT a.id,a.n,d1.flag AS f1,d2.flag AS f2 FROM d d1 JOIN d d2 ON d1.id=d2.id JOIN repeated_target a ON a.id=d1.id WHERE a.n>=0 WITH CASCADED CHECK OPTION")
                  "create repeated CTE view"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='repeated_cte_view'")
                  [ [ Some "YES" ] ]
                  "MySQL 8.4 marks the physical component updatable"
              expectOk (run store "UPDATE repeated_cte_view SET n=n+1 WHERE id=1") "update repeated CTE view"
              Expect.equal
                  (rows store "SELECT id,n FROM repeated_target ORDER BY id")
                  [ [ Some "1"; Some "11" ]; [ Some "2"; Some "20" ] ]
                  "duplicate joined matches update the target once"
              match run store "UPDATE repeated_cte_view SET n=-1 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected CHECK OPTION rejection, got %A" other

              expectOk
                  (run store
                      "CREATE VIEW repeated_aggregate_cte_view AS WITH d(k,f) AS (SELECT id,SUM(flag) FROM repeated_source GROUP BY id) SELECT a.id,a.n,d1.f AS f1,d2.f AS f2 FROM d d1 JOIN d d2 ON d1.k=d2.k JOIN repeated_target a ON a.id=d1.k WHERE a.n>=0 WITH CASCADED CHECK OPTION")
                  "create repeated aggregate CTE view"
              Expect.equal
                  (rows store "SELECT is_updatable FROM information_schema.views WHERE table_name='repeated_aggregate_cte_view'")
                  [ [ Some "YES" ] ]
                  "the repeated aggregate CTE leaves the physical target writable"
              expectOk (run store "UPDATE repeated_aggregate_cte_view SET n=n+1 WHERE id=1") "update aggregate CTE view"
              Expect.equal
                  (rows store "SELECT id,n FROM repeated_target ORDER BY id")
                  [ [ Some "1"; Some "12" ]; [ Some "2"; Some "20" ] ]
                  "each repeated source updates the target once"
              match run store "UPDATE repeated_aggregate_cte_view SET n=-1 WHERE id=1" with
              | Err(1369, _) -> ()
              | other -> failtestf "expected aggregate CTE CHECK OPTION rejection, got %A" other

              expectOk
                  (run store
                      "CREATE VIEW repeated_random_cte_view AS WITH d AS (SELECT id,RAND() AS r FROM repeated_source) SELECT a.id,a.n,d1.r AS r1,d2.r AS r2 FROM d d1 JOIN d d2 ON d1.id=d2.id AND d1.r=d2.r JOIN repeated_target a ON a.id=d1.id")
                  "create repeated volatile CTE view"
              expectOk (run store "UPDATE repeated_random_cte_view SET n=n+1 WHERE id=2") "update volatile CTE view"
              Expect.equal
                  (rows store "SELECT id,n FROM repeated_target ORDER BY id")
                  [ [ Some "1"; Some "12" ]; [ Some "2"; Some "21" ] ]
                  "reused CTE values come from one materialization"

          testCase "a direct view predicate limits UPDATE and DELETE but not INSERT"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE accounts (id INT PRIMARY KEY, name VARCHAR(20), score INT)") "create accounts"
              expectOk (run store "INSERT INTO accounts VALUES (1, 'visible', 10), (2, 'hidden', 1)") "seed accounts"
              expectOk (run store "CREATE VIEW visible_accounts AS SELECT id, name FROM accounts WHERE score >= 5") "create view"

              [ "INSERT INTO visible_accounts VALUES (2, 'upserted') ON DUPLICATE KEY UPDATE name = VALUES(name)"
                "REPLACE INTO visible_accounts VALUES (2, 'replaced')" ]
              |> List.iter (fun sql ->
                  match run store sql with
                  | Err(1369, _) -> ()
                  | other -> failtestf "expected hidden-row conflict rejection for %s, got %A" sql other)

              let mutable predicateCalls = 0
              let registry =
                  Fsdb.Functions.builtins
                  |> Fsdb.Functions.registerScalar "PREDICATE_TOUCH" (fun values ->
                      match values |> List.tryHead with
                      | Some(Fsdb.Value.VInt _ as value) ->
                          predicateCalls <- predicateCalls + 1
                          value
                      | _ -> Fsdb.Value.VNull)

              expectOk
                  (TestSupport.Sql.execute
                      store
                      registry
                      "UPDATE visible_accounts SET name = name WHERE PREDICATE_TOUCH(id) > 0")
                  "update visible rows with an effectful caller predicate"

              Expect.equal predicateCalls 1 "the caller predicate is not evaluated for the hidden row"

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

          testCase "a view predicate with a subquery remains writable"
          <| fun _ ->
              let store = setup ()
              expectOk (run store "CREATE VIEW filtered AS SELECT id, name FROM vendors WHERE id IN (SELECT vendor_id FROM receipts)") "create view"
              expectOk (run store "UPDATE filtered SET name = 'updated'") "update through subquery predicate"
              Expect.equal (rows store "SELECT name FROM vendors ORDER BY id") [ [ Some "updated" ]; [ Some "updated" ] ] "matching rows update"

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

          testCase "nested writable views do not regain columns hidden by an inner view"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE vault (id INT PRIMARY KEY, public_value INT, secret INT)") "create vault"
              expectOk (run store "INSERT INTO vault VALUES (1, 10, 99)") "seed vault"
              expectOk (run store "CREATE VIEW public_vault AS SELECT id, public_value, secret FROM vault") "create inner view"
              expectOk
                  (run store "CREATE VIEW malformed_vault AS SELECT id, public_value, secret AS leaked FROM public_vault")
                  "create outer view"
              expectOk (run store "ALTER VIEW public_vault AS SELECT id, public_value FROM vault") "hide the inner column"

              match run store "UPDATE malformed_vault SET public_value = leaked WHERE id = 1" with
              | Err(1054, _) -> ()
              | other -> failtestf "expected the inner projection boundary to reject secret, got %A" other

              Expect.equal (rows store "SELECT public_value FROM vault") [ [ Some "10" ] ] "the secret was not copied"

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
              let root = apply root "GRANT SELECT, UPDATE, INSERT ON fsdb.writable_vendors TO writer"
              let writer = { Fsdb.Session.create 3 store with User = "writer" }
              let _root = apply root "REVOKE UPDATE, INSERT ON fsdb.vendors FROM owner"

              match Fsdb.QueryHandler.handle writer "UPDATE writable_vendors SET name = 'Blocked' WHERE id = 1" |> snd with
              | Err(1142, message) -> Expect.stringContains message "vendors" "revoked base table named"
              | other -> failtestf "expected definer privilege failure, got %A" other

              match Fsdb.QueryHandler.handle writer "INSERT INTO writable_vendors VALUES (3, 'Blocked')" |> snd with
              | Err(1142, message) -> Expect.stringContains message "vendors" "revoked base table named"
              | other -> failtestf "expected definer privilege failure, got %A" other

          testCase "nested view writes authorize every definer boundary"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let root = Fsdb.Session.create 1 store
              let root = apply root "CREATE TABLE secured_rows (id INT PRIMARY KEY, n INT)"
              let root = apply root "INSERT INTO secured_rows VALUES (1, 10)"
              let root = apply root "CREATE USER inner_owner"
              let root = apply root "CREATE USER outer_owner"
              let root = apply root "CREATE USER nested_writer"
              let root = apply root "GRANT SELECT, UPDATE ON fsdb.secured_rows TO inner_owner"
              let root = apply root "GRANT CREATE VIEW ON fsdb.* TO inner_owner"
              let innerOwner = { Fsdb.Session.create 2 store with User = "inner_owner" }
              let _innerOwner = apply innerOwner "CREATE VIEW inner_secured AS SELECT id, n FROM secured_rows"
              let root = apply root "GRANT SELECT, UPDATE ON fsdb.inner_secured TO outer_owner"
              let root = apply root "GRANT CREATE VIEW ON fsdb.* TO outer_owner"
              let outerOwner = { Fsdb.Session.create 3 store with User = "outer_owner" }
              let _outerOwner = apply outerOwner "CREATE VIEW outer_secured AS SELECT id, n FROM inner_secured"
              let root = apply root "GRANT SELECT, UPDATE ON fsdb.outer_secured TO nested_writer"
              let writer = { Fsdb.Session.create 4 store with User = "nested_writer" }
              let writer = apply writer "UPDATE outer_secured SET n = 11 WHERE id = 1"
              let _root = apply root "REVOKE UPDATE ON fsdb.secured_rows FROM inner_owner"

              match Fsdb.QueryHandler.handle writer "UPDATE outer_secured SET n = 12 WHERE id = 1" |> snd with
              | Err(1356, message) -> Expect.stringContains message "outer_secured" "the invalid outer view is named"
              | other -> failtestf "expected the inner definer's revoked privilege to invalidate the outer view, got %A" other

          testCase "view DELETE subqueries retain the outer definer identity"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let root = Fsdb.Session.create 1 store
              let root = apply root "CREATE TABLE delete_target (id INT PRIMARY KEY)"
              let root = apply root "INSERT INTO delete_target VALUES (1)"
              let root = apply root "CREATE USER delete_owner"
              let root = apply root "CREATE USER delete_writer"
              let root = apply root "GRANT CREATE VIEW ON fsdb.* TO delete_owner"
              let root = apply root "GRANT SELECT, DELETE ON fsdb.delete_target TO delete_owner"
              let owner = { Fsdb.Session.create 2 store with User = "delete_owner" }
              let owner = apply owner "CREATE SQL SECURITY INVOKER VIEW delete_identity AS SELECT CURRENT_USER() AS who"
              let root = apply root "GRANT SELECT ON fsdb.delete_identity TO delete_owner"
              let _owner =
                  apply
                      owner
                      "CREATE VIEW delete_gate AS SELECT id FROM delete_target WHERE EXISTS (SELECT 1 FROM delete_identity WHERE who = 'delete_owner@%')"

              let _root = apply root "GRANT DELETE ON fsdb.delete_gate TO delete_writer"
              let writer = { Fsdb.Session.create 3 store with User = "delete_writer" }
              expectOk (Fsdb.QueryHandler.handle writer "DELETE FROM delete_gate" |> snd) "delete through definer predicate"
              Expect.equal (rows store "SELECT id FROM delete_target") [] "the nested invoker saw the outer definer"

          testCase "view definitions reject user and system variables"
          <| fun _ ->
              let store = setup ()

              [ "CREATE VIEW user_variable AS SELECT @value"
                "CREATE VIEW system_variable AS SELECT @@max_connections" ]
              |> List.iter (fun sql ->
                  match run store sql with
                  | Err(1351, "View's SELECT contains a variable or parameter") -> ()
                  | other -> failtestf "expected view variable rejection, got %A" other)

          testCase "view definitions reject SELECT INTO destinations"
          <| fun _ ->
              let store = setup ()

              [ "CREATE VIEW assigned AS SELECT 1 INTO @value"
                "CREATE VIEW exported AS SELECT 1 INTO OUTFILE '/tmp/view.tsv'" ]
              |> List.iter (fun sql ->
                  match run store sql with
                  | Err(1350, "View's SELECT contains a 'INTO' clause") -> ()
                  | other -> failtestf "expected view INTO rejection, got %A" other)

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

          testCase "view DDL retains declaration options and ALTER adopts the altering definer"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let root = Fsdb.Session.create 1 store
              let root = apply root "CREATE TABLE source (id INT PRIMARY KEY)"
              let root = apply root "INSERT INTO source VALUES (1)"
              let root = apply root "CREATE USER 'owner'@'%'"
              let root = apply root "CREATE USER 'other'@'%'"
              let root = apply root "CREATE USER 'blind'@'%'"
              let root = apply root "GRANT CREATE VIEW, SELECT ON fsdb.* TO 'owner'@'%'"
              let root = apply root "GRANT CREATE VIEW ON fsdb.* TO 'blind'@'%'"
              let owner = { Fsdb.Session.create 2 store with User = "owner"; AccountHost = "%" }
              let blind = { Fsdb.Session.create 3 store with User = "blind"; AccountHost = "%" }

              match Fsdb.QueryHandler.handle blind "CREATE VIEW blind_view AS SELECT id FROM source WITH CHECK OPTION" |> snd with
              | Err(1142, message) -> Expect.stringContains message "SELECT" "definition privileges"
              | other -> failtestf "expected CHECK OPTION definition privilege denial, got %A" other

              let _owner =
                  apply
                      owner
                      "CREATE ALGORITHM=MERGE DEFINER='owner'@'%' SQL SECURITY INVOKER VIEW controlled AS SELECT id FROM source"

              match Fsdb.InformationSchema.showCreateView store.Catalog "fsdb" "controlled" with
              | Ok(_, [ [ _; Some ddl; _; _ ] ]) ->
                  Expect.stringContains ddl "ALGORITHM=MERGE" "algorithm"
                  Expect.stringContains ddl "DEFINER=`owner`@`%`" "definer"
                  Expect.stringContains ddl "SQL SECURITY INVOKER" "security"
              | other -> failtestf "expected SHOW CREATE VIEW, got %A" other

              match Fsdb.QueryHandler.handle owner "CREATE DEFINER='other'@'%' VIEW forbidden AS SELECT id FROM source" |> snd with
              | Err(1227, message) -> Expect.stringContains message "SET_ANY_DEFINER" "definer authority"
              | other -> failtestf "expected explicit definer denial, got %A" other

              let root, missingCreated =
                  Fsdb.QueryHandler.handle root "CREATE DEFINER='missing'@'%' VIEW missing_definer AS SELECT id FROM source"

              expectOk missingCreated "missing definer metadata"
              Expect.exists root.Diagnostics (fun condition -> condition.Code = 1449) "missing definer note"

              match Fsdb.QueryHandler.handle root "SELECT * FROM missing_definer" |> snd with
              | Err(1449, _) -> ()
              | other -> failtestf "expected missing definer execution failure, got %A" other

              let root, expressionCreated =
                  Fsdb.QueryHandler.handle
                      root
                      "CREATE ALGORITHM=MERGE VIEW expression_view AS SELECT id + 1 AS next_id FROM source ORDER BY id"

              expectOk expressionCreated "mergeable expression view"
              Expect.isFalse (root.Diagnostics |> List.exists (fun condition -> condition.Code = 1354)) "MERGE remains valid"

              match Fsdb.InformationSchema.showCreateView store.Catalog "fsdb" "expression_view" with
              | Ok(_, [ [ _; Some ddl; _; _ ] ]) -> Expect.stringContains ddl "ALGORITHM=MERGE" "mergeable algorithm"
              | other -> failtestf "expected mergeable expression view, got %A" other

              let root, aggregateCreated =
                  Fsdb.QueryHandler.handle root "CREATE ALGORITHM=MERGE VIEW aggregate_view AS SELECT COUNT(*) AS n FROM source"

              expectOk aggregateCreated "aggregate view"
              Expect.exists root.Diagnostics (fun condition -> condition.Code = 1354) "incompatible MERGE warning"

              match Fsdb.InformationSchema.showCreateView store.Catalog "fsdb" "aggregate_view" with
              | Ok(_, [ [ _; Some ddl; _; _ ] ]) -> Expect.stringContains ddl "ALGORITHM=UNDEFINED" "normalized algorithm"
              | other -> failtestf "expected normalized aggregate view, got %A" other

              let root = apply root "ALTER VIEW controlled AS SELECT id FROM source WHERE id > 0"

              match Fsdb.InformationSchema.showCreateView store.Catalog "fsdb" "controlled" with
              | Ok(_, [ [ _; Some ddl; _; _ ] ]) ->
                  Expect.stringContains ddl "ALGORITHM=MERGE" "ALTER preserves algorithm"
                  Expect.stringContains ddl "DEFINER=`root`@`%`" "ALTER uses the altering account"
                  Expect.stringContains ddl "SQL SECURITY INVOKER" "ALTER preserves security"
              | other -> failtestf "expected preserved ALTER envelope, got %A" other

              let _root =
                  apply
                      root
                      "ALTER ALGORITHM=TEMPTABLE DEFINER=CURRENT_USER SQL SECURITY DEFINER VIEW controlled AS SELECT id FROM source"

              match Fsdb.InformationSchema.showCreateView store.Catalog "fsdb" "controlled" with
              | Ok(_, [ [ _; Some ddl; _; _ ] ]) ->
                  Expect.stringContains ddl "ALGORITHM=TEMPTABLE" "changed algorithm"
                  Expect.stringContains ddl "DEFINER=`root`@`%`" "changed definer"
                  Expect.stringContains ddl "SQL SECURITY DEFINER" "changed security"
              | other -> failtestf "expected altered SHOW CREATE VIEW, got %A" other

              match run store "UPDATE controlled SET id = 2" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected TEMPTABLE view to be non-updatable, got %A" other

              match run store "ALTER VIEW absent AS SELECT 1" with
              | Err(1146, _) -> ()
              | other -> failtestf "expected missing ALTER target, got %A" other

              match run store "ALTER VIEW source AS SELECT id FROM source" with
              | Err(1347, _) -> ()
              | other -> failtestf "expected table ALTER target rejection, got %A" other

              match run store "CREATE OR REPLACE VIEW source AS SELECT 1" with
              | Err(1347, _) -> ()
              | other -> failtestf "expected table replacement rejection, got %A" other

          testCase "recursive view references fail cleanly"
          <| fun _ ->
              let store = setup ()
              expectOk (run store "CREATE VIEW looped AS SELECT 1 AS n") "create view"
              expectOk
                  (run store "UPDATE mysql.views SET view_definition='SELECT * FROM looped' WHERE view_name='looped'")
                  "seed a recursive stored definition"

              match run store "SELECT * FROM looped" with
              | Err(1462, message) -> Expect.stringContains message "recursive reference" "clear recursion error"
              | other -> failtestf "expected 1462, got %A" other

              let session = Fsdb.Session.create 1 store

              match Fsdb.QueryHandler.prepareStatementForSession session "SELECT * FROM looped" with
              | Ok(statement, parameterCount) ->
                  let parameters, _ = Fsdb.QueryHandler.preparedMetadata session statement parameterCount
                  Expect.isEmpty parameters "cyclic origin discovery completes without parameter metadata"
              | Error error -> failtestf "expected cyclic view text to prepare safely, got %A" error

              expectOk (run store "CREATE VIEW looped_peer AS SELECT * FROM looped") "create peer view"
              expectOk (run store "CREATE OR REPLACE VIEW looped AS SELECT * FROM looped_peer") "create mutual cycle"

              match Fsdb.QueryHandler.prepareStatementForSession session "SELECT * FROM looped_peer" with
              | Ok(statement, parameterCount) ->
                  Fsdb.QueryHandler.preparedMetadata session statement parameterCount |> ignore
              | Error error -> failtestf "expected mutual view cycle text to prepare safely, got %A" error

          testCase "acyclic view metadata traversal is depth bounded"
          <| fun _ ->
              let store = setup ()
              let deepest = Fsdb.Limits.maxViewMetadataNesting * 4
              expectOk (run store "CREATE VIEW depth_view_0 AS SELECT id FROM vendors") "create base view"

              for index in 1 .. deepest do
                  let result =
                      run
                          store
                          (sprintf "CREATE VIEW depth_view_%d AS SELECT * FROM depth_view_%d" index (index - 1))

                  expectOk result "create depth-bounded view"

              let session = Fsdb.Session.create 1 store

              match
                  Fsdb.QueryHandler.prepareStatementForSession
                      session
                      (sprintf "SELECT * FROM depth_view_%d" deepest)
              with
              | Ok(statement, parameterCount) ->
                  Fsdb.QueryHandler.preparedMetadata session statement parameterCount |> ignore
              | Error _ -> ()

              match run store (sprintf "SELECT * FROM depth_view_%d" deepest) with
              | Err(1436, message) -> Expect.stringContains message "view" "execution uses the same expansion ceiling"
              | other -> failtestf "expected bounded view execution to return 1436, got %A" other

          testCase "view definitions persist through the WAL"
          <| fun _ ->
              TestSupport.withDirectory "view" (fun dir ->
                  let store = Fsdb.Storage.create ()
                  Fsdb.Persistence.attach dir store
                  expectOk (run store "CREATE TABLE t (id INT PRIMARY KEY)") "create table"
                  expectOk (run store "INSERT INTO t VALUES (1), (2)") "seed"
                  expectOk
                      (run store "CREATE ALGORITHM=MERGE DEFINER='root'@'%' VIEW doubled AS SELECT id, id * 2 AS n FROM t")
                      "create view"
                  expectOk (run store "CREATE VIEW positive AS SELECT id FROM t WHERE id > 0 WITH LOCAL CHECK OPTION") "create guarded view"
                  expectOk (run store "CREATE SQL SECURITY INVOKER VIEW invoked AS SELECT id FROM t") "create invoker view"

                  let reloaded = Fsdb.Persistence.load dir
                  Expect.equal
                      (rows reloaded "SELECT * FROM doubled ORDER BY id")
                      [ [ Some "1"; Some "2" ]; [ Some "2"; Some "4" ] ]
                      "reloaded view"

                  match run reloaded "INSERT INTO positive VALUES (0)" with
                  | Err(1369, "CHECK OPTION failed 'fsdb.positive'") -> ()
                  | other -> failtestf "expected persisted CHECK OPTION, got %A" other

                  Expect.equal
                      (rows reloaded "SELECT security_type FROM mysql.views WHERE view_name = 'invoked'")
                      [ [ Some "INVOKER" ] ]
                      "reloaded security type"

                  match Fsdb.InformationSchema.showCreateView reloaded.Catalog "fsdb" "doubled" with
                  | Ok(_, [ [ _; Some ddl; _; _ ] ]) ->
                      Expect.stringContains ddl "ALGORITHM=MERGE" "reloaded algorithm"
                      Expect.stringContains ddl "DEFINER=`root`@`%`" "reloaded definer"
                  | other -> failtestf "expected persisted view envelope, got %A" other)

          testCase "SHOW and information_schema expose stored views"
          <| fun _ ->
              let store = setup ()
              let session = Fsdb.Session.create 1 store

              let session, created =
                  Fsdb.QueryHandler.handle session "CREATE VIEW totals (vendor, summed) AS SELECT vendor_id, SUM(total) AS total FROM receipts GROUP BY vendor_id"

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
                  Expect.stringStarts
                      (row.[1] |> Option.defaultValue "")
                      "CREATE ALGORITHM=UNDEFINED DEFINER=`root`@`%` SQL SECURITY DEFINER VIEW `totals` (`vendor`, `summed`) AS"
                      "SHOW statement"
              | other -> failtestf "expected SHOW CREATE VIEW row, got %A" other

              let _, created = Fsdb.QueryHandler.handle session "CREATE VIEW `quoted``view` (`quoted``column`) AS SELECT vendor_id FROM receipts"
              expectOk created "create quoted view"

              match Fsdb.InformationSchema.showCreateView store.Catalog "fsdb" "quoted`view" with
              | Ok(_, [ [ _; Some ddl; _; _ ] ]) ->
                  Expect.stringContains ddl "VIEW `quoted``view` (`quoted``column`) AS" "quoted identifiers"
              | other -> failtestf "expected quoted SHOW CREATE VIEW row, got %A" other

          testCase "view definitions use canonical qualified SQL"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE users (id INT PRIMARY KEY, name VARCHAR(20))") "create users"
              expectOk (run store "CREATE TABLE orders (id INT PRIMARY KEY, user_id INT, total DECIMAL(10,2))") "create orders"

              expectOk
                  (run
                      store
                      "CREATE VIEW joined AS SELECT u.id AS uid, o.total FROM users u JOIN orders o ON o.user_id = u.id WHERE o.total >= 10.50")
                  "create joined view"

              expectOk
                  (run
                      store
                      "CREATE VIEW combined AS WITH c AS (SELECT id FROM users WHERE id > 0) SELECT id AS n FROM c UNION ALL SELECT user_id FROM orders ORDER BY n LIMIT 5")
                  "create CTE union view"

              match Fsdb.InformationSchema.showCreateView store.Catalog "fsdb" "joined" with
              | Ok(_, [ [ _; Some ddl; _; _ ] ]) ->
                  Expect.stringEnds
                      ddl
                      "AS select `u`.`id` AS `uid`,`o`.`total` AS `total` from (`users` `u` join `orders` `o` on((`o`.`user_id` = `u`.`id`))) where (`o`.`total` >= 10.50)"
                      "SHOW CREATE uses schema-relative canonical SQL"
              | other -> failtestf "expected canonical SHOW CREATE VIEW row, got %A" other

              Expect.equal
                  (rows
                      store
                      "SELECT VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA = 'fsdb' AND TABLE_NAME = 'joined'")
                  [ [ Some
                          "select `u`.`id` AS `uid`,`o`.`total` AS `total` from (`fsdb`.`users` `u` join `fsdb`.`orders` `o` on((`o`.`user_id` = `u`.`id`))) where (`o`.`total` >= 10.50)" ] ]
                  "information_schema qualifies physical tables"

              Expect.equal
                  (rows
                      store
                      "SELECT VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA = 'fsdb' AND TABLE_NAME = 'combined'")
                  [ [ Some
                          "with `c` as (select `fsdb`.`users`.`id` AS `id` from `fsdb`.`users` where (`fsdb`.`users`.`id` > 0)) select `c`.`id` AS `n` from `c` union all select `fsdb`.`orders`.`user_id` AS `user_id` from `fsdb`.`orders` order by `n` limit 5" ] ]
                  "CTE names remain local while their physical sources are qualified"

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

          testCase "invoker views use the caller's privileges and identity"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = Fsdb.Session.create 1 store

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let root = apply root "CREATE TABLE source (id INT PRIMARY KEY)"
              let root = apply root "INSERT INTO source VALUES (1)"
              let root = apply root "CREATE USER owner"
              let root = apply root "CREATE USER reader"
              let root = apply root "GRANT CREATE VIEW ON fsdb.* TO owner"
              let root = apply root "GRANT SELECT, UPDATE ON fsdb.source TO owner"
              let root = apply root "GRANT SELECT, SHOW VIEW ON fsdb.invoker_view TO reader"
              let root = apply root "GRANT SELECT, UPDATE ON fsdb.invoker_writable TO reader"
              let owner = { Fsdb.Session.create 2 store with User = "owner" }
              let reader = { Fsdb.Session.create 3 store with User = "reader" }

              let _owner =
                  apply
                      owner
                      "CREATE SQL SECURITY INVOKER VIEW invoker_view AS SELECT id, CURRENT_USER() AS execution_identity FROM source"

              let _owner = apply owner "CREATE SQL SECURITY INVOKER VIEW invoker_writable AS SELECT id FROM source"

              match Fsdb.QueryHandler.handle reader "SELECT id, execution_identity FROM invoker_view" |> snd with
              | Err(1142, message) -> Expect.stringContains message "source" "invoker needs base-table access"
              | other -> failtestf "expected invoker privilege failure, got %A" other

              match Fsdb.QueryHandler.handle reader "UPDATE invoker_writable SET id = 2 WHERE id = 1" |> snd with
              | Err(1142, message) -> Expect.stringContains message "source" "invoker write needs base-table access"
              | other -> failtestf "expected invoker write privilege failure, got %A" other

              let _root = apply root "GRANT SELECT, UPDATE ON fsdb.source TO reader"

              match Fsdb.QueryHandler.handle reader "SELECT id, execution_identity FROM invoker_view" |> snd with
              | ResultSet(_, [ [ Some "1"; Some identity ] ]) -> Expect.equal identity "reader@%" "invoker identity"
              | other -> failtestf "expected invoker-backed row, got %A" other

              expectOk
                  (Fsdb.QueryHandler.handle reader "UPDATE invoker_writable SET id = 2 WHERE id = 1" |> snd)
                  "invoker update"

              Expect.equal
                  (rows store "SELECT view_name, security_type FROM mysql.views WHERE view_name = 'invoker_view'")
                  [ [ Some "invoker_view"; Some "INVOKER" ] ]
                  "stored security type"

              match Fsdb.QueryHandler.handle reader "SELECT security_type FROM information_schema.views WHERE table_name = 'invoker_view'" |> snd with
              | ResultSet(_, [ [ Some "INVOKER" ] ]) -> ()
              | other -> failtestf "expected invoker metadata, got %A" other

              match Fsdb.InformationSchema.showCreateView store.Catalog "fsdb" "invoker_view" with
              | Ok(_, [ [ _; Some ddl; _; _ ] ]) -> Expect.stringContains ddl "SQL SECURITY INVOKER" "SHOW statement"
              | other -> failtestf "expected SHOW CREATE VIEW, got %A" other

          testCase "view materialization cache is isolated by effective account"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              let apply session sql =
                  let session, result = Fsdb.QueryHandler.handle session sql
                  expectOk result sql
                  session

              let root = Fsdb.Session.create 1 store
              let root = apply root "CREATE TABLE cache_source (id INT PRIMARY KEY, secret VARCHAR(20))"
              let root = apply root "INSERT INTO cache_source VALUES (1, 'classified')"
              let root = apply root "CREATE USER cache_owner"
              let root = apply root "CREATE USER cache_reader"
              let root = apply root "GRANT CREATE VIEW ON fsdb.* TO cache_owner"
              let root = apply root "GRANT SELECT ON fsdb.cache_source TO cache_owner"
              let owner = { Fsdb.Session.create 2 store with User = "cache_owner" }
              let owner = apply owner "CREATE SQL SECURITY INVOKER VIEW cache_invoker AS SELECT id, secret FROM cache_source"
              let root = apply root "GRANT SELECT ON fsdb.cache_invoker TO cache_owner"
              let _owner = apply owner "CREATE VIEW cache_public AS SELECT id FROM cache_invoker"
              let root = apply root "GRANT SELECT ON fsdb.cache_invoker TO cache_reader"
              let _root = apply root "GRANT SELECT ON fsdb.cache_public TO cache_reader"
              let reader = { Fsdb.Session.create 3 store with User = "cache_reader" }

              match
                  Fsdb.QueryHandler.handle
                      reader
                      "SELECT i.secret FROM cache_public p JOIN cache_invoker i ON i.id = p.id"
                  |> snd
              with
              | Err(1142, message) -> Expect.stringContains message "cache_source" "the reader must authorize the invoker view"
              | other -> failtestf "expected the cached invoker boundary to reject the reader, got %A" other

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
