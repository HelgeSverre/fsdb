// Spike: JSON vs binary at 10 GB scale, streamed to disk (no in-memory
// byte[], which .NET caps at 2 GB). Rows are generated deterministically on
// the fly, one at a time, so memory stays flat; the two encoders are the
// real ones (toWire+JSON vs Value.encodeValue+binary framing).
#I "../src/Fsdb/bin/Release/net10.0"
#r "Fsdb.dll"

open System
open System.Diagnostics
open System.IO
open System.Text
open Fsdb.Value
open Fsdb.Binary

let timed (name: string) (f: unit -> 'a) : 'a =
    GC.Collect()
    let sw = Stopwatch.StartNew()
    let r = f ()
    sw.Stop()
    printfn "%-28s %10.0f ms" name sw.Elapsed.TotalMilliseconds
    r

let rowCount =
    match Environment.GetEnvironmentVariable "SPIKE_ROWS" with
    | null -> 26_000_000
    | s -> (match Int32.TryParse s with true, n when n > 0 -> n | _ -> 26_000_000)

let jsonBlob =
    """{"plan":"pro","nested":{"x":1,"y":2,"z":3,"w":4},"tags":["alpha","beta","gamma","delta","epsilon"],"note":"a longer json payload standing in for a real application json column"}"""

let mkRow (i: int) : Value[] =
    [| VInt(int64 i)
       VString $"user_{i}"
       VString $"user_{i}@example.com"
       VInt(int64 (i % 80 + 18))
       VJson jsonBlob
       VDateTime(DateTime(2024, 1, 1).AddSeconds(float (i % 1_000_000))) |]

let jsonPath = Path.Combine(Path.GetTempPath(), "snap-json.fsdb")
let binPath = Path.Combine(Path.GetTempPath(), "snap-bin.fsdb")

// --- encode: stream both to disk ---
timed "json encode (stream)" (fun () ->
    use fs = new FileStream(jsonPath, FileMode.Create, FileAccess.Write)
    use sw = new StreamWriter(fs, UTF8Encoding(false), 1 <<< 20)
    sw.Write "["

    for i in 0 .. rowCount - 1 do
        if i > 0 then sw.Write ","
        let row = mkRow i
        sw.Write "["

        for ci in 0 .. row.Length - 1 do
            if ci > 0 then sw.Write ","
            sw.Write "\""
            sw.Write(toWire row.[ci])
            sw.Write "\""

        sw.Write "]"

    sw.Write "]"
    sw.Flush())
|> ignore

timed "binary encode (stream)" (fun () ->
    use fs = new FileStream(binPath, FileMode.Create, FileAccess.Write)
    let mutable w = Writer()
    let rowsPerFlush = 4096

    for i in 0 .. rowCount - 1 do
        let row = mkRow i
        w.WriteInt32LE row.Length

        for v in row do
            encodeValue w v

        if (i + 1) % rowsPerFlush = 0 then
            let bytes = w.ToArray()
            fs.Write(bytes, 0, bytes.Length)
            w <- Writer()

    if rowCount % rowsPerFlush <> 0 then
        let bytes = w.ToArray()
        fs.Write(bytes, 0, bytes.Length)

    fs.Flush())
|> ignore

let jsonSize = FileInfo(jsonPath).Length
let binSize = FileInfo(binPath).Length
let gb (b: int64) = float b / 1_000_000_000.0
printfn ""
printfn "json size:   %8.2f GB" (gb jsonSize)
printfn "binary size: %8.2f GB  (%.2fx smaller)" (gb binSize) (float jsonSize / float binSize)

// --- read back ---
try
    timed "json read (ReadAllText)" (fun () -> File.ReadAllText jsonPath) |> ignore
    printfn "json read: OK"
with ex ->
    printfn "json read: FAILED (%s) — the snapshot loader's ReadAllText+JsonNode.Parse path cannot hold a multi-GB snapshot" (ex.GetType().Name)

// Streaming binary decode: read one record at a time, mirroring decodeValue's
// byte layout, so the whole file never sits in memory at once.
let readLenenc (r: BinaryReader) : int =
    let b = r.ReadByte()

    if b < 0xfbuy then int b
    elif b = 0xfcuy then int (r.ReadInt16())
    elif b = 0xfduy then
        let a = int (r.ReadByte())
        let b2 = int (r.ReadByte())
        let c = int (r.ReadByte())
        a ||| (b2 <<< 8) ||| (c <<< 16)
    else
        int (r.ReadInt64())

let readValue (r: BinaryReader) : unit =
    match r.ReadByte() with
    | 0x00uy -> ()
    | 0x01uy -> r.ReadInt64() |> ignore
    | 0x02uy -> r.ReadInt64() |> ignore
    | 0x03uy ->
        for _ in 1..4 do
            r.ReadInt32() |> ignore
    | 0x04uy
    | 0x08uy -> r.ReadBytes(readLenenc r) |> ignore
    | 0x05uy -> r.ReadBytes(readLenenc r) |> ignore
    | 0x06uy -> r.ReadInt32() |> ignore
    | 0x07uy ->
        r.ReadInt64() |> ignore
        r.ReadByte() |> ignore
    | t -> failwithf "unknown value tag 0x%02x" t

timed "binary decode (stream)" (fun () ->
    use fs = new FileStream(binPath, FileMode.Open, FileAccess.Read)
    use br = new BinaryReader(new BufferedStream(fs, 1 <<< 20))

    while br.BaseStream.Position < br.BaseStream.Length do
        let cols = br.ReadInt32()

        for _ in 1..cols do
            readValue br)
|> ignore

File.Delete jsonPath
File.Delete binPath
