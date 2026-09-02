/// Active transaction snapshots shared by isolation visibility and metadata.
/// Registries follow the shared database roots, keeping independent stores
/// isolated while transaction snapshots come and go with their sessions.
module Fsdb.TransactionRegistry

open System.Collections.Concurrent
open System.Runtime.CompilerServices
open Fsdb.Storage

type Entry =
    { BaseCatalog: Catalog
      Snapshot: Store }

let private registries =
    ConditionalWeakTable<ConcurrentDictionary<string, Database ref>, ConcurrentDictionary<int, Entry>>()

let private registryFor (store: Store) =
    registries.GetValue(store.Databases, fun _ -> ConcurrentDictionary<int, Entry>())

let publish (store: Store) (connectionId: int) (entry: Entry) =
    (registryFor store).[connectionId] <- entry

let remove (store: Store) (connectionId: int) =
    match registries.TryGetValue store.Databases with
    | true, registry -> registry.TryRemove connectionId |> ignore
    | false, _ -> ()

let others (store: Store) (connectionId: int) =
    registryFor store
    |> Seq.filter (fun entry -> entry.Key <> connectionId)
    |> Seq.sortBy _.Key
    |> Seq.map _.Value
    |> List.ofSeq
