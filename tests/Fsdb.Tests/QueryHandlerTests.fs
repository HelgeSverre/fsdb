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
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLong; TypeVarString ] "id reports INT's own width, name is a string"
              | _, other -> failtestf "expected a resultset, got %A" other

          testCase "LIMIT 0 (the getColumnMeta/\"metadata, no rows\" idiom) still reports real column wire types, not a blanket VAR_STRING"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT, name VARCHAR(10))"
              let session, _ = handle session "INSERT INTO t VALUES (1, 'a')"

              match handle session "SELECT id, name FROM t LIMIT 0" with
              | session, ResultSet([ "id"; "name" ], []) ->
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLong; TypeVarString ] "LIMIT 0 must not narrow types to the empty row set it returns"
              | _, other -> failtestf "expected an empty resultset, got %A" other

          // A resultset's types are read off the row `Value`s, which know
          // nothing about how the column was declared. Where a projection
          // resolves back to a real column, the declared type wins — clients
          // act on the difference (an ENUM is only an ENUM when the column
          // definition carries ENUM_FLAG; TINYINT(1) is a bool, not a number).
          testCase "a bare column reference reports its declared type, not the one its stored Value implies"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, _ =
                  handle
                      session
                      "CREATE TABLE t (st ENUM('a','b') NOT NULL, ok BOOLEAN NOT NULL, yr YEAR NOT NULL, \
                       tiny TINYINT NOT NULL, small SMALLINT NOT NULL, mid INT NOT NULL, big BIGINT NOT NULL)"

              let session, _ = handle session "INSERT INTO t VALUES ('a', 1, 2011, 1, 2, 3, 4)"

              match handle session "SELECT st, ok, yr, tiny, small, mid, big FROM t" with
              | session, ResultSet(_, [ _ ]) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeString; TypeTiny; TypeYear; TypeTiny; TypeShort; TypeLong; TypeLongLong ]
                      "each column reports the width and family it was declared with"
              | _, other -> failtestf "expected one row, got %A" other

          // MySQL declares YEAR()'s result as YEAR even though the value it
          // returns is an ordinary integer.
          testCase "YEAR() reports the YEAR type its integer result would otherwise hide"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (d DATE NOT NULL)"
              let session, _ = handle session "INSERT INTO t VALUES ('2011-10-16')"

              match handle session "SELECT YEAR(d) AS yr, MONTH(d) AS mo FROM t" with
              | session, ResultSet(_, [ [ Some "2011"; Some "10" ] ]) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeYear; TypeLongLong ]
                      "YEAR() is YEAR; the other extractors stay plain integers"
              | _, other -> failtestf "expected the extracted parts, got %A" other

          // WITH ROLLUP materializes each grouped column into a nullable
          // temporary to hold the super-aggregate row's NULL, and an enum's
          // value set doesn't survive it.
          testCase "WITH ROLLUP drops a grouped ENUM back to its data-driven type"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (st ENUM('a','b') NOT NULL, n INT NOT NULL)"
              let session, _ = handle session "INSERT INTO t VALUES ('a', 1), ('b', 2)"

              match handle session "SELECT st, COUNT(*) AS c FROM t GROUP BY st" |> fst with
              | grouped ->
                  Expect.equal
                      (grouped.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeString; TypeLongLong ]
                      "a plain GROUP BY keeps the enum"

              match handle session "SELECT st, COUNT(*) AS c FROM t GROUP BY st WITH ROLLUP" |> fst with
              | rolled ->
                  Expect.equal
                      (rolled.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeVarString; TypeLongLong ]
                      "the rollup temporary loses it, so claiming ENUM would overclaim"

          testCase "SUM over an integer column reports MySQL's DECIMAL result type"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (n BIGINT)"
              let session, _ = handle session "INSERT INTO t VALUES (9223372036854775807), (1)"

              match handle session "SELECT SUM(n) AS total FROM t" with
              | session, ResultSet([ "total" ], [ [ Some "9223372036854775808" ] ]) ->
                  Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeNewDecimal ] "SUM(BIGINT) is NEWDECIMAL, not LONGLONG"
              | _, other -> failtestf "expected a decimal SUM resultset, got %A" other

          testCase "computed expressions report their static result types"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SELECT 1, 256, 40000, 2147483648, 1 + 2, 1 = 1, 1 / 2, CAST('x' AS CHAR(8))" with
              | session, ResultSet(_, [ _ ]) ->
                  Expect.equal
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
                      [ TypeTiny; TypeShort; TypeLong; TypeLongLong; TypeLongLong; TypeLongLong; TypeNewDecimal; TypeString ]
                      "literal, arithmetic, predicate, division, and cast types"

                  let charMetadata = List.last session.LastResultColumnMetadata
                  Expect.equal charMetadata.ColumnLength 32u "CHAR(8) carries its utf8mb4 byte width"
              | _, other -> failtestf "expected one computed row, got %A" other

          testCase "declared result metadata carries widths and column flags"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE meta (id INT UNSIGNED AUTO_INCREMENT PRIMARY KEY, code CHAR(8) NOT NULL, state ENUM('new','closed') UNIQUE)"

              match handle session "SELECT id, code, state FROM meta LIMIT 0" with
              | session, ResultSet(_, []) ->
                  let id, code, state =
                      match session.LastResultColumnMetadata with
                      | [ id; code; state ] -> id, code, state
                      | metadata -> failtestf "expected three metadata records, got %A" metadata

                  Expect.equal id.TypeId TypeLong "INT wire type"
                  Expect.isTrue (id.Flags &&& UnsignedFlag <> 0us) "UNSIGNED flag"
                  Expect.isTrue (id.Flags &&& PrimaryKeyFlag <> 0us) "PRIMARY_KEY flag"
                  Expect.isTrue (id.Flags &&& AutoIncrementFlag <> 0us) "AUTO_INCREMENT flag"
                  Expect.equal code.TypeId TypeString "CHAR wire type"
                  Expect.equal code.ColumnLength 32u "CHAR utf8mb4 byte width"
                  Expect.isTrue (code.Flags &&& NotNullFlag <> 0us) "NOT_NULL flag"
                  Expect.equal state.TypeId TypeString "ENUM wire type"
                  Expect.isTrue (state.Flags &&& EnumFlag <> 0us) "ENUM flag"
                  Expect.isTrue (state.Flags &&& UniqueKeyFlag <> 0us) "UNIQUE_KEY flag"
              | _, other -> failtestf "expected an empty typed resultset, got %A" other

          testCase "result column metadata doesn't leak from a real SELECT onto a later same-arity probe result"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT)"
              let session, _ = handle session "INSERT INTO t VALUES (1)"
              let session, _ = handle session "SELECT id FROM t"
              Expect.equal (session.LastResultColumnMetadata |> List.map _.TypeId) [ TypeLong ] "SELECT set a real type"

              // `SELECT @@version` is also a single-column resultset (the
              // `handleAtVarSelect` probe path, not `executeStatement`'s
              // typed one) — without an explicit reset this would silently
              // inherit the previous statement's LONGLONG type instead of
              // falling back to VAR_STRING for its actual string value.
              let session, _ = handle session "SELECT @@version"
              Expect.equal session.LastResultColumnMetadata [] "an unrelated probe result clears it"

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
                      (session.LastResultColumnMetadata |> List.map _.TypeId)
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

          testCase "empty SET fragments do not allocate one string per separator"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let sql = "SET " + String.replicate 500_000 " ,"
              GC.Collect()
              let before = GC.GetAllocatedBytesForCurrentThread()
              handle session sql |> ignore
              let allocated = GC.GetAllocatedBytesForCurrentThread() - before
              Expect.isLessThan allocated 8_000_000L "separator count does not amplify allocation"

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

          testCase "GROUP_CONCAT obeys the session group_concat_max_len byte limit"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE gc (v VARCHAR(600))"
              let session, _ = handle session ("INSERT INTO gc VALUES ('" + String.replicate 600 "x" + "'), ('" + String.replicate 600 "y" + "')")

              match handle session "SELECT LENGTH(GROUP_CONCAT(v)) FROM gc" |> snd with
              | ResultSet(_, [ [ Some "1024" ] ]) -> ()
              | other -> failtestf "expected the MySQL default 1024-byte cap, got %A" other

              let session, _ = handle session "SET SESSION group_concat_max_len = 2048"

              match handle session "SELECT LENGTH(GROUP_CONCAT(v)) FROM gc" |> snd with
              | ResultSet(_, [ [ Some "1201" ] ]) -> ()
              | other -> failtestf "expected the larger session limit, got %A" other

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

          testCase "an escaped quote keeps a comma inside one SET assignment"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let session, result = handle session "SET @x='a\\\', @y=1'"

              match result with
              | Affected _ -> ()
              | other -> failtestf "expected the escaped quote to keep the fragment intact, got %A" other

              match handle session "SELECT @x, @y" |> snd with
              | ResultSet(_, [ [ Some "a\\', @y=1"; None ] ]) -> ()
              | other -> failtestf "expected only @x to be assigned, got %A" other

          testCase "a session refuses user variables beyond its fixed memory-growth cap"
          <| fun _ ->
              let variables = seq { for i in 1..65536 -> sprintf "v%d" i, Some "1" } |> Map.ofSeq
              let session = { create 1 (Fsdb.Storage.create ()) with UserVariables = variables }

              match handle session "SET @overflow = 1" with
              | unchanged, Err(1105, "Too many user-defined variables") ->
                  Expect.equal unchanged.UserVariables.Count 65536 "the rejected SET leaves the map unchanged"
              | _, other -> failtestf "expected the user-variable cap error, got %A" other

              match handle session "SET @v1 = 2" with
              | updated, Affected _ -> Expect.equal updated.UserVariables.["v1"] (Some "2") "existing variables remain writable"
              | _, other -> failtestf "expected an existing variable update to succeed, got %A" other

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

          testCase "SHOW STATUS answers session/global forms and unmatched patterns with empty sets"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "show session status like 'ssl_version'" |> snd with
              | ResultSet([ "Variable_name"; "Value" ], [ [ Some "Ssl_version"; Some "" ] ]) -> ()
              | other -> failtestf "expected the empty Ssl_version row, got %A" other

              match handle session "SHOW GLOBAL STATUS LIKE 'Uptime'" |> snd with
              | ResultSet(_, [ [ Some "Uptime"; Some _ ] ]) -> ()
              | other -> failtestf "expected an Uptime row, got %A" other

              match handle session "SHOW STATUS LIKE 'no_such_counter%'" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected an empty set, got %A" other

          testCase "SHOW SESSION/GLOBAL VARIABLES match like the bare form; GLOBAL reads the store scope"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET GLOBAL wait_timeout = 123"

              match handle session "SHOW SESSION VARIABLES LIKE 'wait_timeout'" |> snd with
              | ResultSet(_, [ [ Some "wait_timeout"; Some "300" ] ]) -> ()
              | other -> failtestf "expected the session value untouched, got %A" other

              match handle session "SHOW GLOBAL VARIABLES LIKE 'wait_timeout'" |> snd with
              | ResultSet(_, [ [ Some "wait_timeout"; Some "123" ] ]) -> ()
              | other -> failtestf "expected the global override, got %A" other

          testCase "SHOW ENGINES / STORAGE ENGINES / CHARACTER SET / PRIVILEGES / GRANTS answer"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW ENGINES" |> snd with
              | ResultSet(_, [ [ Some "InnoDB"; Some "DEFAULT"; _; Some "YES"; Some "YES"; Some "YES" ] ]) -> ()
              | other -> failtestf "expected the InnoDB engine row, got %A" other

              match handle session "SHOW STORAGE ENGINES" |> snd with
              | ResultSet(_, [ _ ]) -> ()
              | other -> failtestf "expected the same single row, got %A" other

              match handle session "SHOW CHARACTER SET LIKE 'utf8mb4'" |> snd with
              | ResultSet([ "Charset"; "Default collation"; "Description"; "Maxlen" ], [ [ Some "utf8mb4"; Some "utf8mb4_0900_ai_ci"; _; Some "4" ] ]) -> ()
              | other -> failtestf "expected the utf8mb4 charset row, got %A" other

              match handle session "SHOW PRIVILEGES" |> snd with
              | ResultSet([ "Privilege"; "Context"; "Comment" ], rows) -> Expect.isGreaterThan rows.Length 30 "the full static list"
              | other -> failtestf "expected the privileges list, got %A" other

              match handle session "SHOW GRANTS FOR CURRENT_USER()" |> snd with
              | ResultSet(_, [ [ Some grant ] ]) -> Expect.stringContains grant "GRANT ALL PRIVILEGES ON *.*" "the one truthful grant"
              | other -> failtestf "expected one grant row, got %A" other

          testCase "SHOW TRIGGERS/EVENTS/PROCEDURE STATUS are empty with real headers; unknown db still 1049"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"

              match handle session "SHOW TRIGGERS FROM shop" |> snd with
              | ResultSet("Trigger" :: _, []) -> ()
              | other -> failtestf "expected empty triggers, got %A" other

              match handle session "SHOW TRIGGERS FROM nope" |> snd with
              | Err(1049, _) -> ()
              | other -> failtestf "expected 1049, got %A" other

              match handle session "SHOW EVENTS FROM shop" |> snd with
              | ResultSet("Db" :: _, []) -> ()
              | other -> failtestf "expected empty events, got %A" other

              match handle session "SHOW PROCEDURE STATUS WHERE Db='shop'" |> snd with
              | ResultSet("Db" :: _, []) -> ()
              | other -> failtestf "expected empty procedure status, got %A" other

              match handle session "SHOW FUNCTION STATUS" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected empty function status, got %A" other

          testCase "SHOW FULL TABLES WHERE Table_type filters on the pseudo-column"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE widgets (id INT PRIMARY KEY)"

              match handle session "SHOW FULL TABLES FROM shop WHERE Table_type IN ('BASE TABLE', 'SYSTEM VERSIONED')" |> snd with
              | ResultSet(_, [ [ Some "widgets"; Some "BASE TABLE" ] ]) -> ()
              | other -> failtestf "expected the table to pass the filter, got %A" other

              match handle session "SHOW FULL TABLES FROM shop WHERE Table_type = 'VIEW'" |> snd with
              | ResultSet(_, []) -> ()
              | other -> failtestf "expected the VIEW filter to exclude everything, got %A" other

          testCase "SHOW TABLES FROM information_schema lists the virtual tables"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW TABLES FROM information_schema" |> snd with
              | ResultSet([ "Tables_in_information_schema" ], rows) ->
                  Expect.isGreaterThan rows.Length 20 "all virtual tables listed"
              | other -> failtestf "expected the virtual-table listing, got %A" other

          testCase "information_schema is readable but cannot be materialized or dropped by an unprivileged user"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let _, _ = handle root "CREATE USER 'limited' IDENTIFIED BY 'pw'"
              let limited = { create 2 store with User = "limited" }

              match handle limited "SELECT TABLE_NAME FROM information_schema.TABLES" |> snd with
              | ResultSet _ -> ()
              | other -> failtestf "expected information_schema SELECT to remain available, got %A" other

              match handle limited "CREATE TABLE information_schema.evil (id INT)" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected CREATE in information_schema to be denied, got %A" other

              match handle limited "DROP DATABASE information_schema" |> snd with
              | Err(1044, _) -> ()
              | other -> failtestf "expected DROP information_schema to be denied, got %A" other

          testCase "information_schema only reveals schemas, definitions, and grants visible to the viewer"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE secret"
              let root, _ = handle root "USE secret"
              let root, _ = handle root "CREATE TABLE t (id INT)"
              let root, _ = handle root "CREATE TABLE log (id INT)"
              let root, _ = handle root "CREATE VIEW secret_view AS SELECT id FROM t"
              let root, _ = handle root "CREATE TRIGGER secret_trigger AFTER INSERT ON t FOR EACH ROW INSERT INTO log VALUES (NEW.id)"
              let root, _ = handle root "CREATE USER 'limited' IDENTIFIED BY 'pw'"
              let root, _ = handle root "CREATE USER 'grantee' IDENTIFIED BY 'pw'"
              let root, _ = handle root "GRANT SELECT ON secret.t TO 'grantee'"
              let limited = { create 2 store with User = "limited" }

              let expectEmpty sql =
                  match handle limited sql |> snd with
                  | ResultSet(_, []) -> ()
                  | other -> failtestf "expected no visible rows for %s, got %A" sql other

              expectEmpty "SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = 'secret'"
              expectEmpty "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'secret'"
              expectEmpty "SELECT VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA = 'secret'"
              expectEmpty "SELECT ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = 'secret'"
              expectEmpty "SELECT GRANTEE FROM information_schema.SCHEMA_PRIVILEGES WHERE GRANTEE LIKE '%grantee%'"
              expectEmpty "SELECT GRANTEE FROM information_schema.TABLE_PRIVILEGES WHERE GRANTEE LIKE '%grantee%'"

              let _, _ = handle root "GRANT SELECT ON secret.t TO 'limited'"

              match handle limited "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = 'secret'" |> snd with
              | ResultSet(_, [ [ Some "t" ] ]) -> ()
              | other -> failtestf "expected the granted table to become visible, got %A" other

              match handle limited "SELECT GRANTEE FROM information_schema.TABLE_PRIVILEGES WHERE GRANTEE LIKE '%limited%'" |> snd with
              | ResultSet(_, [ [ Some grantee ] ]) -> Expect.stringContains grantee "limited" "only the viewer's grant is visible"
              | other -> failtestf "expected the viewer's table grant, got %A" other

              expectEmpty "SELECT VIEW_DEFINITION FROM information_schema.VIEWS WHERE TABLE_SCHEMA = 'secret'"
              expectEmpty "SELECT ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = 'secret'"

          testCase "DROP TRIGGER requires TRIGGER privilege on its subject table"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE victim"
              let root, _ = handle root "USE victim"
              let root, _ = handle root "CREATE TABLE t (id INT)"
              let root, _ = handle root "CREATE TRIGGER audit_t AFTER INSERT ON t FOR EACH ROW INSERT INTO t VALUES (NEW.id)"
              let root, _ = handle root "CREATE USER 'limited' IDENTIFIED BY 'pw'"
              let limited = { create 2 store with User = "limited"; Database = Some "victim" }

              match handle limited "DROP TRIGGER audit_t" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected DROP TRIGGER to be denied, got %A" other

              match handle root "DROP TRIGGER audit_t" |> snd with
              | Affected _ -> ()
              | other -> failtestf "expected the denied attempt to leave the trigger intact, got %A" other

          testCase "SHOW PROCESSLIST answers (empty registry outside a server); KILL of an unknown id is 1094"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SHOW FULL PROCESSLIST" |> snd with
              | ResultSet([ "Id"; "User"; "Host"; "db"; "Command"; "Time"; "State"; "Info" ], _) -> ()
              | other -> failtestf "expected the processlist shape, got %A" other

              match handle session "KILL QUERY 999999" |> snd with
              | Err(1094, _) -> ()
              | other -> failtestf "expected 1094 for an unknown thread id, got %A" other

          testCase "PROCESS grants visibility while SUPER grants authority to KILL another user"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, created = handle root "CREATE USER 'pviewer' IDENTIFIED BY 'pw'"

              match created with
              | Err(code, msg) -> failtestf "create user: %d %s" code msg
              | _ -> ()

              // Unique-to-this-test ids/user so a concurrently running
              // server test's registered processes can't interfere.
              Fsdb.InformationSchema.registerProcess 777001L "root" "localhost" |> ignore
              Fsdb.InformationSchema.registerProcess 777002L "pviewer" "localhost" |> ignore

              try
                  let viewer = { create 777002 store with User = "pviewer" }

                  // Without the global PROCESS privilege, PROCESSLIST is
                  // scoped to the caller's own connections.
                  match handle viewer "SHOW PROCESSLIST" |> snd with
                  | ResultSet(_, rows) ->
                      Expect.equal
                          (rows |> List.map (List.item 1))
                          [ Some "pviewer" ]
                          "pviewer sees only its own connection"
                  | other -> failtestf "expected a processlist, got %A" other

                  match handle root "SHOW PROCESSLIST" |> snd with
                  | ResultSet(_, rows) ->
                      let ids = rows |> List.map (List.item 0)
                      Expect.contains ids (Some "777001") "root sees its own row"
                      Expect.contains ids (Some "777002") "root sees pviewer's row too"
                  | other -> failtestf "expected a processlist, got %A" other

                  // A connection pviewer can't see is one it can't name:
                  // MySQL reports the id as unknown, not as denied.
                  match handle viewer "KILL 777001" |> snd with
                  | Err(1094, msg) -> Expect.equal msg "Unknown thread id: 777001" "MySQL's 1094 text"
                  | other -> failtestf "expected 1094 for another user's connection, got %A" other

                  let root, granted = handle root "GRANT PROCESS ON *.* TO 'pviewer'"

                  match granted with
                  | Err(code, msg) -> failtestf "grant PROCESS: %d %s" code msg
                  | _ -> ()

                  match handle viewer "KILL 777001" |> snd with
                  | Err(1095, msg) -> Expect.equal msg "You are not owner of thread 777001" "PROCESS grants visibility, not kill authority"
                  | other -> failtestf "expected 1095 without SUPER, got %A" other

                  // Another account's grants read `mysql.user`; without SELECT
                  // there, MySQL denies with 1142 on that table.
                  match handle viewer "SHOW GRANTS FOR 'root'" |> snd with
                  | Err(1142, msg) ->
                      Expect.equal msg "SELECT command denied to user 'pviewer'@'localhost' for table 'user'" "MySQL's 1142 text"
                  | other -> failtestf "expected 1142 for another account's grants, got %A" other

                  // Its own grants stay readable.
                  match handle viewer "SHOW GRANTS" |> snd with
                  | ResultSet(_, _) -> ()
                  | other -> failtestf "expected pviewer's own grants, got %A" other
              finally
                  Fsdb.InformationSchema.unregisterProcess 777001L
                  Fsdb.InformationSchema.unregisterProcess 777002L

          testCase "SHOW TABLES WHERE filters on Tables_in_<db> and 1054s an unknown column"
          <| fun _ ->
              let session = create 999901 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE widgets (id INT PRIMARY KEY)"
              let session, _ = handle session "CREATE TABLE gears (id INT PRIMARY KEY)"

              match handle session "SHOW TABLES FROM shop WHERE Tables_in_shop = 'widgets'" |> snd with
              | ResultSet(_, [ [ Some "widgets" ] ]) -> ()
              | other -> failtestf "expected only the named table, got %A" other

              match handle session "SHOW TABLES FROM shop WHERE bogus_col = 'x'" |> snd with
              | Err(1054, _) -> ()
              | other -> failtestf "expected 1054 for an unknown filter column, got %A" other

          testCase "SHOW TRIGGERS/EVENTS tolerate extra whitespace between keywords"
          <| fun _ ->
              let session = create 999902 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"

              match handle session "SHOW  TRIGGERS  FROM shop" |> snd with
              | ResultSet("Trigger" :: _, []) -> ()
              | other -> failtestf "expected empty triggers despite doubled spaces, got %A" other

              match handle session "SHOW  EVENTS FROM shop" |> snd with
              | ResultSet("Db" :: _, []) -> ()
              | other -> failtestf "expected empty events despite doubled spaces, got %A" other

          testCase "SHOW STATUS/VARIABLES accept WHERE Variable_name = '...'"
          <| fun _ ->
              let session = create 999903 (Fsdb.Storage.create ())

              match handle session "SHOW SESSION STATUS WHERE Variable_name = 'Uptime'" |> snd with
              | ResultSet(_, [ [ Some "Uptime"; Some _ ] ]) -> ()
              | other -> failtestf "expected the one Uptime row, got %A" other

              match handle session "SHOW VARIABLES WHERE Variable_name = 'autocommit'" |> snd with
              | ResultSet(_, [ [ Some "autocommit"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the one autocommit row, got %A" other

          testCase "CURRENT_USER()/USER() fall back to the root identity off the wire"
          <| fun _ ->
              // A session built directly (embedded `Db`, tests) has no
              // handshake — `Session.create` defaults its user to root.
              let session = create 999904 (Fsdb.Storage.create ())

              match handle session "SELECT CURRENT_USER(), USER()" |> snd with
              | ResultSet(_, [ [ Some "root@%"; Some "root@localhost" ] ]) -> ()
              | other -> failtestf "expected the fallback identity, got %A" other

          testCase "comments ahead of text-probed statements are stripped like real MySQL's lexer"
          <| fun _ ->
              let session = create 999905 (Fsdb.Storage.create ())

              // The TablePlus dump preamble shape: a -- comment banner, blank
              // lines, then a version-gated SET reaching the probe path.
              let preamble =
                  "-- ----------------
-- TablePlus 6.1.2
--
-- Database: x
-- ----------------


/*!40101 SET @OLD_CHARACTER_SET_CLIENT=@@CHARACTER_SET_CLIENT */"

              match handle session preamble |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the preamble SET to succeed, got %A" other

              match handle session "/* c */ SET @x = 1" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the block-commented SET to succeed, got %A" other

              match handle session "SELECT 3 # hash comment" |> snd with
              | ResultSet(_, [ [ Some "3" ] ]) -> ()
              | other -> failtestf "expected the hash comment to be stripped, got %A" other

              match handle session "SET @a = 1 -- trailing" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the trailing comment to be stripped, got %A" other

              // `--` without following whitespace is arithmetic, not a comment.
              match handle session "SELECT 5--3" |> snd with
              | ResultSet(_, [ [ Some "8" ] ]) -> ()
              | other -> failtestf "expected 5--3 = 8, got %A" other

              // Comment markers inside string literals are data.
              match handle session "SELECT '-- not # a /* comment */'" |> snd with
              | ResultSet(_, [ [ Some "-- not # a /* comment */" ] ]) -> ()
              | other -> failtestf "expected the literal preserved, got %A" other

          testCase "ALTER TABLE ... DISABLE/ENABLE KEYS is a no-op OK, 1146 for a missing table"
          <| fun _ ->
              let session = create 999906 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "USE shop"
              let session, _ = handle session "CREATE TABLE t (id INT PRIMARY KEY)"

              match handle session "/*!40000 ALTER TABLE `t` DISABLE KEYS */" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the versioned DISABLE KEYS no-op, got %A" other

              match handle session "ALTER TABLE t ENABLE KEYS" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected the ENABLE KEYS no-op, got %A" other

              match handle session "ALTER TABLE nope DISABLE KEYS" |> snd with
              | Err(1146, _) -> ()
              | other -> failtestf "expected 1146 for a missing table, got %A" other

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
              | Err(1105, "Internal error") -> ()
              | other -> failtestf "expected a 1105 internal-error Err, got %A" other

          testCase "FOUND_ROWS() and ROW_COUNT() report the previous statement"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE counts (id INT, n INT)"
              let session, _ = handle session "INSERT INTO counts VALUES (1, 0), (2, 0), (3, 0)"
              let session, _ = handle session "SELECT id FROM counts ORDER BY id LIMIT 2"

              match handle session "SELECT FOUND_ROWS(), ROW_COUNT()" with
              | session, ResultSet(_, [ [ Some "2"; Some "-1" ] ]) ->
                  let session, _ = handle session "UPDATE counts SET n = 1"

                  match handle session "SELECT ROW_COUNT(), FOUND_ROWS()" |> snd with
                  | ResultSet(_, [ [ Some "3"; Some "0" ] ]) -> ()
                  | other -> failtestf "expected affected-row accounting, got %A" other
              | _, other -> failtestf "expected result-row accounting, got %A" other

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

          testCase "LAST_INSERT_ID() after an INSERT ... SELECT upsert: set by the insert path, untouched by an update-only run"
          <| fun _ ->
              // MySQL 8.4.11 write probe (disposable server, 2026-08-19):
              // after an INSERT...SELECT ODKU that inserted rows,
              // LAST_INSERT_ID() = the first generated id; a later run that
              // only updates leaves it unchanged.
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE src (k INT, v INT)"
              let session, _ = handle session "INSERT INTO src VALUES (1, 10), (2, 20)"
              let session, _ = handle session "CREATE TABLE dst (id INT AUTO_INCREMENT PRIMARY KEY, k INT UNIQUE, v INT)"

              let session, _ =
                  handle session "INSERT INTO dst (k, v) SELECT k, v FROM src ON DUPLICATE KEY UPDATE v = VALUES(v)"

              match handle session "SELECT LAST_INSERT_ID()" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected LAST_INSERT_ID() = 1 from the insert path, got %A" other

              let session, _ =
                  handle session "INSERT INTO dst (k, v) SELECT k, v + 100 FROM src ON DUPLICATE KEY UPDATE v = VALUES(v)"

              match handle session "SELECT LAST_INSERT_ID()" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected LAST_INSERT_ID() unchanged by the update-only run, got %A" other

          testCase "REPLACE reports deleted plus inserted rows in both client affected-row modes"
          <| fun _ ->
              let replace capabilities =
                  let session = { create 1 (Fsdb.Storage.create ()) with Capabilities = capabilities }
                  let session, _ = handle session "CREATE TABLE t (id INT PRIMARY KEY, n INT)"
                  let session, _ = handle session "INSERT INTO t VALUES (1, 10)"
                  handle session "REPLACE INTO t VALUES (1, 10)" |> snd

              Expect.equal (replace 0u) (Affected 1UL) "changed-row mode"
              Expect.equal (replace ClientFoundRows) (Affected 1UL) "found-row mode"

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

          testCase "SHOW CREATE DATABASE, OPEN TABLES, PLUGINS, and ENGINE INNODB STATUS are truthful"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE app"
              let session, _ = handle session "USE app"
              let session, _ = handle session "CREATE TABLE visible (id INT)"

              match handle session "SHOW CREATE DATABASE app" |> snd with
              | ResultSet([ "Database"; "Create Database" ], [ [ Some "app"; Some ddl ] ]) ->
                  Expect.stringContains ddl "CREATE DATABASE `app`" "database DDL"
              | other -> failtestf "unexpected SHOW CREATE DATABASE result: %A" other

              match handle session "SHOW OPEN TABLES FROM app LIKE 'vis%'" |> snd with
              | ResultSet([ "Database"; "Table"; "In_use"; "Name_locked" ], [ [ Some "app"; Some "visible"; Some "0"; Some "0" ] ]) -> ()
              | other -> failtestf "unexpected SHOW OPEN TABLES result: %A" other

              match handle session "SHOW PLUGINS" |> snd with
              | ResultSet([ "Name"; "Status"; "Type"; "Library"; "License" ], [ [ Some "mysql_native_password"; Some "ACTIVE"; Some "AUTHENTICATION"; None; Some "GPL" ] ]) -> ()
              | other -> failtestf "unexpected SHOW PLUGINS result: %A" other

              for sql in [ "SHOW BINARY LOGS"; "SHOW BINARY LOG STATUS" ] do
                  match handle session sql |> snd with
                  | Err(1381, "You are not using binary logging") -> ()
                  | other -> failtestf "expected binary logging disabled for %s, got %A" sql other

              for sql, operation in [ "ANALYZE TABLE visible", "analyze"; "CHECK TABLE visible", "check" ] do
                  match handle session sql |> snd with
                  | ResultSet([ "Table"; "Op"; "Msg_type"; "Msg_text" ], [ [ Some "app.visible"; Some actual; Some "status"; Some "OK" ] ]) ->
                      Expect.equal actual operation "operation"
                  | other -> failtestf "unexpected maintenance result for %s: %A" sql other

              for sql in [ "FLUSH TABLES"; "FLUSH STATUS" ] do
                  match handle session sql |> snd with
                  | Affected 0UL -> ()
                  | other -> failtestf "unexpected %s result: %A" sql other

              match handle session "SHOW ENGINE INNODB STATUS" |> snd with
              | ResultSet([ "Type"; "Name"; "Status" ], [ [ Some "InnoDB"; Some ""; Some status ] ]) ->
                  Expect.stringContains status "in-memory transactional row store" "engine status describes fsdb"
              | other -> failtestf "unexpected SHOW ENGINE result: %A" other

          testCase "SHOW REPLICA STATUS returns MySQL's empty 60-column shape"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              for sql in [ "SHOW REPLICA STATUS"; "SHOW REPLICA STATUS FOR CHANNEL 'analytics'" ] do
                  match handle session sql |> snd with
                  | ResultSet(columns, []) ->
                      Expect.equal columns.Length 60 "column count"
                      Expect.equal columns.Head "Replica_IO_State" "first column"
                      Expect.equal columns.[55] "Channel_Name" "channel column"
                      Expect.equal columns.[59] "Network_Namespace" "last column"
                  | other -> failtestf "unexpected replica status for %s: %A" sql other

          testCase "SHOW CREATE reports missing routines and events with MySQL errors"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              for sql, expected in
                  [ "SHOW CREATE PROCEDURE fsdb.missing", Err(1305, "PROCEDURE missing does not exist")
                    "SHOW CREATE FUNCTION `fsdb`.`missing`", Err(1305, "FUNCTION missing does not exist")
                    "SHOW CREATE EVENT missing", Err(1539, "Unknown event 'missing'") ] do
                  Expect.equal (handle session sql |> snd) expected sql

          testCase "SET GLOBAL never changes the issuing session's own variable"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, ok = handle session "SET GLOBAL max_heap_table_size = 500"
              Expect.equal ok (Affected 0UL) "SET GLOBAL acks"

              // The session keeps the value it inherited at connection
              // time; a GLOBAL write only changes the default for later
              // sessions.
              match handle session "SELECT @@SESSION.max_heap_table_size" |> snd with
              | ResultSet(_, [ [ Some "16777216" ] ]) -> ()
              | other -> failtestf "SET GLOBAL must not change this session's own value, got %A" other

          testCase "SET rejects a syntactically valid unknown system variable"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "SET SESSION definitely_unknown = 1" |> snd with
              | Err(1193, message) -> Expect.stringContains message "definitely_unknown" "the unknown name is reported"
              | other -> failtestf "expected 1193, got %A" other

          testCase "SET GLOBAL x = y is visible to SELECT @@GLOBAL.x on the same connection"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET GLOBAL max_heap_table_size = 500"

              match handle session "SELECT @@GLOBAL.max_heap_table_size" |> snd with
              | ResultSet(_, [ [ Some "500" ] ]) -> ()
              | other -> failtestf "expected @@GLOBAL.max_heap_table_size = 500, got %A" other

          testCase "a new session inherits a SET GLOBAL made before it connected"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let setter = create 1 store
              let setter, _ = handle setter "SET GLOBAL max_heap_table_size = 500"
              ignore setter

              let newcomer = create 2 store

              Expect.equal
                  (newcomer.Variables |> Map.tryFind "max_heap_table_size" |> Option.flatten)
                  (Some "500")
                  "a session created after the GLOBAL write inherits it as its own session default"

          testCase "SET @@GLOBAL.x = y is equivalent to SET GLOBAL x = y"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "SET @@GLOBAL.max_heap_table_size = 777"
              ignore session

              let newcomer = create 2 store

              Expect.equal
                  (newcomer.Variables |> Map.tryFind "max_heap_table_size" |> Option.flatten)
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

          testCase "transaction access modes, chaining, and SET CHARACTER SET are enforced"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE tx_mode (id INT)"
              let session, started = handle session "START TRANSACTION READ ONLY"
              Expect.equal started (Affected 0UL) "read-only transaction starts"

              match handle session "INSERT INTO tx_mode VALUES (1)" with
              | session, Err(1792, _) ->
                  let session, chained = handle session "COMMIT AND CHAIN"
                  Expect.equal chained (Affected 0UL) "commit chains"
                  Expect.isTrue (session.Tx |> Option.exists _.ReadOnly) "chained transaction retains access mode"

                  let session, _ = handle session "COMMIT AND NO CHAIN"
                  Expect.isNone session.Tx "NO CHAIN ends the transaction"

                  let session, _ = handle session "SET TRANSACTION READ ONLY"
                  let session, _ = handle session "START TRANSACTION"
                  Expect.isTrue (session.Tx |> Option.exists _.ReadOnly) "configured access mode applies"
              | _, other -> failtestf "expected read-only error 1792, got %A" other

              match handle session "SET CHARACTER SET latin1" with
              | session, Affected 0UL ->
                  Expect.equal (session.Variables.["character_set_connection"]) (Some "latin1") "connection charset"
                  Expect.equal (session.Variables.["collation_connection"]) (Some "latin1_swedish_ci") "default collation"
              | _, other -> failtestf "expected SET CHARACTER SET to succeed, got %A" other

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
                      Expect.equal
                          names
                          [ "check_constraints"; "columns_priv"; "db"; "global_grants"; "tables_priv"; "triggers"; "user"; "views" ]
                          "the eight system tables"
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

          testCase "RENAME USER moves the account and its grants"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE DATABASE shop"
              let session, _ = handle session "CREATE USER 'alice'@'localhost' IDENTIFIED BY 'secret'"
              let session, _ = handle session "GRANT SELECT ON shop.* TO alice"

              match handle session "RENAME USER 'alice'@'localhost' TO 'bob'@'%'" |> snd with
              | Affected 0UL ->
                  Expect.isNone (Fsdb.Auth.tryUserRow store "alice") "old account removed"
                  Expect.isSome (Fsdb.Auth.tryUserRow store "bob") "new account created"

                  match Fsdb.Auth.check store "bob" [ "SELECT", Fsdb.Auth.OnDb "shop" ] with
                  | Ok() -> ()
                  | Error error -> failtestf "expected the renamed grant, got %A" error
              | other -> failtestf "expected RENAME USER to succeed, got %A" other

              let session, _ = handle session "CREATE USER carol"

              match handle session "RENAME USER bob TO carol" |> snd with
              | Err(1396, _) ->
                  Expect.isSome (Fsdb.Auth.tryUserRow store "bob") "source survives a destination collision"
                  Expect.isSome (Fsdb.Auth.tryUserRow store "carol") "destination survives a collision"
              | other -> failtestf "expected a destination collision to be 1396, got %A" other

          testCase "SHOW CREATE USER renders the stored authentication definition"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = create 1 store
              let session, _ = handle session "CREATE USER 'show_user'@'%' IDENTIFIED BY 'secret'"

              match handle session "SHOW CREATE USER 'show_user'@'%'" |> snd with
              | ResultSet([ column ], [ [ Some ddl ] ]) ->
                  Expect.equal column "CREATE USER for show_user@%" "column label"
                  Expect.stringContains ddl "CREATE USER `show_user`@`%` IDENTIFIED WITH 'mysql_native_password'" "account and plugin"
                  Expect.stringContains ddl (Fsdb.Auth.nativePasswordHash "secret") "stored password hash"
                  Expect.stringContains ddl "ACCOUNT UNLOCK" "account state"
              | other -> failtestf "expected SHOW CREATE USER row, got %A" other

              match handle session "SHOW CREATE USER missing" |> snd with
              | Err(1396, _) -> ()
              | other -> failtestf "expected missing account error 1396, got %A" other

          testCase "SET PASSWORD is enforced: own password is free, someone else's needs CREATE USER"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE USER mallory"
              let _root, _ = handle root "CREATE USER victim"

              let mallory = { create 2 store with User = "mallory" }

              match handle mallory "SET PASSWORD FOR victim = 'owned'" |> snd with
              | Err(1227, _) -> ()
              | other -> failtestf "expected changing another user's password to be 1227, got %A" other

              match handle mallory "SET PASSWORD = 'mine'" |> snd with
              | Affected 0UL ->
                  match Fsdb.Auth.tryUserRow store "mallory" with
                  | Some(cols, row) ->
                      Expect.equal
                          (Fsdb.Auth.storedPasswordHash cols row)
                          (Fsdb.Auth.nativePasswordHash "mine")
                          "own password change works without privileges"
                  | None -> failtest "mallory vanished"
              | other -> failtestf "expected own-password SET PASSWORD to succeed, got %A" other

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

              match handle worker "SHOW DATABASES" |> snd with
              | ResultSet(_, rows) ->
                  let names = rows |> List.map (List.head >> Option.get)
                  Expect.equal names [ "information_schema"; "shop" ] "only databases reachable through grants are visible"
              | other -> failtestf "expected filtered SHOW DATABASES, got %A" other

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

              // MySQL's grant-denial codes are level-shaped (oracle-verified):
              // 1142 for a table target, 1044 db, 1045 global.
              match handle worker "GRANT SELECT ON shop.secrets TO worker" |> snd with
              | Err(1142, _) -> ()
              | other -> failtestf "expected table-level GRANT without grant option to be 1142, got %A" other

              match handle worker "GRANT SELECT ON shop.* TO worker" |> snd with
              | Err(1044, _) -> ()
              | other -> failtestf "expected db-level GRANT without grant option to be 1044, got %A" other

              match handle worker "GRANT SELECT ON *.* TO worker" |> snd with
              | Err(1045, _) -> ()
              | other -> failtestf "expected global GRANT without grant option to be 1045, got %A" other

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

          testCase "WITH GRANT OPTION delegates at its own level, only for held privileges (MySQL-differential-verified)"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE shop"
              let root, _ = handle root "USE shop"
              let root, _ = handle root "CREATE TABLE t1 (id INT PRIMARY KEY)"
              let root, _ = handle root "CREATE USER dave"
              let root, _ = handle root "CREATE USER eve"
              let root, _ = handle root "CREATE USER carol"
              let root, _ = handle root "GRANT SELECT ON shop.* TO dave WITH GRANT OPTION"
              let root, _ = handle root "GRANT SELECT ON shop.t1 TO carol WITH GRANT OPTION"

              let dave = { create 2 store with User = "dave"; Database = Some "shop" }
              let carol = { create 3 store with User = "carol"; Database = Some "shop" }
              let eve = { create 4 store with User = "eve"; Database = Some "shop" }

              // db-scoped grant option delegates within the db...
              match handle dave "GRANT SELECT ON shop.* TO eve" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected dave's db-scoped delegation to work, got %A" other

              match handle eve "SELECT COUNT(*) FROM t1" |> snd with
              | ResultSet _ -> ()
              | other -> failtestf "expected eve's delegated SELECT to work, got %A" other

              // ...but not privileges the grantor doesn't hold (1044 at db
              // level), nor scopes above its own (1045 at global).
              match handle dave "GRANT INSERT ON shop.* TO eve" |> snd with
              | Err(1044, _) -> ()
              | other -> failtestf "expected granting an unheld privilege to be 1044, got %A" other

              match handle dave "GRANT SELECT ON *.* TO eve" |> snd with
              | Err(1045, _) -> ()
              | other -> failtestf "expected escalating to global to be 1045, got %A" other

              // Table-scoped grant option: that one table only.
              match handle carol "GRANT SELECT ON shop.t1 TO eve" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected carol's table-scoped delegation to work, got %A" other

              match handle carol "GRANT SELECT ON shop.* TO eve" |> snd with
              | Err(1044, _) -> ()
              | other -> failtestf "expected table-scoped option not to cover the db, got %A" other

              // The delegate can revoke what it could grant.
              match handle dave "REVOKE SELECT ON shop.* FROM eve" |> snd with
              | Affected 0UL -> ()
              | other -> failtestf "expected dave's revoke to work, got %A" other

          testCase "REVOKE ALL deletes the emptied grant rows — no ghost USAGE lines in SHOW GRANTS"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = create 1 store
              let root, _ = handle root "CREATE DATABASE shop"
              let root, _ = handle root "USE shop"
              let root, _ = handle root "CREATE TABLE t1 (id INT PRIMARY KEY)"
              let root, _ = handle root "CREATE USER gina"
              let root, _ = handle root "GRANT ALL PRIVILEGES ON shop.* TO gina"
              let root, _ = handle root "GRANT SELECT ON shop.t1 TO gina"
              let root, _ = handle root "REVOKE ALL PRIVILEGES ON shop.* FROM gina"
              let root, _ = handle root "REVOKE SELECT ON shop.t1 FROM gina"

              match handle root "SHOW GRANTS FOR gina" |> snd with
              | ResultSet(_, rows) ->
                  Expect.equal
                      (rows |> List.map (List.head >> Option.get))
                      [ "GRANT USAGE ON *.* TO `gina`@`%`" ]
                      "only the global USAGE line remains, like MySQL"
              | other -> failtestf "expected gina's grants, got %A" other

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
              | Error e -> failtestf "expected mysql.db to scan, got %A" e

          testCase "SqlError surfaces its chosen code and message and still aborts the transaction"
          <| fun _ ->
              let boom =
                  Fsdb.Functions.ScalarFunction.create "BOOM" (fun _ _ -> raise (Fsdb.Functions.SqlError(1210, "no such model")))

              let session =
                  { create 1 (Fsdb.Storage.create ()) with
                      CustomFunctions = Fsdb.Functions.empty |> Fsdb.Functions.registerExtension boom }

              let session, _ = handle session "CREATE TABLE t (n INT)"
              let session, _ = handle session "BEGIN"
              let session, _ = handle session "INSERT INTO t VALUES (1)"
              let session, result = handle session "SELECT BOOM()"

              match result with
              | Err(1210, msg) -> Expect.stringContains msg "no such model" "the chosen message reaches the client"
              | other -> failtestf "expected the chosen 1210, got %A" other

              Expect.isNone session.Tx "the transaction aborts, same as any other throwing function"

              match handle session "SELECT COUNT(*) FROM t" |> snd with
              | ResultSet(_, [ [ Some "0" ] ]) -> ()
              | other -> failtestf "expected the aborted transaction's INSERT rolled back, got %A" other

          testCase "a DirectOnly function is rejected inside a generated column definition but fine in SELECT"
          <| fun _ ->
              let embeddish =
                  Fsdb.Functions.ScalarFunction.create "EMBEDDISH" (fun _ _ -> VString "x")
                  |> Fsdb.Functions.ScalarFunction.effectful

              let session =
                  { create 1 (Fsdb.Storage.create ()) with
                      CustomFunctions = Fsdb.Functions.empty |> Fsdb.Functions.registerExtension embeddish }

              match handle session "SELECT EMBEDDISH('a')" |> snd with
              | ResultSet(_, [ [ Some "x" ] ]) -> ()
              | other -> failtestf "expected the effectful function to run in a plain SELECT, got %A" other

              match handle session "CREATE TABLE d (a VARCHAR(10), b VARCHAR(10) AS (EMBEDDISH(a)))" |> snd with
              | Err(3102, msg) -> Expect.stringContains msg "generated column 'b'" "names the offending column"
              | other -> failtestf "expected 3102 at CREATE time, got %A" other

              let session, _ = handle session "CREATE TABLE d2 (a VARCHAR(10))"

              match handle session "ALTER TABLE d2 ADD COLUMN b VARCHAR(10) AS (EMBEDDISH(a))" |> snd with
              | Err(3102, _) -> ()
              | other -> failtestf "expected 3102 at ALTER time, got %A" other

              // A subquery smuggles the call past the DDL traversal — the
              // eval-time backstop must still refuse to invoke the function
              // when the engine evaluates the generated column on INSERT.
              let session, createResult =
                  handle session "CREATE TABLE d3 (a VARCHAR(10), b VARCHAR(10) AS ((SELECT EMBEDDISH(a))))"

              match createResult with
              | Affected _ -> ()
              | other -> failtestf "expected the subquery-smuggled definition to slip past DDL, got %A" other

              match handle session "INSERT INTO d3 (a) VALUES ('x')" |> snd with
              | Err(3102, msg) -> Expect.stringContains msg "EMBEDDISH" "names the offending function"
              | other -> failtestf "expected the eval-time backstop's 3102 on INSERT, got %A" other

              match handle session "SELECT EMBEDDISH('a')" |> snd with
              | ResultSet(_, [ [ Some "x" ] ]) -> ()
              | other -> failtestf "expected direct SELECT still fine after the backstop fired, got %A" other

          testCase "a rich function's QueryContext agrees with DATABASE() and CURRENT_USER()"
          <| fun _ ->
              let probe =
                  Fsdb.Functions.ScalarFunction.create "CTXPROBE" (fun ctx _ ->
                      VString(sprintf "%s|%s" (ctx.Database |> Option.defaultValue "<none>") ctx.User))

              let session =
                  { create 1 (Fsdb.Storage.create ()) with
                      CustomFunctions = Fsdb.Functions.empty |> Fsdb.Functions.registerExtension probe }

              let session, _ = handle session "CREATE DATABASE app"
              let session, _ = handle session "USE app"

              match handle session "SELECT CTXPROBE(), DATABASE(), CURRENT_USER()" |> snd with
              | ResultSet(_, [ [ Some ctx; Some db; Some user ] ]) ->
                  Expect.equal ctx (db + "|" + (user.Split '@').[0]) "context Database/User match the SQL-visible session"
              | other -> failtestf "expected one row, got %A" other

          testCase "a bare ? over COM_QUERY, incl. in a DDL generated column, is a 1064 (never reaches storage)"
          <| fun _ ->
              let session = create 991001 (Fsdb.Storage.create ())

              match handle session "SELECT ?" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected 1064 for a bare ?, got %A" other

              // The crash vector: a placeholder in a generated-column DDL
              // expression must not survive into Storage/Persistence.
              match handle session "CREATE TABLE t (a INT, b INT AS (?))" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected 1064 for a DDL placeholder, got %A" other

              match handle session "SHOW TABLES FROM d WHERE Tables_in_d = ?" |> snd with
              | Err(1064, _) -> ()
              | other -> failtestf "expected 1064 for a probe placeholder, got %A" other

          testCase "prepareStatement rejects a placeholder the binder can't reach (DDL generated column)"
          <| fun _ ->
              match prepareStatement "CREATE TABLE t (a INT, b INT AS (?))" with
              | Result.Error(1064, _) -> ()
              | other -> failtestf "expected a 1064 prepare error, got %A" other

          testCase "redactSql hides credential statements and string/number literals"
          <| fun _ ->
              Expect.equal
                  (Fsdb.Log.redactSql "CREATE USER 'a'@'%' IDENTIFIED BY 'hunter2'")
                  "[REDACTED CREDENTIAL STATEMENT]"
                  "credential statement collapses whole"

              Expect.equal
                  (Fsdb.Log.redactSql "SET PASSWORD FOR 'a' = 'secret'")
                  "[REDACTED CREDENTIAL STATEMENT]"
                  "SET PASSWORD collapses"

              Expect.equal
                  (Fsdb.Log.redactSql "SELECT * FROM t WHERE token = 'secret' AND n = 42")
                  "SELECT * FROM t WHERE token = ? AND n = ?"
                  "string and number literals become ?"

              Expect.equal
                  (Fsdb.Log.redactSql "SELECT `id_1` FROM `t2`")
                  "SELECT `id_1` FROM `t2`"
                  "backticked identifiers and their digits are kept" ]
