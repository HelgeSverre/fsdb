/// Built-in functional keys have one identity across parsing, planning,
/// maintenance, and metadata so adding a transform cannot update only part of
/// the index pipeline.
module internal Fsdb.Sql.FunctionalIndex

open System
open System.Globalization
open Fsdb.Ast
open Fsdb.Value

exception DoubleOutOfRange of string
exception InvalidLogarithmArgument

type private Builtin =
    { CanonicalName: string
      Aliases: string list
      Transform: IndexTransform }

let private definitions =
    [ { CanonicalName = "LOWER"
        Aliases = [ "LCASE" ]
        Transform = Lowercase }
      { CanonicalName = "UPPER"
        Aliases = [ "UCASE" ]
        Transform = Uppercase }
      { CanonicalName = "TRIM"
        Aliases = []
        Transform = Trimmed }
      { CanonicalName = "REVERSE"
        Aliases = []
        Transform = Reversed }
      { CanonicalName = "CHAR_LENGTH"
        Aliases = [ "CHARACTER_LENGTH" ]
        Transform = CharacterLength }
      { CanonicalName = "LENGTH"
        Aliases = [ "OCTET_LENGTH" ]
        Transform = ByteLength }
      { CanonicalName = "BIT_LENGTH"
        Aliases = []
        Transform = BitLength }
      { CanonicalName = "ASCII"
        Aliases = []
        Transform = FirstByte }
      { CanonicalName = "ORD"
        Aliases = []
        Transform = FirstCharacterCode }
      { CanonicalName = "UNHEX"
        Aliases = []
        Transform = DecodedHex }
      { CanonicalName = "HEX"
        Aliases = []
        Transform = EncodedHex }
      { CanonicalName = "MD5"
        Aliases = []
        Transform = Md5Digest }
      { CanonicalName = "SHA1"
        Aliases = [ "SHA" ]
        Transform = Sha1Digest }
      { CanonicalName = "ABS"
        Aliases = []
        Transform = AbsoluteValue }
      { CanonicalName = "ISNULL"
        Aliases = []
        Transform = IsNullResult }
      { CanonicalName = "SIGN"
        Aliases = []
        Transform = Signum }
      { CanonicalName = "BIT_COUNT"
        Aliases = []
        Transform = BitCounted }
      { CanonicalName = "FLOOR"
        Aliases = []
        Transform = Floored }
      { CanonicalName = "CEIL"
        Aliases = [ "CEILING" ]
        Transform = Ceiled }
      { CanonicalName = "ROUND"
        Aliases = []
        Transform = Rounded }
      { CanonicalName = "SQRT"
        Aliases = []
        Transform = SquareRooted }
      { CanonicalName = "EXP"
        Aliases = []
        Transform = Exponentiated }
      { CanonicalName = "SIN"
        Aliases = []
        Transform = Sine }
      { CanonicalName = "COS"
        Aliases = []
        Transform = Cosine }
      { CanonicalName = "TAN"
        Aliases = []
        Transform = Tangent }
      { CanonicalName = "ASIN"
        Aliases = []
        Transform = ArcSine }
      { CanonicalName = "ACOS"
        Aliases = []
        Transform = ArcCosine }
      { CanonicalName = "ATAN"
        Aliases = [ "ATAN2" ]
        Transform = ArcTangent }
      { CanonicalName = "COT"
        Aliases = []
        Transform = Cotangent }
      { CanonicalName = "DEGREES"
        Aliases = []
        Transform = Degrees }
      { CanonicalName = "RADIANS"
        Aliases = []
        Transform = Radians }
      // MySQL stores LOG and LN as distinct functional expressions.
      { CanonicalName = "LOG"
        Aliases = []
        Transform = Logarithm }
      { CanonicalName = "LN"
        Aliases = []
        Transform = NaturalLogarithm }
      { CanonicalName = "LOG2"
        Aliases = []
        Transform = BinaryLogarithm }
      { CanonicalName = "LOG10"
        Aliases = []
        Transform = DecimalLogarithm } ]

let private namesOf definition =
    definition.CanonicalName :: definition.Aliases

let names transform =
    definitions
    |> List.tryFind (fun definition -> definition.Transform = transform)
    |> Option.map namesOf
    |> Option.defaultValue []

