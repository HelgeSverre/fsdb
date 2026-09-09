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
    type Operation =
        | Insert
        | Update
        | Delete
        | Replace

    type Result =
        { MissingAcknowledged: int64 array
          PartialTransactions: int64 array
          UnattemptedRows: int64 array
          RecoveredOperations: int
          Passed: bool
          Detail: string }

    let coverage =
        [| for statement in [ "insert"; "update"; "delete"; "replace"; "create_table"; "alter_table"; "create_index"; "create_view"; "create_trigger" ] do
               yield "statement:" + statement, [| "recovery" |]

           for columnType in TypeMatrix.capabilities do
               yield "column-type:" + columnType, [| "recovery" |] |]

    let operation operationId =
        match operationId % 4L with
        | 0L -> Insert
        | 1L -> Update
        | 2L -> Delete
        | _ -> Replace

    let private seedPayload operationId = sprintf "seed-%d" operationId
    let private committedPayload operationId = sprintf "committed-%d" operationId

    let private initialState operationId =
        match operation operationId with
        | Insert -> None
        | Update
        | Delete
        | Replace -> Some(seedPayload operationId)

    let private committedState operationId =
        match operation operationId with
        | Delete -> None
        | Insert
        | Update
        | Replace -> Some(committedPayload operationId)

    let classifyState
        (possible: Set<int64>)
        (attempted: Set<int64>)
        (acknowledged: Set<int64>)
        (left: Map<int64, string>)
        (right: Map<int64, string>)
        =
        let mismatches = ResizeArray<string>()

        if left <> right then
            mismatches.Add "the paired state tables differ"

        for operationId in possible do
            let actual = Map.tryFind operationId left
            let expected =
                if acknowledged.Contains operationId then
                    [ committedState operationId ]
                elif attempted.Contains operationId then
                    [ initialState operationId; committedState operationId ] |> List.distinct
                else
                    [ initialState operationId ]

            if not (List.contains actual expected) then
                mismatches.Add(sprintf "operation %d recovered an invalid state" operationId)

        let unexpected = Set.difference (left.Keys |> Set.ofSeq) possible

        if not unexpected.IsEmpty then
            mismatches.Add(sprintf "%d state rows have no planned operation" unexpected.Count)

        mismatches.ToArray()

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

    let private startServer dataDirectory defaultsFile port =
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
            [| "--listen"
               "127.0.0.1"
               "--port"
               string port
               "--data-dir"
               dataDirectory
               "--defaults-file"
               defaultsFile |]
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

    let private plannedOperationIds (options: DurabilityOptions) =
        seq {
            for worker in 0 .. options.Workers - 1 do
                for iteration in 0 .. options.OperationsPerWorker - 1 do
                    yield int64 worker * 1_000_000L + int64 iteration
        }
        |> Set.ofSeq

    let private setup (options: DurabilityOptions) port =
        task {
            use admin = new MySqlConnection(connectionString port "" options.TimeoutSeconds)
            do! admin.OpenAsync()
            let! _ = execute admin options.TimeoutSeconds "CREATE DATABASE IF NOT EXISTS durability"
            use connection = new MySqlConnection(connectionString port "durability" options.TimeoutSeconds)
            do! connection.OpenAsync()
            let! _ = execute connection options.TimeoutSeconds "CREATE TABLE IF NOT EXISTS durable_left (operation_id BIGINT PRIMARY KEY, worker_id INT NOT NULL, payload VARCHAR(64) NOT NULL)"
            let! _ = execute connection options.TimeoutSeconds "CREATE TABLE IF NOT EXISTS durable_right (operation_id BIGINT PRIMARY KEY, worker_id INT NOT NULL, payload VARCHAR(64) NOT NULL)"
            let! _ = execute connection options.TimeoutSeconds "CREATE TABLE IF NOT EXISTS durable_state_left (operation_id BIGINT PRIMARY KEY, payload VARCHAR(64) NOT NULL)"
            let! _ = execute connection options.TimeoutSeconds "CREATE TABLE IF NOT EXISTS durable_state_right (operation_id BIGINT PRIMARY KEY, payload VARCHAR(64) NOT NULL)"
            let possible = plannedOperationIds options

            let seeded =
                possible
                |> Set.filter (fun operationId -> DurabilityChecks.operation operationId <> DurabilityChecks.Insert)

            for table in [ "durable_state_left"; "durable_state_right" ] do
                for chunk in seeded |> Seq.chunkBySize 500 do
                    let values =
                        chunk
                        |> Seq.map (fun operationId -> sprintf "(%d, 'seed-%d')" operationId operationId)
                        |> String.concat ","

                    let! _ = execute connection options.TimeoutSeconds (sprintf "INSERT INTO %s VALUES %s" table values)
                    ()

            return possible
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

            let mutate table =
                task {
                    use command = connection.CreateCommand()
                    command.Transaction <- transaction
                    command.CommandTimeout <- timeoutSeconds
                    command.Parameters.AddWithValue("@id", operationId) |> ignore
                    command.Parameters.AddWithValue("@payload", sprintf "committed-%d" operationId) |> ignore

                    command.CommandText <-
                        match DurabilityChecks.operation operationId with
                        | DurabilityChecks.Insert -> sprintf "INSERT INTO %s VALUES (@id, @payload)" table
                        | DurabilityChecks.Update -> sprintf "UPDATE %s SET payload = @payload WHERE operation_id = @id" table
                        | DurabilityChecks.Delete -> sprintf "DELETE FROM %s WHERE operation_id = @id" table
                        | DurabilityChecks.Replace -> sprintf "REPLACE INTO %s VALUES (@id, @payload)" table

                    let! affected = command.ExecuteNonQueryAsync()

                    if affected < 1 then
                        failwithf "%A operation %d did not affect %s" (DurabilityChecks.operation operationId) operationId table
                }

            do! mutate "durable_state_left"
            do! mutate "durable_state_right"
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

    let private readState port timeoutSeconds table =
        task {
            use connection = new MySqlConnection(connectionString port "durability" timeoutSeconds)
            do! connection.OpenAsync()
            use command = connection.CreateCommand()
            command.CommandTimeout <- timeoutSeconds
            command.CommandText <- sprintf "SELECT operation_id, payload FROM %s ORDER BY operation_id" table
            use! reader = command.ExecuteReaderAsync()
            let mutable values = Map.empty

            while! reader.ReadAsync() do
                values <- Map.add (reader.GetInt64 0) (reader.GetString 1) values

            return values
        }

    let private observe (options: DurabilityOptions) port attempted acknowledged =
        task {
            let! left = readIds port options.TimeoutSeconds "durable_left"
            let! right = readIds port options.TimeoutSeconds "durable_right"
            return DurabilityChecks.classify attempted acknowledged left right, left, right
        }

    let private observeState (options: DurabilityOptions) port possible attempted acknowledged =
        task {
            let! left = readState port options.TimeoutSeconds "durable_state_left"
            let! right = readState port options.TimeoutSeconds "durable_state_right"
            return DurabilityChecks.classifyState possible attempted acknowledged left right
        }

    let private createDurableSchema port timeoutSeconds =
        task {
            use connection = new MySqlConnection(connectionString port "durability" timeoutSeconds)
            do! connection.OpenAsync()
            let statements =
                [ "CREATE TABLE durable_schema (id INT PRIMARY KEY, payload VARCHAR(32) NOT NULL)"
                  "ALTER TABLE durable_schema ADD COLUMN revision INT NOT NULL DEFAULT 1"
                  "CREATE INDEX ix_durable_schema_payload ON durable_schema (payload)"
                  "CREATE VIEW durable_schema_view AS SELECT id, payload, revision FROM durable_schema"
                  "CREATE TRIGGER durable_schema_before BEFORE INSERT ON durable_schema FOR EACH ROW SET NEW.payload = UPPER(NEW.payload)"
                  "INSERT INTO durable_schema (id, payload) VALUES (1, 'survived')"
                  TypeMatrix.createTable "durable_types"
                  TypeMatrix.insert "durable_types" ]

            for statement in statements do
                let! _ = execute connection timeoutSeconds statement
                ()

            let! typeRow = Database.query "fsdb" connection timeoutSeconds (TypeMatrix.select "durable_types" "1")

            if not (ProbeOutcome.succeeded typeRow) then
                failwithf "could not read the durable type matrix: %s" typeRow.Message

            return typeRow.DataSha256
        }

    let private schemaRecovered port timeoutSeconds expectedTypeHash =
        task {
            use connection = new MySqlConnection(connectionString port "durability" timeoutSeconds)
            do! connection.OpenAsync()
            use command = connection.CreateCommand()
            command.CommandTimeout <- timeoutSeconds
            command.CommandText <-
                "SELECT CONCAT(v.payload, ':', v.revision, ':', "
                + "(SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = 'durability' AND TABLE_NAME = 'durable_schema' AND INDEX_NAME = 'ix_durable_schema_payload'), "
                + "':', "
                + "(SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = 'durability' AND TRIGGER_NAME = 'durable_schema_before') "
                + ") FROM durable_schema_view AS v WHERE v.id = 1"
            let! schemaState = command.ExecuteScalarAsync()
            let! typeRow = Database.query "fsdb" connection timeoutSeconds (TypeMatrix.select "durable_types" "1")
            return string schemaState = "SURVIVED:1:1:1" && ProbeOutcome.succeeded typeRow && typeRow.DataSha256 = expectedTypeHash
        }

    let private snapshotPath dataDirectory = Path.Combine(dataDirectory, "snapshot.fsdb")

    let private snapshotStamp path =
        if File.Exists path then
            let info = FileInfo path
            Some(info.Length, info.LastWriteTimeUtc.Ticks)
        else
            None

    let private snapshotHash path =
        if File.Exists path then Some(Hashing.file path) else None

    let private executeAcknowledgedOperation
        (options: DurabilityOptions)
        (connection: MySqlConnection)
        worker
        operationId
        (attempted: ConcurrentDictionary<int64, byte>)
        (acknowledged: ConcurrentDictionary<int64, byte>)
        =
        task {
            attempted.TryAdd(operationId, 0uy) |> ignore
            do! executeOperation connection options.TimeoutSeconds worker operationId
            acknowledged.TryAdd(operationId, 0uy) |> ignore
        }

    let private runUntilCheckpoint
        (options: DurabilityOptions)
        dataDirectory
        port
        phase
        (attempted: ConcurrentDictionary<int64, byte>)
        (acknowledged: ConcurrentDictionary<int64, byte>)
        =
        task {
            use connection = new MySqlConnection(connectionString port "durability" options.TimeoutSeconds)
            do! connection.OpenAsync()
            let path = snapshotPath dataDirectory
            let beforeHash = snapshotHash path
            let beforeStamp = snapshotStamp path
            let mutable afterStamp = beforeStamp
            let mutable offset = 0

            while afterStamp = beforeStamp && offset <= options.CheckpointEntries do
                let operationId = 9_000_000_000L + int64 phase * 1_000_000L + int64 offset * 4L
                do! executeAcknowledgedOperation options connection -phase operationId attempted acknowledged
                afterStamp <- snapshotStamp path
                offset <- offset + 1

            let afterHash =
                if afterStamp = beforeStamp then beforeHash else snapshotHash path

            return beforeHash, afterHash
        }

    let run (options: DurabilityOptions) =
        task {
            let started = DateTimeOffset.UtcNow
            let runId = Paths.uniqueRunId ()
            let caseId =
                sprintf
                    "durability-seed%d-workers%d-ops%d-restarts%d-checkpoint%d"
                    options.Seed
                    options.Workers
                    options.OperationsPerWorker
                    options.Restarts
                    options.CheckpointEntries
            let caseDirectory = Path.Combine(options.ArtifactRoot, runId, caseId)
            let dataDirectory = Path.Combine(caseDirectory, "data")
            Directory.CreateDirectory dataDirectory |> ignore
            let defaultsFile = Path.Combine(caseDirectory, "fsdb.cnf")
            File.WriteAllText(
                defaultsFile,
                sprintf "[mysqld]\nwal_rotate_bytes=1099511627776\nwal_rotate_entries=%d\n" options.CheckpointEntries
            )
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
                let! initial = startServer dataDirectory defaultsFile port
                liveServer <- Some initial
                let! possibleOperations = setup options port

                for cycle in 0 .. options.Restarts - 1 do
                    let! workers = runCrashCycle options port cycle attempted acknowledged ambiguous
                    do! stopLive true

                    try
                        let! _ = Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(float options.TimeoutSeconds))
                        ()
                    with :? TimeoutException ->
                        failwithf "workers did not stop after crash %d" (cycle + 1)

                    let! restarted = startServer dataDirectory defaultsFile port
                    liveServer <- Some restarted

                let! firstBefore, firstAfter =
                    runUntilCheckpoint options dataDirectory port 1 attempted acknowledged

                let! secondBefore, secondAfter =
                    runUntilCheckpoint options dataDirectory port 2 attempted acknowledged

                let checkpointsRotated =
                    firstAfter.IsSome
                    && firstAfter <> firstBefore
                    && secondAfter.IsSome
                    && secondAfter <> secondBefore

                use tailConnection = new MySqlConnection(connectionString port "durability" options.TimeoutSeconds)
                do! tailConnection.OpenAsync()
                let tailOperationId = 9_003_000_000L
                do! executeAcknowledgedOperation options tailConnection -3 tailOperationId attempted acknowledged
                let walPath = Path.Combine(dataDirectory, "wal.bin")
                let walTailWritten = File.Exists walPath && FileInfo(walPath).Length > 0L
                do! tailConnection.CloseAsync()
                do! stopLive true
                let! tailRestarted = startServer dataDirectory defaultsFile port
                liveServer <- Some tailRestarted

                let attemptedSet = attempted.Keys |> Set.ofSeq
                let acknowledgedSet = acknowledged.Keys |> Set.ofSeq
                let! recovered, leftBeforeSnapshot, rightBeforeSnapshot = observe options port attemptedSet acknowledgedSet
                let allPossible = Set.union possibleOperations attemptedSet
                let! stateBeforeSnapshot = observeState options port allPossible attemptedSet acknowledgedSet
                let walTailVerified =
                    walTailWritten
                    && Set.contains tailOperationId leftBeforeSnapshot
                    && Set.contains tailOperationId rightBeforeSnapshot

                let! expectedTypeHash = createDurableSchema port options.TimeoutSeconds
                do! stopLive true
                let! schemaServer = startServer dataDirectory defaultsFile port
                liveServer <- Some schemaServer
                let! schemaAfterCrash = schemaRecovered port options.TimeoutSeconds expectedTypeHash

                do! stopLive false
                let snapshotWritten = File.Exists(snapshotPath dataDirectory)
                let! snapshotServer = startServer dataDirectory defaultsFile port
                liveServer <- Some snapshotServer
                let! afterSnapshot, leftAfterSnapshot, rightAfterSnapshot = observe options port attemptedSet acknowledgedSet
                let! stateAfterSnapshot = observeState options port allPossible attemptedSet acknowledgedSet
                let! schemaAfterSnapshot = schemaRecovered port options.TimeoutSeconds expectedTypeHash
                let snapshotVerified = snapshotWritten && leftBeforeSnapshot = leftAfterSnapshot && rightBeforeSnapshot = rightAfterSnapshot

                use repairedConnection = new MySqlConnection(connectionString port "durability" options.TimeoutSeconds)
                do! repairedConnection.OpenAsync()
                let beforeTornId = 9_004_000_000L
                do! executeAcknowledgedOperation options repairedConnection -4 beforeTornId attempted acknowledged
                do! repairedConnection.CloseAsync()
                do! stopLive true
                let walPath = Path.Combine(dataDirectory, "wal.bin")
                File.AppendAllBytes(walPath, [| 100uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy; 0uy |])
                let! repairedServer = startServer dataDirectory defaultsFile port
                liveServer <- Some repairedServer

                use afterRepairConnection = new MySqlConnection(connectionString port "durability" options.TimeoutSeconds)
                do! afterRepairConnection.OpenAsync()
                let afterTornId = 9_005_000_000L
                do! executeAcknowledgedOperation options afterRepairConnection -5 afterTornId attempted acknowledged
                do! afterRepairConnection.CloseAsync()
                do! stopLive true
                let! finalServer = startServer dataDirectory defaultsFile port
                liveServer <- Some finalServer
                let finalAttempted = attempted.Keys |> Set.ofSeq
                let finalAcknowledged = acknowledged.Keys |> Set.ofSeq
                let finalPossible = Set.union possibleOperations finalAttempted
                let! finalRecovery, finalLeft, finalRight = observe options port finalAttempted finalAcknowledged
                let! finalState = observeState options port finalPossible finalAttempted finalAcknowledged
                let tornTailRepairVerified =
                    [ beforeTornId; afterTornId ]
                    |> List.forall (fun operationId -> Set.contains operationId finalLeft && Set.contains operationId finalRight)

                let schemaRecoveryVerified = schemaAfterCrash && schemaAfterSnapshot
                let stateMismatches = Array.concat [ stateBeforeSnapshot; stateAfterSnapshot; finalState ] |> Array.distinct
                do! stopLive false
                let passed =
                    checkpointsRotated
                    && walTailVerified
                    && recovered.Passed
                    && afterSnapshot.Passed
                    && finalRecovery.Passed
                    && snapshotVerified
                    && schemaRecoveryVerified
                    && tornTailRepairVerified
                    && Array.isEmpty stateMismatches

                let detail =
                    if not checkpointsRotated then "two automatic checkpoint rotations were not observed"
                    elif not walTailWritten then "the post-checkpoint commit did not leave a WAL tail"
                    elif not walTailVerified then "the WAL-tail commit was absent after crash recovery"
                    elif not recovered.Passed then recovered.Detail
                    elif not afterSnapshot.Passed then "snapshot restart: " + afterSnapshot.Detail
                    elif not finalRecovery.Passed then "torn-tail restart: " + finalRecovery.Detail
                    elif not snapshotVerified then "the graceful snapshot restart changed recovered rows"
                    elif not schemaRecoveryVerified then "schema objects did not survive crash and snapshot recovery"
                    elif not tornTailRepairVerified then "the repaired WAL tail lost a surrounding acknowledged commit"
                    elif not (Array.isEmpty stateMismatches) then String.concat "; " stateMismatches
                    else
                        recovered.Detail
                        + "; insert, update, delete, replace, schema, WAL-tail, torn-tail repair, and snapshot recovery preserved their state"

                let classification = if passed then "pass" else "durability_failure"
                let currentProcess = Process.GetCurrentProcess()
                currentProcess.Refresh()
                let signature = if passed then "" else Hashing.combine [ classification; string options.Seed; detail ]
                let manifest =
                    { SchemaVersion = 3
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
                      CrashRestarts = options.Restarts + 4
                      CheckpointEntries = options.CheckpointEntries
                      AttemptedOperations = attempted.Count
                      AcknowledgedOperations = acknowledged.Count
                      AmbiguousOperations = ambiguous.Count
                      RecoveredOperations = finalRecovery.RecoveredOperations
                      MissingAcknowledged = finalRecovery.MissingAcknowledged
                      PartialTransactions = finalRecovery.PartialTransactions
                      UnattemptedRows = finalRecovery.UnattemptedRows
                      StateMismatches = stateMismatches
                      AutomaticCheckpointsVerified = checkpointsRotated
                      WalTailVerified = walTailVerified
                      SnapshotVerified = snapshotVerified
                      SchemaRecoveryVerified = schemaRecoveryVerified
                      TornTailRepairVerified = tornTailRepairVerified
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
