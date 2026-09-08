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
        Transform = CharacterLength } ]

let builtins =
    definitions
    |> List.collect (fun definition ->
        definition.CanonicalName :: definition.Aliases
        |> List.map (fun name -> name, definition.Transform))

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
    | CharacterLength ->
        match columnType with
        | TGeometry _
        | TVector _ -> false
        | _ -> true
    | Expression _ -> false

let fixedKeyLength =
    function
    | CharacterLength -> Some 8
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

let characterLength =
    function
    | VBytes bytes -> int64 bytes.Length
    | value -> value |> toText |> Option.map runeLength |> Option.defaultValue 0L

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
    | Some CharacterLength -> tryExactInt64 value |> Option.map VInt
    | _ -> normalizeStored value

let projectValue transform value =
    match transform, value with
    | Some Lowercase, VString text -> VString(text.ToLowerInvariant())
    | Some Uppercase, VString text -> VString(text.ToUpperInvariant())
    | Some Trimmed, VString text -> VString(text.Trim(' '))
    | Some Trimmed, VBytes bytes -> VBytes(trimBinarySpaces bytes)
    | Some Reversed, VString text -> VString(reverseText text)
    | Some Reversed, VBytes bytes -> VBytes(Array.rev bytes)
    | Some CharacterLength, VNull -> VNull
    | Some CharacterLength, value -> VInt(characterLength value)
    | _ -> value
