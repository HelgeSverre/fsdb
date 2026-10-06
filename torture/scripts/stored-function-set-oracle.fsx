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
    let expect sql expected =
        use command = new MySqlCommand(sql, connection)
        use reader = command.ExecuteReader()
        if not (reader.Read()) then failwithf "%s returned no row" sql
        let actual = [ for i in 0 .. reader.FieldCount - 1 -> render (reader.GetValue i) ]
        if actual <> expected || reader.Read() then failwithf "%s expected %A; got %A" sql expected actual
        printfn "%s -> %A" sql actual
    execute "CREATE DATABASE stored_function_set_oracle"
    execute "USE stored_function_set_oracle"
    try
        execute "CREATE FUNCTION set_tick(step INT) RETURNS INT NOT DETERMINISTIC NO SQL BEGIN DECLARE delta INT DEFAULT step; SET @n=COALESCE(@n,0)+delta,@label=CONCAT('count,',@n); RETURN @n; END"
        execute "SET @n=10"
        for sql, expected in
            [ "SELECT set_tick(2)", [ "12" ]
              "SELECT @n,@label", [ "12"; "count,12" ]
              "SELECT set_tick(3)", [ "15" ]
              "SELECT @label", [ "count,15" ] ] do
            expect sql expected
        execute "SET @n=10,@label='old'"
        execute "SET @n=@n+2,@label=CONCAT('count,',@n)"
        expect "SELECT @n,@label" [ "12"; "count,10" ]
        execute "CREATE FUNCTION raise_set() RETURNS INT NOT DETERMINISTIC NO SQL BEGIN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='failed assignment'; RETURN 0; END"
        execute "CREATE FUNCTION handled_set() RETURNS INT NOT DETERMINISTIC NO SQL BEGIN DECLARE CONTINUE HANDLER FOR SQLSTATE '45000' SET @handled=1; SET @n=@n+1,@bad=raise_set(),@tail=7; RETURN @n; END"
        execute "SET @n=10,@handled=0,@tail=0"
        expect "SELECT handled_set()" [ "11" ]
        expect "SELECT @n,@handled,@tail" [ "11"; "1"; "7" ]
        execute "CREATE FUNCTION loop_set() RETURNS INT NOT DETERMINISTIC NO SQL BEGIN DECLARE i INT DEFAULT 0; WHILE i<3 DO SET @n=COALESCE(@n,0)+i; SET i=i+1; END WHILE; RETURN @n; END"
        execute "SET @n=0"
        expect "SELECT loop_set()" [ "3" ]
        for loop in
            [ "counting/* label */: WHILE i<3 DO SET i=i+1; END WHILE counting"
              "`counting`/* label */: WHILE i<3 DO SET i=i+1; END WHILE counting"
              "REPEAT SET i=i+1; UNTIL i=3 END REPEAT"
              "counting: LOOP SET i=i+1; IF i=3 THEN LEAVE counting; END IF; END LOOP counting"
              "IF i=0 THEN WHILE i<3 DO SET i=i+1; END WHILE; ELSE SET i=3; END IF"
              "DO IF(1,2,3); SET i=3" ] do
            execute ("CREATE FUNCTION control_set() RETURNS INT NO SQL BEGIN DECLARE i INT DEFAULT 0; " + loop + "; RETURN i; END")
            expect "SELECT control_set()" [ "3" ]
            execute "DROP FUNCTION control_set"
        execute "CREATE TABLE trigger_inputs(id INT PRIMARY KEY)"
        execute "CREATE TRIGGER handled_trigger BEFORE INSERT ON trigger_inputs FOR EACH ROW BEGIN DECLARE CONTINUE HANDLER FOR SQLSTATE '45000' SET @handled=1; SET @n=NEW.id,@bad=raise_set(),@tail=NEW.id+1; END"
        execute "SET @n=0,@handled=0,@tail=0"
        execute "INSERT INTO trigger_inputs VALUES(41)"
        expect "SELECT @n,@handled,@tail" [ "41"; "1"; "42" ]
    finally
        execute "DROP DATABASE stored_function_set_oracle"

run ()
