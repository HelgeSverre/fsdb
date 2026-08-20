/// The runtime value type flowing through expression evaluation, storage,
/// and wire encoding, plus MySQL's (famously loose) comparison, coercion,
/// truthiness, and arithmetic rules as pure functions.
module Fsdb.Value

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Fsdb.Binary

type Value =
    | VNull
    | VInt of int64
    /// MySQL's `BIGINT UNSIGNED` domain, whose top half (2^63 .. 2^64-1)
    /// has no `int64` representation — `CAST(-1 AS UNSIGNED)` is
    /// 18446744073709551615, not -1. Only 64-bit unsigned needs its own
    /// case: every narrower unsigned type (`INT UNSIGNED`'s 4294967295 and
    /// down) fits `VInt` exactly.
    | VUInt of uint64
    | VDouble of float
    | VDecimal of decimal
    | VString of string
    | VBytes of byte[]
    | VDate of DateOnly
    | VDateTime of DateTime
    /// Raw JSON text — ponytail: no parsed representation yet, add one
    /// (JVal DU or similar) when JSON_EXTRACT-style path queries land.
    | VJson of string

// MySQL wire protocol column type ids — shared by `Protocol`'s column
// definition packets (a resultset's declared type) and its binary-protocol
// parameter decoding (COM_STMT_EXECUTE's per-param type array). Kept here,
// ahead of both `Executor` and `Protocol` in build order, so `mysqlTypeOf`
// below (used by `Executor` to type a resultset's columns) and `Protocol`
// (used to read/write them on the wire) share one definition instead of
// two copies of the same numeric constants drifting apart.
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
let TypeYear = 0x0duy
let TypeVarchar = 0x0fuy
let TypeNewDecimal = 0xf6uy
let TypeBlob = 0xfcuy
let TypeVarString = 0xfduy
let TypeString = 0xfeuy

let NotNullFlag = 0x0001us
let PrimaryKeyFlag = 0x0002us
let UniqueKeyFlag = 0x0004us
let BlobFlag = 0x0010us
let UnsignedFlag = 0x0020us
let BinaryFlag = 0x0080us
let EnumFlag = 0x0100us
let AutoIncrementFlag = 0x0200us
let SetFlag = 0x0800us

/// The result-column metadata consumed by both the definition packet and
/// binary-row encoder. Keeping these fields together prevents the encoder
/// from disagreeing with the type and flags advertised to the client.
type ColumnMetadata =
    { TypeId: byte
      ColumnLength: uint32
      Flags: uint16
      Decimals: byte }

let columnMetadata typeId =
    { TypeId = typeId
      ColumnLength = 0u
      Flags = 0us
      Decimals = 0uy }

/// .NET's shortest-round-trip double formatting agrees with MySQL on the
/// mantissa but not the exponent: "1E+20" vs MySQL's "1e20" (lowercase,
/// no '+', no zero-padding). Reshapes just the exponent part when present.
let private formatDouble (d: float) : string =
    let s = d.ToString(CultureInfo.InvariantCulture)

    match s.IndexOf 'E' with
    | -1 -> s
    | i ->
        let mantissa = s.Substring(0, i)
        let exp = s.Substring(i + 1)

        let sign, digits =
            if exp.StartsWith "-" then "-", exp.Substring(1) else "", exp.TrimStart '+'

        let digits =
            match digits.TrimStart '0' with
            | "" -> "0"
            | d -> d

        mantissa + "e" + sign + digits

/// Renders a value the way the text resultset protocol does: NULL becomes
/// the lenenc-null marker (`None`), everything else its textual form.
let toText (v: Value) : string option =
    match v with
    | VNull -> None
    | VInt i -> Some(string i)
    | VUInt u -> Some(string u)
    // .NET Core's default double ToString is already the shortest
    // round-trippable representation (no "0.1000000000000001" noise); only
    // the exponent's shape needs reworking to match MySQL's rendering.
    | VDouble d -> Some(formatDouble d)
    | VDecimal d -> Some(d.ToString(CultureInfo.InvariantCulture))
    | VString s -> Some s
    | VBytes b -> Some(Text.Encoding.Latin1.GetString b)
    | VDate d -> Some(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
    | VDateTime dt ->
        // Render sub-second precision (MySQL DATETIME(6)) as exactly six
        // fractional digits when present, none when the value lands on a whole
        // second. Ticks are 100 ns, so the sub-second remainder / 10 is
        // microseconds. The binary protocol encoder keys off this rendering:
        // it only emits its 11-byte, microsecond-bearing form when the
        // rendered string carries the fraction.
        // Current-time sources (`NOW()`, `DEFAULT CURRENT_TIMESTAMP`)
        // truncate to whole seconds so they don't sprout a fraction MySQL's
        // precision-0 NOW() never shows.
        let micros = (dt.Ticks % TimeSpan.TicksPerSecond) / 10L
        let baseStr = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        Some(if micros = 0L then baseStr else sprintf "%s.%06d" baseStr micros)
    | VJson j -> Some j

/// Renders a value at a declared fractional-seconds precision (fsp 0-6),
/// for a column whose schema says `DATETIME(fsp)`/`TIMESTAMP(fsp)` — exactly
/// `fsp` fractional digits, trailing zeros included, so a `DATETIME(6)` on an
/// exact second shows `.000000` and a `DATETIME(0)` shows none (where the
/// bare `VDateTime` alone, via `toText`, can't say how many digits the column
/// wants). The stored value is already rounded to `fsp` at coercion
/// (`Storage.coerceValue`), so the top `fsp` micro-digits are exact — this
/// only chooses how many to show. Any non-`VDateTime` value (a `TIME` column
/// is stored pre-formatted as `VString`; every other type) falls through to
/// `toText`.
let toTextFsp (fsp: int) (v: Value) : string option =
    match v with
    | VDateTime dt ->
        let baseStr = dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)

        if fsp <= 0 then
            Some baseStr
        else
            let micros = (dt.Ticks % TimeSpan.TicksPerSecond) / 10L
            Some(sprintf "%s.%s" baseStr ((sprintf "%06d" micros).Substring(0, min fsp 6)))
    | _ -> toText v

