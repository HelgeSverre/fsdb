namespace Fsdb.Torture

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open MySqlConnector
open Fsdb.Ast
open Fsdb.Storage
open Fsdb.Value

[<RequireQualifiedAccess>]
module AstKind =
    let rec ofStatement =
        function
        | CreateDatabase _ -> "create_database"
        | DropDatabase _ -> "drop_database"
        | CreateTable _ -> "create_table"
        | DropTable _ -> "drop_table"
        | AlterTable _ -> "alter_table"
        | AlterDatabase _ -> "alter_database"
        | RenameTable _ -> "rename_table"
        | CreateIndex _ -> "create_index"
        | DropIndexStmt _ -> "drop_index"
        | Insert _ -> "insert"
        | InsertSelect _ -> "insert_select"
        | Select _ -> "select"
        | Union _ -> "union"
        | Update _ -> "update"
        | Delete _ -> "delete"
        | Truncate _ -> "truncate"
        | CreateUser _ -> "create_user"
        | DropUser _ -> "drop_user"
        | AlterUser _ -> "alter_user"
        | Grant _ -> "grant"
        | Revoke _ -> "revoke"
        | Explain statement -> "explain_" + ofStatement statement

[<RequireQualifiedAccess>]
module ProcessRunner =
    let run (executable: string) (arguments: string array) (workingDirectory: string) (timeout: TimeSpan) =
        task {
            let startInfo = ProcessStartInfo()
            startInfo.FileName <- executable
            startInfo.WorkingDirectory <- workingDirectory
            startInfo.UseShellExecute <- false
            startInfo.RedirectStandardOutput <- true
            startInfo.RedirectStandardError <- true

            arguments |> Array.iter startInfo.ArgumentList.Add

            use childProcess = new Process(StartInfo = startInfo)
            let stopwatch = Stopwatch.StartNew()

            if not (childProcess.Start()) then
                failwithf "could not start %s" executable

            let stdoutTask = childProcess.StandardOutput.ReadToEndAsync()
            let stderrTask = childProcess.StandardError.ReadToEndAsync()
            use timeoutSource = new CancellationTokenSource(timeout)
            let mutable timedOut = false

            try
                do! childProcess.WaitForExitAsync(timeoutSource.Token)
            with :? OperationCanceledException ->
                timedOut <- true

                try
                    childProcess.Kill(true)
                with _ ->
                    ()

                do! childProcess.WaitForExitAsync()

            let! stdout = stdoutTask
            let! stderr = stderrTask
            stopwatch.Stop()

            return
                { ExitCode = if timedOut then -1 else childProcess.ExitCode
                  Stdout = stdout
                  Stderr = stderr
                  ElapsedMs = stopwatch.ElapsedMilliseconds
                  TimedOut = timedOut }
        }

[<RequireQualifiedAccess>]
module Tooling =
    [<Literal>]
    let RequiredSqlSplitterVersion = "1.21.0"

    let private findOnPath (name: string) =
        let candidates =
            Environment.GetEnvironmentVariable("PATH")
            |> Option.ofObj
            |> Option.defaultValue ""
            |> fun value -> value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun directory -> Path.Combine(directory, name))

        candidates |> Array.tryFind File.Exists |> Option.map Path.GetFullPath

    let private explicitCandidate source (path: string) =
        let resolved = Path.GetFullPath path

        if File.Exists resolved then
            Ok resolved
        else
            Error(sprintf "%s does not exist: %s" source resolved)

    let resolveSqlSplitter (requested: string) =
        let local = Path.Combine(Paths.tortureRoot (), ".tools", "bin", "sql-splitter")

        if not (String.IsNullOrWhiteSpace requested) then
            explicitCandidate "--sql-splitter path" requested
        else
            match Environment.GetEnvironmentVariable("SQL_SPLITTER_BIN") with
            | value when not (String.IsNullOrWhiteSpace value) -> explicitCandidate "SQL_SPLITTER_BIN" value
            | _ when File.Exists local -> Ok(Path.GetFullPath local)
            | _ ->
                match findOnPath "sql-splitter" with
                | Some value -> Ok value
                | None ->
                    Error
                        "SQL Splitter was not found; pass --sql-splitter, set SQL_SPLITTER_BIN, or run torture/scripts/bootstrap.sh"

    let inspectSqlSplitter path =
        task {
            let! result = ProcessRunner.run path [| "--version" |] (Paths.tortureRoot ()) (TimeSpan.FromSeconds 10.0)

            if result.ExitCode <> 0 then
                return Error(sprintf "sql-splitter --version failed: %s" result.Stderr)
            else
                let version = result.Stdout.Trim()
                let expected = "sql-splitter " + RequiredSqlSplitterVersion

                if version <> expected then
                    return Error(sprintf "expected %s, got %s" expected version)
                else
                    return
                        Ok
                            { Name = "sql-splitter"
                              Version = RequiredSqlSplitterVersion
                              Path = Path.GetFullPath path
                              Sha256 = Hashing.file path }
        }

    let gitState () =
        task {
            let root = Paths.repoRoot ()
            let! revision = ProcessRunner.run "git" [| "rev-parse"; "HEAD" |] root (TimeSpan.FromSeconds 10.0)
            let! status = ProcessRunner.run "git" [| "status"; "--short" |] root (TimeSpan.FromSeconds 10.0)

            return
                (if revision.ExitCode = 0 then revision.Stdout.Trim() else "unknown"),
                (status.ExitCode <> 0 || not (String.IsNullOrWhiteSpace status.Stdout))
        }

[<RequireQualifiedAccess>]
module CommitEvents =
    let private rowHash (row: Value array) = row |> Array.map Fsdb.Value.toWire |> Json.serialize |> Hashing.text

    let rec summarize =
        function
        | RowsInserted(db, table, rows) ->
            sprintf "rows_inserted db=%s table=%s count=%d hash=%s" db table rows.Length (rows |> Seq.map rowHash |> Hashing.combine)
        | RowsUpdated(db, table, changes) ->
            let hashes = changes |> Seq.collect (fun (before, after) -> [ rowHash before; rowHash after ])
            sprintf "rows_updated db=%s table=%s count=%d hash=%s" db table changes.Length (Hashing.combine hashes)
        | RowsDeleted(db, table, rows) ->
            sprintf "rows_deleted db=%s table=%s count=%d hash=%s" db table rows.Length (rows |> Seq.map rowHash |> Hashing.combine)
        | SchemaChanged(db, statement) -> sprintf "schema_changed db=%s statement=%s" db (AstKind.ofStatement statement)
        | TransactionCommitted events ->
            sprintf "transaction_committed count=%d hash=%s" events.Length (events |> Seq.map summarize |> Hashing.combine)

type FsdbSubject() =
    let store = Fsdb.Storage.create ()
    let events = ConcurrentQueue<string>()

    do store.OnCommit.Add(CommitEvents.summarize >> events.Enqueue)

    let listener = Fsdb.Server.startListening IPAddress.Loopback 0
    let port = Fsdb.Server.port listener
    let serveTask = Fsdb.Server.serve listener store Fsdb.Functions.empty |> Async.StartAsTask

    member _.Store = store
    member _.Port = port

    member _.DrainEvents() =
        let drained = ResizeArray<string>()
        let mutable value = ""

        while events.TryDequeue(&value) do
            drained.Add value

        drained.ToArray()

    interface IDisposable with
        member _.Dispose() =
            listener.Stop()

            try
                serveTask.Wait(TimeSpan.FromSeconds 2.0) |> ignore
            with _ ->
                ()

