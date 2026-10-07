#r "../../tests/Fsdb.Tests/bin/Debug/net10.0/MySqlConnector.dll"
open System
open MySqlConnector
let run () =
    use connection = new MySqlConnection(Environment.GetEnvironmentVariable "FSDB_ORACLE_CONNECTION")
    connection.Open()
    if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11, got %s" connection.ServerVersion
    let execute sql =
        use command = new MySqlCommand(sql,connection)
        command.ExecuteNonQuery() |> ignore
    let database = "ngram_columns_" + Guid.NewGuid().ToString("N")
    execute ("CREATE DATABASE " + database + " CHARACTER SET utf8mb4")
    try
        execute ("USE " + database)
        execute "CREATE TABLE docs(id INT PRIMARY KEY,a TEXT,b TEXT,FULLTEXT(a,b))"
        execute "INSERT INTO docs VALUES(1,'mysql','security'),(2,'mysql security',''),(3,'mysqlsecurity',NULL),(4,'my','sql'),(5,'sql','mysql security'),(6,'mysql extra security',''),(7,'security mysql',''),(8,'database',''),(9,'mysql the security',''),(10,'mysql x security','')"
        execute "ANALYZE TABLE docs"
        let cases =
            [ "\"mysql security\"", [2;5], [2;4;5]
              "\"mysql security\" database", [2;5;8], [2;4;5;8]
              "mysql \"security database\"", [1;2;5;6;7;9;10], [1;2;4;5;6;7;9;10]
              "\"mysql security\" \"security mysql\"", [2;5;7], [2;4;5;7]
              "\"mysql the security\"", [9], [9]
              "\"the mysql\"", [1;2;5;6;7;9;10], [1;2;4;5;6;7;9;10]
              "\"mysql x security\"", [10], [10]
              "\"mysql mysql\"", [], []
              "\"mysql security", [1;2;5;6;7;9;10], [1;2;4;5;6;7;9;10]
              "mysql security\"", [1;2;5;6;7;9;10], [1;2;4;5;6;7;9;10]
              "\"mysql security\" @100", [2;5], [2;4;5] ]
        for term, natural, expanded in cases do
            for mode, expected in ["IN NATURAL LANGUAGE MODE", natural; "WITH QUERY EXPANSION", expanded] do
                let sql = sprintf "SELECT id FROM docs WHERE MATCH(a,b) AGAINST('%s' %s) ORDER BY id" term mode
                use command = new MySqlCommand(sql,connection)
                use reader = command.ExecuteReader()
                let rows = [while reader.Read() do yield reader.GetInt32 0]
                if rows <> expected then failwithf "%s expected %A, got %A" sql expected rows
                printfn "%s -> %A" sql rows
        for term, row, expected in
            [ "\"mysql security\"", 2, 0.04798923433
              "\"mysql security\" mysql", 1, 0.02135340311
              "\"mysql security\" mysql", 2, 0.04534801841
              "mysql mysql", 1, 0.02135340311
              "\"mysql security\" \"mysql security\"", 2, 0.04270680621 ] do
            let sql = sprintf "SELECT MATCH(a,b) AGAINST('%s') FROM docs WHERE id=%d" term row
            use command = new MySqlCommand(sql, connection)
            let actual = Convert.ToDouble(command.ExecuteScalar())
            if abs (actual - expected) > 0.000001 then
                failwithf "%s expected %g, got %g" sql expected actual
            printfn "%s -> %g" sql actual
    finally
        execute ("DROP DATABASE " + database)
run ()
