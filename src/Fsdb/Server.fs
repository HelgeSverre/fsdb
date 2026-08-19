/// TCP listener and per-connection command loop: handshake, then
/// COM_QUERY / COM_PING / COM_INIT_DB / COM_QUIT.
module Fsdb.Server

open System
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open Fsdb.Binary
open Fsdb.Functions
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Session
open Fsdb.Storage
open Fsdb.Value
open Fsdb.Executor

// COM_* command byte values handled here.
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
    | ResetConnection
    | Unsupported of code: byte
    /// A command byte this server recognizes, but whose payload was too
    /// short/malformed to decode (e.g. a truncated COM_STMT_CLOSE with no
    /// 4-byte statement id). Answered with an ERR rather than let the
    /// `Reader`/`Encoding` exception escape the command loop and drop the
    /// connection.
    | Malformed of code: byte

/// None means a completely empty command packet — treat as disconnect (real
/// clients never send one). A non-empty payload always decodes to `Some`,
/// falling back to `Malformed` if the command byte's own payload is too
/// short to parse — see that case's doc.
let private parseCommand (payload: byte[]) : Command option =
    if payload.Length = 0 then
        None
    else
        let rest () = Encoding.UTF8.GetString(payload, 1, payload.Length - 1)
        let restBytes () = payload.[1..]

        try
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
                | 0x1fuy -> ResetConnection
                | b -> Unsupported b
            )
        with _ ->
            Some(Malformed payload.[0])

let private randomAuthPluginData () : byte[] =
    let bytes = Array.zeroCreate<byte> 20
    Random.Shared.NextBytes bytes
    // Auth-plugin-data fields are null-terminated on the wire; a stray 0x00
    // would truncate them. Harmless either way since the scramble is never
    // checked, but keep the bytes well-formed.
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

/// Builds the non-row packet payloads of an OK/ERR/resultset reply: the
/// column count, column defs, and the pre-rows EOF. Rows stream separately
/// (see `sendResult`) rather than materializing one `byte[]` per row here.
let private resultHeadPayloads
    (capabilities: uint32)
    (statusFlags: int)
    (lastInsertId: uint64)
    (columnTypes: byte list)
    (result: Executor.QueryResult)
    : byte[] list =
    match result with
    | Affected affectedRows -> [ okPayload capabilities statusFlags affectedRows lastInsertId ]
    | Err(code, message) -> [ errPayload capabilities code message ]
    | ResultSet(columns, rows) ->
        let deprecateEof = capabilities &&& ClientDeprecateEof <> 0u

        // `columnTypes` only means anything if the caller actually had one
        // entry per column (a mismatch means "no real info", e.g. a probe
        // result — see `Session.LastResultColumnTypes`'s doc). The column
        // definition packets and `sendResult`'s row encoder reconcile to
        // this same `types`, so the two can never disagree.
        let types =
            if List.length columnTypes = columns.Length then
                columnTypes
            else
                List.replicate columns.Length TypeVarString

        let columnCountPayload =
            let w = Writer()
            w.WriteLenEncInt(uint64 columns.Length)
            w.ToArray()

        // The `decimals` (fsp) each column advertises: for a DATETIME/
        // TIMESTAMP/TIME column, read back the fractional digits the renderer
        // already emitted into the rows (see `Protocol.fractionalDigitsOf`),
        // so a client learns a `DATETIME(6)`/`TIME(3)`'s precision even on an
        // exact second. Non-temporal columns advertise 0.
        let decimalsOf (colIndex: int) (ty: byte) : byte =
            if ty = TypeDateTime || ty = TypeTime then
                rows |> List.map (fun r -> List.tryItem colIndex r |> Option.flatten) |> Protocol.fractionalDigitsOf
            else
                0uy

        [ columnCountPayload ]
        @ (List.zip columns types
           |> List.mapi (fun i (name, ty) -> columnDefPayload { Name = name; Type = ty; Decimals = decimalsOf i ty }))
        @ (if deprecateEof then [] else [ eofPayload capabilities statusFlags ])