[<RequireQualifiedAccess>]
module Invariants =
    let private sequenceOptions values =
        let folder state value =
            match state, value with
            | Some accumulated, Some item -> Some(item :: accumulated)
            | _ -> None

        values |> List.fold folder (Some []) |> Option.map List.rev

    let private columnIndex (columns: ColumnDef list) name =
        columns
        |> List.tryFindIndex (fun column -> String.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))

    let private keyText (row: Value array) (indices: int array) =
        let canonical =
            function
            | VNull -> "N"
            | VInt value -> "I" + string value
            | VDouble value -> "D" + (if value = 0.0 then 0.0 else value).ToString("R", CultureInfo.InvariantCulture)
            | VDecimal value -> "M" + value.ToString("G29", CultureInfo.InvariantCulture)
            | VString value -> "S" + value.TrimEnd(' ').ToUpperInvariant()
            | VBytes value -> "B" + Convert.ToHexString value
            | VDate value -> "T" + string value.DayNumber
            | VDateTime value -> "V" + string value.Ticks
            | VJson value -> "J" + value.TrimEnd(' ').ToUpperInvariant()

        indices |> Array.map (fun index -> canonical row.[index]) |> Json.serialize

    let validate (store: Store) =
        let errors = ResizeArray<string>()

        for dbName, database in Map.toSeq store.Catalog do
            for tableKey, table in Map.toSeq database do
                let expectedArity = table.Columns.Length

                table.Rows
                |> List.iteri (fun rowIndex row ->
                    if row.Length <> expectedArity then
                        errors.Add(sprintf "%s.%s row %d has arity %d, expected %d" dbName tableKey rowIndex row.Length expectedArity))

                let uniqueGroups =
                    let primary =
                        table.Columns
                        |> List.filter _.PrimaryKey
                        |> List.map _.Name
                        |> function
                            | [] -> []
                            | columns -> [ "PRIMARY", columns ]

                    primary
                    @ (table.Indexes |> List.filter _.Unique |> List.map (fun index -> index.Name, index.Columns))

                for keyName, columnNames in uniqueGroups do
                    match columnNames |> List.map (columnIndex table.Columns) |> sequenceOptions with
                    | None -> errors.Add(sprintf "%s.%s key %s references a missing column" dbName tableKey keyName)
                    | Some indexList ->
                        let indices = Array.ofList indexList
                        let seen = HashSet<string>(StringComparer.Ordinal)

                        for row in table.Rows do
                            if indices |> Array.exists (fun index -> index >= row.Length) then
                                errors.Add(sprintf "%s.%s key %s cannot inspect a short row" dbName tableKey keyName)
                            else
                                let hasNull = indices |> Array.exists (fun index -> row.[index] = VNull)

                                if keyName = "PRIMARY" && hasNull then
                                    errors.Add(sprintf "%s.%s primary key contains NULL" dbName tableKey)
                                elif not hasNull && not (seen.Add(keyText row indices)) then
                                    errors.Add(sprintf "%s.%s key %s contains a duplicate" dbName tableKey keyName)

                for foreignKey in table.ForeignKeys do
                    match
                        foreignKey.Columns |> List.map (columnIndex table.Columns) |> sequenceOptions,
                        database |> Map.tryFind (Fsdb.Storage.normalizeTableName foreignKey.RefTable)
                    with
                    | Some childIndices, Some parent ->
                        match foreignKey.RefColumns |> List.map (columnIndex parent.Columns) |> sequenceOptions with
                        | None -> errors.Add(sprintf "%s.%s foreign key %s references a missing parent column" dbName tableKey foreignKey.Name)
                        | Some parentIndices when childIndices.Length <> parentIndices.Length ->
                            errors.Add(sprintf "%s.%s foreign key %s has mismatched child and parent arity" dbName tableKey foreignKey.Name)
                        | Some parentIndices ->
                            let parentIndexArray = Array.ofList parentIndices
                            let parentKeys = HashSet<string>(StringComparer.Ordinal)

                            for parentRow in parent.Rows do
                                if parentIndexArray |> Array.forall (fun index -> index < parentRow.Length && parentRow.[index] <> VNull) then
                                    parentKeys.Add(keyText parentRow parentIndexArray) |> ignore

                            for row in table.Rows do
                                if childIndices |> List.exists (fun index -> index >= row.Length) then
                                    errors.Add(sprintf "%s.%s foreign key %s cannot inspect a short child row" dbName tableKey foreignKey.Name)
                                else
                                    let childValues = childIndices |> List.map (fun index -> row.[index])

                                    if childValues |> List.forall ((<>) VNull) then
                                        let parentExists = keyText row (Array.ofList childIndices) |> parentKeys.Contains

                                        if not parentExists then
                                            errors.Add(sprintf "%s.%s foreign key %s has an orphan" dbName tableKey foreignKey.Name)
                    | _ -> errors.Add(sprintf "%s.%s foreign key %s references a missing table or child column" dbName tableKey foreignKey.Name)

                table.Columns
                |> List.tryFindIndex _.AutoIncrement
                |> Option.iter (fun autoIndex ->
                    let maximum =
                        table.Rows
                        |> List.choose (fun row ->
                            if autoIndex >= row.Length then
                                None
                            else
                                match row.[autoIndex] with
                                | VInt value -> Some value
                                | _ -> None)
                        |> function
                            | [] -> 0L
                            | values -> List.max values

                    if table.NextAutoId <= maximum then
                        errors.Add(sprintf "%s.%s NextAutoId %d is not above %d" dbName tableKey table.NextAutoId maximum))

        errors.ToArray()

