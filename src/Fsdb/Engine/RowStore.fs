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

type private RowSlot<'T> = RowId * 'T

/// Builds one immutable row-store root without publishing partial changes.
[<Sealed>]
type RowStoreBuilder<'T> internal (
    liveCount: int,
    nextRowId: int,
    positions: Map<RowId, int>,
    slots: PagedVector<RowSlot<'T> option>,
    layoutIdentity: obj
) =
    let mutable count = liveCount
    let mutable nextId = nextRowId
    let mutable positions = positions
    let slots = slots.ToBuilder()

    member _.Count = count

    member _.TryFind(rowId: RowId) =
        positions
        |> Map.tryFind rowId
        |> Option.bind (fun index -> slots.[index] |> Option.map snd)

    member this.Item
        with get (rowId: RowId) : 'T =
            match this.TryFind rowId with
            | Some item -> item
            | None -> raise (KeyNotFoundException(sprintf "Unknown row id %d." (RowId.value rowId)))
        and set (rowId: RowId) (item: 'T) =
            match Map.tryFind rowId positions with
            | Some index -> slots.[index] <- Some(rowId, item)
            | None -> raise (KeyNotFoundException(sprintf "Unknown row id %d." (RowId.value rowId)))

    member _.Add(item: 'T) =
        if nextId = Int32.MaxValue then
            raise (InvalidOperationException "The row identity space is exhausted.")

        let rowId = RowId.create nextId
        let index = slots.Count
        slots.Add(Some(rowId, item))
        positions <- Map.add rowId index positions
        nextId <- nextId + 1
        count <- count + 1
        rowId

    member this.AddRange(items: seq<'T>) =
        items |> Seq.map this.Add |> List.ofSeq

    member _.Remove(rowId: RowId) =
        match Map.tryFind rowId positions with
        | None -> false
        | Some index ->
            slots.[index] <- None
            positions <- Map.remove rowId positions
            count <- count - 1
            true

    member internal _.Drain() = count, nextId, positions, slots.DrainToImmutable(), layoutIdentity

/// An insertion-ordered immutable row heap with stable, non-reused identities.
[<Sealed>]
type RowStore<'T> internal (
    liveCount: int,
    nextRowId: int,
    positions: Map<RowId, int>,
    slots: PagedVector<RowSlot<'T> option>,
    layoutIdentity: obj
) =
    static let empty = RowStore<'T>(0, 0, Map.empty, PagedVector.empty, obj ())

    member _.Count = liveCount
    member _.Length = liveCount
    member _.IsEmpty = liveCount = 0
    member _.TombstoneCount = slots.Count - liveCount

    member private _.DrainBuilder(builder: RowStoreBuilder<'T>) =
        let count, nextRowId, positions, slots, layoutIdentity = builder.Drain()
        RowStore<'T>(count, nextRowId, positions, slots, layoutIdentity)

    member _.TryFind(rowId: RowId) =
        positions
        |> Map.tryFind rowId
        |> Option.bind (fun index -> slots.[index] |> Option.map snd)

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
        rowId, this.DrainBuilder builder

    member this.AppendRange(items: seq<'T>) =
        let builder: RowStoreBuilder<'T> = this.ToBuilder()
        let rowIds = builder.AddRange items
        rowIds, this.DrainBuilder builder

    member this.SetItem(rowId: RowId, item: 'T) =
        let builder: RowStoreBuilder<'T> = this.ToBuilder()
        builder.[rowId] <- item
        this.DrainBuilder builder

    member this.Remove(rowId: RowId) =
        let builder: RowStoreBuilder<'T> = this.ToBuilder()
        builder.Remove rowId |> ignore
        this.DrainBuilder builder

    member _.ToBuilder() : RowStoreBuilder<'T> =
        RowStoreBuilder<'T>(liveCount, nextRowId, positions, slots, layoutIdentity)

    member private _.Slots = slots
    member private _.Positions = positions
    member private _.LayoutIdentity = layoutIdentity

    member internal this.Compact() =
        if this.TombstoneCount = 0 then
            this
        else
            let denseSlots = PagedVectorBuilder<RowSlot<'T> option>(0, Map.empty)
            let mutable densePositions = Map.empty

            for rowId, item in this.Indexed do
                densePositions <- Map.add rowId denseSlots.Count densePositions
                denseSlots.Add(Some(rowId, item))

            RowStore<'T>(liveCount, nextRowId, densePositions, denseSlots.DrainToImmutable(), obj ())

    member internal this.CompactIfNeeded() =
        let tombstones = this.TombstoneCount

        if tombstones >= 256 && int64 tombstones * 4L >= int64 slots.Count then
            this.Compact()
        else
            this

    member internal this.ChangesFrom(baseline: RowStore<'T>) =
        let changed rowId before after =
            if EqualityComparer<'T option>.Default.Equals(before, after) then
                None
            else
                Some(rowId, before, after)

        if obj.ReferenceEquals(layoutIdentity, baseline.LayoutIdentity) then
            seq {
                for index in slots.ChangedIndices baseline.Slots do
                    let before =
                        if index < baseline.Slots.Count then
                            baseline.Slots.[index]
                        else
                            None

                    let after = if index < slots.Count then slots.[index] else None

                    match before, after with
                    | Some(beforeId, beforeItem), Some(afterId, afterItem) when beforeId = afterId ->
                        match changed beforeId (Some beforeItem) (Some afterItem) with
                        | Some change -> yield change
                        | None -> ()
                    | Some(beforeId, beforeItem), Some(afterId, afterItem) ->
                        yield beforeId, Some beforeItem, None
                        yield afterId, None, Some afterItem
                    | Some(rowId, item), None -> yield rowId, Some item, None
                    | None, Some(rowId, item) -> yield rowId, None, Some item
                    | None, None -> ()
            }
        else
            seq {
                let rowIds =
                    Seq.append positions.Keys baseline.Positions.Keys
                    |> Set.ofSeq

                for rowId in rowIds do
                    match changed rowId (baseline.TryFind rowId) (this.TryFind rowId) with
                    | Some change -> yield change
                    | None -> ()
            }

    member this.Indexed =
        seq {
            for slot in slots do
                match slot with
                | Some(rowId, item) -> yield rowId, item
                | None -> ()
        }

    member private this.Items = this.Indexed |> Seq.map snd

    interface IEnumerable<'T> with
        member this.GetEnumerator() = this.Items.GetEnumerator()

    interface IEnumerable with
        member this.GetEnumerator() = (this.Items :> IEnumerable).GetEnumerator()

    static member Empty = empty

    static member OfSeq(items: seq<'T>) =
        let builder = RowStoreBuilder<'T>(0, 0, Map.empty, PagedVector.empty, obj ())
        builder.AddRange items |> ignore
        let count, nextRowId, positions, slots, layoutIdentity = builder.Drain()
        RowStore<'T>(count, nextRowId, positions, slots, layoutIdentity)

type RowStoreBuilder<'T> with
    member this.DrainToImmutable() =
        let count, nextRowId, positions, slots, layoutIdentity = this.Drain()
        RowStore<'T>(count, nextRowId, positions, slots, layoutIdentity)

[<RequireQualifiedAccess>]
module RowStore =
    let empty<'T> : RowStore<'T> = RowStore<'T>.Empty
    let ofSeq (items: seq<'T>) = RowStore<'T>.OfSeq items

    let map mapping (rows: RowStore<'T>) =
        let builder = rows.ToBuilder()

        for rowId, item in rows.Indexed do
            builder.[rowId] <- mapping item

        builder.DrainToImmutable()
