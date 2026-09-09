/// Built-in functional keys have one identity across parsing, planning,
/// maintenance, and metadata so adding a transform cannot update only part of
/// the index pipeline.
module internal Fsdb.Sql.FunctionalIndex

open System
open System.Globalization
open Fsdb.Ast
open Fsdb.Value

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
      { CanonicalName = "ABS"
        Aliases = []
        Transform = AbsoluteValue } ]

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
    match transform with
    | Lowercase
    | Uppercase
    | Trimmed
    | Reversed ->
        match columnType with
        | TChar _
        | TVarchar _
        | TBinary _
        | TVarBinary _ -> true
        | _ -> false
    | CharacterLength
    | ByteLength
    | BitLength ->
        match columnType with
        | TGeometry _
        | TVector _ -> false
        | _ -> true
    | AbsoluteValue ->
        isNumeric columnType || isTextOrBinary columnType
    | Expression _ -> false

let private isTextTransform = function
    | Lowercase
    | Uppercase
    | Trimmed
    | Reversed -> true
    | _ -> false

let private isLengthTransform = function
    | CharacterLength
    | ByteLength
    | BitLength -> true
    | _ -> false

let supportsColumnType transform columnType =
    match transform with
    | Expression expression ->
        tryPhysicalExpression expression
        |> Option.exists (fun physical ->
            let transforms = physical.Calls |> List.map snd

            match transforms, List.rev transforms with
            | first :: _, length :: inner when isLengthTransform length && List.forall isTextTransform inner ->
                if inner.IsEmpty then
                    supportsSingleTransform length columnType
                else
                    supportsSingleTransform first columnType
            | first :: _, _ when List.forall isTextTransform transforms -> supportsSingleTransform first columnType
            | _ :: _, _ when List.forall ((=) AbsoluteValue) transforms -> supportsSingleTransform AbsoluteValue columnType
            | _ -> false)
    | transform -> supportsSingleTransform transform columnType

let rec fixedKeyLength = function
    | CharacterLength
    | ByteLength
    | BitLength -> Some 8
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
    match tryRawBytes value with
    | Some bytes -> int64 bytes.Length
    | None -> value |> toText |> Option.map runeLength |> Option.defaultValue 0L

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

let rec tryNormalizeProbe columnType transform normalizeStored value =
    match transform, value with
    | Some AbsoluteValue, VNull -> Some VNull
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
    | Some CharacterLength, _
    | Some ByteLength, _
    | Some BitLength, _ -> tryExactInt64 value |> Option.map VInt
    | Some(Expression expression), _ ->
        tryPhysicalExpression expression
        |> Option.bind (fun physical ->
            let transforms = physical.Calls |> List.map snd

            match List.tryLast transforms with
            | Some transform when isLengthTransform transform -> tryExactInt64 value |> Option.map VInt
            | Some AbsoluteValue when List.forall ((=) AbsoluteValue) transforms ->
                tryNormalizeProbe columnType (Some AbsoluteValue) normalizeStored value
            | Some _ -> normalizeStored value
            | None -> None)
    | _ -> normalizeStored value

let private mapTextOrBytes mapText mapBytes value =
    match tryRawBytes value with
    | Some bytes -> VBytes(mapBytes bytes)
    | None -> value |> toText |> Option.defaultValue "" |> mapText |> VString

let rec projectValueWithStatus encodeText transform value =
    match transform, value with
    | Some _, VNull -> VNull, None
    | Some Lowercase, value -> mapTextOrBytes _.ToLowerInvariant() id value, None
    | Some Uppercase, value -> mapTextOrBytes _.ToUpperInvariant() id value, None
    | Some Trimmed, value -> mapTextOrBytes _.Trim(' ') trimBinarySpaces value, None
    | Some Reversed, value -> mapTextOrBytes reverseText Array.rev value, None
    | Some CharacterLength, value -> VInt(characterLength value), None
    | Some ByteLength, value -> VInt(byteLengthWith encodeText value), None
    | Some BitLength, value -> VInt(byteLengthWith encodeText value * 8L), None
    | Some AbsoluteValue, VInt Int64.MinValue -> raise SignedOutOfRange
    | Some AbsoluteValue, VInt value -> VInt(abs value), None
    | Some AbsoluteValue, VUInt value -> VUInt value, None
    | Some AbsoluteValue, VBit(_, value) -> VUInt value, None
    | Some AbsoluteValue, VDouble value -> VDouble(abs value), None
    | Some AbsoluteValue, VDecimal value -> VDecimal(abs value), None
    | Some AbsoluteValue, ((VString _ | VBytes _) as value) ->
        let text = value |> toText |> Option.defaultValue ""
        let number, truncated = coerceLeadingDouble text
        VDouble(abs number), (if truncated then Some text else None)
    | Some AbsoluteValue, value -> VDouble(abs (toDouble value)), None
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
