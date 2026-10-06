#r "../../tests/Fsdb.Tests/bin/Debug/net10.0/MySqlConnector.dll"

open System
open System.Globalization
open MySqlConnector

let render (value: obj) =
    match value with
    | :? DBNull -> "NULL"
    | :? decimal as value -> value.ToString("G29", CultureInfo.InvariantCulture)
    | value -> Convert.ToString(value, CultureInfo.InvariantCulture)

let run () =
    use connection = new MySqlConnection(Environment.GetEnvironmentVariable "FSDB_ORACLE_CONNECTION")
    connection.Open()
    if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion
    let execute sql =
        use command = new MySqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore
    execute "CREATE DATABASE volatile_window_oracle"
    execute "USE volatile_window_oracle"
    try
        execute "CREATE TABLE inputs(id INT PRIMARY KEY)"
        execute "INSERT INTO inputs VALUES(1),(2),(3)"
        execute "CREATE FUNCTION tick() RETURNS INT NOT DETERMINISTIC NO SQL RETURN (@n:=COALESCE(@n,0)+1)"
        let cases =
            [ "SELECT id,SUM(tick()) OVER(ORDER BY id ROWS BETWEEN 1 FOLLOWING AND 2 FOLLOWING) AS v FROM inputs ORDER BY id",
                  [ "5"; "3"; "NULL" ], [  ], 3
              "SELECT id,SUM(tick()) OVER(ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS v FROM inputs ORDER BY id",
                  [ "NULL"; "1"; "3" ], [  ], 3
              "SELECT id,COUNT(tick()) OVER(ORDER BY id) AS v FROM inputs ORDER BY id",
                  [ "1"; "2"; "3" ], [  ], 3
              "SELECT id,MIN(tick()) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS v FROM inputs ORDER BY id",
                  [ "1"; "1"; "2" ], [  ], 3
              "SELECT id,MAX(tick()) OVER(ORDER BY id ROWS BETWEEN 1 FOLLOWING AND 2 FOLLOWING) AS v FROM inputs ORDER BY id",
                  [ "3"; "3"; "NULL" ], [  ], 3
              "SELECT id,SUM(CONCAT(tick(),'x')) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS v FROM inputs ORDER BY id",
                  [ "1"; "3"; "5" ], [ "Truncated incorrect DOUBLE value: '1x'"; "Truncated incorrect DOUBLE value: '1x'"; "Truncated incorrect DOUBLE value: '2x'"; "Truncated incorrect DOUBLE value: '2x'"; "Truncated incorrect DOUBLE value: '3x'" ], 3
              "SELECT id,SUM(tick()) OVER() AS v FROM inputs ORDER BY id",
                  [ "6"; "6"; "6" ], [  ], 3
              "SELECT id,SUM(tick()) OVER(ORDER BY id) AS v FROM inputs ORDER BY id",
                  [ "1"; "3"; "6" ], [  ], 3
              "SELECT id,AVG(tick()) OVER(ORDER BY id) AS v FROM inputs ORDER BY id",
                  [ "1"; "1.5"; "2" ], [  ], 3
              "SELECT id,SUM(tick()) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS v FROM inputs ORDER BY id",
                  [ "1"; "3"; "5" ], [  ], 3
              "SELECT id,SUM(tick()) OVER(ORDER BY id ROWS BETWEEN CURRENT ROW AND CURRENT ROW) AS v FROM inputs ORDER BY id",
                  [ "1"; "2"; "3" ], [  ], 3
              "SELECT id,SUM(tick()) OVER(ORDER BY id DESC) AS v FROM inputs ORDER BY id",
                  [ "6"; "3"; "1" ], [  ], 3
              "SELECT id,SUM(CONCAT(tick(),'x')) OVER(ORDER BY id) AS v FROM inputs ORDER BY id",
                  [ "1"; "3"; "6" ], [ "Truncated incorrect DOUBLE value: '1x'"; "Truncated incorrect DOUBLE value: '2x'"; "Truncated incorrect DOUBLE value: '3x'" ], 3 ]
        for prepare in [ false; true ] do
            for sql, expected, warnings, calls in cases do
                execute "SET @n=0"
                use command = new MySqlCommand(sql, connection)
                if prepare then command.Prepare()
                let actual =
                    use reader = command.ExecuteReader()
                    [ while reader.Read() do render (reader.GetValue 1) ]
                if actual <> expected then failwithf "%s: expected %A; got %A" sql expected actual
                use warningCommand = new MySqlCommand("SHOW WARNINGS", connection)
                let actualWarnings =
                    use reader = warningCommand.ExecuteReader()
                    [ while reader.Read() do reader.GetInt32 1, reader.GetString 2 ]
                let expectedWarnings = List.map (fun message -> 1292, message) warnings
                if actualWarnings <> expectedWarnings then
                    failwithf "%s warnings: expected %A; got %A" sql expectedWarnings actualWarnings
                use counter = new MySqlCommand("SELECT @n", connection)
                let actualCalls = Convert.ToInt32(counter.ExecuteScalar())
                if actualCalls <> calls then failwithf "%s: expected %d calls; got %d" sql calls actualCalls
                printfn "Prepared=%b | %s | %A | calls=%d | warnings=%d" prepare sql actual calls warnings.Length
    finally
        execute "DROP DATABASE volatile_window_oracle"

run ()
