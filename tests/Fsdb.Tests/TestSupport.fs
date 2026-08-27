module Fsdb.Tests.TestSupport

open System
open System.IO
open Expecto
open Fsdb.Executor
open Fsdb.Functions
open Fsdb.Storage

let private root =
    Path.Combine(Path.GetTempPath(), "fsdb-tests", sprintf "%d-%s" Environment.ProcessId (Guid.NewGuid().ToString "N"))

let directory (category: string) =
    let path = Path.Combine(root, category, Guid.NewGuid().ToString "N")
    Directory.CreateDirectory path |> ignore
    path

let withDirectory (category: string) action =
    let path = directory category

    try
        action path
    finally
        if Directory.Exists path then
            Directory.Delete(path, true)

let processGlobalCase name body =
    testCase name body |> testSequenced

module Sql =
    let execute (store: Store) (registry: Registry) (sql: string) : QueryResult =
        match Fsdb.Parser.parse sql with
        | Error message -> failtestf "expected %s to parse, got error: %s" sql message
        | Ok statement -> Fsdb.Executor.execute store registry defaultDatabase (0L, 0L) false statement |> snd

    let executeDefault (store: Store) (sql: string) : QueryResult =
        execute store builtins sql

    let expectOk (result: QueryResult) context =
        match result with
        | Err(code, message) -> failtestf "%s failed (%d): %s" context code message
        | _ -> ()

    let expectError code (result: QueryResult) context =
        match result with
        | Err(actual, _) -> Expect.equal actual code context
        | other -> failtestf "%s: expected error %d, got %A" context code other

    let rows (store: Store) (sql: string) =
        match executeDefault store sql with
        | ResultSet(_, result) -> result
        | other -> failtestf "expected rows from %s, got %A" sql other

module SqlCommentMutation =
    let whitespaceRuns (sql: string) =
        let runs = ResizeArray<int * int>()
        let mutable quote = None
        let mutable blockComment = false
        let mutable lineComment = false
        let mutable index = 0

        while index < sql.Length do
            match quote with
            | Some current when current <> '`' && sql.[index] = '\\' && index + 1 < sql.Length -> index <- index + 2
            | Some current when sql.[index] = current && index + 1 < sql.Length && sql.[index + 1] = current -> index <- index + 2
            | Some current when sql.[index] = current ->
                quote <- None
                index <- index + 1
            | Some _ -> index <- index + 1
            | None when blockComment && sql.[index] = '*' && index + 1 < sql.Length && sql.[index + 1] = '/' ->
                blockComment <- false
                index <- index + 2
            | None when blockComment -> index <- index + 1
            | None when lineComment && (sql.[index] = '\n' || sql.[index] = '\r') ->
                lineComment <- false
                index <- index + 1
            | None when lineComment -> index <- index + 1
            | None when sql.[index] = '\'' || sql.[index] = '"' || sql.[index] = '`' ->
                quote <- Some sql.[index]
                index <- index + 1
            | None when sql.[index] = '#' ->
                lineComment <- true
                index <- index + 1
            | None when
                sql.[index] = '-'
                && index + 1 < sql.Length
                && sql.[index + 1] = '-'
                && (index + 2 = sql.Length || Char.IsWhiteSpace sql.[index + 2])
                ->
                lineComment <- true
                index <- index + 2
            | None when sql.[index] = '/' && index + 1 < sql.Length && sql.[index + 1] = '*' ->
                blockComment <- true
                index <- index + 2
            | None when Char.IsWhiteSpace sql.[index] ->
                let start = index

                while index < sql.Length && Char.IsWhiteSpace sql.[index] do
                    index <- index + 1

                runs.Add(start, index - start)
            | None -> index <- index + 1

        runs.ToArray()

    let injectAt (sql: string) (start, length) comment =
        sql.Substring(0, start) + comment + sql.Substring(start + length)

    let injectEverywhere (sql: string) comment =
        whitespaceRuns sql
        |> Array.rev
        |> Array.fold (fun current run -> injectAt current run comment) sql

type ServerFixture(listener: Net.Sockets.TcpListener, completion: Threading.Tasks.Task) =
    member _.Listener = listener
    member _.Port = Fsdb.Server.port listener
    member _.Completion = completion
    member _.Stop() = listener.Stop()

    interface IDisposable with
        member this.Dispose() = this.Stop()

module ServerFixture =
    let startWithOptions options store registry =
        let listener = Fsdb.Server.startListening Net.IPAddress.Loopback 0

        try
            let completion =
                Fsdb.Server.serveWithOptions options listener store registry
                |> Async.StartAsTask
                :> Threading.Tasks.Task

            new ServerFixture(listener, completion)
        with _ ->
            listener.Stop()
            reraise ()

    let start store registry =
        startWithOptions Fsdb.ServerOptions.defaults store registry

let mutable private stressRun = false

let configureForArguments (arguments: string array) =
    stressRun <- arguments |> Array.contains "--stress"

let skipTimingAssertions () =
    stressRun || Environment.GetEnvironmentVariable "FSDB_COVERAGE" = "1"

let cleanup () =
    if Directory.Exists root then
        Directory.Delete(root, true)
