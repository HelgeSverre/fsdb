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
        [ testCase "named zones use catalog transitions including daylight saving gaps and folds" <| fun _ ->
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