let builtins =
    definitions
    |> List.collect (fun definition ->
        namesOf definition |> List.map (fun name -> name, definition.Transform))

let tryBuiltin (name: string) =
    builtins
    |> List.tryPick (fun (candidate, transform) ->
        if name.Equals(candidate, StringComparison.OrdinalIgnoreCase) then Some transform else None)

let tryBuiltinName transform =
    definitions
    |> List.tryPick (fun definition ->
        if definition.Transform = transform then Some definition.CanonicalName else None)

type PhysicalExpression =
    { Qualifier: string option
      Column: string
      Calls: (string * IndexTransform) list
      Transform: IndexTransform }

let private canonicalExpression (column: string) (calls: (string * IndexTransform) list) =
    calls
    |> List.fold
        (fun expression (_, transform) ->
            let name = transform |> tryBuiltinName |> Option.defaultWith (fun () -> invalidArg (nameof transform) "Not a built-in index transform")
            FuncCall(name, [ expression ]))
        (Col(column.ToLowerInvariant()))

let tryPhysicalExpression expression =
    let rec collect calls = function
        | Col column -> Some(None, column, calls)
        | QualifiedCol(qualifier, column) -> Some(Some qualifier, column, calls)
        | FuncCall(name, [ argument ]) ->
            tryBuiltin name
            |> Option.bind (fun transform -> collect ((name, transform) :: calls) argument)
        | _ -> None

    collect [] expression
    |> Option.bind (fun (qualifier, column, calls) ->
        match calls with
        | [] -> None
        | calls ->
            Some
                { Qualifier = qualifier
                  Column = column
                  Calls = calls
                  Transform = Expression(canonicalExpression column calls) })

let tryKeyPart (column: IndexColumn) =
    match column.Transform with
    | Some(Expression expression) ->
        tryPhysicalExpression expression
        |> Option.filter (fun physical -> physical.Qualifier.IsNone)
        |> Option.map (fun physical -> physical.Column, Some physical.Transform)
    | transform when column.Name <> "" -> Some(column.Name, transform)
    | _ -> None

let isBuiltin = function
    | Expression expression -> tryPhysicalExpression expression |> Option.isSome
    | transform -> tryBuiltinName transform |> Option.isSome

type private TransformFamily =
    | TextOperation
    | IntegerFromText
    | EncodedTextResult
    | DecodedBinaryResult
    | NumericGeneral
    | NumericDoubleResult
    | NullIndicator
    | ComposedExpression

let private transformFamily = function
    | Lowercase
    | Uppercase
    | Trimmed
    | Reversed -> TextOperation
    | CharacterLength
    | ByteLength
    | BitLength
    | FirstByte
    | FirstCharacterCode -> IntegerFromText
    | EncodedHex
    | Md5Digest
    | Sha1Digest -> EncodedTextResult
    | DecodedHex -> DecodedBinaryResult
    | AbsoluteValue
    | Signum
    | BitCounted
    | Floored
    | Ceiled
    | Rounded -> NumericGeneral
    | SquareRooted
    | Exponentiated
    | Sine
    | Cosine
    | Tangent
    | ArcSine
    | ArcCosine
    | ArcTangent
    | Cotangent
    | Degrees
    | Radians
    | Logarithm
    | NaturalLogarithm
    | BinaryLogarithm
    | DecimalLogarithm -> NumericDoubleResult
    | IsNullResult -> NullIndicator
    | Expression _ -> ComposedExpression

let rec hasTextResult = function
    | Expression expression ->
        tryPhysicalExpression expression
        |> Option.bind (_.Calls >> List.tryLast)
        |> Option.exists (snd >> hasTextResult)
    | transform ->
        match transformFamily transform with
        | TextOperation | EncodedTextResult -> true
        | _ -> false

let tryRebaseColumn column = function
    | Expression expression ->
        tryPhysicalExpression expression
        |> Option.map (fun physical -> Expression(canonicalExpression column physical.Calls))
    | transform -> Some transform

let private isTextOrBinary =
    function
    | TChar _
    | TVarchar _
    | TTinyText
    | TText
    | TMediumText
    | TLongText
    | TBinary _
    | TVarBinary _
    | TTinyBlob
    | TBlob
    | TMediumBlob
    | TLongBlob -> true
    | _ -> false

