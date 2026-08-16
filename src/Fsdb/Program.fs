module Fsdb.Program

open System.Net
open Argu

type Arguments =
    | [<AltCommandLine("-p")>] Port of port: int
    | Listen of address: string
    | Data_Dir of path: string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Port _ -> "port to listen on (default 3307)"
            | Listen _ -> "IP address to bind, or 'localhost' (default loopback)"
            | Data_Dir _ -> "enable durability: WAL + snapshots stored here, replayed on startup"

let private parser =
    ArgumentParser.Create<Arguments>(programName = "fsdb", errorHandler = ProcessExiter())

/// `--listen` takes an IP address ("0.0.0.0", "::"), with "localhost" as the
/// one spelled-out convenience.
let private resolveListenAddress (results: ParseResults<Arguments>) : IPAddress option =
    match results.TryGetResult Listen with
    | None
    | Some "localhost" -> Some IPAddress.Loopback
    | Some s ->
        match IPAddress.TryParse s with
        | true, address -> Some address
        | false, _ -> None

[<EntryPoint>]
let main argv =
    let results = parser.Parse argv

    match resolveListenAddress results with
    | None ->
        eprintfn "fsdb: --listen expects an IP address or 'localhost'"
        1
    | Some address ->
        let port = results.GetResult(Port, defaultValue = 3307)

        let db =
            match results.TryGetResult Data_Dir with
            | Some dataDir ->
                printfn "fsdb: durability on, data-dir %s" dataDir
                Db.create () |> Db.withDataDir dataDir
            | None -> Db.create ()

        try
            let serve = db |> Db.listen address port
            printfn "fsdb listening on %O:%d" address port
            serve |> Async.RunSynchronously
            0
        with :? System.Net.Sockets.SocketException as ex when
            ex.SocketErrorCode = System.Net.Sockets.SocketError.AddressAlreadyInUse ->
            eprintfn "fsdb: %O:%d is already in use — another server is running there (use --port to pick another)" address port
            1
