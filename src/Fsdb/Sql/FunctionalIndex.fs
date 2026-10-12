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
exception IntegerFallbackIndexValueOutOfRange

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
      { CanonicalName = "CRC32"
        Aliases = []
        Transform = Checksum32 }
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

type NullFallbackFunction =
    | CoalesceFunction
    | IfNullFunction
    | IfNullBranch
    | IfNotNullBranch

let fallbackBuiltinName = function
    | CoalesceFunction -> "COALESCE"
    | IfNullFunction -> "IFNULL"
    | IfNullBranch | IfNotNullBranch -> "IF"

let private isConditionalFallback = function
    | IfNullBranch | IfNotNullBranch -> true
    | _ -> false

let private coalescingFunction (name: string) =
    if name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase) then CoalesceFunction else IfNullFunction

type NullFallback =
    { Function: NullFallbackFunction
      Value: Value
      Literal: Expr }

type NullComparison =
    { Value: Value
      Literal: Expr }

type PhysicalExpression =
    { Qualifier: string option
      Column: string
      NullFallback: NullFallback option
      NullComparison: NullComparison option
      Calls: (string * IndexTransform) list
      Transform: IndexTransform }

let private canonicalExpression (column: string) fallback nullComparison (calls: (string * IndexTransform) list) =
    let source =
        let column = Col(column.ToLowerInvariant())
        match fallback, nullComparison with
        | Some({ Function = IfNullBranch } as fallback), None ->
            FuncCall("IF", [ IsNull column; fallback.Literal; column ])
        | Some({ Function = IfNotNullBranch } as fallback), None ->
            FuncCall("IF", [ IsNotNull column; column; fallback.Literal ])
        | Some fallback, None -> FuncCall(fallbackBuiltinName fallback.Function, [ column; fallback.Literal ])
        | None, Some comparison -> FuncCall("NULLIF", [ column; comparison.Literal ])
        | None, None -> column
        | Some _, Some _ -> invalidArg (nameof nullComparison) "An index source cannot have two NULL rules"

    calls
    |> List.fold
        (fun expression (_, transform) ->
            let name = transform |> tryBuiltinName |> Option.defaultWith (fun () -> invalidArg (nameof transform) "Not a built-in index transform")
            FuncCall(name, [ expression ]))
        source

let tryPhysicalExpression expression =
    let literal = function
        | Lit value
        | ConnectionLiteral(value, _) as expression -> Some(expression, value)
        | IntroducedLiteral((VString _ | VEncodedString _) as value, charset, _) ->
            value
            |> toText
            |> Option.map (fun text ->
                let text = VString text
                IntroducedLiteral(text, Fsdb.Charset.canonicalName charset, ResolvedIntroducer), text)
        | IntroducedLiteral(value, _, _) as expression -> Some(expression, value)
        | _ -> None

    let matchingColumn tested source =
        match tested, source with
        | Col tested, Col column when tested.Equals(column, StringComparison.OrdinalIgnoreCase) ->
            Some(None, column)
        | QualifiedCol(testedQualifier, tested), QualifiedCol(qualifier, column)
            when testedQualifier.Equals(qualifier, StringComparison.OrdinalIgnoreCase)
                 && tested.Equals(column, StringComparison.OrdinalIgnoreCase) ->
            Some(Some qualifier, column)
        | _ -> None

    let conditionalLiteral = function
        | Lit(VString _ as value) ->
            literal (IntroducedLiteral(value, "utf8mb4", ResolvedIntroducer))
        | ConnectionLiteral((VString _ | VEncodedString _) as value, collation) ->
            let charset = Fsdb.Collation.charsetOfCollation collation |> Fsdb.Charset.canonicalName
            literal (IntroducedLiteral(value, charset, ResolvedIntroducer))
        | argument -> literal argument

    let conditionalFallback form tested argument source calls =
        matchingColumn tested source
        |> Option.bind (fun (qualifier, column) ->
            conditionalLiteral argument
            |> Option.map (fun (expression, value) ->
                qualifier, column, Some { Function = form; Value = value; Literal = expression }, None, calls))

    let rec collect calls = function
        | Col column -> Some(None, column, None, None, calls)
        | QualifiedCol(qualifier, column) -> Some(Some qualifier, column, None, None, calls)
        | FuncCall(name, [ Col column; argument ])
            when name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("IFNULL", StringComparison.OrdinalIgnoreCase) ->
            literal argument
            |> Option.map (fun (expression, value) ->
                None, column, Some { Function = coalescingFunction name; Value = value; Literal = expression }, None, calls)
        | FuncCall(name, [ QualifiedCol(qualifier, column); argument ])
            when name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("IFNULL", StringComparison.OrdinalIgnoreCase) ->
            literal argument
            |> Option.map (fun (expression, value) ->
                Some qualifier, column, Some { Function = coalescingFunction name; Value = value; Literal = expression }, None, calls)
        | FuncCall(name, [ IsNull tested; argument; source ])
            when name.Equals("IF", StringComparison.OrdinalIgnoreCase) ->
            conditionalFallback IfNullBranch tested argument source calls
        | FuncCall(name, [ IsNotNull tested; source; argument ])
            when name.Equals("IF", StringComparison.OrdinalIgnoreCase) ->
            conditionalFallback IfNotNullBranch tested argument source calls
        | FuncCall(name, [ Col column; argument ]) when name.Equals("NULLIF", StringComparison.OrdinalIgnoreCase) ->
            conditionalLiteral argument
            |> Option.map (fun (literal, value) -> None, column, None, Some { Value = value; Literal = literal }, calls)
        | FuncCall(name, [ QualifiedCol(qualifier, column); argument ])
            when name.Equals("NULLIF", StringComparison.OrdinalIgnoreCase) ->
            conditionalLiteral argument
            |> Option.map (fun (literal, value) -> Some qualifier, column, None, Some { Value = value; Literal = literal }, calls)
        | FuncCall(name, [ argument ]) ->
            tryBuiltin name
            |> Option.bind (fun transform -> collect ((name, transform) :: calls) argument)
        | _ -> None

    collect [] expression
    |> Option.bind (fun (qualifier, column, fallback, nullComparison, calls) ->
        if fallback.IsNone && nullComparison.IsNone && calls.IsEmpty then None
        else
            Some
                { Qualifier = qualifier
                  Column = column
                  NullFallback = fallback
                  NullComparison = nullComparison
                  Calls = calls
                  Transform = Expression(canonicalExpression column fallback nullComparison calls) })

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

