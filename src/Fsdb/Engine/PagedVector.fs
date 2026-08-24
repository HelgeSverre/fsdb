namespace Fsdb

open System
open System.Collections
open System.Collections.Generic

module private Page =
    [<Literal>]
    let Size = 256

[<Sealed>]
type PagedVectorBuilder<'T> internal (initialCount: int, initialPages: Map<int, 'T array>) =
    let mutable count = initialCount
    let mutable pages = initialPages
    let mutable writablePages = Set.empty
    let mutable drained = false

    let ensureActive () =
        if drained then
            raise (InvalidOperationException "The builder has already been drained.")

    let ensureWritable pageIndex =
        ensureActive ()

        if not (Set.contains pageIndex writablePages) then
            let page =
                match Map.tryFind pageIndex pages with
                | Some existing -> Array.copy existing
                | None -> Array.zeroCreate Page.Size

            pages <- Map.add pageIndex page pages
            writablePages <- Set.add pageIndex writablePages

        pages.[pageIndex]

    member _.Count = count

    member _.Item
        with get (index: int) : 'T =
            ensureActive ()

            if index < 0 || index >= count then
                raise (ArgumentOutOfRangeException(nameof index))

            pages.[index / Page.Size].[index % Page.Size]
        and set (index: int) (item: 'T) =
            ensureActive ()

            if index < 0 || index >= count then
                raise (ArgumentOutOfRangeException(nameof index))

            let page = ensureWritable (index / Page.Size)
            page.[index % Page.Size] <- item

    member _.Add(item: 'T) : unit =
        let page = ensureWritable (count / Page.Size)
        page.[count % Page.Size] <- item
        count <- count + 1

    member this.AddRange(items: seq<'T>) : unit =
        for item in items do
            this.Add item

    member internal _.Drain() =
        ensureActive ()
        drained <- true
        count, pages

[<Sealed>]
type PagedVector<'T> internal (count: int, pages: Map<int, 'T array>) =
    static let empty = PagedVector<'T>(0, Map.empty)

    member _.Count = count
    member _.Length = count
    member _.IsEmpty = count = 0

    member _.Item
        with get (index: int) : 'T =
            if index < 0 || index >= count then
                raise (ArgumentOutOfRangeException(nameof index))

            pages.[index / Page.Size].[index % Page.Size]

    member this.Add(item: 'T) : PagedVector<'T> =
        let builder: PagedVectorBuilder<'T> = this.ToBuilder()
        builder.Add item
        let count, pages = builder.Drain()
        PagedVector<'T>(count, pages)

    member this.AddRange(items: seq<'T>) : PagedVector<'T> =
        let builder: PagedVectorBuilder<'T> = this.ToBuilder()
        builder.AddRange items
        let count, pages = builder.Drain()
        PagedVector<'T>(count, pages)

    member this.SetItem(index: int, item: 'T) : PagedVector<'T> =
        let builder: PagedVectorBuilder<'T> = this.ToBuilder()
        builder.[index] <- item
        let count, pages = builder.Drain()
        PagedVector<'T>(count, pages)

    member _.ToBuilder() : PagedVectorBuilder<'T> = PagedVectorBuilder<'T>(count, pages)

    member private _.TryPage(pageIndex: int) = Map.tryFind pageIndex pages

    member internal _.ChangedIndices(other: PagedVector<'T>) =
        seq {
            let length = max count other.Count

            if length > 0 then
                for pageIndex = 0 to (length - 1) / Page.Size do
                    match Map.tryFind pageIndex pages, other.TryPage pageIndex with
                    | Some left, Some right when obj.ReferenceEquals(left, right) -> ()
                    | _ ->
                        let first = pageIndex * Page.Size
                        let last = min length (first + Page.Size) - 1

                        for index = first to last do
                            yield index
        }

    member private _.Items =
        seq {
            if count > 0 then
                for pageIndex = 0 to (count - 1) / Page.Size do
                    let page = pages.[pageIndex]
                    let length = min Page.Size (count - pageIndex * Page.Size)

                    for offset = 0 to length - 1 do
                        yield page.[offset]
        }

    interface IEnumerable<'T> with
        member this.GetEnumerator() = this.Items.GetEnumerator()

    interface IEnumerable with
        member this.GetEnumerator() = (this.Items :> IEnumerable).GetEnumerator()

    static member Empty = empty

    static member OfSeq(items: seq<'T>) : PagedVector<'T> =
        let builder = PagedVectorBuilder<'T>(0, Map.empty)
        builder.AddRange items
        let count, pages = builder.Drain()
        PagedVector<'T>(count, pages)

type PagedVectorBuilder<'T> with
    member this.DrainToImmutable() : PagedVector<'T> =
        let count, pages = this.Drain()
        PagedVector<'T>(count, pages)

[<RequireQualifiedAccess>]
module PagedVector =
    let empty<'T> : PagedVector<'T> = PagedVector<'T>.Empty
    let ofSeq (items: seq<'T>) : PagedVector<'T> = PagedVector<'T>.OfSeq items
