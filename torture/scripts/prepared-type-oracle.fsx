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