/// Writes an OK/ERR/resultset reply. Rows are framed and written in batches —
/// one `WriteAsync` per ~64 KiB instead of one per row packet — so a large
/// result set neither pays a syscall per row nor holds every row's bytes at
/// once. Column count/defs/EOF stay ordinary packets.
let private sendResult
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (lastInsertId: uint64)
    (columnTypes: byte list)
    (rowEncoder: byte list -> string option list -> byte[])
    (result: Executor.QueryResult)
    : Async<unit> =
    async {
        let! seqId = sendPayloads stream startSeq (resultHeadPayloads capabilities statusFlags lastInsertId columnTypes result)

        match result with
        | ResultSet(columns, rows) ->
            let types =
                if List.length columnTypes = columns.Length then
                    columnTypes
                else
                    List.replicate columns.Length TypeVarString

            let mutable seqId = seqId
            let buf = ResizeArray<byte>()

            let flush () =
                async {
                    if buf.Count > 0 then
                        let bytes = buf.ToArray()
                        do! stream.WriteAsync(bytes, 0, bytes.Length) |> Async.AwaitTask
                        buf.Clear()
                }

            for row in rows do
                let payload = rowEncoder types row

                if payload.Length < maxPacketPayload then
                    // 4-byte packet header (3-byte length + seq id) written
                    // straight into the batch buffer — the common case, a
                    // row safely under the 16 MiB single-packet ceiling, so
                    // inlining skips `frame`'s intermediate `byte[]` per row.
                    buf.Add(byte (payload.Length &&& 0xff))
                    buf.Add(byte ((payload.Length >>> 8) &&& 0xff))
                    buf.Add(byte ((payload.Length >>> 16) &&& 0xff))
                    buf.Add seqId
                    buf.AddRange payload
                    seqId <- seqId + 1uy
                else
                    // A row >= 16 MiB (an uncapped REPEAT, a large BLOB/TEXT
                    // selected back, ...) can't fit the inlined 3-byte
                    // length header — the length would wrap mod 2^24 and
                    // desync the connection. Flush whatever's batched so far
                    // to keep ordering, then route this one row through the
                    // real multi-packet framing.
                    do! flush ()
                    let! nextSeqId = writePacketAsync stream { SeqId = seqId; Payload = payload }
                    seqId <- nextSeqId

                if buf.Count >= (1 <<< 16) then
                    do! flush ()

            do! flush ()

            let deprecateEof = capabilities &&& ClientDeprecateEof <> 0u

            do!
                sendPayloads
                    stream
                    seqId
                    [ (if deprecateEof then
                           okEndOfResultSetPayload capabilities statusFlags
                       else
                           eofPayload capabilities statusFlags) ]
                |> Async.Ignore
        | _ -> ()
    }

/// Writes a text resultset (or OK/ERR) as one or more packets, continuing
/// the sequence-id numbering from `startSeq`. Not private: exercised
/// directly by the test suite, since it's the only sequence-id-bearing
/// logic in the server and the legacy (non CLIENT_DEPRECATE_EOF) path isn't
/// reachable through the MySqlConnector integration test.
let sendQueryResult
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (lastInsertId: uint64)
    (columnTypes: byte list)
    (result: Executor.QueryResult)
    : Async<unit> =
    sendResult stream capabilities startSeq statusFlags lastInsertId columnTypes textRowPayloadTyped result

/// As `sendQueryResult`, but encodes resultset rows in the binary protocol
/// row format COM_STMT_EXECUTE requires (`binaryRowPayload`, which — unlike
/// `textRowPayload` — actually reads `columnTypes` to pick each value's
/// wire encoding, not just what `columnDefPayload` advertises).
let sendBinaryQueryResult
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (lastInsertId: uint64)
    (columnTypes: byte list)
    (result: Executor.QueryResult)
    : Async<unit> =
    sendResult stream capabilities startSeq statusFlags lastInsertId columnTypes binaryRowPayload result

/// `SERVER_STATUS_AUTOCOMMIT` always, plus `SERVER_STATUS_IN_TRANS` while
/// `session.Tx` is open — every OK/EOF packet reports this so PDO's
/// `inTransaction()`/`beginTransaction()`/`commit()` (which read the status
/// bit off the wire, not just whatever `COMMIT`/`ROLLBACK` themselves reply)
/// see the real transaction state.
let private statusFlagsFor (session: Session) : int =
    StatusAutocommit ||| (if session.Tx.IsSome then StatusInTrans else 0)

/// A dead socket, detected without consuming any data: `Poll(SelectRead)`
/// returns true both when the peer closed/reset the connection *and* when
/// there's unread data waiting, so `Available = 0` is what tells the two
/// cases apart.
let private isSocketDead (client: TcpClient) : bool =
    try
        client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available = 0
    with _ ->
        true

