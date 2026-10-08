module Fsdb.Tests.ExecutorSubqueryTests

open Expecto
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Executor

let private run = TestSupport.Sql.execute
let private runDefault = TestSupport.Sql.executeDefault
let private newStore () = create ()

let tests =
    testList
        "correlated subqueries"
        [ testCase "numeric aggregate metadata retains binary numeric coercibility"
          <| fun _ ->
              Expect.equal
                  (runDefault (newStore ()) "SELECT COERCIBILITY(SUM('1')) AS s,COERCIBILITY(AVG('1')) AS a,COERCIBILITY(COUNT(*)) AS c,COLLATION(SUM('1')) AS col,CHARSET(AVG('1')) AS ch")
                  (ResultSet([ "s"; "a"; "c"; "col"; "ch" ], [ [ Some "5"; Some "5"; Some "5"; Some "binary"; Some "binary" ] ]))
                  "Metadata-only aggregate expressions retain their result domain"

          testCase "grouped aggregate inputs retain source-row evaluation order"
          <| fun _ ->
              let connection = Fsdb.Db.create () |> Fsdb.Db.connect
              let execute sql = connection.Query sql
              execute "CREATE TABLE aggregate_input(v INT)" |> ignore
              execute "INSERT INTO aggregate_input VALUES(2),(1),(2)" |> ignore
              execute "SET @n=0" |> ignore
              Expect.equal
                  (execute "SELECT v,SUM(@n:=@n+1) AS s FROM aggregate_input GROUP BY v ORDER BY v")
                  (ResultSet([ "v"; "s" ], [ [ Some "1"; Some "2" ]; [ Some "2"; Some "4" ] ]))
                  "Interleaved groups accumulate values at their source-row positions"
              Expect.equal (execute "SELECT @n") (ResultSet([ "@n" ], [ [ Some "3" ] ])) "Each input evaluates once"

              for sql, names, rows, count in
                  [ "SELECT COERCIBILITY(SUM(@n:=@n+1)) AS c FROM aggregate_input GROUP BY v ORDER BY v", [ "c" ], [ [ Some "5" ]; [ Some "5" ] ], "3"
                    "SELECT v,SUM(@n:=@n+1) AS s FROM aggregate_input GROUP BY v HAVING SUM(@n:=@n+1)>0 ORDER BY v", [ "v"; "s" ], [ [ Some "1"; Some "4" ]; [ Some "2"; Some "8" ] ], "6"
                    "SELECT SUM(@n:=@n+1) AS s FROM aggregate_input GROUP BY v ORDER BY SUM(@n:=@n+1)", [ "s" ], [ [ Some "2" ]; [ Some "4" ] ], "3"
                    "SELECT SUM(@n:=@n+1) AS s FROM aggregate_input GROUP BY v ORDER BY ABS(SUM(@n:=@n+1))", [ "s" ], [ [ Some "4" ]; [ Some "8" ] ], "6"
                    "SELECT SUM(@n:=@n+1) AS a,SUM(@n:=@n+1) AS b FROM aggregate_input GROUP BY v ORDER BY ABS(SUM(@n:=@n+1))", [ "a"; "b" ], [ [ Some "5"; Some "6" ]; [ Some "10"; Some "12" ] ], "9" ] do
                  execute "SET @n=0" |> ignore
                  Expect.equal (execute sql) (ResultSet(names, rows)) sql
                  Expect.equal (execute "SELECT @n") (ResultSet([ "@n" ], [ [ Some count ] ])) "Each aggregate occurrence retains its input values"

          testCase "aggregate families select grouping input evaluation order"
          <| fun _ ->
              let connection = Fsdb.Db.create () |> Fsdb.Db.connect
              let execute sql = connection.Query sql
              execute "CREATE TABLE group_plan(id INT PRIMARY KEY,g INT,v INT)" |> ignore
              execute "INSERT INTO group_plan VALUES(1,2,10),(2,1,20),(3,2,30)" |> ignore
              for sql, names, rows in
                  [ "SELECT g,SUM(@n:=@n+1) AS s, MIN(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "2"; Some "20" ]; [ Some "2"; Some "4"; Some "10" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, MAX(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "2"; Some "20" ]; [ Some "2"; Some "4"; Some "30" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, COUNT(DISTINCT 1) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "1"; Some "1" ]; [ Some "2"; Some "5"; Some "1" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, SUM(DISTINCT 1) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "1"; Some "1" ]; [ Some "2"; Some "5"; Some "1" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, AVG(DISTINCT 1) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "1"; Some "1.0000" ]; [ Some "2"; Some "5"; Some "1.0000" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "1"; Some "1" ]; [ Some "2"; Some "5"; Some "2" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, SUM(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "1"; Some "20" ]; [ Some "2"; Some "5"; Some "40" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, AVG(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "1"; Some "20.0000" ]; [ Some "2"; Some "5"; Some "20.0000" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, MIN(v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "2"; Some "20" ]; [ Some "2"; Some "4"; Some "10" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, MAX(v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "2"; Some "20" ]; [ Some "2"; Some "4"; Some "30" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, GROUP_CONCAT(v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "1"; Some "20" ]; [ Some "2"; Some "5"; Some "10,30" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, GROUP_CONCAT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "1"; Some "20" ]; [ Some "2"; Some "5"; Some "10,30" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, JSON_ARRAYAGG(v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "1"; Some "[20]" ]; [ Some "2"; Some "5"; Some "[10, 30]" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, JSON_OBJECTAGG(id,v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "1"; Some "{\"2\": 20}" ]; [ Some "2"; Some "5"; Some "{\"1\": 10, \"3\": 30}" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s, BIT_AND(v) AS other FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s"; "other" ], [ [ Some "1"; Some "2"; Some "20" ]; [ Some "2"; Some "4"; Some "10" ] ]
                    "SELECT g,SUM(@n:=@n+1) AS s FROM group_plan GROUP BY g ORDER BY g", [ "g"; "s" ], [ [ Some "1"; Some "2" ]; [ Some "2"; Some "4" ] ]
                    "SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY x DESC", [ "x"; "s"; "other" ], [ [ Some "2"; Some "3"; Some "2" ]; [ Some "1"; Some "3"; Some "1" ] ]
                    "SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g ORDER BY g DESC,s", [ "x"; "s"; "other" ], [ [ Some "2"; Some "5"; Some "2" ]; [ Some "1"; Some "1"; Some "1" ] ] ] do
                  execute "SET @n=0" |> ignore
                  Expect.equal (execute sql) (ResultSet(names, rows)) sql
                  Expect.equal (execute "SELECT @n") (ResultSet([ "@n" ], [ [ Some "3" ] ])) "Each source row evaluates once"

          testCase "grouping input reuses only matching order prefixes"
          <| fun _ ->
              let connection = Fsdb.Db.create () |> Fsdb.Db.connect
              let execute sql = connection.Query sql
              execute "CREATE TABLE group_plan(id INT PRIMARY KEY,g INT,h INT,v INT)" |> ignore
              execute "INSERT INTO group_plan VALUES(1,2,2,10),(2,1,1,20),(3,2,1,30),(4,1,2,40)" |> ignore
              for sql, rows in
                  [ "SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g,h ORDER BY g DESC,h ASC", [ [ Some "2"; Some "1"; Some "1"; Some "1" ]; [ Some "2"; Some "2"; Some "2"; Some "1" ]; [ Some "1"; Some "1"; Some "3"; Some "1" ]; [ Some "1"; Some "2"; Some "4"; Some "1" ] ]
                    "SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g,h ORDER BY h DESC,g ASC", [ [ Some "1"; Some "2"; Some "2"; Some "1" ]; [ Some "2"; Some "2"; Some "4"; Some "1" ]; [ Some "1"; Some "1"; Some "1"; Some "1" ]; [ Some "2"; Some "1"; Some "3"; Some "1" ] ]
                    "SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM group_plan GROUP BY g,h ORDER BY g DESC,h DESC,s", [ [ Some "2"; Some "2"; Some "4"; Some "1" ]; [ Some "2"; Some "1"; Some "3"; Some "1" ]; [ Some "1"; Some "2"; Some "2"; Some "1" ]; [ Some "1"; Some "1"; Some "1"; Some "1" ] ] ] do
                  execute "SET @n=0" |> ignore
                  Expect.equal (execute sql) (ResultSet([ "g"; "h"; "s"; "other" ], rows)) sql
                  Expect.equal (execute "SELECT @n") (ResultSet([ "@n" ], [ [ Some "4" ] ])) "Each source row evaluates once"

          testCase "ROLLUP evaluates aggregate levels only as demanded"
          <| fun _ ->
              let connection = Fsdb.Db.create () |> Fsdb.Db.connect
              let execute sql = connection.Query sql
              execute "CREATE TABLE rollup_input(id INT PRIMARY KEY,g INT,h INT)" |> ignore
              execute "INSERT INTO rollup_input VALUES(1,2,2),(2,1,1),(3,2,1),(4,1,2),(5,2,1)" |> ignore
              for sql, expected, counter in
                  [ "SELECT g,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g WITH ROLLUP", [ [ Some "1"; Some "6" ]; [ Some "2"; Some "24" ]; [ None; Some "25" ] ], "10"
                    "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP", [ [ Some "1"; Some "1"; Some "3" ]; [ Some "1"; Some "2"; Some "6" ]; [ Some "1"; None; Some "7" ]; [ Some "2"; Some "1"; Some "21" ]; [ Some "2"; Some "2"; Some "15" ]; [ Some "2"; None; Some "33" ]; [ None; None; Some "35" ] ], "15"
                    "SELECT g,h,SUM(@n:=@n+1) AS a,SUM(@n:=@n+1) AS b FROM rollup_input GROUP BY g,h WITH ROLLUP", [ [ Some "1"; Some "1"; Some "3"; Some "6" ]; [ Some "1"; Some "2"; Some "9"; Some "12" ]; [ Some "1"; None; Some "10"; Some "16" ]; [ Some "2"; Some "1"; Some "36"; Some "42" ]; [ Some "2"; Some "2"; Some "27"; Some "30" ]; [ Some "2"; None; Some "60"; Some "69" ]; [ None; None; Some "65"; Some "80" ] ], "30"
                    "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP ORDER BY g DESC,h DESC", [ [ Some "2"; Some "2"; Some "3" ]; [ Some "2"; Some "1"; Some "15" ]; [ Some "2"; None; Some "15" ]; [ Some "1"; Some "2"; Some "12" ]; [ Some "1"; Some "1"; Some "15" ]; [ Some "1"; None; Some "25" ]; [ None; None; Some "35" ] ], "15"
                    "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP HAVING GROUPING(g,h)=0", [ [ Some "1"; Some "1"; Some "3" ]; [ Some "1"; Some "2"; Some "6" ]; [ Some "2"; Some "1"; Some "21" ]; [ Some "2"; Some "2"; Some "15" ] ], "15"
                    "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 1", [ [ Some "1"; Some "1"; Some "3" ] ], "3"
                    "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 0", [  ], "0"
                    "SELECT g,h,COUNT(DISTINCT (@n:=@n+1)) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP", [ [ Some "1"; Some "1"; Some "1" ]; [ Some "1"; Some "2"; Some "1" ]; [ Some "1"; None; Some "2" ]; [ Some "2"; Some "1"; Some "2" ]; [ Some "2"; Some "2"; Some "1" ]; [ Some "2"; None; Some "3" ]; [ None; None; Some "5" ] ], "5"
                    "SELECT g,h,GROUP_CONCAT(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP", [ [ Some "1"; Some "1"; Some "1" ]; [ Some "1"; Some "2"; Some "2" ]; [ Some "1"; None; Some "1,2" ]; [ Some "2"; Some "1"; Some "3,4" ]; [ Some "2"; Some "2"; Some "5" ]; [ Some "2"; None; Some "3,4,5" ]; [ None; None; Some "1,2,3,4,5" ] ], "5"
                    "SELECT g,h,JSON_ARRAYAGG(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP", [ [ Some "1"; Some "1"; Some "[3]" ]; [ Some "1"; Some "2"; Some "[6]" ]; [ Some "1"; None; Some "[2, 5]" ]; [ Some "2"; Some "1"; Some "[9, 12]" ]; [ Some "2"; Some "2"; Some "[15]" ]; [ Some "2"; None; Some "[8, 11, 14]" ]; [ None; None; Some "[1, 4, 7, 10, 13]" ] ], "15"
                    "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input WHERE FALSE GROUP BY g,h WITH ROLLUP", [  ], "0"
                    "SELECT g,h,SUM(DISTINCT (@n:=@n+1)) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP", [ [ Some "1"; Some "1"; Some "3" ]; [ Some "1"; Some "2"; Some "6" ]; [ Some "1"; None; Some "7" ]; [ Some "2"; Some "1"; Some "21" ]; [ Some "2"; Some "2"; Some "15" ]; [ Some "2"; None; Some "33" ]; [ None; None; Some "35" ] ], "15"
                    "SELECT g,h,MIN(@n:=@n+1) AS lo,MAX(@n:=@n+1) AS hi FROM rollup_input GROUP BY g,h WITH ROLLUP", [ [ Some "1"; Some "1"; Some "3"; Some "6" ]; [ Some "1"; Some "2"; Some "9"; Some "12" ]; [ Some "1"; None; Some "2"; Some "11" ]; [ Some "2"; Some "1"; Some "15"; Some "24" ]; [ Some "2"; Some "2"; Some "27"; Some "30" ]; [ Some "2"; None; Some "14"; Some "29" ]; [ None; None; Some "1"; Some "28" ] ], "30"
                    "SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT (@n:=@n+1)) AS n FROM rollup_input GROUP BY g,h WITH ROLLUP", [ [ Some "1"; Some "1"; Some "3"; Some "1" ]; [ Some "1"; Some "2"; Some "7"; Some "1" ]; [ Some "1"; None; Some "8"; Some "2" ]; [ Some "2"; Some "1"; Some "26"; Some "2" ]; [ Some "2"; Some "2"; Some "19"; Some "1" ]; [ Some "2"; None; Some "42"; Some "3" ]; [ None; None; Some "45"; Some "5" ] ], "20"
                    "SELECT g,h,SUM(@n:=@n+1) AS s,GROUP_CONCAT(@n:=@n+1) AS c FROM rollup_input GROUP BY g,h WITH ROLLUP", [ [ Some "1"; Some "1"; Some "3"; Some "4" ]; [ Some "1"; Some "2"; Some "7"; Some "8" ]; [ Some "1"; None; Some "8"; Some "4,8" ]; [ Some "2"; Some "1"; Some "26"; Some "12,16" ]; [ Some "2"; Some "2"; Some "19"; Some "20" ]; [ Some "2"; None; Some "42"; Some "12,16,20" ]; [ None; None; Some "45"; Some "4,8,12,16,20" ] ], "20"
                    "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP ORDER BY g DESC,h DESC LIMIT 1", [ [ Some "2"; Some "2"; Some "3" ] ], "15"
                    "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP ORDER BY s DESC LIMIT 1", [ [ None; None; Some "35" ] ], "15"
                    "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 1 OFFSET 2", [ [ Some "1"; None; Some "7" ] ], "6"
                    "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP HAVING GROUPING(g,h)=3 LIMIT 1", [ [ None; None; Some "35" ] ], "15" ] do
                  execute "SET @n=0" |> ignore
                  match execute sql with
                  | ResultSet(_, rows) -> Expect.equal rows expected sql
                  | result -> failtestf "Expected ROLLUP rows, got %A" result
                  Expect.equal (execute "SELECT @n") (ResultSet([ "@n" ], [ [ Some counter ] ])) sql

          testCase "grouped projections restore returned root assignments"
          <| fun _ ->
              let connection = Fsdb.Db.create () |> Fsdb.Db.connect
              let execute sql = connection.Query sql
              execute "CREATE TABLE replay_input(v INT)" |> ignore
              execute "INSERT INTO replay_input VALUES(2),(1),(3)" |> ignore
              for sql, expected, variables in
                  [ "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)", [ [ Some "9"; Some "10"; Some "3" ]; [ Some "1"; Some "2"; Some "2" ]; [ Some "5"; Some "6"; Some "1" ] ], [ Some "6"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "9"; Some "10"; Some "3" ] ], [ Some "10"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1", [ [ Some "1"; Some "2"; Some "2" ] ], [ Some "2"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0", [  ], [ Some "0"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10", [  ], [ Some "12"; Some "0" ]
                    "SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)", [ [ Some "109"; Some "110"; Some "3" ]; [ Some "101"; Some "102"; Some "2" ]; [ Some "105"; Some "106"; Some "1" ] ], [ Some "12"; Some "0" ]
                    "SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "109"; Some "110"; Some "3" ] ], [ Some "12"; Some "0" ]
                    "SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1", [ [ Some "101"; Some "102"; Some "2" ] ], [ Some "12"; Some "0" ]
                    "SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0", [  ], [ Some "0"; Some "0" ]
                    "SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10", [  ], [ Some "12"; Some "0" ]
                    "SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)", [ [ Some "109"; Some "110"; Some "3" ]; [ Some "101"; Some "102"; Some "2" ]; [ Some "105"; Some "106"; Some "1" ] ], [ Some "106"; Some "12" ]
                    "SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "109"; Some "110"; Some "3" ] ], [ Some "110"; Some "12" ]
                    "SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1", [ [ Some "101"; Some "102"; Some "2" ] ], [ Some "102"; Some "12" ]
                    "SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0", [  ], [ Some "0"; Some "0" ]
                    "SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10", [  ], [ Some "112"; Some "12" ]
                    "SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)", [ [ Some "3"; Some "3"; Some "1" ]; [ Some "1"; Some "1"; Some "2" ]; [ Some "5"; Some "5"; Some "3" ] ], [ Some "5"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "3"; Some "3"; Some "1" ] ], [ Some "3"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1", [ [ Some "1"; Some "1"; Some "2" ] ], [ Some "1"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0", [  ], [ Some "0"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10", [  ], [ Some "6"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)", [ [ Some "3"; Some "3"; Some "1" ]; [ Some "1"; Some "1"; Some "2" ]; [ Some "5"; Some "5"; Some "3" ] ], [ Some "5"; Some "5" ]
                    "SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "3"; Some "3"; Some "1" ] ], [ Some "3"; Some "3" ]
                    "SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1", [ [ Some "1"; Some "1"; Some "2" ] ], [ Some "1"; Some "1" ]
                    "SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0", [  ], [ Some "0"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10", [  ], [ Some "6"; Some "6" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input ORDER BY IF(a=b,v,-v)", [ [ Some "9"; Some "10"; Some "3" ]; [ Some "1"; Some "2"; Some "2" ]; [ Some "5"; Some "6"; Some "1" ] ], [ Some "12"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY (@m:=v) DESC LIMIT 1", [ [ Some "5"; Some "6"; Some "3" ] ], [ Some "6"; Some "3" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1 ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "9"; Some "10"; Some "3" ] ], [ Some "10"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input WHERE v>1 GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "5"; Some "6"; Some "3" ] ], [ Some "6"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1", [ [ Some "1"; Some "2"; Some "2" ]; [ Some "5"; Some "6"; Some "3" ] ], [ Some "6"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1 ORDER BY v", [ [ Some "1"; Some "2"; Some "2" ]; [ Some "5"; Some "6"; Some "3" ] ], [ Some "6"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1 ORDER BY IF(a=b,v,-v)", [ [ Some "9"; Some "10"; Some "3" ]; [ Some "1"; Some "2"; Some "2" ] ], [ Some "2"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 2", [ [ Some "5"; Some "6"; Some "1" ] ], [ Some "6"; Some "0" ]
                    "SELECT SQL_CALC_FOUND_ROWS (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0", [  ], [ Some "12"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v,COUNT(DISTINCT v) AS c FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "9"; Some "10"; Some "3"; Some "1" ] ], [ Some "10"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v,COUNT(DISTINCT v) AS c FROM replay_input GROUP BY v HAVING v>1 ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "9"; Some "10"; Some "3"; Some "1" ] ], [ Some "10"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v,SUM(v) AS c FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "5"; Some "6"; Some "3"; Some "3" ] ], [ Some "6"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v,SUM(v) AS c FROM replay_input GROUP BY v HAVING v>1 ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "5"; Some "6"; Some "3"; Some "3" ] ], [ Some "6"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v WITH ROLLUP ORDER BY IF(a=b,v,-v) LIMIT 1", [ [ Some "13"; Some "14"; None ] ], [ Some "14"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING a>2 ORDER BY v", [ [ Some "3"; Some "4"; Some "1" ]; [ Some "5"; Some "6"; Some "3" ] ], [ Some "6"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,v FROM replay_input GROUP BY v HAVING FALSE ORDER BY a+0", [  ], [ Some "0"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,v FROM replay_input GROUP BY v HAVING 1=0 ORDER BY a+0", [  ], [ Some "0"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,v FROM replay_input GROUP BY v HAVING v<0 ORDER BY a+0", [  ], [ Some "6"; Some "0" ]
                    "SELECT (@n:=@n+1) AS a,v FROM replay_input GROUP BY v HAVING TRUE ORDER BY a+0", [ [ Some "1"; Some "2" ]; [ Some "3"; Some "1" ]; [ Some "5"; Some "3" ] ], [ Some "5"; Some "0" ] ] do
                  execute "SET @n=0,@m=0" |> ignore
                  match execute sql with
                  | ResultSet(_, rows) -> Expect.equal rows expected sql
                  | result -> failtestf "Expected grouped projection rows, got %A" result
                  Expect.equal (execute "SELECT @n,@m") (ResultSet([ "@n"; "@m" ], [ variables ])) sql

          testCase "window key bindings report their own clauses before execution"
          <| fun _ ->
              let connection = Fsdb.Db.create () |> Fsdb.Db.connect
              let execute sql = connection.Query sql
              execute "CREATE TABLE window_clause(v INT)" |> ignore
              execute "INSERT INTO window_clause VALUES(2),(1)" |> ignore
              for sql, message in
                  [ "SELECT v AS a FROM window_clause ORDER BY ROW_NUMBER() OVER (ORDER BY a)", "Unknown column 'a' in 'window order by'"
                    "SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause", "Unknown column 'missing' in 'window order by'"
                    "SELECT ROW_NUMBER() OVER (PARTITION BY missing) FROM window_clause", "Unknown column 'missing' in 'window partition by'"
                    "SELECT ROW_NUMBER() OVER w FROM window_clause WINDOW w AS (ORDER BY missing)", "Unknown column 'missing' in 'window order by'"
                    "SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause WHERE FALSE", "Unknown column 'missing' in 'window order by'"
                    "SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause LIMIT 0", "Unknown column 'missing' in 'window order by'"
                    "SELECT SUM(v) OVER (ORDER BY missing RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) FROM window_clause", "Unknown column 'missing' in 'window order by'"
                    "SELECT ROW_NUMBER() OVER (ORDER BY absent.v) FROM window_clause", "Unknown column 'absent.v' in 'window order by'"
                    "SELECT ROW_NUMBER() OVER (PARTITION BY absent.v) FROM window_clause", "Unknown column 'absent.v' in 'window partition by'" ] do
                  Expect.equal (execute sql) (Err(1054, message)) sql
                  let prepared = "PREPARE window_binding FROM '" + sql.Replace("'", "''") + "'"
                  Expect.equal (execute prepared) (Err(1054, message)) prepared

          testCase "window binding validates unused declarations and respects where precedence"
          <| fun _ ->
              let connection = Fsdb.Db.create () |> Fsdb.Db.connect
              let execute sql = connection.Query sql
              execute "CREATE TABLE window_clause(v INT)" |> ignore
              execute "INSERT INTO window_clause VALUES(2),(1)" |> ignore
              for sql, (code, message) in
                  [
                    "SELECT 1 FROM window_clause WINDOW w AS (ORDER BY missing)", (1054, "Unknown column 'missing' in 'window order by'")
                    "SELECT ROW_NUMBER() OVER (ORDER BY absent.v)", (1109, "Unknown table 'absent' in window order by")
                    "SELECT ROW_NUMBER() OVER (PARTITION BY absent.v)", (1109, "Unknown table 'absent' in window partition by")
                    "SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause WHERE absent=1", (1054, "Unknown column 'absent' in 'where clause'")
                    "SELECT v AS a,ROW_NUMBER() OVER (PARTITION BY a) FROM window_clause", (1054, "Unknown column 'a' in 'window partition by'")
                    "SELECT ROW_NUMBER() OVER child FROM window_clause WINDOW parent AS (ORDER BY missing),child AS (parent)", (1054, "Unknown column 'missing' in 'window order by'") ] do
                  Expect.equal (execute sql) (Err(code, message)) sql
                  let prepared = "PREPARE window_binding FROM '" + sql.Replace("'", "''") + "'"
                  Expect.equal (execute prepared) (Err(code, message)) prepared

          testCase "ordering aggregates cannot introduce an implicit group"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE aggregate_order(v INT)" |> ignore
              runDefault store "INSERT INTO aggregate_order VALUES(2),(1)" |> ignore
              let session = Fsdb.Session.create 1 store
              for sql, ordinal in
                  [ "SELECT v FROM aggregate_order ORDER BY SUM(v)", 1
                    "SELECT v AS a FROM aggregate_order ORDER BY SUM(a)", 1
                    "SELECT 1 ORDER BY SUM(1)", 1
                    "SELECT v FROM aggregate_order ORDER BY v,SUM(v)", 2
                    "SELECT v FROM aggregate_order WHERE FALSE ORDER BY SUM(v)", 1
                    "SELECT v FROM aggregate_order ORDER BY SUM(v) LIMIT 0", 1
                    "SELECT v FROM aggregate_order HAVING TRUE ORDER BY SUM(v)", 1
                    "SELECT ROW_NUMBER() OVER (ORDER BY v) FROM aggregate_order ORDER BY SUM(v)", 1
                    "SELECT v FROM aggregate_order ORDER BY (SELECT SUM(v))", 1
                    "SELECT v FROM aggregate_order ORDER BY (SELECT SUM((SELECT v)))", 1
                    "SELECT DISTINCT v FROM aggregate_order ORDER BY ABS(SUM(v))", 1 ] do
                  let message = sprintf "Expression #%d of ORDER BY contains aggregate function and applies to the result of a non-aggregated query" ordinal
                  Expect.equal (runDefault store sql) (Err(3029, message)) sql
                  match Fsdb.QueryHandler.prepareStatementForSession session sql with
                  | Error(code, actual) -> Expect.equal (code, actual) (3029, message) sql
                  | result -> failtestf "Expected preparation error 3029, got %A" result

              for sql, expected in
                  [ "SELECT v FROM aggregate_order ORDER BY missing,SUM(v)", 1054
                    "SELECT v FROM aggregate_order ORDER BY SUM(v),missing", 3029
                    "SELECT v FROM aggregate_order ORDER BY ABS(missing),SUM(v)", 1054
                    "SELECT (SELECT missing) FROM aggregate_order ORDER BY SUM(v)", 1054
                    "SELECT v FROM aggregate_order HAVING missing ORDER BY SUM(v)", 1054
                    "SELECT v AS a FROM aggregate_order ORDER BY a,SUM(v)", 3029 ] do
                  match runDefault store sql with
                  | Err(code, _) -> Expect.equal code expected sql
                  | result -> failtestf "Expected error %d, got %A" expected result
                  match Fsdb.QueryHandler.prepareStatementForSession session sql with
                  | Error(code, _) -> Expect.equal code expected sql
                  | result -> failtestf "Expected preparation error %d, got %A" expected result
              Fsdb.Storage.setOnlyFullGroupBy store false
              match runDefault store "SELECT v FROM aggregate_order ORDER BY SUM(v)" with
              | Err(code, _) -> Expect.equal code 3029 "Query classification does not depend on ONLY_FULL_GROUP_BY"
              | result -> failtestf "Expected error 3029, got %A" result

          testCase "aggregate arguments bind a scalar subquery total to the outer source"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE aggregate_order(v INT)" |> ignore
              runDefault store "INSERT INTO aggregate_order VALUES(2),(1)" |> ignore
              Expect.equal
                  (runDefault store "SELECT (SELECT SUM(v)) AS total FROM aggregate_order")
                  (ResultSet([ "total" ], [ [ Some "3" ] ]))
                  "Outer-owned aggregation produces one total, not a total per source row"
              Expect.equal
                  (runDefault store "SELECT v FROM aggregate_order ORDER BY (SELECT SUM(i.v+aggregate_order.v) FROM aggregate_order i)")
                  (ResultSet([ "v" ], [ [ Some "1" ]; [ Some "2" ] ]))
                  "An inner input keeps a mixed aggregate in the inner query"
              for sql, names, rows in
                  [ "SELECT (SELECT SUM(v)) AS total,ROW_NUMBER() OVER () AS rn FROM aggregate_order", [ "total"; "rn" ], [ [ Some "3"; Some "1" ] ]
                    "SELECT v,(SELECT SUM(v)) AS total,ROW_NUMBER() OVER (ORDER BY v) AS rn FROM aggregate_order GROUP BY v ORDER BY v", [ "v"; "total"; "rn" ], [ [ Some "1"; Some "1"; Some "1" ]; [ Some "2"; Some "2"; Some "2" ] ]
                    "SELECT (SELECT SUM(v)) AS total FROM aggregate_order WHERE FALSE", [ "total" ], [ [ None ] ]
                    "SELECT (SELECT SUM(v) WHERE FALSE) AS total FROM aggregate_order", [ "total" ], [ [ None ] ]
                    "SELECT (SELECT (SELECT SUM(v))) AS total FROM aggregate_order", [ "total" ], [ [ Some "3" ] ]
                    "SELECT (SELECT COUNT(v)) AS total FROM aggregate_order", [ "total" ], [ [ Some "2" ] ]
                    "SELECT v,(SELECT SUM(v)) AS total FROM aggregate_order GROUP BY v ORDER BY v", [ "v"; "total" ], [ [ Some "1"; Some "1" ]; [ Some "2"; Some "2" ] ] ] do
                  Expect.equal (runDefault store sql) (ResultSet(names, rows)) sql
              for sql in
                  [ "SELECT 3 IN (SELECT SUM(v)) AS hit FROM aggregate_order"
                    "SELECT 3=ANY(SELECT SUM(v)) AS hit FROM aggregate_order"
                    "SELECT EXISTS(SELECT SUM(v)) AS hit FROM aggregate_order" ] do
                  Expect.equal (runDefault store sql) (ResultSet([ "hit" ], [ [ Some "1" ] ])) sql
              match runDefault store "SELECT (SELECT SUM(aggregate_order.v) FROM aggregate_order i) AS total FROM aggregate_order" with
              | Err(code, _) -> Expect.equal code 1242 "A correlated aggregate does not remove the inner source rows"
              | result -> failtestf "Expected error 1242, got %A" result
              match runDefault store "SELECT (SELECT v) AS value,(SELECT SUM(v)) AS total FROM aggregate_order" with
              | Err(code, _) -> Expect.equal code 1140 "A correlated nonaggregate is still subject to ONLY_FULL_GROUP_BY"
              | result -> failtestf "Expected error 1140, got %A" result
              for sql in
                  [ "SELECT ANY_VALUE((SELECT v)) AS value,(SELECT SUM(v)) AS total FROM aggregate_order"
                    "SELECT (SELECT ANY_VALUE(v)) AS value,(SELECT SUM(v)) AS total FROM aggregate_order" ] do
                  Expect.equal (runDefault store sql) (ResultSet([ "value"; "total" ], [ [ Some "2"; Some "3" ] ])) sql
              let session = Fsdb.Session.create 1 store
              match Fsdb.QueryHandler.prepareStatementForSession session "SELECT (SELECT v) AS value,(SELECT SUM(v)) AS total FROM aggregate_order" with
              | Error(code, _) -> Expect.equal code 1140 "Preparation applies the enclosing grouping rule"
              | result -> failtestf "Expected preparation error 1140, got %A" result

          testCase "correlated EXISTS: WHERE EXISTS (... referencing the outer row) — the Eloquent whereHas() shape"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
              runDefault store "CREATE TABLE posts (id INT, user_id INT)" |> ignore
              runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
              // only alice has a post.
              runDefault store "INSERT INTO posts VALUES (1, 1)" |> ignore

              let sql = "SELECT name FROM users WHERE EXISTS (SELECT 1 FROM posts WHERE posts.user_id = users.id)"

              match runDefault store sql with
              | ResultSet([ "name" ], [ [ Some "alice" ] ]) -> ()
              | other -> failtestf "expected only alice (who has a post), got %A" other

          testCase "correlated NOT EXISTS"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
              runDefault store "CREATE TABLE posts (id INT, user_id INT)" |> ignore
              runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
              runDefault store "INSERT INTO posts VALUES (1, 1)" |> ignore

              let sql = "SELECT name FROM users WHERE NOT EXISTS (SELECT 1 FROM posts WHERE posts.user_id = users.id)"

              match runDefault store sql with
              | ResultSet([ "name" ], [ [ Some "bob" ] ]) -> ()
              | other -> failtestf "expected only bob (who has no post), got %A" other

          testCase "IN (SELECT ...): a non-correlated subquery's first column is the candidate set"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
              runDefault store "CREATE TABLE posts (user_id INT)" |> ignore
              runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
              runDefault store "INSERT INTO posts VALUES (1)" |> ignore

              match runDefault store "SELECT name FROM users WHERE id IN (SELECT user_id FROM posts)" with
              | ResultSet([ "name" ], [ [ Some "alice" ] ]) -> ()
              | other -> failtestf "expected only alice, got %A" other

          testCase "IN rejects a subquery that returns more than one column"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t (a INT, b INT)" |> ignore
              runDefault store "INSERT INTO t VALUES (1, 2)" |> ignore

              match runDefault store "SELECT 1 IN (SELECT a, b FROM t)" with
              | Err(1241, "Operand should contain 1 column(s)") -> ()
              | other -> failtestf "expected MySQL error 1241, got %A" other

          testCase "NOT IN (SELECT ...)"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
              runDefault store "CREATE TABLE posts (user_id INT)" |> ignore
              runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
              runDefault store "INSERT INTO posts VALUES (1)" |> ignore

              match runDefault store "SELECT name FROM users WHERE id NOT IN (SELECT user_id FROM posts)" with
              | ResultSet([ "name" ], [ [ Some "bob" ] ]) -> ()
              | other -> failtestf "expected only bob, got %A" other

          testCase "NULL NOT IN (SELECT ...) survives when the subquery has no rows"
          <| fun _ ->
              // `NULL IN (<empty set>)` is FALSE, not UNKNOWN, so
              // `NOT IN` against an empty subquery must be TRUE
              // regardless of `v`'s own NULL-ness — the subquery has
              // to run before any NULL short-circuit.
              let store = newStore ()
              runDefault store "CREATE TABLE t (id INT, v INT)" |> ignore
              runDefault store "CREATE TABLE empty_t (id INT)" |> ignore
              runDefault store "INSERT INTO t VALUES (1, NULL), (2, 5)" |> ignore

              match runDefault store "SELECT id FROM t WHERE v NOT IN (SELECT id FROM empty_t) ORDER BY id" with
              | ResultSet([ "id" ], rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "both rows survive against an empty candidate set"
              | other -> failtestf "expected both rows to survive NOT IN against an empty subquery, got %A" other

          testCase "quantified comparisons fold empty inputs and NULLs with MySQL's three-valued logic"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE empty_values (n INT)" |> ignore
              runDefault store "CREATE TABLE values_with_null (n INT)" |> ignore
              runDefault store "INSERT INTO values_with_null VALUES (NULL), (5)" |> ignore

              match
                  runDefault
                      store
                      "SELECT 5 = ANY (SELECT n FROM empty_values), 5 = ALL (SELECT n FROM empty_values), NULL = ANY (SELECT n FROM empty_values), NULL = ALL (SELECT n FROM empty_values)"
              with
              | ResultSet(_, [ [ Some "0"; Some "1"; Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected quantified empty-set identities, got %A" other

              match
                  runDefault
                      store
                      "SELECT 5 = SOME (SELECT n FROM values_with_null), 5 = ALL (SELECT n FROM values_with_null), 5 <> ANY (SELECT n FROM values_with_null), 5 <> ALL (SELECT n FROM values_with_null)"
              with
              | ResultSet(_, [ [ Some "1"; None; None; Some "0" ] ]) -> ()
              | other -> failtestf "expected quantified NULL propagation, got %A" other

          testCase "quantified comparisons preserve correlation, coercion, collation, and the one-column requirement"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE outer_rows (id INT)" |> ignore
              runDefault store "CREATE TABLE inner_rows (owner_id INT, value INT)" |> ignore
              runDefault store "INSERT INTO outer_rows VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO inner_rows VALUES (1, 1), (1, 2), (2, NULL), (2, 3)" |> ignore
              runDefault store "CREATE TABLE binary_text (s VARCHAR(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin)" |> ignore
              runDefault store "INSERT INTO binary_text VALUES ('A'), ('a')" |> ignore
              runDefault store "CREATE TABLE enum_text (s ENUM('red', 'blue'))" |> ignore
              runDefault store "INSERT INTO enum_text VALUES ('red')" |> ignore
              runDefault store "CREATE TABLE pairs (a INT, b INT)" |> ignore
              runDefault store "INSERT INTO pairs VALUES (1, 2)" |> ignore

              match
                  runDefault
                      store
                      "SELECT id, id = ANY (SELECT value FROM inner_rows WHERE owner_id = outer_rows.id), id < ALL (SELECT value FROM inner_rows WHERE owner_id = outer_rows.id) FROM outer_rows ORDER BY id"
              with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "1"; Some "0" ]; [ Some "2"; None; None ]; [ Some "3"; Some "0"; Some "1" ] ] "each outer row evaluates its own candidate set"
              | other -> failtestf "expected correlated quantified results, got %A" other

              match runDefault store "SELECT s, s = ANY (SELECT 'a') FROM binary_text ORDER BY s" with
              | ResultSet(_, [ [ Some "A"; Some "0" ]; [ Some "a"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the left column collation to govern equality, got %A" other

              match runDefault store "SELECT 'A' = ANY (SELECT s FROM binary_text WHERE s = 'a'), 'A' < ANY (SELECT s FROM binary_text WHERE s = 'a')" with
              | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected the subquery column collation to govern comparison, got %A" other

              match runDefault store "SELECT 'A' = ANY (SELECT s COLLATE utf8mb4_0900_ai_ci FROM binary_text WHERE s = 'a')" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected the projected COLLATE to override the subquery column collation, got %A" other

              match runDefault store "SELECT 1 = ANY (SELECT s FROM enum_text)" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected an ENUM subquery result to retain its ordinal comparison, got %A" other

              match runDefault store "SELECT 'A' = ANY (SELECT s FROM (SELECT s FROM binary_text WHERE s = 'a') AS derived_binary), 1 = ANY (SELECT s FROM (SELECT s FROM enum_text) AS derived_enum)" with
              | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected derived subquery columns to retain collation and ENUM metadata, got %A" other

              match runDefault store "SELECT 'A' = ANY (SELECT d FROM (SELECT s COLLATE utf8mb4_0900_ai_ci AS d FROM binary_text WHERE s = 'a') AS derived_collated)" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected a derived projected COLLATE to govern comparison, got %A" other

              runDefault store "CREATE VIEW binary_view AS SELECT s FROM binary_text WHERE s = 'a'" |> ignore
              runDefault store "CREATE VIEW enum_view AS SELECT s FROM enum_text" |> ignore
              runDefault store "CREATE VIEW collated_binary_view AS SELECT s COLLATE utf8mb4_0900_ai_ci AS d FROM binary_text WHERE s = 'a'" |> ignore

              match runDefault store "SELECT 'A' = ANY (SELECT s FROM binary_view), 1 = ANY (SELECT s FROM enum_view)" with
              | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected view subquery columns to retain collation and ENUM metadata, got %A" other

              match runDefault store "SELECT 'A' = ANY (SELECT d FROM collated_binary_view)" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected a view projected COLLATE to govern comparison, got %A" other

              match runDefault store "WITH binary_cte AS (SELECT s FROM binary_text WHERE s = 'a'), enum_cte AS (SELECT s FROM enum_text) SELECT 'A' = ANY (SELECT s FROM binary_cte), 1 = ANY (SELECT s FROM enum_cte)" with
              | ResultSet(_, [ [ Some "0"; Some "1" ] ]) -> ()
              | other -> failtestf "expected CTE subquery columns to retain collation and ENUM metadata, got %A" other

              match runDefault store "WITH collated_cte AS (SELECT s COLLATE utf8mb4_0900_ai_ci AS d FROM binary_text WHERE s = 'a') SELECT 'A' = ANY (SELECT d FROM collated_cte)" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected a CTE projected COLLATE to govern comparison, got %A" other

              match runDefault store "SELECT 2 = ANY (SELECT '2')" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected numeric coercion to match, got %A" other

              match runDefault store "SELECT 1 = ANY (SELECT a, b FROM pairs)" with
              | Err(1241, "Operand should contain 1 column(s)") -> ()
              | other -> failtestf "expected MySQL error 1241, got %A" other

              match runDefault store "CREATE TABLE generated_quantified (n INT, q INT GENERATED ALWAYS AS (n = ANY (SELECT n FROM outer_rows)))" with
              | Err(3102, "Expression of generated column 'q' contains a disallowed function.") -> ()
              | other -> failtestf "expected MySQL error 3102, got %A" other

          testCase "quantified comparisons support every ordered comparison operator"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE values_to_compare (n INT)" |> ignore
              runDefault store "INSERT INTO values_to_compare VALUES (1), (2), (3)" |> ignore

              match
                  runDefault
                      store
                      "SELECT 2 = ANY (SELECT n FROM values_to_compare), 2 <> ALL (SELECT n FROM values_to_compare), 2 < ANY (SELECT n FROM values_to_compare), 2 <= ALL (SELECT n FROM values_to_compare WHERE n >= 2), 2 > SOME (SELECT n FROM values_to_compare), 2 >= ALL (SELECT n FROM values_to_compare WHERE n <= 2)"
              with
              | ResultSet(_, [ [ Some "1"; Some "0"; Some "1"; Some "1"; Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected every quantified comparison operator to agree with MySQL, got %A" other

          testCase "typed equality quantifiers preserve exact values and NULL dominance"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE typed_values (i BIGINT, d DECIMAL(30,10), s VARCHAR(10) COLLATE utf8mb4_bin)" |> ignore

              runDefault
                  store
                  "INSERT INTO typed_values VALUES (9007199254740992, 1.0000000001, 'A'), (9007199254740993, 1.0000000002, 'a'), (NULL, NULL, NULL)"
              |> ignore

              match
                  runDefault
                      store
                      ("SELECT 9007199254740992 = ALL (SELECT i FROM typed_values), "
                       + "9007199254740992 <> ANY (SELECT i FROM typed_values), "
                       + "9007199254740992 <> ALL (SELECT i FROM typed_values), "
                       + "CAST(1.0000000001 AS DECIMAL(30,10)) = ALL (SELECT d FROM typed_values WHERE d IS NOT NULL), "
                       + "'A' <> ANY (SELECT s FROM typed_values), "
                       + "'A' = ALL (SELECT s FROM typed_values WHERE s = 'A')")
              with
              | ResultSet(_, [ [ Some "0"; Some "1"; Some "0"; Some "0"; Some "1"; Some "1" ] ]) -> ()
              | other -> failtestf "expected typed quantified equality semantics, got %A" other

          testCase "scalar subquery: (SELECT ...) used as a value, zero rows is NULL, one row is that value"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE users (id INT, name VARCHAR(10))" |> ignore
              runDefault store "CREATE TABLE posts (id INT, user_id INT)" |> ignore
              runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
              runDefault store "INSERT INTO posts VALUES (1, 1), (2, 1)" |> ignore

              let sql = "SELECT name, (SELECT COUNT(*) FROM posts WHERE posts.user_id = users.id) AS n FROM users ORDER BY name"

              match runDefault store sql with
              | ResultSet([ "name"; "n" ], rows) ->
                  Expect.equal rows [ [ Some "alice"; Some "2" ]; [ Some "bob"; Some "0" ] ] "correlated scalar subquery per row"
              | other -> failtestf "expected a per-row correlated count, got %A" other

          testCase "correlated COUNT rechecks residual predicates and empty inputs"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE users (id INT PRIMARY KEY)" |> ignore
              runDefault store "CREATE TABLE posts (id INT PRIMARY KEY, user_id INT, KEY ix_user (user_id))" |> ignore
              runDefault store "INSERT INTO users VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO posts VALUES (1, 1), (2, 1), (3, 2)" |> ignore

              match
                  runDefault
                      store
                      "SELECT u.id, (SELECT COUNT(*) FROM posts p WHERE p.user_id = u.id AND p.id >= 2) FROM users u ORDER BY u.id"
              with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "1" ]; [ Some "2"; Some "1" ]; [ Some "3"; Some "0" ] ] "the complete predicate determines each count"
              | other -> failtestf "expected correlated counts, got %A" other

              match
                  runDefault
                      store
                      "SELECT (SELECT COUNT(*) FROM posts p WHERE p.user_id = u.id AND missing = 1) FROM users u WHERE u.id = 3"
              with
              | Err(1054, _) -> ()
              | other -> failtestf "expected an unknown-column error for an empty correlated input, got %A" other

          testCase "correlated materialized lookups preserve binary collation and NULL equality"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE outer_labels (id INT, label VARCHAR(10) COLLATE utf8mb4_bin)" |> ignore
              runDefault store "CREATE TABLE inner_labels (label VARCHAR(10) COLLATE utf8mb4_bin, KEY ix_label (label))" |> ignore
              runDefault store "INSERT INTO outer_labels VALUES (1, 'A'), (2, 'a'), (3, NULL)" |> ignore
              runDefault store "INSERT INTO inner_labels VALUES ('a'), (NULL)" |> ignore

              let expected = [ [ Some "1"; Some "0" ]; [ Some "2"; Some "1" ]; [ Some "3"; Some "0" ] ]

              let assertRows sql =
                  match runDefault store sql with
                  | ResultSet(_, rows) -> Expect.equal rows expected sql
                  | other -> failtestf "expected correlated materialized results, got %A" other

              assertRows
                  "SELECT o.id, EXISTS (SELECT 1 FROM (SELECT label AS candidate FROM inner_labels) d WHERE d.candidate = o.label) FROM outer_labels o ORDER BY o.id"

              assertRows
                  "WITH labels AS (SELECT label FROM inner_labels) SELECT o.id, EXISTS (SELECT 1 FROM labels d WHERE d.label = o.label) FROM outer_labels o ORDER BY o.id"

              assertRows
                  ("SELECT o.id, EXISTS (SELECT 1 FROM "
                   + "(SELECT candidate AS nested_candidate FROM "
                   + "(SELECT label AS candidate FROM inner_labels WHERE label IS NOT NULL) first) d "
                   + "WHERE d.nested_candidate = o.label) FROM outer_labels o ORDER BY o.id")

              assertRows
                  ("WITH first(candidate) AS (SELECT label FROM inner_labels WHERE label IS NOT NULL), "
                   + "labels(label) AS (SELECT candidate FROM first) "
                   + "SELECT o.id, EXISTS (SELECT 1 FROM labels d WHERE d.label = o.label) FROM outer_labels o ORDER BY o.id")

              match
                  runDefault
                      store
                      "SELECT o.id, (WITH labels AS (SELECT o.label AS label) SELECT COUNT(*) FROM labels d WHERE d.label = o.label) FROM outer_labels o ORDER BY o.id"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ Some "1"; Some "1" ]; [ Some "2"; Some "1" ]; [ Some "3"; Some "0" ] ]
                      "an outer-dependent CTE is rebuilt for each outer row"
              | other -> failtestf "expected correlated CTE results, got %A" other

          testCase "scalar subquery returning more than one row is MySQL error 1242"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t (n INT)" |> ignore
              runDefault store "INSERT INTO t VALUES (1), (2)" |> ignore

              match runDefault store "SELECT (SELECT n FROM t) AS x" with
              | Err(1242, _) -> ()
              | other -> failtestf "expected error 1242, got %A" other

          testCase "statement-stable IN and scalar subqueries run once across outer rows"
          <| fun _ ->
              let mutable touches = 0

              let touch (values: Value list) : Value =
                  touches <- touches + 1
                  List.head values

              let registry = registerScalar "TOUCH" touch builtins
              let store = newStore ()
              runDefault store "CREATE TABLE outer_rows (id INT)" |> ignore
              runDefault store "CREATE TABLE inner_rows (id INT)" |> ignore
              runDefault store "INSERT INTO outer_rows VALUES (1), (2), (3)" |> ignore
              runDefault store "INSERT INTO inner_rows VALUES (1), (2)" |> ignore

              touches <- 0

              match run store registry "SELECT id FROM outer_rows WHERE id IN (SELECT TOUCH(id) FROM inner_rows) ORDER BY id" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "IN retains matching rows"
              | other -> failtestf "expected a resultset, got %A" other

              let inTouches = touches
              Expect.isLessThan inTouches 5 "the IN subquery is evaluated once, not once per outer row"

              touches <- 0

              match run store registry "SELECT id, (SELECT TOUCH(7)) AS probe FROM outer_rows ORDER BY id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "7" ]; [ Some "2"; Some "7" ]; [ Some "3"; Some "7" ] ] "scalar result is shared"
              | other -> failtestf "expected a resultset, got %A" other

              let scalarTouches = touches
              Expect.isLessThan scalarTouches 3 "the scalar subquery is evaluated once, not once per outer row"

              touches <- 0

              match run store registry "SELECT id, (SELECT TOUCH(outer_rows.id)) AS probe FROM outer_rows ORDER BY id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "1" ]; [ Some "2"; Some "2" ]; [ Some "3"; Some "3" ] ] "correlation stays per-row"
              | other -> failtestf "expected a resultset, got %A" other

              Expect.isGreaterThan touches scalarTouches "a correlated scalar subquery remains per-row"

              touches <- 0

              match run store registry "SELECT id FROM outer_rows WHERE id = ANY (SELECT TOUCH(id) FROM inner_rows) ORDER BY id" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "ANY retains matching rows"
              | other -> failtestf "expected a resultset, got %A" other

              let anyTouches = touches
              Expect.isLessThan anyTouches 5 "an uncorrelated ANY subquery runs once"

              touches <- 0

              match run store registry "SELECT id FROM outer_rows WHERE id <= ALL (SELECT TOUCH(id) FROM inner_rows) ORDER BY id" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ] ] "ALL retains only the universal match"
              | other -> failtestf "expected a resultset, got %A" other

              let allTouches = touches
              Expect.isLessThan allTouches 5 "an uncorrelated ALL subquery runs once"

              touches <- 0

              match
                  run
                      store
                      registry
                      "SELECT id FROM outer_rows WHERE id = ANY (SELECT TOUCH(id) FROM inner_rows WHERE outer_rows.id >= 0) ORDER BY id"
              with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "2" ] ] "the correlated comparison still matches"
              | other -> failtestf "expected a resultset, got %A" other

              Expect.isGreaterThan touches allTouches "a correlated quantified subquery runs for every outer row"

          testCase "memoized integer IN preserves NULL and empty-set semantics"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE outer_rows (id INT)" |> ignore
              runDefault store "CREATE TABLE inner_rows (id INT)" |> ignore
              runDefault store "INSERT INTO outer_rows VALUES (1), (2), (NULL)" |> ignore
              runDefault store "INSERT INTO inner_rows VALUES (2), (NULL)" |> ignore

              match
                  runDefault
                      store
                      "SELECT id, id IN (SELECT id FROM inner_rows), id IN (SELECT id FROM inner_rows WHERE FALSE) FROM outer_rows ORDER BY id"
              with
              | ResultSet(_, rows) ->
                  Expect.equal
                      rows
                      [ [ None; None; Some "0" ]; [ Some "1"; None; Some "0" ]; [ Some "2"; Some "1"; Some "0" ] ]
                      "memoized membership retains three-valued IN behavior"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "memoized string and decimal IN preserves collation and NULL semantics"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE ci_values (v VARCHAR(20) COLLATE utf8mb4_0900_ai_ci)" |> ignore
              runDefault store "CREATE TABLE bin_values (v VARCHAR(20) COLLATE utf8mb4_bin)" |> ignore
              runDefault store "CREATE TABLE decimals (v DECIMAL(10,2))" |> ignore
              runDefault store "INSERT INTO ci_values VALUES ('alpha'), (NULL)" |> ignore
              runDefault store "INSERT INTO bin_values VALUES ('alpha'), (NULL)" |> ignore
              runDefault store "INSERT INTO decimals VALUES (1.20), (NULL)" |> ignore

              match
                  runDefault
                      store
                      "SELECT 'ALPHA' IN (SELECT v FROM ci_values), 'ALPHA' IN (SELECT v FROM bin_values), 1.2 IN (SELECT v FROM decimals), 2.0 IN (SELECT v FROM decimals)"
              with
              | ResultSet(_, [ row ]) ->
                  Expect.equal row [ Some "1"; None; Some "1"; None ] "typed membership follows comparison semantics"
              | other -> failtestf "expected one typed-membership row, got %A" other

          testCase "stable integer IN narrows an indexed outer table"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE outer_rows (id INT PRIMARY KEY, label VARCHAR(10))" |> ignore
              runDefault store "CREATE TABLE inner_rows (id INT)" |> ignore
              runDefault store "INSERT INTO outer_rows VALUES (1, 'one'), (2, 'two'), (3, 'three')" |> ignore
              runDefault store "INSERT INTO inner_rows VALUES (3), (1), (1), (NULL)" |> ignore

              match runDefault store "SELECT id, label FROM outer_rows WHERE id IN (SELECT id FROM inner_rows) ORDER BY id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "one" ]; [ Some "3"; Some "three" ] ] "outer index candidates retain set semantics"
              | other -> failtestf "expected a resultset, got %A" other

          testCase "duplicate IN-subquery keys do not multiply non-unique index candidates"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE duplicate_outer (id INT PRIMARY KEY, k INT, INDEX ix_k (k))" |> ignore
              runDefault store "CREATE TABLE duplicate_inner (k INT)" |> ignore

              [ 1..200 ]
              |> List.map (fun id -> sprintf "(%d,1)" id)
              |> String.concat ","
              |> sprintf "INSERT INTO duplicate_outer VALUES %s"
              |> runDefault store
              |> ignore

              String.replicate 199 "(1)," + "(1)"
              |> sprintf "INSERT INTO duplicate_inner VALUES %s"
              |> runDefault store
              |> ignore

              match runDefault store "SELECT COUNT(*) FROM duplicate_outer WHERE k IN (SELECT k FROM duplicate_inner)" with
              | ResultSet(_, [ [ Some "200" ] ]) -> ()
              | other -> failtestf "expected each outer row once, got %A" other

          testCase "stable string and decimal IN subqueries narrow indexed outer rows"
          <| fun _ ->
              let mutable touches = 0

              let touch values =
                  touches <- touches + 1
                  List.head values

              let registry = registerScalar "TOUCH" touch builtins
              let store = newStore ()

              runDefault
                  store
                  "CREATE TABLE outer_rows (id INT PRIMARY KEY, code VARCHAR(20) COLLATE utf8mb4_bin, amount DECIMAL(10,2), INDEX ix_code (code), INDEX ix_amount (amount))"
              |> ignore

              runDefault
                  store
                  "CREATE TABLE inner_rows (code VARCHAR(20) COLLATE utf8mb4_bin, amount DECIMAL(10,2))"
              |> ignore

              [ 1 .. 100 ]
              |> List.map (fun id -> sprintf "(%d, 'code-%03d', %d.25)" id id id)
              |> String.concat ", "
              |> sprintf "INSERT INTO outer_rows VALUES %s"
              |> runDefault store
              |> ignore

              runDefault store "INSERT INTO inner_rows VALUES ('code-007', 81.25), ('code-081', 7.25), (NULL, NULL)" |> ignore

              match
                  run
                      store
                      registry
                      "SELECT id FROM outer_rows WHERE code IN (SELECT code FROM inner_rows) AND TOUCH(id) = id ORDER BY id"
              with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "7" ]; [ Some "81" ] ] "string candidates match"
              | other -> failtestf "expected indexed string matches, got %A" other

              Expect.isLessThan touches 5 "the string index avoids evaluating every outer row"
              touches <- 0

              match
                  run
                      store
                      registry
                      "SELECT id FROM outer_rows WHERE code = ANY (SELECT code FROM inner_rows) AND TOUCH(id) = id ORDER BY id"
              with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "7" ]; [ Some "81" ] ] "ANY candidates match"
              | other -> failtestf "expected indexed ANY matches, got %A" other

              Expect.isLessThan touches 5 "ANY shares the string index probe"
              touches <- 0

              match
                  run
                      store
                      registry
                      "SELECT id FROM outer_rows WHERE amount IN (SELECT amount FROM inner_rows) AND TOUCH(id) = id ORDER BY id"
              with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "7" ]; [ Some "81" ] ] "decimal candidates match"
              | other -> failtestf "expected indexed decimal matches, got %A" other

              Expect.isLessThan touches 5 "the decimal index avoids evaluating every outer row"

          testCase "indexed string IN rechecks prefix collisions"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE outer_rows (id INT PRIMARY KEY, code VARCHAR(20), INDEX ix_code (code(2)))" |> ignore
              runDefault store "CREATE TABLE inner_rows (code VARCHAR(20))" |> ignore
              runDefault store "INSERT INTO outer_rows VALUES (1, 'ab-target'), (2, 'ab-other'), (3, 'different')" |> ignore
              runDefault store "INSERT INTO inner_rows VALUES ('ab-target')" |> ignore

              match runDefault store "SELECT id FROM outer_rows WHERE code IN (SELECT code FROM inner_rows) ORDER BY id" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ] ] "the full IN predicate filters prefix candidates"
              | other -> failtestf "expected only the full string match, got %A" other

          testCase "stable row IN subqueries narrow composite indexes"
          <| fun _ ->
              let mutable touches = 0

              let touch values =
                  touches <- touches + 1
                  List.head values

              let registry = registerScalar "TOUCH" touch builtins
              let store = newStore ()

              runDefault
                  store
                  "CREATE TABLE outer_rows (id INT PRIMARY KEY, tenant_id INT, status VARCHAR(20), INDEX ix_tenant_status (tenant_id, status))"
              |> ignore

              runDefault store "CREATE TABLE inner_rows (tenant_id INT, status VARCHAR(20))" |> ignore

              [ 1 .. 100 ]
              |> List.map (fun id -> sprintf "(%d, %d, 'status-%03d')" id (id % 5) id)
              |> String.concat ", "
              |> sprintf "INSERT INTO outer_rows VALUES %s"
              |> runDefault store
              |> ignore

              runDefault
                  store
                  "INSERT INTO inner_rows VALUES (2, 'status-007'), (1, 'status-081'), (2, 'status-007'), (NULL, 'status-010'), (0, NULL)"
              |> ignore

              match
                  run
                      store
                      registry
                      "SELECT id FROM outer_rows WHERE (status, tenant_id) IN (SELECT status, tenant_id FROM inner_rows) AND TOUCH(id) = id ORDER BY id"
              with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "7" ]; [ Some "81" ] ] "composite candidates match"
              | other -> failtestf "expected indexed composite matches, got %A" other

              Expect.isLessThan touches 5 "the composite index avoids evaluating every outer row"

          testCase "EXISTS stops after its first matching row"
          <| fun _ ->
              let mutable touches = 0

              let touch (values: Value list) : Value =
                  touches <- touches + 1
                  List.head values

              let registry = registerScalar "TOUCH" touch builtins
              let store = newStore ()
              runDefault store "CREATE TABLE inner_rows (id INT)" |> ignore
              let values = [ for id in 1 .. 100 -> sprintf "(%d)" id ] |> String.concat ", "
              runDefault store (sprintf "INSERT INTO inner_rows VALUES %s" values) |> ignore

              match run store registry "SELECT EXISTS (SELECT 1 FROM inner_rows WHERE TOUCH(id) > 0) AS present" with
              | ResultSet(_, [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected exists=1, got %A" other

              Expect.isLessThan touches 5 "EXISTS stops after its first matching row"

          testCase "derived table: FROM (SELECT ...) AS t"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t (n INT)" |> ignore
              runDefault store "INSERT INTO t VALUES (1), (2), (3)" |> ignore

              match runDefault store "SELECT doubled FROM (SELECT n * 2 AS doubled FROM t) AS d WHERE doubled > 2 ORDER BY doubled" with
              | ResultSet([ "doubled" ], rows) -> Expect.equal rows [ [ Some "4" ]; [ Some "6" ] ] "derived table filtered and projected"
              | other -> failtestf "expected a derived-table resultset, got %A" other

          testCase "a derived table's columns compare numerically, not as re-wrapped text"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE nums (n INT)" |> ignore
              runDefault store "INSERT INTO nums VALUES (2), (10), (9)" |> ignore

              match runDefault store "SELECT MAX(y.n) AS m FROM (SELECT n FROM nums) y" with
              | ResultSet([ "m" ], [ [ Some "10" ] ]) -> ()
              | other -> failtestf "expected MAX to compare numerically (10), got %A" other

              match runDefault store "SELECT y.n FROM (SELECT n FROM nums) y ORDER BY y.n" with
              | ResultSet([ "n" ], rows) ->
                  Expect.equal rows [ [ Some "2" ]; [ Some "9" ]; [ Some "10" ] ] "ORDER BY sorts numerically, not lexicographically"
              | other -> failtestf "expected a numerically-sorted resultset, got %A" other

          testCase "LEFT JOIN (SELECT ...) AS t ON ... — a derived table as a JOIN target, not just the leading FROM"
          <| fun _ ->
              // Eloquent's leftJoinSub/joinSub send exactly this shape.
              let store = newStore ()
              runDefault store "CREATE TABLE users (id INT, name VARCHAR(20))" |> ignore
              runDefault store "CREATE TABLE orders (user_id INT, total INT)" |> ignore
              runDefault store "INSERT INTO users VALUES (1, 'alice'), (2, 'bob')" |> ignore
              runDefault store "INSERT INTO orders VALUES (1, 10), (1, 5)" |> ignore

              match
                  runDefault
                      store
                      "SELECT users.name, o.total_spent FROM users LEFT JOIN (SELECT user_id, SUM(total) AS total_spent FROM orders GROUP BY user_id) AS o ON users.id = o.user_id ORDER BY users.id"
              with
              | ResultSet([ "name"; "total_spent" ], rows) ->
                  Expect.equal rows [ [ Some "alice"; Some "15" ]; [ Some "bob"; None ] ] "bob has no orders, padded with NULL"
              | other -> failtestf "expected a joined-against-subquery resultset, got %A" other

          testCase "derived joins filter multi-table UPDATE and DELETE targets"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE t1 (id INT, n INT)" |> ignore
              runDefault store "CREATE TABLE source (id INT, n INT)" |> ignore
              runDefault store "INSERT INTO t1 VALUES (1, 10), (2, 20), (3, 30)" |> ignore
              runDefault store "INSERT INTO source VALUES (1, 5), (2, 0), (3, 7)" |> ignore

              match runDefault store "UPDATE t1 JOIN (SELECT id, n FROM source WHERE n > 0) dt ON t1.id = dt.id SET t1.n = dt.n" with
              | Affected 2UL -> ()
              | other -> failtestf "expected two updated rows, got %A" other

              match runDefault store "SELECT id, n FROM t1 ORDER BY id" with
              | ResultSet(_, rows) ->
                  Expect.equal rows [ [ Some "1"; Some "5" ]; [ Some "2"; Some "20" ]; [ Some "3"; Some "7" ] ] "the derived rows supply update values"
              | other -> failtestf "expected updated rows, got %A" other

              match runDefault store "DELETE t1 FROM t1 JOIN (SELECT id FROM source WHERE n = 0) dt ON t1.id = dt.id" with
              | Affected 1UL -> ()
              | other -> failtestf "expected one deleted row, got %A" other

              match runDefault store "SELECT id FROM t1 ORDER BY id" with
              | ResultSet(_, rows) -> Expect.equal rows [ [ Some "1" ]; [ Some "3" ] ] "only the physical target is deleted"
              | other -> failtestf "expected surviving rows, got %A" other

              match runDefault store "UPDATE t1 JOIN (SELECT id FROM source) dt ON t1.id = dt.id SET dt.id = 9" with
              | Err(1288, _) -> ()
              | other -> failtestf "expected a non-updatable derived target error, got %A" other

              match runDefault store "EXPLAIN UPDATE t1 JOIN (SELECT id FROM source) dt ON t1.id = dt.id SET t1.n = 1" with
              | ResultSet _ -> ()
              | other -> failtestf "expected a derived UPDATE plan, got %A" other

          testCase "a scalar subquery's comparison is numeric, not lexicographic text"
          <| fun _ ->
              let store = newStore ()
              runDefault store "CREATE TABLE nums (n INT)" |> ignore
              runDefault store "INSERT INTO nums VALUES (2), (10), (9)" |> ignore

              match runDefault store "SELECT (SELECT MAX(n) FROM nums) > (SELECT MIN(n) FROM nums) AS r" with
              | ResultSet([ "r" ], [ [ Some "1" ] ]) -> ()
              | other -> failtestf "expected 10 > 2 to be true, got %A" other ]
