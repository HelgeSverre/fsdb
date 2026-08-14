/// HandshakeV10, HandshakeResponse41, OK/ERR/EOF packets, and text resultset
/// encoding. https://dev.mysql.com/doc/dev/mysql-server/latest/page_protocol_connection_lifecycle.html
module Fsdb.Protocol

open System.Text
open Fsdb.Packet

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

let private okPayloadWithHeader (header: byte) (capabilities: uint32) (affectedRows: uint64) (lastInsertId: uint64) : byte[] =
    let w = Writer()
    w.WriteByte header
    w.WriteLenEncInt affectedRows
    w.WriteLenEncInt lastInsertId

    if capabilities &&& ClientProtocol41 <> 0u then
        w.WriteInt16LE StatusAutocommit
        w.WriteInt16LE 0 // warnings

    w.ToArray()

/// Builds an OK packet payload (header 0x00). Used for command responses
/// (handshake, COM_QUERY for non-SELECT statements, COM_PING, ...).
let okPayload (capabilities: uint32) (affectedRows: uint64) (lastInsertId: uint64) : byte[] =
    okPayloadWithHeader 0uy capabilities affectedRows lastInsertId

/// Builds the OK packet that terminates a resultset when CLIENT_DEPRECATE_EOF
/// is negotiated. Same shape as `okPayload`, but header 0xfe — clients tell
/// it apart from a row by that header byte together with the packet length,
/// so this can't just reuse okPayload's 0x00.
let okEndOfResultSetPayload (capabilities: uint32) : byte[] =
    okPayloadWithHeader 0xfeuy capabilities 0UL 0UL

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
let eofPayload (capabilities: uint32) : byte[] =
    let w = Writer()
    w.WriteByte 0xfeuy

    if capabilities &&& ClientProtocol41 <> 0u then
        w.WriteInt16LE 0 // warnings
        w.WriteInt16LE StatusAutocommit

    w.ToArray()

/// A resultset column. Every column is reported as MYSQL_TYPE_VAR_STRING —
/// the text protocol sends all values as strings anyway, and clients coerce.
type ColumnDef = { Name: string }

/// MYSQL_TYPE_VAR_STRING
let private ColumnTypeVarString = 0xfduy

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
    w.WriteByte ColumnTypeVarString
    w.WriteInt16LE 0 // flags
    w.WriteByte 0uy // decimals
    w.WriteInt16LE 0 // filler
    w.ToArray()

/// Encodes one text-protocol row. None means SQL NULL.
let textRowPayload (values: string option list) : byte[] =
    let w = Writer()

    for v in values do
        match v with
        | None -> w.WriteLenEncNull()
        | Some s -> w.WriteLenEncString s

    w.ToArray()
