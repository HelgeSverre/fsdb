#r "../../tests/Fsdb.Tests/bin/Debug/net10.0/MySqlConnector.dll"

open System
open System.Diagnostics
open System.Globalization
open MySqlConnector

let execute (connection: MySqlConnection) sql =
    use command = new MySqlCommand(sql, connection)
    command.CommandTimeout <- 120
    command.ExecuteNonQuery() |> ignore

let count (connection: MySqlConnection) sql =
    use command = new MySqlCommand(sql, connection)
    command.CommandTimeout <- 120
    use reader = command.ExecuteReader()
    let mutable rows = 0L
    while reader.Read() do rows <- rows + 1L
    rows

let measure target rows label iterations action =
    let warmup = Stopwatch.StartNew()
    let mutable warmed = 0
    while warmed < 3 || warmup.Elapsed.TotalSeconds < 1.0 do
        action ()
        warmed <- warmed + 1
    let samples =
        [| for _ in 1..7 do
               let timer = Stopwatch.StartNew()
               for _ in 1..iterations do action ()
               yield timer.Elapsed.TotalMilliseconds / float iterations |]
    let ordered = Array.sort samples
    let render (value: float) = value.ToString("F4", CultureInfo.InvariantCulture)
    printfn "| %s | %d | %s | %s | %s | %s |" target rows label (render ordered[3]) (render ordered[0]) (render ordered[6])
    printfn "<!-- samples-ms: %s -->" (samples |> Array.map render |> String.concat ",")

let run target variable =
    use connection = new MySqlConnection(Environment.GetEnvironmentVariable variable)
    connection.Open()
    if target = "mysql" && connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11, got %s" connection.ServerVersion
    let database = "fulltext_perf_" + Guid.NewGuid().ToString("N")
    execute connection ("CREATE DATABASE " + database)
    execute connection ("USE " + database)
    try
        for rows in [1000; 10000] do
            execute connection "CREATE TABLE words(id INT PRIMARY KEY, body TEXT, FULLTEXT ft(body))"
            execute connection "CREATE TABLE grams(id INT PRIMARY KEY, body TEXT, FULLTEXT ft(body) WITH PARSER ngram)"
            for batch in [1..rows] |> List.chunkBySize 250 do
                let values selected other =
                    batch
                    |> List.map (fun id -> sprintf "(%d,'%s')" id (if id % 4 = 0 then selected else other))
                    |> String.concat ","
                execute connection ("INSERT INTO words VALUES " + values "database concurrency indexing" "application storage transactions")
                execute connection ("INSERT INTO grams VALUES " + values "生日快乐" "中文数据库管理")
            execute connection "ANALYZE TABLE words, grams"
            let cases =
                [ "Word natural", "words", "'database'", rows / 4
                  "Word natural phrase", "words", "'\"database concurrency\"'", rows / 4
                  "Word phrase expansion", "words", "'\"database concurrency\"' WITH QUERY EXPANSION", rows / 4
                  "Word boolean", "words", "'+database +concurrency' IN BOOLEAN MODE", rows / 4
                  "Ngram natural", "grams", "'生日'", rows / 4
                  "Ngram natural phrase", "grams", "'\"生日快乐\"'", rows / 4
                  "Ngram boolean phrase", "grams", "'\"生日快乐\"' IN BOOLEAN MODE", rows / 4
                  "Ngram prefix", "grams", "'生*' IN BOOLEAN MODE", rows / 4 ]
            let verify sql expected () =
                let actual = count connection sql
                if actual <> int64 expected then failwithf "%s: expected %d, got %d" sql expected actual
            for label, table, query, expected in cases do
                let sql = sprintf "SELECT id FROM %s WHERE MATCH(body) AGAINST (%s)" table query
                measure target rows label 10 (verify sql expected)
            for table, changed, original in ["words", "database concurrency indexing", "application storage transactions"; "grams", "生日快乐", "中文数据库管理"] do
                // A pair restores the corpus after every sample. Each UPDATE commits separately.
                let updatePair () =
                    execute connection (sprintf "UPDATE %s SET body='%s' WHERE id=1" table changed)
                    execute connection (sprintf "UPDATE %s SET body='%s' WHERE id=1" table original)
                measure target rows (table + " durable update pair") 10 updatePair
            execute connection "DROP TABLE words, grams"
    finally
        execute connection ("DROP DATABASE " + database)

printfn "| Target | Rows | Workload | Median ms | Min ms | Max ms |"
printfn "|---|---:|---|---:|---:|---:|"
run "fsdb-wal" "FSDB_PERF_CONNECTION"
run "mysql" "FSDB_ORACLE_CONNECTION"
