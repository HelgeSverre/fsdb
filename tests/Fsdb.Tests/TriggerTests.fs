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

/// Same shape as `ExecutorTests.run`: parse + execute one statement against
/// `store`, failing the test on a parse error so cases read as plain SQL.
let private run (store: Store) (registry: Registry) (sql: string) : QueryResult =
    match Fsdb.Parser.parse sql with
    | Error msg -> failtestf "expected %s to parse, got error: %s" sql msg
    | Ok stmt -> execute store registry defaultDatabase (0L, 0L) false stmt |> snd

let private runDefault (store: Store) (sql: string) : QueryResult = run store builtins sql

let private expectOk (result: QueryResult) (label: string) =
    match result with
    | Err(code, msg) -> failtestf "%s: unexpected error %d: %s" label code msg
    | _ -> ()

let private rows (store: Store) (sql: string) : string option list list =
    match runDefault store sql with
    | ResultSet(_, rows) -> rows
    | other -> failtestf "expected a resultset from %s, got %A" sql other

/// t (the trigger's subject) + log (what the body writes into).
let private setup (store: Store) =
    expectOk (runDefault store "CREATE TABLE t (id INT AUTO_INCREMENT PRIMARY KEY, n INT)") "create t"
    expectOk (runDefault store "CREATE TABLE log (id INT AUTO_INCREMENT PRIMARY KEY, n INT)") "create log"

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
              | Ok(Fsdb.Ast.CreateTrigger("before_delete", Fsdb.Ast.Before, Fsdb.Ast.TriggerDelete, "t", _)) -> ()
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

          testCase "SET NEW is rejected outside a trigger body"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              Expect.equal
                  (runDefault store "SET NEW.n = 1")
                  (Err(1064, "SET NEW is only valid in a trigger body"))
                  "NEW has no row image outside a trigger"

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
              Expect.equal (rows store "SELECT n FROM log ORDER BY id") [ [ Some "10" ]; [ Some "20" ] ] "one insert event per candidate"

          testCase "REPLACE refuses tables with DELETE triggers"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store
              expectOk (runDefault store "INSERT INTO t VALUES (1, 10)") "seed t"
              expectOk
                  (runDefault store "CREATE TRIGGER delete_log AFTER DELETE ON t FOR EACH ROW INSERT INTO log(n) VALUES (OLD.n)")
                  "create delete trigger"

              Expect.equal
                  (runDefault store "REPLACE INTO t VALUES (1, 20)")
                  (Err(1235, "REPLACE on a table with DELETE triggers is not supported"))
                  "REPLACE cannot silently skip DELETE triggers"

              Expect.equal (rows store "SELECT n FROM t") [ [ Some "10" ] ] "refused statement changes no rows"

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

          testCase "a trigger chain deeper than 8 fires 1442"
          <| fun _ ->
              let store = Fsdb.Storage.create ()

              for i in 0..9 do
                  expectOk (runDefault store (sprintf "CREATE TABLE c%d (n INT)" i)) "create chain table"

              for i in 0..8 do
                  expectOk
                      (runDefault
                          store
                          (sprintf "CREATE TRIGGER chain%d AFTER INSERT ON c%d FOR EACH ROW INSERT INTO c%d(n) VALUES (NEW.n)" i i (i + 1)))
                      "create chain trigger"

              match runDefault store "INSERT INTO c0(n) VALUES (1)" with
              | Err(1442, _) -> ()
              | other -> failtestf "expected the depth cap's 1442, got %A" other

          testCase "duplicate trigger name and second trigger on the same table both refuse with 1359"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (1)")
                  "first trigger"

              match runDefault store "CREATE TRIGGER trg AFTER INSERT ON log FOR EACH ROW INSERT INTO t(n) VALUES (1)" with
              | Err(1359, "Trigger already exists") -> ()
              | other -> failtestf "expected 1359 for the duplicate name, got %A" other

              // ponytail divergence (MySQL 8 allows multiple triggers per
              // table/timing/event): fsdb pins one per slot.
              match runDefault store "CREATE TRIGGER other AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (2)" with
              | Err(1359, "Trigger already exists") -> ()
              | other -> failtestf "expected 1359 for a second trigger on the same table, got %A" other

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

              match runDefault store "CREATE TRIGGER bad AFTER INSERT ON t FOR EACH ROW SELECT 1" with
              | Err(1064, msg) -> Expect.stringContains msg "single INSERT, UPDATE, or DELETE" "kind restriction named"
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

              let step session sql =
                  let session, result = handle session sql
                  expectOk result sql
                  session

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
              let dir =
                  System.IO.Path.Combine(
                      System.IO.Path.GetTempPath(),
                      "fsdb-trigger-tests",
                      System.Guid.NewGuid().ToString "N"
                  )

              System.IO.Directory.CreateDirectory dir |> ignore

              let store = Fsdb.Storage.create ()
              Fsdb.Persistence.attach dir store
              setup store

              expectOk
                  (runDefault store "CREATE TRIGGER trg AFTER INSERT ON t FOR EACH ROW INSERT INTO log(n) VALUES (NEW.n)")
                  "create trigger"

              expectOk (runDefault store "INSERT INTO t(n) VALUES (7)") "insert before restart"

              let reloaded = Fsdb.Persistence.load dir
              Expect.equal (rows reloaded "SELECT n FROM log") [ [ Some "7" ] ] "replayed effect rows survive"

              expectOk (runDefault reloaded "INSERT INTO t(n) VALUES (8)") "insert after restart"

              Expect.equal
                  (rows reloaded "SELECT n FROM log ORDER BY n")
                  [ [ Some "7" ]; [ Some "8" ] ]
                  "the trigger itself survived the restart and fired again"

          testCase "SHOW TRIGGERS renders the mysql.triggers row in MySQL's probed shape"
          <| fun _ ->
              let store = Fsdb.Storage.create ()
              let session = Fsdb.Session.create 1 store

              let step session sql =
                  let session, result = handle session sql
                  expectOk result sql
                  session

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
              | other -> failtestf "expected one SHOW TRIGGERS row, got %A" other

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
