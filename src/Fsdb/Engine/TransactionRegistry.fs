/// Active transaction snapshots shared by isolation visibility and metadata.
/// Registries follow the shared commit domain, keeping independent stores
/// isolated while remaining visible through their private snapshots.
module Fsdb.TransactionRegistry

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open Fsdb.Storage

type Metadata =
    { Id: uint64
      Started: DateTime
      Isolation: string
      ReadOnly: bool
      UniqueChecks: bool
      ForeignKeyChecks: bool
      RowsModified: uint64
      LockStructs: uint64 }

type Entry =
    { BaseCatalog: Catalog
      Snapshot: Store
      Metadata: Metadata }

let private registries =
    ConditionalWeakTable<obj, ConcurrentDictionary<int, Entry>>()

let private registryFor (store: Store) =
    registries.GetValue(store.CommitLock, fun _ -> ConcurrentDictionary<int, Entry>())

let publish (store: Store) (connectionId: int) (entry: Entry) =
    let registry = registryFor store

    registry.AddOrUpdate(
        connectionId,
        entry,
        fun _ current ->
            if current.Metadata.Id = entry.Metadata.Id then
                { entry with
                    Metadata =
                        { entry.Metadata with
                            Started = current.Metadata.Started } }
            else
                entry
    )
    |> ignore

let remove (store: Store) (connectionId: int) =
    match registries.TryGetValue store.CommitLock with
    | true, registry -> registry.TryRemove connectionId |> ignore
    | false, _ -> ()

let entries (store: Store) =
    registryFor store
    |> Seq.map (fun entry -> entry.Key, entry.Value)
    |> Seq.sortBy fst
    |> List.ofSeq

let others (store: Store) (connectionId: int) =
    entries store
    |> List.choose (fun (id, entry) -> if id = connectionId then None else Some entry)
