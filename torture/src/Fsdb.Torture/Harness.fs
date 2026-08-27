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
        | CreateTableLike _ -> "create_table_like"
        | CreateTableAs _ -> "create_table_as"
        | DropTable _ -> "drop_table"
        | AlterTable _ -> "alter_table"
        | AlterDatabase _ -> "alter_database"
        | RenameTable _ -> "rename_table"
        | CreateIndex _ -> "create_index"
        | DropIndexStmt _ -> "drop_index"
        | Insert _ -> "insert"
        | InsertSelect _ -> "insert_select"
        | Replace _ -> "replace"
        | ReplaceSelect _ -> "replace_select"
        | ReplaceSet _ -> "replace_set"
        | Select _ -> "select"
        | Do _ -> "do"
        | Union _ -> "union"
        | Update _ -> "update"
        | Delete _ -> "delete"
        | Truncate _ -> "truncate"
        | CreateUser _ -> "create_user"
        | DropUser _ -> "drop_user"
        | RenameUser _ -> "rename_user"
        | AlterUser _ -> "alter_user"
        | CreateRole _ -> "create_role"
        | DropRole _ -> "drop_role"
        | Grant _ -> "grant"
        | Revoke _ -> "revoke"
        | CreateTrigger _ -> "create_trigger"
        | DropTrigger _ -> "drop_trigger"
        | CreateView _ -> "create_view"
        | DropView _ -> "drop_view"
        | SetTriggerNew _ -> "set_trigger_new"
        | ChecksumTables _ -> "checksum_table"
        | Explain(_, statement) -> "explain_" + ofStatement statement

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
        | SchemaChangedAt(db, statement, createTime) ->
            sprintf "schema_changed db=%s statement=%s create_time=%s" db (AstKind.ofStatement statement) (createTime.ToString("O", CultureInfo.InvariantCulture))
        | TransactionCommitted events ->
            sprintf "transaction_committed count=%d hash=%s" events.Length (events |> Seq.map summarize |> Hashing.combine)

