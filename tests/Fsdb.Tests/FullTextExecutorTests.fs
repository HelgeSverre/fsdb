module Fsdb.Tests.FullTextExecutorTests

open Expecto
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor
open Fsdb.FullText

let private run = TestSupport.Sql.executeDefault

/// The manual's `articles` corpus behind a `(title, body)` FULLTEXT index —
/// expected row sets and orderings all read off a live MySQL 8.4.11.
let private setup () : Store =
    let store = create ()

    run
        store
        "CREATE TABLE articles (id INT UNSIGNED AUTO_INCREMENT NOT NULL PRIMARY KEY, title VARCHAR(200), body TEXT, FULLTEXT (title,body))"
    |> ignore

    run
        store
        "INSERT INTO articles (title,body) VALUES
         ('MySQL Tutorial','DBMS stands for DataBase Management System ...'),
         ('How To Use MySQL Well','After you went through a ...'),
         ('Optimizing MySQL','In this tutorial, we show ...'),
         ('1001 MySQL Tricks','1. Never run mysqld as root. 2. ...'),
         ('MySQL vs. YourSQL','In the following database comparison ...'),
         ('MySQL Security','When configured properly, MySQL ...')"
    |> ignore

    store

let private ids (result: QueryResult) : string list =
    match result with
    | ResultSet(_, rows) -> rows |> List.map (fun r -> r.[0] |> Option.defaultValue "NULL")
    | other -> failtestf "expected a resultset, got %A" other

