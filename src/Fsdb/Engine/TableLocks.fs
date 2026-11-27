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
    | UpdateSourceAccess
    | UpdateReadAccess
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
      TransactionAccesses: Dictionary<int, AccessMode>
      mutable PreparedReaders: int
      mutable PreparedWriters: int
      mutable MetadataWriter: int option
      WaitingMetadataWriters: ResizeArray<int>
      WaitingExplicit: ResizeArray<int * AccessMode>
      WaitingWriters: HashSet<int>
      PrioritizedWriters: HashSet<int> }

type private ExplicitContext =
    { Names: Map<string, Access>
      Physical: Map<string, AccessMode> }

type private GlobalReadPolicy =
    | RespectGlobalRead
    | IgnoreGlobalRead

type private MetadataWait =
    { Blockers: unit -> seq<int64>
      Exclusive: bool
      mutable Victim: bool }

type private MetadataWaitResult = Granted | TimedOut | Deadlocked

type internal ExplicitAcquireResult = ExplicitAcquired | ExplicitPlanChanged

type private Manager =
    { SyncRoot: obj
      Tables: Dictionary<string, TableState>
      Explicit: Dictionary<int, ExplicitContext>
      TransactionTables: Dictionary<int, HashSet<string>>
      PreparedTables: Dictionary<Xa.Xid, Map<string, AccessMode>>
      MetadataWaits: Dictionary<int64, MetadataWait>
      GlobalReaders: HashSet<int>
      WaitingGlobalReaders: HashSet<int> }

let private managers =
    ConditionalWeakTable<ConcurrentDictionary<string, Database ref>, Manager>()

let private managerFor (store: Store) =
    managers.GetValue(
        store.Databases,
        fun _ ->
            { SyncRoot = obj ()
              Tables = Dictionary(StringComparer.OrdinalIgnoreCase)
              Explicit = Dictionary()
              TransactionTables = Dictionary()
              PreparedTables = Dictionary()
              MetadataWaits = Dictionary()
              GlobalReaders = HashSet()
              WaitingGlobalReaders = HashSet() }
    )

let private normalize (value: string) = value.ToLowerInvariant()
let private tableKey database table = normalize database + "\u0000" + normalizeTableName table

let private stronger left right =
    match left, right with
    | WriteAccess, _
    | _, WriteAccess -> WriteAccess
    | UpdateReadAccess, _
    | _, UpdateReadAccess -> UpdateReadAccess
    | UpdateSourceAccess, _
    | _, UpdateSourceAccess -> UpdateSourceAccess
    | _ -> ReadAccess

let private physicalAccesses accesses =
    accesses
    |> List.fold
        (fun result access ->
            let key = tableKey access.Database access.Table
            let mode = result |> Map.tryFind key |> Option.map (stronger access.Mode) |> Option.defaultValue access.Mode
            Map.add key mode result)
        Map.empty

let private writesRows mode = mode = WriteAccess
let private writesMetadata mode = mode <> ReadAccess
let private requiresExplicitWrite mode = mode = WriteAccess || mode = UpdateReadAccess

let private stateFor (manager: Manager) key =
    match manager.Tables.TryGetValue key with
    | true, state -> state
    | false, _ ->
        let state =
            { ExplicitReaders = HashSet()
              ExplicitWriter = None
              StatementReaders = HashSet()
              StatementWriters = HashSet()
              TransactionAccesses = Dictionary()
              PreparedReaders = 0
              PreparedWriters = 0
              MetadataWriter = None
              WaitingMetadataWriters = ResizeArray()
              WaitingExplicit = ResizeArray()
              WaitingWriters = HashSet()
              PrioritizedWriters = HashSet() }

        manager.Tables.Add(key, state)
        state

let private hasNoOwners (owners: HashSet<int>) = owners.Count = 0

let private hasNoOtherOwner owner (owners: HashSet<int>) =
    hasNoOwners owners || (owners.Count = 1 && owners.Contains owner)

let private readerGateOpen owner (manager: Manager) (state: TableState) =
    not (hasNoOwners manager.GlobalReaders)
    || state.TransactionAccesses.ContainsKey owner
    || (hasNoOtherOwner owner state.WaitingWriters
        && hasNoOtherOwner owner state.PrioritizedWriters)

let private globalWriteAvailable policy (manager: Manager) =
    match policy with
    | IgnoreGlobalRead -> true
    | RespectGlobalRead ->
        hasNoOwners manager.GlobalReaders
        && hasNoOwners manager.WaitingGlobalReaders

let private containsWrite physical =
    physical |> Map.exists (fun _ mode -> writesRows mode)

let private conflictsWithOwnedGlobalRead owner (manager: Manager) physical =
    containsWrite physical && manager.GlobalReaders.Contains owner

let private blockedByOwnedGlobalRead policy owner manager physical =
    match policy with
    | IgnoreGlobalRead -> false
    | RespectGlobalRead -> conflictsWithOwnedGlobalRead owner manager physical