let private disconnectPollIntervalMs = 50

/// Idle timeout for waiting on the *next* command packet — a half-open peer
/// that opens a connection and sends nothing (or only a partial 4-byte
/// packet header) would otherwise pin a thread-pool task and a socket
/// forever. `Socket.ReceiveTimeout` doesn't help here: it only bounds the
/// synchronous `Read()`, and this server exclusively awaits `ReadAsync` —
/// which .NET does not honor it for, and which F#'s own cooperative
/// cancellation (including `Async.StartChild`'s timeout) can't preempt
/// either, since a `Task`-backed await only resumes when that task actually
/// completes. Only bounds time spent waiting to *start* reading a packet,
/// not time spent running a long query in between.
let private socketIdleTimeoutMs = 5 * 60 * 1000

/// `readPacketAsync`, but abandoned if no complete packet arrives within
/// `timeoutMs` — see `socketIdleTimeoutMs`'s doc for why this races the
/// read against `Task.Delay` instead of a cancellation token. When the
/// timer wins, the stuck read has to be forced to unblock: closing
/// `client`'s socket faults the pending `ReadAsync` with an exception,
/// which is what actually reaps the connection (this function then reports
/// that as `None`, same as any other clean-disconnect path — the caller
/// already treats `None` as "stop the command loop"). Not private: the
/// timeout itself is exercised directly by the test suite with a short
/// `timeoutMs` rather than waiting out the real 5-minute production value.
let readPacketWithTimeoutMs (timeoutMs: int) (client: TcpClient) (stream: IO.Stream) : Async<Packet option> =
    async {
        let readTask = Async.StartAsTask(readPacketAsync stream)

        let! winner =
            Threading.Tasks.Task.WhenAny(readTask :> Threading.Tasks.Task, Threading.Tasks.Task.Delay timeoutMs)
            |> Async.AwaitTask

        if obj.ReferenceEquals(winner, readTask) then
            // `Async.AwaitTask` on a task that faulted synchronously inside
            // `Async.StartAsTask` (before any real `await`, as
            // `PacketTooLargeException` does — raised straight out of
            // `readPacketAsync`'s loop) surfaces the fault wrapped in an
            // `AggregateException` rather than unwrapped, unlike a task that
            // faults after suspending on real I/O. The caller's `with` match
            // on the specific exception type (`PacketTooLargeException`,
            // for the best-effort 1153 reply) needs the original exception,
            // not its wrapper.
            try
                return! Async.AwaitTask readTask
            with :? AggregateException as agg when agg.InnerExceptions.Count = 1 ->
                return raise (agg.InnerExceptions.[0])
        else
            client.Close()
            return None
    }

let private readPacketWithTimeout (client: TcpClient) (stream: IO.Stream) : Async<Packet option> =
    readPacketWithTimeoutMs socketIdleTimeoutMs client stream

/// Ceiling on concurrently handled connections — past this, `serve` stops
/// calling `AcceptTcpClientAsync` until a slot frees, so excess connection
/// attempts queue at the OS socket backlog (or get refused) instead of each
/// one costing this process a thread-pool task and a 16 MiB read buffer's
/// worth of memory pressure.
let private maxConcurrentConnections = 500

/// Polls `client`'s socket while a query runs, cancelling `queryCts` the
/// moment the peer is gone — the only way to notice a disconnect while
/// `QueryHandler.handle` is busy inside a synchronous row fold with nothing
/// of its own to `await`. Runs detached (`Async.Start`) under `watchToken`,
/// which the caller cancels as soon as the query returns so this loop never
/// outlives the query it's watching.
let rec private watchForDisconnect (client: TcpClient) (queryCts: CancellationTokenSource) : Async<unit> =
    async {
        if isSocketDead client then
            queryCts.Cancel()
        else
            do! Async.Sleep disconnectPollIntervalMs
            return! watchForDisconnect client queryCts
    }

