module Fsdb.TableLocks

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open Fsdb.Ast
open Fsdb.Engine
open Fsdb.Sql
open Fsdb.Storage

type AccessMode =
    | ReadAccess
    | WriteAccess

type Access =
    { Database: string
      Table: string
      ReferenceName: string option
      Mode: AccessMode }

type private TableState =
    { ExplicitReaders: HashSet<int>
      mutable ExplicitWriter: int option
      StatementReaders: HashSet<int>
      StatementWriters: HashSet<int>
      WaitingWriters: HashSet<int>
      PrioritizedWriters: HashSet<int> }

type private ExplicitContext =
    { Names: Map<string, Access>
      Physical: Map<string, AccessMode> }

type private Manager =
    { SyncRoot: obj
      Tables: Dictionary<string, TableState>
      Explicit: Dictionary<int, ExplicitContext> }

let private managers =
    ConditionalWeakTable<ConcurrentDictionary<string, Database ref>, Manager>()

let private managerFor (store: Store) =
    managers.GetValue(
        store.Databases,
        fun _ ->
            { SyncRoot = obj ()
              Tables = Dictionary(StringComparer.OrdinalIgnoreCase)
              Explicit = Dictionary() }
    )

let private normalize (value: string) = value.ToLowerInvariant()
let private tableKey database table = normalize database + "\u0000" + normalizeTableName table

let private stronger left right =
    match left, right with
    | WriteAccess, _
    | _, WriteAccess -> WriteAccess
    | _ -> ReadAccess

let private physicalAccesses accesses =
    accesses
    |> List.fold
        (fun result access ->
            let key = tableKey access.Database access.Table
            let mode = result |> Map.tryFind key |> Option.map (stronger access.Mode) |> Option.defaultValue access.Mode
            Map.add key mode result)
        Map.empty

let private stateFor (manager: Manager) key =
    match manager.Tables.TryGetValue key with
    | true, state -> state
    | false, _ ->
        let state =
            { ExplicitReaders = HashSet()
              ExplicitWriter = None
              StatementReaders = HashSet()
              StatementWriters = HashSet()
              WaitingWriters = HashSet()
              PrioritizedWriters = HashSet() }

        manager.Tables.Add(key, state)
        state

let private hasNoOtherOwner owner (owners: HashSet<int>) =
    owners.Count = 0 || (owners.Count = 1 && owners.Contains owner)

let private readerGateOpen owner (state: TableState) =
    hasNoOtherOwner owner state.WaitingWriters
    && hasNoOtherOwner owner state.PrioritizedWriters

let private explicitlyAvailable owner mode (state: TableState) =
    match mode with
    | ReadAccess ->
        state.ExplicitWriter |> Option.forall ((=) owner)
        && (state.StatementWriters.Count = 0 || (state.StatementWriters.Count = 1 && state.StatementWriters.Contains owner))
        && readerGateOpen owner state
    | WriteAccess ->
        state.ExplicitWriter |> Option.forall ((=) owner)
        && (state.ExplicitReaders.Count = 0
            || (state.ExplicitReaders.Count = 1 && state.ExplicitReaders.Contains owner))
        && (state.StatementReaders.Count = 0 || (state.StatementReaders.Count = 1 && state.StatementReaders.Contains owner))
        && (state.StatementWriters.Count = 0 || (state.StatementWriters.Count = 1 && state.StatementWriters.Contains owner))

let private explicitAvailable owner mode (manager: Manager) key =
    match manager.Tables.TryGetValue key with
    | false, _ -> true
    | true, state -> explicitlyAvailable owner mode state

let private statementAvailable owner mode (state: TableState) =
    match mode with
    | ReadAccess -> state.ExplicitWriter.IsNone && readerGateOpen owner state
    | WriteAccess -> state.ExplicitWriter.IsNone && state.ExplicitReaders.Count = 0

let private stateIsIdle (state: TableState) =
    state.ExplicitWriter.IsNone
    && state.ExplicitReaders.Count = 0
    && state.StatementReaders.Count = 0
    && state.StatementWriters.Count = 0
    && state.WaitingWriters.Count = 0
    && state.PrioritizedWriters.Count = 0

let private removeIdleState (manager: Manager) key state =
    if stateIsIdle state then
        manager.Tables.Remove key |> ignore

