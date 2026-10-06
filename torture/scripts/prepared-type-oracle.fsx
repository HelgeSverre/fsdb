#r "../../tests/Fsdb.Tests/bin/Debug/net10.0/MySqlConnector.dll"

open System
open System.Globalization
open MySqlConnector

type Parameter =
    | Null
    | Integer of int64
    | Exact of decimal
    | Approximate of double
    | Text of string

type Protocol = Binary | Sql

type Execution =
    { Parameters: Parameter list
      Expected: string list }

let invariant = CultureInfo.InvariantCulture
let quote (text: string) = "'" + text.Replace("\\", "\\\\").Replace("'", "''") + "'"

let sqlValue = function
    | Null -> "NULL"
    | Integer value -> value.ToString(invariant)
    | Exact value -> value.ToString("0.0###########################", invariant)
    | Approximate value ->
        let literal = value.ToString("R", invariant)
        if literal.Contains('E') || literal.Contains('e') then literal else literal + "e0"
    | Text value -> quote value

let parameterValue = function
    | Null -> box DBNull.Value
    | Integer value -> box value
    | Exact value -> box value
    | Approximate value -> box value
    | Text value -> box value

let renderValue (value: obj) =
    match value with
    | :? DBNull -> "NULL"
    | :? decimal as number -> number.ToString("G29", invariant)
    | :? double as number -> number.ToString("R", invariant)
    | :? IFormattable as value -> value.ToString(null, invariant)
    | _ -> string value

let connectionString =
    match Environment.GetEnvironmentVariable "FSDB_ORACLE_CONNECTION" with
    | null | "" -> failwith "Set FSDB_ORACLE_CONNECTION to the pinned MySQL oracle connection string."
    | value ->
        let builder = MySqlConnectionStringBuilder(value)
        builder.Pooling <- false
        builder.AllowUserVariables <- true
        builder.ConnectionString

let run protocol sql executions =
    // Connector statement caching must not carry types between independent histories.
    use connection = new MySqlConnection(connectionString)
    connection.Open()
    if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion

    let execute sql =
        use command = new MySqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore

    use command = new MySqlCommand(sql, connection)
    match protocol with
    | Binary ->
        executions |> List.head |> _.Parameters
        |> List.iteri (fun index value ->
            command.Parameters.AddWithValue("@p" + string index, parameterValue value) |> ignore)
        command.Prepare()
    | Sql -> execute ("PREPARE probe FROM " + quote sql)

    executions
    |> List.iteri (fun index execution ->
        match protocol with
        | Binary ->
            execution.Parameters
            |> List.iteri (fun index value -> command.Parameters[index].Value <- parameterValue value)
        | Sql ->
            let variables = execution.Parameters |> List.mapi (fun index _ -> "@p" + string index)
            List.zip variables execution.Parameters
            |> List.iter (fun (variable, value) -> execute ("SET " + variable + "=" + sqlValue value))
            command.CommandText <- "EXECUTE probe USING " + String.concat "," variables

        use reader = command.ExecuteReader()
        if not (reader.Read()) then failwithf "%A %s returned no row" protocol sql
        let actual =
            [ for column in 0 .. reader.FieldCount - 1 ->
                reader.GetDataTypeName(column) + ":" + renderValue(reader.GetValue(column)) ]
        if actual <> execution.Expected then
            failwithf "%A %s execution %d (%A): expected %A; got %A"
                protocol sql (index + 1) execution.Parameters execution.Expected actual
        if reader.Read() then failwithf "%A %s returned an unexpected second row" protocol sql
        printfn "%A | %s | %A -> %s" protocol sql execution.Parameters (String.concat " | " actual))

    if protocol = Sql then execute "DEALLOCATE PREPARE probe"

let execution parameters expected = { Parameters = parameters; Expected = expected }
let repeated value expected = execution [value; value; value] expected

let histories =
    [ "SELECT ?, ABS(?), ? + 1",
      [ repeated (Integer -2L) ["BIGINT:-2"; "BIGINT:2"; "BIGINT:-1"]
        repeated (Text "-3") ["DECIMAL:-3"; "DECIMAL:3"; "DECIMAL:-2"]
        repeated Null ["DECIMAL:NULL"; "DECIMAL:NULL"; "DECIMAL:NULL"]
        repeated (Exact 1.25M) ["DECIMAL:1.25"; "DECIMAL:1.25"; "DECIMAL:2.25"]
        repeated (Integer -4L) ["DECIMAL:-4"; "DECIMAL:4"; "DECIMAL:-3"]
        repeated (Approximate 1.5) ["DOUBLE:1.5"; "DOUBLE:1.5"; "DOUBLE:2.5"]
        repeated (Text "oops") ["DOUBLE:0"; "DOUBLE:0"; "DOUBLE:1"]
        repeated (Integer -5L) ["DOUBLE:-5"; "DOUBLE:5"; "DOUBLE:-4"] ]

      "SELECT ABS(?)",
      [ execution [Integer -2L] ["DOUBLE:2"]
        execution [Exact 1.25M] ["DOUBLE:1.25"]
        execution [Null] ["DOUBLE:NULL"]
        execution [Text "oops"] ["DOUBLE:0"] ]

      "SELECT ?, ABS(?)",
      [ execution [Integer -2L; Integer -2L] ["BIGINT:-2"; "BIGINT:2"]
        execution [Exact 1.25M; Integer -3L] ["DECIMAL:1.25"; "BIGINT:3"]
        execution [Integer -4L; Integer -4L] ["DECIMAL:-4"; "BIGINT:4"]
        execution [Approximate 1.5; Integer -5L] ["DOUBLE:1.5"; "BIGINT:5"] ]

      "SELECT ?, ABS(?)",
      [ execution [Integer -2L; Integer -2L] ["BIGINT:-2"; "BIGINT:2"]
        execution [Null; Exact 1.25M] ["VARCHAR:NULL"; "DECIMAL:1.25"]
        execution [Integer -4L; Integer -4L] ["BIGINT:-4"; "BIGINT:4"] ]

      "SELECT ?, ABS(?)",
      [ execution [Null; Integer -2L] ["VARCHAR:NULL"; "DOUBLE:2"]
        execution [Integer -3L; Null] ["BIGINT:-3"; "DOUBLE:NULL"]
        execution [Integer -4L; Integer -4L] ["BIGINT:-4"; "DOUBLE:4"] ]

      "SELECT ?, ABS(?)",
      [ execution [Integer -2L; Integer -2L] ["BIGINT:-2"; "BIGINT:2"]
        execution [Text "abc"; Integer -3L] ["BIGINT:0"; "BIGINT:3"]
        execution [Integer -4L; Text "-4.5"] ["BIGINT:-4"; "DECIMAL:4.5"]
        execution [Integer -6L; Integer -6L] ["BIGINT:-6"; "DECIMAL:6"] ] ]

for protocol in [Binary; Sql] do
    for sql, executions in histories do
        run protocol sql executions

let runSetHistory sql expected =
    use connection = new MySqlConnection(connectionString)
    connection.Open()
    if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion

    let execute sql =
        use command = new MySqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore

    execute "SET @v=2,@x=9"
    execute ("PREPARE probe FROM " + quote sql)
    List.zip [Exact 1.75M; Text "abc"; Null; Integer 3L] expected
    |> List.iter (fun (value, expectedRow) ->
        execute ("SET @v=" + sqlValue value)
        execute "EXECUTE probe"
        // Explicit LIMIT keeps a captured NULL's zero sql_select_limit observable.
        use command = new MySqlCommand("SELECT @x,@@session.sql_select_limit LIMIT 1", connection)
        use reader = command.ExecuteReader()
        if not (reader.Read()) then failwithf "%s returned no inspection row" sql
        let actual =
            [ for column in 0 .. reader.FieldCount - 1 ->
                reader.GetDataTypeName(column) + ":" + renderValue(reader.GetValue(column)) ]
        if actual <> expectedRow then
            failwithf "%s after %A: expected %A; got %A" sql value expectedRow actual
        printfn "SQL SET | %s | %A -> %s" sql value (String.concat " | " actual))
    execute "DEALLOCATE PREPARE probe"

runSetHistory "SET @x=@v, SESSION sql_select_limit=100"
    [ ["BIGINT:2"; "BIGINT:100"]
      ["BIGINT:0"; "BIGINT:100"]
      ["BIGINT:NULL"; "BIGINT:100"]
      ["BIGINT:3"; "BIGINT:100"] ]

runSetHistory "SET SESSION sql_select_limit=@v"
    [ ["BIGINT:9"; "BIGINT:2"]
      ["BIGINT:9"; "BIGINT:0"]
      ["BIGINT:9"; "BIGINT:0"]
      ["BIGINT:9"; "BIGINT:3"] ]

runSetHistory "SET SESSION sql_select_limit=@v, @x=@v"
    [ ["BIGINT:2"; "BIGINT:2"]
      ["BIGINT:0"; "BIGINT:0"]
      ["BIGINT:NULL"; "BIGINT:0"]
      ["BIGINT:3"; "BIGINT:3"] ]

