namespace Fsdb.Torture

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Threading
open System.Threading.Tasks
open MySqlConnector

[<RequireQualifiedAccess>]
module ConcurrencyWorkload =
    [<Literal>]
    let InitialBalance = 1_000_000L

    let operation (options: ConcurrencyOptions) worker iteration : ConcurrencyOperationPlan =
        let operationId = int64 worker * int64 options.OperationsPerWorker + int64 iteration + 1L
        let hot = uint64 options.HotAccounts
        let ordinal = uint64 operationId
        let fromAccount = int ((options.Seed % hot + (ordinal * 17UL) % hot) % hot) + 1
        let offset = int ((options.Seed / 7UL + (ordinal * 31UL) % (hot - 1UL)) % (hot - 1UL)) + 1
        let toAccount = ((fromAccount - 1 + offset) % options.HotAccounts) + 1
        let amount = int64 (1UL + ((options.Seed % 97UL + (ordinal * 43UL) % 97UL) % 97UL))

        { OperationId = operationId
          Worker = worker
          Iteration = iteration
          FromAccount = fromAccount
          ToAccount = toAccount
          Amount = amount
          Rollback = options.RollbackEvery > 0 && operationId % int64 options.RollbackEvery = 0L }

    let plan (options: ConcurrencyOptions) =
        [| for worker in 0 .. options.Workers - 1 do
               for iteration in 0 .. options.OperationsPerWorker - 1 do
                   yield operation options worker iteration |]

    let expected (options: ConcurrencyOptions) =
        let balances = Array.create options.Accounts InitialBalance
        let versions = Array.zeroCreate<int64> options.Accounts
        let ledger = ResizeArray<int64>()

        for operation in plan options do
            if not operation.Rollback then
                let fromIndex = operation.FromAccount - 1
                let toIndex = operation.ToAccount - 1
                balances.[fromIndex] <- balances.[fromIndex] - operation.Amount
                balances.[toIndex] <- balances.[toIndex] + operation.Amount
                versions.[fromIndex] <- versions.[fromIndex] + 1L
                versions.[toIndex] <- versions.[toIndex] + 1L
                ledger.Add operation.OperationId

        balances, versions, ledger.ToArray()