let private markWriter owner writersOf (manager: Manager) physical =
    for KeyValue(key, mode) in physical do
        if mode = WriteAccess then
            let state = stateFor manager key
            (writersOf state: HashSet<int>).Add owner |> ignore

let private unmarkWaitingWriter owner (manager: Manager) physical =
    for KeyValue(key, mode) in physical do
        if mode = WriteAccess then
            match manager.Tables.TryGetValue key with
            | false, _ -> ()
            | true, state ->
                state.WaitingWriters.Remove owner |> ignore
                removeIdleState manager key state

let private markWaitingWriter owner = markWriter owner _.WaitingWriters
let private markPrioritizedWriter owner = markWriter owner _.PrioritizedWriters

let private trackWaitingWriter owner (manager: Manager) physical action =
    markWaitingWriter owner manager physical

    try
        action ()
    finally
        unmarkWaitingWriter owner manager physical
        Monitor.PulseAll manager.SyncRoot

let private acquirePhysical owner (manager: Manager) physical =
    for KeyValue(key, mode) in physical do
        let state = stateFor manager key

        match mode with
        | ReadAccess -> state.ExplicitReaders.Add owner |> ignore
        | WriteAccess -> state.ExplicitWriter <- Some owner

let private releasePhysical owner (manager: Manager) physical =
    for KeyValue(key, mode) in physical do
        match manager.Tables.TryGetValue key with
        | false, _ -> ()
        | true, state ->
            match mode with
            | ReadAccess -> state.ExplicitReaders.Remove owner |> ignore
            | WriteAccess when state.ExplicitWriter = Some owner -> state.ExplicitWriter <- None
            | WriteAccess -> ()

            removeIdleState manager key state

let private waitUntil timeout syncRoot ready =
    let deadline = DateTime.UtcNow + timeout

    let rec wait () =
        if ready () then
            true
        else
            let remaining = deadline - DateTime.UtcNow
            remaining > TimeSpan.Zero && Monitor.Wait(syncRoot, remaining) && wait ()

    wait ()

let private waitForPhysical timeout owner (manager: Manager) physical =
    waitUntil timeout manager.SyncRoot (fun () ->
        physical
        |> Map.forall (fun key mode -> explicitAvailable owner mode manager key))

let private waitForStatement timeout owner (manager: Manager) physical =
    let deadline = DateTime.UtcNow + timeout

    let ready () =
        physical
        |> Map.forall (fun key mode ->
            match manager.Tables.TryGetValue key with
            | false, _ -> true
            | true, state -> statementAvailable owner mode state)

    let waited = not (ready ())
    waitUntil (deadline - DateTime.UtcNow) manager.SyncRoot ready, waited

let private acquireStatement owner (manager: Manager) physical =
    for KeyValue(key, mode) in physical do
        let state = stateFor manager key

        match mode with
        | ReadAccess -> state.StatementReaders.Add owner |> ignore
        | WriteAccess -> state.StatementWriters.Add owner |> ignore

let private releaseStatement owner (manager: Manager) physical =
    for KeyValue(key, mode) in physical do
        match manager.Tables.TryGetValue key with
        | false, _ -> ()
        | true, state ->
            match mode with
            | ReadAccess -> state.StatementReaders.Remove owner |> ignore
            | WriteAccess ->
                state.StatementWriters.Remove owner |> ignore
                state.PrioritizedWriters.Remove owner |> ignore

            removeIdleState manager key state

let private accessName (access: Access) =
    access.ReferenceName |> Option.defaultValue access.Table

let private buildNames accesses =
    let rec loop result =
        function
        | [] -> Ok result
        | access :: rest when access.ReferenceName.IsNone -> loop result rest
        | access :: rest ->
            let name = normalize (accessName access)

            if Map.containsKey name result then
                Error(1066, sprintf "Not unique table/alias: '%s'" (accessName access))
            else
                loop (Map.add name access result) rest

    loop Map.empty accesses

let private releaseExplicitUnderLock owner (manager: Manager) =
    match manager.Explicit.TryGetValue owner with
    | false, _ -> false
    | true, context ->
        releasePhysical owner manager context.Physical
        manager.Explicit.Remove owner |> ignore
        true

