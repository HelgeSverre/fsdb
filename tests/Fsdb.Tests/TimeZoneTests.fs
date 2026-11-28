module Fsdb.Tests.TimeZoneTests

open Expecto
open Fsdb.Session
open Fsdb.Executor
open Fsdb.QueryHandler

let private execute session sql =
    let session, result = handle session sql
    match result with
    | Err(code, message) -> failtestf "%s: %d %s" sql code message
    | _ -> session

let private populate session =
    [ "INSERT INTO mysql.time_zone (Time_zone_id,Use_leap_seconds) VALUES (900001,'N')"
      "INSERT INTO mysql.time_zone_name VALUES ('Fsdb/Test_Eastern',900001)"
      "INSERT INTO mysql.time_zone_transition_type (Time_zone_id, Transition_type_id, Offset, Is_DST, Abbreviation) VALUES (900001,0,-18000,0,'EST'),(900001,1,-14400,1,'EDT')"
      "INSERT INTO mysql.time_zone_transition VALUES (900001,1710054000,1),(900001,1730613600,0)" ]
    |> List.fold execute session

let private populatedSession () = create 1 (Fsdb.Storage.create ()) |> populate

let private expectRow session sql expected =
    match handle session sql |> snd with
    | ResultSet(_, rows) -> Expect.equal rows [ List.map Some expected ] sql
    | other -> failtestf "Expected rows for %s, got %A" sql other

