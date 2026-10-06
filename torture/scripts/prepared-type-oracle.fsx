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
