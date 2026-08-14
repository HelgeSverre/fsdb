module Fsdb.Program

open Fsdb.Server

let private parsePort (argv: string[]) : int =
    argv
    |> Array.tryFindIndex ((=) "--port")
    |> Option.bind (fun i -> Array.tryItem (i + 1) argv)
    |> Option.bind (fun s ->
        match System.Int32.TryParse s with
        | true, v -> Some v
        | false, _ -> None)
    |> Option.defaultValue 3307

[<EntryPoint>]
let main argv =
    let port = parsePort argv
    let listener = startListening port
    printfn "fsdb listening on 127.0.0.1:%d" port
    serve listener |> Async.RunSynchronously
    0