[<RequireQualifiedAccess>]
module ConcurrencyRunner =
    type private AsyncPhaseBarrier(participants: int) =
        let syncRoot = obj ()
        let mutable remaining = participants

        let newPhase () =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable phase = newPhase ()

        do
            if participants <= 0 then
                invalidArg "participants" "an async phase barrier requires at least one participant"

        member _.SignalAndWaitAsync() =
            lock syncRoot (fun () ->
                let current = phase
                remaining <- remaining - 1

                if remaining = 0 then
                    remaining <- participants
                    phase <- newPhase ()
                    current.SetResult(())

                current.Task)

    let private connectionStringWithoutPooling (connectionString: string) timeoutSeconds =
        let builder = MySqlConnectionStringBuilder(connectionString)
        builder.Pooling <- false
        builder.ConnectionTimeout <- uint timeoutSeconds
        builder.ConnectionString

    let private operationFailure (plan: ConcurrencyOperationPlan) stage (error: exn) elapsedMs : ConcurrencyOperationRecord =
        let errorCode, sqlState =
            match error with
            | :? MySqlException as mysql -> int mysql.ErrorCode, (mysql.SqlState |> Option.ofObj |> Option.defaultValue "")
            | _ -> 0, ""

        { OperationId = plan.OperationId
          Worker = plan.Worker
          Iteration = plan.Iteration
          FromAccount = plan.FromAccount
          ToAccount = plan.ToAccount
          Amount = plan.Amount
          IntendedOutcome = if plan.Rollback then "rollback" else "commit"
          Status = "failed"
          Stage = stage
          ErrorCode = errorCode
          SqlState = sqlState
          Message = error.Message
          ElapsedMs = elapsedMs }

    let private operationSuccess (plan: ConcurrencyOperationPlan) elapsedMs : ConcurrencyOperationRecord =
        { OperationId = plan.OperationId
          Worker = plan.Worker
          Iteration = plan.Iteration
          FromAccount = plan.FromAccount
          ToAccount = plan.ToAccount
          Amount = plan.Amount
          IntendedOutcome = if plan.Rollback then "rollback" else "commit"
          Status = if plan.Rollback then "rolled_back" else "committed"
          Stage = if plan.Rollback then "rollback" else "commit"
          ErrorCode = 0
          SqlState = ""
          Message = ""
          ElapsedMs = elapsedMs }

    let private startupFailure worker (error: exn) =
        let plan: ConcurrencyOperationPlan =
            { OperationId = -int64 (worker + 1)
              Worker = worker
              Iteration = -1
              FromAccount = 0
              ToAccount = 0
              Amount = 0L
              Rollback = false }

        operationFailure plan "worker_startup" error 0L

    let private executeDelta (command: MySqlCommand) (transaction: MySqlTransaction) accountId delta (token: CancellationToken) =
        task {
            command.Transaction <- transaction
            command.Parameters.["@delta"].Value <- delta
            command.Parameters.["@id"].Value <- accountId
            let! affected = command.ExecuteNonQueryAsync(token)

            if affected <> 1 then
                return raise (InvalidOperationException(sprintf "prepared account update affected %d rows for account %d" affected accountId))
        }

    let private executeLedger
        (command: MySqlCommand)
        (transaction: MySqlTransaction)
        (plan: ConcurrencyOperationPlan)
        (token: CancellationToken)
        =
        task {
            command.Transaction <- transaction
            command.Parameters.["@operation_id"].Value <- plan.OperationId
            command.Parameters.["@worker"].Value <- plan.Worker
            command.Parameters.["@from_account"].Value <- plan.FromAccount
            command.Parameters.["@to_account"].Value <- plan.ToAccount
            command.Parameters.["@amount"].Value <- plan.Amount
            let! affected = command.ExecuteNonQueryAsync(token)

            if affected <> 1 then
                return raise (InvalidOperationException(sprintf "prepared ledger insert affected %d rows for operation %d" affected plan.OperationId))
        }

    let private runWorker
        connectionString
        (options: ConcurrencyOptions)
        (barrier: AsyncPhaseBarrier)
        (preparedCount: ConcurrentBag<int>)
        worker
        =
        task {
            let history = ResizeArray<ConcurrencyOperationRecord>()

            try
                use! connection = Database.openConnection connectionString
                use update = connection.CreateCommand()
                update.CommandText <- "UPDATE concurrency_accounts SET balance = balance + @delta, version = version + 1 WHERE id = @id"
                update.CommandTimeout <- options.TimeoutSeconds
                update.Parameters.Add("@delta", MySqlDbType.Int64) |> ignore
                update.Parameters.Add("@id", MySqlDbType.Int32) |> ignore

                use ledger = connection.CreateCommand()

                ledger.CommandText <-
                    "INSERT INTO concurrency_ledger (operation_id, worker_id, from_account, to_account, amount) VALUES (@operation_id, @worker, @from_account, @to_account, @amount)"

                ledger.CommandTimeout <- options.TimeoutSeconds
                ledger.Parameters.Add("@operation_id", MySqlDbType.Int64) |> ignore
                ledger.Parameters.Add("@worker", MySqlDbType.Int32) |> ignore
                ledger.Parameters.Add("@from_account", MySqlDbType.Int32) |> ignore
                ledger.Parameters.Add("@to_account", MySqlDbType.Int32) |> ignore
                ledger.Parameters.Add("@amount", MySqlDbType.Int64) |> ignore

                use prepareTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(float options.TimeoutSeconds))
                do! update.PrepareAsync(prepareTimeout.Token)
                do! ledger.PrepareAsync(prepareTimeout.Token)
                preparedCount.Add 2

                for iteration in 0 .. options.OperationsPerWorker - 1 do
                    let plan = ConcurrencyWorkload.operation options worker iteration
                    let stopwatch = Stopwatch.StartNew()
                    let mutable transaction: MySqlTransaction = null
                    let mutable stage = "begin"
                    let mutable failure: exn option = None
                    use operationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(float options.TimeoutSeconds))

                    try
                        let! opened = connection.BeginTransactionAsync(operationTimeout.Token)
                        transaction <- opened
                    with error ->
                        failure <- Some error

                    // Every worker reaches both gates even if BEGIN or an
                    // operation failed. That keeps one diagnostic failure
                    // from stranding otherwise healthy workers in a phase.
                    do! barrier.SignalAndWaitAsync()

                    if failure.IsNone then
                        try
                            stage <- "first_update"
                            let firstId, firstDelta, secondId, secondDelta =
                                if plan.FromAccount < plan.ToAccount then
                                    plan.FromAccount, -plan.Amount, plan.ToAccount, plan.Amount
                                else
                                    plan.ToAccount, plan.Amount, plan.FromAccount, -plan.Amount

                            do! executeDelta update transaction firstId firstDelta operationTimeout.Token
                            stage <- "second_update"
                            do! executeDelta update transaction secondId secondDelta operationTimeout.Token
                            stage <- "ledger_insert"
                            do! executeLedger ledger transaction plan operationTimeout.Token

                            if plan.Rollback then
                                stage <- "rollback"
                                do! transaction.RollbackAsync(operationTimeout.Token)
                            else
                                stage <- "commit"
                                do! transaction.CommitAsync(operationTimeout.Token)
                        with error ->
                            failure <- Some error

                            if not (isNull transaction) then
                                try
                                    do! transaction.RollbackAsync()
                                with _ ->
                                    ()

                    if not (isNull transaction) then
                        transaction.Dispose()

                    do! barrier.SignalAndWaitAsync()

                    stopwatch.Stop()

                    match failure with
                    | None -> history.Add(operationSuccess plan stopwatch.ElapsedMilliseconds)
                    | Some error -> history.Add(operationFailure plan stage error stopwatch.ElapsedMilliseconds)
            with error ->
                history.Add(startupFailure worker error)

                // A worker that cannot connect/prepare still participates
                // as a no-op in every phase. The target report fails on the
                // startup record and missing history, while healthy workers
                // finish and leave useful evidence instead of deadlocking.
                for _ in 0 .. options.OperationsPerWorker - 1 do
                    do! barrier.SignalAndWaitAsync()
                    do! barrier.SignalAndWaitAsync()

            return history.ToArray()
        }

    let private setup target connectionString (options: ConcurrencyOptions) =
        task {
            use! connection = Database.openConnection connectionString

            let statements =
                [| "CREATE TABLE concurrency_accounts (id INT PRIMARY KEY, balance BIGINT NOT NULL, version BIGINT NOT NULL)"
                   "CREATE TABLE concurrency_ledger (operation_id BIGINT PRIMARY KEY, worker_id INT NOT NULL, from_account INT NOT NULL, to_account INT NOT NULL, amount BIGINT NOT NULL)"
                   [ for accountId in 1 .. options.Accounts ->
                         sprintf "(%d, %d, 0)" accountId ConcurrencyWorkload.InitialBalance ]
                   |> String.concat ", "
                   |> sprintf "INSERT INTO concurrency_accounts (id, balance, version) VALUES %s" |]

            for sql in statements do
                let! outcome = Database.execute target connection options.TimeoutSeconds sql

                if not (TargetOutcome.succeeded outcome) then
                    return raise (InvalidOperationException(sprintf "%s setup failed: %s" target outcome.Message))
        }

    let private readFinalState connectionString timeoutSeconds =
        task {
            use! connection = Database.openConnection connectionString
            use accountCommand = connection.CreateCommand()
            accountCommand.CommandText <- "SELECT id, balance, version FROM concurrency_accounts ORDER BY id"
            accountCommand.CommandTimeout <- timeoutSeconds
            do! accountCommand.PrepareAsync()
            use! accountReader = accountCommand.ExecuteReaderAsync()
            let accounts = ResizeArray<int * int64 * int64>()
            let mutable reading = true

            while reading do
                let! hasRow = accountReader.ReadAsync()

                if hasRow then
                    accounts.Add(accountReader.GetInt32 0, accountReader.GetInt64 1, accountReader.GetInt64 2)
                else
                    reading <- false

            do! accountReader.CloseAsync()

            use ledgerCommand = connection.CreateCommand()
            ledgerCommand.CommandText <- "SELECT operation_id FROM concurrency_ledger ORDER BY operation_id"
            ledgerCommand.CommandTimeout <- timeoutSeconds
            do! ledgerCommand.PrepareAsync()
            use! ledgerReader = ledgerCommand.ExecuteReaderAsync()
            let operationIds = ResizeArray<int64>()
            reading <- true

            while reading do
                let! hasRow = ledgerReader.ReadAsync()

                if hasRow then
                    operationIds.Add(ledgerReader.GetInt64 0)
                else
                    reading <- false

            return accounts.ToArray(), operationIds.ToArray()
        }

    let private percentile percentile (values: int64 array) =
        if values.Length = 0 then
            0L
        else
            let sorted = Array.sort values
            let index = int (Math.Ceiling(percentile * float sorted.Length)) - 1
            sorted.[max 0 (min (sorted.Length - 1) index)]

    let private buildReport
        (target: string)
        elapsedMs
        preparedCommands
        (options: ConcurrencyOptions)
        (history: ConcurrencyOperationRecord array)
        (actualAccounts: (int * int64 * int64) array)
        (actualLedger: int64 array)
        =
        let expectedBalances, expectedVersions, expectedLedger = ConcurrencyWorkload.expected options
        let actualById = actualAccounts |> Array.map (fun (id, balance, version) -> id, (balance, version)) |> Map.ofArray

        let accountRecords =
            [| for index in 0 .. options.Accounts - 1 do
                   let id = index + 1
                   let actualBalance, actualVersion = Map.tryFind id actualById |> Option.defaultValue (Int64.MinValue, Int64.MinValue)

                   yield
                       { Id = id
                         ExpectedBalance = expectedBalances.[index]
                         ActualBalance = actualBalance
                         ExpectedVersion = expectedVersions.[index]
                         ActualVersion = actualVersion
                         Equal = actualBalance = expectedBalances.[index] && actualVersion = expectedVersions.[index] } |]

        let orderedHistory = history |> Array.sortBy _.OperationId
        let failed = orderedHistory |> Array.filter (fun record -> record.Status = "failed")
        let committed = orderedHistory |> Array.filter (fun record -> record.Status = "committed")
        let rolledBack = orderedHistory |> Array.filter (fun record -> record.Status = "rolled_back")
        let latencies = orderedHistory |> Array.filter (fun record -> record.Iteration >= 0) |> Array.map _.ElapsedMs
        let expectedLedgerHash = expectedLedger |> Seq.map string |> Hashing.combine
        let actualLedgerHash = actualLedger |> Seq.map string |> Hashing.combine
        let expectedTotal = int64 options.Accounts * ConcurrencyWorkload.InitialBalance
        let actualTotal = actualAccounts |> Array.sumBy (fun (_, balance, _) -> balance)
        let accountsEqual = accountRecords |> Array.forall _.Equal
        let ledgerEqual = actualLedger = expectedLedger
        let allPrepared = preparedCommands = options.Workers * 2
        let expectedAttempts = options.Workers * options.OperationsPerWorker

        let passed =
            failed.Length = 0
            && orderedHistory.Length = expectedAttempts
            && committed.Length = expectedLedger.Length
            && rolledBack.Length = expectedAttempts - expectedLedger.Length
            && allPrepared
            && accountsEqual
            && ledgerEqual
            && actualTotal = expectedTotal

        let detail =
            if passed then
                "all prepared operations, commit/rollback outcomes, account balances/versions, ledger ids, and conservation checks matched"
            else
                sprintf
                    "prepared=%d/%d history=%d/%d failed=%d committed=%d/%d rolled_back=%d/%d ledger=%d/%d accounts_mismatched=%d total=%d/%d"
                    preparedCommands
                    (options.Workers * 2)
                    orderedHistory.Length
                    expectedAttempts
                    failed.Length
                    committed.Length
                    expectedLedger.Length
                    rolledBack.Length
                    (expectedAttempts - expectedLedger.Length)
                    actualLedger.Length
                    expectedLedger.Length
                    (accountRecords |> Array.filter (fun account -> not account.Equal) |> Array.length)
                    actualTotal
                    expectedTotal

        { Target = target
          ElapsedMs = elapsedMs
          PreparedCommands = preparedCommands
          AttemptedOperations = orderedHistory.Length
          CommittedOperations = committed.Length
          RolledBackOperations = rolledBack.Length
          FailedOperations = failed.Length
          OperationsPerSecond = if elapsedMs = 0L then 0.0 else float expectedAttempts * 1000.0 / float elapsedMs
          LatencyP50Ms = percentile 0.50 latencies
          LatencyP95Ms = percentile 0.95 latencies
          LatencyP99Ms = percentile 0.99 latencies
          ExpectedLedgerRows = expectedLedger.Length
          ActualLedgerRows = actualLedger.Length
          ExpectedLedgerSha256 = expectedLedgerHash
          ActualLedgerSha256 = actualLedgerHash
          ExpectedTotalBalance = expectedTotal
          ActualTotalBalance = actualTotal
          Accounts = accountRecords
          History = orderedHistory
          Passed = passed
          Detail = detail }

    /// Public so `MultiDbRunner` can reuse the exact same setup/worker/report
    /// pipeline against several concurrently-opened databases instead of
    /// re-implementing prepared-transaction execution for the multi-db lane.
    let runTarget target connectionString (options: ConcurrencyOptions) =
        task {
            let connectionString = connectionStringWithoutPooling connectionString options.TimeoutSeconds
            do! setup target connectionString options
            let barrier = AsyncPhaseBarrier(options.Workers)
            let preparedCount = ConcurrentBag<int>()
            let stopwatch = Stopwatch.StartNew()

            let! workerHistory =
                [| for worker in 0 .. options.Workers - 1 -> runWorker connectionString options barrier preparedCount worker |]
                |> Task.WhenAll

            stopwatch.Stop()
            let history = Array.concat workerHistory
            let! accounts, ledger = readFinalState connectionString options.TimeoutSeconds
            return buildReport target stopwatch.ElapsedMilliseconds (preparedCount |> Seq.sum) options history accounts ledger
        }

    let run (options: ConcurrencyOptions) =
        task {
            let started = DateTimeOffset.UtcNow
            let runId = Paths.uniqueRunId ()

            let caseId =
                sprintf
                    "concurrency-seed%d-workers%d-ops%d-hot%d-rollback%d"
                    options.Seed
                    options.Workers
                    options.OperationsPerWorker
                    options.HotAccounts
                    options.RollbackEvery

            let caseDirectory = Path.Combine(options.ArtifactRoot, runId, caseId)
            Directory.CreateDirectory caseDirectory |> ignore
            Json.write (Path.Combine(caseDirectory, "plan.json")) (ConcurrencyWorkload.plan options)
            let! revision, dirty = Tooling.gitState ()
            let assemblyPath = typeof<Fsdb.Storage.Store>.Assembly.Location

            let databaseName =
                sprintf "torture_concurrency_%d_%s" Environment.ProcessId ((Hashing.text runId).Substring(0, 12))

            let! oracle = Database.createOracleDatabase options.MySqlConnection databaseName options.TimeoutSeconds

            match oracle with
            | Error error -> return Error(sprintf "could not create concurrency oracle database: %s" error.Message)
            | Ok oracleConnectionString ->
                use subject = new FsdbSubject()
                use! mysqlVersionConnection = Database.openConnection oracleConnectionString
                let! mysqlVersion = Database.scalarString mysqlVersionConnection options.TimeoutSeconds "SELECT VERSION()"
                let! mysql = runTarget "mysql" oracleConnectionString options
                let! fsdb = runTarget "fsdb" (Runner.fsdbConnectionString subject.Port) options
                let invariantErrors = Invariants.validate subject.Store

                let classification, detail =
                    if not mysql.Passed then
                        "oracle_concurrency_failure", mysql.Detail
                    elif fsdb.FailedOperations > 0 then
                        "fsdb_concurrency_execution_gap", fsdb.Detail
                    elif not fsdb.Passed then
                        "fsdb_transaction_atomicity_gap", fsdb.Detail
                    elif invariantErrors.Length > 0 then
                        "invariant_failure", String.concat "; " invariantErrors
                    else
                        "pass", fsdb.Detail

                let signature =
                    if classification = "pass" then
                        ""
                    else
                        Hashing.combine
                            [ classification
                              string options.Seed
                              string options.Workers
                              string options.OperationsPerWorker
                              string options.Accounts
                              string options.HotAccounts
                              string options.RollbackEvery
                              detail ]

                let currentProcess = Process.GetCurrentProcess()
                currentProcess.Refresh()

                let manifest =
                    { SchemaVersion = 1
                      RunId = runId
                      CaseId = caseId
                      StartedUtc = started.ToString("O", CultureInfo.InvariantCulture)
                      FinishedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                      FsdbRevision = revision
                      FsdbDirty = dirty
                      FsdbAssemblySha256 = Hashing.file assemblyPath
                      MySqlVersion = mysqlVersion
                      Seed = options.Seed
                      Workers = options.Workers
                      OperationsPerWorker = options.OperationsPerWorker
                      Accounts = options.Accounts
                      HotAccounts = options.HotAccounts
                      RollbackEvery = options.RollbackEvery
                      TimeoutSeconds = options.TimeoutSeconds
                      MySql = mysql
                      Fsdb = fsdb
                      FsdbInvariantErrors = invariantErrors
                      PeakWorkingSetBytes = max currentProcess.PeakWorkingSet64 currentProcess.WorkingSet64
                      Classification = classification
                      ClassificationDetail = detail
                      FailureSignature = signature
                      Passed = classification = "pass" }

                Json.write (Path.Combine(caseDirectory, "manifest.json")) manifest
                return Ok(manifest, caseDirectory)
        }
