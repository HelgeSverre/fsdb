/// TCP transport, MySQL handshake, and per-connection command dispatch.
module Fsdb.Server

open System
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Net
open System.Net.Sockets
open System.Net.Security
open System.Security.Authentication
open System.Text
open System.Threading
open Fsdb.Binary
open Fsdb.Compression
open Fsdb.Functions
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Session
open Fsdb.Storage
open Fsdb.Value
open Fsdb.Executor

let private validateDatabaseSelection store account activeRoles database =
    if not (Storage.databaseExists store database) then
        let code, message = Storage.toMySqlError (Storage.NoSuchDatabase database)
        Error(code, message)
    elif Auth.canSeeDatabaseForAccountWithRoles store account activeRoles database then
        Ok()
    else
        Error(
            1044,
            sprintf "Access denied for user '%s'@'%s' to database '%s'" account.Name account.Host database
        )

let private isValidClientCertificate
    (certificateAuthorities: X509Certificate2 list)
    (certificate: X509Certificate)
    (presentedChain: X509Chain)
    =
    use remoteCertificate = X509CertificateLoader.LoadCertificate(certificate.Export X509ContentType.Cert)
    use chain = new X509Chain()
    chain.ChainPolicy.TrustMode <- X509ChainTrustMode.CustomRootTrust
    chain.ChainPolicy.RevocationMode <- X509RevocationMode.NoCheck
    chain.ChainPolicy.VerificationFlags <- X509VerificationFlags.NoFlag
    chain.ChainPolicy.CustomTrustStore.AddRange(certificateAuthorities |> List.toArray)
    chain.ChainPolicy.ApplicationPolicy.Add(Oid "1.3.6.1.5.5.7.3.2") |> ignore

    if not (isNull presentedChain) then
        presentedChain.ChainElements
        |> Seq.skip 1
        |> Seq.map _.Certificate
        |> Seq.toArray
        |> chain.ChainPolicy.ExtraStore.AddRange

    chain.Build remoteCertificate

/// Carries byte progress across raw, TLS, and compressed buffering boundaries.
type private ReadProgress() =
    let signal = new SemaphoreSlim(0, Int32.MaxValue)

    member _.Notify() = signal.Release() |> ignore

    member _.Drain() =
        while signal.Wait 0 do
            ()

    member _.WaitAsync(cancellationToken: CancellationToken) = signal.WaitAsync cancellationToken

    interface IDisposable with
        member _.Dispose() = signal.Dispose()

type private CountingStream(inner: IO.Stream, metrics: TransportMetrics, progress: ReadProgress) =
    inherit IO.Stream()

    override _.CanRead = inner.CanRead
    override _.CanSeek = inner.CanSeek
    override _.CanWrite = inner.CanWrite
    override _.Length = inner.Length

    override _.Position
        with get () = inner.Position
        and set value = inner.Position <- value

    override _.Flush() = inner.Flush()
    override _.FlushAsync cancellationToken = inner.FlushAsync cancellationToken

    override _.Read(buffer, offset, count) =
        let read = inner.Read(buffer, offset, count)
        metrics.BytesReceived <- metrics.BytesReceived + int64 read
        if read > 0 then progress.Notify()
        read

    override _.ReadAsync(buffer, offset, count, cancellationToken) =
        task {
            let! read = inner.ReadAsync(buffer, offset, count, cancellationToken)
            metrics.BytesReceived <- metrics.BytesReceived + int64 read
            if read > 0 then progress.Notify()
            return read
        }

    override _.ReadAsync(buffer: Memory<byte>, cancellationToken) =
        Threading.Tasks.ValueTask<int>(task {
            let! read = inner.ReadAsync(buffer, cancellationToken)
            metrics.BytesReceived <- metrics.BytesReceived + int64 read
            if read > 0 then progress.Notify()
            return read
        })

    override _.Write(buffer, offset, count) =
        inner.Write(buffer, offset, count)
        metrics.BytesSent <- metrics.BytesSent + int64 count

    override _.WriteAsync(buffer, offset, count, cancellationToken) =
        task {
            do! inner.WriteAsync(buffer, offset, count, cancellationToken)
            metrics.BytesSent <- metrics.BytesSent + int64 count
        }
        :> Threading.Tasks.Task

    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellationToken) =
        Threading.Tasks.ValueTask(task {
            do! inner.WriteAsync(buffer, cancellationToken)
            metrics.BytesSent <- metrics.BytesSent + int64 buffer.Length
        })

    override _.Seek(offset, origin) = inner.Seek(offset, origin)
    override _.SetLength value = inner.SetLength value

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
    | ChangeUser of ChangeUserRequest
    | FieldList of table: string
    | StmtPrepare of sql: string
    /// Payload with the COM_STMT_EXECUTE command byte already stripped —
    /// decoding needs a `Reader` positioned right after it, easier built at
    /// the call site than threaded through this DU field by field.
    | StmtExecute of payload: byte[]
    | StmtFetch of stmtId: int * rowCount: uint32
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

type private CursorRequest =
    | ImmediateResult
    | ReadOnlyCursor
    | UnsupportedCursor

let private cursorRequest = function
    | 0uy -> ImmediateResult
    | 1uy -> ReadOnlyCursor
    | _ -> UnsupportedCursor

