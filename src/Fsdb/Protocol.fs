/// HandshakeV10, HandshakeResponse41, OK/ERR/EOF packets, and text resultset
/// encoding. https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_connection_lifecycle.html
module Fsdb.Protocol

open System
open System.Text
open Fsdb.Binary
open Fsdb.Packet
open Fsdb.Value

// Capability flags (the subset this server negotiates).
// https://dev.mysql.com/doc/dev/mysql-server/latest/group__group__cs__capabilities__flags.html
let ClientLongPassword = 0x00000001u
let ClientFoundRows = 0x00000002u
let ClientLongFlag = 0x00000004u
let ClientConnectWithDb = 0x00000008u
let ClientProtocol41 = 0x00000200u
let ClientSsl = 0x00000800u
let ClientSecureConnection = 0x00008000u
let ClientTransactions = 0x00002000u
let ClientMultiResults = 0x00020000u
let ClientPluginAuth = 0x00080000u
let ClientPluginAuthLenencClientData = 0x00200000u
let ClientDeprecateEof = 0x01000000u

/// What this server offers during the handshake. Effective per-connection
/// capabilities are this AND-ed with whatever the client requests.
let ServerCapabilities =
    ClientLongPassword
    ||| ClientFoundRows
    ||| ClientLongFlag
    ||| ClientConnectWithDb
    ||| ClientProtocol41
    ||| ClientSecureConnection
    ||| ClientTransactions
    ||| ClientMultiResults
    ||| ClientPluginAuth
    ||| ClientDeprecateEof

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

/// Builds the initial HandshakeV10 payload. `authPluginData` must be 20
/// bytes — the mysql_native_password scramble `Server.authenticateHandshake`
/// verifies the client's response against when the account has a stored
/// password (an account with no password accepts anything, see `Auth`).
let buildHandshakeV10 (connectionId: int) (authPluginData: byte[]) : byte[] =
    let w = Writer()
    w.WriteByte 10uy // protocol version
    w.WriteNullTerminatedString ServerVersion
    w.WriteInt32LE connectionId
    w.WriteBytes authPluginData.[0..7] // auth-plugin-data-part-1
    w.WriteByte 0uy // filler
    w.WriteInt16LE(int (ServerCapabilities &&& 0xffffu)) // capability flags, lower 2 bytes
    w.WriteByte(byte Utf8Mb4GeneralCi)
    w.WriteInt16LE StatusAutocommit
    w.WriteInt16LE(int ((ServerCapabilities >>> 16) &&& 0xffffu)) // capability flags, upper 2 bytes
    w.WriteByte 21uy // length of auth-plugin-data (8 + 12 + 1 null terminator)
    w.WriteBytes(Array.zeroCreate<byte> 10) // reserved
    w.WriteBytes authPluginData.[8..19] // auth-plugin-data-part-2 (12 bytes)
    w.WriteByte 0uy // null terminator for auth-plugin-data-part-2
    w.WriteNullTerminatedString "mysql_native_password"
    w.ToArray()

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
      Database: string option }

let private boundedLen (len: uint64) : int =
    if len > uint64 Int32.MaxValue then
        failwith "length-encoded value length out of range"
    else
        int len

exception SslRequestException

/// Parses a HandshakeResponse41 payload: capability flags, username, auth
/// response bytes, optional database, and the client's auth plugin name.
let parseHandshakeResponse (payload: byte[]) : HandshakeResponse =
    let r = Reader(payload)
    let capabilities = uint32 (r.ReadInt32LE())

    if payload.Length = 32 && capabilities &&& ClientSsl <> 0u then
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
            Some(r.ReadNullTerminatedString())
        else
            None

    let clientPlugin =
        if capabilities &&& ClientPluginAuth <> 0u && r.Remaining > 0 then
            Some(r.ReadNullTerminatedString())
        else
            None

    { Capabilities = capabilities
      Username = username
      AuthResponse = authResponse
      ClientPlugin = clientPlugin
      Database = database }

