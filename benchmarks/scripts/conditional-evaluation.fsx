#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"
open System
open System.Diagnostics
open Fsdb
open Fsdb.Executor
let session =
    let initial = Session.create 1 (Storage.create())
    let created, _ = QueryHandler.handle initial "CREATE TABLE t(n INT)"
    QueryHandler.handle created "INSERT INTO t VALUES(1)" |> fst
let cases =
    [ "plain", "SELECT 1 AS n", true
      "signed-cast", "SELECT CAST('1x' AS SIGNED) AS n", true
      "unsigned-cast", "SELECT CAST('1.9' AS UNSIGNED) AS n", true
      "if", "SELECT IF(n,1,CAST('x' AS SIGNED)) AS n FROM t", true
      "ifnull", "SELECT IFNULL(n,CAST('x' AS SIGNED)) AS n FROM t", true
      "coalesce", "SELECT COALESCE(NULL,n,CAST('x' AS SIGNED)) AS n FROM t", true
      "nested", "SELECT IFNULL((SELECT 1),CAST('x' AS SIGNED)) AS n", true ]
printfn "case,trial,total_ms,allocated_bytes"
for name, sql, hasRows in cases do
    for _ in 1..200 do QueryHandler.handle session sql |> ignore
    for trial in 1..5 do
        GC.Collect()
        let allocated = GC.GetAllocatedBytesForCurrentThread()
        let clock = Stopwatch.StartNew()
        for _ in 1..2000 do
            let _, result = QueryHandler.handle session sql
            match result with
            | ResultSet(_, rows) when rows = (if hasRows then [[Some "1"]] else []) -> ()
            | other -> failwithf "%A" other
        clock.Stop()
        printfn "%s,%d,%.3f,%d" name trial clock.Elapsed.TotalMilliseconds (GC.GetAllocatedBytesForCurrentThread() - allocated)