let private stmtExecuteHeaderLength = 9
let private stmtLongDataHeaderLength = 6
/// None means a completely empty command packet — treat as disconnect (real
/// clients never send one). A non-empty payload always decodes to `Some`,
/// falling back to `Malformed` if the command byte's own payload is too
/// short to parse — see that case's doc.
let private parseCommand (capabilities: uint32) (payload: byte[]) : Command option =
    if payload.Length = 0 then
        None
    else
        let rest () = Encoding.UTF8.GetString(payload, 1, payload.Length - 1)
        let sql () = payload.[1..] |> decodeSqlBytes
        let restBytes () = payload.[1..]

        try
            Some(
                match payload.[0] with
                | 0x01uy -> Quit
                | 0x02uy -> InitDb(rest ())
                | 0x03uy -> Query(sql ())
                | 0x04uy -> FieldList(Reader(restBytes ()).ReadNullTerminatedString())
                | 0x09uy -> Statistics
                | 0x0auy -> ProcessInfo
                | 0x0cuy -> ProcessKill(int64 (Reader(restBytes ()).ReadInt32LE()))
                | 0x0duy -> Debug
                | 0x0euy -> Ping
                | 0x11uy -> ChangeUser(parseChangeUserRequest capabilities (restBytes ()))
                | 0x16uy -> StmtPrepare(sql ())
                | 0x17uy -> StmtExecute(restBytes ())
                | 0x18uy -> StmtSendLongData(restBytes ())
                | 0x19uy -> StmtClose(Reader(restBytes ()).ReadInt32LE())
                | 0x1auy -> StmtReset(Reader(restBytes ()).ReadInt32LE())
                | 0x1buy -> SetOption(Reader(restBytes ()).ReadInt16LE())
                | 0x1cuy ->
                    let reader = Reader(restBytes ())
                    StmtFetch(reader.ReadInt32LE(), reader.ReadUInt32LE())
                | 0x1fuy -> ResetConnection
                | b -> Unsupported b
            )
        with _ ->
            Some(Malformed payload.[0])

let private commandStatus = function
    | InitDb _ -> Some InformationSchema.StatusCommand.changeDatabase
    | FieldList _ -> Some InformationSchema.StatusCommand.showFields
    | Statistics
    | Debug
    | Ping
    | ChangeUser _
    | ResetConnection -> Some InformationSchema.StatusCommand.adminCommands
    | StmtPrepare _ -> Some InformationSchema.StatusCommand.statementPrepare
    | StmtExecute _ -> Some InformationSchema.StatusCommand.statementExecute
    | StmtFetch _ -> Some InformationSchema.StatusCommand.statementFetch
    | StmtSendLongData _ -> Some InformationSchema.StatusCommand.statementSendLongData
    | StmtClose _ -> Some InformationSchema.StatusCommand.statementClose
    | StmtReset _ -> Some InformationSchema.StatusCommand.statementReset
    | SetOption _ -> Some InformationSchema.StatusCommand.setOption
    | Quit
    | Query _
    | ProcessInfo
    | ProcessKill _
    | Unsupported _
    | Malformed _ -> None

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

/// Produces one descriptor list shared by column definitions and row encoding.
/// Untyped probe results fall back only when the supplied arity is unusable.
let private resultMetadata columns rows metadata =
    let metadata =
        if List.length metadata = List.length columns then
            metadata
        else
            List.replicate (List.length columns) (Value.columnMetadata TypeVarString)

    let withValuePrecision colIndex metadata =
        if metadata.Decimals = 0uy && (metadata.TypeId = TypeDateTime || metadata.TypeId = TypeTime) then
            let decimals =
                rows
                |> List.map (List.tryItem colIndex >> Option.flatten)
                |> Protocol.fractionalDigitsOf

            { metadata with Decimals = decimals }
        else
            metadata

    List.mapi withValuePrecision metadata

let private sendRows
    (stream: IO.Stream)
    (startSeq: byte)
    (metadata: ColumnMetadata list)
    (rowEncoder: ColumnMetadata list -> string option list -> byte[])
    (rows: string option list seq)
    : Async<byte> =
    async {
        let mutable seqId = startSeq
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
                buf.Add(byte (payload.Length &&& 0xff))
                buf.Add(byte ((payload.Length >>> 8) &&& 0xff))
                buf.Add(byte ((payload.Length >>> 16) &&& 0xff))
                buf.Add seqId
                buf.AddRange payload
                seqId <- seqId + 1uy
            else
                do! flush ()
                let! nextSeqId = writePacketAsync stream { SeqId = seqId; Payload = payload }
                seqId <- nextSeqId

            if buf.Count >= (1 <<< 16) then
                do! flush ()

        do! flush ()
        return seqId
    }

/// Builds the column definitions and pre-row terminator without row packets.
let private resultHeadPayloads
    (capabilities: uint32)
    (statusFlags: int)
    (lastInsertId: uint64)
    (warningCount: int)
    (sessionStateChanges: SessionStateChange list)
    (columnMetadata: ColumnMetadata list)
    (result: Executor.QueryResult)
    : byte[] list =
    match result with
    | Affected affectedRows ->
        [ okPayloadWithWarningsAndSessionState
              capabilities
              statusFlags
              affectedRows
              lastInsertId
              warningCount
              sessionStateChanges ]
    | Err(code, message) ->
        let state = result |> Executor.errorInfo |> Option.map _.State |> Option.defaultValue (sqlStateForCode code)
        [ errPayloadWithState capabilities code state message ]
    | MultipleResults _ -> invalidArg (nameof result) "Nested result collections must be sent through sendResult"
    | ResultSet(columns, _) ->
        let deprecateEof = capabilities &&& ClientDeprecateEof <> 0u

        let columnCountPayload =
            let w = Writer()
            w.WriteLenEncInt(uint64 columns.Length)
            w.ToArray()

        [ columnCountPayload ]
        @ (List.map2 (fun name metadata -> columnDefPayload { Name = name; Metadata = metadata }) columns columnMetadata)
        @ (if deprecateEof then [] else [ eofPayloadWithWarnings capabilities statusFlags warningCount ])