let private isNumeric =
    function
    | TBool
    | TTinyInt _
    | TSmallInt _
    | TMediumInt _
    | TInt _
    | TBigInt _
    | TBit _
    | TFloat _
    | TDouble _
    | TDecimal _
    | TYear -> true
    | _ -> false

let private supportsSingleTransform transform columnType =
    match transformFamily transform with
    | TextOperation ->
        match columnType with
        | TChar _
        | TVarchar _
        | TBinary _
        | TVarBinary _ -> true
        | _ -> false
    | IntegerFromText
    | DecodedBinaryResult
    | EncodedTextResult ->
        match columnType with
        | TGeometry _
        | TVector _ -> false
        | _ -> true
    | NumericGeneral
    | NumericDoubleResult ->
        isNumeric columnType || isTextOrBinary columnType
    | NullIndicator -> true
    | ComposedExpression -> false

let private isTextTransform transform = transformFamily transform = TextOperation

let private isTextToIntegerTransform transform = transformFamily transform = IntegerFromText

let private isEncodedTextTransform transform = transformFamily transform = EncodedTextResult

let private isNumericTransform transform =
    match transformFamily transform with
    | NumericGeneral | NumericDoubleResult -> true
    | _ -> false

let private producesDoubleResult transform = transformFamily transform = NumericDoubleResult

type private NumericKeyResult =
    | ExactSource
    | SignedInteger
    | Approximate

let private numericKeyResult columnType transforms =
    let sourceResult =
        match columnType, transforms with
        | TBit _, AbsoluteValue :: _ -> ExactSource
        | TBool, _
        | TYear, _
        | TBit _, _
        | TFloat _, _
        | TDouble _, _ -> Approximate
        | _ when isTextOrBinary columnType -> Approximate
        | _ -> ExactSource

    transforms
    |> List.fold
        (fun result transform ->
            match transform with
            | Signum | BitCounted -> SignedInteger
            | transform when producesDoubleResult transform -> Approximate
            | _ -> result)
        sourceResult

let supportsColumnType transform columnType =
    match transform with
    | Expression expression ->
        tryPhysicalExpression expression
        |> Option.exists (fun physical ->
            let transforms = physical.Calls |> List.map snd

            match transforms, List.rev transforms with
            | first :: _, textResult :: inner
                when (isTextToIntegerTransform textResult
                      || textResult = DecodedHex
                      || isEncodedTextTransform textResult)
                     && List.forall isTextTransform inner ->
                if inner.IsEmpty then
                    supportsSingleTransform textResult columnType
                else
                    supportsSingleTransform first columnType
            | first :: _, _ when List.forall isTextTransform transforms -> supportsSingleTransform first columnType
            | first :: _, _ when List.forall isNumericTransform transforms ->
                supportsSingleTransform first columnType
            | _ -> false)
    | transform -> supportsSingleTransform transform columnType

let rec fixedKeyLength = function
    | CharacterLength
    | ByteLength
    | BitLength
    | FirstCharacterCode
    | Signum
    | BitCounted -> Some 8
    | FirstByte
    | IsNullResult -> Some 4
    | Expression expression ->
        tryPhysicalExpression expression
        |> Option.bind (fun physical -> physical.Calls |> List.tryLast |> Option.bind (snd >> fixedKeyLength))
    | _ -> None

let private trimBinarySpaces (bytes: byte[]) =
    let mutable first = 0
    let mutable afterLast = bytes.Length

    while first < afterLast && bytes.[first] = 0x20uy do
        first <- first + 1

    while afterLast > first && bytes.[afterLast - 1] = 0x20uy do
        afterLast <- afterLast - 1

    if first = 0 && afterLast = bytes.Length then
        bytes
    else
        Array.sub bytes first (afterLast - first)

let reverseText (text: string) =
    text.EnumerateRunes()
    |> Seq.rev
    |> Seq.map _.ToString()
    |> String.concat ""

let private runeLength (text: string) =
    text.EnumerateRunes() |> Seq.length |> int64

let characterLength value =
    match value, tryRawBytes value with
    | VEncodedString(charset, bytes), _ -> int64 ((Fsdb.Charset.characterByteOffsets charset bytes).Length - 1)
    | _, Some bytes -> int64 bytes.Length
    | _, None -> value |> toText |> Option.map runeLength |> Option.defaultValue 0L