let acquireExplicit timeout store owner accesses =
    let manager = managerFor store

    match buildNames accesses with
    | Error error -> Error error
    | Ok names ->
        let physical = physicalAccesses accesses

        lock manager.SyncRoot (fun () ->
            let released = releaseExplicitUnderLock owner manager

            if released then
                Monitor.PulseAll manager.SyncRoot

            let acquired =
                trackWaitingWriter owner manager physical (fun () ->
                    waitForPhysical timeout owner manager physical)

            if acquired then
                acquirePhysical owner manager physical
                manager.Explicit.[owner] <- { Names = names; Physical = physical }
                Ok()
            else
                Error(1205, "Lock wait timeout exceeded; try restarting transaction"))

let releaseExplicit store owner =
    let manager = managerFor store

    lock manager.SyncRoot (fun () ->
        if releaseExplicitUnderLock owner manager then
            Monitor.PulseAll manager.SyncRoot)

let holdsExplicit store owner =
    let manager = managerFor store
    lock manager.SyncRoot (fun () -> manager.Explicit.ContainsKey owner)

let internal waitingWriterCount store database table =
    let manager = managerFor store
    let key = tableKey database table

    lock manager.SyncRoot (fun () ->
        match manager.Tables.TryGetValue key with
        | true, state -> state.WaitingWriters.Count
        | false, _ -> 0)

let private validateExplicitAccess (context: ExplicitContext) (access: Access) =
    let requiredKey = tableKey access.Database access.Table

    let held =
        match access.ReferenceName with
        | Some name ->
            context.Names
            |> Map.tryFind (normalize name)
            |> Option.filter (fun locked -> tableKey locked.Database locked.Table = requiredKey)
            |> Option.map _.Mode
        | None -> context.Physical |> Map.tryFind requiredKey

    match held, access.Mode with
    | None, _ -> Error(1100, sprintf "Table '%s' was not locked with LOCK TABLES" (accessName access))
    | Some ReadAccess, WriteAccess ->
        Error(1099, sprintf "Table '%s' was locked with a READ lock and can't be updated" (accessName access))
    | _ -> Ok()

let withStatementAccess timeout store owner accesses body =
    let manager = managerFor store
    let physical = physicalAccesses accesses

    let acquired =
        lock manager.SyncRoot (fun () ->
            match manager.Explicit.TryGetValue owner with
            | true, context ->
                accesses
                |> List.fold
                    (fun result access -> result |> Result.bind (fun () -> validateExplicitAccess context access))
                    (Ok())
                |> Result.map (fun () -> false)
            | false, _ ->
                let acquired, waited =
                    trackWaitingWriter owner manager physical (fun () ->
                        waitForStatement timeout owner manager physical)

                if acquired then
                    acquireStatement owner manager physical

                    if waited then
                        markPrioritizedWriter owner manager physical

                    Ok true
                else
                    Error(1205, "Lock wait timeout exceeded; try restarting transaction"))

    match acquired with
    | Error error -> Error error
    | Ok releaseAfter ->
        try
            Ok(body ())
        finally
            if releaseAfter then
                lock manager.SyncRoot (fun () ->
                    releaseStatement owner manager physical
                    Monitor.PulseAll manager.SyncRoot)

let private access database table referenceName mode =
    { Database = database
      Table = table
      ReferenceName = referenceName
      Mode = mode }

let private tableReference defaultDb mode (table: TableRef) =
    access
        (table.Database |> Option.defaultValue defaultDb)
        table.Table
        (Some(table.Alias |> Option.defaultValue table.Table))
        mode

let rec private expressionAccesses boundCtes defaultDb expression =
    Expression.collectSubqueries expression
    |> List.collect (selectAccesses boundCtes defaultDb)

and private sourceAccesses boundCtes defaultDb =
    function
    | FromTable table when table.Database.IsNone && Set.contains (normalize table.Table) boundCtes -> []
    | FromTable table -> [ tableReference defaultDb ReadAccess table ]
    | FromSubquery(body, _)
    | FromLateral(body, _) -> selectOrUnionAccesses boundCtes defaultDb body
    | FromJsonTable(source, _, _, _) -> expressionAccesses boundCtes defaultDb source

and private selectOrUnionAccesses boundCtes defaultDb =
    function
    | PlainSelect select -> selectAccesses boundCtes defaultDb select
    | UnionSelect(first, rest, orderBy, limit, offset) ->
        selectAccesses boundCtes defaultDb first
        @ (rest |> List.collect (snd >> selectAccesses boundCtes defaultDb))
        @ (orderBy |> List.collect (fst >> expressionAccesses boundCtes defaultDb))
        @ (limit |> Option.map (expressionAccesses boundCtes defaultDb) |> Option.defaultValue [])
        @ (offset |> Option.map (expressionAccesses boundCtes defaultDb) |> Option.defaultValue [])

