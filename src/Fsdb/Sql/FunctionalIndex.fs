/// Built-in functional keys have one identity across parsing, planning,
/// maintenance, and metadata so adding a transform cannot update only part of
/// the index pipeline.
module internal Fsdb.Sql.FunctionalIndex

open System
open Fsdb.Ast
open Fsdb.Value

let builtins =
    [ "LOWER", Lowercase
      "UPPER", Uppercase
      "TRIM", Trimmed ]

let tryBuiltin (name: string) =
    builtins
    |> List.tryPick (fun (candidate, transform) ->
        if name.Equals(candidate, StringComparison.OrdinalIgnoreCase) then Some transform else None)

let tryBuiltinName transform =
    builtins
    |> List.tryPick (fun (name, candidate) -> if candidate = transform then Some name else None)

let isBuiltin transform = tryBuiltinName transform |> Option.isSome

let supportsColumnType transform columnType =
    if not (isBuiltin transform) then
        false
    else
        match columnType with
        | TChar _
        | TVarchar _
        | TBinary _
        | TVarBinary _ -> true
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

let projectValue transform value =
    match transform, value with
    | Some Lowercase, VString text -> VString(text.ToLowerInvariant())
    | Some Uppercase, VString text -> VString(text.ToUpperInvariant())
    | Some Trimmed, VString text -> VString(text.Trim(' '))
    | Some Trimmed, VBytes bytes -> VBytes(trimBinarySpaces bytes)
    | _ -> value