type FsdbSubject(?captureEvents: bool) =
    let store = Fsdb.Storage.create ()
    let events = ConcurrentQueue<string>()

    do
        if defaultArg captureEvents true then
            store.OnCommit.Add(CommitEvents.summarize >> events.Enqueue)

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
            // Same "I" prefix as VInt, matching Storage's own unique-key
            // encoder: a BIGINT UNSIGNED and a BIGINT holding the same
            // number must key alike or this invariant check disagrees with
            // the index it is checking.
            | VUInt value -> "I" + string (decimal value)
            | VBit(_, value) -> "I" + string (decimal value)
            | VDouble value -> "D" + (if value = 0.0 then 0.0 else value).ToString("R", CultureInfo.InvariantCulture)
            | VDecimal value -> "M" + value.ToString("G29", CultureInfo.InvariantCulture)
            | VString value -> "S" + value.TrimEnd(' ').ToUpperInvariant()
            | VBytes value -> "B" + Convert.ToHexString value
            | VDate value -> "T" + string value.DayNumber
            | VDateTime value -> "V" + string value.Ticks
            | VTime value -> "H" + string (Fsdb.Temporal.timeTicks value)
            | VZeroDate value -> "Z" + Fsdb.Temporal.formatZeroDate value
            | VZeroDateTime value -> "W" + Fsdb.Temporal.formatZeroDateTime value
            | VJson value -> "J" + value.TrimEnd(' ').ToUpperInvariant()
            | VGeometry value -> "G" + Convert.ToBase64String(Fsdb.Value.geometryToMySqlBinary value)

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
                      ColumnTypes = declaredTypes
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
               "SELECT id, exact_value, approximate_value, optional_text, defaulted_text FROM scalar_matrix ORDER BY id LIMIT 16"
               // JSON_TABLE's supported subset (correlated/lateral comma-join,
               // FOR ORDINALITY, INT-path coercion): the document is built by
               // CONCAT per left row so the expansion is deterministic and
               // non-empty regardless of what the json_value generator emits.
               "json_table_lateral",
               "SELECT s.id, jt.ord, jt.n FROM scalar_matrix AS s, JSON_TABLE(CONCAT('[', s.signed_tiny, ',', s.unsigned_tiny, ']'), '$[*]' COLUMNS (ord FOR ORDINALITY, n INT PATH '$')) AS jt ORDER BY s.id, jt.ord LIMIT 16"
               // Inner-drop semantics over real generated JSON documents: a
               // doc with no `$[*]` match (objects, scalars) contributes zero
               // combinations, so both engines must agree on the drop count.
               "json_table_inner_drop",
               "SELECT COUNT(*) AS expanded FROM scalar_matrix AS s, JSON_TABLE(s.json_value, '$[*]' COLUMNS (v VARCHAR(255) PATH '$')) AS jt"

               // NULL semantics: three-valued logic, the null-safe operator,
               // NULL-aware aggregates, sort placement, and propagation through
               // scalar functions.
               "null_three_valued_logic",
               "SELECT (NULL AND 0) AS and_false, (NULL AND 1) AS and_unknown, (NULL OR 1) AS or_true, (NULL OR 0) AS or_unknown, (NOT NULL) AS not_unknown, (NULL XOR 1) AS xor_unknown"
               "null_equality_vs_null_safe",
               "SELECT (NULL = NULL) AS eq_null, (NULL <> NULL) AS ne_null, (NULL <=> NULL) AS safe_both, (1 <=> NULL) AS safe_left, (NULL <=> 0) AS safe_right, ('a' <=> 'a') AS safe_equal"
               "null_is_null_counts",
               "SELECT COUNT(*) AS total_rows, SUM(optional_text IS NULL) AS text_null, SUM(optional_text IS NOT NULL) AS text_not_null, SUM(blob_value IS NULL) AS blob_null, COUNT(optional_text) AS text_counted, COUNT(blob_value) AS blob_counted FROM scalar_matrix"
               "null_coalesce_ifnull_nullif",
               "SELECT id, COALESCE(optional_text, '<none>') AS coalesced, IFNULL(optional_text, 'fallback') AS fallback, NULLIF(defaulted_text, 'pending') AS nullified, NULLIF(signed_tiny, signed_tiny) AS always_null, COALESCE(NULL, NULL, signed_tiny) AS third_arg FROM scalar_matrix ORDER BY id LIMIT 20"
               "null_propagates_through_expressions",
               "SELECT id, optional_text IS NULL AS was_null, LENGTH(optional_text) AS len_null, CONCAT('x', optional_text) AS concat_null, CONCAT_WS('-', 'x', optional_text) AS concat_ws_skips, signed_tiny + NULL AS arith_null, UPPER(optional_text) AS upper_null FROM scalar_matrix ORDER BY id LIMIT 20"
               "null_ordering_ascending_first",
               "SELECT id, optional_text FROM scalar_matrix ORDER BY optional_text ASC, id ASC LIMIT 12"
               "null_ordering_descending_last",
               "SELECT id, optional_text FROM scalar_matrix ORDER BY optional_text DESC, id ASC LIMIT 12"
               "null_in_and_not_in_lists",
               "SELECT COUNT(*) AS in_with_null, (SELECT COUNT(*) FROM scalar_matrix WHERE signed_tiny NOT IN (1, NULL)) AS not_in_with_null, (SELECT COUNT(*) FROM scalar_matrix WHERE optional_text IN ('nope', NULL)) AS text_in_null FROM scalar_matrix WHERE signed_tiny IN (1, NULL)"
               "null_case_and_between_semantics",
               "SELECT id, CASE WHEN optional_text = 'never-matches' THEN 'hit' ELSE 'else' END AS case_null_falls_else, CASE optional_text WHEN NULL THEN 'null-branch' ELSE 'else' END AS case_value_null, (optional_text BETWEEN 'a' AND 'z') AS between_null, (blob_value IS NULL) XOR (optional_text IS NULL) AS xor_nulls FROM scalar_matrix ORDER BY id LIMIT 20"
               "null_aggregate_ignores_nulls",
               "SELECT COUNT(*) AS rows_all, COUNT(optional_text) AS non_null_text, MIN(optional_text) AS min_text, MAX(optional_text) AS max_text, ROUND(AVG(NULLIF(signed_tiny, 0)), 6) AS avg_skipping_zero, SUM(NULLIF(unsigned_tiny, 0)) AS sum_skipping_zero FROM scalar_matrix"

               // Integer domains and promotion: signed/unsigned tinyint bounds,
               // 64-bit widening, DIV/MOD sign rules, and division by zero.
               "integer_signedness_bounds",
               "SELECT MIN(signed_tiny) AS min_signed_tiny, MAX(signed_tiny) AS max_signed_tiny, MIN(unsigned_tiny) AS min_unsigned_tiny, MAX(unsigned_tiny) AS max_unsigned_tiny, SUM(signed_tiny) AS sum_signed_tiny, SUM(unsigned_tiny) AS sum_unsigned_tiny, MIN(signed_big) AS min_big, MAX(signed_big) AS max_big FROM scalar_matrix"
               "unsigned_cast_wraparound",
               "SELECT CAST(-1 AS UNSIGNED) AS neg_one_unsigned, CAST(-9223372036854775808 AS UNSIGNED) AS min_big_unsigned, CAST(CAST(18446744073709551615 AS UNSIGNED) AS SIGNED) AS round_trip_signed, CAST(255 AS UNSIGNED) + 1 AS unsigned_add"
               "signed_unsigned_mixed_arithmetic",
               "SELECT id, CAST(unsigned_tiny AS SIGNED) - 300 AS widened_negative, CAST(signed_tiny AS SIGNED) * 1000000 AS widened_product, CAST(unsigned_tiny AS SIGNED) + signed_tiny AS mixed_sum, ABS(signed_tiny) AS abs_tiny, -signed_tiny AS negated FROM scalar_matrix ORDER BY id LIMIT 16"
               "bigint_boundary_arithmetic",
               "SELECT 9223372036854775807 AS max_bigint, 9223372036854775807 - 1 AS max_minus_one, -9223372036854775807 - 1 AS min_bigint, 9223372036854775807 DIV 2 AS half_max, SUM(signed_big) AS sum_big, ROUND(AVG(signed_big), 4) AS avg_big FROM scalar_matrix"
               "integer_division_modulo_signs",
               "SELECT id, signed_integer DIV 7 AS int_div, signed_integer % 7 AS pct_mod, MOD(signed_integer, -7) AS mod_negative_divisor, -signed_integer DIV 7 AS neg_div, signed_integer / 7 AS decimal_div FROM scalar_matrix ORDER BY id LIMIT 16"
               "division_by_zero_yields_null",
               "SELECT 1 / 0 AS div_zero, 1 DIV 0 AS int_div_zero, MOD(1, 0) AS mod_zero, 1 % 0 AS pct_zero, 0.0 / 0 AS zero_over_zero, NULLIF(5, 5) / 2 AS null_numerator"

               // Exact DECIMAL vs approximate DOUBLE: scale propagation,
               // rounding mode differences, and formatting.
               "decimal_exact_aggregation",
               "SELECT COUNT(*) AS row_count, SUM(exact_value) AS exact_sum, MIN(exact_value) AS exact_min, MAX(exact_value) AS exact_max, ROUND(AVG(exact_value), 10) AS exact_avg FROM scalar_matrix"
               "decimal_division_scale_increment",
               "SELECT id, exact_value / 3 AS div_three, exact_value / 7 AS div_seven, exact_value * 3 AS mul_three, exact_value + 0.1 AS plus_tenth FROM scalar_matrix ORDER BY id LIMIT 12"
               "decimal_round_truncate_floor",
               "SELECT id, ROUND(exact_value, 2) AS round_two, ROUND(exact_value, 0) AS round_zero, ROUND(exact_value, -2) AS round_hundreds, TRUNCATE(exact_value, 2) AS trunc_two, FLOOR(exact_value) AS floored, CEILING(exact_value) AS ceiled, ABS(exact_value) AS absolute FROM scalar_matrix ORDER BY id LIMIT 12"
               "decimal_vs_double_rounding_modes",
               "SELECT ROUND(2.5) AS dec_half_up, ROUND(-2.5) AS dec_half_up_neg, ROUND(0.5) AS dec_half_up_small, ROUND(2.5e0) AS dbl_half_even, ROUND(1.5e0) AS dbl_half_even_odd, ROUND(1.005, 2) AS dec_two_places, TRUNCATE(-1.999, 2) AS trunc_negative"
               "double_vs_decimal_comparison",
               "SELECT (0.1 + 0.2 = 0.3) AS decimal_exact, (0.1e0 + 0.2e0 = 0.3e0) AS double_inexact, ROUND(0.1e0 + 0.2e0 - 0.3e0, 20) AS double_residue, COUNT(*) AS gt_count FROM scalar_matrix WHERE exact_value > approximate_value"
               "double_formatting_and_scaling",
               "SELECT id, ROUND(approximate_value, 6) AS rounded, ROUND(approximate_value * 2, 6) AS doubled, ROUND(approximate_value / 4, 8) AS quartered, FORMAT(approximate_value, 2) AS formatted, ROUND(CAST(approximate_value AS DECIMAL(20,4)), 4) AS as_decimal FROM scalar_matrix ORDER BY id LIMIT 12"

               // Implicit coercion, NO PAD trailing spaces, and the
               // case/accent-insensitive default collation.
               "string_to_number_coercion_arithmetic",
               "SELECT '12abc' + 0 AS prefix_digits, '  42  ' + 0 AS padded_number, '3.5' * 2 AS decimal_string, 'abc' + 0 AS no_digits, '1e3' + 0 AS exponent_string, '0x10' + 0 AS hex_looking, '-7.5xyz' + 0 AS negative_prefix, '' + 0 AS empty_string"
               "string_number_comparison_coercion",
               "SELECT ('10' = 10) AS numeric_equal, ('10a' = 10) AS trailing_junk_equal, (' 10' = 10) AS leading_space_equal, ('1e2' = 100) AS exponent_equal, ('abc' = 0) AS junk_equals_zero, ('10' = '10.0') AS string_vs_string, (10 > '9') AS numeric_greater, ('10' > '9') AS lexical_greater"
               "char_trailing_space_comparison",
               "SELECT ('a' = 'a ') AS no_pad_equal, ('a' LIKE 'a ') AS like_trailing_space, LENGTH('a ') AS literal_length, (CAST('a' AS BINARY) = CAST('a ' AS BINARY)) AS binary_compare, ('' = ' ') AS empty_vs_space, STRCMP('a', 'a ') AS strcmp_pad"
               "char_column_padding_vs_varchar",
               "SELECT id, fixed_code, LENGTH(fixed_code) AS char_bytes, CHAR_LENGTH(fixed_code) AS char_chars, (fixed_code = CONCAT(fixed_code, '   ')) AS eq_padded, CONCAT('[', RPAD(fixed_code, 12, '.'), ']') AS padded, LENGTH(short_text) AS var_bytes, CHAR_LENGTH(short_text) AS var_chars FROM scalar_matrix ORDER BY id LIMIT 12"
               "collation_case_and_accent_insensitivity",
               "SELECT ('ABC' = 'abc') AS case_insensitive, ('e' = 'é') AS accent_insensitive, ('a' < 'B') AS mixed_case_order, STRCMP('a', 'B') AS strcmp_mixed, STRCMP('B', 'a') AS strcmp_reverse, (CAST('ABC' AS BINARY) = CAST('abc' AS BINARY)) AS binary_case_sensitive, UPPER('æøå') AS uppered"
               "collation_group_and_order_of_text",
               "SELECT short_text, COUNT(*) AS occurrences, MIN(id) AS first_id FROM scalar_matrix GROUP BY short_text ORDER BY short_text, first_id"

               // LIKE: wildcards, backslash and custom ESCAPE, collation
               // case folding, NULL propagation.
               "like_wildcards_and_escape",
               """SELECT COUNT(*) AS total, SUM(short_text LIKE 'plain%') AS prefix_match, SUM(short_text LIKE '%quote%') AS contains_match, SUM(short_text LIKE '_uote%') AS underscore_match, SUM(short_text LIKE '%\\%') AS backslash_match, SUM(fixed_code LIKE '__##%' ESCAPE '#') AS escaped_hash, SUM(long_text NOT LIKE '%zzzz%') AS not_like_match FROM scalar_matrix"""
               "like_null_and_case_folding",
               "SELECT ('A' LIKE 'a') AS like_case_insensitive, ('a%' LIKE 'a|%' ESCAPE '|') AS escaped_percent, ('ab' LIKE 'a|%' ESCAPE '|') AS escaped_percent_no_match, (NULL LIKE '%') AS null_subject, ('x' LIKE NULL) AS null_pattern, ('' LIKE '%') AS empty_matches_percent, ('' LIKE '_') AS empty_vs_underscore"

               // Temporal: part extraction, DATE_FORMAT specifiers, INTERVAL
               // arithmetic, DATETIME/TIMESTAMP truncation and round-trip.
               "date_part_extraction",
               "SELECT id, YEAR(event_date) AS yr, MONTH(event_date) AS mo, DAY(event_date) AS dy, QUARTER(event_date) AS qtr, DAYOFWEEK(event_date) AS dow_sunday_one, WEEKDAY(event_date) AS weekday_monday_zero, DAYOFYEAR(event_date) AS doy, LAST_DAY(event_date) AS month_end FROM scalar_matrix ORDER BY id LIMIT 16"
               "date_format_specifiers",
               "SELECT id, DATE_FORMAT(event_at, '%Y-%m-%d %H:%i:%s') AS iso_like, DATE_FORMAT(event_at, '%d/%m/%y') AS short_form, DATE_FORMAT(event_at, '%W %M') AS names, DATE_FORMAT(event_at, '%j') AS day_of_year, DATE_FORMAT(event_at, '%p %h') AS twelve_hour, DATE_FORMAT(event_date, '%%Y literal %Y') AS escaped_percent FROM scalar_matrix ORDER BY id LIMIT 12"
               "date_interval_arithmetic",
               "SELECT id, DATE_ADD(event_date, INTERVAL 45 DAY) AS plus_days, DATE_SUB(event_date, INTERVAL 3 MONTH) AS minus_months, event_date + INTERVAL 1 YEAR AS plus_year, DATE_ADD(event_at, INTERVAL 90 MINUTE) AS plus_minutes, DATEDIFF(event_date, '2020-01-01') AS days_since, TIMESTAMPDIFF(MONTH, event_at, '2030-01-01 00:00:00') AS months_until FROM scalar_matrix ORDER BY id LIMIT 16"
               "datetime_component_and_truncation",
               "SELECT id, DATE(event_at) AS date_part, TIME(event_at) AS time_part, EXTRACT(HOUR FROM event_at) AS hour_part, EXTRACT(YEAR_MONTH FROM event_at) AS year_month_part, CAST(event_at AS DATE) AS cast_date, CAST(event_date AS DATETIME) AS cast_datetime, (event_date = DATE(event_at)) AS same_day FROM scalar_matrix ORDER BY id LIMIT 12"
               "timestamp_vs_datetime_columns",
               "SELECT id, DATE_FORMAT(recorded_at, '%Y-%m-%d %H:%i:%s') AS ts_text, DATE_FORMAT(event_at, '%Y-%m-%d %H:%i:%s') AS dt_text, YEAR(recorded_at) AS ts_year, TIMESTAMPDIFF(DAY, recorded_at, event_at) AS day_gap FROM scalar_matrix ORDER BY id LIMIT 12"

               // Explicit CAST: numeric-to-text rendering, scale narrowing, and
               // string-to-number truncation.
               "cast_to_char_and_decimal",
               "SELECT id, CAST(signed_big AS CHAR) AS big_text, CAST(exact_value AS DECIMAL(20,2)) AS narrowed_decimal, CAST(approximate_value AS DECIMAL(20,4)) AS double_to_decimal, CAST(signed_tiny AS CHAR(4)) AS tiny_text, CONCAT(signed_integer, '') AS implicit_text, LENGTH(CAST(signed_big AS CHAR)) AS text_len FROM scalar_matrix ORDER BY id LIMIT 12"
               "cast_string_to_numeric",
               "SELECT CAST('123abc' AS SIGNED) AS prefix_signed, CAST('  -45  ' AS SIGNED) AS padded_signed, CAST('abc' AS SIGNED) AS junk_signed, CAST('12.7' AS SIGNED) AS decimal_string_signed, CAST('12.7' AS DECIMAL(10,2)) AS decimal_string_decimal, CAST('2020-01-05' AS DATE) AS string_date, CAST(1 AS CHAR) AS one_text"

               // BOOLEAN is TINYINT(1): 0/1 storage versus general truthiness.
               "boolean_is_tinyint",
               "SELECT active, COUNT(*) AS row_count, MIN(active + 0) AS as_number, MAX(CAST(active AS SIGNED)) AS cast_signed, SUM(active) AS active_sum, MIN(IF(active, 'yes', 'no')) AS if_text FROM scalar_matrix GROUP BY active ORDER BY active"
               "truth_value_of_nonzero",
               "SELECT (2 = TRUE) AS two_is_true, (1 = TRUE) AS one_is_true, IF(2, 'truthy', 'falsy') AS two_truthy, IF('abc', 'truthy', 'falsy') AS string_truthy, IF('1abc', 'truthy', 'falsy') AS numeric_prefix_truthy, (NOT 2) AS not_two, (TRUE AND 2) AS and_two"

               // Byte-addressed VARBINARY/BLOB, HEX/UNHEX round-trip, and
               // utf8mb4 byte length versus character length.
               "binary_hex_and_lengths",
               "SELECT id, HEX(binary_value) AS hex_value, LENGTH(binary_value) AS byte_len, CHAR_LENGTH(binary_value) AS char_len, HEX(LEFT(binary_value, 4)) AS hex_prefix, (binary_value = binary_value) AS self_equal FROM scalar_matrix ORDER BY id LIMIT 16"
               "blob_null_and_length_distribution",
               "SELECT id, blob_value IS NULL AS is_null, LENGTH(blob_value) AS byte_len, HEX(LEFT(blob_value, 8)) AS hex_prefix, COALESCE(LENGTH(blob_value), -1) AS len_or_sentinel FROM scalar_matrix ORDER BY id LIMIT 16"
               "hex_unhex_round_trip",
               "SELECT id, (UNHEX(HEX(binary_value)) = binary_value) AS round_trips, HEX(UNHEX('00FF10')) AS literal_round_trip, LENGTH(UNHEX(HEX(binary_value))) AS unhexed_len, HEX(CAST(255 AS CHAR)) AS number_hex, HEX('AB') AS text_hex FROM scalar_matrix ORDER BY id LIMIT 12"
               "multibyte_length_vs_char_length",
               "SELECT short_text, LENGTH(short_text) AS byte_len, CHAR_LENGTH(short_text) AS char_len, LENGTH(short_text) - CHAR_LENGTH(short_text) AS extra_bytes, MIN(id) AS first_id FROM scalar_matrix GROUP BY short_text ORDER BY short_text, first_id"

               // JSON: type/validity/shape over the generated documents, canonical
               // serialization, path extraction, and -> versus ->>.
               "json_type_histogram",
               "SELECT JSON_TYPE(json_value) AS json_type, COUNT(*) AS row_count FROM scalar_matrix GROUP BY JSON_TYPE(json_value) ORDER BY json_type"
               "json_valid_length_totals",
               "SELECT COUNT(*) AS row_count, SUM(JSON_VALID(json_value)) AS valid_docs, SUM(JSON_LENGTH(json_value)) AS total_length, SUM(JSON_DEPTH(json_value)) AS total_depth FROM scalar_matrix"
               "json_extract_root_render",
               "SELECT id, CAST(JSON_EXTRACT(json_value, '$') AS CHAR) AS doc_text, JSON_DEPTH(json_value) AS doc_depth FROM scalar_matrix ORDER BY id LIMIT 8"
               "json_arrow_first_element",
               "SELECT id, CAST(json_value->'$[0]' AS CHAR) AS first_json, json_value->>'$[0]' AS first_text FROM scalar_matrix ORDER BY id LIMIT 12"
               "json_unquote_matches_arrow_unquote",
               "SELECT COUNT(*) AS row_count, SUM(JSON_UNQUOTE(JSON_EXTRACT(json_value, '$[0]')) <=> (json_value->>'$[0]')) AS agreeing FROM scalar_matrix"
               "json_keys_render",
               "SELECT id, CAST(JSON_KEYS(json_value) AS CHAR) AS keys_text FROM scalar_matrix WHERE JSON_TYPE(json_value) = 'OBJECT' ORDER BY id LIMIT 8"
               "json_keys_length_totals",
               "SELECT COUNT(*) AS object_rows, SUM(JSON_LENGTH(JSON_KEYS(json_value))) AS total_keys FROM scalar_matrix WHERE JSON_TYPE(json_value) = 'OBJECT'"
               "json_path_wildcards",
               "SELECT id, CAST(JSON_EXTRACT(json_value, '$.*') AS CHAR) AS object_values, CAST(JSON_EXTRACT(json_value, '$[*]') AS CHAR) AS array_values FROM scalar_matrix ORDER BY id LIMIT 12"
               "json_extract_missing_key_is_null",
               "SELECT SUM(JSON_EXTRACT(json_value, '$.__absent') IS NULL) AS missing_key_nulls, SUM(JSON_TYPE(JSON_EXTRACT(json_value, '$')) = 'NULL') AS json_null_docs, SUM(JSON_EXTRACT(json_value, '$[9999]') IS NULL) AS missing_index_nulls FROM scalar_matrix"

               // JSON construction and mutation: type mapping into documents, the
               // SET/INSERT/REPLACE distinction, and left-to-right path application.
               "json_object_array_construction",
               "SELECT id, CAST(JSON_OBJECT('t', signed_tiny, 'd', exact_value, 'n', optional_text, 'a', JSON_ARRAY(active, fixed_code, NULL)) AS CHAR) AS built_doc FROM scalar_matrix ORDER BY id LIMIT 8"
               "json_set_insert_replace_render",
               "SELECT id, CAST(JSON_SET(JSON_OBJECT('a', 1, 'b', JSON_ARRAY(1, 2)), '$.a', signed_tiny, '$.b[0]', short_text, '$.c', unsigned_tiny) AS CHAR) AS set_doc, CAST(JSON_INSERT(JSON_OBJECT('a', 1), '$.a', 9, '$.z', signed_tiny) AS CHAR) AS insert_doc, CAST(JSON_REPLACE(JSON_OBJECT('a', 1), '$.a', signed_tiny, '$.missing', 5) AS CHAR) AS replace_doc FROM scalar_matrix ORDER BY id LIMIT 8"
               "json_remove_multi_path",
               "SELECT id, CAST(JSON_REMOVE(JSON_OBJECT('a', signed_tiny, 'b', 2, 'c', 3), '$.b') AS CHAR) AS removed_doc, JSON_LENGTH(JSON_REMOVE(JSON_ARRAY(1, 2, 3, 4), '$[1]', '$[2]')) AS remaining FROM scalar_matrix ORDER BY id LIMIT 8"

               // JSON comparison: containment, SQL-to-JSON coercion, and MySQL's
               // fixed cross-type ordering precedence.
               "json_contains_constructed",
               """SELECT SUM(JSON_CONTAINS(JSON_OBJECT('a', signed_tiny, 'b', 'x'), CAST('{"b":"x"}' AS JSON))) AS contains_member, SUM(JSON_CONTAINS(JSON_ARRAY(signed_tiny, unsigned_tiny), CAST(unsigned_tiny AS JSON))) AS contains_element, SUM(JSON_CONTAINS(json_value, CAST('null' AS JSON), '$')) AS contains_json_null FROM scalar_matrix"""
               "json_scalar_coercion_compare",
               "SELECT SUM(JSON_EXTRACT(JSON_OBJECT('n', signed_tiny), '$.n') = signed_tiny) AS int_matches, SUM(JSON_EXTRACT(JSON_OBJECT('s', fixed_code), '$.s') = fixed_code) AS string_matches, SUM(JSON_EXTRACT(JSON_OBJECT('d', exact_value), '$.d') = exact_value) AS decimal_matches FROM scalar_matrix"
               "json_unquoted_vs_quoted_compare",
               "SELECT SUM(JSON_UNQUOTE(JSON_EXTRACT(JSON_OBJECT('s', fixed_code), '$.s')) = fixed_code) AS unquoted_matches, SUM(JSON_EXTRACT(JSON_OBJECT('s', fixed_code), '$.s') = fixed_code) AS quoted_matches, SUM(CAST(JSON_UNQUOTE(JSON_EXTRACT(JSON_OBJECT('n', signed_tiny), '$.n')) AS SIGNED) = signed_tiny) AS cast_matches FROM scalar_matrix"
               "json_cross_type_ordering",
               """SELECT CAST('1' AS JSON) < CAST('"a"' AS JSON) AS int_lt_string, CAST('true' AS JSON) > CAST('1' AS JSON) AS bool_gt_int, CAST('null' AS JSON) < CAST('1' AS JSON) AS json_null_lt_int, JSON_TYPE(CAST('1' AS JSON)) AS int_type, JSON_TYPE(CAST('1.5' AS JSON)) AS decimal_type"""

               // JSON_TABLE beyond the currently supported subset: typed paths,
               // NESTED PATH, EXISTS PATH, and ON EMPTY / ON ERROR defaults.
               "json_table_typed_path_columns",
               """SELECT jt.ord, jt.d, jt.s, jt.dt FROM JSON_TABLE(CAST('[{"d":"12.345","s":"hello","dt":"2020-03-04"},{"d":"-1.5","s":"unicode æøå","dt":"1999-12-31"}]' AS JSON), '$[*]' COLUMNS (ord FOR ORDINALITY, d DECIMAL(10,3) PATH '$.d', s VARCHAR(32) PATH '$.s', dt DATE PATH '$.dt')) AS jt ORDER BY jt.ord"""
               "json_table_path_miss_yields_null",
               """SELECT jt.ord, jt.present, jt.absent FROM JSON_TABLE(CAST('[{"a":1},{"b":2}]' AS JSON), '$[*]' COLUMNS (ord FOR ORDINALITY, present INT PATH '$.a', absent INT PATH '$.zzz')) AS jt ORDER BY jt.ord"""
               "json_table_nested_path_unsupported",
               """SELECT jt.a, jt.k FROM JSON_TABLE(CAST('[{"a":1,"kids":[10,11]},{"a":2,"kids":[]}]' AS JSON), '$[*]' COLUMNS (a INT PATH '$.a', NESTED PATH '$.kids[*]' COLUMNS (k INT PATH '$'))) AS jt ORDER BY jt.a, jt.k"""
               "json_table_exists_path_unsupported",
               """SELECT jt.ord, jt.has_a FROM JSON_TABLE(CAST('[{"a":1},{"b":2}]' AS JSON), '$[*]' COLUMNS (ord FOR ORDINALITY, has_a INT EXISTS PATH '$.a')) AS jt ORDER BY jt.ord"""
               "json_table_default_on_empty_error_unsupported",
               """SELECT jt.ord, jt.v FROM JSON_TABLE(CAST('[{"v":1},{"x":2},{"v":"nope"}]' AS JSON), '$[*]' COLUMNS (ord FOR ORDINALITY, v INT PATH '$.v' DEFAULT '7' ON EMPTY DEFAULT '9' ON ERROR)) AS jt ORDER BY jt.ord"""

               // JSON aggregate builder fsdb has not implemented yet. (The
               // MySQL 9 VECTOR builtins are deliberately absent: the 8.4
               // oracle errors on them, so such a probe compares nothing.)
               "json_arrayagg_ordered_subset_unsupported",
               "SELECT JSON_LENGTH(JSON_ARRAYAGG(v)) AS agg_length, CAST(JSON_ARRAYAGG(v) AS CHAR) AS agg_text FROM (SELECT signed_tiny AS v FROM scalar_matrix ORDER BY id LIMIT 3) AS src"

               // Common table expressions, including chained and recursive forms.
               "cte_bucket_summary",
               "WITH bucketed AS (SELECT id, id MOD 8 AS bucket, exact_value FROM scalar_matrix WHERE id <= 200) SELECT bucket, COUNT(*) AS row_count, SUM(exact_value) AS exact_sum FROM bucketed GROUP BY bucket ORDER BY bucket"
               "cte_chained_reference",
               "WITH lo AS (SELECT id, signed_integer FROM scalar_matrix WHERE id <= 50), hi AS (SELECT id, signed_integer FROM lo WHERE signed_integer > 0) SELECT COUNT(*) AS positive_count, MIN(id) AS min_id, MAX(id) AS max_id FROM hi"
               "recursive_integer_series",
               "WITH RECURSIVE seq (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < 12) SELECT n, n * n AS square FROM seq ORDER BY n"

               // Window functions: explicit ROWS/RANGE frames, named windows, the
               // offset and distribution families, and partitioned ranking.
               "window_rows_frame_trailing",
               "SELECT id, signed_integer, SUM(signed_integer) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS trailing_sum FROM scalar_matrix WHERE id <= 12 ORDER BY id"
               "window_range_frame_symmetric",
               "SELECT id, SUM(signed_tiny) OVER (ORDER BY id RANGE BETWEEN 3 PRECEDING AND 3 FOLLOWING) AS window_sum FROM scalar_matrix WHERE id <= 12 ORDER BY id"
               "lag_lead_offset_defaults",
               "SELECT id, LAG(signed_integer, 2, -1) OVER (ORDER BY id) AS lag_two, LEAD(signed_integer, 3, -1) OVER (ORDER BY id) AS lead_three FROM scalar_matrix WHERE id <= 12 ORDER BY id"
               "first_last_nth_value_named_window",
               "SELECT id, FIRST_VALUE(fixed_code) OVER w AS first_code, LAST_VALUE(fixed_code) OVER w AS last_code, NTH_VALUE(fixed_code, 2) OVER w AS second_code FROM scalar_matrix WHERE id <= 10 WINDOW w AS (ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) ORDER BY id"
               "percent_rank_cume_dist_ntile",
               "SELECT id, NTILE(4) OVER (ORDER BY id) AS quartile, ROUND(PERCENT_RANK() OVER (ORDER BY id), 6) AS pct_rank, ROUND(CUME_DIST() OVER (ORDER BY id), 6) AS cume FROM scalar_matrix WHERE id <= 16 ORDER BY id"
               "partitioned_rank_family",
               "SELECT active, id, ROW_NUMBER() OVER (PARTITION BY active ORDER BY id) AS rn, RANK() OVER (PARTITION BY active ORDER BY signed_tiny, id) AS rnk, DENSE_RANK() OVER (PARTITION BY active ORDER BY signed_tiny) AS dense_rnk FROM scalar_matrix WHERE id <= 20 ORDER BY active, id"

               // VALUES ROW(...) as a table constructor.
               "values_table_constructor",
               "SELECT v.n, v.label FROM (VALUES ROW(1,'one'), ROW(2,'two'), ROW(3,'three')) AS v (n, label) ORDER BY v.n"
               "values_joined_to_table",
               "SELECT s.id, v.label, s.fixed_code FROM scalar_matrix AS s JOIN (VALUES ROW(1,'first'), ROW(2,'second'), ROW(3,'third')) AS v (n, label) ON s.id = v.n ORDER BY s.id"

               // INTERSECT/EXCEPT set operations.
               "intersect_set_operation",
               "(SELECT id FROM scalar_matrix WHERE id <= 20) INTERSECT (SELECT id FROM scalar_matrix WHERE id >= 15) ORDER BY id"
               "except_set_operation",
               "(SELECT id FROM scalar_matrix WHERE id <= 20) EXCEPT (SELECT id FROM scalar_matrix WHERE id MOD 2 = 0) ORDER BY id"

               // ICU regexp builtins including the match-type flag argument.
               "regexp_replace_substr_instr",
               "SELECT id, REGEXP_REPLACE(fixed_code, '[0-9]+', '#') AS masked, REGEXP_SUBSTR(fixed_code, '[0-9]+') AS first_digits, REGEXP_INSTR(fixed_code, '[0-9]') AS first_digit_pos FROM scalar_matrix WHERE id <= 12 ORDER BY id"
               "regexp_like_case_insensitive_flag",
               "SELECT COUNT(*) AS matched, SUM(REGEXP_LIKE(short_text, 'UNICODE', 'i')) AS unicode_hits FROM scalar_matrix WHERE REGEXP_LIKE(short_text, '^QUOTE', 'i')"

               // Less-common string and numeric builtins.
               "field_elt_export_set",
               "SELECT id, FIELD(short_text, 'plain text', 'unicode æøå 🚀') AS text_rank, ELT(1 + (id MOD 3), 'alpha', 'beta', 'gamma') AS elt_value, EXPORT_SET(unsigned_tiny, 'Y', 'n', ',', 8) AS bit_display FROM scalar_matrix WHERE id <= 12 ORDER BY id"
               "pad_truncation_and_substring_index",
               "SELECT id, LPAD(fixed_code, 3, '*') AS truncating_lpad, RPAD(short_text, 12, 'xy') AS cycling_rpad, SUBSTRING_INDEX(long_text, ' ', 3) AS first_words, SUBSTRING_INDEX(long_text, ' ', -2) AS last_words FROM scalar_matrix WHERE id <= 12 ORDER BY id"
               "bit_count_crc32_conv",
               "SELECT id, BIT_COUNT(signed_big) AS set_bits, CRC32(fixed_code) AS code_crc, CONV(unsigned_tiny, 10, 16) AS hex_value, CONV(fixed_code, 36, 10) AS base36_value FROM scalar_matrix WHERE id <= 12 ORDER BY id"

               // Calendar, timezone, and compound-INTERVAL builtins.
               "calendar_week_and_makedate",
               "SELECT id, LAST_DAY(event_date) AS month_end, MAKEDATE(YEAR(event_date), 200) AS day_two_hundred, WEEK(event_date, 0) AS week_mode0, WEEK(event_date, 3) AS week_mode3, YEARWEEK(event_date, 1) AS iso_yearweek FROM scalar_matrix WHERE id <= 12 ORDER BY id"
               "convert_tz_numeric_offsets",
               "SELECT id, CONVERT_TZ(event_at, '+00:00', '+05:30') AS shifted_forward, CONVERT_TZ(event_at, '-08:00', '+00:00') AS normalized FROM scalar_matrix WHERE id <= 12 ORDER BY id"
               "interval_arithmetic_varieties",
               "SELECT id, event_date + INTERVAL 1 MONTH AS plus_month, event_at - INTERVAL '1:30' HOUR_MINUTE AS minus_ninety_minutes, DATE_ADD(event_date, INTERVAL '2-3' YEAR_MONTH) AS plus_two_years_three_months, TIMESTAMPADD(QUARTER, 1, event_at) AS plus_quarter, TIMESTAMPDIFF(DAY, event_date, DATE '2030-01-01') AS days_to_2030 FROM scalar_matrix WHERE id <= 12 ORDER BY id"

               // ORDER BY over generated-column-style expressions.
               "expression_order_by_generated_style",
               "SELECT id, fixed_code, exact_value FROM scalar_matrix WHERE id <= 40 ORDER BY ABS(exact_value) DESC, CHAR_LENGTH(REGEXP_REPLACE(fixed_code, '[^0-9]', '')) , id LIMIT 10" |]
        | Relational ->
            [| "project_tenant_orphans",
               "SELECT COUNT(*) AS orphan_count FROM projects AS p LEFT JOIN tenants AS t ON p.tenant_id = t.id WHERE t.id IS NULL"
               "task_assignee_orphans",
               "SELECT COUNT(*) AS orphan_count FROM tasks AS t LEFT JOIN users AS u ON t.assignee_id = u.id WHERE t.assignee_id IS NOT NULL AND u.id IS NULL"
               "membership_roles",
               "SELECT role, COUNT(*) AS member_count FROM memberships GROUP BY role ORDER BY role"

               // Join shapes over the FK graph: inner chains, NULL extension, the
               // ON-versus-WHERE distinction, RIGHT/CROSS/USING/NATURAL, self-join.
               "inner_join_three_table_chain",
               "SELECT t.id AS task_id, p.name AS project_name, te.slug AS tenant_slug FROM tasks AS t JOIN projects AS p ON t.project_id = p.id JOIN tenants AS te ON p.tenant_id = te.id ORDER BY t.id LIMIT 12"
               "left_join_null_extended_assignee",
               "SELECT t.id AS task_id, t.assignee_id, u.display_name, COALESCE(u.email, '<none>') AS email FROM tasks AS t LEFT JOIN users AS u ON u.id = t.assignee_id WHERE t.assignee_id IS NULL ORDER BY t.id LIMIT 10"
               "left_join_on_clause_vs_where_filter",
               "SELECT (SELECT COUNT(*) FROM projects AS p LEFT JOIN tasks AS t ON t.project_id = p.id AND t.status = 'done') AS on_clause_rows, (SELECT COUNT(*) FROM projects AS p LEFT JOIN tasks AS t ON t.project_id = p.id WHERE t.status = 'done') AS where_clause_rows"
               "right_join_projects_without_tasks",
               "SELECT COUNT(*) AS projects_without_tasks FROM tasks AS t RIGHT JOIN projects AS p ON t.project_id = p.id WHERE t.id IS NULL"
               "cross_join_bounded",
               "SELECT a.id AS tenant_id, b.id AS user_id FROM (SELECT id FROM tenants ORDER BY id LIMIT 3) AS a CROSS JOIN (SELECT id FROM users ORDER BY id LIMIT 4) AS b ORDER BY a.id, b.id"
               "join_using_tenant_id",
               "SELECT tenant_id, COUNT(*) AS pair_count FROM projects JOIN memberships USING (tenant_id) GROUP BY tenant_id ORDER BY tenant_id LIMIT 10"
               "natural_join_shared_column",
               "SELECT COUNT(*) AS joined_rows, COUNT(DISTINCT tenant_id) AS distinct_tenants FROM memberships NATURAL JOIN projects"
               "self_join_tasks_same_project",
               "SELECT COUNT(*) AS sibling_pairs FROM tasks AS a JOIN tasks AS b ON a.project_id = b.project_id AND a.id < b.id"

               // Aggregation: COUNT variants, the empty group, GROUP_CONCAT
               // ordering, HAVING, expression keys, and ORDER BY ordinal/alias.
               "count_star_vs_count_col_vs_distinct",
               "SELECT COUNT(*) AS all_rows, COUNT(assignee_id) AS non_null_assignees, COUNT(DISTINCT assignee_id) AS distinct_assignees, COUNT(DISTINCT status) AS distinct_statuses FROM tasks"
               "aggregates_over_empty_set",
               "SELECT COUNT(*) AS row_count, COUNT(budget) AS budget_count, SUM(budget) AS budget_sum, AVG(budget) AS budget_avg, MIN(budget) AS budget_min, MAX(budget) AS budget_max FROM projects WHERE id < 0"
               "group_concat_distinct_ordered",
               "SELECT tenant_id, COUNT(*) AS member_count, GROUP_CONCAT(DISTINCT role ORDER BY role SEPARATOR '|') AS roles FROM memberships GROUP BY tenant_id ORDER BY tenant_id LIMIT 10"
               "group_by_having_task_counts",
               "SELECT project_id, COUNT(*) AS task_count, COUNT(assignee_id) AS assigned_count FROM tasks GROUP BY project_id HAVING COUNT(*) >= 4 AND COUNT(assignee_id) > 0 ORDER BY task_count DESC, project_id LIMIT 20"
               "group_by_expression_modulo",
               "SELECT (project_id % 7) AS bucket, COUNT(*) AS task_count, MIN(id) AS min_id, MAX(id) AS max_id FROM tasks GROUP BY (project_id % 7) ORDER BY bucket"
               "order_by_ordinal_and_alias",
               "SELECT status AS s, COUNT(*) AS n FROM tasks GROUP BY status ORDER BY 2 DESC, s ASC"

               // Subqueries: NOT IN against NULLs, correlated EXISTS as a semi-join,
               // and scalar subqueries in the select list.
               "not_in_subquery_with_nulls",
               "SELECT (SELECT COUNT(*) FROM users AS u WHERE u.id NOT IN (SELECT t.assignee_id FROM tasks AS t)) AS not_in_with_nulls, (SELECT COUNT(*) FROM users AS u WHERE u.id NOT IN (SELECT t.assignee_id FROM tasks AS t WHERE t.assignee_id IS NOT NULL)) AS not_in_null_filtered"
               "correlated_exists_negative_budget",
               "SELECT te.id AS tenant_id, te.slug FROM tenants AS te WHERE EXISTS (SELECT 1 FROM projects AS p WHERE p.tenant_id = te.id AND p.budget < 0) ORDER BY te.id LIMIT 10"
               "scalar_subquery_in_select_list",
               "SELECT p.id AS project_id, (SELECT COUNT(*) FROM tasks AS t WHERE t.project_id = p.id) AS task_count, (SELECT MAX(t.id) FROM tasks AS t WHERE t.project_id = p.id AND t.status = 'done') AS max_done_task FROM projects AS p ORDER BY p.id LIMIT 12"

               // Set operations, OFFSET past the tail, and partitioned ROW_NUMBER.
               "union_all_ordered_over_branches",
               "(SELECT id AS entity_id, 'tenant' AS source FROM tenants WHERE id <= 3) UNION ALL (SELECT id, 'user' FROM users WHERE id <= 3) ORDER BY source, entity_id"
               "limit_offset_tail_boundary",
               "SELECT id, slug FROM tenants ORDER BY id LIMIT 5 OFFSET 998"
               "window_row_number_partition",
               "SELECT t.project_id, t.id AS task_id, ROW_NUMBER() OVER (PARTITION BY t.project_id ORDER BY t.id) AS rn FROM tasks AS t WHERE t.project_id <= 4 ORDER BY t.project_id, t.id"

               // JSON over a nullable column: SQL NULL versus stored JSON null, keys,
               // correlated JSON_TABLE expansion, and the object aggregate.
               "tenant_settings_type_histogram",
               "SELECT JSON_TYPE(settings) AS json_type, COUNT(*) AS row_count FROM tenants GROUP BY JSON_TYPE(settings) ORDER BY json_type"
               "tenant_settings_null_and_length_totals",
               "SELECT SUM(settings IS NULL) AS sql_nulls, SUM(JSON_TYPE(settings) = 'NULL') AS json_nulls, SUM(JSON_VALID(settings)) AS valid_docs, SUM(JSON_LENGTH(settings)) AS total_length FROM tenants"
               "tenant_settings_keys_sample",
               "SELECT slug, CAST(JSON_KEYS(settings) AS CHAR) AS keys_text, settings->>'$.name' AS name_text, JSON_LENGTH(settings) AS member_count FROM tenants WHERE JSON_TYPE(settings) = 'OBJECT' ORDER BY slug LIMIT 10"
               "project_metadata_json_table_expansion",
               "SELECT p.id, jt.ord, jt.v FROM projects AS p, JSON_TABLE(p.metadata, '$[*]' COLUMNS (ord FOR ORDINALITY, v VARCHAR(255) PATH '$')) AS jt ORDER BY p.id, jt.ord LIMIT 20"
               "tenant_settings_objectagg_unsupported",
               "SELECT CAST(JSON_OBJECTAGG(slug, JSON_TYPE(settings)) AS CHAR) AS agg_text FROM (SELECT slug, settings FROM tenants ORDER BY slug LIMIT 3) AS src"

               // LATERAL derived tables, WITH ROLLUP plus GROUPING(), and a CTE
               // carrying a window function.
               "lateral_correlated_aggregate",
               "SELECT p.id, agg.task_count FROM projects AS p, LATERAL (SELECT COUNT(*) AS task_count FROM tasks AS t WHERE t.project_id = p.id) AS agg WHERE p.id <= 10 ORDER BY p.id"
               "lateral_top_one_per_tenant",
               "SELECT t.id AS tenant_id, x.id AS project_id, x.name FROM tenants AS t JOIN LATERAL (SELECT p.id, p.name FROM projects AS p WHERE p.tenant_id = t.id ORDER BY p.id LIMIT 1) AS x ON TRUE WHERE t.id <= 10 ORDER BY t.id, x.id"
               "rollup_with_grouping_flag",
               "SELECT status, COUNT(*) AS task_count, GROUPING(status) AS is_grand_total FROM tasks GROUP BY status WITH ROLLUP ORDER BY GROUPING(status), status"
               // An ENUM is its declaration ordinal in numeric context, but
               // still a label everywhere else — and ROLLUP's materialized
               // group key loses the ENUM type entirely (see the probe above,
               // which sorts lexically where the plain GROUP BY sorts by
               // ordinal).
               "enum_numeric_context",
               "SELECT status, status + 0 AS ordinal, CAST(status AS UNSIGNED) AS cast_ordinal, CAST(status AS CHAR) AS label FROM tasks ORDER BY id LIMIT 20"
               "enum_aggregates_split_numeric_and_label",
               "SELECT SUM(status) AS ordinal_sum, MIN(status) AS smallest_label, MAX(status) AS largest_label, COUNT(DISTINCT status) AS distinct_labels FROM tasks"
               "enum_group_by_sorts_by_ordinal",
               "SELECT status, COUNT(*) AS task_count FROM tasks GROUP BY status ORDER BY status"
               "rollup_two_level_with_expression_key",
               "SELECT role, tenant_id MOD 4 AS tenant_bucket, COUNT(*) AS member_count FROM memberships GROUP BY role, tenant_id MOD 4 WITH ROLLUP ORDER BY GROUPING(role), role, GROUPING(tenant_id MOD 4), tenant_bucket"
               "cte_window_dense_rank_per_tenant",
               "WITH ranked AS (SELECT p.tenant_id, p.id, p.budget, ROW_NUMBER() OVER (PARTITION BY p.tenant_id ORDER BY p.budget DESC, p.id) AS budget_rank FROM projects AS p) SELECT tenant_id, id, budget, budget_rank FROM ranked WHERE budget_rank = 1 AND tenant_id <= 10 ORDER BY tenant_id, id" |]
        | Commerce ->
            [| "order_totals",
               "SELECT status, COUNT(*) AS order_count, SUM(total) AS total_amount FROM orders GROUP BY status ORDER BY status"
               "item_order_orphans",
               "SELECT COUNT(*) AS orphan_count FROM order_items AS i LEFT JOIN orders AS o ON i.order_id = o.id WHERE o.id IS NULL"
               "extended_prices",
               "SELECT order_id, SUM(quantity * unit_price) AS extended_amount FROM order_items GROUP BY order_id ORDER BY order_id LIMIT 16"

               // Join shapes across the order graph, including the payments
               // hierarchy self-join and USING() fan-out.
               "left_join_null_product_orphans",
               "SELECT i.id AS item_id, i.product_id, p.sku, COALESCE(p.name, '<missing>') AS product_name FROM order_items AS i LEFT JOIN products AS p ON p.id = i.product_id WHERE i.product_id IS NULL ORDER BY i.id LIMIT 10"
               "four_table_join_chain",
               "SELECT o.id AS order_id, c.email, p.sku, i.quantity FROM orders AS o JOIN customers AS c ON c.id = o.customer_id JOIN order_items AS i ON i.order_id = o.id JOIN products AS p ON p.id = i.product_id ORDER BY o.id, i.id LIMIT 12"
               "payments_self_join_parent_child",
               "SELECT c.id AS child_id, c.parent_id, c.state AS child_state, p.state AS parent_state FROM payments AS c JOIN payments AS p ON p.id = c.parent_id ORDER BY c.id LIMIT 12"
               "join_using_order_id",
               "SELECT order_id, COUNT(*) AS combination_count FROM order_items JOIN payments USING (order_id) GROUP BY order_id ORDER BY order_id LIMIT 10"

               // Aggregation: an all-NULL group, GROUP_CONCAT ordering, HAVING and
               // GROUP BY over select-list aliases, tuple COUNT(DISTINCT).
               "aggregates_over_all_null_column",
               "SELECT COUNT(*) AS row_count, COUNT(product_id) AS product_count, SUM(product_id) AS product_sum, MIN(product_id) AS product_min, MAX(product_id) AS product_max, AVG(quantity) AS avg_quantity FROM order_items WHERE product_id IS NULL"
               "group_concat_items_ordered",
               "SELECT order_id, GROUP_CONCAT(quantity ORDER BY id SEPARATOR ',') AS quantities FROM order_items WHERE order_id <= 6 GROUP BY order_id ORDER BY order_id"
               "group_by_having_on_alias",
               "SELECT customer_id, COUNT(*) AS order_count, SUM(total) AS total_amount FROM orders GROUP BY customer_id HAVING order_count >= 8 ORDER BY order_count DESC, customer_id LIMIT 20"
               "group_by_case_bucket_alias",
               "SELECT CASE WHEN quantity < 100 THEN 'small' WHEN quantity < 500 THEN 'medium' ELSE 'large' END AS bucket, COUNT(*) AS line_count, SUM(line_total) AS bucket_amount FROM order_items GROUP BY bucket ORDER BY bucket"
               "count_distinct_column_pairs",
               "SELECT COUNT(*) AS all_rows, COUNT(DISTINCT product_id) AS distinct_products, COUNT(DISTINCT order_id, product_id) AS distinct_pairs FROM order_items"

               // Subqueries and derived tables: IN versus EXISTS agreement and NOT IN
               // against a NULL-bearing subquery.
               "in_subquery_vs_correlated_exists",
               "SELECT (SELECT COUNT(*) FROM orders AS o WHERE o.id IN (SELECT p.order_id FROM payments AS p WHERE p.state = 'captured')) AS via_in, (SELECT COUNT(*) FROM orders AS o WHERE EXISTS (SELECT 1 FROM payments AS p WHERE p.order_id = o.id AND p.state = 'captured')) AS via_exists"
               "not_in_with_nullable_product_ids",
               "SELECT (SELECT COUNT(*) FROM products AS p WHERE p.id NOT IN (SELECT i.product_id FROM order_items AS i)) AS not_in_with_nulls, (SELECT COUNT(*) FROM products AS p WHERE p.id NOT IN (SELECT i.product_id FROM order_items AS i WHERE i.product_id IS NOT NULL)) AS not_in_null_filtered"
               "derived_table_top_paid_orders",
               "SELECT d.order_id, d.line_count, d.line_amount FROM (SELECT order_id, COUNT(*) AS line_count, SUM(line_total) AS line_amount FROM order_items GROUP BY order_id) AS d JOIN orders AS o ON o.id = d.order_id WHERE o.status = 'paid' ORDER BY d.line_amount DESC, d.order_id LIMIT 10"

               // Set operations, DISTINCT tuples, tail paging, and partitioned ranking.
               "union_dedup_vs_union_all",
               "SELECT 'union' AS kind, COUNT(*) AS label_count FROM (SELECT state AS label FROM payments UNION SELECT status FROM orders) AS u UNION ALL SELECT 'union_all', COUNT(*) FROM (SELECT state AS label FROM payments UNION ALL SELECT status FROM orders) AS a ORDER BY kind"
               "distinct_multi_column_projection",
               "SELECT DISTINCT customer_id, status FROM orders ORDER BY customer_id, status LIMIT 20"
               "limit_offset_last_page",
               "SELECT id, reference, status FROM orders ORDER BY id LIMIT 5 OFFSET 3998"
               "order_by_ordinal_descending_group",
               "SELECT status AS s, COUNT(*) AS n, ROUND(AVG(total), 4) AS avg_total FROM orders GROUP BY status ORDER BY 1 DESC"
               "window_rank_dense_rank_partition",
               "SELECT i.order_id, i.id AS item_id, i.quantity, RANK() OVER (PARTITION BY i.order_id ORDER BY i.quantity DESC) AS quantity_rank, DENSE_RANK() OVER (PARTITION BY i.order_id ORDER BY i.quantity DESC) AS quantity_dense_rank FROM order_items AS i WHERE i.order_id <= 4 ORDER BY i.order_id, i.id"

               // JSON over the commerce columns: shape histogram, rendering,
               // containment, mutation, wildcard counts, and the array aggregate.
               "product_attributes_type_histogram",
               "SELECT JSON_TYPE(attributes) AS json_type, COUNT(*) AS row_count, SUM(JSON_LENGTH(attributes)) AS total_length FROM products GROUP BY JSON_TYPE(attributes) ORDER BY json_type"
               "customer_profile_extract_sample",
               "SELECT id, CAST(JSON_EXTRACT(profile, '$') AS CHAR) AS profile_text, profile->>'$.tier' AS tier_text, JSON_LENGTH(profile) AS profile_length FROM customers ORDER BY id LIMIT 10"
               "shipping_address_contains_totals",
               """SELECT SUM(JSON_CONTAINS(shipping_address, CAST('"US"' AS JSON), '$')) AS contains_us, SUM(JSON_LENGTH(shipping_address)) AS total_length, SUM(JSON_DEPTH(shipping_address)) AS total_depth FROM orders"""
               "product_attributes_json_set_render",
               "SELECT id, CAST(JSON_SET(attributes, '$.sku', sku, '$.price', unit_price) AS CHAR) AS enriched FROM products ORDER BY id LIMIT 8"
               "order_item_metadata_wildcard_counts",
               "SELECT COUNT(*) AS rows_with_metadata, SUM(JSON_LENGTH(JSON_EXTRACT(metadata, '$.*'))) AS object_member_count, SUM(JSON_LENGTH(JSON_EXTRACT(metadata, '$[*]'))) AS array_element_count FROM order_items WHERE metadata IS NOT NULL"
               "customer_profile_arrayagg_unsupported",
               "SELECT CAST(JSON_ARRAYAGG(JSON_OBJECT('id', id, 'email', email)) AS CHAR) AS agg_text FROM (SELECT id, email FROM customers ORDER BY id LIMIT 3) AS src"

               // Recursive CTE over the payments tree, decimal running-total frames,
               // LATERAL top-N-per-group, and the ALL multiset set operations.
               "recursive_payment_hierarchy_depth",
               "WITH RECURSIVE chain AS (SELECT id, parent_id, 0 AS depth FROM payments WHERE parent_id IS NULL AND id <= 50 UNION ALL SELECT p.id, p.parent_id, c.depth + 1 FROM payments AS p JOIN chain AS c ON p.parent_id = c.id) SELECT depth, COUNT(*) AS node_count FROM chain GROUP BY depth ORDER BY depth"
               "window_running_total_decimal",
               "SELECT i.order_id, i.id, i.line_total, SUM(i.line_total) OVER (PARTITION BY i.order_id ORDER BY i.id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS running_total FROM order_items AS i WHERE i.order_id <= 3 ORDER BY i.order_id, i.id"
               "lateral_top_line_item_per_order",
               "SELECT o.id AS order_id, top_item.id AS item_id, top_item.line_total FROM orders AS o JOIN LATERAL (SELECT i.id, i.line_total FROM order_items AS i WHERE i.order_id = o.id ORDER BY i.line_total DESC, i.id LIMIT 1) AS top_item ON TRUE WHERE o.id <= 10 ORDER BY o.id, top_item.id"
               "except_all_multiset_difference",
               "((SELECT status FROM orders WHERE id <= 100) EXCEPT ALL (SELECT status FROM orders WHERE id <= 50)) ORDER BY status"
               "intersect_all_multiset_intersection",
               "((SELECT state FROM payments WHERE id <= 200) INTERSECT ALL (SELECT state FROM payments WHERE id BETWEEN 100 AND 300)) ORDER BY state" |]
        | Volume ->
            [| "volume_bounds",
               "SELECT COUNT(*) AS row_count, MIN(id) AS min_id, MAX(id) AS max_id, SUM(signed_value) AS signed_sum FROM volume_rows"
               "volume_groups",
               "SELECT active, COUNT(*) AS row_count, SUM(exact_value) AS exact_sum FROM volume_rows GROUP BY active ORDER BY active"
               "volume_edges",
               "SELECT id, bucket, signed_value, exact_value, active, payload FROM volume_rows ORDER BY id LIMIT 16"

               // Windows over aggregate output, NTILE bucket distribution, and a
               // recursive series used for gap-filling.
               "window_over_grouped_rows",
               "SELECT bucket, COUNT(*) AS row_count, ROUND(AVG(signed_value), 4) AS avg_value, SUM(COUNT(*)) OVER (ORDER BY bucket ROWS UNBOUNDED PRECEDING) AS running_rows FROM volume_rows WHERE bucket < 64 GROUP BY bucket ORDER BY bucket"
               "cte_ntile_decile_bounds",
               "WITH ranked AS (SELECT id, signed_value, NTILE(10) OVER (ORDER BY signed_value, id) AS decile FROM volume_rows) SELECT decile, COUNT(*) AS row_count, MIN(signed_value) AS min_value, MAX(signed_value) AS max_value FROM ranked GROUP BY decile ORDER BY decile"
               "recursive_series_joined_to_table",
               "WITH RECURSIVE buckets (b) AS (SELECT 0 UNION ALL SELECT b + 1 FROM buckets WHERE b < 15) SELECT buckets.b, COUNT(v.id) AS row_count FROM buckets LEFT JOIN volume_rows AS v ON v.bucket = buckets.b GROUP BY buckets.b ORDER BY buckets.b" |]