and private cteAccesses boundCtes defaultDb ctes =
    ctes
    |> List.fold
        (fun (accesses, names) cte ->
            let name = normalize cte.CteName
            let visible = if cte.Recursive then Set.add name names else names
            selectOrUnionAccesses visible defaultDb cte.Body @ accesses, Set.add name names)
        ([], boundCtes)

and private selectAccesses boundCtes defaultDb (select: SelectStmt) =
    let ctes, localCtes = cteAccesses boundCtes defaultDb select.Ctes

    ctes
    @ (select.From |> Option.map (sourceAccesses localCtes defaultDb) |> Option.defaultValue [])
    @ (select.Joins
       |> List.collect (fun join ->
           sourceAccesses localCtes defaultDb join.Table
           @ expressionAccesses localCtes defaultDb join.On))
    @ (select.Projections |> List.collect (fst >> expressionAccesses localCtes defaultDb))
    @ (select.Where |> Option.map (expressionAccesses localCtes defaultDb) |> Option.defaultValue [])
    @ (select.GroupBy |> List.collect (expressionAccesses localCtes defaultDb))
    @ (select.Having |> Option.map (expressionAccesses localCtes defaultDb) |> Option.defaultValue [])
    @ (select.OrderBy |> List.collect (fst >> expressionAccesses localCtes defaultDb))
    @ (select.Windows
       |> List.collect (snd >> OverSpec >> Expression.overExpressions >> List.collect (expressionAccesses localCtes defaultDb)))
    @ (select.Limit |> Option.map (expressionAccesses localCtes defaultDb) |> Option.defaultValue [])
    @ (select.Offset |> Option.map (expressionAccesses localCtes defaultDb) |> Option.defaultValue [])

let private splitName defaultDb name =
    let database, table = splitQualified defaultDb name
    database, table

let private namedAccess defaultDb mode name =
    let database, table = splitName defaultDb name
    access database table (Some table) mode

let private expressionsAccesses defaultDb expressions =
    expressions |> List.collect (expressionAccesses Set.empty defaultDb)

let private updateAccesses defaultDb (update: UpdateStmt) =
    let ctes, boundCtes = cteAccesses Set.empty defaultDb update.Ctes

    let sources =
        update.From
        :: (update.Joins
            |> List.choose (fun join ->
                match join.Table with
                | FromTable table -> Some table
                | _ -> None))

    let written =
        update.Assignments
        |> List.map (fun assignment ->
            assignment.Table
            |> Option.defaultValue (update.From.Alias |> Option.defaultValue update.From.Table)
            |> normalize)
        |> Set.ofList

    let physicalSources =
        sources
        |> List.filter (fun table ->
            not (table.Database.IsNone && Set.contains (normalize table.Table) boundCtes))
        |> List.map (fun table ->
            let qualifier = table.Alias |> Option.defaultValue table.Table
            tableReference defaultDb (if Set.contains (normalize qualifier) written then WriteAccess else ReadAccess) table)

    ctes
    @ physicalSources
    @ (update.Joins
       |> List.collect (fun join ->
           (match join.Table with
            | FromTable _ -> []
            | source -> sourceAccesses boundCtes defaultDb source)
           @ expressionAccesses boundCtes defaultDb join.On))
    @ (update.Assignments |> List.collect (_.Value >> expressionAccesses boundCtes defaultDb))
    @ (update.Where |> Option.map (expressionAccesses boundCtes defaultDb) |> Option.defaultValue [])
    @ (update.OrderBy |> List.collect (fst >> expressionAccesses boundCtes defaultDb))
    @ (update.Limit |> Option.map (expressionAccesses boundCtes defaultDb) |> Option.defaultValue [])

