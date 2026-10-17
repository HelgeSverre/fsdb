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
        [ testCase "equality join bounds restrict fulltext row preparation"
          <| fun _ ->
              let mutable calls = 0
              let registry =
                  builtins
                  |> registerScalar "TOUCH" (fun values ->
                      calls <- calls + 1
                      values.Head)
              let store = create ()
              let execute = TestSupport.Sql.execute store registry
              execute "CREATE TABLE owners(id INT PRIMARY KEY)" |> ignore
              execute "INSERT INTO owners VALUES(42)" |> ignore
              Expect.equal
                  (execute "CREATE TABLE docs(id INT PRIMARY KEY, owner_id INT, body TEXT, observed INT AS (TOUCH(id)) VIRTUAL, KEY(owner_id), FULLTEXT(body))")
                  (Affected 0UL)
                  "generated column measures prepared candidates"
              let values =
                  [ 1..1000 ]
                  |> List.map (fun id -> sprintf "(%d,%d,'%s')" id (id % 100) (if id % 5 = 0 then "ordinary" else "needle"))
                  |> String.concat ","
              execute ("INSERT INTO docs(id,owner_id,body) VALUES " + values) |> ignore
              execute "CREATE TABLE bounds(id INT PRIMARY KEY)" |> ignore
              execute "INSERT INTO bounds VALUES(42)" |> ignore
              execute "CREATE TABLE limits(chosen_owner INT PRIMARY KEY)" |> ignore
              execute "INSERT INTO limits VALUES(42)" |> ignore
              execute "CREATE TABLE shadow(owner_id INT PRIMARY KEY)" |> ignore
              execute "INSERT INTO shadow VALUES(42)" |> ignore
              for joins, bound in
                  [ "JOIN owners o ON o.id=d.owner_id", "o.id=42"
                    "STRAIGHT_JOIN owners o ON o.id=d.owner_id", "o.id=42"
                    "JOIN owners o ON o.id=d.owner_id JOIN bounds b ON b.id=o.id", "42=b.id"
                    "JOIN owners o ON o.id=d.owner_id AND 42=o.id", "TRUE"
                    "CROSS JOIN owners o", "o.id=d.owner_id AND o.id=42"
                    "JOIN owners o ON o.id=owner_id", "o.id=42"
                    "JOIN owners o ON o.id=owner_id JOIN limits b ON chosen_owner=o.id", "chosen_owner=42"
                    "JOIN owners o ON o.id=owner_id JOIN limits b ON chosen_owner=o.id JOIN shadow s ON s.owner_id=o.id", "chosen_owner=42" ] do
                  let query redundant =
                      "SELECT d.id, ROUND(MATCH(d.body) AGAINST('needle'),6) FROM docs d "
                      + joins + " WHERE " + bound + " AND MATCH(d.body) AGAINST('needle')"
                      + redundant + " ORDER BY d.id"
                  let control = execute (query " AND d.owner_id=42")
                  calls <- 0
                  let actual = execute (query "")
                  Expect.equal (ids actual) ([ 42..100..942 ] |> List.map string) "MySQL 8.4 matching IDs"
                  Expect.equal actual control "the bound preserves scores from the complete corpus"
                  Expect.isLessThan calls 30 "only the ten matching candidates need virtual values"

              execute "CREATE TABLE cjk_docs(id INT PRIMARY KEY,owner_id INT,body TEXT,title TEXT,observed INT AS (TOUCH(id)) VIRTUAL,KEY(owner_id),FULLTEXT KEY ft(body,title) WITH PARSER ngram)" |> ignore
              [ 1..1000 ]
              |> List.map (fun id -> sprintf "(%d,%d,'%s','祝福')" id (id % 100) (if id % 5 = 0 then "日本語" else "生日快乐"))
              |> String.concat ","
              |> fun values -> execute ("INSERT INTO cjk_docs(id,owner_id,body,title) VALUES " + values) |> ignore
              let cjkScore = "ROUND(MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE),6)"
              let cjkDirect =
                  "SELECT d.id," + cjkScore + " FROM cjk_docs d WHERE d.owner_id=42"
                  + " AND MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE) ORDER BY d.id"
              let cjkJoined =
                  "SELECT d.id," + cjkScore + " FROM cjk_docs d JOIN owners o ON o.id=d.owner_id"
                  + " WHERE o.id=42 AND MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE) ORDER BY d.id"
              let cjkControl = execute cjkDirect
              calls <- 0
              let cjkActual = execute cjkJoined
              Expect.equal (ids cjkActual) ([ 42..100..942 ] |> List.map string) "MySQL 8.4 ngram join IDs"
              Expect.equal cjkActual cjkControl "ngram join bound preserves complete-corpus relevance"
              match cjkActual with
              | ResultSet(_, rows) ->
                  rows |> List.iter (fun row -> Expect.equal row.[1] (Some "0.009392") "MySQL 8.4 ngram relevance")
              | other -> failtestf "expected scored CJK join rows, got %A" other
              Expect.isLessThan calls 30 "only the ten CJK join candidates need virtual values"

              execute "INSERT INTO owners VALUES(43),(97)" |> ignore
              let cjkLeftJoined =
                  "SELECT d.id," + cjkScore + " FROM owners o LEFT JOIN cjk_docs d"
                  + " ON d.owner_id=o.id AND d.owner_id=42 WHERE o.id IN(42,43)"
                  + " AND MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE) ORDER BY d.id"
              calls <- 0
              let cjkLeftActual = execute cjkLeftJoined
              Expect.equal cjkLeftActual cjkControl "MySQL 8.4 ngram left-join ON bound preserves results and scores"
              Expect.isLessThan calls 30 "the right-side ON bound restricts CJK candidate preparation"
              let cjkLeftTransitive =
                  "SELECT d.id," + cjkScore + " FROM owners o LEFT JOIN cjk_docs d"
                  + " ON d.owner_id=o.id WHERE o.id=42"
                  + " AND MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE) ORDER BY d.id"
              calls <- 0
              Expect.equal (execute cjkLeftTransitive) cjkControl "preserved owner equality retains CJK scores"
              Expect.isLessThan calls 30 "the owner key narrows nullable CJK scoring candidates"
              let cjkLeftOnOwner =
                  "SELECT d.id," + cjkScore + " FROM owners o LEFT JOIN cjk_docs d"
                  + " ON d.owner_id=o.id AND o.id=42 WHERE o.id IN(42,43)"
                  + " AND MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE) ORDER BY d.id"
              calls <- 0
              Expect.equal (execute cjkLeftOnOwner) cjkControl "preserved ON bound retains CJK scores"
              Expect.isLessThan calls 30 "the preserved ON literal narrows nullable CJK scoring"
              let cjkPreserved =
                  "SELECT o.id,d.id," + cjkScore + " FROM owners o LEFT JOIN cjk_docs d"
                  + " ON d.owner_id=o.id AND d.owner_id=42 WHERE o.id IN(42,43)"
                  + " ORDER BY o.id,d.id"
              match execute cjkPreserved, cjkControl with
              | ResultSet(_, rows), ResultSet(_, controlRows) ->
                  Expect.equal (List.length rows) 11 "the unmatched owner remains visible"
                  Expect.sequenceEqual
                      (rows |> List.take 10 |> List.map (fun row -> row.[1], row.[2]))
                      (controlRows |> List.map (fun row -> row.[0], row.[1]))
                      "matching CJK rows retain MySQL relevance"
                  Expect.equal (List.last rows) [ Some "43"; None; Some "0" ] "MySQL 8.4 null-extended score"
              | actual, _ -> failtestf "expected left-join CJK rows, got %A" actual
              execute "INSERT INTO owners VALUES(999)" |> ignore
              let cjkTransitivePreserved =
                  "SELECT o.id,d.id," + cjkScore + " FROM owners o LEFT JOIN cjk_docs d"
                  + " ON d.owner_id=o.id WHERE o.id IN(42,999) ORDER BY o.id,d.id"
              calls <- 0
              let cjkTransitiveActual = execute cjkTransitivePreserved
              match cjkTransitiveActual, cjkControl with
              | ResultSet(_, rows), ResultSet(_, controlRows) ->
                  Expect.sequenceEqual
                      (rows |> List.take 10 |> List.map (fun row -> row.[1], row.[2]))
                      (controlRows |> List.map (fun row -> row.[0], row.[1]))
                      "an IN bound retains complete-corpus CJK scores"
                  Expect.equal (List.last rows) [ Some "999"; None; Some "0" ] "unmatched owner keeps its zero score"
              | actual, _ -> failtestf "expected transitive left-join CJK rows, got %A" actual
              Expect.isLessThan calls 30 "the preserved IN bound narrows nullable CJK scoring"
              let cjkRightTransitivePreserved =
                  "SELECT o.id,d.id," + cjkScore + " FROM cjk_docs d RIGHT JOIN owners o"
                  + " ON d.owner_id=o.id WHERE o.id IN(42,999) ORDER BY o.id,d.id"
              calls <- 0
              Expect.equal (execute cjkRightTransitivePreserved) cjkTransitiveActual "right join retains the same unmatched owner"
              Expect.isLessThan calls 30 "the right-join IN bound narrows nullable CJK scoring"
              execute "DELETE FROM owners WHERE id=999" |> ignore
              let cjkRightPreserved =
                  "SELECT o.id,d.id," + cjkScore + " FROM cjk_docs d RIGHT JOIN owners o"
                  + " ON d.owner_id=o.id AND d.owner_id=42 WHERE o.id IN(42,43)"
                  + " ORDER BY o.id,d.id"
              Expect.equal (execute cjkRightPreserved) (execute cjkPreserved) "nullable CJK scores match across join orientation"
              let cjkRightJoined =
                  "SELECT d.id," + cjkScore + " FROM cjk_docs d RIGHT JOIN owners o"
                  + " ON d.owner_id=o.id AND d.owner_id=42 WHERE o.id IN(42,43)"
                  + " AND MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE) ORDER BY d.id"
              calls <- 0
              Expect.equal (execute cjkRightJoined) cjkControl "MySQL 8.4 ngram right-join ON bound preserves results and scores"
              Expect.isLessThan calls 30 "the left-side ON bound restricts CJK candidate preparation"
              let cjkRightTransitive =
                  "SELECT d.id," + cjkScore + " FROM cjk_docs d RIGHT JOIN owners o"
                  + " ON d.owner_id=o.id WHERE o.id=42"
                  + " AND MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE) ORDER BY d.id"
              calls <- 0
              Expect.equal (execute cjkRightTransitive) cjkControl "preserved right-join equality retains scores"
              Expect.isLessThan calls 30 "the right-join owner key narrows nullable CJK scoring"
              let cjkChainedRightJoined =
                  "SELECT d.id," + cjkScore + " FROM owners p JOIN cjk_docs d ON d.owner_id=p.id"
                  + " RIGHT JOIN owners o ON d.owner_id=o.id AND d.owner_id=42 WHERE o.id IN(42,43)"
                  + " AND MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE) ORDER BY d.id"
              calls <- 0
              Expect.equal (execute cjkChainedRightJoined) cjkControl "MySQL 8.4 chained right-join scores"
              Expect.isLessThan calls 30 "a preceding scored source receives its local right-join ON bound"

              for bound, selected in
                  [ "o.id BETWEEN 42 AND 43", "42,43"
                    "o.id IN (42,97,NULL)", "42,97" ] do
                  let joined =
                      "SELECT d.id," + cjkScore + " FROM cjk_docs d JOIN owners o ON o.id=d.owner_id"
                      + " WHERE " + bound + " AND MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE) ORDER BY d.id"
                  let direct =
                      "SELECT d.id," + cjkScore + " FROM cjk_docs d WHERE d.owner_id IN (" + selected + ")"
                      + " AND MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE) ORDER BY d.id"
                  let control = execute direct
                  calls <- 0
                  let actual = execute joined
                  Expect.equal actual control (sprintf "ngram %s preserves complete-corpus relevance" bound)
                  Expect.equal (ids actual).Length 20 (sprintf "ngram %s selects two owner groups" bound)
                  Expect.isLessThan calls 50 (sprintf "ngram %s prepares only selected candidates" bound)

              for bound, selected in
                  [ "o.id BETWEEN 42 AND 43", [ 42; 43 ]
                    "o.id>=42 AND o.id<44", [ 42; 43 ]
                    "44>o.id AND 42<=o.id", [ 42; 43 ]
                    "o.id IN (42,97)", [ 42; 97 ]
                    "o.id IN (42,97,NULL)", [ 42; 97 ] ] do
                  let query redundant =
                      "SELECT d.id, ROUND(MATCH(d.body) AGAINST('needle'),6) FROM docs d JOIN owners o ON o.id=d.owner_id WHERE "
                      + bound + " AND MATCH(d.body) AGAINST('needle')" + redundant + " ORDER BY d.id"
                  let control = execute (query (" AND " + bound.Replace("o.id", "d.owner_id")))
                  calls <- 0
                  let actual = execute (query "")
                  let expected = [ 1..1000 ] |> List.filter (fun id -> List.contains (id % 100) selected) |> List.map string
                  Expect.equal (ids actual) expected "MySQL range and membership IDs"
                  Expect.equal actual control "propagated filters preserve full-corpus scores"
                  Expect.isLessThan calls 50 "only bounded candidates need virtual values"

          testCase "fulltext bound inference preserves ambiguous and forward reference errors"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE docs(id INT PRIMARY KEY,owner_id INT,body TEXT,KEY(owner_id),FULLTEXT(body))" |> ignore
              run store "INSERT INTO docs VALUES(1,42,'needle'),(2,43,'ordinary')" |> ignore
              run store "CREATE TABLE owners(id INT PRIMARY KEY)" |> ignore
              run store "INSERT INTO owners VALUES(42)" |> ignore
              run store "CREATE TABLE limits(chosen_owner INT PRIMARY KEY)" |> ignore
              run store "INSERT INTO limits VALUES(42)" |> ignore
              for joins, predicate, expected in
                  [ "JOIN owners o ON o.id=d.owner_id", "id=42", 1052
                    "JOIN owners o ON id=d.owner_id", "o.id=42", 1052
                    "JOIN owners o ON o.id=chosen_owner JOIN limits b ON b.chosen_owner=d.owner_id", "b.chosen_owner=42", 1054 ] do
                  match run store ("SELECT d.id FROM docs d " + joins + " WHERE " + predicate + " AND MATCH(d.body) AGAINST('needle')") with
                  | Err(code, _) -> Expect.equal code expected "MySQL name-resolution error"
                  | other -> failtestf "expected name-resolution error, got %A" other

          testCase "fulltext join bounds preserve text coercion and optional predicates"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE names(id INT PRIMARY KEY,k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci,body TEXT,KEY(k),FULLTEXT(body))" |> ignore
              run store "CREATE TABLE labels(k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci)" |> ignore
              run store "INSERT INTO names VALUES(1,'①','needle'),(2,'1','needle'),(3,'other','ordinary'),(4,'missing','needle')" |> ignore
              run store "INSERT INTO labels VALUES('1')" |> ignore
              let check expected joins predicate =
                  run store ("SELECT d.id FROM names d " + joins + " WHERE " + predicate + " AND MATCH(d.body) AGAINST('needle') ORDER BY d.id")
                  |> ids
                  |> fun actual -> Expect.equal actual expected predicate
              check [ "1"; "2" ] "JOIN labels o ON o.k=d.k" "o.k=1"
              check [ "2" ] "JOIN labels o ON o.k=d.k" "o.k=1 AND d.k=1"
              check [ "1"; "2" ] "JOIN labels o ON o.k=d.k" "o.k BETWEEN 1 AND 1"
              check [ "1"; "2" ] "JOIN labels o ON o.k=d.k" "o.k='1'"
              check [ "1"; "2"; "4" ] "LEFT JOIN labels o ON o.k=d.k AND o.k='1'" "TRUE"
              check [ "1"; "2"; "4" ] "CROSS JOIN labels o" "(o.k=d.k OR d.id=4) AND o.k='1'"

          testCase "fulltext document replacement survives transaction publication with concurrent writes"
          <| fun _ ->
              let table store =
                  match tableSnapshot store defaultDatabase "docs" with
                  | Ok table -> table
                  | Error error -> failtestf "table snapshot: %A" error
              for parser, original, replacement in [ "", "orchard", "cobalt"; " WITH PARSER ngram", "生日", "中文" ] do
                  for restoreOriginal in [ false; true ] do
                      let store = create ()
                      run store ("CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body)" + parser + ")") |> ignore
                      run store ($"INSERT INTO docs VALUES(1,'{original}'),(2,'{original}')") |> ignore
                      let before = table store
                      let rowId id (table: Table) =
                          table.RowsArray.Indexed |> Seq.find (fun (_, row) -> row.[0] = VInt id) |> fst
                      let firstId, secondId = rowId 1L before, rowId 2L before
                      let baseCatalog, branch = beginTransactionSnapshotWithBase store
                      run branch ($"UPDATE docs SET body='{replacement}' WHERE id=1") |> ignore
                      if restoreOriginal then
                          run branch ($"UPDATE docs SET body='{original}' WHERE id=1") |> ignore
                      let pending = table branch
                      run store ($"UPDATE docs SET body='{replacement}' WHERE id=2") |> ignore
                      let concurrent = table store
                      commitCatalogInto store baseCatalog branch
                      let committed = table store
                      Expect.isTrue (sameDocument firstId pending.FullTextIndexes.["ft"] committed.FullTextIndexes.["ft"]) "publication retains the branch document"
                      Expect.isFalse (sameDocument firstId before.FullTextIndexes.["ft"] committed.FullTextIndexes.["ft"]) "restoring field values does not restore document identity"
                      Expect.isTrue (sameDocument secondId concurrent.FullTextIndexes.["ft"] committed.FullTextIndexes.["ft"]) "concurrent documents retain their identity"
                      let term = if restoreOriginal then original else replacement
                      let expected = if restoreOriginal then [ "1" ] else [ "1"; "2" ]
                      Expect.equal (ids (run store ($"SELECT id FROM docs WHERE MATCH(body) AGAINST('{term}' IN BOOLEAN MODE) ORDER BY id"))) expected "published postings match the merged rows"

          testCase "ngram fulltext indexes distinguish natural unions and boolean phrases"
          <| fun _ ->
              let store = create ()
              Expect.equal
                  (run store "CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body) WITH PARSER ngram)")
                  (Affected 0UL)
                  "ngram index creation"
              run store "INSERT INTO docs VALUES (1,'生日快乐'),(2,'生日'),(3,'快乐'),(4,'生 日'),(5,'生日 开心'),(6,'abc'),(7,'ab bc'),(8,'a,b'),(9,'dbms'),(10,'mysql'),(11,'日本語'),(12,'日本 語'),(13,'한국어'),(14,'한국 어'),(15,'🙂生日'),(16,'生🙂日'),(17,'生'),(18,''),(19,NULL),(20,'生日生日'),(21,'生日!快乐'),(22,'日快')"
              |> ignore
              for term, mode, expected in
                  [ "生日快乐", "IN NATURAL LANGUAGE MODE", [ "1"; "2"; "3"; "5"; "15"; "20"; "21"; "22" ]
                    "生日快乐", "IN BOOLEAN MODE", [ "1" ]
                    "生", "IN NATURAL LANGUAGE MODE", []
                    "生*", "IN BOOLEAN MODE", [ "1"; "2"; "5"; "15"; "16"; "20"; "21" ]
                    "生日快乐*", "IN BOOLEAN MODE", [ "1" ]
                    "\"生日 快乐\"", "IN BOOLEAN MODE", [ "21" ]
                    "+生日 +快乐", "IN BOOLEAN MODE", [ "1"; "21" ]
                    "生日 -快乐", "IN BOOLEAN MODE", [ "2"; "5"; "15"; "20" ]
                    "日本語", "IN NATURAL LANGUAGE MODE", [ "11"; "12" ]
                    "日本語", "IN BOOLEAN MODE", [ "11" ]
                    "한국어", "IN NATURAL LANGUAGE MODE", [ "13"; "14" ]
                    "한국어", "IN BOOLEAN MODE", [ "13" ]
                    "abc", "IN BOOLEAN MODE", [ "6"; "7" ]
                    "ab", "IN NATURAL LANGUAGE MODE", []
                    "🙂生", "IN NATURAL LANGUAGE MODE", [ "15" ]
                    "🙂生", "IN BOOLEAN MODE", [] ] do
                  let sql = sprintf "SELECT id FROM docs WHERE MATCH(body) AGAINST('%s' %s) ORDER BY id" term mode
                  Expect.equal (ids (run store sql)) expected sql
              match run store "SELECT MATCH(body) AGAINST('生日快乐') FROM docs WHERE id=1" with
              | ResultSet(_, [ [ Some score ] ]) ->
                  Expect.isTrue (abs (float score - 2.1516475677490234) < 0.000001) "ngram relevance"
              | other -> failtestf "expected ngram relevance, got %A" other

          testCase "ngram searches fold halfwidth katakana under the default collation"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE kana_docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO kana_docs VALUES(1,'カタカナ'),(2,'ｶﾀｶﾅ'),(3,'日本語カタカナ'),(4,'生日快乐'),(5,'abc')" |> ignore
              for term in [ "カタカナ"; "ｶﾀｶﾅ" ] do
                  for mode in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE" ] do
                      let sql = $"SELECT id FROM kana_docs WHERE MATCH(body) AGAINST('{term}' {mode}) ORDER BY id"
                      Expect.equal (ids (run store sql)) [ "1"; "2"; "3" ] sql
                  match run store ($"SELECT id,MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) FROM kana_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id") with
                  | ResultSet(_, rows) ->
                      for row in rows do
                          let score = row.[1] |> Option.defaultWith (fun () -> failtest "missing relevance")
                          Expect.isLessThan (abs (float score - 0.1476505994796753)) 0.000001 "MySQL 8.4 relevance"
                  | other -> failtestf "expected ngram results, got %A" other

          testCase "repeated collation keys in a voiced ngram phrase contribute once with query frequency"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE kana_scores(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO kana_scores VALUES(1,'ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ')" |> ignore
              for mode in [ "IN BOOLEAN MODE"; "IN NATURAL LANGUAGE MODE" ] do
                  match run store ($"SELECT id,MATCH(body) AGAINST('ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ' {mode}) FROM kana_scores") with
                  | ResultSet(_, [ [ Some "1"; Some score ] ]) ->
                      Expect.isLessThan (abs (float score - 0.7249524593353271)) 0.000001 ("MySQL 8.4 " + mode)
                  | other -> failtestf "expected one scored ngram row, got %A" other

          testCase "single-character ngrams share hiragana and katakana postings"
          <| fun _ ->
              let store = create ()
              configureNgramTokenSize 1 store
              run store "CREATE TABLE kana_script(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO kana_script VALUES(1,'ば'),(2,'パ'),(3,'ｽ'),(4,'abc')" |> ignore
              for term in [ "ば"; "パ" ] do
                  let sql = $"SELECT id FROM kana_script WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"
                  Expect.equal (ids (run store sql)) [ "1"; "2" ] sql

          testCase "single-character ngrams share MySQL comma presentation forms"
          <| fun _ ->
              let store = create ()
              configureNgramTokenSize 1 store
              run store "CREATE TABLE comma_docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO comma_docs VALUES(1,'，'),(2,'︐'),(3,'﹐'),(4,'、'),(5,'︑'),(6,'﹅'),(7,'中文︐標點')" |> ignore
              for term in [ "，"; "︐"; "﹐" ] do
                  let sql = $"SELECT id FROM comma_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"
                  Expect.equal (ids (run store sql)) [ "1"; "2"; "3"; "7" ] sql
              for term in [ "、"; "︑" ] do
                  let sql = $"SELECT id FROM comma_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"
                  Expect.equal (ids (run store sql)) [ "4"; "5" ] sql

          testCase "legacy Unicode ngrams distinguish vertical punctuation"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE legacy_commas(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_unicode_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO legacy_commas VALUES(1,'中、文'),(2,'中︑文'),(3,'中﹑文'),(4,'中。文'),(5,'中︒文')" |> ignore
              let search term =
                  let sql = $"SELECT id FROM legacy_commas WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"
                  ids (run store sql)
              Expect.equal (search "中、") [ "1"; "3" ] "the small form retains the ordinary comma's weight"
              Expect.equal (search "中︑") [ "2" ] "the vertical form has a distinct UCA 4.0 weight"
              Expect.equal (search "中。") [ "4" ] "the ordinary period does not match its vertical form"
              Expect.equal (search "中︒") [ "5" ] "the vertical period keeps its implicit UCA 4.0 weight"

          testCase "legacy Unicode ngrams retain small comma token boundaries"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE small_commas(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_unicode_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO small_commas VALUES(1,'中,文'),(2,'中，文'),(3,'中﹐文')" |> ignore
              let search term =
                  let sql = $"SELECT id FROM small_commas WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"
                  ids (run store sql)
              Expect.equal (search "中,") [] "ASCII punctuation splits the ngram token"
              Expect.equal (search "中，") [ "2"; "3" ] "fullwidth and small comma tokens share a key"
              Expect.equal (search "中﹐") [ "2"; "3" ] "small comma finds both token forms"

          testCase "legacy Unicode ngrams keep implicit enclosed CJK forms distinct"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE enclosed_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_unicode_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO enclosed_docs VALUES(1,'中㉆文'),(2,'中㊥文'),(3,'中㉊文'),(4,'中㉚文'),(5,'中㉋文'),(6,'中㊵文'),(7,'中㉌文'),(8,'中㊿文')" |> ignore
              let search term mode =
                  let sql = $"SELECT id FROM enclosed_docs WHERE MATCH(body) AGAINST('{term}' {mode}) ORDER BY id"
                  ids (run store sql)
              for mode in [ "IN NATURAL LANGUAGE MODE"; "WITH QUERY EXPANSION" ] do
                  for id, term in [ "1", "中㉆"; "2", "中㊥"; "3", "中㉊"; "4", "中㉚"
                                    "5", "中㉋"; "6", "中㊵"; "7", "中㉌"; "8", "中㊿" ] do
                      Expect.equal (search term mode) [ id ] (mode + " " + term)
              for id, term in [ "4", "中㉚"; "6", "中㊵"; "8", "中㊿" ] do
                  Expect.equal (search term "IN BOOLEAN MODE") [ id ] ("Boolean " + term)
              for term in [ "中㉆"; "中㊥"; "中㉊"; "中㉋"; "中㉌" ] do
                  Expect.equal (search term "IN BOOLEAN MODE") [] ("Boolean Unicode boundary " + term)

          testCase "legacy Unicode ngrams retain historical letterlike case distinctions"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE symbol_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_unicode_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO symbol_docs VALUES(1,'中Ⅎ文'),(2,'中ⅎ文'),(3,'中Ↄ文'),(4,'中ↄ文')" |> ignore
              let search term mode =
                  let sql = $"SELECT id FROM symbol_docs WHERE MATCH(body) AGAINST('{term}' {mode}) ORDER BY id"
                  ids (run store sql)
              for id, term in [ "1", "中Ⅎ"; "2", "中ⅎ"; "3", "中Ↄ"; "4", "中ↄ" ] do
                  for mode in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE" ] do
                      Expect.equal (search term mode) [ id ] (term + " keeps its own " + mode + " posting")

          testCase "legacy Unicode ngrams fold kana extensions and keep Bopomofo distinctions"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE kana_ligatures(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_unicode_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO kana_ligatures VALUES(1,'中ゟ文'),(2,'中ヿ文'),(3,'中より文'),(4,'中コト文'),(5,'中ㇰ文'),(6,'中ク文'),(7,'中ㄮ文'),(8,'中ㄜ文'),(9,'中ㆻ文'),(10,'中ㄍ文'),(11,'中ㆣ文')" |> ignore
              let search term mode =
                  let sql = $"SELECT id FROM kana_ligatures WHERE MATCH(body) AGAINST('{term}' {mode}) ORDER BY id"
                  ids (run store sql)
              for mode in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE" ] do
                  Expect.equal (search "中ゟ" mode) [ "1" ] (mode + " ligature posting")
                  Expect.equal (search "中ヿ" mode) [ "2" ] (mode + " katakana ligature posting")
                  Expect.equal (search "中ㇰ" mode) [ "5"; "6" ] (mode + " small katakana folds")
                  Expect.equal (search "中ㄍ" mode) [ "10"; "11" ] (mode + " older velar forms remain equal")
              Expect.equal (search "中ㄮ" "IN NATURAL LANGUAGE MODE") [ "7" ] "Bopomofo extension stays distinct"
              Expect.equal (search "中ㆻ" "IN NATURAL LANGUAGE MODE") [ "9" ] "newer Bopomofo stays distinct"
              Expect.equal (search "中ㄮ" "IN BOOLEAN MODE") [] "historical Boolean word table excludes ㄮ"
              Expect.equal (search "中ㆻ" "IN BOOLEAN MODE") [] "historical Boolean word table excludes ㆻ"

          testCase "legacy Unicode ngrams keep later BMP ideographs distinct and share supplementary keys"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE compatibility_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_unicode_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO compatibility_docs VALUES(1,'中郞文'),(2,'中郞文'),(3,'中並文'),(4,'中並文'),(5,'中丽文'),(6,'中丸文'),(7,'中𠀀文')" |> ignore
              let search term =
                  let sql = $"SELECT id FROM compatibility_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"
                  ids (run store sql)
              Expect.equal (search "中郞") [ "1" ] "later BMP form keeps its implicit key"
              Expect.equal (search "中並") [ "3" ] "second BMP form stays distinct from its base"
              Expect.equal (search "中丽") [ "5"; "6"; "7" ] "supplementary forms share MySQL's fallback key"

          testCase "general Unicode ngrams share supplementary posting keys"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE general_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_general_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO general_docs VALUES(1,'中𠮷文'),(2,'中🙂文'),(3,'中𠀀文'),(4,'中生文')" |> ignore
              let search term =
                  let sql = $"SELECT id FROM general_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"
                  ids (run store sql)
              Expect.equal (search "中𠮷") [ "1"; "2"; "3" ] "supplementary query shares fallback postings"
              Expect.equal (search "中生") [ "4" ] "BMP Han remains a distinct posting"

          testCase "general Unicode single-character ngrams count compatibility ideographs separately"
          <| fun _ ->
              let store = create ()
              configureNgramTokenSize 1 store
              run store "CREATE TABLE compatibility_scores(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_general_ci,title TEXT COLLATE utf8mb4_general_ci,FULLTEXT KEY ft(body,title) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO compatibility_scores VALUES(38,'神神社','神社')" |> ignore
              let score term mode =
                  match run store ($"SELECT id,MATCH(body,title) AGAINST('{term}' {mode}) FROM compatibility_scores") with
                  | ResultSet(_, [ [ Some "38"; Some value ] ]) -> float value
                  | other -> failtestf "expected one scored compatibility row, got %A" other
              for mode in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE" ] do
                  let compatibility = score "神" mode
                  let unified = score "神" mode
                  Expect.isLessThan (abs (unified / compatibility - 2.0)) 0.00001 ("MySQL 8.4 " + mode)

          testCase "general Unicode square-unit ngrams retain their written symbols"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE square_units(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_general_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO square_units VALUES(1,'中㎞文'),(2,'中㏎文')" |> ignore
              let search term =
                  ids (run store ($"SELECT id FROM square_units WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"))
              Expect.equal (search "中㎞") [ "1" ] "square kilometer keeps its own posting"
              Expect.equal (search "中㏎") [ "2" ] "square uppercase kilometer keeps its own posting"

          testCase "general Unicode enclosed-number ngrams retain their written symbols"
          <| fun _ ->
              let store = create ()
              configureNgramTokenSize 1 store
              run store "CREATE TABLE enclosed_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_general_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO enclosed_docs VALUES(1,'中①文'),(2,'中⓵文'),(3,'中Ⓐ文'),(4,'中ⓐ文')" |> ignore
              let search term =
                  ids (run store ($"SELECT id FROM enclosed_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"))
              Expect.equal (search "①") [ "1" ] "circled one keeps its own posting"
              Expect.equal (search "⓵") [ "2" ] "double-circled one keeps its own posting"
              Expect.equal (search "Ⓐ") [ "3"; "4" ] "circled letter case remains insensitive"

          testCase "general Unicode CJK symbol ngrams retain their written marks"
          <| fun _ ->
              let store = create ()
              configureNgramTokenSize 1 store
              run store "CREATE TABLE symbol_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_general_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO symbol_docs VALUES(1,'中〒文'),(2,'中〶文'),(3,'中〝文'),(4,'中〞文')" |> ignore
              let search term =
                  ids (run store ($"SELECT id FROM symbol_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"))
              Expect.equal (search "〒") [ "1" ] "postal mark keeps its own posting"
              Expect.equal (search "〶") [ "2" ] "circled postal mark keeps its own posting"
              Expect.equal (search "〝") [ "3" ] "opening quote keeps its own posting"
              Expect.equal (search "〞") [ "4" ] "closing quote keeps its own posting"

          testCase "general Unicode letterlike ngrams retain distinct postings"
          <| fun _ ->
              let store = create ()
              configureNgramTokenSize 1 store
              run store "CREATE TABLE letterlike_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_general_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO letterlike_docs VALUES(1,'中℁文'),(2,'中⅍文'),(3,'中ℂ文'),(4,'中ℭ文'),(5,'中Ⅎ文'),(6,'中ⅎ文'),(7,'中Ↄ文'),(8,'中ↄ文'),(9,'中Ⅰ文'),(10,'中ⅰ文')" |> ignore
              let search term =
                  ids (run store ($"SELECT id FROM letterlike_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"))
              Expect.equal (search "℁") [ "1" ] "care-of sign keeps its own posting"
              Expect.equal (search "⅍") [ "2" ] "aktieselskab sign keeps its own posting"
              Expect.equal (search "ℂ") [ "3" ] "double-struck C keeps its own posting"
              Expect.equal (search "ℭ") [ "4" ] "black-letter C keeps its own posting"
              Expect.equal (search "Ⅎ") [ "5" ] "turned capital F keeps its own posting"
              Expect.equal (search "ⅎ") [ "6" ] "turned small F keeps its own posting"
              Expect.equal (search "Ↄ") [ "7" ] "reversed capital C keeps its own posting"
              Expect.equal (search "ↄ") [ "8" ] "reversed small C keeps its own posting"
              Expect.equal (search "Ⅰ") [ "9"; "10" ] "Roman numeral case shares postings"

          testCase "general Unicode mathematical ngrams retain distinct postings"
          <| fun _ ->
              let store = create ()
              configureNgramTokenSize 1 store
              run store "CREATE TABLE operator_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_general_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO operator_docs VALUES(1,'中∃文'),(2,'中∄文'),(3,'中∈文'),(4,'中∉文'),(5,'中∣文'),(6,'中∤文')" |> ignore
              let search term =
                  ids (run store ($"SELECT id FROM operator_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"))
              for term, expected in [ "∃", "1"; "∄", "2"; "∈", "3"; "∉", "4"; "∣", "5"; "∤", "6" ] do
                  Expect.equal (search term) [ expected ] (term + " keeps its own posting")

          testCase "general Unicode dingbat and supplemental ngrams retain distinct postings"
          <| fun _ ->
              let store = create ()
              configureNgramTokenSize 1 store
              run store "CREATE TABLE later_symbol_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_general_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO later_symbol_docs VALUES(1,'中❶文'),(2,'中➀文'),(3,'中➊文'),(4,'中⫝̸文'),(5,'中⫝文')" |> ignore
              let search term =
                  ids (run store ($"SELECT id FROM later_symbol_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"))
              for term, expected in [ "❶", "1"; "➀", "2"; "➊", "3"; "⫝̸", "4"; "⫝", "5" ] do
                  Expect.equal (search term) [ expected ] (term + " keeps its own posting")

          testCase "general Unicode Bopomofo extension ngrams retain distinct postings"
          <| fun _ ->
              let store = create ()
              configureNgramTokenSize 1 store
              run store "CREATE TABLE bopomofo_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_general_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO bopomofo_docs VALUES(1,'中ㆣ文'),(2,'中ㆻ文'),(3,'中ㆤ文'),(4,'中ㆥ文')" |> ignore
              let search term =
                  ids (run store ($"SELECT id FROM bopomofo_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"))
              for term, expected in [ "ㆣ", "1"; "ㆻ", "2"; "ㆤ", "3"; "ㆥ", "4" ] do
                  Expect.equal (search term) [ expected ] (term + " keeps its own posting")

          testCase "ngram Boolean lexer uses MySQL's historical Unicode word characters"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE unicode_words(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO unicode_words VALUES(1,'中㉎文'),(2,'中①文'),(3,'中\u4DBC文'),(4,'中\u9FA6文')" |> ignore
              let search term mode =
                  let sql = $"SELECT id FROM unicode_words WHERE MATCH(body) AGAINST('{term}' {mode}) ORDER BY id"
                  ids (run store sql)
              Expect.equal (search "中㉎" "IN NATURAL LANGUAGE MODE") [ "1" ] "ngram postings retain newer numeric symbols"
              Expect.equal (search "中㉎" "IN BOOLEAN MODE") [] "Unicode 5 draft excludes U+324E from Boolean terms"
              Expect.equal (search "\"中㉎\"" "IN BOOLEAN MODE") [] "quoted Boolean terms use the same boundary"
              Expect.equal (search "中①" "IN BOOLEAN MODE") [ "2" ] "older circled digits remain Boolean terms"
              Expect.equal (search "中\u4DBC" "IN BOOLEAN MODE") [] "later-assigned ideographs are not Boolean terms"
              Expect.equal (search "中\u9FA6" "IN BOOLEAN MODE") [] "draft/final Unicode 5 exception is excluded"

          testCase "legacy Unicode ngrams share width-folded punctuation keys"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE width_forms(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_unicode_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO width_forms VALUES(1,'中︵文'),(2,'中﹙文'),(3,'中（文'),(4,'中﹡文'),(5,'中＊文')" |> ignore
              let search term =
                  let sql = $"SELECT id FROM width_forms WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"
                  ids (run store sql)
              Expect.equal (search "中︵") [ "1"; "2"; "3" ] "vertical, small, and fullwidth parentheses share a key"
              Expect.equal (search "中＊") [ "4"; "5" ] "small and fullwidth stars share a key"
              let booleanSearch term =
                  let sql = $"SELECT id FROM width_forms WHERE MATCH(body) AGAINST('{term}' IN BOOLEAN MODE) ORDER BY id"
                  ids (run store sql)
              Expect.equal (booleanSearch "中(") [] "an unclosed Boolean group rejects the query"
              Expect.equal (booleanSearch "中)") [] "a stray Boolean close rejects the query"

          testCase "legacy Unicode ngrams fold small kana and separate quote forms"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE kana_quote_docs(id INT PRIMARY KEY,body TEXT COLLATE utf8mb4_unicode_ci,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO kana_quote_docs VALUES(1,'中＂文'),(2,'中〝文'),(3,'中〞文'),(4,'中〟文'),(5,'中ｧ文'),(6,'中ｱ文'),(7,'中ァ文'),(8,'中ア文')" |> ignore
              let search term =
                  let sql = $"SELECT id FROM kana_quote_docs WHERE MATCH(body) AGAINST('{term}' IN NATURAL LANGUAGE MODE) ORDER BY id"
                  ids (run store sql)
              Expect.equal (search "中＂") [ "1" ] "the fullwidth quote stays separate from three CJK quotes"
              Expect.equal (search "中〝") [ "2" ] "the opening CJK quote keeps its own posting"
              Expect.equal (search "中〞") [ "3" ] "the closing CJK quote keeps its own posting"
              Expect.equal (search "中〟") [ "4" ] "the low CJK quote keeps its own posting"
              Expect.equal (search "中ｧ") [ "5"; "6"; "7"; "8" ] "small and regular kana share a posting"
              Expect.equal (search "中ア") [ "5"; "6"; "7"; "8" ] "fullwidth kana finds the same corpus"

          testCase "single-character ngram scores count repeated query tokens"
          <| fun _ ->
              let store = create ()
              configureNgramTokenSize 1 store
              run store "CREATE TABLE ngram_scores(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO ngram_scores VALUES(1,'生日快乐'),(2,'日本語'),(3,'生日 日本語'),(4,'中文')" |> ignore

              let check term mode expected =
                  match run store ($"SELECT id,MATCH(body) AGAINST('{term}' {mode}) FROM ngram_scores ORDER BY id") with
                  | ResultSet(_, rows) ->
                      for id, score in expected do
                          let actual = rows |> List.find (fun row -> row.[0] = Some(string id))
                          let actualScore = actual.[1] |> Option.defaultWith (fun () -> failtestf "missing score for row %d" id)
                          Expect.isLessThan (abs (float actualScore - score)) 0.00001 $"MySQL 8.4 ngram score for row {id}"
                  | other -> failtestf "expected ngram scores, got %A" other

              check "生日 日本語" "IN NATURAL LANGUAGE MODE" [ 1, 0.1216271892; 2, 0.2122462392; 3, 0.3338734508 ]
              check "生日 日本語" "IN BOOLEAN MODE" [ 1, 0.1216271892; 2, 0.2122462392; 3, 0.3338734508 ]
              check "+生日 -日本語" "IN BOOLEAN MODE" [ 1, 0.1216271892 ]
              check "生日 日本語" "WITH QUERY EXPANSION" [ 1, 0.8465796709; 2, 0.2122462392; 3, 0.3338734508 ]

          testCase "ngram phrases retain stopped positions and split quoted words"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE ngram_boundaries(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO ngram_boundaries VALUES(1,'cdabef'),(2,'cd ab ef'),(3,'cdef'),(4,'cd xx ef'),(5,'cdaef'),(6,'ab生日'),(7,'生日ab快乐'),(8,'生日xx快乐'),(9,'生日 快乐'),(10,'生日　快乐'),(11,'生日，快乐'),(12,'生😀日'),(13,'𠮷野家'),(14,'b,c'),(15,'b_c'),(16,'b''c'),(17,'b-c'),(18,'b.c'),(19,'bc'),(20,'áb'),(21,'生日a快乐'),(22,'生日abc快乐')" |> ignore
              for sql, expected in
                  [
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('cdabef' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1"; "2"; "3"; "4"; "5"; "20" ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('\"cdabef\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1"; "2"; "3"; "4"; "5"; "20" ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('cdabef' IN BOOLEAN MODE) ORDER BY id", [ "1" ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('\"cdabef\"' IN BOOLEAN MODE) ORDER BY id", [ "1" ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('\"cd ef\"' IN BOOLEAN MODE) ORDER BY id", [  ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('cdef' IN BOOLEAN MODE) ORDER BY id", [ "3" ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('生日a快乐' IN BOOLEAN MODE) ORDER BY id", [ "21" ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('\"生日 快乐\"' IN BOOLEAN MODE) ORDER BY id", [ "9" ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('\"生日，快乐\"' IN BOOLEAN MODE) ORDER BY id", [ "9" ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('\"生日 快乐\" @2' IN BOOLEAN MODE) ORDER BY id", [ "9" ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('\"生日 快乐\" @3' IN BOOLEAN MODE) ORDER BY id", [ "9" ]
                    "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('+\"生日 快乐\"' IN BOOLEAN MODE) ORDER BY id", [ "9" ]
                  ] do
                  Expect.equal (ids (run store sql)) expected sql

          testCase "ngram quoted phrases retain explicit short words"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run store "INSERT INTO docs VALUES(1,'生日 快乐'),(2,'生日 生 快乐'),(3,'生日 x 快乐'),(4,'生日 a 快乐')" |> ignore
              for term, expected in
                  [ "\"生日 快乐\"", [ "1"; "2"; "3"; "4" ]
                    "\"生 生日\"", []
                    "\"生日 生\"", []
                    "\"生日 生 快乐\"", []
                    "\"生日 x 快乐\"", []
                    "\"a 生日\"", [ "1"; "2"; "3"; "4" ] ] do
                  let sql = sprintf "SELECT id FROM docs WHERE MATCH(body) AGAINST('%s' IN BOOLEAN MODE) ORDER BY id" term
                  Expect.equal (ids (run store sql)) expected sql

          testCase "fulltext exact phrases stay within indexed columns"
          <| fun _ ->
              for parser, first, second in [ " WITH PARSER ngram", "生日", "快乐"; "", "mysql", "security" ] do
                  let store = create ()
                  run store ("CREATE TABLE docs(id INT PRIMARY KEY,a TEXT,b TEXT,FULLTEXT(a,b)" + parser + ")") |> ignore
                  run store (sprintf "INSERT INTO docs VALUES(1,'%s','%s'),(2,'%s %s',''),(3,'%s%s',NULL),(5,'other','%s %s')" first second first second first second first second) |> ignore
                  let phrase = sprintf "\"%s %s\"" first second
                  let query suffix = sprintf "SELECT id FROM docs WHERE MATCH(a,b) AGAINST('%s%s' IN BOOLEAN MODE) ORDER BY id" phrase suffix
                  Expect.equal (ids (run store (query ""))) [ "2"; "5" ] "exact phrase respects fields"
                  if parser = "" then
                      Expect.equal (ids (run store (query " @100"))) [ "1"; "2"; "5" ] "word proximity can cross fields"

          testCase "natural-language phrases preserve words and column boundaries"
          <| fun _ ->
              let store = create ()
              run store "CREATE TABLE docs(id INT PRIMARY KEY,a TEXT,b TEXT,FULLTEXT(a,b))" |> ignore
              run store "INSERT INTO docs VALUES(1,'mysql','security'),(2,'mysql security',''),(3,'mysqlsecurity',NULL),(4,'my','sql'),(5,'sql','mysql security'),(6,'mysql extra security',''),(7,'security mysql',''),(8,'database',''),(9,'mysql the security',''),(10,'mysql x security','')" |> ignore
              for term, natural, expanded in
                  [ "\"mysql security\"", [ "2"; "5" ], [ "2"; "4"; "5" ]
                    "\"mysql security\" database", [ "2"; "5"; "8" ], [ "2"; "4"; "5"; "8" ]
                    "\"mysql security\" \"security mysql\"", [ "2"; "5"; "7" ], [ "2"; "4"; "5"; "7" ]
                    "\"mysql the security\"", [ "9" ], [ "9" ]
                    "\"mysql x security\"", [ "10" ], [ "10" ]
                    "\"mysql mysql\"", [], [] ] do
                  for mode, expected in [ "IN NATURAL LANGUAGE MODE", natural; "WITH QUERY EXPANSION", expanded ] do
                      let sql = sprintf "SELECT id FROM docs WHERE MATCH(a,b) AGAINST('%s' %s) ORDER BY id" term mode
                      Expect.equal (ids (run store sql)) expected sql

              for term, row, expected in
                  [ "\"mysql security\"", 2, 0.04798923433
                    "\"mysql security\" mysql", 1, 0.02135340311
                    "\"mysql security\" mysql", 2, 0.04534801841
                    "mysql mysql", 1, 0.02135340311
                    "\"mysql security\" \"mysql security\"", 2, 0.04270680621 ] do
                  let sql = sprintf "SELECT MATCH(a,b) AGAINST('%s') FROM docs WHERE id=%d" term row
                  match run store sql with
                  | ResultSet(_, [ [ Some score ] ]) ->
                      Expect.isTrue (abs (float score - expected) < 0.000001) sql
                  | other -> failtestf "expected phrase relevance, got %A" other

          testCase "ngram indexes maintain postings through DDL and rollback"
          <| fun _ ->
              for definition in
                  [ [ "CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body) WITH PARSER ngram)" ]
                    [ "CREATE TABLE docs(id INT PRIMARY KEY, body TEXT)"
                      "CREATE FULLTEXT INDEX ft ON docs(body) WITH PARSER ngram" ]
                    [ "CREATE TABLE docs(id INT PRIMARY KEY, body TEXT)"
                      "ALTER TABLE docs ADD FULLTEXT KEY ft(body) WITH PARSER ngram" ] ] do
                  let mutable session = Fsdb.Session.create 1 (create ())
                  let execute sql =
                      let next, result = Fsdb.QueryHandler.handle session sql
                      session <- next
                      result
                  for ddl in definition do Expect.equal (execute ddl) (Affected 0UL) ddl
                  Expect.equal (execute "INSERT INTO docs VALUES(1,'生日快乐'),(2,'生日')") (Affected 2UL) "insert"
                  Expect.equal (execute "UPDATE docs SET body='中文检索' WHERE id=1") (Affected 1UL) "update"
                  Expect.equal (execute "DELETE FROM docs WHERE id=2") (Affected 1UL) "delete"
                  Expect.equal (execute "INSERT INTO docs VALUES(3,'生日')") (Affected 1UL) "insert after removal"
                  for sql in [ "START TRANSACTION"; "UPDATE docs SET body='中文' WHERE id=3"; "ROLLBACK" ] do
                      execute sql |> ignore
                  let query = "SELECT id FROM docs WHERE MATCH(body) AGAINST('生日') ORDER BY id"
                  Expect.equal (ids (execute query)) [ "3" ] "rollback preserves postings"
                  Expect.equal (execute "CREATE TABLE copied LIKE docs") (Affected 0UL) "copy definition"
                  Expect.equal (execute "INSERT INTO copied SELECT * FROM docs") (Affected 2UL) "copy rows"
                  Expect.equal (ids (execute (query.Replace("docs", "copied")))) [ "3" ] "copied parser"
                  match execute "SHOW CREATE TABLE docs" with
                  | ResultSet(_, [ [ _; Some ddl ] ]) ->
                      Expect.stringContains ddl "/*!50100 WITH PARSER `ngram` */" "parser survives introspection"
                  | other -> failtestf "expected table DDL, got %A" other
                  Expect.equal
                      (execute "CREATE FULLTEXT INDEX missing ON docs(body) WITH PARSER missing_parser")
                      (Err(1128, "Function 'missing_parser' is not defined"))
                      "unknown parser"
                  match execute "CREATE INDEX ordinary ON docs(body(10)) WITH PARSER ngram" with
                  | Err(1064, _) -> ()
                  | other -> failtestf "expected non-fulltext syntax rejection, got %A" other

          testCase "natural fulltext predicates permit aggregate queries without explicit grouping"
          <| fun _ ->
              let connection = Fsdb.Db.create () |> Fsdb.Db.connect
              connection.Query "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body))" |> ignore
              connection.Query "INSERT INTO docs VALUES(1,'database concurrency'),(2,'storage transactions')" |> ignore
              for sql, expected in
                  [ "SELECT COUNT(*) AS n FROM docs WHERE MATCH(body) AGAINST('database')", "1"
                    "SELECT SUM(id)+COUNT(*) AS n FROM docs WHERE MATCH(body) AGAINST('database')", "2"
                    "SELECT 1 AS n FROM docs WHERE MATCH(body) AGAINST('database') HAVING COUNT(*)>0", "1"
                    "SELECT COUNT(*) AS n FROM docs WHERE MATCH(body) AGAINST('absent')", "0"
                    "SELECT COUNT(*) AS n FROM docs WHERE MATCH(body) AGAINST('database') LIMIT 1", "1"
                    "SELECT SUM(COUNT(*)) OVER() AS n FROM docs WHERE MATCH(body) AGAINST('database')", "1" ] do
                  Expect.equal (connection.Query sql) (ResultSet([ "n" ], [ [ Some expected ] ])) sql

          testCase "natural-language WHERE keeps matching rows and orders by relevance implicitly"
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
                  Expect.equal label "MATCH (title,body) AGAINST ('Tutorial')" "original expression header"
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

          testCase "boolean proximity uses strict unordered windows"
          <| fun _ ->
              let store = create ()

              [ "CREATE TABLE docs (id INT PRIMARY KEY, body TEXT, FULLTEXT(body))"
                "INSERT INTO docs VALUES
                 (1, 'database concurrency'),
                 (2, 'database x concurrency'),
                 (3, 'database x y concurrency'),
                 (4, 'concurrency x database')" ]
              |> List.iter (run store >> ignore)

              Expect.equal
                  (ids (run store "SELECT id FROM docs WHERE MATCH(body) AGAINST ('\"database concurrency\" @1' IN BOOLEAN MODE) ORDER BY id"))
                  []
                  "a distance of one excludes adjacent distinct words"

              Expect.equal
                  (ids (run store "SELECT id FROM docs WHERE MATCH(body) AGAINST ('\"database concurrency\" @2' IN BOOLEAN MODE) ORDER BY id"))
                  [ "1" ]
                  "adjacent words span one position"

              Expect.equal
                  (ids (run store "SELECT id FROM docs WHERE MATCH(body) AGAINST ('\"database concurrency\" @3' IN BOOLEAN MODE) ORDER BY id"))
                  [ "1"; "2"; "4" ]
                  "proximity ignores word order"

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
                      | 82 -> "needle needle beta"
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

              let pointIntersection =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT id FROM docs WHERE MATCH(body) AGAINST('needle') AND TOUCH(id) = id AND id = 37"

              Expect.equal (ids pointIntersection) [ "37" ] "the point lookup intersects the postings"
              Expect.equal calls 2 "only the metadata probe and shared candidate enter the residual pipeline"

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

              run store "CREATE TABLE owners (id INT PRIMARY KEY)" |> ignore
              run store "INSERT INTO owners VALUES (37), (82)" |> ignore
              calls <- 0

              let joinedPointIntersection =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT d.id FROM docs d JOIN owners o ON o.id = d.id WHERE MATCH(d.body) AGAINST('needle') AND TOUCH(d.id) = d.id AND d.id = 37"

              Expect.equal (ids joinedPointIntersection) [ "37" ] "the joined point lookup retains the matching row"
              Expect.equal calls 2 "the joined full-text plan scores only the shared physical candidate"

              calls <- 0

              let limitedJoinedSearch =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT d.id, TOUCH(o.id) FROM docs d JOIN owners o ON o.id = d.id WHERE MATCH(d.body) AGAINST('needle') LIMIT 1"

              match limitedJoinedSearch with
              | ResultSet(_, [ [ Some id; Some touched ] ]) ->
                  Expect.equal id "82" "the unique join preserves relevance order"
                  Expect.equal id touched "the unique join retains the full-text row"
              | other -> failtestf "expected one joined full-text row, got %A" other

              Expect.equal calls 2 "the relevance-ordered unique join stops after the requested row"

              calls <- 0

              let reversedJoinedSearch =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT d.id, TOUCH(o.id) FROM owners o JOIN docs d ON d.id = o.id WHERE MATCH(d.body) AGAINST('needle') AND TOUCH(o.id) = d.id LIMIT 1"

              match reversedJoinedSearch with
              | ResultSet(_, [ [ Some id; Some touched ] ]) ->
                  Expect.equal id "82" "the full-text source drives a reorderable inner join"
                  Expect.equal id touched "the reordered join retains the written projection"
              | other -> failtestf "expected one reordered full-text row, got %A" other

              Expect.equal calls 4 "the reordered unique join evaluates only the surviving row"
              let reorderedCalls = calls

              for query in
                  [ "SELECT STRAIGHT_JOIN d.id, TOUCH(o.id) FROM owners o JOIN docs d ON d.id = o.id WHERE MATCH(d.body) AGAINST('needle') AND TOUCH(o.id) = d.id LIMIT 1"
                    "SELECT d.id, TOUCH(o.id) FROM owners o STRAIGHT_JOIN docs d ON d.id = o.id WHERE MATCH(d.body) AGAINST('needle') AND TOUCH(o.id) = d.id LIMIT 1" ] do
                  calls <- 0
                  let pinnedJoinedSearch = TestSupport.Sql.execute store registry query
                  Expect.equal (ids pinnedJoinedSearch) [ "82" ] "STRAIGHT_JOIN preserves the result"
                  Expect.isGreaterThan calls reorderedCalls "STRAIGHT_JOIN retains the written source order"

              run store "DELETE FROM owners WHERE id = 82" |> ignore
              calls <- 0

              let limitedJoinedSurvivor =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT d.id, TOUCH(o.id) FROM docs d JOIN owners o ON o.id = d.id WHERE MATCH(d.body) AGAINST('needle') LIMIT 1"

              match limitedJoinedSurvivor with
              | ResultSet(_, [ [ Some id; Some touched ] ]) ->
                  Expect.equal id "37" "the stream continues past an unmatched higher score"
                  Expect.equal id touched "the surviving unique join retains its row"
              | other -> failtestf "expected the next joined full-text row, got %A" other

              Expect.equal calls 2 "an unmatched score is skipped before projection"

              run store "CREATE TABLE owner_duplicates (id INT)" |> ignore
              run store "INSERT INTO owner_duplicates VALUES (37), (37), (82)" |> ignore
              calls <- 0

              let limitedOneToMany =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT d.id, TOUCH(o.id) FROM docs d JOIN owner_duplicates o ON o.id = d.id WHERE MATCH(d.body) AGAINST('needle') LIMIT 1"

              match limitedOneToMany with
              | ResultSet(_, [ _ ]) -> ()
              | other -> failtestf "expected one limited one-to-many row, got %A" other

              Expect.equal calls 4 "a one-to-many join evaluates every candidate before the relevance limit"

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
                "CREATE TABLE notes (article_id INT, body TEXT, KEY ix_article_id (article_id), FULLTEXT(body))"
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

              let mutable calls = 0
              let registry =
                  builtins
                  |> registerScalar "TOUCH" (fun values ->
                      calls <- calls + 1
                      values |> List.tryHead |> Option.defaultValue VNull)

              let joinedSourceIntersection =
                  TestSupport.Sql.execute
                      store
                      registry
                      "SELECT a.id FROM articles a JOIN notes n ON n.article_id = a.id WHERE MATCH(n.body) AGAINST('note') AND TOUCH(n.article_id) = n.article_id AND n.article_id = 2"

              Expect.equal (ids joinedSourceIntersection) [ "2" ] "the joined source retains its matching point candidate"
              Expect.equal calls 2 "the joined source scores only the shared physical candidate"

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