/// Text byte length follows the source character set; callers without a
/// declared source use UTF-8, matching ordinary string literals.
let byteLengthWith encodeText value =
    match tryRawBytes value, value with
    | Some bytes, _ -> int64 bytes.Length
    | None, VString text -> text |> encodeText |> Array.length |> int64
    | None, _ -> value |> toText |> Option.defaultValue "" |> Text.Encoding.UTF8.GetByteCount |> int64

let private tryExactInt64 =
    function
    | VInt value -> Some value
    | VUInt value when value <= uint64 Int64.MaxValue -> Some(int64 value)
    | VDecimal value
        when value >= decimal Int64.MinValue
             && value <= decimal Int64.MaxValue
             && Decimal.Truncate value = value ->
        Some(int64 value)
    | VDouble value
        when Double.IsFinite value
             && value >= -9223372036854775808.0
             && value < 9223372036854775808.0
             && Math.Truncate value = value ->
        Some(int64 value)
    | _ -> None

let private tryExactUInt64 =
    let ofDecimal value =
        if value >= 0m && value <= decimal UInt64.MaxValue && Decimal.Truncate value = value then
            Some(uint64 value)
        else
            None

    let ofText (text: string) =
        match Decimal.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value -> ofDecimal value
        | false, _ -> None

    function
    | VInt value when value >= 0L -> Some(uint64 value)
    | VUInt value
    | VBit(_, value) -> Some value
    | VDecimal value -> ofDecimal value
    | VDouble value
        when Double.IsFinite value
             && value >= 0.0
             && value < 18446744073709551616.0
             && Math.Truncate value = value ->
        Some(uint64 value)
    | VString text -> ofText text
    | VBytes bytes -> bytes |> Text.Encoding.Latin1.GetString |> ofText
    | _ -> None

let private tryRoundedProbe columnType value =
    let exactDoubleLimit = 1L <<< 53

    match columnType, value with
    | TDecimal _, VInt number -> Some(VDecimal(decimal number))
    | TDecimal _, VUInt number -> Some(VDecimal(decimal number))
    | TDecimal _, VDecimal number when Decimal.Truncate number = number -> Some(VDecimal number)
    | (TFloat _ | TDouble _), VInt number when number >= -exactDoubleLimit && number <= exactDoubleLimit ->
        Some(VDouble(float number))
    | (TFloat _ | TDouble _), VUInt number when number <= uint64 exactDoubleLimit ->
        Some(VDouble(float number))
    | (TFloat _ | TDouble _), VDouble number when Double.IsFinite number && Math.Truncate number = number ->
        Some(VDouble number)
    | _ -> None

let private tryBinaryProbe = function
    | VBytes bytes
    | VBinaryLiteral bytes -> Some(VBytes bytes)
    | VEncodedString(_, bytes) -> Some(VBytes bytes)
    | VString text when text |> Seq.forall (fun character -> character <= '\u007f') ->
        Some(VBytes(Text.Encoding.ASCII.GetBytes text))
    | _ -> None

let private tryTextResultProbe = function
    | VString text -> Some(VString text)
    | _ -> None

