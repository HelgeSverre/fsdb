#r "../../tests/Fsdb.Tests/bin/Debug/net10.0/MySqlConnector.dll"

open System
open System.Globalization
open MySqlConnector

let run () =
    use connection = new MySqlConnection(Environment.GetEnvironmentVariable "FSDB_ORACLE_CONNECTION")
    connection.Open()
    if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion

    let execute sql =
        use command = new MySqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore

    execute "CREATE DATABASE aggregate_warning_oracle"
    execute "USE aggregate_warning_oracle"
    try
        execute "CREATE TABLE inputs(id INT PRIMARY KEY,v VARCHAR(30))"
        execute "INSERT INTO inputs VALUES(1,'12x'),(2,'12x'),(3,'bad'),(4,''),(5,'  '),(6,NULL),(7,' 2 ')"
        let malformed = [ "12x"; "12x"; "bad" ]
        let cases =
            [ "SUM(v)", [ "26" ], malformed
              "AVG(v)", [ "4.333333333333333" ], malformed
              "SUM(DISTINCT v)", [ "14" ], []
              "AVG(DISTINCT v)", [ "4.666666666666667" ], []
              "COUNT(v)", [ "6" ], []
              "SUM(v) OVER(ORDER BY id ROWS BETWEEN CURRENT ROW AND CURRENT ROW)",
                  [ "12"; "12"; "0"; "0"; "0"; "NULL"; "2" ], malformed
              "SUM(v) OVER(ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)",
                  [ "12"; "24"; "24"; "24"; "24"; "24"; "26" ], malformed
              "SUM(v) OVER()", List.replicate 7 "26", malformed
              "SUM(v) OVER(PARTITION BY MOD(id,2) ORDER BY id)",
                  [ "12"; "12"; "12"; "12"; "12"; "12"; "14" ], malformed
              "SUM(v) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)",
                  [ "12"; "24"; "12"; "0"; "0"; "0"; "2" ], [ "12x"; "12x"; "12x"; "12x"; "bad"; "bad" ] ]
        for prepare in [ false; true ] do
            for expression, expected, warnings in cases do
                let order = if expression.Contains("OVER") then " ORDER BY id" else ""
                use command = new MySqlCommand("SELECT " + expression + " AS value FROM inputs" + order, connection)
                if prepare then command.Prepare()
                let actual =
                    use reader = command.ExecuteReader()
                    [ while reader.Read() do
                        if reader.IsDBNull 0 then "NULL"
                        else Convert.ToString(reader.GetValue 0, CultureInfo.InvariantCulture) ]
                if actual <> expected then failwithf "%s: expected %A; got %A" expression expected actual
                use warningCommand = new MySqlCommand("SHOW WARNINGS", connection)
                let actualWarnings =
                    use reader = warningCommand.ExecuteReader()
                    [ while reader.Read() do
                        reader.GetString 0, reader.GetInt32 1, reader.GetString 2 ]
                let expectedWarnings =
                    warnings |> List.map (fun value -> "Warning", 1292, "Truncated incorrect DOUBLE value: '" + value + "'")
                if actualWarnings <> expectedWarnings then
                    failwithf "%s warnings: expected %A; got %A" expression expectedWarnings actualWarnings
                printfn "Prepared=%b | %s | %A | warnings=%d" prepare expression actual actualWarnings.Length
    finally
        execute "DROP DATABASE aggregate_warning_oracle"

run ()
