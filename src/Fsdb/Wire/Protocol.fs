/// HandshakeV10, HandshakeResponse41, OK/ERR/EOF packets, and text resultset
/// encoding. https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_connection_lifecycle.html
module Fsdb.Protocol

open System
open System.Text
open Fsdb.Binary
open Fsdb.Packet
open Fsdb.Value
open Fsdb.Temporal

let private strictUtf8 = UTF8Encoding(false, true)

let stringValueOfBytes (bytes: byte[]) : Value =
    try
        VString(strictUtf8.GetString bytes)
    with :? DecoderFallbackException ->
        VBytes bytes

let decodeSqlBytes (bytes: byte[]) : string =
    try
        strictUtf8.GetString bytes
    with :? DecoderFallbackException ->
        let sql = StringBuilder(bytes.Length * 2)
        let mutable index = 0
        let mutable segmentStart = 0

        let appendUtf8 start count =
            if count > 0 then
                sql.Append(strictUtf8.GetString(bytes, start, count)) |> ignore

        while index < bytes.Length do
            if bytes.[index] <> byte '\'' then
                index <- index + 1
            else
                appendUtf8 segmentStart (index - segmentStart)
                let literal = ResizeArray<byte>()
                let start = index
                let mutable closed = false
                index <- index + 1

                while index < bytes.Length && not closed do
                    match bytes.[index] with
                    | 0x27uy when index + 1 < bytes.Length && bytes.[index + 1] = 0x27uy ->
                        literal.Add 0x27uy
                        index <- index + 2
                    | 0x27uy ->
                        closed <- true
                        index <- index + 1
                    | 0x5cuy when index + 1 < bytes.Length ->
                        let escaped =
                            match bytes.[index + 1] with
                            | 0x30uy -> 0uy
                            | 0x62uy -> 8uy
                            | 0x6euy -> 10uy
                            | 0x72uy -> 13uy
                            | 0x74uy -> 9uy
                            | 0x5auy -> 26uy
                            | value -> value

                        literal.Add escaped
                        index <- index + 2
                    | value ->
                        literal.Add value
                        index <- index + 1

                if closed then
                    let source = bytes.[start .. index - 1]

                    try
                        sql.Append(strictUtf8.GetString source) |> ignore
                    with :? DecoderFallbackException ->
                        sql.Append("X'").Append(Convert.ToHexString(literal.ToArray())).Append('\'') |> ignore

                    segmentStart <- index
                else
                    appendUtf8 start (bytes.Length - start)
                    segmentStart <- bytes.Length

        appendUtf8 segmentStart (bytes.Length - segmentStart)

        sql.ToString()

// Capability flags (the subset this server negotiates).
// https://dev.mysql.com/doc/dev/mysql-server/latest/group__group__cs__capabilities__flags.html
let ClientLongPassword = 0x00000001u
let ClientFoundRows = 0x00000002u
let ClientLongFlag = 0x00000004u
let ClientConnectWithDb = 0x00000008u
let ClientCompress = 0x00000020u
let ClientLocalFiles = 0x00000080u
let ClientProtocol41 = 0x00000200u
let ClientInteractive = 0x00000400u
let ClientSsl = 0x00000800u
let ClientSecureConnection = 0x00008000u
let ClientTransactions = 0x00002000u
let ClientMultiStatements = 0x00010000u
let ClientMultiResults = 0x00020000u
let ClientPluginAuth = 0x00080000u
let ClientPluginAuthLenencClientData = 0x00200000u
let ClientCanHandleExpiredPasswords = 0x00400000u
let ClientSessionTrack = 0x00800000u
let ClientDeprecateEof = 0x01000000u
let ClientZstdCompressionAlgorithm = 0x04000000u

/// What this server offers during the handshake. Effective per-connection
/// capabilities are this AND-ed with whatever the client requests.
let ServerCapabilities =
    ClientLongPassword
    ||| ClientFoundRows
    ||| ClientLongFlag
    ||| ClientConnectWithDb
    ||| ClientCompress
    ||| ClientProtocol41
    ||| ClientInteractive
    ||| ClientSecureConnection
    ||| ClientTransactions
    ||| ClientMultiStatements
    ||| ClientMultiResults
    ||| ClientPluginAuth
    ||| ClientCanHandleExpiredPasswords
    ||| ClientSessionTrack
    ||| ClientDeprecateEof
    ||| ClientZstdCompressionAlgorithm

/// Adds capabilities enabled by the current transport and server settings.
let serverCapabilities (tlsEnabled: bool) =
    ServerCapabilities
    ||| (if tlsEnabled then ClientSsl else 0u)
    ||| (if Limits.localInfile then ClientLocalFiles else 0u)

let ServerVersion = "8.4.0-fsdb"

/// utf8mb4_general_ci, used both as the handshake charset id and column charset.
let Utf8Mb4GeneralCi = 45

