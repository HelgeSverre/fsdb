module Fsdb.SpatialIndex

open System
open System.Collections.Immutable
open Fsdb.Value

type Relation =
    | Intersects
    | Within
    | Contains

type private Marker<'id when 'id: comparison> =
    | Before
    | Item of 'id
    | After

[<CustomEquality; CustomComparison>]
type private AxisEntry<'id when 'id: comparison> =
    { Coordinate: float
      Marker: Marker<'id> }

    override this.Equals other =
        match other with
        | :? AxisEntry<'id> as other ->
            Operators.compare this.Coordinate other.Coordinate = 0
            && Operators.compare this.Marker other.Marker = 0
        | _ -> false

    override this.GetHashCode() = hash (this.Coordinate, this.Marker)

    interface IComparable with
        member this.CompareTo other =
            match other with
            | :? AxisEntry<'id> as other ->
                match Operators.compare this.Coordinate other.Coordinate with
                | 0 -> Operators.compare this.Marker other.Marker
                | comparison -> comparison
            | _ -> invalidArg "other" "AxisEntry expected"

type Index<'id when 'id: comparison> =
    private
        { Bounds: Map<'id, GeometryBounds>
          MinX: ImmutableSortedSet<AxisEntry<'id>>
          MinY: ImmutableSortedSet<AxisEntry<'id>>
          MaxX: ImmutableSortedSet<AxisEntry<'id>>
          MaxY: ImmutableSortedSet<AxisEntry<'id>> }

let empty<'id when 'id: comparison> : Index<'id> =
    { Bounds = Map.empty
      MinX = ImmutableSortedSet.Empty
      MinY = ImmutableSortedSet.Empty
      MaxX = ImmutableSortedSet.Empty
      MaxY = ImmutableSortedSet.Empty }

let private entry coordinate id =
    { Coordinate = coordinate
      Marker = Item id }

let remove id (index: Index<'id>) =
    match Map.tryFind id index.Bounds with
    | None -> index
    | Some bounds ->
        { Bounds = Map.remove id index.Bounds
          MinX = index.MinX.Remove(entry bounds.MinX id)
          MinY = index.MinY.Remove(entry bounds.MinY id)
          MaxX = index.MaxX.Remove(entry bounds.MaxX id)
          MaxY = index.MaxY.Remove(entry bounds.MaxY id) }

let add id (bounds: GeometryBounds) (index: Index<'id>) =
    let index = remove id index
    { Bounds = Map.add id bounds index.Bounds
      MinX = index.MinX.Add(entry bounds.MinX id)
      MinY = index.MinY.Add(entry bounds.MinY id)
      MaxX = index.MaxX.Add(entry bounds.MaxX id)
      MaxY = index.MaxY.Add(entry bounds.MaxY id) }

let private ids (entries: seq<AxisEntry<'id>>) =
    entries
    |> Seq.choose (fun entry ->
        match entry.Marker with
        | Item id -> Some id
        | Before
        | After -> None)
    |> Set.ofSeq

let private insertionIndex entry (entries: ImmutableSortedSet<AxisEntry<'id>>) =
    let position = entries.IndexOf entry
    if position < 0 then ~~~position else position

let private atMost coordinate (entries: ImmutableSortedSet<AxisEntry<'id>>) =
    let afterLast = insertionIndex { Coordinate = coordinate; Marker = After } entries
    Seq.init afterLast (fun index -> entries.[index])
    |> ids

let private atLeast coordinate (entries: ImmutableSortedSet<AxisEntry<'id>>) =
    let first = insertionIndex { Coordinate = coordinate; Marker = Before } entries
    Seq.init (entries.Count - first) (fun index -> entries.[first + index])
    |> ids

let private intersect candidates =
    candidates
    |> List.sortBy Set.count
    |> function
        | [] -> Set.empty
        | smallest :: rest -> rest |> List.fold Set.intersect smallest

let search relation (bounds: GeometryBounds) (index: Index<'id>) =
    match relation with
    | Intersects ->
        [ atMost bounds.MaxX index.MinX;
          atLeast bounds.MinX index.MaxX;
          atMost bounds.MaxY index.MinY;
          atLeast bounds.MinY index.MaxY ]
    | Within ->
        [ atLeast bounds.MinX index.MinX;
          atMost bounds.MaxX index.MaxX;
          atLeast bounds.MinY index.MinY;
          atMost bounds.MaxY index.MaxY ]
    | Contains ->
        [ atMost bounds.MinX index.MinX;
          atLeast bounds.MaxX index.MaxX;
          atMost bounds.MinY index.MinY;
          atLeast bounds.MaxY index.MaxY ]
    |> intersect

let count (index: Index<'id>) = Map.count index.Bounds