/// Writes an OK/ERR/resultset reply. Rows are framed and written in batches —
/// one `WriteAsync` per ~64 KiB instead of one per row packet — so a large
/// result set neither pays a syscall per row nor holds every row's bytes at
/// once. Column count/defs/EOF stay ordinary packets.
let rec private sendResult
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (lastInsertId: uint64)
    (warningCount: int)
    (sessionStateChanges: SessionStateChange list)
    (columnMetadata: ColumnMetadata list)
    (rowEncoder: ColumnMetadata list -> string option list -> byte[])
    (result: Executor.QueryResult)
    : Async<byte> =
    async {
        match result with
        | MultipleResults [] ->
            return!
                sendResult
                    stream
                    capabilities
                    startSeq
                    statusFlags
                    lastInsertId
                    warningCount
                    sessionStateChanges
                    []
                    rowEncoder
                    (Affected 0UL)
        | MultipleResults results ->
            let rec sendParts seqId =
                function
                | [] -> async { return seqId }
                | (part, metadata) :: remaining ->
                    async {
                        let partStatus =
                            if remaining.IsEmpty then statusFlags else statusFlags ||| StatusMoreResultsExists

                        let partSessionState = if remaining.IsEmpty then sessionStateChanges else []

                        let! nextSeqId =
                            sendResult
                                stream
                                capabilities
                                seqId
                                partStatus
                                lastInsertId
                                warningCount
                                partSessionState
                                metadata
                                rowEncoder
                                part

                        return! sendParts nextSeqId remaining
                    }

            return! sendParts startSeq results
        | ResultSet(columns, rows) ->
            let metadata = resultMetadata columns rows columnMetadata

            let! seqId =
                sendPayloads
                    stream
                    startSeq
                    (resultHeadPayloads capabilities statusFlags lastInsertId warningCount sessionStateChanges metadata result)

            let! seqId = sendRows stream seqId metadata rowEncoder rows

            let deprecateEof = capabilities &&& ClientDeprecateEof <> 0u

            let! nextSeqId =
                sendPayloads
                    stream
                    seqId
                    [ (if deprecateEof then
                           okEndOfResultSetPayloadWithWarningsAndSessionState
                               capabilities
                               statusFlags
                               warningCount
                               sessionStateChanges
                       else
                           eofPayloadWithWarnings capabilities statusFlags warningCount) ]
            return nextSeqId
        | _ ->
            return!
                sendPayloads
                    stream
                    startSeq
                    (resultHeadPayloads capabilities statusFlags lastInsertId warningCount sessionStateChanges [] result)
    }

let private sendCursorHead
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (warningCount: int)
    (columns: string list)
    (metadata: ColumnMetadata list)
    : Async<unit> =
    let columnCount = Writer()
    columnCount.WriteLenEncInt(uint64 columns.Length)
    let terminator =
        if capabilities &&& ClientDeprecateEof <> 0u then
            okEndOfResultSetPayloadWithWarnings capabilities statusFlags warningCount
        else
            eofPayloadWithWarnings capabilities statusFlags warningCount

    [ columnCount.ToArray() ]
    @ List.map2 (fun name descriptor -> columnDefPayload { Name = name; Metadata = descriptor }) columns metadata
    @ [ terminator ]
    |> sendPayloads stream startSeq
    |> Async.Ignore

let private sendCursorRows
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (warningCount: int)
    (metadata: ColumnMetadata list)
    (rows: string option list seq)
    : Async<unit> =
    async {
        let! seqId = sendRows stream startSeq metadata binaryRowPayload rows
        let terminator =
            if capabilities &&& ClientDeprecateEof <> 0u then
                okEndOfResultSetPayloadWithWarnings capabilities statusFlags warningCount
            else
                eofPayloadWithWarnings capabilities statusFlags warningCount
        do! sendPayloads stream seqId [ terminator ] |> Async.Ignore
    }

let private sendTextResult
    (sessionStateChanges: SessionStateChange list)
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (lastInsertId: uint64)
    (warningCount: int)
    (columnMetadata: ColumnMetadata list)
    (result: Executor.QueryResult)
    : Async<byte> =
    sendResult
        stream
        capabilities
        startSeq
        statusFlags
        lastInsertId
        warningCount
        sessionStateChanges
        columnMetadata
        textRowPayloadTyped
        result

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
    sendTextResult [] stream capabilities startSeq statusFlags lastInsertId warningCount columnMetadata result |> Async.Ignore

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
    sendTextResult [] stream capabilities startSeq statusFlags lastInsertId warningCount columnMetadata result

let private sendQueryResultWithSessionStateAndNextSeq
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (lastInsertId: uint64)
    (warningCount: int)
    (sessionStateChanges: SessionStateChange list)
    (columnMetadata: ColumnMetadata list)
    (result: Executor.QueryResult)
    : Async<byte> =
    sendTextResult sessionStateChanges stream capabilities startSeq statusFlags lastInsertId warningCount columnMetadata result

let private sendBinaryResult
    (sessionStateChanges: SessionStateChange list)
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (lastInsertId: uint64)
    (warningCount: int)
    (columnMetadata: ColumnMetadata list)
    (result: Executor.QueryResult)
    : Async<unit> =
    sendResult
        stream
        capabilities
        startSeq
        statusFlags
        lastInsertId
        warningCount
        sessionStateChanges
        columnMetadata
        binaryRowPayload
        result
    |> Async.Ignore

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
    sendBinaryResult [] stream capabilities startSeq statusFlags lastInsertId warningCount columnMetadata result

let private sendBinaryQueryResultWithSessionState
    (stream: IO.Stream)
    (capabilities: uint32)
    (startSeq: byte)
    (statusFlags: int)
    (lastInsertId: uint64)
    (warningCount: int)
    (sessionStateChanges: SessionStateChange list)
    (columnMetadata: ColumnMetadata list)
    (result: Executor.QueryResult)
    : Async<unit> =
    sendBinaryResult sessionStateChanges stream capabilities startSeq statusFlags lastInsertId warningCount columnMetadata result

/// `SERVER_STATUS_AUTOCOMMIT` always, plus `SERVER_STATUS_IN_TRANS` while
/// `session.Tx` is open — every OK/EOF packet reports this so PDO's
/// `inTransaction()`/`beginTransaction()`/`commit()` (which read the status
/// bit off the wire, not just whatever `COMMIT`/`ROLLBACK` themselves reply)
/// see the real transaction state.
let private statusFlagsFor (session: Session) : int =
    let autocommit =
        match Map.tryFind "autocommit" session.Variables with
        | Some(Some "0") -> 0
        | _ -> StatusAutocommit

    autocommit ||| (if session.Tx.IsSome then StatusInTrans else 0)

