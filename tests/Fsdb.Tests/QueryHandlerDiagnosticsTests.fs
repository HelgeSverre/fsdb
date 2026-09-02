module Fsdb.Tests.QueryHandlerDiagnosticsTests

open System
open Expecto
open Fsdb.Packet
open Fsdb.Protocol
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let private note code message = Fsdb.Diagnostics.Note, code, message

let private conditionTriples session =
    session.Diagnostics
    |> List.map (fun condition -> condition.Level, condition.Code, condition.Message)

let private expectAffectedWithConditions context expected (session, result) =
    Expect.equal result (Affected 0UL) context
    Expect.equal (conditionTriples session) expected (context + " diagnostics")
    session

let tests =
    testList
        "Diagnostics"
        [ testCase "SHOW WARNINGS LIMIT n is accepted, matching the mysql CLI's/mysqli's routine probe"
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

          testCase "conditional schema and table DDL records MySQL notes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE DATABASE diagnostics_db"
              let session =
                  handle session "CREATE DATABASE IF NOT EXISTS diagnostics_db"
                  |> expectAffectedWithConditions
                      "existing database is ignored"
                      [ note 1007 "Can't create database 'diagnostics_db'; database exists" ]

              let session, _ = handle session "CREATE TABLE diagnostics_table (id INT)"
              let session =
                  handle session "CREATE TABLE IF NOT EXISTS diagnostics_table (other INT)"
                  |> expectAffectedWithConditions
                      "existing table is ignored"
                      [ note 1050 "Table 'diagnostics_table' already exists" ]

              let session, _ = handle session "CREATE TABLE diagnostics_source (id INT)"
              let session =
                  handle session "CREATE TABLE IF NOT EXISTS diagnostics_table AS SELECT id FROM diagnostics_source"
                  |> expectAffectedWithConditions
                      "existing table skips CREATE TABLE AS"
                      [ note 1050 "Table 'diagnostics_table' already exists" ]

              let session =
                  handle session "CREATE TABLE IF NOT EXISTS diagnostics_table LIKE diagnostics_source"
                  |> expectAffectedWithConditions
                      "existing table skips CREATE TABLE LIKE"
                      [ note 1050 "Table 'diagnostics_table' already exists" ]

              let session =
                  handle session "DROP TABLE IF EXISTS absent_one, absent_two"
                  |> expectAffectedWithConditions
                      "missing tables are ignored"
                      [ note 1051 "Unknown table 'fsdb.absent_one'"
                        note 1051 "Unknown table 'fsdb.absent_two'" ]

              let session, result = handle session "DROP DATABASE IF EXISTS absent_database"
              Expect.equal result (Affected 0UL) "missing database is ignored"
              Expect.isEmpty session.Diagnostics "DROP DATABASE IF EXISTS remains silent"

          testCase "conditional object and account DDL records MySQL notes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session =
                  handle session "DROP VIEW IF EXISTS absent_view"
                  |> expectAffectedWithConditions "missing view is ignored" [ note 1051 "Unknown table 'fsdb.absent_view'" ]

              let session =
                  handle session "DROP TRIGGER IF EXISTS absent_trigger"
                  |> expectAffectedWithConditions "missing trigger is ignored" [ note 1360 "Trigger does not exist" ]

              let session, _ = handle session "CREATE PROCEDURE present_procedure() SELECT 1 AS value"
              let session =
                  handle session "CREATE PROCEDURE IF NOT EXISTS present_procedure() SELECT 2 AS value"
                  |> expectAffectedWithConditions
                      "existing procedure is ignored"
                      [ note 1304 "PROCEDURE present_procedure already exists" ]

              match handle session "CALL present_procedure()" |> snd with
              | MultipleResults [ (ResultSet([ "value" ], [ [ Some "1" ] ]), _); (Affected _, []) ] -> ()
              | other -> failtestf "expected the original procedure body, got %A" other

              let session =
                  handle session "DROP PROCEDURE IF EXISTS absent_procedure"
                  |> expectAffectedWithConditions
                      "missing procedure is ignored"
                      [ note 1305 "PROCEDURE fsdb.absent_procedure does not exist" ]

              let session =
                  handle session "DROP FUNCTION IF EXISTS absent_function"
                  |> expectAffectedWithConditions
                      "missing function is ignored"
                      [ note 1305 "FUNCTION fsdb.absent_function does not exist" ]

              let session =
                  handle session "DROP EVENT IF EXISTS absent_event"
                  |> expectAffectedWithConditions "missing event is ignored" [ note 1305 "Event absent_event does not exist" ]

              let session, _ = handle session "CREATE USER 'present_user'@'localhost'"
              let session =
                  handle session "CREATE USER IF NOT EXISTS 'present_user'@'localhost'"
                  |> expectAffectedWithConditions
                      "existing user is ignored"
                      [ note 3163 "Authorization ID 'present_user'@'localhost' already exists." ]

              let session =
                  handle session "ALTER USER IF EXISTS 'absent_user'@'localhost' IDENTIFIED BY 'secret'"
                  |> expectAffectedWithConditions
                      "missing user alteration is ignored"
                      [ note 3162 "Authorization ID 'absent_user'@'localhost' does not exist." ]

              let session =
                  handle session "DROP USER IF EXISTS 'absent_user'@'localhost'"
                  |> expectAffectedWithConditions
                      "missing user drop is ignored"
                      [ note 3162 "Authorization ID 'absent_user'@'localhost' does not exist." ]

              let session, _ = handle session "CREATE ROLE 'present_role'"
              let session =
                  handle session "CREATE ROLE IF NOT EXISTS 'present_role'"
                  |> expectAffectedWithConditions
                      "existing role is ignored"
                      [ note 3163 "Authorization ID 'present_role'@'%' already exists." ]

              handle session "DROP ROLE IF EXISTS 'absent_role'"
              |> expectAffectedWithConditions
                  "missing role drop is ignored"
                  [ note 3162 "Authorization ID 'absent_role'@'%' does not exist." ]
              |> ignore

          testCase "GET CURRENT DIAGNOSTICS assigns statement and condition information"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE diagnostics_row (id INT PRIMARY KEY)"
              let session, _ = handle session "INSERT INTO diagnostics_row VALUES (1)"

              let session, result =
                  handle session "GET CURRENT DIAGNOSTICS @condition_count = NUMBER, @affected = ROW_COUNT;"

              Expect.equal result (Affected 0UL) "statement diagnostics"

              match handle session "SELECT @condition_count, @affected" |> snd with
              | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected statement diagnostics, got %A" other

              let session, _ = handle session "SET @kept = 41"
              let session, result = handle session "GET CURRENT DIAGNOSTICS CONDITION 2 @kept = MESSAGE_TEXT"

              Expect.equal result (Affected 0UL) "invalid condition number succeeds"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1758 ] "invalid condition becomes current"
              Expect.equal (session.Diagnostics |> List.map _.State) [ "35000" ] "invalid condition SQLSTATE"

              let session, _ =
                  handle
                      session
                      "GET CURRENT DIAGNOSTICS CONDITION 1 @diagnostic_code = MYSQL_ERRNO, @diagnostic_message = MESSAGE_TEXT"

              let session, _ =
                  handle session "GET CURRENT DIAGNOSTICS CONDITION 0x1 @hex_code = MYSQL_ERRNO"

              let session, _ =
                  handle session "GET CURRENT DIAGNOSTICS CONDITION 1e0 @exponent_code = MYSQL_ERRNO"

              let session, _ =
                  handle session "GET CURRENT DIAGNOSTICS CONDITION '1' @text_code = MYSQL_ERRNO"

              match
                  handle
                      session
                      "SELECT @kept, @diagnostic_code, @diagnostic_message, @hex_code, @exponent_code, @text_code"
                  |> snd
              with
              | ResultSet(
                  _,
                  [ [ Some "41"
                      Some "1758"
                      Some "Invalid condition number"
                      Some "1758"
                      Some "1758"
                      Some "1758" ] ]
                ) ->
                  ()
              | other -> failtestf "expected condition diagnostics, got %A" other

              match handle session "GET STACKED DIAGNOSTICS @condition_count = NUMBER" |> snd |> errorInfo with
              | Some error ->
                  Expect.equal error.Code 3004 "stacked error code"
                  Expect.equal error.State "0Z002" "stacked SQLSTATE"
                  Expect.equal error.Message "GET STACKED DIAGNOSTICS when handler not active" "stacked error text"
              | None -> failtest "expected GET STACKED DIAGNOSTICS to fail outside a handler"

          testCase "INSERT IGNORE records warnings until the next ordinary statement"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT PRIMARY KEY, value INT NOT NULL)"
              let session, _ = handle session "INSERT INTO t VALUES (1, 1)"
              let session, result = handle session "INSERT IGNORE INTO t VALUES (1, 2), (2, NULL)"

              Expect.equal result (Affected 0UL) "ignored rows do not affect the count"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1062; 1048 ] "one condition per ignored row"

              match handle session "SHOW WARNINGS" |> snd with
              | ResultSet([ "Level"; "Code"; "Message" ], [ [ Some "Warning"; Some "1062"; _ ]; [ Some "Warning"; Some "1048"; _ ] ]) -> ()
              | other -> failtestf "expected INSERT IGNORE warnings, got %A" other

              match handle session "SELECT @@warning_count" |> snd with
              | ResultSet(_, [ [ Some "2" ] ]) -> ()
              | other -> failtestf "expected warning count 2, got %A" other

              let session, _ = handle session "SELECT 1"
              Expect.isEmpty session.Diagnostics "ordinary statements replace the diagnostics area"

          testCase "non-strict omitted columns report their implicit defaults"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "SET sql_mode = 'NO_ENGINE_SUBSTITUTION'"
              let session, _ = handle session "CREATE TABLE t (id INT, name VARCHAR(10) NOT NULL)"
              let session, result = handle session "INSERT INTO t (id) VALUES (1)"
              Expect.equal result (Affected 1UL) "insert succeeds"

              match session.Diagnostics with
              | [ { Level = Fsdb.Diagnostics.Warning; Code = 1364; Message = "Field 'name' doesn't have a default value" } ] -> ()
              | other -> failtestf "expected the implicit-default warning, got %A" other

              match handle session "SELECT name FROM t" |> snd with
              | ResultSet(_, [ [ Some "" ] ]) -> ()
              | other -> failtestf "expected the implicit empty string, got %A" other

          testCase "statement errors appear in SHOW ERRORS and SHOW WARNINGS"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT)"
              let session, result = handle session "SELECT missing FROM t"

              match result with
              | Err(1054, _) -> ()
              | other -> failtestf "expected an unknown-column error, got %A" other

              match handle session "SHOW ERRORS" |> snd with
              | ResultSet(_, [ [ Some "Error"; Some "1054"; _ ] ]) -> ()
              | other -> failtestf "expected one error condition, got %A" other

              match handle session "SHOW COUNT(*) WARNINGS" |> snd with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected warning count to include errors, got %A" other

          testCase "division by zero follows the session sql mode in reads"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "SELECT 1 / 0, 1 DIV 0, MOD(1, 0)"

              match result with
              | ResultSet(_, [ [ None; None; None ] ]) -> ()
              | other -> failtestf "expected NULL division results, got %A" other

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Warning, 1365, "Division by 0"
                    Fsdb.Diagnostics.Warning, 1365, "Division by 0"
                    Fsdb.Diagnostics.Warning, 1365, "Division by 0" ]
                  "default mode reports each zero divisor"

              let session, result = handle session "SELECT NULL / 0, NULL DIV 0, MOD(NULL, 0), 1 / NULL"

              match result with
              | ResultSet(_, [ [ None; None; None; None ] ]) -> ()
              | other -> failtestf "expected NULL arithmetic to remain NULL, got %A" other

              Expect.isEmpty session.Diagnostics "NULL arithmetic does not report division by zero"

              let session, _ = handle session "SET SESSION sql_mode = 'STRICT_TRANS_TABLES'"
              let session, result = handle session "SELECT 1 / 0"

              match result with
              | ResultSet(_, [ [ None ] ]) -> ()
              | other -> failtestf "expected mode-disabled division to remain NULL, got %A" other

              Expect.isEmpty session.Diagnostics "disabled ERROR_FOR_DIVISION_BY_ZERO is silent"

          testCase "strict writes reject division by zero unless errors are ignored"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT PRIMARY KEY, value INT NULL)"
              let session, _ = handle session "INSERT INTO t VALUES (1, 10)"
              let session, result = handle session "INSERT INTO t VALUES (2, NULL / 0)"
              Expect.equal result (Affected 1UL) "NULL arithmetic remains valid in a strict write"
              Expect.isEmpty session.Diagnostics "NULL arithmetic remains silent in a strict write"

              let session, result = handle session "INSERT INTO t VALUES (9, 1 / 0)"

              match result with
              | Err(1365, "Division by 0") -> ()
              | other -> failtestf "expected strict INSERT division error, got %A" other

              let session, result = handle session "UPDATE t SET value = 99 WHERE 1 DIV 0"

              match result with
              | Err(1365, "Division by 0") -> ()
              | other -> failtestf "expected strict UPDATE predicate division error, got %A" other

              let session, result = handle session "INSERT IGNORE INTO t VALUES (3, MOD(1, 0))"
              Expect.equal result (Affected 1UL) "IGNORE retains the row"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1365 ] "IGNORE downgrades the error"

              let session, _ = handle session "SET SESSION sql_mode = 'ERROR_FOR_DIVISION_BY_ZERO'"
              let session, result = handle session "INSERT INTO t VALUES (4, 1 / 0)"
              Expect.equal result (Affected 1UL) "non-strict mode retains the row"
              Expect.equal (session.Diagnostics |> List.map _.Code) [ 1365 ] "non-strict mode reports a warning"

              let session, _ = handle session "SET SESSION sql_mode = 'STRICT_TRANS_TABLES'"
              let session, result = handle session "INSERT INTO t VALUES (5, 1 / 0)"
              Expect.equal result (Affected 1UL) "disabled error mode retains the row"
              Expect.isEmpty session.Diagnostics "disabled error mode is silent"

              let session, _ = handle session "SET SESSION sql_mode = 'STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO'"
              let session, _ = handle session "START TRANSACTION"
              let session, _ = handle session "INSERT INTO t VALUES (6, 60)"
              let session, result = handle session "INSERT INTO t VALUES (7, 1 / 0)"

              match result with
              | Err(1365, "Division by 0") -> ()
              | other -> failtestf "expected the transaction statement to fail, got %A" other

              let session, result = handle session "INSERT INTO t VALUES (8, 80)"
              Expect.equal result (Affected 1UL) "the transaction remains usable after the statement error"
              let session, result = handle session "COMMIT"
              Expect.equal result (Affected 0UL) "the surviving transaction writes commit"

              match handle session "SELECT id, value FROM t ORDER BY id" |> snd with
              | ResultSet(
                  _,
                  [ [ Some "1"; Some "10" ]
                    [ Some "2"; None ]
                    [ Some "3"; None ]
                    [ Some "4"; None ]
                    [ Some "5"; None ]
                    [ Some "6"; Some "60" ]
                    [ Some "8"; Some "80" ] ]
                ) ->
                  ()
              | other -> failtestf "expected only successful writes, got %A" other

          testCase "GROUP_CONCAT truncation records warning 1260"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT, value VARCHAR(10))"
              let session, _ = handle session "INSERT INTO t VALUES (1, 'aa'), (2, 'bb')"
              let session, _ = handle session "SET group_concat_max_len = 4"
              let session, result = handle session "SELECT GROUP_CONCAT(value ORDER BY id SEPARATOR '-') FROM t"

              match result with
              | ResultSet(_, [ [ Some "aa-b" ] ]) -> ()
              | other -> failtestf "expected truncated GROUP_CONCAT result, got %A" other

              match session.Diagnostics with
              | [ { Level = Fsdb.Diagnostics.Warning; Code = 1260; Message = message } ] ->
                  Expect.equal message "Row 2 was cut by GROUP_CONCAT()" "MySQL warning text"
              | other -> failtestf "expected one GROUP_CONCAT warning, got %A" other

          testCase "UPDATE IGNORE records a skipped CHECK violation"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (id INT, CHECK (id > 0))"
              let session, _ = handle session "INSERT INTO t VALUES (1)"
              let session, result = handle session "UPDATE IGNORE t SET id = 0"

              Expect.equal result (Affected 0UL) "ignored CHECK violations leave the row unchanged"

              match session.Diagnostics with
              | [ { Level = Fsdb.Diagnostics.Warning; Code = 3819; Message = _ } ] -> ()
              | other -> failtestf "expected one UPDATE IGNORE warning, got %A" other

          testCase "non-strict inserts retain conversion and truncation conditions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (i INT, u TINYINT UNSIGNED, e ENUM('a', 'b'), s SET('a', 'b'))"
              let session, _ = handle session "SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION'"

              let session, result = handle session "INSERT INTO t VALUES ('abc', 300, 'x', 'a,x')"
              Expect.equal result (Affected 1UL) "non-strict coercions retain the row"

              let conditions =
                  session.Diagnostics
                  |> List.map (fun condition -> condition.Level, condition.Code, condition.Message)

              Expect.equal
                  conditions
                  [ Fsdb.Diagnostics.Warning, 1366, "Incorrect integer value: 'abc' for column 'i' at row 1"
                    Fsdb.Diagnostics.Warning, 1264, "Out of range value for column 'u' at row 1"
                    Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 'e' at row 1"
                    Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 's' at row 1" ]
                  "MySQL condition codes and messages"

              match handle session "SHOW WARNINGS LIMIT 1, 2" |> snd with
              | ResultSet(_, [ [ Some "Warning"; Some "1264"; _ ]; [ Some "Warning"; Some "1265"; _ ] ]) -> ()
              | other -> failtestf "expected limited conditions, got %A" other

              match handle session "SELECT i, u, e, s FROM t" |> snd with
              | ResultSet(_, [ [ Some "0"; Some "255"; Some ""; Some "a" ] ]) -> ()
              | other -> failtestf "expected MySQL non-strict stored values, got %A" other

          testCase "prepared inserts replace prior diagnostics with conversion conditions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (i INT)"
              let session, _ = handle session "SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION'"
              let session, _ = handle session "INSERT INTO t VALUES ('abc')"

              match prepareStatement "INSERT INTO t VALUES (?)" with
              | Ok(Some ast, 1) ->
                  let prepared =
                      { Ast = Some ast
                        Sql = "INSERT INTO t VALUES (?)"
                        ParamCount = 1
                        LastParamTypes = None }

                  let session, result = executePrepared session prepared [ VString "abc" ]
                  Expect.equal result (Affected 1UL) "prepared insert succeeds"

                  match session.Diagnostics with
                  | [ { Level = Fsdb.Diagnostics.Warning; Code = 1366; Message = "Incorrect integer value: 'abc' for column 'i' at row 1" } ] -> ()
                  | other -> failtestf "expected prepared conversion condition, got %A" other
              | other -> failtestf "expected one prepared parameter, got %A" other

          testCase "non-strict multi-row inserts retain source row numbers"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (i INT)"
              let session, _ = handle session "SET SESSION sql_mode = 'NO_ENGINE_SUBSTITUTION'"
              let session, _ = handle session "INSERT INTO t VALUES ('one'), ('two')"

              Expect.equal
                  (session.Diagnostics |> List.map _.Message)
                  [ "Incorrect integer value: 'one' for column 'i' at row 1"
                    "Incorrect integer value: 'two' for column 'i' at row 2" ]
                  "condition rows match the VALUES source rows"

          testCase "DECIMAL scale loss records MySQL note conditions"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (d DECIMAL(5, 2))"
              let session, result = handle session "INSERT INTO t VALUES (12.345), (67.891)"
              Expect.equal result (Affected 2UL) "rounded values insert"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Note, 1265, "Data truncated for column 'd' at row 1"
                    Fsdb.Diagnostics.Note, 1265, "Data truncated for column 'd' at row 2" ]
                  "one MySQL note per rounded source value"

              match handle session "SHOW WARNINGS" |> snd with
              | ResultSet(_, [ [ Some "Note"; Some "1265"; Some "Data truncated for column 'd' at row 1" ]; [ Some "Note"; Some "1265"; Some "Data truncated for column 'd' at row 2" ] ]) -> ()
              | other -> failtestf "expected DECIMAL notes, got %A" other

              match handle session "SELECT d FROM t" |> snd with
              | ResultSet(_, [ [ Some "12.35" ]; [ Some "67.89" ] ]) -> ()
              | other -> failtestf "expected rounded DECIMAL values, got %A" other

          testCase "prepared DECIMAL inserts replace prior diagnostics with scale-loss notes"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (d DECIMAL(5, 2))"
              let session, _ = handle session "INSERT INTO t VALUES (12.345)"

              match prepareStatement "INSERT INTO t VALUES (?)" with
              | Ok(Some ast, 1) ->
                  let prepared =
                      { Ast = Some ast
                        Sql = "INSERT INTO t VALUES (?)"
                        ParamCount = 1
                        LastParamTypes = None }

                  let session, result = executePrepared session prepared [ VDecimal 67.891M ]
                  Expect.equal result (Affected 1UL) "prepared rounded value inserts"

                  match session.Diagnostics with
                  | [ { Level = Fsdb.Diagnostics.Note; Code = 1265; Message = "Data truncated for column 'd' at row 1" } ] -> ()
                  | other -> failtestf "expected prepared scale-loss note, got %A" other
              | other -> failtestf "expected one prepared parameter, got %A" other

          testCase "declared text and binary widths retain non-strict truncation warnings"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (v VARCHAR(3), c CHAR(3), b BINARY(3), vb VARBINARY(3))"
              let session, _ = handle session "SET SESSION sql_mode = ''"
              let session, result = handle session "INSERT INTO t VALUES ('abcd', 'wxyz', 'abcd', 'wxyz')"
              Expect.equal result (Affected 1UL) "truncated values insert"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 'v' at row 1"
                    Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 'c' at row 1"
                    Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 'b' at row 1"
                    Fsdb.Diagnostics.Warning, 1265, "Data truncated for column 'vb' at row 1" ]
                  "MySQL reports each shortened value"

              match handle session "SELECT v, c, HEX(b), HEX(vb) FROM t" |> snd with
              | ResultSet(_, [ [ Some "abc"; Some "wxy"; Some "616263"; Some "777879" ] ]) -> ()
              | other -> failtestf "expected truncated text and bytes, got %A" other

          testCase "strict declared text width returns MySQL's data-too-long error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (v VARCHAR(3))"

              match handle session "INSERT INTO t VALUES ('abcd')" |> snd with
              | Err(1406, "Data too long for column 'v' at row 1") -> ()
              | other -> failtestf "expected MySQL's data-too-long error, got %A" other

              match handle session "INSERT INTO t VALUES ('ok'), ('abcd')" |> snd with
              | Err(1406, "Data too long for column 'v' at row 2") -> ()
              | other -> failtestf "expected a source-row error, got %A" other

          testCase "CHAR removes trailing spaces while VARCHAR preserves them"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (c CHAR(3), v VARCHAR(3))"
              let session, result = handle session "INSERT INTO t VALUES ('x  ', 'x  ')"
              Expect.equal result (Affected 1UL) "space-padded values insert"

              match handle session "SELECT c, LENGTH(c), v, LENGTH(v) FROM t" |> snd with
              | ResultSet(_, [ [ Some "x"; Some "1"; Some "x  "; Some "3" ] ]) -> ()
              | other -> failtestf "expected CHAR to trim and VARCHAR to retain trailing spaces, got %A" other

          testCase "ALTER text widths count Unicode scalar values"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (v VARCHAR(2))"
              let session, _ = handle session "INSERT INTO t VALUES ('😀')"
              let session, result = handle session "ALTER TABLE t MODIFY v VARCHAR(1)"
              Expect.equal result (Affected 0UL) "one scalar value fits VARCHAR(1)"

              match handle session "SELECT v FROM t" |> snd with
              | ResultSet(_, [ [ Some "😀" ] ]) -> ()
              | other -> failtestf "expected the supplementary-plane scalar to survive ALTER, got %A" other

          testCase "over-width text defaults are invalid in every sql mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              match handle session "CREATE TABLE strict_default (v VARCHAR(3) DEFAULT 'abcd')" |> snd with
              | Err(1067, "Invalid default value for 'v'") -> ()
              | other -> failtestf "expected MySQL's strict invalid-default error, got %A" other

              let session, _ = handle session "SET SESSION sql_mode = ''"

              match handle session "CREATE TABLE nonstrict_default (v VARCHAR(3) DEFAULT 'abcd')" |> snd with
              | Err(1067, "Invalid default value for 'v'") -> ()
              | other -> failtestf "expected MySQL's non-strict invalid-default error, got %A" other

              let session, _ = handle session "CREATE TABLE alter_default (v VARCHAR(3))"

              match handle session "ALTER TABLE alter_default ALTER COLUMN v SET DEFAULT 'abcd'" |> snd with
              | Err(1067, "Invalid default value for 'v'") -> ()
              | other -> failtestf "expected MySQL's ALTER invalid-default error, got %A" other

          testCase "DECIMAL defaults retain scale-loss notes in every sql mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, result = handle session "CREATE TABLE strict_default (d DECIMAL(5, 2) DEFAULT 1.234)"
              Expect.equal result (Affected 0UL) "strict DECIMAL default creates"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Note, 1265, "Data truncated for column 'd' at row 1" ]
                  "strict default reports MySQL's scale-loss note"

              let session, _ = handle session "SET SESSION sql_mode = ''"
              let session, result = handle session "CREATE TABLE nonstrict_default (d DECIMAL(5, 2) DEFAULT 1.234)"
              Expect.equal result (Affected 0UL) "non-strict DECIMAL default creates"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Note, 1265, "Data truncated for column 'd' at row 1" ]
                  "non-strict default reports MySQL's scale-loss note"

          testCase "binary and lossy charset defaults are invalid in every sql mode"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())

              let expectInvalid session sql =
                  match handle session sql |> snd with
                  | Err(1067, "Invalid default value for 'v'") -> ()
                  | other -> failtestf "expected MySQL's invalid-default error, got %A" other

              expectInvalid session "CREATE TABLE strict_binary (v BINARY(3) DEFAULT X'61626364')"
              expectInvalid session "CREATE TABLE strict_varbinary (v VARBINARY(3) DEFAULT X'61626364')"
              expectInvalid session "CREATE TABLE strict_ascii (v VARCHAR(3) CHARACTER SET ascii DEFAULT 'å')"
              expectInvalid session "CREATE TABLE strict_latin1 (v VARCHAR(3) CHARACTER SET latin1 DEFAULT '😀')"

              let session, _ = handle session "SET SESSION sql_mode = ''"
              expectInvalid session "CREATE TABLE nonstrict_binary (v BINARY(3) DEFAULT X'61626364')"
              expectInvalid session "CREATE TABLE nonstrict_varbinary (v VARBINARY(3) DEFAULT X'61626364')"
              expectInvalid session "CREATE TABLE nonstrict_ascii (v VARCHAR(3) CHARACTER SET ascii DEFAULT 'å')"
              expectInvalid session "CREATE TABLE nonstrict_latin1 (v VARCHAR(3) CHARACTER SET latin1 DEFAULT '😀')"

          testCase "lossy column charsets retain MySQL conversion warnings"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (a VARCHAR(20) CHARACTER SET ascii, l VARCHAR(20) CHARACTER SET latin1)"
              let session, _ = handle session "SET SESSION sql_mode = ''"
              let session, result = handle session "INSERT INTO t VALUES ('xåy', 'x😀y')"
              Expect.equal result (Affected 1UL) "lossy conversions insert"

              Expect.equal
                  (session.Diagnostics |> List.map (fun condition -> condition.Level, condition.Code, condition.Message))
                  [ Fsdb.Diagnostics.Warning, 1366, "Incorrect string value: '\\xC3\\xA5y' for column 'a' at row 1"
                    Fsdb.Diagnostics.Warning, 1366, "Incorrect string value: '\\xF0\\x9F\\x98\\x80y' for column 'l' at row 1" ]
                  "MySQL reports the UTF-8 suffix from the first unrepresentable character"

              match handle session "SELECT a, l FROM t" |> snd with
              | ResultSet(_, [ [ Some "x?y"; Some "x?y" ] ]) -> ()
              | other -> failtestf "expected replacement characters, got %A" other

          testCase "strict column charsets return MySQL's conversion error"
          <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              let session, _ = handle session "CREATE TABLE t (a VARCHAR(20) CHARACTER SET ascii)"

              match handle session "INSERT INTO t VALUES ('å')" |> snd with
              | Err(1366, "Incorrect string value: '\\xC3\\xA5' for column 'a' at row 1") -> ()
              | other -> failtestf "expected MySQL's incorrect-string error, got %A" other

        ]
