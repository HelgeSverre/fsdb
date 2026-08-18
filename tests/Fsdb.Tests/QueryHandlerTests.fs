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

          testCase "LIMIT 0 (the getColumnMeta/\"metadata, no rows\" idiom) still reports real column wire types, not a blanket VAR_STRING"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT, name VARCHAR(10))"
              let session, _ = handle session "INSERT INTO t VALUES (1, 'a')"

              match handle session "SELECT id, name FROM t LIMIT 0" with
              | session, ResultSet([ "id"; "name" ], []) ->
                  Expect.equal session.LastResultColumnTypes [ TypeLongLong; TypeVarString ] "LIMIT 0 must not narrow types to the empty row set it returns"
              | _, other -> failtestf "expected an empty resultset, got %A" other

          testCase "SUM over an integer column reports MySQL's DECIMAL result type"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (n BIGINT)"
              let session, _ = handle session "INSERT INTO t VALUES (9223372036854775807), (1)"

              match handle session "SELECT SUM(n) AS total FROM t" with
              | session, ResultSet([ "total" ], [ [ Some "9223372036854775808" ] ]) ->
                  Expect.equal session.LastResultColumnTypes [ TypeNewDecimal ] "SUM(BIGINT) is NEWDECIMAL, not LONGLONG"
              | _, other -> failtestf "expected a decimal SUM resultset, got %A" other

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

          testCase "RANK/DENSE_RANK/NTILE report LONGLONG and PERCENT_RANK reports DOUBLE over the wire"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT PRIMARY KEY, v INT)"
              let session, _ = handle session "INSERT INTO t VALUES (1, 10), (2, 20)"

              match
                  handle
                      session
                      "SELECT RANK() OVER (ORDER BY v) AS r, DENSE_RANK() OVER (ORDER BY v) AS dr, PERCENT_RANK() OVER (ORDER BY v) AS pr, NTILE(2) OVER (ORDER BY v) AS nt FROM t"
              with
              | session, ResultSet([ "r"; "dr"; "pr"; "nt" ], _) ->
                  Expect.equal
                      session.LastResultColumnTypes
                      [ TypeLongLong; TypeLongLong; TypeDouble; TypeLongLong ]
                      "RANK/DENSE_RANK/NTILE are integers, PERCENT_RANK is a double, same as real MySQL"
              | _, other -> failtestf "expected a resultset, got %A" other

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

          testCase "MySqlConnector's REPEATABLE READ transaction handshake is accepted"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ" with
              | session, Affected 0UL ->
                  Expect.equal
                      (session.Variables |> Map.tryFind "transaction_isolation" |> Option.flatten)
                      (Some "REPEATABLE-READ")
                      "the advertised isolation matches FSDB's transaction snapshots"
              | _, other -> failtestf "expected MySqlConnector's transaction preamble to return OK, got %A" other

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

          testCase "SET NAMES drives collation_connection, with an explicit COLLATE winning"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              // the explicit COLLATE in the Laravel connector shape wins
              let session, _ = handle session "SET NAMES utf8mb4 COLLATE utf8mb4_bin"

              match handle session "SELECT @@collation_connection" |> snd with
              | ResultSet(_, [ [ Some "utf8mb4_bin" ] ]) -> ()
              | other -> failtestf "expected the explicit COLLATE to set collation_connection, got %A" other

              match handle session "SELECT 'ÅGE' = 'age'" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected literal comparisons under bin, got %A" other

              // the charset's default collation when no COLLATE is written
              let session, _ = handle session "SET NAMES utf8mb4"

              match handle session "SELECT @@collation_connection" |> snd with
              | ResultSet(_, [ [ Some "utf8mb4_0900_ai_ci" ] ]) -> ()
              | other -> failtestf "expected SET NAMES utf8mb4 to restore ai_ci, got %A" other

              match handle session "SELECT 'ÅGE' = 'age'" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected literal comparisons under ai_ci again, got %A" other

              // binary's byte-wise comparisons map to utf8mb4_bin
              let session, _ = handle session "SET NAMES binary"

              match handle session "SELECT @@collation_connection" |> snd with
              | ResultSet(_, [ [ Some "utf8mb4_bin" ] ]) -> ()
              | other -> failtestf "expected SET NAMES binary to report utf8mb4_bin, got %A" other

              match handle session "SELECT 'ÅGE' = 'age'" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected SET NAMES binary to compare byte-wise, got %A" other

              // an unknown COLLATE is a 1273, same as the assignment form
              match handle session "SET NAMES utf8mb4 COLLATE no_such_collation" |> snd with
              | Err(1273, _) -> ()
              | other -> failtestf "expected 1273 for an unknown COLLATE in SET NAMES, got %A" other

          testCase "collation_connection drives LIKE, DISTINCT, and GROUP BY over literals"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              let session, _ = handle session "SET collation_connection = utf8mb4_bin"

              match handle session "SELECT 'åge' LIKE 'ÅGE'" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected bin LIKE on literals to be case-sensitive, got %A" other

              match handle session "SELECT DISTINCT v FROM (SELECT 'åge' AS v UNION ALL SELECT 'ÅGE') t" |> snd with
              | ResultSet(_, rows) -> Expect.equal (List.length rows) 2 "bin connection keeps both literals distinct"
              | other -> failtestf "expected bin DISTINCT over literals to keep both, got %A" other

              match handle session "SELECT COUNT(*) FROM (SELECT 'åge' AS v UNION ALL SELECT 'ÅGE') t GROUP BY v" |> snd with
              | ResultSet(_, [ [ Some "1" ]; [ Some "1" ] ]) -> ()
              | other -> failtestf "expected bin GROUP BY over literals to split them, got %A" other

              let session, _ = handle session "SET collation_connection = utf8mb4_0900_ai_ci"

              match handle session "SELECT 'åge' LIKE 'ÅGE'" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected ai_ci LIKE on literals to fold, got %A" other

          testCase "SET collation_connection drives literal comparisons, column collations still win"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              handle session "CREATE TABLE g (name VARCHAR(20))" |> ignore
              handle session "INSERT INTO g VALUES ('age')" |> ignore

              match handle session "SELECT 'a' = 'A'" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the default ai_ci to fold, got %A" other

              match handle session "SET collation_connection = utf8mb4_bin" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected SET collation_connection to succeed, got %A" other

              match handle session "SELECT 'a' = 'A'" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected bin literals after SET, got %A" other

              // the column's own ai_ci still folds
              match handle session "SELECT COUNT(*) FROM g WHERE name = 'AGE'" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the column collation to win, got %A" other

              // an unknown collation is MySQL's 1273
              match handle session "SET collation_connection = no_such_collation" |> snd with
              | Err(1273, _) -> ()
              | other -> failtestf "expected 1273 for an unknown collation, got %A" other

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
              // `Store` — a session's `sql_mode` must stay scoped to that
              // session, not leak to a sibling connection that shares the
              // store.
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
              // `setUserVar`; `@user_var=1` can't serve as the bad fragment
              // because `SET @foo = ...` is a real feature.
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
              let session, _ = handle session "CREATE DATABASE mydb"
              let session, _ = handle session "USE mydb"

              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ Some "mydb" ] ]) -> ()
              | other -> failtestf "expected mydb, got %A" other

          testCase "SCHEMA() is a synonym for DATABASE(), matching MySQL"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE mydb"
              let session, _ = handle session "USE mydb"

              match handle session "SELECT SCHEMA()" |> snd with
              | ResultSet(_, [ [ Some "mydb" ] ]) -> ()
              | other -> failtestf "expected mydb, got %A" other

          testCase "USE against a database that doesn't exist is a 1049, not a silent success"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "USE nope_does_not_exist" |> snd with
              | Err(1049, msg) -> Expect.stringContains msg "nope_does_not_exist" "message names the missing database"
              | other -> failtestf "expected a 1049 Unknown database error, got %A" other

              // The session's database is unchanged by the failed USE.
              match handle session "SELECT DATABASE()" |> snd with
              | ResultSet(_, [ [ None ] ]) -> ()
              | other -> failtestf "expected DATABASE() to still be NULL, got %A" other

          testCase "USE information_schema succeeds even though it isn't a real catalog entry"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "USE information_schema" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected USE information_schema to succeed, got %A" other

          testCase "CREATE DATABASE with a charset/collate tail actually creates the database"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "CREATE DATABASE crescat_testing CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the CREATE DATABASE to succeed, got %A" other

              match handle session "USE crescat_testing" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the newly-created database to be usable, got %A" other

          testCase "ALTER DATABASE succeeds on an existing database and 1049s on a missing one"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"

              match handle session "ALTER DATABASE shop CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected ALTER DATABASE to succeed, got %A" other

              match handle session "ALTER DATABASE nope_does_not_exist CHARACTER SET utf8mb4" |> snd with
              | Err(1049, _) -> ()
              | other -> failtestf "expected a 1049 error, got %A" other

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
              let session, _ = handle session "CREATE DATABASE shop"
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
              let session, _ = handle session "CREATE DATABASE shop"
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
              let session, _ = handle session "CREATE DATABASE shop"
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
              let session, _ = handle session "CREATE DATABASE shop"
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
              let session, _ = handle session "CREATE DATABASE shop"
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
              | other -> failtestf "expected a 1105 internal-error Err, got %A" other

          testCase "LAST_INSERT_ID() stays 0 for an explicit AUTO_INCREMENT id, unlike the OK packet's last_insert_id"
          <| fun _ ->
              // Real MySQL 8.4: PDO::lastInsertId()/the OK packet reports an
              // explicitly-supplied AUTO_INCREMENT id back to the caller, but
              // the separate LAST_INSERT_ID() SQL function only ever reflects
              // a *generated* id and stays 0 until one actually is.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, n INT)"
              let session, _ = handle session "INSERT INTO t (id, n) VALUES (5, 1)"
              Expect.equal session.LastInsertId 5L "OK packet reports the explicit id"

              match handle session "SELECT LAST_INSERT_ID()" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected LAST_INSERT_ID() to stay 0, got %A" other

              // A later statement that *does* generate an id moves
              // LAST_INSERT_ID() to it, and a further all-explicit multi-row
              // insert after that leaves LAST_INSERT_ID() unchanged (matching
              // MySQL) while still reporting its own id on the OK packet.
              let session, _ = handle session "INSERT INTO t (n) VALUES (2)"

              match handle session "SELECT LAST_INSERT_ID()" |> snd with
              | ResultSet(_, [ [ Some "6" ] ]) -> ()
              | other -> failtestf "expected LAST_INSERT_ID() to report the generated id 6, got %A" other

              let session, _ = handle session "INSERT INTO t (id, n) VALUES (20, 1), (21, 2)"
              Expect.equal session.LastInsertId 21L "OK packet reports the last row's explicit id"

              match handle session "SELECT LAST_INSERT_ID()" |> snd with
              | ResultSet(_, [ [ Some "6" ] ]) -> ()
              | other -> failtestf "expected LAST_INSERT_ID() to still be 6, unchanged by the all-explicit insert, got %A" other

          testCase "SHOW WARNINGS LIMIT n is accepted, matching the mysql CLI's/mysqli's routine probe"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW WARNINGS LIMIT 10" |> snd with
              | ResultSet([ "Level"; "Code"; "Message" ], []) -> ()
              | other -> failtestf "expected an empty warnings resultset, got %A" other

              match handle session "SHOW WARNINGS LIMIT 5, 10" |> snd with
              | ResultSet([ "Level"; "Code"; "Message" ], []) -> ()
              | other -> failtestf "expected an empty warnings resultset with offset, got %A" other

          testCase "SHOW COUNT(*) WARNINGS / SHOW COUNT(*) ERRORS report a single zero row"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW COUNT(*) WARNINGS" |> snd with
              | ResultSet([ "@@session.warning_count" ], [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected @@session.warning_count = 0, got %A" other

              match handle session "SHOW COUNT(*) ERRORS" |> snd with
              | ResultSet([ "@@session.error_count" ], [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected @@session.error_count = 0, got %A" other

          testCase "SHOW ERRORS is accepted like SHOW WARNINGS"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW ERRORS" |> snd with
              | ResultSet([ "Level"; "Code"; "Message" ], []) -> ()
              | other -> failtestf "expected an empty errors resultset, got %A" other

          testCase "SET GLOBAL never changes the issuing session's own variable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, ok = handle session "SET GLOBAL max_connections = 500"
              Expect.equal ok (Affected 0UL) "SET GLOBAL acks"

              // `max_connections` was never in this session's own `Variables`
              // (only in the store-wide GLOBAL map SET GLOBAL just wrote) —
              // `@@SESSION.` scoped explicitly, it stays unknown to this
              // session, proving the GLOBAL write never touched
              // `session.Variables`.
              match handle session "SELECT @@SESSION.max_connections" |> snd with
              | Err(1193, _) -> ()
              | other -> failtestf "SET GLOBAL must not leak into this session's own @@SESSION value, got %A" other

          testCase "SET GLOBAL x = y is visible to SELECT @@GLOBAL.x on the same connection"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET GLOBAL max_connections = 500"

              match handle session "SELECT @@GLOBAL.max_connections" |> snd with
              | ResultSet(_, [ [ Some "500" ] ]) -> ()
              | other -> failtestf "expected @@GLOBAL.max_connections = 500, got %A" other

          testCase "a new session inherits a SET GLOBAL made before it connected"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setter = create 1 store
              let setter, _ = handle setter "SET GLOBAL max_connections = 500"
              ignore setter

              let newcomer = create 2 store

              Expect.equal
                  (newcomer.Variables |> Map.tryFind "max_connections" |> Option.flatten)
                  (Some "500")
                  "a session created after the GLOBAL write inherits it as its own session default"

          testCase "SET @@GLOBAL.x = y is equivalent to SET GLOBAL x = y"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "SET @@GLOBAL.max_connections = 777"
              ignore session

              let newcomer = create 2 store

              Expect.equal
                  (newcomer.Variables |> Map.tryFind "max_connections" |> Option.flatten)
                  (Some "777")
                  "the @@GLOBAL. spelling reaches the same global map as SET GLOBAL"

          testCase "SET [SESSION] TRANSACTION ISOLATION LEVEL accepts READ COMMITTED and READ UNCOMMITTED"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED" with
              | session, Affected 0UL ->
                  Expect.equal
                      (session.Variables |> Map.tryFind "transaction_isolation" |> Option.flatten)
                      (Some "READ-COMMITTED")
                      "hyphenated, matching MySQL's own @@transaction_isolation spelling"
              | _, other -> failtestf "expected OK, got %A" other

              match handle session "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED" with
              | session, Affected 0UL ->
                  Expect.equal
                      (session.Variables |> Map.tryFind "transaction_isolation" |> Option.flatten)
                      (Some "READ-UNCOMMITTED")
                      "hyphenated"
              | _, other -> failtestf "expected OK, got %A" other

          testCase "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE is a clear 1235, not a silent lie or a 1064"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE" |> snd with
              | Err(1235, _) -> ()
              | other -> failtestf "expected a 1235 unsupported-feature error, got %A" other

          // -----------------------------------------------------------------
          // Session user identity + the built-in `mysql` system schema
          // -----------------------------------------------------------------

          testCase "CURRENT_USER()/USER()/SESSION_USER() report the session's user, not a hardcoded name"
          <| fun _ ->
              let session = { create 1 (Fsdb.Storage.create ()) with User = "alice" }

              match handle session "SELECT CURRENT_USER(), USER(), SESSION_USER()" |> snd with
              | ResultSet(_, [ [ Some "alice@%"; Some "alice@localhost"; Some "alice@localhost" ] ]) -> ()
              | other -> failtestf "expected the session user's identities, got %A" other

          testCase "paren-less SELECT CURRENT_USER parses as the function, not a column (TablePlus/phpMyAdmin form)"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT CURRENT_USER" |> snd with
              | ResultSet(_, [ [ Some "root@%" ] ]) -> ()
              | other -> failtestf "expected root@%%, got %A" other

          testCase "SHOW DATABASES lists mysql alphabetically interleaved with real databases"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE zoo"

              match handle session "SHOW DATABASES" |> snd with
              | ResultSet(_, rows) ->
                  let names = rows |> List.map (List.head >> Option.get)
                  Expect.equal names [ "fsdb"; "information_schema"; "mysql"; "zoo" ] "sorted, mysql included"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "USE mysql works and SHOW TABLES FROM mysql lists the system tables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "USE mysql" with
              | session, Affected 0UL ->
                  match handle session "SHOW TABLES" |> snd with
                  | ResultSet([ "Tables_in_mysql" ], rows) ->
                      let names = rows |> List.map (List.head >> Option.get)
                      Expect.equal names [ "columns_priv"; "db"; "global_grants"; "tables_priv"; "user" ] "the 5 system tables"
                  | other -> failtestf "expected the mysql table list, got %A" other
              | _, other -> failtestf "expected USE mysql to succeed, got %A" other

          testCase "SHOW TABLES FROM information_schema lists the virtual tables instead of 1049"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW FULL TABLES FROM information_schema" |> snd with
              | ResultSet([ "Tables_in_information_schema"; "Table_type" ], rows) ->
                  Expect.isTrue
                      (rows |> List.exists (fun r -> r = [ Some "TABLES"; Some "SYSTEM VIEW" ]))
                      "TABLES present as a SYSTEM VIEW"
              | other -> failtestf "expected the virtual table list, got %A" other

          testCase "SELECT from mysql.user finds the bootstrap root row (phpMyAdmin's isSuperUser probe shape)"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT User, Host, plugin, Select_priv FROM mysql.user" |> snd with
              | ResultSet(_, [ [ Some "root"; Some "%"; Some "mysql_native_password"; Some "Y" ] ]) -> ()
              | other -> failtestf "expected the root row, got %A" other

              match handle session "SELECT 1 FROM mysql.user LIMIT 1" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the isSuperUser probe to succeed, got %A" other

          testCase "CREATE USER / DROP USER manage mysql.user rows with MySQL's 1396 duplicate/missing semantics"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store

              match handle session "CREATE USER 'bob'@'%' IDENTIFIED BY 's3cret'" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected CREATE USER to succeed, got %A" other

              match Fsdb.Auth.tryUserRow store "bob" with
              | Some(cols, row) ->
                  Expect.equal
                      (Fsdb.Auth.storedPasswordHash cols row)
                      (Fsdb.Auth.nativePasswordHash "s3cret")
                      "hash landed in authentication_string"
              | None -> failtest "expected bob to exist"

              match handle session "CREATE USER bob" |> snd with
              | Err(1396, msg) -> Expect.stringContains msg "CREATE USER failed" "duplicate is 1396"
              | other -> failtestf "expected 1396, got %A" other

              match handle session "CREATE USER IF NOT EXISTS bob" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected IF NOT EXISTS to be a no-op, got %A" other

              match handle session "DROP USER bob" |> snd with
              | Affected 0UL -> Expect.isNone (Fsdb.Auth.tryUserRow store "bob") "bob gone"
              | other -> failtestf "expected DROP USER to succeed, got %A" other

              match handle session "DROP USER bob" |> snd with
              | Err(1396, _) -> ()
              | other -> failtestf "expected dropping a missing user to be 1396, got %A" other

          testCase "ALTER USER and SET PASSWORD rewrite the stored hash; SET PASSWORD defaults to the session user"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE USER carol"

              match handle session "ALTER USER 'carol'@'%' IDENTIFIED BY 'first'" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected ALTER USER to succeed, got %A" other

              match handle session "SET PASSWORD FOR 'carol'@'%' = 'second'" |> snd with
              | Affected 0UL ->
                  match Fsdb.Auth.tryUserRow store "carol" with
                  | Some(cols, row) ->
                      Expect.equal
                          (Fsdb.Auth.storedPasswordHash cols row)
                          (Fsdb.Auth.nativePasswordHash "second")
                          "SET PASSWORD FOR overwrote ALTER USER's hash"
                  | None -> failtest "carol vanished"
              | other -> failtestf "expected SET PASSWORD FOR to succeed, got %A" other

              // No FOR clause: applies to the session's own user (root).
              match handle session "SET PASSWORD = 'rootpw'" |> snd with
              | Affected 0UL ->
                  match Fsdb.Auth.tryUserRow store "root" with
                  | Some(cols, row) ->
                      Expect.equal
                          (Fsdb.Auth.storedPasswordHash cols row)
                          (Fsdb.Auth.nativePasswordHash "rootpw")
                          "session user's hash set"
                  | None -> failtest "root vanished"
              | other -> failtestf "expected SET PASSWORD to succeed, got %A" other

          testCase "DROP DATABASE mysql is rejected with 3552 like a real system schema"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "DROP DATABASE mysql" |> snd with
              | Err(3552, msg) -> Expect.stringContains msg "system schema" "names the rejection"
              | other -> failtestf "expected 3552, got %A" other

          testCase "privilege enforcement: db and table grants gate SELECT/INSERT/DDL with 1142/1044/1227"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE shop"
              let root, _ = handle root "USE shop"
              let root, _ = handle root "CREATE TABLE orders (id INT PRIMARY KEY)"
              let root, _ = handle root "CREATE TABLE secrets (id INT PRIMARY KEY)"
              let root, _ = handle root "CREATE USER worker"
              let root, _ = handle root "GRANT SELECT ON shop.orders TO worker"

              let worker = { create 2 store with User = "worker"; Database = Some "shop" }

              match handle worker "SELECT * FROM orders" |> snd with
              | ResultSet _ -> ()
              | other -> failtestf "expected the table grant to allow SELECT, got %A" other

              match handle worker "SELECT * FROM secrets" |> snd with
              | Err(1142, msg) -> Expect.stringContains msg "SELECT command denied to user 'worker'" "1142 shape"
              | other -> failtestf "expected 1142 on the ungranted table, got %A" other

              match handle worker "INSERT INTO orders VALUES (1)" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected INSERT to be denied, got %A" other

              match handle worker "CREATE DATABASE sneaky" |> snd with
              | Err(1044, _) -> ()
              | other -> failtestf "expected CREATE DATABASE to be 1044, got %A" other

              match handle worker "CREATE USER accomplice" |> snd with
              | Err(1227, _) -> ()
              | other -> failtestf "expected CREATE USER to be 1227, got %A" other

              match handle worker "GRANT SELECT ON shop.secrets TO worker" |> snd with
              | Err(1227, _) -> ()
              | other -> failtestf "expected GRANT without grant option to be 1227, got %A" other

              // A db-level grant covers every table in the db.
              let root, _ = handle root "GRANT INSERT ON shop.* TO worker"

              match handle worker "INSERT INTO secrets VALUES (2)" |> snd with
              | Affected 1UL -> ()
              | other -> failtestf "expected the db-level INSERT grant to work, got %A" other

              // information_schema stays readable for everyone.
              match handle worker "SELECT COUNT(*) FROM information_schema.TABLES" |> snd with
              | ResultSet _ -> ()
              | other -> failtestf "expected information_schema to stay readable, got %A" other

              // REVOKE takes it back.
              let _root, _ = handle root "REVOKE SELECT ON shop.orders FROM worker"

              match handle worker "SELECT * FROM orders" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected the revoked SELECT to be denied again, got %A" other

          testCase "SHOW GRANTS renders global/db/table lines; USER_PRIVILEGES and SHOW PRIVILEGES enumerate"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store

              match handle root "SHOW GRANTS" |> snd with
              | ResultSet([ header ], rows) ->
                  Expect.equal header "Grants for root@%" "header names the account"

                  Expect.equal
                      (rows |> List.map (List.head >> Option.get))
                      [ "GRANT ALL PRIVILEGES ON *.* TO `root`@`%` WITH GRANT OPTION" ]
                      "root's single global line"
              | other -> failtestf "expected root's grants, got %A" other

              let root, _ = handle root "CREATE USER worker"
              let root, _ = handle root "GRANT SELECT, UPDATE ON shop.* TO worker"
              let root, _ = handle root "GRANT DELETE ON shop.orders TO worker"

              match handle root "SHOW GRANTS FOR 'worker'@'%'" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (List.head >> Option.get))
                      [ "GRANT USAGE ON *.* TO `worker`@`%`"
                        "GRANT SELECT, UPDATE ON `shop`.* TO `worker`@`%`"
                        "GRANT DELETE ON `shop`.`orders` TO `worker`@`%`" ]
                      "usage + db + table lines in order"
              | other -> failtestf "expected worker's grants, got %A" other

              match handle root "SHOW GRANTS FOR nobody" |> snd with
              | Err(1141, _) -> ()
              | other -> failtestf "expected 1141 for an unknown grantee, got %A" other

              match handle root "SELECT PRIVILEGE_TYPE FROM information_schema.USER_PRIVILEGES WHERE GRANTEE = \"'worker'@'%'\"" |> snd with
              | ResultSet(_, [ [ Some "USAGE" ] ]) -> ()
              | other -> failtestf "expected worker's USAGE row in USER_PRIVILEGES, got %A" other

              match handle root "SHOW PRIVILEGES" |> snd with
              | ResultSet([ "Privilege"; "Context"; "Comment" ], rows) ->
                  Expect.equal (List.length rows) 73 "MySQL 8.4's 73 privileges"
              | other -> failtestf "expected the privilege table, got %A" other

              match handle root "FLUSH PRIVILEGES" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected FLUSH PRIVILEGES to be an OK no-op, got %A" other

          testCase "mysql.user has MySQL 8.4's exact 51-column shape and mysql.db its 22"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              match Fsdb.Storage.scanList store "mysql" "user" with
              | Ok(cols, rows) ->
                  Expect.equal (List.length cols) 51 "51 columns"
                  Expect.equal (cols |> List.item 2 |> fun c -> c.Name) "Select_priv" "priv columns start at 3"
                  Expect.equal (cols |> List.last |> fun c -> c.Name) "User_attributes" "last column"
                  Expect.equal (List.length rows) 1 "just root"
              | Error e -> failtestf "expected mysql.user to scan, got %A" e

              match Fsdb.Storage.scanList store "mysql" "db" with
              | Ok(cols, rows) ->
                  Expect.equal (List.length cols) 22 "22 columns"
                  Expect.isEmpty rows "no db-level grants out of the box"
              | Error e -> failtestf "expected mysql.db to scan, got %A" e ]