let tests =
    testList
        "fulltext executor"
        [ testCase "natural-language WHERE keeps matching rows and orders by relevance implicitly"
          <| fun _ ->
              let store = setup ()

              // Oracle: 'MySQL Security' matches all six (mysql's epsilon),
              // the security article first.
              Expect.equal
                  (ids (run store "SELECT id FROM articles WHERE MATCH (title,body) AGAINST ('MySQL Security')"))
                  [ "6"; "1"; "2"; "3"; "4"; "5" ]
                  "relevance-descending, ties in row order"

              // 'database': only the two documents containing it.
              Expect.equal
                  (ids (run store "SELECT id FROM articles WHERE MATCH (title,body) AGAINST ('database' IN NATURAL LANGUAGE MODE) ORDER BY id"))
                  [ "1"; "5" ]
                  "an explicit ORDER BY wins over the implicit one"

          testCase "the relevance score projects, dedupes with the WHERE copy, and labels like MySQL"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT id, MATCH (title,body) AGAINST ('Tutorial') AS score FROM articles WHERE id = 1" with
              | ResultSet([ _; "score" ], [ [ Some "1"; Some s ] ]) ->
                  Expect.isTrue (abs (float s - 0.22764469683170319) < 1e-6) "the oracle's TF×IDF² value"
              | other -> failtestf "expected the scored row, got %A" other

              match run store "SELECT MATCH (title,body) AGAINST ('Tutorial') FROM articles WHERE id = 2" with
              | ResultSet([ label ], [ [ Some "0" ] ]) ->
                  Expect.stringContains label "match (title,body) against" "MySQL-style header label"
              | other -> failtestf "expected the zero score, got %A" other

          testCase "SELECT * never leaks the synthetic score column"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT * FROM articles WHERE MATCH (title,body) AGAINST ('database')" with
              | ResultSet(cols, _) -> Expect.equal cols [ "id"; "title"; "body" ] "only real columns"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "boolean mode filters without implicit ordering"
          <| fun _ ->
              let store = setup ()

              Expect.equal
                  (ids (run store "SELECT id FROM articles WHERE MATCH (title,body) AGAINST ('+MySQL -YourSQL' IN BOOLEAN MODE)"))
                  [ "1"; "2"; "3"; "4"; "6" ]
                  "the YourSQL row excluded, row order preserved"

              Expect.equal
                  (ids (run store "SELECT id FROM articles WHERE MATCH (title,body) AGAINST ('\"database comparison\"' IN BOOLEAN MODE)"))
                  [ "5" ]
                  "phrase search"

          testCase "query expansion reaches documents sharing seed terms"
          <| fun _ ->
              let store = setup ()

              let rows =
                  ids (run store "SELECT id FROM articles WHERE MATCH (title,body) AGAINST ('database' WITH QUERY EXPANSION)")

              Expect.equal rows.Length 6 "expansion pulls in every article (oracle-verified)"
              Expect.equal (List.item 0 rows) "1" "direct match ranks first"
              Expect.equal (List.item 1 rows) "5" "other direct match second"

          testCase "a reversed column list matches the index; a subset is 1191"
          <| fun _ ->
              let store = setup ()

              Expect.equal
                  (ids (run store "SELECT id FROM articles WHERE MATCH (body,title) AGAINST ('database') ORDER BY id"))
                  [ "1"; "5" ]
                  "order-insensitive column list"

              match run store "SELECT id FROM articles WHERE MATCH (title) AGAINST ('database')" with
              | Err(1191, _) -> ()
              | other -> failtestf "expected 1191, got %A" other

          testCase "a non-constant AGAINST argument is 1210"
          <| fun _ ->
              let store = setup ()

              match run store "SELECT id FROM articles WHERE MATCH (title,body) AGAINST (title)" with
              | Err(1210, _) -> ()
              | other -> failtestf "expected 1210, got %A" other

          testCase "FULLTEXT matching follows the indexed column collation"
          <| fun _ ->
              let store = create ()

              [ "CREATE TABLE ft_ai (id INT, body VARCHAR(100) COLLATE utf8mb4_0900_ai_ci, FULLTEXT(body))"
                "CREATE TABLE ft_as (id INT, body VARCHAR(100) COLLATE utf8mb4_0900_as_ci, FULLTEXT(body))"
                "CREATE TABLE ft_bin (id INT, body VARCHAR(100) COLLATE utf8mb4_bin, FULLTEXT(body))"
                "INSERT INTO ft_ai VALUES (1, 'résumé'), (2, 'Resume'), (3, 'CAFÉ')"
                "INSERT INTO ft_as VALUES (1, 'résumé'), (2, 'Resume'), (3, 'CAFÉ')"
                "INSERT INTO ft_bin VALUES (1, 'résumé'), (2, 'Resume'), (3, 'CAFÉ')" ]
              |> List.iter (run store >> ignore)

              Expect.equal
                  (ids (run store "SELECT id FROM ft_ai WHERE MATCH(body) AGAINST('resume') ORDER BY id"))
                  [ "1"; "2" ]
                  "ai_ci folds accents"

              Expect.equal
                  (ids (run store "SELECT id FROM ft_ai WHERE MATCH(body) AGAINST('cafe')"))
                  [ "3" ]
                  "ai_ci folds accents and case"

              Expect.equal
                  (ids (run store "SELECT id FROM ft_as WHERE MATCH(body) AGAINST('resume')"))
                  [ "2" ]
                  "as_ci preserves accents"

              Expect.equal
                  (ids (run store "SELECT id FROM ft_as WHERE MATCH(body) AGAINST('cafe')"))
                  []
                  "as_ci rejects accent differences"

              Expect.equal
                  (ids (run store "SELECT id FROM ft_bin WHERE MATCH(body) AGAINST('Resume')"))
                  [ "2" ]
                  "binary exact match"

              Expect.equal
                  (ids (run store "SELECT id FROM ft_bin WHERE MATCH(body) AGAINST('resume')"))
                  []
                  "binary preserves case"

              match
                  run
                      store
                      "CREATE TABLE mixed_ft (a VARCHAR(100) COLLATE utf8mb4_0900_ai_ci, b VARCHAR(100) COLLATE utf8mb4_bin, FULLTEXT(a,b))"
              with
              | Err(1283, "Column 'b' cannot be part of FULLTEXT index") -> ()
              | other -> failtestf "expected mixed-collation FULLTEXT rejection, got %A" other

              run
                  store
                  "CREATE TABLE mixed_ft_alter (a VARCHAR(100) COLLATE utf8mb4_0900_ai_ci, b VARCHAR(100) COLLATE utf8mb4_0900_as_ci)"
              |> ignore

              match run store "ALTER TABLE mixed_ft_alter ADD FULLTEXT(a,b)" with
              | Err(1283, "Column 'b' cannot be part of FULLTEXT index") -> ()
              | other -> failtestf "expected mixed-collation ALTER rejection, got %A" other

          testCase "FULLTEXT over a non-text column is 1283, at CREATE and at ALTER"
          <| fun _ ->
              let store = setup ()

              match run store "CREATE TABLE bad (n INT, FULLTEXT (n))" with
              | Err(1283, msg) -> Expect.stringContains msg "cannot be part of FULLTEXT index" "MySQL's message"
              | other -> failtestf "expected 1283, got %A" other

              match run store "ALTER TABLE articles ADD FULLTEXT KEY ft_id (id)" with
              | Err(1283, _) -> ()
              | other -> failtestf "expected 1283 on ALTER, got %A" other

          testCase "CREATE FULLTEXT INDEX works and introspection reports FULLTEXT"
          <| fun _ ->
              let store = setup ()

              match run store "CREATE FULLTEXT INDEX ft_title ON articles (title) INVISIBLE" with
              | Affected 0UL -> ()
              | other -> failtestf "expected OK, got %A" other

              match run store "SELECT id FROM articles WHERE MATCH (title) AGAINST ('tutorial')" with
              | Err(1191, _) -> ()
              | other -> failtestf "expected the invisible FULLTEXT index to stay out of the plan, got %A" other

              Expect.equal
                  (run store "ALTER TABLE articles ALTER INDEX ft_title VISIBLE")
                  (Affected 0UL)
                  "the FULLTEXT index becomes visible"

              Expect.equal
                  (ids (run store "SELECT id FROM articles WHERE MATCH (title) AGAINST ('tutorial') ORDER BY id"))
                  [ "1" ]
                  "the single-column index now serves MATCH (title)"

              match run store "SELECT index_name, index_type, collation FROM information_schema.statistics WHERE table_name = 'articles' AND index_name = 'ft_title'" with
              | ResultSet(_, [ [ Some "ft_title"; Some "FULLTEXT"; None ] ]) -> ()
              | other -> failtestf "expected the FULLTEXT statistics row, got %A" other

          testCase "FULLTEXT postings follow inserts, updates, deletes, upserts, and replacements"
          <| fun _ ->
              let store = create ()

              [ "CREATE TABLE docs (id INT PRIMARY KEY, body TEXT, FULLTEXT(body))"
                "INSERT INTO docs VALUES (1, 'alpha original'), (2, 'beta original')"
                "UPDATE docs SET body = 'gamma revised' WHERE id = 1"
                "DELETE FROM docs WHERE id = 2"
                "INSERT INTO docs VALUES (1, 'delta upserted') ON DUPLICATE KEY UPDATE body = VALUES(body)"
                "REPLACE INTO docs VALUES (1, 'epsilon replaced')"
                "INSERT INTO docs VALUES (3, 'zeta inserted')" ]
              |> List.iter (run store >> ignore)

              let matching term =
                  ids (run store (sprintf "SELECT id FROM docs WHERE MATCH(body) AGAINST('%s') ORDER BY id" term))

              for removed in [ "alpha"; "beta"; "gamma"; "delta" ] do
                  Expect.isEmpty (matching removed) (sprintf "%s was removed from the postings" removed)

              Expect.equal (matching "epsilon") [ "1" ] "REPLACE publishes the replacement document"
              Expect.equal (matching "zeta") [ "3" ] "INSERT publishes the new document"

          testCase "a MATCH conjunct narrows the residual predicate to posting candidates"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE docs (id INT PRIMARY KEY, body TEXT, FULLTEXT(body))" |> ignore

              [ 1..100 ]
              |> List.map (fun id ->
                  let body =
                      match id with
                      | 37 -> "needle alpha"
                      | 82 -> "needle beta"
                      | _ -> "ordinary"

                  sprintf "(%d, '%s')" id body)
              |> String.concat ","
              |> sprintf "INSERT INTO docs VALUES %s"
              |> run store
              |> ignore

              let mutable calls = 0
              let registry =
                  builtins
                  |> registerScalar "TOUCH" (fun values ->
                      calls <- calls + 1
                      values |> List.tryHead |> Option.defaultValue VNull)

              let result =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT id FROM docs WHERE MATCH(body) AGAINST('needle') AND TOUCH(id) = id ORDER BY id"

              Expect.equal (ids result) [ "37"; "82" ] "the residual predicate retains the matching rows"
              Expect.equal calls 3 "only the metadata probe and posting candidates enter the residual pipeline"

              calls <- 0

              let intersected =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT id FROM docs WHERE MATCH(body) AGAINST('needle') AND MATCH(body) AGAINST('alpha') AND TOUCH(id) = id"

              Expect.equal (ids intersected) [ "37" ] "multiple MATCH conjuncts intersect their candidates"
              Expect.equal calls 2 "only the metadata probe and intersected candidate enter the residual pipeline"

              calls <- 0

              let unioned =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT id FROM docs WHERE (MATCH(body) AGAINST('alpha') OR MATCH(body) AGAINST('beta')) AND TOUCH(id) = id ORDER BY id"

              Expect.equal (ids unioned) [ "37"; "82" ] "bounded MATCH alternatives union their candidates"
              Expect.equal calls 3 "only the metadata probe and unioned candidates enter the residual pipeline"

              calls <- 0

              let projected =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT MATCH(body) AGAINST('needle') FROM docs WHERE id = 37 AND TOUCH(id) = id"

              match projected with
              | ResultSet(_, [ [ Some score ] ]) -> Expect.isGreaterThan (float score) 0.0 "the projected score is retained"
              | other -> failtestf "expected one projected score, got %A" other

              Expect.equal calls 2 "projection-only MATCH retains ordinary point narrowing"

          testCase "captured table roots retain their full-text snapshot"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE docs (id INT PRIMARY KEY, body TEXT, FULLTEXT(body))" |> ignore
              run store "INSERT INTO docs VALUES (1, 'alpha original')" |> ignore

              let before = tableSnapshot store defaultDatabase "docs" |> Result.toOption |> Option.get
              run store "UPDATE docs SET body = 'gamma revised' WHERE id = 1" |> ignore
              let after = tableSnapshot store defaultDatabase "docs" |> Result.toOption |> Option.get
              let index table = table.FullTextIndexes |> Map.toSeq |> Seq.head |> snd

              Expect.equal (naturalScores (index before) "alpha").Count 1 "the captured root keeps its posting"
              Expect.equal (naturalScores (index after) "alpha").Count 0 "the published root removes the old posting"
              Expect.equal (naturalScores (index after) "gamma").Count 1 "the published root adds the new posting"

          testCase "single-table UPDATE and DELETE consume full-text candidates"
          <| fun _ ->
              let store = create ()

              [ "CREATE TABLE docs (id INT PRIMARY KEY, body TEXT, FULLTEXT(body))"
                "INSERT INTO docs VALUES (1, 'needle alpha'), (2, 'needle beta'), (3, 'ordinary')" ]
              |> List.iter (run store >> ignore)

              Expect.equal
                  (run store "UPDATE docs SET body = 'archived alpha' WHERE MATCH(body) AGAINST('needle') AND id = 1")
                  (Affected 1UL)
                  "UPDATE applies its residual predicate to MATCH candidates"

              Expect.equal
                  (ids (run store "SELECT id FROM docs WHERE MATCH(body) AGAINST('needle')"))
                  [ "2" ]
                  "the updated document leaves its old posting"

              Expect.equal
                  (run store "DELETE FROM docs WHERE MATCH(body) AGAINST('needle') OR MATCH(body) AGAINST('ordinary')")
                  (Affected 2UL)
                  "DELETE unions bounded MATCH alternatives"

              Expect.equal
                  (ids (run store "SELECT id FROM docs ORDER BY id"))
                  [ "1" ]
                  "only documents selected by the full-text predicate are deleted"

              match run store "UPDATE docs SET body = 'x' WHERE MATCH(id) AGAINST('1')" with
              | Err(1191, _) -> ()
              | other -> failtestf "expected an unmatched UPDATE index to remain 1191, got %A" other

              match run store "DELETE FROM docs WHERE MATCH(body) AGAINST(body)" with
              | Err(1210, _) -> ()
              | other -> failtestf "expected an invalid DELETE query argument to remain 1210, got %A" other

          testCase "multi-table UPDATE evaluates MATCH in joins predicates and assignments"
          <| fun _ ->
              let store = create ()

              [ "CREATE TABLE articles (id INT PRIMARY KEY, body TEXT, flag INT DEFAULT 0, FULLTEXT(body))"
                "CREATE TABLE notes (article_id INT PRIMARY KEY, body TEXT, tag INT DEFAULT 0, FULLTEXT(body))"
                "INSERT INTO articles VALUES (1, 'database security', 0), (2, 'ordinary article', 0), (3, 'database tutorial', 0), (4, 'ordinary fourth', 0)"
                "INSERT INTO notes VALUES (1, 'release note', 0), (2, 'security note', 0), (3, 'ordinary note', 0), (4, 'security memo', 0)" ]
              |> List.iter (run store >> ignore)

              Expect.equal
                  (run
                      store
                      "UPDATE articles a JOIN notes n ON n.article_id = a.id SET a.flag = 10, n.tag = 20 WHERE MATCH(a.body) AGAINST('database') AND MATCH(n.body) AGAINST('note')")
                  (Affected 4UL)
                  "each matched physical row is updated once"

              Expect.equal
                  (run store "UPDATE articles a JOIN notes n ON n.article_id = a.id AND MATCH(n.body) AGAINST('security') SET a.flag = 30")
                  (Affected 2UL)
                  "MATCH filters a multi-table join condition"

              Expect.equal
                  (run store "UPDATE articles a JOIN notes n ON n.article_id = a.id SET a.flag = MATCH(n.body) AGAINST('security') > 0")
                  (Affected 4UL)
                  "MATCH scores remain available to assignment expressions"

              match run store "SELECT a.id, a.flag, n.tag FROM articles a JOIN notes n ON n.article_id = a.id ORDER BY a.id" with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "1"; Some "0"; Some "20" ]
                        [ Some "2"; Some "1"; Some "0" ]
                        [ Some "3"; Some "0"; Some "20" ]
                        [ Some "4"; Some "1"; Some "0" ] ]
                      "WHERE ON and SET use the owning full-text corpus"
              | other -> failtestf "expected updated joined rows, got %A" other

              match run store "UPDATE articles a JOIN notes n ON n.article_id = a.id SET a.flag = 1 WHERE MATCH(body) AGAINST('security')" with
              | Err(1052, _) -> ()
              | other -> failtestf "expected an ambiguous unqualified MATCH column, got %A" other

          testCase "multi-table DELETE evaluates MATCH for every target"
          <| fun _ ->
              let store = create ()

              [ "CREATE TABLE articles (id INT PRIMARY KEY, body TEXT, FULLTEXT(body))"
                "CREATE TABLE notes (article_id INT PRIMARY KEY, body TEXT, FULLTEXT(body))"
                "INSERT INTO articles VALUES (1, 'database security'), (2, 'ordinary article'), (3, 'database tutorial'), (4, 'ordinary fourth')"
                "INSERT INTO notes VALUES (1, 'release note'), (2, 'security note'), (3, 'ordinary note'), (4, 'security memo')" ]
              |> List.iter (run store >> ignore)

              Expect.equal
                  (run
                      store
                      "DELETE a, n FROM articles a JOIN notes n ON n.article_id = a.id WHERE MATCH(a.body) AGAINST('database') AND MATCH(n.body) AGAINST('note')")
                  (Affected 4UL)
                  "both physical targets use their own full-text scores"

              Expect.equal
                  (run store "DELETE a FROM articles a JOIN notes n ON n.article_id = a.id AND MATCH(n.body) AGAINST('security') WHERE a.id = 2")
                  (Affected 1UL)
                  "MATCH filters a multi-table DELETE join condition"

              Expect.equal (ids (run store "SELECT id FROM articles ORDER BY id")) [ "4" ] "only unmatched articles remain"
              Expect.equal
                  (ids (run store "SELECT article_id FROM notes ORDER BY article_id"))
                  [ "2"; "4" ]
                  "single-target deletion leaves joined rows intact"

          testCase "MATCH scores physical sources before joining"
          <| fun _ ->
              let store = create ()

              [ "CREATE TABLE articles (id INT PRIMARY KEY, body TEXT, FULLTEXT(body))"
                "CREATE TABLE notes (article_id INT, body TEXT, FULLTEXT(body))"
                "INSERT INTO articles VALUES (1, 'database security'), (2, 'ordinary article'), (3, 'database tutorial')"
                "INSERT INTO notes VALUES (1, 'release note'), (2, 'security note'), (3, 'ordinary note')" ]
              |> List.iter (run store >> ignore)

              Expect.equal
                  (ids (
                      run
                          store
                          "SELECT a.id FROM articles a JOIN notes n ON n.article_id = a.id WHERE MATCH(a.body) AGAINST('database') AND MATCH(n.body) AGAINST('note') ORDER BY a.id"
                  ))
                  [ "1"; "3" ]
                  "each MATCH reads the corpus and row identity of its owning source"

              match
                  run
                      store
                      "SELECT a.id, MATCH(n.body) AGAINST('security') AS score FROM articles a JOIN notes n ON n.article_id = a.id WHERE MATCH(n.body) AGAINST('security')"
              with
              | ResultSet([ "id"; "score" ], [ [ Some "2"; Some score ] ]) ->
                  Expect.isGreaterThan (float score) 0.0 "a joined-source score projects"
              | other -> failtestf "expected one scored join row, got %A" other

              match
                  run
                      store
                      "SELECT a.*, n.* FROM articles a JOIN notes n ON n.article_id = a.id WHERE MATCH(a.body) AGAINST('database') ORDER BY a.id"
              with
              | ResultSet(columns, rows) ->
                  Expect.equal columns [ "id"; "body"; "article_id"; "body" ] "synthetic score columns remain private"
                  Expect.equal rows.Length 2 "the joined rows remain intact"
              | other -> failtestf "expected joined rows, got %A" other

              Expect.equal
                  (ids (
                      run
                          store
                          "SELECT a.id FROM articles a JOIN notes n ON n.article_id = a.id AND MATCH(n.body) AGAINST('security') ORDER BY a.id"
                  ))
                  [ "2" ]
                  "MATCH is valid in a join condition"

              match run store "SELECT a.id FROM articles a JOIN notes n ON n.article_id = a.id WHERE MATCH(body) AGAINST('security')" with
              | Err(1052, _) -> ()
              | other -> failtestf "expected an unqualified joined MATCH column to remain ambiguous, got %A" other

          testCase "SHOW CREATE TABLE renders FULLTEXT KEY in MySQL's format"
          <| fun _ ->
              let store = setup ()
              let session = Fsdb.Session.create 999950 store

              match Fsdb.QueryHandler.handle session "SHOW CREATE TABLE articles" |> snd with
              | ResultSet(_, [ [ _; Some ddl ] ]) ->
                  Expect.stringContains ddl "FULLTEXT KEY `title` (`title`,`body`)" "the dump-compatible rendering"
              | other -> failtestf "expected the DDL row, got %A" other

          testCase "a FULLTEXT index survives the persistence round-trip"
          <| fun _ ->
              TestSupport.withDirectory "fulltext" (fun dir ->
                  let store = Fsdb.Persistence.load dir
                  Fsdb.Persistence.attach dir store

                  run store "CREATE TABLE docs (id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft_body (body))" |> ignore
                  run store "INSERT INTO docs VALUES (1, 'database tutorial'), (2, 'nothing here')" |> ignore
                  Fsdb.Persistence.snapshotNow dir store
                  run store "UPDATE docs SET body = 'revised tutorial' WHERE id = 1" |> ignore
                  run store "INSERT INTO docs VALUES (3, 'database security')" |> ignore

                  let reloaded = Fsdb.Persistence.load dir

                  match run reloaded "SELECT id FROM docs WHERE MATCH (body) AGAINST ('database')" with
                  | ResultSet(_, [ [ Some "3" ] ]) -> ()
                  | other -> failtestf "expected the rebuilt fulltext index to include the WAL tail, got %A" other) ]