/// MySQL's `binary` pseudo-collation id. BLOB/VARBINARY result columns must
/// advertise this rather than a UTF-8 collation or clients are entitled to
/// decode arbitrary bytes as text (and replace invalid sequences).
let BinaryCollation = 63

/// SERVER_STATUS_IN_TRANS
let StatusInTrans = 0x0001

/// SERVER_STATUS_AUTOCOMMIT
let StatusAutocommit = 2

/// SERVER_MORE_RESULTS_EXISTS
let StatusMoreResultsExists = 0x0008

/// SERVER_STATUS_CURSOR_EXISTS
let StatusCursorExists = 0x0040

/// SERVER_STATUS_LAST_ROW_SENT
let StatusLastRowSent = 0x0080

/// SERVER_SESSION_STATE_CHANGED
let StatusSessionStateChanged = 0x4000

let SessionTrackSystemVariables = 0uy
let SessionTrackSchema = 1uy
let SessionTrackStateChange = 2uy
let SessionTrackTransactionCharacteristics = 4uy
let SessionTrackTransactionState = 5uy

type SessionStateChange =
    | SystemVariableChanged of name: string * value: string
    | SchemaChanged of name: string
    | StateChanged
    | TransactionCharacteristicsChanged of sql: string
    | TransactionStateChanged of state: string

/// Builds the initial HandshakeV10 payload. `authPluginData` must be 20
/// bytes — the mysql_native_password scramble `Server.authenticateHandshake`
/// verifies the client's response against when the account has a stored
/// password (an account with no password accepts anything, see `Auth`).
let buildHandshakeV10WithCapabilities (capabilities: uint32) (connectionId: int) (authPluginData: byte[]) : byte[] =
    let w = Writer()
    w.WriteByte 10uy // protocol version
    w.WriteNullTerminatedString ServerVersion
    w.WriteInt32LE connectionId
    w.WriteBytes authPluginData.[0..7] // auth-plugin-data-part-1
    w.WriteByte 0uy // filler
    w.WriteInt16LE(int (capabilities &&& 0xffffu)) // capability flags, lower 2 bytes
    w.WriteByte(byte Utf8Mb4GeneralCi)
    w.WriteInt16LE StatusAutocommit
    w.WriteInt16LE(int ((capabilities >>> 16) &&& 0xffffu)) // capability flags, upper 2 bytes
    w.WriteByte 21uy // length of auth-plugin-data (8 + 12 + 1 null terminator)
    w.WriteBytes(Array.zeroCreate<byte> 10) // reserved
    w.WriteBytes authPluginData.[8..19] // auth-plugin-data-part-2 (12 bytes)
    w.WriteByte 0uy // null terminator for auth-plugin-data-part-2
    w.WriteNullTerminatedString "mysql_native_password"
    w.ToArray()

/// Builds a HandshakeV10 payload without TLS negotiation.
let buildHandshakeV10 (connectionId: int) (authPluginData: byte[]) : byte[] =
    buildHandshakeV10WithCapabilities ServerCapabilities connectionId authPluginData

type HandshakeResponse =
    { Capabilities: uint32
      Username: string
      /// The client's answer to the auth challenge — for
      /// mysql_native_password, `SHA1(pw) XOR SHA1(scramble + SHA1(SHA1(pw)))`
      /// (20 bytes), or empty for an empty password. Verified by `Server`
      /// only when the account has a stored password hash.
      AuthResponse: byte[]
      /// The auth plugin the client answered with (CLIENT_PLUGIN_AUTH) —
      /// `Server` sends an AuthSwitchRequest when this isn't
      /// mysql_native_password and the account needs verification.
      ClientPlugin: string option
      Database: string option
      ZstdCompressionLevel: int option }

type ChangeUserRequest =
    { Username: string
      AuthResponse: byte[]
      Database: string option
      CharacterSet: int option
      ClientPlugin: string option }

let private boundedLen (len: uint64) : int =
    if len > uint64 Int32.MaxValue then
        failwith "length-encoded value length out of range"
    else
        int len

exception SslRequestException

let private sslRequestPayloadLength = 32

/// The fixed-size SSLRequest packet sent before the encrypted handshake response.
type SslRequest =
    { Capabilities: uint32 }

/// Recognizes SSLRequest without attempting to parse it as a login response.
let tryParseSslRequest (payload: byte[]) : SslRequest option =
    if payload.Length = sslRequestPayloadLength then
        let capabilities = uint32 (Reader(payload).ReadInt32LE())

        if capabilities &&& ClientSsl <> 0u then
            Some { Capabilities = capabilities }
        else
            None
    else
        None