/// Runs `body` (a synchronous statement dispatch to `QueryHandler.handle`)
/// with `Storage.queryCancellation` armed for this thread and a background
/// watcher polling the socket — see `watchForDisconnect`. Cleared again
/// afterwards, success or exception, so a thread-pool thread that later
/// picks up an unrelated connection's query never inherits a stale
/// cancelled token.
let private withCancellationWatch (client: TcpClient) (entry: InformationSchema.ProcessEntry option) (body: unit -> 'a) : 'a =
    // Deliberately not `use`: `watchForDisconnect` runs detached
    // (`Async.Start`) and `watchCts.Cancel()` below doesn't synchronously
    // stop an iteration already past its `isSocketDead` check — that
    // iteration can still call `queryCts.Cancel()` after this function has
    // returned. Disposing either CTS here raced that call and could throw
    // `ObjectDisposedException` on a thread-pool thread, which is
    // unhandled and kills the process. Neither CTS holds timers or
    // registrations of its own, so there is nothing to leak by skipping
    // `Dispose`.
    let queryCts = new CancellationTokenSource()
    let watchCts = new CancellationTokenSource()
    Async.Start(watchForDisconnect client queryCts, watchCts.Token)
    Storage.queryCancellation.Value <- queryCts.Token
    // `KILL QUERY <id>` from another connection cancels through the same
    // token the disconnect watcher uses.
    entry |> Option.iter (fun e -> e.CancelQuery <- Some(fun () -> queryCts.Cancel()))

    try
        body ()
    finally
        entry |> Option.iter (fun e -> e.CancelQuery <- None)
        watchCts.Cancel()
        Storage.queryCancellation.Value <- CancellationToken.None

let private connectionCounter = ref 0L

/// The AuthSwitchRequest packet payload: asks the client to answer the same
/// 20-byte scramble with mysql_native_password (sent when it responded with
/// a different plugin, or with nothing, and the account needs verification).
let private authSwitchPayload (authData: byte[]) : byte[] =
    let w = Writer()
    w.WriteByte 0xFEuy
    w.WriteNullTerminatedString "mysql_native_password"
    w.WriteBytes authData
    w.WriteByte 0uy
    w.ToArray()

/// Authenticates a parsed handshake response against `mysql.user`: the
/// account must exist, and its credential must match: a non-empty stored
/// hash is verified as mysql_native_password over `authData`'s scramble; an
/// empty stored hash (no password set) accepts only an *empty* offered
/// password, exactly like real MySQL — offering one is `1045 (using
/// password: YES)`. Writes the 1045 ERR itself on denial and returns `None`; returns
/// `Some seqId` (the sequence id the OK packet must use) on success —
/// `firstSeq + 1` more when an AuthSwitch round trip happened.
let private authenticateHandshake
    (client: TcpClient)
    (stream: IO.Stream)
    (capabilities: uint32)
    (store: Storage.Store)
    (authData: byte[])
    (resp: HandshakeResponse)
    (firstSeq: byte)
    : Async<byte option> =
    async {
        let deny (seqId: byte) (usingPassword: bool) =
            async {
                let msg =
                    sprintf
                        "Access denied for user '%s'@'localhost' (using password: %s)"
                        resp.Username
                        (if usingPassword then "YES" else "NO")

                do! writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities 1045 msg } |> Async.Ignore
                return None
            }

        match Auth.tryUserRow store resp.Username with
        | None -> return! deny firstSeq (resp.AuthResponse.Length > 0)
        | Some(cols, row) ->
            let stored = Auth.storedPasswordHash cols row

            if stored = "" then
                // No password set: only an empty offered password matches
                // (every auth plugin sends a zero-length response for an
                // empty password, so no AuthSwitch round trip is needed).
                if resp.AuthResponse.Length = 0 then
                    return Some firstSeq
                else
                    return! deny firstSeq true
            elif Auth.verifyNative stored authData resp.AuthResponse then
                return Some firstSeq
            elif resp.ClientPlugin = Some "mysql_native_password" then
                // Right plugin, wrong password — no switch will fix it.
                return! deny firstSeq (resp.AuthResponse.Length > 0)
            else
                // The client answered with another plugin (mysql CLI 8.x
                // defaults to caching_sha2_password) or nothing; ask it to
                // redo the same scramble with mysql_native_password.
                do! writePacketAsync stream { SeqId = firstSeq; Payload = authSwitchPayload authData } |> Async.Ignore

                match! readPacketWithTimeout client stream with
                | None -> return None // client gave up; nothing to reply to
                | Some switchResp when Auth.verifyNative stored authData switchResp.Payload ->
                    return Some(switchResp.SeqId + 1uy)
                | Some switchResp -> return! deny (switchResp.SeqId + 1uy) (switchResp.Payload.Length > 0)
    }

