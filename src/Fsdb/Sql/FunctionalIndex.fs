/// Built-in functional keys have one identity across parsing, planning,
/// maintenance, and metadata so adding a transform cannot update only part of
/// the index pipeline.
module internal Fsdb.Sql.FunctionalIndex

open System
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

let isBuiltin transform = tryBuiltinName transform |> Option.isSome

let supportsColumnType transform columnType =
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
        match columnType with
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
    | Expression _ -> false

let fixedKeyLength =
    function
    | CharacterLength
    | ByteLength
    | BitLength -> Some 8
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

let tryNormalizeProbe transform normalizeStored value =
    match transform with
    | Some CharacterLength
    | Some ByteLength
    | Some BitLength -> tryExactInt64 value |> Option.map VInt
    | _ -> normalizeStored value

let private mapTextOrBytes mapText mapBytes value =
    match tryRawBytes value with
    | Some bytes -> VBytes(mapBytes bytes)
    | None -> value |> toText |> Option.defaultValue "" |> mapText |> VString

let projectValueWith encodeText transform value =
    match transform, value with
    | Some _, VNull -> VNull
    | Some Lowercase, value -> mapTextOrBytes _.ToLowerInvariant() id value
    | Some Uppercase, value -> mapTextOrBytes _.ToUpperInvariant() id value
    | Some Trimmed, value -> mapTextOrBytes _.Trim(' ') trimBinarySpaces value
    | Some Reversed, value -> mapTextOrBytes reverseText Array.rev value
    | Some CharacterLength, value -> VInt(characterLength value)
    | Some ByteLength, value -> VInt(byteLengthWith encodeText value)
    | Some BitLength, value -> VInt(byteLengthWith encodeText value * 8L)
    | Some AbsoluteValue, VInt Int64.MinValue -> raise SignedOutOfRange
    | Some AbsoluteValue, VInt value -> VInt(abs value)
    | Some AbsoluteValue, VUInt value -> VUInt value
    | Some AbsoluteValue, VBit(_, value) -> VUInt value
    | Some AbsoluteValue, VDouble value -> VDouble(abs value)
    | Some AbsoluteValue, VDecimal value -> VDecimal(abs value)
    | Some AbsoluteValue, value -> VDouble(abs (toDouble value))
    | _ -> value

let projectValue transform value =
    projectValueWith Text.Encoding.UTF8.GetBytes transform value