/// Parses a HandshakeResponse41 payload: capability flags, username, auth
/// response bytes, optional database, and the client's auth plugin name.
let parseHandshakeResponse (payload: byte[]) : HandshakeResponse =
    let r = Reader(payload)
    let capabilities = uint32 (r.ReadInt32LE())

    if (tryParseSslRequest payload).IsSome then
        raise SslRequestException

    r.ReadInt32LE() |> ignore // max packet size
    r.ReadByte() |> ignore // charset
    r.ReadBytes 23 |> ignore // reserved
    let username = r.ReadNullTerminatedString()

    let authResponse =
        if capabilities &&& ClientPluginAuthLenencClientData <> 0u then
            match r.ReadLenEncInt() with
            | Some len -> r.ReadBytes(boundedLen len)
            | None -> [||]
        elif capabilities &&& ClientSecureConnection <> 0u then
            let len = int (r.ReadByte())
            r.ReadBytes len
        else
            Text.Encoding.UTF8.GetBytes(r.ReadNullTerminatedString())

    let database =
        if capabilities &&& ClientConnectWithDb <> 0u && r.Remaining > 0 then
            match r.ReadNullTerminatedString() with
            | "" -> None
            | name -> Some name
        else
            None

    let clientPlugin =
        if capabilities &&& ClientPluginAuth <> 0u && r.Remaining > 0 then
            Some(r.ReadNullTerminatedString())
        else
            None

    let zstdCompressionLevel =
        if capabilities &&& ClientZstdCompressionAlgorithm <> 0u && r.Remaining > 0 then
            Some(int (r.ReadByte()))
        else
            None

    { Capabilities = capabilities
      Username = username
      AuthResponse = authResponse
      ClientPlugin = clientPlugin
      Database = database
      ZstdCompressionLevel = zstdCompressionLevel }

let negotiatedCompression capabilities zstdCompressionLevel =
    if capabilities &&& ClientCompress <> 0u then
        Ok(Some Compression.Algorithm.Zlib)
    elif capabilities &&& ClientZstdCompressionAlgorithm <> 0u then
        match zstdCompressionLevel with
        | Some level when level >= 1 && level <= 22 -> Ok(Some(Compression.Algorithm.Zstandard level))
        | _ -> Error(3923, "Invalid zstd compression level for algorithm 'zstd'.")
    else
        Ok None

/// Parses the COM_CHANGE_USER payload after its command byte. Optional
/// fields follow the capabilities negotiated during the initial handshake.
let parseChangeUserRequest (capabilities: uint32) (payload: byte[]) : ChangeUserRequest =
    let reader = Reader(payload)

    let readNullTerminatedBytes () =
        let bytes = ResizeArray<byte>()
        let mutable value = reader.ReadByte()

        while value <> 0uy do
            bytes.Add value
            value <- reader.ReadByte()

        bytes.ToArray()

    let username = reader.ReadNullTerminatedString()

    let authResponse =
        if capabilities &&& ClientSecureConnection <> 0u then
            reader.ReadBytes(int (reader.ReadByte()))
        else
            readNullTerminatedBytes ()

    let database =
        match reader.ReadNullTerminatedString() with
        | "" -> None
        | name -> Some name

    let characterSet =
        if capabilities &&& ClientProtocol41 <> 0u && reader.Remaining > 0 then
            Some(reader.ReadInt16LE())
        else
            None

    let clientPlugin =
        if capabilities &&& ClientPluginAuth <> 0u && reader.Remaining > 0 then
            Some(reader.ReadNullTerminatedString())
        else
            None

    if reader.Remaining <> 0 then
        invalidArg (nameof payload) "unexpected COM_CHANGE_USER payload data"

    { Username = username
      AuthResponse = authResponse
      Database = database
      CharacterSet = characterSet
      ClientPlugin = clientPlugin }

let private okPayloadWithHeader
    (header: byte)
    (capabilities: uint32)
    (statusFlags: int)
    (affectedRows: uint64)
    (lastInsertId: uint64)
    (warnings: int)
    (sessionStateChanges: SessionStateChange list)
    : byte[] =
    let sessionState =
        let writer = Writer()

        let writeBlock kind writeData =
            let data = Writer()
            writeData data
            writer.WriteByte kind
            writer.WriteLenEncBytes(data.ToArray())

        for change in sessionStateChanges do
            match change with
            | SystemVariableChanged(name, value) ->
                writeBlock SessionTrackSystemVariables (fun data ->
                    data.WriteLenEncString name
                    data.WriteLenEncString value)
            | SchemaChanged name ->
                writeBlock SessionTrackSchema (fun data -> data.WriteLenEncString name)
            | StateChanged ->
                writeBlock SessionTrackStateChange (fun data -> data.WriteLenEncString "1")
            | TransactionCharacteristicsChanged sql ->
                writeBlock SessionTrackTransactionCharacteristics (fun data -> data.WriteLenEncString sql)
            | TransactionStateChanged state ->
                writeBlock SessionTrackTransactionState (fun data -> data.WriteLenEncString state)

        writer.ToArray()

    let tracksSession = capabilities &&& ClientSessionTrack <> 0u
    let statusFlags =
        if tracksSession && sessionState.Length > 0 then
            statusFlags ||| StatusSessionStateChanged
        else
            statusFlags

    let w = Writer()
    w.WriteByte header
    w.WriteLenEncInt affectedRows
    w.WriteLenEncInt lastInsertId

    if capabilities &&& ClientProtocol41 <> 0u then
        w.WriteInt16LE statusFlags
        w.WriteInt16LE warnings

        if tracksSession then
            w.WriteLenEncString ""

            if sessionState.Length > 0 then
                w.WriteLenEncBytes sessionState

    w.ToArray()