let rec tryNormalizeProbe columnType transform normalizeStored value =
    match transform, value with
    | Some _, VNull -> Some VNull
    | Some AbsoluteValue, _ when isTextOrBinary columnType ->
        match value with
        | VString text -> text |> coerceLeadingDouble |> fst |> VDouble |> Some
        | VBytes bytes -> bytes |> Text.Encoding.Latin1.GetString |> coerceLeadingDouble |> fst |> VDouble |> Some
        | value -> value |> toDouble |> VDouble |> Some
    | Some AbsoluteValue, _ ->
        match columnType with
        | TBit width ->
            let maximum = if width = 64 then UInt64.MaxValue else (1UL <<< width) - 1UL

            value
            |> tryExactUInt64
            |> Option.filter (fun value -> value <= maximum)
            |> Option.map VUInt
        | _ -> normalizeStored value
    | (Some Floored | Some Ceiled | Some Rounded), _ when isTextOrBinary columnType || (match columnType with TBit _ -> true | _ -> false) ->
        value |> toDouble |> VDouble |> Some
    | (Some Floored | Some Ceiled | Some Rounded), _ ->
        tryRoundedProbe columnType value |> Option.orElseWith (fun () -> normalizeStored value)
    | Some transform, VDouble number when producesDoubleResult transform && Double.IsFinite number -> Some(VDouble number)
    | Some transform, _ when producesDoubleResult transform -> None
    | Some CharacterLength, _
    | Some ByteLength, _
    | Some BitLength, _
    | Some FirstByte, _
    | Some FirstCharacterCode, _
    | Some IsNullResult, _
    | Some Signum, _
    | Some BitCounted, _ -> tryExactInt64 value |> Option.map VInt
    | Some DecodedHex, _ -> tryBinaryProbe value
    | Some transform, _ when isEncodedTextTransform transform -> tryTextResultProbe value
    | Some(Expression expression), _ ->
        tryPhysicalExpression expression
        |> Option.bind (fun physical ->
            let transforms = physical.Calls |> List.map snd

            match List.tryLast transforms with
            | Some transform when isTextToIntegerTransform transform -> tryExactInt64 value |> Option.map VInt
            | Some DecodedHex -> tryBinaryProbe value
            | Some transform when isEncodedTextTransform transform -> tryTextResultProbe value
            | Some _ when List.forall isNumericTransform transforms ->
                match numericKeyResult columnType transforms, value with
                | SignedInteger, _ -> tryExactInt64 value |> Option.map VInt
                | Approximate, VDouble number when Double.IsFinite number -> Some(VDouble number)
                | Approximate, _ -> None
                | ExactSource, _ -> tryNormalizeProbe columnType (Some AbsoluteValue) normalizeStored value
            | Some _ -> normalizeStored value
            | None -> None)
    | _ -> normalizeStored value

let private mapTextOrBytes mapText mapBytes value =
    match tryRawBytes value with
    | Some bytes -> VBytes(mapBytes bytes)
    | None -> value |> toText |> Option.defaultValue "" |> mapText |> VString

let firstByteValue bytes =
    bytes |> Array.tryHead |> Option.defaultValue 0uy |> int64 |> VInt

let private firstCharacterBytes encodeText = function
    | VEncodedString(charset, bytes) ->
        bytes |> Array.truncate (Fsdb.Charset.firstCharacterByteWidth charset bytes)
    | value ->
        match tryRawBytes value with
        | Some bytes -> bytes |> Array.truncate 1
        | None ->
            let text = value |> toText |> Option.defaultValue ""
            text.EnumerateRunes() |> Seq.tryHead |> Option.map (fun rune -> encodeText (rune.ToString())) |> Option.defaultValue [||]

let firstCharacterCode encodeText value =
    firstCharacterBytes encodeText value
    |> Array.fold (fun code part -> code * 256L + int64 part) 0L
    |> VInt

let private numericInputWithStatus = function
    | (VString _ | VBytes _) as textValue ->
        let text = textValue |> toText |> Option.defaultValue ""
        let number, truncated = coerceLeadingDouble text
        number, (if truncated then Some text else None)
    | value -> toDouble value, None

let private logarithmValue logarithm value =
    let number, truncated = numericInputWithStatus value
    if number <= 0.0 then raise InvalidLogarithmArgument
    (if Double.IsNaN number then VNull else VDouble(logarithm number)), truncated

let private approximateFunctionValue name operation value =
    let number, truncated = numericInputWithStatus value
    let result = operation number
    if Double.IsInfinity result then raise (DoubleOutOfRange name)
    (if Double.IsNaN result then VNull else VDouble result), truncated

let cotangent number = 1.0 / Math.Tan number
let degrees number = number * 180.0 / Math.PI
let radians number = number / 180.0 * Math.PI

let private roundFunctionalValue (roundDecimal: decimal -> decimal) (roundDouble: float -> float) value =
    match value with
    | VInt _ | VUInt _ -> value, None
    | VDecimal number -> VDecimal(roundDecimal number), None
    | value ->
        let number, truncated = numericInputWithStatus value
        VDouble(roundDouble number), truncated

type BitCountWarning =
    | TextConversion of string
    | BinaryConversion of string

/// Exact integer bits must not pass through double, which loses the top half
/// of BIGINT UNSIGNED and saturates negative signed patterns.
let integerBitPattern value : uint64 =
    match value with
    | VUInt number -> number
    | VBit(_, number) -> number
    | VInt number -> uint64 number
    | _ ->
        let number = toDouble value
        if number >= 1.8446744073709552e19 then UInt64.MaxValue
        elif number < 0.0 then uint64 (int64 (max number -9.2233720368547758e18))
        else uint64 number

