#r "../../tests/Fsdb.Tests/bin/Debug/net10.0/MySqlConnector.dll"

open System
open System.Diagnostics
open System.Globalization
open MySqlConnector

let execute (connection: MySqlConnection) sql =
    use command = new MySqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let query (connection: MySqlConnection) sql =
    use command = new MySqlCommand(sql, connection)
    command.CommandTimeout <- 120
    use reader = command.ExecuteReader()
    let mutable count = 0
    let mutable total = 0M
    while reader.Read() do
        count <- count + 1
        total <- total + Convert.ToDecimal(reader.GetValue(0), CultureInfo.InvariantCulture)
    count, total

let timed iterations action =
    let timer = Stopwatch.StartNew()
    for _ in 1..iterations do action () |> ignore
    timer.Elapsed.TotalMilliseconds / float iterations

let report rows target workload samples =
    let ordered = Array.sort samples
    let render (value: float) = value.ToString("F4", CultureInfo.InvariantCulture)
    printfn "| %d | %s | %s | %s | %s | %s |" rows target workload
        (render ordered[ordered.Length / 2]) (render ordered[0]) (render (Array.last ordered))
    printfn "<!-- samples-ms: %s -->" (samples |> Array.map render |> String.concat ",")

let run () =
    use baseline = new MySqlConnection(Environment.GetEnvironmentVariable "FSDB_BASELINE_CONNECTION")
    use candidate = new MySqlConnection(Environment.GetEnvironmentVariable "FSDB_CANDIDATE_CONNECTION")
    use mysql = new MySqlConnection(Environment.GetEnvironmentVariable "FSDB_MYSQL_CONNECTION")
    let targets = [ "baseline", baseline; "candidate", candidate; "mysql", mysql ]
    for target, connection in targets do
        connection.Open()
        if target = "mysql" && connection.ServerVersion.Split('-')[0] <> "8.4.11" then
            failwithf "Expected MySQL 8.4.11, got %s" connection.ServerVersion
        printfn "<!-- %s server: %s -->" target connection.ServerVersion
    let database = "window_comparison_" + Guid.NewGuid().ToString("N")
    try
        for _, connection in targets do
            execute connection ("CREATE DATABASE " + database)
            execute connection ("USE " + database)
            execute connection "CREATE TABLE inputs(id INT PRIMARY KEY)"
            execute connection "CREATE FUNCTION tick() RETURNS INT NOT DETERMINISTIC NO SQL BEGIN SET @n=COALESCE(@n,0)+1; RETURN @n; END"
        printfn "| Rows | Target | Workload | Median ms | Min ms | Max ms |"
        printfn "|---:|---|---|---:|---:|---:|"
        for rows in [1000; 5000; 10000] do
            let prefixTotal = [1..rows] |> List.sumBy (fun i -> decimal i * decimal (i+1) / 2M)
            let slidingTotal = [1..rows] |> List.sumBy (fun i -> [max 1 (i-10)..i] |> List.sum |> decimal)
            let workloads =
                [ "Grouped text SUM", "SELECT SUM(CAST(id AS CHAR)) FROM inputs", (1, decimal rows * decimal (rows+1) / 2M), false
                  "Prefix ROWS SUM", "SELECT SUM(id) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM inputs", (rows, prefixTotal), false
                  "Sliding ROWS SUM", "SELECT SUM(id) OVER(ORDER BY id ROWS BETWEEN 10 PRECEDING AND CURRENT ROW) FROM inputs", (rows, slidingTotal), false
                  "Offset RANGE SUM", "SELECT SUM(id) OVER(ORDER BY id RANGE BETWEEN 10 PRECEDING AND CURRENT ROW) FROM inputs", (rows, slidingTotal), false
                  "Stored-function prefix SUM", "SELECT SUM(tick()) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM inputs", (rows, prefixTotal), true ]
            let control connection () = query connection "SELECT id FROM inputs WHERE id=50"
            for target, connection in targets do
                execute connection "TRUNCATE TABLE inputs"
                for batch in [1..rows] |> List.chunkBySize 250 do
                    execute connection ("INSERT INTO inputs VALUES " + (batch |> List.map (sprintf "(%d)") |> String.concat ","))
                timed 200 (control connection) |> ignore
                [| for _ in 1..7 -> timed 100 (control connection) |]
                |> report rows target "Point lookup before"
            for name, sql, expected, counter in workloads do
                let runQuery target connection () =
                    if counter then execute connection "SET @n=0"
                    let actual = query connection sql
                    if actual <> expected then failwithf "%s/%d/%s: expected %A, got %A" target rows name expected actual
                for target, connection in targets do
                    let warmup = Stopwatch.StartNew()
                    let mutable warmed = 0
                    while warmed < 3 || warmup.Elapsed.TotalSeconds < 2.0 do
                        runQuery target connection ()
                        warmed <- warmed + 1
                    if counter && query connection "SELECT @n" <> (1, decimal rows) then
                        failwithf "%s: function arguments were not evaluated once per input" target
                let samples = targets |> List.map (fun (target, _) -> target, ResizeArray<float>()) |> Map.ofList
                for round in 0..8 do
                    let shift = round % targets.Length
                    let rotated = List.skip shift targets @ List.take shift targets
                    let order = if round % 2 = 0 then rotated else List.rev rotated
                    for target, connection in order do
                        timed 1 (runQuery target connection) |> samples[target].Add
                for target, _ in targets do samples[target].ToArray() |> report rows target name
            for target, connection in targets do
                [| for _ in 1..7 -> timed 100 (control connection) |]
                |> report rows target "Point lookup after"
    finally
        for _, connection in targets do
            execute connection ("DROP DATABASE IF EXISTS " + database)

run ()