/// Builds an OK packet payload (header 0x00). Used for command responses
/// (handshake, COM_QUERY for non-SELECT statements, COM_PING, ...).
/// `statusFlags` carries the session's autocommit mode and whether a
/// transaction is open (see `Server.statusFlagsFor`) —
/// PDO's `inTransaction()`/`beginTransaction()`/`commit()` read this bit
/// directly off the OK packet rather than tracking transaction state
/// themselves.
let okPayload (capabilities: uint32) (statusFlags: int) (affectedRows: uint64) (lastInsertId: uint64) : byte[] =
    okPayloadWithHeader 0uy capabilities statusFlags affectedRows lastInsertId 0 []

let okPayloadWithWarnings
    (capabilities: uint32)
    (statusFlags: int)
    (affectedRows: uint64)
    (lastInsertId: uint64)
    (warnings: int)
    : byte[] =
    okPayloadWithHeader 0uy capabilities statusFlags affectedRows lastInsertId warnings []

let okPayloadWithWarningsAndSessionState
    (capabilities: uint32)
    (statusFlags: int)
    (affectedRows: uint64)
    (lastInsertId: uint64)
    (warnings: int)
    (sessionStateChanges: SessionStateChange list)
    : byte[] =
    okPayloadWithHeader 0uy capabilities statusFlags affectedRows lastInsertId warnings sessionStateChanges

/// Builds the OK packet that terminates a resultset when CLIENT_DEPRECATE_EOF
/// is negotiated. Same shape as `okPayload`, but header 0xfe — clients tell
/// it apart from a row by that header byte together with the packet length,
/// so this can't just reuse okPayload's 0x00.
let okEndOfResultSetPayload (capabilities: uint32) (statusFlags: int) : byte[] =
    okPayloadWithHeader 0xfeuy capabilities statusFlags 0UL 0UL 0 []

let okEndOfResultSetPayloadWithWarnings (capabilities: uint32) (statusFlags: int) (warnings: int) : byte[] =
    okPayloadWithHeader 0xfeuy capabilities statusFlags 0UL 0UL warnings []

let okEndOfResultSetPayloadWithWarningsAndSessionState
    (capabilities: uint32)
    (statusFlags: int)
    (warnings: int)
    (sessionStateChanges: SessionStateChange list)
    : byte[] =
    okPayloadWithHeader 0xfeuy capabilities statusFlags 0UL 0UL warnings sessionStateChanges

/// Minimal MySQL error-code -> SQLSTATE mapping. Drivers/ORMs branch on
/// SQLSTATE, not the vendor code — PDO/Doctrine map 42000 to a syntax-error
/// exception, 08S01 to a retryable link failure, etc. — so reporting every
/// error as the generic HY000 silently degrades error classification and
/// retry logic. This grows as new error codes are introduced; anything
/// unmapped falls back to HY000, matching MySQL's own default.
let sqlStateForCode = SqlState.forCode

let errPayloadWithState (capabilities: uint32) (code: int) (state: string) (message: string) : byte[] =
    let w = Writer()
    w.WriteByte 0xffuy
    w.WriteInt16LE code

    if capabilities &&& ClientProtocol41 <> 0u then
        w.WriteByte(byte '#')
        w.WriteBytes(Encoding.ASCII.GetBytes state)

    w.WriteBytes(Encoding.UTF8.GetBytes message)
    w.ToArray()

/// Builds an ERR packet payload (header 0xff).
let errPayload (capabilities: uint32) (code: int) (message: string) : byte[] =
    errPayloadWithState capabilities code (sqlStateForCode code) message

/// Builds an EOF packet payload (header 0xfe). Only used when the client
/// hasn't negotiated CLIENT_DEPRECATE_EOF.
let eofPayload (capabilities: uint32) (statusFlags: int) : byte[] =
    let w = Writer()
    w.WriteByte 0xfeuy

    if capabilities &&& ClientProtocol41 <> 0u then
        w.WriteInt16LE 0
        w.WriteInt16LE statusFlags

    w.ToArray()

let eofPayloadWithWarnings (capabilities: uint32) (statusFlags: int) (warnings: int) : byte[] =
    let w = Writer()
    w.WriteByte 0xfeuy

    if capabilities &&& ClientProtocol41 <> 0u then
        w.WriteInt16LE warnings
        w.WriteInt16LE statusFlags

    w.ToArray()

/// A resultset column definition ready for protocol encoding.
type ColumnDef =
    { Name: string
      Metadata: ColumnMetadata }

