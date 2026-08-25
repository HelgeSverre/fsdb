namespace Fsdb.Torture

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading.Tasks
open MySqlConnector

type private RunningServer =
    { Process: Process
      Stdout: Task<string>
      Stderr: Task<string> }

[<RequireQualifiedAccess>]
module DurabilityChecks =
    type Result =
        { MissingAcknowledged: int64 array
          PartialTransactions: int64 array
          UnattemptedRows: int64 array
          RecoveredOperations: int
          Passed: bool
          Detail: string }

    let classify
        (attempted: Set<int64>)
        (acknowledged: Set<int64>)
        (left: Set<int64>)
        (right: Set<int64>)
        =
        let recovered = Set.intersect left right
        let partial = Set.union (Set.difference left right) (Set.difference right left) |> Set.toArray
        let missing = Set.difference acknowledged recovered |> Set.toArray
        let unattempted = Set.difference (Set.union left right) attempted |> Set.toArray

        let problems =
            [ if missing.Length > 0 then
                  yield sprintf "%d acknowledged commits were lost" missing.Length
              if partial.Length > 0 then
                  yield sprintf "%d transactions were recovered partially" partial.Length
              if unattempted.Length > 0 then
                  yield sprintf "%d rows have no attempted operation" unattempted.Length ]

        { MissingAcknowledged = missing
          PartialTransactions = partial
          UnattemptedRows = unattempted
          RecoveredOperations = recovered.Count
          Passed = List.isEmpty problems
          Detail =
            if List.isEmpty problems then
                "all acknowledged commits and transaction boundaries survived recovery"
            else
                String.concat "; " problems }

