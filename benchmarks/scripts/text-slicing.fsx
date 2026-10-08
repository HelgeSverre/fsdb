#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"
open System
open System.Diagnostics
open Fsdb
open Fsdb.Value
for size in [1024; 1000000] do
    let input = VString(String('a', size))
    for name, args in ["LEFT", [input; VInt 1L]; "SUBSTRING", [input; VInt 2L; VInt 1L]; "RIGHT", [input; VInt 1L]] do
        let fn = Functions.lookup name Functions.builtins |> Option.get
        for _ in 1..10 do fn args |> GC.KeepAlive
        let samples =
            [| for _ in 1..5 do
                let allocated = GC.GetAllocatedBytesForCurrentThread()
                let timer = Stopwatch.StartNew()
                for _ in 1..100 do fn args |> GC.KeepAlive
                timer.Stop()
                yield timer.Elapsed.TotalMilliseconds, float (GC.GetAllocatedBytesForCurrentThread() - allocated) / 100. |]
        let ms = samples |> Array.map fst |> Array.sort |> fun values -> values.[2]
        printfn "%s size=%d median_ms_per_100=%.4f bytes_per_call=%.0f" name size ms (snd samples.[4])