let private ownsSharedMetadata owner (state: TableState) =
    state.TransactionAccesses.ContainsKey owner
    || state.StatementReaders.Contains owner
    || state.StatementWriters.Contains owner
    || state.ExplicitReaders.Contains owner
    || state.ExplicitWriter = Some owner

let private sharedMetadataBlocker owner (state: TableState) =
    match state.MetadataWriter with
    | Some writer when writer <> owner -> Some writer
    | Some _ -> None
    | _ when state.WaitingMetadataWriters.Count > 0
             && state.WaitingMetadataWriters.[0] <> owner
             && not (ownsSharedMetadata owner state) -> Some state.WaitingMetadataWriters.[0]
    | _ -> None

let private canReuseMetadata owner mode (state: TableState) =
    match state.TransactionAccesses.TryGetValue owner with
    | true, held -> not (writesMetadata mode) || writesMetadata held
    | _ -> false

let private blocksAccess lockMode mode = writesRows lockMode || writesMetadata mode

let private pendingExplicitBlocker owner mode (state: TableState) =
    if state.WaitingExplicit.Count = 0 || state.MetadataWriter = Some owner || canReuseMetadata owner mode state then None
    else
        state.WaitingExplicit
        |> Seq.tryPick (fun (waiting, lockMode) ->
            if waiting <> owner && blocksAccess lockMode mode then Some waiting else None)

let private stateIsIdle (state: TableState) =
    state.ExplicitWriter.IsNone
    && hasNoOwners state.ExplicitReaders
    && hasNoOwners state.StatementReaders
    && hasNoOwners state.StatementWriters
    && state.TransactionAccesses.Count = 0
    && state.PreparedReaders = 0
    && state.MetadataWriter.IsNone
    && state.WaitingMetadataWriters.Count = 0
    && state.WaitingExplicit.Count = 0
    && hasNoOwners state.WaitingWriters
    && hasNoOwners state.PrioritizedWriters

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
        | WriteAccess -> state.ExplicitWriter <- Some owner
        | _ -> state.ExplicitReaders.Add owner |> ignore

let private releasePhysical owner (manager: Manager) physical =
    for KeyValue(key, mode) in physical do
        match manager.Tables.TryGetValue key with
        | false, _ -> ()
        | true, state ->
            match mode with
            | WriteAccess when state.ExplicitWriter = Some owner -> state.ExplicitWriter <- None
            | WriteAccess -> ()
            | _ -> state.ExplicitReaders.Remove owner |> ignore

            removeIdleState manager key state

let private waitUntilDeadline syncRoot deadline ready =
    let cancellation = Limits.queryCancellation.Value
    let maxWait = if cancellation.CanBeCanceled then 100 else Int32.MaxValue

    let rec wait () =
        cancellation.ThrowIfCancellationRequested()
        if ready () then
            true
        else
            let remaining = deadline () - DateTime.UtcNow
            if remaining <= TimeSpan.Zero then false
            else
                Monitor.Wait(syncRoot, min remaining (TimeSpan.FromMilliseconds(float maxWait))) |> ignore
                wait ()

    wait ()

let private waitUntil timeout syncRoot ready =
    let deadline = DateTime.UtcNow + timeout
    waitUntilDeadline syncRoot (fun () -> deadline) ready

let private metadataWaitError = function
    | Deadlocked -> 1213, "Deadlock found when trying to get lock; try restarting transaction"
    | _ -> 1205, "Lock wait timeout exceeded; try restarting transaction"

let private waitForMetadata timeout metadataTimeout owner exclusive (manager: Manager) ready blockers =
    Limits.queryCancellation.Value.ThrowIfCancellationRequested()
    if ready () then Granted
    else
        let owner = int64 owner
        let started = DateTime.UtcNow
        let request = { Blockers = blockers; Exclusive = exclusive; Victim = false }
        manager.MetadataWaits.Add(owner, request)
        let successors id =
            match manager.MetadataWaits.TryGetValue id with
            | true, pending when not pending.Victim -> pending.Blockers ()
            | _ -> Seq.empty
        let canComplete () =
            if request.Victim || ready () then true
            else
                match tryFindLockWaitPath owner (blockers ()) successors with
                | None -> false
                | Some cycle ->
                    // A statement is cheaper to retry than an exclusive DDL request.
                    let victim = cycle |> List.minBy (fun id -> manager.MetadataWaits.[id].Exclusive, id <> owner)
                    manager.MetadataWaits.[victim].Victim <- true
                    Monitor.PulseAll manager.SyncRoot
                    request.Victim
        let deadline () =
            started + (if Seq.isEmpty (blockers ()) then timeout else metadataTimeout)
        try
            let completed = waitUntilDeadline manager.SyncRoot deadline canComplete
            if request.Victim then Deadlocked
            elif completed then Granted
            else TimedOut
        finally
            manager.MetadataWaits.Remove owner |> ignore

