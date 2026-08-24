namespace Fsdb.Torture

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type ScenarioName =
    | Scalar
    | Relational
    | Commerce
    | Volume

[<RequireQualifiedAccess>]
module ScenarioName =
    let all = [| Scalar; Relational; Commerce; Volume |]

    let text =
        function
        | Scalar -> "scalar"
        | Relational -> "relational"
        | Commerce -> "commerce"
        | Volume -> "volume"

    let parse (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "scalar" -> Ok Scalar
        | "relational" -> Ok Relational
        | "commerce" -> Ok Commerce
        | "volume" -> Ok Volume
        | _ -> Error(sprintf "unknown scenario '%s' (expected scalar, relational, commerce, or volume)" value)

    let defaultBatchSize =
        function
        | Scalar
        | Relational -> 1
        | Commerce -> 8
        | Volume -> 10000

[<CLIMutable>]
type RunOptions =
    { Scenario: ScenarioName
      Seed: uint64
      Scale: uint64
      MaxRows: uint64
      BatchSize: uint64
      InvariantEvery: int
      TimeoutSeconds: int
      ArtifactRoot: string
      MySqlConnection: string
      SqlSplitter: string }

[<CLIMutable>]
type SyntaxOptions =
    { Seed: uint64
      Cases: int
      Depth: int
      TimeoutSeconds: int
      ArtifactRoot: string
      MySqlConnection: string }

[<CLIMutable>]
type ConcurrencyOptions =
    { Seed: uint64
      Workers: int
      OperationsPerWorker: int
      Accounts: int
      HotAccounts: int
      RollbackEvery: int
      TimeoutSeconds: int
      ArtifactRoot: string
      MySqlConnection: string }

[<CLIMutable>]
type ConcurrencyOperationPlan =
    { OperationId: int64
      Worker: int
      Iteration: int
      FromAccount: int
      ToAccount: int
      Amount: int64
      Rollback: bool }

[<CLIMutable>]
type ConcurrencyOperationRecord =
    { OperationId: int64
      Worker: int
      Iteration: int
      FromAccount: int
      ToAccount: int
      Amount: int64
      IntendedOutcome: string
      Status: string
      Stage: string
      ErrorCode: int
      SqlState: string
      Message: string
      ElapsedMs: int64 }

[<CLIMutable>]
type ConcurrencyAccountRecord =
    { Id: int
      ExpectedBalance: int64
      ActualBalance: int64
      ExpectedVersion: int64
      ActualVersion: int64
      Equal: bool }

[<CLIMutable>]
type ConcurrencyTargetReport =
    { Target: string
      ElapsedMs: int64
      PreparedCommands: int
      AttemptedOperations: int
      CommittedOperations: int
      RolledBackOperations: int
      FailedOperations: int
      OperationsPerSecond: float
      LatencyP50Ms: int64
      LatencyP95Ms: int64
      LatencyP99Ms: int64
      ExpectedLedgerRows: int
      ActualLedgerRows: int
      ExpectedLedgerSha256: string
      ActualLedgerSha256: string
      ExpectedTotalBalance: int64
      ActualTotalBalance: int64
      Accounts: ConcurrencyAccountRecord array
      History: ConcurrencyOperationRecord array
      Passed: bool
      Detail: string }

[<CLIMutable>]
type ConcurrencyManifest =
    { SchemaVersion: int
      RunId: string
      CaseId: string
      StartedUtc: string
      FinishedUtc: string
      FsdbRevision: string
      FsdbDirty: bool
      FsdbAssemblySha256: string
      MySqlVersion: string
      Seed: uint64
      Workers: int
      OperationsPerWorker: int
      Accounts: int
      HotAccounts: int
      RollbackEvery: int
      TimeoutSeconds: int
      MySql: ConcurrencyTargetReport
      Fsdb: ConcurrencyTargetReport
      FsdbInvariantErrors: string array
      PeakWorkingSetBytes: int64
      Classification: string
      ClassificationDetail: string
      FailureSignature: string
      Passed: bool }

[<CLIMutable>]
type MultiDbOptions =
    { Seed: uint64
      Databases: int
      WorkersPerDatabase: int
      OperationsPerWorker: int
      Accounts: int
      HotAccounts: int
      RollbackEvery: int
      ScalingFactor: float
      TimeoutSeconds: int
      ArtifactRoot: string
      MySqlConnection: string }

[<CLIMutable>]
type MultiDbDatabaseReport =
    { DatabaseIndex: int
      DatabaseName: string
      MySql: ConcurrencyTargetReport
      Fsdb: ConcurrencyTargetReport
      Classification: string
      Passed: bool }

[<CLIMutable>]
type MultiDbManifest =
    { SchemaVersion: int
      RunId: string
      CaseId: string
      StartedUtc: string
      FinishedUtc: string
      FsdbRevision: string
      FsdbDirty: bool
      FsdbAssemblySha256: string
      MySqlVersion: string
      Seed: uint64
      Databases: int
      WorkersPerDatabase: int
      OperationsPerWorker: int
      Accounts: int
      HotAccounts: int
      RollbackEvery: int
      TimeoutSeconds: int
      DatabaseReports: MultiDbDatabaseReport array
      BaselineSingleDbElapsedMs: int64
      MultiDbWallClockElapsedMs: int64
      SerialProjectedElapsedMs: int64
      ScalingRatio: float
      ScalingFactor: float
      ScalingPassed: bool
      FsdbInvariantErrors: string array
      PeakWorkingSetBytes: int64
      Classification: string
      ClassificationDetail: string
      FailureSignature: string
      Passed: bool }

[<CLIMutable>]
type ProcessResult =
    { ExitCode: int
      Stdout: string
      Stderr: string
      ElapsedMs: int64
      TimedOut: bool }

[<CLIMutable>]
type ToolRecord =
    { Name: string
      Version: string
      Path: string
      Sha256: string }

[<CLIMutable>]
type TargetOutcome =
    { Target: string
      Status: string
      AffectedRows: int
      ErrorCode: int
      SqlState: string
      Message: string
      ElapsedMs: int64 }

[<RequireQualifiedAccess>]
module TargetOutcome =
    let notRun target =
        { Target = target
          Status = "not_run"
          AffectedRows = 0
          ErrorCode = 0
          SqlState = ""
          Message = ""
          ElapsedMs = 0L }

    let succeeded outcome = outcome.Status = "success"

[<CLIMutable>]
type StatementRecord =
    { Index: int
      StartByte: int
      EndByte: int
      Sha256: string
      Sql: string
      ParserStatus: string
      AstKind: string
      MySql: TargetOutcome
      Fsdb: TargetOutcome
      CompareAffectedRows: bool
      Classification: string
      Detail: string
      CommitEvents: string array
      InvariantErrors: string array }

[<CLIMutable>]
type ProbeOutcome =
    { Target: string
      Status: string
      Columns: string array
      ColumnTypes: string array
      Rows: string array
      DataSha256: string
      ErrorCode: int
      SqlState: string
      Message: string
      ElapsedMs: int64 }

[<RequireQualifiedAccess>]
module ProbeOutcome =
    let notRun target =
        { Target = target
          Status = "not_run"
          Columns = [||]
          ColumnTypes = [||]
          Rows = [||]
          DataSha256 = ""
          ErrorCode = 0
          SqlState = ""
          Message = ""
          ElapsedMs = 0L }

    let succeeded outcome = outcome.Status = "success"

[<CLIMutable>]
type ProbeRecord =
    { Name: string
      Sql: string
      SqlSha256: string
      ParserStatus: string
      ParserDetail: string
      MySql: ProbeOutcome
      Fsdb: ProbeOutcome
      Equal: bool
      Detail: string }

[<CLIMutable>]
type DmlRecord =
    { Mode: string
      Index: int
      Name: string
      Sql: string
      SqlSha256: string
      MySql: TargetOutcome
      Fsdb: TargetOutcome
      Classification: string
      Equal: bool
      Detail: string }

[<CLIMutable>]
type SyntaxCaseRecord =
    { Index: int
      Feature: string
      Mutation: string
      Sql: string
      SqlSha256: string
      ParserStatus: string
      AstKind: string
      MySql: TargetOutcome
      Fsdb: TargetOutcome
      Classification: string
      Detail: string
      Passed: bool }

[<CLIMutable>]
type SyntaxManifest =
    { SchemaVersion: int
      RunId: string
      CaseId: string
      Seed: uint64
      RequestedMutations: int
      MutationDepth: int
      StartedUtc: string
      FinishedUtc: string
      FsdbRevision: string
      FsdbDirty: bool
      FsdbAssemblySha256: string
      MySqlVersion: string
      Cases: SyntaxCaseRecord array
      Classification: string
      ClassificationDetail: string
      FailureSignature: string
      Passed: bool }

[<CLIMutable>]
type ColumnSnapshot =
    { Name: string
      Type: string
      Nullable: bool
      Key: string
      DefaultValue: string
      Extra: string }

[<CLIMutable>]
type IndexSnapshot =
    { Name: string
      Unique: bool
      Sequence: int
      Column: string }

[<CLIMutable>]
type ForeignKeySnapshot =
    { Name: string
      Sequence: int
      Column: string
      ReferencedTable: string
      ReferencedColumn: string }

[<CLIMutable>]
type DataChunkSnapshot =
    { Index: int
      StartRow: int64
      RowCount: int
      Sha256: string
      FirstRow: string
      LastRow: string }

[<CLIMutable>]
type TableSnapshot =
    { Name: string
      Columns: ColumnSnapshot array
      Indexes: IndexSnapshot array
      ForeignKeys: ForeignKeySnapshot array
      RowCount: int64
      DataSha256: string
      DataChunks: DataChunkSnapshot array
      Rows: string array }

[<CLIMutable>]
type DatabaseSnapshot = { Tables: TableSnapshot array }

[<CLIMutable>]
type ComparisonRecord =
    { Equal: bool
      Category: string
      Detail: string
      MySql: DatabaseSnapshot
      Fsdb: DatabaseSnapshot }

[<CLIMutable>]
type RunManifest =
    { SchemaVersion: int
      RunId: string
      CaseId: string
      Scenario: string
      Seed: uint64
      Scale: uint64
      MaxRows: uint64
      BatchSize: uint64
      InvariantEvery: int
      TimeoutSeconds: int
      StartedUtc: string
      FinishedUtc: string
      FsdbRevision: string
      FsdbDirty: bool
      FsdbAssemblySha256: string
      SqlSplitter: ToolRecord
      MySqlVersion: string
      ModelPath: string
      ModelSha256: string
      GeneratedSqlSha256: string
      GeneratedSqlBytes: int64
      GeneratorExitCode: int
      GeneratorElapsedMs: int64
      GeneratorTimedOut: bool
      GeneratorDiagnostics: string
      MySqlLoadElapsedMs: int64
      FsdbLoadElapsedMs: int64
      InvariantElapsedMs: int64
      MySqlSnapshotElapsedMs: int64
      FsdbSnapshotElapsedMs: int64
      PeakWorkingSetBytes: int64
      Statements: StatementRecord array
      Dml: DmlRecord array
      Probes: ProbeRecord array
      Comparison: ComparisonRecord
      Classification: string
      ClassificationDetail: string
      FailureSignature: string
      KnownGap: bool
      Passed: bool }

[<CLIMutable>]
type KnownGapFile =
    { SchemaVersion: int
      Signatures: string array }

[<RequireQualifiedAccess>]
module Hashing =
    let bytes (value: byte array) = Convert.ToHexString(SHA256.HashData value).ToLowerInvariant()

    let text (value: string) = value |> Encoding.UTF8.GetBytes |> bytes

    let file path = File.ReadAllBytes path |> bytes

    let combine (values: string seq) = values |> String.concat "\n" |> text

[<RequireQualifiedAccess>]
module Json =
    let options =
        let value = JsonSerializerOptions(WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
        value.PropertyNameCaseInsensitive <- true
        value

    let serialize value = JsonSerializer.Serialize(value, options)

    let deserialize<'T> (value: string) =
        let parsed = JsonSerializer.Deserialize<'T>(value, options)

        if obj.ReferenceEquals(box parsed, null) then
            failwithf "JSON document did not contain a %s value" typeof<'T>.FullName

        parsed

    let write (path: string) value =
        let directory = Path.GetDirectoryName path

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory directory |> ignore

        File.WriteAllText(path, serialize value + Environment.NewLine, UTF8Encoding(false))

[<RequireQualifiedAccess>]
module Paths =
    let rec private findFrom (directory: DirectoryInfo) =
        if File.Exists(Path.Combine(directory.FullName, "src", "Fsdb", "Fsdb.fsproj")) then
            directory.FullName
        elif isNull directory.Parent then
            failwith "could not find the FSDB repository root"
        else
            findFrom directory.Parent

    let repoRoot () = findFrom (DirectoryInfo(Directory.GetCurrentDirectory()))

    let tortureRoot () = Path.Combine(repoRoot (), "torture")

    let scenarioModel scenario = Path.Combine(tortureRoot (), "corpus", "models", ScenarioName.text scenario + ".yaml")

    let defaultArtifactRoot () = Path.Combine(tortureRoot (), "artifacts", "runs")

    let uniqueRunId () =
        sprintf
            "%s-%d"
            (DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture))
            Environment.ProcessId
