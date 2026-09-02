module Fsdb.Tests.TriggerTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor
open Fsdb.QueryHandler

let private run = TestSupport.Sql.execute
let private runDefault = TestSupport.Sql.executeDefault
let private expectOk = TestSupport.Sql.expectOk
let private rows = TestSupport.Sql.rows

/// t (the trigger's subject) + log (what the body writes into).
let private setup (store: Store) =
    expectOk (runDefault store "CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, n INT)") "create t"
    expectOk (runDefault store "CREATE TABLE log (id INT AUTO_INCREMENT PRIMARY KEY, n INT)") "create log"

let private step session sql =
    let next, result = handle session sql
    expectOk result sql
    next

/// MySQL 8.4.11's exact 1442 text (write-probed on the disposable server).
let private text1442 (table: string) =
    sprintf
        "Can't update table '%s' in stored function/trigger because it is already used by statement which invoked this stored function/trigger."
        table

let tests =
    testList
        "triggers"
        [ testCase "CREATE TRIGGER retains its timing and event"
          <| fun _ ->
              match
                  Fsdb.Parser.parse
                      "CREATE TRIGGER before_delete BEFORE DELETE ON t FOR EACH ROW DELETE FROM log WHERE n = OLD.n"
              with
              | Ok(Fsdb.Ast.CreateTrigger creation) ->
                  Expect.equal creation.Name "before_delete" "name"
                  Expect.isFalse creation.IfNotExists "unconditional creation"
                  Expect.equal creation.Timing Fsdb.Ast.Before "timing"
                  Expect.equal creation.Event Fsdb.Ast.TriggerDelete "event"
                  Expect.equal creation.Table "t" "table"
                  Expect.isNone creation.Order "order"
              | other -> failtestf "expected BEFORE DELETE trigger AST, got %A" other

          testCase "BEFORE INSERT can assign NEW values"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER increment BEFORE INSERT ON t FOR EACH ROW SET NEW.n = NEW.n + 1")
                  "create trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (10)") "insert"
              Expect.equal (rows store "SELECT n FROM t") [ [ Some "11" ] ] "stored row contains the value assigned by the trigger"

          testCase "compound trigger bodies execute statements in order"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault
                      store
                      "CREATE TRIGGER compound BEFORE INSERT ON t FOR EACH ROW BEGIN SET NEW.n = NEW.n + 1; SET NEW.n = NEW.n * 2; END")
                  "create compound trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (10)") "fire compound trigger"
              Expect.equal (rows store "SELECT n FROM t") [ [ Some "22" ] ] "later assignments observe the updated NEW row"

          testCase "trigger bodies call procedures with nested DML and output variables"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable session = Fsdb.Session.create 1 store
              session <- step session "CREATE TABLE procedure_source (id INT PRIMARY KEY, n INT)"
              session <- step session "CREATE TABLE procedure_log (n INT, tag VARCHAR(20))"
              session <- step session "CREATE PROCEDURE write_log(IN value INT) INSERT INTO procedure_log VALUES (value, 'direct')"
              session <- step session "CREATE PROCEDURE nested_log(IN value INT) INSERT INTO procedure_log VALUES (value, 'nested')"
              session <- step session "CREATE PROCEDURE call_nested(IN value INT) CALL nested_log(value)"
              session <- step session "CREATE PROCEDURE assign_output(IN value INT, OUT result INT) SET result = value * 2"

              session <-
                  step
                      session
                      "CREATE TRIGGER procedure_calls AFTER INSERT ON procedure_source FOR EACH ROW BEGIN CALL write_log(NEW.n); CALL call_nested(NEW.n + 1); CALL assign_output(NEW.n, @trigger_output); END"

              let executed, result = handle session "INSERT INTO procedure_source VALUES (1, 9)"
              expectOk result "fire procedure trigger"

              Expect.equal
                  (rows store "SELECT n, tag FROM procedure_log ORDER BY n")
                  [ [ Some "9"; Some "direct" ]; [ Some "10"; Some "nested" ] ]
                  "direct and nested procedure writes"

              Expect.equal (Map.tryFind "trigger_output" executed.UserVariables) (Some(VInt 18L)) "OUT variable"

              let prepared =
                  match prepareStatementForSession executed "INSERT INTO procedure_source VALUES (?, ?)" with
                  | Ok(Some ast, 2) ->
                      let statement: Fsdb.Session.PreparedStmt =
                          { Ast = Some ast
                            Sql = "INSERT INTO procedure_source VALUES (?, ?)"
                            ParamCount = 2
                            LastParamTypes = None }

                      let prepared, result = executePrepared executed statement [ VInt 2L; VInt 11L ]
                      expectOk result "prepared trigger procedure call"
                      Expect.equal (Map.tryFind "trigger_output" prepared.UserVariables) (Some(VInt 22L)) "prepared OUT variable"
                      prepared
                  | other -> failtestf "expected prepared trigger insert, got %A" other

              session <- step prepared "CREATE TABLE local_output_source (id INT PRIMARY KEY, n INT)"
              session <- step session "CREATE PROCEDURE double_value(IN value INT, OUT doubled INT) SET doubled = value * 2"

              session <-
                  step
                      session
                      "CREATE TRIGGER local_output BEFORE INSERT ON local_output_source FOR EACH ROW BEGIN DECLARE doubled INT; CALL double_value(NEW.n, doubled); SET NEW.n = doubled; END"

              session <- step session "INSERT INTO local_output_source VALUES (1, 7)"
              Expect.equal (rows store "SELECT n FROM local_output_source") [ [ Some "14" ] ] "typed local OUT target"

          testCase "trigger procedure resultsets and dynamic SQL fail atomically"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable session = Fsdb.Session.create 1 store
              session <- step session "CREATE TABLE procedure_source (id INT PRIMARY KEY)"
              session <- step session "CREATE PROCEDURE returns_rows() SELECT 1"
              session <- step session "CREATE TRIGGER result_trigger AFTER INSERT ON procedure_source FOR EACH ROW CALL returns_rows()"

              match handle session "INSERT INTO procedure_source VALUES (1)" |> snd with
              | Err(1415, "Not allowed to return a result set from a trigger") -> ()
              | other -> failtestf "expected trigger resultset refusal, got %A" other

              Expect.equal (rows store "SELECT COUNT(*) FROM procedure_source") [ [ Some "0" ] ] "resultset failure rolls back"
              session <- step session "DROP TRIGGER result_trigger"

              session <-
                  step
                      session
                      "CREATE PROCEDURE dynamic_sql() BEGIN PREPARE trigger_stmt FROM 'SELECT 1'; EXECUTE trigger_stmt; DEALLOCATE PREPARE trigger_stmt; END"

              session <- step session "CREATE TRIGGER dynamic_trigger AFTER INSERT ON procedure_source FOR EACH ROW CALL dynamic_sql()"

              match handle session "INSERT INTO procedure_source VALUES (2)" |> snd with
              | Err(1336, "Dynamic SQL is not allowed in stored function or trigger") -> ()
              | other -> failtestf "expected trigger dynamic SQL refusal, got %A" other

              Expect.equal (rows store "SELECT COUNT(*) FROM procedure_source") [ [ Some "0" ] ] "dynamic SQL failure rolls back"
              session <- step session "DROP TRIGGER dynamic_trigger"
              session <- step session "CREATE PROCEDURE mutate_source(IN value INT) UPDATE procedure_source SET id = value"
              session <- step session "CREATE TRIGGER self_write AFTER INSERT ON procedure_source FOR EACH ROW CALL mutate_source(NEW.id)"

              match handle session "INSERT INTO procedure_source VALUES (3)" |> snd with
              | Err(1442, message) -> Expect.stringContains message "procedure_source" "protected table named"
              | other -> failtestf "expected trigger self-write refusal, got %A" other

              Expect.equal (rows store "SELECT COUNT(*) FROM procedure_source") [ [ Some "0" ] ] "self-write failure rolls back"

          testCase "multi-table UPDATE fires each changed table's triggers"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable session = Fsdb.Session.create 1 store
              session <- step session "CREATE TABLE multi_update_a (id INT PRIMARY KEY, n INT)"
              session <- step session "CREATE TABLE multi_update_b (id INT PRIMARY KEY, a_id INT, n INT)"
              session <- step session "CREATE TABLE multi_update_audit (tag VARCHAR(10), id INT, old_n INT, new_n INT)"
              session <- step session "INSERT INTO multi_update_a VALUES (1, 10)"
              session <- step session "INSERT INTO multi_update_b VALUES (2, 1, 100)"

              session <-
                  step
                      session
                      "CREATE TRIGGER multi_a_before BEFORE UPDATE ON multi_update_a FOR EACH ROW BEGIN INSERT INTO multi_update_audit VALUES ('a_before', OLD.id, OLD.n, NEW.n); SET NEW.n = NEW.n + 10; END"

              session <-
                  step
                      session
                      "CREATE TRIGGER multi_a_after AFTER UPDATE ON multi_update_a FOR EACH ROW INSERT INTO multi_update_audit VALUES ('a_after', OLD.id, OLD.n, NEW.n)"

              session <-
                  step
                      session
                      "CREATE TRIGGER multi_b_before BEFORE UPDATE ON multi_update_b FOR EACH ROW BEGIN INSERT INTO multi_update_audit VALUES ('b_before', OLD.id, OLD.n, NEW.n); SET NEW.n = NEW.n + 20; END"

              session <-
                  step
                      session
                      "CREATE TRIGGER multi_b_after AFTER UPDATE ON multi_update_b FOR EACH ROW INSERT INTO multi_update_audit VALUES ('b_after', OLD.id, OLD.n, NEW.n)"

              match
                  handle
                      session
                      "UPDATE multi_update_a AS a JOIN multi_update_b AS b ON b.a_id = a.id SET a.n = a.n + 1, b.n = b.n + 2"
                  |> snd
              with
              | Affected 2UL -> ()
              | other -> failtestf "expected two changed physical rows, got %A" other

              Expect.equal (rows store "SELECT n FROM multi_update_a") [ [ Some "21" ] ] "first target BEFORE value"
              Expect.equal (rows store "SELECT n FROM multi_update_b") [ [ Some "122" ] ] "second target BEFORE value"

              Expect.equal
                  (rows store "SELECT tag, old_n, new_n FROM multi_update_audit ORDER BY tag")
                  [ [ Some "a_after"; Some "10"; Some "21" ]
                    [ Some "a_before"; Some "10"; Some "11" ]
                    [ Some "b_after"; Some "100"; Some "122" ]
                    [ Some "b_before"; Some "100"; Some "102" ] ]
                  "each target's OLD and NEW images"

              session <- step session "DROP TRIGGER multi_a_after"

              session <-
                  step
                      session
                      "CREATE TRIGGER multi_a_after AFTER UPDATE ON multi_update_a FOR EACH ROW UPDATE multi_update_b SET n = n + 1 WHERE a_id = NEW.id"

              match
                  handle
                      session
                      "UPDATE multi_update_a AS a JOIN multi_update_b AS b ON b.a_id = a.id SET a.n = a.n + 1, b.n = b.n + 1"
                  |> snd
              with
              | Err(1442, message) -> Expect.stringContains message "multi_update_b" "all statement targets are protected"
              | other -> failtestf "expected protected target refusal, got %A" other

              Expect.equal (rows store "SELECT n FROM multi_update_a") [ [ Some "21" ] ] "failed statement restores first target"
              Expect.equal (rows store "SELECT n FROM multi_update_b") [ [ Some "122" ] ] "failed statement restores second target"
              Expect.equal (rows store "SELECT COUNT(*) FROM multi_update_audit") [ [ Some "4" ] ] "failed trigger effects roll back"

          testCase "multi-table DELETE fires triggers and rolls every target back on failure"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let mutable session = Fsdb.Session.create 1 store
              session <- step session "CREATE TABLE multi_delete_a (id INT PRIMARY KEY)"
              session <- step session "CREATE TABLE multi_delete_b (id INT PRIMARY KEY, a_id INT)"
              session <- step session "CREATE TABLE multi_delete_audit (tag VARCHAR(10), id INT)"
              session <- step session "INSERT INTO multi_delete_a VALUES (1), (2)"
              session <- step session "INSERT INTO multi_delete_b VALUES (10, 1), (20, 2)"
              session <- step session "CREATE TRIGGER multi_a_delete AFTER DELETE ON multi_delete_a FOR EACH ROW INSERT INTO multi_delete_audit VALUES ('a_after', OLD.id)"
              session <- step session "CREATE TRIGGER multi_b_delete AFTER DELETE ON multi_delete_b FOR EACH ROW INSERT INTO multi_delete_audit VALUES ('b_after', OLD.id)"

              match
                  handle session "DELETE a, b FROM multi_delete_a AS a JOIN multi_delete_b AS b ON b.a_id = a.id WHERE a.id = 1"
                  |> snd
              with
              | Affected 2UL -> ()
              | other -> failtestf "expected two deleted physical rows, got %A" other

              Expect.equal
                  (rows store "SELECT tag, id FROM multi_delete_audit ORDER BY tag")
                  [ [ Some "a_after"; Some "1" ]; [ Some "b_after"; Some "10" ] ]
                  "both targets fire"

              session <-
                  step
                      session
                      "CREATE TRIGGER multi_b_before BEFORE DELETE ON multi_delete_b FOR EACH ROW BEGIN SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO = 60001, MESSAGE_TEXT = 'stop delete'; END"

              match
                  handle session "DELETE a, b FROM multi_delete_a AS a JOIN multi_delete_b AS b ON b.a_id = a.id WHERE a.id = 2"
                  |> snd
              with
              | Err(60001, "stop delete") -> ()
              | other -> failtestf "expected trigger signal, got %A" other

              Expect.equal (rows store "SELECT id FROM multi_delete_a") [ [ Some "2" ] ] "first target restored"
              Expect.equal (rows store "SELECT id FROM multi_delete_b") [ [ Some "20" ] ] "second target restored"
              Expect.equal (rows store "SELECT COUNT(*) FROM multi_delete_audit") [ [ Some "2" ] ] "failed trigger effects rolled back"

              session <- step session "DROP TRIGGER multi_b_before"
              session <- step session "DROP TRIGGER multi_a_delete"

              session <-
                  step
                      session
                      "CREATE TRIGGER multi_a_delete AFTER DELETE ON multi_delete_a FOR EACH ROW UPDATE multi_delete_b SET a_id = a_id WHERE a_id = OLD.id"

              match
                  handle session "DELETE a FROM multi_delete_a AS a JOIN multi_delete_b AS b ON b.a_id = a.id WHERE a.id = 999"
                  |> snd
              with
              | Affected 0UL -> ()
              | other -> failtestf "expected unmatched delete to skip triggers, got %A" other

              match
                  handle session "DELETE a FROM multi_delete_a AS a JOIN multi_delete_b AS b ON b.a_id = a.id WHERE a.id = 2"
                  |> snd
              with
              | Err(1442, message) -> Expect.stringContains message "multi_delete_b" "joined tables are protected"
              | other -> failtestf "expected joined-table refusal, got %A" other

              Expect.equal (rows store "SELECT id FROM multi_delete_a") [ [ Some "2" ] ] "joined-table refusal restores delete"

          testCase "conditional trigger bodies execute only when their predicate is true"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "CREATE TABLE second_log (n INT)") "create second log"
              expectOk (runDefault store "INSERT INTO t(n) VALUES (10), (20)") "seed rows"

              expectOk
                  (runDefault
                      store
                      "CREATE TRIGGER changed AFTER UPDATE ON t FOR EACH ROW BEGIN IF (NOT(NEW.n <=> OLD.n)) THEN INSERT INTO log(n) VALUES (NEW.n); END IF; IF (NOT(NEW.n <=> OLD.n)) THEN INSERT INTO second_log(n) VALUES (NEW.n); END IF; END")
                  "create conditional trigger"

              expectOk (runDefault store "UPDATE t SET n = IF(n = 10, 11, n)") "update rows"
              Expect.equal (rows store "SELECT n FROM log") [ [ Some "11" ] ] "only the changed row is logged"
              Expect.equal (rows store "SELECT n FROM second_log") [ [ Some "11" ] ] "later conditions also execute"

          testCase "nested trigger conditions select ELSEIF and ELSE statement blocks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault
                      store
                      "CREATE TRIGGER branch BEFORE INSERT ON t FOR EACH ROW BEGIN IF NEW.n > 0 THEN IF NEW.n = 1 THEN SET NEW.n = 10; ELSEIF NEW.n = 2 THEN SET NEW.n = 20; SET NEW.n = NEW.n + 1; ELSE SET NEW.n = 30; END IF; END IF; END")
                  "create nested conditional trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (1), (2), (3), (-1)") "fire each branch"
              Expect.equal (rows store "SELECT n FROM t ORDER BY id") [ [ Some "10" ]; [ Some "21" ]; [ Some "30" ]; [ Some "-1" ] ] "each row follows one branch"

          testCase "trigger bodies compose CASE and WHILE control flow"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              let definition =
                  """CREATE TRIGGER control BEFORE INSERT ON t FOR EACH ROW
                      BEGIN
                        DECLARE i INT DEFAULT 0;
                        WHILE i < 2 DO
                          SET NEW.n = NEW.n + 1;
                          SET i = i + 1;
                        END/*loop*/WHILE;
                        CASE/*selector*/NEW.n
                          WHEN 3 THEN SET NEW.n = 30;
                          ELSE SET NEW.n = 40;
                        END/*case*/CASE;
                      END"""

              expectOk
                  (runDefault store definition)
                  "create control-flow trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (1), (5)") "fire trigger"
              Expect.equal (rows store "SELECT n FROM t ORDER BY n") [ [ Some "30" ]; [ Some "40" ] ] "control flow"

              match runDefault store "CREATE TRIGGER bad_label BEFORE INSERT ON t FOR EACH ROW BEGIN LEAVE nowhere; END" with
              | Err(1308, "LEAVE with no matching label: nowhere") -> ()
              | other -> failtestf "expected unmatched-label error, got %A" other

          testCase "trigger condition handlers can continue after SIGNAL"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault
                      store
                      """CREATE TRIGGER handled BEFORE INSERT ON t FOR EACH ROW
                          BEGIN
                            DECLARE CONTINUE HANDLER FOR SQLSTATE '45010'
                              SET NEW.n = NEW.n + 10;
                            SIGNAL SQLSTATE '45010'
                              SET MYSQL_ERRNO = 60010, MESSAGE_TEXT = 'handled';
                            SET NEW.n = NEW.n + 1;
                          END""")
                  "create handled trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (1)") "fire handled trigger"
              Expect.equal (rows store "SELECT n FROM t") [ [ Some "12" ] ] "handler resumes trigger body"

          testCase "trigger handlers can inspect stacked diagnostics"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault
                      store
                      """CREATE TRIGGER inspect_error BEFORE INSERT ON t FOR EACH ROW
                          BEGIN
                            DECLARE diagnostic_code INT DEFAULT 0;
                            DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
                            BEGIN
                              GET STACKED DIAGNOSTICS CONDITION 1 diagnostic_code = MYSQL_ERRNO;
                              SET NEW.n = diagnostic_code;
                            END;
                            SIGNAL SQLSTATE '45011' SET MYSQL_ERRNO = 60011;
                          END""")
                  "create diagnostics trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (1)") "fire diagnostics trigger"
              Expect.equal (rows store "SELECT n FROM t") [ [ Some "60011" ] ] "handler receives the raised code"

          testCase "trigger cursors iterate query results with NOT FOUND handlers"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "CREATE TABLE trigger_values (n INT)") "create cursor source"
              expectOk (runDefault store "INSERT INTO trigger_values VALUES (3), (1), (2)") "seed cursor source"

              expectOk
                  (runDefault
                      store
                      """CREATE TRIGGER cursor_total BEFORE INSERT ON t FOR EACH ROW
                          BEGIN
                            DECLARE done INT DEFAULT 0;
                            DECLARE value INT;
                            DECLARE total INT DEFAULT 0;
                            DECLARE minimum_value INT DEFAULT 3;
                            DECLARE values_cursor CURSOR FOR
                              SELECT n FROM trigger_values WHERE n >= minimum_value ORDER BY n;
                            DECLARE CONTINUE HANDLER FOR NOT FOUND SET done = 1;
                            SET minimum_value = 1;
                            OPEN values_cursor;
                            read_loop: LOOP
                              FETCH values_cursor INTO value;
                              IF done THEN LEAVE read_loop; END IF;
                              SET total = total + value;
                            END LOOP;
                            CLOSE values_cursor;
                            SET NEW.n = total;
                          END""")
                  "create cursor trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (0)") "fire cursor trigger"
              Expect.equal (rows store "SELECT n FROM t") [ [ Some "6" ] ] "cursor trigger accumulated rows"

          testCase "trigger blocks retain declared values from scalar subqueries"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "CREATE TABLE rates (id INT PRIMARY KEY, rate DECIMAL(10,2))") "create rates"
              expectOk (runDefault store "INSERT INTO rates VALUES (1, 5.00)") "seed rate"

              expectOk
                  (runDefault
                      store
                      "CREATE TRIGGER local_value BEFORE INSERT ON t FOR EACH ROW BEGIN IF NEW.n > 0 THEN BEGIN DECLARE taxRate DECIMAL(10,2); SET taxRate = (SELECT rate FROM rates WHERE id = 1); SET NEW.n = NEW.n + taxRate; END; ELSE BEGIN SET NEW.n = 0; END; END IF; END")
                  "create local-value trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (1), (-1)") "fire local-value trigger"
              Expect.equal (rows store "SELECT n FROM t ORDER BY id") [ [ Some "6" ]; [ Some "0" ] ] "the selected local value reaches the assignment"

          testCase "BEFORE INSERT preserves body writes in the subject database"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault
                      store
                      "CREATE TRIGGER preserve_write BEFORE INSERT ON t FOR EACH ROW BEGIN INSERT INTO log(n) VALUES (NEW.n); SET NEW.n = NEW.n + 1; END")
                  "create compound trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (10)") "fire compound trigger"
              Expect.equal (rows store "SELECT n FROM t") [ [ Some "11" ] ] "subject row persists"
              Expect.equal (rows store "SELECT n FROM log") [ [ Some "10" ] ] "body row persists"

          testCase "a failing compound body rolls back earlier statements"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "CREATE TABLE unique_log (n INT UNIQUE)") "create unique log"
              expectOk (runDefault store "INSERT INTO unique_log VALUES (1)") "seed unique log"

              expectOk
                  (runDefault
                      store
                      "CREATE TRIGGER compound_failure AFTER INSERT ON t FOR EACH ROW BEGIN INSERT INTO log(n) VALUES (NEW.n); INSERT INTO unique_log VALUES (1); END")
                  "create failing compound trigger"

              match runDefault store "INSERT INTO t(n) VALUES (10)" with
              | Err(1062, _) -> ()
              | result -> failtestf "expected duplicate-key failure, got %A" result

              Expect.equal (rows store "SELECT n FROM t") [] "the originating insert rolls back"
              Expect.equal (rows store "SELECT n FROM log") [] "earlier body statements roll back"

          testCase "SET NEW is rejected outside a trigger body"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              Expect.equal
                  (runDefault store "SET NEW.n = 1")
                  (Err(1064, "SET NEW is only valid in a trigger body"))
                  "NEW has no row image outside a trigger"

          testCase "BEFORE INSERT changes to an auto-increment key advance its sequence"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (runDefault store "CREATE TABLE trigger_auto (id INT AUTO_INCREMENT PRIMARY KEY, n INT)") "create table"

              expectOk
                  (runDefault
                      store
                      "CREATE TRIGGER trigger_auto_before BEFORE INSERT ON trigger_auto FOR EACH ROW SET NEW.id = NEW.n")
                  "create trigger"

              expectOk (runDefault store "INSERT INTO trigger_auto(n) VALUES (100)") "insert assigned key"
              expectOk (runDefault store "DROP TRIGGER trigger_auto_before") "drop trigger"
              expectOk (runDefault store "INSERT INTO trigger_auto(n) VALUES (0)") "insert generated key"

              Expect.equal
                  (rows store "SELECT id, n FROM trigger_auto ORDER BY id")
                  [ [ Some "100"; Some "100" ]; [ Some "101"; Some "0" ] ]
                  "the next generated key follows the trigger-assigned value"

          testCase "UPDATE and DELETE expose their row images at each timing"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "INSERT INTO t(n) VALUES (10)") "seed t"

              expectOk
                  (runDefault store "CREATE TRIGGER increment BEFORE UPDATE ON t FOR EACH ROW SET NEW.n = NEW.n + 1")
                  "create before update trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER update_log AFTER UPDATE ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n - OLD.n)")
                  "create after update trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER delete_before BEFORE DELETE ON t FOR EACH ROW INSERT INTO log(n) VALUES (OLD.n)")
                  "create before delete trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER delete_after AFTER DELETE ON t FOR EACH ROW INSERT INTO log(n) VALUES (OLD.n + 1)")
                  "create after delete trigger"

              expectOk (runDefault store "UPDATE t SET n = 20") "update"
              expectOk (runDefault store "DELETE FROM t") "delete"

              Expect.equal
                  (rows store "SELECT n FROM log ORDER BY id")
                  [ [ Some "11" ]; [ Some "21" ]; [ Some "22" ] ]
                  "every timing sees its corresponding old or new row image"

          testCase "AFTER INSERT fires once per row with NEW.* bound per row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n + 1)")
                  "create trigger"

              match runDefault store "INSERT INTO t(n) VALUES (10), (20), (30)" with
              | Affected 3UL -> ()
              | other -> failtestf "trigger effects must not inflate the statement's affected count, got %A" other

              Expect.equal
                  (rows store "SELECT n FROM log ORDER BY id")
                  [ [ Some "11" ]; [ Some "21" ]; [ Some "31" ] ]
                  "one log row per inserted row, NEW.n bound to each row's own value"

          testCase "INSERT ... SELECT fires the target table's trigger per inserted row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "CREATE TABLE src (n INT)") "create src"
              expectOk (runDefault store "INSERT INTO src VALUES (1), (2)") "seed src"

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                  "create trigger"

              expectOk (runDefault store "INSERT INTO t(n) SELECT n FROM src") "insert...select"

              Expect.equal (rows store "SELECT n FROM log ORDER BY n") [ [ Some "1" ]; [ Some "2" ] ] "fired per select-derived row"

          testCase "REPLACE fires AFTER INSERT for both new and replacement candidates"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                  "create trigger"

              Expect.equal (runDefault store "REPLACE INTO t VALUES (1, 10)") (Affected 1UL) "insert path"
              Expect.equal (runDefault store "REPLACE INTO t VALUES (1, 20)") (Affected 2UL) "replacement path"
              Expect.equal (runDefault store "REPLACE INTO t VALUES (1, 20)") (Affected 2UL) "triggered unchanged path"

              Expect.equal
                  (rows store "SELECT n FROM log ORDER BY id")
                  [ [ Some "10" ]; [ Some "20" ]; [ Some "20" ] ]
                  "one insert event per candidate"

          testCase "REPLACE fires INSERT and DELETE timings in row order"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "INSERT INTO t VALUES (1, 10)") "seed t"

              expectOk
                  (runDefault store "CREATE TRIGGER replace_bi BEFORE INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n * 10 + 1)")
                  "create before insert trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER replace_bd BEFORE DELETE ON t FOR EACH ROW INSERT INTO log(n) VALUES (OLD.n * 10 + 2)")
                  "create before delete trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER replace_ad AFTER DELETE ON t FOR EACH ROW INSERT INTO log(n) VALUES (OLD.n * 10 + 3)")
                  "create after delete trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER replace_ai AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n * 10 + 4)")
                  "create after insert trigger"

              Expect.equal
                  (runDefault store "REPLACE INTO t VALUES (1, 20)")
                  (Affected 2UL)
                  "delete and insert count separately"

              Expect.equal
                  (rows store "SELECT n FROM log ORDER BY id")
                  [ [ Some "201" ]; [ Some "102" ]; [ Some "103" ]; [ Some "204" ] ]
                  "BEFORE INSERT precedes the DELETE pair and AFTER INSERT"

              Expect.equal (rows store "SELECT n FROM t") [ [ Some "20" ] ] "replacement is stored"

          testCase "REPLACE fires DELETE triggers for every unique-key conflict"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (runDefault store "CREATE TABLE t (id INT PRIMARY KEY, u INT UNIQUE, n INT)") "create t"
              expectOk (runDefault store "CREATE TABLE log (id INT AUTO_INCREMENT PRIMARY KEY, n INT)") "create log"
              expectOk (runDefault store "INSERT INTO t VALUES (1, 10, 10), (2, 20, 20)") "seed t"

              expectOk
                  (runDefault store "CREATE TRIGGER replace_bi BEFORE INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n * 10 + 1)")
                  "create before insert trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER replace_bd BEFORE DELETE ON t FOR EACH ROW INSERT INTO log(n) VALUES (OLD.n * 10 + 2)")
                  "create before delete trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER replace_ad AFTER DELETE ON t FOR EACH ROW INSERT INTO log(n) VALUES (OLD.n * 10 + 3)")
                  "create after delete trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER replace_ai AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n * 10 + 4)")
                  "create after insert trigger"

              Expect.equal (runDefault store "REPLACE INTO t VALUES (1, 20, 99)") (Affected 3UL) "two deletes and one insert"

              Expect.equal
                  (rows store "SELECT n FROM log ORDER BY id")
                  [ [ Some "991" ]; [ Some "102" ]; [ Some "103" ]; [ Some "202" ]; [ Some "203" ]; [ Some "994" ] ]
                  "each conflicting row has its own DELETE trigger pair"

          testCase "REPLACE trigger failures roll back every phase"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "CREATE TABLE unique_log (n INT UNIQUE)") "create unique log"
              expectOk (runDefault store "INSERT INTO t VALUES (1, 10)") "seed t"
              expectOk (runDefault store "INSERT INTO unique_log VALUES (10)") "seed unique log"

              expectOk
                  (runDefault store "CREATE TRIGGER replace_bi BEFORE INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                  "create before insert trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER replace_ad AFTER DELETE ON t FOR EACH ROW INSERT INTO unique_log VALUES (OLD.n)")
                  "create failing after delete trigger"

              match runDefault store "REPLACE INTO t VALUES (1, 20)" with
              | Err(1062, _) -> ()
              | other -> failtestf "expected trigger failure, got %A" other

              Expect.equal (rows store "SELECT n FROM t") [ [ Some "10" ] ] "deleted row is restored"
              Expect.equal (rows store "SELECT n FROM log") [] "earlier trigger effects roll back"

          testCase "triggered REPLACE inserts a new self-referencing candidate"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              expectOk
                  (runDefault
                      store
                      "CREATE TABLE self_ref (id INT PRIMARY KEY, parent_id INT, FOREIGN KEY (parent_id) REFERENCES self_ref(id))")
                  "create self reference"

              expectOk (runDefault store "CREATE TABLE log (n INT)") "create log"

              expectOk
                  (runDefault store "CREATE TRIGGER self_ref_insert AFTER INSERT ON self_ref FOR EACH ROW INSERT INTO log VALUES (NEW.id)")
                  "create insert trigger"

              Expect.equal (runDefault store "REPLACE INTO self_ref VALUES (1, 1)") (Affected 1UL) "insert self reference"
              Expect.equal (rows store "SELECT * FROM self_ref") [ [ Some "1"; Some "1" ] ] "candidate can satisfy its own parent"
              Expect.equal (rows store "SELECT * FROM log") [ [ Some "1" ] ] "delete trigger fired"

          testCase "ON DUPLICATE KEY UPDATE fires only for rows that actually inserted"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (runDefault store "CREATE TABLE t (id INT PRIMARY KEY, n INT)") "create t"
              expectOk (runDefault store "CREATE TABLE log (id INT AUTO_INCREMENT PRIMARY KEY, n INT)") "create log"

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                  "create trigger"

              expectOk (runDefault store "INSERT INTO t VALUES (1, 5) ON DUPLICATE KEY UPDATE n = 6") "insert path"
              expectOk (runDefault store "INSERT INTO t VALUES (1, 7) ON DUPLICATE KEY UPDATE n = 7") "update path"

              Expect.equal
                  (rows store "SELECT n FROM log")
                  [ [ Some "5" ] ]
                  "only the first statement's insert path fired; the duplicate's update path didn't"

          testCase "generated columns are not valid OLD or NEW references"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (runDefault store "CREATE TABLE g (a INT, b INT AS (a * 2))") "create g"
              expectOk (runDefault store "CREATE TABLE log (id INT AUTO_INCREMENT PRIMARY KEY, n INT)") "create log"

              match runDefault store "CREATE TRIGGER trg AFTER INSERT ON g FOR EACH ROW INSERT INTO log(n) VALUES (NEW.b)" with
              | Err(3105, message) -> Expect.stringContains message "generated" "generated column named"
              | other -> failtestf "expected generated row-image rejection, got %A" other

          testCase "INSERT has no OLD row and DELETE has no NEW row"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              match runDefault store "CREATE TRIGGER bad_insert AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (OLD.n)" with
              | Err(1363, _) -> ()
              | other -> failtestf "expected OLD rejection in INSERT trigger, got %A" other

              match runDefault store "CREATE TRIGGER bad_delete AFTER DELETE ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)" with
              | Err(1363, _) -> ()
              | other -> failtestf "expected NEW rejection in DELETE trigger, got %A" other

          testCase "nested trigger queries validate row-image references"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (runDefault store "CREATE TABLE g (a INT, b INT AS (a * 2))") "create g"
              expectOk (runDefault store "CREATE TABLE log (n INT)") "create log"

              match runDefault store "CREATE TRIGGER bad_generated AFTER INSERT ON g FOR EACH ROW INSERT INTO log SELECT NEW.b" with
              | Err(3105, _) -> ()
              | other -> failtestf "expected nested generated reference rejection, got %A" other

              match runDefault store "CREATE TRIGGER bad_old AFTER INSERT ON g FOR EACH ROW INSERT INTO log SELECT OLD.a" with
              | Err(1363, _) -> ()
              | other -> failtestf "expected nested OLD rejection, got %A" other

          testCase "later row-image references are validated at trigger creation"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (runDefault store "CREATE TABLE g (a INT, b INT AS (a * 2))") "create g"
              expectOk (runDefault store "CREATE TABLE log (n INT)") "create log"

              match runDefault store "CREATE TRIGGER later_invalid AFTER INSERT ON g FOR EACH ROW INSERT INTO log VALUES (NEW.a + (SELECT NEW.b))" with
              | Err(3105, _) -> ()
              | other -> failtestf "expected later generated reference rejection, got %A" other

              match runDefault store "CREATE TRIGGER missing_column AFTER INSERT ON g FOR EACH ROW INSERT INTO log VALUES (NEW.missing)" with
              | Err(1054, _) -> ()
              | other -> failtestf "expected missing row-image column rejection, got %A" other

          testCase "the body executes in the trigger's schema, not the session's current database"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              expectOk (runDefault store "CREATE DATABASE a") "create a"
              expectOk (runDefault store "CREATE DATABASE b") "create b"
              expectOk (runDefault store "CREATE TABLE a.t (n INT)") "create a.t"
              expectOk (runDefault store "CREATE TABLE a.work_log (n INT)") "create a.work_log"
              expectOk (runDefault store "CREATE TABLE b.work_log (n INT)") "create b.work_log"

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON a.t FOR EACH ROW INSERT INTO work_log(n) VALUES (NEW.n)")
                  "create trigger on a.t"

              // Probed: MySQL resolves the body's unqualified `work_log`
              // against the trigger's schema `a` even when the session is
              // defaulted to `b` — b.work_log stays untouched (and the body
              // must not 1146 when b.work_log doesn't exist).
              let runOnB sql =
                  match Fsdb.Parser.parse sql with
                  | Error msg -> failtestf "expected %s to parse, got error: %s" sql msg
                  | Ok stmt -> execute store builtins "b" (0L, 0L) false stmt |> snd

              expectOk (runOnB "INSERT INTO a.t(n) VALUES (7)") "insert from a session on b"
              Expect.equal (rows store "SELECT n FROM a.work_log") [ [ Some "7" ] ] "landed in the trigger's schema"
              Expect.equal (rows store "SELECT n FROM b.work_log") [] "the session's database was not written"

          testCase "a cross-database trigger preserves a concurrent write to its target database"
          <| fun _ ->
              use entered = new ManualResetEventSlim(false)
              use release = new ManualResetEventSlim(false)
              use concurrentStarted = new ManualResetEventSlim(false)

              let pause =
                  function
                  | [] ->
                      entered.Set()

                      if not (release.Wait(TimeSpan.FromSeconds 5.0)) then
                          raise (TimeoutException "trigger was not released")

                      VInt 10L
                  | _ -> VNull

              let db = Fsdb.Db.create () |> Fsdb.Db.registerScalar "pause_trigger" pause
              let setup = Fsdb.Db.connect db
              expectOk (setup.Query "CREATE DATABASE source_db") "create source_db"
              expectOk (setup.Query "CREATE DATABASE audit_db") "create audit_db"
              expectOk (setup.Query "CREATE TABLE source_db.items (id INT PRIMARY KEY)") "create items"
              expectOk (setup.Query "CREATE TABLE audit_db.events (id INT PRIMARY KEY)") "create events"

              expectOk
                  (setup.Query "CREATE TRIGGER item_log AFTER INSERT ON source_db.items FOR EACH ROW INSERT INTO audit_db.events VALUES (pause_trigger())")
                  "create trigger"

              let firing = Fsdb.Db.connect db
              let concurrent = Fsdb.Db.connect db
              let triggerInsert = Task.Run(fun () -> firing.Query "INSERT INTO source_db.items VALUES (1)")

              try
                  Expect.isTrue (entered.Wait(TimeSpan.FromSeconds 5.0)) "the trigger body started"
                  let concurrentInsert =
                      Task.Run(fun () ->
                          concurrentStarted.Set()
                          concurrent.Query "INSERT INTO audit_db.events VALUES (20)")

                  Expect.isTrue (concurrentStarted.Wait(TimeSpan.FromSeconds 5.0)) "the concurrent insert started"
                  Expect.isTrue
                      (concurrentInsert.Wait(TimeSpan.FromSeconds 5.0))
                      "the trigger does not serialize a disjoint target row"

                  release.Set()
                  expectOk triggerInsert.Result "trigger insert"
                  expectOk concurrentInsert.Result "concurrent insert"
              finally
                  release.Set()

              Expect.equal
                  (setup.Query "SELECT id FROM audit_db.events ORDER BY id")
                  (ResultSet([ "id" ], [ [ Some "10" ]; [ Some "20" ] ]))
                  "both writes survive"

          testCase "an INSERT ... SELECT body substitutes NEW.* in its ON DUPLICATE KEY UPDATE clause"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "CREATE TABLE agg (k INT PRIMARY KEY, total INT)") "create agg"
              expectOk (runDefault store "INSERT INTO log(n) VALUES (100)") "seed the body's SELECT source"

              expectOk
                  (runDefault
                      store
                      "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO agg(k, total) SELECT 1, n FROM log ON DUPLICATE KEY UPDATE total = total + NEW.n")
                  "create InsertSelect-bodied trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (5)") "insert path (agg empty)"
              Expect.equal (rows store "SELECT total FROM agg") [ [ Some "100" ] ] "insert path took the SELECT's value"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (7)") "dup-key path binds NEW.n"
              Expect.equal (rows store "SELECT total FROM agg") [ [ Some "107" ] ] "NEW.n substituted inside the body's ODKU"

          testCase "a chained trigger's effects land too, and errors in a body fail the statement"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "CREATE TABLE audit (n INT)") "create audit"

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                  "trigger t -> log"

              expectOk
                  (runDefault store "CREATE TRIGGER trg2 AFTER INSERT ON log FOR EACH ROW INSERT INTO audit(n) VALUES (NEW.n)")
                  "trigger log -> audit"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (9)") "insert"
              Expect.equal (rows store "SELECT n FROM audit") [ [ Some "9" ] ] "chain fired through log into audit"

              // Error path: a *runtime* body failure (unique violation in
              // the log insert, past the pre-flight 1442/parse checks) is
              // the statement's error, and — probed MySQL semantics — the
              // whole statement rolls back: no originating rows, no
              // earlier rows' trigger effects.
              expectOk (runDefault store "CREATE TABLE t2 (n INT)") "create t2"
              expectOk (runDefault store "CREATE TABLE ulog (n INT PRIMARY KEY)") "create ulog"
              expectOk (runDefault store "INSERT INTO ulog(n) VALUES (20)") "seed the collision"

              expectOk
                  (runDefault store "CREATE TRIGGER trg3 AFTER INSERT ON t2 FOR EACH ROW INSERT INTO ulog(n) VALUES (NEW.n)")
                  "trigger t2 -> ulog"

              match runDefault store "INSERT INTO t2(n) VALUES (10), (20), (30)" with
              | Err(1062, _) -> ()
              | other -> failtestf "expected row 2's body 1062 to fail the statement, got %A" other

              Expect.equal (rows store "SELECT n FROM t2") [] "the originating rows rolled back with the failed body"

              Expect.equal
                  (rows store "SELECT n FROM ulog")
                  [ [ Some "20" ] ]
                  "row 1's trigger effect rolled back too — only the seed row remains"

          testCase "a self-targeting trigger fires 1442 with MySQL's exact text"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER selft AFTER INSERT ON log FOR EACH ROW INSERT INTO log(n) VALUES (1)")
                  "create self-target trigger"

              match runDefault store "INSERT INTO log(n) VALUES (5)" with
              | Err(1442, msg) -> Expect.equal msg (text1442 "log") "probed 1442 text"
              | other -> failtestf "expected 1442, got %A" other

          testCase "a two-table trigger cycle fires 1442 instead of recursing forever"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER ab AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                  "t -> log"

              expectOk
                  (runDefault store "CREATE TRIGGER ba AFTER INSERT ON log FOR EACH ROW INSERT INTO t(n) VALUES (NEW.n)")
                  "log -> t"

              match runDefault store "INSERT INTO t(n) VALUES (1)" with
              | Err(1442, msg) -> Expect.equal msg (text1442 "t") "the cycle is caught when log's trigger loops back into t"
              | other -> failtestf "expected 1442, got %A" other

          testCase "long acyclic trigger chains complete"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              for i in 0..12 do
                  expectOk (runDefault store (sprintf "CREATE TABLE c%d (n INT)" i)) "create chain table"

              for i in 0..11 do
                  expectOk
                      (runDefault
                          store
                          (sprintf "CREATE TRIGGER chain%d AFTER INSERT ON c%d FOR EACH ROW INSERT INTO c%d(n) VALUES (NEW.n)" i i (i + 1)))
                      "create chain trigger"

              expectOk (runDefault store "INSERT INTO c0(n) VALUES (1)") "fire chain"
              Expect.equal (rows store "SELECT n FROM c12") [ [ Some "1" ] ] "the terminal trigger receives the row"

          testCase "multiple triggers honor creation and explicit action order"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER middle AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (2)")
                  "middle trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER last AFTER INSERT ON t FOR EACH ROW FOLLOWS middle INSERT INTO log(n) VALUES (3)")
                  "following trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER first AFTER INSERT ON t FOR EACH ROW PRECEDES middle INSERT INTO log(n) VALUES (1)")
                  "preceding trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (10)") "fire ordered triggers"

              Expect.equal (rows store "SELECT n FROM log") [ [ Some "1" ]; [ Some "2" ]; [ Some "3" ] ] "triggers fire by action order"

              Expect.equal
                  (rows store "SELECT TRIGGER_NAME, ACTION_ORDER FROM information_schema.TRIGGERS ORDER BY ACTION_ORDER")
                  [ [ Some "first"; Some "1" ]; [ Some "middle"; Some "2" ]; [ Some "last"; Some "3" ] ]
                  "metadata exposes the same action order"

              expectOk (runDefault store "DROP TRIGGER middle") "drop middle trigger"

              Expect.equal
                  (rows store "SELECT TRIGGER_NAME, ACTION_ORDER FROM information_schema.TRIGGERS ORDER BY ACTION_ORDER")
                  [ [ Some "first"; Some "1" ]; [ Some "last"; Some "2" ] ]
                  "dropping a trigger closes the action-order gap"

          testCase "trigger names stay unique and ordering references share the same slot"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (1)")
                  "first trigger"

              match runDefault store "CREATE TRIGGER trg AFTER INSERT ON log FOR EACH ROW INSERT INTO t(n) VALUES (1)" with
              | Err(1359, "Trigger already exists") -> ()
              | other -> failtestf "expected 1359 for the duplicate name, got %A" other

              match runDefault store "CREATE TRIGGER other AFTER INSERT ON t FOR EACH ROW FOLLOWS absent INSERT INTO log(n) VALUES (2)" with
              | Err(3011, _) -> ()
              | other -> failtestf "expected 3011 for a missing ordering reference, got %A" other

          testCase "DROP TRIGGER removes it; a missing name is 1360 unless IF EXISTS"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (1)")
                  "create trigger"

              expectOk (runDefault store "DROP TRIGGER trg") "drop"
              expectOk (runDefault store "INSERT INTO t(n) VALUES (1)") "insert after drop"
              Expect.equal (rows store "SELECT n FROM log") [] "dropped trigger no longer fires"

              match runDefault store "DROP TRIGGER trg" with
              | Err(1360, "Trigger does not exist") -> ()
              | other -> failtestf "expected 1360, got %A" other

              expectOk (runDefault store "DROP TRIGGER IF EXISTS trg") "IF EXISTS suppresses 1360"

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (2)")
                  "the slot is free again after DROP"

          testCase "CREATE TRIGGER validates its body: syntax, statement kind, missing table"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              match runDefault store "CREATE TRIGGER bad AFTER INSERT ON t FOR EACH ROW INSERT INTO" with
              | Err(1064, _) -> ()
              | other -> failtestf "expected 1064 for an unparseable body, got %A" other

              match runDefault store "CREATE TRIGGER bad AFTER INSERT ON t FOR EACH ROW CALL p(1); DROP TABLE log" with
              | Err(1064, _) -> ()
              | other -> failtestf "expected 1064 for trailing CALL syntax, got %A" other

              match runDefault store "CREATE TRIGGER bad AFTER INSERT ON t FOR EACH ROW SELECT 1" with
              | Err(1064, msg) -> Expect.stringContains msg "accepts INSERT, UPDATE, DELETE, REPLACE, CALL, or SET NEW" "kind restriction named"
              | other -> failtestf "expected 1064 for a SELECT body, got %A" other

              match runDefault store "CREATE TRIGGER bad AFTER INSERT ON nosuch FOR EACH ROW INSERT INTO log(n) VALUES (1)" with
              | Err(1146, _) -> ()
              | other -> failtestf "expected 1146 for a missing subject table, got %A" other

          testCase "a DirectOnly extension call in the body is rejected at CREATE with 3102"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              let registry =
                  builtins
                  |> registerExtension (ScalarFunction.effectful (ScalarFunction.create "boom" (fun _ _ -> VInt 1L)))

              match run store registry "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (boom(1))" with
              | Err(3102, msg) -> Expect.stringContains msg "boom" "names the offending function"
              | other -> failtestf "expected 3102 at CREATE, got %A" other

          testCase "a DirectOnly function registered after CREATE is still rejected at fire time"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              // At CREATE time `boom` isn't a registered extension, so the
              // DDL-time check can't see it — the fire-time registry shadow
              // (`shadowDirectOnly`) is the backstop.
              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (boom(1))")
                  "create against a not-yet-registered function"

              let registry =
                  builtins
                  |> registerExtension (ScalarFunction.effectful (ScalarFunction.create "boom" (fun _ _ -> VInt 1L)))

              match run store registry "INSERT INTO t(n) VALUES (1)" with
              | Err(3102, msg) -> Expect.stringContains msg "BOOM" "fire-time backstop fired"
              | other -> failtestf "expected 3102 at fire time, got %A" other

          testCase "trigger effects join the transaction and roll back with it"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let session = step session "CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, n INT)"
              let session = step session "CREATE TABLE log (id INT AUTO_INCREMENT PRIMARY KEY, n INT)"
              let session = step session "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)"
              let session = step session "BEGIN"
              let session = step session "INSERT INTO t(n) VALUES (42)"

              match handle session "SELECT n FROM log" |> snd with
              | ResultSet(_, r) -> Expect.equal r [ [ Some "42" ] ] "inside the transaction the effect is visible"
              | other -> failtestf "expected a resultset, got %A" other

              let session = step session "ROLLBACK"

              match handle session "SELECT n FROM log" |> snd with
              | ResultSet(_, r) -> Expect.equal r [] "rollback discarded the trigger's effect with the insert"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "triggers survive a WAL restart and keep firing"
          <| fun _ ->
              let dir = TestSupport.directory "trigger"

              let store = Fsdb.Storage.create ()
              Fsdb.Persistence.attach dir store
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                  "create insert trigger"

              expectOk
                  (runDefault store "CREATE TRIGGER trg_delete AFTER DELETE ON t FOR EACH ROW INSERT INTO log(n) VALUES (OLD.n + 100)")
                  "create delete trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (7)") "insert before restart"
              expectOk (runDefault store "REPLACE INTO t VALUES (1, 8)") "replace before restart"

              let reloaded = Fsdb.Persistence.load dir
              Expect.equal
                  (rows reloaded "SELECT n FROM log ORDER BY id")
                  [ [ Some "7" ]; [ Some "107" ]; [ Some "8" ] ]
                  "replayed effect rows survive"

              expectOk (runDefault reloaded "REPLACE INTO t VALUES (1, 9)") "replace after restart"

              Expect.equal
                  (rows reloaded "SELECT n FROM log ORDER BY id")
                  [ [ Some "7" ]; [ Some "107" ]; [ Some "8" ]; [ Some "108" ]; [ Some "9" ] ]
                  "the triggers survived the restart and fired again"

          testCase "triggers execute with their captured sql_mode"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let session = step session "CREATE TABLE lax_source (n INT)"
              let session = step session "CREATE TABLE lax_log (n INT)"
              let session = step session "SET SESSION sql_mode=''"

              let session =
                  step
                      session
                      "CREATE TRIGGER lax_trigger AFTER INSERT ON lax_source FOR EACH ROW INSERT INTO lax_log VALUES ('not an integer')"

              let session = step session "SET SESSION sql_mode='STRICT_TRANS_TABLES'"
              let session = step session "INSERT INTO lax_source VALUES (1)"
              Expect.equal (rows store "SELECT n FROM lax_log") [ [ Some "0" ] ] "the creation mode controls coercion"

              let session = step session "CREATE TABLE strict_source (n INT)"
              let session = step session "CREATE TABLE strict_log (n INT)"

              let session =
                  step
                      session
                      "CREATE TRIGGER strict_trigger AFTER INSERT ON strict_source FOR EACH ROW INSERT INTO strict_log VALUES ('not an integer')"

              let session = step session "SET SESSION sql_mode=''"

              match handle session "INSERT INTO strict_source VALUES (1)" |> snd with
              | Err(1366, _) -> ()
              | other -> failtestf "expected the captured strict mode to reject the trigger write, got %A" other

              Expect.equal (rows store "SELECT n FROM strict_source") [] "the source insert remains atomic"
              Expect.equal (rows store "SELECT n FROM strict_log") [] "the failed body leaves no effects"

          testCase "triggers retain their parser and collation context"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let session = step session "CREATE TABLE context_source (n INT)"
              let session = step session "CREATE TABLE metadata_source (n INT)"
              let session = step session "CREATE TABLE comparison_log (same_value INT)"
              let session = step session "CREATE TABLE quoted_log (n INT)"
              let session = step session "SET SESSION collation_connection='utf8mb4_bin'"

              let session =
                  step
                      session
                      "CREATE TRIGGER comparison_trigger AFTER INSERT ON context_source FOR EACH ROW INSERT INTO comparison_log VALUES ('A' = 'a')"

              let session = step session "SET SESSION sql_mode='ANSI_QUOTES'"

              let session =
                  step
                      session
                      "CREATE TRIGGER quoted_trigger AFTER INSERT ON context_source FOR EACH ROW INSERT INTO quoted_log VALUES (NEW.\"n\")"

              let session = step session "SET NAMES latin1 COLLATE latin1_swedish_ci"
              let session = step session "SET SESSION sql_mode=''"

              let session =
                  step
                      session
                      "CREATE TRIGGER metadata_trigger AFTER INSERT ON metadata_source FOR EACH ROW INSERT INTO comparison_log VALUES (1)"

              match handle session "SHOW CREATE TRIGGER metadata_trigger" |> snd with
              | ResultSet(_, [ row ]) ->
                  Expect.equal (List.item 1 row) (Some "") "sql_mode"
                  Expect.equal (List.item 3 row) (Some "latin1") "character_set_client"
                  Expect.equal (List.item 4 row) (Some "latin1_swedish_ci") "collation_connection"
                  Expect.equal (List.item 5 row) (Some "utf8mb4_0900_ai_ci") "database collation"
              | other -> failtestf "expected captured trigger metadata, got %A" other

              match
                  handle
                      session
                      "SELECT SQL_MODE, CHARACTER_SET_CLIENT, COLLATION_CONNECTION, DATABASE_COLLATION FROM information_schema.TRIGGERS WHERE TRIGGER_NAME = 'metadata_trigger'"
                  |> snd
              with
              | ResultSet(_, [ [ Some ""; Some "latin1"; Some "latin1_swedish_ci"; Some "utf8mb4_0900_ai_ci" ] ]) -> ()
              | other -> failtestf "expected matching information_schema trigger metadata, got %A" other

              let session = step session "SET SESSION collation_connection='utf8mb4_0900_ai_ci'"
              let session = step session "SET SESSION sql_mode=''"
              let _ = step session "INSERT INTO context_source VALUES (7)"

              Expect.equal (rows store "SELECT same_value FROM comparison_log") [ [ Some "0" ] ] "the binary collation is retained"
              Expect.equal (rows store "SELECT n FROM quoted_log") [ [ Some "7" ] ] "ANSI_QUOTES is retained"

          testCase "trigger execution context survives WAL recovery"
          <| fun _ ->
              let dir = TestSupport.directory "trigger-context"
              let store = Fsdb.Storage.create ()
              Fsdb.Persistence.attach dir store
              let session = Fsdb.Session.create 1 store

              let session = step session "CREATE TABLE context_source (n INT)"
              let session = step session "CREATE TABLE context_log (n INT)"
              let session = step session "SET SESSION sql_mode=''"

              let _ =
                  step
                      session
                      "CREATE TRIGGER context_trigger AFTER INSERT ON context_source FOR EACH ROW INSERT INTO context_log VALUES ('not an integer')"

              let reloaded = Fsdb.Persistence.load dir
              let session = Fsdb.Session.create 2 reloaded
              let session = step session "SET SESSION sql_mode='STRICT_TRANS_TABLES'"
              let _ = step session "INSERT INTO context_source VALUES (1)"

              Expect.equal (rows reloaded "SELECT n FROM context_log") [ [ Some "0" ] ] "the recovered trigger remains non-strict"

          testCase "SHOW TRIGGERS renders the mysql.triggers row in MySQL's probed shape"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let session = step session "CREATE TABLE t (id INT, n INT)"
              let session = step session "CREATE TABLE log (n INT)"
              let session = step session "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)"

              match handle session "SHOW TRIGGERS" |> snd with
              | ResultSet(cols, [ row ]) ->
                  Expect.equal
                      cols
                      [ "Trigger"; "Event"; "Table"; "Statement"; "Timing"; "Created"; "sql_mode"; "Definer"
                        "character_set_client"; "collation_connection"; "Database Collation" ]
                      "probed headers"

                  Expect.equal (List.item 0 row) (Some "trg") "Trigger"
                  Expect.equal (List.item 1 row) (Some "INSERT") "Event"
                  Expect.equal (List.item 2 row) (Some "t") "Table"
                  Expect.equal (List.item 3 row) (Some "INSERT INTO log(n) VALUES (NEW.n)") "Statement is the raw body"
                  Expect.equal (List.item 4 row) (Some "AFTER") "Timing"
                  Expect.isSome (List.item 5 row) "Created present"
                  Expect.equal (List.item 6 row) (Some Fsdb.Sql.SqlMode.defaultText) "sql_mode"
                  Expect.equal (List.item 7 row) (Some "root@%") "Definer"
                  Expect.equal (List.item 8 row) (Some "utf8mb4") "character_set_client"
                  Expect.equal (List.item 9 row) (Some "utf8mb4_0900_ai_ci") "collation_connection"
                  Expect.equal (List.item 10 row) (Some "utf8mb4_0900_ai_ci") "Database Collation"
              | other -> failtestf "expected one SHOW TRIGGERS row, got %A" other

          testCase "SHOW CREATE TRIGGER renders a reusable definition"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let session = step session "CREATE TABLE t (id INT, n INT)"
              let session = step session "CREATE TABLE log (n INT)"
              let session = step session "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)"

              match handle session "SHOW CREATE TRIGGER fsdb.trg" |> snd with
              | ResultSet(columns, [ row ]) ->
                  Expect.equal
                      columns
                      [ "Trigger"; "sql_mode"; "SQL Original Statement"; "character_set_client"; "collation_connection"
                        "Database Collation"; "Created" ]
                      "MySQL headers"

                  Expect.equal (List.item 0 row) (Some "trg") "trigger name"
                  Expect.equal
                      (List.item 2 row)
                      (Some "CREATE DEFINER=`root`@`%` TRIGGER `trg` AFTER INSERT ON `t` FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                      "trigger definition"
                  Expect.equal (List.item 1 row) (Some Fsdb.Sql.SqlMode.defaultText) "sql_mode"
                  Expect.equal (List.item 3 row) (Some "utf8mb4") "character_set_client"
                  Expect.equal (List.item 4 row) (Some "utf8mb4_0900_ai_ci") "collation_connection"
                  Expect.equal (List.item 5 row) (Some "utf8mb4_0900_ai_ci") "Database Collation"
                  Expect.isSome (List.item 6 row) "creation time"
              | other -> failtestf "expected one SHOW CREATE TRIGGER row, got %A" other

              match handle session "SHOW CREATE TRIGGER missing" |> snd with
              | Err(1360, _) -> ()
              | other -> failtestf "expected missing-trigger 1360, got %A" other

          testCase "information_schema.TRIGGERS renders the row with MySQL's probed constants"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (1)")
                  "create trigger"

              Expect.equal
                  (rows
                      store
                      "SELECT TRIGGER_NAME, EVENT_MANIPULATION, EVENT_OBJECT_TABLE, ACTION_ORDER, ACTION_STATEMENT, ACTION_ORIENTATION, ACTION_TIMING, ACTION_REFERENCE_NEW_ROW FROM information_schema.TRIGGERS")
                  [ [ Some "trg"; Some "INSERT"; Some "t"; Some "1"; Some "INSERT INTO log(n) VALUES (1)"; Some "ROW"
                      Some "AFTER"; Some "NEW" ] ]
                  "probed information_schema row"

          // ---------------------------------------------------------------
          // DEFINER semantics. A body runs with the privileges of whoever
          // created the trigger, never the account whose INSERT fired it —
          // otherwise GRANT TRIGGER on one table would hand its holder write
          // access to every table the body can name.
          // ---------------------------------------------------------------

          testCase "a body runs as its definer, so TRIGGER on one table can't write a table the definer lacks"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store
              let sql (s: Fsdb.Session.Session) text = handle s text |> snd

              expectOk (sql session "CREATE TABLE pub (id INT PRIMARY KEY)") "create pub"
              expectOk (sql session "CREATE TABLE secret (id INT PRIMARY KEY)") "create secret"
              expectOk (sql session "CREATE USER low") "create user"
              expectOk (sql session "GRANT SELECT, INSERT, TRIGGER ON fsdb.pub TO low") "grant pub"
              expectOk (sql session "GRANT SELECT ON fsdb.secret TO low") "grant secret select"

              // `low` creates the trigger (allowed: they hold TRIGGER on pub)
              // and is therefore its definer.
              let lowSession = { Fsdb.Session.create 2 store with User = "low" }

              expectOk
                  (sql lowSession "CREATE TRIGGER esc AFTER INSERT ON pub FOR EACH ROW INSERT INTO secret VALUES (NEW.id)")
                  "low creates trigger"

              // Firing it must fail exactly like low's direct INSERT would.
              match sql lowSession "INSERT INTO pub VALUES (1)" with
              | Err(1142, msg) -> Expect.stringContains msg "secret" "1142 names the table the definer can't write"
              | other -> failtestf "expected the body to be denied 1142, got %A" other

              Expect.equal (rows store "SELECT COUNT(*) FROM secret") [ [ Some "0" ] ] "nothing was written to secret"

          testCase "a root-created trigger still fires for an inserter who can't write the body's table"
          <| fun _ ->
              // The other half of DEFINER semantics: privileges follow the
              // definer, so root's trigger works no matter who inserts.
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store
              let sql (s: Fsdb.Session.Session) text = handle s text |> snd

              expectOk (sql session "CREATE TABLE pub (id INT PRIMARY KEY)") "create pub"
              expectOk (sql session "CREATE TABLE audit (id INT PRIMARY KEY)") "create audit"
              expectOk (sql session "CREATE USER low2") "create user"
              expectOk (sql session "GRANT SELECT, INSERT ON fsdb.pub TO low2") "grant pub"

              expectOk
                  (sql session "CREATE TRIGGER aud AFTER INSERT ON pub FOR EACH ROW INSERT INTO audit VALUES (NEW.id)")
                  "root creates trigger"

              let lowSession = { Fsdb.Session.create 2 store with User = "low2" }
              expectOk (sql lowSession "INSERT INTO pub VALUES (7)") "low2 inserts"

              Expect.equal (rows store "SELECT id FROM audit") [ [ Some "7" ] ] "root's trigger wrote the audit row"

          testCase "a trigger retains its host-qualified definer"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = Fsdb.Session.create 1 store
              let sql (session: Fsdb.Session.Session) text = handle session text |> snd

              expectOk (sql root "CREATE TABLE pub (id INT PRIMARY KEY)") "create pub"
              expectOk (sql root "CREATE TABLE audit (id INT PRIMARY KEY)") "create audit"
              expectOk (sql root "CREATE USER 'owner'@'%'") "create broad owner"
              expectOk (sql root "CREATE USER 'owner'@'localhost'") "create local owner"
              expectOk (sql root "CREATE USER writer") "create writer"
              expectOk (sql root "GRANT SELECT, INSERT, TRIGGER ON fsdb.pub TO 'owner'@'localhost'") "grant local subject privileges"
              expectOk (sql root "GRANT INSERT ON fsdb.audit TO 'owner'@'localhost'") "grant local body privilege"
              expectOk (sql root "GRANT INSERT ON fsdb.pub TO writer") "grant writer"

              let owner = { Fsdb.Session.create 2 store with User = "owner"; AccountHost = "localhost" }
              let writer = { Fsdb.Session.create 3 store with User = "writer" }

              expectOk
                  (sql owner "CREATE TRIGGER hosted AFTER INSERT ON pub FOR EACH ROW INSERT INTO audit VALUES (NEW.id)")
                  "create hosted trigger"
              expectOk (sql writer "INSERT INTO pub VALUES (1)") "fire hosted trigger"

              Expect.equal (rows store "SELECT id FROM audit") [ [ Some "1" ] ] "localhost definer writes the audit row"

              match sql root "SHOW TRIGGERS" with
              | ResultSet(_, [ row ]) -> Expect.equal (List.item 7 row) (Some "owner@localhost") "stored full definer"
              | other -> failtestf "expected hosted trigger metadata, got %A" other

          testCase "a trigger evaluates CURRENT_USER as its definer"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let root = Fsdb.Session.create 1 store
              let sql (session: Fsdb.Session.Session) text = handle session text |> snd

              expectOk (sql root "CREATE TABLE pub (id INT PRIMARY KEY)") "create pub"
              expectOk
                  (sql root "CREATE TABLE audit (definer_identity VARCHAR(100), invoker_identity VARCHAR(100), function_identity VARCHAR(100))")
                  "create audit"
              expectOk (sql root "CREATE USER owner") "create owner"
              expectOk (sql root "CREATE USER writer") "create writer"
              expectOk (sql root "GRANT SELECT, INSERT, TRIGGER ON fsdb.pub TO owner") "grant owner subject privileges"
              expectOk (sql root "GRANT INSERT ON fsdb.audit TO owner") "grant owner body privilege"
              expectOk (sql root "GRANT CREATE ROUTINE, EXECUTE ON fsdb.* TO owner") "grant owner routine privileges"
              expectOk (sql root "GRANT INSERT ON fsdb.pub TO writer") "grant writer"

              let owner = { Fsdb.Session.create 2 store with User = "owner" }
              let writer = { Fsdb.Session.create 3 store with User = "writer" }

              expectOk
                  (sql owner "CREATE FUNCTION trigger_identity() RETURNS VARCHAR(100) SQL SECURITY INVOKER RETURN CURRENT_USER()")
                  "create trigger function"

              expectOk
                  (sql owner "CREATE TRIGGER identity AFTER INSERT ON pub FOR EACH ROW INSERT INTO audit VALUES (CURRENT_USER(), USER(), trigger_identity())")
                  "create identity trigger"
              expectOk (sql writer "INSERT INTO pub VALUES (1)") "fire identity trigger"

              Expect.equal
                  (rows store "SELECT definer_identity, invoker_identity, function_identity FROM audit")
                  [ [ Some "owner@%"; Some "writer@localhost"; Some "owner@%" ] ]
                  "definer, invoker, and nested function identities"

          testCase "SHOW TRIGGERS reports the real definer, not a constant"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store
              let sql (s: Fsdb.Session.Session) text = handle s text |> snd

              expectOk (sql session "CREATE TABLE pub (id INT PRIMARY KEY)") "create pub"
              expectOk (sql session "CREATE TABLE log2 (id INT PRIMARY KEY)") "create log2"
              expectOk (sql session "CREATE USER dev") "create user"
              expectOk (sql session "GRANT ALL PRIVILEGES ON fsdb.* TO dev") "grant"

              let devSession = { Fsdb.Session.create 2 store with User = "dev" }

              expectOk
                  (sql devSession "CREATE TRIGGER t_dev AFTER INSERT ON pub FOR EACH ROW INSERT INTO log2 VALUES (NEW.id)")
                  "dev creates trigger"

              // SHOW is a text probe, so it goes through `handle`, not the parser.
              match handle session "SHOW TRIGGERS" |> snd with
              | ResultSet(_, [ row ]) -> Expect.equal (List.item 7 row) (Some "dev@%") "Definer is the creating account"
              | other -> failtestf "expected one SHOW TRIGGERS row, got %A" other

          testCase "dropping a subject table removes its trigger"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER stale AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                  "create trigger"

              expectOk (runDefault store "DROP TABLE t") "drop subject"
              expectOk (runDefault store "CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, n INT)") "recreate subject"
              expectOk (runDefault store "INSERT INTO t(n) VALUES (9)") "insert into recreated table"
              Expect.equal (rows store "SELECT COUNT(*) FROM log") [ [ Some "0" ] ] "dropped trigger did not reattach"

          testCase "renaming a subject table keeps its trigger attached"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER carried AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                  "create trigger"

              expectOk (runDefault store "RENAME TABLE t TO renamed") "rename subject"
              expectOk (runDefault store "INSERT INTO renamed(n) VALUES (12)") "insert into renamed table"
              Expect.equal (rows store "SELECT n FROM log") [ [ Some "12" ] ] "trigger followed its subject" ]
