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

let internal traverseArrayIndexed mapping (values: 'a array) : Result<'b array, 'error> =
    let mapped = Array.zeroCreate<'b> values.Length

    let rec loop index =
        if index = values.Length then
            Ok mapped
        else
            match mapping index values.[index] with
            | Ok value ->
                mapped.[index] <- value
                loop (index + 1)
            | Error error -> Error error

    loop 0
