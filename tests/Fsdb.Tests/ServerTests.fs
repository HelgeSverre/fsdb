module Fsdb.Tests.ServerTests

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

/// Reads every packet off a stream until clean EOF.
let private readAllPackets (stream: IO.Stream) : Async<Packet list> =
    let rec loop acc =
        async {
            match! readPacketAsync stream with
            | Some p -> return! loop (p :: acc)
            | None -> return List.rev acc
        }

    loop []

let tests =
    testSequenced
    <| testList
        "Server"
        [ testCase "second-valued connection timeouts cannot overflow milliseconds"
          <| fun _ ->
              Expect.equal (Fsdb.Server.timeoutMilliseconds 300) 300_000 "ordinary timeout is exact"
              Expect.equal
                  (Fsdb.Server.timeoutMilliseconds 3_000_000)
                  Int32.MaxValue
                  "a configured timeout beyond Task.Delay's int range saturates instead of wrapping negative"

          // MySqlConnector always negotiates CLIENT_DEPRECATE_EOF; PDO/mysqlnd
          // can still require the legacy terminator sequence.
          for caps, label, terminator in
              [ ClientProtocol41 ||| ClientDeprecateEof, "CLIENT_DEPRECATE_EOF", 0xfeuy
                ClientProtocol41, "legacy EOF", 0xfeuy ] do
              testCase (sprintf "resultset packets are sequential and correctly terminated (%s)" label)
              <| fun _ ->
                  async {
                      use stream = new IO.MemoryStream()

                      do!
                          Fsdb.Server.sendQueryResult
                              stream
                              caps
                              1uy
                              StatusAutocommit
                              0UL
                              3
                              []
                              (ResultSet([ "a"; "b" ], [ [ Some "1"; None ] ]))

                      stream.Position <- 0L
                      let! packets = readAllPackets stream

                      Expect.sequenceEqual
                          (packets |> List.map (fun p -> p.SeqId))
                          [ 1uy .. byte packets.Length ]
                          "sequence ids are contiguous starting at 1"

                      Expect.equal
                          (Reader(packets.Head.Payload).ReadLenEncInt())
                          (Some 2UL)
                          "first packet is the column count"

                      Expect.equal (List.last packets).Payload.[0] terminator "terminator header"

                      let terminatorWarnings =
                          let reader = Reader((List.last packets).Payload.[1..])

                          if caps &&& ClientDeprecateEof <> 0u then
                              reader.ReadLenEncInt() |> ignore
                              reader.ReadLenEncInt() |> ignore
                              reader.ReadInt16LE() |> ignore

                          reader.ReadInt16LE()

                      Expect.equal terminatorWarnings 3 "terminator warning count" }
                  |> Async.RunSynchronously

          testCase "the legacy EOF path emits one more packet than CLIENT_DEPRECATE_EOF (the column-defs EOF)"
          <| fun _ ->
              async {
                  let run caps =
                      async {
                          use stream = new IO.MemoryStream()
                          do! Fsdb.Server.sendQueryResult stream caps 1uy StatusAutocommit 0UL 0 [] (ResultSet([ "a" ], [ [ Some "1" ] ]))
                          stream.Position <- 0L
                          return! readAllPackets stream
                      }

                  let! deprecated = run (ClientProtocol41 ||| ClientDeprecateEof)
                  let! legacy = run ClientProtocol41
                  Expect.equal (List.length legacy) (List.length deprecated + 1) "legacy path has one extra packet" }
              |> Async.RunSynchronously

          testCase "result metadata and rows share inferred temporal precision"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream()
                  let metadata = columnMetadata TypeDateTime

                  do!
                      Fsdb.Server.sendQueryResult
                          stream
                          (ClientProtocol41 ||| ClientDeprecateEof)
                          1uy
                          StatusAutocommit
                          0UL
                          0
                          [ metadata ]
                          (ResultSet([ "at" ], [ [ Some "2026-08-25 12:00:00.1234" ] ]))

                  stream.Position <- 0L
                  let! packets = readAllPackets stream
                  let definition = Reader(packets.[1].Payload)

                  for _ in 1..6 do
                      definition.ReadLenEncString() |> ignore

                  definition.ReadLenEncInt() |> ignore
                  definition.ReadInt16LE() |> ignore
                  definition.ReadInt32LE() |> ignore
                  Expect.equal (definition.ReadByte()) TypeDateTime "wire type"
                  definition.ReadInt16LE() |> ignore
                  Expect.equal (definition.ReadByte()) 4uy "fractional precision" }
              |> Async.RunSynchronously

          // ReadAsync timeout behavior requires a real socket that can be closed.
          testCase "readPacketWithTimeoutMs reaps a connection that never sends a packet"
          <| fun _ ->
              async {
                  let listener = new Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
                  listener.Start()
                  let port = (listener.LocalEndpoint :?> Net.IPEndPoint).Port

                  try
                      let accepted = listener.AcceptTcpClientAsync() |> Async.AwaitTask

                      use idleClient = new Net.Sockets.TcpClient()
                      do! idleClient.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask

                      use! serverSideClient = accepted
                      let stream = serverSideClient.GetStream()

                      let sw = Diagnostics.Stopwatch.StartNew()
                      let! result = Fsdb.Server.readPacketWithTimeoutMs 300 serverSideClient stream
                      Expect.equal result None "a connection that never sends anything is reaped, not hung forever"
                      Expect.isLessThan sw.ElapsedMilliseconds 5000L "reaped close to the requested timeout, not by some other means"
                  finally
                      listener.Stop()
              }
              |> Async.RunSynchronously

          testCase "physical packets preserve an empty LOCAL INFILE terminator"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream()
                  do! writePacketAsync stream { SeqId = 2uy; Payload = [| 1uy; 2uy |] } |> Async.Ignore
                  do! writePacketAsync stream { SeqId = 3uy; Payload = [||] } |> Async.Ignore
                  stream.Position <- 0L
                  let! data = readPhysicalPacketAsync stream
                  let! terminator = readPhysicalPacketAsync stream
                  Expect.equal data (Some { SeqId = 2uy; Payload = [| 1uy; 2uy |] }) "data packet"
                  Expect.equal terminator (Some { SeqId = 3uy; Payload = [||] }) "empty upload terminator" }
              |> Async.RunSynchronously

          testCase "successive query results keep sequence ids and MORE_RESULTS status"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream()
                  let caps = ClientProtocol41 ||| ClientDeprecateEof
                  let! next =
                      Fsdb.Server.sendQueryResultAndNextSeq
                          stream
                          caps
                          1uy
                          (StatusAutocommit ||| StatusMoreResultsExists)
                          0UL
                          0
                          []
                          (Affected 0UL)

                  let! _ = Fsdb.Server.sendQueryResultAndNextSeq stream caps next StatusAutocommit 0UL 0 [] (Affected 0UL)
                  stream.Position <- 0L
                  let! packets = readAllPackets stream
                  Expect.sequenceEqual (packets |> List.map (fun p -> p.SeqId)) [ 1uy; 2uy ] "reply sequence is continuous"
                  let first = Reader(packets.Head.Payload.[1..])
                  first.ReadLenEncInt() |> ignore
                  first.ReadLenEncInt() |> ignore
                  Expect.isTrue (first.ReadInt16LE() &&& StatusMoreResultsExists <> 0) "first result advertises another result"
                  let second = Reader(packets.Tail.Head.Payload.[1..])
                  second.ReadLenEncInt() |> ignore
                  second.ReadLenEncInt() |> ignore
                  Expect.equal (second.ReadInt16LE() &&& StatusMoreResultsExists) 0 "last result clears MORE_RESULTS" }
              |> Async.RunSynchronously

          testCase "procedure result collections carry MORE_RESULTS through the final OK"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream()
                  let capabilities = ClientProtocol41 ||| ClientDeprecateEof

                  let result =
                      MultipleResults
                          [ ResultSet([ "first" ], [ [ Some "1" ] ]), []
                            ResultSet([ "second" ], [ [ Some "2" ] ]), []
                            Affected 0UL, [] ]

                  let! _ =
                      Fsdb.Server.sendQueryResultAndNextSeq
                          stream
                          capabilities
                          1uy
                          StatusAutocommit
                          0UL
                          0
                          []
                          result

                  stream.Position <- 0L
                  let! packets = readAllPackets stream
                  Expect.sequenceEqual (packets |> List.map _.SeqId) [ 1uy .. byte packets.Length ] "sequence ids"

                  let statuses =
                      packets
                      |> List.choose (fun packet ->
                          if
                              packet.Payload.Length > 0
                              && (packet.Payload.[0] = 0uy || packet.Payload.[0] = 0xfeuy)
                          then
                              let reader = Reader(packet.Payload.[1..])
                              reader.ReadLenEncInt() |> ignore
                              reader.ReadLenEncInt() |> ignore
                              Some(reader.ReadInt16LE())
                          else
                              None)

                  Expect.equal statuses.Length 3 "two result terminators and one final OK"
                  Expect.all (statuses |> List.take 2) (fun status -> status &&& StatusMoreResultsExists <> 0) "non-final results"
                  Expect.equal (statuses.[2] &&& StatusMoreResultsExists) 0 "final result"
              }
              |> Async.RunSynchronously

          // Synchronous packet faults must retain their protocol exception type.
          testCase "readPacketWithTimeoutMs surfaces PacketTooLargeException unwrapped, not as AggregateException"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream()
                  let chunkCount = Fsdb.Limits.maxAllowedPacket / maxPacketPayload + 2
                  let chunk = Array.zeroCreate<byte> maxPacketPayload

                  for i in 0 .. chunkCount - 1 do
                      let bytes = frame { SeqId = byte i; Payload = chunk }
                      stream.Write(bytes, 0, bytes.Length)

                  stream.Position <- 0L

                  use dummyListener = new Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
                  dummyListener.Start()
                  let port = (dummyListener.LocalEndpoint :?> Net.IPEndPoint).Port
                  use dummyClient = new Net.Sockets.TcpClient()
                  do! dummyClient.ConnectAsync(Net.IPAddress.Loopback, port) |> Async.AwaitTask

                  let! outcome = Async.Catch(Fsdb.Server.readPacketWithTimeoutMs 5000 dummyClient stream)

                  match outcome with
                  | Choice2Of2(:? PacketTooLargeException) -> ()
                  | Choice2Of2 ex -> failtestf "expected PacketTooLargeException, got %s" (ex.GetType().FullName)
                  | Choice1Of2 _ -> failtest "expected the oversized read to fail"

                  dummyListener.Stop()
              }
              |> Async.RunSynchronously

          testCase "connections above max_connections receive a pre-handshake 1040 error"
          <| fun _ ->
              Fsdb.Limits.withSettings [ "max_connections", "1" ] (fun () ->
                  async {
                      let store = Fsdb.Storage.create ()
                      use server = TestSupport.ServerFixture.start store Fsdb.Functions.empty

                      use admitted = new Net.Sockets.TcpClient()
                      use refused = new Net.Sockets.TcpClient()

                      do! admitted.ConnectAsync(Net.IPAddress.Loopback, server.Port) |> Async.AwaitTask
                      let! handshake = readPacketAsync (admitted.GetStream())
                      Expect.isSome handshake "the admitted connection receives a handshake"

                      do! refused.ConnectAsync(Net.IPAddress.Loopback, server.Port) |> Async.AwaitTask
                      let! response = readPacketAsync (refused.GetStream())

                      match response with
                      | None -> failtest "expected a 1040 packet before close"
                      | Some packet ->
                          Expect.equal packet.SeqId 0uy "the pre-handshake error starts a packet sequence"
                          Expect.equal packet.Payload.[0] 0xffuy "ERR header"
                          let reader = Reader(packet.Payload.[1..])
                          Expect.equal (reader.ReadInt16LE()) 1040 "Too many connections code"
                          Expect.equal (Text.Encoding.ASCII.GetString(packet.Payload, 4, 5)) "08004" "connection-rejection SQLSTATE"

                      // A client is allowed to disappear before the
                      // best-effort 1040 write completes. Repeated reset
                      // peers must not take the detached accept loop down.
                      for _ in 1 .. 20 do
                          use resetPeer = new Net.Sockets.TcpClient()
                          resetPeer.Client.LingerState <- Net.Sockets.LingerOption(true, 0)
                          do! resetPeer.ConnectAsync(Net.IPAddress.Loopback, server.Port) |> Async.AwaitTask
                          resetPeer.Close()

                      admitted.Close()
                      do! Async.Sleep 50

                      use afterResets = new Net.Sockets.TcpClient()
                      do! afterResets.ConnectAsync(Net.IPAddress.Loopback, server.Port) |> Async.AwaitTask
                      let! laterHandshake = readPacketAsync (afterResets.GetStream())
                      Expect.isSome laterHandshake "the server still accepts after reset rejection writes"
                  }
                  |> Async.RunSynchronously) ]
