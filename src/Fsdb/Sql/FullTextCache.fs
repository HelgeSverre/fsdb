module internal Fsdb.FullTextCache

open System

type private Color = Red | Black

type private Tree =
    | Empty
    | Node of Color * Tree * key: string * text: string * Tree

type Terms =
    private
        { Tree: Tree
          LastInserted: string list }

let empty = { Tree = Empty; LastInserted = [] }

let private isRed = function Node(Red, _, _, _, _) -> true | _ -> false

let private blacken = function
    | Node(Red, left, key, text, right) -> Node(Black, left, key, text, right)
    | tree -> tree

let private hasRedChild = function
    | Node(_, left, _, _, right) -> isRed left || isRed right
    | Empty -> false

// Prefix lookup stops at the first match on the descent, so the insertion
// rotations matter as well as the resulting term order.
let private balance color left key text right =
    match color, left, right with
    | Black, Node(Red, _, _, _, _), Node(Red, _, _, _, _)
        when hasRedChild left || hasRedChild right ->
        Node(Red, blacken left, key, text, blacken right)
    | Black, Node(Red, Node(Red, a, x, xt, b), y, yt, c), d
    | Black, Node(Red, a, x, xt, Node(Red, b, y, yt, c)), d ->
        Node(Black, Node(Red, a, x, xt, b), y, yt, Node(Red, c, key, text, d))
    | Black, a, Node(Red, Node(Red, b, y, yt, c), z, zt, d)
    | Black, a, Node(Red, b, y, yt, Node(Red, c, z, zt, d)) ->
        Node(Black, Node(Red, a, key, text, b), y, yt, Node(Red, c, z, zt, d))
    | _ -> Node(color, left, key, text, right)

let add key text terms =
    let rec insert = function
        | Empty -> Some(Node(Red, Empty, key, text, Empty))
        | Node(color, left, existing, spelling, right) ->
            match String.CompareOrdinal(key, existing) with
            | comparison when comparison < 0 ->
                insert left |> Option.map (fun left -> balance color left existing spelling right)
            | comparison when comparison > 0 ->
                insert right |> Option.map (balance color left existing spelling)
            | _ -> None

    match insert terms.Tree with
    | None -> terms
    | Some tree -> { Tree = blacken tree; LastInserted = text :: terms.LastInserted }

let inInsertionOrder terms = List.rev terms.LastInserted

[<Struct>]
type PostingTail =
    { LastDocument: int64
      UsedBytes: int64
      Capacity: int64
      EarlierSegmentEnds: int64 list }

type Budget =
    { Bytes: int64
      Tails: Map<string, PostingTail> }

let emptyBudget = { Bytes = 0L; Tails = Map.empty }

let private emptyTail = { LastDocument = 0L; UsedBytes = 0L; Capacity = 0L; EarlierSegmentEnds = [] }

let rec encodedLength delta =
    if delta < 128L then 1L else 1L + encodedLength (delta >>> 7)

let addDocument order (words: (string * int * int64)[]) (budget: Budget) =
    if words.Length = 0 then budget
    else
        // InnoDB accounts for each document's statistics and each new term's
        // tree entry and initial node vector on the 64-bit server.
        ({ budget with Bytes = budget.Bytes + 16L }, words)
        ||> Array.fold (fun current (key, spellingBytes, positionBytes) ->
            let tail, overhead =
                match Map.tryFind key current.Tails with
                | None -> emptyTail, 336L + int64 spellingBytes + 56L
                | Some tail when tail.UsedBytes > 65536L ->
                    { emptyTail with EarlierSegmentEnds = tail.LastDocument :: tail.EarlierSegmentEnds }, 56L
                | Some tail -> tail, 0L
            let size = encodedLength (order - tail.LastDocument) + positionBytes + 1L
            let used = tail.UsedBytes + size
            let capacity, growth =
                if used <= tail.Capacity then tail.Capacity, 0L
                else
                    let allocated = if used < 48L then (used / 16L + 1L) * 16L else used * 6L / 5L
                    allocated, allocated - tail.UsedBytes
            { Bytes = current.Bytes + overhead + growth
              Tails = Map.add key { tail with LastDocument = order; UsedBytes = used; Capacity = capacity } current.Tails })

let boundedSegments lower upper earlierEnds (orders: int64 seq) =
    let ends = earlierEnds |> List.rev |> List.toArray
    let segments = Array.create (ends.Length + 1) (struct (Int64.MaxValue, 0L, 0L))
    for order in orders do
        let found = Array.BinarySearch(ends, order)
        let segment = if found < 0 then ~~~found else found
        let struct (first, last, count) = segments.[segment]
        segments.[segment] <- struct (min first order, max last order, count + 1L)

    // A bound reads each overlapping segment in full, including deleted postings.
    (struct (Int64.MaxValue, 0L, 0L), segments)
    ||> Array.fold (fun (struct (first, last, total)) (struct (start, finish, count)) ->
        if start <= upper && finish >= lower then
            struct (min first start, max last finish, total + count)
        else struct (first, last, total))

let prefixOrder comparePrefix terms =
    let rec pivot = function
        | Empty -> None
        | Node(_, left, key, text, right) ->
            let comparison = comparePrefix key text
            if comparison < 0 then pivot left
            elif comparison > 0 then pivot right
            else Some key

    let rec matching = function
        | Empty -> Seq.empty
        | Node(_, left, key, text, right) ->
            let comparison = comparePrefix key text
            if comparison < 0 then matching left
            elif comparison > 0 then matching right
            else seq { yield! matching left; yield key; yield! matching right }

    match pivot terms.Tree with
    | None -> []
    | Some first ->
        let before, after =
            matching terms.Tree |> Seq.filter ((<>) first) |> Seq.toList
            |> List.partition (fun key -> String.CompareOrdinal(key, first) < 0)
        first :: (List.rev before @ after)
