module Fsdb.SpatialIndex

open Fsdb.Value

type Relation =
    | Intersects
    | Within
    | Contains

type private Entry<'id> =
    { Id: 'id
      Bounds: GeometryBounds }

type private Node<'id> =
    { Entry: Entry<'id>
      Priority: uint64
      Left: Node<'id> option
      Right: Node<'id> option
      SubtreeMaxX: float }

type Index<'id when 'id: comparison> =
    private
        { Bounds: Map<'id, GeometryBounds>
          Root: Node<'id> option }

let empty<'id when 'id: comparison> : Index<'id> =
    { Bounds = Map.empty
      Root = None }

let private maxX = function
    | Some node -> node.SubtreeMaxX
    | None -> System.Double.NegativeInfinity

let private node entry priority left right =
    { Entry = entry
      Priority = priority
      Left = left
      Right = right
      SubtreeMaxX = max entry.Bounds.MaxX (max (maxX left) (maxX right)) }

let private compareEntry (left: Entry<'id>) (right: Entry<'id>) =
    match Operators.compare left.Bounds.MinX right.Bounds.MinX with
    | 0 -> Operators.compare left.Id right.Id
    | comparison -> comparison

let private priority id =
    let mutable value = uint64 (uint32 (hash id)) + 0x9e3779b97f4a7c15UL
    value <- (value ^^^ (value >>> 30)) * 0xbf58476d1ce4e5b9UL
    value <- (value ^^^ (value >>> 27)) * 0x94d049bb133111ebUL
    value ^^^ (value >>> 31)

let private rotateRight root =
    match root.Left with
    | None -> root
    | Some pivot ->
        let right = node root.Entry root.Priority pivot.Right root.Right
        node pivot.Entry pivot.Priority pivot.Left (Some right)

let private rotateLeft root =
    match root.Right with
    | None -> root
    | Some pivot ->
        let left = node root.Entry root.Priority root.Left pivot.Left
        node pivot.Entry pivot.Priority (Some left) pivot.Right

let rec private insert (entry: Entry<'id>) entryPriority (root: Node<'id> option) =
    match root with
    | None -> Some(node entry entryPriority None None)
    | Some current ->
        match compareEntry entry current.Entry with
        | 0 -> Some(node entry current.Priority current.Left current.Right)
        | comparison when comparison < 0 ->
            let updated = node current.Entry current.Priority (insert entry entryPriority current.Left) current.Right

            match updated.Left with
            | Some left when left.Priority > updated.Priority -> Some(rotateRight updated)
            | _ -> Some updated
        | _ ->
            let updated = node current.Entry current.Priority current.Left (insert entry entryPriority current.Right)

            match updated.Right with
            | Some right when right.Priority > updated.Priority -> Some(rotateLeft updated)
            | _ -> Some updated

let rec private merge left right =
    match left, right with
    | None, other
    | other, None -> other
    | Some left, Some right when left.Priority > right.Priority ->
        Some(node left.Entry left.Priority left.Left (merge left.Right (Some right)))
    | Some left, Some right ->
        Some(node right.Entry right.Priority (merge (Some left) right.Left) right.Right)

let rec private delete (entry: Entry<'id>) (root: Node<'id> option) =
    match root with
    | None -> None
    | Some current ->
        match compareEntry entry current.Entry with
        | 0 -> merge current.Left current.Right
        | comparison when comparison < 0 ->
            Some(node current.Entry current.Priority (delete entry current.Left) current.Right)
        | _ ->
            Some(node current.Entry current.Priority current.Left (delete entry current.Right))

let remove id (index: Index<'id>) =
    match Map.tryFind id index.Bounds with
    | None -> index
    | Some bounds ->
        let entry = { Id = id; Bounds = bounds }
        { Bounds = Map.remove id index.Bounds
          Root = delete entry index.Root }

let add id (bounds: GeometryBounds) (index: Index<'id>) =
    let index = remove id index
    let entry = { Id = id; Bounds = bounds }

    { Bounds = Map.add id bounds index.Bounds
      Root = insert entry (priority id) index.Root }

let private matches relation query candidate =
    match relation with
    | Intersects ->
        candidate.MinX <= query.MaxX
        && candidate.MaxX >= query.MinX
        && candidate.MinY <= query.MaxY
        && candidate.MaxY >= query.MinY
    | Within ->
        candidate.MinX >= query.MinX
        && candidate.MaxX <= query.MaxX
        && candidate.MinY >= query.MinY
        && candidate.MaxY <= query.MaxY
    | Contains ->
        candidate.MinX <= query.MinX
        && candidate.MaxX >= query.MaxX
        && candidate.MinY <= query.MinY
        && candidate.MaxY >= query.MaxY

let search relation (bounds: GeometryBounds) (index: Index<'id>) =
    let lowerMinX, upperMinX, requiredMaxX =
        match relation with
        | Intersects -> System.Double.NegativeInfinity, bounds.MaxX, bounds.MinX
        | Within -> bounds.MinX, bounds.MaxX, System.Double.NegativeInfinity
        | Contains -> System.Double.NegativeInfinity, bounds.MinX, bounds.MaxX

    let rec collect found = function
        | None -> found
        | Some current when current.SubtreeMaxX < requiredMaxX -> found
        | Some current when current.Entry.Bounds.MinX < lowerMinX -> collect found current.Right
        | Some current when current.Entry.Bounds.MinX > upperMinX -> collect found current.Left
        | Some current ->
            let found = collect found current.Left

            let found =
                if matches relation bounds current.Entry.Bounds then
                    Set.add current.Entry.Id found
                else
                    found

            collect found current.Right

    collect Set.empty index.Root

let count (index: Index<'id>) = Map.count index.Bounds
