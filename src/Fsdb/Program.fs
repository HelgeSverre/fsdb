module Fsdb.Program

open System.Net

let private argValue (name: string) (argv: string[]) : string option =
    argv
    |> Array.tryFindIndex ((=) name)
    |> Option.bind (fun i -> Array.tryItem (i + 1) argv)

let private parsePort (argv: string[]) : int =
    argValue "--port" argv
    |> Option.bind (fun s ->
        match System.Int32.TryParse s with
        | true, v -> Some v
        | false, _ -> None)
    |> Option.defaultValue 3307

/// `--data-dir PATH` opts into durability — `Db.withDataDir` loads whatever's
/// already there and keeps writing to it. No flag means pure in-memory.
let private parseDataDir (argv: string[]) : string option = argValue "--data-dir" argv

/// --listen takes an IP address ("0.0.0.0", "::"), with "localhost" as the
/// one spelled-out convenience. Defaults to loopback.
let private parseListenAddress (argv: string[]) : IPAddress option =
    match argValue "--listen" argv with
    | None
    | Some "localhost" -> Some IPAddress.Loopback
    | Some s ->
        match IPAddress.TryParse s with
        | true, address -> Some address
        | false, _ -> None

[<EntryPoint>]
let main argv =
    match parseListenAddress argv with
    | None ->
        eprintfn "fsdb: --listen expects an IP address or 'localhost'"
        1
    | Some address ->
        let port = parsePort argv

        let db =
            match parseDataDir argv with
            | Some dataDir ->
                printfn "fsdb: durability on, data-dir %s" dataDir
                Db.create () |> Db.withDataDir dataDir
            | None -> Db.create ()

        printfn "fsdb listening on %O:%d" address port
        db |> Db.listen address port |> Async.RunSynchronously
        0
