#r "../../tests/Fsdb.Tests/bin/Debug/net10.0/MySqlConnector.dll"

open System
open System.Globalization
open MySqlConnector

let run () =
    use connection = new MySqlConnection(Environment.GetEnvironmentVariable "FSDB_ORACLE_CONNECTION")
    connection.Open()
    if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11, got %s" connection.ServerVersion
    let execute sql =
        use command = new MySqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore
    let inspect sql =
        use command = new MySqlCommand(sql, connection)
        try
            use reader = command.ExecuteReader()
            let rows =
                [ while reader.Read() do
                    yield [ for i in 0..reader.FieldCount-1 ->
                                if reader.IsDBNull i then "NULL"
                                else Convert.ToString(reader.GetValue i, CultureInfo.InvariantCulture) ] ]
            printfn "%s\n%A" sql rows
            rows
        with :? MySqlException as error ->
            printfn "%s\nERROR %d %s: %s" sql error.Number error.SqlState error.Message
            [ [ "ERROR"; string error.Number; error.SqlState ] ]
    let database = "ngram_oracle_" + Guid.NewGuid().ToString("N")
    execute ("CREATE DATABASE " + database + " CHARACTER SET utf8mb4")
    try
        execute ("USE " + database)
        inspect "SELECT @@ngram_token_size,@@innodb_ft_enable_stopword" |> ignore
        execute "CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body) WITH PARSER ngram)"
        execute "INSERT INTO docs VALUES (1,'生日快乐'),(2,'生日'),(3,'快乐'),(4,'生 日'),(5,'生日 开心'),(6,'abc'),(7,'ab bc'),(8,'a,b'),(9,'dbms'),(10,'mysql'),(11,'日本語'),(12,'日本 語'),(13,'한국어'),(14,'한국 어'),(15,'🙂生日'),(16,'生🙂日'),(17,'生'),(18,''),(19,NULL),(20,'生日生日'),(21,'生日!快乐'),(22,'日快')"
        inspect "SHOW CREATE TABLE docs" |> ignore
        let expected =
            [
            ("IN NATURAL LANGUAGE MODE", "生日快乐"), [ "1"; "2"; "3"; "5"; "15"; "20"; "21"; "22" ]
            ("IN NATURAL LANGUAGE MODE", "生日"), [ "1"; "2"; "5"; "15"; "20"; "21" ]
            ("IN NATURAL LANGUAGE MODE", "生"), [  ]
            ("IN NATURAL LANGUAGE MODE", "生*"), [  ]
            ("IN NATURAL LANGUAGE MODE", "生日*"), [ "1"; "2"; "5"; "15"; "20"; "21" ]
            ("IN NATURAL LANGUAGE MODE", "生日快乐*"), [ "1"; "2"; "3"; "5"; "15"; "20"; "21"; "22" ]
            ("IN NATURAL LANGUAGE MODE", "\"生日快乐\""), [ "1"; "2"; "3"; "5"; "15"; "20"; "21"; "22" ]
            ("IN NATURAL LANGUAGE MODE", "\"生日 快乐\""), [ "1"; "2"; "3"; "5"; "15"; "20"; "21" ]
            ("IN NATURAL LANGUAGE MODE", "+生日 +快乐"), [ "1"; "2"; "3"; "5"; "15"; "20"; "21" ]
            ("IN NATURAL LANGUAGE MODE", "生日 -快乐"), [ "1"; "2"; "3"; "5"; "15"; "20"; "21" ]
            ("IN NATURAL LANGUAGE MODE", "日本語"), [ "11"; "12" ]
            ("IN NATURAL LANGUAGE MODE", "한국어"), [ "13"; "14" ]
            ("IN NATURAL LANGUAGE MODE", "abc"), [ "6"; "7" ]
            ("IN NATURAL LANGUAGE MODE", "ab"), [  ]
            ("IN NATURAL LANGUAGE MODE", "bc"), [ "6"; "7" ]
            ("IN NATURAL LANGUAGE MODE", "a,b"), [  ]
            ("IN NATURAL LANGUAGE MODE", "dbms"), [ "9" ]
            ("IN NATURAL LANGUAGE MODE", "🙂生"), [ "15" ]
            ("IN NATURAL LANGUAGE MODE", "生🙂日"), [ "16" ]
            ("IN BOOLEAN MODE", "生日快乐"), [ "1" ]
            ("IN BOOLEAN MODE", "生日"), [ "1"; "2"; "5"; "15"; "20"; "21" ]
            ("IN BOOLEAN MODE", "生"), [  ]
            ("IN BOOLEAN MODE", "生*"), [ "1"; "2"; "5"; "15"; "16"; "20"; "21" ]
            ("IN BOOLEAN MODE", "生日*"), [ "1"; "2"; "5"; "15"; "20"; "21" ]
            ("IN BOOLEAN MODE", "生日快乐*"), [ "1" ]
            ("IN BOOLEAN MODE", "\"生日快乐\""), [ "1" ]
            ("IN BOOLEAN MODE", "\"生日 快乐\""), [ "21" ]
            ("IN BOOLEAN MODE", "+生日 +快乐"), [ "1"; "21" ]
            ("IN BOOLEAN MODE", "生日 -快乐"), [ "2"; "5"; "15"; "20" ]
            ("IN BOOLEAN MODE", "日本語"), [ "11" ]
            ("IN BOOLEAN MODE", "한국어"), [ "13" ]
            ("IN BOOLEAN MODE", "abc"), [ "6"; "7" ]
            ("IN BOOLEAN MODE", "ab"), [  ]
            ("IN BOOLEAN MODE", "bc"), [ "6"; "7" ]
            ("IN BOOLEAN MODE", "a,b"), [  ]
            ("IN BOOLEAN MODE", "dbms"), [ "9" ]
            ("IN BOOLEAN MODE", "🙂生"), [  ]
            ("IN BOOLEAN MODE", "生🙂日"), [  ]
            ] |> Map.ofList
        for mode in ["IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE"] do
            for term in ["生日快乐"; "生日"; "生"; "生*"; "生日*"; "生日快乐*"; "\"生日快乐\""; "\"生日 快乐\""; "+生日 +快乐"; "生日 -快乐"; "日本語"; "한국어"; "abc"; "ab"; "bc"; "a,b"; "dbms"; "🙂生"; "生🙂日"] do
                let against = sprintf "MATCH(body) AGAINST('%s' %s)" term mode
                let sql = sprintf "SELECT id FROM docs WHERE %s ORDER BY id" against
                let actual = inspect sql
                let wanted = expected[mode, term] |> List.map List.singleton
                if actual <> wanted then failwithf "%s expected %A; got %A" sql wanted actual
        inspect "SELECT id,MATCH(body) AGAINST('生日快乐') FROM docs WHERE MATCH(body) AGAINST('生日快乐') ORDER BY id" |> ignore
        for sql, code, state in
            [ "CREATE FULLTEXT INDEX other ON docs(body) WITH PARSER missing_parser", "1128", "HY000"
              "CREATE INDEX ordinary ON docs(body(10)) WITH PARSER ngram", "1064", "42000" ] do
            let actual = inspect sql
            if actual <> [ [ "ERROR"; code; state ] ] then failwithf "Unexpected DDL result: %A" actual
        inspect "SHOW INDEX FROM docs" |> ignore
        let checkScopeLabels () =
            let expressions = [ "@@GLOBAL.ngram_token_size"; "@@gLoBaL.ngram_token_size"; "@@global.ngram_token_size" ]
            use command = new MySqlCommand("SELECT " + String.concat "," expressions, connection)
            use reader = command.ExecuteReader()
            let labels = [ for i in 0..reader.FieldCount-1 -> reader.GetName i ]
            if labels <> expressions then failwithf "Expected original scope labels; got %A" labels
        checkScopeLabels ()
        for sql, expected in
            [ "SELECT @@ngram_token_size,@@GLOBAL.ngram_token_size", [ [ "2"; "2" ] ]
              "SELECT @@SESSION.ngram_token_size", [ [ "ERROR"; "1238"; "HY000" ] ]
              "SET SESSION ngram_token_size=3", [ [ "ERROR"; "1238"; "HY000" ] ]
              "SET GLOBAL ngram_token_size=3", [ [ "ERROR"; "1238"; "HY000" ] ] ] do
            let actual = inspect sql
            if actual <> expected then failwithf "%s expected %A; got %A" sql expected actual
        for ddl in
            [ "CREATE TABLE created(id INT PRIMARY KEY, body TEXT)"
              "CREATE FULLTEXT INDEX ft ON created(body) WITH PARSER ngram"
              "CREATE TABLE altered(id INT PRIMARY KEY, body TEXT)"
              "ALTER TABLE altered ADD FULLTEXT INDEX ft(body) WITH PARSER ngram" ] do
            execute ddl
        for table in [ "created"; "altered" ] do
            execute (sprintf "INSERT INTO %s VALUES(1,'生日快乐'),(2,'生日')" table)
            let actual = inspect (sprintf "SELECT id FROM %s WHERE MATCH(body) AGAINST('生日快乐' IN BOOLEAN MODE) ORDER BY id" table)
            if actual <> [ [ "1" ] ] then failwithf "Unexpected %s results: %A" table actual
        execute "UPDATE docs SET body='中文检索' WHERE id=1"
        execute "DELETE FROM docs WHERE id=2"
        execute "INSERT INTO docs VALUES(23,'生日')"
        execute "START TRANSACTION"
        execute "UPDATE docs SET body='生日' WHERE id=3"
        execute "ROLLBACK"
        let actual = inspect "SELECT id FROM docs WHERE MATCH(body) AGAINST('生日') ORDER BY id"
        let expected = [ "5"; "15"; "20"; "21"; "23" ] |> List.map List.singleton
        if actual <> expected then failwithf "Unexpected mutation results: %A" actual

        execute "CREATE TABLE stopped(id INT PRIMARY KEY,body TEXT,FULLTEXT(body) WITH PARSER ngram)"
        execute "INSERT INTO stopped VALUES(1,'áb'),(2,'bé'),(3,'cdabef'),(4,'cdef'),(5,'cd ab ef'),(6,'cd be ef'),(7,'cd XX ef'),(8,'生日 快乐'),(9,'生日a快乐')"
        for sql, expected in
            [
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('ab' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('be' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "2" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('cdabef' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1"; "2"; "3"; "4"; "5"; "6"; "7" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"cd ef\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "3"; "4"; "5"; "6"; "7" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"cd be ef\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "2"; "3"; "4"; "5"; "6"; "7" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生日 快乐\" @100' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "8"; "9" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('ab' IN BOOLEAN MODE) ORDER BY id", [  ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('be' IN BOOLEAN MODE) ORDER BY id", [  ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('cdabef' IN BOOLEAN MODE) ORDER BY id", [ "3" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"cd ef\"' IN BOOLEAN MODE) ORDER BY id", [  ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"cd be ef\"' IN BOOLEAN MODE) ORDER BY id", [ "6" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生日 快乐\" @100' IN BOOLEAN MODE) ORDER BY id", [ "8" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生 生日\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "8"; "9" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生日 生\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "8"; "9" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"a 生日\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "8"; "9" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"ab 生日\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1"; "8"; "9" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生日 a\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "8"; "9" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生日 生 快乐\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "8"; "9" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生日 x 快乐\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "8"; "9" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生 生日\"' IN BOOLEAN MODE) ORDER BY id", [  ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生日 生\"' IN BOOLEAN MODE) ORDER BY id", [  ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"a 生日\"' IN BOOLEAN MODE) ORDER BY id", [ "8"; "9" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"ab 生日\"' IN BOOLEAN MODE) ORDER BY id", [ "8"; "9" ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生日 a\"' IN BOOLEAN MODE) ORDER BY id", [  ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生日 生 快乐\"' IN BOOLEAN MODE) ORDER BY id", [  ]
              "SELECT id FROM stopped WHERE MATCH(body) AGAINST('\"生日 x 快乐\"' IN BOOLEAN MODE) ORDER BY id", [  ]
            ] do
            let actual = inspect sql
            let expected = expected |> List.map List.singleton
            if actual <> expected then failwithf "%s expected %A; got %A" sql expected actual
        execute "CREATE TABLE multi_ngram(id INT PRIMARY KEY,a TEXT,b TEXT,FULLTEXT(a,b) WITH PARSER ngram)"
        execute "INSERT INTO multi_ngram VALUES(1,'生日','快乐'),(2,'生日 快乐',''),(3,'生日快乐',NULL),(4,'生','日'),(5,'日','生日 快乐')"
        for sql, expected in
            [
              "SELECT id FROM multi_ngram WHERE MATCH(a,b) AGAINST('生日快乐' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1"; "2"; "3"; "5" ]
              "SELECT id FROM multi_ngram WHERE MATCH(a,b) AGAINST('\"生日 快乐\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1"; "2"; "3"; "5" ]
              "SELECT id FROM multi_ngram WHERE MATCH(a,b) AGAINST('生日' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1"; "2"; "3"; "5" ]
              "SELECT id FROM multi_ngram WHERE MATCH(a,b) AGAINST('+生日 +快乐' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1"; "2"; "3"; "5" ]
              "SELECT id FROM multi_ngram WHERE MATCH(a,b) AGAINST('生日快乐' IN BOOLEAN MODE) ORDER BY id", [ "3" ]
              "SELECT id FROM multi_ngram WHERE MATCH(a,b) AGAINST('\"生日 快乐\"' IN BOOLEAN MODE) ORDER BY id", [ "2"; "5" ]
              "SELECT id FROM multi_ngram WHERE MATCH(a,b) AGAINST('生日' IN BOOLEAN MODE) ORDER BY id", [ "1"; "2"; "3"; "5" ]
              "SELECT id FROM multi_ngram WHERE MATCH(a,b) AGAINST('+生日 +快乐' IN BOOLEAN MODE) ORDER BY id", [ "1"; "2"; "3"; "5" ]
            ] do
            let actual = inspect sql
            let expected = expected |> List.map List.singleton
            if actual <> expected then failwithf "%s expected %A; got %A" sql expected actual
        execute "CREATE TABLE multi_words(id INT PRIMARY KEY,a TEXT,b TEXT,FULLTEXT(a,b) )"
        execute "INSERT INTO multi_words VALUES(1,'mysql','security'),(2,'mysql security',''),(3,'mysqlsecurity',NULL),(4,'my','sql'),(5,'sql','mysql security')"
        for sql, expected in
            [
              "SELECT id FROM multi_words WHERE MATCH(a,b) AGAINST('mysqlsecurity' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "3" ]
              "SELECT id FROM multi_words WHERE MATCH(a,b) AGAINST('\"mysql security\"' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "2"; "5" ]
              "SELECT id FROM multi_words WHERE MATCH(a,b) AGAINST('mysql' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1"; "2"; "5" ]
              "SELECT id FROM multi_words WHERE MATCH(a,b) AGAINST('+mysql +security' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "1"; "2"; "5" ]
              "SELECT id FROM multi_words WHERE MATCH(a,b) AGAINST('\"mysql security\" @100' IN NATURAL LANGUAGE MODE) ORDER BY id", [ "2"; "5" ]
              "SELECT id FROM multi_words WHERE MATCH(a,b) AGAINST('mysqlsecurity' IN BOOLEAN MODE) ORDER BY id", [ "3" ]
              "SELECT id FROM multi_words WHERE MATCH(a,b) AGAINST('\"mysql security\"' IN BOOLEAN MODE) ORDER BY id", [ "2"; "5" ]
              "SELECT id FROM multi_words WHERE MATCH(a,b) AGAINST('mysql' IN BOOLEAN MODE) ORDER BY id", [ "1"; "2"; "5" ]
              "SELECT id FROM multi_words WHERE MATCH(a,b) AGAINST('+mysql +security' IN BOOLEAN MODE) ORDER BY id", [ "1"; "2"; "5" ]
              "SELECT id FROM multi_words WHERE MATCH(a,b) AGAINST('\"mysql security\" @100' IN BOOLEAN MODE) ORDER BY id", [ "1"; "2"; "5" ]
            ] do
            let actual = inspect sql
            let expected = expected |> List.map List.singleton
            if actual <> expected then failwithf "%s expected %A; got %A" sql expected actual
    finally
        execute ("DROP DATABASE " + database)

run ()