/// Counts the fractional-second digits a temporal value's already-rendered
/// text carries (`... :00.000000` → 6, `... :00` → 0) — the fsp the wire
/// `decimals` field must advertise. The resultset path has no `ColumnType`
/// in hand at send time (only the rendered rows), but the renderer already
/// emitted exactly fsp digits (see `Value.toTextFsp`), so reading them back
/// off the first non-NULL value recovers the precision without threading the
/// schema all the way to the wire. Gated by the caller on a temporal wire
/// type, so a `VARCHAR` that merely looks like a timestamp is never counted.
let fractionalDigitsOf (values: string option list) : byte =
    match values |> List.tryPick id with
    | Some s ->
        match s.LastIndexOf '.' with
        | -1 -> 0uy
        | dot -> byte (min 6 (s.Length - dot - 1))
    | None -> 0uy

let columnDefPayload (col: ColumnDef) : byte[] =
    let w = Writer()
    let origin = col.Metadata.Origin
    w.WriteLenEncString "def" // catalog
    w.WriteLenEncString(origin |> Option.map _.Schema |> Option.defaultValue "")
    w.WriteLenEncString(origin |> Option.map _.Table |> Option.defaultValue "")
    w.WriteLenEncString(origin |> Option.map _.OriginalTable |> Option.defaultValue "")
    w.WriteLenEncString col.Name
    w.WriteLenEncString(origin |> Option.map _.OriginalName |> Option.defaultValue "")
    w.WriteLenEncInt 0x0cUL // length of fixed-length fields
    let isBinary =
        col.Metadata.TypeId <> TypeJson
        && (col.Metadata.Flags &&& BinaryFlag <> 0us || col.Metadata.TypeId = TypeBit)
    let collation =
        if isBinary then
            BinaryCollation
        else
            col.Metadata.CollationId |> Option.map int |> Option.defaultValue Utf8Mb4GeneralCi

    w.WriteInt16LE collation
    w.WriteInt32LE(int col.Metadata.ColumnLength)
    w.WriteByte col.Metadata.TypeId
    w.WriteInt16LE(int col.Metadata.Flags)
    w.WriteByte col.Metadata.Decimals
    w.WriteInt16LE 0 // filler
    w.ToArray()

/// Maps a column's *declared* SQL type to its MySQL wire type id. The
/// mapping itself lives in `ColumnWire`, which compiles early enough for
/// `Executor` to share it; this alias is the name the COM_FIELD_LIST path
/// (`Server`'s `FieldList` handler) has always called it by.
let wireTypeOfColumnType = ColumnWire.wireTypeOf

/// The `decimals` (fsp) a *declared* column type advertises — the COM_FIELD_LIST
/// counterpart to `fractionalDigitsOf` (which recovers it from rendered rows),
/// used where the real `Ast.ColumnType` is in hand instead of row data.
let decimalsOfColumnType (ty: Ast.ColumnType) : byte =
    match ty with
    | Ast.TDateTime fsp
    | Ast.TTimestamp fsp
    | Ast.TTime fsp -> byte fsp
    | _ -> 0uy

/// Encodes one text-protocol row. None means SQL NULL.
let textRowPayload (values: string option list) : byte[] =
    let w = Writer()

    for v in values do
        match v with
        | None -> w.WriteLenEncNull()
        | Some s -> w.WriteLenEncString s

    w.ToArray()

/// Text-protocol row encoding with the advertised column types available.
/// `Executor.QueryResult` represents VBytes losslessly as a Latin-1 string;
/// turn that carrier back into its original bytes for binary columns rather
/// than UTF-8-encoding it and changing every byte above 0x7f.
let textRowPayloadTyped (columns: ColumnMetadata list) (values: string option list) : byte[] =
    let w = Writer()

    List.zip columns values
    |> List.iter (fun (metadata, value) ->
        match value with
        | None -> w.WriteLenEncNull()
        | Some s when metadata.Flags &&& BinaryFlag <> 0us || metadata.TypeId = TypeBit -> w.WriteLenEncBytes(Encoding.Latin1.GetBytes s)
        | Some s -> w.WriteLenEncString s)

    w.ToArray()

