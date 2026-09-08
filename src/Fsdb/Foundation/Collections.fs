module Fsdb.Collections

let internal tryAllSome values =
    let rec collect resolved =
        function
        | [] -> Some(List.rev resolved)
        | Some value :: rest -> collect (value :: resolved) rest
        | None :: _ -> None

    collect [] values

let internal sameLength left right =
    let rec loop left right =
        match left, right with
        | [], [] -> true
        | _ :: left, _ :: right -> loop left right
        | _ -> false

    loop left right