type DecimalColumn =
    { Name: string
      Expression: string
      Precision: int
      Scale: int }

let decimalColumn name expression precision scale =
    { Name = name; Expression = expression; Precision = precision; Scale = scale }

let decimalColumns =
    [ decimalColumn "direct" "@v" 65 30
      decimalColumn "addition" "@v+1" 66 30
      decimalColumn "subtraction" "@v-1" 66 30
      decimalColumn "multiplication" "@v*2" 65 30
      decimalColumn "division" "@v/2" 65 30
      decimalColumn "thirds" "@v/3" 65 30
      decimalColumn "modulo" "@v%1" 65 30
      decimalColumn "absolute_value" "ABS(@v)" 65 30
      decimalColumn "negative_value" "-@v" 65 30
      decimalColumn "rounded" "ROUND(@v,2)" 38 2
      decimalColumn "truncated" "TRUNCATE(@v,2)" 37 2
      decimalColumn "coalesced" "COALESCE(@v,0)" 65 30
      decimalColumn "conditional" "CASE WHEN 1 THEN @v ELSE 0 END" 65 30 ]

let fixedDecimal scale (value: decimal) = value.ToString("F" + string scale, invariant)
let decimal30 = fixedDecimal 30

let decimalHistories =
    [ "1.25",
      [ "1.25"; decimal30 2.25M; decimal30 0.25M; decimal30 2.5M
        decimal30 0.625M; decimal30 0.416666666M; decimal30 0.25M
        decimal30 1.25M; decimal30 -1.25M; "1.25"; "1.25"
        decimal30 1.25M; "1.25" ]
      "2",
      [ "2"; decimal30 3M; decimal30 1M; decimal30 4M
        decimal30 1M; decimal30 0.666666666M; decimal30 0M
        decimal30 2M; decimal30 -2M; "2.00"; "2.00"; decimal30 2M; "2" ]
      "NULL",
      [ "NULL"; "NULL"; "NULL"; "NULL"; "NULL"; "NULL"; "NULL"
        "NULL"; "NULL"; "NULL"; "NULL"; decimal30 0M; "NULL" ]
      "1.2345",
      [ "1.2345"; decimal30 2.2345M; decimal30 0.2345M; decimal30 2.469M
        decimal30 0.61725M; decimal30 0.4115M; decimal30 0.2345M
        decimal30 1.2345M; decimal30 -1.2345M; "1.23"; "1.23"
        decimal30 1.2345M; "1.2345" ] ]

let runDecimalExpressions () =
    use connection = new MySqlConnection(connectionString)
    connection.Open()
    if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion

    let execute sql =
        use command = new MySqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore

    let inspect label sql expected =
        use command = new MySqlCommand(sql, connection)
        use reader = command.ExecuteReader()
        let schema = reader.GetColumnSchema()
        if reader.FieldCount <> decimalColumns.Length || not (reader.Read()) then
            failwithf "%s returned an unexpected decimal result shape" label
        List.zip decimalColumns expected
        |> List.iteri (fun index (column, expectedValue) ->
            // System.Decimal normalization would hide scale and precision differences.
            let actual = if reader.IsDBNull index then "NULL" else string (reader.GetMySqlDecimal index)
            let metadata = schema[index]
            if reader.GetDataTypeName(index) <> "DECIMAL"
               || metadata.NumericScale <> Nullable column.Scale
               || metadata.NumericPrecision <> Nullable column.Precision
               || actual <> expectedValue then
                failwithf "%s %s: expected DECIMAL(%d,%d) %s; got %s(%O,%O) %s"
                    label column.Name column.Precision column.Scale expectedValue
                    (reader.GetDataTypeName index) metadata.NumericPrecision metadata.NumericScale actual
            printfn "Decimal | %s | %s | DECIMAL(%d,%d) -> %s"
                label column.Name column.Precision column.Scale actual)
        if reader.Read() then failwithf "%s returned an unexpected second row" label

    let sql =
        decimalColumns
        |> List.map (fun column -> column.Expression + " AS " + column.Name)
        |> String.concat ", "
        |> (+) "SELECT "
    execute "SET @v=1.25"
    inspect "ordinary" sql (decimalHistories |> List.head |> snd)
    execute ("PREPARE decimal_probe FROM " + quote sql)
    for value, expected in decimalHistories do
        execute ("SET @v=" + value)
        inspect ("prepared after " + value) "EXECUTE decimal_probe" expected
    execute "DEALLOCATE PREPARE decimal_probe"

runDecimalExpressions ()

let runDivisionIncrement () =
    use connection = new MySqlConnection(connectionString)
    connection.Open()
    if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion

    let execute sql =
        use command = new MySqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore

    let inspect label (command: MySqlCommand) expected =
        use reader = command.ExecuteReader()
        let schema = reader.GetColumnSchema()
        if reader.FieldCount <> List.length expected || not (reader.Read()) then
            failwithf "%s returned an unexpected result shape" label
        expected |> List.iteri (fun index (precision, scale, value) ->
            let actual = string (reader.GetMySqlDecimal index)
            if reader.GetDataTypeName(index) <> "DECIMAL"
               || schema[index].NumericPrecision <> Nullable precision
               || schema[index].NumericScale <> Nullable scale
               || actual <> value then
                failwithf "%s column %d: expected DECIMAL(%d,%d) %s; got %s(%O,%O) %s"
                    label index precision scale value (reader.GetDataTypeName index)
                    schema[index].NumericPrecision schema[index].NumericScale actual)
        if reader.Read() then failwithf "%s returned an unexpected second row" label
        printfn "Division increment | %s -> %A" label expected

    let query label sql expected =
        use command = new MySqlCommand(sql, connection)
        inspect label command expected

    let sql = "SELECT 1/3 AS a,10.00/3 AS b,(1/3)*3 AS c"
    let defaultResults = [ 5, 4, "0.3333"; 8, 6, "3.333333"; 6, 4, "1.0000" ]
    let histories =
        [ 0, [ 1, 0, "0"; 4, 2, "3.33"; 2, 0, "0" ], (19, 0, "1")
          1, [ 2, 1, "0.3"; 5, 3, "3.333"; 3, 1, "1.0" ], (20, 1, "1.7")
          4, defaultResults, (23, 4, "1.6667")
          9, [ 10, 9, "0.333333333"; 13, 11, "3.33333333333"; 11, 9, "0.999999999" ], (28, 9, "1.666666666")
          30, [ 31, 30, "0.333333333333333333333333333333"
                34, 30, "3.333333333333333333333333333333"
                32, 30, "1.000000000000000000000000000000" ], (49, 30, "1.666666666666666666666666666667") ]
    execute "SET div_precision_increment=4"
    execute ("PREPARE increment_probe FROM " + quote sql)
    // A distinct text prevents connector caching from preparing the ordinary-query probe.
    use binary = new MySqlCommand(sql + " /* retained increment */", connection)
    binary.Prepare()
    for increment, expected, average in histories do
        execute (sprintf "SET div_precision_increment=%d" increment)
        query (sprintf "ordinary at %d" increment) sql expected
        query (sprintf "SQL prepared at 4, executed at %d" increment) "EXECUTE increment_probe" defaultResults
        inspect (sprintf "binary prepared at 4, executed at %d" increment) binary defaultResults
        query (sprintf "AVG at %d" increment)
            "SELECT AVG(n) FROM (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 2) t" [ average ]
    execute "DEALLOCATE PREPARE increment_probe"

    for assigned, retained in [ -1, 0; 31, 30 ] do
        execute (sprintf "SET div_precision_increment=%d" assigned)
        use warnings = new MySqlCommand("SHOW WARNINGS", connection)
        use reader = warnings.ExecuteReader()
        let expectedMessage = sprintf "Truncated incorrect div_precision_increment value: '%d'" assigned
        if not (reader.Read()) || reader.GetString(0) <> "Warning"
           || reader.GetInt32(1) <> 1292 || reader.GetString(2) <> expectedMessage then
            failwithf "Expected warning 1292 when assigning %d" assigned
        if reader.Read() then failwith "Unexpected additional assignment warning"
        reader.Close()
        use value = new MySqlCommand("SELECT @@div_precision_increment", connection)
        if Convert.ToInt32(value.ExecuteScalar(), invariant) <> retained then
            failwithf "Expected %d to clamp to %d" assigned retained
        printfn "Division increment | %d clamps to %d with warning 1292" assigned retained

    for assigned in [ "1.5"; "'2'"; "NULL" ] do
        try
            execute ("SET div_precision_increment=" + assigned)
            failwithf "Expected assignment %s to fail" assigned
        with :? MySqlException as error when error.Number = 1232 && error.SqlState = "42000" ->
            printfn "Division increment | assignment %s -> 1232/42000" assigned

    let database = "fsdb_division_oracle_" + Guid.NewGuid().ToString("N")
    execute ("CREATE DATABASE " + database)
    try
        execute ("USE " + database)
        execute "CREATE TABLE source(n INT)"
        execute "INSERT INTO source VALUES (1)"
        execute "SET div_precision_increment=4"
        execute "PREPARE schema_increment FROM 'SELECT n/3 FROM source'"
        execute "SET div_precision_increment=1"
        query "before schema reprepare" "EXECUTE schema_increment" [ 14, 4, "0.3333" ]
        execute "ALTER TABLE source ADD COLUMN extra INT"
        query "after schema reprepare" "EXECUTE schema_increment" [ 11, 1, "0.3" ]
        execute "DEALLOCATE PREPARE schema_increment"
    finally
        execute ("DROP DATABASE " + database)

