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

              let conn = Fsdb.Db.connect (Fsdb.Db.create () |> Fsdb.Db.registerTable t)
              conn.Query "CREATE TABLE fsdb.v (n INT)" |> ignore

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
