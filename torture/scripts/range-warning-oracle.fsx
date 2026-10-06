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
    execute "CREATE DATABASE range_warning_oracle"
    execute "USE range_warning_oracle"
    try
        execute "CREATE TABLE inputs(id INT PRIMARY KEY,k INT,v VARCHAR(20))"
        execute "INSERT INTO inputs VALUES(1,NULL,'1x'),(2,NULL,'2x'),(3,1,'4x'),(4,3,'8x'),(5,3,'16x'),(6,7,'32x')"
        let cases =
            [ "ASC", "UNBOUNDED PRECEDING AND 1 PRECEDING",
                  [ "3"; "3"; "3"; "7"; "7"; "31" ], [ 1; 2; 4; 8; 16 ]
              "ASC", "UNBOUNDED PRECEDING AND 1 FOLLOWING",
                  [ "3"; "3"; "7"; "31"; "31"; "63" ], [ 1; 2; 4; 8; 16; 32 ]
              "ASC", "1 PRECEDING AND UNBOUNDED FOLLOWING",
                  [ "63"; "63"; "60"; "56"; "56"; "32" ], [ 1; 2; 4; 8; 16; 32; 1; 2; 4; 8; 16; 32; 4; 8; 16; 32; 8; 16; 32; 8; 16; 32; 32 ]
              "ASC", "1 FOLLOWING AND UNBOUNDED FOLLOWING",
                  [ "63"; "63"; "56"; "32"; "32"; "NULL" ], [ 1; 2; 4; 8; 16; 32; 1; 2; 4; 8; 16; 32; 8; 16; 32; 32; 32 ]
              "ASC", "1 PRECEDING AND 1 FOLLOWING",
                  [ "3"; "3"; "4"; "24"; "24"; "32" ], [ 1; 2; 1; 2; 4; 8; 16; 8; 16; 32 ]
              "DESC", "UNBOUNDED PRECEDING AND 1 PRECEDING",
                  [ "63"; "63"; "56"; "32"; "32"; "NULL" ], [ 32; 8; 16; 4; 1; 2 ]
              "DESC", "UNBOUNDED PRECEDING AND 1 FOLLOWING",
                  [ "63"; "63"; "60"; "56"; "56"; "32" ], [ 32; 8; 16; 4; 1; 2 ]
              "DESC", "1 PRECEDING AND UNBOUNDED FOLLOWING",
                  [ "3"; "3"; "7"; "31"; "31"; "63" ], [ 32; 8; 16; 4; 1; 2; 8; 16; 4; 1; 2; 8; 16; 4; 1; 2; 4; 1; 2; 1; 2; 1; 2 ]
              "DESC", "1 FOLLOWING AND UNBOUNDED FOLLOWING",
                  [ "3"; "3"; "3"; "7"; "7"; "31" ], [ 8; 16; 4; 1; 2; 4; 1; 2; 4; 1; 2; 1; 2; 1; 2; 1; 2 ]
              "DESC", "1 PRECEDING AND 1 FOLLOWING",
                  [ "3"; "3"; "4"; "24"; "24"; "32" ], [ 32; 8; 16; 8; 16; 4; 1; 2; 1; 2 ] ]
        for prepare in [ false; true ] do
            for direction, frame, expected, warnings in cases do
                let sql = "SELECT SUM(v) OVER(ORDER BY k " + direction + " RANGE BETWEEN " + frame + ") AS value FROM inputs ORDER BY id"
                use command = new MySqlCommand(sql, connection)
                if prepare then command.Prepare()
                let actual =
                    use reader = command.ExecuteReader()
                    [ while reader.Read() do
                        if reader.IsDBNull 0 then "NULL"
                        else Convert.ToString(reader.GetValue 0, CultureInfo.InvariantCulture) ]
                if actual <> expected then failwithf "%s: expected %A; got %A" sql expected actual
                use warningCommand = new MySqlCommand("SHOW WARNINGS", connection)
                let actualWarnings =
                    use reader = warningCommand.ExecuteReader()
                    [ while reader.Read() do
                        reader.GetString 0, reader.GetInt32 1, reader.GetString 2 ]
                let expectedWarnings =
                    warnings |> List.map (fun value -> "Warning", 1292, sprintf "Truncated incorrect DOUBLE value: '%dx'" value)
                if actualWarnings <> expectedWarnings then
                    failwithf "%s warnings: expected %A; got %A" sql expectedWarnings actualWarnings
                printfn "Prepared=%b | %s | %A | warnings=%d" prepare sql actual warnings.Length
    finally
        execute "DROP DATABASE range_warning_oracle"

run ()