let private statusFlagsForMore (session: Session) = statusFlagsFor session ||| StatusMoreResultsExists

let private warningCountFor (session: Session) =
    min (int UInt16.MaxValue) session.Diagnostics.Length

let private localInfileRequestPayload (fileName: string) =
    Array.append [| 0xfbuy |] (Encoding.UTF8.GetBytes fileName)

let private singleCharacter (value: string) =
    if value.Length = 1 then Some value.[0] else None

let private decodeLocalLoad (load: Parser.LocalLoad) (bytes: byte[]) : Result<Value list list, int * string> =
    try
        let text = UTF8Encoding(false, true).GetString bytes
        let enclosedBy = load.EnclosedBy |> Option.bind singleCharacter
        let escape = load.Escape |> Option.bind singleCharacter
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

/// ReadAsync ignores Socket.ReceiveTimeout and cooperative cancellation.
/// Closing the client forces a timed-out read to unblock.
let private readWithTimeoutMs
    (read: IO.Stream -> Async<'T option>)
    (timeoutMs: int)
    (client: TcpClient)
    (stream: IO.Stream)
    : Async<'T option> =
    async {
        let readTask = Async.StartAsTask(read stream)
        // Cancel the losing timer so active connections do not accumulate delays.
        let timerCts = new CancellationTokenSource()

        try
            let! winner =
                Threading.Tasks.Task.WhenAny(
                    readTask :> Threading.Tasks.Task,
                    Threading.Tasks.Task.Delay(timeoutMs, timerCts.Token)
                )
                |> Async.AwaitTask

            if obj.ReferenceEquals(winner, readTask) then
                // Synchronous task faults arrive wrapped; command dispatch
                // matches the original protocol exception type.
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

let private readWithProgressTimeoutMs
    (read: Async<'T option>)
    (idleTimeoutMs: int)
    (transferTimeoutMs: int)
    (client: TcpClient)
    (progress: ReadProgress)
    : Async<'T option> =
    async {
        progress.Drain()
        let readTask = Async.StartAsTask read

        let rec wait (timeoutMs: int) =
            async {
                use roundCts = new CancellationTokenSource()
                let progressTask = progress.WaitAsync roundCts.Token
                let timeoutTask = Threading.Tasks.Task.Delay(timeoutMs, roundCts.Token)

                let! winner =
                    Threading.Tasks.Task.WhenAny(readTask :> Threading.Tasks.Task, progressTask, timeoutTask)
                    |> Async.AwaitTask

                roundCts.Cancel()

                if obj.ReferenceEquals(winner, readTask) then
                    try
                        return! Async.AwaitTask readTask
                    with :? AggregateException as agg when agg.InnerExceptions.Count = 1 ->
                        return raise (agg.InnerExceptions.[0])
                elif obj.ReferenceEquals(winner, progressTask) then
                    return! wait transferTimeoutMs
                else
                    client.Close()

                    try
                        let! _ = Async.AwaitTask readTask
                        ()
                    with _ ->
                        ()

                    return None
            }

        return! wait idleTimeoutMs
    }

let private readSomeWithProgress (stream: IO.Stream) (progress: ReadProgress) buffer offset count =
    async {
        let! count = stream.ReadAsync(buffer, offset, count) |> Async.AwaitTask
        if count > 0 then progress.Notify()
        return count
    }

let private readPacketWithTimeoutsMs
    (idleTimeoutMs: int)
    (readTimeoutMs: int)
    (client: TcpClient)
    (stream: IO.Stream)
    (progress: ReadProgress)
    : Async<Packet option> =
    readWithProgressTimeoutMs
        (readPacketWithReaderAsync (readSomeWithProgress stream progress))
        idleTimeoutMs
        readTimeoutMs
        client
        progress

/// Converts the MySQL seconds-valued timeout without wrapping the `int`
/// milliseconds accepted by `Task.Delay`. Values beyond that API's range
/// use its longest finite delay instead of breaking every connection before
/// authentication.
let timeoutMilliseconds (timeoutSeconds: int) : int =
    int (min (int64 Int32.MaxValue) (max 0L (int64 timeoutSeconds * 1000L)))

let private readPacketWithTimeoutSeconds (timeoutSeconds: int) (client: TcpClient) (stream: IO.Stream) : Async<Packet option> =
    readPacketWithTimeoutMs (timeoutMilliseconds timeoutSeconds) client stream

let private readPacketWithTimeoutsSeconds
    (idleTimeoutSeconds: int)
    (readTimeoutSeconds: int)
    (client: TcpClient)
    (stream: IO.Stream)
    (progress: ReadProgress)
    : Async<Packet option> =
    readPacketWithTimeoutsMs
        (timeoutMilliseconds idleTimeoutSeconds)
        (timeoutMilliseconds readTimeoutSeconds)
        client
        stream
        progress

let private readPhysicalPacketWithTimeoutSeconds
    (timeoutSeconds: int)
    (client: TcpClient)
    (stream: IO.Stream)
    (progress: ReadProgress)
    : Async<Packet option> =
    let timeoutMs = timeoutMilliseconds timeoutSeconds
    readWithProgressTimeoutMs
        (readPhysicalPacketWithReaderAsync (readSomeWithProgress stream progress))
        timeoutMs
        timeoutMs
        client
        progress

let private receiveLocalData
    (client: TcpClient)
    (stream: IO.Stream)
    (progress: ReadProgress)
    (timeoutSeconds: int)
    (startSeqId: byte)
    : Async<Result<byte[] * byte, (int * string) * byte>> =
    async {
        use bytes = new IO.MemoryStream()
        let mutable expectedSeqId = startSeqId
        let mutable finished = false
        let mutable error: (int * string) option = None

        while not finished do
            match! readPhysicalPacketWithTimeoutSeconds timeoutSeconds client stream progress with
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

let private sessionTimeout (name: string) (fallback: int) (session: Session) =
    match session.Variables |> Map.tryFind name |> Option.flatten with
    | Some value ->
        match Int32.TryParse value with
        | true, seconds -> seconds
        | _ -> fallback
    | None -> fallback

let private sessionWaitTimeout (session: Session) =
    if session.Capabilities &&& ClientInteractive <> 0u then
        sessionTimeout "interactive_timeout" Limits.interactiveTimeoutSeconds session
    else
        sessionTimeout "wait_timeout" Limits.waitTimeoutSeconds session

let private sessionNetReadTimeout = sessionTimeout "net_read_timeout" Limits.netReadTimeoutSeconds

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

/// Authenticates a wire response against `mysql.user`. `forceAuthSwitch`
/// starts a fresh challenge before account lookup so COM_CHANGE_USER does
/// not reveal account existence or reuse the connection's first scramble.
let private authenticateAccount
    (client: TcpClient)
    (stream: IO.Stream)
    (capabilities: uint32)
    (store: Storage.Store)
    (authData: byte[])
    (resp: HandshakeResponse)
    (clientHost: string option)
    (encryptedTransport: bool)
    (clientCertificate: bool)
    (forceAuthSwitch: bool)
    (firstSeq: byte)
    : Async<(byte * Auth.Account * bool) option> =
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

        let accept seqId selected cols row =
            async {
                let expired =
                    Auth.isPasswordExpiredAtWithDefault Limits.defaultPasswordLifetimeDays DateTime.Now cols row

                if expired && capabilities &&& ClientCanHandleExpiredPasswords = 0u then
                    let message =
                        "Your password has expired. To log in you must change it using a client that supports expired passwords."

                    do! writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities 1862 message } |> Async.Ignore
                    return None
                else
                    return Some(seqId, selected, expired)
            }

        let! offered =
            if forceAuthSwitch then
                async {
                    do! writePacketAsync stream { SeqId = firstSeq; Payload = authSwitchPayload authData } |> Async.Ignore
                    let! response = readPacketWithTimeoutSeconds Limits.waitTimeoutSeconds client stream
                    return response |> Option.map (fun response -> response.SeqId + 1uy, response.Payload)
                }
            else
                async { return Some(firstSeq, resp.AuthResponse) }

        match offered with
        | None -> return None
        | Some(authSeq, authResponse) ->
            match clientHost |> Option.bind (Auth.resolveAccount store resp.Username) with
            | None -> return! deny authSeq (authResponse.Length > 0)
            | Some(_, cols, row) when Auth.isAccountLocked cols row ->
                let message =
                    sprintf
                        "Access denied for user '%s'@'%s'. Account is locked."
                        resp.Username
                        (clientHost |> Option.defaultValue "unknown")

                do! writePacketAsync stream { SeqId = authSeq; Payload = errPayload capabilities 3118 message } |> Async.Ignore
                return None
            | Some(_, cols, row) when
                not (
                    Auth.transportSatisfiesAccount
                        { Encrypted = encryptedTransport
                          ClientCertificateValidated = clientCertificate }
                        cols
                        row
                ) ->
                return! deny authSeq (authResponse.Length > 0)
            | Some(selected, cols, row) ->
                let stored = Auth.storedPasswordHash cols row

                if stored = "" then
                    if authResponse.Length = 0 then
                        return! accept authSeq selected cols row
                    else
                        return! deny authSeq true
                elif Auth.verifyNative stored authData authResponse then
                    return! accept authSeq selected cols row
                elif forceAuthSwitch || resp.ClientPlugin = Some "mysql_native_password" then
                    return! deny authSeq (authResponse.Length > 0)
                else
                    do! writePacketAsync stream { SeqId = authSeq; Payload = authSwitchPayload authData } |> Async.Ignore

                    match! readPacketWithTimeoutSeconds Limits.waitTimeoutSeconds client stream with
                    | None -> return None
                    | Some switchResp when Auth.verifyNative stored authData switchResp.Payload ->
                        return! accept (switchResp.SeqId + 1uy) selected cols row
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

let private resolveChangeUserCharacterSet (characterSet: int option) =
    characterSet
    |> Option.bind (fun id ->
        Collation.tryFindById id
        |> Option.orElseWith (fun () -> Collation.tryFind "utf8mb4_0900_ai_ci"))

let private applyChangeUserCharacterSet (collation: Collation.Collation option) (session: Session) =
    match collation with
    | None -> session
    | Some collation ->
        let charset = Collation.charsetOfCollation collation.Name
        Storage.setConnectionCharset session.Store charset
        Storage.setConnectionCollation session.Store collation

        { session with
            Variables =
                session.Variables
                |> Map.add "character_set_client" (Some charset)
                |> Map.add "character_set_connection" (Some charset)
                |> Map.add "character_set_results" (Some charset)
                |> Map.add "collation_connection" (Some collation.Name) }

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
        let metrics =
            { BytesReceived = 0L
              BytesSent = 0L }

        use readProgress = new ReadProgress()
        let countedStream = new CountingStream(networkStream, metrics, readProgress)
        let mutable stream: IO.Stream = countedStream
        let mutable tlsStream: SslStream option = None
        let mutable compressedStream: CompressedStream option = None
        let mutable tlsVersion: string option = None
        let mutable tlsCipher: string option = None
        let mutable clientCertificateValidated = false
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
                let secured = new SslStream(countedStream, true)
                let authentication = SslServerAuthenticationOptions()
                authentication.ServerCertificate <- certificate
                authentication.EnabledSslProtocols <- SslProtocols.Tls12 ||| SslProtocols.Tls13
                authentication.ClientCertificateRequired <- not (List.isEmpty options.ClientCertificateAuthorities)
                authentication.AllowRenegotiation <- false

                authentication.RemoteCertificateValidationCallback <-
                    fun _ remoteCertificate presentedChain _ ->
                        match remoteCertificate with
                        | null -> true
                        | presented ->
                            let valid =
                                isValidClientCertificate options.ClientCertificateAuthorities presented presentedChain

                            clientCertificateValidated <- valid
                            valid

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
        let mutable accountLease: IDisposable option = None

        let releaseAccountLease () =
            accountLease |> Option.iter _.Dispose()
            accountLease <- None

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
                let! authenticated =
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
                        authenticateAccount
                            client
                            stream
                            capabilities
                            store
                            authData
                            resp
                            clientHost
                            tlsVersion.IsSome
                            clientCertificateValidated
                            false
                            (handshakeResp.SeqId + 1uy)
                let! authOkSeq =
                    async {
                        match authenticated with
                        | None -> return None
                        | Some(seqId, selected, expired) ->
                            match Auth.tryAcquireAccountConnection store selected with
                            | Ok lease ->
                                accountLease <- Some lease
                                return Some(seqId, selected, expired)
                            | Error(code, message) ->
                                do! writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities code message } |> Async.Ignore
                                return None
                    }
                let mutable databaseAccepted = false
                let selectedAccount =
                    authOkSeq
                    |> Option.map (fun (_, account, _) -> account)
                    |> Option.defaultValue (Auth.account resp.Username "%")

                let passwordExpired = authOkSeq |> Option.exists (fun (_, _, expired) -> expired)

                let createSessionFor (account: Auth.Account) loginUser passwordExpired database =
                    { Session.create connectionId store with
                        User = account.Name
                        AccountHost = account.Host
                        ActiveRoles = Session.initialRoles store account
                        PasswordExpired = passwordExpired
                        LoginUser = loginUser
                        ClientHost = displayHost
                        Database = database
                        CustomFunctions = customFunctions
                        Capabilities = capabilities
                        MultiStatementsEnabled = capabilities &&& ClientMultiStatements <> 0u
                        TlsVersion = tlsVersion
                        TlsCipher = tlsCipher
                        TransportMetrics = metrics }

                let session = createSessionFor selectedAccount resp.Username passwordExpired resp.Database

                activeSession <- Some session

                let remoteHost =
                    try string client.Client.RemoteEndPoint with _ -> ""

                // Registered even when auth is about to deny — the command
                // loop below never runs then, and the connection teardown's
                // `unregisterProcess` removes the short-lived entry.
                let mutable processEntry =
                    InformationSchema.registerProcessAs (int64 connectionId) selectedAccount resp.Username remoteHost
                processEntry.Db <- resp.Database
                // `KILL CONNECTION <id>`: closing the socket makes this
                // connection's next read fail, which ends its command loop.
                processEntry.CloseConnection <- Some(fun () -> try client.Close() with _ -> ())

                match authOkSeq with
                | None -> () // denied: the 1045 is already written, no OK
                | Some(okSeq, _, _) ->
                    let databaseAllowed =
                        match resp.Database with
                        | None -> Ok()
                        | Some db -> validateDatabaseSelection store selectedAccount session.ActiveRoles db

                    match databaseAllowed with
                    | Error(code, message) ->
                        do! writePacketAsync stream { SeqId = okSeq; Payload = errPayload capabilities code message } |> Async.Ignore
                    | Ok() ->
                        databaseAccepted <- true

                        do!
                            writePacketAsync
                                stream
                                { SeqId = okSeq
                                  Payload = okPayload capabilities (statusFlagsFor session) 0UL 0UL }
                            |> Async.Ignore

                        if capabilities &&& ClientCompress <> 0u then
                            let compressed = new CompressedStream(stream, true)
                            compressedStream <- Some compressed
                            stream <- compressed

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
                let runCancellable sql (dispatch: unit -> Session * Executor.QueryResult) : (Session * Executor.QueryResult) option =
                    InformationSchema.beginProcessQuery processEntry (Log.redactSql sql)

                    try
                        try
                            Some(withCancellationWatch client (Some processEntry) dispatch)
                        with :? OperationCanceledException ->
                            Log.diagnostic "fsdb: connection %d: query cancelled (client disconnected)" connectionId
                            None
                    finally
                        InformationSchema.finishProcessQuery processEntry

                let rec loop (session: Session) : Async<unit> =
                    async {
                        activeSession <- Some session
                        compressedStream |> Option.iter (fun compressed -> compressed.BeginCommand())

                        match!
                            readPacketWithTimeoutsSeconds
                                (sessionWaitTimeout session)
                                (sessionNetReadTimeout session)
                                client
                                stream
                                readProgress
                        with
                        | None -> ()
                        | Some cmdPacket ->
                            let seqId = cmdPacket.SeqId + 1uy
                            let command = parseCommand capabilities cmdPacket.Payload

                            command
                            |> Option.bind commandStatus
                            |> Option.iter (InformationSchema.recordCommand session.StatusCounters)

                            match command with
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
                            | Some(ChangeUser request) ->
                                let supportsPluginAuth = capabilities &&& ClientPluginAuth <> 0u
                                let changeAuthData = if supportsPluginAuth then randomAuthPluginData () else authData

                                let response =
                                    { Capabilities = capabilities
                                      Username = request.Username
                                      AuthResponse = request.AuthResponse
                                      ClientPlugin = request.ClientPlugin
                                      Database = request.Database }

                                let! authenticated =
                                    authenticateAccount
                                        client
                                        stream
                                        capabilities
                                        store
                                        changeAuthData
                                        response
                                        clientHost
                                        tlsVersion.IsSome
                                        false
                                        supportsPluginAuth
                                        seqId

                                match authenticated with
                                | None -> ()
                                | Some(okSeq, selected, expired) ->
                                    let validated =
                                        match request.Database with
                                        | Some db ->
                                            validateDatabaseSelection
                                                store
                                                selected
                                                (Session.initialRoles store selected)
                                                db
                                        | _ -> Ok()

                                    match validated with
                                    | Error(code, message) ->
                                        do!
                                            writePacketAsync stream { SeqId = okSeq; Payload = errPayload capabilities code message }
                                            |> Async.Ignore
                                    | Ok() ->
                                        let currentAccount = Auth.account session.User session.AccountHost

                                        let acquired =
                                            if Auth.sameAccount currentAccount selected then
                                                Ok None
                                            else
                                                Auth.tryAcquireAccountConnection store selected |> Result.map Some

                                        match acquired with
                                        | Error(code, message) ->
                                            do!
                                                writePacketAsync stream { SeqId = okSeq; Payload = errPayload capabilities code message }
                                                |> Async.Ignore
                                        | Ok newLease ->
                                            QueryHandler.closeSession session

                                            let session =
                                                createSessionFor selected request.Username expired request.Database
                                                |> applyChangeUserCharacterSet (resolveChangeUserCharacterSet request.CharacterSet)

                                            match newLease with
                                            | None -> ()
                                            | Some lease ->
                                                releaseAccountLease ()
                                                accountLease <- Some lease

                                            let replacement =
                                                InformationSchema.registerProcessAs
                                                    (int64 connectionId)
                                                    selected
                                                    request.Username
                                                    remoteHost

                                            replacement.Db <- request.Database
                                            replacement.CloseConnection <- processEntry.CloseConnection
                                            processEntry <- replacement
                                            activeSession <- Some session

                                            do!
                                                writePacketAsync
                                                    stream
                                                    { SeqId = okSeq
                                                      Payload = okPayload capabilities (statusFlagsFor session) 0UL 0UL }
                                                |> Async.Ignore

                                            return! loop session
                            | Some(InitDb db) ->
                                let currentStore = Session.currentStore session
                                let account = Auth.account session.User session.AccountHost

                                match validateDatabaseSelection currentStore account session.ActiveRoles db with
                                | Ok() ->
                                    let session =
                                        Session.clearSessionStateChanges session
                                        |> fun session -> Session.trackSchemaAssignment db { session with Database = Some db }

                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload =
                                                okPayloadWithWarningsAndSessionState
                                                    capabilities
                                                    (statusFlagsFor session)
                                                    0UL
                                                    0UL
                                                    0
                                                    session.SessionStateChanges }
                                        |> Async.Ignore

                                    return! loop session
                                | Error(code, message) ->
                                    do!
                                        writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities code message }
                                        |> Async.Ignore

                                    return! loop session
                            | Some(Query sql) ->
                                InformationSchema.recordQuestion session.StatusCounters
                                if isShutdownStatement sql then
                                    InformationSchema.recordCommand session.StatusCounters InformationSchema.StatusCommand.shutdown
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
                                    let statements =
                                        match Parser.splitStatements sql with
                                        | Result.Ok statements -> Result.Ok statements
                                        | Result.Error _ -> Result.Error(1064, "You have an error in your SQL syntax")

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
                                        let dispatched = runCancellable sql (fun () -> QueryHandler.handle session sql)

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
                                                                    runCancellable statement (fun () -> QueryHandler.handle session statement)
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

                                                                match! receiveLocalData client stream readProgress (sessionNetReadTimeout session) uploadSeqId with
                                                                | Result.Error((code, _), _) when code = 2013 || code = 1156 ->
                                                                    client.Close()
                                                                    return None
                                                                | Result.Error((code, message), responseSeqId) -> return Some(session, Err(code, message), responseSeqId)
                                                                | Result.Ok(bytes, responseSeqId) ->
                                                                    match decodeLocalLoad load bytes with
                                                                    | Result.Error(code, message) -> return Some(session, Err(code, message), responseSeqId)
                                                                    | Result.Ok rows ->
                                                                        return
                                                                            runCancellable statement (fun () ->
                                                                                QueryHandler.executeLocalLoad session load rows)
                                                                            |> Option.map (fun (nextSession, result) ->
                                                                                nextSession, result, responseSeqId)
                                                            }

                                                    match dispatched with
                                                    | None -> return None
                                                    | Some(nextSession, result, resultSeqId) ->
                                                        activeSession <- Some nextSession
                                                        processEntry.Db <- nextSession.Database
                                                        let hasMore = not remaining.IsEmpty && (match result with Err _ -> false | _ -> true)
                                                        let! nextSeqId =
                                                            sendQueryResultWithSessionStateAndNextSeq
                                                                stream
                                                                capabilities
                                                                resultSeqId
                                                                (if hasMore then statusFlagsForMore nextSession else statusFlagsFor nextSession)
                                                                (uint64 nextSession.LastInsertId)
                                                                (warningCountFor nextSession)
                                                                nextSession.SessionStateChanges
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
                                InformationSchema.recordQuestion session.StatusCounters

                                match runCancellable "SHOW PROCESSLIST" (fun () -> QueryHandler.handle session "SHOW PROCESSLIST") with
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
                                InformationSchema.recordQuestion session.StatusCounters

                                let sql = sprintf "KILL CONNECTION %d" connectionId

                                match runCancellable sql (fun () -> QueryHandler.handle session sql) with
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

                                match Storage.tableSnapshot (Session.currentStore session) dbName table with
                                | Result.Error e ->
                                    let code, message = Storage.toMySqlError e

                                    do!
                                        writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities code message }
                                        |> Async.Ignore

                                    return! loop session
                                | Result.Ok storedTable ->
                                    let payloads =
                                        (storedTable.Columns
                                         |> List.map (fun column ->
                                             let metadata =
                                                 { ColumnWire.metadataOfTableColumn storedTable.Indexes column with
                                                     Origin =
                                                         Some
                                                             { Schema = dbName
                                                               Table = table
                                                               OriginalTable = table
                                                               OriginalName = column.Name } }

                                             columnDefPayload { Name = column.Name; Metadata = metadata }))
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
                                match QueryHandler.prepareStatementForSession session sql with
                                | Result.Error(code, message) ->
                                    do!
                                        writePacketAsync stream { SeqId = seqId; Payload = errPayload capabilities code message }
                                        |> Async.Ignore

                                    return! loop session
                                | Result.Ok(ast, paramCount) ->
                                    let stmtId = session.NextStmtId
                                    let parameterMetadata, resultColumns = QueryHandler.preparedMetadata session ast paramCount

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

                                    let resultDefEof =
                                        if not resultColumns.IsEmpty && not deprecateEof then
                                            [ eofPayload capabilities (statusFlagsFor session) ]
                                        else
                                            []

                                    let payloads =
                                        stmtPrepareOkPayload stmtId resultColumns.Length paramCount
                                        :: (parameterMetadata
                                            |> List.map (fun metadata -> columnDefPayload { Name = "?"; Metadata = metadata }))
                                        @ paramDefEof
                                        @ (resultColumns |> List.map columnDefPayload)
                                        @ resultDefEof

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
                                InformationSchema.recordQuestion session.StatusCounters
                                let r = Reader(payload)
                                let stmtId = r.ReadInt32LE()
                                let cursor = r.ReadByte() |> cursorRequest
                                r.ReadInt32LE() |> ignore // iteration count, always 1
                                let session = { session with Cursors = Map.remove stmtId session.Cursors }

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
                                | Some _ when cursor = UnsupportedCursor ->
                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload = errPayload capabilities 1235 "This version of fsdb doesn't yet support updatable or scrollable cursors" }
                                        |> Async.Ignore

                                    return! loop session
                                | Some stmt ->
                                    // A malformed COM_STMT_EXECUTE payload (a declared type not
                                    // matching what's actually on the wire, a truncated param,
                                    // ...) makes `Reader`'s reads throw straight out of this
                                    // decode step — caught here rather than escaping the
                                    // connection loop. Well-formed values that fsdb cannot
                                    // represent are normalized by `readBinaryValue`.
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
                                                                stringValueOfBytes bytes
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

                                        match runCancellable stmt.Sql (fun () -> QueryHandler.executePrepared session stmt values) with
                                        | None -> ()
                                        | Some(session, result) ->
                                            match cursor, result with
                                            | ReadOnlyCursor, ResultSet(columns, rows) ->
                                                let metadata = resultMetadata columns rows session.LastResultColumnMetadata
                                                let cursor =
                                                    { Metadata = metadata
                                                      Rows = List.toArray rows
                                                      Offset = 0 }
                                                let session = { session with Cursors = Map.add stmtId cursor session.Cursors }
                                                activeSession <- Some session

                                                do!
                                                    sendCursorHead
                                                        stream
                                                        capabilities
                                                        seqId
                                                        (statusFlagsFor session ||| StatusCursorExists)
                                                        (warningCountFor session)
                                                        columns
                                                        metadata

                                                return! loop session
                                            | _ ->
                                                activeSession <- Some session

                                                do!
                                                    sendBinaryQueryResultWithSessionState
                                                        stream
                                                        capabilities
                                                        seqId
                                                        (statusFlagsFor session)
                                                        (uint64 session.LastInsertId)
                                                        (warningCountFor session)
                                                        session.SessionStateChanges
                                                        session.LastResultColumnMetadata
                                                        result

                                                return! loop session
                            | Some(StmtFetch(stmtId, rowCount)) ->
                                match Map.tryFind stmtId session.Statements, Map.tryFind stmtId session.Cursors with
                                | None, _ ->
                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload = errPayload capabilities 1243 "Unknown prepared statement handler" }
                                        |> Async.Ignore

                                    return! loop session
                                | Some _, None ->
                                    do!
                                        writePacketAsync
                                            stream
                                            { SeqId = seqId
                                              Payload = errPayload capabilities 1421 (sprintf "The statement (%d) has no open cursor." stmtId) }
                                        |> Async.Ignore

                                    return! loop session
                                | Some _, Some cursor ->
                                    let available = cursor.Rows.Length - cursor.Offset
                                    let requested = min (uint64 rowCount) (uint64 available) |> int
                                    let nextOffset = cursor.Offset + requested
                                    let exhausted = nextOffset = cursor.Rows.Length
                                    let rows = cursor.Rows |> Seq.skip cursor.Offset |> Seq.truncate requested
                                    let status =
                                        statusFlagsFor session
                                        ||| (if exhausted then StatusLastRowSent else StatusCursorExists)
                                    let session =
                                        if exhausted then
                                            { session with Cursors = Map.remove stmtId session.Cursors }
                                        else
                                            { session with
                                                Cursors = Map.add stmtId { cursor with Offset = nextOffset } session.Cursors }
                                    activeSession <- Some session

                                    do!
                                        sendCursorRows
                                            stream
                                            capabilities
                                            seqId
                                            status
                                            (warningCountFor session)
                                            cursor.Metadata
                                            rows

                                    return! loop session
                            | Some(StmtSendLongData payload) when payload.Length < stmtLongDataHeaderLength ->
                                // stmt-id(4) + param-index(2); a shorter payload
                                // is malformed. COM_STMT_SEND_LONG_DATA takes
                                // no reply, so just skip it (never throw).
                                return! loop session
                            | Some(StmtSendLongData payload) ->
                                // No response is ever sent for this command,
                                // success or failure. Chunks are buffered until
                                // COM_STMT_EXECUTE consumes the parameter.
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
                                return!
                                    loop
                                        ({ session with
                                            Statements = Map.remove stmtId session.Statements
                                            Cursors = Map.remove stmtId session.Cursors }
                                         |> discardLongData stmtId)
                            | Some(StmtReset stmtId) ->
                                if session.Statements.ContainsKey stmtId then
                                    let session =
                                        { session with Cursors = Map.remove stmtId session.Cursors }
                                        |> discardLongData stmtId

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
                                    createSessionFor
                                        (Auth.account session.User session.AccountHost)
                                        session.LoginUser
                                        session.PasswordExpired
                                        session.Database

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
            releaseAccountLease ()
            InformationSchema.unregisterProcess (int64 connectionId)
            activeSession |> Option.iter QueryHandler.closeSession
            return raise error

        closeTls ()
        releaseAccountLease ()
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
    | None when not (List.isEmpty options.ClientCertificateAuthorities) ->
        invalidArg "options" "client certificate authorities need a TLS certificate"
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
    use _scheduler = EventScheduler.acquire store customFunctions

    let rejectAtCapacity (client: TcpClient) =
        async {
            try
                use client = client
                use stream = client.GetStream()
                let payload = errPayload ClientProtocol41 1040 "Too many connections"
                do! writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.Ignore
            // Rejection is detached after admission fails. Socket errors belong
            // to the rejected peer and must not escape onto the thread pool.
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
