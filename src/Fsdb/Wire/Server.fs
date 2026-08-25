/// TCP listener and per-connection command loop: handshake, then
/// COM_QUERY / COM_STATISTICS / COM_PROCESS_INFO / COM_PING / COM_INIT_DB /
/// COM_QUIT.
module Fsdb.Server

open System
open System.Security.Cryptography
open System.Net
open System.Net.Sockets
open System.Net.Security
open System.Security.Authentication
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
    | Statistics
    | ProcessInfo
    | ProcessKill of connectionId: int64
    | Debug
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
    | SetOption of option: int
    | ResetConnection
    | Unsupported of code: byte
    /// A command byte this server recognizes, but whose payload was too
    /// short/malformed to decode (e.g. a truncated COM_STMT_CLOSE with no
    /// 4-byte statement id). Answered with an ERR rather than let the
    /// `Reader`/`Encoding` exception escape the command loop and drop the
    /// connection.
    | Malformed of code: byte

let private stmtExecuteHeaderLength = 9
let private stmtLongDataHeaderLength = 6

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
                | 0x09uy -> Statistics
                | 0x0auy -> ProcessInfo
                | 0x0cuy -> ProcessKill(int64 (Reader(restBytes ()).ReadInt32LE()))
                | 0x0duy -> Debug
                | 0x0euy -> Ping
                | 0x16uy -> StmtPrepare(rest ())
                | 0x17uy -> StmtExecute(restBytes ())
                | 0x18uy -> StmtSendLongData(restBytes ())
                | 0x19uy -> StmtClose(Reader(restBytes ()).ReadInt32LE())
                | 0x1auy -> StmtReset(Reader(restBytes ()).ReadInt32LE())
                | 0x1buy -> SetOption(Reader(restBytes ()).ReadInt16LE())
                | 0x1fuy -> ResetConnection
                | b -> Unsupported b
            )
        with _ ->
            Some(Malformed payload.[0])

let private isShutdownStatement (sql: string) : bool =
    let text = sql.Trim()
    let text = if text.EndsWith ';' then text.[..text.Length - 2].TrimEnd() else text
    text.Equals("SHUTDOWN", StringComparison.OrdinalIgnoreCase)

