#r "nuget: MySqlConnector, 2.6.2"

open System
open System.Diagnostics
open System.Globalization
open System.Threading
open System.Threading.Tasks
open MySqlConnector

let engine, port, pass =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| engine; port; pass |] -> engine, uint32 port, int pass
    | _ -> failwith "Usage: dotnet fsi native-write-contention.fsx -- ENGINE PORT PASS"

CultureInfo.CurrentCulture <- CultureInfo.InvariantCulture

let settings =
    MySqlConnectionStringBuilder(
        Server = "127.0.0.1",
        Port = port,
        UserID = "root",
        SslMode = MySqlSslMode.Disabled,
        Pooling = false
    )

let connect () =
    let connection = new MySqlConnection(settings.ConnectionString)
    connection.Open()
    connection

let execute (connection: MySqlConnection) sql =
    use command = new MySqlCommand(sql, connection)
    command.ExecuteNonQuery()

let sum (connection: MySqlConnection) =
    use command = new MySqlCommand("SELECT SUM(age) FROM users", connection)
    Convert.ToInt64(command.ExecuteScalar())

let phase workers hot seconds =
    use ready = new CountdownEvent(workers)
    use start = new ManualResetEventSlim(false)
    let connections = Array.init workers (fun _ -> connect ())
    let timer = Stopwatch()

    try
        for connection in connections do
            execute connection "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED" |> ignore

        let tasks =
            connections
            |> Array.mapi (fun worker connection ->
                Task.Factory.StartNew(
                    (fun () ->
                        let rng = Random(9348 + worker)
                        let samples = ResizeArray<float>()
                        let mutable retries = 0
                        ready.Signal() |> ignore
                        start.Wait()

                        while timer.Elapsed.TotalSeconds < seconds do
                            let id = if hot then 1 else 1 + worker * 500 + rng.Next(500)
                            let started = Stopwatch.GetTimestamp()

                            try
                                let affected = execute connection $"UPDATE users SET age = age + 1 WHERE id = {id}"
                                if affected <> 1 then failwithf "Expected one affected row, got %d" affected
                                samples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds)
                            with
                            | :? MySqlException as error when error.Number = 1205 || error.Number = 1213 ->
                                retries <- retries + 1
                        samples, retries),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                ))

        ready.Wait()
        timer.Start()
        start.Set()
        Task.WaitAll(tasks |> Array.map (fun task -> task :> Task))
        timer.Stop()
        let samples = tasks |> Array.collect (fun task -> fst task.Result |> Seq.toArray) |> Array.sort
        let retries = tasks |> Array.sumBy (fun task -> snd task.Result)
        samples, retries, timer.Elapsed.TotalSeconds
    finally
        connections |> Array.iter _.Dispose()

let admin = connect ()
let database = $"write_peers_{pass}"
execute admin $"CREATE DATABASE {database}" |> ignore

try
    settings.Database <- database
    use observer = connect ()
    execute observer "CREATE TABLE users (id INT PRIMARY KEY, age INT NOT NULL, KEY ix_age(age))" |> ignore

    for first in 1..250..10000 do
        let values = [ first .. first + 249 ] |> List.map (fun id -> $"({id},0)") |> String.concat ","
        execute observer $"INSERT INTO users VALUES {values}" |> ignore

    let lanes = [ 1, false; 1, true; 16, false; 16, true ]
    for workers, hot in (if pass % 2 = 0 then List.rev lanes else lanes) do
        let previous = sum observer
        let warm, _, _ = phase workers hot 1.5
        let afterWarmup = sum observer
        let samples, retries, elapsed = phase workers hot 6.0
        let warmCommitted = afterWarmup - previous
        let committed = sum observer - afterWarmup
        let lane = if hot then "hot-row" else "disjoint"
        let status, throughput, latency =
            if warmCommitted <> int64 warm.Length || committed <> int64 samples.Length then
                "mismatch", "", ""
            elif samples.Length = 0 then
                "no-completions", "", ""
            else
                let p99 = samples[int (ceil (0.99 * float samples.Length)) - 1]
                "ok", sprintf "%.3f" (float samples.Length / elapsed), sprintf "%.6f" p99
        printfn "%s,%d,%d,%s,%s,%s,%s,%d,%d,%d,%d,%d"
            engine pass workers lane status throughput latency retries warm.Length warmCommitted samples.Length committed
finally
    execute admin $"DROP DATABASE {database}" |> ignore
    admin.Dispose()