let private statementBlockers owner mode (state: TableState) =
    seq {
        yield! sharedMetadataBlocker owner state |> Option.toList
        yield! pendingExplicitBlocker owner mode state |> Option.toList
        yield! state.ExplicitWriter |> Option.toList
        if writesMetadata mode then yield! state.ExplicitReaders
    }
    |> Seq.filter ((<>) owner)

let private waitForStatement policy timeout metadataTimeout owner (manager: Manager) physical =
    let blockers () =
        seq {
            for KeyValue(key, mode) in physical do
                match manager.Tables.TryGetValue key with
                | true, state -> yield! statementBlockers owner mode state |> Seq.map int64
                | _ -> ()
        }

    let ready () =
        physical
        |> Map.forall (fun key mode ->
            (not (writesRows mode) || globalWriteAvailable policy manager)
            && match manager.Tables.TryGetValue key with
               | false, _ -> true
               | true, state ->
                   (sharedMetadataBlocker owner state).IsNone
                   && (pendingExplicitBlocker owner mode state).IsNone
                   && (state.ExplicitWriter |> Option.forall ((=) owner))
                   && (if writesMetadata mode then hasNoOtherOwner owner state.ExplicitReaders
                       else readerGateOpen owner manager state))

    let waited = not (ready ())
    waitForMetadata timeout metadataTimeout owner false manager ready blockers, waited

let private waitForExplicit timeout owner (manager: Manager) physical planIsCurrent =
    let targets = physical |> Map.toList |> List.map (fun (key, mode) -> key, mode, stateFor manager key)
    let blockers () =
        seq {
            for _, mode, state in targets do
                yield! sharedMetadataBlocker owner state |> Option.toList
                yield! state.ExplicitWriter |> Option.toList
                yield! state.StatementWriters
                for KeyValue(holder, held) in state.TransactionAccesses do
                    if blocksAccess mode held then yield holder
                if writesRows mode then
                    yield! state.ExplicitReaders
                    yield! state.StatementReaders
                for waiting, earlierMode in state.WaitingExplicit |> Seq.takeWhile (fun (waiting, _) -> waiting <> owner) do
                    if blocksAccess earlierMode mode then yield waiting
        }
        |> Seq.filter ((<>) owner)
        |> Seq.map int64

    let ready () =
        not (planIsCurrent ())
        || ((targets |> List.forall (fun (_, mode, state) ->
                if writesRows mode then state.PreparedReaders = 0
                else state.PreparedWriters = 0))
            && (blockers () |> Seq.isEmpty))

    for _, mode, state in targets do
        state.WaitingExplicit.Add(owner, mode)

    try
        waitForMetadata timeout timeout owner (containsWrite physical) manager ready blockers
    finally
        for key, mode, state in targets do
            state.WaitingExplicit.Remove(owner, mode) |> ignore
            removeIdleState manager key state
        Monitor.PulseAll manager.SyncRoot

let private acquireStatement owner (manager: Manager) physical =
    physical |> Map.filter (fun key mode ->
        let state = stateFor manager key

        if writesMetadata mode then state.StatementWriters.Add owner
        else state.StatementReaders.Add owner)

let private releaseStatement owner (manager: Manager) physical =
    for KeyValue(key, mode) in physical do
        match manager.Tables.TryGetValue key with
        | false, _ -> ()
        | true, state ->
            if writesMetadata mode then
                state.StatementWriters.Remove owner |> ignore
                state.PrioritizedWriters.Remove owner |> ignore
            else
                state.StatementReaders.Remove owner |> ignore

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

let internal acquireExplicitRechecking timeout store owner accesses planIsCurrent =
    let manager = managerFor store

    match buildNames accesses with
    | Error error -> Error error
    | Ok names ->
        let physical = physicalAccesses accesses

        lock manager.SyncRoot (fun () ->
            if conflictsWithOwnedGlobalRead owner manager physical then
                Error(1223, "Can't execute the query because you have a conflicting read lock")
            else
                let released = releaseExplicitUnderLock owner manager

                if released then
                    Monitor.PulseAll manager.SyncRoot

                let deadline = DateTime.UtcNow + timeout
                // A writer queues for table metadata only after passing the global read gate.
                let acquired =
                    if containsWrite physical
                       && not (waitUntilDeadline manager.SyncRoot (fun () -> deadline) (fun () -> globalWriteAvailable RespectGlobalRead manager)) then
                        TimedOut
                    else
                        trackWaitingWriter owner manager physical (fun () ->
                            waitForExplicit (max TimeSpan.Zero (deadline - DateTime.UtcNow)) owner manager physical planIsCurrent)

                match acquired with
                | Granted when not (planIsCurrent ()) -> Ok ExplicitPlanChanged
                | Granted ->
                    acquirePhysical owner manager physical
                    manager.Explicit.[owner] <- { Names = names; Physical = physical }
                    Ok ExplicitAcquired
                | failure -> Error(metadataWaitError failure))

