/// Active transaction snapshots shared by isolation visibility and metadata.
/// Registries follow the shared commit domain, keeping independent stores
/// isolated while remaining visible through their private snapshots.
module Fsdb.TransactionRegistry

open System.Collections.Concurrent
open System.Runtime.CompilerServices
open Fsdb.Storage

type Entry =
    { BaseCatalog: Catalog
      Snapshot: Store }

let private registries =
    ConditionalWeakTable<obj, ConcurrentDictionary<int, Entry>>()

let private registryFor (store: Store) =
    registries.GetValue(store.CommitLock, fun _ -> ConcurrentDictionary<int, Entry>())

let publish (store: Store) (connectionId: int) (entry: Entry) =
    (registryFor store).[connectionId] <- entry

let remove (store: Store) (connectionId: int) =
    match registries.TryGetValue store.CommitLock with
    | true, registry -> registry.TryRemove connectionId |> ignore
    | false, _ -> ()

let others (store: Store) (connectionId: int) =
    registryFor store
    |> Seq.filter (fun entry -> entry.Key <> connectionId)
    |> Seq.sortBy _.Key
    |> Seq.map _.Value
    |> List.ofSeq
