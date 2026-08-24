module Fsdb.Tests.TestSupport

open System
open System.IO

let private root =
    Path.Combine(Path.GetTempPath(), "fsdb-tests", sprintf "%d-%s" Environment.ProcessId (Guid.NewGuid().ToString "N"))

let directory (category: string) =
    let path = Path.Combine(root, category, Guid.NewGuid().ToString "N")
    Directory.CreateDirectory path |> ignore
    path

let mutable private stressRun = false

let configureForArguments (arguments: string array) =
    stressRun <- arguments |> Array.contains "--stress"

let skipTimingAssertions () =
    stressRun || Environment.GetEnvironmentVariable "FSDB_COVERAGE" = "1"

let cleanup () =
    if Directory.Exists root then
        Directory.Delete(root, true)
