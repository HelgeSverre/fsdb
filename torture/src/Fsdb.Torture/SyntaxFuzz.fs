namespace Fsdb.Torture

open System
open System.IO
open System.Text
open System.Threading.Tasks

type SyntaxCandidate =
    { Feature: string
      Mutation: string
      Sql: string
      Baseline: bool }

[<RequireQualifiedAccess>]
module SyntaxFuzz =
    let private seeds suffix =
        [| "row_constructor", "SELECT ROW(1, 2) = ROW(1, 2)"
           "quantified_comparison", "SELECT 1 = ANY (SELECT n FROM syntax_target)"
           "geometry_topology", "SELECT ST_AsText(ST_ConvexHull(ST_GeomFromText('MULTIPOINT((0 0),(2 0),(0 2))')))"
           "regexp_collation", "SELECT REGEXP_LIKE(_utf8mb4'Ångström' COLLATE utf8mb4_0900_as_ci, '^ångström$')"
           "composite_index", sprintf "CREATE INDEX ix_syntax_%s ON syntax_target (n, label)" suffix
           "view_check_option", sprintf "CREATE VIEW syntax_view_%s AS SELECT id, n FROM syntax_target WHERE n > 0 WITH CHECK OPTION" suffix
           "ordered_compound_trigger",
           sprintf
               "CREATE TRIGGER syntax_after_%s BEFORE INSERT ON syntax_trigger_target FOR EACH ROW FOLLOWS syntax_first BEGIN INSERT INTO syntax_log VALUES (NEW.n); SET NEW.n = NEW.n + 1; END"
               suffix
           "odku", "INSERT INTO syntax_target VALUES (1, 11, 'changed') ON DUPLICATE KEY UPDATE n = VALUES(n), label = VALUES(label)"
           "replace_select", "REPLACE INTO syntax_target SELECT 2, 20, 'replacement'"
           "serializable", "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE"
           "column_comment", sprintf "CREATE TABLE syntax_comment_%s (id INT COMMENT 'syntax corpus')" suffix |]

    let private fixtures =
        [| "CREATE TABLE syntax_target (id INT PRIMARY KEY, n INT, label VARCHAR(40), INDEX ix_n_label (n, label))"
           "INSERT INTO syntax_target VALUES (1, 10, 'seed')"
           "CREATE TABLE syntax_trigger_target (id INT PRIMARY KEY, n INT)"
           "CREATE TABLE syntax_log (n INT)"
           "CREATE TRIGGER syntax_first BEFORE INSERT ON syntax_trigger_target FOR EACH ROW SET NEW.n = NEW.n + 1" |]

    let private replaceAt index length replacement (value: string) =
        value.Substring(0, index) + replacement + value.Substring(index + length)

    let private firstIndexOf (value: string) (text: string) =
        let index = text.IndexOf(value, StringComparison.Ordinal)
        if index < 0 then None else Some index

    let private mutationOperators =
        [| "drop_last", fun (sql: string) -> if sql.Length > 1 then Some(sql.Substring(0, sql.Length - 1)) else None
           "truncate_half", fun sql -> if sql.Length > 3 then Some(sql.Substring(0, sql.Length / 2)) else None
           "extra_close_paren", fun sql -> Some(sql + ")")
           "remove_open_paren", fun sql -> firstIndexOf "(" sql |> Option.map (fun index -> replaceAt index 1 "" sql)
           "double_comma", fun sql -> firstIndexOf "," sql |> Option.map (fun index -> replaceAt index 1 ",," sql)
           "duplicate_select", fun sql -> firstIndexOf "SELECT" sql |> Option.map (fun index -> replaceAt index 6 "SELECT SELECT" sql)
           "remove_from", fun sql -> firstIndexOf " FROM " sql |> Option.map (fun index -> replaceAt index 6 " " sql)
           "remove_equals", fun sql -> firstIndexOf "=" sql |> Option.map (fun index -> replaceAt index 1 "" sql)
           "prepend_close_paren", fun sql -> Some(")" + sql)
           "append_identifier", fun sql -> Some(sql + " unexpected_token") |]

    let candidates seed count =
        let seedStatements = seeds "baseline"

        let baselines =
            seedStatements
            |> Array.map (fun (feature, sql) ->
                { Feature = feature
                  Mutation = "baseline"
                  Sql = sql
                  Baseline = true })

        let mutations =
            seedStatements
            |> Array.indexed
            |> Array.collect (fun (seedIndex, (feature, sql)) ->
                mutationOperators
                |> Array.indexed
                |> Array.choose (fun (mutationIndex, (name, mutate)) ->
                    let isolatedSql = sql.Replace("baseline", sprintf "m%d_%d" seedIndex mutationIndex)

                    mutate isolatedSql
                    |> Option.filter ((<>) isolatedSql)
                    |> Option.map (fun mutated ->
                        { Feature = feature
                          Mutation = name
                          Sql = mutated
                          Baseline = false })))
            |> Array.distinctBy (fun candidate -> candidate.Feature, candidate.Mutation, candidate.Sql)
            |> Array.sortBy (fun candidate -> Hashing.text (sprintf "%d\n%s\n%s\n%s" seed candidate.Feature candidate.Mutation candidate.Sql))
            |> Array.truncate count

        Array.append baselines mutations

    let classify baseline (mysql: TargetOutcome) (fsdb: TargetOutcome) =
        if mysql.Status = "timeout" || mysql.Status = "driver_error" then
            "infrastructure"
        elif fsdb.Status = "timeout" then
            "fsdb_timeout"
        elif fsdb.Status = "driver_error" then
            "protocol_fault"
        elif baseline then
            if not (TargetOutcome.succeeded mysql) then "oracle_baseline_rejected"
            elif TargetOutcome.succeeded fsdb then "pass"
            else "fsdb_feature_gap"
        elif TargetOutcome.succeeded mysql then
            if TargetOutcome.succeeded fsdb then "accepted_mutation"
            else "fsdb_syntax_rejection_gap"
        elif mysql.ErrorCode <> 1064 then
            "oracle_semantic_rejection"
        elif TargetOutcome.succeeded fsdb then
            "fsdb_syntax_acceptance_gap"
        elif fsdb.ErrorCode = mysql.ErrorCode && fsdb.SqlState = mysql.SqlState then
            "matched_syntax_error"
        else
            "syntax_error_contract_mismatch"

    let private passed =
        function
        | "pass"
        | "accepted_mutation"
        | "oracle_semantic_rejection"
        | "matched_syntax_error" -> true
        | _ -> false

    let private detail classification (mysql: TargetOutcome) (fsdb: TargetOutcome) =
        match classification with
        | "pass" -> "baseline is accepted by both servers"
        | "accepted_mutation" -> "mutation remains valid on both servers"
        | "matched_syntax_error" -> sprintf "both servers returned %d/%s" mysql.ErrorCode mysql.SqlState
        | "oracle_semantic_rejection" -> sprintf "mutation reached MySQL semantic validation: %d/%s" mysql.ErrorCode mysql.SqlState
        | "fsdb_syntax_acceptance_gap" -> sprintf "MySQL returned %d/%s while fsdb accepted the statement" mysql.ErrorCode mysql.SqlState
        | "syntax_error_contract_mismatch" ->
            sprintf "syntax errors differ: mysql=%d/%s fsdb=%d/%s" mysql.ErrorCode mysql.SqlState fsdb.ErrorCode fsdb.SqlState
        | "oracle_baseline_rejected"
        | "infrastructure" -> mysql.Message
        | _ -> fsdb.Message

    let private parserOutcome sql =
        match Fsdb.Parser.parse sql with
        | Ok statement -> "ok", AstKind.ofStatement statement
        | Error error -> "error", error

    let private executeFixtures timeoutSeconds (mysql: MySqlConnector.MySqlConnection) (fsdb: MySqlConnector.MySqlConnection) =
        task {
            let mutable failure = None

            for sql in fixtures do
                if failure.IsNone then
                    let! mysqlOutcome = Database.execute "mysql" mysql timeoutSeconds sql
                    let! fsdbOutcome = Database.execute "fsdb" fsdb timeoutSeconds sql

                    if not (TargetOutcome.succeeded mysqlOutcome && TargetOutcome.succeeded fsdbOutcome) then
                        failure <- Some(mysqlOutcome, fsdbOutcome, sql)

            return failure
        }

    let run (options: SyntaxOptions) : Task<Result<SyntaxManifest * string, string>> =
        task {
            let started = DateTimeOffset.UtcNow
            let runId = Paths.uniqueRunId ()
            let caseId = sprintf "syntax-seed-%d" options.Seed
            let caseDirectory = Path.Combine(options.ArtifactRoot, runId, caseId)
            Directory.CreateDirectory caseDirectory |> ignore
            let databaseName = sprintf "fsdb_syntax_%d_%d" Environment.ProcessId options.Seed
            let! revision, dirty = Tooling.gitState ()
            let assemblyPath = typeof<Fsdb.Storage.Store>.Assembly.Location

            match! Database.createOracleDatabase options.MySqlConnection databaseName options.TimeoutSeconds with
            | Error outcome -> return Error outcome.Message
            | Ok oracleConnectionString ->
                use subject = new FsdbSubject()
                use! mysql = Database.openConnection oracleConnectionString
                use! fsdb = Database.openConnection (Runner.fsdbConnectionString subject.Port)
                let! mysqlVersion = Database.scalarString mysql options.TimeoutSeconds "SELECT VERSION()"

                match! executeFixtures options.TimeoutSeconds mysql fsdb with
                | Some(mysqlOutcome, fsdbOutcome, sql) ->
                    return Error(sprintf "syntax fixture failed for %s: mysql=%s fsdb=%s" sql mysqlOutcome.Message fsdbOutcome.Message)
                | None ->
                    let records = ResizeArray<SyntaxCaseRecord>()

                    for index, candidate in candidates options.Seed options.Cases |> Array.indexed do
                        let parserStatus, astKind = parserOutcome candidate.Sql
                        let! mysqlOutcome = Database.execute "mysql" mysql options.TimeoutSeconds candidate.Sql
                        let! fsdbOutcome = Database.execute "fsdb" fsdb options.TimeoutSeconds candidate.Sql
                        let classification = classify candidate.Baseline mysqlOutcome fsdbOutcome

                        records.Add
                            { Index = index
                              Feature = candidate.Feature
                              Mutation = candidate.Mutation
                              Sql = candidate.Sql
                              SqlSha256 = Hashing.text candidate.Sql
                              ParserStatus = parserStatus
                              AstKind = astKind
                              MySql = mysqlOutcome
                              Fsdb = fsdbOutcome
                              Classification = classification
                              Detail = detail classification mysqlOutcome fsdbOutcome
                              Passed = passed classification }

                    let cases = records.ToArray()
                    let firstFailure = cases |> Array.tryFind (fun record -> not record.Passed)
                    let classification = firstFailure |> Option.map _.Classification |> Option.defaultValue "pass"
                    let classificationDetail = firstFailure |> Option.map _.Detail |> Option.defaultValue "all syntax outcomes match"

                    let signature =
                        firstFailure
                        |> Option.map (fun record ->
                            Hashing.combine
                                [ record.Classification
                                  record.SqlSha256
                                  string record.MySql.ErrorCode
                                  string record.Fsdb.ErrorCode
                                  record.Detail ])
                        |> Option.defaultValue ""

                    let manifest =
                        { SchemaVersion = 1
                          RunId = runId
                          CaseId = caseId
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
                          ClassificationDetail = classificationDetail
                          FailureSignature = signature
                          Passed = firstFailure.IsNone }

                    let sqlCorpus = cases |> Array.map _.Sql |> String.concat ";\n\n"
                    File.WriteAllText(Path.Combine(caseDirectory, "mutations.sql"), sqlCorpus + ";\n", UTF8Encoding(false))
                    Json.write (Path.Combine(caseDirectory, "manifest.json")) manifest
                    return Ok(manifest, caseDirectory)
        }