let acquireExplicit timeout store owner accesses =
    acquireExplicitRechecking timeout store owner accesses (fun () -> true) |> Result.map ignore

let releaseExplicit store owner =
    let manager = managerFor store

    lock manager.SyncRoot (fun () ->
        if releaseExplicitUnderLock owner manager then
            Monitor.PulseAll manager.SyncRoot)

let holdsExplicit store owner =
    let manager = managerFor store
    lock manager.SyncRoot (fun () -> manager.Explicit.ContainsKey owner)

let private globalReadAvailable (manager: Manager) =
    manager.Tables.Values
    |> Seq.forall (fun state ->
        state.ExplicitWriter.IsNone
        && hasNoOwners state.StatementWriters
        && not (state.WaitingExplicit |> Seq.exists (snd >> writesRows)))

let acquireGlobalRead timeout store owner =
    let manager = managerFor store

    lock manager.SyncRoot (fun () ->
        if manager.GlobalReaders.Contains owner then
            Ok()
        else
            manager.WaitingGlobalReaders.Add owner |> ignore

            try
                if waitUntil timeout manager.SyncRoot (fun () -> globalReadAvailable manager) then
                    manager.GlobalReaders.Add owner |> ignore
                    Ok()
                else
                    Error(1205, "Lock wait timeout exceeded; try restarting transaction")
            finally
                manager.WaitingGlobalReaders.Remove owner |> ignore
                Monitor.PulseAll manager.SyncRoot)

let releaseGlobalRead store owner =
    let manager = managerFor store

    lock manager.SyncRoot (fun () ->
        if manager.GlobalReaders.Remove owner then
            Monitor.PulseAll manager.SyncRoot)

let holdsGlobalRead store owner =
    let manager = managerFor store
    lock manager.SyncRoot (fun () -> manager.GlobalReaders.Contains owner)

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

    match held with
    | None -> Error(1100, sprintf "Table '%s' was not locked with LOCK TABLES" (accessName access))
    | Some mode when not (requiresExplicitWrite mode) && requiresExplicitWrite access.Mode ->
        Error(1099, sprintf "Table '%s' was locked with a READ lock and can't be updated" (accessName access))
    | _ -> Ok()

let private retainTransactionAccess owner (manager: Manager) physical =
    if not (Map.isEmpty physical) then
        let tables =
            match manager.TransactionTables.TryGetValue owner with
            | true, tables -> tables
            | false, _ ->
                let tables = HashSet()
                manager.TransactionTables.Add(owner, tables)
                tables
        for KeyValue(key, mode) in physical do
            let accesses = (stateFor manager key).TransactionAccesses
            if tables.Add key then accesses.Add(owner, mode)
            elif writesMetadata mode then accesses.[owner] <- mode

let internal transactionAccesses store owner =
    let manager = managerFor store
    lock manager.SyncRoot (fun () ->
        match manager.TransactionTables.TryGetValue owner with
        | false, _ -> Set.empty
        | true, tables -> Set.ofSeq tables)

let internal releaseLaterTransactionAccess store owner retained =
    let manager = managerFor store
    lock manager.SyncRoot (fun () ->
        match manager.TransactionTables.TryGetValue owner with
        | false, _ -> ()
        | true, tables ->
            let released = tables |> Seq.filter (fun key -> not (Set.contains key retained)) |> Seq.toArray
            for key in released do
                tables.Remove key |> ignore
                let state = manager.Tables.[key]
                state.TransactionAccesses.Remove owner |> ignore
                removeIdleState manager key state
            if tables.Count = 0 then manager.TransactionTables.Remove owner |> ignore
            if released.Length > 0 then Monitor.PulseAll manager.SyncRoot)

let internal releaseTransactionAccess store owner =
    releaseLaterTransactionAccess store owner Set.empty

let private retainPreparedTables (manager: Manager) xid tables =
    manager.PreparedTables.Add(xid, tables)
    for KeyValue(key, mode) in tables do
        let state = stateFor manager key
        state.PreparedReaders <- state.PreparedReaders + 1
        if writesMetadata mode then state.PreparedWriters <- state.PreparedWriters + 1

let internal detachTransactionAccess store owner xid =
    let manager = managerFor store
    lock manager.SyncRoot (fun () ->
        match manager.TransactionTables.TryGetValue owner with
        | false, _ -> ()
        | true, tables ->
            let prepared =
                tables |> Seq.map (fun key -> key, manager.Tables.[key].TransactionAccesses.[owner]) |> Map.ofSeq
            retainPreparedTables manager xid prepared
            manager.TransactionTables.Remove owner |> ignore
            for key in tables do
                manager.Tables.[key].TransactionAccesses.Remove owner |> ignore
            Monitor.PulseAll manager.SyncRoot)

