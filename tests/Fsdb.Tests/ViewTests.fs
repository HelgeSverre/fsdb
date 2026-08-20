module Fsdb.Tests.ViewTests

open Expecto
open Fsdb.Executor

let private run store sql =
    match Fsdb.Parser.parse sql with
    | Ok statement -> execute store Fsdb.Functions.builtins Fsdb.Storage.defaultDatabase (0L, 0L) false statement |> snd
    | Error message -> failtestf "parse failed for %s: %s" sql message

let private expectOk result context =
    match result with
    | Err(code, message) -> failtestf "%s failed (%d): %s" context code message
    | _ -> ()

let private rows store sql =
    match run store sql with
    | ResultSet(_, rows) -> rows
    | other -> failtestf "expected rows from %s, got %A" sql other

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
              let dir =
                  System.IO.Path.Combine(
                      System.IO.Path.GetTempPath(),
                      "fsdb-view-tests",
                      System.Guid.NewGuid().ToString "N"
                  )

              System.IO.Directory.CreateDirectory dir |> ignore

              try
                  let store = Fsdb.Storage.create ()
                  Fsdb.Persistence.attach dir store
                  expectOk (run store "CREATE TABLE t (id INT PRIMARY KEY)") "create table"
                  expectOk (run store "INSERT INTO t VALUES (1), (2)") "seed"
                  expectOk (run store "CREATE VIEW doubled AS SELECT id, id * 2 AS n FROM t") "create view"

                  let reloaded = Fsdb.Persistence.load dir
                  Expect.equal
                      (rows reloaded "SELECT * FROM doubled ORDER BY id")
                      [ [ Some "1"; Some "2" ]; [ Some "2"; Some "4" ] ]
                      "reloaded view"
              finally
                  System.IO.Directory.Delete(dir, true)

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