let private randomAuthPluginData () : byte[] =
    let bytes = Array.zeroCreate<byte> 20
    RandomNumberGenerator.Fill bytes
    // Auth-plugin-data fields are null-terminated on the wire, so replace
    // zero bytes before using the same value for password verification.
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
    (warningCount: int)
    (columnMetadata: ColumnMetadata list)
    (result: Executor.QueryResult)
    : byte[] list =
    match result with
    | Affected affectedRows -> [ okPayloadWithWarnings capabilities statusFlags affectedRows lastInsertId warningCount ]
    | Err(code, message) -> [ errPayload capabilities code message ]
    | ResultSet(columns, rows) ->
        let deprecateEof = capabilities &&& ClientDeprecateEof <> 0u

        // Metadata only applies when the caller has one entry per result
        // entry per column (a mismatch means "no real info", e.g. a probe
        // result — see `Session.LastResultColumnMetadata`'s doc). The column
        // definition packets and `sendResult`'s row encoder reconcile to
        // this same `types`, so the two can never disagree.
        let metadata =
            if List.length columnMetadata = columns.Length then
                columnMetadata
            else
                List.replicate columns.Length (Value.columnMetadata TypeVarString)

        let columnCountPayload =
            let w = Writer()
            w.WriteLenEncInt(uint64 columns.Length)
            w.ToArray()

        // The `decimals` (fsp) each column advertises: for a DATETIME/
        // TIMESTAMP/TIME column, read back the fractional digits the renderer
        // already emitted into the rows (see `Protocol.fractionalDigitsOf`),
        // so a client learns a `DATETIME(6)`/`TIME(3)`'s precision even on an
        // exact second. Non-temporal columns advertise 0.
        let withValuePrecision (colIndex: int) (metadata: ColumnMetadata) =
            if metadata.Decimals = 0uy && (metadata.TypeId = TypeDateTime || metadata.TypeId = TypeTime) then
                let decimals =
                    rows |> List.map (fun r -> List.tryItem colIndex r |> Option.flatten) |> Protocol.fractionalDigitsOf

                { metadata with Decimals = decimals }
            else
                metadata

        [ columnCountPayload ]
        @ (List.zip columns metadata
           |> List.mapi (fun i (name, metadata) -> columnDefPayload { Name = name; Metadata = withValuePrecision i metadata }))
        @ (if deprecateEof then [] else [ eofPayloadWithWarnings capabilities statusFlags warningCount ])

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
    (warningCount: int)
    (columnMetadata: ColumnMetadata list)
    (rowEncoder: ColumnMetadata list -> string option list -> byte[])
    (result: Executor.QueryResult)
    : Async<byte> =
    async {
        let! seqId = sendPayloads stream startSeq (resultHeadPayloads capabilities statusFlags lastInsertId warningCount columnMetadata result)

        match result with
        | ResultSet(columns, rows) ->
            let metadata =
                if List.length columnMetadata = columns.Length then
                    columnMetadata
                else
                    List.replicate columns.Length (Value.columnMetadata TypeVarString)

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
                let payload = rowEncoder metadata row

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

            let! nextSeqId =
                sendPayloads
                    stream
                    seqId
                    [ (if deprecateEof then
                           okEndOfResultSetPayloadWithWarnings capabilities statusFlags warningCount
                       else
                           eofPayloadWithWarnings capabilities statusFlags warningCount) ]
            return nextSeqId
        | _ -> return seqId
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
    (warningCount: int)
    (columnMetadata: ColumnMetadata list)
    (result: Executor.QueryResult)
    : Async<unit> =
    sendResult stream capabilities startSeq statusFlags lastInsertId warningCount columnMetadata textRowPayloadTyped result
    |> Async.Ignore

/// As `sendQueryResult`, returning the next packet sequence id for a
/// multi-result COM_QUERY response.
let sendQueryResultAndNextSeq
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (lastInsertId: uint64)
    (warningCount: int)
    (columnMetadata: ColumnMetadata list)
    (result: Executor.QueryResult)
    : Async<byte> =
    sendResult stream capabilities startSeq statusFlags lastInsertId warningCount columnMetadata textRowPayloadTyped result

/// As `sendQueryResult`, but encodes resultset rows in the binary protocol
/// row format COM_STMT_EXECUTE requires (`binaryRowPayload`, which — unlike
/// `textRowPayload` — reads `columnMetadata` to pick each value's
/// wire encoding, not just what `columnDefPayload` advertises).
let sendBinaryQueryResult
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (lastInsertId: uint64)
    (warningCount: int)
    (columnMetadata: ColumnMetadata list)
    (result: Executor.QueryResult)
    : Async<unit> =
    sendResult stream capabilities startSeq statusFlags lastInsertId warningCount columnMetadata binaryRowPayload result
    |> Async.Ignore

/// `SERVER_STATUS_AUTOCOMMIT` always, plus `SERVER_STATUS_IN_TRANS` while
/// `session.Tx` is open — every OK/EOF packet reports this so PDO's
/// `inTransaction()`/`beginTransaction()`/`commit()` (which read the status
/// bit off the wire, not just whatever `COMMIT`/`ROLLBACK` themselves reply)
/// see the real transaction state.
let private statusFlagsFor (session: Session) : int =
    StatusAutocommit ||| (if session.Tx.IsSome then StatusInTrans else 0)

let private statusFlagsForMore (session: Session) = statusFlagsFor session ||| StatusMoreResultsExists

let private warningCountFor (session: Session) =
    min (int UInt16.MaxValue) session.Diagnostics.Length

let private localInfileRequestPayload (fileName: string) =
    Array.append [| 0xfbuy |] (Encoding.UTF8.GetBytes fileName)

let private decodeLocalLoad (load: Parser.LocalLoad) (bytes: byte[]) : Result<Value list list, int * string> =
    try
        let text = UTF8Encoding(false, true).GetString bytes
        let enclosedBy = load.EnclosedBy |> Option.bind (fun value -> if value.Length = 1 then Some value.[0] else None)
        let escape = load.Escape |> Option.bind (fun value -> if value.Length = 1 then Some value.[0] else None)
        let nullMarker = escape |> Option.map (fun value -> string value + "N")
        let rows = ResizeArray<Value list>()
        let fields = ResizeArray<Value>()
        let value = StringBuilder()
        let raw = StringBuilder()
        let mutable index = 0
        let mutable enclosed = false
        let mutable fieldEnclosed = false
        let mutable escaped = false

        let endField () =
            let text = value.ToString()
            let rawText = raw.ToString()
            fields.Add(if not fieldEnclosed && Some rawText = nullMarker then VNull else VString text)
            value.Clear() |> ignore
            raw.Clear() |> ignore
            fieldEnclosed <- false

        let endRow () =
            endField ()
            rows.Add(List.ofSeq fields)
            fields.Clear()

        let startsWith (value: string) =
            value <> ""
            && index + value.Length <= text.Length
            && String.CompareOrdinal(text, index, value, 0, value.Length) = 0

        while index < text.Length do
            let current = text.[index]
            raw.Append current |> ignore

            if escaped then
                value.Append(
                    match current with
                    | '0' -> '\u0000'
                    | 'b' -> '\b'
                    | 'n' -> '\n'
                    | 'r' -> '\r'
                    | 't' -> '\t'
                    | value -> value
                )
                |> ignore

                escaped <- false
                index <- index + 1
            elif escape = Some current then
                escaped <- true
                index <- index + 1
            elif enclosed then
                if enclosedBy = Some current then
                    enclosed <- false
                else
                    value.Append current |> ignore

                index <- index + 1
            elif enclosedBy = Some current && value.Length = 0 then
                enclosed <- true
                fieldEnclosed <- true
                index <- index + 1
            elif startsWith load.LineTerminator then
                raw.Length <- raw.Length - 1
                endRow ()
                index <- index + load.LineTerminator.Length
            elif startsWith load.FieldTerminator then
                raw.Length <- raw.Length - 1
                endField ()
                index <- index + load.FieldTerminator.Length
            else
                value.Append current |> ignore
                index <- index + 1

        if escaped || enclosed then
            Result.Error(1300, "Invalid LOAD DATA input")
        else
            if value.Length > 0 || raw.Length > 0 || fields.Count > 0 then
                endRow ()

            Result.Ok(rows |> Seq.skip (min load.IgnoreLines rows.Count) |> List.ofSeq)
    with :? DecoderFallbackException ->
        Result.Error(1300, "Invalid utf8mb4 character string")

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

/// Why waiting on the *next* command packet needs a timeout of its own (the
/// value itself is `Limits.waitTimeoutSeconds`): a half-open peer that opens
/// a connection and sends nothing (or only a partial 4-byte packet header)
/// would otherwise pin a thread-pool task and a socket forever.
/// `Socket.ReceiveTimeout` doesn't help here: it only bounds the synchronous
/// `Read()`, and this server exclusively awaits `ReadAsync` — which .NET does
/// not honor it for, and which F#'s own cooperative cancellation (including
/// `Async.StartChild`'s timeout) can't preempt either, since a `Task`-backed
/// await only resumes when that task actually completes. Only bounds time
/// spent waiting to *start* reading a packet, not time spent running a long
/// query in between.

/// `readPacketAsync`, but abandoned if no complete packet arrives within
/// `timeoutMs` — see `readPacketWithTimeout`'s doc for why this races the
/// read against `Task.Delay` instead of a cancellation token. When the
/// timer wins, the stuck read has to be forced to unblock: closing
/// `client`'s socket faults the pending `ReadAsync` with an exception,
/// which is what actually reaps the connection (this function then reports
/// that as `None`, same as any other clean-disconnect path — the caller
/// already treats `None` as "stop the command loop"). Not private: the
/// timeout itself is exercised directly by the test suite with a short
/// `timeoutMs` rather than waiting out the real 5-minute production value.
let private readWithTimeoutMs
    (read: IO.Stream -> Async<Packet option>)
    (timeoutMs: int)
    (client: TcpClient)
    (stream: IO.Stream)
    : Async<Packet option> =
    async {
        let readTask = Async.StartAsTask(read stream)
        // Cancelled on the way out so the loser's `Task.Delay` dies with the
        // read that beat it. Without this, every packet read on every
        // connection leaves a live five-minute timer behind — a busy
        // connection accumulates one per statement until they all fire for
        // nothing.
        let timerCts = new CancellationTokenSource()

        try
            let! winner =
                Threading.Tasks.Task.WhenAny(
                    readTask :> Threading.Tasks.Task,
                    Threading.Tasks.Task.Delay(timeoutMs, timerCts.Token)
                )
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
        finally
            timerCts.Cancel()
            timerCts.Dispose()
    }

let readPacketWithTimeoutMs (timeoutMs: int) (client: TcpClient) (stream: IO.Stream) : Async<Packet option> =
    readWithTimeoutMs readPacketAsync timeoutMs client stream

/// Converts the MySQL seconds-valued timeout without wrapping the `int`
/// milliseconds accepted by `Task.Delay`. Values beyond that API's range
/// use its longest finite delay instead of breaking every connection before
/// authentication.
let timeoutMilliseconds (timeoutSeconds: int) : int =
    int (min (int64 Int32.MaxValue) (max 0L (int64 timeoutSeconds * 1000L)))

let private readPacketWithTimeoutSeconds (timeoutSeconds: int) (client: TcpClient) (stream: IO.Stream) : Async<Packet option> =
    readPacketWithTimeoutMs (timeoutMilliseconds timeoutSeconds) client stream

let private readPhysicalPacketWithTimeoutSeconds (timeoutSeconds: int) (client: TcpClient) (stream: IO.Stream) : Async<Packet option> =
    readWithTimeoutMs readPhysicalPacketAsync (timeoutMilliseconds timeoutSeconds) client stream

let private receiveLocalData
    (client: TcpClient)
    (stream: IO.Stream)
    (timeoutSeconds: int)
    (startSeqId: byte)
    : Async<Result<byte[] * byte, (int * string) * byte>> =
    async {
        use bytes = new IO.MemoryStream()
        let mutable expectedSeqId = startSeqId
        let mutable finished = false
        let mutable error: (int * string) option = None

        while not finished do
            match! readPhysicalPacketWithTimeoutSeconds timeoutSeconds client stream with
            | None ->
                finished <- true
                error <- Some(2013, "Lost connection to client during LOAD DATA LOCAL INFILE")
            | Some packet when packet.SeqId <> expectedSeqId ->
                finished <- true
                error <- Some(1156, "Packets out of order during LOAD DATA LOCAL INFILE")
            | Some packet ->
                expectedSeqId <- expectedSeqId + 1uy

                if packet.Payload.Length = 0 then
                    finished <- true
                elif error.IsNone && int64 bytes.Length + int64 packet.Payload.Length > int64 Limits.maxLoadDataBytes then
                    error <- Some(1153, "LOAD DATA LOCAL INFILE exceeds max_load_data_bytes")
                elif error.IsNone then
                    bytes.Write(packet.Payload, 0, packet.Payload.Length)

        match error with
        | Some error -> return Result.Error(error, expectedSeqId)
        | None -> return Result.Ok(bytes.ToArray(), expectedSeqId)
    }

let private sessionWaitTimeout (session: Session) =
    match session.Variables |> Map.tryFind "wait_timeout" |> Option.flatten with
    | Some value ->
        match Int32.TryParse value with
        | true, seconds -> seconds
        | _ -> Limits.waitTimeoutSeconds
    | None -> Limits.waitTimeoutSeconds

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
let private activeConnectionCounter = ref 0

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
/// `Some(seqId, account)` on success — `firstSeq + 1` more when an
/// AuthSwitch round trip happened.
let private authenticateHandshake
    (client: TcpClient)
    (stream: IO.Stream)
    (capabilities: uint32)
    (store: Storage.Store)
    (authData: byte[])
    (resp: HandshakeResponse)
    (clientHost: string option)
    (firstSeq: byte)
    : Async<(byte * Auth.Account) option> =
    async {
        let deny (seqId: byte) (usingPassword: bool) =
            async {
                let msg =
                    sprintf
                        "Access denied for user '%s'@'%s' (using password: %s)"
                        resp.Username
                        (clientHost |> Option.defaultValue "unknown")
                        (if usingPassword then "YES" else "NO")

                do! writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities 1045 msg } |> Async.Ignore
                return None
            }

        match clientHost |> Option.bind (Auth.resolveAccount store resp.Username) with
        | None -> return! deny firstSeq (resp.AuthResponse.Length > 0)
        | Some(_, cols, row) when Auth.isAccountLocked cols row ->
            let message =
                sprintf
                    "Access denied for user '%s'@'%s'. Account is locked."
                    resp.Username
                    (clientHost |> Option.defaultValue "unknown")

            do! writePacketAsync stream { SeqId = firstSeq; Payload = errPayload capabilities 3118 message } |> Async.Ignore
            return None
        | Some(selected, cols, row) ->
            let stored = Auth.storedPasswordHash cols row

            if stored = "" then
                // No password set: only an empty offered password matches
                // (every auth plugin sends a zero-length response for an
                // empty password, so no AuthSwitch round trip is needed).
                if resp.AuthResponse.Length = 0 then
                    return Some(firstSeq, selected)
                else
                    return! deny firstSeq true
            elif Auth.verifyNative stored authData resp.AuthResponse then
                return Some(firstSeq, selected)
            elif resp.ClientPlugin = Some "mysql_native_password" then
                // Right plugin, wrong password — no switch will fix it.
                return! deny firstSeq (resp.AuthResponse.Length > 0)
            else
                // The client answered with another plugin (mysql CLI 8.x
                // defaults to caching_sha2_password) or nothing; ask it to
                // redo the same scramble with mysql_native_password.
                do! writePacketAsync stream { SeqId = firstSeq; Payload = authSwitchPayload authData } |> Async.Ignore

                match! readPacketWithTimeoutSeconds Limits.waitTimeoutSeconds client stream with
                | None -> return None // client gave up; nothing to reply to
                | Some switchResp when Auth.verifyNative stored authData switchResp.Payload ->
                    return Some(switchResp.SeqId + 1uy, selected)
                | Some switchResp -> return! deny (switchResp.SeqId + 1uy) (switchResp.Payload.Length > 0)
    }

