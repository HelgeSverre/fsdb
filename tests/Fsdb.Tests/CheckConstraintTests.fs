module Fsdb.Tests.CheckConstraintTests

open Expecto
open Fsdb.Executor

let private run store sql =
    match Fsdb.Parser.parse sql with
    | Ok statement -> execute store Fsdb.Functions.builtins Fsdb.Storage.defaultDatabase (0L, 0L) false statement |> snd
    | Error message -> failtestf "parse failed for %s: %s" sql message

let private expectOk result context =
    match result with
    | Err(code, message) -> failtestf "%s failed (%d): %s" context code message
    | _ -> ()

let private expectError code result context =
    match result with
    | Err(actual, _) -> Expect.equal actual code context
    | other -> failtestf "%s: expected error %d, got %A" context code other

let private rows store sql =
    match run store sql with
    | ResultSet(_, result) -> result
    | other -> failtestf "expected rows from %s, got %A" sql other

let tests =
    testList
        "check constraints"
        [ testCase "table and column checks are named, exposed, and enforced"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              expectOk
                  (run store "CREATE TABLE amounts (amount INT CHECK (amount >= 0), tax INT, CONSTRAINT tax_limit CHECK (tax <= amount) NOT ENFORCED)")
                  "create"

              Expect.equal
                  (rows store "SELECT CONSTRAINT_NAME, ENFORCED FROM information_schema.TABLE_CONSTRAINTS WHERE TABLE_NAME = 'amounts' AND CONSTRAINT_TYPE = 'CHECK' ORDER BY CONSTRAINT_NAME")
                  [ [ Some "amounts_chk_1"; Some "YES" ]; [ Some "tax_limit"; Some "NO" ] ]
                  "constraint metadata"

              expectOk (run store "INSERT INTO amounts VALUES (1, 9)") "NOT ENFORCED passes"
              expectError 3819 (run store "INSERT INTO amounts VALUES (-1, 0)") "column check"

              match Fsdb.InformationSchema.showCreateTable store.Catalog Fsdb.Storage.defaultDatabase "amounts" with
              | Ok(_, [ [ _; Some ddl ] ]) ->
                  Expect.stringContains ddl "CONSTRAINT `amounts_chk_1` CHECK" "generated constraint"
                  Expect.stringContains ddl "NOT ENFORCED" "enforcement state"
              | other -> failtestf "unexpected SHOW CREATE result: %A" other

          testCase "NULL passes and a failing multi-row insert is atomic"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE t (n INT, CONSTRAINT positive CHECK (n > 0))") "create"
              expectOk (run store "INSERT INTO t VALUES (NULL)") "unknown passes"
              expectError 3819 (run store "INSERT INTO t VALUES (1), (-1), (2)") "batch violation"
              Expect.equal (rows store "SELECT n FROM t") [ [ None ] ] "no prefix of the failed batch landed"

          testCase "INSERT IGNORE skips only violating rows"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE t (n INT CHECK (n > 0))") "create"
              Expect.equal (run store "INSERT IGNORE INTO t VALUES (1), (-1), (2)") (Affected 2UL) "accepted count"
              Expect.equal (rows store "SELECT n FROM t ORDER BY n") [ [ Some "1" ]; [ Some "2" ] ] "bad row skipped"

              expectOk (run store "CREATE TABLE ai (id INT AUTO_INCREMENT PRIMARY KEY, n INT CHECK (n > 0))") "auto table"
              expectOk (run store "INSERT IGNORE INTO ai (n) VALUES (1), (-1), (2)") "ignored auto batch"
              expectOk (run store "INSERT INTO ai (n) VALUES (3)") "next auto id"
              Expect.equal
                  (rows store "SELECT id, n FROM ai ORDER BY id")
                  [ [ Some "1"; Some "1" ]; [ Some "2"; Some "2" ]; [ Some "4"; Some "3" ] ]
                  "ignored candidates reserve the batch's auto-increment range"

          testCase "UPDATE IGNORE leaves violating rows unchanged"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE t (id INT PRIMARY KEY, n INT CHECK (n > 0))") "create"
              expectOk (run store "INSERT INTO t VALUES (1, 1), (2, 2)") "seed"
              Expect.equal (run store "UPDATE IGNORE t SET n = n - 2") (Affected 0UL) "violations become skipped updates"
              Expect.equal (rows store "SELECT n FROM t ORDER BY id") [ [ Some "1" ]; [ Some "2" ] ] "rows unchanged"

          testCase "UPDATE, duplicate-key update, REPLACE, and generated columns check final values"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              expectOk
                  (run store "CREATE TABLE t (id INT PRIMARY KEY, n INT, doubled INT AS (n * 2), CONSTRAINT bounded CHECK (doubled <= 10))")
                  "create"

              expectOk (run store "INSERT INTO t (id, n) VALUES (1, 4)") "insert"
              expectError 3819 (run store "UPDATE t SET n = 6 WHERE id = 1") "update"
              expectError 3819 (run store "INSERT INTO t (id, n) VALUES (1, 7) ON DUPLICATE KEY UPDATE n = VALUES(n)") "upsert"
              expectError 3819 (run store "REPLACE INTO t (id, n) VALUES (1, 8)") "replace"
              Expect.equal (rows store "SELECT n, doubled FROM t") [ [ Some "4"; Some "8" ] ] "failed writes preserve the row"

          testCase "ALTER ADD, DROP, and enforcement changes validate existing rows atomically"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE t (n INT)") "create"
              expectOk (run store "INSERT INTO t VALUES (-1)") "seed"
              expectOk (run store "ALTER TABLE t ADD CONSTRAINT positive CHECK (n > 0) NOT ENFORCED") "add disabled"
              expectError 3819 (run store "ALTER TABLE t ALTER CHECK positive ENFORCED") "existing row validation"
              expectOk (run store "UPDATE t SET n = 1") "disabled after failed enable"
              expectOk (run store "ALTER TABLE t ALTER CHECK positive ENFORCED") "enable"
              expectError 3819 (run store "INSERT INTO t VALUES (-2)") "enabled"
              expectOk (run store "ALTER TABLE t DROP CHECK positive") "drop"
              expectOk (run store "INSERT INTO t VALUES (-2)") "dropped"

          testCase "DDL rejects invalid dependencies and duplicate schema names"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectError 3813 (run store "CREATE TABLE bad (a INT CHECK (a > b), b INT)") "column check cross-reference"
              expectError 3814 (run store "CREATE TABLE bad (a INT, CHECK (a < NOW()))") "nondeterministic function"
              expectOk (run store "CREATE TABLE first_table (a INT, CONSTRAINT shared_name CHECK (a > 0))") "first name"
              expectError 3822 (run store "CREATE TABLE second_table (a INT, CONSTRAINT shared_name CHECK (a > 0))") "schema-wide duplicate"

          testCase "column evolution follows MySQL dependency rules"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE t (a INT CHECK (a > 0), b INT, CONSTRAINT both_columns CHECK (a < b))") "create"
              expectError 3959 (run store "ALTER TABLE t RENAME COLUMN a TO renamed") "rename referenced column"
              expectError 3959 (run store "ALTER TABLE t DROP COLUMN a") "table check blocks automatic drop"
              expectOk (run store "ALTER TABLE t DROP CHECK both_columns, DROP COLUMN a") "drop table check and checked column together"
              Expect.equal (rows store "SELECT * FROM information_schema.CHECK_CONSTRAINTS") [] "column-owned check drops with its column"

          testCase "foreign-key referential rewrites cannot bypass checks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE parent (id INT PRIMARY KEY)") "parent"

              expectError
                  3823
                  (run store "CREATE TABLE child (pid INT CHECK (pid > 0), CONSTRAINT child_fk FOREIGN KEY (pid) REFERENCES parent (id) ON DELETE SET NULL)")
                  "SET NULL conflict"

              expectOk
                  (run store "CREATE TABLE deleting_child (pid INT CHECK (pid > 0), CONSTRAINT deleting_fk FOREIGN KEY (pid) REFERENCES parent (id) ON DELETE CASCADE)")
                  "row-deleting cascade is safe"

          testCase "rename retargets metadata and drop removes it"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (run store "CREATE TABLE old_name (n INT CHECK (n > 0))") "create"
              expectOk (run store "RENAME TABLE old_name TO new_name") "rename"

              Expect.equal
                  (rows store "SELECT CONSTRAINT_NAME, TABLE_NAME FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_TYPE = 'CHECK'")
                  [ [ Some "new_name_chk_1"; Some "new_name" ] ]
                  "generated name and owner retargeted"

              expectError 3819 (run store "INSERT INTO new_name VALUES (-1)") "renamed constraint remains active"
              expectOk (run store "DROP TABLE new_name") "drop"
              Expect.equal (rows store "SELECT * FROM information_schema.CHECK_CONSTRAINTS") [] "metadata removed"

          testCase "ALTER column checks parse and constraints survive WAL reload"
          <| fun _ ->
              let directory = TestSupport.directory "check-constraint"

              try
                  let store = Fsdb.Storage.create ()
                  Fsdb.Persistence.attach directory store
                  expectOk (run store "CREATE TABLE t (id INT PRIMARY KEY)") "create"
                  expectOk (run store "ALTER TABLE t ADD COLUMN amount INT CHECK (amount >= 0)") "alter inline check"
                  expectOk (run store "INSERT INTO t VALUES (1, 4)") "seed"

                  let reloaded = Fsdb.Persistence.load directory
                  expectError 3819 (run reloaded "INSERT INTO t VALUES (2, -1)") "reloaded enforcement"
                  Expect.equal
                      (rows reloaded "SELECT CONSTRAINT_NAME FROM information_schema.CHECK_CONSTRAINTS")
                      [ [ Some "t_chk_1" ] ]
                      "reloaded metadata"
              finally
                  System.IO.Directory.Delete(directory, true) ]