let internal releasePreparedAccess store xid =
    let manager = managerFor store
    lock manager.SyncRoot (fun () ->
        match manager.PreparedTables.TryGetValue xid with
        | false, _ -> ()
        | true, tables ->
            manager.PreparedTables.Remove xid |> ignore
            for KeyValue(key, mode) in tables do
                let state = manager.Tables.[key]
                state.PreparedReaders <- state.PreparedReaders - 1
                if writesMetadata mode then state.PreparedWriters <- state.PreparedWriters - 1
                removeIdleState manager key state
            Monitor.PulseAll manager.SyncRoot)

let internal restorePreparedAccess store =
    // Recovery reconstructs metadata for tables with durable row changes.
    // Views and other tables opened only for reading have no such record.
    let rec writtenTables tables = function
        | WithDdlAllowInvalidDates(_, event)
        | WithFullTextWordLengths(_, event)
        | WithNgramTokenSize(_, event)
        | WithStopwordFiltering(_, event)
        | WithFullTextStopwordSettings(_, event)
        | WithFullTextStopwords(_, _, _, event) -> writtenTables tables event
        | TransactionCommitted events -> List.fold writtenTables tables events
        | RowsInserted(database, table, _)
        | RowsUpdated(database, table, _)
        | RowsDeleted(database, table, _)
        | RowsUpdatedById(database, table, _)
        | RowsDeletedById(database, table, _) -> Map.add (tableKey database table) WriteAccess tables
        | _ -> tables

    let branches = preparedXas store
    if not branches.IsEmpty then
        let manager = managerFor store
        lock manager.SyncRoot (fun () ->
            for xid, branch in branches do
                let tables = List.fold writtenTables Map.empty branch.Events
                if not tables.IsEmpty then
                    retainPreparedTables manager xid tables)

let internal withMetadataWrite timeout store owner targets accesses body =
    let manager = managerFor store
    let keys = targets |> List.map (fun (database, table) -> tableKey database table) |> Set.ofList
    let physical = physicalAccesses accesses
    let reserved = ResizeArray<string>()
    let reserve key =
        let state = stateFor manager key
        if state.MetadataWriter = Some owner then Granted
        else
            state.WaitingMetadataWriters.Add owner
            let blockers () =
                seq {
                    yield! state.MetadataWriter |> Option.toList
                    yield! state.ExplicitWriter |> Option.toList
                    yield! state.ExplicitReaders
                    yield! state.StatementReaders
                    yield! state.StatementWriters
                    yield! state.TransactionAccesses.Keys
                    yield state.WaitingMetadataWriters.[0]
                }
                |> Seq.filter ((<>) owner)
                |> Seq.map int64
            try
                // Detached readers can block DDL but cannot wait for another owner.
                let available () = state.PreparedReaders = 0 && (blockers () |> Seq.isEmpty)
                let result = waitForMetadata timeout timeout owner true manager available blockers
                if result = Granted then
                    state.MetadataWriter <- Some owner
                    reserved.Add key
                result
            finally
                state.WaitingMetadataWriters.Remove owner |> ignore
                removeIdleState manager key state
                Monitor.PulseAll manager.SyncRoot

    let mutable statements = Map.empty
    try
        let result =
            lock manager.SyncRoot (fun () ->
                // A multi-table DDL establishes its reservations in name order.
                let rec reserveAll = function
                    | [] ->
                        waitForStatement IgnoreGlobalRead timeout timeout owner manager physical |> fst
                    | key :: rest ->
                        match reserve key with
                        | Granted -> reserveAll rest
                        | failure -> failure
                let result = reserveAll (Set.toList keys)
                if result = Granted then statements <- acquireStatement owner manager physical
                result)
        match result with
        | Granted -> Ok(body ())
        | failure -> Error(metadataWaitError failure)
    finally
        lock manager.SyncRoot (fun () ->
            releaseStatement owner manager statements
            for key in reserved do
                let state = manager.Tables.[key]
                state.MetadataWriter <- None
                removeIdleState manager key state
            Monitor.PulseAll manager.SyncRoot)

let private withStatementAccessUnder policy timeout metadataTimeout store owner accesses retain body =
    let manager = managerFor store
    let physical = physicalAccesses accesses

    let acquired =
        lock manager.SyncRoot (fun () ->
            if blockedByOwnedGlobalRead policy owner manager physical then
                Error(1223, "Can't execute the query because you have a conflicting read lock")
            else
                match manager.Explicit.TryGetValue owner with
                | true, context ->
                    accesses
                    |> List.fold
                        (fun result access -> result |> Result.bind (fun () -> validateExplicitAccess context access))
                        (Ok())
                    |> Result.map (fun () -> Map.empty)
                | false, _ ->
                    let result, waited =
                        trackWaitingWriter owner manager physical (fun () ->
                            waitForStatement policy timeout metadataTimeout owner manager physical)

                    match result with
                    | Granted ->
                        let acquired = acquireStatement owner manager physical

                        if waited then
                            markPrioritizedWriter owner manager acquired

                        Ok acquired
                    | failure -> Error(metadataWaitError failure))

    match acquired with
    | Error error -> Error error
    | Ok acquired ->
        try
            let result = body ()
            if retain result then
                lock manager.SyncRoot (fun () -> retainTransactionAccess owner manager physical)
            Ok result
        finally
            if not (Map.isEmpty acquired) then
                lock manager.SyncRoot (fun () ->
                    releaseStatement owner manager acquired
                    Monitor.PulseAll manager.SyncRoot)

