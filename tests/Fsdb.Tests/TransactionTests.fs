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
        [ testCase "a write inside BEGIN...COMMIT is invisible to another connection until commit"
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
              let session, _ = handle session "SAVEPOINT sp1"
              let session, _ = handle session "INSERT INTO tx_s VALUES (2)"
              let session, _ = handle session "ROLLBACK TO SAVEPOINT sp1"
              let session, _ = handle session "COMMIT"

              match handle session "SELECT id FROM tx_s ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected only the pre-savepoint row, got %A" result

          testCase "SET autocommit = 0 opens an implicit transaction; SET autocommit = 1 commits it"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_ac (id INT)"
              let session, _ = handle session "SET autocommit = 0"
              let session, _ = handle session "INSERT INTO tx_ac VALUES (1)"
              let other = create 2 store

              match handle other "SELECT id FROM tx_ac" |> snd with
              | ResultSet(_, []) -> ()
              | result -> failtestf "expected no rows visible before autocommit = 1, got %A" result

              let session, _ = handle session "SET autocommit = 1"
              ignore session

              match handle other "SELECT id FROM tx_ac" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the row visible once autocommit = 1 commits it, got %A" result

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

          testCase "concurrent transactions rebase updates to the same row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let first = create 1 store
              let first, _ = handle first "CREATE TABLE tx_hot (id INT PRIMARY KEY, n INT)"
              let first, _ = handle first "INSERT INTO tx_hot VALUES (1, 0)"
              let first, _ = handle first "BEGIN"
              let second, _ = handle (create 2 store) "BEGIN"
              let first, firstUpdate = handle first "UPDATE tx_hot SET n = n + 1 WHERE id = 1"

              match firstUpdate with
              | Affected 1UL -> ()
              | result -> failtestf "expected the first increment to succeed, got %A" result

              let second, secondResult = handle second "UPDATE tx_hot SET n = n + 1 WHERE id = 1"

              match secondResult with
              | Affected 1UL -> ()
              | result -> failtestf "expected the second snapshot update to succeed locally, got %A" result

              let _, firstCommit = handle first "COMMIT"
              Expect.equal firstCommit (Affected 0UL) "the first writer commits"

              Expect.equal (handle second "COMMIT" |> snd) (Affected 0UL) "the overlapping writer rebases and commits"

              match handle (create 3 store) "SELECT n FROM tx_hot WHERE id = 1" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both increments to survive, got %A" result

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

          testCase "concurrent transaction identity uses primary-key collation"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup, _ = handle (create 1 store) "CREATE TABLE tx_text_key (id VARCHAR(20) PRIMARY KEY)"
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "BEGIN"
              let first, _ = handle first "INSERT INTO tx_text_key VALUES ('A')"
              let second, _ = handle second "INSERT INTO tx_text_key VALUES ('a')"
              let _, firstCommit = handle first "COMMIT"
              Expect.equal firstCommit (Affected 0UL) "the first spelling commits"

              match handle second "COMMIT" |> snd with
              | Err(1205, _) -> ()
              | result -> failtestf "expected the collation-equivalent key to be rejected, got %A" result

              match handle setup "SELECT id FROM tx_text_key" |> snd with
              | ResultSet(_, [ [ Some "A" ] ]) -> ()
              | result -> failtestf "expected only the first key to remain, got %A" result

          testCase "concurrent transactions cannot commit the same primary key"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE tx_unique (id INT PRIMARY KEY)"
              let first, _ = handle (create 2 store) "BEGIN"
              let second, _ = handle (create 3 store) "BEGIN"
              let first, firstInsert = handle first "INSERT INTO tx_unique VALUES (1)"
              let second, secondInsert = handle second "INSERT INTO tx_unique VALUES (1)"

              Expect.equal firstInsert (Affected 1UL) "the first snapshot accepts the key"
              Expect.equal secondInsert (Affected 1UL) "the second snapshot accepts the key"
              Expect.equal (handle first "COMMIT" |> snd) (Affected 0UL) "the first transaction commits"

              match handle second "COMMIT" |> snd with
              | Err(1205, _) -> ()
              | result -> failtestf "expected the conflicting commit to fail, got %A" result

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

          testCase "a read-only transaction rebases a later write over a concurrent commit"
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

              Expect.equal (handle a "COMMIT" |> snd) (Affected 0UL) "the stale writer replays on the live row"

              match handle (create 4 store) "SELECT v FROM up_t" |> snd with
              | ResultSet(_, [ [ Some "7" ] ]) -> ()
              | result -> failtestf "expected the later assignment to commit, got %A" result

              ignore setup ]
