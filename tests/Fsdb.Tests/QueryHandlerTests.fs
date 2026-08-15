module Fsdb.Tests.QueryHandlerTests

open System
open Expecto
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let tests =
    testList
        "QueryHandler"
        [ testCase "SELECT 1 returns a single row with column name '1'"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT 1" |> snd with
              | ResultSet(cols, rows) ->
                  Expect.equal cols [ "1" ] "column name"
                  Expect.equal rows [ [ Some "1" ] ] "row value"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@version returns the server version"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@version" |> snd with
              | ResultSet(cols, [ [ Some v ] ]) ->
                  Expect.equal cols [ "@@version" ] "column name"
                  Expect.equal v ServerVersion "version value"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@version, @@version_comment returns both columns"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@version, @@version_comment" |> snd with
              | ResultSet(cols, [ row ]) ->
                  Expect.equal cols [ "@@version"; "@@version_comment" ] "columns"
                  Expect.equal (List.length row) 2 "row has two values"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SELECT @@unknown_var returns a 1193 unknown-system-variable error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @@totally_not_a_var" |> snd with
              | Err(1193, msg) -> Expect.stringContains msg "totally_not_a_var" "message names the variable"
              | other -> failtestf "expected a 1193 error, got %A" other

          testCase "the Connector/J connection probe (auto_increment_increment, transaction_isolation) resolves"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match
                  handle
                      session
                      "SELECT @@session.auto_increment_increment AS auto_increment_increment, @@character_set_client AS character_set_client, @@session.transaction_isolation AS transaction_isolation"
                  |> snd
              with
              | ResultSet(_, [ [ Some "1"; Some _; Some "REPEATABLE-READ" ] ]) -> ()
              | other -> failtestf "expected all three variables resolved, got %A" other

          testCase "SELECT @@version_comment LIMIT 1 tolerates the trailing LIMIT clause"
          <| fun _ ->
              // Regression: mysql CLI probes the connection banner with exactly
              // this query at connect time.
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "select @@version_comment limit 1" |> snd with
              | ResultSet([ "@@version_comment" ], [ [ Some _ ] ]) -> ()
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SET NAMES utf8mb4 returns OK"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET NAMES utf8mb4" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected OK, got %A" other

          testCase "SET NAMES updates character_set_client, reflected by SELECT @@character_set_client"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET NAMES latin1"

              match handle session "SELECT @@character_set_client" |> snd with
              | ResultSet(_, [ [ Some "latin1" ] ]) -> ()
              | other -> failtestf "expected latin1, got %A" other

          testCase "SET sql_mode = '...' updates the session variable, reflected by SELECT @@sql_mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode = 'ANSI_QUOTES'"

              match handle session "SELECT @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some "ANSI_QUOTES" ] ]) -> ()
              | other -> failtestf "expected ANSI_QUOTES, got %A" other

          testCase "SET @user_var = 1 is a loud 1193 error, not a silent fake OK"
          <| fun _ ->
              // Regression: `setVar`'s (\w+) can't match `@foo`, so this
              // used to fall through to handleSet's catch-all and report
              // `Affected 0UL` — the client believes the write landed, then
              // `SELECT @foo` is a 1064 syntax error right after.
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET @user_var = 1" |> snd with
              | Err(1193, msg) -> Expect.stringContains msg "@user_var" "the error names the unhandled variable"
              | other -> failtestf "expected a 1193 error, got %A" other

          testCase "SELECT DATABASE() returns NULL before USE"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ None ] ]) -> ()
              | other -> failtestf "expected a single NULL row, got %A" other

          testCase "USE sets the session database, reflected by SELECT DATABASE()"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE mydb"

              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ Some "mydb" ] ]) -> ()
              | other -> failtestf "expected mydb, got %A" other

          testCase "SHOW DATABASES returns a resultset"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW DATABASES" |> snd with
              | ResultSet([ "Database" ], _ :: _) -> ()
              | other -> failtestf "expected a non-empty resultset, got %A" other

          testCase "SHOW VARIABLES LIKE filters by pattern"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW VARIABLES LIKE 'autocommit'" |> snd with
              | ResultSet(_, [ [ Some "autocommit"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the autocommit row, got %A" other

          testCase "SHOW TABLES / SHOW FULL TABLES list the current database's tables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE widgets (id INT PRIMARY KEY)"

              match handle session "SHOW TABLES" |> snd with
              | ResultSet([ "Tables_in_shop" ], [ [ Some "widgets" ] ]) -> ()
              | other -> failtestf "expected the one table, got %A" other

              match handle session "SHOW FULL TABLES" |> snd with
              | ResultSet([ "Tables_in_shop"; "Table_type" ], [ [ Some "widgets"; Some "BASE TABLE" ] ]) -> ()
              | other -> failtestf "expected the FULL variant's extra column, got %A" other

          testCase "SHOW COLUMNS FROM t / DESCRIBE t report field metadata"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE shop"

              let session, _ =
                  handle session "CREATE TABLE widgets (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50) NOT NULL)"

              match handle session "SHOW COLUMNS FROM widgets" |> snd with
              | ResultSet([ "Field"; "Type"; "Null"; "Key"; "Default"; "Extra" ], rows) ->
                  Expect.equal
                      rows
                      [ [ Some "id"; Some "int"; Some "NO"; Some "PRI"; None; Some "auto_increment" ]
                        [ Some "name"; Some "varchar(50)"; Some "NO"; Some ""; None; Some "" ] ]
                      "both columns with their metadata"
              | other -> failtestf "expected a resultset, got %A" other

              match handle session "DESCRIBE widgets" |> snd with
              | ResultSet([ "Field"; "Type"; "Null"; "Key"; "Default"; "Extra" ], rows) -> Expect.equal (List.length rows) 2 "DESCRIBE is SHOW COLUMNS under another name"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SHOW CREATE TABLE reconstructs plausible DDL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE shop"

              let session, _ =
                  handle session "CREATE TABLE widgets (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50) UNIQUE)"

              match handle session "SHOW CREATE TABLE widgets" |> snd with
              | ResultSet([ "Table"; "Create Table" ], [ [ Some "widgets"; Some ddl ] ]) ->
                  Expect.stringContains ddl "CREATE TABLE `widgets`" "names the table"
                  Expect.stringContains ddl "PRIMARY KEY (`id`)" "includes the primary key"
                  Expect.stringContains ddl "UNIQUE KEY `name`" "includes the unique index"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SHOW CREATE TABLE with a backtick-quoted, db-qualified name matches the unqualified result"
          <| fun _ ->
              // Regression: stripBackticks used to run on the whole
              // "`db`.`table`" string *before* splitting on '.', so
              // "`shop`.`users`".Trim('`') -> "shop`.`users" -> split on
              // '.' -> ("shop`", "`users") — an unknown-database error for
              // any SHOW target that's both qualified and backtick-quoted.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE users (id INT PRIMARY KEY)"

              let unqualified = handle session "SHOW CREATE TABLE users" |> snd
              let qualified = handle session "SHOW CREATE TABLE `shop`.`users`" |> snd
              Expect.equal qualified unqualified "backtick-quoted db.table matches the unqualified form"

              match handle session "SHOW COLUMNS FROM `shop`.`users`" |> snd with
              | ResultSet(_, [ _ ]) -> ()
              | other -> failtestf "expected SHOW COLUMNS to resolve the backtick-quoted db.table, got %A" other

              match handle session "SHOW INDEX FROM `shop`.`users`" |> snd with
              | ResultSet(_, [ _ ]) -> ()
              | other -> failtestf "expected SHOW INDEX to resolve the backtick-quoted db.table, got %A" other

          testCase "SHOW INDEX FROM t lists the primary key and other indexes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE widgets (id INT PRIMARY KEY, sku VARCHAR(20) UNIQUE)"

              match handle session "SHOW INDEX FROM widgets" |> snd with
              | ResultSet(cols, rows) ->
                  Expect.equal cols.[0] "Table" "first column is Table"
                  Expect.equal (List.length rows) 2 "primary key + the unique index"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "an unrecognized statement is a 1064 syntax error naming the query"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "GARBAGE NOT SQL" |> snd with
              | Err(1064, msg) -> Expect.stringContains msg "GARBAGE NOT SQL" "message names the query"
              | other -> failtestf "expected a 1064 error, got %A" other

          testCase "a query whose string data merely starts with SET is not hijacked by the SET-statement probe"
          <| fun _ ->
              // handle's `upper.StartsWith "SET "` check is anchored to the
              // whole trimmed query text, so this can't actually misfire —
              // this test documents and locks in that guarantee rather than
              // reproducing a live bug.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE notes (body VARCHAR(50))"

              match handle session "INSERT INTO notes VALUES ('SET x = 1')" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected a normal INSERT, got %A" other

              match handle session "SELECT body FROM notes" |> snd with
              | ResultSet(_, [ [ Some "SET x = 1" ] ]) -> ()
              | other -> failtestf "expected the literal string preserved, got %A" other

          testCase "a query containing @@ inside a string literal is not hijacked by the @@-variable probe"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE users (email VARCHAR(50))"
              let session, _ = handle session "INSERT INTO users VALUES ('a@@b.com')"

              match handle session "SELECT * FROM users WHERE email LIKE '%@@%'" |> snd with
              | ResultSet(_, [ [ Some "a@@b.com" ] ]) -> ()
              | other -> failtestf "expected the row to be found via the real parser, got %A" other

          testCase "an exception inside the engine (decimal overflow) is an Err, not an escaping exception"
          <| fun _ ->
              // Storage.coerceValue's `decimal d` throws OverflowException for
              // a DECIMAL column given a value outside decimal's range — this
              // must never escape `handle` and drop the connection.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE overflow_t (d DECIMAL(10,2))"

              match handle session "INSERT INTO overflow_t VALUES (1e300)" |> snd with
              | Err(1105, _) -> ()
              | other -> failtestf "expected a 1105 internal-error Err, got %A" other ]
