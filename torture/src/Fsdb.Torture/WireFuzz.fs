namespace Fsdb.Torture

open System
open System.Diagnostics
open System.IO
open System.Net.Sockets
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
    let private query (sql: string) = Array.append [| 0x03uy |] (Encoding.UTF8.GetBytes sql)

    let baselines =
        [| "empty-command", [||]
           "unknown-command", [| 0xffuy |]
           "empty-query", [| 0x03uy |]
           "delimiter-only-query", query ";"
           "invalid-query", query "SELECT ("
           "invalid-utf8-query", Array.append (query "SELECT (") [| 0xffuy |]
           "ping-with-trailing-data", [| 0x0euy; 0xaauy; 0x55uy |]
           "empty-init-db", [| 0x02uy |]
           "whitespace-init-db", [| 0x02uy; byte ' ' |]
           "empty-field-list", [| 0x04uy |]
           "empty-statement-prepare", [| 0x16uy |]
           "comment-only-statement-prepare", [| 0x16uy; byte '#' |]
           "short-statement-execute", [| 0x17uy; 1uy |]
           "short-statement-fetch", [| 0x1cuy; 1uy |]
           "short-set-option", [| 0x1buy |]
           "reset-with-trailing-data", [| 0x1fuy; 1uy |] |]

    let coverage =
        [| "protocol-capability:client_long_password", [| "wire-success"; "malformed-input" |]
           "protocol-capability:client_protocol41", [| "wire-success"; "malformed-input" |]
           "protocol-capability:client_secure_connection", [| "wire-success"; "malformed-input" |]
           "protocol-capability:client_plugin_auth", [| "wire-success"; "malformed-input" |] |]

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
    let private capabilities = ClientLongPassword ||| ClientProtocol41 ||| ClientSecureConnection ||| ClientPluginAuth

    let private endpoint (connectionString: string) =
        let builder = MySqlConnectionStringBuilder connectionString
        builder.Server, int builder.Port

    let private readWithTimeout timeoutSeconds (stream: Stream) =
        task {
            let read = readPacketAsync stream |> Async.StartAsTask
            let! completed = Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(float timeoutSeconds)))

            if obj.ReferenceEquals(completed, read) then
                return Some read.Result
            else
                return None
        }

    let private connect connectionString timeoutSeconds =
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
                response.WriteNullTerminatedString "torture_wire"
                response.WriteByte 0uy
                response.WriteNullTerminatedString "caching_sha2_password"
                let! _ = writePacketAsync stream { SeqId = greeting.SeqId + 1uy; Payload = response.ToArray() } |> Async.StartAsTask

                let rec authenticated () =
                    task {
                        match! readWithTimeout timeoutSeconds stream with
                        | None
                        | Some None -> return Error "server closed during authentication"
                        | Some(Some packet) when packet.Payload.Length > 0 && packet.Payload.[0] = 0x00uy ->
                            return Ok(client, stream)
                        | Some(Some packet) when packet.Payload.Length >= 2 && packet.Payload.[0] = 0x01uy && packet.Payload.[1] = 0x03uy ->
                            return! authenticated ()
                        | Some(Some packet) when packet.Payload.Length > 0 && packet.Payload.[0] = 0xfeuy ->
                            let! _ = writePacketAsync stream { SeqId = packet.SeqId + 1uy; Payload = [||] } |> Async.StartAsTask
                            return! authenticated ()
                        | Some(Some packet) ->
                            client.Dispose()
                            return Error(sprintf "authentication returned %s" (Convert.ToHexString packet.Payload))
                    }

                return! authenticated ()
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

    let private runCase target connectionString timeoutSeconds payload =
        task {
            let stopwatch = Stopwatch.StartNew()

            match! connect connectionString timeoutSeconds with
            | Error error -> return outcome target stopwatch "infrastructure" 0 "" error -1
            | Ok(client, stream) ->
                use client = client
                use stream = stream
                let! _ = writePacketAsync stream { SeqId = 0uy; Payload = payload } |> Async.StartAsTask
                let! response = readWithTimeout timeoutSeconds stream
                stopwatch.Stop()
                return parseResponse target stopwatch response
        }

    let private classify (mysql: WireOutcome) (fsdb: WireOutcome) =
        if mysql.Status = "infrastructure" || mysql.Status = "timeout" then
            "oracle_infrastructure"
        elif fsdb.Status = "infrastructure" || fsdb.Status = "timeout" then
            "fsdb_infrastructure"
        elif mysql.Status <> fsdb.Status then
            "response_kind_mismatch"
        elif mysql.Status = "error" && (mysql.ErrorCode <> fsdb.ErrorCode || mysql.SqlState <> fsdb.SqlState) then
            "error_contract_mismatch"
        elif mysql.Status = "response" && mysql.FirstByte <> fsdb.FirstByte then
            "response_header_mismatch"
        else
            "pass"

    let private createWireUser target connectionString timeoutSeconds =
        task {
            use! connection = Database.openConnection connectionString
            let! dropped = Database.execute target connection timeoutSeconds "DROP USER IF EXISTS 'torture_wire'@'%'"

            if not (TargetOutcome.succeeded dropped) then
                return Error dropped.Message
            else
                let! created = Database.execute target connection timeoutSeconds "CREATE USER 'torture_wire'@'%' IDENTIFIED BY ''"

                if not (TargetOutcome.succeeded created) then
                    return Error created.Message
                else
                    let! granted =
                        Database.execute target connection timeoutSeconds "GRANT ALL PRIVILEGES ON *.* TO 'torture_wire'@'%'"

                    return if TargetOutcome.succeeded granted then Ok() else Error granted.Message
        }

    let private dropWireUser target connectionString timeoutSeconds =
        task {
            use! connection = Database.openConnection connectionString
            return! Database.execute target connection timeoutSeconds "DROP USER IF EXISTS 'torture_wire'@'%'"
        }

    let run (options: WireOptions) =
        task {
            let started = DateTimeOffset.UtcNow
            let runId = Paths.uniqueRunId ()
            let directory = Path.Combine(options.ArtifactRoot, runId, "wire")
            Directory.CreateDirectory directory |> ignore
            let! revision, dirty = Tooling.gitState ()
            let assemblyPath = typeof<Fsdb.Storage.Store>.Assembly.Location
            Fsdb.Log.silence ()
            use subject = new FsdbSubject()
            let fsdbConnection = Runner.fsdbConnectionString subject.Port

            match! createWireUser "mysql" options.MySqlConnection options.TimeoutSeconds with
            | Error error -> return Error("could not create MySQL wire user: " + error)
            | Ok() ->
                match! createWireUser "fsdb" fsdbConnection options.TimeoutSeconds with
                | Error error -> return Error("could not create fsdb wire user: " + error)
                | Ok() ->
                    use! versionConnection = Database.openConnection options.MySqlConnection
                    let! mysqlVersion = Database.scalarString versionConnection options.TimeoutSeconds "SELECT VERSION()"
                    let records = ResizeArray<WireCaseRecord>()

                    for name, payload in WireCorpus.cases options.Seed options.Cases do
                        let! mysql = runCase "mysql" options.MySqlConnection options.TimeoutSeconds payload
                        let! fsdb = runCase "fsdb" fsdbConnection options.TimeoutSeconds payload
                        let classification = classify mysql fsdb

                        records.Add
                            { Name = name
                              PayloadHex = Convert.ToHexString payload
                              MySql = mysql
                              Fsdb = fsdb
                              Classification = classification
                              Passed = classification = "pass" }

                    let! mysqlCleanup = dropWireUser "mysql" options.MySqlConnection options.TimeoutSeconds
                    let! fsdbCleanup = dropWireUser "fsdb" fsdbConnection options.TimeoutSeconds

                    if not (TargetOutcome.succeeded mysqlCleanup && TargetOutcome.succeeded fsdbCleanup) then
                        return Error "wire test user cleanup failed"
                    else
                        let cases = records.ToArray()
                        let firstFailure = cases |> Array.tryFind (fun item -> not item.Passed)
                        let classification = firstFailure |> Option.map _.Classification |> Option.defaultValue "pass"

                        let signature =
                            firstFailure
                            |> Option.map (fun item -> Hashing.combine [ item.Name; item.PayloadHex; item.Classification ])
                            |> Option.defaultValue ""

                        let manifest =
                            { SchemaVersion = 1
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
                        return Ok(manifest, directory)
        }
