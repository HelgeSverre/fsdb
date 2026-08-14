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
type private Command =
    | Quit
    | InitDb of database: string
    | Query of sql: string
    | Ping
    | Unsupported of code: byte

/// None means a malformed (empty) command packet — treat as disconnect.
let private parseCommand (payload: byte[]) : Command option =
    if payload.Length = 0 then
        None
    else
        let rest () = Encoding.UTF8.GetString(payload, 1, payload.Length - 1)

        Some(
            match payload.[0] with
            | 0x01uy -> Quit
            | 0x02uy -> InitDb(rest ())
            | 0x03uy -> Query(rest ())
            | 0x0euy -> Ping
            | b -> Unsupported b
        )

let private randomAuthPluginData () : byte[] =
    let bytes = Array.zeroCreate<byte> 20
    Random.Shared.NextBytes bytes
    // Auth-plugin-data fields are null-terminated on the wire; a stray 0x00
    // would truncate them. Harmless either way since we never check the
    // scramble, but keep the bytes well-formed.
    bytes |> Array.map (fun b -> if b = 0uy then 1uy else b)

/// Writes a text resultset (or OK/ERR) as one or more packets, continuing
/// the sequence-id numbering from `startSeq`. Not private: exercised
/// directly by the test suite, since it's the only sequence-id-bearing
/// logic in the server and the legacy (non CLIENT_DEPRECATE_EOF) path isn't
/// reachable through the MySqlConnector integration test.
let sendQueryResult
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (result: QueryHandler.QueryResult)
    : Async<unit> =
    // Writes each payload in turn, threading the *actual* next sequence id
    // returned by writePacketAsync rather than assuming one payload == one
    // packet == one seq id — a payload that itself splits into multiple
    // wire packets (>= 16 MiB) would otherwise desync every packet after it.
    let rec sendAll (seqId: byte) (payloads: byte[] list) : Async<unit> =
        async {
            match payloads with
            | [] -> ()
            | payload :: rest ->
                let! nextSeqId = writePacketAsync stream { SeqId = seqId; Payload = payload }
                return! sendAll nextSeqId rest
        }

    async {
        match result with
        | QueryHandler.Affected affectedRows ->
            do! sendAll startSeq [ okPayload capabilities affectedRows 0UL ]
        | QueryHandler.Err(code, message) -> do! sendAll startSeq [ errPayload capabilities code message ]
        | QueryHandler.ResultSet(columns, rows) ->
            let deprecateEof = capabilities &&& ClientDeprecateEof <> 0u

            let columnCountPayload =
                let w = Writer()
                w.WriteLenEncInt(uint64 columns.Length)
                w.ToArray()

            // The packet order here reads top-to-bottom as the protocol spec
            // describes a text resultset: column count, column defs, an EOF
            // (unless CLIENT_DEPRECATE_EOF), rows, then the terminator.
            let payloads =
                seq {
                    columnCountPayload
                    yield! columns |> Seq.map (fun col -> columnDefPayload { Name = col })
                    if not deprecateEof then
                        eofPayload capabilities
                    yield! rows |> Seq.map textRowPayload
                    if deprecateEof then
                        okEndOfResultSetPayload capabilities
                    else
                        eofPayload capabilities
                }

            do! sendAll startSeq (List.ofSeq payloads)
    }

let private handleConnection (connectionId: int) (client: TcpClient) : Async<unit> =
    async {
        use client = client
        use stream = client.GetStream()
        let authData = randomAuthPluginData ()
        do! writePacketAsync stream { SeqId = 0uy; Payload = buildHandshakeV10 connectionId authData } |> Async.Ignore

        match! readPacketAsync stream with
        | None -> ()
        | Some handshakeResp ->
            let resp = parseHandshakeResponse handshakeResp.Payload
            // Effective capabilities: never claim something the client didn't ask for.
            let capabilities = resp.Capabilities &&& ServerCapabilities
            let session = { Session.create connectionId with Database = resp.Database }

            do!
                writePacketAsync
                    stream
                    { SeqId = handshakeResp.SeqId + 1uy
                      Payload = okPayload capabilities 0UL 0UL }
                |> Async.Ignore

            let rec loop (session: Session) : Async<unit> =
                async {
                    match! readPacketAsync stream with
                    | None -> ()
                    | Some cmdPacket ->
                        let seqId = cmdPacket.SeqId + 1uy

                        match parseCommand cmdPacket.Payload with
                        | None
                        | Some Quit -> ()
                        | Some Ping ->
                            do!
                                writePacketAsync stream { SeqId = seqId; Payload = okPayload capabilities 0UL 0UL }
                                |> Async.Ignore

                            return! loop session
                        | Some(InitDb db) ->
                            do!
                                writePacketAsync stream { SeqId = seqId; Payload = okPayload capabilities 0UL 0UL }
                                |> Async.Ignore

                            return! loop { session with Database = Some db }
                        | Some(Query sql) ->
                            let session, result = QueryHandler.handle session sql
                            do! sendQueryResult stream capabilities seqId result
                            return! loop session
                        | Some(Unsupported _) ->
                            do!
                                writePacketAsync
                                    stream
                                    { SeqId = seqId
                                      Payload = errPayload capabilities 1047 "Unknown command" }
                                |> Async.Ignore

                            return! loop session
                }

            do! loop session
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