/// Writes one non-NULL binary-protocol row value already rendered as
/// `Value.toText`-style text (`Executor.QueryResult` never keeps the
/// original typed `Value` around — see `Value.mysqlTypeOf`'s doc), parsed
/// back and re-encoded per `typeId`'s fixed-width wire shape
/// (https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_binary_resultset.html#sect_protocol_binary_resultset_row_value)
/// — the inverse of `readBinaryValue`'s numeric/date cases. Reporting a
/// non-VAR_STRING type in the column definition but still encoding the row
/// as a length-encoded string (or vice versa) desyncs every column after
/// it for a real binary-protocol client, so this must stay in lockstep
/// with `Value.mysqlTypeOf`/`wireTypeOfColumnType`'s type choices.
/// NEWDECIMAL and anything this doesn't special-case fall back to the
/// same length-encoded string DECIMAL/VARCHAR/VARSTRING/STRING already use
/// on the wire (see `readBinaryValue`) — decimal precision has to survive
/// as text either way, so there's no fixed-width form to prefer.
/// Encodes a `[-]H+:MM:SS[.ffffff]` time string into the binary-protocol TIME
/// form — the inverse of `readBinaryTime`. MySQL's TIME hours run past 24
/// (up to 838), so the hour field splits into a `days`+`hour` pair the same
/// way `readBinaryTime` recombines them. Length 0 for the all-zero time, 8
/// without a fraction, 12 with microseconds.
let private writeBinaryTime (w: Writer) (s: string) : unit =
    match tryParseTimeValue s with
    | Some value when timeTicks value = 0L -> w.WriteByte 0uy
    | Some value ->
        let ticks = timeTicks value
        let magnitude = timeMagnitude ticks
        let totalSeconds = magnitude / uint64 TimeSpan.TicksPerSecond
        let totalHours = totalSeconds / 3600UL
        let minute = totalSeconds % 3600UL / 60UL
        let second = totalSeconds % 60UL
        let micros = magnitude % uint64 TimeSpan.TicksPerSecond / 10UL
        w.WriteByte(if micros = 0UL then 8uy else 12uy)
        w.WriteByte(if ticks < 0L then 1uy else 0uy)
        w.WriteInt32LE(int (totalHours / 24UL))
        w.WriteByte(byte (totalHours % 24UL))
        w.WriteByte(byte minute)
        w.WriteByte(byte second)

        if micros <> 0UL then
            w.WriteInt32LE(int micros)
    | None -> w.WriteByte 0uy

/// `Int64.Parse` that yields `fallback` instead of throwing — see the
/// integer cases in `writeBinaryValue` for why a throw is not an option
/// there.
let private parseIntOr (fallback: int64) (s: string) : int64 =
    match Int64.TryParse(s, Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture) with
    | true, v -> v
    | _ -> fallback

let private writeBinaryValue (w: Writer) (metadata: ColumnMetadata) (s: string) : unit =
    let typeId = metadata.TypeId

    if typeId = TypeBit then
        w.WriteLenEncBytes(Encoding.Latin1.GetBytes s)
    elif typeId = TypeLongLong then
        if metadata.Flags &&& UnsignedFlag <> 0us then
            w.WriteInt64LE(int64 (UInt64.Parse(s, Globalization.CultureInfo.InvariantCulture)))
        else
            w.WriteInt64LE(Int64.Parse(s, Globalization.CultureInfo.InvariantCulture))
    elif typeId = TypeDouble then
        w.WriteDoubleLE(Double.Parse(s, Globalization.CultureInfo.InvariantCulture))
    elif typeId = TypeDate then
        match tryParseZeroDate s with
        | Some date when isAllZeroDate date -> w.WriteByte 0uy
        | Some date ->
            let year, month, day = zeroDateParts date
            w.WriteByte 4uy
            w.WriteInt16LE year
            w.WriteByte(byte month)
            w.WriteByte(byte day)
        | None ->
            let d = DateOnly.Parse(s, Globalization.CultureInfo.InvariantCulture)
            w.WriteByte 4uy
            w.WriteInt16LE d.Year
            w.WriteByte(byte d.Month)
            w.WriteByte(byte d.Day)
    elif typeId = TypeDateTime || typeId = TypeTimestamp then
        let write date hour minute second micros =
            let year, month, day = zeroDateParts date
            w.WriteByte(if micros = 0 then 7uy else 11uy)
            w.WriteInt16LE year
            w.WriteByte(byte month)
            w.WriteByte(byte day)
            w.WriteByte(byte hour)
            w.WriteByte(byte minute)
            w.WriteByte(byte second)

            if micros <> 0 then
                w.WriteInt32LE micros

        match tryParseZeroDateTime s with
        | Some dateTime when isAllZeroDateTime dateTime -> w.WriteByte 0uy
        | Some dateTime ->
            let date, hour, minute, second, micros = zeroDateTimeParts dateTime
            write date hour minute second micros
        | None ->
            let dt = DateTime.Parse(s, Globalization.CultureInfo.InvariantCulture)
        // Sub-second precision: ticks are 100ns, so the sub-second
        // remainder divided by 10 is microseconds. A value with a non-zero
        // fractional second needs the 11-byte wire form (length 7 has no
        // room for it) or the fraction silently drops on the wire.
            let micros = int ((dt.Ticks % TimeSpan.TicksPerSecond) / 10L)
            w.WriteByte(if micros = 0 then 7uy else 11uy)
            w.WriteInt16LE dt.Year
            w.WriteByte(byte dt.Month)
            w.WriteByte(byte dt.Day)
            w.WriteByte(byte dt.Hour)
            w.WriteByte(byte dt.Minute)
            w.WriteByte(byte dt.Second)

            if micros <> 0 then
                w.WriteInt32LE micros
    elif typeId = TypeTime then
        writeBinaryTime w s
    // The narrower integer widths, which a resultset only advertises when
    // the output column resolved back to a declared TINYINT/SMALLINT/INT/
    // YEAR/BOOLEAN (see `Executor.outputColumnWireOverrides`). Each must
    // write exactly the width its advertised type implies: a length-encoded
    // string here instead desyncs every column after it, which is what this
    // function's doc is about. Parsed defensively for the same reason
    // `writeBinaryTime` is — this runs after the handler returned, so a
    // throw would drop the connection with no ERR ever sent.
    elif typeId = TypeTiny then
        w.WriteByte(byte (sbyte (parseIntOr 0L s)))
    elif typeId = TypeYear || typeId = TypeShort then
        w.WriteInt16LE(int (int16 (parseIntOr 0L s)))
    elif typeId = TypeLong then
        w.WriteInt32LE(int (int32 (parseIntOr 0L s)))
    elif metadata.Flags &&& BinaryFlag <> 0us then
        w.WriteLenEncBytes(Encoding.Latin1.GetBytes s)
    else
        w.WriteLenEncString s

