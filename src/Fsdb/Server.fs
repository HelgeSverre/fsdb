/// TCP listener and per-connection command loop: handshake, then
/// COM_QUERY / COM_PING / COM_INIT_DB / COM_QUIT.
module Fsdb.Server

open System
open System.Net
open System.Net.Sockets
open System.Text
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Session

// COM_* command byte values we handle.
// https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_command_phase.html
let private ComQuit = 0x01uy
let private ComInitDb = 0x02uy
let private ComQuery = 0x03uy
let private ComPing = 0x0euy

let private randomAuthPluginData () : byte[] =
    let bytes = Array.zeroCreate<byte> 20
    Random.Shared.NextBytes bytes
    // Auth-plugin-data fields are null-terminated on the wire; a stray 0x00
    // would truncate them. Harmless either way since we never check the
    // scramble, but keep the bytes well-formed.
    bytes |> Array.map (fun b -> if b = 0uy then 1uy else b)

/// Writes a text resultset (or OK/ERR) as one or more packets, continuing
/// the sequence-id numbering from `startSeq`.
let private sendQueryResult
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (result: QueryHandler.QueryResult)
    : Async<unit> =
    async {
        match result with
        | QueryHandler.Ok affectedRows ->
            do! writePacketAsync stream { SeqId = startSeq; Payload = okPayload capabilities affectedRows 0UL }
        | QueryHandler.Err(code, message) ->
            do! writePacketAsync stream { SeqId = startSeq; Payload = errPayload capabilities code message }
        | QueryHandler.ResultSet(columns, rows) ->
            let deprecateEof = capabilities &&& ClientDeprecateEof <> 0u
            let mutable seq = startSeq

            let send payload =
                async {
                    do! writePacketAsync stream { SeqId = seq; Payload = payload }
                    seq <- seq + 1uy
                }

            let colCountPayload = Writer()
            colCountPayload.WriteLenEncInt(uint64 columns.Length)
            do! send (colCountPayload.ToArray())

            for col in columns do
                do! send (columnDefPayload { Name = col })

            if not deprecateEof then
                do! send (eofPayload capabilities)

            for row in rows do
                do! send (textRowPayload row)

            if deprecateEof then
                do! send (okEndOfResultSetPayload capabilities)
            else
                do! send (eofPayload capabilities)
    }

let private handleConnection (connectionId: int) (client: TcpClient) : Async<unit> =
    async {
        use client = client
        use stream = client.GetStream()
        let authData = randomAuthPluginData ()
        do! writePacketAsync stream { SeqId = 0uy; Payload = buildHandshakeV10 connectionId authData }

        match! readPacketAsync stream with
        | None -> ()
        | Some handshakeResp ->
            let resp = parseHandshakeResponse handshakeResp.Payload
            // Effective capabilities: never claim something the client didn't ask for.
            let capabilities = resp.Capabilities &&& ServerCapabilities
            let session = Session.create connectionId
            session.Database <- resp.Database

            do!
                writePacketAsync
                    stream
                    { SeqId = handshakeResp.SeqId + 1uy
                      Payload = okPayload capabilities 0UL 0UL }

            let mutable running = true

            while running do
                match! readPacketAsync stream with
                | None -> running <- false
                | Some cmdPacket when cmdPacket.Payload.Length = 0 -> running <- false
                | Some cmdPacket ->
                    let cmd = cmdPacket.Payload.[0]
                    let seqId = cmdPacket.SeqId + 1uy

                    match cmd with
                    | b when b = ComQuit -> running <- false
                    | b when b = ComPing ->
                        do! writePacketAsync stream { SeqId = seqId; Payload = okPayload capabilities 0UL 0UL }
                    | b when b = ComInitDb ->
                        let db = Encoding.UTF8.GetString(cmdPacket.Payload, 1, cmdPacket.Payload.Length - 1)
                        session.Database <- Some db
                        do! writePacketAsync stream { SeqId = seqId; Payload = okPayload capabilities 0UL 0UL }
                    | b when b = ComQuery ->
                        let sql = Encoding.UTF8.GetString(cmdPacket.Payload, 1, cmdPacket.Payload.Length - 1)
                        do! sendQueryResult stream capabilities seqId (QueryHandler.handle session sql)
                    | _ ->
                        do!
                            writePacketAsync
                                stream
                                { SeqId = seqId
                                  Payload = errPayload capabilities 1047 "Unknown command" }
    }

/// Starts listening on 127.0.0.1:port. Pass 0 for an OS-assigned ephemeral
/// port (used by the integration tests); read it back via `port`.
let startListening (port: int) : TcpListener =
    let listener = new TcpListener(IPAddress.Loopback, port)
    listener.Start()
    listener

let port (listener: TcpListener) : int =
    (listener.LocalEndpoint :?> IPEndPoint).Port

/// Accepts connections forever, handling each on its own async.
let serve (listener: TcpListener) : Async<unit> =
    async {
        let mutable connectionId = 0

        while true do
            let! client = listener.AcceptTcpClientAsync() |> Async.AwaitTask
            connectionId <- connectionId + 1
            let cid = connectionId

            Async.Start(
                async {
                    try
                        do! handleConnection cid client
                    with _ ->
                        ()
                }
            )
    }