let sameLookupTransform (column: ColumnDef) requested stored =
    if requested = stored then
        true
    else
        match requested, stored with
        | Some(Expression requested), Some(Expression stored) ->
            match tryPhysicalExpression requested, tryPhysicalExpression stored with
            | Some requested, Some stored when
                requested.Column.Equals(stored.Column, StringComparison.OrdinalIgnoreCase)
                && requested.Calls = stored.Calls
                && requested.NullFallback.IsNone
                && stored.NullFallback.IsNone ->
                match requested.NullComparison, stored.NullComparison with
                | Some requestedComparison, Some storedComparison ->
                    match requestedComparison.Value, storedComparison.Value,
                          requestedComparison.Literal, storedComparison.Literal with
                    | VString requestedText, VString storedText,
                      IntroducedLiteral(_, requestedCharset, _), IntroducedLiteral(_, storedCharset, _) ->
                        let sameCharset = Fsdb.Charset.canonicalName requestedCharset = Fsdb.Charset.canonicalName storedCharset
                        let defaultUnicodeCollation =
                            column.Collation
                            |> Option.exists (fun name -> name.Equals("utf8mb4_0900_ai_ci", StringComparison.OrdinalIgnoreCase))

                        (sameCharset
                         || (defaultUnicodeCollation && Fsdb.Charset.canonicalName requestedCharset <> "binary"))
                        && (Fsdb.Collation.findOrDefault column.Collation).Equals requestedText storedText
                    | _ -> false
                | _ -> false
            | _ -> false
        | _ -> false

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
    | Checksum32
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
        |> Option.exists (fun physical ->
            match List.tryLast physical.Calls with
            | Some(_, transform) -> hasTextResult transform
            | None ->
                physical.NullFallback
                |> Option.map _.Value
                |> Option.orElseWith (fun () -> physical.NullComparison |> Option.map _.Value)
                |> Option.exists (function VString _ | VEncodedString _ -> true | _ -> false))
    | transform ->
        match transformFamily transform with
        | TextOperation | EncodedTextResult -> true
        | _ -> false

let hasNonNullableTextFallback = function
    | Expression expression ->
        tryPhysicalExpression expression
        |> Option.exists (fun physical ->
            physical.Calls.IsEmpty
            && (physical.NullFallback
                |> Option.exists (fun fallback ->
                    not (isConditionalFallback fallback.Function)
                    && (match fallback.Value with
                        | VString _ | VEncodedString _ -> true
                        | _ -> false))))
    | _ -> false

let tryRebaseColumn column = function
    | Expression expression ->
        tryPhysicalExpression expression
        |> Option.map (fun physical ->
            Expression(canonicalExpression column physical.NullFallback physical.NullComparison physical.Calls))
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

