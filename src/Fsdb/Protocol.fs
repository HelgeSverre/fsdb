/// HandshakeV10, HandshakeResponse41, OK/ERR/EOF packets, and text resultset
/// encoding. https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_connection_lifecycle.html
module Fsdb.Protocol

open System
open System.Text
open Fsdb.Packet
open Fsdb.Value

// Capability flags (subset we care about).
// https://dev.mysql.com/doc/dev/mysql-server/latest/group__group__cs__capabilities__flags.html
let ClientLongPassword = 0x00000001u
let ClientFoundRows = 0x00000002u
let ClientLongFlag = 0x00000004u
let ClientConnectWithDb = 0x00000008u
let ClientProtocol41 = 0x00000200u
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

let ServerVersion = "8.0.36-fsdb"

/// utf8mb4_general_ci, used both as the handshake charset id and column charset.
let Utf8Mb4GeneralCi = 45

/// SERVER_STATUS_IN_TRANS
let StatusInTrans = 0x0001

/// SERVER_STATUS_AUTOCOMMIT
let StatusAutocommit = 2

/// Builds the initial HandshakeV10 payload. `authPluginData` must be 20 bytes;
/// its contents are irrelevant because we accept any password (see
/// `parseHandshakeResponse` — ponytail: no auth verification, this is a dev
/// server; add real mysql_native_password checking if this ever needs to be
/// exposed beyond localhost).
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
      Database: string option }

/// Parses a HandshakeResponse41 payload. We only need the capability flags,
/// username, and optional database — the auth response bytes are read (to
/// advance past them correctly) but never checked.
let parseHandshakeResponse (payload: byte[]) : HandshakeResponse =
    let r = Reader(payload)
    let capabilities = uint32 (r.ReadInt32LE())
    r.ReadInt32LE() |> ignore // max packet size
    r.ReadByte() |> ignore // charset
    r.ReadBytes 23 |> ignore // reserved
    let username = r.ReadNullTerminatedString()

    (if capabilities &&& ClientPluginAuthLenencClientData <> 0u then
         r.ReadLenEncString() |> ignore
     elif capabilities &&& ClientSecureConnection <> 0u then
         let len = int (r.ReadByte())
         r.ReadBytes len |> ignore
     else
         r.ReadNullTerminatedString() |> ignore)

    let database =
        if capabilities &&& ClientConnectWithDb <> 0u && r.Remaining > 0 then
            Some(r.ReadNullTerminatedString())
        else
            None

    { Capabilities = capabilities
      Username = username
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
    | 1064 -> "42000" // ER_PARSE_ERROR
    | 1146 -> "42S02" // ER_NO_SUCH_TABLE
    | 1054 -> "42S22" // ER_BAD_FIELD_ERROR
    | 1047 -> "08S01" // ER_UNKNOWN_COM_ERROR
    | 1048 -> "23000" // ER_BAD_NULL_ERROR
    | 1052 -> "23000" // ER_NON_UNIQ_ERROR
    | 1062 -> "23000" // ER_DUP_ENTRY
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

/// A resultset column: its name and its MySQL wire type — see
/// `Value.mysqlTypeOf` (the data-driven source for most resultsets) and
/// `wireTypeOfColumnType` below (the declared-schema source for the
/// COM_FIELD_LIST path, which has no row data to read a type off of).
type ColumnDef = { Name: string; Type: byte }

let columnDefPayload (col: ColumnDef) : byte[] =
    let w = Writer()
    w.WriteLenEncString "def" // catalog
    w.WriteLenEncString "" // schema
    w.WriteLenEncString "" // table
    w.WriteLenEncString "" // org_table
    w.WriteLenEncString col.Name
    w.WriteLenEncString col.Name // org_name
    w.WriteLenEncInt 0x0cUL // length of fixed-length fields
    w.WriteInt16LE Utf8Mb4GeneralCi
    w.WriteInt32LE 0 // column length
    w.WriteByte col.Type
    w.WriteInt16LE 0 // flags
    w.WriteByte 0uy // decimals
    w.WriteInt16LE 0 // filler
    w.ToArray()

/// Maps a column's *declared* SQL type to its MySQL wire type id — used
/// only by the deprecated COM_FIELD_LIST path (`Server`'s `FieldList`
/// handler), which reads straight off `Storage`'s schema instead of a
/// query result's rows, so there's no `Value` for `Value.mysqlTypeOf` to
/// read a type off of.
let wireTypeOfColumnType (ty: Ast.ColumnType) : byte =
    match ty with
    | Ast.TTinyInt _ -> TypeTiny
    | Ast.TSmallInt _ -> TypeShort
    | Ast.TMediumInt _
    | Ast.TInt _ -> TypeLong
    | Ast.TBigInt _ -> TypeLongLong
    | Ast.TDecimal _ -> TypeNewDecimal
    | Ast.TDouble -> TypeDouble
    | Ast.TFloat -> TypeFloat
    | Ast.TDate -> TypeDate
    | Ast.TDateTime
    | Ast.TTimestamp -> TypeDateTime
    | _ -> TypeVarString

/// Encodes one text-protocol row. None means SQL NULL.
let textRowPayload (values: string option list) : byte[] =
    let w = Writer()

    for v in values do
        match v with
        | None -> w.WriteLenEncNull()
        | Some s -> w.WriteLenEncString s

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
let private writeBinaryValue (w: Writer) (typeId: byte) (s: string) : unit =
    if typeId = TypeLongLong then
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
        w.WriteByte 7uy
        w.WriteInt16LE dt.Year
        w.WriteByte(byte dt.Month)
        w.WriteByte(byte dt.Day)
        w.WriteByte(byte dt.Hour)
        w.WriteByte(byte dt.Minute)
        w.WriteByte(byte dt.Second)
    else
        w.WriteLenEncString s

/// Encodes one binary-protocol resultset row
/// (https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_binary_resultset.html#sect_protocol_binary_resultset_row).
/// `columnTypes` must be the same list `columnDefPayload` advertised each
/// column as (see `writeBinaryValue`'s doc on why); a shorter/longer list
/// than `values` is a caller bug, not a value this falls back for. None
/// means SQL NULL.
let binaryRowPayload (columnTypes: byte list) (values: string option list) : byte[] =
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

    List.zip columnTypes values
    |> List.iter (fun (typeId, v) ->
        match v with
        | Some s -> writeBinaryValue w typeId s
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
        let totalHours = days * 24 + hour
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
    let lenEncText () =
        match r.ReadLenEncInt() with
        | None -> ""
        | Some len -> Encoding.UTF8.GetString(r.ReadBytes(int len))

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
            VDecimal(decimal (BitConverter.ToUInt64(bytes, 0)))
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
        || typeId = TypeBlob
    then
        VString(lenEncText ())
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
