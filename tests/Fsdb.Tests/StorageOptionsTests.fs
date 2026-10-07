module Fsdb.Tests.StorageOptionsTests

open Expecto
open Fsdb
open Fsdb.Executor

let private entry name value : OptionFile.Entry =
    { Name = name; Value = value; Source = "test.cnf"; Line = 7 }

let private rows = function
    | ResultSet(_, rows) -> rows
    | result -> failtestf "expected rows, got %A" result

let tests =
    testList "storage options"
        [ testCase "custom stopword assignments validate source shape and report MySQL SQLSTATE"
          <| fun _ ->
              let store = Storage.create ()
              let mutable session = Session.create 1 store
              let run sql =
                  let next, result = QueryHandler.handle session sql
                  session <- next
                  result
              for sql in
                  [ "CREATE TABLE valid_words(value VARCHAR(30),extra INT)"
                    "CREATE TABLE upper_words(VALUE VARCHAR(30))"
                    "CREATE TABLE wrong_words(value TEXT)"
                    "CREATE TABLE second_words(id INT,value VARCHAR(30))"
                    "CREATE TEMPORARY TABLE temp_words(value VARCHAR(30))" ] do
                  Expect.equal (run sql) (Affected 0UL) "source setup"
              for assignment, code, state in
                  [ "SET SESSION innodb_ft_user_stopword_table=''", 1231, "42000"
                    "SET SESSION innodb_ft_user_stopword_table=123", 1232, "42000"
                    "SET SESSION innodb_ft_user_stopword_table='fsdb/missing'", 1231, "42000"
                    "SET SESSION innodb_ft_user_stopword_table='fsdb/upper_words'", 1231, "42000"
                    "SET SESSION innodb_ft_user_stopword_table='fsdb/wrong_words'", 1231, "42000"
                    "SET SESSION innodb_ft_user_stopword_table='fsdb/second_words'", 1231, "42000"
                    "SET SESSION innodb_ft_user_stopword_table='fsdb/temp_words'", 1231, "42000"
                    "SET SESSION innodb_ft_server_stopword_table=NULL", 1229, "HY000"
                    "SET SESSION innodb_ft_server_stopword_table=123", 1229, "HY000"
                    "SET SESSION innodb_ft_server_stopword_table='missing'", 1229, "HY000" ] do
                  match run assignment |> errorInfo with
                  | Some error ->
                      Expect.equal error.Code code assignment
                      Expect.equal error.State state "diagnostics and wire errors share the native state"
                  | None -> failtestf "expected rejected assignment: %s" assignment
              Expect.equal (run "SET SESSION innodb_ft_user_stopword_table='fsdb/valid_words'") (Affected 0UL) "extra columns after value are accepted"
              Expect.equal (run "SET SESSION innodb_ft_user_stopword_table=NULL") (Affected 0UL) "NULL restores fallback"
              Expect.equal (run "SET GLOBAL innodb_ft_server_stopword_table='fsdb/valid_words'") (Affected 0UL) "server source is global"
              Expect.equal (run "SHOW SESSION VARIABLES LIKE 'innodb_ft_%stopword_table'" |> rows)
                  [ [ Some "innodb_ft_server_stopword_table"; Some "fsdb/valid_words" ]
                    [ Some "innodb_ft_user_stopword_table"; Some "" ] ] "SHOW uses the global server value and empty text for NULL"


          testCase "custom stopword SQL sources reload for new writes while preserving historical postings"
          <| fun _ ->
              for checkpoint in [ false; true ] do
                  let dir = TestSupport.directory "fulltext-custom-source"
                  let connect store =
                      let mutable session = Session.create 1 store
                      fun sql ->
                          let next, result = QueryHandler.handle session sql
                          session <- next
                          match result with
                          | Err(code, message) -> failtestf "%s failed: %d %s" sql code message
                          | _ -> result
                  let store = Persistence.load dir
                  Persistence.attach dir store
                  let run = connect store
                  run "CREATE TABLE words(value VARCHAR(30))" |> ignore
                  run "INSERT INTO words VALUES('orchard')" |> ignore
                  run "SET SESSION innodb_ft_user_stopword_table='fsdb/words'" |> ignore
                  run "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body))" |> ignore
                  run "INSERT INTO docs VALUES(1,'orchard'),(2,'cobalt'),(3,'the'),(4,'zzzz')" |> ignore
                  run "DELETE FROM words" |> ignore
                  run "INSERT INTO words VALUES('cobalt')" |> ignore
                  let query term = $"SELECT id FROM docs WHERE MATCH(body) AGAINST('{term}' IN BOOLEAN MODE) ORDER BY id"
                  Expect.isEmpty (run (query "orchard") |> rows) "source edits leave loaded policies alone"
                  if checkpoint then Persistence.snapshotNow dir store
                  let recovered = Persistence.load dir
                  Persistence.attach dir recovered
                  let run = connect recovered
                  Expect.equal (run "SELECT @@SESSION.innodb_ft_user_stopword_table" |> rows) [ [ None ] ] "settings reset independently of remembered index sources"
                  run "ALTER TABLE docs RENAME INDEX ft TO renamed" |> ignore
                  run "INSERT INTO docs VALUES(5,'orchard cobalt the')" |> ignore
                  Expect.equal (run (query "orchard") |> rows) [ [ Some "5" ] ] "new writes use the reloaded source"
                  Expect.equal (run (query "cobalt") |> rows) [ [ Some "2" ] ] "old postings remain searchable"
                  run "DROP TABLE words" |> ignore
                  if checkpoint then Persistence.snapshotNow dir recovered
                  let fallback = Persistence.load dir
                  Persistence.attach dir fallback
                  let run = connect fallback
                  run "INSERT INTO docs VALUES(6,'orchard cobalt the')" |> ignore
                  Expect.equal (run (query "orchard") |> rows) [ [ Some "5" ]; [ Some "6" ] ] "missing source falls back for future writes"
                  Expect.equal (run (query "cobalt") |> rows) [ [ Some "2" ]; [ Some "6" ] ] "fallback preserves the prior posting history"
                  Expect.equal (run (query "the") |> rows) [ [ Some "3" ]; [ Some "5" ] ] "built-in filtering resumes only for future writes"
                  let final = connect (Persistence.load dir)
                  Expect.equal (final (query "the") |> rows) [ [ Some "3" ]; [ Some "5" ] ] "the complete history survives another WAL replay"

          testCase "adding a full-text index captures the reloaded stopword policy for recovery"
          <| fun _ ->
              for checkpoint in [ false; true ] do
                  let dir = TestSupport.directory "fulltext-alter-source"
                  let db = Db.create () |> Db.withDataDir dir
                  let connection = Db.connect db
                  for sql in
                      [ "CREATE TABLE words(value VARCHAR(30))"
                        "INSERT INTO words VALUES('orchard')"
                        "SET SESSION innodb_ft_user_stopword_table='fsdb/words'"
                        "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,other TEXT,FULLTEXT KEY ft(body))"
                        "INSERT INTO docs VALUES(1,'orchard','orchard'),(2,'cobalt','cobalt'),(3,'the','the')"
                        "DELETE FROM words"
                        "INSERT INTO words VALUES('cobalt')" ] do
                      match connection.Query sql with
                      | Err(code, message) -> failtestf "%s failed: %d %s" sql code message
                      | _ -> ()
                  let recovered = Persistence.load dir
                  Persistence.attach dir recovered
                  if checkpoint then Persistence.snapshotNow dir recovered
                  let run = TestSupport.Sql.executeDefault recovered
                  run "SELECT id FROM docs WHERE MATCH(body) AGAINST('orchard')" |> ignore
                  Expect.equal (run "ALTER TABLE docs ADD FULLTEXT KEY ft_other(other)") (Affected 0UL) "add index after source reload"
                  let query term = $"SELECT id FROM docs WHERE MATCH(other) AGAINST('{term}') ORDER BY id"
                  let verify run =
                      Expect.equal (run (query "orchard") |> rows) [ [ Some "1" ] ] "the new index uses the reloaded list"
                      Expect.isEmpty (run (query "cobalt") |> rows) "the reloaded stopword is excluded"
                      Expect.equal (run (query "the") |> rows) [ [ Some "3" ] ] "custom lists replace built-ins"
                  verify run
                  verify (TestSupport.Sql.executeDefault (Persistence.load dir))

          testCase "a custom source missing at index creation is not remembered"
          <| fun _ ->
              let dir = TestSupport.directory "fulltext-missing-source"
              let db = Db.create () |> Db.withDataDir dir
              let connection = Db.connect db
              for sql in
                  [ "CREATE TABLE words(value VARCHAR(30))"
                    "SET SESSION innodb_ft_user_stopword_table='fsdb/words'"
                    "DROP TABLE words"
                    "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body))"
                    "INSERT INTO docs VALUES(1,'orchard the')"
                    "CREATE TABLE words(value VARCHAR(30))"
                    "INSERT INTO words VALUES('orchard')" ] do
                  match connection.Query sql with
                  | Err(code, message) -> failtestf "%s failed: %d %s" sql code message
                  | _ -> ()
              let recovered = Persistence.load dir
              let index = recovered.Catalog.[Storage.defaultDatabase].["docs"].FullTextIndexes.["ft"]
              Expect.equal (FullText.stopwordSource index) None "only a successfully loaded source is remembered"
              let run = TestSupport.Sql.executeDefault recovered
              run "INSERT INTO docs VALUES(2,'orchard the')" |> ignore
              Expect.equal (run "SELECT id FROM docs WHERE MATCH(body) AGAINST('orchard') ORDER BY id" |> rows)
                  [ [ Some "1" ]; [ Some "2" ] ] "recreated source does not affect the index"
              Expect.isEmpty (run "SELECT id FROM docs WHERE MATCH(body) AGAINST('the')" |> rows) "built-in filtering remains active"

          testCase "WAL captures loaded stopword policies for ordinary writes and transactions"
          <| fun _ ->
              for checkpoint in [ false; true ] do
                  let dir = TestSupport.directory "fulltext-wal-policy-history"
                  let store = Persistence.load dir
                  Persistence.attach dir store
                  let mutable session = Session.create 1 store
                  let run sql =
                      let next, result = QueryHandler.handle session sql
                      session <- next
                      match result with
                      | Err(code, message) -> failtestf "%s failed: %d %s" sql code message
                      | _ -> result
                  let setPolicy (store: Storage.Store) policy =
                      let database = store.Catalog.[Storage.defaultDatabase]
                      let table = database.["docs"]
                      let indexes = table.FullTextIndexes |> Map.map (fun _ index ->
                          FullText.withIndexingRules { FullText.activeRules index with Stopwords = policy } index)
                      Storage.setCatalog store (store.Catalog |> Map.add Storage.defaultDatabase (database |> Map.add "docs" { table with FullTextIndexes = indexes }))
                  run "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body))" |> ignore
                  setPolicy store (FullText.customStopwords Collation.defaultCollation [ "orchard" ])
                  run "INSERT INTO docs VALUES(1,'orchard cobalt the')" |> ignore
                  if checkpoint then Persistence.snapshotNow dir store
                  setPolicy store (FullText.customStopwords Collation.defaultCollation [ "cobalt" ])
                  run "START TRANSACTION" |> ignore
                  run "INSERT INTO docs VALUES(2,'orchard cobalt the')" |> ignore
                  run "COMMIT" |> ignore
                  setPolicy store FullText.StopwordPolicy.BuiltIn
                  run "INSERT INTO docs VALUES(3,'orchard cobalt the')" |> ignore
                  let recovered = Persistence.load dir
                  let query term = $"SELECT id FROM docs WHERE MATCH(body) AGAINST('{term}') ORDER BY id"
                  for term, expected in
                      [ "orchard", [ [ Some "2" ]; [ Some "3" ] ]
                        "cobalt", [ [ Some "1" ]; [ Some "3" ] ]
                        "the", [ [ Some "1" ]; [ Some "2" ] ] ] do
                      Expect.equal (TestSupport.Sql.executeDefault recovered (query term) |> rows) expected "each write retains the list loaded at that time"
                  Persistence.attach dir recovered
                  let table = recovered.Catalog.[Storage.defaultDatabase].["docs"]
                  Expect.equal (FullText.activeStopwords table.FullTextIndexes.["ft"]) FullText.StopwordPolicy.BuiltIn "the fallback policy is captured too"
                  setPolicy recovered (FullText.customStopwords Collation.defaultCollation [ "cobalt" ])
                  TestSupport.Sql.executeDefault recovered "UPDATE docs SET body='orchard cobalt the violet' WHERE id=1" |> ignore
                  let updated = Persistence.load dir
                  Expect.equal (TestSupport.Sql.executeDefault updated (query "the") |> rows) [ [ Some "1" ]; [ Some "2" ] ] "updates capture a newly loaded custom policy"
                  Expect.equal (TestSupport.Sql.executeDefault updated (query "cobalt") |> rows) [ [ Some "3" ] ] "replay removes the replaced document's old posting"

          testCase "stopword startup values follow MySQL boolean parsing"
          <| fun _ ->
              for value, enabled in
                  [ None, true; Some "ON", true; Some "TrUe", true; Some "1", true
                    Some "OFF", false; Some "false", false; Some "0", false
                    Some "2", false; Some "-1", false; Some "", false
                    Some "yes", false; Some "01", false; Some " ON ", false ] do
                  Expect.equal
                      (StorageOptions.fromEntries [ entry "innodb-ft-enable-stopword" value ])
                      (Ok({ StorageOptions.defaults with FullTextStopwordsEnabled = enabled }, []))
                      (sprintf "startup value %A" value)

          testCase "stopword startup aliases and precedence preserve other storage settings"
          <| fun _ ->
              for name, enabled in
                  [ "skip_innodb_ft_enable_stopword", false
                    "disable_innodb_ft_enable_stopword", false
                    "enable_innodb_ft_enable_stopword", true ] do
                  let other = entry "max_allowed_packet" (Some "64M")
                  let entries =
                      [ entry "innodb_ft_enable_stopword" (Some "OFF")
                        entry ("LOOSE-" + name) (Some "ON")
                        entry "ngram_token_size" (Some "3"); other ]
                  Expect.equal
                      (StorageOptions.fromEntries entries)
                      (Ok({ NgramTokenSize = 3; FullTextStopwordsEnabled = enabled }, [ other ]))
                      name

          testCase "startup stopword settings survive builder order without reinterpreting saved indexes"
          <| fun _ ->
              for beforePersistence in [ false; true ] do
                  let dir = TestSupport.directory "stopword-startup"
                  let openDb enabled =
                      if beforePersistence then
                          Db.create () |> Db.withFullTextStopwords enabled |> Db.withDataDir dir
                      else
                          Db.create () |> Db.withDataDir dir |> Db.withFullTextStopwords enabled
                  let initial = openDb false
                  let connection = Db.connect initial
                  Expect.equal (connection.Query "SELECT @@GLOBAL.innodb_ft_enable_stopword,@@SESSION.innodb_ft_enable_stopword" |> rows) [ [ Some "0"; Some "0" ] ] "startup seeds both scopes"
                  connection.Query "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body))" |> ignore
                  connection.Query "INSERT INTO docs VALUES(1,'the'),(2,'zzzz')" |> ignore
                  Persistence.snapshotNow dir initial.Store
                  let recovered = openDb true |> Db.connect
                  let query = "SELECT id FROM docs WHERE MATCH(body) AGAINST('the' IN BOOLEAN MODE) ORDER BY id"
                  Expect.equal (recovered.Query "SELECT @@GLOBAL.innodb_ft_enable_stopword,@@SESSION.innodb_ft_enable_stopword" |> rows) [ [ Some "1"; Some "1" ] ] "restart uses current startup setting"
                  Expect.equal (recovered.Query query |> rows) [ [ Some "1" ] ] "loaded postings keep old policy"
                  recovered.Query "ALTER TABLE docs ENGINE=InnoDB" |> ignore
                  Expect.isEmpty (recovered.Query query |> rows) "rebuild adopts new startup setting"
                  Expect.equal (Db.create () |> Db.connect |> fun fresh -> fresh.Query "SELECT @@innodb_ft_enable_stopword" |> rows) [ [ Some "1" ] ] "other stores retain default"

          testCase "stopword settings survive WAL and snapshot recovery and follow index rebuilds"
          <| fun _ ->
              for checkpoint in [ false; true ] do
                  for parser, term, size in [ "", "the", 2; " WITH PARSER ngram", "ab", 2; " WITH PARSER ngram", "abc", 3 ] do
                      let dir = TestSupport.directory "stopword-setting"
                      let openDb () = Db.create () |> Db.withNgramTokenSize size |> Db.withDataDir dir
                      let initial = openDb ()
                      let connection = Db.connect initial
                      Expect.equal (connection.Query "SET SESSION innodb_ft_enable_stopword=OFF") (Affected 0UL) "disable stopwords"
                      Expect.equal (connection.Query ("CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body)" + parser + ")")) (Affected 0UL) "create disabled index"
                      connection.Query ($"INSERT INTO docs VALUES(1,'{term}'),(2,'zzzz')") |> ignore
                      connection.Query "SET SESSION innodb_ft_enable_stopword=ON" |> ignore
                      connection.Query ($"INSERT INTO docs VALUES(3,'{term}')") |> ignore
                      let query = $"SELECT id FROM docs WHERE MATCH(body) AGAINST('{term}' IN BOOLEAN MODE) ORDER BY id"
                      let matches = [ [ Some "1" ]; [ Some "3" ] ]
                      Expect.equal (connection.Query query |> rows) matches "later session setting does not change index policy"
                      if checkpoint then Persistence.snapshotNow dir initial.Store
                      let recovered = openDb ()
                      let connection = Db.connect recovered
                      Expect.equal (connection.Query query |> rows) matches "saved index policy survives recovery"
                      connection.Query ($"ALTER TABLE docs DROP INDEX ft,ADD FULLTEXT KEY ft(body){parser}") |> ignore
                      Expect.equal (connection.Query query |> rows) matches "combined drop/add retains policy"
                      connection.Query "ALTER TABLE docs DROP INDEX ft" |> ignore
                      connection.Query ($"ALTER TABLE docs ADD FULLTEXT KEY ft(body){parser}") |> ignore
                      Expect.isEmpty (connection.Query query |> rows) "separate drop/add adopts current policy"
                      connection.Query "SET SESSION innodb_ft_enable_stopword=OFF" |> ignore
                      connection.Query "ALTER TABLE docs ENGINE=InnoDB" |> ignore
                      Expect.equal (connection.Query query |> rows) matches "physical rebuild adopts disabled policy"
                      let reopened = openDb () |> Db.connect
                      Expect.equal (reopened.Query query |> rows) matches "rebuild policy survives replay"

          testCase "new fulltext indexes inherit existing table stopword policy"
          <| fun _ ->
              for initial, current, expected in [ "ON", "OFF", []; "OFF", "ON", [ [ Some "1" ] ] ] do
                  let db = Db.create ()
                  let connection = Db.connect db
                  connection.Query ($"SET SESSION innodb_ft_enable_stopword={initial}") |> ignore
                  connection.Query "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,other TEXT,FULLTEXT KEY first_ft(body))" |> ignore
                  connection.Query "INSERT INTO docs VALUES(1,'the','the'),(2,'zzzz','zzzz')" |> ignore
                  connection.Query ($"SET SESSION innodb_ft_enable_stopword={current}") |> ignore
                  connection.Query "ALTER TABLE docs ADD FULLTEXT KEY second_ft(other)" |> ignore
                  for column in [ "body"; "other" ] do
                      Expect.equal
                          (connection.Query ($"SELECT id FROM docs WHERE MATCH({column}) AGAINST('the' IN BOOLEAN MODE) ORDER BY id") |> rows)
                          expected
                          "adding an index does not refresh a pre-existing fulltext table policy"

          testCase "global stopword settings seed new sessions without changing existing ones"
          <| fun _ ->
              let db = Db.create ()
              let original = Db.connect db
              Expect.equal (original.Query "SET GLOBAL innodb_ft_enable_stopword=OFF") (Affected 0UL) "global assignment"
              let newer = Db.connect db
              Expect.equal (original.Query "SELECT @@SESSION.innodb_ft_enable_stopword,@@GLOBAL.innodb_ft_enable_stopword" |> rows) [ [ Some "1"; Some "0" ] ] "existing session retains its setting"
              Expect.equal (newer.Query "SELECT @@SESSION.innodb_ft_enable_stopword" |> rows) [ [ Some "0" ] ] "new session inherits OFF"
              match newer.Query "SET SESSION innodb_ft_enable_stopword=2" with
              | Err(1231, _) -> ()
              | result -> failtestf "invalid boolean setting: %A" result

          testCase "metadata ALTER preserves each document's indexing rules"
          <| fun _ ->
              let store = Storage.create ()
              let run = TestSupport.Sql.executeDefault store
              run "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run "INSERT INTO docs VALUES(1,'ab'),(2,'ab')" |> ignore
              let database = store.Catalog.[Storage.defaultDatabase]
              let table = database.["docs"]
              let filtered: FullText.IndexingRules =
                  { Tokenizer = FullText.Ngrams 2; Stopwords = FullText.StopwordPolicy.BuiltIn }
              let documents =
                  table.RowsArray.Indexed
                  |> Seq.mapi (fun position (id, _) ->
                      let rules = if position = 0 then { filtered with Stopwords = FullText.StopwordPolicy.Disabled } else filtered
                      id, rules, [ "ab" ])
              let index = FullText.buildIndexWithDocumentSettings filtered Collation.defaultCollation documents
              Storage.setCatalog store (store.Catalog |> Map.add Storage.defaultDatabase (database |> Map.add "docs" { table with FullTextIndexes = Map.ofList [ "ft", index ] }))
              let query = "SELECT id FROM docs WHERE MATCH(body) AGAINST('ab') ORDER BY id"
              Expect.equal (run query |> rows) [ [ Some "1" ] ] "initial historical posting"
              Expect.equal (run "ALTER TABLE docs RENAME INDEX ft TO renamed") (Affected 0UL) "metadata rename"
              Expect.equal (run query |> rows) [ [ Some "1" ] ] "rename retains historical filtering"
              run "INSERT INTO docs VALUES(3,'ab')" |> ignore
              Expect.equal (run query |> rows) [ [ Some "1" ] ] "future writes retain the index's active filtering"
              run "ALTER TABLE docs ENGINE=InnoDB" |> ignore
              Expect.isEmpty (run query |> rows) "physical rebuild replaces all historical rules"

          testCase "snapshots preserve custom stopword lists and each document's loaded policy"
          <| fun _ ->
              let dir = TestSupport.directory "fulltext-custom-history"
              let store = Storage.create ()
              let run = TestSupport.Sql.executeDefault store
              run "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body))" |> ignore
              run "INSERT INTO docs VALUES(1,'orchard cobalt the'),(2,'orchard cobalt the'),(3,'zzzz')" |> ignore
              let database = store.Catalog.[Storage.defaultDatabase]
              let table = database.["docs"]
              let oldRules: FullText.IndexingRules =
                  { Tokenizer = FullText.Words
                    Stopwords = FullText.customStopwords Collation.defaultCollation [ "orchard" ] }
              let currentRules =
                  { oldRules with Stopwords = FullText.customStopwords (Collation.tryFind "utf8mb4_bin" |> Option.get) [ "cobalt" ] }
              let documents =
                  table.RowsArray.Indexed
                  |> Seq.mapi (fun position (id, _) ->
                      id, (if position = 0 then oldRules else currentRules), [ if position < 2 then "orchard cobalt the" else "zzzz" ])
                  |> Seq.toList
              let index = FullText.buildIndexWithDocumentSettings currentRules Collation.defaultCollation documents
              Storage.setCatalog store (store.Catalog |> Map.add Storage.defaultDatabase (database |> Map.add "docs" { table with FullTextIndexes = Map.ofList [ "ft", index ] }))
              Persistence.snapshotNow dir store
              let recovered = Persistence.load dir
              let restored = recovered.Catalog.[Storage.defaultDatabase].["docs"].FullTextIndexes.["ft"]
              Expect.equal (FullText.activeRules restored) currentRules "active source collation and words survive"
              for id, rules, _ in documents do
                  Expect.equal (FullText.documentRules id restored) (Some rules) "historical lists survive independently"
              let run = TestSupport.Sql.executeDefault recovered
              run "INSERT INTO docs VALUES(4,'orchard cobalt the')" |> ignore
              for mode in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE" ] do
                  Expect.equal (run ($"SELECT id FROM docs WHERE MATCH(body) AGAINST('orchard' {mode}) ORDER BY id") |> rows)
                      [ [ Some "2" ]; [ Some "4" ] ] "future writes use the active list"
                  Expect.equal (run ($"SELECT id FROM docs WHERE MATCH(body) AGAINST('cobalt' {mode}) ORDER BY id") |> rows)
                      [ [ Some "1" ] ] "old postings remain searchable"
              run "DELETE FROM docs WHERE id=1" |> ignore
              Expect.isEmpty (run "SELECT id FROM docs WHERE MATCH(body) AGAINST('cobalt')" |> rows) "removal uses the captured custom list"

          testCase "snapshots preserve mixed historical indexing rules within one fulltext index"
          <| fun _ ->
              let dir = TestSupport.directory "fulltext-rule-history"
              let store = Storage.create ()
              let run = TestSupport.Sql.executeDefault store
              run "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run "INSERT INTO docs VALUES(1,'ab'),(2,'ab'),(3,'abc'),(4,'zz')" |> ignore
              let database = store.Catalog.[Storage.defaultDatabase]
              let table = database.["docs"]
              let filtered: FullText.IndexingRules =
                  { Tokenizer = FullText.Ngrams 2; Stopwords = FullText.StopwordPolicy.BuiltIn }
              let unfiltered = { filtered with Stopwords = FullText.StopwordPolicy.Disabled }
              let histories = [| unfiltered, "ab"; filtered, "ab"; { unfiltered with Tokenizer = FullText.Ngrams 3 }, "abc"; filtered, "zz" |]
              let documents =
                  table.RowsArray.Indexed
                  |> Seq.mapi (fun position (id, _) ->
                      let rules, text = histories.[position]
                      id, rules, [ text ])
                  |> Seq.toList
              let index = FullText.buildIndexWithDocumentSettings filtered Collation.defaultCollation documents
              Storage.setCatalog store (store.Catalog |> Map.add Storage.defaultDatabase (database |> Map.add "docs" { table with FullTextIndexes = Map.ofList [ "ft", index ] }))
              Persistence.snapshotNow dir store
              let recovered = Persistence.load dir
              let restored = recovered.Catalog.[Storage.defaultDatabase].["docs"].FullTextIndexes.["ft"]
              for id, rules, _ in documents do
                  Expect.equal (FullText.documentRules id restored) (Some rules) "each document retains its historical tokenizer and policy"
              Expect.equal (FullText.activeRules restored) filtered "active rules remain independent of document history"
              Expect.equal
                  (TestSupport.Sql.executeDefault recovered "SELECT id FROM docs WHERE MATCH(body) AGAINST('ab') ORDER BY id" |> rows)
                  [ [ Some "1" ] ]
                  "only the historically indexed bigram matches"
              let query = "SELECT id FROM docs WHERE MATCH(body) AGAINST('a*' IN BOOLEAN MODE) ORDER BY id"
              Expect.equal (TestSupport.Sql.executeDefault recovered query |> rows) [ [ Some "1" ]; [ Some "3" ] ] "prefixes survive with mixed document rules"
              TestSupport.Sql.executeDefault recovered "INSERT INTO docs VALUES(5,'ab')" |> ignore
              Expect.equal (TestSupport.Sql.executeDefault recovered query |> rows) [ [ Some "1" ]; [ Some "3" ] ] "future writes use active rules"

          testCase "snapshot word lookups retain historical postings after filtering changes"
          <| fun _ ->
              let dir = TestSupport.directory "fulltext-word-policy-history"
              let store = Storage.create ()
              let run = TestSupport.Sql.executeDefault store
              run "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,FULLTEXT KEY ft(body))" |> ignore
              run "INSERT INTO docs VALUES(1,'the'),(2,'the'),(3,'zzzz')" |> ignore
              let database = store.Catalog.[Storage.defaultDatabase]
              let table = database.["docs"]
              let filtered: FullText.IndexingRules =
                  { Tokenizer = FullText.Words; Stopwords = FullText.StopwordPolicy.BuiltIn }
              let documents =
                  table.RowsArray.Indexed
                  |> Seq.mapi (fun position (id, _) ->
                      let rules = if position = 0 then { filtered with Stopwords = FullText.StopwordPolicy.Disabled } else filtered
                      id, rules, [ if position < 2 then "the" else "zzzz" ])
              let index = FullText.buildIndexWithDocumentSettings filtered Collation.defaultCollation documents
              Storage.setCatalog store (store.Catalog |> Map.add Storage.defaultDatabase (database |> Map.add "docs" { table with FullTextIndexes = Map.ofList [ "ft", index ] }))
              Persistence.snapshotNow dir store
              let recovered = Persistence.load dir
              let run = TestSupport.Sql.executeDefault recovered
              run "INSERT INTO docs VALUES(4,'the')" |> ignore
              for mode in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE"; "WITH QUERY EXPANSION" ] do
                  Expect.equal
                      (run ($"SELECT id FROM docs WHERE MATCH(body) AGAINST('the' {mode}) ORDER BY id") |> rows)
                      [ [ Some "1" ] ]
                      "saved postings survive while newer filtered rows stay absent"
              run "DELETE FROM docs WHERE id=1" |> ignore
              Expect.isEmpty (run "SELECT id FROM docs WHERE MATCH(body) AGAINST('the')" |> rows) "removal respects historical document rules"

          testCase "snapshots preserve fulltext stopword policies for empty and populated indexes"
          <| fun _ ->
              let run = TestSupport.Sql.executeDefault
              for populated in [ false; true ] do
                  for wordPolicy, ngramPolicy in
                      [ FullText.StopwordPolicy.Disabled, FullText.StopwordPolicy.BuiltIn
                        FullText.StopwordPolicy.BuiltIn, FullText.StopwordPolicy.Disabled
                        FullText.customStopwords Collation.defaultCollation [], FullText.customStopwords Collation.defaultCollation [] ] do
                      let dir = TestSupport.directory "fulltext-stopwords"
                      let store = Storage.create ()
                      run store "CREATE TABLE docs(id INT PRIMARY KEY,body TEXT,other TEXT,FULLTEXT KEY z_word(body),FULLTEXT KEY a_ngram(other) WITH PARSER ngram)" |> ignore
                      if populated then run store "INSERT INTO docs VALUES(1,'the','ab')" |> ignore
                      let database = store.Catalog.[Storage.defaultDatabase]
                      let table = database.["docs"]
                      let policies = Map.ofList [ "z_word", wordPolicy; "a_ngram", ngramPolicy ]
                      let indexes = Storage.restoreFullTextIndexesWithRules policies Map.empty table
                      Storage.setCatalog store (store.Catalog |> Map.add Storage.defaultDatabase (database |> Map.add "docs" { table with FullTextIndexes = indexes }))
                      Persistence.snapshotNow dir store
                      let recovered = Persistence.load dir
                      let recoveredTable = recovered.Catalog.[Storage.defaultDatabase].["docs"]
                      for name, expected in Map.toList policies do
                          Expect.equal (FullText.activeStopwords recoveredTable.FullTextIndexes.[name]) expected "policy survives in index-definition order, even without rows"
                      Expect.equal (run recovered "INSERT INTO docs VALUES(2,'the','ab')") (Affected 1UL) "insert after recovery"
                      for column, term, policy in [ "body", "the", wordPolicy; "other", "ab", ngramPolicy ] do
                          let expected =
                              if policy = FullText.StopwordPolicy.BuiltIn then []
                              elif populated then [ [ Some "1" ]; [ Some "2" ] ]
                              else [ [ Some "2" ] ]
                          Expect.equal
                              (run recovered ($"SELECT id FROM docs WHERE MATCH({column}) AGAINST('{term}' IN BOOLEAN MODE) ORDER BY id") |> rows)
                              expected
                              "recovered postings and new writes use the saved policy"

          testCase "FSNG snapshots restore policies without a remembered source"
          <| fun _ ->
              let dir = TestSupport.directory "fulltext-fsng"
              // Produced by the format-16 writer with disabled stopwords and one ngram row.
              let snapshot = System.Convert.FromBase64String "RlNORwEAAAAEZnNkYgEAAAAKbGVnYWN5X2Z0cwpsZWdhY3lfZnRzAQAAAARib2R5CQEAAAAAAAESdXRmOG1iNF8wOTAwX2FpX2NpAAAAAAEAAAACZnQBAAAABwBOOmJvZHkAAQMFbmdyYW0AAAAAAAAAAApdD4ZWJN8IAQAAAAAAAAABAAAAAQAAAAEBAAAAAgEAAAAAAQAAAAQCYWIAAAAAAJ8AAAAAAAAAtz6uCg=="
              System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "snapshot.fsdb"), snapshot)
              let recovered = Persistence.load dir
              let index = recovered.Catalog.[Storage.defaultDatabase].["legacy_fts"].FullTextIndexes.["ft"]
              Expect.equal (FullText.stopwordSource index) None "legacy indexes have no source to reload"
              Expect.equal (FullText.activeStopwords index) FullText.StopwordPolicy.Disabled "legacy policy survives startup"
              Expect.equal
                  (TestSupport.Sql.executeDefault recovered "SELECT body FROM legacy_fts WHERE MATCH(body) AGAINST('ab' IN BOOLEAN MODE)" |> rows)
                  [ [ Some "ab" ] ] "legacy postings remain searchable"

          testCase "FSNF snapshots retain historical indexing rules"
          <| fun _ ->
              let dir = TestSupport.directory "fulltext-fsnf"
              // Produced by the format-15 writer with disabled stopwords and one ngram row.
              let snapshot = System.Convert.FromBase64String "RlNORgEAAAAEZnNkYgEAAAAKbGVnYWN5X2Z0cwpsZWdhY3lfZnRzAQAAAARib2R5CQEAAAAAAAESdXRmOG1iNF8wOTAwX2FpX2NpAAAAAAEAAAACZnQBAAAABwBOOmJvZHkAAQMFbmdyYW0AAAAAAAAAAOQvwmlPJN8IAQAAAAAAAAABAAAAAQAAAAEBAAAAAgEAAAAAAQAAAAQCYWIAAAAAAJ8AAAAAAAAAFWXOrg=="
              System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "snapshot.fsdb"), snapshot)
              let recovered = Persistence.load dir
              let index = recovered.Catalog.[Storage.defaultDatabase].["legacy_fts"].FullTextIndexes.["ft"]
              Expect.equal (FullText.activeStopwords index) FullText.StopwordPolicy.Disabled "legacy active policy"
              Expect.equal
                  (TestSupport.Sql.executeDefault recovered "SELECT body FROM legacy_fts WHERE MATCH(body) AGAINST('ab' IN BOOLEAN MODE)" |> rows)
                  [ [ Some "ab" ] ] "legacy document rule table remains readable"

          testCase "FSNE snapshots retain their index stopword policy for every document"
          <| fun _ ->
              let dir = TestSupport.directory "fulltext-fsne"
              // Produced by the format-14 writer with disabled stopwords and one ngram row.
              let snapshot = System.Convert.FromBase64String "RlNORQEAAAAEZnNkYgEAAAAKbGVnYWN5X2Z0cwpsZWdhY3lfZnRzAQAAAARib2R5CQEAAAAAAAESdXRmOG1iNF8wOTAwX2FpX2NpAAAAAAEAAAACZnQBAAAABwBOOmJvZHkAAQMFbmdyYW0AAAAAAAAAAAh3ID1JJN8IAQAAAAAAAAABAAAAAQAAAAEAAAAAAQAAAAQCYWICAAAAAJkAAAAAAAAAcNydBQ=="
              System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "snapshot.fsdb"), snapshot)
              let recovered = Persistence.load dir
              let table = recovered.Catalog.[Storage.defaultDatabase].["legacy_fts"]
              let index = table.FullTextIndexes.["ft"]
              Expect.equal (FullText.activeStopwords index) FullText.StopwordPolicy.Disabled "legacy active policy"
              for id, _ in table.RowsArray.Indexed do
                  Expect.equal (FullText.documentRules id index)
                      (Some { Tokenizer = FullText.Ngrams 2; Stopwords = FullText.StopwordPolicy.Disabled })
                      "legacy index policy supplies document policy"
              Expect.equal
                  (TestSupport.Sql.executeDefault recovered "SELECT body FROM legacy_fts WHERE MATCH(body) AGAINST('ab' IN BOOLEAN MODE)" |> rows)
                  [ [ Some "ab" ] ]
                  "legacy stopped word remains indexed"

          testCase "snapshots reject missing and out-of-range fulltext rule references"
          <| fun _ ->
              let dir = TestSupport.directory "fulltext-invalid-rule-reference"
              let store = Storage.create ()
              let run = TestSupport.Sql.executeDefault store
              run "CREATE TABLE docs(body TEXT,FULLTEXT KEY ft(body) WITH PARSER ngram)" |> ignore
              run "INSERT INTO docs VALUES('zz')" |> ignore
              let table = store.Catalog.[Storage.defaultDatabase].["docs"]
              Storage.setCatalog store (Map.ofList [ Storage.defaultDatabase, Map.ofList [ "docs", table ] ])
              Persistence.snapshotNow dir store
              let path = System.IO.Path.Combine(dir, "snapshot.fsdb")
              let bytes = System.IO.File.ReadAllBytes path
              Expect.equal (System.Text.Encoding.ASCII.GetString(bytes, 0, 4)) "FSNH" "rule references use the current snapshot format"
              let trailerSize, preparedCountSize = 12, 4
              let lastRuleReference = bytes.Length - trailerSize - preparedCountSize - 1
              for invalid in [ 250uy; 251uy ] do
                  bytes.[lastRuleReference] <- invalid
                  let checksum = Binary.Writer()
                  checksum.WriteUInt32LE(Binary.crc32 bytes.[4 .. bytes.Length - trailerSize - 1])
                  System.Array.Copy(checksum.ToArray(), 0, bytes, bytes.Length - 4, 4)
                  System.IO.File.WriteAllBytes(path, bytes)
                  Expect.throws (fun () -> Persistence.load dir |> ignore) "a valid checksum cannot legitimize an invalid rule reference"

          testCase "FSND fulltext snapshots default to built-in stopwords"
          <| fun _ ->
              let dir = TestSupport.directory "fulltext-fsnd"
              // Produced by the format-13 writer: one ngram row and no stopword metadata.
              let snapshot = System.Convert.FromBase64String "RlNORAEAAAAEZnNkYgEAAAAKbGVnYWN5X2Z0cwpsZWdhY3lfZnRzAQAAAARib2R5CQEAAAAAAAESdXRmOG1iNF8wOTAwX2FpX2NpAAAAAAEAAAACZnQBAAAABwBOOmJvZHkAAQMFbmdyYW0AAAAAAAAAABId5NVBJN8IAQAAAAAAAAABAAAAAQAAAAAAAAABAAAABAznlJ/ml6Xlv6vkuZACAAAAAKIAAAAAAAAAEAsCZA=="
              System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "snapshot.fsdb"), snapshot)
              let recovered = Persistence.load dir
              let index = recovered.Catalog.[Storage.defaultDatabase].["legacy_fts"].FullTextIndexes.["ft"]
              Expect.equal (FullText.activeStopwords index) FullText.StopwordPolicy.BuiltIn "legacy policy"
              Expect.equal
                  (TestSupport.Sql.executeDefault recovered "SELECT body FROM legacy_fts WHERE MATCH(body) AGAINST('生日' IN BOOLEAN MODE)" |> rows)
                  [ [ Some "生日快乐" ] ]
                  "historical tokenizer data still decodes"

          testCase "ngram startup values follow MySQL integer parsing and bounds"
          <| fun _ ->
              Expect.equal (StorageOptions.fromEntries []) (Ok(StorageOptions.defaults, [])) "default size"
              for value, expected in [ "-1", 1; "0", 1; "1", 1; "2", 2; "3", 3; "10", 10; "11", 10; "3k", 10; "4294967296", 10 ] do
                  Expect.equal
                      (StorageOptions.fromEntries [ entry "ngram-token-size" (Some value) ])
                      (Ok({ StorageOptions.defaults with NgramTokenSize = expected }, []))
                      value

          testCase "ngram startup rejects missing and malformed integers with source location"
          <| fun _ ->
              for value in [ None; Some ""; Some "abc"; Some "3.0"; Some "64MB"; Some "9223372036854775807K" ] do
                  match StorageOptions.fromEntries [ entry "ngram_token_size" value; entry "ngram_token_size" (Some "3") ] with
                  | Error message -> Expect.stringContains message "test.cnf:7" "error identifies its source even if overridden"
                  | Ok _ -> failtestf "accepted %A" value

          testCase "ngram startup takes the last setting and preserves unrelated entries"
          <| fun _ ->
              let other = entry "max_allowed_packet" (Some "64M")
              Expect.equal
                  (StorageOptions.fromEntries [ entry "ngram_token_size" (Some "1"); other; entry "LOOSE-NGRAM-TOKEN-SIZE" (Some "3") ])
                  (Ok({ StorageOptions.defaults with NgramTokenSize = 3 }, [ other ]))
                  "normalized loose spelling and command-line precedence"

          testCase "ngram startup settings and reported variables are isolated per database"
          <| fun _ ->
              let two = Db.create () |> Db.withNgramTokenSize 2 |> Db.connect
              let three = Db.create () |> Db.withNgramTokenSize 3 |> Db.connect
              for connection, size, expected in [ two, "2", [ [ Some "1" ] ]; three, "3", [] ] do
                  Expect.equal (connection.Query "SELECT @@GLOBAL.ngram_token_size, @@ngram_token_size" |> rows) [ [ Some size; Some size ] ] "store-specific defaults"
                  Expect.equal (connection.Query "CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body) WITH PARSER ngram)") (Affected 0UL) "create"
                  Expect.equal (connection.Query "INSERT INTO docs VALUES(1,'生日快乐')") (Affected 1UL) "insert"
                  Expect.equal (connection.Query "SELECT id FROM docs WHERE MATCH(body) AGAINST('生日' IN BOOLEAN MODE)" |> rows) expected "write tokenizer"
                  match connection.Query "SET GLOBAL ngram_token_size=4" with
                  | Err(1238, _) -> ()
                  | result -> failtestf "expected read-only variable error, got %A" result
              Expect.equal (two.Query "SELECT @@ngram_token_size" |> rows) [ [ Some "2" ] ] "other store remains unchanged"

          testCase "ngram ALTER preserves postings for metadata and rebuilds for physical changes"
          <| fun _ ->
              let textCases =
                  [ "COMMENT='changed'", "body", "docs", false
                    "COMMENT='changed', ALGORITHM=COPY", "body", "docs", true
                    "RENAME INDEX ft TO renamed", "body", "docs", false
                    "DROP INDEX second, RENAME INDEX ft TO second", "body", "docs", false
                    "RENAME COLUMN body TO renamed_body", "renamed_body", "docs", false
                    "RENAME TO renamed_table", "body", "renamed_table", false
                    "ADD INDEX extra_id(extra)", "body", "docs", false
                    "ADD FULLTEXT KEY second(body) WITH PARSER ngram", "body", "docs", false
                    "ALTER INDEX existing INVISIBLE", "body", "docs", false
                    "ADD CONSTRAINT nonnegative CHECK(extra>=0)", "body", "docs", true
                    "DROP INDEX existing", "body", "docs", false
                    "ADD COLUMN added INT", "body", "docs", true
                    "DROP COLUMN extra", "body", "docs", true
                    "MODIFY body MEDIUMTEXT", "body", "docs", true
                    "MODIFY body TEXT", "body", "docs", false
                    "MODIFY body TEXT COMMENT 'changed'", "body", "docs", false
                    "CHANGE body renamed_body TEXT", "renamed_body", "docs", false
                    "MODIFY extra INT DEFAULT 4", "body", "docs", false
                    "MODIFY extra INT COMMENT 'changed'", "body", "docs", false
                    "MODIFY body TEXT NOT NULL", "body", "docs", true
                    "MODIFY body TEXT AFTER id", "body", "docs", true
                    "MODIFY body TEXT AFTER extra", "body", "docs", false
                    "MODIFY extra BIGINT", "body", "docs", true
                    "MODIFY body TEXT COLLATE utf8mb4_0900_as_cs", "body", "docs", true
                    "MODIFY body TEXT CHARACTER SET utf8mb4", "body", "docs", false
                    "ALTER COLUMN extra SET DEFAULT 3", "body", "docs", false
                    "AUTO_INCREMENT=20", "body", "docs", false
                    "ENGINE=InnoDB", "body", "docs", true
                    "ROW_FORMAT=DYNAMIC", "body", "docs", true
                    "DROP INDEX ft, ADD FULLTEXT KEY ft(body) WITH PARSER ngram", "body", "docs", false ]
              let cases =
                  (textCases |> List.map (fun (action, column, table, rebuilt) -> action, column, table, rebuilt, "TEXT"))
                  @ [ "MODIFY body VARCHAR(21)", "body", "docs", false, "VARCHAR(20)"
                      "MODIFY body VARCHAR(300)", "body", "docs", false, "VARCHAR(200)"
                      "MODIFY body VARCHAR(300)", "body", "docs", true, "VARCHAR(20)" ]
              for action, column, table, rebuilt, initialBodyType in cases do
                  let dir = TestSupport.directory "ngram-alter"
                  let initial = Db.create () |> Db.withDataDir dir
                  let connection = Db.connect initial
                  Expect.equal (connection.Query (sprintf "CREATE TABLE docs(id INT PRIMARY KEY AUTO_INCREMENT,extra INT DEFAULT 0,body %s,KEY existing(extra),FULLTEXT KEY ft(body) WITH PARSER ngram)" initialBodyType)) (Affected 0UL) "create"
                  Expect.equal (connection.Query "INSERT INTO docs(id,body) VALUES(1,'生日快乐')") (Affected 1UL) "historical write"
                  let changed = Db.create () |> Db.withDataDir dir |> Db.withNgramTokenSize 3
                  let connection = Db.connect changed
                  Expect.equal (connection.Query "INSERT INTO docs(id,body) VALUES(2,'生日快乐')") (Affected 1UL) "current write"
                  if action.StartsWith "DROP INDEX second" then
                      Expect.equal (connection.Query "ALTER TABLE docs ADD FULLTEXT KEY second(body) WITH PARSER ngram") (Affected 0UL) "new destination index"
                  Expect.equal (connection.Query ("ALTER TABLE docs " + action)) (Affected 0UL) action
                  let verify (connection: Db.Connection) =
                      for term, expected in
                          [ "生日", (if rebuilt then [] else [ [ Some "1" ] ])
                            "生日快", (if rebuilt then [ [ Some "1" ]; [ Some "2" ] ] else [ [ Some "2" ] ]) ] do
                          let sql = sprintf "SELECT id FROM %s WHERE MATCH(%s) AGAINST('%s' IN BOOLEAN MODE) ORDER BY id" table column term
                          Expect.equal (connection.Query sql |> rows) expected (action + ": " + term)
                  verify connection
                  let recovered = Db.create () |> Db.withDataDir dir |> Db.withNgramTokenSize 3
                  verify (Db.connect recovered)
                  Persistence.snapshotNow dir recovered.Store
                  let checkpointed = Db.create () |> Db.withDataDir dir |> Db.withNgramTokenSize 3 |> Db.connect
                  verify checkpointed
                  if action.StartsWith "ADD FULLTEXT" then
                      Expect.equal (checkpointed.Query "ALTER TABLE docs DROP INDEX ft") (Affected 0UL) "drop historical index"
                      Expect.equal
                          (checkpointed.Query "SELECT id FROM docs WHERE MATCH(body) AGAINST('生日快' IN BOOLEAN MODE) ORDER BY id" |> rows)
                          [ [ Some "1" ]; [ Some "2" ] ]
                          "new index tokenizes every row with the current size"

          testCase "ngram startup builder preserves historical postings through WAL and snapshots"
          <| fun _ ->
              for checkpoint in [ false; true ] do
                  for beforeDataDir in [ false; true ] do
                      let dir = TestSupport.directory "ngram-startup"
                      let openDb size =
                          if beforeDataDir then
                              Db.create () |> Db.withNgramTokenSize size |> Db.withDataDir dir
                          else
                              Db.create () |> Db.withDataDir dir |> Db.withNgramTokenSize size
                      let initial = openDb 2
                      let connection = Db.connect initial
                      Expect.equal (connection.Query "CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body) WITH PARSER ngram)") (Affected 0UL) "create"
                      Expect.equal (connection.Query "INSERT INTO docs VALUES(1,'生日快乐')") (Affected 1UL) "historical insert"
                      if checkpoint then Persistence.snapshotNow dir initial.Store
                      let changed = openDb 3
                      let connection = Db.connect changed
                      Expect.equal (connection.Query "SELECT @@ngram_token_size" |> rows) [ [ Some "3" ] ] "active setting survives builder order"
                      Expect.equal (connection.Query "INSERT INTO docs VALUES(2,'生日快乐')") (Affected 1UL) "current insert"
                      if checkpoint then Persistence.snapshotNow dir changed.Store
                      let recovered = openDb 3 |> Db.connect
                      for term, expected in [ "生日", "1"; "生日快", "2" ] do
                          let sql = sprintf "SELECT id FROM docs WHERE MATCH(body) AGAINST('%s' IN BOOLEAN MODE) ORDER BY id" term
                          Expect.equal (recovered.Query sql |> rows) [ [ Some expected ] ] "historical and current postings remain distinct" ]