let internal withStatementAccessRetaining timeout metadataTimeout store owner accesses retain body =
    withStatementAccessUnder RespectGlobalRead timeout metadataTimeout store owner accesses retain body

let withStatementAccess timeout store owner accesses body =
    withStatementAccessRetaining timeout timeout store owner accesses (fun _ -> false) body

let internal withTemporaryStatementAccessRetaining timeout metadataTimeout store owner accesses retain body =
    // Temporary sources have already been removed from `accesses`; a remaining
    // writer still changes shared state and must obey the global read lock.
    let policy =
        if accesses |> List.exists (fun access -> access.Mode = WriteAccess) then RespectGlobalRead
        else IgnoreGlobalRead

    withStatementAccessUnder policy timeout metadataTimeout store owner accesses retain body

let withTemporaryStatementAccess timeout store owner accesses body =
    withTemporaryStatementAccessRetaining timeout timeout store owner accesses (fun _ -> false) body

let withGlobalWriteAccess timeout store owner body =
    let access =
        { Database = ""
          Table = "\u0000global-write"
          ReferenceName = None
          Mode = WriteAccess }

    withStatementAccess timeout store owner [ access ] body

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

and private optionalExpressionAccesses boundCtes defaultDb expression =
    expression
    |> Option.toList
    |> List.collect (expressionAccesses boundCtes defaultDb)

and private sourceAccesses boundCtes defaultDb source =
    queryBlockSourceAccesses boundCtes defaultDb (fun _ -> ReadAccess) source

and private queryBlockSourceAccesses boundCtes defaultDb modeForTable =
    function
    | FromJoinGroup(source, joins) ->
        queryBlockSourceAccesses boundCtes defaultDb modeForTable source
        @ (joins |> List.collect (fun join ->
            queryBlockSourceAccesses boundCtes defaultDb modeForTable join.Table @ expressionAccesses boundCtes defaultDb join.On))
    | FromTable table when table.Database.IsNone && Set.contains (normalize table.Table) boundCtes -> []
    | FromTable table -> [ tableReference defaultDb (modeForTable table) table ]
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
        @ optionalExpressionAccesses boundCtes defaultDb limit
        @ optionalExpressionAccesses boundCtes defaultDb offset

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
    let updateLocks = select.Locking |> List.filter (fun clause -> clause.Strength = UpdateLock)
    let blockSource =
        queryBlockSourceAccesses localCtes defaultDb (fun table ->
            let qualifier = table.Alias |> Option.defaultValue table.Table
            let matches (clause: LockingRead) =
                clause.Tables.IsEmpty
                || (clause.Tables |> List.exists (fun name -> name.Equals(qualifier, StringComparison.OrdinalIgnoreCase)))
            if List.exists matches updateLocks then UpdateReadAccess else ReadAccess)

    ctes
    @ (select.From |> Option.toList |> List.collect blockSource)
    @ (select.Joins
       |> List.collect (fun join ->
           blockSource join.Table
           @ expressionAccesses localCtes defaultDb join.On))
    @ (select.Projections |> List.collect (_.Expression >> expressionAccesses localCtes defaultDb))
    @ optionalExpressionAccesses localCtes defaultDb select.Where
    @ (select.GroupBy |> List.collect (expressionAccesses localCtes defaultDb))
    @ optionalExpressionAccesses localCtes defaultDb select.Having
    @ (select.OrderBy |> List.collect (fst >> expressionAccesses localCtes defaultDb))
    @ (select.Windows
       |> List.collect (snd >> OverSpec >> Expression.overExpressions >> List.collect (expressionAccesses localCtes defaultDb)))
    @ optionalExpressionAccesses localCtes defaultDb select.Limit
    @ optionalExpressionAccesses localCtes defaultDb select.Offset

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
            |> List.collect (fun join -> FromItem.leaves join.Table)
            |> List.choose (function
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
            let mode =
                if Set.contains (normalize qualifier) written then WriteAccess
                else UpdateSourceAccess
            tableReference defaultDb mode table)

    ctes
    @ physicalSources
    @ (update.Joins
       |> List.collect (fun join ->
           (match join.Table with
            | FromTable _ -> []
            | source -> sourceAccesses boundCtes defaultDb source)
           @ expressionAccesses boundCtes defaultDb join.On))
    @ (update.Assignments |> List.collect (_.Value >> expressionAccesses boundCtes defaultDb))
    @ optionalExpressionAccesses boundCtes defaultDb update.Where
    @ (update.OrderBy |> List.collect (fst >> expressionAccesses boundCtes defaultDb))
    @ optionalExpressionAccesses boundCtes defaultDb update.Limit

