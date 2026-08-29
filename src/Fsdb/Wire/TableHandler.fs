module Fsdb.TableHandler

open System
open Fsdb.Ast
open Fsdb.Executor
open Fsdb.Engine
open Fsdb.Session
open Fsdb.Storage
open Fsdb.Value

let private normalize (value: string) = value.ToLowerInvariant()

let private overlayCatalog (catalog: Catalog) (overlay: Catalog) =
    overlay
    |> Map.fold (fun result database tables ->
        result
        |> Map.change database (fun current ->
            current
            |> Option.defaultValue Map.empty
            |> fun existing -> Some(Map.fold (fun state name table -> Map.add name table state) existing tables))) catalog

let private hasTemporaryTable (catalog: Catalog) database table =
    catalog
    |> Map.tryFind (normalize database)
    |> Option.exists (Map.containsKey (normalizeTableName table))

let private storeFor (session: Session) (handler: TableHandler) =
    let store = Session.currentStore session

    if handler.Temporary then
        beginTransactionSnapshotFromCatalog store (overlayCatalog store.Catalog session.TemporaryCatalog)
    else
        store

let private tableForOpen (session: Session) database table temporary =
    let store = Session.currentStore session

    if temporary then
        tableSnapshot
            (beginTransactionSnapshotFromCatalog store (overlayCatalog store.Catalog session.TemporaryCatalog))
            database
            table
    else
        tableSnapshot store database table

let private viewExists (session: Session) database table =
    match scanList (Session.currentStore session) "mysql" "views" with
    | Error _ -> false
    | Ok(_, rows) ->
        rows
        |> List.choose SystemCatalog.View.tryRead
        |> List.exists (fun view ->
            view.Schema.Equals(database, StringComparison.OrdinalIgnoreCase)
            && view.Name.Equals(table, StringComparison.OrdinalIgnoreCase))

let private renderColumn (column: Fsdb.Ast.ColumnDef) value =
    match column.Type with
    | TDateTime fsp
    | TTimestamp fsp
    | TTime fsp -> Value.toTextFsp fsp value
    | _ -> Value.toText value

let private resultSet (columns: Fsdb.Ast.ColumnDef list) (rows: Value[] list) =
    ResultSet(
        columns |> List.map _.Name,
        rows |> List.map (fun row -> List.map2 renderColumn columns (List.ofArray row))
    )

let private countOr defaultValue = function
    | None -> defaultValue
    | Some(Lit(VInt value)) -> max 0 (int value)
    | Some(Lit(VUInt value)) -> int (min value (uint64 Int32.MaxValue))
    | _ -> 0

let private positionKey = function
    | HandlerNatural _ -> ""
    | HandlerIndexPosition(index, _)
    | HandlerIndexComparison(index, _, _) -> normalize index

let private comparisonMatches comparison value =
    match comparison with
    | HandlerEqual -> value = 0
    | HandlerLessOrEqual -> value <= 0
    | HandlerGreaterOrEqual -> value >= 0
    | HandlerLess -> value < 0
    | HandlerGreater -> value > 0

let private direction = function
    | HandlerNatural HandlerFirst
    | HandlerNatural HandlerNext
    | HandlerIndexPosition(_, HandlerFirst)
    | HandlerIndexPosition(_, HandlerNext)
    | HandlerIndexComparison _ -> 1
    | HandlerIndexPosition(_, HandlerPrevious)
    | HandlerIndexPosition(_, HandlerLast) -> -1
    | HandlerNatural _ -> 1

let private startingAt position step (rows: (RowId * Value[]) list) mode =
    let edge () = if step > 0 then 0 else rows.Length - 1

    match mode with
    | HandlerNatural HandlerFirst
    | HandlerIndexPosition(_, HandlerFirst) -> 0
    | HandlerIndexPosition(_, HandlerLast) -> rows.Length - 1
    | HandlerIndexComparison _ -> 0
    | _ ->
        match position with
        | Unpositioned -> edge ()
        | BeforeFirst -> if step > 0 then 0 else -1
        | AfterLast -> if step < 0 then rows.Length - 1 else rows.Length
        | AtRow rowId ->
            rows
            |> List.tryFindIndex (fun (candidate, _) -> candidate = rowId)
            |> Option.map ((+) step)
            |> Option.defaultWith edge