let tests =
    testList "Time zones"
        [ testCase "indexed TIMESTAMP keys compare session-time values"
          <| fun _ ->
              let session =
                  [ "CREATE DATABASE stamped"
                    "USE stamped"
                    "CREATE TABLE stamped_key (id INT PRIMARY KEY, at TIMESTAMP NULL, KEY (at))"
                    "SET time_zone = '+00:00'"
                    "INSERT INTO stamped_key VALUES (1, '2026-01-01 10:00:00'), (2, '2026-01-01 12:00:00')"
                    "SET time_zone = '+02:00'" ]
                  |> List.fold execute (create 1 (Fsdb.Storage.create ()))

              expectRow session "SELECT id FROM stamped_key WHERE at = '2026-01-01 12:00:00'" [ "1" ]
              expectRow session "SELECT id FROM stamped_key WHERE at IN ('2026-01-01 14:00:00')" [ "2" ]
              expectRow session "SELECT id FROM stamped_key WHERE id = 2 AND at = '2026-01-01 14:00:00'" [ "2" ]

          testCase "correlated ordered TIMESTAMP equality uses the indexed UTC occurrence"
          <| fun _ ->
              let session =
                  [ "SET time_zone = '+00:00'"
                    "CREATE TABLE picks (id INT PRIMARY KEY, instant TIMESTAMP(6) NULL, wall DATETIME(6) NULL)"
                    "CREATE TABLE events (id INT PRIMARY KEY, instant TIMESTAMP(2) NULL, ordinal INT, enabled INT, KEY by_slot(instant,ordinal DESC,id))"
                    "INSERT INTO picks VALUES (1,'2024-11-03 05:30:00.20','2024-11-03 01:30:00.20'),(2,'2024-11-03 06:30:00.20','2024-11-03 01:30:00.20'),(3,'2024-11-03 06:30:00.21','2024-11-03 01:30:00.210001'),(4,NULL,NULL)"
                    "INSERT INTO events VALUES (11,'2024-11-03 05:30:00.20',3,0),(12,'2024-11-03 05:30:00.20',2,1),(13,'2024-11-03 05:30:00.20',1,1),(21,'2024-11-03 06:30:00.20',3,0),(22,'2024-11-03 06:30:00.20',4,1),(23,'2024-11-03 06:30:00.20',1,1),(31,'2024-11-03 06:30:00.21',3,0),(32,'2024-11-03 06:30:00.21',2,1),(33,'2024-11-03 06:30:00.21',1,1),(41,NULL,3,0),(42,NULL,2,1),(43,NULL,1,1)"
                    "SET time_zone = 'Fsdb/Test_Eastern'" ]
                  |> List.fold execute (populatedSession ())

              let scalar hint column =
                  $"(SELECT e.id FROM events e {hint} INDEX(by_slot) WHERE e.instant<=>p.{column} AND e.enabled>0 ORDER BY e.ordinal DESC,e.id LIMIT 1 OFFSET 1)"

              let checkRows session sql expected =
                  match handle session sql |> snd with
                  | ResultSet(_, rows) -> Expect.equal rows expected sql
                  | other -> failtestf "Expected rows for %s, got %A" sql other

              let check session column expected =
                  let forced, ignored = scalar "FORCE" column, scalar "IGNORE" column
                  checkRows session $"SELECT {forced}, {ignored} FROM picks p ORDER BY p.id" expected

              let both first second = [ Some first; Some second ]
              check session "instant" [ both "13" "12"; both "13" "12"; [ None; Some "33" ]; both "43" "43" ]
              check session "wall" [ both "13" "12"; both "13" "12"; [ None; None ]; both "43" "43" ]

              let ordered = scalar "FORCE" "instant"
              let derived =
                  ordered.Replace("events e FORCE INDEX(by_slot)", "(SELECT id,instant,ordinal,enabled FROM events FORCE INDEX(by_slot)) e")
              let expectedDerived = [ [ Some "13" ]; [ Some "13" ]; [ None ]; [ Some "43" ] ]
              checkRows session $"SELECT {derived} FROM picks p ORDER BY p.id" expectedDerived
              let wildcard = derived.Replace("SELECT id,instant,ordinal,enabled", "SELECT *")
              checkRows session $"SELECT {wildcard} FROM picks p ORDER BY p.id" expectedDerived

              let sorted = ordered.Replace("e.ordinal DESC,e.id", "e.ordinal ASC,e.id ASC")
              let sortedQuery = $"SELECT {sorted} FROM picks p ORDER BY p.id"
              checkRows session sortedQuery [ [ Some "12" ]; [ Some "12" ]; [ None ]; [ Some "42" ] ]
              match handle session ("EXPLAIN " + sortedQuery) |> snd with
              | ResultSet(_, [ _; dependent ]) ->
                  Expect.equal dependent.[4] (Some "ref") "the filesort still reads an indexed UTC slice"
                  Expect.equal dependent.[6] (Some "by_slot") "the UTC prefix uses the stored composite index"
                  Expect.stringContains (dependent.[11] |> Option.defaultValue "") "Using filesort" "the suffix changes index direction"
              | other -> failtestf "Expected the dependent ref and filesort, got %A" other

              let fixedOffset = execute session "SET time_zone = '-04:00'"
              check fixedOffset "instant" [ both "13" "13"; both "23" "23"; both "33" "33"; both "43" "43" ]

          testCase "TIMESTAMP filesort distinguishes both occurrences of a repeated local time"
          <| fun _ ->
              let session =
                  [ "SET time_zone = '+00:00'"
                    "CREATE TABLE events (id INT PRIMARY KEY, instant TIMESTAMP, KEY by_slot(instant))"
                    "INSERT INTO events VALUES (2,'2024-11-03 05:30:00'),(1,'2024-11-03 06:30:00')"
                    "CREATE TABLE picks (wall DATETIME)"
                    "INSERT INTO picks VALUES ('2024-11-03 01:30:00')"
                    "CREATE ALGORITHM=MERGE VIEW merged_events AS SELECT id,instant FROM events IGNORE INDEX(by_slot)"
                    "CREATE ALGORITHM=TEMPTABLE VIEW materialized_events AS SELECT id,instant FROM events IGNORE INDEX(by_slot)"
                    "SET time_zone = 'Fsdb/Test_Eastern'" ]
                  |> List.fold execute (populatedSession ())

              let check sql ids =
                  match handle session sql |> snd with
                  | ResultSet(_, rows) -> Expect.equal (rows |> List.map List.last) (ids |> List.map Some) sql
                  | other -> failtestf "Expected rows for %s, got %A" sql other

              let direct = "events e IGNORE INDEX(by_slot)"
              let derived = "(SELECT id,instant AS stamp FROM events IGNORE INDEX(by_slot)) e"
              check $"SELECT e.instant AS stamp,e.id FROM {direct} ORDER BY stamp,id" [ "2"; "1" ]
              check $"SELECT e.instant,e.id FROM {direct} ORDER BY 1,id LIMIT 1" [ "2" ]
              check $"SELECT id FROM {direct} ORDER BY CAST(e.instant AS DATETIME),id" [ "1"; "2" ]
              check $"SELECT id FROM {derived} ORDER BY e.stamp,id" [ "2"; "1" ]
              check $"SELECT /*+ NO_MERGE(e) */ id FROM {derived} ORDER BY e.stamp,id" [ "1"; "2" ]
              check $"SELECT (SELECT id FROM {direct} WHERE e.instant=p.wall ORDER BY e.instant,id LIMIT 1) FROM picks p" [ "2" ]
              check $"SELECT (SELECT id FROM {derived} WHERE e.stamp=p.wall ORDER BY e.stamp,id LIMIT 1) FROM picks p" [ "2" ]
              expectRow session
                  ("SELECT (SELECT id FROM merged_events e WHERE e.instant=p.wall ORDER BY e.instant,id LIMIT 1), "
                   + "(SELECT id FROM materialized_events e WHERE e.instant=p.wall ORDER BY e.instant,id LIMIT 1) FROM picks p")
                  [ "2"; "1" ]

          testCase "named zones use catalog transitions including daylight saving gaps and folds" <| fun _ ->
              let session = populatedSession ()
              expectRow session
                  "SELECT CONVERT_TZ('2024-01-01 12:00:00','Fsdb/Test_Eastern','+00:00'), CONVERT_TZ('2024-07-01 12:00:00','Fsdb/Test_Eastern','+00:00'), CONVERT_TZ('2024-03-10 02:30:12.123456','Fsdb/Test_Eastern','+00:00'), CONVERT_TZ('2024-11-03 01:30:12.123456','Fsdb/Test_Eastern','+00:00')"
                  [ "2024-01-01 17:00:00"; "2024-07-01 16:00:00"; "2024-03-10 07:00:00.123456"; "2024-11-03 05:30:12.123456" ]

          testCase "named session zones drive Unix functions and timestamp storage" <| fun _ ->
              let session = populatedSession () |> fun s -> execute s "SET time_zone='Fsdb/Test_Eastern'"
              expectRow session
                  "SELECT @@time_zone, FROM_UNIXTIME(1710054000), UNIX_TIMESTAMP('2024-11-03 01:30:12.123456')"
                  [ "Fsdb/Test_Eastern"; "2024-03-10 03:00:00"; "1730611812.123456" ]
              let session = execute session "CREATE TABLE tz_values (t TIMESTAMP(6), d DATETIME(6))"
              let session = execute session "INSERT INTO tz_values VALUES ('2024-11-03 01:30:12.123456','2024-11-03 01:30:12.123456')"
              let session = execute session "SET time_zone='+00:00'"
              expectRow session "SELECT t,d FROM tz_values" [ "2024-11-03 05:30:12.123456"; "2024-11-03 01:30:12.123456" ]

          testCase "timestamp writes reject skipped local times in strict mode" <| fun _ ->
              let session = populatedSession () |> fun s -> execute s "SET time_zone='Fsdb/Test_Eastern'"
              let session = execute session "CREATE TABLE tz_gap (t TIMESTAMP(6))"
              match handle session "INSERT INTO tz_gap VALUES ('2024-03-10 02:30:12.123456')" |> snd with
              | Err(1292, _) -> ()
              | other -> failtestf "Expected invalid datetime error, got %A" other
              let session = execute session "SET sql_mode=''"
              let session = execute session "INSERT INTO tz_gap VALUES ('2024-03-10 02:30:12.123456')"
              Expect.equal (session.Diagnostics |> List.map (fun condition -> condition.Code, condition.Message))
                  [ 1299, "Invalid TIMESTAMP value in column 't' at row 1" ] "invalid timestamp warning"
              expectRow session "SELECT t FROM tz_gap" [ "2024-03-10 03:00:00.123456" ]

          testCase "named zone cache is shared by sessions and preserves loaded rules" <| fun _ ->
              let session = populatedSession () |> fun s -> execute s "SET GLOBAL time_zone='Fsdb/Test_Eastern'"
              let reader = create 2 session.Store
              expectRow reader "SELECT @@time_zone, FROM_UNIXTIME(1710054000)" [ "Fsdb/Test_Eastern"; "2024-03-10 03:00:00" ]
              let session = execute session "DELETE FROM mysql.time_zone_transition_type WHERE Time_zone_id=900001"
              let reader = execute reader "SET time_zone='fsdb/test_eastern'"
              expectRow reader "SELECT @@time_zone, FROM_UNIXTIME(1710054000)" [ "Fsdb/Test_Eastern"; "2024-03-10 03:00:00" ]
              expectRow session "SELECT CONVERT_TZ('2024-07-01 12:00:00','Fsdb/Test_Eastern','+00:00')" [ "2024-07-01 16:00:00" ]

          testCase "named rules preserve transition boundaries and fractional pre-epoch inputs" <| fun _ ->
              let session = populatedSession ()
              expectRow session
                  "SELECT CONVERT_TZ('2024-03-10 06:59:59.999999','+00:00','Fsdb/Test_Eastern'), CONVERT_TZ('2024-03-10 07:00:00','+00:00','Fsdb/Test_Eastern'), CONVERT_TZ('2024-11-03 06:00:00','+00:00','Fsdb/Test_Eastern'), CONVERT_TZ('1969-12-31 23:59:59.123456','Fsdb/Test_Eastern','+00:00')"
                  [ "2024-03-10 01:59:59.999999"; "2024-03-10 03:00:00"; "2024-11-03 01:00:00"; "1970-01-01 04:59:59.123456" ]

          testCase "unknown zones can be loaded later and fixed named zones need no transitions" <| fun _ ->
              let session = create 1 (Fsdb.Storage.create ())
              match handle session "SET time_zone='Fsdb/Test_Eastern'" |> snd with
              | Err(1298, _) -> ()
              | other -> failtestf "Expected unknown zone, got %A" other
              let session = populate session
              let session = execute session "DELETE FROM mysql.time_zone_transition WHERE Time_zone_id=900001"
              let session = execute session "SET time_zone='fsdb/test_eastern'"
              expectRow session "SELECT @@time_zone, CONVERT_TZ('2024-07-01','fsdb/test_eastern','+00:00')"
                  [ "fsdb/test_eastern"; "2024-07-01 05:00:00" ]

          testCase "named zone rules reload from WAL and snapshots" <| fun _ ->
              let directory = TestSupport.directory "time-zones"
              let store = Fsdb.Persistence.load directory
              Fsdb.Persistence.attach directory store
              let session = create 1 store |> populate
              let session = execute session "SET time_zone='Fsdb/Test_Eastern'"
              let session = execute session "CREATE TABLE tz_persisted (t TIMESTAMP(6))"
              execute session "INSERT INTO tz_persisted VALUES ('2024-07-01 12:00:00.123456')" |> ignore
              let verify () =
                  let reader = create 2 (Fsdb.Persistence.load directory)
                  let reader = execute reader "SET time_zone='Fsdb/Test_Eastern'"
                  expectRow reader "SELECT t FROM tz_persisted" [ "2024-07-01 12:00:00.123456" ]
                  expectRow reader "SELECT CONVERT_TZ('2024-11-03 01:30:12','Fsdb/Test_Eastern','+00:00')" [ "2024-11-03 05:30:12" ]
              verify ()
              Fsdb.Persistence.snapshotNow directory store
              verify () ]
