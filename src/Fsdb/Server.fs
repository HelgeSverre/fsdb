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
open Fsdb.Storage
open Fsdb.Value
open Fsdb.Executor

// COM_* command byte values we handle.
// https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_command_phase.html
type private Command =
    | Quit
    | InitDb of database: string
    | Query of sql: string
    | Ping
    | FieldList of table: string
    | StmtPrepare of sql: string
    /// Payload with the COM_STMT_EXECUTE command byte already stripped —
    /// decoding needs a `Reader` positioned right after it, easier built at
    /// the call site than threaded through this DU field by field.
    | StmtExecute of payload: byte[]
    | StmtSendLongData of payload: byte[]
    | StmtClose of stmtId: int
    | StmtReset of stmtId: int
    | Unsupported of code: byte

/// None means a malformed (empty) command packet — treat as disconnect.
let private parseCommand (payload: byte[]) : Command option =
    if payload.Length = 0 then
        None
    else
        let rest () = Encoding.UTF8.GetString(payload, 1, payload.Length - 1)
        let restBytes () = payload.[1..]

        Some(
            match payload.[0] with
            | 0x01uy -> Quit
            | 0x02uy -> InitDb(rest ())
            | 0x03uy -> Query(rest ())
            | 0x04uy -> FieldList(Reader(restBytes ()).ReadNullTerminatedString())
            | 0x0euy -> Ping
            | 0x16uy -> StmtPrepare(rest ())
            | 0x17uy -> StmtExecute(restBytes ())
            | 0x18uy -> StmtSendLongData(restBytes ())
            | 0x19uy -> StmtClose(Reader(restBytes ()).ReadInt32LE())
            | 0x1auy -> StmtReset(Reader(restBytes ()).ReadInt32LE())
            | b -> Unsupported b
        )

let private randomAuthPluginData () : byte[] =
    let bytes = Array.zeroCreate<byte> 20
    Random.Shared.NextBytes bytes
    // Auth-plugin-data fields are null-terminated on the wire; a stray 0x00
    // would truncate them. Harmless either way since we never check the
    // scramble, but keep the bytes well-formed.
    bytes |> Array.map (fun b -> if b = 0uy then 1uy else b)

/// Writes each payload in turn, threading the *actual* next sequence id
/// returned by writePacketAsync rather than assuming one payload == one
/// packet == one seq id — a payload that itself splits into multiple wire
/// packets (>= 16 MiB) would otherwise desync every packet after it. Shared
/// by every multi-packet reply this server sends: text/binary resultsets,
/// COM_STMT_PREPARE_OK's param defs, COM_FIELD_LIST's column defs.
let sendPayloads (stream: IO.Stream) (startSeq: byte) (payloads: byte[] list) : Async<byte> =
    let rec loop (seqId: byte) (payloads: byte[] list) : Async<byte> =
        async {
            match payloads with
            | [] -> return seqId
            | payload :: rest ->
                let! nextSeqId = writePacketAsync stream { SeqId = seqId; Payload = payload }
                return! loop nextSeqId rest
        }

    loop startSeq payloads

/// Builds the packet payloads for an OK/ERR/resultset reply, encoding
/// resultset rows with `rowEncoder` — `textRowPayload` for COM_QUERY,
/// `binaryRowPayload` for COM_STMT_EXECUTE (see `sendQueryResult` /
/// `sendBinaryQueryResult`). The packet order reads top-to-bottom as the
/// protocol spec describes a resultset: column count, column defs, an EOF
/// (unless CLIENT_DEPRECATE_EOF), rows, then the terminator.
let private resultPayloads
    (rowEncoder: string option list -> byte[])
    (capabilities: uint32)
    (lastInsertId: uint64)
    (result: Executor.QueryResult)
    : byte[] list =
    match result with
    | Affected affectedRows -> [ okPayload capabilities affectedRows lastInsertId ]
    | Err(code, message) -> [ errPayload capabilities code message ]
    | ResultSet(columns, rows) ->
        let deprecateEof = capabilities &&& ClientDeprecateEof <> 0u

        let columnCountPayload =
            let w = Writer()
            w.WriteLenEncInt(uint64 columns.Length)
            w.ToArray()

        [ columnCountPayload ]
        @ (columns |> List.map (fun col -> columnDefPayload { Name = col }))
        @ (if deprecateEof then [] else [ eofPayload capabilities ])
        @ (rows |> List.map rowEncoder)
        @ [ (if deprecateEof then okEndOfResultSetPayload capabilities else eofPayload capabilities) ]

