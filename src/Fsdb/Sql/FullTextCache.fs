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
