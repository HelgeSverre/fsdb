open System
open System.Diagnostics
open Fsdb

let collation = Collation.tryFind "utf8mb4_general_ci" |> Option.get
let values =
    [| "ordinary ascii text"; "生日快乐日本語"; "カタカナﾊﾟ"; "中①文"
       "中㎞文"; "中〒文"; "e\u0301日"; "生\u200d日" |]

let run () =
    let mutable checksum = 0
    for _ in 1 .. 30000 do
        for value in values do
            checksum <- checksum + (collation.KeyOf value).Length
    checksum

run () |> ignore
GC.Collect()
GC.WaitForPendingFinalizers()
GC.Collect()
let before = GC.GetAllocatedBytesForCurrentThread()
let timer = Stopwatch.StartNew()
let checksum = run ()
timer.Stop()
let allocated = GC.GetAllocatedBytesForCurrentThread() - before
printfn "elapsed_ms=%d allocated_bytes=%d checksum=%d" timer.ElapsedMilliseconds allocated checksum
