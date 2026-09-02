module Fsdb.Tests.TransactionTests

open System
open Expecto
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let tests =
    testList
        "Transactions"
        [ testCase "BEGIN defers the private catalog until the first database statement"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE deferred_view (id INT PRIMARY KEY)"
              let reader, _ = handle (create 2 store) "BEGIN"

              match reader.Tx with
              | Some transaction ->
                  Expect.isFalse transaction.Seeded "the consistent view is still deferred"
                  Expect.isTrue (obj.ReferenceEquals(transaction.Snapshot.Databases, reader.Store.Databases)) "the provisional context shares the live catalog"
              | None -> failtest "expected an open transaction"

              handle setup "INSERT INTO deferred_view VALUES (1)" |> ignore
              let reader, result = handle reader "SELECT id FROM deferred_view"

              match result with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the first read to capture the committed row, got %A" other

              match reader.Tx with
              | Some transaction ->
                  Expect.isTrue transaction.Seeded "the first database statement seeds the view"
                  Expect.isFalse (obj.ReferenceEquals(transaction.Snapshot.Databases, reader.Store.Databases)) "the seeded view owns private database cells"
              | None -> failtest "expected the transaction to remain open"

          testCase "a write inside BEGIN...COMMIT is invisible to another connection until commit"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_t (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_t VALUES (1)"
              let other = create 2 store

              match handle other "SELECT id FROM tx_t" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected no rows visible before commit, got %A" result

              let session, _ = handle session "COMMIT"
              ignore session

              match handle other "SELECT id FROM tx_t" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the committed row, got %A" result

          testCase "READ COMMITTED refreshes nonlocking reads and retains own writes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let writer = create 1 store
              let writer, _ = handle writer "CREATE TABLE tx_rc (id INT PRIMARY KEY)"
              let writer, configured = handle writer "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"
              Expect.equal configured (Affected 0UL) "READ COMMITTED is configured"
              let writer, started = handle writer "BEGIN"
              Expect.equal started (Affected 0UL) "the transaction starts"

              match handle writer "SELECT id FROM tx_rc ORDER BY id" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected no rows at the first read, got %A" result

              let other = create 2 store
              let other, firstInsert = handle other "INSERT INTO tx_rc VALUES (1)"
              Expect.equal firstInsert (Affected 1UL) "the concurrent row commits"

              match handle writer "SELECT id FROM tx_rc ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the fresh committed row, got %A" result

              let writer, ownInsert = handle writer "INSERT INTO tx_rc VALUES (2)"
              Expect.equal ownInsert (Affected 1UL) "the transaction writes privately"

              match handle other "SELECT id FROM tx_rc ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the private row to remain hidden, got %A" result

              let other, secondInsert = handle other "INSERT INTO tx_rc VALUES (3)"
              Expect.equal secondInsert (Affected 1UL) "the second concurrent row commits"

              match handle writer "SELECT id FROM tx_rc ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ]) -> ()
              | result -> failtestf "expected the committed rows and own write, got %A" result

              let writer, committed = handle writer "COMMIT"
              Expect.equal committed (Affected 0UL) "the transaction commits"

              match handle other "SELECT id FROM tx_rc ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ]) -> ()
              | result -> failtestf "expected every committed row, got %A" result

          testCase "READ COMMITTED upserts serialize duplicate rows"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_upsert (collection VARCHAR(32), name VARCHAR(32), value VARCHAR(32), PRIMARY KEY (collection, name))"
              let setup, _ = handle setup "INSERT INTO tx_upsert VALUES ('state', 'entry', 'initial')"
              let transaction, _ = handle setup "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"
              let transaction, _ = handle transaction "BEGIN"
              let transaction, first = handle transaction "INSERT INTO tx_upsert VALUES ('state', 'entry', 'transaction') ON DUPLICATE KEY UPDATE value = VALUES(value)"
              Expect.equal first (Affected 2UL) "the transaction updates the duplicate row"

              use writerStarted = new Threading.ManualResetEventSlim(false)
              let writer = create 2 store

              let concurrent =
                  Threading.Tasks.Task.Run(fun () ->
                      writerStarted.Set()
                      handle writer "INSERT INTO tx_upsert VALUES ('state', 'entry', 'concurrent') ON DUPLICATE KEY UPDATE value = VALUES(value)")

              Expect.isTrue (writerStarted.Wait(TimeSpan.FromSeconds 1.0)) "the concurrent upsert started"
              Threading.Thread.Sleep 50

              let transaction, visible = handle transaction "SELECT value FROM tx_upsert WHERE collection = 'state' AND name = 'entry'"

              match visible with
              | ResultSet(_, [ [ Some "transaction" ] ]) -> ()
              | result -> failtestf "expected the transaction's row without a rebase conflict, got %A" result

              let _, committed = handle transaction "COMMIT"
              Expect.equal committed (Affected 0UL) "the transaction commits before the waiting writer"
              Expect.isTrue (concurrent.Wait(TimeSpan.FromSeconds 5.0)) "the waiting upsert completes after commit"

              match concurrent.GetAwaiter().GetResult() |> snd with
              | Affected 2UL -> ()
              | result -> failtestf "expected the waiting upsert to update the committed row, got %A" result

              match handle writer "SELECT value FROM tx_upsert WHERE collection = 'state' AND name = 'entry'" |> snd with
              | ResultSet(_, [ [ Some "concurrent" ] ]) -> ()
              | result -> failtestf "expected the later writer's value, got %A" result

          testCase "READ COMMITTED inserts serialize unique keys"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let transaction = create 1 store
              let transaction, _ = handle transaction "CREATE TABLE tx_insert_key (collection VARCHAR(32), name VARCHAR(32), value VARCHAR(32), PRIMARY KEY (collection, name))"
              let transaction, _ = handle transaction "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"
              let transaction, _ = handle transaction "BEGIN"
              let transaction, inserted = handle transaction "INSERT INTO tx_insert_key VALUES ('state', 'new-entry', 'transaction')"
              Expect.equal inserted (Affected 1UL) "the transaction inserts the unique key privately"

              use writerStarted = new Threading.ManualResetEventSlim(false)
              let writer = create 2 store

              let concurrent =
                  Threading.Tasks.Task.Run(fun () ->
                      writerStarted.Set()
                      handle writer "INSERT INTO tx_insert_key VALUES ('state', 'new-entry', 'concurrent') ON DUPLICATE KEY UPDATE value = VALUES(value)")

              Expect.isTrue (writerStarted.Wait(TimeSpan.FromSeconds 1.0)) "the concurrent upsert started"
              Threading.Thread.Sleep 50

              let transaction, visible = handle transaction "SELECT value FROM tx_insert_key WHERE collection = 'state' AND name = 'new-entry'"

              match visible with
              | ResultSet(_, [ [ Some "transaction" ] ]) -> ()
              | result -> failtestf "expected the private insert without a rebase conflict, got %A" result

              let _, committed = handle transaction "COMMIT"
              Expect.equal committed (Affected 0UL) "the new key commits before the waiting writer"
              Expect.isTrue (concurrent.Wait(TimeSpan.FromSeconds 5.0)) "the waiting upsert completes after commit"

              match concurrent.GetAwaiter().GetResult() |> snd with
              | Affected 2UL -> ()
              | result -> failtestf "expected the waiting upsert to update the committed key, got %A" result

              match handle writer "SELECT value FROM tx_insert_key WHERE collection = 'state' AND name = 'new-entry'" |> snd with
              | ResultSet(_, [ [ Some "concurrent" ] ]) -> ()
              | result -> failtestf "expected the later writer's value, got %A" result

          testCase "READ COMMITTED keeps generated values and trigger effects stable across refreshes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let writer = create 1 store
              let writer, _ = handle writer "CREATE TABLE tx_rc_generated (id INT AUTO_INCREMENT PRIMARY KEY, token VARCHAR(36))"
              let writer, _ = handle writer "CREATE TABLE tx_rc_generated_log (id INT AUTO_INCREMENT PRIMARY KEY, token VARCHAR(36))"
              let writer, _ = handle writer "CREATE TABLE tx_rc_refresh (id INT PRIMARY KEY)"

              let writer, _ =
                  handle
                      writer
                      "CREATE TRIGGER tx_rc_generated_trigger AFTER INSERT ON tx_rc_generated FOR EACH ROW INSERT INTO tx_rc_generated_log(token) VALUES (NEW.token)"

              let writer, _ = handle writer "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"
              let writer, _ = handle writer "BEGIN"
              let writer, inserted = handle writer "INSERT INTO tx_rc_generated(token) VALUES (UUID())"
              Expect.equal inserted (Affected 1UL) "the generated row is inserted"

              let writer, sourceRows =
                  match handle writer "SELECT id, token FROM tx_rc_generated" with
                  | session, ResultSet(_, rows) -> session, rows
                  | _, result -> failtestf "expected the generated source row, got %A" result

              let writer, logRows =
                  match handle writer "SELECT id, token FROM tx_rc_generated_log" with
                  | session, ResultSet(_, rows) -> session, rows
                  | _, result -> failtestf "expected the trigger row, got %A" result

              let other = create 2 store
              let other, refreshed = handle other "INSERT INTO tx_rc_refresh VALUES (1)"
              Expect.equal refreshed (Affected 1UL) "the concurrent row commits"

              let writer, refreshedSourceRows =
                  match handle writer "SELECT id, token FROM tx_rc_generated" with
                  | session, ResultSet(_, rows) -> session, rows
                  | _, result -> failtestf "expected the generated source row after refresh, got %A" result

              let writer, refreshedLogRows =
                  match handle writer "SELECT id, token FROM tx_rc_generated_log" with
                  | session, ResultSet(_, rows) -> session, rows
                  | _, result -> failtestf "expected the trigger row after refresh, got %A" result

              Expect.equal refreshedSourceRows sourceRows "the generated row remains byte-for-byte stable"
              Expect.equal refreshedLogRows logRows "the trigger effect remains byte-for-byte stable"

              match handle writer "SELECT id, token FROM tx_rc_generated" |> snd with
              | ResultSet(_, rows) -> Expect.equal rows sourceRows "a second refresh keeps the generated row stable"
              | result -> failtestf "expected the generated source row after the second refresh, got %A" result

              let writer, committed = handle writer "COMMIT"
              Expect.equal committed (Affected 0UL) "the transaction commits"

              match handle other "SELECT id, token FROM tx_rc_generated" |> snd with
              | ResultSet(_, rows) -> Expect.equal rows sourceRows "the committed generated row remains stable"
              | result -> failtestf "expected the committed generated source row, got %A" result

              match handle other "SELECT id, token FROM tx_rc_generated_log" |> snd with
              | ResultSet(_, rows) -> Expect.equal rows logRows "the committed trigger effect remains stable"
              | result -> failtestf "expected the committed trigger row, got %A" result

          testCase "READ COMMITTED preserves an auto-increment identity across a refresh"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let writer = create 1 store
              let writer, _ = handle writer "CREATE TABLE tx_rc_auto (id INT AUTO_INCREMENT PRIMARY KEY, note VARCHAR(20))"
              let writer, _ = handle writer "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"
              let writer, _ = handle writer "BEGIN"
              let writer, inserted = handle writer "INSERT INTO tx_rc_auto(note) VALUES ('private')"
              Expect.equal inserted (Affected 1UL) "the private row is inserted"

              let other = create 2 store
              let other, external = handle other "INSERT INTO tx_rc_auto(id, note) VALUES (100, 'external')"
              Expect.equal external (Affected 1UL) "the explicit concurrent row commits"

              match handle writer "SELECT id, note FROM tx_rc_auto ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "private" ]; [ Some "100"; Some "external" ] ]) -> ()
              | result -> failtestf "expected the original private identity and external row, got %A" result

              let writer, committed = handle writer "COMMIT"
              Expect.equal committed (Affected 0UL) "the transaction commits"

              match handle other "SELECT id, note FROM tx_rc_auto ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "private" ]; [ Some "100"; Some "external" ] ]) -> ()
              | result -> failtestf "expected the original private identity after commit, got %A" result

          testCase "READ COMMITTED reserves auto-increment identities across connections"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let first = create 1 store
              let first, _ = handle first "CREATE TABLE tx_rc_auto_concurrent (id INT AUTO_INCREMENT PRIMARY KEY, note VARCHAR(20))"
              let first, _ = handle first "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"
              let first, _ = handle first "BEGIN"
              let first, inserted = handle first "INSERT INTO tx_rc_auto_concurrent(note) VALUES ('private')"
              Expect.equal inserted (Affected 1UL) "private insert"

              let second = create 2 store
              let second, inserted = handle second "INSERT INTO tx_rc_auto_concurrent(note) VALUES ('committed')"
              Expect.equal inserted (Affected 1UL) "concurrent insert"

              match handle first "SELECT id, note FROM tx_rc_auto_concurrent ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "private" ]; [ Some "2"; Some "committed" ] ]) -> ()
              | result -> failtestf "expected both reserved identities, got %A" result

          testCase "transactions reserve distinct auto-increment identities from one snapshot"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_auto_reservations (id INT AUTO_INCREMENT PRIMARY KEY, note VARCHAR(20) UNIQUE)"

              let prepared =
                  [| 1 .. 8 |]
                  |> Array.map (fun id ->
                      let baseCatalog, snapshot = Fsdb.Storage.beginTransactionSnapshotWithBase store

                      match
                          Fsdb.Storage.insertRows
                              snapshot
                              Fsdb.Storage.defaultDatabase
                              "tx_auto_reservations"
                              (Some [ "note" ])
                              [ [ VString(sprintf "row-%d" id) ] ]
                      with
                      | Ok _ -> baseCatalog, snapshot
                      | Error error -> failtestf "expected transaction %d to insert privately, got %A" id error)

              prepared
              |> Array.iter (fun (_, snapshot) -> Fsdb.Storage.bumpAutoIncrementsInto store snapshot.Catalog)

              prepared
              |> Array.iter (fun (baseCatalog, snapshot) -> Fsdb.Storage.commitCatalogInto store baseCatalog snapshot)

              match handle setup "SELECT id, note FROM tx_auto_reservations ORDER BY id" |> snd with
              | ResultSet(_, rows) ->
                  let expected =
                      [ for id in 1 .. prepared.Length -> [ Some(string id); Some(sprintf "row-%d" id) ] ]

                  Expect.equal rows expected "each transaction retains its reserved identity"
              | result -> failtestf "expected committed auto-increment rows, got %A" result

          testCase "READ COMMITTED savepoint rollback retains concurrent committed rows"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let writer = create 1 store
              let writer, _ = handle writer "CREATE TABLE tx_rc_savepoint (id INT PRIMARY KEY)"
              let writer, _ = handle writer "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED"
              let writer, _ = handle writer "BEGIN"
              let writer, _ = handle writer "SELECT id FROM tx_rc_savepoint"
              let writer, saved = handle writer "SAVEPOINT before_write"
              Expect.equal saved (Affected 0UL) "the savepoint is created"
              let writer, inserted = handle writer "INSERT INTO tx_rc_savepoint VALUES (1)"
              Expect.equal inserted (Affected 1UL) "the private row is inserted"

              let other = create 2 store
              let other, external = handle other "INSERT INTO tx_rc_savepoint VALUES (2)"
              Expect.equal external (Affected 1UL) "the concurrent row commits"

              let writer, rolledBack = handle writer "ROLLBACK TO SAVEPOINT before_write"
              Expect.equal rolledBack (Affected 0UL) "the private write is rolled back"

              match handle writer "SELECT id FROM tx_rc_savepoint ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected only the concurrent committed row, got %A" result

              let writer, committed = handle writer "COMMIT"
              Expect.equal committed (Affected 0UL) "the transaction commits"

              match handle other "SELECT id FROM tx_rc_savepoint ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected the concurrent row after commit, got %A" result

          testCase "SERIALIZABLE rejects write skew after a concurrent commit"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let first = create 1 store
              let first, _ = handle first "CREATE TABLE tx_serial (id INT PRIMARY KEY)"
              let first, _ = handle first "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE"
              let second = create 2 store
              let second, _ = handle second "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE"
              let first, _ = handle first "BEGIN"
              let second, _ = handle second "BEGIN"
              let first, _ = handle first "SELECT COUNT(*) FROM tx_serial"
              let second, _ = handle second "SELECT COUNT(*) FROM tx_serial"
              let first, _ = handle first "INSERT INTO tx_serial VALUES (1)"
              let second, _ = handle second "INSERT INTO tx_serial VALUES (2)"
              let _, committed = handle first "COMMIT"
              Expect.equal committed (Affected 0UL) "the first transaction commits"

              match handle second "COMMIT" |> snd with
              | Err(1205, _) -> ()
              | result -> failtestf "expected the stale serializable transaction to fail, got %A" result

              let observer = create 3 store

              match handle observer "SELECT id FROM tx_serial ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected only the serialized commit, got %A" result

          testCase "SERIALIZABLE read-only snapshots do not conflict with later writes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let reader = create 1 store
              let reader, _ = handle reader "CREATE TABLE tx_serial_read (id INT PRIMARY KEY)"
              let reader, _ = handle reader "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE"
              let reader, _ = handle reader "BEGIN"
              let reader, _ = handle reader "SELECT id FROM tx_serial_read"
              let writer = create 2 store
              let _, inserted = handle writer "INSERT INTO tx_serial_read VALUES (1)"
              Expect.equal inserted (Affected 1UL) "the concurrent write commits"

              match handle reader "SELECT id FROM tx_serial_read" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected the repeatable serializable snapshot, got %A" result

              match handle reader "COMMIT" |> snd with
              | Affected 0UL -> ()
              | result -> failtestf "expected the read-only transaction to commit, got %A" result

          testCase "ROLLBACK discards writes made inside the transaction"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_r (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_r VALUES (1)"
              let session, _ = handle session "ROLLBACK"

              match handle session "SELECT id FROM tx_r" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected the insert to be rolled back, got %A" result

          testCase "ROLLBACK restores rows deleted and inserted by REPLACE"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_replace (id INT PRIMARY KEY, u INT UNIQUE)"
              let session, _ = handle session "INSERT INTO tx_replace VALUES (1, 10), (2, 20)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "REPLACE INTO tx_replace VALUES (1, 20)"
              let session, _ = handle session "ROLLBACK"

              match handle session "SELECT id, u FROM tx_replace ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "10" ]; [ Some "2"; Some "20" ] ]) -> ()
              | result -> failtestf "expected both original rows after rollback, got %A" result

          testCase "SAVEPOINT / ROLLBACK TO SAVEPOINT undoes only the writes made after it"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_s (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_s VALUES (1)"

              let rollbackWork session =
                  session.Tx
                  |> Option.map (fun transaction -> Fsdb.Storage.transactionRollbackWork transaction.Snapshot)
                  |> Option.defaultValue 0L

              Expect.equal (rollbackWork session) 1L "the first insert contributes one rollback row"
              let session, _ = handle session "SAVEPOINT sp1"
              let session, _ = handle session "INSERT INTO tx_s VALUES (2)"
              Expect.equal (rollbackWork session) 2L "the post-savepoint insert contributes another rollback row"
              let session, _ = handle session "ROLLBACK TO SAVEPOINT sp1"
              Expect.equal (rollbackWork session) 1L "rolling back to the savepoint restores its rollback cost"
              let session, _ = handle session "COMMIT"

              match handle session "SELECT id FROM tx_s ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected only the pre-savepoint row, got %A" result

          testCase "SET autocommit = 0 starts a transaction on the first table statement; SET autocommit = 1 commits it"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_ac (id INT)"
              let session, _ = handle session "SET autocommit = 0"
              Expect.isNone session.Tx "changing the mode alone does not enter a transaction"
              let session, _ = handle session "INSERT INTO tx_ac VALUES (1)"
              Expect.isSome session.Tx "the first table write enters a transaction"
              let other = create 2 store

              match handle other "SELECT id FROM tx_ac" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected no rows visible before autocommit = 1, got %A" result

              let session, _ = handle session "SET autocommit = 1"
              ignore session

              match handle other "SELECT id FROM tx_ac" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the row visible once autocommit = 1 commits it, got %A" result

          testCase "DDL commits an active transaction before executing"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_ddl_rows (id INT PRIMARY KEY)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_ddl_rows VALUES (1)"
              let session, createResult = handle session "CREATE TABLE tx_ddl_schema (id INT PRIMARY KEY)"

              Expect.equal createResult (Affected 0UL) "the DDL succeeds in its own transaction"
              Expect.isNone session.Tx "the DDL leaves no active transaction"
              let session, _ = handle session "ROLLBACK"

              match handle session "SELECT id FROM tx_ddl_rows" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the pre-DDL row to remain committed, got %A" result

          testCase "failed DDL still commits the preceding transaction"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_failed_ddl (id INT PRIMARY KEY)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_failed_ddl VALUES (1)"
              let session, result = handle session "CREATE TABLE tx_failed_ddl (other INT)"

              match result with
              | Err(1050, _) -> ()
              | other -> failtestf "expected the duplicate table error, got %A" other

              Expect.isNone session.Tx "the failed DDL does not restore the old transaction"

              match handle session "SELECT id FROM tx_failed_ddl" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the pre-DDL write to remain committed, got %A" other

          testCase "autocommit zero starts a fresh transaction after DDL"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_ac_ddl (id INT PRIMARY KEY)"
              let session, _ = handle session "SET autocommit = 0"
              let session, _ = handle session "INSERT INTO tx_ac_ddl VALUES (1)"
              let session, _ = handle session "CREATE TABLE tx_ac_schema (id INT PRIMARY KEY)"
              Expect.isNone session.Tx "DDL ends the first implicit transaction"
              let session, _ = handle session "INSERT INTO tx_ac_ddl VALUES (2)"
              Expect.isSome session.Tx "the next write starts a fresh implicit transaction"
              let session, _ = handle session "ROLLBACK"

              match handle session "SELECT id FROM tx_ac_ddl ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected only the write committed by DDL, got %A" result

          testCase "temporary table DDL does not commit an active transaction"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_temp_rows (id INT PRIMARY KEY)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_temp_rows VALUES (1)"
              let session, result = handle session "CREATE TEMPORARY TABLE tx_temp_schema (id INT PRIMARY KEY)"
              Expect.equal result (Affected 0UL) "temporary DDL succeeds"
              Expect.isSome session.Tx "temporary DDL leaves the transaction active"
              let session, _ = handle session "ROLLBACK"

              match handle session "SELECT id FROM tx_temp_rows" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected the permanent row to roll back, got %A" result

              match handle session "SELECT COUNT(*) FROM tx_temp_schema" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | result -> failtestf "expected the temporary table to survive rollback, got %A" result

          testCase "RELEASE SAVEPOINT on an unknown savepoint is a 1305 error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "BEGIN"

              match handle session "RELEASE SAVEPOINT nope" |> snd with
              | Err(1305, _) -> ()
              | result -> failtestf "expected a 1305 error, got %A" result

          testCase "COMMIT doesn't discard a concurrent write another connection made to a different table"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_m (id INT)"
              let session, _ = handle session "CREATE TABLE tx_other (id INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_m VALUES (1)"

              let other = create 2 store
              use otherStarted = new Threading.ManualResetEventSlim(false)

              let otherInsert =
                  Threading.Tasks.Task.Run(fun () ->
                      otherStarted.Set()
                      handle other "INSERT INTO tx_other VALUES (99)")

              Expect.isTrue (otherStarted.Wait(TimeSpan.FromSeconds 1.0)) "the concurrent different-table writer started"

              Expect.isTrue
                  (otherInsert.Wait(TimeSpan.FromSeconds 5.0))
                  "the disjoint-table write completes before the transaction commits"

              let other, otherResult = otherInsert.GetAwaiter().GetResult()

              match otherResult with
              | Affected 1UL -> ()
              | result -> failtestf "expected the concurrent insert into a different table to succeed, got %A" result

              let session, commitResult = handle session "COMMIT"

              match commitResult with
              | Affected 0UL -> ()
              | result -> failtestf "expected the disjoint transaction to commit, got %A" result

              match handle other "SELECT id FROM tx_other" |> snd with
              | ResultSet(_, [ [ Some "99" ] ]) -> ()
              | result -> failtestf "expected the concurrent write to survive the commit, got %A" result

              match handle other "SELECT id FROM tx_m" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the transaction's own write to also be there, got %A" result

          testCase "concurrent transactions preserve tables created by other transactions"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let first, _ = handle (create 1 store) "BEGIN"
              let second, _ = handle (create 2 store) "BEGIN"
              let first, firstCreate = handle first "CREATE TABLE tx_created_first (id INT PRIMARY KEY)"
              let second, secondCreate = handle second "CREATE TABLE tx_created_second (id INT PRIMARY KEY)"

              Expect.equal firstCreate (Affected 0UL) "the first private catalog accepts its table"
              Expect.equal secondCreate (Affected 0UL) "the second private catalog accepts its table"
              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first catalog commits"
              Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the disjoint second catalog also commits"

              match handle (create 3 store) "SHOW TABLES" |> snd with
              | ResultSet(_, rows) ->
                  let names = rows |> List.choose List.tryHead |> List.choose id |> Set.ofList
                  Expect.isTrue (Set.contains "tx_created_first" names) "the first table remains visible"
                  Expect.isTrue (Set.contains "tx_created_second" names) "the second table is published"
              | result -> failtestf "expected both committed tables, got %A" result

          testCase "a qualified cross-database transaction merges a disjoint concurrent row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              Fsdb.Storage.createDatabase store "tx_db_x" |> ignore
              Fsdb.Storage.createDatabase store "tx_db_y" |> ignore

              let a = create 1 store
              let a, _ = handle a "USE tx_db_x"
              let a, _ = handle a "CREATE TABLE tx_db_y.t (id INT PRIMARY KEY)"
              let a, _ = handle a "BEGIN"
              let a, _ = handle a "INSERT INTO tx_db_y.t VALUES (1)"

              let b = create 2 store
              let b, _ = handle b "USE tx_db_y"
              use otherStarted = new Threading.ManualResetEventSlim(false)

              let bInsert =
                  Threading.Tasks.Task.Run(fun () ->
                      otherStarted.Set()
                      handle b "INSERT INTO t VALUES (2)")

              Expect.isTrue (otherStarted.Wait(TimeSpan.FromSeconds 1.0)) "the concurrent writer to tx_db_y started"

              Expect.isTrue
                  (bInsert.Wait(TimeSpan.FromSeconds 5.0))
                  "the disjoint row write completes while the transaction remains open"

              match bInsert.GetAwaiter().GetResult() |> snd with
              | Affected 1UL -> ()
              | result -> failtestf "expected the concurrent insert to succeed, got %A" result

              let _, commitResult = handle a "COMMIT"

              match commitResult with
              | Affected 0UL -> ()
              | result -> failtestf "expected the disjoint row to merge at commit, got %A" result

              match handle b "SELECT id FROM t ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ]; [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both the transaction's row and the concurrent row to survive — neither lost, got %A" result

          testCase "an open transaction in one database doesn't block a write to an unrelated database"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              Fsdb.Storage.createDatabase store "tx_db_a" |> ignore
              Fsdb.Storage.createDatabase store "tx_db_b" |> ignore

              let a = create 1 store
              let a, _ = handle a "USE tx_db_a"
              let a, _ = handle a "CREATE TABLE t (id INT)"
              let a, _ = handle a "BEGIN"
              let a, _ = handle a "INSERT INTO t VALUES (1)"

              let b = create 2 store
              let b, _ = handle b "USE tx_db_b"
              let b, _ = handle b "CREATE TABLE t (id INT)"

              let bInsert = Threading.Tasks.Task.Run(fun () -> handle b "INSERT INTO t VALUES (99)")

              Expect.isTrue
                  (bInsert.Wait(TimeSpan.FromSeconds 5.0))
                  "the unrelated database's write completed without waiting for tx_db_a's still-open transaction"

              match bInsert.GetAwaiter().GetResult() |> snd with
              | Affected 1UL -> ()
              | result -> failtestf "expected the unrelated database's insert to succeed, got %A" result

              handle a "ROLLBACK" |> ignore

          testCase "row locks in separate databases use separate namespaces"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let tableName = "t"

              let legacyStripe databaseName =
                  HashCode.Combine(
                      StringComparer.OrdinalIgnoreCase.GetHashCode databaseName,
                      StringComparer.OrdinalIgnoreCase.GetHashCode tableName
                  )
                  &&& Int32.MaxValue
                  |> fun value -> value % 4096

              let firstDatabase, secondDatabase =
                  seq { 0 .. 10000 }
                  |> Seq.map (sprintf "tx_lock_db_%d")
                  |> Seq.groupBy legacyStripe
                  |> Seq.map (snd >> Seq.truncate 2 >> Seq.toList)
                  |> Seq.find (fun names -> names.Length = 2)
                  |> function
                      | [ first; second ] -> first, second
                      | _ -> failwith "expected a pair of database names"

              for databaseName in [ firstDatabase; secondDatabase ] do
                  Fsdb.Storage.createDatabase store databaseName |> ignore
                  let setup = create 1 store
                  let setup, _ = handle setup $"USE {databaseName}"
                  let setup, _ = handle setup "CREATE TABLE t (id INT PRIMARY KEY, n INT)"
                  handle setup "INSERT INTO t VALUES (1, 0)" |> ignore

              let first = create 2 store
              let first, _ = handle first $"USE {firstDatabase}"
              let first, _ = handle first "BEGIN"
              let first, firstUpdate = handle first "UPDATE t SET n = n + 1 WHERE id = 1"
              Expect.equal firstUpdate (Affected 1UL) "the first database claims its row"

              let second = create 3 store
              let second, _ = handle second $"USE {secondDatabase}"
              let second, _ = handle second "SET innodb_lock_wait_timeout = 1"
              let second, _ = handle second "BEGIN"
              let second, secondUpdate = handle second "UPDATE t SET n = n + 1 WHERE id = 1"
              Expect.equal secondUpdate (Affected 1UL) "an unrelated database does not share the first database's row lock"

              handle second "ROLLBACK" |> ignore
              handle first "ROLLBACK" |> ignore

          testCase "a cancelled transaction statement leaves later transactions usable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_cancel (id INT PRIMARY KEY, v INT)"
              let session, _ = handle session "INSERT INTO tx_cancel VALUES (1, 1)"
              let session, _ = handle session "BEGIN"

              use cts = new Threading.CancellationTokenSource()
              cts.Cancel()
              Fsdb.Storage.queryCancellation.Value <- cts.Token

              let threw =
                  try
                      handle session "UPDATE tx_cancel SET v = v + 1 WHERE id = 1" |> ignore
                      false
                  with :? OperationCanceledException ->
                      true

              Fsdb.Storage.queryCancellation.Value <- Threading.CancellationToken.None
              Expect.isTrue threw "expected the cancelled statement to throw OperationCanceledException"

              let fresh = create 2 store
              let fresh, _ = handle fresh "BEGIN"

              match handle fresh "UPDATE tx_cancel SET v = v + 1 WHERE id = 1" |> snd with
              | Affected 1UL -> ()
              | result -> failtestf "expected a later transaction to remain usable, got %A" result

          testCase "an exception on a transaction's second statement aborts the whole transaction"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_abort (id INT PRIMARY KEY, v DECIMAL(10,2))"
              let session, _ = handle session "INSERT INTO tx_abort VALUES (1, 1)"
              let session, _ = handle session "BEGIN"
              // The first write must be discarded if the next statement
              // aborts the transaction.
              let session, firstResult = handle session "UPDATE tx_abort SET v = 2 WHERE id = 1"

              match firstResult with
              | Affected 1UL -> ()
              | other -> failtestf "expected the first statement to succeed, got %A" other

              // Second statement: an out-of-range DECIMAL literal throws
              // `OverflowException` straight out of `Storage.coerceValue`'s
              // numeric cast (see `QueryHandler.handle`'s own doc comment) —
              // a genuine internal error, not a normal `StorageError` reply.
              let session, secondResult = handle session "INSERT INTO tx_abort VALUES (2, 1e300)"

              match secondResult with
              | Err(1105, _) -> ()
              | other -> failtestf "expected the overflow to surface as a 1105 internal error, got %A" other

              Expect.isTrue session.Tx.IsNone "expected the broken statement to abort the whole transaction"

              // A stray COMMIT against the now-transactionless session must
              // be the no-op real MySQL gives after a fatal statement error
              // — not a merge of the aborted transaction's stale snapshot.
              let session, _ = handle session "COMMIT"
              ignore session

          testCase "a transaction can retry after its row wait times out"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let first = create 1 store
              let first, _ = handle first "CREATE TABLE tx_hot (id INT PRIMARY KEY, n INT)"
              let first, _ = handle first "INSERT INTO tx_hot VALUES (1, 0)"
              let first, _ = handle first "BEGIN"
              let second, _ = handle (create 2 store) "SET innodb_lock_wait_timeout = 1"
              let second, _ = handle second "BEGIN"
              let first, firstUpdate = handle first "UPDATE tx_hot SET n = n + 1 WHERE id = 1"

              match firstUpdate with
              | Affected 1UL -> ()
              | result -> failtestf "expected the first increment to succeed, got %A" result

              let second, timedOut = handle second "UPDATE tx_hot SET n = n + 1 WHERE id = 1"

              match timedOut with
              | Err(1205, _) -> ()
              | result -> failtestf "expected the row wait to time out, got %A" result

              let _, firstCommit = handle first "COMMIT"
              Expect.equal firstCommit (Affected 0UL) "the first writer commits"

              let second, secondUpdate = handle second "UPDATE tx_hot SET n = n + 1 WHERE id = 1"
              Expect.equal secondUpdate (Affected 1UL) "the timed-out transaction can retry"
              Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the retry commits"

              match handle (create 3 store) "SELECT n FROM tx_hot WHERE id = 1" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both serialized increments, got %A" result

          testCase "a row-lock cycle returns 1213 and rolls back the victim"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup, _ = handle (create 1 store) "CREATE TABLE tx_deadlock (id INT PRIMARY KEY, n INT)"
              let _, _ = handle setup "INSERT INTO tx_deadlock VALUES (1, 0), (2, 0)"
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 1"
              let second, _ = handle second "BEGIN"
              let first, _ = handle first "UPDATE tx_deadlock SET n = 1 WHERE id = 1"
              let second, _ = handle second "UPDATE tx_deadlock SET n = 2 WHERE id = 2"

              let firstWaiting =
                  Threading.Tasks.Task.Run(fun () ->
                      handle first "UPDATE tx_deadlock SET n = 1 WHERE id = 2")

              Expect.isFalse (firstWaiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the first transaction waits for row two"
              let second, deadlock = handle second "UPDATE tx_deadlock SET n = 2 WHERE id = 1"

              if second.Tx.IsSome then
                  handle second "ROLLBACK" |> ignore

              Expect.isTrue (firstWaiting.Wait(TimeSpan.FromSeconds 2.0)) "releasing the victim lets the survivor continue"
              let first, firstResult = firstWaiting.Result
              Expect.equal firstResult (Affected 1UL) "the surviving transaction acquires row two"
              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the survivor commits"

              match Fsdb.Executor.errorInfo deadlock with
              | Some error when error.Code = 1213 ->
                  Expect.equal error.State "40001" "deadlocks use MySQL's transaction-rollback SQLSTATE"
                  Expect.equal error.Message "Deadlock found when trying to get lock; try restarting transaction" "deadlock message"
                  Expect.isNone second.Tx "the deadlock victim transaction is gone"
              | _ -> failtestf "expected a 1213 deadlock victim, got %A" deadlock

          testCase "deadlock detection chooses the transaction with less row ownership"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup, _ = handle (create 1 store) "CREATE TABLE tx_deadlock_cost (id INT PRIMARY KEY, n INT)"
              let _, _ = handle setup "INSERT INTO tx_deadlock_cost VALUES (1, 0), (2, 0), (3, 0), (4, 0)"
              let smaller, _ = handle (create 2 store) "BEGIN"
              let larger, _ = handle (create 3 store) "BEGIN"
              let smaller, _ = handle smaller "UPDATE tx_deadlock_cost SET n = 1 WHERE id = 1"
              let larger, _ = handle larger "UPDATE tx_deadlock_cost SET n = 2 WHERE id = 2"
              let larger, _ = handle larger "UPDATE tx_deadlock_cost SET n = 2 WHERE id = 3"
              let larger, _ = handle larger "UPDATE tx_deadlock_cost SET n = 2 WHERE id = 4"

              let smallerWaiting =
                  Threading.Tasks.Task.Run(fun () ->
                      handle smaller "UPDATE tx_deadlock_cost SET n = 1 WHERE id = 2")

              Expect.isFalse (smallerWaiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the smaller transaction waits for row two"
              let larger, largerResult = handle larger "UPDATE tx_deadlock_cost SET n = 2 WHERE id = 1"
              Expect.isTrue (smallerWaiting.Wait(TimeSpan.FromSeconds 2.0)) "the selected victim releases its row"
              let smaller, smallerResult = smallerWaiting.Result

              if smaller.Tx.IsSome then
                  handle smaller "ROLLBACK" |> ignore

              if larger.Tx.IsSome then
                  handle larger "ROLLBACK" |> ignore

              match Fsdb.Executor.errorInfo smallerResult with
              | Some error ->
                  Expect.equal error.Code 1213 "the smaller transaction is the victim"
                  Expect.equal error.State "40001" "the selected victim keeps the deadlock SQLSTATE"
                  Expect.isNone smaller.Tx "the selected victim is rolled back"
              | None -> failtestf "expected the smaller transaction to deadlock, got %A" smallerResult

              Expect.equal largerResult (Affected 1UL) "the larger transaction survives and acquires row one"

          testCase "deadlock victim cost counts changed rows rather than lock stripes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup, _ = handle (create 1 store) "CREATE TABLE tx_deadlock_collision (id INT PRIMARY KEY, n INT)"

              let seedRows =
                  [ for id in 1L .. 4097L -> [ VInt id; VInt 0L ] ]

              match
                  Fsdb.Storage.insertRows
                      store
                      Fsdb.Storage.defaultDatabase
                      "tx_deadlock_collision"
                      None
                      seedRows
              with
              | Ok outcome -> Expect.equal outcome.Affected 4097 "all collision rows are seeded"
              | Error error -> failtestf "expected collision rows to insert, got %A" error

              let smaller, _ = handle (create 2 store) "BEGIN"
              let larger, _ = handle (create 3 store) "BEGIN"
              let smaller, _ = handle smaller "UPDATE tx_deadlock_collision SET n = 1 WHERE id = 2"

              let larger, largerUpdate =
                  handle larger "UPDATE tx_deadlock_collision SET n = 2 WHERE id IN (1, 4097)"

              Expect.equal largerUpdate (Affected 2UL) "the larger transaction changes two rows on one lock stripe"

              let smallerWaiting =
                  Threading.Tasks.Task.Run(fun () ->
                      handle smaller "UPDATE tx_deadlock_collision SET n = 1 WHERE id = 1")

              Expect.isFalse (smallerWaiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the smaller transaction waits"
              let larger, largerResult = handle larger "UPDATE tx_deadlock_collision SET n = 2 WHERE id = 2"
              Expect.isTrue (smallerWaiting.Wait(TimeSpan.FromSeconds 2.0)) "the selected victim releases its row"
              let smaller, smallerResult = smallerWaiting.Result

              if larger.Tx.IsSome then
                  handle larger "ROLLBACK" |> ignore

              match Fsdb.Executor.errorInfo smallerResult with
              | Some error ->
                  Expect.equal error.Code 1213 "the one-row transaction is the victim"
                  Expect.equal error.State "40001" "the selected victim keeps the deadlock SQLSTATE"
                  Expect.isNone smaller.Tx "the victim transaction is rolled back"
              | None -> failtestf "expected the smaller transaction to deadlock, got %A" smallerResult

              Expect.equal largerResult (Affected 1UL) "the two-row transaction survives despite sharing one stripe"

          testCase "deadlock detection follows cycles longer than two transactions"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup, _ = handle (create 1 store) "CREATE TABLE tx_deadlock_three (id INT PRIMARY KEY, n INT)"
              let _, _ = handle setup "INSERT INTO tx_deadlock_three VALUES (1, 0), (2, 0), (3, 0)"
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "BEGIN"
              let third, _ = handle (create 4 store) "BEGIN"
              let first, _ = handle first "UPDATE tx_deadlock_three SET n = 1 WHERE id = 1"
              let second, _ = handle second "UPDATE tx_deadlock_three SET n = 2 WHERE id = 2"
              let third, _ = handle third "UPDATE tx_deadlock_three SET n = 3 WHERE id = 3"

              let firstWaiting =
                  Threading.Tasks.Task.Run(fun () ->
                      handle first "UPDATE tx_deadlock_three SET n = 1 WHERE id = 2")

              Expect.isFalse (firstWaiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the first transaction waits for the second"

              let secondWaiting =
                  Threading.Tasks.Task.Run(fun () ->
                      handle second "UPDATE tx_deadlock_three SET n = 2 WHERE id = 3")

              Expect.isFalse (secondWaiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the second transaction waits for the third"
              let third, thirdResult = handle third "UPDATE tx_deadlock_three SET n = 3 WHERE id = 1"

              match Fsdb.Executor.errorInfo thirdResult with
              | Some error -> Expect.equal error.Code 1213 "the newest equal-cost transaction closes and loses the cycle"
              | None -> failtestf "expected the third transaction to deadlock, got %A" thirdResult

              Expect.isNone third.Tx "the third transaction is rolled back"
              Expect.isTrue (secondWaiting.Wait(TimeSpan.FromSeconds 2.0)) "the second transaction continues after row three is released"
              let second, secondResult = secondWaiting.Result
              Expect.equal secondResult (Affected 1UL) "the second transaction acquires row three"
              Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the second transaction commits"
              Expect.isTrue (firstWaiting.Wait(TimeSpan.FromSeconds 2.0)) "the first transaction continues after row two is released"
              let first, firstResult = firstWaiting.Result
              Expect.equal firstResult (Affected 1UL) "the first transaction acquires row two"
              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first transaction commits"

          testCase "a timed-out multi-row wait releases its partial claims"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_hot (id INT PRIMARY KEY, n INT)"
              let _, _ = handle setup "INSERT INTO tx_hot VALUES (1, 0), (2, 0)"

              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, _ = handle owner "UPDATE tx_hot SET n = n + 1 WHERE id = 2"
              let waiter, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 1"
              let waiter, _ = handle waiter "BEGIN"

              match handle waiter "UPDATE tx_hot SET n = n + 1 WHERE id >= 1 AND id <= 2" |> snd with
              | Err(1205, _) -> ()
              | result -> failtestf "expected the second row claim to time out, got %A" result

              let independent, _ = handle (create 4 store) "BEGIN"

              match handle independent "UPDATE tx_hot SET n = n + 1 WHERE id = 1" with
              | independent, Affected 1UL ->
                  Expect.equal (handle independent "COMMIT" |> snd) (Affected 0UL) "the released row claim commits"
              | _, result -> failtestf "expected the partial claim on row one to be released, got %A" result

              handle waiter "ROLLBACK" |> ignore
              handle owner "ROLLBACK" |> ignore

          testCase "literal IN updates claim every indexed row before execution"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup, _ = handle (create 1 store) "CREATE TABLE tx_in_claims (id INT PRIMARY KEY, n INT)"
              let _, _ = handle setup "INSERT INTO tx_in_claims VALUES (1, 0), (2, 0), (3, 0)"
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, ownerUpdate = handle owner "UPDATE tx_in_claims SET n = 10 WHERE id = 2"
              Expect.equal ownerUpdate (Affected 1UL) "the owner claims row two"
              let waiter, _ = handle (create 3 store) "BEGIN"
              use started = new Threading.ManualResetEventSlim(false)

              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle waiter "UPDATE tx_in_claims SET n = n + 1 WHERE id IN (1, 2)")

              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the competing update starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              let ownerCommit = handle owner "COMMIT" |> snd
              Expect.equal ownerCommit (Affected 0UL) "the owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 2.0)) "the IN update continues after row two is released"
              let waiter, waiterUpdate = waiting.Result
              let waiterCommit = handle waiter "COMMIT" |> snd

              Expect.isTrue blocked "the IN update waits before reading any claimed row"
              Expect.equal waiterUpdate (Affected 2UL) "both listed rows are updated"
              Expect.equal waiterCommit (Affected 0UL) "the serialized update commits"

              match handle (create 4 store) "SELECT id, n FROM tx_in_claims ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "1" ]; [ Some "2"; Some "11" ]; [ Some "3"; Some "0" ] ]) -> ()
              | result -> failtestf "expected the serialized literal-IN values, got %A" result

          testCase "concurrent transactions merge updates to different rows in the same table"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_rows (id INT PRIMARY KEY, n INT)"
              let setup, _ = handle setup "INSERT INTO tx_rows VALUES (1, 0), (2, 0)"
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "BEGIN"
              let first, firstUpdate = handle first "UPDATE tx_rows SET n = 10 WHERE id = 1"
              let second, secondUpdate = handle second "UPDATE tx_rows SET n = 20 WHERE id = 2"
              Expect.equal firstUpdate (Affected 1UL) "first snapshot updated its row"
              Expect.equal secondUpdate (Affected 1UL) "second snapshot updated its row"

              let firstCommit = Threading.Tasks.Task.Run(fun () -> handle first "COMMIT")
              let secondCommit = Threading.Tasks.Task.Run(fun () -> handle second "COMMIT")
              Threading.Tasks.Task.WaitAll [| firstCommit :> Threading.Tasks.Task; secondCommit :> Threading.Tasks.Task |]
              Expect.equal (firstCommit.Result |> snd) (Affected 0UL) "first commit succeeds"
              Expect.equal (secondCommit.Result |> snd) (Affected 0UL) "second commit succeeds"

              match handle setup "SELECT id, n FROM tx_rows ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "10" ]; [ Some "2"; Some "20" ] ]) -> ()
              | result -> failtestf "expected both disjoint updates to survive, got %A" result

          testCase "transaction locks on different tables never alias"
          <| fun _ ->
              let stripeCount = 4096

              let stripe name =
                  (StringComparer.OrdinalIgnoreCase.GetHashCode(name) &&& Int32.MaxValue) % stripeCount

              let firstTable, secondTable =
                  [ 0 .. stripeCount ]
                  |> Seq.map (sprintf "tx_lock_namespace_%d")
                  |> Seq.groupBy stripe
                  |> Seq.pick (fun (_, names) ->
                      match names |> Seq.truncate 2 |> Seq.toList with
                      | [ first; second ] -> Some(first, second)
                      | _ -> None)

              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup (sprintf "CREATE TABLE %s (id INT PRIMARY KEY)" firstTable)
              let _, _ = handle setup (sprintf "CREATE TABLE %s (id INT PRIMARY KEY)" secondTable)
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 1"
              let second, _ = handle second "BEGIN"
              let first, firstInsert = handle first (sprintf "INSERT INTO %s VALUES (1)" firstTable)
              let second, secondInsert = handle second (sprintf "INSERT INTO %s VALUES (1)" secondTable)

              Expect.equal firstInsert (Affected 1UL) "the first table accepts its key"
              Expect.equal secondInsert (Affected 1UL) "the unrelated table does not share the key lock"
              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first transaction commits"
              Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the second transaction commits"

          testCase "a disjoint transaction commit maintains indexes incrementally"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_indexed (id INT PRIMARY KEY, email VARCHAR(64) UNIQUE, category VARCHAR(20), n INT, KEY ix_category (category))"
              let setup, _ = handle setup "INSERT INTO tx_indexed VALUES (1, 'a@example.test', 'new', 0), (2, 'b@example.test', 'new', 0)"
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "BEGIN"
              let first, _ = handle first "UPDATE tx_indexed SET category = 'active', n = 10 WHERE id = 1"
              let second, _ = handle second "UPDATE tx_indexed SET category = 'archived', n = 20 WHERE id = 2"
              let _, firstCommit = handle first "COMMIT"
              Expect.equal firstCommit (Affected 0UL) "the first transaction commits"

              let reindexesBefore = Fsdb.Storage.reindexCallCount ()
              let _, secondCommit = handle second "COMMIT"
              Expect.equal secondCommit (Affected 0UL) "the stale disjoint transaction commits"
              Expect.equal (Fsdb.Storage.reindexCallCount ()) reindexesBefore "the merge preserves the incremental indexes"

              match handle setup "SELECT id, email, category, n FROM tx_indexed ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "a@example.test"; Some "active"; Some "10" ]; [ Some "2"; Some "b@example.test"; Some "archived"; Some "20" ] ]) -> ()
              | result -> failtestf "expected both indexed rows to remain queryable, got %A" result

              match handle setup "SELECT id FROM tx_indexed WHERE category = 'archived'" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected the merged secondary bucket, got %A" result

          testCase "concurrent transaction identity locks primary-key collations"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup, _ = handle (create 1 store) "CREATE TABLE tx_text_key (id VARCHAR(20) PRIMARY KEY)"
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 1"
              let second, _ = handle second "BEGIN"
              let first, firstInsert = handle first "INSERT INTO tx_text_key VALUES ('A')"
              let second, secondInsert = handle second "INSERT INTO tx_text_key VALUES ('a')"

              Expect.equal firstInsert (Affected 1UL) "the first spelling claims the collation key"

              match secondInsert with
              | Err(1205, _) -> ()
              | result -> failtestf "expected the collation-equivalent key to wait, got %A" result

              let _, firstCommit = handle first "COMMIT"
              Expect.equal firstCommit (Affected 0UL) "the first spelling commits"
              handle second "ROLLBACK" |> ignore

              match handle setup "SELECT id FROM tx_text_key" |> snd with
              | ResultSet(_, [ [ Some "A" ] ]) -> ()
              | result -> failtestf "expected only the first key to remain, got %A" result

          testCase "concurrent transactions cannot insert the same primary key"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_unique (id INT PRIMARY KEY)"
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 1"
              let second, _ = handle second "BEGIN"
              let first, firstInsert = handle first "INSERT INTO tx_unique VALUES (1)"
              let second, secondInsert = handle second "INSERT INTO tx_unique VALUES (1)"

              Expect.equal firstInsert (Affected 1UL) "the first transaction claims the key"

              match secondInsert with
              | Err(1205, _) -> ()
              | result -> failtestf "expected the second insert to wait for the key, got %A" result

              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first transaction commits"
              handle second "ROLLBACK" |> ignore

              match handle setup "SELECT id FROM tx_unique" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected one committed primary key, got %A" result

          testCase "ROLLBACK does not roll back an AUTO_INCREMENT counter, matching MySQL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_ai (id INT AUTO_INCREMENT PRIMARY KEY, v INT)"
              let session, _ = handle session "INSERT INTO tx_ai (v) VALUES (1)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_ai (v) VALUES (2)"
              let session, _ = handle session "INSERT INTO tx_ai (v) VALUES (3)"
              let session, _ = handle session "ROLLBACK"
              let session, _ = handle session "INSERT INTO tx_ai (v) VALUES (4)"

              match handle session "SELECT id, v FROM tx_ai ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "1" ]; [ Some "4"; Some "4" ] ]) -> ()
              | result -> failtestf "expected the id 1/4 rows MySQL 8.4 produces, got %A" result

          testCase "ROLLBACK TO SAVEPOINT does not roll back an AUTO_INCREMENT counter either, matching a full ROLLBACK"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_sp_ai (id INT AUTO_INCREMENT PRIMARY KEY, v INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO tx_sp_ai (v) VALUES (1)" // burns id 1
              let session, _ = handle session "SAVEPOINT sp1"
              let session, _ = handle session "INSERT INTO tx_sp_ai (v) VALUES (2)" // burns id 2
              let session, _ = handle session "ROLLBACK TO SAVEPOINT sp1" // undoes the row, not the burned id
              let session, _ = handle session "INSERT INTO tx_sp_ai (v) VALUES (3)"
              let session, _ = handle session "COMMIT"

              match handle session "SELECT id, v FROM tx_sp_ai ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "1" ]; [ Some "3"; Some "3" ] ]) -> ()
              | result -> failtestf "expected id 2 to stay burned across the savepoint rollback (rows 1 and 3, not 1 and 2), got %A" result

          testCase "ROLLBACK TO SAVEPOINT destroys every savepoint established after it, but not the named one"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "SAVEPOINT a"
              let session, _ = handle session "SAVEPOINT b"
              let session, _ = handle session "SAVEPOINT c"
              let session, _ = handle session "ROLLBACK TO SAVEPOINT a"

              // b and c were established after a, so both are gone.
              match handle session "RELEASE SAVEPOINT b" |> snd with
              | Err(1305, _) -> ()
              | other -> failtestf "expected b to have been dropped by the rollback, got %A" other

              // a itself survives a rollback to it.
              match handle session "ROLLBACK TO SAVEPOINT a" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected a to still exist after rolling back to itself, got %A" other

          testCase "RELEASE SAVEPOINT destroys every savepoint established after it, matching real MySQL's cascade"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "SAVEPOINT a"
              let session, _ = handle session "SAVEPOINT b"
              let session, _ = handle session "SAVEPOINT c"
              let session, _ = handle session "RELEASE SAVEPOINT a"

              match handle session "RELEASE SAVEPOINT b" |> snd with
              | Err(1305, _) -> ()
              | other -> failtestf "expected b to have been dropped along with a, got %A" other

              match handle session "RELEASE SAVEPOINT c" |> snd with
              | Err(1305, _) -> ()
              | other -> failtestf "expected c to have been dropped along with a, got %A" other

          testCase "re-issuing SAVEPOINT with an existing name moves it to the end of the establishment order"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "SAVEPOINT a"
              let session, _ = handle session "SAVEPOINT b"
              let session, _ = handle session "SAVEPOINT a" // re-establishes a after b

              // b was established before this second `a`, so releasing a
              // must not cascade-drop it.
              match handle session "RELEASE SAVEPOINT a" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected RELEASE SAVEPOINT a to succeed, got %A" other

              match handle session "RELEASE SAVEPOINT b" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected b to survive releasing the re-established a, got %A" other

          testCase "a read-only transaction doesn't block another connection's write"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE ro_t (id INT)"
              let setup, _ = handle setup "INSERT INTO ro_t VALUES (1)"

              let reader = create 2 store
              let reader, _ = handle reader "BEGIN"
              let reader, _ = handle reader "SELECT id FROM ro_t"

              let writer = Threading.Tasks.Task.Run(fun () -> handle (create 3 store) "INSERT INTO ro_t VALUES (2)")

              Expect.isTrue
                  (writer.Wait(TimeSpan.FromSeconds 5.0))
                  "a write must not wait on a transaction that has only read"

              match writer.GetAwaiter().GetResult() |> snd with
              | Affected 1UL -> ()
              | result -> failtestf "expected the concurrent insert to succeed, got %A" result

              match handle reader "SELECT id FROM ro_t" |> snd with
              | ResultSet(_, [ _ ]) -> ()
              | result -> failtestf "expected repeatable read to hide the concurrent insert, got %A" result

              handle reader "ROLLBACK" |> ignore
              ignore setup

          testCase "a stale transaction write conflicts with a concurrent commit"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE up_t (id INT, v INT)"
              let setup, _ = handle setup "INSERT INTO up_t VALUES (1, 1)"

              let a = create 2 store
              let a, _ = handle a "BEGIN"
              let a, _ = handle a "SELECT v FROM up_t"

              handle (create 3 store) "UPDATE up_t SET v = 1234 WHERE id = 1" |> ignore

              let a, updateResult = handle a "UPDATE up_t SET v = 7 WHERE id = 1"
              Expect.equal updateResult (Affected 1UL) "the snapshot update succeeds locally"

              match handle a "COMMIT" |> snd with
              | Err(1205, _) -> ()
              | result -> failtestf "expected the stale writer to conflict, got %A" result

              match handle (create 4 store) "SELECT v FROM up_t" |> snd with
              | ResultSet(_, [ [ Some "1234" ] ]) -> ()
              | result -> failtestf "expected the concurrent assignment to remain, got %A" result

              ignore setup ]
