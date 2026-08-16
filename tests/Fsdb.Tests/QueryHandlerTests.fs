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

          testCase "a version-gated /*!NNNNN ... */ comment executes its wrapped SET, matching a mysqldump preamble"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "/*!40103 SET @OLD_TIME_ZONE=@@TIME_ZONE */" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected an OK/0-rows ack, got %A" other

          testCase "a comment-only statement is a harmless no-op, not a syntax error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "/* trailing comment */" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected an OK/0-rows ack, got %A" other

          testCase "a /*!NNNNN lookalike inside a string literal round-trips unchanged"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT 'a /*!40101 x*/ b'" |> snd with
              | ResultSet(_, [ [ Some "a /*!40101 x*/ b" ] ]) -> ()
              | other -> failtestf "expected the literal intact, got %A" other

          testCase "a SELECT's int/string columns report their real MySQL wire types, not a blanket VAR_STRING"
          <| fun _ ->
              // Real MySQL clients (PHP's mysqlnd in particular)
              // auto-convert a LONGLONG-typed column to a native int even
              // over the text protocol — Eloquent code doing `$model->foo_id
              // === $other->id` only gets that conversion if the column
              // definition packet reports the same type real MySQL would.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT, name VARCHAR(10))"
              let session, _ = handle session "INSERT INTO t VALUES (1, 'a')"

              match handle session "SELECT id, name FROM t" with
              | session, ResultSet([ "id"; "name" ], [ [ Some "1"; Some "a" ] ]) ->
                  Expect.equal session.LastResultColumnTypes [ TypeLongLong; TypeVarString ] "id is int, name is a string"
              | _, other -> failtestf "expected a resultset, got %A" other

          testCase "LastResultColumnTypes doesn't leak from a real SELECT onto a later same-arity probe result"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT)"
              let session, _ = handle session "INSERT INTO t VALUES (1)"
              let session, _ = handle session "SELECT id FROM t"
              Expect.equal session.LastResultColumnTypes [ TypeLongLong ] "SELECT set a real type"

              // `SELECT @@version` is also a single-column resultset (the
              // `handleAtVarSelect` probe path, not `executeStatement`'s
              // typed one) — without an explicit reset this would silently
              // inherit the previous statement's LONGLONG type instead of
              // falling back to VAR_STRING for its actual string value.
              let session, _ = handle session "SELECT @@version"
              Expect.equal session.LastResultColumnTypes [] "an unrelated probe result clears it"

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
              // mysql CLI probes the connection banner with exactly this
              // query at connect time.
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

          testCase "SET NAMES 'x' COLLATE 'y', SESSION sql_mode='...' applies both assignments"
          <| fun _ ->
              // Laravel's MySqlConnector::configureConnection sends exactly
              // this shape — NAMES-with-COLLATE and sql_mode as one
              // comma-joined SET, not two separate statements.
              let session = create 1 (Fsdb.Storage.create ())

              let session, result =
                  handle session "SET NAMES 'utf8mb4' COLLATE 'utf8mb4_unicode_ci', SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"

              match result with
              | Affected _ -> ()
              | other -> failtestf "expected OK, got %A" other

              match handle session "SELECT @@character_set_client, @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some "utf8mb4"; Some "NO_ENGINE_SUBSTITUTION" ] ]) -> ()
              | other -> failtestf "expected both variables updated, got %A" other

          testCase "sql_mode inside a comma-joined SET still splits on its own internal commas correctly"
          <| fun _ ->
              // The mode list itself is comma-separated *inside its quotes*
              // — `splitSetAssignments` must not split there, only on the
              // comma between this assignment and the next.
              let session = create 1 (Fsdb.Storage.create ())

              let session, _ =
                  handle session "SET SESSION sql_mode='ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES', NAMES 'latin1'"

              match handle session "SELECT @@sql_mode, @@character_set_client" |> snd with
              | ResultSet(_, [ [ Some "ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES"; Some "latin1" ] ]) -> ()
              | other -> failtestf "expected both variables updated, got %A" other

          testCase "sql_mode inside Laravel's real comma-joined connect-time SET turns off strict coercion"
          <| fun _ ->
              // The exact statement `strict => false` sends
              // (`MySqlConnector::configureConnection`) — reproduces the
              // real-world bug where the compound form's `sql_mode` half
              // was silently dropped and every insert stayed strict.
              let session = create 1 (Fsdb.Storage.create ())

              let session, _ =
                  handle session "SET NAMES 'utf8mb4' COLLATE 'utf8mb4_unicode_ci', SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"

              let session, _ = handle session "CREATE TABLE t (n INT)"

              match handle session "INSERT INTO t VALUES ('not a number')" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected the non-strict insert to succeed, got %A" other

          testCase "one connection's non-strict sql_mode doesn't leak into a sibling connection sharing the same Store"
          <| fun _ ->
              // Two independent sessions (e.g. Laravel's default + a
              // 'strict' => false read connection) sharing one Store, the
              // way `Server` hands every accepted connection the same
              // `Store` — `Store.StrictMode` used to be set once by whoever
              // last ran `SET sql_mode`, and stayed that way for every other
              // connection forever after.
              let store = Fsdb.Storage.create ()
              let strictSession = create 1 store
              let laxSession = create 2 store
              let laxSession, _ = handle laxSession "SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"

              let strictSession, _ = handle strictSession "CREATE TABLE t (n INT)"

              match handle strictSession "INSERT INTO t VALUES ('not a number')" |> snd with
              | Err(1366, _) -> ()
              | other -> failtestf "expected the strict sibling to still reject a bad value, got %A" other

              ignore laxSession

          testCase "SET @@SESSION.sql_mode = CONCAT(@@sql_mode, ',ANSI_QUOTES') isn't split on the CONCAT's own comma"
          <| fun _ ->
              // `splitSetAssignments` must track paren depth, not just quote
              // state — a function-call argument list has its own commas
              // that aren't assignment separators.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode = 'STRICT_TRANS_TABLES'"

              match handle session "SET @@SESSION.sql_mode = CONCAT(@@sql_mode, ',ANSI_QUOTES')" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected OK, got %A" other

          testCase "a bad multi-assignment SET applies none of its assignments, not just the ones before the bad one"
          <| fun _ ->
              // Two-phase: every fragment parses before any of them apply,
              // so a `SET` that fails partway through — the same as real
              // MySQL — can't leave `sql_mode` (or any other variable it
              // named first) half-updated. `bad-name` (a hyphen isn't a
              // valid identifier char) matches neither `setVar` nor
              // `setUserVar` — `@user_var=1` used to be this fixture's bad
              // fragment, before `SET @foo = ...` became a real feature.
              let session = create 1 (Fsdb.Storage.create ())

              let session, result = handle session "SET SESSION sql_mode='ANSI_QUOTES', bad-name=1"

              match result with
              | Err(1193, _) -> ()
              | other -> failtestf "expected a 1193 error, got %A" other

              match handle session "SELECT @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some mode ] ]) -> Expect.stringContains mode "STRICT_TRANS_TABLES" "sql_mode is unchanged from its default"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "SET @user_var = 1 defines a user variable, readable back via SELECT @user_var"
          <| fun _ ->
              // Real MySQL never validates a user-defined variable's name —
              // `SET @foo = ...` is always legal, unlike `SET SESSION x =
              // y`. mysqldump leans on this to save/restore settings around
              // a dump (`SET @OLD_SQL_MODE=@@SQL_MODE` ... later `SET
              // SQL_MODE=@OLD_SQL_MODE`).
              let session = create 1 (Fsdb.Storage.create ())
              let session, setResult = handle session "SET @user_var = 1"

              match setResult with
              | Affected 0UL -> ()
              | other -> failtestf "expected OK, got %A" other

              match handle session "SELECT @user_var" |> snd with
              | ResultSet([ "@user_var" ], [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected user_var to read back as 1, got %A" other

          testCase "SELECT @never_set is NULL, not an error — unlike an unknown @@system_var"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT @never_set" |> snd with
              | ResultSet([ "@never_set" ], [ [ None ] ]) -> ()
              | other -> failtestf "expected a NULL row, got %A" other

          testCase "SET @x = NULL defines a user variable holding NULL, not the string 'NULL'"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @x = NULL"

              match handle session "SELECT @x" |> snd with
              | ResultSet([ "@x" ], [ [ None ] ]) -> ()
              | other -> failtestf "expected a real NULL, not the string 'NULL', got %A" other

          testCase "SET foreign_key_checks = @never_set is a 1231, not a silent empty string"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET foreign_key_checks = @never_set" |> snd with
              | Err(1231, _) -> ()
              | other -> failtestf "expected a 1231 error, got %A" other

          testCase "SET @x = 'NULL' stores the four-character string, not a real NULL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @x = 'NULL'"

              match handle session "SELECT @x" |> snd with
              | ResultSet([ "@x" ], [ [ Some "NULL" ] ]) -> ()
              | other -> failtestf "expected the string 'NULL', not a real NULL, got %A" other

          testCase "SET character_set_results = NULL is accepted, unlike other system variables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, setResult = handle session "SET character_set_results = NULL"

              match setResult with
              | Affected 0UL -> ()
              | other -> failtestf "expected OK, got %A" other

              match handle session "SELECT @@character_set_results" |> snd with
              | ResultSet([ "@@character_set_results" ], [ [ None ] ]) -> ()
              | other -> failtestf "expected @@character_set_results to read back as NULL, got %A" other

          testCase "SET x = @old_var restores a saved system variable, mysqldump's preamble/postamble idiom"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET @OLD_SQL_MODE=@@SQL_MODE"
              let session, _ = handle session "SET SESSION sql_mode='ANSI_QUOTES'"
              let session, _ = handle session "SET SQL_MODE=@OLD_SQL_MODE"

              match handle session "SELECT @@sql_mode" |> snd with
              | ResultSet(_, [ [ Some mode ] ]) -> Expect.stringContains mode "STRICT_TRANS_TABLES" "sql_mode restored to its original value"
              | other -> failtestf "expected a resultset, got %A" other

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

          testCase "SCHEMA() is a synonym for DATABASE(), matching MySQL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "USE mydb"

              match handle session "SELECT SCHEMA()" |> snd with
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
              // whole trimmed query text, so this can't actually misfire.
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