/// Encodes one binary-protocol resultset row
/// (https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_binary_resultset.html#sect_protocol_binary_resultset_row).
/// `columns` must be the same list `columnDefPayload` advertised each
/// column as (see `writeBinaryValue`'s doc on why); a shorter/longer list
/// than `values` is a caller bug, not a value this falls back for. None
/// means SQL NULL.
let binaryRowPayload (columns: ColumnMetadata list) (values: string option list) : byte[] =
    let w = Writer()
    w.WriteByte 0uy // packet header, always 0x00 for a row

    // Null bitmap: one bit per column, offset by 2 (bits 0 and 1 are
    // reserved), rounded up to whole bytes.
    let nullBitmap = Array.zeroCreate<byte> ((values.Length + 7 + 2) / 8)

    values
    |> List.iteri (fun i v ->
        if v.IsNone then
            let bitPos = i + 2
            nullBitmap.[bitPos / 8] <- nullBitmap.[bitPos / 8] ||| (1uy <<< (bitPos % 8)))

    w.WriteBytes nullBitmap

    List.zip columns values
    |> List.iter (fun (metadata, v) ->
        match v with
        | Some s -> writeBinaryValue w metadata s
        | None -> ())

    w.ToArray()

/// Builds the fixed COM_STMT_PREPARE_OK header. Parameter and result-column
/// definitions follow as separate packets.
let stmtPrepareOkPayload (stmtId: int) (numColumns: int) (numParams: int) : byte[] =
    let w = Writer()
    w.WriteByte 0uy
    w.WriteInt32LE stmtId
    w.WriteInt16LE numColumns
    w.WriteInt16LE numParams
    w.WriteByte 0uy // reserved
    w.WriteInt16LE 0 // warning count
    w.ToArray()

// MySQL binary protocol column type ids, as used in COM_STMT_EXECUTE's
// per-parameter type array.
// https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_basic_dt_types.html
let TypeTiny = 0x01uy
let TypeShort = 0x02uy
let TypeLong = 0x03uy
let TypeFloat = 0x04uy
let TypeDouble = 0x05uy
let TypeNull = 0x06uy
let TypeTimestamp = 0x07uy
let TypeLongLong = 0x08uy
let TypeDate = 0x0auy
let TypeTime = 0x0buy
let TypeDateTime = 0x0cuy
let TypeVarchar = 0x0fuy
let TypeNewDecimal = 0xf6uy
let TypeBlob = 0xfcuy
let TypeVarString = 0xfduy
let TypeString = 0xfeuy

/// Reads a MySQL binary-protocol DATE/DATETIME/TIMESTAMP value off `r`: a
/// length byte, then that many bytes of year/month/day[/hour/min/sec[/µs]]
/// — a shorter length just omits the trailing fields (MySQL only sends as
/// many bytes as the value needs). Length 0 is the zero date.
/// https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_binary_resultset.html#sect_protocol_binary_resultset_row_value
let private readBinaryDateTime (r: Reader) : Value =
    let len = int (r.ReadByte())

    if len = 0 then
        tryZeroDate 0 0 0
        |> Option.bind (fun date -> tryZeroDateTime date 0 0 0 0)
        |> Option.map VZeroDateTime
        |> Option.defaultWith (fun () -> failwith "invalid all-zero datetime")
    else
        let year = r.ReadInt16LE()
        let month = int (r.ReadByte())
        let day = int (r.ReadByte())

        let hour, minute, second =
            if len > 4 then int (r.ReadByte()), int (r.ReadByte()), int (r.ReadByte()) else 0, 0, 0

        let micros = if len > 7 then r.ReadInt32LE() else 0
        match tryZeroDate year month day with
        | Some date ->
            tryZeroDateTime date hour minute second micros
            |> Option.map VZeroDateTime
            |> Option.defaultWith (fun () -> failwith "invalid zero datetime")
        | None -> VDateTime(DateTime(year, month, day, hour, minute, second).AddTicks(int64 micros * 10L))