let private evaluateIndexRows store registry database table mode =
    let ordered index =
        tryHandlerIndexRows table index
        |> Option.map (fun result -> result.Rows |> List.map (fun (rowId, _, row) -> rowId, row))

    match mode with
    | HandlerNatural _ ->
        ordered "PRIMARY"
        |> Option.defaultWith (fun () -> handlerNaturalRows table)
        |> Ok
    | HandlerIndexPosition(index, _) ->
        ordered index
        |> Option.map Ok
        |> Option.defaultValue (Error(Err(1176, sprintf "Key '%s' doesn't exist in table '%s'" index table.OriginalName)))
    | HandlerIndexComparison(index, comparison, expressions) ->
        match tryHandlerIndexRows table index with
        | None -> Error(Err(1176, sprintf "Key '%s' doesn't exist in table '%s'" index table.OriginalName))
        | Some ordered when expressions.Length > ordered.ColumnIndices.Length ->
            Error(Err(1070, sprintf "Too many key parts specified; max %d parts allowed" ordered.ColumnIndices.Length))
        | Some ordered ->
            expressions
            |> List.map (Executor.evaluateExpression store registry database)
            |> List.fold
                (fun state item ->
                    state
                    |> Result.bind (fun values -> item |> Result.map (fun value -> value :: values)))
                (Ok [])
            |> Result.map List.rev
            |> Result.bind (fun values ->
                coerceHandlerIndexValues store ordered values
                |> Result.mapError (fun error ->
                    let code, message = toMySqlError error
                    Err(code, message)))
            |> Result.map (fun probe ->
                ordered.Rows
                |> List.filter (fun (_, values, _) ->
                    compareHandlerIndexValues ordered values probe
                    |> comparisonMatches comparison)
                |> List.map (fun (rowId, _, row) -> rowId, row))

let private readCore registry (session: Session) name mode where limit offset =
    let handlerKey = normalize name

    match Map.tryFind handlerKey session.TableHandlers with
    | None -> session, Err(1109, sprintf "Unknown table '%s' in HANDLER" name)
    | Some handler ->
        let store = storeFor session handler

        match tableSnapshot store handler.Database handler.Table with
        | Error _ ->
            { session with TableHandlers = Map.remove handlerKey session.TableHandlers },
            Err(1109, sprintf "Unknown table '%s' in HANDLER" name)
        | Ok table when table.CreateTime <> handler.CreateTime || table.Columns <> handler.Columns || table.Indexes <> handler.Indexes ->
            { session with TableHandlers = Map.remove handlerKey session.TableHandlers },
            Err(1109, sprintf "Unknown table '%s' in HANDLER" name)
        | Ok table ->
            match Auth.checkForAccount store (Auth.account session.User session.AccountHost) [ "SELECT", Auth.OnTable(handler.Database, handler.Table) ] with
            | Error(code, message) -> session, Err(code, message)
            | Ok() ->
                match evaluateIndexRows store registry handler.Database table mode with
                | Error result -> session, result
                | Ok rows ->
                    let take = countOr 1 limit
                    let skip = countOr 0 offset
                    let metadata = table.Columns |> List.map ColumnWire.metadataOfColumn

                    if take = 0 then
                        { session with LastResultColumnMetadata = metadata }, resultSet table.Columns []
                    else
                        let path = positionKey mode
                        let position = handler.Positions |> Map.tryFind path |> Option.defaultValue Unpositioned
                        let step = direction mode
                        let start = startingAt position step rows mode

                        let rec scan index remainingSkip acceptedCount accepted lastVisited =
                            if index < 0 || index >= rows.Length then
                                Ok(List.rev accepted, if step > 0 then AfterLast else BeforeFirst)
                            elif acceptedCount = take then
                                Ok(List.rev accepted, lastVisited |> Option.map AtRow |> Option.defaultValue position)
                            else
                                let rowId, row = rows.[index]

                                let matches =
                                    where
                                    |> Option.map (Executor.evaluateRowPredicate store registry handler.Database name table.Columns row)
                                    |> Option.defaultValue (Ok true)

                                matches
                                |> Result.bind (fun matches ->
                                    if matches && remainingSkip > 0 then
                                        scan (index + step) (remainingSkip - 1) acceptedCount accepted (Some rowId)
                                    elif matches then
                                        scan (index + step) remainingSkip (acceptedCount + 1) (row :: accepted) (Some rowId)
                                    else
                                        scan (index + step) remainingSkip acceptedCount accepted (Some rowId))

                        match scan start skip 0 [] None with
                        | Error result -> session, result
                        | Ok(rows, nextPosition) ->
                            let handler =
                                { handler with Positions = Map.add path nextPosition handler.Positions }

                            { session with
                                TableHandlers = Map.add handlerKey handler session.TableHandlers
                                LastResultColumnMetadata = metadata },
                            resultSet table.Columns rows

