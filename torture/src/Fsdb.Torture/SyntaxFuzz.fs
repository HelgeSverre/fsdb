namespace Fsdb.Torture

open System
open System.IO
open System.Text
open System.Threading.Tasks

type SyntaxCandidate =
    { Feature: string
      Mutation: string
      Sql: string
      CleanupSql: string option
      Baseline: bool }

[<RequireQualifiedAccess>]
module SyntaxFuzz =
    type private ScanMode =
        | Code
        | Quoted of char
        | BlockComment
        | LineComment

    let private seeds suffix =
        [| "row_constructor", "SELECT ROW(1, 2) = ROW(1, 2)"
           "row_subquery", "SELECT ROW(1, 'A') = (SELECT 1, _utf8mb4'A' COLLATE utf8mb4_bin)"
           "quantified_comparison", "SELECT 1 = ANY (SELECT n FROM syntax_target)"
           "geometry_topology", "SELECT ST_AsText(ST_ConvexHull(ST_GeomFromText('MULTIPOINT((0 0),(2 0),(0 2))')))"
           "geometry_relation", "SELECT ST_Contains(ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))'), ST_PointFromText('POINT(2 2)'))"
           "regexp_collation", "SELECT REGEXP_LIKE(_utf8mb4'Ångström' COLLATE utf8mb4_0900_as_ci, '^ångström$')"
           "regexp_replace", "SELECT REGEXP_REPLACE('a😀b', '(?=(.))', '$1', 1, 0, 'n')"
           "fulltext_match",
           "SELECT id, MATCH(title, body) AGAINST ('+database +security' IN BOOLEAN MODE) AS relevance FROM syntax_fulltext WHERE MATCH(body, title) AGAINST ('database') ORDER BY relevance DESC, id"
           "fulltext_conjuncts",
           "SELECT id FROM syntax_fulltext WHERE MATCH(title, body) AGAINST ('database') AND MATCH(body, title) AGAINST ('+security' IN BOOLEAN MODE) AND id > 0 ORDER BY id"
           "fulltext_alternatives",
           "SELECT id FROM syntax_fulltext WHERE (MATCH(title, body) AGAINST ('database') OR MATCH(body, title) AGAINST ('+security' IN BOOLEAN MODE)) AND id > 0 ORDER BY id"
           "fulltext_join",
           "SELECT f.id, MATCH(n.body) AGAINST ('security') AS relevance FROM syntax_fulltext AS f JOIN syntax_fulltext_notes AS n ON n.article_id = f.id AND MATCH(n.body) AGAINST ('notes') WHERE MATCH(f.title, f.body) AGAINST ('database') ORDER BY f.id"
           "collation_symmetric", "SELECT ci = bin, bin = ci, ci < bin, bin > ci, ci LIKE bin FROM syntax_collation"
           "collation_row", "SELECT (ci, 1) = (bin, 1), (bin, 1) IN ((ci, 1), ('z', 2)) FROM syntax_collation"
           "collation_quantified",
           "SELECT ci = ANY (SELECT bin FROM syntax_collation), bin = ANY (SELECT ci FROM syntax_collation) FROM syntax_collation"
           "collation_cte",
           "WITH c AS (SELECT bin COLLATE utf8mb4_0900_ai_ci AS value FROM syntax_collation) SELECT 'A' = ANY (SELECT value FROM c)"
           "collation_case_between", "SELECT CASE ci WHEN bin THEN 1 ELSE 0 END, ci BETWEEN bin AND 'z' FROM syntax_collation"
           "collation_join",
           "SELECT COUNT(*) FROM syntax_collation AS left_side JOIN syntax_collation AS right_side ON left_side.ci = right_side.bin"
           "typed_time", "SELECT MAKETIME(34, 20, 30.123456), SEC_TO_TIME(3661.25), TIME('-34:20:30.123456')"
           "weight_string", "SELECT HEX(WEIGHT_STRING(_utf8mb4'a' COLLATE utf8mb4_bin AS CHAR(3)))"
           "quoted_user_variable", "SET @`syntax.name` := (@'second' := 2) + 1"
           "recursive_cte", "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c WHERE n < 3) SELECT SUM(n) FROM c"
           "nested_cte", "WITH outer_c AS (WITH inner_c AS (SELECT 42 AS n) SELECT n FROM inner_c) SELECT n FROM outer_c"
           "cte_subqueries",
           "SELECT (WITH scalar_c AS (SELECT 2 AS n) SELECT n FROM scalar_c), EXISTS (WITH exists_c AS (SELECT 1 AS n) SELECT n FROM exists_c), 2 IN (WITH in_c AS (SELECT 1 AS n UNION ALL SELECT 2) SELECT n FROM in_c)"
           "cte_derived", "SELECT d.n FROM (WITH c AS (SELECT 1 AS n) SELECT n FROM c UNION ALL SELECT 2) AS d ORDER BY d.n"
           "cte_update", "WITH c AS (SELECT id FROM syntax_target WHERE id < 0) UPDATE syntax_target SET n = n + 1 WHERE id IN (SELECT id FROM c)"
           "cte_delete", "WITH c AS (SELECT id FROM syntax_target WHERE id < 0) DELETE FROM syntax_target WHERE id IN (SELECT id FROM c)"
           "cte_union_branch", "SELECT 1 AS n UNION ALL (WITH c AS (SELECT 2 AS n) SELECT n FROM c)"
           "set_scalar_subquery", "SELECT (SELECT 1 UNION SELECT 1)"
           "set_exists_subquery", "SELECT EXISTS (SELECT 1 UNION ALL SELECT 2)"
           "set_in_subquery", "SELECT 2 IN (SELECT 1 UNION ALL SELECT 2)"
           "planned_join",
           "SELECT t.id FROM syntax_target AS t JOIN syntax_source AS s ON s.id = t.id JOIN syntax_collation AS c ON c.id = t.id WHERE t.id >= 1 ORDER BY t.id"
           "straight_join",
           "SELECT STRAIGHT_JOIN t.id FROM syntax_target AS t JOIN syntax_source AS s ON s.id = t.id JOIN syntax_collation AS c ON c.id = t.id WHERE t.id >= 1"
           "correlated_index", "SELECT t.id, (SELECT COUNT(*) FROM syntax_source AS s WHERE s.n = t.n) FROM syntax_target AS t"
           "range_update", "UPDATE syntax_target SET n = n WHERE id >= 999"
           "range_delete", "DELETE FROM syntax_target WHERE id >= 999"
           "composite_index", sprintf "CREATE INDEX ix_syntax_%s ON syntax_target (n, label)" suffix
           "descending_invisible_index", sprintf "CREATE INDEX ix_direction_%s ON syntax_target (n DESC, label ASC) INVISIBLE" suffix
           "alter_index_visibility", sprintf "ALTER TABLE syntax_target ALTER INDEX ix_direction_%s VISIBLE" suffix
           "view_check_option", sprintf "CREATE VIEW syntax_view_%s AS SELECT id, n FROM syntax_target WHERE n > 0 WITH CHECK OPTION" suffix
           "nested_join_view_update", "UPDATE syntax_nested_join SET n = n WHERE id = 1"
           "outer_join_view_update", "UPDATE syntax_outer_join SET note = note WHERE doubled = 20"
           "nested_join_view_insert", "INSERT INTO syntax_nested_join (id, n) VALUES (999999, 1) ON DUPLICATE KEY UPDATE n = VALUES(n)"
           "materialized_join_view_update", "UPDATE syntax_materialized_join SET n = n WHERE id = 1 AND total = 11"
           "union_join_view_update", "UPDATE syntax_union_join SET n = n WHERE id = 1 AND marker = 11"
           "ordered_compound_trigger",
           sprintf
               "CREATE TRIGGER syntax_after_%s BEFORE INSERT ON syntax_trigger_target FOR EACH ROW FOLLOWS syntax_first BEGIN INSERT INTO syntax_log VALUES (NEW.n); SET NEW.n = NEW.n + 1; END"
               suffix
           "odku", "INSERT INTO syntax_target VALUES (1, 11, 'changed') ON DUPLICATE KEY UPDATE n = VALUES(n), label = VALUES(label)"
           "odku_select_source", "INSERT INTO syntax_target (id, n, label) SELECT id, n, label FROM syntax_source AS s ON DUPLICATE KEY UPDATE n = VALUES(n), label = s.update_label"
           "cte_insert_source",
           "INSERT INTO syntax_target (id, n, label) WITH c AS (SELECT id, n, label FROM syntax_source) SELECT id, n, label FROM c ON DUPLICATE KEY UPDATE n = VALUES(n), label = VALUES(label)"
           "replace_select", "REPLACE INTO syntax_target SELECT 2, 20, 'replacement'"
           "serializable", "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE"
           "temporal_range_frame",
           "SELECT dt, SUM(n) OVER (ORDER BY dt RANGE BETWEEN INTERVAL 1 DAY PRECEDING AND CURRENT ROW) FROM syntax_temporal ORDER BY dt"
           "derived_table_update",
           "UPDATE syntax_target AS t JOIN (SELECT id FROM syntax_source) AS s ON s.id = t.id SET t.n = t.n"
           "functional_default", sprintf "CREATE TABLE syntax_default_%s (id INT DEFAULT (ABS(-2)))" suffix
           "partitioned_table", sprintf "CREATE TABLE syntax_partition_%s (id INT) PARTITION BY HASH(id) PARTITIONS 2" suffix
           "table_comment", "ALTER TABLE syntax_target COMMENT = 'syntax corpus'"
           "explain_json", "EXPLAIN FORMAT=JSON SELECT id FROM syntax_target WHERE id = 1"
           "explain_analyze", "EXPLAIN ANALYZE SELECT id FROM syntax_target WHERE id = 1"
           "checksum_table", "CHECKSUM TABLE syntax_target"
           "locking_nowait", "SELECT id FROM syntax_target WHERE id = 1 FOR UPDATE NOWAIT"
           "locking_skip", "SELECT id FROM syntax_target ORDER BY id FOR UPDATE SKIP LOCKED"
           "locking_share_of",
           "SELECT t.id,s.id FROM syntax_target t JOIN syntax_source s ON s.id=t.id FOR UPDATE OF t FOR SHARE OF s NOWAIT"
           "select_into_variable", "SELECT COUNT(*) INTO @syntax_count FROM syntax_target"
           "text_prepared_statement", sprintf "PREPARE syntax_stmt_%s FROM 'SELECT 1'" suffix
           "table_lock", "LOCK TABLES syntax_target READ"
           "commented_table_lock", "LOCK/**/TABLES syntax_target AS/*alias*/locked READ/**/LOCAL"
           "handler_open", "HANDLER syntax_target OPEN AS syntax_handler"
           "stored_procedure", sprintf "CREATE PROCEDURE syntax_proc_%s() SELECT 1" suffix
           "procedure_parameter", sprintf "CREATE PROCEDURE syntax_proc_param_%s(IN value INT) SELECT value" suffix
           "procedure_compound",
           sprintf
               "CREATE PROCEDURE syntax_proc_body_%s(IN value INT, OUT doubled INT) BEGIN DECLARE local_value INT DEFAULT value + 1; SET doubled = local_value * 2; SELECT local_value, doubled; END"
               suffix
           "procedure_call", "CALL syntax_callable(3, @syntax_output)"
           "scheduled_event",
           sprintf "CREATE EVENT syntax_event_%s ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 DAY DO INSERT INTO syntax_log VALUES (999)" suffix
           "recurring_event",
           sprintf "CREATE EVENT syntax_recurring_%s ON SCHEDULE EVERY 1 DAY DO INSERT INTO syntax_log VALUES (998)" suffix
           "role_account", sprintf "CREATE ROLE 'syntax_role_%s'@'%%'" suffix
           "role_activation", "SET ROLE NONE"
           "locked_user", sprintf "CREATE USER 'syntax_user_%s'@'%%' ACCOUNT LOCK" suffix
           "account_requirements",
           sprintf
               "CREATE USER 'syntax_secure_%s'@'%%' REQUIRE SSL WITH MAX_QUERIES_PER_HOUR 60 PASSWORD EXPIRE INTERVAL 180 DAY"
               suffix
           "read_uncommitted", "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED"
           "partition_selection", "SELECT id FROM syntax_partitioned PARTITION (p0) ORDER BY id"
           "partition_growth", "ALTER TABLE syntax_partitioned ADD PARTITION PARTITIONS 1"
           "spatial_buffer", "SELECT ST_AsText(ST_Buffer(ST_PointFromText('POINT(0 0)'), 1))"
           "column_comment", sprintf "CREATE TABLE syntax_comment_%s (id INT COMMENT 'syntax corpus')" suffix
           "numeric_display", sprintf "CREATE TABLE syntax_display_%s (i INT(7) ZEROFILL, d DECIMAL(7,2) ZEROFILL, f FLOAT(8,2) ZEROFILL)" suffix
           "bit_type", sprintf "CREATE TABLE syntax_bit_%s (b BIT(64) DEFAULT b'1')" suffix |]

    let private fixtures =
        [| "CREATE TABLE syntax_target (id INT PRIMARY KEY, n INT, label VARCHAR(40), INDEX ix_n_label (n, label))"
           "INSERT INTO syntax_target VALUES (1, 10, 'seed')"
           "CREATE TABLE syntax_source (id INT, n INT, label VARCHAR(40), update_label VARCHAR(40), INDEX ix_syntax_source_n (n))"
           "INSERT INTO syntax_source VALUES (1, 11, 'candidate', 'source')"
           "CREATE TABLE syntax_collation (id INT PRIMARY KEY, ci VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci, bin VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin, cs VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs, latin VARCHAR(20) CHARACTER SET latin1 COLLATE latin1_swedish_ci)"
           "INSERT INTO syntax_collation VALUES (1, 'A', 'a', 'A', 'a')"
           "CREATE TABLE syntax_trigger_target (id INT PRIMARY KEY, n INT)"
           "CREATE TABLE syntax_log (n INT)"
           "CREATE TABLE syntax_temporal (dt DATETIME, n INT)"
           "INSERT INTO syntax_temporal VALUES ('2026-01-01', 1), ('2026-01-02', 2)"
           "CREATE TABLE syntax_fulltext (id INT PRIMARY KEY, title VARCHAR(100), body TEXT, FULLTEXT(title, body))"
           "INSERT INTO syntax_fulltext VALUES (1, 'Database tutorial', 'Database security guide'), (2, 'Other notes', 'Unrelated material')"
           "CREATE TABLE syntax_fulltext_notes (article_id INT, body TEXT, FULLTEXT(body))"
           "INSERT INTO syntax_fulltext_notes VALUES (1, 'Security notes'), (2, 'Other notes')"
           "CREATE TABLE syntax_view_left (id INT PRIMARY KEY, n INT NOT NULL)"
           "CREATE TABLE syntax_view_right (id INT PRIMARY KEY, note VARCHAR(20) NOT NULL)"
           "INSERT INTO syntax_view_left VALUES (1, 10)"
           "INSERT INTO syntax_view_right VALUES (1, 'one')"
           "CREATE VIEW syntax_left_view AS SELECT id, n FROM syntax_view_left WHERE n > 0 WITH CASCADED CHECK OPTION"
           "CREATE VIEW syntax_right_view AS SELECT id, note FROM syntax_view_right"
           "CREATE VIEW syntax_nested_join AS SELECT l.id, l.n, r.note FROM syntax_left_view AS l JOIN syntax_right_view AS r ON r.id = l.id"
           "CREATE VIEW syntax_outer_join AS SELECT id, n, note, n * 2 AS doubled FROM syntax_nested_join WHERE n < 50"
           "CREATE VIEW syntax_materialized_totals AS SELECT id, SUM(n) AS total FROM syntax_source GROUP BY id"
           "CREATE VIEW syntax_materialized_join AS SELECT t.id, t.n, x.total FROM syntax_target AS t JOIN syntax_materialized_totals AS x ON x.id = t.id"
           "CREATE VIEW syntax_union_component AS SELECT id, n AS marker FROM syntax_source WHERE n > 0 UNION ALL SELECT id, n AS marker FROM syntax_source WHERE n < 0"
           "CREATE VIEW syntax_union_join AS SELECT t.id, t.n, x.marker FROM syntax_target AS t JOIN syntax_union_component AS x ON x.id = t.id"
           "CREATE TABLE syntax_partitioned (id INT) PARTITION BY HASH(id) PARTITIONS 2"
           "INSERT INTO syntax_partitioned VALUES (1), (2), (3)"
           "CREATE PROCEDURE syntax_callable(IN value INT, OUT doubled INT) BEGIN DECLARE local_value INT DEFAULT value + 1; SET doubled = local_value * 2; SELECT local_value, doubled; END"
           "CREATE TRIGGER syntax_first BEFORE INSERT ON syntax_trigger_target FOR EACH ROW SET NEW.n = NEW.n + 1" |]

    let private cleanupStatement feature suffix =
        match feature with
        | "composite_index" -> Some(sprintf "DROP INDEX ix_syntax_%s ON syntax_target" suffix)
        | "view_check_option" -> Some(sprintf "DROP VIEW syntax_view_%s" suffix)
        | "ordered_compound_trigger" -> Some(sprintf "DROP TRIGGER syntax_after_%s" suffix)
        | "column_comment" -> Some(sprintf "DROP TABLE syntax_comment_%s" suffix)
        | "numeric_display" -> Some(sprintf "DROP TABLE syntax_display_%s" suffix)
        | "bit_type" -> Some(sprintf "DROP TABLE syntax_bit_%s" suffix)
        | "functional_default" -> Some(sprintf "DROP TABLE syntax_default_%s" suffix)
        | "partitioned_table" -> Some(sprintf "DROP TABLE syntax_partition_%s" suffix)
        | "text_prepared_statement" -> Some(sprintf "DEALLOCATE PREPARE syntax_stmt_%s" suffix)
        | "table_lock" -> Some "UNLOCK TABLES"
        | "commented_table_lock" -> Some "UNLOCK TABLE"
        | "handler_open" -> Some "HANDLER syntax_handler CLOSE"
        | "stored_procedure" -> Some(sprintf "DROP PROCEDURE syntax_proc_%s" suffix)
        | "procedure_parameter" -> Some(sprintf "DROP PROCEDURE syntax_proc_param_%s" suffix)
        | "procedure_compound" -> Some(sprintf "DROP PROCEDURE syntax_proc_body_%s" suffix)
        | "scheduled_event" -> Some(sprintf "DROP EVENT syntax_event_%s" suffix)
        | "recurring_event" -> Some(sprintf "DROP EVENT syntax_recurring_%s" suffix)
        | "role_account" -> Some(sprintf "DROP ROLE IF EXISTS 'syntax_role_%s'@'%%', 'syntax_role_%s'@''" suffix suffix)
        | "locked_user" -> Some(sprintf "DROP USER IF EXISTS 'syntax_user_%s'@'%%', 'syntax_user_%s'@''" suffix suffix)
        | "account_requirements" -> Some(sprintf "DROP USER IF EXISTS 'syntax_secure_%s'@'%%', 'syntax_secure_%s'@''" suffix suffix)
        | _ -> None

    let private cleanupStatements candidate =
        match candidate.Feature, candidate.CleanupSql with
        | "handler_open", Some cleanup -> [ cleanup; "HANDLER syntax_target CLOSE" ]
        | _, Some cleanup -> [ cleanup ]
        | _, None -> []

    let private cleanupSucceeded feature outcome =
        TargetOutcome.succeeded outcome || feature = "handler_open" && outcome.ErrorCode = 1109

    let private replaceAt index length replacement (value: string) =
        value.Substring(0, index) + replacement + value.Substring(index + length)

    let private firstIndexOf (value: string) (text: string) =
        let index = text.IndexOf(value, StringComparison.Ordinal)
        if index < 0 then None else Some index

    let private codeSpans (sql: string) =
        let spans = ResizeArray<int * int>()
        let mutable mode = Code
        let mutable index = 0
        let mutable codeStart = 0

        let endCode () =
            if index > codeStart then
                spans.Add(codeStart, index - codeStart)

        while index < sql.Length do
            match mode with
            | Quoted current when current <> '`' && sql.[index] = '\\' && index + 1 < sql.Length -> index <- index + 2
            | Quoted current when sql.[index] = current && index + 1 < sql.Length && sql.[index + 1] = current -> index <- index + 2
            | Quoted current when sql.[index] = current ->
                mode <- Code
                index <- index + 1
                codeStart <- index
            | Quoted _ -> index <- index + 1
            | BlockComment when index + 1 < sql.Length && sql.[index] = '*' && sql.[index + 1] = '/' ->
                mode <- Code
                index <- index + 2
                codeStart <- index
            | BlockComment -> index <- index + 1
            | LineComment when sql.[index] = '\n' || sql.[index] = '\r' ->
                mode <- Code
                index <- index + 1
                codeStart <- index
            | LineComment -> index <- index + 1
            | Code when sql.[index] = '\'' || sql.[index] = '"' || sql.[index] = '`' ->
                endCode ()
                mode <- Quoted sql.[index]
                index <- index + 1
            | Code when index + 1 < sql.Length && sql.[index] = '/' && sql.[index + 1] = '*' ->
                endCode ()
                mode <- BlockComment
                index <- index + 2
            | Code when sql.[index] = '#' ->
                endCode ()
                mode <- LineComment
                index <- index + 1
            | Code when
                index + 1 < sql.Length
                && sql.[index] = '-'
                && sql.[index + 1] = '-'
                && (index + 2 = sql.Length || Char.IsWhiteSpace sql.[index + 2])
                ->
                endCode ()
                mode <- LineComment
                index <- index + 2
            | Code -> index <- index + 1

        if mode = Code then
            endCode ()

        spans.ToArray()

    let private whitespaceRuns (sql: string) =
        codeSpans sql
        |> Array.collect (fun (start, length) ->
            let runs = ResizeArray<int * int>()
            let finish = start + length
            let mutable index = start

            while index < finish do
                if Char.IsWhiteSpace sql.[index] then
                    let runStart = index

                    while index < finish && Char.IsWhiteSpace sql.[index] do
                        index <- index + 1

                    runs.Add(runStart, index - runStart)
                else
                    index <- index + 1

            runs.ToArray())

    let private replaceWhitespace comment (sql: string) =
        whitespaceRuns sql
        |> Array.rev
        |> Array.fold (fun current (start, length) -> replaceAt start length comment current) sql

    let private punctuationPositions (sql: string) =
        codeSpans sql
        |> Array.collect (fun (start, length) ->
            [| for index in start .. start + length - 1 do
                   if sql.[index] = '(' || sql.[index] = ')' || sql.[index] = ',' then
                       yield index, sql.[index] |])

    let private surroundPunctuation comment (sql: string) =
        punctuationPositions sql
        |> Array.rev
        |> Array.fold (fun current (index, punctuation) ->
            let replacement =
                match punctuation with
                | '(' -> comment + "("
                | ')' -> ")" + comment
                | ',' -> comment + "," + comment
                | _ -> string punctuation

            replaceAt index 1 replacement current) sql

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
           "append_identifier", fun sql -> Some(sql + " unexpected_token")
           "unterminated_quote", fun sql -> Some(sql + " '")
           "unterminated_comment", fun sql -> Some(sql + " /*")
           "surround_whitespace", fun sql -> Some("\n\t" + sql + " \n")
           "lowercase", fun (sql: string) -> Some(sql.ToLowerInvariant())
           "inline_select_comment", fun sql -> firstIndexOf "SELECT" sql |> Option.map (fun index -> replaceAt index 6 "SELECT/**/" sql)
           "dense_block_comments", replaceWhitespace "/* fuzz */" >> Some
           "dense_hash_comments", replaceWhitespace "# fuzz\n" >> Some
           "dense_dash_comments", replaceWhitespace "-- fuzz\n" >> Some
           "dense_version_comments", replaceWhitespace "/*!080400 */" >> Some
           "dense_future_comments", replaceWhitespace "/*!99999 ignored_tokens */" >> Some
           "punctuation_block_comments", surroundPunctuation "/**/" >> Some
           "punctuation_version_comments", surroundPunctuation "/*!080400 */" >> Some
           "punctuation_future_comments", surroundPunctuation "/*!99999 ignored_tokens */" >> Some |]

    let private mutationTrees depth sql =
        let rec expand remaining (path: string list) current =
            seq {
                if not (List.isEmpty path) then
                    yield List.rev path, current

                if remaining > 0 then
                    for name, mutate in mutationOperators do
                        match mutate current with
                        | Some mutated when mutated <> current -> yield! expand (remaining - 1) (name :: path) mutated
                        | _ -> ()
            }

        expand depth [] sql

    let candidates seed depth count =
        let depth = depth |> max 1 |> min 3
        let count = count |> max 0 |> min 10000
        let seedStatements = seeds "baseline"

        let baselines =
            seedStatements
            |> Array.map (fun (feature, sql) ->
                { Feature = feature
                  Mutation = "baseline"
                  Sql = sql
                  CleanupSql = cleanupStatement feature "baseline"
                  Baseline = true })

        let mutations =
            seedStatements
            |> Array.collect (fun (feature, sql) ->
                mutationTrees depth sql
                |> Seq.distinctBy snd
                |> Seq.map (fun (path, mutated) ->
                    let mutation = String.concat "+" path
                    let suffix = Hashing.text (sprintf "%d\n%s\n%s\n%s" seed feature mutation mutated) |> fun hash -> hash.Substring(0, 12)

                    { Feature = feature
                      Mutation = mutation
                      Sql = mutated.Replace("baseline", "m" + suffix)
                      CleanupSql = cleanupStatement feature ("m" + suffix)
                      Baseline = false })
                |> Seq.toArray)
            |> Array.distinctBy (fun candidate -> candidate.Feature, candidate.Sql)
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

    let private allowUserVariables (connectionString: string) =
        let builder = MySqlConnector.MySqlConnectionStringBuilder connectionString
        builder.AllowUserVariables <- true
        builder.ConnectionString

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
                Fsdb.Log.silence ()
                use subject = new FsdbSubject()
                use! mysql = Database.openConnection (allowUserVariables oracleConnectionString)
                use! fsdb = Database.openConnection (Runner.fsdbConnectionString subject.Port |> allowUserVariables)
                let! mysqlVersion = Database.scalarString mysql options.TimeoutSeconds "SELECT VERSION()"

                match! executeFixtures options.TimeoutSeconds mysql fsdb with
                | Some(mysqlOutcome, fsdbOutcome, sql) ->
                    return Error(sprintf "syntax fixture failed for %s: mysql=%s fsdb=%s" sql mysqlOutcome.Message fsdbOutcome.Message)
                | None ->
                    let records = ResizeArray<SyntaxCaseRecord>()

                    for index, candidate in candidates options.Seed options.Depth options.Cases |> Array.indexed do
                        let parserStatus, astKind = parserOutcome candidate.Sql
                        let! mysqlOutcome = Database.execute "mysql" mysql options.TimeoutSeconds candidate.Sql
                        let! fsdbOutcome = Database.execute "fsdb" fsdb options.TimeoutSeconds candidate.Sql

                        if TargetOutcome.succeeded mysqlOutcome then
                            for cleanup in cleanupStatements candidate do
                                let! outcome = Database.execute "mysql" mysql options.TimeoutSeconds cleanup

                                if not (cleanupSucceeded candidate.Feature outcome) then
                                    failwithf "MySQL syntax cleanup failed for %s: %s" cleanup outcome.Message

                        if TargetOutcome.succeeded fsdbOutcome then
                            for cleanup in cleanupStatements candidate do
                                let! outcome = Database.execute "fsdb" fsdb options.TimeoutSeconds cleanup

                                if not (cleanupSucceeded candidate.Feature outcome) then
                                    failwithf "fsdb syntax cleanup failed for %s: %s" cleanup outcome.Message

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

                    let firstFailure =
                        cases
                        |> Array.tryFind (fun record ->
                            record.Classification = "infrastructure"
                            || record.Classification = "oracle_baseline_rejected")
                        |> Option.orElseWith (fun () -> cases |> Array.tryFind (fun record -> not record.Passed))

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
                        { SchemaVersion = 2
                          RunId = runId
                          CaseId = caseId
                          Seed = options.Seed
                          RequestedMutations = options.Cases
                          MutationDepth = options.Depth
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
