#r "nuget: MySqlConnector, 2.6.2"

open System
open System.Diagnostics
open System.Globalization
open System.Threading
open System.Threading.Tasks
open MySqlConnector

let engine, port, pass, pointReads =
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| engine; port; pass |] -> engine, uint32 port, int pass, false
    | [| engine; port; pass; "point-reads" |] -> engine, uint32 port, int pass, true
    | _ -> failwith "Usage: dotnet fsi native-write-contention.fsx -- ENGINE PORT PASS [point-reads]"

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

let phase workers seconds operation =
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
                            let started = Stopwatch.GetTimestamp()

                            try
                                operation connection worker rng
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

let update hot connection worker (rng: Random) =
    let id = if hot then 1 else 1 + worker * 500 + rng.Next(500)
    let affected = execute connection $"UPDATE users SET age = age + 1 WHERE id = {id}"
    if affected <> 1 then failwithf "Expected one affected row, got %d" affected

let pointName id = $"user{id}"

let readPoint (connection: MySqlConnection) _ (rng: Random) =
    let id = rng.Next(1, 10001)
    use command = new MySqlCommand($"SELECT id, name, age FROM users WHERE id = {id}", connection)
    use reader = command.ExecuteReader()
    if not (reader.Read()) || reader.GetInt32(0) <> id || reader.GetString(1) <> pointName id || reader.GetInt32(2) <> 0 || reader.Read() then
        failwithf "Unexpected point read result for id %d" id

let measurePointReads () =
    let workers = if pass % 2 = 0 then [ 16; 1 ] else [ 1; 16 ]
    for count in workers do
        let _, warmRetries, _ = phase count 1.5 readPoint
        let samples, retries, elapsed = phase count 6.0 readPoint
        if warmRetries <> 0 || retries <> 0 || samples.Length = 0 then
            failwithf "Invalid point-read sample with %d workers" count

        let p99 = samples[int (ceil (0.99 * float samples.Length)) - 1]
        printfn "%s,%d,%d,%.3f,%.6f,%d" engine pass count (float samples.Length / elapsed) p99 samples.Length

let admin = connect ()
let database = $"write_peers_{pass}"
execute admin $"CREATE DATABASE {database}" |> ignore

try
    settings.Database <- database
    use observer = connect ()
    let nameColumn = if pointReads then ", name VARCHAR(40) NOT NULL" else ""
    execute observer $"CREATE TABLE users (id INT PRIMARY KEY, age INT NOT NULL{nameColumn}, KEY ix_age(age))" |> ignore

    for first in 1..250..10000 do
        let row id =
            if pointReads then $"({id},0,'{pointName id}')"
            else $"({id},0)"

        let values = [ first .. first + 249 ] |> List.map row |> String.concat ","
        execute observer $"INSERT INTO users VALUES {values}" |> ignore

    if pointReads then measurePointReads ()
    else
        let lanes = [ 1, false; 1, true; 16, false; 16, true ]
        for workers, hot in (if pass % 2 = 0 then List.rev lanes else lanes) do
            let previous = sum observer
            let warm, _, _ = phase workers 1.5 (update hot)
            let afterWarmup = sum observer
            let samples, retries, elapsed = phase workers 6.0 (update hot)
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
