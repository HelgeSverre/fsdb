module Fsdb.Tests.EmbeddingIntegrationTests

open System
open Expecto
open Fsdb.Binary
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let tests =
    testList
        "Embedding integration"
        [
          testCase "embedded prepared queries preserve typed values and parameter semantics"
          <| fun _ ->
              use conn = Fsdb.Db.create () |> Fsdb.Db.connect
              conn.Query "CREATE TABLE typed_items (id BIGINT PRIMARY KEY, amount DECIMAL(10,2), payload BLOB)" |> ignore

              match conn.Execute("INSERT INTO typed_items VALUES (?,?,?)", [ VInt 7L; VDecimal 12.50M; VBytes [| 0uy; 255uy |] ]) with
              | Affected 1UL -> ()
              | other -> failtestf "expected one insert, got %A" other

              match conn.QueryValues("SELECT id,amount,payload FROM typed_items WHERE id=?", [ VInt 7L ]) with
              | Ok([ "id"; "amount"; "payload" ], [ [ Some(VInt 7L); Some(VDecimal 12.50M); Some(VBytes bytes) ] ]) ->
                  Expect.sequenceEqual bytes [| 0uy; 255uy |] "binary value survives without text rendering"
                  bytes.[0] <- 42uy
              | other -> failtestf "expected typed row, got %A" other

              match conn.QueryValues("SELECT payload FROM typed_items WHERE id=?", [ VInt 7L ]) with
              | Ok(_, [ [ Some(VBytes bytes) ] ]) ->
                  Expect.sequenceEqual bytes [| 0uy; 255uy |] "caller mutation cannot change stored bytes"
              | other -> failtestf "expected unchanged binary row, got %A" other

          testCase "embedded async query observes cancellation and typed values"
          <| fun _ ->
              use conn = Fsdb.Db.create () |> Fsdb.Db.connect
              use cancelled = new Threading.CancellationTokenSource()
              cancelled.Cancel()
              Expect.throwsT<Threading.Tasks.TaskCanceledException>
                  (fun () -> conn.QueryAsync("SELECT 1", cancelled.Token).GetAwaiter().GetResult() |> ignore)
                  "pre-cancelled query does not run"

              let columns, rows =
                  match conn.QueryValuesAsync("SELECT ? AS answer", [ VInt 42L ], Threading.CancellationToken.None).GetAwaiter().GetResult() with
                  | Ok result -> result
                  | Error error -> failtestf "unexpected async query error: %A" error
              Expect.equal columns [ "answer" ] "projection name"
              Expect.equal rows [ [ Some(VInt 42L) ] ] "typed async row"

          testCase "prepared command reuses SQL with changing values and owns its lifetime"
          <| fun _ ->
              use conn = Fsdb.Db.create () |> Fsdb.Db.connect
              conn.Query "CREATE TABLE prepared_items (id INT PRIMARY KEY, payload BLOB)" |> ignore
              let prepare sql =
                  match conn.Prepare sql with
                  | Ok command -> command
                  | Error error -> failtestf "prepare failed: %A" error
              use insert = prepare "INSERT INTO prepared_items VALUES (?, ?)"
              for id in 1L..3L do
                  match insert.Execute [ VInt id; VBytes [| byte id |] ] with
                  | Affected 1UL -> ()
                  | other -> failtestf "insert failed: %A" other
              use select = prepare "SELECT payload FROM prepared_items WHERE id=?"
              for id in 1L..3L do
                  match select.QueryValues [ VInt id ] with
                  | Ok(_, [ [ Some(VBytes bytes) ] ]) -> Expect.sequenceEqual bytes [| byte id |] "selected payload"
                  | other -> failtestf "select failed: %A" other
              match select.QueryValuesAsync([ VInt 2L ], Threading.CancellationToken.None).GetAwaiter().GetResult() with
              | Ok(_, [ [ Some(VBytes bytes) ] ]) -> Expect.sequenceEqual bytes [| 2uy |] "async payload"
              | other -> failtestf "async select failed: %A" other
              (select :> IDisposable).Dispose()
              Expect.throwsT<ObjectDisposedException> (fun () -> select.Execute [ VInt 1L ] |> ignore) "disposed command"
              (conn :> IDisposable).Dispose()
              Expect.throwsT<ObjectDisposedException> (fun () -> insert.Execute [ VInt 4L; VNull ] |> ignore) "disposed connection"

          testCase "prepared command reports invalid SQL at preparation"
          <| fun _ ->
              use conn = Fsdb.Db.create () |> Fsdb.Db.connect
              match conn.Prepare "SELECT FROM" with
              | Error _ -> ()
              | Ok _ -> failtest "invalid SQL should fail to prepare"

          testCase "session functions reflect changes after repeated lookups"
          <| fun _ ->
              use conn = Fsdb.Db.create () |> Fsdb.Db.connect
              let one sql =
                  match conn.Query sql with
                  | ResultSet(_, [ [ value ] ]) -> value
                  | other -> failtestf "expected one value from %s, got %A" sql other

              conn.Query "CREATE DATABASE registry_a" |> ignore
              conn.Query "CREATE DATABASE registry_b" |> ignore
              conn.Query "USE registry_a" |> ignore
              Expect.equal (one "SELECT DATABASE()") (Some "registry_a") "initial database"
              Expect.equal (one "SELECT DATABASE()") (Some "registry_a") "repeated lookup"
              conn.Query "USE registry_b" |> ignore
              Expect.equal (one "SELECT DATABASE()") (Some "registry_b") "database change"

              conn.Query "CREATE TABLE ids (id INT AUTO_INCREMENT PRIMARY KEY)" |> ignore
              conn.Query "INSERT INTO ids VALUES (NULL)" |> ignore
              Expect.equal (one "SELECT LAST_INSERT_ID()") (Some "1") "generated identity"
              conn.Query "INSERT INTO ids VALUES (NULL)" |> ignore
              Expect.equal (one "SELECT LAST_INSERT_ID()") (Some "2") "new generated identity"

              conn.Query "SET time_zone = '+02:00'" |> ignore
              Expect.equal (one "SELECT FROM_UNIXTIME(0)") (Some "1970-01-01 02:00:00") "first timezone"
              conn.Query "SET time_zone = '+03:00'" |> ignore
              Expect.equal (one "SELECT FROM_UNIXTIME(0)") (Some "1970-01-01 03:00:00") "updated timezone"

          testCase "Db.connect persists USE and session state across queries, with registered functions in scope"
          <| fun _ ->
              let shout =
                  function
                  | [ VString s ] -> VString(s.ToUpperInvariant())
                  | _ -> VNull

              let db = Fsdb.Db.create () |> Fsdb.Db.registerScalar "SHOUT" shout
              let conn = Fsdb.Db.connect db
              conn.Query "CREATE DATABASE app" |> ignore
              conn.Query "USE app" |> ignore
              conn.Query "CREATE TABLE t (n INT)" |> ignore
              conn.Query "INSERT INTO t VALUES (7)" |> ignore

              match conn.Query "SELECT n, SHOUT('hi') FROM t" with
              | ResultSet(_, [ [ Some "7"; Some "HI" ] ]) -> ()
              | other -> failtestf "expected the USE'd database's row plus the custom scalar, got %A" other

          testCase "two Db.onCommit subscribers both receive one TransactionCommitted per transaction"
          <| fun _ ->
              let a = ResizeArray<Fsdb.Storage.CommitEvent>()
              let b = ResizeArray<Fsdb.Storage.CommitEvent>()

              let db = Fsdb.Db.create () |> Fsdb.Db.onCommit a.Add |> Fsdb.Db.onCommit b.Add
              let conn = Fsdb.Db.connect db
              conn.Query "CREATE TABLE t (n INT)" |> ignore
              conn.Query "BEGIN" |> ignore
              conn.Query "INSERT INTO t VALUES (1)" |> ignore
              conn.Query "INSERT INTO t VALUES (2)" |> ignore
              conn.Query "COMMIT" |> ignore

              let commits (events: ResizeArray<Fsdb.Storage.CommitEvent>) =
                  events
                  |> Seq.filter (function
                      | Fsdb.Storage.TransactionCommitted inner -> List.length inner = 2
                      | _ -> false)
                  |> Seq.length

              Expect.equal (commits a) 1 "subscriber A sees exactly one TransactionCommitted wrapping both inserts"
              Expect.equal (commits b) 1 "subscriber B sees the same single TransactionCommitted"

          testCase "queued commit subscription unregisters on dispose"
          <| fun _ ->
              let db = Fsdb.Db.create ()
              let subscription = Fsdb.Db.subscribeCommits db
              use conn = Fsdb.Db.connect db
              conn.Query "CREATE TABLE queued_events (n INT)" |> ignore
              let mutable first = Unchecked.defaultof<Fsdb.Storage.CommitEvent>
              Expect.isTrue (subscription.Reader.TryRead(&first)) "the queue receives committed DDL"
              (subscription :> IDisposable).Dispose()
              conn.Query "INSERT INTO queued_events VALUES (1)" |> ignore
              let mutable later = Unchecked.defaultof<Fsdb.Storage.CommitEvent>
              Expect.isFalse (subscription.Reader.TryRead(&later)) "disposed subscription receives no new event"

          testCase "owned database host checkpoints and closes its connections"
          <| fun _ ->
              let directory = IO.Path.Combine(IO.Path.GetTempPath(), "fsdb-owned-" + Guid.NewGuid().ToString("N"))
              try
                  let connection =
                      use host = Fsdb.Db.create () |> Fsdb.Db.withDataDir directory |> Fsdb.Db.own
                      let conn = host.Connect()
                      conn.Query "CREATE TABLE owned_rows (n INT)" |> ignore
                      conn.Query "INSERT INTO owned_rows VALUES (8)" |> ignore
                      conn

                  Expect.throwsT<ObjectDisposedException>
                      (fun () -> connection.Query "SELECT 1" |> ignore)
                      "the host closes owned connections"

                  use reopened = Fsdb.Db.create () |> Fsdb.Db.withDataDir directory |> Fsdb.Db.own
                  use conn = reopened.Connect()
                  match conn.Query "SELECT n FROM owned_rows" with
                  | ResultSet(_, [ [ Some "8" ] ]) -> ()
                  | other -> failtestf "expected checkpointed row, got %A" other
              finally
                  if IO.Directory.Exists directory then IO.Directory.Delete(directory, true)

          testCase "virtual table scan receives safe equality and limit hints"
          <| fun _ ->
              let requests = ResizeArray<Fsdb.Functions.VirtualTableRequest>()
              let source () = [ for n in 1L .. 5L -> [| VInt n |] ]
              let table =
                  Fsdb.Functions.VirtualTable.create "numbers" [ Fsdb.Functions.VirtualTable.bigint "n" ] source
                  |> Fsdb.Functions.VirtualTable.withScan (fun request ->
                      requests.Add request
                      source ()
                      |> List.filter (fun row ->
                          request.Equalities
                          |> List.forall (fun (column, value) -> column = "n" && row.[0] = value))
                      |> fun rows ->
                          match request.Limit with
                          | Some count -> List.truncate count rows
                          | None -> rows)
              use conn = Fsdb.Db.create () |> Fsdb.Db.registerTable table |> Fsdb.Db.connect
              match conn.Query "SELECT n FROM fsdb.numbers WHERE n = 4" with
              | ResultSet(_, [ [ Some "4" ] ]) -> ()
              | other -> failtestf "expected narrowed row, got %A" other
              Expect.equal requests.[requests.Count - 1].Equalities [ "n", VInt 4L ] "equality reaches provider"
              match conn.Query "SELECT n FROM fsdb.numbers LIMIT 2" with
              | ResultSet(_, [ [ Some "1" ]; [ Some "2" ] ]) -> ()
              | other -> failtestf "expected bounded rows, got %A" other
              Expect.equal requests.[requests.Count - 1].Limit (Some 2) "safe limit reaches provider"
              match conn.Query "SELECT COUNT(*) FROM fsdb.numbers LIMIT 2" with
              | ResultSet(_, [ [ Some "5" ] ]) -> ()
              | other -> failtestf "aggregate must see all rows, got %A" other
              Expect.equal requests.[requests.Count - 1].Limit None "aggregate suppresses early limit"

          testCase "Db.serve exposes its bound address and port until stopped"
          <| fun _ ->
              let running = Fsdb.Db.create () |> Fsdb.Db.serve Net.IPAddress.Loopback 0
              Expect.equal running.Address Net.IPAddress.Loopback "address matches the bound listener"
              Expect.isTrue (running.Port > 0) "port 0 resolves to the OS-assigned port"

              use alive = new Net.Sockets.TcpClient()
              alive.Connect(running.Address, running.Port)
              Expect.isTrue alive.Connected "connects while running"

              running.Stop()

              Expect.throws
                  (fun () ->
                      use dead = new Net.Sockets.TcpClient()
                      dead.Connect(running.Address, running.Port))
                  "connections are refused after Stop"

          testCase "registered virtual table answers SELECT/WHERE/JOIN from the fsdb schema"
          <| fun _ ->
              let models =
                  Fsdb.Functions.VirtualTable.create
                      "models"
                      [ Fsdb.Functions.VirtualTable.text "name"
                        Fsdb.Functions.VirtualTable.int "dim" ]
                      (fun () ->
                          [ [| VString "small"; VInt 384L |]
                            [| VString "large"; VInt 1536L |] ])

              let db = Fsdb.Db.create () |> Fsdb.Db.registerTable models
              let conn = Fsdb.Db.connect db

              match conn.Query "SELECT name FROM fsdb.models WHERE dim > 1000" with
              | ResultSet(_, [ [ Some "large" ] ]) -> ()
              | other -> failtestf "expected WHERE to post-filter the virtual rows, got %A" other

              Expect.equal (conn.Query "CREATE DATABASE test") (Affected 1UL) "create the physical schema"
              conn.Query "CREATE TABLE test.prefs (model VARCHAR(50), fave INT)" |> ignore
              conn.Query "INSERT INTO test.prefs VALUES ('small', 1), ('large', 0)" |> ignore

              match conn.Query "SELECT m.dim FROM fsdb.models m JOIN test.prefs p ON p.model = m.name WHERE p.fave = 1" with
              | ResultSet(_, [ [ Some "384" ] ]) -> ()
              | other -> failtestf "expected the virtual table to join a real one, got %A" other

          testCase "empty virtual-table registry adds nothing to the fsdb schema"
          <| fun _ ->
              let conn = Fsdb.Db.connect (Fsdb.Db.create ())

              (match conn.Query "SELECT * FROM fsdb.models" with
               | Err _ -> ()
               | other -> failtestf "expected an error for an unregistered virtual table, got %A" other)

              // With the real default `fsdb` database dropped and nothing
              // registered, the schema is gone entirely — no SHOW DATABASES
              // entry, and USE gets a real 1049.
              conn.Query "DROP DATABASE fsdb" |> ignore

              (match conn.Query "SHOW DATABASES" with
               | ResultSet(_, rows) ->
                   Expect.isFalse (rows |> List.exists (fun r -> r = [ Some "fsdb" ])) "SHOW DATABASES omits fsdb"
               | other -> failtestf "expected a result set, got %A" other)

              match conn.Query "USE fsdb" with
              | Err(1049, _) -> ()
              | other -> failtestf "expected 1049 for USE fsdb on an empty registry, got %A" other

          testCase "registered tables keep the fsdb schema alive even without the real database"
          <| fun _ ->
              let t =
                  Fsdb.Functions.VirtualTable.create "models" [ Fsdb.Functions.VirtualTable.text "name" ] (fun () ->
                      [ [| VString "m" |] ])

              let conn = Fsdb.Db.connect (Fsdb.Db.create () |> Fsdb.Db.registerTable t)
              conn.Query "DROP DATABASE fsdb" |> ignore

              (match conn.Query "SHOW DATABASES" with
               | ResultSet(_, rows) ->
                   Expect.isTrue (rows |> List.exists (fun r -> r = [ Some "fsdb" ])) "SHOW DATABASES lists fsdb"
               | other -> failtestf "expected a result set, got %A" other)

              (match conn.Query "SHOW TABLES FROM fsdb" with
               | ResultSet([ "Tables_in_fsdb" ], [ [ Some "models" ] ]) -> ()
               | other -> failtestf "expected the registered table listed, got %A" other)

              (match conn.Query "USE fsdb" with
               | Affected 0UL -> ()
               | other -> failtestf "expected USE fsdb to work with a non-empty registry, got %A" other)

              match conn.Query "SELECT name FROM models" with
              | ResultSet(_, [ [ Some "m" ] ]) -> ()
              | other -> failtestf "expected an unqualified select after USE fsdb, got %A" other

              // Anything SHOW TABLES lists must be describable — ORMs
              // introspect via DESCRIBE — even here, where no real `fsdb`
              // database backs the schema the registry keeps alive.
              (match conn.Query "DESCRIBE fsdb.models" with
               | ResultSet("Field" :: _, [ Some "name" :: _ ]) -> ()
               | other -> failtestf "expected DESCRIBE to render the virtual columns, got %A" other)

              match conn.Query "SHOW COLUMNS FROM fsdb.models" with
              | ResultSet("Field" :: _, [ Some "name" :: _ ]) -> ()
              | other -> failtestf "expected SHOW COLUMNS to render the virtual columns, got %A" other

          testCase "a registered virtual table overlays a same-named real table, others resolve unchanged"
          <| fun _ ->
              let t =
                  Fsdb.Functions.VirtualTable.create "v" [ Fsdb.Functions.VirtualTable.int "n" ] (fun () ->
                      [ [| VInt 42L |] ])

              let db = Fsdb.Db.create ()
              let seed = Fsdb.Db.connect db
              seed.Query "CREATE TABLE fsdb.v (n INT, KEY ix_n(n))" |> ignore
              seed.Query "INSERT INTO fsdb.v VALUES (1), (2)" |> ignore
              let conn = Fsdb.Db.connect (db |> Fsdb.Db.registerTable t)

              // The overlay is read-only: a write addressed to the
              // registered name must error (1036) rather than silently land
              // in the shadowed real table while SELECT keeps answering
              // from the overlay — the host would lose read-your-writes.
              (match conn.Query "INSERT INTO fsdb.v VALUES (1)" with
               | Err(1036, _) -> ()
               | other -> failtestf "expected 1036 for INSERT into a virtual table, got %A" other)

              (match conn.Query "UPDATE fsdb.v SET n = 2" with
               | Err(1036, _) -> ()
               | other -> failtestf "expected 1036 for UPDATE of a virtual table, got %A" other)

              (match conn.Query "DELETE FROM fsdb.v" with
               | Err(1036, _) -> ()
               | other -> failtestf "expected 1036 for DELETE from a virtual table, got %A" other)

              (match conn.Query "DROP TABLE fsdb.v" with
               | Err(1036, _) -> ()
               | other -> failtestf "expected 1036 for DROP of a virtual table, got %A" other)

              conn.Query "CREATE TABLE fsdb.real_t (n INT)" |> ignore
              conn.Query "INSERT INTO fsdb.real_t VALUES (7)" |> ignore

              (match conn.Query "SELECT n FROM fsdb.v" with
               | ResultSet(_, [ [ Some "42" ] ]) -> ()
               | other -> failtestf "expected the virtual table to win the name collision, got %A" other)

              (match conn.Query "SELECT n FROM fsdb.v WHERE n >= 0" with
               | ResultSet(_, [ [ Some "42" ] ]) -> ()
               | other -> failtestf "expected range predicates to retain the virtual overlay, got %A" other)

              (match conn.Query "EXPLAIN SELECT n FROM fsdb.v WHERE n >= 0" with
               | ResultSet(_, [ row ]) ->
                   Expect.equal row.[2] (Some "v") "EXPLAIN names the virtual source"
                   Expect.equal row.[4] (Some "ALL") "the virtual source uses its ordinary scan"
                   Expect.equal row.[6] None "the shadowed physical key is not disclosed"
               | other -> failtestf "expected virtual-table EXPLAIN, got %A" other)

              (match conn.Query "SELECT n FROM fsdb.real_t" with
               | ResultSet(_, [ [ Some "7" ] ]) -> ()
               | other -> failtestf "expected other real fsdb tables to stay reachable, got %A" other)

              // SHOW FULL TABLES types the overlay SYSTEM VIEW and dedupes
              // the shadowed same-named real table away.
              match conn.Query "SHOW FULL TABLES FROM fsdb" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "real_t"; Some "BASE TABLE" ]; [ Some "v"; Some "SYSTEM VIEW" ] ]
                      "overlay and real tables list once each with their own types"
              | other -> failtestf "expected a result set, got %A" other
        ]
