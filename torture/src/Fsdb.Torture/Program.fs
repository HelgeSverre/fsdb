namespace Fsdb.Torture

open System
open System.IO

type private Command =
    | Run
    | Suite
    | Concurrency
    | MultiDb
    | Syntax
    | Replay
    | CheckTools
    | Help

type private Cli =
    { Command: Command
      Scenario: ScenarioName option
      Seed: uint64
      Scale: uint64
      MaxRows: uint64
      BatchSize: uint64 option
      InvariantEvery: int
      TimeoutSeconds: int
      Workers: int
      OperationsPerWorker: int
      Accounts: int
      HotAccounts: int
      RollbackEvery: int
      Databases: int
      ScalingFactor: float
      SyntaxCases: int
      SyntaxDepth: int
      Artifacts: string
      MySql: string
      SqlSplitter: string
      ReplayCase: string }

[<RequireQualifiedAccess>]
module private Cli =
    let usage =
        """FSDB bespoke differential torture harness

Usage:
  fsdb-torture run --scenario <scalar|relational|commerce|volume> [options]
  fsdb-torture suite [options]
  fsdb-torture concurrency [options]
  fsdb-torture multidb [options]
  fsdb-torture syntax [options]
  fsdb-torture replay --case <artifact-directory> [options]
  fsdb-torture check-tools [--sql-splitter <path>]

Options:
  --seed <n>                 Root SQL Splitter seed (default 1)
  --scale <n>                Multiply model row counts (default 1)
  --max-rows <n>             Per-table row cap (default 8)
  --batch-size <n>           Override scenario INSERT batch size
  --invariant-every <n>      Validate every N statements; 0 means final only (default 1)
  --timeout-seconds <n>      Per-statement/query deadline (default 10)
  --workers <n>              Concurrent prepared-statement sessions per database (default 8)
  --operations <n>           Transactions per worker (default 100)
  --accounts <n>             Total initialized accounts per database (default 32)
  --hot-accounts <n>         Contended account set, at least 2 (default 4)
  --rollback-every <n>       Roll back every Nth operation; 0 disables (default 5)
  --databases <n>            multidb: databases run concurrently (default 4)
  --scaling-factor <n>       multidb: multi-db wall-clock must be <= this fraction of the serial-projected cost (default 0.65)
  --syntax-cases <n>          syntax: deterministic mutations; 0 runs baselines only (default 64)
  --syntax-depth <1..3>       syntax: maximum chained mutations per statement (default 1)
  --artifacts <directory>    Artifact root (default torture/artifacts/runs)
  --mysql <connection>       MySQL admin connection string
  --sql-splitter <path>      SQL Splitter executable
"""

    let private parseUInt64 (flag: string) (value: string) =
        match UInt64.TryParse value with
        | true, parsed -> Ok parsed
        | _ -> Error(sprintf "%s expects a non-negative integer, got '%s'" flag value)

    let private parseInt (flag: string) (value: string) =
        match Int32.TryParse value with
        | true, parsed when parsed > 0 -> Ok parsed
        | _ -> Error(sprintf "%s expects a positive integer, got '%s'" flag value)

    let private parseRange (flag: string) minimum maximum (value: string) =
        match Int32.TryParse value with
        | true, parsed when parsed >= minimum && parsed <= maximum -> Ok parsed
        | _ -> Error(sprintf "%s expects an integer from %d through %d, got '%s'" flag minimum maximum value)

    let private parseNonNegativeInt (flag: string) (value: string) =
        match Int32.TryParse value with
        | true, parsed when parsed >= 0 -> Ok parsed
        | _ -> Error(sprintf "%s expects a non-negative integer, got '%s'" flag value)

    let private parsePositiveFloat (flag: string) (value: string) =
        match Double.TryParse(value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, parsed when parsed > 0.0 -> Ok parsed
        | _ -> Error(sprintf "%s expects a positive number, got '%s'" flag value)

    let parse (argv: string array) =
        let command, offset =
            match Array.tryHead argv |> Option.map (fun value -> value.ToLowerInvariant()) with
            | Some "run" -> Run, 1
            | Some "suite" -> Suite, 1
            | Some "concurrency" -> Concurrency, 1
            | Some "multidb" -> MultiDb, 1
            | Some "syntax" -> Syntax, 1
            | Some "replay" -> Replay, 1
            | Some "check-tools" -> CheckTools, 1
            | Some "--help"
            | Some "-h"
            | None -> Help, 1
            | Some other -> failwithf "unknown command '%s'" other

        let mutable scenario = None
        let mutable seed = 1UL
        let mutable scale = 1UL
        let mutable maxRows = 8UL
        let mutable batchSize = None
        let mutable invariantEvery = 1
        let mutable timeoutSeconds = 10
        let mutable workers = 8
        let mutable operationsPerWorker = 100
        let mutable accounts = 32
        let mutable hotAccounts = 4
        let mutable rollbackEvery = 5
        let mutable databases = 4
        let mutable scalingFactor = 0.65
        let mutable syntaxCases = 64
        let mutable syntaxDepth = 1
        let mutable artifacts = Paths.defaultArtifactRoot ()
        let mutable mysql = ""
        let mutable sqlSplitter = ""
        let mutable replayCase = ""
        let mutable index = offset
        let mutable failure: string option = None

        let nextValue flag =
            if index + 1 >= argv.Length then
                failure <- Some(sprintf "%s requires a value" flag)
                ""
            else
                index <- index + 1
                argv.[index]

        while index < argv.Length && failure.IsNone do
            let flag = argv.[index]

            match flag with
            | "--scenario" ->
                match ScenarioName.parse (nextValue flag) with
                | Ok value -> scenario <- Some value
                | Error error -> failure <- Some error
            | "--seed" ->
                match parseUInt64 flag (nextValue flag) with
                | Ok value -> seed <- value
                | Error error -> failure <- Some error
            | "--scale" ->
                match parseUInt64 flag (nextValue flag) with
                | Ok value when value > 0UL -> scale <- value
                | Ok _ -> failure <- Some "--scale must be greater than zero"
                | Error error -> failure <- Some error
            | "--max-rows" ->
                match parseUInt64 flag (nextValue flag) with
                | Ok value when value > 0UL -> maxRows <- value
                | Ok _ -> failure <- Some "--max-rows must be greater than zero"
                | Error error -> failure <- Some error
            | "--batch-size" ->
                match parseUInt64 flag (nextValue flag) with
                | Ok value when value > 0UL -> batchSize <- Some value
                | Ok _ -> failure <- Some "--batch-size must be greater than zero"
                | Error error -> failure <- Some error
            | "--invariant-every" ->
                match parseNonNegativeInt flag (nextValue flag) with
                | Ok value -> invariantEvery <- value
                | Error error -> failure <- Some error
            | "--timeout-seconds" ->
                match parseInt flag (nextValue flag) with
                | Ok value -> timeoutSeconds <- value
                | Error error -> failure <- Some error
            | "--workers" ->
                match parseInt flag (nextValue flag) with
                | Ok value -> workers <- value
                | Error error -> failure <- Some error
            | "--operations" ->
                match parseInt flag (nextValue flag) with
                | Ok value -> operationsPerWorker <- value
                | Error error -> failure <- Some error
            | "--accounts" ->
                match parseInt flag (nextValue flag) with
                | Ok value -> accounts <- value
                | Error error -> failure <- Some error
            | "--hot-accounts" ->
                match parseInt flag (nextValue flag) with
                | Ok value -> hotAccounts <- value
                | Error error -> failure <- Some error
            | "--rollback-every" ->
                match parseNonNegativeInt flag (nextValue flag) with
                | Ok value -> rollbackEvery <- value
                | Error error -> failure <- Some error
            | "--databases" ->
                match parseInt flag (nextValue flag) with
                | Ok value -> databases <- value
                | Error error -> failure <- Some error
            | "--scaling-factor" ->
                match parsePositiveFloat flag (nextValue flag) with
                | Ok value -> scalingFactor <- value
                | Error error -> failure <- Some error
            | "--syntax-cases" ->
                match parseRange flag 0 10000 (nextValue flag) with
                | Ok value -> syntaxCases <- value
                | Error error -> failure <- Some error
            | "--syntax-depth" ->
                match parseRange flag 1 3 (nextValue flag) with
                | Ok value -> syntaxDepth <- value
                | Error error -> failure <- Some error
            | "--artifacts" -> artifacts <- nextValue flag |> Path.GetFullPath
            | "--mysql" -> mysql <- nextValue flag
            | "--sql-splitter" -> sqlSplitter <- nextValue flag
            | "--case" -> replayCase <- nextValue flag |> Path.GetFullPath
            | "--help"
            | "-h" -> ()
            | other -> failure <- Some(sprintf "unknown option '%s'" other)

            index <- index + 1

        match failure with
        | Some error -> Error error
        | None when command = Run && scenario.IsNone -> Error "run requires --scenario"
        | None when command = Replay && String.IsNullOrWhiteSpace replayCase -> Error "replay requires --case"
        | None when (command = Concurrency || command = MultiDb) && hotAccounts < 2 -> Error "concurrency/multidb requires --hot-accounts of at least 2"
        | None when (command = Concurrency || command = MultiDb) && hotAccounts > accounts -> Error "--hot-accounts cannot exceed --accounts"
        | None when command = MultiDb && databases < 1 -> Error "multidb requires --databases of at least 1"
        | None when (command = Run || command = Suite || command = Replay || command = Concurrency || command = MultiDb || command = Syntax) && String.IsNullOrWhiteSpace mysql ->
            Error "run, suite, concurrency, multidb, syntax, and replay require --mysql (torture/scripts/run.sh supplies it automatically)"
        | None ->
            Ok
                { Command = command
                  Scenario = scenario
                  Seed = seed
                  Scale = scale
                  MaxRows = maxRows
                  BatchSize = batchSize
                  InvariantEvery = invariantEvery
                  TimeoutSeconds = timeoutSeconds
                  Workers = workers
                  OperationsPerWorker = operationsPerWorker
                  Accounts = accounts
                  HotAccounts = hotAccounts
                  RollbackEvery = rollbackEvery
                  Databases = databases
                  ScalingFactor = scalingFactor
                  SyntaxCases = syntaxCases
                  SyntaxDepth = syntaxDepth
                  Artifacts = artifacts
                  MySql = mysql
                  SqlSplitter = sqlSplitter
                  ReplayCase = replayCase }

    let runOptions cli scenario toolPath =
        { Scenario = scenario
          Seed = cli.Seed
          Scale = cli.Scale
          MaxRows = cli.MaxRows
          BatchSize = cli.BatchSize |> Option.defaultValue (uint64 (ScenarioName.defaultBatchSize scenario))
          InvariantEvery = cli.InvariantEvery
          TimeoutSeconds = cli.TimeoutSeconds
          ArtifactRoot = cli.Artifacts
          MySqlConnection = cli.MySql
          SqlSplitter = toolPath }

    let concurrencyOptions cli =
        { Seed = cli.Seed
          Workers = cli.Workers
          OperationsPerWorker = cli.OperationsPerWorker
          Accounts = cli.Accounts
          HotAccounts = cli.HotAccounts
          RollbackEvery = cli.RollbackEvery
          TimeoutSeconds = cli.TimeoutSeconds
          ArtifactRoot = cli.Artifacts
          MySqlConnection = cli.MySql }

    let multiDbOptions cli =
        { Seed = cli.Seed
          Databases = cli.Databases
          WorkersPerDatabase = cli.Workers
          OperationsPerWorker = cli.OperationsPerWorker
          Accounts = cli.Accounts
          HotAccounts = cli.HotAccounts
          RollbackEvery = cli.RollbackEvery
          ScalingFactor = cli.ScalingFactor
          TimeoutSeconds = cli.TimeoutSeconds
          ArtifactRoot = cli.Artifacts
          MySqlConnection = cli.MySql }

    let syntaxOptions cli : SyntaxOptions =
        { Seed = cli.Seed
          Cases = cli.SyntaxCases
          Depth = cli.SyntaxDepth
          TimeoutSeconds = cli.TimeoutSeconds
          ArtifactRoot = cli.Artifacts
          MySqlConnection = cli.MySql }

module Program =
    let private inspectTool requested =
        task {
            match Tooling.resolveSqlSplitter requested with
            | Error error -> return Error error
            | Ok path -> return! Tooling.inspectSqlSplitter path
        }

    let private checkDocker () =
        task {
            try
                let! result = ProcessRunner.run "docker" [| "info" |] (Paths.tortureRoot ()) (TimeSpan.FromSeconds 15.0)
                return if result.ExitCode = 0 then Ok() else Error("docker info failed: " + result.Stderr)
            with error ->
                return Error("docker is unavailable: " + error.Message)
        }

    let private printManifest report directory =
        printfn
            "%s: %s%s — %s"
            report.CaseId
            report.Classification
            (if report.KnownGap then " (known)" else "")
            directory

        let totalRows = report.Comparison.Fsdb.Tables |> Array.sumBy _.RowCount
        let loadMs = report.FsdbLoadElapsedMs
        let rowsPerSecond = if loadMs > 0L then float totalRows * 1000.0 / float loadMs else 0.0
        let mysqlProbeMs = report.Probes |> Array.sumBy (fun probe -> probe.MySql.ElapsedMs)
        let fsdbProbeMs = report.Probes |> Array.sumBy (fun probe -> probe.Fsdb.ElapsedMs)
        let dmlFailures = report.Dml |> Array.filter (fun record -> not record.Equal) |> Array.length

        printfn
            "  rows=%d sql=%.1f MiB mysql-load=%d ms fsdb-load=%d ms (%.0f rows/s) snapshots=%d/%d ms peak=%.1f MiB"
            totalRows
            (float report.GeneratedSqlBytes / 1048576.0)
            report.MySqlLoadElapsedMs
            report.FsdbLoadElapsedMs
            rowsPerSecond
            report.MySqlSnapshotElapsedMs
            report.FsdbSnapshotElapsedMs
            (float report.PeakWorkingSetBytes / 1048576.0)

        printfn
            "  invariants=%d ms probes=%d/%d ms (mysql/fsdb) dml=%d cases/%d differences"
            report.InvariantElapsedMs
            mysqlProbeMs
            fsdbProbeMs
            report.Dml.Length
            dmlFailures

        if report.Classification <> "pass" then
            let firstLine =
                report.ClassificationDetail.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.tryHead
                |> Option.defaultValue "no classification detail"
                |> fun value -> if value.Length > 240 then value.Substring(0, 240) + "…" else value

            printfn "  detail: %s" firstLine

        if not report.Passed then
            printfn "  signature: %s" report.FailureSignature

    let private printConcurrency (report: ConcurrencyManifest) directory =
        printfn "%s: %s — %s" report.CaseId report.Classification directory

        let printTarget (target: ConcurrencyTargetReport) =
            printfn
                "  %s: attempted=%d committed=%d rolled-back=%d failed=%d ledger=%d/%d elapsed=%d ms throughput=%.0f tx/s p50/p95/p99=%d/%d/%d ms"
                target.Target
                target.AttemptedOperations
                target.CommittedOperations
                target.RolledBackOperations
                target.FailedOperations
                target.ActualLedgerRows
                target.ExpectedLedgerRows
                target.ElapsedMs
                target.OperationsPerSecond
                target.LatencyP50Ms
                target.LatencyP95Ms
                target.LatencyP99Ms

        printTarget report.MySql
        printTarget report.Fsdb
        printfn "  peak=%.1f MiB detail: %s" (float report.PeakWorkingSetBytes / 1048576.0) report.ClassificationDetail

        if not report.Passed then
            printfn "  signature: %s" report.FailureSignature

    let private printMultiDb (report: MultiDbManifest) directory =
        printfn "%s: %s — %s" report.CaseId report.Classification directory

        for db in report.DatabaseReports do
            printfn
                "  db[%d] %s: %s fsdb committed=%d/%d rolled-back=%d failed=%d elapsed=%d ms throughput=%.0f tx/s"
                db.DatabaseIndex
                db.DatabaseName
                db.Classification
                db.Fsdb.CommittedOperations
                db.Fsdb.ExpectedLedgerRows
                db.Fsdb.RolledBackOperations
                db.Fsdb.FailedOperations
                db.Fsdb.ElapsedMs
                db.Fsdb.OperationsPerSecond

        printfn
            "  baseline=%d ms serial-projected=%d ms multidb-wallclock=%d ms ratio=%.2f threshold=%.2f scaling=%s"
            report.BaselineSingleDbElapsedMs
            report.SerialProjectedElapsedMs
            report.MultiDbWallClockElapsedMs
            report.ScalingRatio
            report.ScalingFactor
            (if report.ScalingPassed then "pass" else "fail")

        printfn "  peak=%.1f MiB detail: %s" (float report.PeakWorkingSetBytes / 1048576.0) report.ClassificationDetail

        if not report.Passed then
            printfn "  signature: %s" report.FailureSignature

    let private printSyntax (report: SyntaxManifest) directory =
        let baselines = report.Cases |> Array.filter (fun record -> record.Mutation = "baseline")
        let mutations = report.Cases.Length - baselines.Length
        let rejected = report.Cases |> Array.filter (fun record -> record.Classification = "matched_syntax_error") |> Array.length
        let accepted = report.Cases |> Array.filter (fun record -> record.Classification = "accepted_mutation") |> Array.length
        let differences = report.Cases |> Array.filter (fun record -> not record.Passed) |> Array.length

        printfn "%s: %s — %s" report.CaseId report.Classification directory
        printfn
            "  baselines=%d mutations=%d depth=%d matched-errors=%d accepted-mutations=%d differences=%d"
            baselines.Length
            mutations
            report.MutationDepth
            rejected
            accepted
            differences

        if not report.Passed then
            printfn "  detail: %s" report.ClassificationDetail
            printfn "  signature: %s" report.FailureSignature

    let execute argv =
        task {
            let parsed =
                try
                    Cli.parse argv
                with error ->
                    Error error.Message

            match parsed with
            | Error error ->
                eprintfn "error: %s\n\n%s" error Cli.usage
                return 1
            | Ok cli when cli.Command = Help ->
                printf "%s" Cli.usage
                return 0
            | Ok cli when cli.Command = Concurrency ->
                try
                    match! ConcurrencyRunner.run (Cli.concurrencyOptions cli) with
                    | Error error ->
                        eprintfn "concurrency infrastructure failure: %s" error
                        return 1
                    | Ok(report, directory) ->
                        printConcurrency report directory

                        if report.Passed then
                            return 0
                        elif report.Classification = "oracle_concurrency_failure" then
                            return 1
                        else
                            return 2
                with error ->
                    eprintfn "concurrency infrastructure exception: %O" error
                    return 1
            | Ok cli when cli.Command = MultiDb ->
                try
                    match! MultiDbRunner.run (Cli.multiDbOptions cli) with
                    | Error error ->
                        eprintfn "multidb infrastructure failure: %s" error
                        return 1
                    | Ok(report, directory) ->
                        printMultiDb report directory

                        if report.Passed then
                            return 0
                        elif report.Classification = "oracle_concurrency_failure" then
                            return 1
                        else
                            return 2
                with error ->
                    eprintfn "multidb infrastructure exception: %O" error
                    return 1
            | Ok cli when cli.Command = Syntax ->
                try
                    match! SyntaxFuzz.run (Cli.syntaxOptions cli) with
                    | Error error ->
                        eprintfn "syntax infrastructure failure: %s" error
                        return 1
                    | Ok(report, directory) ->
                        printSyntax report directory

                        if report.Passed then
                            return 0
                        elif report.Classification = "infrastructure" || report.Classification = "oracle_baseline_rejected" then
                            return 1
                        else
                            return 2
                with error ->
                    eprintfn "syntax infrastructure exception: %O" error
                    return 1
            | Ok cli ->
                match! inspectTool cli.SqlSplitter with
                | Error error ->
                    eprintfn "error: %s" error
                    return 1
                | Ok tool when cli.Command = CheckTools ->
                    match! checkDocker () with
                    | Error error ->
                        eprintfn "error: %s" error
                        return 1
                    | Ok() ->
                        printfn "sql-splitter %s: %s" tool.Version tool.Path
                        printfn "docker: reachable"
                        printfn ".NET: %s" (Environment.Version.ToString())
                        return 0
                | Ok tool when cli.Command = Replay ->
                    match! Runner.replay tool cli.ReplayCase cli.MySql tool.Path with
                    | Error error ->
                        eprintfn "replay failed: %s" error
                        return 3
                    | Ok(report, directory) ->
                        printManifest report directory
                        return 0
                | Ok tool ->
                    let scenarios =
                        match cli.Command, cli.Scenario with
                        | Run, Some scenario -> [| scenario |]
                        | Suite, _ -> ScenarioName.all
                        | _ -> [||]

                    let runId = Paths.uniqueRunId ()
                    let mutable infrastructureFailure = false
                    let mutable finding = false

                    for scenario in scenarios do
                        let options = Cli.runOptions cli scenario tool.Path

                        try
                            let! report, directory = Runner.runScenario tool options runId
                            printManifest report directory

                            if not report.Passed then
                                match report.Classification with
                                | "generator_preflight"
                                | "oracle_rejected"
                                | "oracle_timeout"
                                | "infrastructure" -> infrastructureFailure <- true
                                | _ -> finding <- true
                        with error ->
                            infrastructureFailure <- true
                            eprintfn "%s: infrastructure exception: %O" (ScenarioName.text scenario) error

                    if infrastructureFailure then return 1
                    elif finding then return 2
                    else return 0
        }

module EntryPoint =
    [<EntryPoint>]
    let main argv = Program.execute argv |> Async.AwaitTask |> Async.RunSynchronously
