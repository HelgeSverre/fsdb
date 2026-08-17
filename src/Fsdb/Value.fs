/// The runtime value type flowing through expression evaluation, storage,
/// and wire encoding, plus MySQL's (famously loose) comparison, coercion,
/// truthiness, and arithmetic rules as pure functions.
module Fsdb.Value

open System
open System.Globalization
open System.Text.RegularExpressions

type Value =
    | VNull
    | VInt of int64
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
let TypeVarchar = 0x0fuy
let TypeNewDecimal = 0xf6uy
let TypeBlob = 0xfcuy
let TypeVarString = 0xfduy
let TypeString = 0xfeuy

/// Renders a value the way the text resultset protocol does: NULL becomes
/// the lenenc-null marker (`None`), everything else its textual form.
let toText (v: Value) : string option =
    match v with
    | VNull -> None
    | VInt i -> Some(string i)
    // .NET Core's default double ToString is already the shortest
    // round-trippable representation (no "0.1000000000000001" noise).
    | VDouble d -> Some(d.ToString(CultureInfo.InvariantCulture))
    | VDecimal d -> Some(d.ToString(CultureInfo.InvariantCulture))
    | VString s -> Some s
    | VBytes b -> Some(Text.Encoding.Latin1.GetString b)
    | VDate d -> Some(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
    | VDateTime dt -> Some(dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
    | VJson j -> Some j

/// A round-trippable encoding of a `Value` as one tagged, newline-free
/// string — `ofWire (toWire v) = v` for every case. Exists for
/// `Persistence`'s physical WAL/snapshot, which needs its own serialization
/// distinct from `toText`'s display-only rendering: `toText` collapses
/// `VNull` to `None` (indistinguishable from "no column" once flattened into
/// a delimited line) and truncates `VDateTime` to whole seconds (real MySQL
/// datetimes carry microseconds; round-tripping through `"yyyy-MM-dd
/// HH:mm:ss"` would silently drop them on every replay). Strings/bytes/JSON
/// are base64-encoded so the encoded line is safe to store one-per-line (or
/// comma/pipe-joined) regardless of what the value itself contains.
let toWire (v: Value) : string =
    let b64 (s: string) = Convert.ToBase64String(Text.Encoding.UTF8.GetBytes s)

    match v with
    | VNull -> "N"
    | VInt i -> "I" + string i
    | VDouble d -> "D" + d.ToString("R", CultureInfo.InvariantCulture)
    | VDecimal d -> "M" + d.ToString(CultureInfo.InvariantCulture)
    | VString s -> "S" + b64 s
    | VBytes b -> "B" + Convert.ToBase64String b
    | VDate d -> "T" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    // "O" (round-trip) format, not `toText`'s display format — keeps
    // sub-second precision.
    | VDateTime dt -> "V" + dt.ToString("O", CultureInfo.InvariantCulture)
    | VJson j -> "J" + b64 j

/// Inverse of `toWire`. Throws on malformed input — a corrupt WAL line is a
/// startup-time failure to surface loudly, not a value to silently coerce.
let ofWire (s: string) : Value =
    let unb64 (payload: string) = Text.Encoding.UTF8.GetString(Convert.FromBase64String payload)

    if s = "N" then
        VNull
    else
        let payload = s.Substring(1)

        match s.[0] with
        | 'I' -> VInt(Int64.Parse(payload, CultureInfo.InvariantCulture))
        | 'D' -> VDouble(Double.Parse(payload, NumberStyles.Float, CultureInfo.InvariantCulture))
        | 'M' -> VDecimal(Decimal.Parse(payload, CultureInfo.InvariantCulture))
        | 'S' -> VString(unb64 payload)
        | 'B' -> VBytes(Convert.FromBase64String payload)
        | 'T' -> VDate(DateOnly.Parse(payload, CultureInfo.InvariantCulture))
        | 'V' -> VDateTime(DateTime.Parse(payload, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
        | 'J' -> VJson(unb64 payload)
        | tag -> failwithf "Value.ofWire: unknown tag '%c' in %s" tag s

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
let mysqlTypeOf (v: Value) : byte =
    match v with
    // No data to type; NULL round-trips the same regardless of the
    // declared column type, so the caller's fallback (typically
    // VAR_STRING) is as good as anything else here.
    | VNull -> TypeVarString
    | VInt _ -> TypeLongLong
    | VDouble _ -> TypeDouble
    | VDecimal _ -> TypeNewDecimal
    | VString _
    | VJson _ -> TypeVarString
    | VBytes _ -> TypeBlob
    | VDate _ -> TypeDate
    | VDateTime _ -> TypeDateTime

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
/// same way a CHAR/VARCHAR compare trims trailing spaces before comparing.
let private compareStrings (x: string) (y: string) : int =
    String.Compare(x.TrimEnd(' '), y.TrimEnd(' '), StringComparison.OrdinalIgnoreCase) |> sign

/// `VDate`/`VDateTime` as one .NET `DateTime`, midnight for the date-only
/// case — the shared instant `compare`'s VDate/VDateTime-vs-VString branch
/// parses a string bound against.
let private asDateTime (v: Value) : DateTime =
    match v with
    | VDate d -> d.ToDateTime(TimeOnly.MinValue)
    | VDateTime dt -> dt
    | _ -> invalidArg "v" "asDateTime expects VDate or VDateTime"

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
    | VDecimal x, VDecimal y -> Decimal.Compare(x, y)
    | VInt x, VInt y -> Operators.compare x y
    | VString x, VString y -> compareStrings x y
    | VBytes x, VBytes y -> Operators.compare x y
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
    | (VInt _ | VDouble _ | VDecimal _), _
    | _, (VInt _ | VDouble _ | VDecimal _) -> Operators.compare (toDouble a) (toDouble b)
    | _ -> compareStrings (toText a |> Option.defaultValue "") (toText b |> Option.defaultValue "")

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
        | c when c = escapeChar && i + 1 < pattern.Length && (pattern.[i + 1] = '%' || pattern.[i + 1] = '_') ->
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
    | KDecimal of decimal
    | KDouble of float

let private classify (v: Value) : NumKind option =
    match v with
    | VNull -> None
    | VInt i -> Some(KInt i)
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
    | KDecimal d -> float d
    | KDouble d -> d

let private asDecimal =
    function
    | KInt i -> decimal i
    | KDecimal d -> d
    | KDouble d -> decimal d

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
    | Some(KInt x), Some(KInt y) -> VInt(opInt x y)
    | Some ka, Some kb ->
        match ka, kb with
        | KDouble _, _
        | _, KDouble _ -> VDouble(opDbl (asDouble ka) (asDouble kb))
        | _ -> VDecimal(opDec (asDecimal ka) (asDecimal kb))

let add = arith (+) (+) (+)
let sub = arith (-) (-) (-)
let mul = arith ( * ) ( * ) ( * )

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
                let dividendScale = match ka with KDecimal d -> scaleOf d | _ -> 0
                VDecimal(withScale (dividendScale + divPrecisionIncrement) (asDecimal ka / y))

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
        if y = 0m then VNull else VInt(int64 (Math.Truncate(asDecimal ka / y)))
