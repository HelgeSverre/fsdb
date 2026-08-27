module Fsdb.Tests.ParserCommentFuzzTests

open Expecto
open TestSupport.SqlCommentMutation

type private Sample = { Name: string; Sql: string }

let private samples =
    [| { Name = "recursive window and quantified subquery"
         Sql =
           "WITH RECURSIVE c(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM c WHERE n < 4) SELECT n, SUM(n) OVER (ORDER BY n ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS running FROM c WHERE n = ANY (SELECT n FROM c) ORDER BY n" }
       { Name = "row comparison join"
         Sql =
           "SELECT ROW(a.id, b.n) <=> ROW(b.id, a.n) AS same_pair FROM alpha AS a INNER JOIN beta AS b ON a.id = b.id AND a.n < b.n WHERE (a.id, a.n) IN (SELECT id, n FROM gamma)" }
       { Name = "insert duplicate update"
         Sql =
           "INSERT INTO target (id, n, label) VALUES (1, 2, 'x'), (2, 3, 'y') ON DUPLICATE KEY UPDATE n = VALUES(n), label = CONCAT(VALUES(label), '-updated')" }
       { Name = "replace select"
         Sql = "REPLACE INTO target (id, n, label) SELECT id, n, label FROM source WHERE n > 0 ORDER BY id LIMIT 10" }
       { Name = "table constraints"
         Sql =
           "CREATE TABLE child (id BIGINT PRIMARY KEY, parent_id BIGINT NOT NULL, label VARCHAR(40) COMMENT 'kept text', amount DECIMAL(12, 2) DEFAULT 0.00, INDEX ix_parent_label (parent_id, label), CONSTRAINT fk_parent FOREIGN KEY (parent_id) REFERENCES parent (id)) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci" }
       { Name = "ordered compound trigger"
         Sql =
           "CREATE TRIGGER audit_child BEFORE INSERT ON child FOR EACH ROW FOLLOWS normalize_child BEGIN INSERT INTO audit_log VALUES (NEW.id, NEW.label); SET NEW.label = UPPER(NEW.label); END" }
       { Name = "checked CTE view"
         Sql =
           "CREATE VIEW positive_rows AS WITH selected AS (SELECT id, n FROM target WHERE n > 0) SELECT id, n FROM selected WITH CHECK OPTION" }
       { Name = "joined update"
         Sql =
           "UPDATE target AS t INNER JOIN source AS s ON t.id = s.id SET t.n = s.n, t.label = CONCAT(s.label, '-copied') WHERE s.n > 0" }
       { Name = "joined delete"
         Sql = "DELETE t FROM target AS t INNER JOIN source AS s ON t.id = s.id WHERE s.n < 0" }
       { Name = "geometry regexp and time"
         Sql =
           "SELECT ST_Contains(ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))'), ST_PointFromText('POINT(2 2)')) AS inside_shape, REGEXP_REPLACE('a😀b', '(?=(.))', '$1', 1, 0, 'n') AS replaced, MAKETIME(34, 20, 30.123456) AS elapsed" }
       { Name = "JSON table"
         Sql =
           "SELECT d.id, jt.ord, jt.n FROM documents AS d INNER JOIN JSON_TABLE(d.payload, '$[*]' COLUMNS (ord FOR ORDINALITY, n INT PATH '$.n' NULL ON EMPTY)) AS jt ON jt.n > 0 WHERE d.payload IS NOT NULL ORDER BY d.id, jt.ord" }
       { Name = "case and aggregates"
         Sql =
           "SELECT tenant_id, COUNT(*) AS total, SUM(CASE WHEN state = 'open' THEN amount ELSE 0 END) AS open_amount FROM invoices WHERE created_at >= TIMESTAMP '2026-01-01 00:00:00' GROUP BY tenant_id HAVING COUNT(*) > 1 ORDER BY open_amount DESC" }
       { Name = "pre-commented CTE join"
         Sql =
           "WITH/* existing block */ c AS (SELECT 1 AS n UNION ALL SELECT 2) # existing line\nSELECT c.n FROM c -- join follows\nJOIN (SELECT 1 AS n) AS d ON d.n = c.n ORDER BY c.n" } |]

let private commentForms =
    [| "block", "/* fuzz */"
       "multiline", "/* fuzz\nline */"
       "hash", "# fuzz\n"
       "dash", "-- fuzz\n"
       "executable", "/*! */"
       "five-digit version", "/*!80400 */"
       "six-digit version", "/*!080400 */"
       "future version", "/*!99999 ignored_tokens */"
       "optimizer hint", "/*+ NO_RANGE_OPTIMIZATION(t) */" |]

let private punctuationTemplate =
    "WITH c AS (§SELECT 1 AS id UNION ALL SELECT 2) "
    + "SELECT COUNT(§*), COALESCE(§MAX(§c§.§id§),§0), ROW(§c.id§,§c.id + 1§) "
    + "FROM (§SELECT id FROM c§) AS d JOIN c ON d§.§id§=§c.id "
    + "WHERE (§d.id§+§1§)§>§1 AND EXISTS(§SELECT 1 FROM c AS nested WHERE nested.id = d.id§)"

let private punctuationParts = punctuationTemplate.Split '§'

let private injectPunctuation ordinal comment =
    punctuationParts
    |> Array.mapi (fun index part ->
        if index = ordinal + 1 then
            comment + part
        else
            part)
    |> String.concat ""

let private expectParse sampleName formName sql =
    match Fsdb.Parser.parse sql with
    | Ok _ -> ()
    | Error error -> failtestf "%s with %s failed:\n%s\n%s" sampleName formName sql error

let tests =
    testList
        "parser comment fuzz"
        [ testCase "comments parse at every existing token boundary"
          <| fun _ ->
              let mutable cases = 0

              for sample in samples do
                  expectParse sample.Name "baseline" sample.Sql

                  for start, length in whitespaceRuns sample.Sql do
                      for formName, comment in commentForms do
                          expectParse sample.Name formName (injectAt sample.Sql (start, length) comment)
                          cases <- cases + 1

              Expect.isGreaterThan cases 1500 "the theory corpus covers many independent boundaries"

          testCase "dense comments parse across complex statements"
          <| fun _ ->
              for sample in samples do
                  for formName, comment in commentForms do
                      expectParse sample.Name formName (injectEverywhere sample.Sql comment)

          testCase "comments parse beside punctuation and qualified identifiers"
          <| fun _ ->
              let forms =
                  [| "block", "/* boundary */"
                     "multiline", "/* boundary\nline */"
                     "hash", "# boundary\n"
                     "dash", "-- boundary\n" |]

              let boundaryCount = punctuationParts.Length - 1

              for boundary in 0 .. boundaryCount - 1 do
                  for formName, comment in forms do
                      expectParse "punctuation boundaries" formName (injectPunctuation boundary comment)

          testCase "executable comment versions follow MySQL boundaries"
          <| fun _ ->
              for sql in
                  [ "SELECT /*!80400 SQL_NO_CACHE */ 1"
                    "SELECT /*!080400 SQL_NO_CACHE */ 1"
                    "SELECT /*!99999 invalid tokens */ 1"
                    "SELECT /*!1234 invalid tokens */ 1" ] do
                  expectParse "executable comment version" "version boundary" sql ]
