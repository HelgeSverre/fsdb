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
    testList
        "Server"
        [ // sendQueryResult is the only sequence-id-bearing logic in the
          // server, and getting a resultset terminator wrong hangs mysql
          // CLI / mysqlnd forever waiting for a terminator that never
          // arrives (see the okEndOfResultSetPayload test above).
          // MySqlConnector always negotiates CLIENT_DEPRECATE_EOF, so the
          // integration test alone never exercises the legacy EOF path that
          // PDO/mysqlnd may still use — cover both here directly.
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

                      Expect.equal (List.last packets).Payload.[0] terminator "terminator header" }
                  |> Async.RunSynchronously

          testCase "the legacy EOF path emits one more packet than CLIENT_DEPRECATE_EOF (the column-defs EOF)"
          <| fun _ ->
              async {
                  let run caps =
                      async {
                          use stream = new IO.MemoryStream()
                          do! Fsdb.Server.sendQueryResult stream caps 1uy StatusAutocommit 0UL [] (ResultSet([ "a" ], [ [ Some "1" ] ]))
                          stream.Position <- 0L
                          return! readAllPackets stream
                      }

                  let! deprecated = run (ClientProtocol41 ||| ClientDeprecateEof)
                  let! legacy = run ClientProtocol41
                  Expect.equal (List.length legacy) (List.length deprecated + 1) "legacy path has one extra packet" }
              |> Async.RunSynchronously ]