let private accumulateLongData (key: int * int) (chunk: byte[]) (session: Session) : Session =
    let existing = session.LongData |> Map.tryFind key |> Option.defaultValue []
    let room = int64 Limits.maxAllowedPacket - session.LongDataBytes

    if chunk.Length = 0 then
        session
    elif int64 chunk.Length > room then
        { session with LongDataOverflow = Set.add key session.LongDataOverflow }
    else
        { session with
            LongData = Map.add key (chunk :: existing) session.LongData
            LongDataBytes = session.LongDataBytes + int64 chunk.Length }

let private discardLongData (statementId: int) (session: Session) : Session =
    let retained = session.LongData |> Map.filter (fun (id, _) _ -> id <> statementId)
    let retainedBytes =
        retained
        |> Seq.sumBy (fun (KeyValue(_, chunks)) -> chunks |> List.sumBy (fun bytes -> int64 bytes.Length))

    { session with
        LongData = retained
        LongDataBytes = retainedBytes
        LongDataOverflow = session.LongDataOverflow |> Set.filter (fun (id, _) -> id <> statementId) }

let private handleConnection
    (connectionId: int)
    (store: Storage.Store)
    (customFunctions: Functions.Registry)
    (options: ServerOptions.Settings)
    (shutdown: unit -> unit)
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
        use networkStream = client.GetStream()
        let mutable stream: IO.Stream = networkStream
        let mutable tlsStream: SslStream option = None
        let mutable tlsVersion: string option = None
        let mutable tlsCipher: string option = None
        let offeredCapabilities = serverCapabilities options.Certificate.IsSome

        let closeTls () =
            tlsStream
            |> Option.iter (fun secured ->
                try
                    secured.Dispose()
                with _ ->
                    ())

        let upgradeToTls (certificate: Security.Cryptography.X509Certificates.X509Certificate2) =
            async {
                let secured = new SslStream(networkStream, false)
                let authentication = SslServerAuthenticationOptions()
                authentication.ServerCertificate <- certificate
                authentication.EnabledSslProtocols <- SslProtocols.Tls12 ||| SslProtocols.Tls13
                authentication.ClientCertificateRequired <- false
                authentication.AllowRenegotiation <- false

                use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(float Limits.waitTimeoutSeconds))

                try
                    do!
                        secured.AuthenticateAsServerAsync(authentication, timeout.Token)
                        |> Async.AwaitTask
                with error ->
                    secured.Dispose()
                    return raise error
                stream <- secured
                tlsStream <- Some secured

                tlsVersion <-
                    match secured.SslProtocol with
                    | SslProtocols.Tls12 -> Some "TLSv1.2"
                    | SslProtocols.Tls13 -> Some "TLSv1.3"
                    | protocol -> Some(string protocol)

                tlsCipher <- Some(string secured.NegotiatedCipherSuite)
            }

        // Negotiated once the handshake response arrives; used as a fallback
        // for the "packet too large" ERR reply if that happens beforehand.
        let mutable capabilities = offeredCapabilities
        let mutable activeSession: Session option = None

        try
            let authData = randomAuthPluginData ()

            do!
                writePacketAsync stream { SeqId = 0uy; Payload = buildHandshakeV10WithCapabilities offeredCapabilities connectionId authData }
                |> Async.Ignore

            match! readPacketWithTimeoutSeconds Limits.waitTimeoutSeconds client stream with
            | None -> ()
            | Some firstHandshakePacket ->
                let! handshakeResp =
                    async {
                        match tryParseSslRequest firstHandshakePacket.Payload with
                        | None -> return Some firstHandshakePacket
                        | Some request ->
                            capabilities <- request.Capabilities &&& offeredCapabilities

                            match options.Certificate with
                            | None ->
                                do!
                                    writePacketAsync
                                        stream
                                        { SeqId = firstHandshakePacket.SeqId + 1uy
                                          Payload = errPayload capabilities 1045 "SSL is not supported" }
                                    |> Async.Ignore

                                return None
                            | Some certificate ->
                                do! upgradeToTls certificate
                                return! readPacketWithTimeoutSeconds Limits.waitTimeoutSeconds client stream
                    }

                match handshakeResp with
                | None ->
                    closeTls ()
                    return ()
                | Some _ -> ()

                let handshakeResp = handshakeResp.Value
                let resp = parseHandshakeResponse handshakeResp.Payload
                let clientHost =
                    match client.Client.RemoteEndPoint with
                    | :? IPEndPoint as endpoint -> Some(endpoint.Address.ToString())
                    | _ -> None
                let displayHost =
                    let address =
                        clientHost
                        |> Option.bind (fun host ->
                            match IPAddress.TryParse host with
                            | true, parsed -> Some parsed
                            | _ -> None)

                    match address with
                    | Some address when IPAddress.IsLoopback address -> "localhost"
                    | _ -> clientHost |> Option.defaultValue "unknown"
                // Effective capabilities: never claim something the client didn't ask for.
                capabilities <- resp.Capabilities &&& offeredCapabilities
                // Authenticate before any session state exists; on denial the
                // 1045 is already written and the command loop below never
                // runs (see the guard on `do! loop session` at the bottom).
                let! authOkSeq =
                    if options.RequireSecureTransport && tlsVersion.IsNone then
                        async {
                            do!
                                writePacketAsync
                                    stream
                                    { SeqId = handshakeResp.SeqId + 1uy
                                      Payload =
                                        errPayload
                                            capabilities
                                            3159
                                            "Connections using insecure transport are prohibited while --require_secure_transport=ON." }
                                |> Async.Ignore

                            return None
                        }
                    else
                        authenticateHandshake client stream capabilities store authData resp clientHost (handshakeResp.SeqId + 1uy)
                let mutable databaseAccepted = false
                let selectedAccount = authOkSeq |> Option.map snd |> Option.defaultValue (Auth.account resp.Username "%")

                let session =
                    { Session.create connectionId store with
                        User = selectedAccount.Name
                        AccountHost = selectedAccount.Host
                        LoginUser = resp.Username
                        ClientHost = displayHost
                        Database = resp.Database
                        CustomFunctions = customFunctions
                        Capabilities = capabilities
                        MultiStatementsEnabled = capabilities &&& ClientMultiStatements <> 0u
                        TlsVersion = tlsVersion
                        TlsCipher = tlsCipher }

                activeSession <- Some session

                let remoteHost =
                    try string client.Client.RemoteEndPoint with _ -> ""

                // Registered even when auth is about to deny — the command
                // loop below never runs then, and the connection teardown's
                // `unregisterProcess` removes the short-lived entry.
                let processEntry = InformationSchema.registerProcessAs (int64 connectionId) selectedAccount resp.Username remoteHost
                processEntry.Db <- resp.Database
                // `KILL CONNECTION <id>`: closing the socket makes this
                // connection's next read fail, which ends its command loop.
                processEntry.CloseConnection <- Some(fun () -> try client.Close() with _ -> ())

                match authOkSeq with
                | None -> () // denied: the 1045 is already written, no OK
                | Some(okSeq, _) ->
                    let databaseAllowed =
                        match resp.Database with
                        | None -> Ok()
                        | Some db when Storage.databaseExists store db -> Ok()
                        | Some db -> Auth.checkForAccount store selectedAccount [ "CREATE", Auth.OnDb db ]

                    match databaseAllowed with
                    | Error(code, message) ->
                        do! writePacketAsync stream { SeqId = okSeq; Payload = errPayload capabilities code message } |> Async.Ignore
                    | Ok() ->
                        resp.Database |> Option.iter (Storage.ensureDatabase store)
                        databaseAccepted <- true

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

                        match! readPacketWithTimeoutSeconds (sessionWaitTimeout session) client stream with
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
                                InformationSchema.recordQuestion ()
                                if isShutdownStatement sql then
                                    match Auth.checkForAccount store (Auth.account session.User session.AccountHost) [ "SHUTDOWN", Auth.Global ] with
                                    | Ok() ->
                                        do!
                                            writePacketAsync
                                                stream
                                                { SeqId = seqId
                                                  Payload = okPayload capabilities (statusFlagsFor session) 0UL 0UL }
                                            |> Async.Ignore

                                        shutdown ()
                                    | Error(code, message) ->
                                        do!
                                            writePacketAsync
                                                stream
                                                { SeqId = seqId
                                                  Payload = errPayload capabilities code message }
                                            |> Async.Ignore

                                        return! loop session
                                else
                                    processEntry.Command <- "Query"
                                    processEntry.State <- "executing"
                                    processEntry.StateSince <- DateTime.Now
                                    processEntry.Info <- Some(Log.redactSql sql)

                                    let statements =
                                        match Parser.splitStatements sql with
                                        | Result.Ok statements -> Result.Ok statements
                                        | Result.Error _ -> Result.Error(1064, "You have an error in your SQL syntax")

                                    processEntry.Command <- "Sleep"
                                    processEntry.State <- ""
                                    processEntry.StateSince <- DateTime.Now
                                    processEntry.Info <- None

                                    let multiStatements = session.MultiStatementsEnabled && capabilities &&& ClientMultiResults <> 0u

                                    match statements with
                                    | Result.Error(code, message) ->
                                        do! writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities code message } |> Async.Ignore
                                        return! loop session
                                    | Result.Ok statements when statements.Length > 1 && not multiStatements ->
                                        do!
                                            writePacketAsync
                                                stream
                                                { SeqId = seqId; Payload = errPayload capabilities 1064 "You have an error in your SQL syntax" }
                                            |> Async.Ignore

                                        return! loop session
                                    | Result.Ok [] ->
                                        let dispatched = runCancellable (fun () -> QueryHandler.handle session sql)

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
                                                    (warningCountFor session)
                                                    session.LastResultColumnMetadata
                                                    result

                                            return! loop session
                                    | Result.Ok statements ->
                                        let rec sendBatch session seqId statements =
                                            async {
                                                match statements with
                                                | [] -> return Some session
                                                | statement :: remaining ->
                                                    let! dispatched =
                                                        match QueryHandler.tryPrepareLocalLoad session statement with
                                                        | Result.Error result -> async { return Some(session, result, seqId) }
                                                        | Result.Ok None ->
                                                            async {
                                                                return
                                                                    runCancellable (fun () -> QueryHandler.handle session statement)
                                                                    |> Option.map (fun (nextSession, result) -> nextSession, result, seqId)
                                                            }
                                                        | Result.Ok(Some load) when not Limits.localInfile || capabilities &&& ClientLocalFiles = 0u ->
                                                            async {
                                                                return
                                                                    Some(
                                                                        session,
                                                                        Err(3948, "Loading local data is disabled; this must be enabled on both the client and server sides"),
                                                                        seqId
                                                                    )
                                                            }
                                                        | Result.Ok(Some load) ->
                                                            async {
                                                                let! uploadSeqId =
                                                                    writePacketAsync stream { SeqId = seqId; Payload = localInfileRequestPayload load.FileName }

                                                                match! receiveLocalData client stream (sessionWaitTimeout session) uploadSeqId with
                                                                | Result.Error((code, _), _) when code = 2013 || code = 1156 ->
                                                                    client.Close()
                                                                    return None
                                                                | Result.Error((code, message), responseSeqId) -> return Some(session, Err(code, message), responseSeqId)
                                                                | Result.Ok(bytes, responseSeqId) ->
                                                                    match decodeLocalLoad load bytes with
                                                                    | Result.Error(code, message) -> return Some(session, Err(code, message), responseSeqId)
                                                                    | Result.Ok rows ->
                                                                        return
                                                                            runCancellable (fun () -> QueryHandler.executeLocalLoad session load rows)
                                                                            |> Option.map (fun (nextSession, result) -> nextSession, result, responseSeqId)
                                                            }

                                                    match dispatched with
                                                    | None -> return None
                                                    | Some(nextSession, result, resultSeqId) ->
                                                        activeSession <- Some nextSession
                                                        processEntry.Db <- nextSession.Database
                                                        let hasMore = not remaining.IsEmpty && (match result with Err _ -> false | _ -> true)
                                                        let! nextSeqId =
                                                            sendQueryResultAndNextSeq
                                                                stream
                                                                capabilities
                                                                resultSeqId
                                                                (if hasMore then statusFlagsForMore nextSession else statusFlagsFor nextSession)
                                                                (uint64 nextSession.LastInsertId)
                                                                (warningCountFor nextSession)
                                                                nextSession.LastResultColumnMetadata
                                                                result

                                                        match result with
                                                        | Err _ -> return Some nextSession
                                                        | _ -> return! sendBatch nextSession nextSeqId remaining
                                            }

                                        match! sendBatch session seqId statements with
                                        | None -> ()
                                        | Some(session) ->
                                            activeSession <- Some session
                                            processEntry.Db <- session.Database
                                            return! loop session
                            | Some Statistics ->
                                let uptime = max 0L (int64 (DateTime.Now - InformationSchema.serverStartedAt).TotalSeconds)
                                let questions = InformationSchema.questions ()
                                let rate = if uptime = 0L then 0.0 else float questions / float uptime

                                let statistics =
                                    String.Format(
                                        Globalization.CultureInfo.InvariantCulture,
                                        "Uptime: {0}  Threads: {1}  Questions: {2}  Slow queries: 0  Opens: 0  Flush tables: 0  Open tables: 0  Queries per second avg: {3:F3}",
                                        [| box uptime; box (InformationSchema.connectedThreads ()); box questions; box rate |]
                                    )

                                do!
                                    writePacketAsync
                                        stream
                                        { SeqId = seqId
                                          Payload = Encoding.UTF8.GetBytes statistics }
                                    |> Async.Ignore

                                return! loop session
                            | Some ProcessInfo ->
                                InformationSchema.recordQuestion ()

                                match runCancellable (fun () -> QueryHandler.handle session "SHOW PROCESSLIST") with
                                | None -> ()
                                | Some(session, result) ->
                                    activeSession <- Some session

                                    do!
                                        sendQueryResult
                                            stream
                                            capabilities
                                            seqId
                                            (statusFlagsFor session)
                                            (uint64 session.LastInsertId)
                                            (warningCountFor session)
                                            session.LastResultColumnMetadata
                                            result

                                    return! loop session
                            | Some(ProcessKill connectionId) ->
                                InformationSchema.recordQuestion ()

                                match runCancellable (fun () -> QueryHandler.handle session (sprintf "KILL CONNECTION %d" connectionId)) with
                                | None -> ()
                                | Some(session, result) ->
                                    activeSession <- Some session

                                    do!
                                        sendQueryResult
                                            stream
                                            capabilities
                                            seqId
                                            (statusFlagsFor session)
                                            (uint64 session.LastInsertId)
                                            (warningCountFor session)
                                            session.LastResultColumnMetadata
                                            result

                                    return! loop session
                            | Some Debug ->
                                Log.diagnostic
                                    "fsdb: COM_DEBUG: uptime=%d threads=%d questions=%d"
                                    (max 0L (int64 (DateTime.Now - InformationSchema.serverStartedAt).TotalSeconds))
                                    (InformationSchema.connectedThreads ())
                                    (InformationSchema.questions ())

                                do!
                                    writePacketAsync
                                        stream
                                        { SeqId = seqId
                                          Payload = eofPayload capabilities (statusFlagsFor session) }
                                    |> Async.Ignore

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
                                         |> List.map (fun c -> columnDefPayload { Name = c.Name; Metadata = ColumnWire.metadataOfColumn c }))
                                        @ [ eofPayload capabilities (statusFlagsFor session) ]

                                    do! sendPayloads stream seqId payloads |> Async.Ignore
                                    return! loop session
                            | Some(StmtPrepare _)
                                when session.Statements.Count + session.TextStatements.Count >= Limits.maxPreparedStmtCount ->
                                do!
                                    writePacketAsync
                                        stream
                                        { SeqId = seqId
                                          Payload =
                                            errPayload
                                                capabilities
                                                1461
                                                (sprintf
                                                    "Can't create more than max_prepared_stmt_count statements (current value: %d)"
                                                    Limits.maxPreparedStmtCount) }
                                    |> Async.Ignore

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
                                        :: List.replicate paramCount (columnDefPayload { Name = "?"; Metadata = Value.columnMetadata TypeVarString })
                                        @ paramDefEof

                                    do! sendPayloads stream seqId payloads |> Async.Ignore
                                    return! loop session
                            | Some(StmtExecute payload) when payload.Length < stmtExecuteHeaderLength ->
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
                                InformationSchema.recordQuestion ()
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
                                    let session = discardLongData stmtId session

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
                                                    | None -> Result.Error(1210, "COM_STMT_EXECUTE sent no parameter types to bind")

                                            match typesResult with
                                            | Result.Error error -> Result.Error error
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
                                                        | Some chunks ->
                                                            let bytes = chunks |> List.rev |> Array.concat
                                                            if typeId = TypeBlob then
                                                                VBytes bytes
                                                            elif typeId = TypeGeometry then
                                                                match tryGeometryFromMySqlBinary bytes with
                                                                | Some geometry -> VGeometry geometry
                                                                | None -> raise (GeometryError "Invalid GIS data provided to binary parameter")
                                                            else
                                                                VString(Encoding.UTF8.GetString bytes)
                                                        | None -> if isNull i then VNull else readBinaryValue r typeId unsigned)

                                                Result.Ok(types, values)
                                        with
                                        | GeometryError message -> Result.Error(3037, message)
                                        | ex -> Result.Error(1210, ex.Message)

                                    match decoded with
                                    | Result.Error(code, message) ->
                                        do!
                                            writePacketAsync
                                                stream
                                                { SeqId = seqId; Payload = errPayload capabilities code message }
                                            |> Async.Ignore

                                        return! loop session
                                    | Result.Ok(types, values) ->
                                        let session =
                                            { session with Statements = Map.add stmtId { stmt with LastParamTypes = Some types } session.Statements }
                                            |> discardLongData stmtId

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
                                                    (warningCountFor session)
                                                    session.LastResultColumnMetadata
                                                    result

                                            return! loop session
                            | Some(StmtSendLongData payload) when payload.Length < stmtLongDataHeaderLength ->
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
                                    // Cap accumulated long-data for the whole connection at
                                    // the same ceiling `readPacketAsync` enforces for a
                                    // reassembled packet.
                                    // COM_STMT_SEND_LONG_DATA never gets a reply, success or
                                    // failure, so a chunk that would blow the cap can't error
                                    // out here — it marks the param overflowed instead, and
                                    // the next COM_STMT_EXECUTE turns that into ER_NET_PACKET_
                                    // TOO_LARGE (1153) rather than silently truncating the
                                    // parameter's data and executing on short input.
                                    return! loop (accumulateLongData key chunk session)
                                else
                                    return! loop session
                            | Some(StmtClose stmtId) ->
                                // No reply, per protocol.
                                return! loop ({ session with Statements = Map.remove stmtId session.Statements } |> discardLongData stmtId)
                            | Some(StmtReset stmtId) ->
                                if session.Statements.ContainsKey stmtId then
                                    let session = discardLongData stmtId session

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
                            | Some(SetOption option) ->
                                match option with
                                | 0
                                | 1 ->
                                    let session = { session with MultiStatementsEnabled = option = 0 }

                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload = eofPayload capabilities (statusFlagsFor session) }
                                        |> Async.Ignore

                                    return! loop session
                                | _ ->
                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload = errPayload capabilities 1231 "Variable 'option' can't be set to the value supplied" }
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
                                //
                                // Roll the old session back before discarding
                                // its private snapshot.
                                QueryHandler.closeSession session

                                let session =
                                    { Session.create session.ConnectionId session.Store with
                                        User = session.User
                                        AccountHost = session.AccountHost
                                        LoginUser = session.LoginUser
                                        ClientHost = session.ClientHost
                                        Database = session.Database
                                        CustomFunctions = session.CustomFunctions
                                        Capabilities = session.Capabilities
                                        MultiStatementsEnabled = capabilities &&& ClientMultiStatements <> 0u
                                        TlsVersion = session.TlsVersion
                                        TlsCipher = session.TlsCipher }

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

                if authOkSeq.IsSome && databaseAccepted then
                    do! loop session
        with
        | :? PacketTooLargeException ->
            // Reassembling a multi-packet payload blew past
            // Limits.maxAllowedPacket. There's no way to resync mid-stream,
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
            closeTls ()
            InformationSchema.unregisterProcess (int64 connectionId)
            activeSession |> Option.iter QueryHandler.closeSession
            return raise error

        closeTls ()
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
/// accept starts ("Not listening"); some runtimes wrap socket cancellation
/// in `AggregateException`.
let private tryAccept (listener: TcpListener) : Async<TcpClient option> =
    async {
        try
            let! client = listener.AcceptTcpClientAsync() |> Async.AwaitTask
            return Some client
        with
        | :? ObjectDisposedException
        | :? InvalidOperationException
        | :? SocketException -> return None
        | :? AggregateException as aggregate
            when aggregate.Flatten().InnerExceptions
                 |> Seq.forall (fun error -> error :? ObjectDisposedException || error :? InvalidOperationException || error :? SocketException) ->
            return None
    }