/// Binary strings count every byte; numeric values count the rounded 64-bit pattern.
/// Text takes the integer cast path, which truncates instead of rounding.
let bitCountValueWithStatus value =
    let bits, warning =
        match value with
        | VBytes bytes | VEncodedString("binary", bytes) ->
            let bits = bytes |> Array.sumBy (fun byte -> int64 (Numerics.BitOperations.PopCount(uint32 byte)))
            bits, None
        | VBinaryLiteral bytes when bytes.Length > 8 ->
            let literal = sprintf "x'%s'" (Convert.ToHexString(bytes).ToLowerInvariant())
            0L, Some(BinaryConversion literal)
        | VString text ->
            let _, truncated = coerceLeadingDouble text
            let warning =
                if truncated || String.IsNullOrWhiteSpace text then Some(TextConversion text) else None
            int64 (Numerics.BitOperations.PopCount(integerBitPattern value)), warning
        | VDecimal number ->
            let rounded = VDecimal(Math.Round(number, MidpointRounding.AwayFromZero))
            int64 (Numerics.BitOperations.PopCount(integerBitPattern rounded)), None
        | VDouble number ->
            let rounded = VDouble(Math.Round(number, MidpointRounding.ToEven))
            int64 (Numerics.BitOperations.PopCount(integerBitPattern rounded)), None
        | _ -> int64 (Numerics.BitOperations.PopCount(integerBitPattern value)), None
    VInt bits, warning

let hexValueWithStatus encodeText value =
    let encodedBytes (bytes: byte[]) = VString(Convert.ToHexString bytes), false

    match value, tryRawBytes value with
    | VNull, _ -> VNull, false
    | VBit(_, bits), _ -> VString(bits.ToString "X"), false
    | _, Some bytes -> encodedBytes bytes
    | VString text, _ -> encodedBytes (encodeText text)
    | VInt number, _ -> VString(number.ToString "X"), false
    | VUInt number, _ -> VString(number.ToString "X"), false
    | VDecimal number, _ ->
        let rounded = Math.Round(number, MidpointRounding.AwayFromZero)
        let bounded = max (decimal Int64.MinValue) (min (decimal Int64.MaxValue) rounded)
        VString((int64 bounded).ToString "X"), bounded <> rounded
    | value, _ ->
        let number =
            match value with
            | VDouble number -> Math.Round(number, MidpointRounding.ToEven)
            | _ -> toDouble value

        let bounded =
            if Double.IsNaN number then 0L
            elif number >= float Int64.MaxValue then Int64.MaxValue
            elif number <= float Int64.MinValue then Int64.MinValue
            else int64 number

        VString(bounded.ToString "X"), false

let private digestValueWith encodeText (hash: byte[] -> byte[]) value =
    let bytes =
        value
        |> tryRawBytes
        |> Option.defaultWith (fun () -> value |> toText |> Option.defaultValue "" |> encodeText)

    hash bytes |> Convert.ToHexString |> fun text -> VString(text.ToLowerInvariant())

