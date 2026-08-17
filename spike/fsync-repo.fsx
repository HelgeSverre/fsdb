// Phase 0b: same fsync microbenchmark, but writing to the repo directory
// (main APFS data volume) instead of Path.GetTempPath() (/var/folders/.../T).
open System
open System.Diagnostics
open System.IO

let bench (name: string) (path: string) (op: FileStream -> unit) (perOp: bool) (iters: int) =
    let mutable offset = 0
    let payload = Array.init 200 (fun _ -> 0xABuy)

    let runOp () =
        if perOp then
            use s = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
            op s
        else
            op null

    for _ in 1..200 do
        runOp ()

    let sw = Stopwatch.StartNew()
    for _ in 1..iters do
        runOp ()

    sw.Stop()
    let usPerOp = float sw.Elapsed.TotalMilliseconds * 1000.0 / float iters
    printfn "%-46s %10.1f us/op  (%s)" name usPerOp (Path.GetDirectoryName path)

let dir = __SOURCE_DIRECTORY__

// Reused-handle variants against a preallocated file in the repo dir.
let path = Path.Combine(dir, "fsync-repo.bin")

let s = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)
s.SetLength(64L * 1024L * 1024L)
let mutable off = 0
let payload = Array.init 200 (fun _ -> 0xABuy)

let testReuse () =
    s.Seek(int64 off, SeekOrigin.Begin) |> ignore
    s.Write(payload, 0, payload.Length)
    s.Flush true
    off <- (off + payload.Length) % (64 * 1024 * 1024)

bench "repo: prealloc + reuse handle + fsync" path (fun _ -> testReuse ()) false 500
s.Dispose()

// Open/close per op, growing file, in the repo dir.
let testAppend () =
    use st = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
    st.Write(payload, 0, payload.Length)
    st.Flush true

bench "repo: growing open+write+fsync+close" path (fun _ -> testAppend ()) true 500
File.Delete path
