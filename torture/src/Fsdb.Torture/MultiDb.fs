namespace Fsdb.Torture

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Threading.Tasks

/// The single-database concurrency lane (`Concurrency.fs`) proves that
/// FSDB never loses a write among workers sharing one database. It cannot
/// exercise the actual point of FSDB's per-database write gates: that
/// unrelated databases run in parallel instead of queuing behind one
/// another. This lane runs the identical prepared-transaction ledger
/// workload against D independent databases at once, on one shared FSDB
/// server/store, and checks three things MySql/single-db can't: each
/// database's ledger still conserves on its own, no database's write ever
/// lands in another database's tables, and the wall-clock cost of running D
/// databases concurrently stays well under D times the cost of running one
/// alone.
[<RequireQualifiedAccess>]
module MultiDbRunner =
    let private fsdbConnectionStringFor port database =
        sprintf
            "Server=127.0.0.1;Port=%d;User ID=root;Password=;Database=%s;SslMode=None;AllowPublicKeyRetrieval=True"
            port
            database

    // Distinct seeds per database (and for the standalone baseline) give
    // each database its own transfer plan, so a write that leaked into the
    // wrong database's ledger/account rows would break that database's
    // exact expected-state match instead of silently matching by
    // coincidence.
    let private perDatabaseOptions (options: MultiDbOptions) seedOffset : ConcurrencyOptions =
        { Seed = options.Seed + uint64 seedOffset * 104729UL
          Workers = options.WorkersPerDatabase
          OperationsPerWorker = options.OperationsPerWorker
          Accounts = options.Accounts
          HotAccounts = options.HotAccounts
          RollbackEvery = options.RollbackEvery
          TimeoutSeconds = options.TimeoutSeconds
          ArtifactRoot = options.ArtifactRoot
          MySqlConnection = options.MySqlConnection }

    let private classifyDatabase (mysql: ConcurrencyTargetReport) (fsdb: ConcurrencyTargetReport) =
        if not mysql.Passed then "oracle_concurrency_failure", mysql.Detail
        elif fsdb.FailedOperations > 0 then "fsdb_concurrency_execution_gap", fsdb.Detail
        elif not fsdb.Passed then "fsdb_transaction_atomicity_gap", fsdb.Detail
        else "pass", fsdb.Detail

    let run (options: MultiDbOptions) =
        task {
            let started = DateTimeOffset.UtcNow
            let runId = Paths.uniqueRunId ()

            let caseId =
                sprintf
                    "multidb-seed%d-dbs%d-workers%d-ops%d-hot%d-rollback%d"
                    options.Seed
                    options.Databases
                    options.WorkersPerDatabase
                    options.OperationsPerWorker
                    options.HotAccounts
                    options.RollbackEvery

            let caseDirectory = Path.Combine(options.ArtifactRoot, runId, caseId)
            Directory.CreateDirectory caseDirectory |> ignore
            let! revision, dirty = Tooling.gitState ()
            let assemblyPath = typeof<Fsdb.Storage.Store>.Assembly.Location
            let runTag = (Hashing.text runId).Substring(0, 12)

            use subject = new FsdbSubject()
            use! versionAdmin = Database.openConnection (fsdbConnectionStringFor subject.Port Fsdb.Storage.defaultDatabase)
            let! mysqlVersionConnection = Database.openConnection options.MySqlConnection
            let! mysqlVersion = Database.scalarString mysqlVersionConnection options.TimeoutSeconds "SELECT VERSION()"

            // A dedicated baseline database, outside the D databases under
            // test, isolates the single-database timing from any
            // contention with the concurrent phase that follows it.
            let baselineDbName = sprintf "torture_multidb_baseline_%d_%s" Environment.ProcessId runTag
            let databaseNames = [| for index in 0 .. options.Databases - 1 -> sprintf "torture_multidb_%d_%s_%d" Environment.ProcessId runTag index |]

            let! baselineCreate =
                Database.execute "fsdb" versionAdmin options.TimeoutSeconds (sprintf "CREATE DATABASE %s" (Database.quoteIdentifier baselineDbName))

            let mutable fsdbCreateFailure = if TargetOutcome.succeeded baselineCreate then None else Some baselineCreate.Message

            for name in databaseNames do
                if fsdbCreateFailure.IsNone then
                    let! created = Database.execute "fsdb" versionAdmin options.TimeoutSeconds (sprintf "CREATE DATABASE %s" (Database.quoteIdentifier name))

                    if not (TargetOutcome.succeeded created) then
                        fsdbCreateFailure <- Some created.Message

            match fsdbCreateFailure with
            | Some message -> return Error(sprintf "could not create multidb fsdb database: %s" message)
            | None ->
                let! oracleResults =
                    databaseNames
                    |> Array.map (fun name -> Database.createOracleDatabase options.MySqlConnection name options.TimeoutSeconds)
                    |> Task.WhenAll

                match oracleResults |> Array.tryPick (function
                    | Error error -> Some error
                    | Ok _ -> None) with
                | Some error -> return Error(sprintf "could not create multidb oracle database: %s" error.Message)
                | None ->
                    let oracleConnectionStrings =
                        oracleResults
                        |> Array.map (function
                            | Ok connectionString -> connectionString
                            | Error _ -> "")

                    let baselineOptions = perDatabaseOptions options -1
                    let baselineConnectionString = fsdbConnectionStringFor subject.Port baselineDbName
                    let! baselineReport = ConcurrencyRunner.runTarget "fsdb" baselineConnectionString baselineOptions

                    let dbOptions = [| for index in 0 .. options.Databases - 1 -> perDatabaseOptions options index |]
                    let fsdbConnectionStrings = databaseNames |> Array.map (fsdbConnectionStringFor subject.Port)

                    let! mysqlReports =
                        Array.map2 (fun connectionString opts -> ConcurrencyRunner.runTarget "mysql" connectionString opts) oracleConnectionStrings dbOptions
                        |> Task.WhenAll

                    // Only the fsdb phase is timed for the scaling proof —
                    // MySQL's row-level locking isn't the system under
                    // test, and running it first/outside the stopwatch
                    // keeps the wall-clock measurement clean.
                    let stopwatch = Stopwatch.StartNew()

                    let! fsdbReports =
                        Array.map2 (fun connectionString opts -> ConcurrencyRunner.runTarget "fsdb" connectionString opts) fsdbConnectionStrings dbOptions
                        |> Task.WhenAll

                    stopwatch.Stop()
                    let multiDbWallClockElapsedMs = stopwatch.ElapsedMilliseconds

                    let databaseReports =
                        [| for index in 0 .. options.Databases - 1 ->
                               let classification, _ = classifyDatabase mysqlReports.[index] fsdbReports.[index]

                               { DatabaseIndex = index
                                 DatabaseName = databaseNames.[index]
                                 MySql = mysqlReports.[index]
                                 Fsdb = fsdbReports.[index]
                                 Classification = classification
                                 Passed = classification = "pass" } |]

                    let invariantErrors = Invariants.validate subject.Store

                    let serialProjectedElapsedMs = int64 options.Databases * baselineReport.ElapsedMs

                    let scalingRatio =
                        if serialProjectedElapsedMs <= 0L then
                            0.0
                        else
                            float multiDbWallClockElapsedMs / float serialProjectedElapsedMs

                    let scalingPassed = options.Databases <= 1 || scalingRatio <= options.ScalingFactor

                    let classification, detail =
                        match databaseReports |> Array.tryFind (fun report -> report.Classification = "oracle_concurrency_failure") with
                        | Some bad -> "oracle_concurrency_failure", bad.MySql.Detail
                        | None ->
                            match databaseReports |> Array.tryFind (fun report -> report.Classification = "fsdb_concurrency_execution_gap") with
                            | Some bad -> "fsdb_concurrency_execution_gap", bad.Fsdb.Detail
                            | None ->
                                match databaseReports |> Array.tryFind (fun report -> report.Classification = "fsdb_transaction_atomicity_gap") with
                                | Some bad -> "fsdb_transaction_atomicity_gap", bad.Fsdb.Detail
                                | None when invariantErrors.Length > 0 -> "invariant_failure", String.concat "; " invariantErrors
                                | None when not scalingPassed ->
                                    "multidb_scaling_gap",
                                    sprintf
                                        "multidb wall-clock %d ms across %d databases exceeded %.0f%% of the %d ms serial-projected cost (baseline %d ms for %d workers on one database)"
                                        multiDbWallClockElapsedMs
                                        options.Databases
                                        (options.ScalingFactor * 100.0)
                                        serialProjectedElapsedMs
                                        baselineReport.ElapsedMs
                                        options.WorkersPerDatabase
                                | None ->
                                    "pass", "every database conserved independently, no cross-database bleed, and multi-database wall-clock beat the scaling threshold"

                    let signature =
                        if classification = "pass" then
                            ""
                        else
                            Hashing.combine
                                [ classification
                                  string options.Seed
                                  string options.Databases
                                  string options.WorkersPerDatabase
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
                          Databases = options.Databases
                          WorkersPerDatabase = options.WorkersPerDatabase
                          OperationsPerWorker = options.OperationsPerWorker
                          Accounts = options.Accounts
                          HotAccounts = options.HotAccounts
                          RollbackEvery = options.RollbackEvery
                          TimeoutSeconds = options.TimeoutSeconds
                          DatabaseReports = databaseReports
                          BaselineSingleDbElapsedMs = baselineReport.ElapsedMs
                          MultiDbWallClockElapsedMs = multiDbWallClockElapsedMs
                          SerialProjectedElapsedMs = serialProjectedElapsedMs
                          ScalingRatio = scalingRatio
                          ScalingFactor = options.ScalingFactor
                          ScalingPassed = scalingPassed
                          FsdbInvariantErrors = invariantErrors
                          PeakWorkingSetBytes = max currentProcess.PeakWorkingSet64 currentProcess.WorkingSet64
                          Classification = classification
                          ClassificationDetail = detail
                          FailureSignature = signature
                          Passed = classification = "pass" }

                    Json.write (Path.Combine(caseDirectory, "manifest.json")) manifest
                    return Ok(manifest, caseDirectory)
        }
