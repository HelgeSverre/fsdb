namespace Fsdb.Torture

open System
open System.Diagnostics
open System.IO
open System.Net.Sockets
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Text
open System.Threading.Tasks
open Fsdb.Binary
open Fsdb.Packet
open Fsdb.Protocol
open MySqlConnector

[<CLIMutable>]
type WireOptions =
    { Seed: uint64
      Cases: int
      TimeoutSeconds: int
      ArtifactRoot: string
      MySqlConnection: string }

[<CLIMutable>]
type WireOutcome =
    { Target: string
      Status: string
      ErrorCode: int
      SqlState: string
      Message: string
      FirstByte: int
      ElapsedMs: int64 }

[<CLIMutable>]
type WireCaseRecord =
    { Name: string
      Capabilities: uint32
      PayloadHex: string
      MySql: WireOutcome
      Fsdb: WireOutcome
      Classification: string
      Passed: bool }

[<CLIMutable>]
type WireManifest =
    { SchemaVersion: int
      RunId: string
      Seed: uint64
      RequestedMutations: int
      StartedUtc: string
      FinishedUtc: string
      FsdbRevision: string
      FsdbDirty: bool
      FsdbAssemblySha256: string
      MySqlVersion: string
      Cases: WireCaseRecord array
      Classification: string
      FailureSignature: string
      Passed: bool }

[<RequireQualifiedAccess>]
module WireCorpus =
    let requiredCapabilities = ClientLongPassword ||| ClientProtocol41 ||| ClientSecureConnection ||| ClientPluginAuth

    let capabilityProfiles =
        [| "client_found_rows", ClientFoundRows
           "client_long_flag", ClientLongFlag
           "client_connect_with_db", ClientConnectWithDb
           "client_interactive", ClientInteractive
           "client_transactions", ClientTransactions
           "client_multi_statements", ClientMultiStatements
           "client_multi_results", ClientMultiResults
           "client_can_handle_expired_passwords", ClientCanHandleExpiredPasswords
           "client_session_track", ClientSessionTrack
           "client_deprecate_eof", ClientDeprecateEof |]

    let private query (sql: string) = Array.append [| 0x03uy |] (Encoding.UTF8.GetBytes sql)

    let baselines =
        [| "empty-command", [||]
           "unknown-command", [| 0xffuy |]
           "empty-query", [| 0x03uy |]
           "delimiter-only-query", query ";"
           "comment-only-query", query "# comment"
           "comment-delimiter-query", query "/* comment */;"
           "invalid-query", query "SELECT ("
           "invalid-utf8-query", Array.append (query "SELECT (") [| 0xffuy |]
           "ping-with-trailing-data", [| 0x0euy; 0xaauy; 0x55uy |]
           "empty-init-db", [| 0x02uy |]
           "whitespace-init-db", [| 0x02uy; byte ' ' |]
           "empty-field-list", [| 0x04uy |]
           "empty-statement-prepare", [| 0x16uy |]
           "comment-only-statement-prepare", [| 0x16uy; byte '#' |]
           "block-comment-statement-prepare", Array.append [| 0x16uy |] (Encoding.ASCII.GetBytes "/* comment */")
           "short-statement-execute", [| 0x17uy; 1uy |]
           "short-statement-fetch", [| 0x1cuy; 1uy |]
           "short-set-option", [| 0x1buy |]
           "reset-with-trailing-data", [| 0x1fuy; 1uy |] |]

    let coverage =
        [| for name in [ "client_long_password"; "client_protocol41"; "client_secure_connection"; "client_plugin_auth" ] do
               yield "protocol-capability:" + name, [| "wire-success"; "malformed-input"; "connection-lifecycle" |]

           for name, _ in capabilityProfiles do
               yield "protocol-capability:" + name, [| "wire-success"; "connection-lifecycle" |]

           yield "protocol-capability:client_ssl", [| "wire-success"; "malformed-input"; "connection-lifecycle" |]
           yield "protocol-capability:client_compress", [| "wire-success"; "malformed-input"; "connection-lifecycle" |]
           yield
               "protocol-capability:client_zstd_compression_algorithm",
               [| "wire-success"; "malformed-input"; "connection-lifecycle" |]

           yield "protocol-capability:client_local_files", [| "wire-success"; "malformed-input" |] |]

    let private nextUInt64 (state: byref<uint64>) =
        state <- state ^^^ (state <<< 13)
        state <- state ^^^ (state >>> 7)
        state <- state ^^^ (state <<< 17)
        state

    let private nextAscii (state: byref<uint64>) =
        byte (0x20UL + nextUInt64 &state % 0x5fUL)

    let private mutate (state: byref<uint64>) (payload: byte array) =
        let choice = int (nextUInt64 &state % 3UL)

        match choice with
        | 0 when payload.Length > 1 ->
            let length = 1 + int (nextUInt64 &state % uint64 payload.Length)
            payload.[.. length - 1]
        | 1 -> Array.append payload [| nextAscii &state; nextAscii &state |]
        | _ when payload.Length > 1 ->
            let copy = Array.copy payload
            let index = 1 + int (nextUInt64 &state % uint64 (copy.Length - 1))
            copy.[index] <- nextAscii &state
            copy
        | _ -> Array.append payload [| nextAscii &state |]

    let cases seed requested =
        let mutable state = if seed = 0UL then 0x9e3779b97f4a7c15UL else seed
        let generated = ResizeArray<string * byte array>()
        let mutationSources = baselines |> Array.filter (snd >> Array.isEmpty >> not)
        let seen = Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        baselines |> Array.iter (snd >> Convert.ToHexString >> seen.Add >> ignore)
        let mutable attempts = 0

        while generated.Count < requested && attempts < requested * 32 + 32 do
            let baselineName, baseline = mutationSources.[int (nextUInt64 &state % uint64 mutationSources.Length)]
            let payload = mutate &state baseline
            let hex = Convert.ToHexString payload

            if seen.Add hex then
                generated.Add(sprintf "%s-mutation-%d" baselineName generated.Count, payload)

            attempts <- attempts + 1

        Array.append baselines (generated.ToArray())

