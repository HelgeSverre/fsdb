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

              // The current transaction gate deliberately serializes even
              // disjoint-table writers. Commit the first transaction so the
              // queued writer can run, then prove neither result was lost.
              let session, _ = handle session "COMMIT"
              ignore session
              let other, otherResult = otherInsert.GetAwaiter().GetResult()

              match otherResult with
              | Affected 1UL -> ()
              | result -> failtestf "expected the concurrent insert into a different table to succeed, got %A" result

              match handle other "SELECT id FROM tx_other" |> snd with
              | ResultSet(_, [ [ Some "99" ] ]) -> ()
              | result -> failtestf "expected the concurrent write to survive the commit, got %A" result

              match handle other "SELECT id FROM tx_m" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | result -> failtestf "expected the transaction's own write to also be there, got %A" result

          testCase "a transaction writing into a qualified other database holds THAT database's gate too, not just its own session database"
          <| fun _ ->
              // A qualified `INSERT INTO tx_db_y.t` must take tx_db_y's
              // gate, not only the session database's (`tx_db_x`) —
              // otherwise a concurrent autocommit writer to `tx_db_y` can
              // land its commit between this transaction's base-catalog
              // read and its COMMIT merge, losing one of the two rows
              // (`Storage.mergeDatabaseSlot`'s "batch's table wins
              // outright" rule).
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

              Expect.isFalse
                  (bInsert.Wait(TimeSpan.FromMilliseconds 300.0))
                  "the concurrent write to tx_db_y must wait for the open transaction's gate on tx_db_y, not race past it"

              let a, _ = handle a "COMMIT"
              ignore a

              Expect.isTrue (bInsert.Wait(TimeSpan.FromSeconds 5.0)) "the queued writer completed once the transaction released tx_db_y's gate"

              match bInsert.GetAwaiter().GetResult() |> snd with
              | Affected 1UL -> ()
              | result -> failtestf "expected the concurrent insert to succeed once unblocked, got %A" result

              match handle b "SELECT id FROM t ORDER BY id" |> snd with
              | ResultSet(_, [ [ Some "1" ]; [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both the transaction's row and the concurrent row to survive — neither lost, got %A" result

          testCase "an open transaction in one database doesn't block a write to an unrelated database"
          <| fun _ ->
              // The transaction gate is one `SemaphoreSlim` per database — a
              // single store-wide semaphore would serialize every
              // connection's writes behind any one open transaction,
              // anywhere, collapsing a parallel test suite (each worker in
              // its own database) to fully serial.
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

          testCase "a write gate that doesn't clear within its timeout raises a retryable 1205, not an indefinite hang"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              use held = Fsdb.Storage.enterTransactionGate store Fsdb.Storage.defaultDatabase (TimeSpan.FromSeconds 30.0)

              try
                  Fsdb.Storage.enterTransactionGate store Fsdb.Storage.defaultDatabase (TimeSpan.FromMilliseconds 50.0)
                  |> ignore

                  failtest "expected the second waiter to time out"
              with Fsdb.Storage.LockWaitTimeout db ->
                  Expect.equal db Fsdb.Storage.defaultDatabase "names the database still holding the gate"

          testCase "an exception mid-statement releases the transaction gate it just acquired instead of leaking it"
          <| fun _ ->
              // A killed client's query cancellation (`Server.watchForDisconnect`
              // flips `Storage.queryCancellation`) can land *after*
              // `startTransactionStatement` has already acquired this
              // database's transaction gate but *before* `execute` returns —
              // the only place that acquired lease is referenced. Every path
              // that turns an exception here into a reply (`handle`'s
              // catch-all, and its deliberate `OperationCanceledException`
              // reraise for `Server`'s command loop) hands back the
              // *original* pre-statement `Session`, whose `Tx.GateLease` is
              // still `None` — so nothing downstream (a later ROLLBACK, or
              // connection cleanup's `closeSession`) ever disposes the lease
              // that really did decrement the semaphore. Left unhandled, the
              // gate stays decremented forever and every future transaction
              // against this database hangs.
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

              // The gate must not be leaked: a fresh transaction against the
              // same (default) database has to be able to acquire it right
              // away rather than hang.
              use acquired =
                  Fsdb.Storage.enterTransactionGate store Fsdb.Storage.defaultDatabase (TimeSpan.FromSeconds 5.0)

              ignore acquired

          testCase "an exception on a transaction's second statement aborts the whole transaction, not just leaks its gate"
          <| fun _ ->
              // Merely disposing the just-acquired lease on the statement
              // that throws isn't enough: `TransactionGates`'s whole
              // contract is that a transaction holds its database's gate
              // *continuously* from its first real statement through
              // COMMIT/ROLLBACK, so `mergeCatalogInto`'s three-way merge
              // (`baseCatalog` captured at BEGIN vs. the live catalog *right
              // now*) never races a concurrent committer. Freeing the gate
              // mid-transaction and then leaving `session.Tx` still `Some`
              // — its `BaseCatalog`/`Snapshot` now stale — lets a later
              // COMMIT on this same (zombie) transaction silently clobber
              // whatever another transaction committed to this database in
              // the gap, a real lost update. The whole transaction must die
              // with the statement that broke it.
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE TABLE tx_abort (id INT PRIMARY KEY, v DECIMAL(10,2))"
              let session, _ = handle session "INSERT INTO tx_abort VALUES (1, 1)"
              let session, _ = handle session "BEGIN"
              // First statement: a normal write that really does acquire
              // and hold the gate.
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

              use acquired =
                  Fsdb.Storage.enterTransactionGate store Fsdb.Storage.defaultDatabase (TimeSpan.FromSeconds 5.0)

              ignore acquired

          testCase "concurrent transactions updating the same table serialize without losing a committed increment"
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

              use secondStarted = new Threading.ManualResetEventSlim(false)

              let secondUpdate =
                  Threading.Tasks.Task.Run(fun () ->
                      secondStarted.Set()
                      handle second "UPDATE tx_hot SET n = n + 1 WHERE id = 1")

              Expect.isTrue (secondStarted.Wait(TimeSpan.FromSeconds 1.0)) "the second writer started"
              let first, _ = handle first "COMMIT"
              ignore first
              let second, secondResult = secondUpdate.GetAwaiter().GetResult()

              match secondResult with
              | Affected 1UL -> ()
              | result -> failtestf "expected the queued increment to succeed, got %A" result

              let second, _ = handle second "COMMIT"

              match handle second "SELECT n FROM tx_hot WHERE id = 1" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | result -> failtestf "expected both committed increments to survive, got %A" result

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

          testCase "a transaction that has only read holds no gate, so another connection can still write"
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

          testCase "a read-only transaction that later writes fails retryably rather than clobbering a commit it never saw"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setup = create 1 store
              let setup, _ = handle setup "CREATE TABLE up_t (id INT, v INT)"
              let setup, _ = handle setup "INSERT INTO up_t VALUES (1, 1)"

              let a = create 2 store
              let a, _ = handle a "BEGIN"
              let a, _ = handle a "SELECT v FROM up_t"

              handle (create 3 store) "UPDATE up_t SET v = 1234 WHERE id = 1" |> ignore

              match handle a "UPDATE up_t SET v = 7 WHERE id = 1" |> snd with
              | Err(1205, _) -> ()
              | result -> failtestf "expected a retryable 1205 on the stale write, got %A" result

              match handle (create 4 store) "SELECT v FROM up_t" |> snd with
              | ResultSet(_, [ [ Some "1234" ] ]) -> ()
              | result -> failtestf "expected the concurrent commit to survive intact, got %A" result

              ignore setup ]