let private handleConnection
    (connectionId: int)
    (store: Storage.Store)
    (customFunctions: Functions.Registry)
    (client: TcpClient)
    : Async<unit> =
    async {
        use client = client
        // Nagle's algorithm batches small writes to wait for more data or an
        // ACK before sending — exactly wrong for this protocol's
        // one-small-packet-request, one-small-packet-response pattern
        // (every statement is its own round trip), where it just adds
        // latency with nothing to batch. Off, matching what a real MySQL
        // server does for the same reason.
        client.NoDelay <- true
        use stream = client.GetStream()
        // Negotiated once the handshake response arrives; used as a fallback
        // for the "packet too large" ERR reply if that happens beforehand.
        let mutable capabilities = ServerCapabilities
        let mutable activeSession: Session option = None

        try
            let authData = randomAuthPluginData ()

            do!
                writePacketAsync stream { SeqId = 0uy; Payload = buildHandshakeV10 connectionId authData }
                |> Async.Ignore

            match! readPacketWithTimeout client stream with
            | None -> ()
            | Some handshakeResp ->
                let resp = parseHandshakeResponse handshakeResp.Payload
                // Effective capabilities: never claim something the client didn't ask for.
                capabilities <- resp.Capabilities &&& ServerCapabilities
                // A client that names a database at connect time (`mysql -D
                // foo`, PDO's DSN `dbname=foo`) gets it auto-created, same
                // as `USE` on a fresh in-memory server with no setup step.
                resp.Database |> Option.iter (Storage.ensureDatabase store)

                // Authenticate before any session state exists; on denial the
                // 1045 is already written and the command loop below never
                // runs (see the guard on `do! loop session` at the bottom).
                let! authOkSeq =
                    authenticateHandshake client stream capabilities store authData resp (handshakeResp.SeqId + 1uy)

                let session =
                    { Session.create connectionId store with
                        User = resp.Username
                        Database = resp.Database
                        CustomFunctions = customFunctions
                        Capabilities = capabilities }

                activeSession <- Some session

                let remoteHost =
                    try
                        string client.Client.RemoteEndPoint
                    with _ ->
                        ""

                // Registered even when auth is about to deny — the command
                // loop below never runs then, and the connection teardown's
                // `unregisterProcess` removes the short-lived entry.
                let processEntry = InformationSchema.registerProcess (int64 connectionId) resp.Username remoteHost
                processEntry.Db <- resp.Database
                // `KILL CONNECTION <id>`: closing the socket makes this
                // connection's next read fail, which ends its command loop.
                processEntry.CloseConnection <- Some(fun () -> try client.Close() with _ -> ())

                match authOkSeq with
                | None -> () // denied: the 1045 is already written, no OK
                | Some okSeq ->
                    do!
                        writePacketAsync
                            stream
                            { SeqId = okSeq
                              Payload = okPayload capabilities (statusFlagsFor session) 0UL 0UL }
                        |> Async.Ignore

                // Runs a statement dispatch under `withCancellationWatch`,
                // catching the `OperationCanceledException` a killed
                // client's abandoned query unwinds with (see
                // `Storage.queryCancellation`) rather than letting it fall
                // through to the connection-level catch-all below, which
                // logs a scarier "connection N: <exn message>" line meant
                // for genuine bugs. `None` means the client is already
                // gone — there's no one to send a reply to, so the caller
                // just lets the command loop end instead of trying to reply
                // then read the next command off a dead stream.
                let runCancellable (dispatch: unit -> Session * Executor.QueryResult) : (Session * Executor.QueryResult) option =
                    try
                        Some(withCancellationWatch client (Some processEntry) dispatch)
                    with :? OperationCanceledException ->
                        Log.diagnostic "fsdb: connection %d: query cancelled (client disconnected)" connectionId
                        None

                let rec loop (session: Session) : Async<unit> =
                    async {
                        activeSession <- Some session

                        match! readPacketWithTimeout client stream with
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
                                          Payload = okPayload capabilities (statusFlagsFor session) 0UL 0UL }
                                    |> Async.Ignore

                                return! loop session
                            | Some(InitDb db) ->
                                if Storage.databaseExists (Session.currentStore session) db then
                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload = okPayload capabilities (statusFlagsFor session) 0UL 0UL }
                                        |> Async.Ignore

                                    return! loop { session with Database = Some db }
                                else
                                    let code, message = Storage.toMySqlError (Storage.NoSuchDatabase db)

                                    do!
                                        writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities code message }
                                        |> Async.Ignore

                                    return! loop session
                            | Some(Query sql) ->
                                processEntry.Command <- "Query"
                                processEntry.State <- "executing"
                                processEntry.StateSince <- DateTime.Now
                                processEntry.Info <- Some sql

                                let dispatched = runCancellable (fun () -> QueryHandler.handle session sql)
                                processEntry.Command <- "Sleep"
                                processEntry.State <- ""
                                processEntry.StateSince <- DateTime.Now
                                processEntry.Info <- None

                                match dispatched with
                                | None -> ()
                                | Some(session, result) ->
                                    activeSession <- Some session
                                    processEntry.Db <- session.Database

                                    do!
                                        sendQueryResult
                                            stream
                                            capabilities
                                            seqId
                                            (statusFlagsFor session)
                                            (uint64 session.LastInsertId)
                                            session.LastResultColumnTypes
                                            result

                                    return! loop session
                            | Some(FieldList table) ->
                                // Deprecated in MySQL 8.0, but PDO/mysqlnd's
                                // metadata probing can still send it —
                                // reply with the table's columns, EOF-terminated,
                                // or a 1146 ERR if the table doesn't exist.
                                let session = QueryHandler.startTransactionStatement session
                                activeSession <- Some session
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
                                        (columns
                                         |> List.map (fun c -> columnDefPayload { Name = c.Name; Type = wireTypeOfColumnType c.Type; Decimals = decimalsOfColumnType c.Type }))
                                        @ [ eofPayload capabilities (statusFlagsFor session) ]

                                    do! sendPayloads stream seqId payloads |> Async.Ignore
                                    return! loop session
                            | Some(StmtPrepare sql) ->
                                match QueryHandler.prepareStatement sql with
                                | Result.Error(code, message) ->
                                    do!
                                        writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities code message }
                                        |> Async.Ignore

                                    return! loop session
                                | Result.Ok(ast, paramCount) ->
                                    let stmtId = session.NextStmtId

                                    let stmt: PreparedStmt =
                                        { Ast = ast
                                          Sql = sql
                                          ParamCount = paramCount
                                          LastParamTypes = None }

                                    let session =
                                        { session with
                                            Statements = Map.add stmtId stmt session.Statements
                                            NextStmtId = stmtId + 1 }

                                    let deprecateEof = capabilities &&& ClientDeprecateEof <> 0u

                                    let paramDefEof =
                                        if paramCount > 0 && not deprecateEof then
                                            [ eofPayload capabilities (statusFlagsFor session) ]
                                        else
                                            []

                                    let payloads =
                                        stmtPrepareOkPayload stmtId paramCount
                                        :: List.replicate paramCount (columnDefPayload { Name = "?"; Type = TypeVarString; Decimals = 0uy })
                                        @ paramDefEof

                                    do! sendPayloads stream seqId payloads |> Async.Ignore
                                    return! loop session
                            | Some(StmtExecute payload) when payload.Length < 9 ->
                                // Header is stmt-id(4) + flags(1) + iteration(4);
                                // a shorter payload can't be decoded — ERR
                                // rather than let the reader throw and drop
                                // the connection.
                                do!
                                    writePacketAsync
                                        stream
                                        { SeqId = seqId; Payload = errPayload capabilities 1047 "Malformed command packet" }
                                    |> Async.Ignore

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
                                | Some _ when session.LongDataOverflow |> Set.exists (fun (sid, _) -> sid = stmtId) ->
                                    // A COM_STMT_SEND_LONG_DATA chunk for this statement
                                    // overflowed the accumulation cap (see there) — that
                                    // command got no reply, so the failure surfaces here
                                    // instead, and the connection stays usable rather than
                                    // executing on silently truncated parameter data.
                                    let session =
                                        { session with
                                            LongData = session.LongData |> Map.filter (fun (sid, _) _ -> sid <> stmtId)
                                            LongDataOverflow = session.LongDataOverflow |> Set.filter (fun (sid, _) -> sid <> stmtId) }

                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload = errPayload capabilities 1153 "Got a packet bigger than 'max_allowed_packet' bytes" }
                                        |> Async.Ignore

                                    return! loop session
                                | Some stmt ->
                                    // A malformed COM_STMT_EXECUTE payload (a declared type not
                                    // matching what's actually on the wire, a truncated param,
                                    // ...) makes `Reader`'s reads throw straight out of this
                                    // decode step — caught here rather than escaping the
                                    // connection loop (see the ponytail note on `readBinaryValue`
                                    // in Protocol.fs for the other half of this: a well-formed but
                                    // un-representable value like a zero DATETIME, clamped instead
                                    // of thrown).
                                    let decoded =
                                        try
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
                                            | Result.Error message -> Result.Error message
                                            | Result.Ok types ->
                                                let isNull i =
                                                    (int nullBitmap.[i / 8] >>> (i % 8)) &&& 1 = 1

                                                let values =
                                                    types
                                                    |> List.mapi (fun i (typeId, unsigned) ->
                                                        match Map.tryFind (stmtId, i) session.LongData with
                                                        // Binary/blob params keep the raw bytes a
                                                        // COM_STMT_SEND_LONG_DATA sender streamed in —
                                                        // force-decoding them as UTF-8 corrupts any byte
                                                        // sequence that isn't valid UTF-8 (an image, a
                                                        // compressed column, ...). Only text types decode.
                                                        | Some bytes -> if typeId = TypeBlob then VBytes bytes else VString(Encoding.UTF8.GetString bytes)
                                                        | None -> if isNull i then VNull else readBinaryValue r typeId unsigned)

                                                Result.Ok(types, values)
                                        with ex ->
                                            Result.Error ex.Message

                                    match decoded with
                                    | Result.Error message ->
                                        do!
                                            writePacketAsync
                                                stream
                                                { SeqId = seqId; Payload = errPayload capabilities 1210 message }
                                            |> Async.Ignore

                                        return! loop session
                                    | Result.Ok(types, values) ->
                                        let session =
                                            { session with
                                                Statements = Map.add stmtId { stmt with LastParamTypes = Some types } session.Statements
                                                LongData = session.LongData |> Map.filter (fun (sid, _) _ -> sid <> stmtId) }

                                        match runCancellable (fun () -> QueryHandler.executePrepared session stmt values) with
                                        | None -> ()
                                        | Some(session, result) ->
                                            activeSession <- Some session

                                            do!
                                                sendBinaryQueryResult
                                                    stream
                                                    capabilities
                                                    seqId
                                                    (statusFlagsFor session)
                                                    (uint64 session.LastInsertId)
                                                    session.LastResultColumnTypes
                                                    result

                                            return! loop session
                            | Some(StmtSendLongData payload) when payload.Length < 6 ->
                                // stmt-id(4) + param-index(2); a shorter payload
                                // is malformed. COM_STMT_SEND_LONG_DATA takes
                                // no reply, so just skip it (never throw).
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
                                let chunk = r.ReadBytes(max 0 r.Remaining)

                                if session.Statements.ContainsKey stmtId then
                                    let key = stmtId, paramIndex
                                    let existing = session.LongData |> Map.tryFind key |> Option.defaultValue [||]
                                    // Cap accumulated long-data per param at the same ceiling
                                    // `readPacketAsync` enforces for a reassembled packet.
                                    // COM_STMT_SEND_LONG_DATA never gets a reply, success or
                                    // failure, so a chunk that would blow the cap can't error
                                    // out here — it marks the param overflowed instead, and
                                    // the next COM_STMT_EXECUTE turns that into ER_NET_PACKET_
                                    // TOO_LARGE (1153) rather than silently truncating the
                                    // parameter's data and executing on short input.
                                    let room = maxAccumulatedPacketSize - existing.Length

                                    if room <= 0 || chunk.Length > room then
                                        return!
                                            loop
                                                { session with
                                                    LongDataOverflow = Set.add key session.LongDataOverflow }
                                    else
                                        return! loop { session with LongData = Map.add key (Array.append existing chunk) session.LongData }
                                else
                                    return! loop session
                            | Some(StmtClose stmtId) ->
                                // No reply, per protocol.
                                return!
                                    loop
                                        { session with
                                            Statements = Map.remove stmtId session.Statements
                                            LongData = session.LongData |> Map.filter (fun (sid, _) _ -> sid <> stmtId)
                                            LongDataOverflow = session.LongDataOverflow |> Set.filter (fun (sid, _) -> sid <> stmtId) }
                            | Some(StmtReset stmtId) ->
                                if session.Statements.ContainsKey stmtId then
                                    let session =
                                        { session with
                                            LongData = session.LongData |> Map.filter (fun (sid, _) _ -> sid <> stmtId)
                                            LongDataOverflow = session.LongDataOverflow |> Set.filter (fun (sid, _) -> sid <> stmtId) }

                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload = okPayload capabilities (statusFlagsFor session) 0UL 0UL }
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
                            | Some ResetConnection ->
                                // Resets session state (variables, prepared
                                // statements, buffered long-data, any open
                                // transaction) the same way a fresh
                                // connection would start, but keeps the
                                // already-authenticated socket and current
                                // database — what connection pools use this
                                // for instead of a full reconnect.
                                let session =
                                    { Session.create session.ConnectionId session.Store with
                                        User = session.User
                                        Database = session.Database
                                        CustomFunctions = session.CustomFunctions
                                        Capabilities = session.Capabilities }

                                activeSession <- Some session

                                do!
                                    writePacketAsync
                                        stream
                                        { SeqId = seqId
                                          Payload = okPayload capabilities (statusFlagsFor session) 0UL 0UL }
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
                            | Some(Malformed _) ->
                                // Reply ERR and keep the connection alive —
                                // see `Malformed`'s doc.
                                do!
                                    writePacketAsync
                                        stream
                                        { SeqId = seqId
                                          Payload = errPayload capabilities 1047 "Malformed command packet" }
                                    |> Async.Ignore

                                return! loop session
                    }

                if authOkSeq.IsSome then
                    do! loop session
        with
        | :? PacketTooLargeException ->
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
        | error ->
            InformationSchema.unregisterProcess (int64 connectionId)
            activeSession |> Option.iter QueryHandler.closeSession
            return raise error

        InformationSchema.unregisterProcess (int64 connectionId)
        activeSession |> Option.iter QueryHandler.closeSession
    }