runDivisionIncrement ()

let runDecimalDescriptors () =
    let columns =
        [ "1.25", 5, 2; "0.00", 5, 2; "1.25+1", 6, 2; "1.25*2", 6, 2
          "1.25/3.00", 11, 6; "@v+1", 68, 30; "@v*2", 67, 30
          "-@v", 67, 30; "0-@v", 68, 30
          "ROUND(@v,2)", 40, 2; "TRUNCATE(@v,2)", 39, 2; "123.45%6.7", 7, 2
          "COALESCE(1.25,123)", 7, 2; "(@v+@v)+1", 68, 30
          "ROUND(123.45,-1)", 5, 0; "TRUNCATE(123.45,-1)", 4, 0
          "SUM(1.25)", 27, 2; "AVG(1.25)", 9, 6
          "AVG(DISTINCT 1)", 7, 4; "SUM(DISTINCT 1)", 24, 0
          "SUM(b'')", 24, 0; "SUM(b'100000001')", 28, 0
          "AVG(b'100000001')", 11, 4; "SUM(X'010001')", 31, 0
          "CAST(1 AS DECIMAL(65,0))+CAST(1 AS DECIMAL(65,30))", 98, 30
          "COALESCE(CAST(1 AS DECIMAL(40,0)),CAST(1 AS DECIMAL(40,30)))", 67, 30
          "MOD(CAST(1 AS DECIMAL(40,0)),CAST(1 AS DECIMAL(40,30)))", 42, 30 ]
    for protocol in [ Sql; Binary ] do
        // The connector treats @names as binary bindings; CAST supplies the same DECIMAL(65,30) input shape.
        let columns =
            if protocol = Binary then
                columns |> List.map (fun (expression, length, scale) -> expression.Replace("@v", "CAST(1.25 AS DECIMAL(65,30))"), length, scale)
            else columns
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
            failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion
        use setup = new MySqlCommand("SET @v=1.25", connection)
        setup.ExecuteNonQuery() |> ignore
        let sql = columns |> List.mapi (fun index (expression, _, _) -> sprintf "%s AS d%d" expression index) |> String.concat ", " |> (+) "SELECT "
        use command = new MySqlCommand(sql, connection)
        if protocol = Binary then command.Prepare()
        use reader = command.ExecuteReader()
        let schema = reader.GetColumnSchema()
        if reader.FieldCount <> columns.Length || not (reader.Read()) then
            failwith "Unexpected decimal descriptor result shape"
        columns |> List.iteri (fun index (expression, length, scale) ->
            let precision = length - 1 - (if scale > 0 then 1 else 0)
            if reader.GetDataTypeName(index) <> "DECIMAL"
               || schema[index].NumericPrecision <> Nullable precision
               || schema[index].NumericScale <> Nullable scale then
                failwithf "%A %s: expected DECIMAL(%d,%d), got %s(%O,%O)"
                    protocol expression precision scale (reader.GetDataTypeName index)
                    schema[index].NumericPrecision schema[index].NumericScale
            printfn "Decimal descriptor | %A | %s -> DECIMAL(%d,%d)" protocol expression precision scale)
        if reader.Read() then failwith "Unexpected second descriptor row"

runDecimalDescriptors ()

let runApproximateAggregateDescriptors () =
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
            failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion
        for increment in [ 0; 4; 10 ] do
            use setup = new MySqlCommand(sprintf "SET div_precision_increment=%d" increment, connection)
            setup.ExecuteNonQuery() |> ignore
            for argument in [ "NULL"; "DISTINCT NULL"; "NULL+NULL"; "'1.25'"; "DISTINCT '1.25'"; "1.25e0"; "CAST(NULL AS CHAR(10))" ] do
                for aggregate in [ "SUM"; "AVG" ] do
                    let expression = sprintf "%s(%s)" aggregate argument
                    let untypedNull = argument = "NULL" || argument = "DISTINCT NULL" || argument = "NULL+NULL"
                    let scale = if untypedNull then (if aggregate = "AVG" then increment else 0) else 31
                    let length = if untypedNull then 17 + scale else 23
                    use command = new MySqlCommand(sprintf "SELECT %s AS value /* increment=%d */" expression increment, connection)
                    if protocol = Binary then command.Prepare()
                    use reader = command.ExecuteReader()
                    let schema = reader.GetColumnSchema()[0]
                    if not (reader.Read())
                       || reader.GetDataTypeName(0) <> "DOUBLE"
                       || schema.ColumnSize <> Nullable length
                       || schema.NumericScale <> Nullable scale then
                        failwithf "%A %s increment=%d: expected DOUBLE length=%d scale=%d; got %s length=%O scale=%O"
                            protocol expression increment length scale (reader.GetDataTypeName 0) schema.ColumnSize schema.NumericScale
                    let expected = if argument.Contains("NULL") then "NULL" else "1.25"
                    if renderValue(reader.GetValue 0) <> expected then
                        failwithf "%s: expected %s; got %O" expression expected (reader.GetValue 0)
                    printfn "Approximate aggregate | %A | %s | increment=%d -> length=%d scale=%d" protocol expression increment length scale

runApproximateAggregateDescriptors ()