let rec projectValueWithStatus encodeText transform value =
    match transform, value with
    | Some IsNullResult, VNull -> VInt 1L, None
    | Some IsNullResult, _ -> VInt 0L, None
    | Some _, VNull -> VNull, None
    | Some Lowercase, value -> mapTextOrBytes _.ToLowerInvariant() id value, None
    | Some Uppercase, value -> mapTextOrBytes _.ToUpperInvariant() id value, None
    | Some Trimmed, value -> mapTextOrBytes _.Trim(' ') trimBinarySpaces value, None
    | Some Reversed, VEncodedString(charset, bytes) ->
        Fsdb.Charset.reverseCharacterBytes charset bytes |> encodedString charset, None
    | Some Reversed, value -> mapTextOrBytes reverseText Array.rev value, None
    | Some CharacterLength, value -> VInt(characterLength value), None
    | Some ByteLength, value -> VInt(byteLengthWith encodeText value), None
    | Some BitLength, value -> VInt(byteLengthWith encodeText value * 8L), None
    | Some FirstByte, value ->
        let bytes =
            match tryRawBytes value with
            | Some bytes -> bytes
            | None -> value |> toText |> Option.defaultValue "" |> encodeText

        firstByteValue bytes, None
    | Some FirstCharacterCode, value -> firstCharacterCode encodeText value, None
    | Some DecodedHex, value ->
        let text = value |> toText |> Option.defaultValue ""

        if text |> Seq.forall Uri.IsHexDigit |> not then
            VNull, None
        else
            let digits = if text.Length % 2 = 0 then text else "0" + text
            [| for index in 0 .. 2 .. digits.Length - 1 -> Convert.ToByte(digits.Substring(index, 2), 16) |]
            |> VBytes,
            None
    | Some EncodedHex, value ->
        let encoded, overflow = hexValueWithStatus encodeText value
        encoded, (if overflow then toText value else None)
    | Some Md5Digest, value ->
        digestValueWith encodeText System.Security.Cryptography.MD5.HashData value, None
    | Some Sha1Digest, value ->
        digestValueWith encodeText System.Security.Cryptography.SHA1.HashData value, None
    | Some Floored, value -> roundFunctionalValue Math.Floor Math.Floor value
    | Some Ceiled, value -> roundFunctionalValue Math.Ceiling Math.Ceiling value
    | Some Rounded, VUInt value -> narrowUnsigned (decimal value), None
    | Some Rounded, value ->
        roundFunctionalValue
            (fun number -> Math.Round(number, MidpointRounding.AwayFromZero))
            (fun number -> Math.Round(number, MidpointRounding.ToEven))
            value
    | Some SquareRooted, value ->
        let number, truncated = numericInputWithStatus value
        (if number < 0.0 then VNull else VDouble(Math.Sqrt number)), truncated
    | Some Exponentiated, value -> approximateFunctionValue "EXP" Math.Exp value
    | Some Sine, value -> approximateFunctionValue "SIN" Math.Sin value
    | Some Cosine, value -> approximateFunctionValue "COS" Math.Cos value
    | Some Tangent, value -> approximateFunctionValue "TAN" Math.Tan value
    | Some ArcSine, value -> approximateFunctionValue "ASIN" Math.Asin value
    | Some ArcCosine, value -> approximateFunctionValue "ACOS" Math.Acos value
    | Some ArcTangent, value -> approximateFunctionValue "ATAN" Math.Atan value
    | Some Cotangent, value -> approximateFunctionValue "COT" cotangent value
    | Some Degrees, value -> approximateFunctionValue "DEGREES" degrees value
    | Some Radians, value -> approximateFunctionValue "RADIANS" radians value
    | Some Logarithm, value
    | Some NaturalLogarithm, value -> logarithmValue Math.Log value
    | Some BinaryLogarithm, value -> logarithmValue Math.Log2 value
    | Some DecimalLogarithm, value -> logarithmValue Math.Log10 value
    | Some Signum, value ->
        let number, truncated = numericInputWithStatus value
        VInt(int64 (sign number)), truncated
    | Some BitCounted, value ->
        let counted, warning = bitCountValueWithStatus value
        counted, (warning |> Option.map (function TextConversion text | BinaryConversion text -> text))
    | Some AbsoluteValue, VInt Int64.MinValue -> raise SignedOutOfRange
    | Some AbsoluteValue, VInt value -> VInt(abs value), None
    | Some AbsoluteValue, VUInt value -> VUInt value, None
    | Some AbsoluteValue, VBit(_, value) -> VUInt value, None
    | Some AbsoluteValue, VDouble value -> VDouble(abs value), None
    | Some AbsoluteValue, VDecimal value -> VDecimal(abs value), None
    | Some AbsoluteValue, value ->
        let number, truncated = numericInputWithStatus value
        VDouble(abs number), truncated
    | Some(Expression expression), value ->
        match tryPhysicalExpression expression with
        | None -> value, None
        | Some physical ->
            physical.Calls
            |> List.fold
                (fun (current, firstTruncation) (_, step) ->
                    let projected, truncated = projectValueWithStatus encodeText (Some step) current
                    projected, (firstTruncation |> Option.orElse truncated))
                (value, None)
    | _ -> value, None

let projectValueWith encodeText transform value =
    projectValueWithStatus encodeText transform value |> fst

let projectValue transform value =
    projectValueWith Text.Encoding.UTF8.GetBytes transform value