let private deleteAccesses defaultDb (delete: DeleteStmt) =
    let ctes, boundCtes = cteAccesses Set.empty defaultDb delete.Ctes
    let written = delete.Targets |> List.map normalize |> Set.ofList

    let source (table: TableRef) =
        if table.Database.IsNone && Set.contains (normalize table.Table) boundCtes then
            []
        else
            let qualifier = table.Alias |> Option.defaultValue table.Table
            [ tableReference defaultDb (if Set.contains (normalize qualifier) written then WriteAccess else ReadAccess) table ]

    let rec sourceItem = function
        | FromTable table -> source table
        | FromJoinGroup(first, joins) ->
            sourceItem first
            @ (joins |> List.collect (fun join -> sourceItem join.Table @ expressionAccesses boundCtes defaultDb join.On))
        | nested -> sourceAccesses boundCtes defaultDb nested

    ctes
    @ sourceItem delete.From
    @ (delete.Joins
       |> List.collect (fun join ->
           sourceItem join.Table
           @ expressionAccesses boundCtes defaultDb join.On))
    @ optionalExpressionAccesses boundCtes defaultDb delete.Where
    @ (delete.OrderBy |> List.collect (fst >> expressionAccesses boundCtes defaultDb))
    @ optionalExpressionAccesses boundCtes defaultDb delete.Limit

let rec internal directStatementAccesses defaultDb =
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
    | Explain(_, statement) ->
        directStatementAccesses defaultDb statement
        |> List.map (fun access ->
            if access.Mode = UpdateReadAccess then { access with Mode = ReadAccess } else access)
    | _ -> []

let rec private directAccessesAreComplete =
    function
    | Select _
    | Union _
    | Insert _
    | Replace _
    | InsertSelect _
    | ReplaceSelect _
    | ReplaceSet _
    | LoadData _
    | Update _
    | Delete _
    | Do _
    | ChecksumTables _ -> true
    | Explain(_, statement) -> directAccessesAreComplete statement
    | _ -> false

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

let private systemEntries store table read =
    match scan store "mysql" table with
    | Error _ -> Seq.empty
    | Ok(_, rows) -> rows |> Seq.choose read

let private keyedEntries key entries =
    entries |> Seq.map (fun entry -> key entry, entry) |> Map.ofSeq

let private viewEntries store =
    systemEntries store "views" SystemCatalog.View.tryRead
    |> keyedEntries (fun view -> tableKey view.Schema view.Name)

let private triggerEntries store =
    systemEntries store "triggers" SystemCatalog.Trigger.tryRead |> List.ofSeq

let private routineEntries store : Map<string, SystemCatalog.Routine.Entry> =
    systemEntries store "routines" SystemCatalog.Routine.tryRead
    |> keyedEntries (fun routine -> tableKey routine.Schema routine.Name)

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

let private isTemporary (catalog: Catalog) access =
    catalog
    |> Map.tryFind (normalize access.Database)
    |> Option.exists (Map.containsKey (normalizeTableName access.Table))

let private viewSources (view: SystemCatalog.View.Entry) =
    Parser.parseSavedViewDefinition view.Definition
    |> Result.map (fun definition -> directStatementAccesses view.Schema definition.Statement)

let private expandDependencies store temporaryCatalog accesses =
    // A physical table cannot share its name with a view. Most statements
    // only touch physical tables, so avoid scanning the view catalog for them.
    let views = lazy (viewEntries store)
    let triggers = lazy (triggerEntries store)
    let routines = lazy (routineEntries store)

    let rec expand visited access =
        let key = tableKey access.Database access.Table

        if Set.contains key visited || isTemporary temporaryCatalog access then
            [ access ]
        else
            let visited = Set.add key visited

            let viewDependencies =
                let view =
                    match tableSnapshot store access.Database access.Table with
                    | Ok _ -> None
                    | Error _ -> Map.tryFind key views.Value

                match view with
                | None -> []
                | Some view ->
                    match viewSources view with
                    | Error _ -> []
                    | Ok sources ->
                        sources
                        |> List.collect (fun dependency ->
                            { dependency with
                                ReferenceName = None
                                Mode = stronger access.Mode dependency.Mode }
                            |> expand visited)

            let triggerDependencies =
                if not (writesRows access.Mode) then
                    []
                else
                    triggers.Value
                    |> List.filter (fun trigger -> tableKey trigger.Schema trigger.Table = key)
                    |> List.collect (fun trigger ->
                        let options = SqlMode.parserOptionsFor trigger.SqlMode

                        match StoredProgram.parseTrigger options trigger.Body with
                        | Error _ -> []
                        | Ok statements ->
                            storedProgramStatements routines.Value trigger.Schema options statements
                            |> List.collect (directStatementAccesses trigger.Schema)
                            |> List.collect (fun dependency ->
                                expand visited { dependency with ReferenceName = None }))

            access :: (viewDependencies @ triggerDependencies)

    accesses |> List.collect (expand Set.empty) |> mergeAccesses