let private deleteAccesses defaultDb (delete: DeleteStmt) =
    let ctes, boundCtes = cteAccesses Set.empty defaultDb delete.Ctes
    let written = delete.Targets |> List.map normalize |> Set.ofList

    let source (table: TableRef) =
        if table.Database.IsNone && Set.contains (normalize table.Table) boundCtes then
            []
        else
            let qualifier = table.Alias |> Option.defaultValue table.Table
            [ tableReference defaultDb (if Set.contains (normalize qualifier) written then WriteAccess else ReadAccess) table ]

    ctes
    @ source delete.From
    @ (delete.Joins
       |> List.collect (fun join ->
           (match join.Table with
            | FromTable table -> source table
            | nested -> sourceAccesses boundCtes defaultDb nested)
           @ expressionAccesses boundCtes defaultDb join.On))
    @ (delete.Where |> Option.map (expressionAccesses boundCtes defaultDb) |> Option.defaultValue [])
    @ (delete.OrderBy |> List.collect (fst >> expressionAccesses boundCtes defaultDb))
    @ (delete.Limit |> Option.map (expressionAccesses boundCtes defaultDb) |> Option.defaultValue [])

let rec private directStatementAccesses defaultDb =
    function
    | Select select -> selectAccesses Set.empty defaultDb select
    | Union(first, rest, orderBy, limit, offset) ->
        selectOrUnionAccesses Set.empty defaultDb (UnionSelect(first, rest, orderBy, limit, offset))
    | Insert(table, _, rows, onDuplicate, _) ->
        namedAccess defaultDb WriteAccess table
        :: expressionsAccesses defaultDb ((rows |> List.collect id) @ (onDuplicate |> List.map snd))
    | Replace(table, _, rows) ->
        namedAccess defaultDb WriteAccess table
        :: expressionsAccesses defaultDb (rows |> List.collect id)
    | InsertSelect(table, _, select, onDuplicate, _) ->
        namedAccess defaultDb WriteAccess table
        :: (selectAccesses Set.empty defaultDb select @ expressionsAccesses defaultDb (onDuplicate |> List.map snd))
    | ReplaceSelect(table, _, select) ->
        namedAccess defaultDb WriteAccess table :: selectAccesses Set.empty defaultDb select
    | ReplaceSet(table, assignments) ->
        namedAccess defaultDb WriteAccess table
        :: expressionsAccesses defaultDb (assignments |> List.map snd)
    | LoadData load ->
        namedAccess defaultDb WriteAccess load.Table
        :: expressionsAccesses defaultDb (load.Assignments |> List.map snd)
    | Update update -> updateAccesses defaultDb update
    | Delete delete -> deleteAccesses defaultDb delete
    | Do expressions -> expressionsAccesses defaultDb expressions
    | ChecksumTables(tables, _) -> tables |> List.map (namedAccess defaultDb ReadAccess)
    | Explain(_, statement) -> directStatementAccesses defaultDb statement
    | _ -> []

let private requirementMode (privilege: string) =
    if privilege.Equals("SELECT", StringComparison.OrdinalIgnoreCase) then ReadAccess else WriteAccess

let private requiredAccesses store defaultDb statement =
    Auth.requiredPrivilegesInStore store defaultDb statement
    |> List.choose (fun (privilege, target) ->
        match target with
        | Auth.OnTable(database, table) -> Some(access database table (Some table) (requirementMode privilege))
        | _ -> None)

let private mergeAccesses accesses =
    accesses
    |> List.fold
        (fun merged (current: Access) ->
            let key =
                tableKey current.Database current.Table,
                current.ReferenceName |> Option.map normalize

            match Map.tryFind key merged with
            | Some(previous: Access) -> Map.add key { previous with Mode = stronger previous.Mode current.Mode } merged
            | None -> Map.add key current merged)
        Map.empty
    |> Map.values
    |> List.ofSeq

let private viewEntries store =
    match scan store "mysql" "views" with
    | Error _ -> Map.empty
    | Ok(_, rows) ->
        rows
        |> Seq.choose SystemCatalog.View.tryRead
        |> Seq.map (fun view -> tableKey view.Schema view.Name, view)
        |> Map.ofSeq

let private triggerEntries store =
    match scan store "mysql" "triggers" with
    | Error _ -> []
    | Ok(_, rows) -> rows |> Seq.choose SystemCatalog.Trigger.tryRead |> List.ofSeq

let private routineEntries store : Map<string, SystemCatalog.Routine.Entry> =
    match scan store "mysql" "routines" with
    | Error _ -> Map.empty
    | Ok(_, rows) ->
        rows
        |> Seq.choose SystemCatalog.Routine.tryRead
        |> Seq.map (fun routine -> tableKey routine.Schema routine.Name, routine)
        |> Map.ofSeq

