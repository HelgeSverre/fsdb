module Fsdb.ForeignKeyIndexes

open System
open Fsdb.Ast

let private sameName left right = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

let private hasPrefix columns candidate =
    List.length candidate >= List.length columns
    && List.forall2 sameName columns (List.take columns.Length candidate)

let supports (columns: string list) (index: IndexDef) =
    let prefix = index.KeyColumns |> List.truncate columns.Length
    index.Kind = BTree
    && prefix.Length = columns.Length
    && List.forall2 (fun (key: IndexColumn) name ->
        key.PrefixLength.IsNone && key.Transform.IsNone && sameName key.Name name) prefix columns

/// Adds only uncovered references; equivalent generated indexes retain the last declaration.
let complete (indexes: IndexDef list) (declarations: (string option * string list) list) =
    let declared = List.indexed declarations
    let needed =
        declared
        |> List.filter (fun (position, (_, columns)) ->
            not columns.IsEmpty
            && not (indexes |> List.exists (supports columns))
            && not (declared |> List.exists (fun (otherPosition, (_, otherColumns)) ->
                hasPrefix columns otherColumns
                && (otherColumns.Length > columns.Length || otherPosition > position))))
    needed
    |> List.fold (fun (indexes: IndexDef list) (_, (requestedName, columns)) ->
        let name =
            requestedName
            |> Option.defaultWith (fun () ->
                let baseName = List.head columns
                let rec available suffix =
                    let candidate = if suffix = 1 then baseName else baseName + "_" + string suffix
                    if indexes |> List.exists (fun index -> sameName index.Name candidate) then available (suffix + 1)
                    else candidate
                available 1)
        indexes @
            [ { Name = name
                GeneratedForForeignKey = true
                KeyColumns = indexColumns columns
                Unique = false
                Visible = true
                Kind = BTree } ]) indexes

/// Explicit indexes win ties; equal generated indexes retain the last declaration.
let removeRedundantGenerated (indexes: IndexDef list) =
    let positioned = List.indexed indexes
    positioned |> List.choose (fun (position, index) ->
        let redundant =
            index.GeneratedForForeignKey
            && (positioned |> List.exists (fun (otherPosition, other) ->
                position <> otherPosition
                && supports index.Columns other
                && (not other.GeneratedForForeignKey || other.Columns.Length > index.Columns.Length || otherPosition > position)))
        if redundant then None else Some index)