[<RequireQualifiedAccess>]
module Database =
    [<Literal>]
    let private SnapshotChunkSize = 4096

    [<Literal>]
    let private SnapshotSampleSize = 8

    type private DataDigestBuilder() =
        let chunks = ResizeArray<DataChunkSnapshot>()
        let current = ResizeArray<string>(SnapshotChunkSize)
        let firstRows = ResizeArray<string>(SnapshotSampleSize)
        let lastRows = Queue<string>(SnapshotSampleSize)
        let mutable rowCount = 0L

        let flush () =
            if current.Count > 0 then
                let rows = current.ToArray()

                chunks.Add
                    { Index = chunks.Count
                      StartRow = rowCount - int64 rows.Length
                      RowCount = rows.Length
                      Sha256 = Hashing.combine rows
                      FirstRow = rows.[0]
                      LastRow = rows.[rows.Length - 1] }

                current.Clear()

        member _.Add(row: string) =
            if firstRows.Count < SnapshotSampleSize then
                firstRows.Add row

            if lastRows.Count = SnapshotSampleSize then
                lastRows.Dequeue() |> ignore

            lastRows.Enqueue row
            current.Add row
            rowCount <- rowCount + 1L

            if current.Count = SnapshotChunkSize then
                flush ()

        member _.Finish() =
            flush ()
            let finishedChunks = chunks.ToArray()

            let digest =
                seq {
                    yield string rowCount
                    yield! finishedChunks |> Seq.map _.Sha256
                }
                |> Hashing.combine

            let samples =
                if rowCount <= int64 SnapshotSampleSize then
                    firstRows.ToArray()
                else
                    Array.append (firstRows.ToArray()) (lastRows.ToArray())

            rowCount, digest, finishedChunks, samples

    let digestRows (rows: string seq) =
        let builder = DataDigestBuilder()
        rows |> Seq.iter builder.Add
        builder.Finish()

    let quoteIdentifier (value: string) = "`" + value.Replace("`", "``") + "`"

    let execute (target: string) (connection: MySqlConnection) timeoutSeconds (sql: string) =
        task {
            use command = connection.CreateCommand()
            command.CommandText <- sql
            command.CommandTimeout <- timeoutSeconds
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds))
            let stopwatch = Stopwatch.StartNew()

            try
                let! affected = command.ExecuteNonQueryAsync(timeout.Token)
                stopwatch.Stop()

                return
                    { Target = target
                      Status = "success"
                      AffectedRows = affected
                      ErrorCode = 0
                      SqlState = ""
                      Message = ""
                      ElapsedMs = stopwatch.ElapsedMilliseconds }
            with
            | :? OperationCanceledException as error ->
                stopwatch.Stop()

                return
                    { Target = target
                      Status = "timeout"
                      AffectedRows = 0
                      ErrorCode = 0
                      SqlState = ""
                      Message = error.Message
                      ElapsedMs = stopwatch.ElapsedMilliseconds }
            | :? MySqlException as error ->
                stopwatch.Stop()

                return
                    { Target = target
                      Status = "server_error"
                      AffectedRows = 0
                      ErrorCode = int error.ErrorCode
                      SqlState = error.SqlState |> Option.ofObj |> Option.defaultValue ""
                      Message = error.Message
                      ElapsedMs = stopwatch.ElapsedMilliseconds }
            | error ->
                stopwatch.Stop()

                return
                    { Target = target
                      Status = "driver_error"
                      AffectedRows = 0
                      ErrorCode = 0
                      SqlState = ""
                      Message = error.ToString()
                      ElapsedMs = stopwatch.ElapsedMilliseconds }
        }

    let openConnection connectionString =
        task {
            let connection = new MySqlConnection(connectionString)
            do! connection.OpenAsync()
            return connection
        }

    let createOracleDatabase baseConnectionString databaseName timeoutSeconds =
        task {
            let builder = MySqlConnectionStringBuilder(baseConnectionString)
            builder.Database <- ""
            use! admin = openConnection builder.ConnectionString

            let! drop = execute "mysql" admin timeoutSeconds (sprintf "DROP DATABASE IF EXISTS %s" (quoteIdentifier databaseName))

            if not (TargetOutcome.succeeded drop) then
                return Error drop
            else
                let! create = execute "mysql" admin timeoutSeconds (sprintf "CREATE DATABASE %s" (quoteIdentifier databaseName))

                if not (TargetOutcome.succeeded create) then
                    return Error create
                else
                    builder.Database <- databaseName
                    return Ok builder.ConnectionString
        }

    let scalarString (connection: MySqlConnection) timeoutSeconds sql =
        task {
            use command = connection.CreateCommand()
            command.CommandText <- sql
            command.CommandTimeout <- timeoutSeconds
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds))
            let! result = command.ExecuteScalarAsync(timeout.Token)
            return if isNull result || result = box DBNull.Value then "" else Convert.ToString(result, CultureInfo.InvariantCulture)
        }

    let private normalizeType (value: string) =
        value.Trim().ToLowerInvariant().Replace("integer", "int").Replace("tinyint(1)", "tinyint")

    let private valueString (reader: MySqlDataReader) ordinal =
        if reader.IsDBNull ordinal then
            ""
        else
            Convert.ToString(reader.GetValue ordinal, CultureInfo.InvariantCulture)

    let private metadataValue (reader: MySqlDataReader) ordinal =
        if reader.IsDBNull ordinal then
            "null"
        else
            "value:" + Convert.ToString(reader.GetValue ordinal, CultureInfo.InvariantCulture)

    let private readColumns (connection: MySqlConnection) timeoutSeconds (table: string) =
        task {
            use command = connection.CreateCommand()
            command.CommandText <- sprintf "SHOW COLUMNS FROM %s" (quoteIdentifier table)
            command.CommandTimeout <- timeoutSeconds
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds))
            use! reader = command.ExecuteReaderAsync(timeout.Token)
            let rows = ResizeArray<ColumnSnapshot>()

            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync(timeout.Token)

                if hasRow then
                    rows.Add
                        { Name = valueString reader (reader.GetOrdinal "Field")
                          Type = normalizeType (valueString reader (reader.GetOrdinal "Type"))
                          Nullable = valueString reader (reader.GetOrdinal "Null") = "YES"
                          Key = valueString reader (reader.GetOrdinal "Key")
                          DefaultValue = metadataValue reader (reader.GetOrdinal "Default")
                          Extra = valueString reader (reader.GetOrdinal "Extra") }
                else
                    reading <- false

            return rows.ToArray()
        }

    let private readIndexes (connection: MySqlConnection) timeoutSeconds (table: string) =
        task {
            use command = connection.CreateCommand()
            command.CommandText <- sprintf "SHOW INDEX FROM %s" (quoteIdentifier table)
            command.CommandTimeout <- timeoutSeconds
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds))
            use! reader = command.ExecuteReaderAsync(timeout.Token)
            let rows = ResizeArray<IndexSnapshot>()

            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync(timeout.Token)

                if hasRow then
                    rows.Add
                        { Name = valueString reader (reader.GetOrdinal "Key_name")
                          Unique = valueString reader (reader.GetOrdinal "Non_unique") = "0"
                          Sequence = Int32.Parse(valueString reader (reader.GetOrdinal "Seq_in_index"), CultureInfo.InvariantCulture)
                          Column = valueString reader (reader.GetOrdinal "Column_name") }
                else
                    reading <- false

            return rows.ToArray()
        }

    let private readForeignKeys (connection: MySqlConnection) timeoutSeconds (table: string) =
        task {
            use command = connection.CreateCommand()
            command.CommandText <-
                sprintf
                    "SELECT constraint_name, column_name, referenced_table_name, referenced_column_name, ordinal_position FROM information_schema.key_column_usage WHERE table_schema = DATABASE() AND table_name = '%s' AND referenced_table_name IS NOT NULL ORDER BY constraint_name, ordinal_position"
                    (table.Replace("'", "''"))

            command.CommandTimeout <- timeoutSeconds
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds))
            use! reader = command.ExecuteReaderAsync(timeout.Token)
            let rows = ResizeArray<ForeignKeySnapshot>()

            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync(timeout.Token)

                if hasRow then
                    rows.Add
                        { Name = valueString reader 0
                          Sequence = Int32.Parse(valueString reader 4, CultureInfo.InvariantCulture)
                          Column = valueString reader 1
                          ReferencedTable = valueString reader 2
                          ReferencedColumn = valueString reader 3 }
                else
                    reading <- false

            return rows.ToArray()
        }

    let rec private canonicalJsonElement (element: JsonElement) =
        match element.ValueKind with
        | JsonValueKind.Object ->
            element.EnumerateObject()
            |> Seq.sortBy _.Name
            |> Seq.map (fun property -> JsonSerializer.Serialize(property.Name) + ":" + canonicalJsonElement property.Value)
            |> String.concat ","
            |> sprintf "{%s}"
        | JsonValueKind.Array ->
            element.EnumerateArray()
            |> Seq.map canonicalJsonElement
            |> String.concat ","
            |> sprintf "[%s]"
        | JsonValueKind.String -> JsonSerializer.Serialize(element.GetString())
        | JsonValueKind.Number ->
            match element.TryGetDecimal() with
            | true, number -> number.ToString("G29", CultureInfo.InvariantCulture)
            | false, _ -> element.GetDouble().ToString("R", CultureInfo.InvariantCulture)
        | JsonValueKind.True -> "true"
        | JsonValueKind.False -> "false"
        | JsonValueKind.Null -> "null"
        | _ -> element.GetRawText()

    let canonicalCell (declaredType: string) (value: obj) =
        if isNull value || value = box DBNull.Value then
            "null"
        else
            let lowerType = declaredType.ToLowerInvariant()

            if lowerType.Contains("binary") || lowerType.Contains("blob") then
                let bytes =
                    match value with
                    | :? (byte array) as bytes -> bytes
                    | :? string as text -> Encoding.Latin1.GetBytes text
                    | _ -> Encoding.Latin1.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture))

                "bytes:" + Convert.ToHexString(bytes).ToLowerInvariant()
            elif lowerType.Contains("json") then
                let text = Convert.ToString(value, CultureInfo.InvariantCulture)

                try
                    use document = JsonDocument.Parse text
                    "json:" + canonicalJsonElement document.RootElement
                with :? JsonException ->
                    "invalid_json:" + text
            else
                match value with
                | :? bool as boolean -> "integer:" + (if boolean then "1" else "0")
                | :? decimal as number -> "decimal:" + number.ToString(CultureInfo.InvariantCulture)
                | :? double as number -> "float:" + number.ToString("R", CultureInfo.InvariantCulture)
                | :? single as number -> "float:" + number.ToString("R", CultureInfo.InvariantCulture)
                | :? sbyte
                | :? byte
                | :? int16
                | :? uint16
                | :? int
                | :? uint32
                | :? int64
                | :? uint64 -> "integer:" + Convert.ToString(value, CultureInfo.InvariantCulture)
                | :? DateTime as date -> "time:" + date.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)
                | :? DateOnly as date -> "time:" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                | :? (byte array) as bytes -> "bytes:" + Convert.ToHexString(bytes).ToLowerInvariant()
                | _ -> "text:" + Convert.ToString(value, CultureInfo.InvariantCulture)

    let query (target: string) (connection: MySqlConnection) timeoutSeconds (sql: string) =
        task {
            use command = connection.CreateCommand()
            command.CommandText <- sql
            command.CommandTimeout <- timeoutSeconds
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds))
            let stopwatch = Stopwatch.StartNew()

            try
                use! reader = command.ExecuteReaderAsync(timeout.Token)
                let columns = [| for index in 0 .. reader.FieldCount - 1 -> reader.GetName index |]
                let declaredTypes = [| for index in 0 .. reader.FieldCount - 1 -> reader.GetDataTypeName index |]
                let rows = ResizeArray<string>()
                let mutable reading = true

                while reading do
                    let! hasRow = reader.ReadAsync(timeout.Token)

                    if hasRow then
                        let cells =
                            [| for index in 0 .. reader.FieldCount - 1 -> canonicalCell declaredTypes.[index] (reader.GetValue index) |]

                        rows.Add(Json.serialize cells)
                    else
                        reading <- false

                stopwatch.Stop()
                let resultRows = rows.ToArray()
                let digest = Hashing.text (Json.serialize {| Columns = columns; Rows = resultRows |})

                return
                    { Target = target
                      Status = "success"
                      Columns = columns
                      Rows = resultRows
                      DataSha256 = digest
                      ErrorCode = 0
                      SqlState = ""
                      Message = ""
                      ElapsedMs = stopwatch.ElapsedMilliseconds }
            with
            | :? OperationCanceledException as error ->
                stopwatch.Stop()

                return
                    { ProbeOutcome.notRun target with
                        Status = "timeout"
                        Message = error.Message
                        ElapsedMs = stopwatch.ElapsedMilliseconds }
            | :? MySqlException as error ->
                stopwatch.Stop()

                return
                    { ProbeOutcome.notRun target with
                        Status = "server_error"
                        ErrorCode = int error.ErrorCode
                        SqlState = error.SqlState |> Option.ofObj |> Option.defaultValue ""
                        Message = error.Message
                        ElapsedMs = stopwatch.ElapsedMilliseconds }
            | error ->
                stopwatch.Stop()

                return
                    { ProbeOutcome.notRun target with
                        Status = "driver_error"
                        Message = error.ToString()
                        ElapsedMs = stopwatch.ElapsedMilliseconds }
        }

    let private readData (connection: MySqlConnection) timeoutSeconds (table: string) (columns: ColumnSnapshot array) =
        task {
            use command = connection.CreateCommand()

            let orderedColumns =
                let primary = columns |> Array.filter (fun column -> column.Key = "PRI") |> Array.map _.Name

                if primary.Length > 0 then
                    primary
                else
                    columns
                    |> Array.tryFind (fun column -> String.Equals(column.Name, "id", StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun column -> [| column.Name |])
                    |> Option.defaultValue [||]

            let orderBy =
                if orderedColumns.Length = 0 then
                    ""
                else
                    " ORDER BY " + (orderedColumns |> Array.map quoteIdentifier |> String.concat ", ")

            command.CommandText <- sprintf "SELECT * FROM %s%s" (quoteIdentifier table) orderBy
            command.CommandTimeout <- timeoutSeconds
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds))
            use! reader = command.ExecuteReaderAsync(timeout.Token)
            let digest = DataDigestBuilder()

            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync(timeout.Token)

                if hasRow then
                    let cells =
                        [| for index in 0 .. reader.FieldCount - 1 -> canonicalCell columns.[index].Type (reader.GetValue index) |]

                    digest.Add(Json.serialize cells)
                else
                    reading <- false

            return digest.Finish()
        }

    let private readTableNames (connection: MySqlConnection) timeoutSeconds =
        task {
            use command = connection.CreateCommand()
            command.CommandText <- "SHOW TABLES"
            command.CommandTimeout <- timeoutSeconds
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(float timeoutSeconds))
            use! reader = command.ExecuteReaderAsync(timeout.Token)
            let names = ResizeArray<string>()

            let mutable reading = true

            while reading do
                let! hasRow = reader.ReadAsync(timeout.Token)

                if hasRow then
                    names.Add(valueString reader 0)
                else
                    reading <- false

            return names.ToArray() |> Array.sort
        }

    /// Removes MySQL's physical implementation details that FSDB does not
    /// need in memory. InnoDB auto-creates a non-unique index for an FK when
    /// no suitable index exists; the harness already ignores that index, so
    /// SHOW COLUMNS' corresponding `Key=MUL` must be normalized in lockstep.
    let normalizeImplementationMetadata
        (columns: ColumnSnapshot array)
        (indexes: IndexSnapshot array)
        (foreignKeys: ForeignKeySnapshot array)
        : ColumnSnapshot array * IndexSnapshot array =
        let fkColumnSets =
            foreignKeys
            |> Array.groupBy _.Name
            |> Array.map (fun (name, members) -> name, members |> Array.sortBy _.Sequence |> Array.map _.Column)

        let retainedIndexes =
            indexes
            |> Array.groupBy _.Name
            |> Array.collect (fun (name, members) ->
                let ordered = members |> Array.sortBy _.Sequence
                let indexColumns = ordered |> Array.map _.Column

                let implementationFk =
                    not ordered.[0].Unique
                    && (fkColumnSets
                        |> Array.exists (fun (foreignKeyName, foreignKeyColumns) ->
                            String.Equals(name, foreignKeyName, StringComparison.OrdinalIgnoreCase)
                            && foreignKeyColumns = indexColumns))

                if implementationFk then [||] else ordered)
            |> Array.sortBy (fun index -> index.Name.ToLowerInvariant(), index.Sequence)

        let retainedLeadingColumns =
            retainedIndexes
            |> Array.filter (fun index -> index.Sequence = 1)
            |> Array.map (fun index -> index.Column.ToLowerInvariant())
            |> Set.ofArray

        let normalizedColumns =
            columns
            |> Array.map (fun column ->
                if column.Key = "MUL" && not (Set.contains (column.Name.ToLowerInvariant()) retainedLeadingColumns) then
                    { column with Key = "" }
                else
                    column)

        normalizedColumns, retainedIndexes

    let snapshot (connection: MySqlConnection) timeoutSeconds =
        task {
            let! names = readTableNames connection timeoutSeconds
            let tables = ResizeArray<TableSnapshot>()

            for table in names do
                let! columns = readColumns connection timeoutSeconds table
                let! indexes = readIndexes connection timeoutSeconds table
                let! foreignKeys = readForeignKeys connection timeoutSeconds table
                let! rowCount, dataHash, dataChunks, rows = readData connection timeoutSeconds table columns

                let columns, indexes = normalizeImplementationMetadata columns indexes foreignKeys

                tables.Add
                    { Name = table
                      Columns = columns
                      Indexes = indexes
                      ForeignKeys = foreignKeys |> Array.sortBy (fun key -> key.Name, key.Sequence)
                      RowCount = rowCount
                      DataSha256 = dataHash
                      DataChunks = dataChunks
                      Rows = rows }

            return { Tables = tables.ToArray() }
        }