let private numericKeyResult columnType startsWithFallback transforms =
    let sourceResult =
        if startsWithFallback then ExactSource
        else
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
            | Signum | BitCounted | Checksum32 -> SignedInteger
            | transform when producesDoubleResult transform -> Approximate
            | _ -> result)
        sourceResult

type private IntegerFallbackKey =
    | SignedInt32
    | SignedInt64
    | UnsignedInt64
    | ExactDecimal20

let private tryIntegerFallbackKey columnType fallback =
    let wideLiteral value = value >= 10_000_000_000L || value <= -10_000_000_000L

    match columnType, fallback.Function, fallback.Value with
    | (TBool | TTinyInt _ | TSmallInt _ | TMediumInt _ | TInt _),
      (IfNullBranch | IfNotNullBranch), VInt value ->
        Some(if wideLiteral value then SignedInt64 else SignedInt32)
    | TBigInt false, (IfNullBranch | IfNotNullBranch), VInt _ -> Some SignedInt64
    | (TBool | TTinyInt _ | TSmallInt _ | TMediumInt _ | TInt _ | TBigInt false), IfNullFunction, VInt _ ->
        Some SignedInt64
    | TBigInt true, IfNullFunction, VInt _ -> Some ExactDecimal20
    | (TBool | TTinyInt _ | TSmallInt _ | TMediumInt _ | TInt _), CoalesceFunction, VInt value ->
        Some(if wideLiteral value then SignedInt64 else SignedInt32)
    | TBigInt false, CoalesceFunction, VInt _ -> Some SignedInt64
    | TBigInt true, CoalesceFunction, VInt _ -> Some ExactDecimal20
    | (TBool | TTinyInt false | TSmallInt false | TMediumInt false | TInt false | TBigInt false),
      (CoalesceFunction | IfNullFunction), VUInt _ ->
        Some ExactDecimal20
    | (TTinyInt true | TSmallInt true | TMediumInt true | TInt true | TBigInt true),
      (CoalesceFunction | IfNullFunction), VUInt _ ->
        Some UnsignedInt64
    | _ -> None

let private fitsTextFallbackWidth columnType (fallback: NullFallback) =
    match columnType, fallback.Literal, fallback.Value with
    | TVarchar width, IntroducedLiteral _, (VString _ | VEncodedString _) ->
        fallback.Value
        |> toText
        |> Option.exists (fun text -> text.EnumerateRunes() |> Seq.length <= width)
    | _ -> false

let private supportsTextFallback (column: ColumnDef) (fallback: NullFallback) =
    let sourceCharset = column.Charset |> Option.defaultValue "utf8mb4" |> Fsdb.Charset.canonicalName

    fitsTextFallbackWidth column.Type fallback
    && (match fallback.Literal with
        | IntroducedLiteral(_, charset, _) -> Fsdb.Charset.canonicalName charset = sourceCharset
        | _ -> false)

let private nullIfIntegerWidth = function
    | TBool | TTinyInt _ | TSmallInt _ | TMediumInt _ | TInt _ -> Some 4
    | TBigInt _ -> Some 8
    | _ -> None

let private supportsNullIf (column: ColumnDef) comparison =
    match nullIfIntegerWidth column.Type, comparison.Value with
    | Some _, (VInt _ | VUInt _) -> true
    | _, VString _ ->
        match column.Type, column.Collation, comparison.Literal with
        | TVarchar _, Some collation, IntroducedLiteral(_, charset, _) ->
            Fsdb.Collation.tryFind collation |> Option.isSome
            && Fsdb.Charset.canonicalName charset = (column.Charset |> Option.defaultValue "utf8mb4" |> Fsdb.Charset.canonicalName)
        | _ -> false
    | _ -> false

let supportsColumn transform (column: ColumnDef) =
    match transform with
    | Expression expression ->
        tryPhysicalExpression expression
        |> Option.exists (fun physical ->
            let columnType = column.Type
            let transforms = physical.Calls |> List.map snd
            let sourceSupported =
                (physical.NullFallback
                 |> Option.forall (fun fallback ->
                     if isConditionalFallback fallback.Function then
                         transforms.IsEmpty
                         && (tryIntegerFallbackKey columnType fallback |> Option.isSome
                             || supportsTextFallback column fallback)
                     else
                         tryIntegerFallbackKey columnType fallback |> Option.isSome
                         || (transforms.IsEmpty && supportsTextFallback column fallback)))
                && (physical.NullComparison
                    |> Option.forall (supportsNullIf column))

            if not sourceSupported then false
            else
                match transforms, List.rev transforms with
                | [], [] -> physical.NullFallback.IsSome || physical.NullComparison.IsSome
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
                | first :: _, _ when List.forall isNumericTransform transforms -> supportsSingleTransform first columnType
                | _ -> false)
    | transform -> supportsSingleTransform transform column.Type

