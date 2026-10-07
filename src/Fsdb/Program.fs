module Fsdb.Program

open System.Net
open System.Reflection
open Argu

type Arguments =
    | [<AltCommandLine("-p")>] Port of port: int
    | Listen of address: string
    | Data_Dir of path: string
    | Defaults_File of path: string
    | [<EqualsAssignmentOrSpaced>] Innodb_Ft_Min_Token_Size of size: string
    | [<EqualsAssignmentOrSpaced>] Innodb_Ft_Max_Token_Size of size: string
    | Ngram_Token_Size of size: string
    | [<EqualsAssignment>] Innodb_Ft_Enable_Stopword of enabled: string option
    | [<EqualsAssignment>] Skip_Innodb_Ft_Enable_Stopword of ignored: string option
    | [<EqualsAssignment>] Disable_Innodb_Ft_Enable_Stopword of ignored: string option
    | [<EqualsAssignment>] Enable_Innodb_Ft_Enable_Stopword of ignored: string option
    | [<EqualsAssignment>] Innodb_Ft_User_Stopword_Table of source: string option
    | [<EqualsAssignment>] Innodb_Ft_Server_Stopword_Table of source: string option
    | Ssl_Cert of path: string
    | Ssl_Key of path: string
    | Ssl_Ca of path: string
    | Caching_Sha2_Password_Private_Key_Path of path: string
    | Caching_Sha2_Password_Public_Key_Path of path: string
    | Sha256_Password_Private_Key_Path of path: string
    | Sha256_Password_Public_Key_Path of path: string
    | Secure_File_Priv of path: string
    | Require_Secure_Transport
    | Version

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Port _ -> "listen port (default 3307)"
            | Listen _ -> "bind address (default 127.0.0.1)"
            | Data_Dir _ -> "persist trusted server state here (WAL + snapshots); omit for in-memory"
            | Defaults_File _ -> "read server settings from a my.cnf-style file's [mysqld] section"
            | Innodb_Ft_Min_Token_Size _ -> "minimum indexed word length, clamped to 0–16 (default 3)"
            | Innodb_Ft_Max_Token_Size _ -> "maximum indexed word length, clamped to 10–84 (default 84)"
            | Ngram_Token_Size _ -> "ngram token size, clamped to 1–10 (default 2)"
            | Innodb_Ft_Enable_Stopword _ -> "initial full-text stopword filtering (default ON); accepts =ON or =OFF"
            | Skip_Innodb_Ft_Enable_Stopword _
            | Disable_Innodb_Ft_Enable_Stopword _ -> "disable initial full-text stopword filtering"
            | Enable_Innodb_Ft_Enable_Stopword _ -> "enable initial full-text stopword filtering"
            | Innodb_Ft_User_Stopword_Table _ -> "initial session full-text stopword source (=database/table)"
            | Innodb_Ft_Server_Stopword_Table _ -> "initial server full-text stopword source (=database/table)"
            | Ssl_Cert _ -> "PEM server certificate for TLS"
            | Ssl_Key _ -> "PEM private key for TLS"
            | Ssl_Ca _ -> "PEM certificate authorities trusted for TLS clients"
            | Caching_Sha2_Password_Private_Key_Path _ -> "PEM private key for caching SHA-2 authentication"
            | Caching_Sha2_Password_Public_Key_Path _ -> "matching PEM public key for caching SHA-2 authentication"
            | Sha256_Password_Private_Key_Path _ -> "PEM private key for SHA-256 authentication"
            | Sha256_Password_Public_Key_Path _ -> "matching PEM public key for SHA-256 authentication"
            | Secure_File_Priv _ -> "directory allowed for server-side file statements; NULL disables them"
            | Require_Secure_Transport -> "reject plaintext MySQL sessions"
            | Version -> "print the fsdb version and exit"

let private parser =
    ArgumentParser.Create<Arguments>(programName = "fsdb", errorHandler = ProcessExiter())