/// Writes a text resultset (or OK/ERR) as one or more packets, continuing
/// the sequence-id numbering from `startSeq`. Not private: exercised
/// directly by the test suite, since it's the only sequence-id-bearing
/// logic in the server and the legacy (non CLIENT_DEPRECATE_EOF) path isn't
/// reachable through the MySqlConnector integration test.
let sendQueryResult
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (lastInsertId: uint64)
    (result: Executor.QueryResult)
    : Async<unit> =
    sendPayloads stream startSeq (resultPayloads textRowPayload capabilities lastInsertId result) |> Async.Ignore

/// As `sendQueryResult`, but encodes resultset rows in the binary protocol
/// row format COM_STMT_EXECUTE requires.
let sendBinaryQueryResult
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (lastInsertId: uint64)
    (result: Executor.QueryResult)
    : Async<unit> =
    sendPayloads stream startSeq (resultPayloads binaryRowPayload capabilities lastInsertId result) |> Async.Ignore

let private handleConnection (connectionId: int) (store: Storage.Store) (client: TcpClient) : Async<unit> =
    async {
        use client = client
        use stream = client.GetStream()
        // Negotiated once the handshake response arrives; used as a fallback
        // for the "packet too large" ERR reply if that happens beforehand.
        let mutable capabilities = ServerCapabilities

        try
            let authData = randomAuthPluginData ()

            do!
                writePacketAsync stream { SeqId = 0uy; Payload = buildHandshakeV10 connectionId authData }
                |> Async.Ignore

            match! readPacketAsync stream with
            | None -> ()
            | Some handshakeResp ->
                let resp = parseHandshakeResponse handshakeResp.Payload
                // Effective capabilities: never claim something the client didn't ask for.
                capabilities <- resp.Capabilities &&& ServerCapabilities
                // A client that names a database at connect time (`mysql -D
                // foo`, PDO's DSN `dbname=foo`) gets it auto-created, same
                // as `USE` on a fresh in-memory server with no setup step.
                resp.Database |> Option.iter (Storage.ensureDatabase store)
                let session = { Session.create connectionId store with Database = resp.Database }

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
                                    writePacketAsync
                                        stream
                                        { SeqId = seqId
                                          Payload = okPayload capabilities 0UL 0UL }
                                    |> Async.Ignore

                                return! loop session
                            | Some(InitDb db) ->
                                do!
                                    writePacketAsync
                                        stream
                                        { SeqId = seqId
                                          Payload = okPayload capabilities 0UL 0UL }
                                    |> Async.Ignore

                                return! loop { session with Database = Some db }
                            | Some(Query sql) ->
                                let session, result = QueryHandler.handle session sql
                                do! sendQueryResult stream capabilities seqId (uint64 session.LastInsertId) result
                                return! loop session
                            | Some(FieldList table) ->
                                // Deprecated in MySQL 8.0, but PDO/mysqlnd's
                                // metadata probing can still send it —
                                // reply with the table's columns, EOF-terminated,
                                // or a 1146 ERR if the table doesn't exist.
                                let dbName = session.Database |> Option.defaultValue defaultDatabase

                                match Storage.scan (Session.currentStore session) dbName table with
                                | Result.Error e ->
                                    let code, message = Storage.toMySqlError e

                                    do!
                                        writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities code message }
                                        |> Async.Ignore

                                    return! loop session
                                | Result.Ok(columns, _rows) ->
                                    let payloads =
                                        (columns |> List.map (fun c -> columnDefPayload { Name = c.Name }))
                                        @ [ eofPayload capabilities ]

                                    do! sendPayloads stream seqId payloads |> Async.Ignore
                                    return! loop session
                            | Some(StmtPrepare sql) ->
                                match QueryHandler.prepareStatement sql with
                                | Result.Error(code, message) ->
                                    do!
                                        writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities code message }
                                        |> Async.Ignore

                                    return! loop session
                                | Result.Ok paramCount ->
                                    let stmtId = session.NextStmtId

                                    let stmt: PreparedStmt =
                                        { Sql = sql
                                          ParamCount = paramCount
                                          LastParamTypes = None }

                                    let session =
                                        { session with
                                            Statements = Map.add stmtId stmt session.Statements
                                            NextStmtId = stmtId + 1 }

                                    let deprecateEof = capabilities &&& ClientDeprecateEof <> 0u

                                    let paramDefEof =
                                        if paramCount > 0 && not deprecateEof then [ eofPayload capabilities ] else []

                                    let payloads =
                                        stmtPrepareOkPayload stmtId paramCount
                                        :: List.replicate paramCount (columnDefPayload { Name = "?" })
                                        @ paramDefEof

                                    do! sendPayloads stream seqId payloads |> Async.Ignore
                                    return! loop session
                            | Some(StmtExecute payload) ->
                                let r = Reader(payload)
                                let stmtId = r.ReadInt32LE()
                                r.ReadByte() |> ignore // cursor flags — no cursor support
                                r.ReadInt32LE() |> ignore // iteration count, always 1

                                match Map.tryFind stmtId session.Statements with
                                | None ->
                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload = errPayload capabilities 1243 "Unknown prepared statement handler" }
                                        |> Async.Ignore

                                    return! loop session
                                | Some stmt ->
                                    let nullBitmap =
                                        if stmt.ParamCount > 0 then r.ReadBytes((stmt.ParamCount + 7) / 8) else [||]

                                    let newParamsBound = if stmt.ParamCount > 0 then r.ReadByte() else 0uy

                                    let typesResult =
                                        if stmt.ParamCount = 0 then
                                            Result.Ok []
                                        elif newParamsBound = 1uy then
                                            // Explicit sequential reads (not a bare tuple of two
                                            // `ReadByte()` calls) so the type byte is always read
                                            // before the unsigned-flag byte, regardless of F#'s
                                            // tuple-construction evaluation order.
                                            Result.Ok [ for _ in 1 .. stmt.ParamCount -> let t = r.ReadByte() in let u = r.ReadByte() in t, u <> 0uy ]
                                        else
                                            match stmt.LastParamTypes with
                                            | Some types -> Result.Ok types
                                            | None -> Result.Error "COM_STMT_EXECUTE sent no parameter types to bind"

                                    match typesResult with
                                    | Result.Error message ->
                                        do!
                                            writePacketAsync
                                                stream
                                                { SeqId = seqId; Payload = errPayload capabilities 1210 message }
                                            |> Async.Ignore

                                        return! loop session
                                    | Result.Ok types ->
                                        let isNull i =
                                            (int nullBitmap.[i / 8] >>> (i % 8)) &&& 1 = 1

                                        let values =
                                            types
                                            |> List.mapi (fun i (typeId, unsigned) ->
                                                match Map.tryFind (stmtId, i) session.LongData with
                                                | Some bytes -> VString(Encoding.UTF8.GetString bytes)
                                                | None -> if isNull i then VNull else readBinaryValue r typeId unsigned)

                                        let finalSql =
                                            QueryHandler.substitutePlaceholders
                                                stmt.Sql
                                                (values |> List.map QueryHandler.valueToSqlLiteral)

                                        let session =
                                            { session with
                                                Statements = Map.add stmtId { stmt with LastParamTypes = Some types } session.Statements
                                                LongData = session.LongData |> Map.filter (fun (sid, _) _ -> sid <> stmtId) }

                                        let session, result = QueryHandler.handle session finalSql
                                        do! sendBinaryQueryResult stream capabilities seqId (uint64 session.LastInsertId) result
                                        return! loop session
                            | Some(StmtSendLongData payload) ->
                                // No response is ever sent for this command,
                                // success or failure — the client doesn't
                                // wait for one. ponytail: buffered rather
                                // than streamed straight into the value —
                                // COM_STMT_EXECUTE substitutes the buffered
                                // text in place of reading that param off
                                // the wire (see there); fine for the sizes
                                // Laravel/PDO send, revisit if a client ever
                                // streams something too big to buffer.
                                let r = Reader(payload)
                                let stmtId = r.ReadInt32LE()
                                let paramIndex = r.ReadInt16LE()
                                let chunk = r.ReadBytes r.Remaining

                                if session.Statements.ContainsKey stmtId then
                                    let key = stmtId, paramIndex
                                    let existing = session.LongData |> Map.tryFind key |> Option.defaultValue [||]
                                    return! loop { session with LongData = Map.add key (Array.append existing chunk) session.LongData }
                                else
                                    return! loop session
                            | Some(StmtClose stmtId) ->
                                // No reply, per protocol.
                                return!
                                    loop
                                        { session with
                                            Statements = Map.remove stmtId session.Statements
                                            LongData = session.LongData |> Map.filter (fun (sid, _) _ -> sid <> stmtId) }
                            | Some(StmtReset stmtId) ->
                                if session.Statements.ContainsKey stmtId then
                                    let session =
                                        { session with LongData = session.LongData |> Map.filter (fun (sid, _) _ -> sid <> stmtId) }

                                    do!
                                        writePacketAsync stream { SeqId = seqId; Payload = okPayload capabilities 0UL 0UL }
                                        |> Async.Ignore

                                    return! loop session
                                else
                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload = errPayload capabilities 1243 "Unknown prepared statement handler" }
                                        |> Async.Ignore

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
        with :? PacketTooLargeException ->
            // Reassembling a multi-packet payload blew past
            // maxAccumulatedPacketSize. There's no way to resync mid-stream,
            // but a best-effort ERR beats silently dropping the connection.
            do!
                writePacketAsync
                    stream
                    { SeqId = 0uy
                      Payload = errPayload capabilities 1153 "Got a packet bigger than 'max_allowed_packet' bytes" }
                |> Async.Ignore
                |> Async.Catch
                |> Async.Ignore
    }