/// These transforms return exact integer values; their encoded key width also
/// supplies metadata for the hidden functional column.
let rec private integerKeyLength = function
    | CharacterLength
    | ByteLength
    | BitLength
    | FirstCharacterCode
    | Signum
    | BitCounted -> Some 8
    | FirstByte
    | Checksum32
    | IsNullResult -> Some 4
    | Expression expression ->
        tryPhysicalExpression expression
        |> Option.bind (fun physical -> physical.Calls |> List.tryLast |> Option.bind (snd >> integerKeyLength))
    | _ -> None

/// The hidden result type depends on both source signedness and literal width.
let tryIntegerExpressionKeyLength columnType = function
    | Expression expression ->
        match tryPhysicalExpression expression with
        | Some { NullComparison = Some _; Calls = [] } -> nullIfIntegerWidth columnType |> Option.map ((+) 1)
        | Some { NullFallback = Some fallback; Calls = [] } ->
            match tryIntegerFallbackKey columnType fallback with
            | Some SignedInt32 ->
                Some(if isConditionalFallback fallback.Function then 5 else 4)
            | Some SignedInt64 -> Some(if isConditionalFallback fallback.Function then 9 else 8)
            | Some UnsignedInt64 -> Some 8
            | Some ExactDecimal20 -> Some 9
            | None -> None
        | _ -> None
    | _ -> None

let fixedKeyLength columnType transform =
    tryIntegerExpressionKeyLength columnType transform
    |> Option.orElseWith (fun () -> integerKeyLength transform)

let rec hasNumericResult = function
    | Expression expression ->
        tryPhysicalExpression expression
        |> Option.exists (fun physical ->
            (physical.NullFallback
             |> Option.exists (fun fallback ->
                 match fallback.Value with
                 | VInt _ | VUInt _ -> true
                 | _ -> false))
            || physical.NullComparison.IsSome
            || (physical.Calls |> List.tryLast |> Option.exists (snd >> hasNumericResult)))
    | transform ->
        match transformFamily transform with
        | IntegerFromText | NumericGeneral | NumericDoubleResult | NullIndicator -> true
        | _ -> false

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

let private tryIntegerFallbackKeyValue key value =
    match key with
    | SignedInt32 ->
        value
        |> tryExactInt64
        |> Option.filter (fun number -> number >= int64 Int32.MinValue && number <= int64 Int32.MaxValue)
        |> Option.map VInt
    | SignedInt64 -> tryExactInt64 value |> Option.map VInt
    | UnsignedInt64 -> tryExactUInt64 value |> Option.map VUInt
    | ExactDecimal20 ->
        match value with
        | VInt number -> Some(VDecimal(decimal number))
        | VUInt number -> Some(VDecimal(decimal number))
        | VDecimal number
            when number >= decimal Int64.MinValue
                 && number <= decimal UInt64.MaxValue
                 && Decimal.Truncate number = number ->
            Some(VDecimal number)
        | _ -> None

let private tryIntegerFallbackProbe columnType fallback value =
    fallback
    |> Option.bind (tryIntegerFallbackKey columnType)
    |> Option.bind (fun key ->
        match tryIntegerFallbackKeyValue key value with
        | Some projected -> Some projected
        // Preserve an exact, out-of-range probe. It cannot match a clipped
        // key, but still lets the physical lookup prove the result empty.
        | None when key = SignedInt32 ->
            match value with
            | VInt _ | VUInt _ -> Some value
            | _ -> None
        | None -> None)

