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

/// Total order over values for ORDER BY: NULL sorts first, numbers compare
/// numerically (a number vs. a string coerces the string to a double, so
/// `'10' < '9'` numerically even though it's false as a string compare),
/// same-typed values compare natively (strings per `compareStrings`'s
/// collation), and anything else falls back to a text compare.
let compare (a: Value) (b: Value) : int =
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
    | (VInt _ | VDouble _ | VDecimal _), _
    | _, (VInt _ | VDouble _ | VDecimal _) -> Operators.compare (toDouble a) (toDouble b)
    | _ -> compareStrings (toText a |> Option.defaultValue "") (toText b |> Option.defaultValue "")

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

/// Division always yields a double (or NULL for `x / NULL`), and MySQL
/// returns NULL rather than raising on divide-by-zero.
let div (a: Value) (b: Value) : Value =
    match classify a, classify b with
    | None, _
    | _, None -> VNull
    | Some ka, Some kb ->
        let y = asDouble kb
        if y = 0.0 then VNull else VDouble(asDouble ka / y)
