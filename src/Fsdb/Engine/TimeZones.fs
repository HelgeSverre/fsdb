/// Named-zone lookup uses catalog data, never the host's unrelated zone database.
module Fsdb.TimeZones

open System
open Fsdb.Storage
open Fsdb.Temporal
open Fsdb.Value

let private (|Integer|_|) = function
    | VInt value -> Some value
    | VUInt value when value <= uint64 Int64.MaxValue -> Some(int64 value)
    | _ -> None

let private load (store: Store) name =
    let database = store.Catalog |> Map.tryFind "mysql" |> Option.defaultValue Map.empty
    let rows table =
        database
        |> Map.tryFind table
        |> Option.map (fun table -> table.RowsArray |> Seq.toList)
        |> Option.defaultValue []

    rows "time_zone_name"
    |> List.tryPick (function
        | [| VString storedName; Integer id |] when String.Equals(storedName, name, StringComparison.OrdinalIgnoreCase) -> Some id
        | _ -> None)
    |> Option.bind (fun id ->
        let supported =
            rows "time_zone"
            |> List.exists (function
                | [| Integer zoneId; VString "N" |] -> zoneId = id
                | _ -> false)
        let types =
            rows "time_zone_transition_type"
            |> List.choose (function
                | [| Integer zoneId; Integer typeId; Integer offset; Integer dst; _ |]
                    when zoneId = id && offset >= int64 Int32.MinValue && offset <= int64 Int32.MaxValue ->
                    Some(typeId, (int offset, dst <> 0L))
                | _ -> None)
            |> Map.ofList
        let initial =
            types |> Map.toList |> List.tryFind (fun (_, (_, dst)) -> not dst)
            |> Option.orElseWith (fun () -> types |> Map.toList |> List.tryHead)
        let transitions =
            rows "time_zone_transition"
            |> List.choose (function
                | [| Integer zoneId; Integer instant; Integer typeId |] when zoneId = id -> Some(instant, typeId)
                | _ -> None)
        match initial with
        | Some(_, (initialOffset, _)) when supported && transitions |> List.forall (fun (_, typeId) -> Map.containsKey typeId types) ->
            transitions
            |> List.map (fun (instant, typeId) -> instant, fst types.[typeId])
            |> namedTimeZone name initialOffset
            |> NamedTimeZone
            |> Some
        | _ -> None)

let resolve (store: Store) name =
    match trySqlTimeZone name with
    | Some zone -> Some zone
    | None ->
        match store.TimeZones.TryGetValue name with
        | true, zone -> Some zone
        | _ -> load store name |> Option.map (fun zone -> store.TimeZones.GetOrAdd(name, zone))
