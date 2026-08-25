namespace Fsdb

open System
open System.Collections
open System.Collections.Generic

[<Struct>]
type RowId = private RowId of int

[<RequireQualifiedAccess>]
module RowId =
    let internal create value = RowId value
    let internal value (RowId value) = value

/// Builds one immutable row-store root without publishing partial changes.
[<Sealed>]
type RowStoreBuilder<'T> internal (liveCount: int, slots: PagedVector<'T option>) =
    let mutable count = liveCount
    let slots = slots.ToBuilder()

    member _.Count = count

    member _.TryFind(rowId: RowId) =
        let index = RowId.value rowId

        if index < 0 || index >= slots.Count then
            None
        else
            slots.[index]

    member this.Item
        with get (rowId: RowId) : 'T =
            match this.TryFind rowId with
            | Some item -> item
            | None -> raise (KeyNotFoundException(sprintf "Unknown row id %d." (RowId.value rowId)))
        and set (rowId: RowId) (item: 'T) =
            match this.TryFind rowId with
            | Some _ -> slots.[RowId.value rowId] <- Some item
            | None -> raise (KeyNotFoundException(sprintf "Unknown row id %d." (RowId.value rowId)))

    member _.Add(item: 'T) =
        let rowId = RowId.create slots.Count
        slots.Add(Some item)
        count <- count + 1
        rowId

    member this.AddRange(items: seq<'T>) =
        items |> Seq.map this.Add |> List.ofSeq

    member this.Remove(rowId: RowId) =
        match this.TryFind rowId with
        | None -> false
        | Some _ ->
            slots.[RowId.value rowId] <- None
            count <- count - 1
            true

    member internal _.Drain() = count, slots.DrainToImmutable()

/// An insertion-ordered immutable row heap with stable, non-reused identities.
/// ponytail: deleted slots remain addressable tombstones; reclaiming their
/// directory space needs an identity-to-slot indirection so compaction cannot
/// retarget a point lookup captured from an older root.
[<Sealed>]
type RowStore<'T> internal (liveCount: int, slots: PagedVector<'T option>) =
    static let empty = RowStore<'T>(0, PagedVector.empty)

    member _.Count = liveCount
    member _.Length = liveCount
    member _.IsEmpty = liveCount = 0
    /// Deleted slots retained to keep RowId stable across immutable roots.
    member _.TombstoneCount = slots.Count - liveCount

    member _.TryFind(rowId: RowId) =
        let index = RowId.value rowId

        if index < 0 || index >= slots.Count then
            None
        else
            slots.[index]

    member this.Item
        with get (rowId: RowId) : 'T =
            match this.TryFind rowId with
            | Some item -> item
            | None -> raise (KeyNotFoundException(sprintf "Unknown row id %d." (RowId.value rowId)))

    member this.Add(item: 'T) =
        let _, rows = this.Append item
        rows

    member this.AddRange(items: seq<'T>) =
        let _, rows = this.AppendRange items
        rows

    member this.Append(item: 'T) =
        let builder: RowStoreBuilder<'T> = this.ToBuilder()
        let rowId = builder.Add item
        let count, slots = builder.Drain()
        rowId, RowStore<'T>(count, slots)

    member this.AppendRange(items: seq<'T>) =
        let builder: RowStoreBuilder<'T> = this.ToBuilder()
        let rowIds = builder.AddRange items
        let count, slots = builder.Drain()
        rowIds, RowStore<'T>(count, slots)

    member this.SetItem(rowId: RowId, item: 'T) =
        let builder: RowStoreBuilder<'T> = this.ToBuilder()
        builder.[rowId] <- item
        let count, slots = builder.Drain()
        RowStore<'T>(count, slots)

    member this.Remove(rowId: RowId) =
        let builder: RowStoreBuilder<'T> = this.ToBuilder()
        builder.Remove rowId |> ignore
        let count, slots = builder.Drain()
        RowStore<'T>(count, slots)

    member _.ToBuilder() : RowStoreBuilder<'T> = RowStoreBuilder<'T>(liveCount, slots)

    member private _.Slots = slots

    member internal this.ChangesFrom(baseline: RowStore<'T>) =
        seq {
            for index in slots.ChangedIndices baseline.Slots do
                let rowId = RowId.create index
                let before = baseline.TryFind rowId
                let after = this.TryFind rowId

                if not (EqualityComparer<'T option>.Default.Equals(before, after)) then
                    yield rowId, before, after
        }

    member this.Indexed =
        seq {
            for index = 0 to slots.Count - 1 do
                let rowId = RowId.create index

                match this.TryFind rowId with
                | Some item -> yield rowId, item
                | None -> ()
        }

    member private this.Items = this.Indexed |> Seq.map snd

    interface IEnumerable<'T> with
        member this.GetEnumerator() = this.Items.GetEnumerator()

    interface IEnumerable with
        member this.GetEnumerator() = (this.Items :> IEnumerable).GetEnumerator()

    static member Empty = empty

    static member OfSeq(items: seq<'T>) =
        let builder = RowStoreBuilder<'T>(0, PagedVector.empty)
        builder.AddRange items |> ignore
        let count, slots = builder.Drain()
        RowStore<'T>(count, slots)

type RowStoreBuilder<'T> with
    member this.DrainToImmutable() =
        let count, slots = this.Drain()
        RowStore<'T>(count, slots)

[<RequireQualifiedAccess>]
module RowStore =
    let empty<'T> : RowStore<'T> = RowStore<'T>.Empty
    let ofSeq (items: seq<'T>) = RowStore<'T>.OfSeq items

    let map mapping (rows: RowStore<'T>) =
        let builder = rows.ToBuilder()

        for rowId, item in rows.Indexed do
            builder.[rowId] <- mapping item

        builder.DrainToImmutable()