let private storedProgramStatements
    (routines: Map<string, SystemCatalog.Routine.Entry>)
    defaultDb
    (options: Parser.ParserOptions)
    (statements: StoredProgram.Statement list)
    =
    let rec collect visited defaultDb (options: Parser.ParserOptions) statements =
        let direct = statements |> List.collect StoredProgram.sqlStatements

        let called =
            statements
            |> List.collect StoredProgram.textSqlStatements
            |> List.choose (StoredProgram.tryCall options)
            |> List.collect (fun name ->
                let database, name = splitQualified defaultDb name
                let key = tableKey database name

                match Set.contains key visited, Map.tryFind key routines with
                | true, _
                | false, None -> []
                | false, Some(routine: SystemCatalog.Routine.Entry) ->
                    let options = SqlMode.parserOptionsFor routine.SqlMode

                    match StoredProgram.parseRoutine options (StoredProgram.tryCall options >> Option.isSome) routine.Definition with
                    | Error _ -> []
                    | Ok body -> collect (Set.add key visited) routine.Schema options body)

        direct @ called

    collect Set.empty defaultDb options statements

let private expandDependencies store accesses =
    let views = viewEntries store
    let triggers = triggerEntries store
    let routines = routineEntries store

    let rec expand visited access =
        let key = tableKey access.Database access.Table

        if Set.contains key visited then
            [ access ]
        else
            let visited = Set.add key visited

            let viewDependencies =
                match Map.tryFind key views with
                | None -> []
                | Some view ->
                    match Parser.parseViewDefinition view.Definition with
                    | Error _ -> []
                    | Ok definition ->
                        directStatementAccesses view.Schema definition.Statement
                        |> List.collect (fun dependency ->
                            { dependency with
                                ReferenceName = None
                                Mode = stronger access.Mode dependency.Mode }
                            |> expand visited)

            let triggerDependencies =
                if access.Mode = ReadAccess then
                    []
                else
                    triggers
                    |> List.filter (fun trigger -> tableKey trigger.Schema trigger.Table = key)
                    |> List.collect (fun trigger ->
                        let options = SqlMode.parserOptionsFor trigger.SqlMode

                        match StoredProgram.parseTrigger options trigger.Body with
                        | Error _ -> []
                        | Ok statements ->
                            storedProgramStatements routines trigger.Schema options statements
                            |> List.collect (directStatementAccesses trigger.Schema)
                            |> List.collect (fun dependency ->
                                expand visited { dependency with ReferenceName = None }))

            access :: (viewDependencies @ triggerDependencies)

    accesses |> List.collect (expand Set.empty) |> mergeAccesses

let private isTemporary (catalog: Catalog) access =
    catalog
    |> Map.tryFind (normalize access.Database)
    |> Option.exists (Map.containsKey (normalizeTableName access.Table))

let private requiresOwnership temporaryCatalog access =
    not (access.Database.Equals("information_schema", StringComparison.OrdinalIgnoreCase))
    && not (isTemporary temporaryCatalog access)

let accessesForStatement store temporaryCatalog defaultDb statement =
    let direct = directStatementAccesses defaultDb statement
    let represented = direct |> List.map (fun access -> tableKey access.Database access.Table) |> Set.ofList

    let fallback =
        requiredAccesses store defaultDb statement
        |> List.filter (fun access -> not (Set.contains (tableKey access.Database access.Table) represented))

    direct @ fallback
    |> List.filter (requiresOwnership temporaryCatalog)
    |> mergeAccesses
    |> expandDependencies store

let explicitAccesses store temporaryCatalog defaultDb locks =
    let views = viewEntries store

    let rec resolve resolved =
        function
        | [] -> Ok(List.rev resolved)
        | (requested: ExplicitTableLock) :: rest ->
            let database, table = splitName defaultDb requested.Name
            let mode =
                match requested.Mode with
                | ReadTableLock -> ReadAccess
                | WriteTableLock -> WriteAccess

            let current =
                access database table (Some(requested.Alias |> Option.defaultValue table)) mode

            if isTemporary temporaryCatalog current then
                resolve resolved rest
            else
                match tableSnapshot store database table, Map.containsKey (tableKey database table) views with
                | Ok _, _
                | _, true -> resolve (current :: resolved) rest
                | Error _, false -> Error(1146, sprintf "Table '%s.%s' doesn't exist" database table)

    resolve [] locks |> Result.map (expandDependencies store)
