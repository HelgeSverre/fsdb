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

let private runConcurrentInserts store writerCount insertSql =
    use ready = new Threading.CountdownEvent(writerCount)
    use start = new Threading.ManualResetEventSlim()

    let inserts =
        Array.init writerCount (fun index ->
            Threading.Tasks.Task.Factory.StartNew(
                (fun () ->
                    ready.Signal() |> ignore
                    start.Wait()
                    handle (create (index + 2) store) (insertSql index) |> snd),
                Threading.CancellationToken.None,
                Threading.Tasks.TaskCreationOptions.LongRunning,
                Threading.Tasks.TaskScheduler.Default
            ))

    Expect.isTrue (ready.Wait(TimeSpan.FromSeconds 5.)) "every writer reached the start barrier"
    start.Set()
    let completed = Threading.Tasks.Task.WhenAll inserts
    Expect.isTrue (completed.Wait(TimeSpan.FromSeconds 10.)) "every writer completes"
    inserts |> Array.map _.Result

let tests =
    testList
        "Transactions"
        [ testCase "transaction rebases retain concurrent table statistics refreshes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable writer = create 1 store
              let runWriter sql =
                  let next, result = handle writer sql
                  writer <- next
                  match result with
                  | Err(code, message) -> failtestf "%s failed: %d %s" sql code message
                  | _ -> ()

              runWriter "CREATE TABLE refreshed_stats (id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY)"
              runWriter "BEGIN"
              runWriter "INSERT INTO refreshed_stats VALUES (NULL)"

              let analyzer = create 2 store
              match handle analyzer "ANALYZE TABLE refreshed_stats" |> snd with
              | Err(code, message) -> failtestf "ANALYZE TABLE failed: %d %s" code message
              | _ -> ()

              let statistics () =
                  store.Catalog
                  |> Map.find Fsdb.Storage.defaultDatabase
                  |> Map.find "refreshed_stats"
                  |> _.Statistics

              let refreshed = statistics ()
              Expect.isSome refreshed "ANALYZE TABLE publishes statistics"
              runWriter "COMMIT"
              Expect.equal (statistics ()) refreshed "the transaction does not discard the concurrent refresh"

          testCase "foreign_key_checks=0 transactions keep inserting rows whose parents do not exist yet"
          <| fun _ ->
              let mutable session = create 1 (Fsdb.Storage.create ())
              let run sql =
                  let next, result = handle session sql
                  session <- next
                  result
              for sql in
                  [ "CREATE DATABASE fk_off"
                    "USE fk_off"
                    "SET foreign_key_checks = 0"
                    "CREATE TABLE parent (id BIGINT UNSIGNED PRIMARY KEY)"
                    "CREATE TABLE child (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY, parent_id BIGINT UNSIGNED NOT NULL, CONSTRAINT child_parent FOREIGN KEY (parent_id) REFERENCES parent (id))"
                    "BEGIN"
                    "INSERT INTO child VALUES (1, 10)"
                    "INSERT INTO child VALUES (2, 20)"
                    "INSERT INTO child (parent_id) VALUES (30)"
                    "COMMIT" ] do
                  match run sql with
                  | Err(code, message) -> failtestf "%s failed: %d %s" sql code message
                  | _ -> ()

              Expect.equal (run "SELECT id, parent_id FROM child ORDER BY id")
                  (ResultSet([ "id"; "parent_id" ], [ [ Some "1"; Some "10" ]; [ Some "2"; Some "20" ]; [ Some "3"; Some "30" ] ]))
                  "MySQL accepts orphaned children while checks are off"

          testCase "a parent deleted by another session still conflicts with a pending child insert"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable writer = create 1 store
              let runWriter sql =
                  let next, result = handle writer sql
                  writer <- next
                  result
              for sql in
                  [ "CREATE DATABASE fk_race"
                    "USE fk_race"
                    "CREATE TABLE parent (id BIGINT UNSIGNED PRIMARY KEY)"
                    "CREATE TABLE child (id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY, parent_id BIGINT UNSIGNED NOT NULL, CONSTRAINT child_parent FOREIGN KEY (parent_id) REFERENCES parent (id))"
                    "INSERT INTO parent VALUES (1), (2)"
                    "BEGIN"
                    "INSERT INTO child (parent_id) VALUES (1)" ] do
                  match runWriter sql with
                  | Err(code, message) -> failtestf "%s failed: %d %s" sql code message
                  | _ -> ()

              let deleter = create 2 store
              let deleter, _ = handle deleter "USE fk_race"
              match handle deleter "DELETE FROM parent WHERE id = 1" |> snd with
              | Err(code, message) -> failtestf "the parent delete failed: %d %s" code message
              | _ -> ()

              let outcomes = [ runWriter "INSERT INTO child (parent_id) VALUES (2)"; runWriter "COMMIT" ]
              Expect.isTrue
                  (outcomes |> List.exists (function Err(1205, _) -> true | _ -> false))
                  (sprintf "the orphaning merge is refused, got %A" outcomes)

          testCase "a long transaction and autocommit writers share one AUTO_INCREMENT sequence"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable writer = create 1 store
              let runWriter sql =
                  match handle writer sql with
                  | _, Err(code, message) -> failtestf "%s failed: %d %s" sql code message
                  | next, result ->
                      writer <- next
                      result

              for sql in
                  [ "CREATE DATABASE seq"
                    "USE seq"
                    "CREATE TABLE parent (id BIGINT UNSIGNED PRIMARY KEY)"
                    "INSERT INTO parent VALUES (1)"
                    "CREATE TABLE item (id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY, parent_id BIGINT UNSIGNED NOT NULL, label VARCHAR(10) NOT NULL, UNIQUE KEY (label), CONSTRAINT item_parent FOREIGN KEY (parent_id) REFERENCES parent (id))"
                    "BEGIN" ] do
                  runWriter sql |> ignore

              // Autocommit writers run beside the open transaction; a row-lock
              // stripe they share with it only delays them until COMMIT.
              let autocommitInsert connectionId label =
                  Threading.Tasks.Task.Run(fun () ->
                      let session, _ = handle (create connectionId store) "USE seq"
                      handle session (sprintf "INSERT INTO item (parent_id, label) VALUES (1, '%s')" label) |> snd)

              let others =
                  [ for index in 1 .. 30 do
                        runWriter (sprintf "INSERT INTO item (parent_id, label) VALUES (1, 'tx%d')" index) |> ignore
                        if index % 10 = 0 then
                            let task = autocommitInsert (index + 1) (sprintf "auto%d" index)
                            task.Wait(TimeSpan.FromMilliseconds 200.) |> ignore
                            yield task ]

              runWriter "COMMIT" |> ignore

              for task in others do
                  Expect.isTrue (task.Wait(TimeSpan.FromSeconds 30.)) "every autocommit insert finishes after COMMIT"
                  Expect.equal task.Result (Affected 1UL) "autocommit insert"

              let _, counts = handle writer "SELECT COUNT(*), COUNT(DISTINCT id), MAX(id) FROM item"
              Expect.equal counts (ResultSet([ "COUNT(*)"; "COUNT(DISTINCT id)"; "MAX(id)" ], [ [ Some "33"; Some "33"; Some "33" ] ])) "33 distinct sequential ids"

              let writerAfter, inserted = handle writer "INSERT INTO item (parent_id, label) VALUES (1, 'next')"
              Expect.equal inserted (Affected 1UL) "follow-up insert"
              let _, next = handle writerAfter "SELECT id FROM item WHERE label = 'next'"
              Expect.equal next (ResultSet([ "id" ], [ [ Some "34" ] ])) "the sequence continues after every writer"

          testCase "fulltext transaction reads exclude pending document replacements"
          <| fun _ ->
              for parser, oldTerm, newTerm in [ "", "orchard", "cobalt"; " WITH PARSER ngram", "生日", "中文" ] do
                  for mutation in [ $"INSERT INTO docs VALUES(4,'{oldTerm}')"; $"UPDATE docs SET body='{newTerm}' WHERE id=1"; $"UPDATE docs SET body='{oldTerm} changed' WHERE id=1" ] do
                      let store = Fsdb.Storage.create ()
                      let mutable session = create 1 store
                      let query sql =
                          let next, result = handle session sql
                          session <- next
                          result
                      query ("CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body)" + parser + ")") |> ignore
                      query ($"INSERT INTO docs VALUES(1,'{oldTerm}'),(2,'{oldTerm}'),(3,'{newTerm}')") |> ignore
                      query "BEGIN" |> ignore
                      query "SAVEPOINT original" |> ignore
                      query mutation |> ignore
                      let expected = if mutation.StartsWith "INSERT" then [ [ Some "1" ]; [ Some "2" ] ] else [ [ Some "2" ] ]
                      for mode in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE"; "WITH QUERY EXPANSION" ] do
                          match query ($"SELECT id FROM docs WHERE MATCH(body) AGAINST('{oldTerm}' {mode}) ORDER BY id") with
                          | ResultSet(_, rows) -> Expect.equal rows expected (mutation + " " + mode)
                          | other -> failtestf "MATCH query: %A" other
                      query "ROLLBACK TO original" |> ignore
                      match query ($"SELECT id FROM docs WHERE MATCH(body) AGAINST('{oldTerm}' IN BOOLEAN MODE) ORDER BY id") with
                      | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "savepoint restores documents"
                      | other -> failtestf "MATCH after rollback: %A" other
                      query "ROLLBACK" |> ignore

          testCase "repeatable read fulltext excludes concurrently replaced documents"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable writer = create 1 store
              let write sql =
                  let next, result = handle writer sql
                  writer <- next
                  result
              write "CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body))" |> ignore
              write "INSERT INTO docs VALUES(1,'orchard'),(2,'orchard'),(3,'cobalt')" |> ignore
              let reader, _ = handle (create 2 store) "BEGIN"
              let reader, _ = handle reader "SELECT * FROM docs"
              write "UPDATE docs SET body='cobalt' WHERE id=1" |> ignore
              match handle reader "SELECT id FROM docs WHERE MATCH(body) AGAINST('orchard' IN BOOLEAN MODE) ORDER BY id" |> snd with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "2" ] ] "old rows cannot retain superseded postings"
              | other -> failtestf "MATCH after concurrent update: %A" other
              handle reader "ROLLBACK" |> ignore

          testCase "fulltext projections share document visibility across indexes"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable session = create 1 store
              let query sql =
                  let next, result = handle session sql
                  session <- next
                  result
              query "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,other TEXT,extra INT,FULLTEXT KEY ft(body),FULLTEXT KEY other_ft(other))" |> ignore
              query "INSERT INTO docs VALUES(1,'orchard','forest',0),(2,'orchard','ocean',0),(3,'cobalt','ocean',0)" |> ignore
              query "BEGIN" |> ignore
              for mutation, expected in
                  [ "UPDATE docs SET extra=1 WHERE id=1", "1"
                    "UPDATE docs SET body=body WHERE id=1", "1"
                    "UPDATE docs SET body='cobalt' WHERE id=1", "0"
                    "UPDATE docs SET body='orchard' WHERE id=1", "0" ] do
                  query mutation |> ignore
                  match query "SELECT MATCH(other) AGAINST('forest' IN BOOLEAN MODE)>0 FROM docs WHERE id=1" with
                  | ResultSet(_, rows) -> Expect.equal rows [ [ Some expected ] ] mutation
                  | other -> failtestf "MATCH projection: %A" other
              query "ROLLBACK" |> ignore

          testCase "read uncommitted fulltext excludes another connection's pending text"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable writer = create 1 store
              let write sql =
                  let next, result = handle writer sql
                  writer <- next
                  result
              write "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body))" |> ignore
              write "INSERT INTO docs VALUES(1,'orchard'),(2,'orchard'),(3,'cobalt')" |> ignore
              write "BEGIN" |> ignore
              write "UPDATE docs SET body='cobalt' WHERE id=1" |> ignore
              let reader, _ = handle (create 2 store) "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED"
              let reader, _ = handle reader "BEGIN"
              match handle reader "SELECT body,MATCH(body) AGAINST('orchard' IN BOOLEAN MODE)>0,MATCH(body) AGAINST('cobalt' IN BOOLEAN MODE)>0 FROM docs WHERE id=1" |> snd with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "cobalt"; Some "0"; Some "0" ] ] "ordinary pending text has no searchable document"
              | other -> failtestf "dirty-read MATCH: %A" other
              handle reader "ROLLBACK" |> ignore
              write "ROLLBACK" |> ignore

          testCase "fulltext write predicates exclude pending document replacements"
          <| fun _ ->
              for parser, oldTerm, newTerm in [ "", "orchard", "cobalt"; " WITH PARSER ngram", "生日", "中文" ] do
                  for mutation, expected in
                      [ $"UPDATE docs SET id=id+10 WHERE MATCH(body) AGAINST('{oldTerm}' IN BOOLEAN MODE)", [ "1"; "3"; "12" ]
                        $"DELETE FROM docs WHERE MATCH(body) AGAINST('{newTerm}' IN BOOLEAN MODE)", [ "1"; "2" ] ] do
                      let store = Fsdb.Storage.create ()
                      let mutable session = create 1 store
                      let query sql =
                          let next, result = handle session sql
                          session <- next
                          result
                      query ("CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body)" + parser + ")") |> ignore
                      query ($"INSERT INTO docs VALUES(1,'{oldTerm}'),(2,'{oldTerm}'),(3,'{newTerm}')") |> ignore
                      query "BEGIN" |> ignore
                      query ($"UPDATE docs SET body='{newTerm} orange' WHERE id=1") |> ignore
                      query mutation |> ignore
                      match query "SELECT id FROM docs ORDER BY id" with
                      | ResultSet(_, rows) -> Expect.equal rows (expected |> List.map (fun id -> [ Some id ])) mutation
                      | other -> failtestf "rows after MATCH mutation: %A" other
                      query "ROLLBACK" |> ignore

          testCase "BEGIN defers the private catalog until the first database statement"
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

              let keyStripe id =
                  let targets =
                      Fsdb.Storage.tryInsertLockTargets
                          store
                          Fsdb.Storage.defaultDatabase
                          "tx_rc_auto"
                          (Some [ "id" ])
                          [ [ Some(VInt id) ] ]
                      |> Option.get

                  (StringComparer.Ordinal.GetHashCode(List.head targets.Keys) &&& Int32.MaxValue) % 4096

              // This case verifies a refresh over a disjoint write, while the
              // lock tests deliberately cover waits on a shared key stripe.
              let privateStripe = keyStripe 1L
              let externalId = [ 100L .. 200L ] |> List.find (fun id -> keyStripe id <> privateStripe)

              let other = create 2 store
              let other, external = handle other $"INSERT INTO tx_rc_auto(id, note) VALUES ({externalId}, 'external')"
              Expect.equal external (Affected 1UL) "the explicit concurrent row commits"

              match handle writer "SELECT id, note FROM tx_rc_auto ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "private" ]; [ Some id; Some "external" ] ]) when id = string externalId -> ()
              | result -> failtestf "expected the original private identity and external row, got %A" result

              let writer, committed = handle writer "COMMIT"
              Expect.equal committed (Affected 0UL) "the transaction commits"

              match handle other "SELECT id, note FROM tx_rc_auto ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "private" ]; [ Some id; Some "external" ] ]) when id = string externalId -> ()
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

          testCase "concurrent autocommit inserts publish every distinct row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_concurrent_insert (id INT AUTO_INCREMENT PRIMARY KEY, note VARCHAR(40) UNIQUE)"
              let writerCount = 16
              let inserts = runConcurrentInserts store writerCount (fun index -> $"INSERT INTO tx_concurrent_insert(note) VALUES ('row-{index}')")

              inserts
              |> Array.iter (fun result -> Expect.equal result (Affected 1UL) "each insert publishes one row")

              match handle setup "SELECT COUNT(*), COUNT(DISTINCT id), COUNT(DISTINCT note) FROM tx_concurrent_insert" |> snd with
              | ResultSet(_, [ [ count; distinctIds; distinctNotes ] ]) ->
                  let expected = Some(string writerCount)
                  Expect.equal count expected "all inserts are visible"
                  Expect.equal distinctIds expected "every insert receives a distinct identity"
                  Expect.equal distinctNotes expected "every insert retains its distinct value"
              | result -> failtestf "expected every concurrent insert exactly once, got %A" result

          testCase "concurrent keyless inserts retain every committed row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_keyless_insert (value INT NOT NULL)"
              let writerCount = 16
              let inserts = runConcurrentInserts store writerCount (fun index -> $"INSERT INTO tx_keyless_insert VALUES ({index})")
              inserts |> Array.iter (fun result -> Expect.equal result (Affected 1UL) "each keyless insert commits")

              match handle setup "SELECT COUNT(*), COUNT(DISTINCT value) FROM tx_keyless_insert" |> snd with
              | ResultSet(_, [ [ count; distinct ] ]) ->
                  let expected = Some(string writerCount)
                  Expect.equal count expected "all keyless inserts remain visible"
                  Expect.equal distinct expected "no keyless insert is duplicated"
              | result -> failtestf "expected every keyless insert exactly once, got %A" result

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

          testCase "an unseeded savepoint rollback does not replace the live catalog"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup, _ = handle (create 1 store) "CREATE TABLE tx_unseeded_savepoint (id INT PRIMARY KEY)"
              let transaction, _ = handle setup "BEGIN"
              let transaction, saved = handle transaction "SAVEPOINT before_view"
              Expect.equal saved (Affected 0UL) "the savepoint is created before a consistent view"

              match transaction.Tx with
              | Some tx -> Expect.isFalse tx.Seeded "SAVEPOINT does not establish the deferred consistent view"
              | None -> failtest "expected an open transaction"

              let other = create 2 store
              let other, inserted = handle other "INSERT INTO tx_unseeded_savepoint VALUES (1)"
              Expect.equal inserted (Affected 1UL) "the other session commits after the savepoint"

              let transaction, rolledBack = handle transaction "ROLLBACK TO SAVEPOINT before_view"
              Expect.equal rolledBack (Affected 0UL) "the unseeded rollback succeeds"

              match handle other "SELECT id FROM tx_unseeded_savepoint" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the concurrent commit to remain live, got %A" result

              match handle transaction "SELECT id FROM tx_unseeded_savepoint" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the first consistent read to include the earlier commit, got %A" result

          testCase "READ UNCOMMITTED rebases an unseeded savepoint before the first write"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session, _ = handle (create 1 store) "CREATE TABLE tx_ru_unseeded_savepoint (id INT PRIMARY KEY)"
              let session, _ = handle session "SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "SAVEPOINT before_write"
              let session, inserted = handle session "INSERT INTO tx_ru_unseeded_savepoint VALUES (1)"
              Expect.equal inserted (Affected 1UL) "the first database statement seeds and writes"
              let session, rolledBack = handle session "ROLLBACK TO SAVEPOINT before_write"
              Expect.equal rolledBack (Affected 0UL) "the rebased savepoint rolls the write back"
              let session, committed = handle session "COMMIT"
              Expect.equal committed (Affected 0UL) "the empty transaction commits"

              match handle session "SELECT id FROM tx_ru_unseeded_savepoint" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected the post-savepoint write to be absent, got %A" result

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

          testCase "cancellation interrupts a queued row-lock wait"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup, _ = handle (create 1 store) "CREATE TABLE tx_queued_cancel (id INT PRIMARY KEY, v INT)"
              let _, _ = handle setup "INSERT INTO tx_queued_cancel VALUES (1, 0)"
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, _ = handle owner "UPDATE tx_queued_cancel SET v = 1 WHERE id = 1"
              let waiter, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
              let waiter, _ = handle waiter "BEGIN"
              use cancellation = new Threading.CancellationTokenSource()
              use started = new Threading.ManualResetEventSlim(false)

              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      Fsdb.Storage.queryCancellation.Value <- cancellation.Token
                      started.Set()

                      try
                          try
                              Choice1Of2(handle waiter "UPDATE tx_queued_cancel SET v = 2 WHERE id = 1")
                          with :? OperationCanceledException ->
                              Choice2Of2()
                      finally
                          Fsdb.Storage.queryCancellation.Value <- Threading.CancellationToken.None)

              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the competing update starts"
              Threading.Thread.Sleep 100
              cancellation.Cancel()
              let cancelledWhileQueued = waiting.Wait(TimeSpan.FromSeconds 1.0)
              handle owner "ROLLBACK" |> ignore
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 2.0)) "the queued update terminates"

              match waiting.Result with
              | Choice2Of2() -> ()
              | Choice1Of2(waiter, result) ->
                  handle waiter "ROLLBACK" |> ignore
                  failtestf "expected cancellation, got %A" result

              Expect.isTrue cancelledWhileQueued "cancellation wakes the row-lock waiter"

              let fresh, _ = handle (create 4 store) "BEGIN"
              let fresh, result = handle fresh "UPDATE tx_queued_cancel SET v = v + 1 WHERE id = 1"
              Expect.equal result (Affected 1UL) "the cancelled wait leaves the row lock reusable"
              Expect.equal (handle fresh "COMMIT" |> snd) (Affected 0UL) "the replacement transaction commits"

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

              // COMMIT after a fatal statement error is a no-op, not a merge
              // of the aborted transaction's stale snapshot.
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

          testCase "a full-scan update waits for a transaction changing the same row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_scan_wait (id INT, n INT)" |> ignore
              handle setup "INSERT INTO tx_scan_wait VALUES (1, 0)" |> ignore
              let first, _ = handle (create 2 store) "BEGIN"
              let first, firstUpdate = handle first "UPDATE tx_scan_wait SET n = n + 1 WHERE id = 1"
              Expect.equal firstUpdate (Affected 1UL) "the first full-scan update changes its row"
              let second, _ = handle (create 3 store) "BEGIN"
              use started = new Threading.ManualResetEventSlim(false)
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle second "UPDATE tx_scan_wait SET n = n + 1 WHERE id = 1")
              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the competing update starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              let firstCommit = handle first "COMMIT" |> snd
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 3.0)) "the competing update completes after commit"
              let second, secondUpdate = waiting.Result
              let secondCommit = handle second "COMMIT" |> snd
              Expect.isTrue blocked "the second full-scan writer waits for the first"
              Expect.equal firstCommit (Affected 0UL) "the first update commits"
              Expect.equal secondUpdate (Affected 1UL) "the waiting update changes one row"
              Expect.equal secondCommit (Affected 0UL) "the waiting update commits"
              match handle setup "SELECT n FROM tx_scan_wait" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected the two increments to serialize, got %A" result

          testCase "joined single-target updates rebase after a competing transaction commits"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_join_wait (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "CREATE TABLE tx_join_lookup (id INT PRIMARY KEY)" |> ignore
              handle setup "INSERT INTO tx_join_wait VALUES (1, 0)" |> ignore
              handle setup "INSERT INTO tx_join_lookup VALUES (1)" |> ignore
              let sql = "UPDATE tx_join_wait t JOIN tx_join_lookup l ON l.id=t.id SET t.n=t.n+1 WHERE t.id=1"
              let first, _ = handle (create 2 store) "BEGIN"
              let first, firstUpdate = handle first sql
              Expect.equal firstUpdate (Affected 1UL) "first joined update changes the row"
              let second, _ = handle (create 3 store) "BEGIN"
              use started = new Threading.ManualResetEventSlim(false)
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle second sql)
              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the second writer starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "first writer commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "second writer completes after commit"
              let second, secondUpdate = waiting.Result
              Expect.equal secondUpdate (Affected 1UL) "second writer updates the committed row"
              Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "second writer commits"
              Expect.isTrue blocked "the second writer waited for the first"
              match handle setup "SELECT n FROM tx_join_wait WHERE id=1" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both joined increments, got %A" result

              let owner, _ = handle (create 4 store) "BEGIN"
              let owner, ownerUpdate = handle owner sql
              Expect.equal ownerUpdate (Affected 1UL) "the next transaction changes the row"
              let autocommitWriter = create 5 store
              use autocommitStarted = new Threading.ManualResetEventSlim(false)
              let autocommitUpdate =
                  Threading.Tasks.Task.Run(fun () ->
                      autocommitStarted.Set()
                      handle autocommitWriter sql |> snd)
              Expect.isTrue (autocommitStarted.Wait(TimeSpan.FromSeconds 1.0)) "autocommit writer starts"
              let autocommitBlocked = not (autocommitUpdate.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "owner commits"
              Expect.isTrue (autocommitUpdate.Wait(TimeSpan.FromSeconds 5.0)) "autocommit writer resumes"
              Expect.isTrue autocommitBlocked "autocommit writer waited for the row owner"
              Expect.equal autocommitUpdate.Result (Affected 1UL) "autocommit writer commits its update"
              match handle setup "SELECT n FROM tx_join_wait WHERE id=1" |> snd with
              | ResultSet(_, [ [ Some "4" ] ]) -> ()
              | result -> failtestf "expected the autocommit increment after the owner, got %A" result

          testCase "CTE-joined update waits for its physical target and rebases"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_cte_target (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "INSERT INTO tx_cte_target VALUES (1, 0)" |> ignore
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, changed = handle owner "UPDATE tx_cte_target SET n=n+1 WHERE id=1"
              Expect.equal changed (Affected 1UL) "the owner changes the target row"
              let waiter, _ = handle (create 3 store) "BEGIN"
              let sql =
                  "WITH c AS (SELECT 1 AS id) "
                  + "UPDATE tx_cte_target t JOIN c ON c.id=t.id SET t.n=t.n+1 WHERE t.id=1"
              use started = new Threading.ManualResetEventSlim(false)
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle waiter sql)
              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the CTE writer starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the CTE writer resumes"
              let waiter, updated = waiting.Result
              Expect.isTrue blocked "the CTE writer waits for its physical target"
              Expect.equal updated (Affected 1UL) "the CTE writer updates the committed row"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the CTE writer commits"
              match handle setup "SELECT n FROM tx_cte_target WHERE id=1" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both increments after CTE write contention, got %A" result

          testCase "CTE-joined update with an unqualified target waits and rebases"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_cte_bare_target (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "INSERT INTO tx_cte_bare_target VALUES (1, 0)" |> ignore
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, changed = handle owner "UPDATE tx_cte_bare_target SET n=n+1 WHERE id=1"
              Expect.equal changed (Affected 1UL) "the owner changes the target row"
              let waiter, _ = handle (create 3 store) "BEGIN"
              let sql =
                  "WITH c AS (SELECT 1 AS id) "
                  + "UPDATE tx_cte_bare_target t JOIN c ON c.id=t.id SET n=n+1 WHERE t.id=1"
              use started = new Threading.ManualResetEventSlim(false)
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle waiter sql)
              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the CTE writer starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the CTE writer resumes"
              let waiter, updated = waiting.Result
              Expect.isTrue blocked "the CTE writer waits for its physical target"
              Expect.equal updated (Affected 1UL) "the CTE writer updates the committed row"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the CTE writer commits"
              match handle setup "SELECT n FROM tx_cte_bare_target WHERE id=1" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both increments after CTE write contention, got %A" result
              match handle setup "WITH c AS (SELECT 1 AS id,9 AS n) UPDATE tx_cte_bare_target t JOIN c ON c.id=t.id SET n=n+1 WHERE t.id=1" |> snd with
              | Err(1052, _) -> ()
              | result -> failtestf "expected the CTE's second n column to make the assignment ambiguous, got %A" result

          testCase "CTE-joined delete waits for its physical target"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_cte_delete (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "INSERT INTO tx_cte_delete VALUES (1, 0)" |> ignore
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, changed = handle owner "UPDATE tx_cte_delete SET n=n+1 WHERE id=1"
              Expect.equal changed (Affected 1UL) "the owner changes the target row"
              let waiter, _ = handle (create 3 store) "BEGIN"
              let sql =
                  "WITH c AS (SELECT 1 AS id) "
                  + "DELETE t FROM tx_cte_delete t JOIN c ON c.id=t.id WHERE t.id=1"
              use started = new Threading.ManualResetEventSlim(false)
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle waiter sql)
              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the CTE delete starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the CTE delete resumes"
              let waiter, deleted = waiting.Result
              Expect.isTrue blocked "the CTE delete waits for its physical target"
              Expect.equal deleted (Affected 1UL) "the CTE delete removes the committed row"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the CTE delete commits"
              match handle setup "SELECT id FROM tx_cte_delete" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected no row after CTE delete contention, got %A" result

          testCase "derived joined update with an unqualified target waits and rebases"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_derived_bare_target (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "INSERT INTO tx_derived_bare_target VALUES (1, 0)" |> ignore
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, changed = handle owner "UPDATE tx_derived_bare_target SET n=n+1 WHERE id=1"
              Expect.equal changed (Affected 1UL) "the owner changes the target row"
              let waiter, _ = handle (create 3 store) "BEGIN"
              let sql =
                  "UPDATE tx_derived_bare_target t JOIN (SELECT 1 AS id) d ON d.id=t.id "
                  + "SET n=n+1 WHERE t.id=1"
              use started = new Threading.ManualResetEventSlim(false)
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle waiter sql)
              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the derived writer starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the derived writer resumes"
              let waiter, updated = waiting.Result
              Expect.isTrue blocked "the derived writer waits for its physical target"
              Expect.equal updated (Affected 1UL) "the derived writer updates the committed row"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the derived writer commits"
              match handle setup "SELECT n FROM tx_derived_bare_target WHERE id=1" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both increments after derived write contention, got %A" result
              match handle setup "UPDATE tx_derived_bare_target t JOIN (SELECT 1 AS id,9 AS n) d ON d.id=t.id SET n=n+1 WHERE t.id=1" |> snd with
              | Err(1052, _) -> ()
              | result -> failtestf "expected the derived n column to make the assignment ambiguous, got %A" result

          testCase "write targets inside a join group wait and rebase"
          <| fun _ ->
              for statement, expectedRows in
                  [ "UPDATE tx_group_a a JOIN (tx_group_b b JOIN tx_group_c c ON b.id=c.id) ON a.id=b.id SET b.n=b.n+1 WHERE a.id=1",
                    [ [ Some "1"; Some "2" ] ]
                    "UPDATE tx_group_a a JOIN (tx_group_b b JOIN tx_group_c c ON b.id=c.id) ON a.id=b.id SET n=n+1 WHERE a.id=1",
                    [ [ Some "1"; Some "2" ] ]
                    "DELETE b FROM tx_group_a a JOIN (tx_group_b b JOIN tx_group_c c ON b.id=c.id) ON a.id=b.id WHERE a.id=1",
                    [] ] do
                  let store = Fsdb.Storage.create ()
                  let setup = create 1 store
                  handle setup "CREATE TABLE tx_group_a (id INT PRIMARY KEY, flag INT)" |> ignore
                  handle setup "CREATE TABLE tx_group_b (id INT PRIMARY KEY, n INT)" |> ignore
                  handle setup "CREATE TABLE tx_group_c (id INT PRIMARY KEY)" |> ignore
                  handle setup "INSERT INTO tx_group_a VALUES (1,0)" |> ignore
                  handle setup "INSERT INTO tx_group_b VALUES (1,0)" |> ignore
                  handle setup "INSERT INTO tx_group_c VALUES (1)" |> ignore

                  let owner, _ = handle (create 2 store) "BEGIN"
                  let owner, changed = handle owner "UPDATE tx_group_b SET n=n+1 WHERE id=1"
                  Expect.equal changed (Affected 1UL) "the owner changes the grouped target"
                  let waiter, _ = handle (create 3 store) "BEGIN"
                  use started = new Threading.ManualResetEventSlim(false)
                  let waiting =
                      Threading.Tasks.Task.Run(fun () ->
                          started.Set()
                          handle waiter statement)

                  Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the grouped writer starts"
                  let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                  Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the grouped writer resumes"
                  let waiter, affected = waiting.Result
                  Expect.isTrue blocked "the grouped writer waits for its physical target"
                  Expect.equal affected (Affected 1UL) "the grouped writer changes one row"
                  Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the grouped writer commits"

                  match handle setup "SELECT id,n FROM tx_group_b" |> snd with
                  | ResultSet(_, rows) -> Expect.equal rows expectedRows "the committed grouped target agrees with MySQL"
                  | result -> failtestf "expected grouped target rows, got %A" result

          testCase "joined single-target delete waits for an updated target row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_join_delete (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "CREATE TABLE tx_join_delete_lookup (id INT PRIMARY KEY)" |> ignore
              handle setup "INSERT INTO tx_join_delete VALUES (1, 0)" |> ignore
              handle setup "INSERT INTO tx_join_delete_lookup VALUES (1)" |> ignore
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, ownerUpdate = handle owner "UPDATE tx_join_delete SET n=n+1 WHERE id=1"
              Expect.equal ownerUpdate (Affected 1UL) "the owner changes the target row"
              let waiter, _ = handle (create 3 store) "BEGIN"
              let sql = "DELETE t FROM tx_join_delete t JOIN tx_join_delete_lookup l ON l.id=t.id WHERE t.id=1"
              use started = new Threading.ManualResetEventSlim(false)
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle waiter sql)
              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the delete starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the delete resumes"
              let waiter, deleted = waiting.Result
              Expect.isTrue blocked "the delete waits for the target row"
              Expect.equal deleted (Affected 1UL) "the joined delete removes the updated row"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the joined delete commits"
              match handle setup "SELECT id FROM tx_join_delete" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected the target row to be deleted, got %A" result

              handle setup "INSERT INTO tx_join_delete VALUES (1, 0)" |> ignore
              let owner, _ = handle (create 4 store) "BEGIN"
              let owner, ownerUpdate = handle owner "UPDATE tx_join_delete SET n=n+1 WHERE id=1"
              Expect.equal ownerUpdate (Affected 1UL) "the owner changes the reinserted row"
              let autocommitWriter = create 5 store
              let usingSql =
                  "DELETE FROM tx_join_delete USING tx_join_delete JOIN tx_join_delete_lookup l ON l.id=tx_join_delete.id WHERE tx_join_delete.id=1"
              use autocommitStarted = new Threading.ManualResetEventSlim(false)
              let autocommitDelete =
                  Threading.Tasks.Task.Run(fun () ->
                      autocommitStarted.Set()
                      handle autocommitWriter usingSql |> snd)
              Expect.isTrue (autocommitStarted.Wait(TimeSpan.FromSeconds 1.0)) "autocommit delete starts"
              let autocommitBlocked = not (autocommitDelete.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the second owner commits"
              Expect.isTrue (autocommitDelete.Wait(TimeSpan.FromSeconds 5.0)) "autocommit delete resumes"
              Expect.isTrue autocommitBlocked "autocommit USING delete waits for the owner"
              Expect.equal autocommitDelete.Result (Affected 1UL) "autocommit USING delete removes the row"
              match handle setup "SELECT id FROM tx_join_delete" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected the autocommit delete to remove the row, got %A" result

          testCase "multi-target joined update rebases both target tables"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_multi_a (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "CREATE TABLE tx_multi_b (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "INSERT INTO tx_multi_a VALUES (1, 0)" |> ignore
              handle setup "INSERT INTO tx_multi_b VALUES (1, 0)" |> ignore
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, ownerUpdate = handle owner "UPDATE tx_multi_a SET n=n+1 WHERE id=1"
              Expect.equal ownerUpdate (Affected 1UL) "the owner changes the first target"
              let waiter, _ = handle (create 3 store) "BEGIN"
              let sql =
                  "UPDATE tx_multi_a a JOIN tx_multi_b b ON b.id=a.id SET a.n=a.n+1,b.n=b.n+1 WHERE a.id=1"
              use started = new Threading.ManualResetEventSlim(false)
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle waiter sql)
              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "multi-target update starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "multi-target update resumes"
              let waiter, updated = waiting.Result
              Expect.isTrue blocked "multi-target update waits for the first target"
              Expect.equal updated (Affected 2UL) "both tables are updated"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "multi-target update commits"
              match handle setup "SELECT n FROM tx_multi_a" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected first target to include both updates, got %A" result
              match handle setup "SELECT n FROM tx_multi_b" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected second target to be updated once, got %A" result

              let owner, _ = handle (create 4 store) "BEGIN"
              let owner, ownerUpdate = handle owner "UPDATE tx_multi_a SET n=n+1 WHERE id=1"
              Expect.equal ownerUpdate (Affected 1UL) "the owner changes the physical target"
              let waiter, _ = handle (create 5 store) "BEGIN"
              let derivedSql =
                  "UPDATE tx_multi_a a JOIN (SELECT id FROM tx_multi_b) d ON d.id=a.id SET a.n=a.n+1 WHERE a.id=1"
              use derivedStarted = new Threading.ManualResetEventSlim(false)
              let derivedWaiting =
                  Threading.Tasks.Task.Run(fun () ->
                      derivedStarted.Set()
                      handle waiter derivedSql)
              Expect.isTrue (derivedStarted.Wait(TimeSpan.FromSeconds 1.0)) "derived-source update starts"
              let derivedBlocked = not (derivedWaiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits again"
              Expect.isTrue (derivedWaiting.Wait(TimeSpan.FromSeconds 5.0)) "derived-source update resumes"
              let waiter, derivedUpdate = derivedWaiting.Result
              Expect.isTrue derivedBlocked "the physical target is claimed despite a derived join source"
              Expect.equal derivedUpdate (Affected 1UL) "derived-source update changes the target"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "derived-source update commits"
              match handle setup "SELECT n FROM tx_multi_a" |> snd with
              | ResultSet(_, [ [ Some "4" ] ]) -> ()
              | result -> failtestf "expected the derived-source update after the owner, got %A" result

          testCase "joined deletes claim every named target table"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_delete_a (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "CREATE TABLE tx_delete_b (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "INSERT INTO tx_delete_a VALUES (1, 0)" |> ignore
              handle setup "INSERT INTO tx_delete_b VALUES (1, 0)" |> ignore

              let runContendedDelete ownerId waiterId sql expected =
                  let owner, _ = handle (create ownerId store) "BEGIN"
                  let owner, changed = handle owner "UPDATE tx_delete_b SET n=n+1 WHERE id=1"
                  Expect.equal changed (Affected 1UL) "the owner changes the joined target"
                  let waiter, _ = handle (create waiterId store) "BEGIN"
                  use started = new Threading.ManualResetEventSlim(false)
                  let waiting =
                      Threading.Tasks.Task.Run(fun () ->
                          started.Set()
                          handle waiter sql)
                  Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "joined delete starts"
                  let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                  Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "joined delete resumes"
                  let waiter, deleted = waiting.Result
                  Expect.isTrue blocked "joined delete waits for the joined target"
                  Expect.equal deleted (Affected expected) "joined delete removes the named targets"
                  Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "joined delete commits"

              runContendedDelete
                  2
                  3
                  "DELETE a,b FROM tx_delete_a a JOIN tx_delete_b b ON b.id=a.id WHERE a.id=1"
                  2UL
              match handle setup "SELECT id FROM tx_delete_a" |> snd, handle setup "SELECT id FROM tx_delete_b" |> snd with
              | ResultSet(_, []), ResultSet(_, []) -> ()
              | results -> failtestf "expected both target tables empty, got %A" results

              handle setup "INSERT INTO tx_delete_a VALUES (1, 0)" |> ignore
              handle setup "INSERT INTO tx_delete_b VALUES (1, 0)" |> ignore
              runContendedDelete
                  4
                  5
                  "DELETE b FROM tx_delete_a a JOIN tx_delete_b b ON b.id=a.id WHERE a.id=1"
                  1UL
              match handle setup "SELECT id FROM tx_delete_a" |> snd, handle setup "SELECT id FROM tx_delete_b" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]), ResultSet(_, []) -> ()
              | results -> failtestf "expected only the joined target to be removed, got %A" results

          testCase "unqualified joined update claims its unique physical column owner"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_bare_target (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "CREATE TABLE tx_bare_lookup (id INT PRIMARY KEY, flag INT)" |> ignore
              handle setup "INSERT INTO tx_bare_target VALUES (1, 0)" |> ignore
              handle setup "INSERT INTO tx_bare_lookup VALUES (1, 1)" |> ignore
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, changed = handle owner "UPDATE tx_bare_target SET n=n+1 WHERE id=1"
              Expect.equal changed (Affected 1UL) "the owner changes the target row"
              let waiter, _ = handle (create 3 store) "BEGIN"
              let sql =
                  "UPDATE tx_bare_target t JOIN tx_bare_lookup l ON l.id=t.id SET n=n+1 WHERE t.id=1"
              use started = new Threading.ManualResetEventSlim(false)
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle waiter sql)
              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "unqualified joined update starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "unqualified joined update resumes"
              let waiter, updated = waiting.Result
              Expect.isTrue blocked "the inferred target waits for the owner"
              Expect.equal updated (Affected 1UL) "the unique column owner is updated"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the joined update commits"
              match handle setup "SELECT n FROM tx_bare_target" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both unqualified increments, got %A" result

              let owner, _ = handle (create 4 store) "BEGIN"
              let owner, changed = handle owner "UPDATE tx_bare_lookup SET flag=flag+1 WHERE id=1"
              Expect.equal changed (Affected 1UL) "the owner changes the joined table"
              let waiter, _ = handle (create 5 store) "BEGIN"
              let joinedSql =
                  "UPDATE tx_bare_target t JOIN tx_bare_lookup l ON l.id=t.id SET flag=flag+1 WHERE t.id=1"
              use joinedStarted = new Threading.ManualResetEventSlim(false)
              let joinedWaiting =
                  Threading.Tasks.Task.Run(fun () ->
                      joinedStarted.Set()
                      handle waiter joinedSql)
              Expect.isTrue (joinedStarted.Wait(TimeSpan.FromSeconds 1.0)) "joined-table update starts"
              let joinedBlocked = not (joinedWaiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the joined-table owner commits"
              Expect.isTrue (joinedWaiting.Wait(TimeSpan.FromSeconds 5.0)) "joined-table update resumes"
              let waiter, joinedUpdate = joinedWaiting.Result
              Expect.isTrue joinedBlocked "the inferred joined-table target waits"
              Expect.equal joinedUpdate (Affected 1UL) "the joined table is updated"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "joined-table update commits"
              match handle setup "SELECT flag FROM tx_bare_lookup" |> snd with
              | ResultSet(_, [ [ Some "3" ] ]) -> ()
              | result -> failtestf "expected both joined-table increments, got %A" result

          testCase "unqualified joined updates with lateral and JSON read sources rebase"
          <| fun _ ->
              for source in
                  [ "LATERAL (SELECT 1 x) e ON e.x=t.id"
                    "JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j ON j.x=t.id" ] do
                  let store = Fsdb.Storage.create ()
                  let setup = create 1 store
                  handle setup "CREATE TABLE tx_source_target(id INT PRIMARY KEY,n INT)" |> ignore
                  handle setup "INSERT INTO tx_source_target VALUES(1,0)" |> ignore
                  let owner, _ = handle (create 2 store) "BEGIN"
                  let owner, changed = handle owner "UPDATE tx_source_target SET n=n+1 WHERE id=1"
                  Expect.equal changed (Affected 1UL) "the owner changes the target"
                  let waiter, _ = handle (create 3 store) "BEGIN"
                  let sql = "UPDATE tx_source_target t JOIN " + source + " SET n=n+1 WHERE t.id=1"
                  use started = new Threading.ManualResetEventSlim(false)
                  let waiting =
                      Threading.Tasks.Task.Run(fun () ->
                          started.Set()
                          handle waiter sql)
                  Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "joined writer starts"
                  let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                  Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "joined writer resumes"
                  let waiter, updated = waiting.Result
                  Expect.isTrue blocked "the joined writer waits for the target owner"
                  Expect.equal updated (Affected 1UL) "the joined writer updates the rebased row"
                  Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the joined writer commits"
                  match handle setup "SELECT n FROM tx_source_target" |> snd with
                  | ResultSet(_, [ [ Some "2" ] ]) -> ()
                  | result -> failtestf "expected both increments after the %s join, got %A" source result

          testCase "an autocommit full-scan update waits for a transaction changing the same row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_scan_autocommit_wait (id INT, n INT)" |> ignore
              handle setup "INSERT INTO tx_scan_autocommit_wait VALUES (1, 0)" |> ignore
              let first, _ = handle (create 2 store) "BEGIN"
              let first, firstUpdate = handle first "UPDATE tx_scan_autocommit_wait SET n = n + 1 WHERE id = 1"
              Expect.equal firstUpdate (Affected 1UL) "the first writer changes the row"
              use started = new Threading.ManualResetEventSlim(false)
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      started.Set()
                      handle (create 3 store) "UPDATE tx_scan_autocommit_wait SET n = n + 1 WHERE id = 1" |> snd)
              Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the autocommit writer starts"
              let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first writer commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 3.0)) "the autocommit writer completes after commit"
              Expect.isTrue blocked "the autocommit full scan waits for the row owner"
              Expect.equal waiting.Result (Affected 1UL) "the waiting autocommit writer changes one row"
              match handle setup "SELECT n FROM tx_scan_autocommit_wait" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both autocommit and transaction increments, got %A" result

          testCase "limited ordered writes reselect after a contended predicate or sort key changes"
          <| fun _ ->
              for indexed in [ false; true ] do
                  for initialRows, ownerWrite, statement, expectedRows, affected in
                      [ "(1,0),(2,0)", "UPDATE tx_limited_rebase SET n=-1 WHERE id=1",
                        "UPDATE tx_limited_rebase SET n=n+1 WHERE n>=0 ORDER BY id LIMIT 1",
                        [ [ Some "1"; Some "-1" ]; [ Some "2"; Some "1" ] ], 1UL
                        "(1,0),(2,0)", "UPDATE tx_limited_rebase SET n=-1 WHERE id=1",
                        "DELETE FROM tx_limited_rebase WHERE n>=0 ORDER BY id LIMIT 1",
                        [ [ Some "1"; Some "-1" ] ], 1UL
                        "(1,0),(2,1)", "UPDATE tx_limited_rebase SET n=2 WHERE id=1",
                        "UPDATE tx_limited_rebase SET n=n+10 WHERE n>=0 ORDER BY n,id LIMIT 1",
                        [ [ Some "1"; Some "2" ]; [ Some "2"; Some "11" ] ], 1UL
                        "(1,0),(2,1)", "UPDATE tx_limited_rebase SET n=2 WHERE id=1",
                        "DELETE FROM tx_limited_rebase WHERE n>=0 ORDER BY n,id LIMIT 1",
                        [ [ Some "1"; Some "2" ] ], 1UL
                        "(1,0),(2,0),(3,0)", "UPDATE tx_limited_rebase SET n=-1 WHERE id=1",
                        "UPDATE tx_limited_rebase SET n=n+1 WHERE n>=0 ORDER BY id LIMIT 2",
                        [ [ Some "1"; Some "-1" ]; [ Some "2"; Some "1" ]; [ Some "3"; Some "1" ] ], 2UL
                        "(1,0),(2,0),(3,0)", "UPDATE tx_limited_rebase SET n=-1 WHERE id=1",
                        "DELETE FROM tx_limited_rebase WHERE n>=0 ORDER BY id LIMIT 2",
                        [ [ Some "1"; Some "-1" ] ], 2UL
                        "(1,0),(2,1),(3,2)", "UPDATE tx_limited_rebase SET n=3 WHERE id=1",
                        "UPDATE tx_limited_rebase SET n=n+10 WHERE n>=0 ORDER BY n,id LIMIT 2",
                        [ [ Some "1"; Some "3" ]; [ Some "2"; Some "11" ]; [ Some "3"; Some "12" ] ], 2UL
                        "(1,0),(2,1),(3,2)", "UPDATE tx_limited_rebase SET n=3 WHERE id=1",
                        "DELETE FROM tx_limited_rebase WHERE n>=0 ORDER BY n,id LIMIT 2",
                        [ [ Some "1"; Some "3" ] ], 2UL ] do
                      let store = Fsdb.Storage.create ()
                      let setup = create 1 store
                      let index = if indexed then ", KEY ix_n(n)" else ""
                      handle setup ("CREATE TABLE tx_limited_rebase (id INT PRIMARY KEY, n INT" + index + ")") |> ignore
                      handle setup ("INSERT INTO tx_limited_rebase VALUES " + initialRows) |> ignore
                      let owner, _ = handle (create 2 store) "BEGIN"
                      let owner, firstWrite = handle owner ownerWrite
                      Expect.equal firstWrite (Affected 1UL) "the owner changes the first candidate"
                      let waiter, _ = handle (create 3 store) "BEGIN"
                      use started = new Threading.ManualResetEventSlim(false)
                      let waiting =
                          Threading.Tasks.Task.Run(fun () ->
                              started.Set()
                              handle waiter statement)
                      Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "limited writer starts"
                      let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                      Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "owner commits"
                      Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "limited writer resumes"
                      let waiter, result = waiting.Result
                      Expect.isTrue blocked "limited writer waits for the first candidate"
                      Expect.equal result (Affected affected) "limited writer changes the rebased targets"
                      Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "limited writer commits"
                      match handle setup "SELECT id,n FROM tx_limited_rebase ORDER BY id" |> snd with
                      | ResultSet(_, rows) -> Expect.equal rows expectedRows (statement + $" (indexed={indexed})")
                      | actual -> failtestf "expected a result set after %s, got %A" statement actual

          testCase "an autocommit full-scan delete rechecks the row after a competing commit"
          <| fun _ ->
              for predicate, affected, remaining in
                  [ "id = 1", Affected 1UL, []
                    "n = 0", Affected 0UL, [ [ Some "1"; Some "1" ] ] ] do
                  let store = Fsdb.Storage.create ()
                  let setup = create 1 store
                  handle setup "CREATE TABLE tx_scan_delete_wait (id INT, n INT)" |> ignore
                  handle setup "INSERT INTO tx_scan_delete_wait VALUES (1, 0)" |> ignore
                  let first, _ = handle (create 2 store) "BEGIN"
                  let first, firstUpdate = handle first "UPDATE tx_scan_delete_wait SET n = 1 WHERE id = 1"
                  Expect.equal firstUpdate (Affected 1UL) "the first writer changes the row"
                  use started = new Threading.ManualResetEventSlim(false)
                  let waiting =
                      Threading.Tasks.Task.Run(fun () ->
                          started.Set()
                          handle (create 3 store) ("DELETE FROM tx_scan_delete_wait WHERE " + predicate) |> snd)
                  Expect.isTrue (started.Wait(TimeSpan.FromSeconds 1.0)) "the delete starts"
                  let blocked = not (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                  Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the update commits"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 3.0)) "the delete completes after commit"
                  Expect.isTrue blocked "the full-scan delete waits for the row owner"
                  Expect.equal waiting.Result affected "the delete rechecks the committed row"
                  match handle setup "SELECT id, n FROM tx_scan_delete_wait" |> snd with
                  | ResultSet(_, rows) -> Expect.equal rows remaining "the final rows match MySQL 8.4"
                  | result -> failtestf "expected final rows, got %A" result

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

          testCase "distinct unique keys with a legacy stripe collision do not block"
          <| fun _ ->
              let stripe key =
                  (StringComparer.Ordinal.GetHashCode(key) &&& Int32.MaxValue) % 4096

              let firstKey, secondKey =
                  [ 0 .. 4096 ]
                  |> Seq.map (sprintf "unique-key-%d")
                  |> Seq.groupBy stripe
                  |> Seq.pick (fun (_, keys) ->
                      match keys |> Seq.truncate 2 |> Seq.toList with
                      | [ first; second ] -> Some(first, second)
                      | _ -> None)

              let store = Fsdb.Storage.create ()
              let first = Fsdb.Storage.beginTransactionContext store
              let second = Fsdb.Storage.beginTransactionContext store

              try
                  Fsdb.Storage.acquireTransactionWriteTargets TimeSpan.Zero first "test" "items" [] [ firstKey ]
                  Fsdb.Storage.acquireTransactionWriteTargets TimeSpan.Zero second "test" "items" [] [ secondKey ]

                  Expect.throwsT<Fsdb.Storage.LockWaitTimeout>
                      (fun () ->
                          Fsdb.Storage.acquireTransactionWriteTargets TimeSpan.Zero second "test" "items" [] [ firstKey ])
                      "an already-owned exact key still blocks another transaction"
              finally
                  Fsdb.Storage.releaseTransactionLocks first
                  Fsdb.Storage.releaseTransactionLocks second

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

          testCase "a waiting literal replace rebases before it writes"
          <| fun _ ->
              [ "REPLACE INTO tx_replace VALUES (1, %d)"
                "REPLACE INTO tx_replace SET id = 1, v = %d" ]
              |> List.iter (fun replacement ->
                  let replace value = replacement.Replace("%d", string value)
                  let store = Fsdb.Storage.create ()
                  let setup = create 1 store
                  let setup, _ = handle setup "CREATE TABLE tx_replace (id INT PRIMARY KEY, v INT)"
                  let setup, _ = handle setup "INSERT INTO tx_replace VALUES (1, 0)"
                  let first, _ = handle (create 2 store) "BEGIN"
                  let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
                  let second, _ = handle second "BEGIN"
                  let first, firstReplace = handle first (replace 10)
                  Expect.equal firstReplace (Affected 2UL) "the first replacement owns the key"

                  let waiting =
                      Threading.Tasks.Task.Run(fun () -> handle second (replace 20))

                  Expect.isFalse
                      (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                      "the second replacement waits for the key owner"

                  Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first replacement commits"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the waiting replacement resumes"

                  let second, secondReplace = waiting.GetAwaiter().GetResult()
                  Expect.equal secondReplace (Affected 2UL) "the waiter replaces the committed row"
                  Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the rebased replacement commits"

                  match handle setup "SELECT v FROM tx_replace WHERE id = 1" |> snd with
                  | ResultSet(_, [ [ Some "20" ] ]) -> ()
                  | result -> failtestf "expected the waiting replacement to win, got %A" result)

          testCase "updatable view writes wait and rebase on the physical row"
          <| fun _ ->
              for definition in
                  [ "SELECT id, n FROM tx_view_target"
                    "SELECT t.id, t.n FROM tx_view_target t JOIN tx_view_lookup l ON l.id = t.id" ] do
                  let store = Fsdb.Storage.create ()
                  let setup = create 1 store
                  let setup, _ = handle setup "CREATE TABLE tx_view_target (id INT PRIMARY KEY, n INT)"
                  let setup, _ = handle setup "CREATE TABLE tx_view_lookup (id INT PRIMARY KEY)"
                  let setup, _ = handle setup "INSERT INTO tx_view_target VALUES (1, 0)"
                  let setup, _ = handle setup "INSERT INTO tx_view_lookup VALUES (1)"
                  let setup, _ = handle setup ("CREATE VIEW tx_view_write AS " + definition)
                  let first, _ = handle (create 2 store) "BEGIN"
                  let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
                  let second, _ = handle second "BEGIN"
                  let sql = "UPDATE tx_view_write SET n = n + 1 WHERE id = 1"
                  let first, firstWrite = handle first sql
                  Expect.equal firstWrite (Affected 1UL) "the first view update succeeds"

                  let waiting = Threading.Tasks.Task.Run(fun () -> handle second sql)
                  Expect.isFalse
                      (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                      "the second view update waits for the physical row owner"

                  Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first view update commits"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the second view update resumes"
                  let second, secondWrite = waiting.GetAwaiter().GetResult()
                  Expect.equal secondWrite (Affected 1UL) "the second view update sees the committed row"
                  Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the second view update commits"

                  match handle setup "SELECT n FROM tx_view_target WHERE id = 1" |> snd with
                  | ResultSet(_, [ [ Some "2" ] ]) -> ()
                  | result -> failtestf "both view increments should be committed, got %A" result

          testCase "joined view updates wait and rebase on the physical row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_joined_view_target (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "CREATE TABLE tx_joined_view_lookup (id INT PRIMARY KEY)" |> ignore
              handle setup "INSERT INTO tx_joined_view_target VALUES (1, 0)" |> ignore
              handle setup "INSERT INTO tx_joined_view_lookup VALUES (1)" |> ignore
              handle setup "CREATE VIEW tx_joined_view AS SELECT id, n FROM tx_joined_view_target" |> ignore
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, ownerWrite = handle owner "UPDATE tx_joined_view_target SET n=n+1 WHERE id=1"
              Expect.equal ownerWrite (Affected 1UL) "the owner updates the base row"
              let waiter, _ = handle (create 3 store) "BEGIN"
              let sql =
                  "UPDATE tx_joined_view v JOIN tx_joined_view_lookup l ON l.id=v.id "
                  + "SET v.n=v.n+1 WHERE v.id=1"
              let waiting = Threading.Tasks.Task.Run(fun () -> handle waiter sql)
              Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the joined view writer waits"
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the joined view writer resumes"
              let waiter, result = waiting.GetAwaiter().GetResult()
              Expect.equal result (Affected 1UL) "the joined view writer updates the rebased row"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the waiter commits"
              match handle setup "SELECT n FROM tx_joined_view_target WHERE id=1" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both increments, got %A" result

              handle setup
                  "CREATE VIEW tx_joined_renamed AS SELECT id AS public_id, n AS score FROM tx_joined_view_target"
              |> ignore
              let renamedSql =
                  "UPDATE tx_joined_renamed v JOIN tx_joined_view_lookup l ON l.id=v.public_id "
                  + "SET v.score=v.score+1 WHERE v.public_id=1"
              Expect.equal (handle setup renamedSql |> snd) (Affected 1UL) "renamed view columns write through"
              handle setup
                  "CREATE VIEW tx_joined_checked AS SELECT id, n FROM tx_joined_view_target WHERE n>=0 WITH CASCADED CHECK OPTION"
              |> ignore
              let rejectedSql =
                  "UPDATE tx_joined_checked v JOIN tx_joined_view_lookup l ON l.id=v.id "
                  + "SET v.n=-1 WHERE v.id=1"
              match handle setup rejectedSql |> snd with
              | Err(1369, _) -> ()
              | result -> failtestf "expected CHECK OPTION error 1369, got %A" result
              match handle setup "SELECT n FROM tx_joined_view_target WHERE id=1" |> snd with
              | ResultSet(_, [ [ Some "3" ] ]) -> ()
              | result -> failtestf "CHECK OPTION must preserve the prior value, got %A" result

              let bareSql =
                  "UPDATE tx_joined_view v JOIN tx_joined_view_lookup l ON l.id=v.id "
                  + "SET n=n+1 WHERE v.id=1"
              Expect.equal (handle setup bareSql |> snd) (Affected 1UL) "unique bare view column updates"
              handle setup "ALTER TABLE tx_joined_view_lookup ADD COLUMN n INT DEFAULT 0" |> ignore
              match handle setup bareSql |> snd with
              | Err(1052, _) -> ()
              | result -> failtestf "expected ambiguous bare target error 1052, got %A" result
              let ambiguousValueSql =
                  "UPDATE tx_joined_view v JOIN tx_joined_view_lookup l ON l.id=v.id "
                  + "SET v.n=n+1 WHERE v.id=1"
              match handle setup ambiguousValueSql |> snd with
              | Err(1052, _) -> ()
              | result -> failtestf "expected ambiguous value error 1052, got %A" result

          testCase "joined view update checks the caller's outer join reads"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              handle root "CREATE TABLE tx_secure_view_target (id INT PRIMARY KEY, n INT)" |> ignore
              handle root "CREATE TABLE tx_secure_view_lookup (id INT PRIMARY KEY)" |> ignore
              handle root "INSERT INTO tx_secure_view_target VALUES (1, 0)" |> ignore
              handle root "INSERT INTO tx_secure_view_lookup VALUES (1)" |> ignore
              handle root "CREATE VIEW tx_secure_view AS SELECT id, n FROM tx_secure_view_target" |> ignore
              handle root "CREATE USER view_writer" |> ignore
              handle root "GRANT SELECT, UPDATE ON fsdb.tx_secure_view TO view_writer" |> ignore
              let writer = { create 2 store with User = "view_writer" }
              let sql =
                  "UPDATE tx_secure_view v JOIN tx_secure_view_lookup l ON l.id=v.id "
                  + "SET v.n=v.n+1 WHERE v.id=1"
              match handle writer sql |> snd with
              | Err(1143, _) | Err(1142, _) -> ()
              | result -> failtestf "expected denied outer join read, got %A" result
              handle root "GRANT SELECT ON fsdb.tx_secure_view_lookup TO view_writer" |> ignore
              Expect.equal (handle writer sql |> snd) (Affected 1UL) "granted lookup read permits the update"

          testCase "recursive CTE joined view updates wait and rebase on the physical row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_recursive_target(id INT PRIMARY KEY,n INT)" |> ignore
              handle setup "INSERT INTO tx_recursive_target VALUES(1,0),(2,0),(3,0)" |> ignore
              handle setup
                  ("CREATE VIEW tx_recursive_view AS WITH RECURSIVE "
                   + "seq(k) AS (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3), "
                   + "doubled AS (SELECT k,k*2 AS v FROM seq) "
                   + "SELECT t.id,t.n FROM doubled d JOIN tx_recursive_target t ON t.id=d.k")
              |> ignore
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, ownerWrite = handle owner "UPDATE tx_recursive_target SET n=n+1 WHERE id=2"
              Expect.equal ownerWrite (Affected 1UL) "owner updates the physical row"
              let waiter, _ = handle (create 3 store) "BEGIN"
              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      handle waiter "UPDATE tx_recursive_view SET n=n+1 WHERE id=2")
              Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "recursive view writer waits"
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "recursive view writer resumes"
              let waiter, result = waiting.GetAwaiter().GetResult()
              Expect.equal result (Affected 1UL) "recursive view writer updates the rebased row"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "waiter commits"
              match handle setup "SELECT n FROM tx_recursive_target WHERE id=2" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both recursive-view increments, got %A" result

          testCase "nested joined view updates wait for the physical target"
          <| fun _ ->
              for fromClause, assignment in
                  [ "tx_nested_target t JOIN tx_nested_internal l ON l.id=t.id", "v.n=v.n+1"
                    "tx_nested_internal l JOIN tx_nested_target t ON l.id=t.id", "v.n=v.n+1"
                    "tx_nested_internal l JOIN tx_nested_target t ON l.id=t.id", "n=n+1" ] do
                  let store = Fsdb.Storage.create ()
                  let setup = create 1 store
                  handle setup "CREATE TABLE tx_nested_target (id INT PRIMARY KEY, n INT)" |> ignore
                  handle setup "CREATE TABLE tx_nested_internal (id INT PRIMARY KEY)" |> ignore
                  handle setup "CREATE TABLE tx_nested_outer (id INT PRIMARY KEY)" |> ignore
                  handle setup "INSERT INTO tx_nested_target VALUES (1, 0)" |> ignore
                  handle setup "INSERT INTO tx_nested_internal VALUES (1)" |> ignore
                  handle setup "INSERT INTO tx_nested_outer VALUES (1)" |> ignore
                  handle setup ("CREATE VIEW tx_nested_view AS SELECT t.id, t.n FROM " + fromClause)
                  |> ignore
                  let owner, _ = handle (create 2 store) "BEGIN"
                  let owner, ownerWrite = handle owner "UPDATE tx_nested_target SET n=n+1 WHERE id=1"
                  Expect.equal ownerWrite (Affected 1UL) "the owner updates the base row"
                  let waiter, _ = handle (create 3 store) "BEGIN"
                  let sql =
                      "UPDATE tx_nested_view v JOIN tx_nested_outer x ON x.id=v.id "
                      + "SET " + assignment + " WHERE v.id=1"
                  let waiting = Threading.Tasks.Task.Run(fun () -> handle waiter sql)
                  Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the nested joined writer waits"
                  Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the joined writer resumes"
                  let waiter, result = waiting.GetAwaiter().GetResult()
                  Expect.equal result (Affected 1UL) "the joined writer updates the rebased row"
                  Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the waiter commits"
                  match handle setup "SELECT n FROM tx_nested_target WHERE id=1" |> snd with
                  | ResultSet(_, [ [ Some "2" ] ]) -> ()
                  | result -> failtestf "expected both increments, got %A" result

                  handle setup
                      ("CREATE VIEW tx_nested_checked AS SELECT t.id,t.n FROM "
                       + fromClause
                       + " WITH CASCADED CHECK OPTION")
                  |> ignore
                  let rejectedSql =
                      "UPDATE tx_nested_checked v JOIN tx_nested_outer x ON x.id=v.id "
                      + "SET v.id=2 WHERE v.id=1"
                  match handle setup rejectedSql |> snd with
                  | Err(1369, _) -> ()
                  | result -> failtestf "expected nested CHECK OPTION error 1369, got %A" result
                  match handle setup "SELECT id,n FROM tx_nested_target" |> snd with
                  | ResultSet(_, [ [ Some "1"; Some "2" ] ]) -> ()
                  | result -> failtestf "CHECK OPTION must preserve the target, got %A" result

                  if assignment = "n=n+1" then
                      handle setup "ALTER TABLE tx_nested_outer ADD COLUMN n INT DEFAULT 0" |> ignore
                      match handle setup sql |> snd with
                      | Err(1052, _) -> ()
                      | result -> failtestf "expected ambiguous bare n error 1052, got %A" result

          testCase "nested joined view update checks the caller's outer join reads"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              handle root "CREATE TABLE tx_nested_secure_target (id INT PRIMARY KEY, n INT)" |> ignore
              handle root "CREATE TABLE tx_nested_secure_inner (id INT PRIMARY KEY)" |> ignore
              handle root "CREATE TABLE tx_nested_secure_outer (id INT PRIMARY KEY)" |> ignore
              handle root "INSERT INTO tx_nested_secure_target VALUES (1, 0)" |> ignore
              handle root "INSERT INTO tx_nested_secure_inner VALUES (1)" |> ignore
              handle root "INSERT INTO tx_nested_secure_outer VALUES (1)" |> ignore
              handle root
                  "CREATE VIEW tx_nested_secure_view AS SELECT t.id,t.n FROM tx_nested_secure_target t JOIN tx_nested_secure_inner l ON l.id=t.id"
              |> ignore
              handle root "CREATE USER nested_writer" |> ignore
              handle root "GRANT SELECT, UPDATE ON fsdb.tx_nested_secure_view TO nested_writer" |> ignore
              let writer = { create 2 store with User = "nested_writer" }
              let sql =
                  "UPDATE tx_nested_secure_view v JOIN tx_nested_secure_outer x ON x.id=v.id "
                  + "SET v.n=v.n+1 WHERE v.id=1"
              match handle writer sql |> snd with
              | Err(1143, _) | Err(1142, _) -> ()
              | result -> failtestf "expected denied outer join read, got %A" result
              handle root "GRANT SELECT ON fsdb.tx_nested_secure_outer TO nested_writer" |> ignore
              Expect.equal (handle writer sql |> snd) (Affected 1UL) "granted lookup read permits the update"

          testCase "disjoint joined view updates do not wait for another physical row"
          <| fun _ ->
              for fromClause in
                  [ "tx_disjoint_target t JOIN tx_disjoint_inner l ON l.id=t.id"
                    "tx_disjoint_inner l JOIN tx_disjoint_target t ON l.id=t.id" ] do
                  let store = Fsdb.Storage.create ()
                  let setup = create 1 store
                  handle setup "CREATE TABLE tx_disjoint_target (id INT PRIMARY KEY, n INT)" |> ignore
                  handle setup "CREATE TABLE tx_disjoint_inner (id INT PRIMARY KEY)" |> ignore
                  handle setup "CREATE TABLE tx_disjoint_outer (id INT PRIMARY KEY)" |> ignore
                  handle setup "INSERT INTO tx_disjoint_target VALUES (1,0),(2,0)" |> ignore
                  handle setup "INSERT INTO tx_disjoint_inner VALUES (1),(2)" |> ignore
                  handle setup "INSERT INTO tx_disjoint_outer VALUES (1),(2)" |> ignore
                  handle setup ("CREATE VIEW tx_disjoint_view AS SELECT t.id,t.n FROM " + fromClause)
                  |> ignore
                  let owner, _ = handle (create 2 store) "BEGIN"
                  let owner, owned = handle owner "UPDATE tx_disjoint_target SET n=n+1 WHERE id=2"
                  Expect.equal owned (Affected 1UL) "the owner holds row 2"
                  let waiter, _ = handle (create 3 store) "BEGIN"
                  let sql =
                      "UPDATE tx_disjoint_view v JOIN tx_disjoint_outer x ON x.id=v.id "
                      + "SET v.n=v.n+1 WHERE v.id=1"
                  let waiting = Threading.Tasks.Task.Run(fun () -> handle waiter sql)
                  let completedBeforeOwnerCommit = waiting.Wait(TimeSpan.FromMilliseconds 500.0)
                  let ownerCommit = handle owner "COMMIT" |> snd
                  Expect.equal ownerCommit (Affected 0UL) "the owner commits"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the view writer completes"
                  Expect.isTrue completedBeforeOwnerCommit "row 1 does not wait for the owner of row 2"
                  let waiter, result = waiting.GetAwaiter().GetResult()
                  Expect.equal result (Affected 1UL) "the view writer changes row 1"
                  Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the view writer commits"
                  match handle setup "SELECT id,n FROM tx_disjoint_target ORDER BY id" |> snd with
                  | ResultSet(_, [ [ Some "1"; Some "1" ]; [ Some "2"; Some "1" ] ]) -> ()
                  | result -> failtestf "expected both disjoint writes, got %A" result

          testCase "disjoint physical joined updates lock only their matched row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              handle setup "CREATE TABLE tx_direct_disjoint (id INT PRIMARY KEY, n INT)" |> ignore
              handle setup "CREATE TABLE tx_direct_lookup (id INT PRIMARY KEY)" |> ignore
              handle setup "INSERT INTO tx_direct_disjoint VALUES (1,0),(2,0)" |> ignore
              handle setup "INSERT INTO tx_direct_lookup VALUES (1),(2)" |> ignore
              let owner, _ = handle (create 2 store) "BEGIN"
              let owner, owned = handle owner "UPDATE tx_direct_disjoint SET n=n+1 WHERE id=2"
              Expect.equal owned (Affected 1UL) "the owner holds row 2"
              let waiter, _ = handle (create 3 store) "BEGIN"
              let sql =
                  "UPDATE tx_direct_disjoint t JOIN tx_direct_lookup l ON l.id=t.id "
                  + "SET t.n=t.n+1 WHERE t.id=1"
              let waiting = Threading.Tasks.Task.Run(fun () -> handle waiter sql)
              let completedBeforeOwnerCommit = waiting.Wait(TimeSpan.FromMilliseconds 500.0)
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the joined writer completes"
              Expect.isTrue completedBeforeOwnerCommit "the joined writer does not wait on row 2"
              let waiter, result = waiting.GetAwaiter().GetResult()
              Expect.equal result (Affected 1UL) "the joined writer changes row 1"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the joined writer commits"

          testCase "updatable view delete waits and rechecks the physical row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_delete_view_target (id INT PRIMARY KEY, n INT)"
              let setup, _ = handle setup "INSERT INTO tx_delete_view_target VALUES (1, 0)"
              let setup, _ = handle setup "CREATE VIEW tx_delete_view AS SELECT id, n FROM tx_delete_view_target"
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
              let second, _ = handle second "BEGIN"
              let sql = "DELETE FROM tx_delete_view WHERE id = 1"
              let first, firstDelete = handle first sql
              Expect.equal firstDelete (Affected 1UL) "the first view delete succeeds"

              let waiting = Threading.Tasks.Task.Run(fun () -> handle second sql)
              Expect.isFalse
                  (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                  "the second view delete waits for the physical row owner"

              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first view delete commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the second view delete resumes"
              let second, secondDelete = waiting.GetAwaiter().GetResult()
              Expect.equal secondDelete (Affected 0UL) "the second delete sees the committed absence"
              Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the second view delete commits"

              match handle setup "SELECT id FROM tx_delete_view_target" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "the physical row should be absent, got %A" result

          testCase "JSON_TABLE-first view updates wait on the physical target"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_json_view_target (id INT PRIMARY KEY, n INT)"
              let setup, _ = handle setup "INSERT INTO tx_json_view_target VALUES (1, 0)"
              let setup, _ =
                  handle setup
                      "CREATE VIEW tx_json_view AS SELECT t.id,t.n,j.x FROM JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j JOIN tx_json_view_target t ON t.id=j.x"
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
              let second, _ = handle second "BEGIN"
              let sql = "UPDATE tx_json_view SET n=n+1 WHERE id=1"
              let first, firstWrite = handle first sql
              Expect.equal firstWrite (Affected 1UL) "the first view update succeeds"

              let waiting = Threading.Tasks.Task.Run(fun () -> handle second sql)
              Expect.isFalse
                  (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                  "the second view update waits for the physical target"

              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first view update commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the second view update resumes"
              let second, secondWrite = waiting.GetAwaiter().GetResult()
              Expect.equal secondWrite (Affected 1UL) "the rebased update sees the committed row"
              Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the second view update commits"

              match handle setup "SELECT n FROM tx_json_view_target WHERE id=1" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "both view increments should be committed, got %A" result

          testCase "view VALUES and INSERT SELECT wait for the physical primary key"
          <| fun _ ->
              let definitions =
                  [ "SELECT id, n FROM tx_insert_view_target"
                    "SELECT t.id, t.n, l.id AS lookup_id FROM tx_insert_view_target t JOIN tx_insert_view_lookup l ON l.id = t.id" ]
              let statements =
                  [ "INSERT INTO tx_insert_view (id, n) VALUES (1, 10)"
                    "INSERT INTO tx_insert_view (id, n) SELECT id, n FROM tx_insert_view_source WHERE id = 1" ]

              for definition in definitions do
                  for sql in statements do
                      let store = Fsdb.Storage.create ()
                      let setup = create 1 store
                      let setup, _ = handle setup "CREATE TABLE tx_insert_view_target (id INT PRIMARY KEY, n INT)"
                      let setup, _ = handle setup "CREATE TABLE tx_insert_view_lookup (id INT PRIMARY KEY)"
                      let setup, _ = handle setup "CREATE TABLE tx_insert_view_source (id INT PRIMARY KEY, n INT)"
                      let setup, _ = handle setup "INSERT INTO tx_insert_view_lookup VALUES (1)"
                      let setup, _ = handle setup "INSERT INTO tx_insert_view_source VALUES (1, 10)"
                      let setup, _ = handle setup ("CREATE VIEW tx_insert_view AS " + definition)
                      let first, _ = handle (create 2 store) "BEGIN"
                      let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
                      let second, _ = handle second "BEGIN"
                      let first, firstInsert = handle first sql
                      Expect.equal firstInsert (Affected 1UL) "the first view insert succeeds"

                      let waiting = Threading.Tasks.Task.Run(fun () -> handle second sql)
                      Expect.isFalse
                          (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                          "the second view insert waits for the physical key owner"

                      Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first view insert commits"
                      Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the second view insert resumes"

                      match waiting.GetAwaiter().GetResult() |> snd with
                      | Err(1062, _) -> ()
                      | result -> failtestf "the second insert should find the committed key, got %A" result

                      handle second "ROLLBACK" |> ignore

                      match handle setup "SELECT id, n FROM tx_insert_view_target" |> snd with
                      | ResultSet(_, [ [ Some "1"; Some "10" ] ]) -> ()
                      | result -> failtestf "the first inserted row should remain, got %A" result

          testCase "literal defaults participate in transaction key claims"
          <| fun _ ->
              [ "INSERT INTO tx_default_key (id, tenant) VALUES (%d, 7)"
                "INSERT INTO tx_default_key (id, tenant, token) VALUES (%d, 7, DEFAULT)" ]
              |> List.iter (fun insertTemplate ->
                  let insert id = insertTemplate.Replace("%d", string id)
                  let store = Fsdb.Storage.create ()
                  let setup = create 1 store
                  let setup, _ =
                      handle setup "CREATE TABLE tx_default_key (id INT PRIMARY KEY, tenant INT, token VARCHAR(20) DEFAULT 'shared', UNIQUE KEY uq_token (tenant, token))"

                  let first, _ = handle (create 2 store) "BEGIN"
                  let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
                  let second, _ = handle second "BEGIN"
                  let first, firstInsert = handle first (insert 1)
                  Expect.equal firstInsert (Affected 1UL) "the first defaulted value claims its unique key"

                  let waiting = Threading.Tasks.Task.Run(fun () -> handle second (insert 2))

                  Expect.isFalse
                      (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                      "the same literal default waits for its key owner"

                  Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first defaulted row commits"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the default-key waiter resumes"

                  match waiting.GetAwaiter().GetResult() |> snd with
                  | Err(1062, _) -> ()
                  | result -> failtestf "expected the rebased insert to find the duplicate default, got %A" result

                  handle second "ROLLBACK" |> ignore

                  match handle setup "SELECT id, tenant, token FROM tx_default_key" |> snd with
                  | ResultSet(_, [ [ Some "1"; Some "7"; Some "shared" ] ]) -> ()
                  | result -> failtestf "expected only the first defaulted row, got %A" result)

          testCase "functional defaults participate in transaction key claims"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ =
                  handle setup "CREATE TABLE tx_expression_key (id INT PRIMARY KEY, base INT, derived INT DEFAULT (base + 1), UNIQUE KEY uq_derived (derived))"

              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
              let second, _ = handle second "BEGIN"
              let first, firstInsert = handle first "INSERT INTO tx_expression_key (id, base) VALUES (1, 10)"
              Expect.equal firstInsert (Affected 1UL) "the first evaluated default owns its unique key"

              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      handle second "INSERT INTO tx_expression_key (id, base) VALUES (2, 10)")

              Expect.isFalse
                  (waiting.Wait(TimeSpan.FromMilliseconds 100.0))
                  "the equivalent evaluated default waits for its key owner"

              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first evaluated default commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the evaluated-default waiter resumes"

              match waiting.GetAwaiter().GetResult() |> snd with
              | Err(1062, _) -> ()
              | result -> failtestf "expected the rebased insert to find the evaluated duplicate, got %A" result

              handle second "ROLLBACK" |> ignore

              match handle setup "SELECT id, base, derived FROM tx_expression_key" |> snd with
              | ResultSet(_, [ [ Some "1"; Some "10"; Some "11" ] ]) -> ()
              | result -> failtestf "expected only the first evaluated default row, got %A" result

          testCase "a failed prepared insert discards its trigger effects after rebasing"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_prepared_audit (id INT)"
              let setup, _ =
                  handle setup "CREATE TABLE tx_prepared_atomic (id INT PRIMARY KEY, base INT, derived INT DEFAULT (base + 1), UNIQUE KEY uq_derived (derived))"
              let setup, _ =
                  handle setup "CREATE TRIGGER tx_prepared_before BEFORE INSERT ON tx_prepared_atomic FOR EACH ROW INSERT INTO tx_prepared_audit VALUES (NEW.id)"

              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
              let second, _ = handle second "BEGIN"
              let first, firstInsert = handle first "INSERT INTO tx_prepared_atomic (id, base) VALUES (1, 10)"
              Expect.equal firstInsert (Affected 1UL) "the first prepared statement includes its trigger effect"

              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      handle second "INSERT INTO tx_prepared_atomic (id, base) VALUES (2, 10)")

              Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the second prepared statement waits"
              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first prepared statement commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the failed prepared statement resumes"

              let second, result = waiting.GetAwaiter().GetResult()

              match result with
              | Err(1062, _) -> ()
              | actual -> failtestf "expected the rebased duplicate error, got %A" actual

              match handle second "SELECT id FROM tx_prepared_audit ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | actual -> failtestf "expected only the committed trigger effect, got %A" actual

              handle second "ROLLBACK" |> ignore

          testCase "insert-select and replace-select claim prepared unique keys"
          <| fun _ ->
              let run replace =
                  let store = Fsdb.Storage.create ()
                  let setup = create 1 store
                  let setup, _ = handle setup "CREATE TABLE tx_select_source (id INT PRIMARY KEY, base INT)"
                  let setup, _ = handle setup "INSERT INTO tx_select_source VALUES (1, 10), (2, 10)"
                  let setup, _ =
                      handle setup "CREATE TABLE tx_select_key (id INT PRIMARY KEY, base INT, derived INT DEFAULT (base + 1), UNIQUE KEY uq_derived (derived))"

                  let verb = if replace then "REPLACE" else "INSERT"
                  let statement id =
                      sprintf "%s INTO tx_select_key (id, base) SELECT id, base FROM tx_select_source WHERE id = %d" verb id

                  let first, _ = handle (create 2 store) "BEGIN"
                  let second, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
                  let second, _ = handle second "BEGIN"
                  let first, firstWrite = handle first (statement 1)
                  Expect.equal firstWrite (Affected 1UL) "the first prepared SELECT row owns its key"

                  let waiting = Threading.Tasks.Task.Run(fun () -> handle second (statement 2))
                  Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the second prepared SELECT row waits"

                  Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first SELECT write commits"
                  Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the SELECT writer resumes"

                  let second, result = waiting.GetAwaiter().GetResult()

                  if replace then
                      Expect.equal result (Affected 2UL) "REPLACE deletes the committed conflict before inserting"
                      Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the rebased replacement commits"

                      match handle setup "SELECT id, base, derived FROM tx_select_key" |> snd with
                      | ResultSet(_, [ [ Some "2"; Some "10"; Some "11" ] ]) -> ()
                      | actual -> failtestf "expected the replacement selected by the waiter, got %A" actual
                  else
                      match result with
                      | Err(1062, _) -> ()
                      | actual -> failtestf "expected the rebased INSERT SELECT duplicate, got %A" actual

                      handle second "ROLLBACK" |> ignore

              run false
              run true

          testCase "a prepared upsert rebases onto its committed conflict"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ =
                  handle setup "CREATE TABLE tx_prepared_upsert (id INT PRIMARY KEY, base INT, derived INT DEFAULT (base + 1), marker VARCHAR(20), UNIQUE KEY uq_derived (derived))"

              let upsert id marker =
                  sprintf "INSERT INTO tx_prepared_upsert (id, base, marker) VALUES (%d, 10, '%s') ON DUPLICATE KEY UPDATE id = VALUES(id), marker = VALUES(marker)" id marker

              let mutable evaluations = 0
              let registry =
                  Fsdb.Functions.empty
                  |> Fsdb.Functions.registerScalar "PREPARE_ONCE" (function
                      | [ value ] ->
                          Threading.Interlocked.Increment(&evaluations) |> ignore
                          value
                      | _ -> VNull)

              let first, _ = handle (create 2 store) "BEGIN"
              let second = { create 3 store with CustomFunctions = registry }
              let second, _ = handle second "SET innodb_lock_wait_timeout = 5"
              let second, _ = handle second "BEGIN"
              let first, firstWrite = handle first (upsert 1 "first")
              Expect.equal firstWrite (Affected 1UL) "the first upsert inserts its prepared key"

              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      handle
                          second
                          "INSERT INTO tx_prepared_upsert (id, base, marker) VALUES (2, 10, PREPARE_ONCE('second')) ON DUPLICATE KEY UPDATE id = VALUES(id), marker = VALUES(marker)")
              Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the prepared upsert waits for its generated conflict"

              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first prepared upsert commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the prepared upsert resumes"

              let second, result = waiting.GetAwaiter().GetResult()
              Expect.equal result (Affected 2UL) "the waiter updates the newly committed conflict"
              Expect.equal evaluations 1 "the prepared expression is not replayed after the wait"
              Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the rebased upsert commits"

              match handle setup "SELECT id, base, derived, marker FROM tx_prepared_upsert" |> snd with
              | ResultSet(_, [ [ Some "2"; Some "10"; Some "11"; Some "second" ] ]) -> ()
              | actual -> failtestf "expected the rebased duplicate update, got %A" actual

          testCase "a prepared upsert refreshes its duplicate after the row wait"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ =
                  handle setup "CREATE TABLE tx_prepared_refresh (id INT PRIMARY KEY, base INT, derived INT DEFAULT (base + 1), marker VARCHAR(20), UNIQUE KEY uq_derived (derived))"
              let setup, _ = handle setup "INSERT INTO tx_prepared_refresh (id, base, marker) VALUES (1, 10, 'old')"

              let owner, _ = handle (create 2 store) "BEGIN"
              let waiter, _ = handle (create 3 store) "SET innodb_lock_wait_timeout = 5"
              let waiter, _ = handle waiter "BEGIN"
              let owner, updated = handle owner "UPDATE tx_prepared_refresh SET marker = 'owner' WHERE id = 1"
              Expect.equal updated (Affected 1UL) "the owner holds the duplicate row"

              let waiting =
                  Threading.Tasks.Task.Run(fun () ->
                      handle
                          waiter
                          "INSERT INTO tx_prepared_refresh (id, base, marker) VALUES (2, 10, 'candidate') ON DUPLICATE KEY UPDATE id = VALUES(id)")

              Expect.isFalse (waiting.Wait(TimeSpan.FromMilliseconds 100.0)) "the upsert waits for the duplicate row"
              Expect.equal (handle owner "COMMIT" |> snd) (Affected 0UL) "the owner's row version commits"
              Expect.isTrue (waiting.Wait(TimeSpan.FromSeconds 5.0)) "the duplicate-row waiter resumes"

              let waiter, result = waiting.GetAwaiter().GetResult()
              Expect.equal result (Affected 2UL) "the waiter updates the refreshed duplicate"
              Expect.equal (handle waiter "COMMIT" |> snd) (Affected 0UL) "the refreshed update commits"

              match handle setup "SELECT id, base, derived, marker FROM tx_prepared_refresh" |> snd with
              | ResultSet(_, [ [ Some "2"; Some "10"; Some "11"; Some "owner" ] ]) -> ()
              | actual -> failtestf "expected the committed marker to survive the rebased upsert, got %A" actual

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

          testCase "a transaction update reads the latest committed row after a snapshot read"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE up_t (id INT, v INT)"
              let setup, _ = handle setup "INSERT INTO up_t VALUES (1, 1)"

              let a = create 2 store
              let a, _ = handle a "BEGIN"
              let a, _ = handle a "SELECT v FROM up_t"

              handle (create 3 store) "UPDATE up_t SET v = 1234 WHERE id = 1" |> ignore

              let a, updateResult = handle a "UPDATE up_t SET v = v + 1 WHERE id = 1"
              Expect.equal updateResult (Affected 1UL) "the update changes the latest committed row"

              match handle a "COMMIT" |> snd with
              | Affected 0UL -> ()
              | result -> failtestf "expected the current-read update to commit, got %A" result

              match handle (create 4 store) "SELECT v FROM up_t" |> snd with
              | ResultSet(_, [ [ Some "1235" ] ]) -> ()
              | result -> failtestf "expected the increment to use the latest committed value, got %A" result

              ignore setup ]
