// Spike: JSON vs binary serialization of a large catalog's row data, at
// multi-hundred-MB scale. Faithfully reproduces the two real encoders:
//   JSON   — `Persistence.encodeRow`'s shape: a JSON array of rows, each an
//            array of `Value.toWire` strings (base64 for strings/bytes/json).
//   binary — `Persistence.encodeRowBin`'s shape: `[int32 cols]` + `Value.encodeValue`.
// The snapshot file is currently JSON; this measures what a binary snapshot
// would buy in size, encode time, and decode/parse time.
#I "../src/Fsdb/bin/Release/net10.0"
#r "Fsdb.dll"

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json.Nodes
open Fsdb.Value
open Fsdb.Binary

let timed (name: string) (f: unit -> 'a) : 'a =
    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()
    let sw = Stopwatch.StartNew()
    let r = f ()
    sw.Stop()
    printfn "%-26s %10.0f ms" name sw.Elapsed.TotalMilliseconds
    r

let rowCount =
    match Environment.GetEnvironmentVariable "SPIKE_ROWS" with
    | null -> 1_500_000
    | s -> (match Int32.TryParse s with true, n when n > 0 -> n | _ -> 1_500_000)

let jsonBlob =
    """{"plan":"pro","nested":{"x":1,"y":2,"z":3,"w":4},"tags":["alpha","beta","gamma","delta","epsilon"],"note":"a longer json payload standing in for a real application json column"}"""

printfn "building %d rows..." rowCount

let rows =
    Array.init rowCount (fun i ->
        [| VInt(int64 i)
           VString $"user_{i}"
           VString $"user_{i}@example.com"
           VInt(int64 (i % 80 + 18))
           VJson jsonBlob
           VDateTime(DateTime(2024, 1, 1).AddSeconds(float (i % 1_000_000))) |])

// --- JSON encode: streaming, matching `encodeRows` (array of toWire strings) ---
let jsonBytes =
    timed "json encode" (fun () ->
        use ms = new MemoryStream()
        use sw = new StreamWriter(ms, UTF8Encoding(false))
        sw.Write "["

        for ri in 0 .. rowCount - 1 do
            if ri > 0 then sw.Write ","
            let row = rows.[ri]
            sw.Write "["

            for ci in 0 .. row.Length - 1 do
                if ci > 0 then sw.Write ","
                sw.Write "\""
                sw.Write(toWire row.[ci])
                sw.Write "\""

            sw.Write "]"

        sw.Write "]"
        sw.Flush()
        ms.ToArray())

// --- Binary encode: matching `encodeRowBin` ---
let binBytes =
    timed "binary encode" (fun () ->
        let w = Writer()

        for row in rows do
            w.WriteInt32LE row.Length

            for v in row do
                encodeValue w v

        w.ToArray())

printfn ""
printfn "json size:   %10.1f MB" (float jsonBytes.Length / 1_000_000.0)
printfn "binary size: %10.1f MB  (%.1fx smaller)" (float binBytes.Length / 1_000_000.0) (float jsonBytes.Length / float binBytes.Length)

// --- JSON decode: File.ReadAllText + JsonNode.Parse, what `load` does ---
let _ =
    timed "json decode" (fun () ->
        let node = JsonNode.Parse(Encoding.UTF8.GetString jsonBytes)

        for row in node.AsArray() do
            for v in row.AsArray() do
                ofWire(v.GetValue<string>()) |> ignore)

// --- Binary decode: Reader + decodeValue ---
let _ =
    timed "binary decode" (fun () ->
        let r = Reader(binBytes)

        while r.Remaining > 0 do
            let n = r.ReadInt32LE()

            for _ in 1..n do
                decodeValue r |> ignore)
