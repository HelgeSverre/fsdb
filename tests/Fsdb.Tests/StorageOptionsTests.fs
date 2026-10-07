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
        [ testCase "ngram startup values follow MySQL integer parsing and bounds"
          <| fun _ ->
              Expect.equal (StorageOptions.fromEntries []) (Ok(StorageOptions.defaults, [])) "default size"
              for value, expected in [ "-1", 1; "0", 1; "1", 1; "2", 2; "3", 3; "10", 10; "11", 10; "3k", 10; "4294967296", 10 ] do
                  Expect.equal
                      (StorageOptions.fromEntries [ entry "ngram-token-size" (Some value) ])
                      (Ok({ NgramTokenSize = expected }, []))
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
                  (Ok({ NgramTokenSize = 3 }, [ other ]))
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
              for action, column, table, rebuilt in
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
                    "ALTER COLUMN extra SET DEFAULT 3", "body", "docs", false
                    "AUTO_INCREMENT=20", "body", "docs", false
                    "ENGINE=InnoDB", "body", "docs", true
                    "ROW_FORMAT=DYNAMIC", "body", "docs", true
                    "DROP INDEX ft, ADD FULLTEXT KEY ft(body) WITH PARSER ngram", "body", "docs", false ] do
                  let dir = TestSupport.directory "ngram-alter"
                  let initial = Db.create () |> Db.withDataDir dir
                  let connection = Db.connect initial
                  Expect.equal (connection.Query "CREATE TABLE docs(id INT PRIMARY KEY AUTO_INCREMENT,extra INT DEFAULT 0,body TEXT,KEY existing(extra),FULLTEXT KEY ft(body) WITH PARSER ngram)") (Affected 0UL) "create"
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