let internal parseArguments argv =
    // Argu accepts an empty separate argument but rejects an empty equals assignment.
    let arguments =
        argv |> Array.collect (function
            | "--innodb-ft-user-stopword-table=" -> [| "--innodb-ft-user-stopword-table"; "" |]
            | "--innodb-ft-server-stopword-table=" -> [| "--innodb-ft-server-stopword-table"; "" |]
            | argument -> [| argument |])
    parser.Parse arguments

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
    let results = parseArguments argv

    if results.Contains <@ Version @> then
        printfn "fsdb %s (MySQL protocol %s)" fsdbVersion Protocol.ServerVersion
        0
    else
        let startupOptions =
            let parsed =
                match results.TryGetResult Defaults_File with
                | Some path -> OptionFile.parseFile path
                | None -> OptionFile.defaultFilePaths () |> OptionFile.parseFiles

            let commandLineEntry name value : OptionFile.Entry =
                { Name = name
                  Value = value
                  Source = "command line"
                  Line = 1 }

            let commandLineEntries =
                [ for argument in results.GetAllResults() do
                      match argument with
                      | Innodb_Ft_Min_Token_Size value -> yield commandLineEntry "innodb_ft_min_token_size" (Some value)
                      | Innodb_Ft_Max_Token_Size value -> yield commandLineEntry "innodb_ft_max_token_size" (Some value)
                      | Innodb_Ft_User_Stopword_Table value -> yield commandLineEntry "innodb_ft_user_stopword_table" value
                      | Innodb_Ft_Server_Stopword_Table value -> yield commandLineEntry "innodb_ft_server_stopword_table" value
                      | Innodb_Ft_Enable_Stopword value -> yield commandLineEntry "innodb_ft_enable_stopword" value
                      | Skip_Innodb_Ft_Enable_Stopword value -> yield commandLineEntry "skip_innodb_ft_enable_stopword" value
                      | Disable_Innodb_Ft_Enable_Stopword value -> yield commandLineEntry "disable_innodb_ft_enable_stopword" value
                      | Enable_Innodb_Ft_Enable_Stopword value -> yield commandLineEntry "enable_innodb_ft_enable_stopword" value
                      | _ -> ()
                  match results.TryGetResult Ngram_Token_Size with
                  | Some size -> yield commandLineEntry "ngram_token_size" (Some size)
                  | None -> ()
                  match results.TryGetResult Ssl_Cert with
                  | Some path -> yield commandLineEntry "ssl_cert" (Some path)
                  | None -> ()
                  match results.TryGetResult Ssl_Key with
                  | Some path -> yield commandLineEntry "ssl_key" (Some path)
                  | None -> ()
                  match results.TryGetResult Ssl_Ca with
                  | Some path -> yield commandLineEntry "ssl_ca" (Some path)
                  | None -> ()
                  match results.TryGetResult Caching_Sha2_Password_Private_Key_Path with
                  | Some path -> yield commandLineEntry "caching_sha2_password_private_key_path" (Some path)
                  | None -> ()
                  match results.TryGetResult Caching_Sha2_Password_Public_Key_Path with
                  | Some path -> yield commandLineEntry "caching_sha2_password_public_key_path" (Some path)
                  | None -> ()
                  match results.TryGetResult Sha256_Password_Private_Key_Path with
                  | Some path -> yield commandLineEntry "sha256_password_private_key_path" (Some path)
                  | None -> ()
                  match results.TryGetResult Sha256_Password_Public_Key_Path with
                  | Some path -> yield commandLineEntry "sha256_password_public_key_path" (Some path)
                  | None -> ()
                  match results.TryGetResult Secure_File_Priv with
                  | Some path -> yield commandLineEntry "secure_file_priv" (Some path)
                  | None -> ()
                  if results.Contains <@ Require_Secure_Transport @> then
                      yield commandLineEntry "require_secure_transport" None ]

            match ServerOptions.fromEntries (parsed.Entries @ commandLineEntries) with
            | Error message -> Error(String.concat "\n" (parsed.Errors @ [ message ]))
            | Ok(options, storageEntries) ->
                match StorageOptions.fromEntries storageEntries with
                | Error message -> Error(String.concat "\n" (parsed.Errors @ [ message ]))
                | Ok(storageOptions, limitEntries) ->
                    match Limits.applyEntries limitEntries with
                    | Error message -> Error(String.concat "\n" (parsed.Errors @ [ message ]))
                    | Ok() when List.isEmpty parsed.Errors -> Ok(options, storageOptions)
                    | Ok() -> Error(String.concat "\n" parsed.Errors)

        match startupOptions, resolveListenAddress results with
        | Error message, _ ->
            eprintfn "fsdb: %s" message
            1
        | Ok _, None ->
            eprintfn "fsdb: --listen expects an IP address or 'localhost'"
            1
        | Ok(options, storageOptions), Some address ->
            let port = results.GetResult(Port, defaultValue = 3307)

            let db =
                Db.create ()
                |> Db.withNgramTokenSize storageOptions.NgramTokenSize
                |> Db.withFullTextWordLengths storageOptions.FullTextWordLengths
                |> Db.withFullTextStopwords storageOptions.FullTextStopwordsEnabled
                |> Db.withFullTextStopwordTables storageOptions.FullTextStopwordTables

            let db =
                match results.TryGetResult Data_Dir with
                | Some dataDir ->
                    printfn "fsdb: durability on, data-dir %s" dataDir
                    db |> Db.withDataDir dataDir
                | None -> db

            let db = { db with Transport = options }

            try
                let serve = db |> Db.listen address port
                printfn "fsdb listening on %O:%d" address port
                serve |> Async.RunSynchronously
                0
            with :? System.Net.Sockets.SocketException as ex when
                ex.SocketErrorCode = System.Net.Sockets.SocketError.AddressAlreadyInUse ->
                eprintfn "fsdb: %O:%d is already in use — another server is running there (use --port to pick another)" address port
                1
