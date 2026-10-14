/// Groups joined mutation sources by the physical table they write.
module internal Fsdb.MutationTargets

open Fsdb.Ast
open Fsdb.Storage

type Groups =
    { Tables: TableRef array
      RepresentativeSources: int array
      SourceGroups: Map<int, int> }

let physicalTableKey defaultDatabase (tableRef: TableRef) =
    tableRef.Database |> Option.defaultValue defaultDatabase, normalizeTableName tableRef.Table

/// Self-join aliases of one table share a single pending write batch.
let group defaultDatabase (targets: (int * TableRef) list) : Groups =
    let key = physicalTableKey defaultDatabase
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
