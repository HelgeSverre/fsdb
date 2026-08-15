module Fsdb.Tests.PacketTests

open System
open Expecto
open Fsdb.Packet

let tests =
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
                  let! nextSeqId = writePacketAsync stream original
                  Expect.equal nextSeqId 4uy "next seq id after a single-chunk write"
                  stream.Position <- 0L
                  let! result = readPacketAsync stream

                  match result with
                  | Some p ->
                      Expect.equal p.SeqId original.SeqId "seq id"
                      Expect.equal p.Payload original.Payload "payload"
                  | None -> failtest "expected a packet"
              }
              |> Async.RunSynchronously

          testCase "writePacketAsync splits a payload >= 16 MiB instead of truncating the length header"
          <| fun _ ->
              // frame's 3-byte length prefix can't represent more than
              // 0xffffff bytes; naively writing an oversized
              // payload silently declared `length &&& 0xffffff` and then
              // wrote the full body, permanently desyncing the connection.
              async {
                  use stream = new IO.MemoryStream()
                  let payload = Array.zeroCreate<byte> maxPacketPayload // exactly the 0xffffff boundary
                  let! nextSeqId = writePacketAsync stream { SeqId = 5uy; Payload = payload }
                  // An exact-multiple-of-maxPacketPayload payload needs a
                  // trailing empty packet so the reader knows where it ends.
                  Expect.equal nextSeqId 7uy "two wire packets consumed for an exact-boundary payload"

                  stream.Position <- 0L
                  let header1 = Array.zeroCreate<byte> 4
                  stream.Read(header1, 0, 4) |> ignore
                  let r1 = Reader(header1)
                  Expect.equal (r1.ReadInt24LE()) maxPacketPayload "first chunk declares the max length"
                  Expect.equal (r1.ReadByte()) 5uy "first chunk seq id"

                  stream.Seek(int64 maxPacketPayload, IO.SeekOrigin.Current) |> ignore
                  let header2 = Array.zeroCreate<byte> 4
                  stream.Read(header2, 0, 4) |> ignore
                  let r2 = Reader(header2)
                  Expect.equal (r2.ReadInt24LE ()) 0 "trailing packet declares zero length"
                  Expect.equal (r2.ReadByte ()) 6uy "trailing packet seq id"
              }
              |> Async.RunSynchronously

          testCase "readPacketAsync returns None on clean disconnect"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream([||])
                  let! result = readPacketAsync stream
                  Expect.equal result None "empty stream yields no packet"
              }
              |> Async.RunSynchronously

          testCase "readPacketAsync reassembles a payload split across multiple wire packets"
          <| fun _ ->
              // A payload of exactly maxPacketPayload bytes means "more
              // packets follow" per the protocol. Any client sending a
              // statement >= 16 MiB (MySqlConnector, Connector/J,
              // libmysqlclient all do) sends it this way; failing to
              // reassemble ran the first chunk as its own command and then
              // treated the continuation as a new one. Uses `frame` directly
              // (not writePacketAsync, which auto-splits/terminates) so the
              // test controls the exact wire fragmentation.
              async {
                  use stream = new IO.MemoryStream()
                  let firstChunk = Array.create maxPacketPayload 7uy
                  let secondChunk = [| 1uy; 2uy; 3uy |]
                  let bytes1 = frame { SeqId = 9uy; Payload = firstChunk }
                  stream.Write(bytes1, 0, bytes1.Length)
                  let bytes2 = frame { SeqId = 10uy; Payload = secondChunk }
                  stream.Write(bytes2, 0, bytes2.Length)
                  stream.Position <- 0L

                  let! result = readPacketAsync stream

                  match result with
                  | Some p ->
                      Expect.equal p.SeqId 9uy "reassembled packet keeps the FIRST fragment's seq id"
                      Expect.equal p.Payload.Length (maxPacketPayload + 3) "payload is the concatenation of both chunks"
                      Expect.equal p.Payload.[maxPacketPayload..] secondChunk "tail bytes come from the second chunk"
                  | None -> failtest "expected a reassembled packet"
              }
              |> Async.RunSynchronously

          testCase "readPacketAsync raises PacketTooLargeException instead of allocating unboundedly"
          <| fun _ ->
              async {
                  use stream = new IO.MemoryStream()
                  // Enough maxPacketPayload-sized fragments (each declaring
                  // "more data follows") to exceed maxAccumulatedPacketSize.
                  let chunkCount = maxAccumulatedPacketSize / maxPacketPayload + 2
                  let chunk = Array.zeroCreate<byte> maxPacketPayload

                  for i in 0 .. chunkCount - 1 do
                      let bytes = frame { SeqId = byte i; Payload = chunk }
                      stream.Write(bytes, 0, bytes.Length)

                  stream.Position <- 0L

                  let! outcome = Async.Catch(readPacketAsync stream)

                  match outcome with
                  | Choice2Of2(:? PacketTooLargeException) -> ()
                  | Choice1Of2 _ -> failtest "expected PacketTooLargeException, got a result"
                  | Choice2Of2 ex -> failtestf "expected PacketTooLargeException, got %A" ex
              }
              |> Async.RunSynchronously ]