let private okPayloadWithHeader
    (header: byte)
    (capabilities: uint32)
    (statusFlags: int)
    (affectedRows: uint64)
    (lastInsertId: uint64)
    : byte[] =
    let w = Writer()
    w.WriteByte header
    w.WriteLenEncInt affectedRows
    w.WriteLenEncInt lastInsertId

    if capabilities &&& ClientProtocol41 <> 0u then
        w.WriteInt16LE statusFlags
        w.WriteInt16LE 0 // warnings

    w.ToArray()

/// Builds an OK packet payload (header 0x00). Used for command responses
/// (handshake, COM_QUERY for non-SELECT statements, COM_PING, ...).
/// `statusFlags` is `StatusAutocommit` alone outside a transaction, or with
/// `StatusInTrans` also set while one is open (see `Server.statusFlagsFor`) —
/// PDO's `inTransaction()`/`beginTransaction()`/`commit()` read this bit
/// directly off the OK packet rather than tracking transaction state
/// themselves.
let okPayload (capabilities: uint32) (statusFlags: int) (affectedRows: uint64) (lastInsertId: uint64) : byte[] =
    okPayloadWithHeader 0uy capabilities statusFlags affectedRows lastInsertId

/// Builds the OK packet that terminates a resultset when CLIENT_DEPRECATE_EOF
/// is negotiated. Same shape as `okPayload`, but header 0xfe — clients tell
/// it apart from a row by that header byte together with the packet length,
/// so this can't just reuse okPayload's 0x00.
let okEndOfResultSetPayload (capabilities: uint32) (statusFlags: int) : byte[] =
    okPayloadWithHeader 0xfeuy capabilities statusFlags 0UL 0UL

/// Minimal MySQL error-code -> SQLSTATE mapping. Drivers/ORMs branch on
/// SQLSTATE, not the vendor code — PDO/Doctrine map 42000 to a syntax-error
/// exception, 08S01 to a retryable link failure, etc. — so reporting every
/// error as the generic HY000 silently degrades error classification and
/// retry logic. ponytail: grows as new error codes are introduced; anything
/// unmapped falls back to HY000, matching MySQL's own default.
let sqlStateForCode (code: int) : string =
    match code with
    | 1040 -> "08004" // ER_CON_COUNT_ERROR
    | 1064 -> "42000" // ER_PARSE_ERROR
    | 1146 -> "42S02" // ER_NO_SUCH_TABLE
    | 1054 -> "42S22" // ER_BAD_FIELD_ERROR
    | 1047 -> "08S01" // ER_UNKNOWN_COM_ERROR
    | 1153 -> "08S01" // ER_NET_PACKET_TOO_LARGE
    | 1048 -> "23000" // ER_BAD_NULL_ERROR
    | 1052 -> "23000" // ER_NON_UNIQ_ERROR
    | 1062 -> "23000" // ER_DUP_ENTRY
    | 1264 -> "22003" // ER_WARN_DATA_OUT_OF_RANGE
    | 1265 -> "01000" // ER_WARN_DATA_TRUNCATED
    | 1690 -> "22003" // ER_DATA_OUT_OF_RANGE
    | 1426 -> "42000" // ER_TOO_BIG_PRECISION
    | 1235 -> "42000" // ER_NOT_SUPPORTED_YET
    | 1451 -> "23000" // ER_ROW_IS_REFERENCED_2
    | 1452 -> "23000" // ER_NO_REFERENCED_ROW_2
    | _ -> "HY000"

/// Builds an ERR packet payload (header 0xff).
let errPayload (capabilities: uint32) (code: int) (message: string) : byte[] =
    let w = Writer()
    w.WriteByte 0xffuy
    w.WriteInt16LE code

    if capabilities &&& ClientProtocol41 <> 0u then
        w.WriteByte(byte '#')
        w.WriteBytes(Encoding.ASCII.GetBytes(sqlStateForCode code))

    w.WriteBytes(Encoding.UTF8.GetBytes message)
    w.ToArray()

