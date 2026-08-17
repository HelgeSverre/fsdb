// Phase 0 spike: isolate fsdb's ~5-7 ms/commit durable-write cost.
// Tests the four file-handling strategies on this box (APFS) so the WAL
// redesign optimizes the thing that actually matters.
//
//   A  growing file, open+write+fsync+close per op   (today's Persistence.attach)
//   B  preallocated file, reused handle, write+fsync  (target)
//   C  preallocated, write only, no fsync             (floor)
//   D  preallocated, but still open+write+fsync+close (isolates open/close churn)
open System
open System.Diagnostics
open System.IO

let tempDir = Path.GetTempPath()
let path = Path.Combine(tempDir, "fsync-bench-" + Guid.NewGuid().ToString("N") + ".bin")
let payload = Array.init 200 (fun _ -> 0xABuy)

let bench (name: string) (op: unit -> unit) (iters: int) =
    for _ in 1..200 do
        op ()

    let sw = Stopwatch.StartNew()
    for _ in 1..iters do
        op ()

    sw.Stop()
    let usPerOp = float sw.Elapsed.TotalMilliseconds * 1000.0 / float iters
    printfn "%-42s %10.1f us/op" name usPerOp

// A: growing file, open+write+fsync+close per op.
let testA () =
    use s = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
    s.Write(payload, 0, payload.Length)
    s.Flush true

bench "A growing: open+write+fsync+close" testA 500
File.Delete path

// B: preallocated, reused handle, write+fsync per op.
let bStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)
bStream.SetLength(64L * 1024L * 1024L)
let mutable bOffset = 0

let testB () =
    bStream.Seek(int64 bOffset, SeekOrigin.Begin) |> ignore
    bStream.Write(payload, 0, payload.Length)
    bStream.Flush true
    bOffset <- (bOffset + payload.Length) % (64 * 1024 * 1024)

bench "B prealloc: reuse handle, write+fsync" testB 500
bStream.Dispose()
File.Delete path

// C: preallocated, write only, no fsync.
let cStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)
cStream.SetLength(64L * 1024L * 1024L)
let mutable cOffset = 0

let testC () =
    cStream.Seek(int64 cOffset, SeekOrigin.Begin) |> ignore
    cStream.Write(payload, 0, payload.Length)
    cOffset <- (cOffset + payload.Length) % (64 * 1024 * 1024)

bench "C prealloc: write only (no fsync)" testC 500
cStream.Dispose()
File.Delete path

// D: preallocated file, but still open+write+fsync+close per op.
let pre = new FileStream(path, FileMode.Create, FileAccess.Write)
pre.SetLength(64L * 1024L * 1024L)
pre.Dispose()

let mutable dOffset = 0

let testD () =
    use s = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)
    s.Seek(int64 dOffset, SeekOrigin.Begin) |> ignore
    s.Write(payload, 0, payload.Length)
    s.Flush true
    dOffset <- (dOffset + payload.Length) % (64 * 1024 * 1024)

bench "D prealloc: open+write+fsync+close" testD 500
File.Delete path