let runBinaryLiteralContexts () =
    let cases =
        [ "b'01'", "BLOB", "0x01"
          "0b01", "BLOB", "0x01"
          "b''", "BLOB", "0x"
          "b'000000001'", "BLOB", "0x0001"
          "b'01'+0", "BIGINT", "1"
          "b'01'*2", "BIGINT", "2"
          "-b'01'", "DOUBLE", "-1"
          "ABS(b'01')", "DOUBLE", "1"
          "CAST(b'01' AS UNSIGNED)", "BIGINT", "1"
          "CAST(b'01' AS DECIMAL)", "DECIMAL", "1"
          "HEX(b'01')", "VARCHAR", "01"
          "CONCAT(b'01')", "BLOB", "0x01"
          "SUM(b'01')", "DECIMAL", "1"
          "AVG(b'01')", "DECIMAL", "1"
          "SUM(DISTINCT b'01')", "DECIMAL", "1"
          "BIT_COUNT(b'01')", "BIGINT", "1"
          "b'01'=1", "BIGINT", "1"
          "b'01'='1'", "BIGINT", "0"
          "COALESCE(b'01',b'10')+0", "DOUBLE", "0"
          "IFNULL(b'01',b'10')+0", "DOUBLE", "0"
          "IF(1,b'01',b'10')+0", "DOUBLE", "1"
          "IF(0,b'01',b'10')+0", "DOUBLE", "2"
          "CASE WHEN 1 THEN b'01' ELSE b'10' END+0", "DOUBLE", "1"
          "CASE WHEN 0 THEN b'01' ELSE b'10' END+0", "DOUBLE", "2"
          "CONCAT(b'01')+0", "DOUBLE", "0"
          "SUM(IF(1,b'01',b'10'))", "DOUBLE", "1"
          "SUM(COALESCE(b'01',b'10'))", "DOUBLE", "0"
          "0x01", "BLOB", "0x01"
          "0x01+0", "BIGINT", "1"
          "X'01'+0", "BIGINT", "1"
          "_binary b'01'", "BLOB", "0x01"
          "_binary B''", "BLOB", "0x"
          "_binary 0b000000001", "BLOB", "0x0001"
          "_binary 0xabc", "BLOB", "0x0ABC"
          "_binary b'01'+0", "DOUBLE", "0"
          "SUM(_binary b'01')", "DOUBLE", "0"
          "_binary X'01'+0", "DOUBLE", "0"
          "SUM(X'01')", "DECIMAL", "1"
          "SUM(_binary X'01')", "DOUBLE", "0"
          "(SELECT b'01')+0", "BIGINT", "1"
          "(SELECT b'01' WHERE 1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE 0)+0", "DOUBLE", "NULL"
          "(SELECT b'01' WHERE NULL)+0", "DOUBLE", "NULL"
          "(SELECT b'01' WHERE 1=1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE ABS(-1)=1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE 'a'='A')+0", "BIGINT", "1"
          "(SELECT b'01' WHERE COALESCE(NULL,1))+0", "BIGINT", "1"
          "(SELECT b'01' WHERE CEIL(0.1)=1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE CEILING(0.1)=1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE FLOOR(1.9)=1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE SQRT(4)=2)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE POWER(2,3)=8)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE POW(2,3)=8)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE SIGN(-2)=-1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE GREATEST(1,2)=2)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE LEAST(1,2)=1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE NULLIF(1,2)=1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE SIN(0)=0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE COS(0)=1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE TAN(0)=0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE COT(1)>0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE ASIN(0)=0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE ACOS(1)=0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE ATAN(0)=0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE ATAN2(0,1)=0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE PI()>3)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE EXP(0)=1)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE LN(1)=0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE LOG(1)=0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE LOG2(8)=3)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE LOG10(100)=2)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE DEGREES(0)=0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE RADIANS(0)=0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE BIT_COUNT(3)=2)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE CRC32('a')>0)+0", "BIGINT", "1"
          "(SELECT b'01' WHERE HEX('a')='61')+0", "BIGINT", "1"
          "(SELECT b'01' WHERE REVERSE('ab')='ba')+0", "BIGINT", "1"
          "(SELECT b'01' WHERE TRIM(' a ')='a')+0", "BIGINT", "1"
          "(SELECT b'01' WHERE RAND()>=0)+0", "DOUBLE", "0"
          "(SELECT b'01' WHERE EXISTS(SELECT 1))+0", "DOUBLE", "0"
          "(SELECT b'01' LIMIT 0)+0", "BIGINT", "1"
          "(SELECT b'01' LIMIT 1 OFFSET 10)+0", "BIGINT", "1"
          "(SELECT b'01' GROUP BY 1 LIMIT 0)+0", "BIGINT", "1"
          "SUM((SELECT b'01' GROUP BY 1 LIMIT 0))", "DECIMAL", "1"
          "(SELECT b'01' GROUP BY 1 WITH ROLLUP LIMIT 1)+0", "DOUBLE", "0"
          "(SELECT b'01' GROUP BY 1 WITH ROLLUP LIMIT 0)+0", "DOUBLE", "NULL"
          "(SELECT MIN(b'01') LIMIT 0)+0", "DOUBLE", "NULL"
          "(SELECT FIRST_VALUE(b'01') OVER () LIMIT 0)+0", "DOUBLE", "NULL"
          "(SELECT b'01' HAVING 1 LIMIT 0)+0", "DOUBLE", "NULL"
          "(SELECT b'01' FROM (SELECT 1)t LIMIT 0)+0", "DOUBLE", "NULL"
          "ROW(1,1)=(SELECT b'01',b'01')", "BIGINT", "0"
          "ROW(1,1)=(SELECT b'01',b'01' FROM (SELECT 1)t)", "BIGINT", "0"
          "ROW(1,1)=(SELECT b'01',b'01' LIMIT 0)", "BIGINT", "NULL"
          "(SELECT (SELECT b'01'))+0", "BIGINT", "1"
          "SUM((SELECT b'01'))", "DECIMAL", "1"
          "AVG((SELECT b'01'))", "DECIMAL", "1"
          "(SELECT 1.25)+0", "DECIMAL", "1.25"
          "(SELECT b'01' FROM (SELECT 1) t)+0", "DOUBLE", "0"
          "(SELECT b'01' HAVING 1)+0", "DOUBLE", "0"
          "(SELECT MIN(b'01'))+0", "DOUBLE", "0"
          "(SELECT FIRST_VALUE(b'01') OVER ())+0", "DOUBLE", "0"
          "(SELECT b'01' FROM (SELECT 1)t)=1", "BIGINT", "0"
          "1 IN (SELECT b'01' FROM (SELECT 1)t)", "BIGINT", "1"
          "1=ANY(SELECT b'01' FROM (SELECT 1)t)", "BIGINT", "1"
          "(SELECT SUM(v) FROM (SELECT b'01' AS v) t)", "DOUBLE", "0"
          "(SELECT v+0 FROM (SELECT b'01' AS v) t)", "DOUBLE", "0"
          "SUM(b'1111111111111111111111111111111111111111111111111111111111111111')", "DECIMAL", "18446744073709551615"
          "CAST(b'1111111111111111111111111111111111111111111111111111111111111111' AS UNSIGNED)", "BIGINT", "18446744073709551615"
          "CAST(b'1111111111111111111111111111111111111111111111111111111111111111' AS SIGNED)", "BIGINT", "-1"
          "b'1111111111111111111111111111111111111111111111111111111111111111'+0", "BIGINT", "-1"
          "b'10000000000000000000000000000000000000000000000000000000000000000'+0", "BIGINT", "0"
          "SUM(b'10000000000000000000000000000000000000000000000000000000000000000')", "DECIMAL", "0" ]
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
            failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion
        for expression, expectedType, expectedValue in cases do
            use command = new MySqlCommand("SELECT " + expression + " AS value", connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            if not (reader.Read()) then failwithf "%s returned no row" expression
            let actual =
                match reader.GetValue 0 with
                | :? (byte[]) as bytes -> "0x" + Convert.ToHexString bytes
                | value -> renderValue value
            if reader.GetDataTypeName(0) <> expectedType || actual <> expectedValue then
                failwithf "%A %s: expected %s:%s; got %s:%s"
                    protocol expression expectedType expectedValue (reader.GetDataTypeName 0) actual
            printfn "Binary literal | %A | %s -> %s:%s" protocol expression expectedType actual

        if protocol = Sql then
            for sql in [ "SET @literal_bytes=b'01'"; "PREPARE literal_bytes FROM 'SELECT ?+0,SUM(?)'" ] do
                use command = new MySqlCommand(sql, connection)
                command.ExecuteNonQuery() |> ignore
            for sql, expected in
                [ "SELECT @literal_bytes+0,SUM(@literal_bytes)", [ "DOUBLE:0"; "DOUBLE:0" ]
                  "EXECUTE literal_bytes USING @literal_bytes,@literal_bytes", [ "BIGINT:0"; "DOUBLE:0" ] ] do
                use command = new MySqlCommand(sql, connection)
                use reader = command.ExecuteReader()
                if not (reader.Read()) then failwithf "%s returned no row" sql
                let actual = [ for index in 0 .. reader.FieldCount - 1 -> reader.GetDataTypeName(index) + ":" + renderValue(reader.GetValue index) ]
                if actual <> expected then failwithf "%s: expected %A; got %A" sql expected actual
                printfn "Binary literal binding | %s -> %A" sql actual
            use close = new MySqlCommand("DEALLOCATE PREPARE literal_bytes", connection)
            close.ExecuteNonQuery() |> ignore

runBinaryLiteralContexts ()

let runConditionalScalarBindings () =
    use connection = new MySqlConnection(connectionString)
    connection.Open()
    if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
        failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion
    let execute sql =
        use command = new MySqlCommand(sql, connection)
        command.ExecuteNonQuery() |> ignore
    let check label expectedType expectedValue (command: MySqlCommand) =
        use reader = command.ExecuteReader()
        if not (reader.Read()) then failwithf "%s returned no row" label
        let actualType = reader.GetDataTypeName 0
        let actualValue = renderValue(reader.GetValue 0)
        if actualType <> expectedType || actualValue <> expectedValue then
            failwithf "%s: expected %s:%s; got %s:%s" label expectedType expectedValue actualType actualValue
        printfn "Conditional scalar | %s -> %s:%s" label actualType actualValue

    execute "SET @scalar_condition=1"
    execute "PREPARE scalar_condition FROM 'SELECT (SELECT b''01'' WHERE @scalar_condition)+0 AS value'"
    execute "PREPARE scalar_disjunction FROM 'SELECT (SELECT b''01'' WHERE 1 OR @scalar_condition)+0 AS value'"
    try
        for value in [ 1; 0; 1 ] do
            execute (sprintf "SET @scalar_condition=%d" value)
            for sql, family, expected in
                [ "SELECT (SELECT b'01' WHERE @scalar_condition)+0 AS value", "DOUBLE", (if value = 0 then "NULL" else "0")
                  "EXECUTE scalar_condition", "DOUBLE", (if value = 0 then "NULL" else "0")
                  "SELECT (SELECT b'01' WHERE 1 OR @scalar_condition)+0 AS value", "BIGINT", "1"
                  "EXECUTE scalar_disjunction", "BIGINT", "1" ] do
                use command = new MySqlCommand(sql, connection)
                check (sprintf "%s [condition=%d]" sql value) family expected command
    finally
        execute "DEALLOCATE PREPARE scalar_condition"
        execute "DEALLOCATE PREPARE scalar_disjunction"

    use bound = new MySqlCommand("SELECT (SELECT b'01' WHERE @condition)+0 AS value", connection)
    let parameter = bound.Parameters.AddWithValue("@condition", 0)
    bound.Prepare()
    for value in [ 0; 1; 0; 1 ] do
        parameter.Value <- box value
        check (sprintf "binary parameter=%d" value) "DOUBLE" (if value = 0 then "NULL" else "0") bound

    let shapes =
        [ "NOT (0 AND @condition)", "BIGINT", "1"
          "NOT NOT (1 OR @condition)", "BIGINT", "1"
          "IF(1 OR @condition,1,0)", "BIGINT", "1"
          "IF(0 AND @condition,0,1)", "BIGINT", "1"
          "IF(NULL AND @condition,0,1)", "BIGINT", "1"
          "IF(1,1,@condition)", "DOUBLE", "0"
          "(1 OR @condition)=1", "DOUBLE", "0"
          "(1 OR @condition) IS TRUE", "DOUBLE", "0"
          "CAST(1 OR @condition AS SIGNED)", "DOUBLE", "0"
          "COALESCE(1 OR @condition,0)", "DOUBLE", "0"
          "CASE WHEN 1 OR @condition THEN 1 ELSE 0 END", "DOUBLE", "0"
          "(1 OR @condition)+0", "DOUBLE", "0" ]
    for predicate, family, expected in shapes do
        use parameterized = new MySqlCommand("SELECT (SELECT b'01' WHERE " + predicate + ")+0 AS value", connection)
        let parameter = parameterized.Parameters.AddWithValue("@condition", 0)
        parameterized.Prepare()
        for value in [ 0; 1; 0 ] do
            parameter.Value <- box value
            check (sprintf "%s [binary=%d]" predicate value) family expected parameterized
            execute (sprintf "SET @scalar_condition=%d" value)
            use direct = new MySqlCommand("SELECT (SELECT b'01' WHERE " + predicate.Replace("@condition", "@scalar_condition") + ")+0 AS value", connection)
            check (sprintf "%s [variable=%d]" predicate value) family expected direct

runConditionalScalarBindings ()

let runDivisionOperandDescriptors () =
    let cases =
        [ "b'01'/2", "DECIMAL", 9, Some 7, 4, "0.5"
          "2/b'01'", "DECIMAL", 7, Some 5, 4, "2"
          "b''/2", "DECIMAL", 7, Some 5, 4, "0"
          "b'100000001'/2", "DECIMAL", 11, Some 9, 4, "128.5"
          "X'010001'/2", "DECIMAL", 14, Some 12, 4, "32768.5"
          "b'01'/2.00", "DECIMAL", 11, Some 9, 4, "0.5"
          "b'01'/2e0", "DOUBLE", 23, None, 31, "0.5"
          "b'01'/'2'", "DOUBLE", 23, None, 31, "0.5"
          "'1'/2", "DOUBLE", 23, None, 31, "0.5"
          "1/'2'", "DOUBLE", 23, None, 31, "0.5"
          "_binary X'31'/2", "DOUBLE", 23, None, 31, "0.5"
          "_binary b'01'/2", "DOUBLE", 23, None, 31, "0"
          "NULL/2", "DOUBLE", 4, None, 4, "NULL"
          "1/NULL", "DOUBLE", 6, None, 4, "NULL"
          "NULL/NULL", "DOUBLE", 4, None, 4, "NULL"
          "b'01'/NULL", "DOUBLE", 5, None, 4, "NULL"
          "NULL/b'01'", "DOUBLE", 4, None, 4, "NULL"
          "(SELECT b'01')/2", "DECIMAL", 9, Some 7, 4, "0.5"
          "(SELECT b'01' WHERE 1)/2", "DECIMAL", 9, Some 7, 4, "0.5"
          "(SELECT b'01' FROM (SELECT 1)t)/2", "DOUBLE", 5, None, 4, "0"
          "CAST('2020-01-01' AS DATE)/2", "DECIMAL", 14, Some 12, 4, "10100050.5"
          "CAST('2020-01-02 03:04:05' AS DATETIME)/2", "DECIMAL", 20, Some 18, 4, "10100051015202.5"
          "CAST('2020-01-02 03:04:05.123456' AS DATETIME(6))/2", "DECIMAL", 26, Some 24, 10, "10100051015202.561728"
          "CAST('-12:34:56.123456' AS TIME(6))/2", "DECIMAL", 19, Some 17, 10, "-61728.061728"
          "CAST('2020-00-01' AS DATE)/2", "DECIMAL", 14, Some 12, 4, "10100000.5"
          "CAST('2020-00-01 03:04:05.123456' AS DATETIME(6))/2", "DECIMAL", 26, Some 24, 10, "10100000515202.561728"
          "CAST('1' AS JSON)/2", "DOUBLE", 23, None, 31, "0.5" ]
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
            failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion
        use setup = new MySqlCommand("SET div_precision_increment=4,sql_mode=''", connection)
        setup.ExecuteNonQuery() |> ignore
        for expression, family, length, precision, scale, expected in cases do
            use command = new MySqlCommand("SELECT " + expression + " AS value", connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            let metadata = reader.GetColumnSchema()[0]
            if not (reader.Read()) then failwithf "%s returned no row" expression
            let expectedPrecision = precision |> Option.map Nullable |> Option.defaultValue (Nullable())
            let actual = renderValue(reader.GetValue 0)
            if reader.GetDataTypeName(0) <> family
               || metadata.ColumnSize <> Nullable length
               || metadata.NumericPrecision <> expectedPrecision
               || metadata.NumericScale <> Nullable scale
               || actual <> expected then
                failwithf "%A %s: expected %s length=%d precision=%A scale=%d value=%s; got %s length=%O precision=%O scale=%O value=%s"
                    protocol expression family length precision scale expected (reader.GetDataTypeName 0)
                    metadata.ColumnSize metadata.NumericPrecision metadata.NumericScale actual
            printfn "Division operands | %A | %s -> %s length=%d precision=%A scale=%d value=%s"
                protocol expression family length precision scale actual

        // Distinct SQL prevents the connector cache from retaining another precision setting.
        for increment, expression, width, scale in
            [ 0, "NULL/2", 0, 0
              0, "1/NULL", 2, 0
              0, "b'01'/NULL", 1, 0
              0, "NULL/CAST('2020-01-01' AS DATETIME(6))", 6, 6
              30, "NULL/2", 30, 30
              30, "1/NULL", 32, 30
              30, "b'01'/NULL", 31, 30
              30, "NULL/CAST('2020-01-01' AS DATETIME(6))", 23, 31 ] do
            use setup = new MySqlCommand(sprintf "SET div_precision_increment=%d" increment, connection)
            setup.ExecuteNonQuery() |> ignore
            use command = new MySqlCommand(sprintf "SELECT %s AS value /* increment=%d */" expression increment, connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            let metadata = reader.GetColumnSchema()[0]
            if not (reader.Read()) || not (reader.IsDBNull 0)
               || reader.GetDataTypeName(0) <> "DOUBLE"
               || metadata.ColumnSize <> Nullable width || metadata.NumericScale <> Nullable scale then
                failwithf "%A %s increment=%d: expected NULL DOUBLE width=%d scale=%d; got %s width=%O scale=%O"
                    protocol expression increment width scale (reader.GetDataTypeName 0) metadata.ColumnSize metadata.NumericScale
            printfn "Division NULL scale | %A | %s | increment=%d -> width=%d scale=%d" protocol expression increment width scale

runDivisionOperandDescriptors ()

let runCalendarCasts () =
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        for mode, rejectZero, rejectPartial, allowInvalid in
            [ "", false, false, false
              "NO_ZERO_DATE", true, false, false
              "NO_ZERO_IN_DATE", false, true, false
              "NO_ZERO_DATE,NO_ZERO_IN_DATE", true, true, false
              "STRICT_TRANS_TABLES", false, false, false
              "ALLOW_INVALID_DATES", false, false, true ] do
            use setup = new MySqlCommand("SET sql_mode=" + quote mode, connection)
            setup.ExecuteNonQuery() |> ignore
            for source, expectedDate, rejected in
                [ "'2020-00-01'", "2020-00-01", rejectPartial
                  "'0000-00-00'", "0000-00-00", rejectZero
                  "'0000-01-01'", "0000-01-01", false
                  "'2023-02-31'", "2023-02-31", not allowInvalid
                  "'0000-02-29'", "0000-02-29", not allowInvalid
                  "'0000-02-31'", "0000-02-31", not allowInvalid
                  "'2020-13-01'", "", true
                  "'nonsense'", "", true
                  "'0'", "", true
                  "0", "0000-00-00", rejectZero
                  "20200101", "2020-01-01", false ] do
                for target in [ "DATE"; "DATETIME(6)" ] do
                    let sql = sprintf "SELECT CAST(CAST(%s AS %s) AS CHAR) AS value" source target
                    let actual =
                        use command = new MySqlCommand(sql, connection)
                        if protocol = Binary then command.Prepare()
                        use reader = command.ExecuteReader()
                        if not (reader.Read()) then failwithf "%s returned no row" sql
                        if reader.IsDBNull 0 then None else Some(reader.GetString 0)
                    let expected =
                        if rejected then None
                        else Some(expectedDate + (if target = "DATE" then "" else " 00:00:00.000000"))
                    use warnings = new MySqlCommand("SHOW WARNINGS", connection)
                    use reader = warnings.ExecuteReader()
                    let actualWarnings =
                        [ while reader.Read() do
                              yield reader.GetString 0, reader.GetInt32 1, reader.GetString 2 ]
                    let expectedWarnings =
                        if rejected then [ "Warning", 1292, sprintf "Incorrect datetime value: '%s'" (source.Trim('\'')) ]
                        else []
                    if actual <> expected || actualWarnings <> expectedWarnings then
                        failwithf "%A mode=%s %s: expected %A / %A; got %A / %A"
                            protocol mode sql expected expectedWarnings actual actualWarnings
                    printfn "Calendar casts | %A | mode=%s | %s -> %A / %A" protocol mode sql actual actualWarnings

runCalendarCasts ()

let runTemporalTextPrecision () =
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        use setup = new MySqlCommand("SET sql_mode=''", connection)
        setup.ExecuteNonQuery() |> ignore
        for expression, expected in
            [ "CAST(CAST('2020-00-01' AS DATETIME(6)) AS CHAR)", Some "2020-00-01 00:00:00.000000"
              "CONCAT(CAST('2020-01-01' AS DATETIME(3)))", Some "2020-01-01 00:00:00.000"
              "CAST(CAST('-12:34:56.12' AS TIME(4)) AS CHAR)", Some "-12:34:56.1200"
              "CAST(CAST(NULL AS DATETIME(6)) AS CHAR)", None
              "HEX(CAST(CAST('2020-01-01' AS DATETIME(3)) AS BINARY))", Some "323032302D30312D30312030303A30303A30302E303030" ] do
            use command = new MySqlCommand("SELECT " + expression + " AS value", connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            if not (reader.Read()) then failwithf "%s returned no row" expression
            let actual = if reader.IsDBNull 0 then None else Some(reader.GetString 0)
            if actual <> expected then
                failwithf "%A %s: expected %A; got %A" protocol expression expected actual
            printfn "Temporal text precision | %A | %s -> %A" protocol expression actual

runTemporalTextPrecision ()

let runComponentDateTimeFractions () =
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        for mode, truncate in
            [ "ALLOW_INVALID_DATES", false
              "ALLOW_INVALID_DATES,STRICT_TRANS_TABLES", false
              "ALLOW_INVALID_DATES,TIME_TRUNCATE_FRACTIONAL", true ] do
            use setup = new MySqlCommand("SET sql_mode=" + quote mode, connection)
            setup.ExecuteNonQuery() |> ignore
            for source, rounded, truncated in
                [ "2020-00-01 03:04:05.129", Some "2020-00-01 03:04:05.13", "2020-00-01 03:04:05.12"
                  "2020-00-01 03:04:05.999", None, "2020-00-01 03:04:05.99"
                  "0000-00-00 23:59:59.999", None, "0000-00-00 23:59:59.99"
                  "2023-02-31 03:04:05.129", Some "2023-02-31 03:04:05.13", "2023-02-31 03:04:05.12"
                  "2023-02-31 03:04:05.999", None, "2023-02-31 03:04:05.99"
                  "0000-02-29 03:04:05.129", Some "0000-02-29 03:04:05.13", "0000-02-29 03:04:05.12"
                  "0000-02-29 03:04:05.999", None, "0000-02-29 03:04:05.99"
                  "0000-01-01 03:04:05.999", Some "0000-00-00 03:04:06.00", "0000-01-01 03:04:05.99"
                  "0000-03-01 23:59:59.999", Some "0000-00-00 00:00:00.00", "0000-03-01 23:59:59.99"
                  "0000-12-31 23:59:59.999", Some "0001-01-01 00:00:00.00", "0000-12-31 23:59:59.99" ] do
                let sql = "SELECT CAST(CAST(" + quote source + " AS DATETIME(2)) AS CHAR) AS value"
                let actual =
                    use command = new MySqlCommand(sql, connection)
                    if protocol = Binary then command.Prepare()
                    use reader = command.ExecuteReader()
                    if not (reader.Read()) then failwithf "%s returned no row" sql
                    if reader.IsDBNull 0 then None else Some(reader.GetString 0)
                let expected = if truncate then Some truncated else rounded
                use warnings = new MySqlCommand("SHOW WARNINGS", connection)
                use reader = warnings.ExecuteReader()
                if actual <> expected || reader.Read() then
                    failwithf "%A mode=%s %s: expected %A without warnings; got %A" protocol mode sql expected actual
                printfn "Component datetime fractions | %A | mode=%s | %s -> %A" protocol mode sql actual

runComponentDateTimeFractions ()

let runTemporalArithmeticDescriptors () =
    let binaryCases =
        [ "CAST('2020-01-01' AS DATE)", "BIGINT", 10, 0, "20200101"
          "CAST('2020-01-01' AS DATETIME)", "BIGINT", 16, 0, "20200101000000"
          "CAST('2020-01-01' AS DATETIME(6))", "DECIMAL", 23, 6, "20200101000000"
          "CAST('2020-01-01 03:04:05.123456' AS DATETIME(6))", "DECIMAL", 23, 6, "20200101030405.123456"
          "CAST('-12:34:56' AS TIME(3))", "DECIMAL", 13, 3, "-123456"
          "TIMESTAMP '2020-01-01 00:00:00.000'", "DECIMAL", 20, 3, "20200101000000"
          "TIMESTAMP '2020-01-01 00:00:00.123'", "DECIMAL", 20, 3, "20200101000000.123" ]
    let cases =
        [ for operand, family, width, scale, expected in binaryCases do
              for suffix in [ "+0"; "-0"; "*1" ] do
                  yield operand + suffix, family, width, scale, expected
          yield "-TIMESTAMP '2020-01-01 00:00:00.123'", "DOUBLE", 20, 3, "-20200101000000.12"
          yield "-CAST('2020-01-01' AS DATE)", "DOUBLE", 17, 0, "-20200101"
          yield "-CAST('2020-01-01 03:04:05.123456' AS DATETIME(6))", "DOUBLE", 23, 6, "-20200101030405.125"
          yield "ABS(CAST('2020-01-01 03:04:05.123456' AS DATETIME(6)))", "DOUBLE", 23, 6, "20200101030405.125" ]
    let check label family width scale expected (command: MySqlCommand) =
        use reader = command.ExecuteReader()
        if not (reader.Read()) then failwithf "%s returned no row" label
        let schema = reader.GetColumnSchema()[0]
        let actual = renderValue (reader.GetValue 0)
        if reader.GetDataTypeName(0) <> family || schema.ColumnSize <> Nullable width
           || schema.NumericScale <> Nullable scale || actual <> expected then
            failwithf "%s: expected %s/%d/%d/%s; got %s/%O/%O/%s"
                label family width scale expected (reader.GetDataTypeName 0) schema.ColumnSize schema.NumericScale actual
        printfn "Temporal arithmetic | %s -> %s width=%d scale=%d value=%s" label family width scale actual
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        for expression, family, width, scale, expected in cases do
            use command = new MySqlCommand("SELECT " + expression + " AS value", connection)
            if protocol = Binary then command.Prepare()
            check (sprintf "%A %s" protocol expression) family width scale expected command
        use bound = new MySqlCommand("SELECT @value+0 AS value", connection)
        let parameter = bound.Parameters.AddWithValue("@value", DateTime(2020,1,1))
        bound.Prepare()
        for value, expected in
            [ DateTime(2020,1,1), "20200101000000"
              DateTime(2020,1,1).AddMilliseconds(123.0), "20200101000000.123" ] do
            parameter.Value <- box value
            check "binary DATETIME parameter" "DECIMAL" 23 6 expected bound

runTemporalArithmeticDescriptors ()

let runConstantNegationDescriptors () =
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
            failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion
        for operand, width, expected in
            [ "COALESCE(18446744073709551615,0)", 21, "-18446744073709551615"
              "IFNULL(18446744073709551615,0)", 21, "-18446744073709551615"
              "IF(1,18446744073709551615,0)", 21, "-18446744073709551615"
              "GREATEST(18446744073709551615,0)", 21, "-18446744073709551615"
              "LEAST(18446744073709551615,18446744073709551615)", 21, "-18446744073709551615"
              "NULLIF(18446744073709551615,0)", 21, "-18446744073709551615"
              "ROUND(18446744073709551615,0)", 22, "-18446744073709551615"
              "TRUNCATE(18446744073709551615,0)", 22, "-18446744073709551615"
              "CASE WHEN 1 THEN 18446744073709551615 ELSE 0 END", 21, "-18446744073709551615"
              "COALESCE(CAST(-9223372036854775808 AS SIGNED),0)", 21, "9223372036854775808" ] do
            use command = new MySqlCommand("SELECT -" + operand + " AS value", connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            let metadata = reader.GetColumnSchema()[0]
            if not (reader.Read()) || renderValue(reader.GetValue 0) <> expected
               || reader.GetDataTypeName(0) <> "DECIMAL"
               || metadata.ColumnSize <> Nullable width || metadata.NumericScale <> Nullable 0 then
                failwithf "%A -%s: expected DECIMAL width=%d scale=0 value=%s; got %s width=%O scale=%O"
                    protocol operand width expected (reader.GetDataTypeName 0) metadata.ColumnSize metadata.NumericScale
            printfn "Constant negation | %A | -%s -> DECIMAL width=%d value=%s" protocol operand width expected

runConstantNegationDescriptors ()

let runIntegerExpressionDescriptors () =
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
            failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion
        for expression, family, width, scale, expected, unsigned in
            [ "1", "BIGINT", 2, 0, "1", false
              "-1", "BIGINT", 2, 0, "-1", false
              "127", "BIGINT", 4, 0, "127", false
              "128", "BIGINT", 4, 0, "128", false
              "9223372036854775807", "BIGINT", 20, 0, "9223372036854775807", false
              "18446744073709551615", "BIGINT", 20, 0, "18446744073709551615", true
              "1+1", "BIGINT", 3, 0, "2", false
              "12+34", "BIGINT", 4, 0, "46", false
              "1-1", "BIGINT", 3, 0, "0", false
              "1*1", "BIGINT", 3, 0, "1", false
              "12*34", "BIGINT", 5, 0, "408", false
              "CAST(1 AS SIGNED)", "BIGINT", 21, 0, "1", false
              "CAST(1 AS UNSIGNED)", "BIGINT", 21, 0, "1", true
              "CAST(18446744073709551615 AS UNSIGNED)", "BIGINT", 21, 0, "18446744073709551615", true
              "-CAST(18446744073709551615 AS UNSIGNED)", "DECIMAL", 22, 0, "-18446744073709551615", false
              "CAST(1 AS UNSIGNED)/2", "DECIMAL", 27, 4, "0.5", false
              "CAST(1 AS UNSIGNED)/CAST(2 AS UNSIGNED)", "DECIMAL", 26, 4, "0.5", true
              "COALESCE(CAST(18446744073709551615 AS UNSIGNED),0)", "DECIMAL", 22, 0, "18446744073709551615", false
              "1 DIV 2", "BIGINT", 2, 0, "0", false
              "ROUND(1,0)", "BIGINT", 21, 0, "1", false
              "TRUNCATE(1,0)", "BIGINT", 21, 0, "1", false
              "CAST(NULL AS UNSIGNED)", "BIGINT", 21, 0, "NULL", true
              "CAST('123' AS SIGNED)", "BIGINT", 21, 0, "123", false
              "b'01'+1", "BIGINT", 5, 0, "2", false
              "1+b'01'", "BIGINT", 5, 0, "2", false
              "b'01'-1", "BIGINT", 5, 0, "0", false
              "b'01'*2", "BIGINT", 5, 0, "2", false
              "CAST(1 AS UNSIGNED)+2", "BIGINT", 22, 0, "3", true
              "CAST(3 AS UNSIGNED)-2", "BIGINT", 22, 0, "1", true
              "-(b'01'+1)", "BIGINT", 5, 0, "-2", false
              "-(1+1)", "BIGINT", 3, 0, "-2", false
              "MOD(b'01',2)", "BIGINT", 4, 0, "1", false
              "MOD(2,b'01')", "BIGINT", 4, 0, "0", false
              "123 DIV 2", "BIGINT", 4, 0, "61", false
              "1.25 DIV 0.1", "BIGINT", 3, 0, "12", false
              "'12' DIV 2", "BIGINT", 3, 0, "6", false
              "12 DIV '2'", "BIGINT", 4, 0, "6", false
              "b'01' DIV 2", "BIGINT", 4, 0, "0", false
              "2 DIV b'01'", "BIGINT", 2, 0, "2", false
              "NULL DIV 2", "BIGINT", 1, 0, "NULL", false
              "1 DIV NULL", "BIGINT", 2, 0, "NULL", false
              "CAST(1 AS UNSIGNED) DIV 2", "BIGINT", 21, 0, "0", true
              "1 DIV 2e0", "BIGINT", 22, 0, "0", false
              "9223372036854775807+0", "BIGINT", 21, 0, "9223372036854775807", false
              "MOD(CAST(1 AS UNSIGNED),2)", "BIGINT", 22, 0, "1", true
              "MOD(2,CAST(1 AS UNSIGNED))", "BIGINT", 22, 0, "0", false
              "b'01'/b'01'", "DECIMAL", 9, 4, "1", false
              "b'01'/CAST(2 AS UNSIGNED)", "DECIMAL", 9, 4, "0.5", false
              "1=1", "BIGINT", 1, 0, "1", false
              "1<2", "BIGINT", 1, 0, "1", false
              "NOT 0", "BIGINT", 1, 0, "1", false
              "1 IS NULL", "BIGINT", 1, 0, "0", false
              "EXISTS(SELECT 1)", "BIGINT", 1, 0, "1", false
              "-(1=1)", "BIGINT", 2, 0, "-1", false
              "-(NOT 0)", "BIGINT", 2, 0, "-1", false
              "(1=1)+1", "BIGINT", 3, 0, "2", false ] do
            use command = new MySqlCommand("SELECT " + expression + " AS value", connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            let metadata = reader.GetColumnSchema()[0]
            if not (reader.Read()) || renderValue(reader.GetValue 0) <> expected
               || reader.GetDataTypeName(0) <> family
               || metadata.ColumnSize <> Nullable width || metadata.NumericScale <> Nullable scale
               || (family = "BIGINT" && (reader.GetFieldType(0) = typeof<uint64>) <> unsigned) then
                failwithf "%A %s: expected %s width=%d scale=%d unsigned=%b value=%s; got %s width=%O scale=%O CLR=%s"
                    protocol expression family width scale unsigned expected (reader.GetDataTypeName 0)
                    metadata.ColumnSize metadata.NumericScale (reader.GetFieldType(0).Name)
            printfn "Integer descriptor | %A | %s -> %s width=%d scale=%d unsigned=%b" protocol expression family width scale unsigned

        if protocol = Sql then
            for assignment, unsigned, negativeWidth in [ "7", false, 21; "CAST(7 AS UNSIGNED)", true, 22 ] do
                use setup = new MySqlCommand("SET @value=" + assignment, connection)
                setup.ExecuteNonQuery() |> ignore
                for expression, width, expected, resultUnsigned in
                    [ "@value", 21, "7", unsigned
                      "@value+1", 22, "8", unsigned
                      "-@value", negativeWidth, "-7", false
                      "@value DIV 2", 21, "3", unsigned ] do
                    use command = new MySqlCommand("SELECT " + expression + " AS value", connection)
                    use reader = command.ExecuteReader()
                    let metadata = reader.GetColumnSchema()[0]
                    if not (reader.Read()) || renderValue(reader.GetValue 0) <> expected
                       || reader.GetDataTypeName(0) <> "BIGINT" || metadata.ColumnSize <> Nullable width
                       || (reader.GetFieldType(0) = typeof<uint64>) <> resultUnsigned then
                        failwithf "%s assigned %s: expected BIGINT width=%d unsigned=%b" expression assignment width resultUnsigned
                    printfn "Integer variable | %s | %s -> width=%d unsigned=%b" assignment expression width resultUnsigned

runIntegerExpressionDescriptors ()

let runApproximateExpressionDescriptors () =
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
            failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion
        for sql in [ "CREATE DATABASE IF NOT EXISTS fsdb_type_oracle"; "USE fsdb_type_oracle"
                     "CREATE TEMPORARY TABLE approximate_numbers(d DOUBLE,f FLOAT,df DOUBLE(10,2))"
                     "INSERT INTO approximate_numbers VALUES(1.25,1.25,1.25)"
                     "CREATE TEMPORARY TABLE approximate_other LIKE approximate_numbers"
                     "INSERT INTO approximate_other SELECT * FROM approximate_numbers" ] do
            use setup = new MySqlCommand(sql, connection)
            setup.ExecuteNonQuery() |> ignore
        for expression, family, width, scale, expected in
            [ "-1e0", "DOUBLE", 23, 31, Some -1.0
              "1e0+1", "DOUBLE", 23, 31, Some 2.0
              "'1'+1", "DOUBLE", 23, 31, Some 2.0
              "ABS(1e0)", "DOUBLE", 23, 31, Some 1.0
              "SQRT(4)", "DOUBLE", 23, 31, Some 2.0
              "ROUND(1e0,2)", "DOUBLE", 23, 31, Some 1.0
              "COALESCE(1e0,0)", "DOUBLE", 23, 31, Some 1.0
              "COALESCE(1e0,NULL)", "DOUBLE", 23, 31, Some 1.0
              "CAST(1 AS DOUBLE)", "DOUBLE", 23, 31, Some 1.0
              "d", "DOUBLE", 22, 31, Some 1.25
              "f", "FLOAT", 12, 31, Some 1.25
              "df", "DOUBLE", 10, 2, Some 1.25
              "d+0", "DOUBLE", 23, 31, Some 1.25
              "f+0", "DOUBLE", 23, 31, Some 1.25
              "df+0", "DOUBLE", 10, 2, Some 1.25
              "-df", "DOUBLE", 19, 2, Some -1.25
              "ABS(df)", "DOUBLE", 19, 2, Some 1.25
              "ROUND(df,1)", "DOUBLE", 23, 31, Some 1.2
              "TRUNCATE(df,1)", "DOUBLE", 23, 31, Some 1.2
              "FLOOR(df)", "DOUBLE", 23, 31, Some 1.0
              "CEIL(df)", "DOUBLE", 23, 31, Some 2.0
              "ABS(f)", "DOUBLE", 23, 31, Some 1.25
              "-f", "DOUBLE", 23, 31, Some -1.25
              "df+df", "DOUBLE", 10, 2, Some 2.5
              "df*df", "DOUBLE", 10, 2, Some 1.56
              "df+NULL", "DOUBLE", 10, 2, None
              "COALESCE(NULLIF(df,df),2)", "DOUBLE", 10, 2, Some 2.0
              "COALESCE(NULLIF(df,df),2)+0.5", "DOUBLE", 10, 2, Some 2.5
              "COALESCE(df,0)", "DOUBLE", 10, 2, Some 1.25
              "IF(1,df,0)", "DOUBLE", 10, 2, Some 1.25
              "COALESCE(f,0)", "FLOAT", 23, 31, Some 1.25
              "COALESCE(f,1.25)", "DOUBLE", 23, 31, Some 1.25
              "IFNULL(f,1.25)", "DOUBLE", 23, 31, Some 1.25
              "IF(0,df,2)", "DOUBLE", 10, 2, Some 2.0
              "CASE WHEN 0 THEN df ELSE 2 END", "DOUBLE", 10, 2, Some 2.0
              "df*1.2345", "DOUBLE", 12, 4, Some 1.5431
              "df+1.2345", "DOUBLE", 12, 4, Some 2.4845
              "CAST(1 AS FLOAT)", "FLOAT", 23, 31, Some 1.0
              "-b'01'", "DOUBLE", 17, 0, Some -1.0
              "ABS(b'01')", "DOUBLE", 17, 0, Some 1.0 ] do
            use command = new MySqlCommand("SELECT " + expression + " AS value FROM approximate_numbers", connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            let metadata = reader.GetColumnSchema()[0]
            if not (reader.Read()) then failwithf "%s: missing row" expression
            let expected =
                match protocol, expression with
                | Binary, "df*df" -> Some 1.5625
                | Binary, "df*1.2345" -> Some 1.5431249999999999
                | Binary, "df+1.2345" -> Some 2.4844999999999997
                | _ -> expected
            let actual = if reader.IsDBNull 0 then None else Some(Convert.ToDouble(reader.GetValue 0, invariant))
            if actual <> expected || reader.GetDataTypeName(0) <> family
               || metadata.ColumnSize <> Nullable width || metadata.NumericScale <> Nullable scale then
                failwithf "%A %s: expected %s width=%d scale=%d value=%A; got %s width=%O scale=%O value=%A"
                    protocol expression family width scale expected (reader.GetDataTypeName 0)
                    metadata.ColumnSize metadata.NumericScale actual
            printfn "Approximate descriptor | %A | %s -> %s width=%d scale=%d" protocol expression family width scale


        for expression, width, expected in
            [ "df*df", 10, 1.56; "df*0.1", 10, 0.12; "-df*0.1", 19, -0.12 ] do
            let branch = "SELECT " + expression + " AS value FROM approximate_numbers"
            // MySQL cannot reopen the same temporary table in two UNION branches.
            let otherBranch = branch.Replace("approximate_numbers", "approximate_other")
            use command = new MySqlCommand(branch + " UNION ALL " + otherBranch, connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            let metadata = reader.GetColumnSchema()[0]
            if metadata.ColumnSize <> Nullable width || metadata.NumericScale <> Nullable 2 then
                failwithf "%A UNION %s: unexpected descriptor" protocol expression
            let mutable rows = 0
            while reader.Read() do
                rows <- rows + 1
                if reader.GetDouble(0) <> expected then failwithf "%A UNION %s: expected %g" protocol expression expected
            if rows <> 2 then failwithf "%A UNION %s: expected two rows" protocol expression
            printfn "Approximate UNION | %A | %s -> %g" protocol expression expected

        for expression, expected in
            [ "CAST(df*df AS CHAR)", "1.56"
              "CONCAT(df+df)", "2.50"
              "CAST(COALESCE(NULLIF(df,df),2) AS CHAR)", "2.00"
              "CAST(-TIMESTAMP '2020-01-01 00:00:00.123' AS CHAR)", "-20200101000000.120"
              "CAST(ABS(CAST('2020-01-01 03:04:05.123456' AS DATETIME(6))) AS CHAR)", "20200101030405.125000"
              "CAST(-b'1000000000000000000000000000000000000000000000000000000000000000' AS CHAR)", "-9223372036854776000"
              "CAST((SELECT b'01' FROM (SELECT 1)t)/2 AS CHAR)", "0.0000" ] do
            use command = new MySqlCommand("SELECT " + expression + " AS value FROM approximate_numbers", connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            if not (reader.Read()) || reader.GetString(0) <> expected then
                failwithf "%A %s: expected %s" protocol expression expected
            printfn "Approximate text | %A | %s -> %s" protocol expression expected

runApproximateExpressionDescriptors ()


let runScientificLiteralDescriptors () =
    for protocol in [ Sql; Binary ] do
        use connection = new MySqlConnection(connectionString)
        connection.Open()
        if connection.ServerVersion.Split('-')[0] <> "8.4.11" then
            failwithf "Expected MySQL 8.4.11; got %s" connection.ServerVersion
        for sql in [ "CREATE DATABASE IF NOT EXISTS fsdb_type_oracle"; "USE fsdb_type_oracle"
                     "CREATE TEMPORARY TABLE scientific_source(n INT,f FLOAT,d DOUBLE(10,2))"
                     "INSERT INTO scientific_source VALUES(1,1.25,1.25)" ] do
            use setup = new MySqlCommand(sql, connection)
            setup.ExecuteNonQuery() |> ignore
        for expression, expected, family, width, scale in
            [ "1e0", "1", "DOUBLE", 3, 31
              "1E+00", "1", "DOUBLE", 5, 31
              "0001e000", "1", "DOUBLE", 8, 31
              "1.00e0", "1", "DOUBLE", 6, 31
              ".1e1", "1", "DOUBLE", 4, 31
              "1.e0", "1", "DOUBLE", 4, 31
              "+1e0", "1", "DOUBLE", 3, 31
              "-1e0", "-1", "DOUBLE", 23, 31
              "-(-1e0)", "1", "DOUBLE", 23, 31
              "(1e0)", "1", "DOUBLE", 3, 31
              "(SELECT 1e0)", "1", "DOUBLE", 3, 31
              "(SELECT 1e0 FROM scientific_source LIMIT 1)", "1", "DOUBLE", 3, 31
              "(SELECT x FROM (SELECT 1e0 AS x)t)", "1", "DOUBLE", 3, 31
              "1 DIV 2e0", "0", "BIGINT", 22, 0
              "2e0 DIV 1", "2", "BIGINT", 22, 0
              "(SELECT 2e0 FROM scientific_source LIMIT 1) DIV 1", "2", "BIGINT", 22, 0
              "1e0/2", "0.5", "DOUBLE", 23, 31
              "ABS(1e0)", "1", "DOUBLE", 23, 31
              "COALESCE(1e0,NULL)", "1", "DOUBLE", 23, 31
              "1e0+0", "1", "DOUBLE", 23, 31
              "(SELECT f DIV 1 FROM scientific_source)", "1", "BIGINT", 13, 0
              "(SELECT d DIV 1 FROM scientific_source)", "1", "BIGINT", 21, 0
              "(SELECT d DIV 0.1 FROM scientific_source)", "12", "BIGINT", 22, 0
              "(SELECT 2 DIV d FROM scientific_source)", "1", "BIGINT", 4, 0 ] do
            use command = new MySqlCommand("SELECT " + expression + " AS value", connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            let metadata = reader.GetColumnSchema()[0]
            if not (reader.Read()) || renderValue(reader.GetValue 0) <> expected
               || reader.GetDataTypeName(0) <> family || metadata.ColumnSize <> Nullable width
               || metadata.NumericScale <> Nullable scale then
                failwithf "%A %s: expected %s width=%d scale=%d value=%s; got %s width=%O scale=%O"
                    protocol expression family width scale expected (reader.GetDataTypeName 0) metadata.ColumnSize metadata.NumericScale
            printfn "Scientific descriptor | %A | %s -> %s width=%d scale=%d" protocol expression family width scale
        for spelling in [ "1E+00"; "0001e000"; ".1e1" ] do
            use command = new MySqlCommand("SELECT " + spelling, connection)
            if protocol = Binary then command.Prepare()
            use reader = command.ExecuteReader()
            if reader.GetName(0) <> spelling then failwithf "%A %s: unexpected projection name %s" protocol spelling (reader.GetName 0)

runScientificLiteralDescriptors ()