/// Accepts connections until the listener is stopped, handling each on its
/// own async against the one shared `store` every session reads/writes
/// through, with `customFunctions` (an embedding `Db`'s registered scalars/
/// aggregates — `Functions.empty` if none) available to every statement any
/// connection runs. A failing connection is logged, never fatal to the
/// server.
let private validateTransportOptions (options: ServerOptions.Settings) =
    match options.Certificate with
    | Some certificate when not certificate.HasPrivateKey -> invalidArg "options" "TLS certificate needs a private key"
    | None when options.RequireSecureTransport -> invalidArg "options" "require_secure_transport needs a TLS certificate"
    | _ -> ()

/// Row-lock waits and statement execution are synchronous. Reserving only the
/// runtime default lets waiting sessions occupy every worker that could run
/// the lock holder, so worker capacity follows admitted connection pressure.
let private reserveConnectionWorkers activeConnections =
    let mutable workerThreads = 0
    let mutable completionThreads = 0
    ThreadPool.GetMinThreads(&workerThreads, &completionThreads) |> ignore
    let required = min Limits.maxConnections (activeConnections * 2)

    if required > workerThreads then
        ThreadPool.SetMinThreads(required, completionThreads) |> ignore

let serveWithOptions
    (options: ServerOptions.Settings)
    (listener: TcpListener)
    (store: Storage.Store)
    (customFunctions: Functions.Registry)
    : Async<unit> =
    validateTransportOptions options

    let rejectAtCapacity (client: TcpClient) =
        async {
            try
                use client = client
                use stream = client.GetStream()
                let payload = errPayload ClientProtocol41 1040 "Too many connections"
                do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
            // This is a detached best-effort rejection after the server has
            // already decided not to admit the peer. Any socket/stream fault
            // (including Async's AggregateException wrapper) is terminal only
            // for that disposable peer and must not escape onto the thread pool.
            with _ -> ()
        }

    let rec loop () : Async<unit> =
        async {
            match! tryAccept listener with
            | None -> ()
            | Some client ->
                let active = Interlocked.Increment activeConnectionCounter

                if active > Limits.maxConnections then
                    Interlocked.Decrement activeConnectionCounter |> ignore
                    Async.Start(rejectAtCapacity client)
                else
                    reserveConnectionWorkers active

                    // Process-wide, not per-listener: the process registry
                    // (`InformationSchema.registerProcess`) and `KILL <id>` key
                    // on this number, so two listeners in one process (the test
                    // suite, the embedding API) must never hand out the same id.
                    let connectionId = int (Interlocked.Increment connectionCounter)

                    Async.Start(
                        async {
                            try
                                try
                                    do! handleConnection connectionId store customFunctions options listener.Stop client
                                with ex ->
                                    Log.diagnostic "fsdb: connection %d: %s" connectionId ex.Message
                            finally
                                Interlocked.Decrement activeConnectionCounter |> ignore
                        }
                    )

                return! loop ()
        }

    loop ()

/// Serves with plaintext transport unless an embedding host supplies TLS settings.
let serve (listener: TcpListener) (store: Storage.Store) (customFunctions: Functions.Registry) : Async<unit> =
    serveWithOptions ServerOptions.defaults listener store customFunctions