/// Builds an EOF packet payload (header 0xfe). Only used when the client
/// hasn't negotiated CLIENT_DEPRECATE_EOF.
let eofPayload (capabilities: uint32) (statusFlags: int) : byte[] =
    let w = Writer()
    w.WriteByte 0xfeuy

    if capabilities &&& ClientProtocol41 <> 0u then
        w.WriteInt16LE 0 // warnings
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
    w.WriteLenEncString "def" // catalog
    w.WriteLenEncString "" // schema
    w.WriteLenEncString "" // table
    w.WriteLenEncString "" // org_table
    w.WriteLenEncString col.Name
    w.WriteLenEncString col.Name // org_name
    w.WriteLenEncInt 0x0cUL // length of fixed-length fields
    let isBinary = col.Metadata.Flags &&& BinaryFlag <> 0us
    w.WriteInt16LE(if isBinary then BinaryCollation else Utf8Mb4GeneralCi)
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
        | Some s when metadata.Flags &&& BinaryFlag <> 0us -> w.WriteLenEncBytes(Encoding.Latin1.GetBytes s)
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
    let neg = s.StartsWith "-"
    let body = if neg then s.Substring 1 else s

    // A TIME column keeps raw text when TimeSpan parsing failed at insert
    // time, so the rendered string here can be anything a client stored.
    // Parse defensively — any malformed component collapses the whole value
    // to a zero TIME rather than throwing in the binary send path (which
    // runs after the handler returned and would otherwise drop the
    // connection with no ERR).
    let tryInt (x: string) =
        match Int32.TryParse(x, Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture) with
        | true, v -> Some v
        | _ -> None

    let timePart, micros =
        match body.IndexOf '.' with
        | -1 -> body, 0
        | i -> body.Substring(0, i), (tryInt (body.Substring(i + 1).PadRight(6, '0').Substring(0, 6)) |> Option.defaultValue 0)

    let parts = timePart.Split ':'

    let totalHours, minute, second =
        if parts.Length = 3 then
            match tryInt parts.[0], tryInt parts.[1], tryInt parts.[2] with
            | Some h, Some m, Some s -> h, m, s
            | _ -> 0, 0, 0
        else
            0, 0, 0

    let days, hour = totalHours / 24, totalHours % 24

    if totalHours = 0 && minute = 0 && second = 0 && micros = 0 then
        w.WriteByte 0uy
    else
        w.WriteByte(if micros = 0 then 8uy else 12uy)
        w.WriteByte(if neg then 1uy else 0uy)
        w.WriteInt32LE days
        w.WriteByte(byte hour)
        w.WriteByte(byte minute)
        w.WriteByte(byte second)

        if micros <> 0 then
            w.WriteInt32LE micros

/// `Int64.Parse` that yields `fallback` instead of throwing — see the
/// integer cases in `writeBinaryValue` for why a throw is not an option
/// there.
let private parseIntOr (fallback: int64) (s: string) : int64 =
    match Int64.TryParse(s, Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture) with
    | true, v -> v
    | _ -> fallback

let private writeBinaryValue (w: Writer) (metadata: ColumnMetadata) (s: string) : unit =
    let typeId = metadata.TypeId

    if typeId = TypeLongLong then
        if metadata.Flags &&& UnsignedFlag <> 0us then
            w.WriteInt64LE(int64 (UInt64.Parse(s, Globalization.CultureInfo.InvariantCulture)))
        else
            w.WriteInt64LE(Int64.Parse(s, Globalization.CultureInfo.InvariantCulture))
    elif typeId = TypeDouble then
        w.WriteDoubleLE(Double.Parse(s, Globalization.CultureInfo.InvariantCulture))
    elif typeId = TypeDate then
        let d = DateOnly.Parse(s, Globalization.CultureInfo.InvariantCulture)
        w.WriteByte 4uy
        w.WriteInt16LE d.Year
        w.WriteByte(byte d.Month)
        w.WriteByte(byte d.Day)
    elif typeId = TypeDateTime || typeId = TypeTimestamp then
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

