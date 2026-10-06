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
