module Fsdb.Tests.FullTextExecutorTests

open Expecto
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor

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

              match run store "CREATE FULLTEXT INDEX ft_title ON articles (title)" with
              | Affected 0UL -> ()
              | other -> failtestf "expected OK, got %A" other

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

                  let reloaded = Fsdb.Persistence.load dir

                  match run reloaded "SELECT id FROM docs WHERE MATCH (body) AGAINST ('database')" with
                  | ResultSet(_, [ [ Some "1" ] ]) -> ()
                  | other -> failtestf "expected the fulltext index to survive reload, got %A" other) ]