/// Builds the COM_STMT_PREPARE_OK payload: status byte, statement id,
/// column count (always 0 — this server never advertises a prepared
/// statement's result columns ahead of EXECUTE, so no column-definition
/// packets follow this one; see the ponytail note on `Server`'s
/// COM_STMT_PREPARE handler), param count, a reserved byte, and warning
/// count. The `numParams` per-param Column Definition packets (and their
/// trailing EOF, unless CLIENT_DEPRECATE_EOF) are separate packets the
/// caller sends after this one.
let stmtPrepareOkPayload (stmtId: int) (numParams: int) : byte[] =
    let w = Writer()
    w.WriteByte 0uy
    w.WriteInt32LE stmtId
    w.WriteInt16LE 0 // column count
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
let private readBinaryDateTime (r: Reader) : DateTime =
    let len = int (r.ReadByte())

    if len = 0 then
        DateTime.MinValue
    else
        let year = r.ReadInt16LE()
        let month = int (r.ReadByte())
        let day = int (r.ReadByte())

        let hour, minute, second =
            if len > 4 then int (r.ReadByte()), int (r.ReadByte()), int (r.ReadByte()) else 0, 0, 0

        let micros = if len > 7 then r.ReadInt32LE() else 0
        // MySqlConnector (and other clients) send year 0 for MySQL's
        // '0000-00-00' zero-date — `DateTime` has no such value, so clamp
        // the same way month/day already are rather than letting the
        // constructor throw and drop the connection (see the try/with
        // around this call site in `Server`).
        DateTime(max year 1, max month 1, max day 1, hour, minute, second)
            .AddTicks(int64 micros * 10L)

/// Reads a MySQL binary-protocol TIME value off `r` and renders it the way
/// MySQL's TIME text form does (`[-][H]HH:MM:SS[.ffffff]`) — fsdb has no
/// dedicated Value case for TIME (see `Value.Value`), so this returns
/// already-formatted text rather than a typed value.
let private readBinaryTime (r: Reader) : string =
    let len = int (r.ReadByte())

    if len = 0 then
        "00:00:00"
    else
        let isNegative = r.ReadByte()
        let days = r.ReadInt32LE()
        let hour = int (r.ReadByte())
        let minute = int (r.ReadByte())
        let second = int (r.ReadByte())
        let micros = if len > 8 then r.ReadInt32LE() else 0
        // int32 `days` can be attacker-controlled; widen so `days * 24`
        // can't wrap to a bogus hour count.
        let totalHours = int64 days * 24L + int64 hour
        let sign = if isNegative <> 0uy then "-" else ""
        let frac = if micros > 0 then sprintf ".%06d" micros else ""
        sprintf "%s%02d:%02d:%02d%s" sign totalHours minute second frac

/// Reads one COM_STMT_EXECUTE binary parameter value of MySQL binary type
/// id `typeId` off `r`, decoded into an fsdb `Value`. `unsigned` only
/// matters for the fixed-width integer types. NEWDECIMAL/VARCHAR/
/// VAR_STRING/STRING/BLOB all arrive as a length-encoded byte string —
/// decoded as UTF-8 text, matching how the rest of fsdb treats them (see
/// `Storage.coerceValue`).
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
        typeId = TypeNewDecimal
        || typeId = TypeVarchar
        || typeId = TypeVarString
        || typeId = TypeString
    then
        VString(lenEncText ())
    elif typeId = TypeBlob then
        VBytes(lenEncBytes ())
    elif typeId = TypeDate then
        VDate(DateOnly.FromDateTime(readBinaryDateTime r))
    elif typeId = TypeDateTime || typeId = TypeTimestamp then
        VDateTime(readBinaryDateTime r)
    elif typeId = TypeTime then
        VString(readBinaryTime r)
    else
        // TypeNull and anything unrecognized: NULL params never reach here
        // (the caller checks the null-bitmap first), so this is only a
        // fallback for a genuinely unsupported type id.
        VNull
