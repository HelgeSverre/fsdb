// Phase 0c: FileStream.Flush(true) vs raw libc fsync vs fcntl(F_FULLFSYNC).
open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices

[<DllImport("libc", SetLastError = true, EntryPoint = "open")>]
extern int c_open(string path, int flags, int mode)

[<DllImport("libc", SetLastError = true)>]
extern int write(int fd, byte[] buf, int count)

[<DllImport("libc", SetLastError = true)>]
extern int fsync(int fd)

[<DllImport("libc", SetLastError = true)>]
extern int close(int fd)

[<DllImport("libc", SetLastError = true, EntryPoint = "fcntl")>]
extern int c_fcntl(int fd, int cmd, int arg)

let O_WRONLY = 1
let O_CREAT = 0x200
let O_APPEND = 0x8
let F_FULLFSYNC = 51

let bench (name: string) (op: unit -> unit) (iters: int) =
    for _ in 1..100 do
        op ()

    let sw = Stopwatch.StartNew()
    for _ in 1..iters do
        op ()

    sw.Stop()
    let usPerOp = float sw.Elapsed.TotalMilliseconds * 1000.0 / float iters
    printfn "%-36s %10.1f us/op" name usPerOp

let dir = __SOURCE_DIRECTORY__
let path = Path.Combine(dir, "fsync-libc.bin")
let payload = Array.init 200 (fun _ -> 0xABuy)

let testDotNet () =
    use s = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)
    s.Write(payload, 0, payload.Length)
    s.Flush true

bench "FileStream.Flush(true)" testDotNet 300
File.Delete path

let testFsync () =
    let fd = c_open(path, O_WRONLY ||| O_CREAT ||| O_APPEND, 0x1B6)
    write(fd, payload, payload.Length) |> ignore
    fsync fd |> ignore
    close fd |> ignore

bench "libc open/write/fsync/close" testFsync 300
File.Delete path

let testFullFsync () =
    let fd = c_open(path, O_WRONLY ||| O_CREAT ||| O_APPEND, 0x1B6)
    write(fd, payload, payload.Length) |> ignore
    c_fcntl(fd, F_FULLFSYNC, 0) |> ignore
    close fd |> ignore

bench "libc open/write/F_FULLFSYNC/close" testFullFsync 300
File.Delete path