[<RequireQualifiedAccess>]
module ScenarioProbes =
    let all =
        function
        | Scalar ->
            [| "scalar_bounds",
               "SELECT COUNT(*) AS row_count, MIN(id) AS min_id, MAX(id) AS max_id FROM scalar_matrix"
               "scalar_values",
               "SELECT id, exact_value, approximate_value, optional_text, defaulted_text FROM scalar_matrix ORDER BY id LIMIT 16" |]
        | Relational ->
            [| "project_tenant_orphans",
               "SELECT COUNT(*) AS orphan_count FROM projects AS p LEFT JOIN tenants AS t ON p.tenant_id = t.id WHERE t.id IS NULL"
               "task_assignee_orphans",
               "SELECT COUNT(*) AS orphan_count FROM tasks AS t LEFT JOIN users AS u ON t.assignee_id = u.id WHERE t.assignee_id IS NOT NULL AND u.id IS NULL"
               "membership_roles",
               "SELECT role, COUNT(*) AS member_count FROM memberships GROUP BY role ORDER BY role" |]
        | Commerce ->
            [| "order_totals",
               "SELECT status, COUNT(*) AS order_count, SUM(total) AS total_amount FROM orders GROUP BY status ORDER BY status"
               "item_order_orphans",
               "SELECT COUNT(*) AS orphan_count FROM order_items AS i LEFT JOIN orders AS o ON i.order_id = o.id WHERE o.id IS NULL"
               "extended_prices",
               "SELECT order_id, SUM(quantity * unit_price) AS extended_amount FROM order_items GROUP BY order_id ORDER BY order_id LIMIT 16" |]
        | Volume ->
            [| "volume_bounds",
               "SELECT COUNT(*) AS row_count, MIN(id) AS min_id, MAX(id) AS max_id, SUM(signed_value) AS signed_sum FROM volume_rows"
               "volume_groups",
               "SELECT active, COUNT(*) AS row_count, SUM(exact_value) AS exact_sum FROM volume_rows GROUP BY active ORDER BY active"
               "volume_edges",
               "SELECT id, bucket, signed_value, exact_value, active, payload FROM volume_rows ORDER BY id LIMIT 16" |]

