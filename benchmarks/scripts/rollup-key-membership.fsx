#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"
open System
open System.Diagnostics
open Fsdb.Ast
let expression = Col "v"
let measure label contains =
    for _ in 1 .. 1000 do contains [] expression |> ignore
    let before = GC.GetAllocatedBytesForCurrentThread()
    let timer = Stopwatch.StartNew()
    let mutable matches = 0
    for _ in 1 .. 100000 do
        if contains [] expression then matches <- matches + 1
    timer.Stop()
    printfn "%s matches=%d bytes=%d ms=%.3f" label matches (GC.GetAllocatedBytesForCurrentThread()-before) timer.Elapsed.TotalMilliseconds
measure "contains" (fun keys expression -> List.contains expression keys)
measure "nonempty-contains" (fun keys expression -> not (List.isEmpty keys) && List.contains expression keys)