[<RequireQualifiedAccess>]
module DmlBattery =
    let private cleanup =
        [| "DROP VIEW IF EXISTS fsdb_dml_materialized_reversed"
           "DROP VIEW IF EXISTS fsdb_dml_materialized_join"
           "DROP VIEW IF EXISTS fsdb_dml_materialized_totals"
           "DROP VIEW IF EXISTS fsdb_dml_outer_join"
           "DROP VIEW IF EXISTS fsdb_dml_nested_join"
           "DROP VIEW IF EXISTS fsdb_dml_right_view"
           "DROP VIEW IF EXISTS fsdb_dml_left_view"
           "DROP VIEW IF EXISTS fsdb_dml_view"
           "DROP TABLE IF EXISTS fsdb_dml_materialized_target"
           "DROP TABLE IF EXISTS fsdb_dml_materialized_amount"
           "DROP TABLE IF EXISTS fsdb_dml_join_right"
           "DROP TABLE IF EXISTS fsdb_dml_join_left"
           "DROP TABLE IF EXISTS fsdb_trigger_contract"
           "DROP TABLE IF EXISTS fsdb_trigger_log"
           "DROP TABLE IF EXISTS fsdb_replace_contract"
           "DROP TABLE IF EXISTS fsdb_odku_source"
           "DROP TABLE IF EXISTS fsdb_fulltext_contract"
           "DROP TABLE IF EXISTS fsdb_dml_contract" |]

    let private fixtures =
        [| yield! cleanup
           "CREATE TABLE fsdb_dml_contract (id INT PRIMARY KEY, n INT)"
           "CREATE TABLE fsdb_dml_join_left (id INT PRIMARY KEY, n INT NOT NULL)"
           "CREATE TABLE fsdb_dml_join_right (id INT PRIMARY KEY, note VARCHAR(20) NOT NULL)"
           "CREATE TABLE fsdb_dml_materialized_amount (group_id INT, amount INT)"
           "CREATE TABLE fsdb_dml_materialized_target (id INT PRIMARY KEY, group_id INT NOT NULL, n INT NOT NULL)"
           "INSERT INTO fsdb_dml_join_left VALUES (1, 10), (2, 20)"
           "INSERT INTO fsdb_dml_join_right VALUES (1, 'one'), (2, 'two')"
           "INSERT INTO fsdb_dml_materialized_amount VALUES (1, 4), (1, 6), (2, 20)"
           "INSERT INTO fsdb_dml_materialized_target VALUES (1, 1, 10), (2, 2, 20)"
           "CREATE TABLE fsdb_odku_source (id INT, selected_n INT, update_n INT)"
           "INSERT INTO fsdb_odku_source VALUES (1, 70, 80), (1, 70, 90)"
           "CREATE TABLE fsdb_replace_contract (id INT PRIMARY KEY, u INT UNIQUE, n INT DEFAULT 7, INDEX ix_n_u (n, u))"
           "CREATE TABLE fsdb_trigger_contract (id INT PRIMARY KEY, n INT)"
           "CREATE TABLE fsdb_trigger_log (n INT)"
           "CREATE TABLE fsdb_fulltext_contract (id INT PRIMARY KEY, body TEXT, FULLTEXT(body))"
           "INSERT INTO fsdb_fulltext_contract VALUES (1, 'needle alpha'), (2, 'needle beta'), (3, 'ordinary')"
           "CREATE VIEW fsdb_dml_view AS SELECT id, n FROM fsdb_dml_contract WHERE n > 0 WITH CHECK OPTION"
           "CREATE VIEW fsdb_dml_left_view AS SELECT id, n FROM fsdb_dml_join_left WHERE n > 0 WITH CASCADED CHECK OPTION"
           "CREATE VIEW fsdb_dml_right_view AS SELECT id, note FROM fsdb_dml_join_right"
           "CREATE VIEW fsdb_dml_nested_join AS SELECT l.id, l.n, r.id AS right_id, r.note FROM fsdb_dml_left_view AS l JOIN fsdb_dml_right_view AS r ON r.id = l.id"
           "CREATE VIEW fsdb_dml_outer_join AS SELECT id, n, right_id, note, n * 2 AS doubled FROM fsdb_dml_nested_join WHERE n < 50"
           "CREATE VIEW fsdb_dml_materialized_totals AS SELECT group_id, SUM(amount) AS total FROM fsdb_dml_materialized_amount GROUP BY group_id"
           "CREATE VIEW fsdb_dml_materialized_join AS SELECT t.id, t.n, x.total FROM fsdb_dml_materialized_target AS t JOIN fsdb_dml_materialized_totals AS x ON x.group_id = t.group_id"
           "CREATE VIEW fsdb_dml_materialized_reversed AS SELECT t.id, t.n, x.total FROM fsdb_dml_materialized_totals AS x JOIN fsdb_dml_materialized_target AS t ON x.group_id = t.group_id"
           "CREATE TRIGGER fsdb_trigger_first BEFORE INSERT ON fsdb_trigger_contract FOR EACH ROW SET NEW.n = NEW.n + 1"
           "CREATE TRIGGER fsdb_trigger_second BEFORE INSERT ON fsdb_trigger_contract FOR EACH ROW FOLLOWS fsdb_trigger_first BEGIN INSERT INTO fsdb_trigger_log VALUES (NEW.n); SET NEW.n = NEW.n + 1; END" |]

    let private cases =
        [| "insert_new", "INSERT INTO fsdb_dml_contract VALUES (1, 10)"
           "odku_changed", "INSERT INTO fsdb_dml_contract VALUES (1, 20) ON DUPLICATE KEY UPDATE n = VALUES(n)"
           "odku_unchanged", "INSERT INTO fsdb_dml_contract VALUES (1, 20) ON DUPLICATE KEY UPDATE n = VALUES(n)"
           "odku_mixed", "INSERT INTO fsdb_dml_contract VALUES (1, 30), (2, 40) ON DUPLICATE KEY UPDATE n = VALUES(n)"
           "odku_same_statement_changed", "INSERT INTO fsdb_dml_contract VALUES (5, 1), (5, 2) ON DUPLICATE KEY UPDATE n = VALUES(n)"
           "odku_same_statement_unchanged", "INSERT INTO fsdb_dml_contract VALUES (7, 1), (7, 1) ON DUPLICATE KEY UPDATE n = VALUES(n)"
           "odku_select_source", "INSERT INTO fsdb_dml_contract (id, n) SELECT id, selected_n FROM fsdb_odku_source AS s ON DUPLICATE KEY UPDATE n = s.update_n"
           "replace_new", "REPLACE INTO fsdb_dml_contract VALUES (3, 50)"
           "replace_changed", "REPLACE INTO fsdb_dml_contract VALUES (3, 60)"
           "replace_unchanged", "REPLACE INTO fsdb_dml_contract VALUES (3, 60)"
           "replace_multi_unique_seed", "INSERT INTO fsdb_replace_contract VALUES (1, 10, 1), (2, 20, 2)"
           "replace_multi_unique", "REPLACE INTO fsdb_replace_contract VALUES (1, 20, 3)"
           "replace_batch_order", "REPLACE INTO fsdb_replace_contract VALUES (3, 30, 4), (3, 31, 5)"
           "replace_select", "REPLACE INTO fsdb_dml_contract SELECT 20, 2"
           "replace_set_default_reference", "REPLACE INTO fsdb_replace_contract SET id = 1, u = 20, n = n + 1"
           "replace_set_default_function", "REPLACE INTO fsdb_replace_contract SET id = 4, u = 40, n = DEFAULT(n)"
           "composite_index_update", "UPDATE fsdb_replace_contract SET n = 8 WHERE n = 3 AND u = 20"
           "view_insert", "INSERT INTO fsdb_dml_view VALUES (30, 3)"
           "nested_join_update_left", "UPDATE fsdb_dml_nested_join SET n = 11 WHERE id = 1"
           "nested_join_update_right", "UPDATE fsdb_dml_nested_join SET note = 'changed' WHERE id = 1"
           "outer_join_update", "UPDATE fsdb_dml_outer_join SET n = 12 WHERE doubled = 22"
           "nested_join_insert_left", "INSERT INTO fsdb_dml_nested_join (id, n) VALUES (3, 30)"
           "nested_join_insert_right", "INSERT INTO fsdb_dml_nested_join (right_id, note) VALUES (4, 'four')"
           "nested_join_checked_update", "UPDATE fsdb_dml_nested_join SET n = 13 WHERE id = 1"
           "nested_join_checked_ignore", "UPDATE IGNORE fsdb_dml_nested_join SET n = -1 WHERE id = 1"
           "outer_join_no_match", "UPDATE fsdb_dml_outer_join SET note = 'absent' WHERE doubled = 999"
           "nested_join_left_state", "UPDATE fsdb_dml_join_left SET n = 130 WHERE id = 1 AND n = 13"
           "nested_join_right_state", "UPDATE fsdb_dml_join_right SET note = 'verified' WHERE id = 1 AND note = 'changed'"
           "nested_join_left_insert_state", "UPDATE fsdb_dml_join_left SET n = 31 WHERE id = 3 AND n = 30"
           "nested_join_right_insert_state", "UPDATE fsdb_dml_join_right SET note = 'verified' WHERE id = 4 AND note = 'four'"
           "materialized_join_update", "UPDATE fsdb_dml_materialized_join SET n = 11 WHERE id = 1 AND total = 10"
           "materialized_reversed_update", "UPDATE fsdb_dml_materialized_reversed SET n = 21 WHERE id = 2 AND total = 20"
           "materialized_join_state", "UPDATE fsdb_dml_materialized_target SET n = n + 100 WHERE (id = 1 AND n = 11) OR (id = 2 AND n = 21)"
           "ordered_compound_trigger", "INSERT INTO fsdb_trigger_contract VALUES (1, 10)"
           "trigger_side_effect", "UPDATE fsdb_trigger_log SET n = n + 1 WHERE n = 11"
           "insert_ignore_duplicate", "INSERT IGNORE INTO fsdb_dml_contract VALUES (1, 99)"
           "insert_multi", "INSERT INTO fsdb_dml_contract VALUES (10, 1), (11, 2), (12, 3)"
           "update_changed", "UPDATE fsdb_dml_contract SET n = 71 WHERE id = 2"
           "update_unchanged", "UPDATE fsdb_dml_contract SET n = 71 WHERE id = 2"
           "update_multi", "UPDATE fsdb_dml_contract SET n = n + 1 WHERE id IN (10, 11, 12)"
           "update_no_match", "UPDATE fsdb_dml_contract SET n = 1 WHERE id = 999"
           "fulltext_update", "UPDATE fsdb_fulltext_contract SET body = 'archived alpha' WHERE MATCH(body) AGAINST('needle') AND id = 1"
           "fulltext_delete", "DELETE FROM fsdb_fulltext_contract WHERE MATCH(body) AGAINST('needle') OR MATCH(body) AGAINST('ordinary')"
           "delete_match", "DELETE FROM fsdb_dml_contract WHERE id = 12"
           "delete_no_match", "DELETE FROM fsdb_dml_contract WHERE id = 999" |]

    let classify (mysql: TargetOutcome) (fsdb: TargetOutcome) =
        if not (TargetOutcome.succeeded mysql) then
            if mysql.Status = "timeout" then "oracle_timeout"
            elif mysql.Status = "driver_error" then "infrastructure"
            else "oracle_rejected"
        elif not (TargetOutcome.succeeded fsdb) then
            if fsdb.Status = "timeout" then "fsdb_timeout"
            elif fsdb.Status = "driver_error" then "protocol_fault"
            elif fsdb.ErrorCode = 1105 then "contained_internal_error"
            else "fsdb_execution_gap"
        elif mysql.AffectedRows <> fsdb.AffectedRows then
            "dml_affected_rows_mismatch"
        else
            "pass"

    let detail (mysql: TargetOutcome) (fsdb: TargetOutcome) classification =
        match classification with
        | "pass" -> sprintf "affected rows match at %d" mysql.AffectedRows
        | "dml_affected_rows_mismatch" -> sprintf "affected rows differ: mysql=%d fsdb=%d" mysql.AffectedRows fsdb.AffectedRows
        | "oracle_timeout"
        | "oracle_rejected"
        | "infrastructure" -> mysql.Message
        | _ -> fsdb.Message

    let private connectionString useAffectedRows (connectionString: string) =
        let builder = MySqlConnectionStringBuilder connectionString
        builder.Pooling <- false
        builder.UseAffectedRows <- useAffectedRows
        builder.ConnectionString

    let run mysqlConnectionString fsdbConnectionString timeoutSeconds =
        task {
            let records = ResizeArray<DmlRecord>()

            for mode, useAffectedRows in [ "found_rows", false; "changed_rows", true ] do
                use! mysql = Database.openConnection (connectionString useAffectedRows mysqlConnectionString)
                use! fsdb = Database.openConnection (connectionString useAffectedRows fsdbConnectionString)
                let mutable fixtureReady = true

                for index, sql in fixtures |> Array.indexed do
                    if fixtureReady then
                        let! mysqlOutcome = Database.execute "mysql" mysql timeoutSeconds sql

                        let! fsdbOutcome =
                            if TargetOutcome.succeeded mysqlOutcome then
                                Database.execute "fsdb" fsdb timeoutSeconds sql
                            else
                                Task.FromResult(TargetOutcome.notRun "fsdb")

                        let classification = classify mysqlOutcome fsdbOutcome
                        fixtureReady <- classification = "pass"

                        if not fixtureReady then
                            records.Add
                                { Mode = mode
                                  Index = index
                                  Name = "fixture"
                                  Sql = sql
                                  SqlSha256 = Hashing.text sql
                                  MySql = mysqlOutcome
                                  Fsdb = fsdbOutcome
                                  Classification = classification
                                  Equal = false
                                  Detail = detail mysqlOutcome fsdbOutcome classification }

                if fixtureReady then
                    for index, (name, sql) in cases |> Array.indexed do
                        let! mysqlOutcome = Database.execute "mysql" mysql timeoutSeconds sql

                        let! fsdbOutcome =
                            if TargetOutcome.succeeded mysqlOutcome then
                                Database.execute "fsdb" fsdb timeoutSeconds sql
                            else
                                Task.FromResult(TargetOutcome.notRun "fsdb")

                        let classification = classify mysqlOutcome fsdbOutcome

                        records.Add
                            { Mode = mode
                              Index = index
                              Name = name
                              Sql = sql
                              SqlSha256 = Hashing.text sql
                              MySql = mysqlOutcome
                              Fsdb = fsdbOutcome
                              Classification = classification
                              Equal = classification = "pass"
                              Detail = detail mysqlOutcome fsdbOutcome classification }

                for sql in cleanup do
                    let! _ = Database.execute "mysql" mysql timeoutSeconds sql
                    let! _ = Database.execute "fsdb" fsdb timeoutSeconds sql
                    ()

            return records.ToArray()
        }

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

    let private foldResultType (typeName: string) =
        let normalized = typeName.Trim().ToUpperInvariant()

        let baseType =
            normalized.Split([| '('; ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultValue normalized

        match baseType with
        | "TINYINT"
        | "SMALLINT"
        | "MEDIUMINT"
        | "INT"
        | "INTEGER"
        | "BIGINT" -> "integer"
        | "CHAR"
        | "VARCHAR"
        | "TINYTEXT"
        | "TEXT"
        | "MEDIUMTEXT"
        | "LONGTEXT"
        | "STRING"
        | "VAR_STRING" -> "text"
        | other -> other

    let private columnIsEntirelyNull index (outcome: ProbeOutcome) =
        outcome.Rows.Length > 0
        && outcome.Rows
           |> Array.forall (fun row ->
               use document = JsonDocument.Parse row
               let cell = document.RootElement.[index]
               cell.ValueKind = JsonValueKind.String && cell.GetString() = "null")

    let compareProbeTypes (mysql: ProbeOutcome) (fsdb: ProbeOutcome) =
        if not (ProbeOutcome.succeeded mysql) || not (ProbeOutcome.succeeded fsdb) then
            None
        elif mysql.ColumnTypes.Length <> fsdb.ColumnTypes.Length then
            Some(
                sprintf
                    "column type counts differ: mysql=%d fsdb=%d"
                    mysql.ColumnTypes.Length
                    fsdb.ColumnTypes.Length
            )
        else
            Array.zip mysql.ColumnTypes fsdb.ColumnTypes
            |> Array.indexed
            |> Array.tryPick (fun (index, (mysqlType, fsdbType)) ->
                if
                    foldResultType mysqlType = foldResultType fsdbType
                    || (columnIsEntirelyNull index mysql && columnIsEntirelyNull index fsdb)
                then
                    None
                else
                    Some(sprintf "column %d types differ: mysql=%s fsdb=%s" index mysqlType fsdbType))

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
        elif compareProbeTypes mysql fsdb |> Option.isSome then
            "probe_type_mismatch"
        elif mysql.DataSha256 <> fsdb.DataSha256 then
            "probe_result_mismatch"
        else
            "pass"

    let private probeDetail name parserDetail (mysql: ProbeOutcome) (fsdb: ProbeOutcome) classification =
        match classification with
        | "pass" -> sprintf "probe %s columns, types, and ordered rows match" name
        | "fsdb_probe_parser_gap" -> parserDetail
        | "oracle_timeout"
        | "oracle_rejected"
        | "infrastructure" -> mysql.Message
        | "fsdb_timeout"
        | "protocol_fault"
        | "contained_internal_error"
        | "fsdb_probe_execution_gap" -> fsdb.Message
        | "probe_schema_mismatch" -> sprintf "probe %s columns differ: mysql=%A fsdb=%A" name mysql.Columns fsdb.Columns
        | "probe_type_mismatch" -> compareProbeTypes mysql fsdb |> Option.defaultValue (sprintf "probe %s types differ" name)
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

    let classifyStatement
        (parserStatus: string)
        compareAffectedRows
        (mysql: TargetOutcome)
        (fsdb: TargetOutcome)
        (invariantErrors: string array)
        =
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
        elif compareAffectedRows && mysql.AffectedRows <> fsdb.AffectedRows then
            "statement_affected_rows_mismatch"
        else
            "pass"

    let private statementDetail parserDetail (mysql: TargetOutcome) (fsdb: TargetOutcome) invariantErrors classification =
        match classification with
        | "pass" -> "statement outcomes match"
        | "fsdb_parser_gap" -> parserDetail
        | "oracle_timeout"
        | "oracle_rejected"
        | "infrastructure" -> mysql.Message
        | "fsdb_timeout"
        | "protocol_fault"
        | "contained_internal_error"
        | "fsdb_execution_gap" -> fsdb.Message
        | "invariant_failure" -> String.concat "; " invariantErrors
        | "statement_affected_rows_mismatch" -> sprintf "affected rows differ: mysql=%d fsdb=%d" mysql.AffectedRows fsdb.AffectedRows
        | other -> sprintf "statement failed as %s" other

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
            let mutable dml = [||]
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

                        if options.Scenario = Scalar then
                            let! records = DmlBattery.run oracleConnectionString (fsdbConnectionString subject.Port) options.TimeoutSeconds
                            dml <- records
                            Json.write (Path.Combine(caseDirectory, "dml.json")) dml
                            subject.DrainEvents() |> ignore

                        let records = ResizeArray<StatementRecord>()
                        let mutable keepRunning = true
                        let mutable lastInvariantStatement = -1

                        for statement in splitStatements do
                            if keepRunning then
                                let parserStatus, astKind, compareAffectedRows =
                                    match Fsdb.Parser.parse statement.Text with
                                    | Ok ast ->
                                        let compareAffectedRows =
                                            match ast with
                                            | Insert _
                                            | InsertSelect _
                                            | Replace _
                                            | ReplaceSelect _
                                            | ReplaceSet _
                                            | Update _
                                            | Delete _ -> true
                                            | _ -> false

                                        "ok", AstKind.ofStatement ast, compareAffectedRows
                                    | Error error -> "error", error, false

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

                                let result = classifyStatement parserStatus compareAffectedRows mysqlOutcome fsdbOutcome invariantErrors
                                let detail = statementDetail astKind mysqlOutcome fsdbOutcome invariantErrors result

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
                                      CompareAffectedRows = compareAffectedRows
                                      Classification = result
                                      Detail = detail
                                      CommitEvents = commitEvents
                                      InvariantErrors = invariantErrors }

                                records.Add record

                                if result <> "pass" then
                                    classification <- result
                                    failureEvidenceHash <- statement.Sha256
                                    signatureDetail <- detail

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

                            // Independent probes preserve the full evidence
                            // set; the first failure alone defines the case
                            // classification and signature.
                            for name, sql in ScenarioProbes.all options.Scenario do
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

                                    if result <> "pass" && failureEvidenceHash = "" then
                                        classification <- result
                                        failureEvidenceHash <- Hashing.text sql
                                        signatureDetail <- detail
                                        writeText (Path.Combine(caseDirectory, "failure.sql")) sql

                            probes <- probeRecords.ToArray()
                            Json.write (Path.Combine(caseDirectory, "probes.json")) probes
                            // A failing probe still stops the case before the
                            // snapshot comparison, whose category would
                            // otherwise overwrite the probe's.
                            keepRunning <- probes |> Array.forall (fun probe -> probe.Equal)

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

            if classification = "pass" then
                match dml |> Array.tryFind (fun record -> not record.Equal) with
                | Some record ->
                    classification <- record.Classification
                    signatureDetail <- record.Detail
                    failureEvidenceHash <- record.SqlSha256
                | None -> ()

            let statementHash, mysqlError, fsdbError =
                if failureEvidenceHash <> "" then
                    let mysqlError, fsdbError =
                        match dml |> Array.tryFind (fun record -> record.SqlSha256 = failureEvidenceHash && not record.Equal) with
                        | Some record -> record.MySql.ErrorCode, record.Fsdb.ErrorCode
                        | _ ->
                            match probes |> Array.tryFind (fun probe -> not probe.Equal) with
                            | Some probe -> probe.MySql.ErrorCode, probe.Fsdb.ErrorCode
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

            // Every failing probe's own signature, not just the first one's.
            // The case is only "known" when the ledger covers all of them —
            // otherwise ledgering the earliest gap in the corpus would mark
            // the case green while silently carrying whatever fails later.
            let probeFailureSignatures =
                probes
                |> Array.filter (fun probe -> not probe.Equal)
                |> Array.map (fun probe ->
                    failureSignature
                        (classifyProbe probe.ParserStatus probe.MySql probe.Fsdb)
                        probe.SqlSha256
                        probe.MySql.ErrorCode
                        probe.Fsdb.ErrorCode
                        probe.Detail)

            let dmlFailureSignatures =
                dml
                |> Array.filter (fun record -> not record.Equal)
                |> Array.map (fun record ->
                    failureSignature
                        record.Classification
                        record.SqlSha256
                        record.MySql.ErrorCode
                        record.Fsdb.ErrorCode
                        record.Detail)

            let classification, finalFailureSignature, known, finalDetail =
                match loadKnownGaps () with
                | Ok knownGaps ->
                    classification,
                    subjectFailureSignature,
                    subjectFailureSignature <> ""
                    && knownGaps.Contains subjectFailureSignature
                    && dmlFailureSignatures |> Array.forall knownGaps.Contains
                    && probeFailureSignatures |> Array.forall knownGaps.Contains,
                    signatureDetail
                | Error error ->
                    let detail = "invalid harness configuration: " + error
                    "infrastructure", failureSignature "infrastructure" statementHash mysqlError fsdbError detail, false, detail

            let passed = classification = "pass" || known
            let currentProcess = Process.GetCurrentProcess()
            currentProcess.Refresh()

            let manifest =
                { SchemaVersion = 3
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
                  Dml = dml
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
            | Ok original when original.SchemaVersion <> 1 && original.SchemaVersion <> 2 && original.SchemaVersion <> 3 ->
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