[<RequireQualifiedAccess>]
module Comparison =
    let empty =
        { Equal = false
          Category = "not_run"
          Detail = "comparison did not run"
          MySql = { Tables = [||] }
          Fsdb = { Tables = [||] } }

    let private firstTableDifference (mysql: TableSnapshot) (fsdb: TableSnapshot) =
        if mysql.Columns <> fsdb.Columns then
            Some("schema_mismatch", sprintf "table %s columns differ" mysql.Name)
        elif mysql.Indexes <> fsdb.Indexes then
            Some("schema_mismatch", sprintf "table %s indexes differ" mysql.Name)
        elif mysql.ForeignKeys <> fsdb.ForeignKeys then
            Some("schema_mismatch", sprintf "table %s foreign keys differ" mysql.Name)
        elif mysql.RowCount <> fsdb.RowCount then
            Some("row_count_mismatch", sprintf "table %s row counts differ: mysql=%d fsdb=%d" mysql.Name mysql.RowCount fsdb.RowCount)
        elif mysql.DataSha256 <> fsdb.DataSha256 then
            let sharedChunks = min mysql.DataChunks.Length fsdb.DataChunks.Length

            let mismatch =
                seq { 0 .. sharedChunks - 1 }
                |> Seq.tryFind (fun index -> mysql.DataChunks.[index].Sha256 <> fsdb.DataChunks.[index].Sha256)
                |> Option.defaultValue sharedChunks

            Some(
                "data_mismatch",
                sprintf
                    "table %s ordered data differs in chunk %d: mysql=%A fsdb=%A; bounded samples mysql=%A fsdb=%A"
                    mysql.Name
                    mismatch
                    (Array.tryItem mismatch mysql.DataChunks)
                    (Array.tryItem mismatch fsdb.DataChunks)
                    mysql.Rows
                    fsdb.Rows
            )
        else
            None

    let compare mysql fsdb =
        let mysqlNames = mysql.Tables |> Array.map _.Name
        let fsdbNames = fsdb.Tables |> Array.map _.Name

        if mysqlNames <> fsdbNames then
            { Equal = false
              Category = "schema_mismatch"
              Detail = sprintf "table names differ: mysql=%A fsdb=%A" mysqlNames fsdbNames
              MySql = mysql
              Fsdb = fsdb }
        else
            let difference =
                Array.zip mysql.Tables fsdb.Tables
                |> Array.tryPick (fun (left, right) -> firstTableDifference left right)

            match difference with
            | Some(category, detail) ->
                { Equal = false
                  Category = category
                  Detail = detail
                  MySql = mysql
                  Fsdb = fsdb }
            | None ->
                { Equal = true
                  Category = "pass"
                  Detail = "schemas, row counts, and deterministically ordered typed rows match"
                  MySql = mysql
                  Fsdb = fsdb }

