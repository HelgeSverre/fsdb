module Fsdb.Tests.Program

open System
open Expecto
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Session
open Fsdb.QueryHandler

let packetTests =
    testList
        "Packet"
        [ testCase "lenenc int round-trips small values"
          <| fun _ ->
              for v in [ 0UL; 1UL; 250UL ] do
                  let w = Writer()
                  w.WriteLenEncInt v
                  let r = Reader(w.ToArray())
                  Expect.equal (r.ReadLenEncInt ()) (Some v) (sprintf "lenenc int %d" v)

          testCase "lenenc int round-trips 2-byte, 3-byte, and 8-byte values"
          <| fun _ ->
              for v in [ 251UL; 65535UL; 65536UL; 16777215UL; 16777216UL; 4294967295UL ] do
                  let w = Writer()
                  w.WriteLenEncInt v
                  let r = Reader(w.ToArray())
                  Expect.equal (r.ReadLenEncInt ()) (Some v) (sprintf "lenenc int %d" v)

          testCase "lenenc string round-trips"
          <| fun _ ->
              let w = Writer()
              w.WriteLenEncString "hello, world"
              let r = Reader(w.ToArray())
              Expect.equal (r.ReadLenEncString ()) (Some "hello, world") "lenenc string"

          testCase "lenenc null decodes to None"
          <| fun _ ->
              let w = Writer()
              w.WriteLenEncNull ()
              let r = Reader(w.ToArray())
              Expect.equal (r.ReadLenEncInt ()) None "lenenc null"

          testCase "null-terminated string round-trips"
          <| fun _ ->
              let w = Writer()
              w.WriteNullTerminatedString "root"
              w.WriteByte 0xAAuy
              let r = Reader(w.ToArray())
              Expect.equal (r.ReadNullTerminatedString ()) "root" "null-terminated string"
              Expect.equal (r.ReadByte ()) 0xAAuy "byte after string"

          testCase "packet framing round-trips through a MemoryStream"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream()
                  let original = { SeqId = 3uy; Payload = [| 1uy; 2uy; 3uy; 4uy; 5uy |] }
                  do! writePacketAsync stream original
                  stream.Position <- 0L
                  let! result = readPacketAsync stream

                  match result with
                  | Some p ->
                      Expect.equal p.SeqId original.SeqId "seq id"
                      Expect.equal p.Payload original.Payload "payload"
                  | None -> failtest "expected a packet"
              }
              |> Async.RunSynchronously

          testCase "readPacketAsync returns None on clean disconnect"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream([||])
                  let! result = readPacketAsync stream
                  Expect.equal result None "empty stream yields no packet"
              }
              |> Async.RunSynchronously ]

let protocolTests =
    testList
        "Protocol"
        [ testCase "HandshakeV10 payload starts with protocol version 10 and the server version"
          <| fun _ ->
              let authData = Array.create 20 1uy
              let payload = buildHandshakeV10 42 authData
              let r = Reader(payload)
              Expect.equal (r.ReadByte ()) 10uy "protocol version"
              Expect.equal (r.ReadNullTerminatedString ()) ServerVersion "server version string"
              Expect.equal (r.ReadInt32LE ()) 42 "connection id"

          testCase "HandshakeV10 payload declares CLIENT_PLUGIN_AUTH and ends with the plugin name"
          <| fun _ ->
              let payload = buildHandshakeV10 1 (Array.create 20 1uy)
              let text = Text.Encoding.ASCII.GetString payload
              Expect.stringContains text "mysql_native_password" "auth plugin name present"

          testCase "OK payload starts with 0x00 header"
          <| fun _ ->
              let payload = okPayload ClientProtocol41 0UL 0UL
              Expect.equal payload.[0] 0uy "OK header byte"

          testCase "ERR payload carries the error code and message"
          <| fun _ ->
              let payload = errPayload ClientProtocol41 1064 "bad syntax"
              Expect.equal payload.[0] 0xffuy "ERR header byte"
              let r = Reader(payload.[1..])
              Expect.equal (r.ReadInt16LE ()) 1064 "error code"

          testCase "the resultset-terminating OK uses header 0xfe, not 0x00"
          <| fun _ ->
              // Regression: mysql CLI distinguishes this from a plain OK by the
              // 0xfe header (it reuses the legacy EOF marker byte). Sending 0x00
              // here makes mysql_use_result callers (e.g. the CLI's startup
              // banner query) hang forever waiting for a terminator that never
              // looks like one.
              let payload = okEndOfResultSetPayload ClientProtocol41
              Expect.equal payload.[0] 0xfeuy "end-of-resultset OK header byte"

          testCase "parseHandshakeResponse reads username and capabilities"
          <| fun _ ->
              let w = Writer()
              w.WriteInt32LE(int (ClientProtocol41 ||| ClientSecureConnection))
              w.WriteInt32LE 16777216 // max packet size
              w.WriteByte 45uy // charset
              w.WriteBytes(Array.zeroCreate<byte> 23) // reserved
              w.WriteNullTerminatedString "root"
              w.WriteByte 0uy // zero-length auth response
              let resp = parseHandshakeResponse (w.ToArray())
              Expect.equal resp.Username "root" "username"
              Expect.equal resp.Database None "no database requested" ]

