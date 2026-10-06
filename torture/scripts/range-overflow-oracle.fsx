#r "../../tests/Fsdb.Tests/bin/Debug/net10.0/MySqlConnector.dll"

open System
open System.Globalization
open MySqlConnector

type Outcome =
    | Rows of string list
    | Overflow of string

let run () =
    use connection = new MySqlConnection(Environment.GetEnvironmentVariable "FSDB_ORACLE_CONNECTION")
    connection.Open()
    if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion

    let execute sql =
        use command = new MySqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore

    let check prepare sql expected =
        use command = new MySqlCommand(sql, connection)
        if prepare then command.Prepare()
        let actual =
            try
                use reader = command.ExecuteReader()
                Rows [ while reader.Read() do
                           if reader.IsDBNull 0 then "NULL"
                           else Convert.ToString(reader.GetValue 0, CultureInfo.InvariantCulture) ]
            with :? MySqlException as error when error.Number = 1690 && error.SqlState = "22003" ->
                if error.Message.StartsWith("BIGINT UNSIGNED value is out of range") then Overflow "BIGINT UNSIGNED"
                elif error.Message.StartsWith("BIGINT value is out of range") then Overflow "BIGINT"
                else failwithf "Unexpected overflow diagnostic: %s" error.Message
        if actual <> expected then
            failwithf "Prepared=%b | %s: expected %A; got %A" prepare sql expected actual
        printfn "Prepared=%b | %s | %A" prepare sql actual

    execute "CREATE DATABASE range_overflow_oracle"
    execute "USE range_overflow_oracle"
    try
        execute "CREATE TABLE inputs(id INT PRIMARY KEY,u BIGINT UNSIGNED,s BIGINT,v INT)"
        execute "INSERT INTO inputs VALUES(1,0,-9223372036854775808,1),(2,18446744073709551615,9223372036854775807,2)"
        execute "CREATE TABLE single_input(u BIGINT UNSIGNED,s BIGINT,v INT)"
        execute "INSERT INTO single_input VALUES(18446744073709551615,9223372036854775807,2)"
        execute "CREATE TABLE composite_input(a INT,b INT,u BIGINT UNSIGNED,v INT,UNIQUE KEY pair(a,b))"
        execute "INSERT INTO composite_input VALUES(1,1,18446744073709551615,2),(1,2,10,3),(NULL,3,20,4)"
        execute "CREATE TABLE range_failure(k BIGINT UNSIGNED,v VARCHAR(20))"
        execute "INSERT INTO range_failure VALUES(18446744073709551615,'32x'),(3,'4x'),(NULL,'1x')"
        execute "CREATE TABLE exhausted_frame(u BIGINT UNSIGNED,v INT)"
        execute "INSERT INTO exhausted_frame VALUES(18446744073709551613,1),(18446744073709551614,2)"
        for prepare in [ false; true ] do
            execute "SET sql_mode=''"
            check prepare "SELECT SUM(v) OVER(ORDER BY u RANGE BETWEEN 2 FOLLOWING AND UNBOUNDED FOLLOWING) FROM exhausted_frame" (Overflow "BIGINT UNSIGNED")
            check prepare "SELECT SUM(v) OVER(ORDER BY k DESC RANGE BETWEEN 1 PRECEDING AND 1 FOLLOWING) FROM range_failure" (Overflow "BIGINT UNSIGNED")
            let warnings =
                use command = new MySqlCommand("SHOW WARNINGS", connection)
                use reader = command.ExecuteReader()
                [ while reader.Read() do reader.GetString 0, reader.GetInt32 1 ]
            if warnings <> [ ("Error", 1690) ] then
                failwithf "Unexpected diagnostics after descending frame overflow: %A" warnings
            check prepare "SELECT SUM(v) OVER(ORDER BY k DESC RANGE BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) FROM range_failure" (Overflow "BIGINT UNSIGNED")
            let warningsAfterError =
                use command = new MySqlCommand("SHOW WARNINGS", connection)
                use reader = command.ExecuteReader()
                [ while reader.Read() do reader.GetString 0, reader.GetInt32 1, reader.GetString 2 ]
            match warningsAfterError with
            | [ ("Error", 1690, _); ("Warning", 1292, "Truncated incorrect DOUBLE value: '32x'") ] -> ()
            | actual -> failwithf "Unexpected candidate conversion diagnostics: %A" actual
            for mode in [ ""; "NO_UNSIGNED_SUBTRACTION" ] do
                execute ("SET sql_mode='" + mode + "'")
                for key, filter, direction, value, integerOutcome in
                    [ "u", "id<=1", "PRECEDING", "1", (if mode = "" then Overflow "BIGINT UNSIGNED" else Rows [ "1" ])
                      "u", "id>=2", "FOLLOWING", "2", Overflow "BIGINT UNSIGNED"
                      "u", "id>=2", "PRECEDING", "2", (if mode = "" then Rows [ "2" ] else Overflow "BIGINT")
                      "s", "id<=1", "PRECEDING", "1", Overflow "BIGINT"
                      "s", "id>=2", "FOLLOWING", "2", Overflow "BIGINT" ] do
                    for offset in [ "1"; "1.0"; "1e0" ] do
                        let frame =
                            if direction = "PRECEDING" then offset + " PRECEDING AND CURRENT ROW"
                            else "CURRENT ROW AND " + offset + " FOLLOWING"
                        // The connector caches prepared statements; each mode must receive a fresh parse.
                        let sql = sprintf "SELECT /* mode:%s */ SUM(v) OVER(ORDER BY %s RANGE BETWEEN %s) FROM inputs WHERE %s" mode key frame filter
                        check prepare sql (if offset = "1" then integerOutcome else Rows [ value ])

            execute "SET sql_mode=''"
            for filter, expected in
                [ "a=1 AND b=1", [ "2" ]
                  "a=1", [ "NULL"; "NULL" ]
                  "a IS NULL AND b=3", [ "NULL" ] ] do
                check prepare (sprintf "SELECT SUM(v) OVER(ORDER BY u RANGE BETWEEN 2 PRECEDING AND 1 PRECEDING) FROM composite_input WHERE %s" filter) (Rows expected)
            for key in [ "u"; "r.u"; "u+0"; "CAST(u AS UNSIGNED)" ] do
                check prepare (sprintf "SELECT SUM(v) OVER(ORDER BY %s RANGE BETWEEN 2 PRECEDING AND 1 PRECEDING) FROM inputs AS r WHERE id=2" key) (Rows [ "2" ])
            for key, domain in [ "u", "BIGINT UNSIGNED"; "s", "BIGINT" ] do
                check prepare (sprintf "SELECT SUM(v) OVER(ORDER BY %s RANGE BETWEEN CURRENT ROW AND 1 FOLLOWING) FROM single_input" key) (Overflow domain)
            // Unique equality access removes the constant ordering expression, even for offset frames.
            for frame, nonconstant in
                [ "2 PRECEDING AND 1 PRECEDING", Rows [ "NULL" ]
                  "1 FOLLOWING AND 2 FOLLOWING", Overflow "BIGINT UNSIGNED" ] do
                for filter, expected in
                    [ "id=2", Rows [ "2" ]
                      "id>=2", nonconstant
                      "u=18446744073709551615", nonconstant ] do
                    check prepare (sprintf "SELECT SUM(v) OVER(ORDER BY u RANGE BETWEEN %s) FROM inputs WHERE %s" frame filter) expected
    finally
        execute "DROP DATABASE range_overflow_oracle"

run ()