/// A round-trippable tagged-text encoding of a `Value` — `ofWire (toWire v) = v`
/// for every case. Binary persistence uses `encodeValue`; this survives as the
/// human-readable, one-line ASCII form the torture harness hashes rows with
/// (`Harness.rowHash`) and `ValueTests` pins. Strings/bytes/JSON are
/// base64-encoded so the result is safe regardless of what delimiters the
/// caller puts around it.
let toWire (v: Value) : string =
    let b64 (s: string) = Convert.ToBase64String(Text.Encoding.UTF8.GetBytes s)

    match v with
    | VNull -> "N"
    | VInt i -> "I" + string i
    | VUInt u -> "U" + string u
    | VDouble d -> "D" + d.ToString("R", CultureInfo.InvariantCulture)
    | VDecimal d -> "M" + d.ToString(CultureInfo.InvariantCulture)
    | VString s -> "S" + b64 s
    | VBytes b -> "B" + Convert.ToBase64String b
    | VDate d -> "T" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    // "O" (round-trip) format, not `toText`'s display format — keeps
    // sub-second precision.
    | VDateTime dt -> "V" + dt.ToString("O", CultureInfo.InvariantCulture)
    | VJson j -> "J" + b64 j

/// Inverse of `toWire`. Throws on malformed input rather than coercing.
let ofWire (s: string) : Value =
    let unb64 (payload: string) = Text.Encoding.UTF8.GetString(Convert.FromBase64String payload)

    if s = "N" then
        VNull
    else
        let payload = s.Substring(1)

        match s.[0] with
        | 'I' -> VInt(Int64.Parse(payload, CultureInfo.InvariantCulture))
        | 'U' -> VUInt(UInt64.Parse(payload, CultureInfo.InvariantCulture))
        | 'D' -> VDouble(Double.Parse(payload, NumberStyles.Float, CultureInfo.InvariantCulture))
        | 'M' -> VDecimal(Decimal.Parse(payload, CultureInfo.InvariantCulture))
        | 'S' -> VString(unb64 payload)
        | 'B' -> VBytes(Convert.FromBase64String payload)
        | 'T' -> VDate(DateOnly.Parse(payload, CultureInfo.InvariantCulture))
        | 'V' -> VDateTime(DateTime.Parse(payload, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
        | 'J' -> VJson(unb64 payload)
        | tag -> failwithf "Value.ofWire: unknown tag '%c' in %s" tag s

/// Binary encoding of a `Value`, mirroring `toWire`'s tag scheme but
/// length-prefixed rather than base64-encoded — `decodeValue (encodeValue v) = v`
/// for every case. The WAL and the snapshot both use this; `toWire` stays as
/// the human-readable tagged-text rendering (round-trip-tested in `ValueTests`).
let encodeValue (w: Writer) (v: Value) : unit =
    match v with
    | VNull -> w.WriteByte 0x00uy
    | VInt i ->
        w.WriteByte 0x01uy
        w.WriteInt64LE i
    // Same eight bytes as `VInt`, a distinct tag: the payload's *signedness*
    // is what the tag carries, and reinterpreting the bit pattern on the way
    // back is exact for the whole 64-bit range.
    | VUInt u ->
        w.WriteByte 0x09uy
        w.WriteInt64LE(int64 u)
    | VDouble d ->
        w.WriteByte 0x02uy
        w.WriteDoubleLE d
    | VDecimal d ->
        w.WriteByte 0x03uy
        let bits = Decimal.GetBits d
        for i in 0..3 do
            w.WriteInt32LE bits.[i]
    | VString s ->
        w.WriteByte 0x04uy
        w.WriteLenEncString s
    | VBytes b ->
        w.WriteByte 0x05uy
        w.WriteLenEncBytes b
    | VDate d ->
        w.WriteByte 0x06uy
        w.WriteInt32LE d.DayNumber
    | VDateTime dt ->
        w.WriteByte 0x07uy
        w.WriteInt64LE dt.Ticks
        w.WriteByte(byte (int dt.Kind))
    | VJson j ->
        w.WriteByte 0x08uy
        w.WriteLenEncString j

/// Inverse of `encodeValue`. Throws on malformed input, same contract as
/// `ofWire`.
let decodeValue (r: #IReader) : Value =
    match r.ReadByte() with
    | 0x00uy -> VNull
    | 0x01uy -> VInt(r.ReadInt64LE())
    | 0x09uy -> VUInt(uint64 (r.ReadInt64LE()))
    | 0x02uy -> VDouble(BitConverter.Int64BitsToDouble(r.ReadInt64LE()))
    | 0x03uy ->
        let bits = [| for _ in 0..3 -> r.ReadInt32LE() |]
        VDecimal(new decimal (bits))
    | 0x04uy -> VString(r.ReadLenEncString() |> Option.defaultValue "")
    | 0x05uy ->
        r.ReadLenEncInt()
        |> Option.map (fun n -> r.ReadBytes(int n))
        |> Option.defaultValue [||]
        |> VBytes
    | 0x06uy -> VDate(DateOnly.FromDayNumber(r.ReadInt32LE()))
    | 0x07uy ->
        let ticks = r.ReadInt64LE()

        let kind =
            match r.ReadByte() with
            | 0uy -> DateTimeKind.Unspecified
            | 1uy -> DateTimeKind.Utc
            | _ -> DateTimeKind.Local

        VDateTime(new DateTime(ticks, kind))
    | 0x08uy -> VJson(r.ReadLenEncString() |> Option.defaultValue "")
    | tag -> failwithf "Value.decodeValue: unknown tag 0x%02x" tag

/// The MySQL wire type this value's runtime shape reports as, so a
/// resultset's column definition can match the value instead of a blanket
/// VAR_STRING (see the `ponytail` history on `Protocol.columnDefPayload`).
/// This matters for real client drivers: PHP's mysqlnd, in particular,
/// auto-converts a LONGLONG/DOUBLE/DATE/DATETIME-typed column to a native
/// int/float/string even over the text protocol, based on this byte —
/// app code that does `$model->foo_id === $other->id` only gets the
/// native-int conversion real MySQL gives it if fsdb reports the same
/// type real MySQL would, instead of leaving every column a string for
/// the client to coerce.
let mysqlMetadataOf (v: Value) : ColumnMetadata =
    match v with
    // No data to type; NULL round-trips the same regardless of the
    // declared column type, so the caller's fallback (typically
    // VAR_STRING) is as good as anything else here.
    | VNull -> columnMetadata TypeVarString
    | VInt _ -> columnMetadata TypeLongLong
    | VUInt _ -> { columnMetadata TypeLongLong with Flags = UnsignedFlag }
    | VDouble _ -> columnMetadata TypeDouble
    | VDecimal _ -> columnMetadata TypeNewDecimal
    | VString _
    | VJson _ -> columnMetadata TypeVarString
    | VBytes _ -> { columnMetadata TypeBlob with Flags = BlobFlag ||| BinaryFlag }
    | VDate _ -> columnMetadata TypeDate
    | VDateTime _ -> columnMetadata TypeDateTime

let mysqlTypeOf (v: Value) : byte = (mysqlMetadataOf v).TypeId

/// Matches the leading numeric prefix of a string the way MySQL's
/// string-to-number cast does (`'12abc' + 0` = 12, `'abc' + 0` = 0),
/// scanning as much of an optionally-signed float as it can rather than
/// requiring the whole string to be numeric.
let private leadingNumeric =
    Regex(@"^\s*[-+]?(\d+\.\d*|\.\d+|\d+)([eE][-+]?\d+)?")

let private parseLeadingNumeric (s: string) : float =
    let m = leadingNumeric.Match s

    if m.Success then
        match Double.TryParse(m.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, d -> d
        | false, _ -> 0.0
    else
        0.0

/// MySQL's implicit numeric coercion, used by both comparison and
/// arithmetic: numeric types convert directly, strings parse their leading
/// numeric prefix, and anything else coerces through its text rendering.
let toDouble (v: Value) : float =
    match v with
    | VNull -> 0.0
    | VInt i -> float i
    | VUInt u -> float u
    | VDouble d -> d
    | VDecimal d -> float d
    | VString s -> parseLeadingNumeric s
    | VBytes _
    | VDate _
    | VDateTime _
    | VJson _ -> v |> toText |> Option.map parseLeadingNumeric |> Option.defaultValue 0.0

/// String comparison matching MySQL 8's default collation,
/// utf8mb4_0900_ai_ci: case-insensitive ("ai" is accent-insensitive, which
/// .NET's OrdinalIgnoreCase doesn't model — ponytail: ASCII/Latin case
/// folding only, add real accent folding if a _ai collation edge case
/// actually matters) and PAD SPACE-insensitive, so `'a' = 'a '` is true the
/// MySQL 8's default collation (utf8mb4_0900_ai_ci) — accent- and
/// case-insensitive, NO PAD (trailing spaces significant) — delegated to
/// the one home for those rules, `Collation`. This is the *folded* order
/// (accent/case-only differences compare equal, returning 0) because
/// `compare` drives equality everywhere — `equals`, hash-join keys, unique
/// lookups. `ORDER BY`'s tie-breaks among equal-primary strings use
/// `compareTotal` instead.
let private compareStrings (x: string) (y: string) : int =
    Collation.defaultCollation.ComparePrimary x y |> sign

/// Byte-lexicographic (memcmp-style) order for `VARBINARY`/`BLOB` content:
/// compare byte-by-byte, shorter-is-less only once every shared prefix byte
/// ties. F#'s structural `compare` on `byte[]` compares length first, which
/// puts `UNHEX('02')` (length 1) before `UNHEX('0101')` (length 2) even
/// though byte 0x02 is greater than byte 0x01 — wrong versus MySQL's binary
/// collation.
let private compareBytesLex (x: byte[]) (y: byte[]) : int =
    let len = min x.Length y.Length
    let mutable i = 0
    let mutable result = 0

    while result = 0 && i < len do
        result <- Operators.compare x.[i] y.[i]
        i <- i + 1

    if result <> 0 then result else Operators.compare x.Length y.Length

/// `VDate`/`VDateTime` as one .NET `DateTime`, midnight for the date-only
/// case — the shared instant `compare`'s VDate/VDateTime-vs-VString branch
/// parses a string bound against.
let private asDateTime (v: Value) : DateTime =
    match v with
    | VDate d -> d.ToDateTime(TimeOnly.MinValue)
    | VDateTime dt -> dt
    | _ -> invalidArg "v" "asDateTime expects VDate or VDateTime"

/// MySQL's JSON comparison precedence, ascending (the manual lists it
/// descending, highest first): JSON NULL < number < string < object <
/// array < boolean < date < time < datetime < opaque < blob. The *type*
/// decides the order before the content does, and comparing a JSON value
/// against a non-JSON one converts the non-JSON side to JSON first — which
/// is why `JSON_EXTRACT('{"n":1}','$.n') = '1'` is FALSE (JSON number vs
/// JSON string) while `= 1` is TRUE, and why the rendered-text comparison
/// this replaced got `'{"s":"abc"}'->'$.s' = 'abc'` wrong (it compared the
/// quoted `"abc"` against the bare `abc`).
/// https://dev.mysql.com/doc/refman/8.4/en/json.html#json-comparison
///
/// ponytail: TIME and OPAQUE have no `Value` case to reach them, and
/// fsdb has no BIT type, so those ranks are unreachable placeholders —
/// widen when those types land.
let private jsonRankOfNode (node: JsonNode) : int =
    if isNull (box node) then
        0
    else
        match node.GetValueKind() with
        | JsonValueKind.Null -> 0
        | JsonValueKind.Number -> 1
        | JsonValueKind.String -> 2
        | JsonValueKind.Object -> 3
        | JsonValueKind.Array -> 4
        | JsonValueKind.True
        | JsonValueKind.False -> 5
        | _ -> 2

/// A `Value` as the (rank, node) pair `compareJson` orders by. Types with
/// no JSON scalar shape (dates, binary) keep their SQL rank and compare
/// against their own kind through `compare`'s ordinary rules, so the node
/// is unused for them.
let private asJsonOperand (v: Value) : int * JsonNode =
    let node (s: string) =
        try
            JsonNode.Parse s
        with _ ->
            JsonValue.Create s

    match v with
    | VJson j -> let n = node j in jsonRankOfNode n, n
    | VInt i -> 1, JsonValue.Create i
    | VUInt u -> 1, JsonValue.Create u
    | VDouble d -> 1, JsonValue.Create d
    | VDecimal d -> 1, JsonValue.Create d
    | VString s -> 2, JsonValue.Create s
    | VDate _ -> 6, null
    | VDateTime _ -> 8, null
    | VBytes _ -> 11, null
    | VNull -> 0, null

/// A JSON number's exact value where `decimal` can hold it (so two BIGINTs
/// past 2^53 stay distinct), its `double` otherwise.
let private jsonNumber (node: JsonNode) : Choice<decimal, float> =
    let text = node.ToJsonString()

    match Decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, d -> Choice1Of2 d
    | false, _ ->
        match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, d -> Choice2Of2 d
        | false, _ -> Choice2Of2 0.0

/// Orders two same-ranked JSON nodes. JSON strings compare by code unit
/// (binary, NOT the ai_ci collation SQL strings use — the oracle says
/// `CAST('"a"' AS JSON) = CAST('"A"' AS JSON)` is 0), arrays compare
/// element-wise then by length, and objects compare by their sorted keys
/// then the values under them.
let rec private compareJsonNodes (x: JsonNode) (y: JsonNode) : int =
    match jsonRankOfNode x with
    | 0 -> 0
    | 1 ->
        match jsonNumber x, jsonNumber y with
        | Choice1Of2 a, Choice1Of2 b -> Decimal.Compare(a, b)
        | a, b ->
            let asFloat = function
                | Choice1Of2 (d: decimal) -> float d
                | Choice2Of2 f -> f

            Operators.compare (asFloat a) (asFloat b)
    | 2 -> String.CompareOrdinal(x.GetValue<string>(), y.GetValue<string>()) |> sign
    | 5 -> Operators.compare (x.GetValue<bool>()) (y.GetValue<bool>())
    | 4 ->
        let a = x :?> JsonArray
        let b = y :?> JsonArray

        Seq.zip a b
        |> Seq.tryPick (fun (l, r) -> match compareJsonNodes l r with 0 -> None | c -> Some c)
        |> Option.defaultWith (fun () -> Operators.compare a.Count b.Count)
    | 3 ->
        let keys (o: JsonObject) = o |> Seq.map _.Key |> Seq.sortWith (fun l r -> String.CompareOrdinal(l, r)) |> List.ofSeq
        let a = x :?> JsonObject
        let b = y :?> JsonObject
        let ka, kb = keys a, keys b

        match Operators.compare ka kb with
        | 0 ->
            ka
            |> List.tryPick (fun k -> match compareJsonNodes a.[k] b.[k] with 0 -> None | c -> Some c)
            |> Option.defaultValue 0
        | c -> c
    | _ -> 0

/// Total order over values for ORDER BY: NULL sorts first, numbers compare
/// numerically (a number vs. a string coerces the string to a double, so
/// `'10' < '9'` numerically even though it's false as a string compare),
/// same-typed values compare natively (strings per `compareStrings`'s
/// collation), and anything else falls back to a text compare.
let rec compare (a: Value) (b: Value) : int =
    match a, b with
    | VNull, VNull -> 0
    | VNull, _ -> -1
    | _, VNull -> 1
    // A JSON operand pulls the whole comparison into the JSON domain (see
    // `jsonRankOfNode`): type precedence first, content second.
    | VJson _, _
    | _, VJson _ ->
        let ra, na = asJsonOperand a
        let rb, nb = asJsonOperand b

        match Operators.compare ra rb with
        // Ranks that carry no JSON node (dates, binary) still have to order
        // among themselves; their SQL comparison is the same order.
        | 0 when ra >= 6 -> compareStrings (toText a |> Option.defaultValue "") (toText b |> Option.defaultValue "")
        | 0 -> compareJsonNodes na nb
        | c -> c
    | VDecimal x, VDecimal y -> Decimal.Compare(x, y)
    | VInt x, VInt y -> Operators.compare x y
    // The unsigned 64-bit domain and the signed one only overlap on
    // [0, 2^63); `decimal` holds both exactly, so promoting is the one
    // comparison that stays right at both ends (`toDouble` would merge
    // distinct values past 2^53, and a naive `int64`/`uint64` cast would
    // make -1 the largest value there is).
    | VUInt x, VUInt y -> Operators.compare x y
    | VUInt x, VInt y -> Decimal.Compare(decimal x, decimal y)
    | VInt x, VUInt y -> Decimal.Compare(decimal x, decimal y)
    | VUInt x, VDecimal y -> Decimal.Compare(decimal x, y)
    | VDecimal x, VUInt y -> Decimal.Compare(x, decimal y)
    | VString x, VString y -> compareStrings x y
    | VBytes x, VBytes y -> compareBytesLex x y
    // A binary string against a character string compares byte-for-byte
    // (MySQL: `CONVERT('abc' USING binary) = 'ABC'` is false), not via the
    // character collation the generic text fallback below would apply.
    | VBytes x, VString s -> compareBytesLex x (Text.Encoding.UTF8.GetBytes s)
    | VString s, VBytes y -> compareBytesLex (Text.Encoding.UTF8.GetBytes s) y
    | VDate x, VDate y -> Operators.compare x y
    | VDateTime x, VDateTime y -> Operators.compare x y
    | VDate x, VDateTime y -> Operators.compare (x.ToDateTime(TimeOnly.MinValue)) y
    | VDateTime x, VDate y -> Operators.compare x (y.ToDateTime(TimeOnly.MinValue))
    | (VDate _ | VDateTime _), VString s ->
        // A literal like a `WHERE date BETWEEN '2024-01-01 00:00:00' AND
        // ...` bound is still a bare VString here (nothing coerces it to the
        // column's type ahead of the comparison) — parsed as a real instant
        // and compared temporally when it looks like one, the same as real
        // MySQL's DATE/DATETIME-vs-string coercion. Unparseable text falls
        // back to a text compare rather than erroring, matching every other
        // case here. Without this, `VDate "2024-01-01"`.`toText` ("2024-01-01",
        // no time part) sorted *before* "2024-01-01 00:00:00" as plain text —
        // a same-day BETWEEN lower bound excluded rows it should include.
        match DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, dt -> Operators.compare (asDateTime a) dt
        | false, _ -> compareStrings (toText a |> Option.defaultValue "") s
    | VString _, (VDate _ | VDateTime _) -> -(compare b a)
    // BIGINT vs DECIMAL with neither side a DOUBLE: promote both to
    // `decimal` and compare exactly. Routing this through `toDouble`
    // (float has only 53 bits of mantissa) silently merges distinct
    // int64/decimal values above 2^53 — wrong for equality, hash-join
    // keys, and unique-index lookups alike.
    | VInt x, VDecimal y -> Decimal.Compare(decimal x, y)
    | VDecimal x, VInt y -> Decimal.Compare(x, decimal y)
    | (VInt _ | VUInt _ | VDouble _ | VDecimal _), _
    | _, (VInt _ | VUInt _ | VDouble _ | VDecimal _) -> Operators.compare (toDouble a) (toDouble b)
    | _ -> compareStrings (toText a |> Option.defaultValue "") (toText b |> Option.defaultValue "")

/// The `ORDER BY` total order: `compare` first (folded — accent/case-only
/// differences tie at 0), then the collation's full secondary/tertiary
/// weights break the tie, exactly as MySQL's ai_ci sorts equal-primary
/// strings. Only the sort sites use this; equality/joins/keys keep `compare`.
let compareTotal (a: Value) (b: Value) : int =
    match a, b with
    | VString x, VString y ->
        match Collation.defaultCollation.ComparePrimary x y with
        | 0 -> Collation.defaultCollation.Compare x y |> sign
        | c -> sign c
    | _ -> compare a b

/// Translates a SQL LIKE pattern to a .NET regex source: `%` -> `.*`, `_` ->
/// `.`, and an escape-prefixed `%`/`_` (backslash by default, or whatever an
/// `ESCAPE '<c>'` clause names — see `escapeChar`) matches that character
/// literally instead. Anchored with `\A`/`\z` rather than `^`/`$` — `$`
/// alone matches before a trailing newline, which would let `'ab\n' LIKE
/// 'ab'` falsely match — and callers must pass `RegexOptions.Singleline` so
/// `.` spans newlines too (`%`/`_` are unqualified wildcards in MySQL, not
/// "everything but a newline"). One definition shared by `Executor`'s `LIKE`
/// operator, `QueryHandler`'s `SHOW ... LIKE`, and `Functions`'s
/// `JSON_SEARCH`, instead of three copies of the same escaping rules.
let likeToRegexWith (escapeChar: char) (pattern: string) : string =
    let sb = Text.StringBuilder()
    let mutable i = 0

    while i < pattern.Length do
        match pattern.[i] with
        // An escape-prefixed char matches literally, whatever it is. This
        // covers the escape char escaping itself (`\\` in the pattern
        // means one literal backslash) and `\<ordinary char>`, both of
        // which MySQL's LIKE also treats as that literal char.
        | c when c = escapeChar && i + 1 < pattern.Length ->
            sb.Append(Regex.Escape(string pattern.[i + 1])) |> ignore
            i <- i + 2
        | '%' ->
            sb.Append(".*") |> ignore
            i <- i + 1
        | '_' ->
            sb.Append(".") |> ignore
            i <- i + 1
        | c ->
            sb.Append(Regex.Escape(string c)) |> ignore
            i <- i + 1

    @"\A" + sb.ToString() + @"\z"

/// `LIKE` without an `ESCAPE` clause: MySQL's default escape is backslash,
/// which is also what `Parser.stringChar` leaves unresolved in the pattern
/// string for this purpose.
let likeToRegex (pattern: string) : string = likeToRegexWith '\\' pattern

/// WHERE-style equality with MySQL's implicit coercion (`1 = '1'` is true).
/// Three-valued logic: NULL never equals anything, including another NULL.
let equals (a: Value) (b: Value) : bool option =
    match a, b with
    | VNull, _
    | _, VNull -> None
    | _ -> Some(compare a b = 0)

/// A value's truth in a boolean context (WHERE/IF/AND/OR): NULL is unknown,
/// otherwise MySQL treats "coerces to zero" as false.
let truthy (v: Value) : bool option =
    match v with
    | VNull -> None
    | _ -> Some(toDouble v <> 0.0)

/// The three numeric kinds arithmetic promotes between; anything
/// non-numeric coerces through `toDouble` like MySQL's implicit cast.
type private NumKind =
    | KInt of int64
    /// `BIGINT UNSIGNED`. Kept apart from `KInt` because MySQL's promotion
    /// rules make an unsigned operand win over a signed one (`+`/`-`/`*`/
    /// `MOD` on unsigned-and-signed yield unsigned), not because the
    /// arithmetic itself differs — that runs in `decimal` either way.
    | KUInt of uint64
    | KDecimal of decimal
    | KDouble of float

let private classify (v: Value) : NumKind option =
    match v with
    | VNull -> None
    | VInt i -> Some(KInt i)
    | VUInt u -> Some(KUInt u)
    | VDecimal d -> Some(KDecimal d)
    | VDouble d -> Some(KDouble d)
    | VString _
    | VBytes _
    | VDate _
    | VDateTime _
    | VJson _ -> Some(KDouble(toDouble v))

let private asDouble =
    function
    | KInt i -> float i
    | KUInt u -> float u
    | KDecimal d -> float d
    | KDouble d -> d

let private asDecimal =
    function
    | KInt i -> decimal i
    | KUInt u -> decimal u
    | KDecimal d -> d
    | KDouble d -> decimal d

/// The largest `BIGINT UNSIGNED`, as a `decimal` — the ceiling the exact
/// integral operations narrow back through.
let private maxUInt64 = decimal UInt64.MaxValue

/// Narrows an exact `decimal` result of unsigned-domain arithmetic back to
/// `VUInt` when it lands inside `BIGINT UNSIGNED`, keeping the operation's
/// unsigned result type the way MySQL does (`unsigned - signed` is
/// unsigned), and refusing outright when it doesn't: MySQL raises 1690 for
/// `CAST(1 AS UNSIGNED) - 2` and `CAST(-1 AS UNSIGNED) * 2` rather than
/// answering in a wider type, and a returned `DECIMAL` there is a wrong
/// answer a caller cannot tell from a right one.
///
/// An exception, not a `Result`: `Value`'s arithmetic has no error channel
/// and every operator plus every aggregate would have to grow one.
/// `QueryHandler.handle` turns this into the ERR packet.
///
/// A non-integral `decimal` (a fractional intermediate inside a still-exact
/// promotion) is in-domain and just stays a `DECIMAL`.
exception UnsignedOutOfRange

let narrowUnsigned (d: decimal) : Value =
    if d >= 0m && d <= maxUInt64 then
        if Decimal.Truncate d = d then VUInt(uint64 d) else VDecimal d
    else
        raise UnsignedOutOfRange

/// MySQL arithmetic type promotion: int op int stays int; decimal involved
/// (with no double operand) promotes to decimal; a double operand (or a
/// non-numeric one, which coerces through double) promotes to double.
/// NULL propagates through any operand, per SQL's `NULL + 1 = NULL`.
let private arith
    (opInt: int64 -> int64 -> int64)
    (opDec: decimal -> decimal -> decimal)
    (opDbl: float -> float -> float)
    (a: Value)
    (b: Value)
    : Value =
    match classify a, classify b with
    | None, _
    | _, None -> VNull
    | Some(KInt x), Some(KInt y) ->
        // MySQL errors (1690) on int64 overflow rather than wrapping to a
        // bogus negative; there's no error channel to `Value` arithmetic
        // here, so the safe compat move is to promote to `decimal` (exact,
        // just like MySQL's own DECIMAL fallback for oversized literals).
        try
            VInt(opInt x y)
        with :? OverflowException ->
            VDecimal(opDec (decimal x) (decimal y))
    // An unsigned operand against any exact integral one keeps MySQL's
    // unsigned result type. The arithmetic itself runs in `decimal`, which
    // covers the whole [0, 2^64) domain exactly and — unlike `uint64` —
    // survives a negative intermediate without wrapping.
    | Some(KUInt _ as ka), Some((KInt _ | KUInt _) as kb)
    | Some((KInt _) as ka), Some((KUInt _) as kb) ->
        try
            narrowUnsigned (opDec (asDecimal ka) (asDecimal kb))
        with :? OverflowException ->
            VDouble(opDbl (asDouble ka) (asDouble kb))
    | Some ka, Some kb ->
        match ka, kb with
        | KDouble _, _
        | _, KDouble _ -> VDouble(opDbl (asDouble ka) (asDouble kb))
        | _ -> VDecimal(opDec (asDecimal ka) (asDecimal kb))

let add = arith (Checked.(+)) (+) (+)
let sub = arith (Checked.(-)) (-) (-)
let mul = arith (Checked.( * )) ( * ) ( * )

/// A `decimal`'s own scale (digits after the point) as constructed —
/// `10.00m` reports 2, `5m` reports 0 — read straight out of its bit
/// representation the way `System.Decimal` stores it (bits 16-23 of the
/// fourth `int32`), since the BCL exposes no `.Scale` property.
let private scaleOf (d: decimal) : int = (Decimal.GetBits d).[3] >>> 16 &&& 0xFF

/// `/`'s `div_precision_increment`, MySQL's fixed default (`SELECT
/// @@div_precision_increment` is 4 on a stock install, and MySQL doesn't
/// let a plain expression see a session override reflected in its own
/// scale math the way this constant does) — the extra fractional digits
/// `/` adds on top of the dividend's own scale.
let private divPrecisionIncrement = 4

/// Rounds to `scale` fractional digits *and* pads short results back out to
/// it (`Math.Round(5m, 4)` is still `5m`, not `5.0000m`) — `decimal`
/// remembers trailing zeros baked into its own scale, so round-tripping
/// through a fixed-point format string is what actually forces it.
let private withScale (scale: int) (d: decimal) : decimal =
    // `decimal` only carries 28-29 significant digits; a dividend with a
    // large scale plus `divPrecisionIncrement` can ask for more fractional
    // digits than fit alongside its integer part. Clamp to `decimal`'s max
    // scale rather than let `Math.Round`/`Decimal.Parse` throw (MySQL error
    // 1105) — callers treat this as best-effort formatting, not validation.
    let scale = max 0 (min 28 scale)
    let rounded = Math.Round(d, scale, MidpointRounding.AwayFromZero)
    Decimal.Parse(rounded.ToString("F" + string scale, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)

/// `/`: MySQL divides exact-value operands (INT/DECIMAL) to a `DECIMAL`
/// whose scale is the *dividend's* scale plus `div_precision_increment`
/// — `5/2` is `2.5000`, `10.00/3` is `3.333333` (2 + 4) — rather than
/// truncating to either input's scale the way `+`/`-`/`*` do. A `DOUBLE`
/// operand anywhere taints the result to `DOUBLE` instead (no fixed
/// scale to pad), and division by zero is `NULL`, not an exception, for
/// either shape.
let div (a: Value) (b: Value) : Value =
    match classify a, classify b with
    | None, _
    | _, None -> VNull
    | Some ka, Some kb ->
        match ka, kb with
        | KDouble _, _
        | _, KDouble _ ->
            let y = asDouble kb
            if y = 0.0 then VNull else VDouble(asDouble ka / y)
        | _ ->
            let y = asDecimal kb
            if y = 0m then
                VNull
            else
                try
                    let dividendScale = match ka with KDecimal d -> scaleOf d | _ -> 0
                    VDecimal(withScale (dividendScale + divPrecisionIncrement) (asDecimal ka / y))
                with :? OverflowException ->
                    VNull

/// `MOD`/`%`: ordinary MySQL numeric promotion, same as `+`/`-`/`*` (int
/// op int stays int; a `DECIMAL` operand with no `DOUBLE` promotes to
/// `DECIMAL`, keeping `%`'s natural scale rather than `/`'s
/// `div_precision_increment` bump; a `DOUBLE` operand taints to
/// `DOUBLE`), except a zero divisor is `NULL` rather than a `DivideByZeroException`.
let modulo (a: Value) (b: Value) : Value =
    match classify a, classify b with
    | None, _
    | _, None -> VNull
    | Some ka, Some kb ->
        match ka, kb with
        | KInt x, KInt y -> if y = 0L then VNull else VInt(x % y)
        // Unsigned wins the same way `+`/`-`/`*` promote it.
        | (KUInt _, (KInt _ | KUInt _) | KInt _, KUInt _) ->
            let y = asDecimal kb
            if y = 0m then VNull else narrowUnsigned (asDecimal ka % y)
        | KDouble _, _
        | _, KDouble _ ->
            let y = asDouble kb
            if y = 0.0 then VNull else VDouble(asDouble ka % y)
        | _ ->
            let y = asDecimal kb
            if y = 0m then VNull else VDecimal(asDecimal ka % y)

/// `DIV`: MySQL's integer-division operator — always an `INT` (or `NULL`),
/// truncated toward zero, regardless of whether either operand is a
/// float/decimal (`7.5 DIV 2` is `3`, not `4`; MySQL doesn't pre-round the
/// operands, it truncates the quotient).
let intDiv (a: Value) (b: Value) : Value =
    match classify a, classify b with
    | None, _
    | _, None -> VNull
    | Some ka, Some kb ->
        let y = asDecimal kb

        if y = 0m then
            VNull
        else
            // Both the decimal division and the narrowing to int64 can
            // overflow (a huge dividend, or a divisor near zero); MySQL
            // errors (1105/1690) here, and the domain-appropriate stand-in
            // with no error channel is NULL rather than an internal crash.
            try
                let quotient = Math.Truncate(asDecimal ka / y)

                // `BIGINT UNSIGNED DIV` stays unsigned, so a quotient in the
                // top half of the range (`CAST(-1 AS UNSIGNED) DIV 1`) has
                // to survive rather than overflow `int64` into NULL.
                match ka, kb with
                | (KUInt _, _ | _, KUInt _) -> narrowUnsigned quotient
                | _ -> VInt(int64 quotient)
            with :? OverflowException ->
                VNull
