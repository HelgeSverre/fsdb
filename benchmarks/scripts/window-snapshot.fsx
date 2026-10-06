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

let measure target rows name iterations before action =
    let warmup = Stopwatch.StartNew()
    let mutable warmed = 0
    while warmed < 3 || warmup.Elapsed.TotalSeconds < 2.0 do
        before ()
        for _ in 1..iterations do action () |> ignore
        warmed <- warmed + 1
    let samples =
        [| for _ in 1..7 do
               before ()
               let timer = Stopwatch.StartNew()
               for _ in 1..iterations do action () |> ignore
               timer.Stop()
               yield timer.Elapsed.TotalMilliseconds / float iterations |]
    let ordered = Array.sort samples
    let render (value: float) = value.ToString("F4", CultureInfo.InvariantCulture)
    printfn "| %s | %d | %s | %s | %s | %s |" target rows name (render ordered[3]) (render ordered[0]) (render ordered[6])
    printfn "<!-- samples-ms: %s -->" (samples |> Array.map render |> String.concat ",")

let run target environment =
    use connection = new MySqlConnection(Environment.GetEnvironmentVariable environment)
    connection.Open()
    if target = "mysql" && connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11, got %s" connection.ServerVersion
    let database = "window_perf_" + Guid.NewGuid().ToString("N")
    execute connection ("CREATE DATABASE " + database)
    execute connection ("USE " + database)
    try
        execute connection "CREATE TABLE inputs(id INT PRIMARY KEY)"
        execute connection "CREATE FUNCTION tick() RETURNS INT NOT DETERMINISTIC NO SQL RETURN (@n:=COALESCE(@n,0)+1)"
        for rows in [100; 1000; 5000] do
            execute connection "TRUNCATE TABLE inputs"
            for batch in [1..rows] |> List.chunkBySize 250 do
                let values = batch |> List.map (sprintf "(%d)") |> String.concat ","
                execute connection ("INSERT INTO inputs VALUES " + values)
            let control label =
                measure target rows label 100 ignore (fun () -> query connection "SELECT id FROM inputs WHERE id=50")
            control "Point lookup before"
            let prefixTotal = [1..rows] |> List.sumBy (fun i -> decimal i * decimal (i + 1) / 2M)
            let slidingTotal = [1..rows] |> List.sumBy (fun i -> [max 1 (i-10)..i] |> List.sum |> decimal)
            let cases =
                [ "Grouped text SUM", "SELECT SUM(CAST(id AS CHAR)) FROM inputs", 1, decimal rows * decimal (rows+1) / 2M, false
                  "Prefix ROWS SUM", "SELECT SUM(id) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM inputs", rows, prefixTotal, false
                  "Sliding ROWS SUM", "SELECT SUM(id) OVER(ORDER BY id ROWS BETWEEN 10 PRECEDING AND CURRENT ROW) FROM inputs", rows, slidingTotal, false
                  "Offset RANGE SUM", "SELECT SUM(id) OVER(ORDER BY id RANGE BETWEEN 10 PRECEDING AND CURRENT ROW) FROM inputs", rows, slidingTotal, false
                  "Stored-function prefix SUM", "SELECT SUM(tick()) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM inputs", rows, prefixTotal, true ]
            for name, sql, expectedRows, expectedTotal, counter in cases do
                let reset () = if counter then execute connection "SET @n=0"
                reset ()
                let actual = query connection sql
                if actual <> (expectedRows, expectedTotal) then
                    failwithf "%s/%s: expected %A, got %A" target name (expectedRows, expectedTotal) actual
                if counter && query connection "SELECT @n" <> (1, decimal rows) then
                    failwithf "%s: stored function was not evaluated once per row" target
                measure target rows name 1 reset (fun () -> query connection sql)
            control "Point lookup after"
    finally
        execute connection ("DROP DATABASE " + database)

printfn "| Target | Rows | Workload | Median ms | Min ms | Max ms |"
printfn "|---|---:|---|---:|---:|---:|"
run "fsdb" "FSDB_PERF_CONNECTION"
run "mysql" "FSDB_ORACLE_CONNECTION"
