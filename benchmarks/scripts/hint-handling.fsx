#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"
open System
open System.Diagnostics
open Fsdb
open Fsdb.Executor
let session = Session.create 1 (Storage.create())
let cases =
    [ "plain", "SELECT 1 AS n"
      "timeout", "SELECT /*+ MAX_EXECUTION_TIME(10000) */ 1 AS n"
      "combined", "SELECT /*+ MAX_EXECUTION_TIME(10000) SET_VAR(max_points_in_geometry=1000) */ 1 AS n" ]
for name, sql in cases do
    for _ in 1..100 do QueryHandler.handle session sql |> ignore
    for trial in 1..5 do
        GC.Collect()
        let allocated = GC.GetAllocatedBytesForCurrentThread()
        let clock = Stopwatch.StartNew()
        for _ in 1..2000 do
            let _, result = QueryHandler.handle session sql
            match result with
            | ResultSet(_, [[Some "1"]]) -> ()
            | other -> failwithf "%A" other
        clock.Stop()
        printfn "%s,%d,%.3f,%d" name trial clock.Elapsed.TotalMilliseconds (GC.GetAllocatedBytesForCurrentThread() - allocated)