/// Starts listening on address:port. Pass port 0 for an OS-assigned
/// ephemeral port (used by the integration tests); read it back via `port`.
let startListening (address: IPAddress) (port: int) : TcpListener =
    let listener = new TcpListener(address, port)
    listener.Start()
    listener

let port (listener: TcpListener) : int =
    (listener.LocalEndpoint :?> IPEndPoint).Port

/// None once the listener has been stopped/disposed — the clean way to shut
/// the server down from the outside.
let private tryAccept (listener: TcpListener) : Async<TcpClient option> =
    async {
        try
            let! client = listener.AcceptTcpClientAsync() |> Async.AwaitTask
            return Some client
        with
        | :? ObjectDisposedException
        | :? SocketException -> return None
    }

/// Accepts connections until the listener is stopped, handling each on its
/// own async against the one shared `store` every session reads/writes
/// through. A failing connection is logged, never fatal to the server.
let serve (listener: TcpListener) (store: Storage.Store) : Async<unit> =
    let rec loop (connectionId: int) : Async<unit> =
        async {
            match! tryAccept listener with
            | None -> ()
            | Some client ->
                Async.Start(
                    async {
                        try
                            do! handleConnection connectionId store client
                        with ex ->
                            eprintfn "fsdb: connection %d: %s" connectionId ex.Message
                    }
                )

                return! loop (connectionId + 1)
        }

    loop 1