let private requiresOwnership temporaryCatalog access =
    not (access.Database.Equals("information_schema", StringComparison.OrdinalIgnoreCase))
    && not (isTemporary temporaryCatalog access)

let private statementDependencies store defaultDb statement =
    let direct = directStatementAccesses defaultDb statement

    let fallback =
        if directAccessesAreComplete statement then
            []
        else
            let represented = direct |> List.map (fun access -> tableKey access.Database access.Table) |> Set.ofList

            requiredAccesses store defaultDb statement
            |> List.filter (fun access -> not (Set.contains (tableKey access.Database access.Table) represented))

    direct @ fallback

/// Includes temporary tables while respecting their shadowing of stored views.
let dependenciesForStatement store temporaryCatalog defaultDb statement =
    statementDependencies store defaultDb statement
    |> mergeAccesses
    |> expandDependencies store temporaryCatalog

let accessesForStatement store temporaryCatalog defaultDb statement =
    let dependencies =
        // View DDL reserves its target name separately and opens only the new definition's sources.
        match statement with
        | CreateView view ->
            let schema, _ = splitName defaultDb view.Name
            match Parser.parseViewDefinition view.Definition with
            | Ok definition -> statementDependencies store schema definition.Statement
            | Error _ -> []
        | DropView _ -> []
        | _ -> statementDependencies store defaultDb statement

    dependencies
    |> List.filter (requiresOwnership temporaryCatalog)
    |> mergeAccesses
    |> expandDependencies store temporaryCatalog

// The caller still holds the statement's access while checking an execution failure.
let internal retainOpenedStatementAccess live snapshot owner accesses =
    if not (List.isEmpty accesses) then
        let views = lazy (viewEntries snapshot)
        let resolved (access: Access) =
            match tableSnapshot snapshot access.Database access.Table with
            | Ok _ -> true
            | Error _ -> Map.containsKey (tableKey access.Database access.Table) views.Value

        if List.forall resolved accesses then
            let manager = managerFor live
            lock manager.SyncRoot (fun () -> retainTransactionAccess owner manager (physicalAccesses accesses))

type internal ExplicitLockKind = LockList | FlushList

type private ExplicitOpenPhase = BeforeWaiting | AfterWaiting

let internal explicitDependenciesUnchanged kind store =
    match kind with
    | FlushList -> fun () -> true
    | LockList ->
        let snapshot name =
            match tableSnapshot store "mysql" name with
            | Ok table -> box table
            | Error _ -> null

        // These immutable catalog roots describe the dependencies expanded into a lock list.
        let definitions = [ for name in [ "views"; "triggers"; "routines" ] -> name, snapshot name ]
        fun () -> definitions |> List.forall (fun (name, previous) -> obj.ReferenceEquals(previous, snapshot name))

let private resolveExplicitAccesses kind phase store temporaryCatalog defaultDb locks =
    let views = lazy (viewEntries store)

    let rec viewTablesExist opened (view: SystemCatalog.View.Entry) =
        let key = tableKey view.Schema view.Name
        if Set.contains key opened then false
        else
            let opened = Set.add key opened
            match viewSources view with
            | Error _ -> false
            | Ok sources ->
                sources |> List.forall (fun source ->
                    if not (requiresOwnership Map.empty source) then true
                    else
                        match tableSnapshot store source.Database source.Table with
                        | Ok _ -> true
                        | Error _ ->
                            Map.tryFind (tableKey source.Database source.Table) views.Value
                            |> Option.exists (viewTablesExist opened))

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

            if kind = LockList && isTemporary temporaryCatalog current then
                resolve resolved rest
            else
                match tableSnapshot store database table with
                | Ok _ -> resolve (current :: resolved) rest
                | Error _ ->
                    match Map.tryFind (tableKey database table) views.Value with
                    | None -> Error(1146, sprintf "Table '%s.%s' doesn't exist" database table)
                    | Some _ when phase = AfterWaiting && kind = FlushList ->
                        Error(1347, sprintf "'%s.%s' is not BASE TABLE" database table)
                    | Some view when phase = AfterWaiting && not (viewTablesExist Set.empty view) ->
                        Error(SystemCatalog.View.invalidError database table)
                    | Some _ -> resolve (current :: resolved) rest

    match kind with
    | FlushList -> resolve [] locks
    | LockList -> resolve [] locks |> Result.map (expandDependencies store temporaryCatalog)

let explicitAccesses store temporaryCatalog defaultDb locks =
    resolveExplicitAccesses LockList BeforeWaiting store temporaryCatalog defaultDb locks

let flushAccesses store temporaryCatalog defaultDb locks =
    resolveExplicitAccesses FlushList BeforeWaiting store temporaryCatalog defaultDb locks

let internal reopenExplicitAccesses kind store temporaryCatalog defaultDb locks =
    resolveExplicitAccesses kind AfterWaiting store temporaryCatalog defaultDb locks