let queryHandlerTests =
    testList
        "QueryHandler"
        [ testCase "SELECT 1 returns a single row with column name '1'"
          <| fun _ ->
              let session = create 1

              match handle session "SELECT 1" |> snd with
              | ResultSet(cols, rows) ->
                  Expect.equal cols [ "1" ] "column name"
                  Expect.equal rows [ [ Some "1" ] ] "row value"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@version returns the server version"
          <| fun _ ->
              let session = create 1

              match handle session "SELECT @@version" |> snd with
              | ResultSet(cols, [ [ Some v ] ]) ->
                  Expect.equal cols [ "@@version" ] "column name"
                  Expect.equal v ServerVersion "version value"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@version, @@version_comment returns both columns"
          <| fun _ ->
              let session = create 1

              match handle session "SELECT @@version, @@version_comment" |> snd with
              | ResultSet(cols, [ row ]) ->
                  Expect.equal cols [ "@@version"; "@@version_comment" ] "columns"
                  Expect.equal (List.length row) 2 "row has two values"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@version_comment LIMIT 1 tolerates the trailing LIMIT clause"
          <| fun _ ->
              // Regression: mysql CLI probes the connection banner with exactly
              // this query at connect time.
              let session = create 1

              match handle session "select @@version_comment limit 1" |> snd with
              | ResultSet([ "@@version_comment" ], [ [ Some _ ] ]) -> ()
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SET NAMES utf8mb4 returns OK"
          <| fun _ ->
              let session = create 1

              match handle session "SET NAMES utf8mb4" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected OK, got %A" other

          testCase "SELECT DATABASE() returns NULL before USE"
          <| fun _ ->
              let session = create 1

              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ None ] ]) -> ()
              | other -> failtestf "expected a single NULL row, got %A" other

          testCase "USE sets the session database, reflected by SELECT DATABASE()"
          <| fun _ ->
              let session = create 1
              let session, _ = handle session "USE mydb"

              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ Some "mydb" ] ]) -> ()
              | other -> failtestf "expected mydb, got %A" other

          testCase "SHOW DATABASES returns a resultset"
          <| fun _ ->
              let session = create 1

              match handle session "SHOW DATABASES" |> snd with
              | ResultSet([ "Database" ], _ :: _) -> ()
              | other -> failtestf "expected a non-empty resultset, got %A" other

          testCase "SHOW VARIABLES LIKE filters by pattern"
          <| fun _ ->
              let session = create 1

              match handle session "SHOW VARIABLES LIKE 'autocommit'" |> snd with
              | ResultSet(_, [ [ Some "autocommit"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the autocommit row, got %A" other

          testCase "an unrecognized statement is a 1064 syntax error naming the query"
          <| fun _ ->
              let session = create 1

              match handle session "CREATE TABLE t (id INT)" |> snd with
              | Err(1064, msg) -> Expect.stringContains msg "CREATE TABLE t" "message names the query"
              | other -> failtestf "expected a 1064 error, got %A" other ]

let integrationTests =
    testList
        "Integration"
        [ testCase "mysql client can connect, SELECT 1, and read @@version"
          <| fun _ ->
              async {
                  let listener = Fsdb.Server.startListening 0
                  let port = Fsdb.Server.port listener
                  let serverTask = Fsdb.Server.serve listener |> Async.StartAsTask

                  try
                      let connStr =
                          sprintf
                              "Server=127.0.0.1;Port=%d;User ID=root;Password=;AllowPublicKeyRetrieval=True;SslMode=None"
                              port

                      use conn = new MySqlConnector.MySqlConnection(connStr)
                      do! conn.OpenAsync() |> Async.AwaitTask

                      use cmd1 = conn.CreateCommand()
                      cmd1.CommandText <- "SELECT 1"
                      let! result1 = cmd1.ExecuteScalarAsync() |> Async.AwaitTask
                      Expect.equal (string result1) "1" "SELECT 1 result"

                      use cmd2 = conn.CreateCommand()
                      cmd2.CommandText <- "SELECT @@version"
                      let! result2 = cmd2.ExecuteScalarAsync() |> Async.AwaitTask
                      Expect.equal (string result2) ServerVersion "SELECT @@version result"

                      do! conn.CloseAsync() |> Async.AwaitTask
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously ]

[<EntryPoint>]
let main argv =
    Tests.runTestsWithCLIArgs
        []
        argv
        (testList "fsdb" [ packetTests; protocolTests; queryHandlerTests; integrationTests ])