[<RequireQualifiedAccess>]
module Runner =
    let private writeText (path: string) (text: string) = File.WriteAllText(path, text, UTF8Encoding(false))

    let private evidenceSql (sql: string) =
        let half = 16384

        if sql.Length <= half * 2 then
            sql
        else
            sql.Substring(0, half)
            + sprintf "\n/* ... %d UTF-16 characters omitted; generated.sql + byte range + SHA-256 are authoritative ... */\n" (sql.Length - half * 2)
            + sql.Substring(sql.Length - half)

    let failureSignature classification statementHash mysqlError fsdbError detail =
        Hashing.combine [ classification; statementHash; string mysqlError; string fsdbError; detail ]

    let classifyProbe (parserStatus: string) (mysql: ProbeOutcome) (fsdb: ProbeOutcome) =
        if not (ProbeOutcome.succeeded mysql) then
            if mysql.Status = "timeout" then "oracle_timeout"
            elif mysql.Status = "driver_error" then "infrastructure"
            else "oracle_rejected"
        elif parserStatus = "error" then
            "fsdb_probe_parser_gap"
        elif not (ProbeOutcome.succeeded fsdb) then
            if fsdb.Status = "timeout" then "fsdb_timeout"
            elif fsdb.Status = "driver_error" then "protocol_fault"
            elif fsdb.ErrorCode = 1105 then "contained_internal_error"
            else "fsdb_probe_execution_gap"
        elif mysql.Columns <> fsdb.Columns then
            "probe_schema_mismatch"
        elif mysql.DataSha256 <> fsdb.DataSha256 then
            "probe_result_mismatch"
        else
            "pass"

    let private probeDetail name parserDetail (mysql: ProbeOutcome) (fsdb: ProbeOutcome) classification =
        match classification with
        | "pass" -> sprintf "probe %s columns and ordered rows match" name
        | "fsdb_probe_parser_gap" -> parserDetail
        | "oracle_timeout"
        | "oracle_rejected"
        | "infrastructure" -> mysql.Message
        | "fsdb_timeout"
        | "protocol_fault"
        | "contained_internal_error"
        | "fsdb_probe_execution_gap" -> fsdb.Message
        | "probe_schema_mismatch" -> sprintf "probe %s columns differ: mysql=%A fsdb=%A" name mysql.Columns fsdb.Columns
        | "probe_result_mismatch" ->
            let sharedLength = min mysql.Rows.Length fsdb.Rows.Length

            let mismatch =
                seq { 0 .. sharedLength - 1 }
                |> Seq.tryFind (fun index -> mysql.Rows.[index] <> fsdb.Rows.[index])
                |> Option.defaultValue sharedLength

            sprintf
                "probe %s ordered rows differ at %d: mysql=%A fsdb=%A (counts %d/%d)"
                name
                mismatch
                (Array.tryItem mismatch mysql.Rows)
                (Array.tryItem mismatch fsdb.Rows)
                mysql.Rows.Length
                fsdb.Rows.Length
        | other -> sprintf "probe %s failed as %s" name other

    let private loadKnownGaps () =
        try
            let path = Path.Combine(Paths.tortureRoot (), "support", "known-gaps.json")
            let parsed = Json.deserialize<KnownGapFile>(File.ReadAllText path)

            if parsed.SchemaVersion <> 1 then
                Error(sprintf "unsupported known-gap schema version %d" parsed.SchemaVersion)
            elif isNull parsed.Signatures then
                Error "known-gap ledger has no signatures array"
            elif
                parsed.Signatures
                |> Array.exists (fun signature ->
                    String.IsNullOrWhiteSpace signature || signature.Length <> 64 || signature |> Seq.exists (Uri.IsHexDigit >> not))
            then
                Error "known-gap ledger contains a value that is not a SHA-256 signature"
            else
                Ok(HashSet<string>(parsed.Signatures, StringComparer.Ordinal))
        with error ->
            Error("could not read known-gap ledger: " + error.Message)

    let private generate (options: RunOptions) (caseDirectory: string) =
        task {
            let model = Paths.scenarioModel options.Scenario
            let output = Path.Combine(caseDirectory, "generated.sql")
            let resolved = Path.Combine(caseDirectory, "resolved-model.yaml")

            let arguments = ResizeArray<string>()

            [ "generate"
              "--config"
              model
              "--dialect"
              "mysql"
              "--seed"
              string options.Seed ]
            |> List.iter arguments.Add

            if options.Scale <> 1UL then
                arguments.Add "--scale"
                arguments.Add(string options.Scale)

            [ "--max-rows"
              string options.MaxRows
              "--batch-size"
              string options.BatchSize
              "--verify"
              "--json"
              "--emit-config"
              resolved
              "--output"
              output ]
            |> List.iter arguments.Add

            let generationTimeout =
                if options.MaxRows >= 1000000UL || options.Scale > 1UL then
                    TimeSpan.FromMinutes 30.0
                else
                    TimeSpan.FromMinutes 2.0

            let! result = ProcessRunner.run options.SqlSplitter (arguments.ToArray()) (Paths.tortureRoot ()) generationTimeout
            writeText (Path.Combine(caseDirectory, "generate.json")) result.Stdout
            writeText (Path.Combine(caseDirectory, "generate.stderr")) result.Stderr
            return result, model, output
        }

    let classifyStatement (parserStatus: string) (mysql: TargetOutcome) (fsdb: TargetOutcome) (invariantErrors: string array) =
        if not (TargetOutcome.succeeded mysql) then
            if mysql.Status = "timeout" then "oracle_timeout"
            elif mysql.Status = "driver_error" then "infrastructure"
            else "oracle_rejected"
        elif parserStatus = "error" then
            "fsdb_parser_gap"
        elif not (TargetOutcome.succeeded fsdb) then
            if fsdb.Status = "timeout" then "fsdb_timeout"
            elif fsdb.Status = "driver_error" then "protocol_fault"
            elif fsdb.ErrorCode = 1105 then "contained_internal_error"
            else "fsdb_execution_gap"
        elif invariantErrors.Length > 0 then
            "invariant_failure"
        else
            "pass"

    let fsdbConnectionString port =
        sprintf
            "Server=127.0.0.1;Port=%d;User ID=root;Password=;Database=%s;SslMode=None;AllowPublicKeyRetrieval=True"
            port
            Fsdb.Storage.defaultDatabase

    let runScenario (tool: ToolRecord) (options: RunOptions) (runId: string) =
        task {
            let started = DateTimeOffset.UtcNow
            let caseId =
                sprintf
                    "%s-seed%d-scale%d-rows%d-batch%d"
                    (ScenarioName.text options.Scenario)
                    options.Seed
                    options.Scale
                    options.MaxRows
                    options.BatchSize

            let caseDirectory = Path.Combine(options.ArtifactRoot, runId, caseId)
            Directory.CreateDirectory caseDirectory |> ignore
            let! revision, dirty = Tooling.gitState ()
            let assemblyPath = typeof<Fsdb.Storage.Store>.Assembly.Location
            let! generation, modelPath, sqlPath = generate options caseDirectory
            let mutable statements = [||]
            let mutable comparison = Comparison.empty
            let mutable classification = "generator_preflight"
            let mutable signatureDetail = generation.Stdout + Environment.NewLine + generation.Stderr
            let mutable generatedHash = ""
            let mutable generatedBytes = 0L
            let mutable mysqlVersion = ""
            let mutable probes = [||]
            let mutable failureEvidenceHash = ""
            let mutable mysqlLoadElapsedMs = 0L
            let mutable fsdbLoadElapsedMs = 0L
            let mutable invariantElapsedMs = 0L
            let mutable mysqlSnapshotElapsedMs = 0L
            let mutable fsdbSnapshotElapsedMs = 0L

            if generation.ExitCode = 0 && File.Exists sqlPath then
                generatedHash <- Hashing.file sqlPath
                generatedBytes <- FileInfo(sqlPath).Length
                let bytes = File.ReadAllBytes sqlPath

                match SqlScript.splitBytes bytes with
                | Error error ->
                    classification <- "generator_preflight"
                    signatureDetail <- sprintf "script split failed at byte %d: %s" error.ByteOffset error.Message
                | Ok splitStatements ->
                    let databaseName =
                        sprintf
                            "torture_%s_%d_%s"
                            (ScenarioName.text options.Scenario)
                            Environment.ProcessId
                            ((Hashing.text runId).Substring(0, 12))
                        |> fun value -> if value.Length > 60 then value.Substring(0, 60) else value

                    let! oracleDatabase = Database.createOracleDatabase options.MySqlConnection databaseName options.TimeoutSeconds

                    match oracleDatabase with
                    | Error error ->
                        classification <- "infrastructure"
                        signatureDetail <- "could not create oracle database: " + error.Message
                    | Ok oracleConnectionString ->
                        use subject = new FsdbSubject()
                        use! mysql = Database.openConnection oracleConnectionString
                        use! fsdb = Database.openConnection (fsdbConnectionString subject.Port)
                        let! version = Database.scalarString mysql options.TimeoutSeconds "SELECT VERSION()"
                        mysqlVersion <- version
                        let records = ResizeArray<StatementRecord>()
                        let mutable keepRunning = true
                        let mutable lastInvariantStatement = -1

                        for statement in splitStatements do
                            if keepRunning then
                                let parserStatus, astKind =
                                    match Fsdb.Parser.parse statement.Text with
                                    | Ok ast -> "ok", AstKind.ofStatement ast
                                    | Error error -> "error", error

                                let! mysqlOutcome = Database.execute "mysql" mysql options.TimeoutSeconds statement.Text
                                mysqlLoadElapsedMs <- mysqlLoadElapsedMs + mysqlOutcome.ElapsedMs

                                let! fsdbOutcome =
                                    if TargetOutcome.succeeded mysqlOutcome && parserStatus = "ok" then
                                        Database.execute "fsdb" fsdb options.TimeoutSeconds statement.Text
                                    else
                                        Task.FromResult(TargetOutcome.notRun "fsdb")

                                fsdbLoadElapsedMs <- fsdbLoadElapsedMs + fsdbOutcome.ElapsedMs

                                let commitEvents = subject.DrainEvents()

                                let invariantErrors =
                                    if
                                        TargetOutcome.succeeded fsdbOutcome
                                        && options.InvariantEvery > 0
                                        && (records.Count + 1) % options.InvariantEvery = 0
                                    then
                                        let stopwatch = Stopwatch.StartNew()
                                        let errors = Invariants.validate subject.Store
                                        stopwatch.Stop()
                                        invariantElapsedMs <- invariantElapsedMs + stopwatch.ElapsedMilliseconds
                                        lastInvariantStatement <- records.Count
                                        errors
                                    else
                                        [||]

                                let record =
                                    { Index = statement.Index
                                      StartByte = statement.StartByte
                                      EndByte = statement.EndByte
                                      Sha256 = statement.Sha256
                                      Sql = evidenceSql statement.Text
                                      ParserStatus = parserStatus
                                      AstKind = astKind
                                      MySql = mysqlOutcome
                                      Fsdb = fsdbOutcome
                                      CommitEvents = commitEvents
                                      InvariantErrors = invariantErrors }

                                records.Add record
                                let result = classifyStatement parserStatus mysqlOutcome fsdbOutcome invariantErrors

                                if result <> "pass" then
                                    classification <- result
                                    failureEvidenceHash <- statement.Sha256
                                    signatureDetail <-
                                        if parserStatus = "error" then astKind
                                        elif not (TargetOutcome.succeeded mysqlOutcome) then mysqlOutcome.Message
                                        elif not (TargetOutcome.succeeded fsdbOutcome) then fsdbOutcome.Message
                                        else String.concat "; " invariantErrors

                                    writeText (Path.Combine(caseDirectory, "failure.sql")) statement.Text
                                    keepRunning <- false

                        if keepRunning && records.Count > 0 && lastInvariantStatement <> records.Count - 1 then
                            let stopwatch = Stopwatch.StartNew()
                            let invariantErrors = Invariants.validate subject.Store
                            stopwatch.Stop()
                            invariantElapsedMs <- invariantElapsedMs + stopwatch.ElapsedMilliseconds

                            if invariantErrors.Length > 0 then
                                let lastIndex = records.Count - 1
                                let record = records.[lastIndex]
                                records.[lastIndex] <- { record with InvariantErrors = invariantErrors }
                                classification <- "invariant_failure"
                                failureEvidenceHash <- record.Sha256
                                signatureDetail <- String.concat "; " invariantErrors
                                writeText (Path.Combine(caseDirectory, "failure.sql")) splitStatements.[lastIndex].Text
                                keepRunning <- false

                        statements <- records.ToArray()
                        Json.write (Path.Combine(caseDirectory, "statements.json")) statements

                        if keepRunning then
                            let probeRecords = ResizeArray<ProbeRecord>()

                            for name, sql in ScenarioProbes.all options.Scenario do
                                if keepRunning then
                                    let parserStatus, parserDetail =
                                        match Fsdb.Parser.parse sql with
                                        | Ok _ -> "ok", ""
                                        | Error error -> "error", error

                                    let! mysqlOutcome = Database.query "mysql" mysql options.TimeoutSeconds sql

                                    let! fsdbOutcome =
                                        if ProbeOutcome.succeeded mysqlOutcome && parserStatus = "ok" then
                                            Database.query "fsdb" fsdb options.TimeoutSeconds sql
                                        else
                                            Task.FromResult(ProbeOutcome.notRun "fsdb")

                                    let result = classifyProbe parserStatus mysqlOutcome fsdbOutcome
                                    let detail = probeDetail name parserDetail mysqlOutcome fsdbOutcome result

                                    probeRecords.Add
                                        { Name = name
                                          Sql = sql
                                          SqlSha256 = Hashing.text sql
                                          ParserStatus = parserStatus
                                          ParserDetail = parserDetail
                                          MySql = mysqlOutcome
                                          Fsdb = fsdbOutcome
                                          Equal = result = "pass"
                                          Detail = detail }

                                    if result <> "pass" then
                                        classification <- result
                                        failureEvidenceHash <- Hashing.text sql
                                        signatureDetail <- detail
                                        writeText (Path.Combine(caseDirectory, "failure.sql")) sql
                                        keepRunning <- false

                            probes <- probeRecords.ToArray()
                            Json.write (Path.Combine(caseDirectory, "probes.json")) probes

                        if keepRunning then
                            let snapshotStopwatch = Stopwatch.StartNew()

                            let! mysqlSnapshot =
                                task {
                                    try
                                        let! snapshot = Database.snapshot mysql options.TimeoutSeconds
                                        return Ok snapshot
                                    with error ->
                                        return Error error
                                }

                            snapshotStopwatch.Stop()
                            mysqlSnapshotElapsedMs <- snapshotStopwatch.ElapsedMilliseconds

                            match mysqlSnapshot with
                            | Error error ->
                                classification <- "infrastructure"
                                signatureDetail <- "MySQL snapshot failed: " + error.ToString()
                            | Ok mysqlSnapshot ->
                                snapshotStopwatch.Restart()

                                let! fsdbSnapshot =
                                    task {
                                        try
                                            let! snapshot = Database.snapshot fsdb options.TimeoutSeconds
                                            return Ok snapshot
                                        with error ->
                                            return Error error
                                    }

                                snapshotStopwatch.Stop()
                                fsdbSnapshotElapsedMs <- snapshotStopwatch.ElapsedMilliseconds

                                match fsdbSnapshot with
                                | Error error ->
                                    classification <- "metadata_or_snapshot_failure"
                                    signatureDetail <- "FSDB snapshot failed: " + error.ToString()
                                | Ok fsdbSnapshot ->
                                    comparison <- Comparison.compare mysqlSnapshot fsdbSnapshot
                                    classification <- comparison.Category
                                    signatureDetail <- comparison.Detail

            let statementHash, mysqlError, fsdbError =
                if failureEvidenceHash <> "" then
                    let mysqlError, fsdbError =
                        match Array.tryLast probes with
                        | Some probe when not probe.Equal -> probe.MySql.ErrorCode, probe.Fsdb.ErrorCode
                        | _ ->
                            match Array.tryLast statements with
                            | Some statement -> statement.MySql.ErrorCode, statement.Fsdb.ErrorCode
                            | None -> 0, 0

                    failureEvidenceHash, mysqlError, fsdbError
                else
                    match Array.tryLast statements with
                    | Some statement -> statement.Sha256, statement.MySql.ErrorCode, statement.Fsdb.ErrorCode
                    | None -> "", 0, 0

            let subjectFailureSignature =
                if classification = "pass" then "" else failureSignature classification statementHash mysqlError fsdbError signatureDetail

            let classification, finalFailureSignature, known, finalDetail =
                match loadKnownGaps () with
                | Ok knownGaps ->
                    classification,
                    subjectFailureSignature,
                    subjectFailureSignature <> "" && knownGaps.Contains subjectFailureSignature,
                    signatureDetail
                | Error error ->
                    let detail = "invalid harness configuration: " + error
                    "infrastructure", failureSignature "infrastructure" statementHash mysqlError fsdbError detail, false, detail

            let passed = classification = "pass" || known
            let currentProcess = Process.GetCurrentProcess()
            currentProcess.Refresh()

            let manifest =
                { SchemaVersion = 2
                  RunId = runId
                  CaseId = caseId
                  Scenario = ScenarioName.text options.Scenario
                  Seed = options.Seed
                  Scale = options.Scale
                  MaxRows = options.MaxRows
                  BatchSize = options.BatchSize
                  InvariantEvery = options.InvariantEvery
                  TimeoutSeconds = options.TimeoutSeconds
                  StartedUtc = started.ToString("O", CultureInfo.InvariantCulture)
                  FinishedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                  FsdbRevision = revision
                  FsdbDirty = dirty
                  FsdbAssemblySha256 = Hashing.file assemblyPath
                  SqlSplitter = tool
                  MySqlVersion = mysqlVersion
                  ModelPath = Path.GetRelativePath(Paths.repoRoot (), modelPath)
                  ModelSha256 = Hashing.file modelPath
                  GeneratedSqlSha256 = generatedHash
                  GeneratedSqlBytes = generatedBytes
                  GeneratorExitCode = generation.ExitCode
                  GeneratorElapsedMs = generation.ElapsedMs
                  GeneratorTimedOut = generation.TimedOut
                  GeneratorDiagnostics = generation.Stdout + Environment.NewLine + generation.Stderr
                  MySqlLoadElapsedMs = mysqlLoadElapsedMs
                  FsdbLoadElapsedMs = fsdbLoadElapsedMs
                  InvariantElapsedMs = invariantElapsedMs
                  MySqlSnapshotElapsedMs = mysqlSnapshotElapsedMs
                  FsdbSnapshotElapsedMs = fsdbSnapshotElapsedMs
                  PeakWorkingSetBytes = max currentProcess.PeakWorkingSet64 currentProcess.WorkingSet64
                  Statements = statements
                  Probes = probes
                  Comparison = comparison
                  Classification = if known then "known_support_gap" else classification
                  ClassificationDetail = finalDetail
                  FailureSignature = finalFailureSignature
                  KnownGap = known
                  Passed = passed }

            Json.write (Path.Combine(caseDirectory, "manifest.json")) manifest
            Json.write (Path.Combine(caseDirectory, "comparison.json")) comparison
            return manifest, caseDirectory
        }

    let replay tool (caseDirectory: string) mySqlConnection sqlSplitter =
        task {
            let path = Path.Combine(caseDirectory, "manifest.json")
            let original =
                try
                    Ok(Json.deserialize<RunManifest>(File.ReadAllText path))
                with error ->
                    Error(sprintf "could not read replay manifest %s: %s" path error.Message)

            match original with
            | Error error -> return Error error
            | Ok original when original.SchemaVersion <> 1 && original.SchemaVersion <> 2 ->
                return Error(sprintf "unsupported manifest schema version %d" original.SchemaVersion)
            | Ok original ->
                match ScenarioName.parse original.Scenario with
                | Error error -> return Error error
                | Ok scenario ->
                    let options =
                        { Scenario = scenario
                          Seed = original.Seed
                          Scale = if original.Scale = 0UL then 1UL else original.Scale
                          MaxRows = original.MaxRows
                          BatchSize = original.BatchSize
                          InvariantEvery = if original.SchemaVersion = 1 then 1 else original.InvariantEvery
                          TimeoutSeconds = original.TimeoutSeconds
                          ArtifactRoot = Paths.defaultArtifactRoot ()
                          MySqlConnection = mySqlConnection
                          SqlSplitter = sqlSplitter }

                    let! replayed, replayDirectory = runScenario tool options (Paths.uniqueRunId () + "-replay")

                    if original.FailureSignature = replayed.FailureSignature then
                        return Ok(replayed, replayDirectory)
                    else
                        return
                            Error(
                                sprintf
                                    "failure signature changed: expected %s, got %s (report: %s)"
                                    original.FailureSignature
                                    replayed.FailureSignature
                                    replayDirectory
                            )
        }
