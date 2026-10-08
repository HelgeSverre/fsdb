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
      "timeout", "SELECT /*+ MAX_EXECUTION_TIME(10000) */ 1 AS n", true
      "combined", "SELECT /*+ MAX_EXECUTION_TIME(10000) SET_VAR(max_points_in_geometry=1000) */ 1 AS n", true
      "join-warning", "SELECT /*+ JOIN_ORDER(x) */ n FROM t", true
      "join-eliminated", "SELECT /*+ JOIN_ORDER(x) */ n FROM t WHERE 0", false ]
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