let private read registry timeout session name mode where limit offset =
    let handlerKey = normalize name

    match Map.tryFind handlerKey session.TableHandlers with
    | None -> session, Err(1109, sprintf "Unknown table '%s' in HANDLER" name)
    | Some handler when handler.Temporary -> readCore registry session name mode where limit offset
    | Some handler ->
        let access: TableLocks.Access =
            { Database = handler.Database
              Table = handler.Table
              ReferenceName = Some name
              Mode = TableLocks.ReadAccess }

        match
            TableLocks.withStatementAccess
                timeout
                session.Store
                session.ConnectionId
                [ access ]
                (fun () -> readCore registry session name mode where limit offset)
        with
        | Ok result -> result
        | Error(code, message) -> session, Err(code, message)

let run registry timeout (session: Session) = function
    | HandlerOpen(qualifiedTable, alias) ->
        if session.Tx.IsSome || TableLocks.holdsExplicit session.Store session.ConnectionId then
            session, Err(1192, "Can't execute the given command because you have active locked tables or an active transaction")
        else
            let defaultDatabase = session.Database |> Option.defaultValue Storage.defaultDatabase
            let database, tableName = splitQualified defaultDatabase qualifiedTable
            let name = alias |> Option.defaultValue tableName
            let handlerKey = normalize name
            let temporary = hasTemporaryTable session.TemporaryCatalog database tableName

            if Map.containsKey handlerKey session.TableHandlers then
                session, Err(1066, sprintf "Not unique table/alias: '%s'" name)
            elif not temporary && viewExists session database tableName then
                session, Err(1347, sprintf "'%s.%s' is not BASE TABLE" database tableName)
            else
                match tableForOpen session database tableName temporary with
                | Error error ->
                    let code, message = toMySqlError error
                    session, Err(code, message)
                | Ok table ->
                    match Auth.checkForAccount (Session.currentStore session) (Auth.account session.User session.AccountHost) [ "SELECT", Auth.OnTable(database, tableName) ] with
                    | Error(code, message) -> session, Err(code, message)
                    | Ok() ->
                        let handler =
                            { Database = database
                              Table = tableName
                              Temporary = temporary
                              CreateTime = table.CreateTime
                              Columns = table.Columns
                              Indexes = table.Indexes
                              Positions = Map.empty }

                        { session with TableHandlers = Map.add handlerKey handler session.TableHandlers }, Affected 0UL
    | HandlerRead(name, mode, where, limit, offset) ->
        if session.Tx.IsSome || TableLocks.holdsExplicit session.Store session.ConnectionId then
            session, Err(1192, "Can't execute the given command because you have active locked tables or an active transaction")
        else
            read registry timeout session name mode where limit offset
    | HandlerClose name ->
        if session.Tx.IsSome || TableLocks.holdsExplicit session.Store session.ConnectionId then
            session, Err(1192, "Can't execute the given command because you have active locked tables or an active transaction")
        else
            let handlerKey = normalize name

            if Map.containsKey handlerKey session.TableHandlers then
                { session with TableHandlers = Map.remove handlerKey session.TableHandlers }, Affected 0UL
            else
                session, Err(1109, sprintf "Unknown table '%s' in HANDLER" name)

let private invalidationTargets database = function
    | AlterTable(name, _)
    | Truncate name
    | CreateIndex(_, name, _, _, _, _)
    | DropIndexStmt(_, name, _) -> [ splitQualified database name ]
    | DropTable(names, _) -> names |> List.map (splitQualified database)
    | RenameTable pairs ->
        pairs
        |> List.collect (fun (source, target) -> [ splitQualified database source; splitQualified database target ])
    | _ -> []

let invalidate (before: Session) statement (after: Session) result =
    match result with
    | Err _ -> after
    | _ ->
        let database = before.Database |> Option.defaultValue defaultDatabase
        let targets = invalidationTargets database statement

        let droppedDatabase =
            match statement with
            | DropDatabase(name, _) -> Some name
            | _ -> None

        let shouldClose (handler: TableHandler) =
            droppedDatabase
            |> Option.exists (fun name -> handler.Database.Equals(name, StringComparison.OrdinalIgnoreCase))
            || (targets
                |> List.exists (fun (database, table) ->
                    handler.Temporary = hasTemporaryTable before.TemporaryCatalog database table
                    && handler.Database.Equals(database, StringComparison.OrdinalIgnoreCase)
                    && handler.Table.Equals(table, StringComparison.OrdinalIgnoreCase)))

        if targets.IsEmpty && droppedDatabase.IsNone then
            after
        else
            { after with TableHandlers = after.TableHandlers |> Map.filter (fun _ handler -> not (shouldClose handler)) }