/// Starts listening on address:port. Pass port 0 for an OS-assigned
/// ephemeral port (used by the integration tests); read it back via `port`.
let startListening (address: IPAddress) (port: int) : TcpListener =
    InformationSchema.serverStartedAt <- DateTime.Now
    let listener = new TcpListener(address, port)
    listener.Start()
    listener

let port (listener: TcpListener) : int =
    (listener.LocalEndpoint :?> IPEndPoint).Port

/// None once the listener has been stopped/disposed — the clean way to shut
/// the server down from the outside. `InvalidOperationException` is what
/// `AcceptTcpClientAsync` throws when a concurrent `Stop()` lands before the
/// accept starts ("Not listening") — same shutdown, third spelling.
let private tryAccept (listener: TcpListener) : Async<TcpClient option> =
    async {
        try
            let! client = listener.AcceptTcpClientAsync() |> Async.AwaitTask
            return Some client
        with
        | :? ObjectDisposedException
        | :? InvalidOperationException
        | :? SocketException -> return None
    }

/// Accepts connections until the listener is stopped, handling each on its
/// own async against the one shared `store` every session reads/writes
/// through, with `customFunctions` (an embedding `Db`'s registered scalars/
/// aggregates — `Functions.empty` if none) available to every statement any
/// connection runs. A failing connection is logged, never fatal to the
/// server.
let serve (listener: TcpListener) (store: Storage.Store) (customFunctions: Functions.Registry) : Async<unit> =
    // Bounds concurrent connections (see `maxConcurrentConnections`): held
    // for the lifetime of each connection, acquired before accepting the
    // next one so the accept loop itself blocks once the cap is hit rather
    // than accepting unboundedly and queuing work behind the scenes.
    // Deliberately not `use` — `serve` returns this `Async<unit>` unevaluated;
    // disposing here would run at function-return time, before the loop
    // that actually needs it ever executes. Lives for the process's
    // lifetime, same as the listener it's paired with.
    let connectionSlots = new SemaphoreSlim(maxConcurrentConnections)

    let rec loop () : Async<unit> =
        async {
            do! connectionSlots.WaitAsync() |> Async.AwaitTask

            match! tryAccept listener with
            | None -> connectionSlots.Release() |> ignore
            | Some client ->
                // Process-wide, not per-listener: the process registry
                // (`InformationSchema.registerProcess`) and `KILL <id>` key
                // on this number, so two listeners in one process (the test
                // suite, the embedding API) must never hand out the same id.
                let connectionId = int (Interlocked.Increment connectionCounter)

                Async.Start(
                    async {
                        try
                            try
                                do! handleConnection connectionId store customFunctions client
                            with ex ->
                                Log.diagnostic "fsdb: connection %d: %s" connectionId ex.Message
                        finally
                            connectionSlots.Release() |> ignore
                    }
                )

                return! loop ()
        }

    loop ()
