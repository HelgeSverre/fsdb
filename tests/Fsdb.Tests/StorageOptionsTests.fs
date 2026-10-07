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