[<RequireQualifiedAccess>]
module DurabilityRunner =
    let private connectionString port database timeoutSeconds =
        let builder = MySqlConnectionStringBuilder()
        builder.Server <- "127.0.0.1"
        builder.Port <- uint32 port
        builder.UserID <- "root"
        builder.Password <- ""
        builder.SslMode <- MySqlSslMode.None
        builder.Pooling <- false
        builder.ConnectionTimeout <- uint32 timeoutSeconds
        builder.DefaultCommandTimeout <- uint32 timeoutSeconds

        if not (String.IsNullOrWhiteSpace database) then
            builder.Database <- database

        builder.ConnectionString

    let private reservePort () =
        use listener = new TcpListener(IPAddress.Loopback, 0)
        listener.Start()
        let port = (listener.LocalEndpoint :?> IPEndPoint).Port
        listener.Stop()
        port

    let private startServer dataDirectory port =
        task {
            let assemblyPath = typeof<Fsdb.Storage.Store>.Assembly.Location
            let executableName = if OperatingSystem.IsWindows() then "Fsdb.exe" else "Fsdb"
            let executable = Path.Combine(Path.GetDirectoryName assemblyPath, executableName)

            if not (File.Exists executable) then
                failwithf "fsdb executable was not copied beside %s" assemblyPath

            let startInfo = ProcessStartInfo()
            startInfo.FileName <- executable
            startInfo.WorkingDirectory <- Path.GetDirectoryName executable
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true
            [| "--listen"; "127.0.0.1"; "--port"; string port; "--data-dir"; dataDirectory |]
            |> Array.iter startInfo.ArgumentList.Add

            let child = new Process(StartInfo = startInfo)

            if not (child.Start()) then
                failwith "could not start fsdb"

            let running =
                { Process = child
                  Stdout = child.StandardOutput.ReadToEndAsync()
                  Stderr = child.StandardError.ReadToEndAsync() }

            let deadline = Stopwatch.StartNew()
            let mutable ready = false
            let mutable lastError = "server did not accept connections"

            while not ready && deadline.Elapsed < TimeSpan.FromSeconds 15.0 && not child.HasExited do
                try
                    use connection = new MySqlConnection(connectionString port "" 2)
                    do! connection.OpenAsync()
                    ready <- true
                with error ->
                    lastError <- error.Message
                    do! Task.Delay 25

            if not ready then
                if not child.HasExited then
                    child.Kill(true)

                do! child.WaitForExitAsync()
                let! stderr = running.Stderr
                child.Dispose()
                failwithf "fsdb did not start: %s; %s" lastError stderr

            return running
        }

    let private stopServer crash (server: RunningServer) =
        task {
            if not server.Process.HasExited then
                if crash || OperatingSystem.IsWindows() then
                    server.Process.Kill(true)
                else
                    let! signal = ProcessRunner.run "kill" [| "-TERM"; string server.Process.Id |] (Paths.repoRoot ()) (TimeSpan.FromSeconds 5.0)

                    if signal.ExitCode <> 0 && not server.Process.HasExited then
                        server.Process.Kill(true)

            try
                do! server.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 15.0)
            with :? TimeoutException ->
                if not server.Process.HasExited then
                    server.Process.Kill(true)

                do! server.Process.WaitForExitAsync()

            let! stdout = server.Stdout
            let! stderr = server.Stderr
            server.Process.Dispose()
            return stdout, stderr
        }

    let private execute (connection: MySqlConnection) (timeoutSeconds: int) (sql: string) =
        task {
            use command = new MySqlCommand(sql, connection)
            command.CommandTimeout <- timeoutSeconds
            return! command.ExecuteNonQueryAsync()
        }

    let private setup port timeoutSeconds =
        task {
            use admin = new MySqlConnection(connectionString port "" timeoutSeconds)
            do! admin.OpenAsync()
            let! _ = execute admin timeoutSeconds "CREATE DATABASE IF NOT EXISTS durability"
            use connection = new MySqlConnection(connectionString port "durability" timeoutSeconds)
            do! connection.OpenAsync()
            let! _ = execute connection timeoutSeconds "CREATE TABLE IF NOT EXISTS durable_left (operation_id BIGINT PRIMARY KEY, worker_id INT NOT NULL, payload VARCHAR(64) NOT NULL)"
            let! _ = execute connection timeoutSeconds "CREATE TABLE IF NOT EXISTS durable_right (operation_id BIGINT PRIMARY KEY, worker_id INT NOT NULL, payload VARCHAR(64) NOT NULL)"
            return ()
        }

    let private executeOperation (connection: MySqlConnection) timeoutSeconds worker operationId =
        task {
            use! transaction = connection.BeginTransactionAsync()

            let insert table =
                task {
                    use command = connection.CreateCommand()
                    command.Transaction <- transaction
                    command.CommandTimeout <- timeoutSeconds
                    command.CommandText <- sprintf "INSERT INTO %s (operation_id, worker_id, payload) VALUES (@id, @worker, @payload)" table
                    command.Parameters.AddWithValue("@id", operationId) |> ignore
                    command.Parameters.AddWithValue("@worker", worker) |> ignore
                    command.Parameters.AddWithValue("@payload", sprintf "worker-%d-operation-%d" worker operationId) |> ignore
                    return! command.ExecuteNonQueryAsync()
                }

            let! _ = insert "durable_left"
            let! _ = insert "durable_right"
            do! transaction.CommitAsync()
        }

    let private runCrashCycle
        (options: DurabilityOptions)
        port
        cycle
        (attempted: ConcurrentDictionary<int64, byte>)
        (acknowledged: ConcurrentDictionary<int64, byte>)
        (ambiguous: ConcurrentDictionary<int64, byte>)
        =
        task {
            let gate = TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let workers =
                [| for worker in 0 .. options.Workers - 1 ->
                       task {
                           do! gate.Task
                           use connection = new MySqlConnection(connectionString port "durability" options.TimeoutSeconds)

                           try
                               do! connection.OpenAsync()
                           with _ ->
                               ()

                           let mutable connected = connection.State = System.Data.ConnectionState.Open

                           for iteration in cycle .. options.Restarts .. options.OperationsPerWorker - 1 do
                               if connected then
                                   let operationId = int64 worker * 1_000_000L + int64 iteration
                                   attempted.TryAdd(operationId, 0uy) |> ignore

                                   try
                                       do! executeOperation connection options.TimeoutSeconds worker operationId
                                       acknowledged.TryAdd(operationId, 0uy) |> ignore
                                   with _ ->
                                       ambiguous.TryAdd(operationId, 0uy) |> ignore
                                       connected <- false
                       } |]

            gate.SetResult()
            let delay = 60 + int ((options.Seed + uint64 cycle * 37UL) % 140UL)
            do! Task.Delay delay
            return workers
        }

    let private readIds port timeoutSeconds table =
        task {
            use connection = new MySqlConnection(connectionString port "durability" timeoutSeconds)
            do! connection.OpenAsync()
            use command = connection.CreateCommand()
            command.CommandTimeout <- timeoutSeconds
            command.CommandText <- sprintf "SELECT operation_id FROM %s ORDER BY operation_id" table
            use! reader = command.ExecuteReaderAsync()
            let mutable values = Set.empty

            while! reader.ReadAsync() do
                values <- Set.add (reader.GetInt64 0) values

            return values
        }

    let private observe (options: DurabilityOptions) port attempted acknowledged =
        task {
            let! left = readIds port options.TimeoutSeconds "durable_left"
            let! right = readIds port options.TimeoutSeconds "durable_right"
            return DurabilityChecks.classify attempted acknowledged left right, left, right
        }

    let run (options: DurabilityOptions) =
        task {
            let started = DateTimeOffset.UtcNow
            let runId = Paths.uniqueRunId ()
            let caseId =
                sprintf
                    "durability-seed%d-workers%d-ops%d-restarts%d"
                    options.Seed
                    options.Workers
                    options.OperationsPerWorker
                    options.Restarts
            let caseDirectory = Path.Combine(options.ArtifactRoot, runId, caseId)
            let dataDirectory = Path.Combine(caseDirectory, "data")
            Directory.CreateDirectory dataDirectory |> ignore
            let! revision, dirty = Tooling.gitState ()
            let assemblyPath = typeof<Fsdb.Storage.Store>.Assembly.Location
            let attempted = ConcurrentDictionary<int64, byte>()
            let acknowledged = ConcurrentDictionary<int64, byte>()
            let ambiguous = ConcurrentDictionary<int64, byte>()
            let logs = ResizeArray<string>()
            let mutable liveServer: RunningServer option = None
            let mutable result: Result<(DurabilityManifest * string), string> option = None

            let stopLive crash =
                task {
                    match liveServer with
                    | None -> return ()
                    | Some server ->
                        let! stdout, stderr = stopServer crash server
                        liveServer <- None
                        logs.Add(stdout + stderr)
                }

            try
                let port = reservePort ()
                let! initial = startServer dataDirectory port
                liveServer <- Some initial
                do! setup port options.TimeoutSeconds

                for cycle in 0 .. options.Restarts - 1 do
                    let! workers = runCrashCycle options port cycle attempted acknowledged ambiguous
                    do! stopLive true

                    try
                        let! _ = Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(float options.TimeoutSeconds))
                        ()
                    with :? TimeoutException ->
                        failwithf "workers did not stop after crash %d" (cycle + 1)

                    let! restarted = startServer dataDirectory port
                    liveServer <- Some restarted

                let attemptedSet = attempted.Keys |> Set.ofSeq
                let acknowledgedSet = acknowledged.Keys |> Set.ofSeq
                let! recovered, leftBeforeSnapshot, rightBeforeSnapshot = observe options port attemptedSet acknowledgedSet
                do! stopLive false
                let snapshotWritten = File.Exists(Path.Combine(dataDirectory, "snapshot.fsdb"))
                let! snapshotServer = startServer dataDirectory port
                liveServer <- Some snapshotServer
                let! afterSnapshot, leftAfterSnapshot, rightAfterSnapshot = observe options port attemptedSet acknowledgedSet
                let snapshotVerified = snapshotWritten && leftBeforeSnapshot = leftAfterSnapshot && rightBeforeSnapshot = rightAfterSnapshot
                do! stopLive false
                let passed = recovered.Passed && afterSnapshot.Passed && snapshotVerified
                let detail =
                    if not recovered.Passed then recovered.Detail
                    elif not afterSnapshot.Passed then "snapshot restart: " + afterSnapshot.Detail
                    elif not snapshotVerified then "the graceful snapshot restart changed recovered rows"
                    else recovered.Detail + "; graceful snapshot restart preserved the same state"

                let classification = if passed then "pass" else "durability_failure"
                let currentProcess = Process.GetCurrentProcess()
                currentProcess.Refresh()
                let signature = if passed then "" else Hashing.combine [ classification; string options.Seed; detail ]
                let manifest =
                    { SchemaVersion = 1
                      RunId = runId
                      CaseId = caseId
                      StartedUtc = started.ToString("O", CultureInfo.InvariantCulture)
                      FinishedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                      FsdbRevision = revision
                      FsdbDirty = dirty
                      FsdbAssemblySha256 = Hashing.file assemblyPath
                      Seed = options.Seed
                      Workers = options.Workers
                      OperationsPerWorker = options.OperationsPerWorker
                      CrashRestarts = options.Restarts
                      AttemptedOperations = attempted.Count
                      AcknowledgedOperations = acknowledged.Count
                      AmbiguousOperations = ambiguous.Count
                      RecoveredOperations = afterSnapshot.RecoveredOperations
                      MissingAcknowledged = afterSnapshot.MissingAcknowledged
                      PartialTransactions = afterSnapshot.PartialTransactions
                      UnattemptedRows = afterSnapshot.UnattemptedRows
                      SnapshotVerified = snapshotVerified
                      PeakWorkingSetBytes = max currentProcess.PeakWorkingSet64 currentProcess.WorkingSet64
                      Classification = classification
                      ClassificationDetail = detail
                      FailureSignature = signature
                      Passed = passed }

                Json.write (Path.Combine(caseDirectory, "manifest.json")) manifest
                result <- Some(Ok(manifest, caseDirectory))
            with error ->
                result <- Some(Error error.Message)

            do! stopLive true
            Json.write (Path.Combine(caseDirectory, "server.log.json")) (logs.ToArray())

            return result |> Option.defaultValue (Error "durability run did not produce a result")
        }
