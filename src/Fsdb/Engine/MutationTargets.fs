/// Groups joined mutation sources by the physical table they write.
module internal Fsdb.MutationTargets

open Fsdb.Ast
open Fsdb.Storage

type Groups<'table> =
    { Tables: 'table array
      RepresentativeSources: int array
      SourceGroups: Map<int, int> }

let physicalTableKey defaultDatabase (tableRef: TableRef) =
    tableRef.Database |> Option.defaultValue defaultDatabase, normalizeTableName tableRef.Table

/// Self-join aliases of one table share a single pending write batch.
let groupBy key (targets: (int * 'table) list) : Groups<'table> =
    let representatives = targets |> List.distinctBy (snd >> key) |> Array.ofList

    let groupByTable =
        representatives
        |> Array.mapi (fun group (_, tableRef) -> key tableRef, group)
        |> Map.ofArray

    { Tables = representatives |> Array.map snd
      RepresentativeSources = representatives |> Array.map fst
      SourceGroups =
        targets
        |> List.map (fun (source, tableRef) -> source, Map.find (key tableRef) groupByTable)
        |> Map.ofList }

let group defaultDatabase targets =
    groupBy (physicalTableKey defaultDatabase) targets