/// Reads a MySQL binary-protocol TIME value off `r`.
let private readBinaryTime (r: Reader) : Value =
    let len = int (r.ReadByte())

    match len with
    | 0 -> VTime(timeValueOrClamp 0L)
    | 8
    | 12 ->
        let sign = r.ReadByte()
        let days = uint32 (r.ReadInt32LE())
        let hour = r.ReadByte()
        let minute = r.ReadByte()
        let second = r.ReadByte()
        let micros = if len = 12 then r.ReadInt32LE() else 0
        let totalHours = uint64 days * 24UL + uint64 hour

        if
            sign > 1uy
            || hour >= 24uy
            || minute >= 60uy
            || second >= 60uy
            || micros < 0
            || micros > 999999
            || totalHours > 838UL
            || (totalHours = 838UL && (minute > 59uy || second > 59uy || micros <> 0))
        then
            invalidArg "TIME" "Invalid binary TIME value"
        else
            let ticks =
                ((int64 totalHours * 3600L + int64 minute * 60L + int64 second) * TimeSpan.TicksPerSecond)
                + int64 micros * 10L
            let ticks = if sign = 1uy then -ticks else ticks
            VTime(tryTimeValue ticks |> Option.defaultWith (fun () -> invalidArg "TIME" "Invalid binary TIME value"))
    | _ -> invalidArg "TIME" "Invalid binary TIME length"

/// Reads one COM_STMT_EXECUTE binary parameter value of MySQL binary type
/// id `typeId` off `r`, decoded into an fsdb `Value`. `unsigned` only
/// matters for the fixed-width integer types. NEWDECIMAL/VARCHAR/
/// VAR_STRING/STRING/BLOB all arrive as a length-encoded byte string.
let readBinaryValue (r: Reader) (typeId: byte) (unsigned: bool) : Value =
    // A length-encoded length is a uint64 off the wire; casting a value
    // above Int32.MaxValue to `int` goes negative and `ReadBytes` would
    // silently yield an empty slice. Reject it so the COM_STMT_EXECUTE
    // decode loop's catch turns it into a clean 1210 instead.
    let lenEncText () =
        match r.ReadLenEncInt() with
        | None -> ""
        | Some len -> Encoding.UTF8.GetString(r.ReadBytes(boundedLen len))

    let lenEncBytes () =
        match r.ReadLenEncInt() with
        | None -> [||]
        | Some len -> r.ReadBytes(boundedLen len)

    let lenEncStringValue () =
        lenEncBytes () |> stringValueOfBytes

    if typeId = TypeTiny then
        let b = r.ReadByte()
        VInt(if unsigned then int64 b else int64 (sbyte b))
    elif typeId = TypeShort then
        let v = r.ReadInt16LE()
        VInt(if unsigned then int64 (uint16 v) else int64 (int16 v))
    elif typeId = TypeLong then
        let v = r.ReadInt32LE()
        VInt(if unsigned then int64 (uint32 v) else int64 v)
    elif typeId = TypeLongLong then
        let bytes = r.ReadBytes 8

        if unsigned then
            VUInt(BitConverter.ToUInt64(bytes, 0))
        else
            VInt(BitConverter.ToInt64(bytes, 0))
    elif typeId = TypeFloat then
        VDouble(float (BitConverter.ToSingle(r.ReadBytes 4, 0)))
    elif typeId = TypeDouble then
        VDouble(BitConverter.ToDouble(r.ReadBytes 8, 0))
    elif
        typeId = TypeVarchar
        || typeId = TypeVarString
        || typeId = TypeString
    then
        lenEncStringValue ()
    elif typeId = TypeNewDecimal then
        VString(lenEncText ())
    elif typeId = TypeBlob then
        VBytes(lenEncBytes ())
    elif typeId = TypeGeometry then
        let bytes = lenEncBytes ()

        match tryGeometryFromMySqlBinary bytes with
        | Some geometry -> VGeometry geometry
        | None -> raise (GeometryError "Invalid GIS data provided to binary parameter")
    elif typeId = TypeDate then
        match readBinaryDateTime r with
        | VZeroDateTime dateTime -> VZeroDate(zeroDateOfDateTime dateTime)
        | VDateTime dateTime -> VDate(DateOnly.FromDateTime dateTime)
        | _ -> VNull
    elif typeId = TypeDateTime || typeId = TypeTimestamp then
        readBinaryDateTime r
    elif typeId = TypeTime then
        readBinaryTime r
    elif typeId = TypeBit then
        VBytes(lenEncBytes ())
    else
        // TypeNull and anything unrecognized: NULL params never reach here
        // (the caller checks the null-bitmap first), so this is only a
        // fallback for a genuinely unsupported type id.
        VNull
