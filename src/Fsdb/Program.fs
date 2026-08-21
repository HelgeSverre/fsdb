module Fsdb.Program

open System.Net
open System.Reflection
open Argu

type Arguments =
    | [<AltCommandLine("-p")>] Port of port: int
    | Listen of address: string
    | Data_Dir of path: string
    | Defaults_File of path: string
    | Version

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Port _ -> "listen port (default 3307)"
            | Listen _ -> "bind address (default 127.0.0.1)"
            | Data_Dir _ -> "persist trusted server state here (WAL + snapshots); omit for in-memory"
            | Defaults_File _ -> "read server settings from a my.cnf-style file's [mysqld] section"
            | Version -> "print the fsdb version and exit"

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

// fsdb's own version, stamped into the assembly from the fsproj `<Version>`.
// The SDK appends a `+<git-sha>` build-metadata suffix; the sha stays on the
// assembly for provenance, but semver metadata is noise on a version line.
let private fsdbVersion =
    match Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
    | null -> "unknown"
    | attr -> attr.InformationalVersion.Split([| '+' |], 2).[0]

[<EntryPoint>]
let main argv =
    let results = parser.Parse argv

    if results.Contains <@ Version @> then
        printfn "fsdb %s (MySQL protocol %s)" fsdbVersion Protocol.ServerVersion
        0
    else
        // Before anything builds a store or a listener: every `Limits` knob
        // is read live from then on, so applying settings first is what makes
        // "written once, before any connection exists" true.
        let configError =
            let loaded =
                match results.TryGetResult Defaults_File with
                | Some path -> Limits.loadDefaultsFile path
                | None -> Limits.defaultFilePaths () |> Limits.loadDefaultsFiles

            match loaded with
            | Ok() -> None
            | Error message -> Some message

        match configError, resolveListenAddress results with
        | Some message, _ ->
            eprintfn "fsdb: %s" message
            1
        | None, None ->
            eprintfn "fsdb: --listen expects an IP address or 'localhost'"
            1
        | None, Some address ->
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