[<RequireQualifiedAccess>]
module WireRunner =
    let private hasCapability capability capabilities = capabilities &&& capability <> 0u

    let private endpoint (connectionString: string) =
        let builder = MySqlConnectionStringBuilder connectionString
        builder.Server, int builder.Port

    let private serverCertificate () =
        use key = RSA.Create 2048
        let request = CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        request.CertificateExtensions.Add(X509BasicConstraintsExtension(false, false, 0, false))
        request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1.0), DateTimeOffset.UtcNow.AddDays(1.0))

    let private readWithTimeout timeoutSeconds (stream: Stream) =
        task {
            let read = readPacketAsync stream |> Async.StartAsTask
            let! completed = Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(float timeoutSeconds)))

            if obj.ReferenceEquals(completed, read) then
                return Some read.Result
            else
                return None
        }

    let private connect username capabilities connectionString timeoutSeconds =
        task {
            let host, port = endpoint connectionString
            let client = new TcpClient()
            do! client.ConnectAsync(host, port)
            let stream = client.GetStream()

            match! readWithTimeout timeoutSeconds stream with
            | None
            | Some None ->
                client.Dispose()
                return Error "server did not send a handshake"
            | Some(Some greeting) ->
                let response = Writer()
                response.WriteUInt32LE capabilities
                response.WriteUInt32LE 16777216u
                response.WriteByte 45uy
                response.WriteBytes(Array.zeroCreate 23)
                response.WriteNullTerminatedString username
                response.WriteByte 0uy

                if hasCapability ClientConnectWithDb capabilities then
                    response.WriteNullTerminatedString "mysql"

                if hasCapability ClientPluginAuth capabilities then
                    response.WriteNullTerminatedString "caching_sha2_password"

                if hasCapability ClientZstdCompressionAlgorithm capabilities then
                    response.WriteByte 3uy

                let! _ = writePacketAsync stream { SeqId = greeting.SeqId + 1uy; Payload = response.ToArray() } |> Async.StartAsTask

                let mutable finished = false
                let mutable authenticationResult = Error "server closed during authentication"

                while not finished do
                    match! readWithTimeout timeoutSeconds stream with
                    | None
                    | Some None -> finished <- true
                    | Some(Some packet) when packet.Payload.Length > 0 && packet.Payload.[0] = 0x00uy ->
                        authenticationResult <- Ok(client, stream)
                        finished <- true
                    | Some(Some packet) when packet.Payload.Length >= 2 && packet.Payload.[0] = 0x01uy && packet.Payload.[1] = 0x03uy ->
                        ()
                    | Some(Some packet) when packet.Payload.Length > 0 && packet.Payload.[0] = 0xfeuy ->
                        let! _ = writePacketAsync stream { SeqId = packet.SeqId + 1uy; Payload = [||] } |> Async.StartAsTask
                        ()
                    | Some(Some packet) ->
                        authenticationResult <- Error(sprintf "authentication returned %s" (Convert.ToHexString packet.Payload))
                        finished <- true

                if authenticationResult.IsError then
                    client.Dispose()

                return authenticationResult
        }

    let private outcome target (stopwatch: Stopwatch) status code state message firstByte =
        { Target = target
          Status = status
          ErrorCode = code
          SqlState = state
          Message = message
          FirstByte = firstByte
          ElapsedMs = stopwatch.ElapsedMilliseconds }

    let private parseResponse target stopwatch =
        function
        | None -> outcome target stopwatch "timeout" 0 "" "" -1
        | Some None -> outcome target stopwatch "disconnect" 0 "" "" -1
        | Some(Some packet) when packet.Payload.Length = 0 -> outcome target stopwatch "empty-response" 0 "" "" -1
        | Some(Some packet) when packet.Payload.[0] = 0xffuy && packet.Payload.Length >= 3 ->
            let reader = Reader packet.Payload
            reader.ReadByte() |> ignore
            let code = reader.ReadInt16LE()

            let state =
                if reader.Remaining >= 6 && reader.ReadByte() = byte '#' then
                    reader.ReadBytes 5 |> Encoding.ASCII.GetString
                else
                    ""

            outcome target stopwatch "error" code state "" 255
        | Some(Some packet) -> outcome target stopwatch "response" 0 "" "" (int packet.Payload.[0])

    let private runCase username capabilities target connectionString timeoutSeconds payload =
        task {
            let stopwatch = Stopwatch.StartNew()

            match! connect username capabilities connectionString timeoutSeconds with
            | Error error -> return outcome target stopwatch "infrastructure" 0 "" error -1
            | Ok(client, stream) ->
                use client = client
                use stream = stream
                let! _ = writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.StartAsTask
                let! response = readWithTimeout timeoutSeconds stream
                stopwatch.Stop()
                return parseResponse target stopwatch response
        }

    let private runCompressedCase username algorithm capability target connectionString timeoutSeconds =
        task {
            let stopwatch = Stopwatch.StartNew()
            let capabilities = WireCorpus.requiredCapabilities ||| capability

            match! connect username capabilities connectionString timeoutSeconds with
            | Error error -> return outcome target stopwatch "infrastructure" 0 "" error -1
            | Ok(client, rawStream) ->
                use client = client
                use rawStream = rawStream
                use stream = new Fsdb.Compression.CompressedStream(rawStream, true, algorithm)
                stream.BeginCommand()
                let! _ = writePacketAsync stream { SeqId = 0uy; Payload = [| 0x0euy |] } |> Async.StartAsTask
                let! response = readWithTimeout timeoutSeconds stream
                stopwatch.Stop()
                return parseResponse target stopwatch response
        }

    let private readContainment target (stopwatch: Stopwatch) timeoutSeconds (stream: Stream) =
        task {
            let buffer = Array.zeroCreate<byte> 1

            try
                let read = stream.ReadAsync(buffer, 0, 1)
                let! completed = Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(float timeoutSeconds)))
                stopwatch.Stop()

                if obj.ReferenceEquals(completed, read) then
                    return outcome target stopwatch "contained" 0 "" "" (if read.Result = 0 then -1 else int buffer.[0])
                else
                    return outcome target stopwatch "timeout" 0 "" "" -1
            with error ->
                stopwatch.Stop()
                return outcome target stopwatch "contained" 0 "" error.Message -1
        }

    let private runMalformedCompression username capability target connectionString timeoutSeconds =
        task {
            let stopwatch = Stopwatch.StartNew()
            let capabilities = WireCorpus.requiredCapabilities ||| capability

            match! connect username capabilities connectionString timeoutSeconds with
            | Error error -> return outcome target stopwatch "infrastructure" 0 "" error -1
            | Ok(client, stream) ->
                use client = client
                use stream = stream
                let frame = [| 1uy; 0uy; 0uy; 1uy; 0uy; 0uy; 0uy; 0uy |]
                do! stream.WriteAsync(frame, 0, frame.Length)
                do! stream.FlushAsync()
                return! readContainment target stopwatch timeoutSeconds stream
        }

    let private runMalformedTls target connectionString timeoutSeconds =
        task {
            let stopwatch = Stopwatch.StartNew()
            let host, port = endpoint connectionString
            use client = new TcpClient()
            do! client.ConnectAsync(host, port)
            use stream = client.GetStream()

            match! readWithTimeout timeoutSeconds stream with
            | None
            | Some None -> return outcome target stopwatch "infrastructure" 0 "" "server did not send a handshake" -1
            | Some(Some greeting) ->
                let capabilities = WireCorpus.requiredCapabilities ||| ClientSsl
                let request = Writer()
                request.WriteUInt32LE capabilities
                request.WriteUInt32LE 16777216u
                request.WriteByte 45uy
                request.WriteBytes(Array.zeroCreate 23)
                let! _ = writePacketAsync stream { SeqId = greeting.SeqId + 1uy; Payload = request.ToArray() } |> Async.StartAsTask
                let invalidTlsRecord = [| 0xffuy; 0uy; 0uy; 0uy; 0uy |]
                do! stream.WriteAsync(invalidTlsRecord, 0, invalidTlsRecord.Length)
                do! stream.FlushAsync()
                return! readContainment target stopwatch timeoutSeconds stream
        }

    let private runConnectorCase username requireTls compression target connectionString timeoutSeconds =
        task {
            let stopwatch = Stopwatch.StartNew()
            let builder = MySqlConnectionStringBuilder connectionString
            builder.UserID <- username
            builder.Password <- ""
            builder.Pooling <- false
            builder.SslMode <- if requireTls then MySqlSslMode.Required else MySqlSslMode.None
            builder.UseCompression <- compression

            try
                use connection = new MySqlConnection(builder.ConnectionString)
                do! connection.OpenAsync()
                use command = connection.CreateCommand()
                command.CommandTimeout <- timeoutSeconds
                command.CommandText <- "SELECT LENGTH(REPEAT('transport-', 128))"
                let! value = command.ExecuteScalarAsync()
                stopwatch.Stop()

                if Convert.ToInt32 value = 1280 then
                    return outcome target stopwatch "response" 0 "" "" 0
                else
                    return outcome target stopwatch "unexpected" 0 "" (string value) -1
            with
            | :? MySqlException as error ->
                stopwatch.Stop()
                return outcome target stopwatch "error" error.Number error.SqlState error.Message 255
            | error ->
                stopwatch.Stop()
                return outcome target stopwatch "infrastructure" 0 "" error.Message -1
        }

    let private runLocalInfileCase database malformedSequence username target connectionString timeoutSeconds =
        task {
            let table = database + ".local_infile"
            use! admin = Database.openConnection connectionString
            let! _ = Database.execute target admin timeoutSeconds ("DROP DATABASE IF EXISTS " + database)
            let! databaseCreated = Database.execute target admin timeoutSeconds ("CREATE DATABASE " + database)
            let! created = Database.execute target admin timeoutSeconds (sprintf "CREATE TABLE %s (id INT PRIMARY KEY, name VARCHAR(20))" table)

            if not (TargetOutcome.succeeded databaseCreated && TargetOutcome.succeeded created) then
                let failed = if TargetOutcome.succeeded databaseCreated then created else databaseCreated
                return outcome target (Stopwatch.StartNew()) "infrastructure" failed.ErrorCode failed.SqlState failed.Message -1
            else
                let stopwatch = Stopwatch.StartNew()
                let capabilities = WireCorpus.requiredCapabilities ||| ClientLocalFiles

                let! result =
                    task {
                        match! connect username capabilities connectionString timeoutSeconds with
                        | Error error -> return outcome target stopwatch "infrastructure" 0 "" error -1
                        | Ok(client, stream) ->
                            use client = client
                            use stream = stream
                            let sql = sprintf "LOAD DATA LOCAL INFILE 'wire.tsv' INTO TABLE %s" table
                            let payload = Array.append [| 0x03uy |] (Encoding.UTF8.GetBytes sql)
                            let! _ = writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.StartAsTask

                            match! readWithTimeout timeoutSeconds stream with
                            | Some(Some request) when request.Payload.Length > 0 && request.Payload.[0] = 0xfbuy ->
                                let rows = Encoding.UTF8.GetBytes "1\tAda\n2\tGrace\n"

                                if malformedSequence then
                                    let! _ = writePacketAsync stream { SeqId = 7uy; Payload = rows } |> Async.StartAsTask
                                    ()
                                else
                                    let! _ = writePacketAsync stream { SeqId = 2uy; Payload = rows } |> Async.StartAsTask
                                    let! _ = writePacketAsync stream { SeqId = 3uy; Payload = [||] } |> Async.StartAsTask
                                    ()

                                let! response = readWithTimeout timeoutSeconds stream
                                stopwatch.Stop()
                                return parseResponse target stopwatch response
                            | response ->
                                stopwatch.Stop()
                                return parseResponse target stopwatch response
                    }

                let! count = Database.scalarString admin timeoutSeconds (sprintf "SELECT COUNT(*) FROM %s" table)
                let! _ = Database.execute target admin timeoutSeconds ("DROP DATABASE IF EXISTS " + database)

                if malformedSequence then
                    return result
                elif result.Status = "response" && count = "2" then
                    return result
                else
                    return { result with Status = "unexpected"; Message = "loaded row count=" + count }
        }

    let private classify (mysql: WireOutcome) (fsdb: WireOutcome) =
        if mysql.Status = "infrastructure" || mysql.Status = "timeout" then
            "oracle_infrastructure"
        elif fsdb.Status = "infrastructure" || fsdb.Status = "timeout" then
            "fsdb_infrastructure"
        elif mysql.Status = "unexpected" then
            "oracle_contract_drift"
        elif fsdb.Status = "unexpected" then
            "fsdb_contract_failure"
        elif mysql.Status <> fsdb.Status then
            "response_kind_mismatch"
        elif mysql.Status = "error" && (mysql.ErrorCode <> fsdb.ErrorCode || mysql.SqlState <> fsdb.SqlState) then
            "error_contract_mismatch"
        elif mysql.Status = "response" && mysql.FirstByte <> fsdb.FirstByte then
            "response_header_mismatch"
        else
            "pass"

    let private createWireUser username target connectionString timeoutSeconds =
        task {
            use! connection = Database.openConnection connectionString
            let account = sprintf "'%s'@'%%'" username
            let! dropped = Database.execute target connection timeoutSeconds ("DROP USER IF EXISTS " + account)

            if not (TargetOutcome.succeeded dropped) then
                return Error dropped.Message
            else
                let! created = Database.execute target connection timeoutSeconds ("CREATE USER " + account + " IDENTIFIED BY ''")

                if not (TargetOutcome.succeeded created) then
                    return Error created.Message
                else
                    let! granted =
                        Database.execute target connection timeoutSeconds ("GRANT ALL PRIVILEGES ON *.* TO " + account)

                    return if TargetOutcome.succeeded granted then Ok() else Error granted.Message
        }

    let private dropWireUser username target connectionString timeoutSeconds =
        task {
            use! connection = Database.openConnection connectionString
            return!
                Database.execute
                    target
                    connection
                    timeoutSeconds
                    (sprintf "DROP USER IF EXISTS '%s'@'%%'" username)
        }

    let private dropWireDatabase name target connectionString timeoutSeconds =
        task {
            use! connection = Database.openConnection connectionString
            return! Database.execute target connection timeoutSeconds ("DROP DATABASE IF EXISTS " + name)
        }

    let private restoreGlobalSettings target connectionString timeoutSeconds settings =
        task {
            match settings with
            | None -> return Ok()
            | Some(connectTimeout, localInfile) ->
                try
                    use! connection = Database.openConnection connectionString
                    let! connectRestored =
                        Database.execute target connection timeoutSeconds ("SET GLOBAL connect_timeout = " + connectTimeout)

                    let! localRestored =
                        Database.execute target connection timeoutSeconds ("SET GLOBAL local_infile = " + localInfile)

                    if TargetOutcome.succeeded connectRestored && TargetOutcome.succeeded localRestored then
                        return Ok()
                    else
                        return Error(sprintf "connect_timeout=%s local_infile=%s" connectRestored.Message localRestored.Message)
                with error ->
                    return Error error.Message
        }

    let run (options: WireOptions) =
        task {
            let started = DateTimeOffset.UtcNow
            let runId = Paths.uniqueRunId ()
            let directory = Path.Combine(options.ArtifactRoot, runId, "wire")
            Directory.CreateDirectory directory |> ignore
            let! revision, dirty = Tooling.gitState ()
            let assemblyPath = typeof<Fsdb.Storage.Store>.Assembly.Location
            let username = sprintf "fsdb_wire_%d_%s" Environment.ProcessId ((Hashing.text runId).Substring(0, 8))
            let localDatabase = "fsdb_wire_" + (Hashing.text runId).Substring(0, 12)
            Fsdb.Log.silence ()
            use certificate = serverCertificate ()
            let serverOptions = Fsdb.ServerOptions.defaults |> Fsdb.ServerOptions.withCertificate certificate
            use subject = new FsdbSubject(serverOptions = serverOptions)
            let fsdbConnection = Runner.fsdbConnectionString subject.Port

            match! createWireUser username "mysql" options.MySqlConnection options.TimeoutSeconds with
            | Error error -> return Error("could not create MySQL wire user: " + error)
            | Ok() ->
                match! createWireUser username "fsdb" fsdbConnection options.TimeoutSeconds with
                | Error error ->
                    let! _ = dropWireUser username "mysql" options.MySqlConnection options.TimeoutSeconds
                    return Error("could not create fsdb wire user: " + error)
                | Ok() ->
                    let mutable runResult = Error "wire run did not produce a result"
                    let mutable mysqlGlobalSettings = None
                    let mutable fsdbGlobalSettings = None

                    try
                        use! versionConnection = Database.openConnection options.MySqlConnection
                        use! fsdbAdmin = Database.openConnection fsdbConnection
                        let! mysqlVersion = Database.scalarString versionConnection options.TimeoutSeconds "SELECT VERSION()"
                        let! mysqlOriginalConnect = Database.scalarString versionConnection options.TimeoutSeconds "SELECT @@GLOBAL.connect_timeout"
                        let! mysqlOriginalLocal = Database.scalarString versionConnection options.TimeoutSeconds "SELECT @@GLOBAL.local_infile"
                        let! fsdbOriginalConnect = Database.scalarString fsdbAdmin options.TimeoutSeconds "SELECT @@GLOBAL.connect_timeout"
                        let! fsdbOriginalLocal = Database.scalarString fsdbAdmin options.TimeoutSeconds "SELECT @@GLOBAL.local_infile"
                        mysqlGlobalSettings <- Some(mysqlOriginalConnect, mysqlOriginalLocal)
                        fsdbGlobalSettings <- Some(fsdbOriginalConnect, fsdbOriginalLocal)
                        let! mysqlConnectTimeout = Database.execute "mysql" versionConnection options.TimeoutSeconds "SET GLOBAL connect_timeout = 2"
                        let! fsdbConnectTimeout = Database.execute "fsdb" fsdbAdmin options.TimeoutSeconds "SET GLOBAL connect_timeout = 2"
                        let! mysqlLocal = Database.execute "mysql" versionConnection options.TimeoutSeconds "SET GLOBAL local_infile = ON"
                        let! fsdbLocal = Database.execute "fsdb" fsdbAdmin options.TimeoutSeconds "SET GLOBAL local_infile = ON"

                        if
                            not (
                                TargetOutcome.succeeded mysqlConnectTimeout
                                && TargetOutcome.succeeded fsdbConnectTimeout
                                && TargetOutcome.succeeded mysqlLocal
                                && TargetOutcome.succeeded fsdbLocal
                            )
                        then
                            failwithf
                                "could not configure transport globals: mysql=%s/%s fsdb=%s/%s"
                                mysqlConnectTimeout.Message
                                mysqlLocal.Message
                                fsdbConnectTimeout.Message
                                fsdbLocal.Message

                        let records = ResizeArray<WireCaseRecord>()
                        let addRecord name capabilities (payload: byte[]) mysql fsdb =
                            let classification = classify mysql fsdb

                            records.Add
                                { Name = name
                                  Capabilities = capabilities
                                  PayloadHex = Convert.ToHexString payload
                                  MySql = mysql
                                  Fsdb = fsdb
                                  Classification = classification
                                  Passed = classification = "pass" }

                        let runInput name capabilities payload =
                            task {
                                let! mysql = runCase username capabilities "mysql" options.MySqlConnection options.TimeoutSeconds payload
                                let! fsdb = runCase username capabilities "fsdb" fsdbConnection options.TimeoutSeconds payload
                                addRecord name capabilities payload mysql fsdb
                            }

                        for name, capability in WireCorpus.capabilityProfiles do
                            do!
                                runInput
                                    ("capability-" + name)
                                    (WireCorpus.requiredCapabilities ||| capability)
                                    [| 0x0euy |]

                        for name, payload in WireCorpus.cases options.Seed options.Cases do
                            do! runInput name WireCorpus.requiredCapabilities payload

                        let runTransport name capabilities mysqlOperation fsdbOperation =
                            task {
                                let! mysql = mysqlOperation
                                let! fsdb = fsdbOperation
                                addRecord name capabilities [||] mysql fsdb
                            }

                        do!
                            runTransport
                                "tls-required"
                                ClientSsl
                                (runConnectorCase username true false "mysql" options.MySqlConnection options.TimeoutSeconds)
                                (runConnectorCase username true false "fsdb" fsdbConnection options.TimeoutSeconds)

                        do!
                            runTransport
                                "tls-with-zlib"
                                (ClientSsl ||| ClientCompress)
                                (runConnectorCase username true true "mysql" options.MySqlConnection options.TimeoutSeconds)
                                (runConnectorCase username true true "fsdb" fsdbConnection options.TimeoutSeconds)

                        do!
                            runTransport
                                "zlib-ping"
                                ClientCompress
                                (runCompressedCase username Fsdb.Compression.Algorithm.Zlib ClientCompress "mysql" options.MySqlConnection options.TimeoutSeconds)
                                (runCompressedCase username Fsdb.Compression.Algorithm.Zlib ClientCompress "fsdb" fsdbConnection options.TimeoutSeconds)

                        do!
                            runTransport
                                "zstd-ping"
                                ClientZstdCompressionAlgorithm
                                (runCompressedCase
                                    username
                                    (Fsdb.Compression.Algorithm.Zstandard 3)
                                    ClientZstdCompressionAlgorithm
                                    "mysql"
                                    options.MySqlConnection
                                    options.TimeoutSeconds)
                                (runCompressedCase
                                    username
                                    (Fsdb.Compression.Algorithm.Zstandard 3)
                                    ClientZstdCompressionAlgorithm
                                    "fsdb"
                                    fsdbConnection
                                    options.TimeoutSeconds)

                        do!
                            runTransport
                                "malformed-tls-record"
                                ClientSsl
                                (runMalformedTls "mysql" options.MySqlConnection options.TimeoutSeconds)
                                (runMalformedTls "fsdb" fsdbConnection options.TimeoutSeconds)

                        do!
                            runTransport
                                "malformed-zlib-frame"
                                ClientCompress
                                (runMalformedCompression username ClientCompress "mysql" options.MySqlConnection options.TimeoutSeconds)
                                (runMalformedCompression username ClientCompress "fsdb" fsdbConnection options.TimeoutSeconds)

                        do!
                            runTransport
                                "malformed-zstd-frame"
                                ClientZstdCompressionAlgorithm
                                (runMalformedCompression
                                    username
                                    ClientZstdCompressionAlgorithm
                                    "mysql"
                                    options.MySqlConnection
                                    options.TimeoutSeconds)
                                (runMalformedCompression
                                    username
                                    ClientZstdCompressionAlgorithm
                                    "fsdb"
                                    fsdbConnection
                                    options.TimeoutSeconds)

                        do!
                            runTransport
                                "local-infile-upload"
                                ClientLocalFiles
                                (runLocalInfileCase localDatabase false username "mysql" options.MySqlConnection options.TimeoutSeconds)
                                (runLocalInfileCase localDatabase false username "fsdb" fsdbConnection options.TimeoutSeconds)

                        do!
                            runTransport
                                "local-infile-sequence"
                                ClientLocalFiles
                                (runLocalInfileCase localDatabase true username "mysql" options.MySqlConnection options.TimeoutSeconds)
                                (runLocalInfileCase localDatabase true username "fsdb" fsdbConnection options.TimeoutSeconds)

                        let cases = records.ToArray()
                        let firstFailure = cases |> Array.tryFind (fun item -> not item.Passed)
                        let classification = firstFailure |> Option.map _.Classification |> Option.defaultValue "pass"

                        let signature =
                            firstFailure
                            |> Option.map (fun item -> Hashing.combine [ item.Name; item.PayloadHex; item.Classification ])
                            |> Option.defaultValue ""

                        let manifest =
                            { SchemaVersion = 2
                              RunId = runId
                              Seed = options.Seed
                              RequestedMutations = options.Cases
                              StartedUtc = started.ToString("O")
                              FinishedUtc = DateTimeOffset.UtcNow.ToString("O")
                              FsdbRevision = revision
                              FsdbDirty = dirty
                              FsdbAssemblySha256 = Hashing.file assemblyPath
                              MySqlVersion = mysqlVersion
                              Cases = cases
                              Classification = classification
                              FailureSignature = signature
                              Passed = firstFailure.IsNone }

                        Json.write (Path.Combine(directory, "manifest.json")) manifest
                        runResult <- Ok(manifest, directory)
                    with error ->
                        runResult <- Error error.Message

                    let! mysqlRestored =
                        restoreGlobalSettings "mysql" options.MySqlConnection options.TimeoutSeconds mysqlGlobalSettings

                    let! fsdbRestored =
                        restoreGlobalSettings "fsdb" fsdbConnection options.TimeoutSeconds fsdbGlobalSettings

                    let! mysqlCleanup = dropWireUser username "mysql" options.MySqlConnection options.TimeoutSeconds
                    let! fsdbCleanup = dropWireUser username "fsdb" fsdbConnection options.TimeoutSeconds
                    let! mysqlDatabaseCleanup = dropWireDatabase localDatabase "mysql" options.MySqlConnection options.TimeoutSeconds
                    let! fsdbDatabaseCleanup = dropWireDatabase localDatabase "fsdb" fsdbConnection options.TimeoutSeconds

                    if
                        Result.isOk mysqlRestored
                        && Result.isOk fsdbRestored
                        && TargetOutcome.succeeded mysqlCleanup
                        && TargetOutcome.succeeded fsdbCleanup
                        && TargetOutcome.succeeded mysqlDatabaseCleanup
                        && TargetOutcome.succeeded fsdbDatabaseCleanup
                    then
                        return runResult
                    else
                        return Error "wire test cleanup failed"
        }
