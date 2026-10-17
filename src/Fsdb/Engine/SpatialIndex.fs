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
      Height: int
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

let private height = function
    | Some node -> node.Height
    | None -> 0

let private node entry left right =
    { Entry = entry
      Height = 1 + max (height left) (height right)
      Left = left
      Right = right
      SubtreeMaxX = max entry.Bounds.MaxX (max (maxX left) (maxX right)) }

let private compareEntry (left: Entry<'id>) (right: Entry<'id>) =
    match Operators.compare left.Bounds.MinX right.Bounds.MinX with
    | 0 -> Operators.compare left.Id right.Id
    | comparison -> comparison

let private rotateRight root =
    match root.Left with
    | None -> root
    | Some pivot ->
        let right = node root.Entry pivot.Right root.Right
        node pivot.Entry pivot.Left (Some right)

let private rotateLeft root =
    match root.Right with
    | None -> root
    | Some pivot ->
        let left = node root.Entry root.Left pivot.Left
        node pivot.Entry (Some left) pivot.Right

let private balance root =
    let difference = height root.Left - height root.Right

    if difference > 1 then
        let left = root.Left.Value
        let root =
            if height left.Right > height left.Left then
                node root.Entry (Some(rotateLeft left)) root.Right
            else
                root
        rotateRight root
    elif difference < -1 then
        let right = root.Right.Value
        let root =
            if height right.Left > height right.Right then
                node root.Entry root.Left (Some(rotateRight right))
            else
                root
        rotateLeft root
    else
        root

let rec private insert (entry: Entry<'id>) (root: Node<'id> option) =
    match root with
    | None -> Some(node entry None None)
    | Some current ->
        match compareEntry entry current.Entry with
        | 0 -> Some(node entry current.Left current.Right)
        | comparison when comparison < 0 ->
            Some(balance (node current.Entry (insert entry current.Left) current.Right))
        | _ ->
            Some(balance (node current.Entry current.Left (insert entry current.Right)))

let rec private removeMinimum root =
    match root.Left with
    | None -> root.Entry, root.Right
    | Some left ->
        let minimum, remaining = removeMinimum left
        minimum, Some(balance (node root.Entry remaining root.Right))

let rec private delete (entry: Entry<'id>) (root: Node<'id> option) =
    match root with
    | None -> None
    | Some current ->
        match compareEntry entry current.Entry with
        | 0 ->
            match current.Left, current.Right with
            | None, other
            | other, None -> other
            | Some _, Some right ->
                let successor, remaining = removeMinimum right
                Some(balance (node successor current.Left remaining))
        | comparison when comparison < 0 ->
            Some(balance (node current.Entry (delete entry current.Left) current.Right))
        | _ ->
            Some(balance (node current.Entry current.Left (delete entry current.Right)))

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
      Root = insert entry index.Root }

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

/// Diagnostic depth of the spatial search tree. Traversal stays iterative even
/// when an existing index has a degenerate shape.
let maximumDepth (index: Index<'id>) =
    let rec visit maximum pending =
        match pending with
        | [] -> maximum
        | (None, _) :: rest -> visit maximum rest
        | (Some current, depth) :: rest ->
            visit (max maximum depth) ((current.Left, depth + 1) :: (current.Right, depth + 1) :: rest)

    visit 0 [ index.Root, 1 ]