let prepareIndexValue columnType transform value =
    match transform with
    | Some(Expression expression) ->
        match tryPhysicalExpression expression with
        | Some { NullFallback = Some fallback } ->
            match tryIntegerFallbackKey columnType fallback with
            | Some key ->
                let selected = if value = VNull then fallback.Value else value
                match tryIntegerFallbackKeyValue key selected with
                | Some projected -> projected, false
                | None ->
                    match key, selected with
                    | SignedInt32, VInt number ->
                        VInt(if number < int64 Int32.MinValue then int64 Int32.MinValue else int64 Int32.MaxValue), true
                    | SignedInt32, VUInt _ -> VInt(int64 Int32.MaxValue), true
                    | _ -> raise IntegerFallbackIndexValueOutOfRange
            | None -> value, false
        | _ -> value, false
    | _ -> value, false

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
    | Some BitCounted, _
    | Some Checksum32, _ -> tryExactInt64 value |> Option.map VInt
    | Some DecodedHex, _ -> tryBinaryProbe value
    | Some transform, _ when isEncodedTextTransform transform -> tryTextResultProbe value
    | Some(Expression expression), _ ->
        tryPhysicalExpression expression
        |> Option.bind (fun physical ->
            let transforms = physical.Calls |> List.map snd

            match List.tryLast transforms with
            | None when physical.NullFallback.IsSome ->
                match physical.NullFallback with
                | Some fallback when fitsTextFallbackWidth columnType fallback ->
                    match value with
                    | VString _ | VEncodedString _ -> value |> toText |> Option.map VString
                    | _ -> None
                | _ -> tryIntegerFallbackProbe columnType physical.NullFallback value
            | None when physical.NullComparison.IsSome && isTextOrBinary columnType ->
                match value with
                | VString _ | VEncodedString _ -> value |> toText |> Option.map VString
                | _ -> None
            | None when physical.NullComparison.IsSome -> normalizeStored value
            | Some transform when isTextToIntegerTransform transform -> tryExactInt64 value |> Option.map VInt
            | Some DecodedHex -> tryBinaryProbe value
            | Some transform when isEncodedTextTransform transform -> tryTextResultProbe value
            | Some _ when List.forall isNumericTransform transforms ->
                match numericKeyResult columnType physical.NullFallback.IsSome transforms, value with
                | SignedInteger, _ -> tryExactInt64 value |> Option.map VInt
                | Approximate, VDouble number when Double.IsFinite number -> Some(VDouble number)
                | Approximate, _ -> None
                | ExactSource, _ when physical.NullFallback.IsSome ->
                    tryIntegerFallbackProbe columnType physical.NullFallback value
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

/// MySQL's CRC32 uses the reflected IEEE polynomial over the source bytes.
let private crc32Table =
    Array.init 256 (fun index ->
        let mutable crc = uint32 index
        for _ in 0 .. 7 do
            crc <- if crc &&& 1u <> 0u then 0xEDB88320u ^^^ (crc >>> 1) else crc >>> 1
        crc)

let crc32ValueWith encodeText value =
    let bytes =
        value
        |> tryRawBytes
        |> Option.defaultWith (fun () -> value |> toText |> Option.defaultValue "" |> encodeText)
    let mutable crc = 0xFFFFFFFFu
    for byte in bytes do
        crc <- crc32Table.[int ((crc ^^^ uint32 byte) &&& 0xFFu)] ^^^ (crc >>> 8)
    VInt(int64 (crc ^^^ 0xFFFFFFFFu))

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

let private matchesNullComparison textEquals source comparison =
    match source, comparison with
    | VString text, VString literal -> textEquals text literal
    | _ -> Fsdb.Value.equals source comparison = Some true

let rec projectValueWithStatus textEquals encodeText transform value =
    match transform, value with
    | Some IsNullResult, VNull -> VInt 1L, None
    | Some IsNullResult, _ -> VInt 0L, None
    | Some(Expression expression), value ->
        match tryPhysicalExpression expression with
        | None -> value, None
        | Some physical ->
            let source =
                match value, physical.NullFallback with
                | VNull, Some fallback -> fallback.Value
                | _ -> value

            let source =
                match physical.NullComparison with
                | Some comparison when matchesNullComparison textEquals source comparison.Value -> VNull
                | _ -> source

            physical.Calls
            |> List.fold
                (fun (current, firstTruncation) (_, step) ->
                    let projected, truncated = projectValueWithStatus textEquals encodeText (Some step) current
                    projected, (firstTruncation |> Option.orElse truncated))
                (source, None)
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
    | Some Checksum32, value -> crc32ValueWith encodeText value, None
    | Some AbsoluteValue, VInt Int64.MinValue -> raise SignedOutOfRange
    | Some AbsoluteValue, VInt value -> VInt(abs value), None
    | Some AbsoluteValue, VUInt value -> VUInt value, None
    | Some AbsoluteValue, VBit(_, value) -> VUInt value, None
    | Some AbsoluteValue, VDouble value -> VDouble(abs value), None
    | Some AbsoluteValue, VDecimal value -> VDecimal(abs value), None
    | Some AbsoluteValue, value ->
        let number, truncated = numericInputWithStatus value
        VDouble(abs number), truncated
    | _ -> value, None

let projectValueWith encodeText transform value =
    projectValueWithStatus (fun left right -> String.Equals(left, right, StringComparison.Ordinal)) encodeText transform value |> fst

let projectValue transform value =
    projectValueWith Text.Encoding.UTF8.GetBytes transform value
