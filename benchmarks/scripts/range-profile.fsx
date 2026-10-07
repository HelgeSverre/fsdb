#r "../../tests/Fsdb.Tests/bin/Debug/net10.0/MySqlConnector.dll"
open System
open System.Diagnostics
open MySqlConnector
let runProfile () =
    use connection = new MySqlConnection(Environment.GetEnvironmentVariable "FSDB_PERF_CONNECTION")
    connection.Open()
    let execute sql =
        use command = new MySqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore
    let database = "range_profile_" + Guid.NewGuid().ToString("N")
    execute ("CREATE DATABASE " + database)
    try
        execute ("USE " + database)
        execute "CREATE TABLE inputs(id INT PRIMARY KEY)"
        for batch in [1..5000] |> List.chunkBySize 250 do
            execute ("INSERT INTO inputs VALUES " + (batch |> List.map (sprintf "(%d)") |> String.concat ","))
        let run () =
            use command = new MySqlCommand("SELECT SUM(id) OVER(ORDER BY id RANGE BETWEEN 10 PRECEDING AND CURRENT ROW) FROM inputs", connection)
            use reader = command.ExecuteReader()
            let mutable rows = 0
            let mutable total = 0M
            while reader.Read() do
                rows <- rows + 1
                total <- total + Convert.ToDecimal(reader.GetValue 0)
            if rows <> 5000 || total <> 137252665M then failwithf "Unexpected result: %d / %M" rows total
        for _ in 1..3 do run ()
        let elapsed = Stopwatch.StartNew()
        let mutable queries = 0
        while elapsed.Elapsed.TotalSeconds < 40.0 do
            run ()
            queries <- queries + 1
        printfn "%d queries in %.3f seconds" queries elapsed.Elapsed.TotalSeconds
    finally
        execute ("DROP DATABASE " + database)

runProfile ()
