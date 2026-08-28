module Fsdb.EventScheduler

open System
open System.Runtime.CompilerServices
open System.Threading
open Fsdb.Engine
open Fsdb.Sql
open Fsdb.Value

type private State =
    { Cancellation: CancellationTokenSource
      mutable References: int
      mutable Functions: Functions.Registry }

type private Lease(release: unit -> unit) =
    let mutable released = 0

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&released, 1) = 0 then
                release ()

let private states = ConditionalWeakTable<obj, State>()
let private stateLock = obj ()
let mutable private connectionId = -1_000_000

let private eventEntries (store: Storage.Store) =
    match Storage.scanList store "mysql" "events" with
    | Ok(_, rows) -> rows |> List.choose SystemCatalog.Event.tryRead
    | Error _ -> []

let private sameOccurrence (expected: SystemCatalog.Event.Entry) (due: DateTime) row =
    SystemCatalog.Event.tryRead row
    |> Option.exists (fun current ->
        SystemCatalog.Event.matches expected.Schema expected.Name current
        && current.Status = Event.Status.Enabled
        && current.Schedule = expected.Schedule
        && current.LastExecuted = expected.LastExecuted
        && SystemCatalog.Event.timing current = SystemCatalog.Event.timing expected
        && (current.LastExecuted |> Option.forall (fun previous -> due > previous)))

let private claim (store: Storage.Store) (entry: SystemCatalog.Event.Entry) due =
    let started = Functions.truncateToSecond DateTime.Now

    let update (row: Value[]) =
        let updated = Array.copy row
        updated.[10] <- VDateTime started
        Ok updated

    match
        Storage.updateRows
            store
            "mysql"
            "events"
            None
            (sameOccurrence entry due >> Ok)
            update
    with
    | Ok 1 -> Some started
    | Ok _ -> None
    | Error error ->
        let code, message = Storage.toMySqlError error
        Log.diagnostic "fsdb: event scheduler claim %s.%s: ERR %d %s" entry.Schema entry.Name code message
        None

let private claimedOccurrence (entry: SystemCatalog.Event.Entry) (started: DateTime) row =
    SystemCatalog.Event.tryRead row
    |> Option.exists (fun current ->
        SystemCatalog.Event.matches entry.Schema entry.Name current
        && current.Schedule = entry.Schedule
        && current.LastExecuted = Some started)

let private logCompletionError (entry: SystemCatalog.Event.Entry) action = function
    | Ok _ -> ()
    | Error error ->
        let code, message = Storage.toMySqlError error
        Log.diagnostic "fsdb: event scheduler %s %s.%s: ERR %d %s" action entry.Schema entry.Name code message

let private complete (store: Storage.Store) (entry: SystemCatalog.Event.Entry) (started: DateTime) final =
    if final then
        if entry.OnCompletion = "PRESERVE" then
            let disable (row: Value[]) =
                let updated = Array.copy row
                updated.[6] <- VString(Event.statusText Event.Status.Disabled)
                Ok updated

            Storage.updateRows store "mysql" "events" None (claimedOccurrence entry started >> Ok) disable
            |> logCompletionError entry "disable"
        else
            Storage.deleteRows store "mysql" "events" (claimedOccurrence entry started >> Ok)
            |> logCompletionError entry "remove"

let rec private resultError = function
    | Executor.Err(code, message) -> Some(code, message)
    | Executor.MultipleResults results -> results |> List.tryPick (fst >> resultError)
    | _ -> None

let private executionSession
    (store: Storage.Store)
    (functions: Functions.Registry)
    (entry: SystemCatalog.Event.Entry)
    (account: Auth.Account)
    =
    let session = Session.create (Interlocked.Decrement &connectionId) store
    let executionSettings: Storage.ExecutionSettings =
        { SqlModeText = entry.SqlMode
          SqlMode = SqlMode.settingsFor entry.SqlMode
          ConnectionCharset = entry.CharacterSetClient
          ConnectionCollation =
            entry.CollationConnection
            |> Collation.tryFind
            |> Option.defaultValue Collation.defaultCollation }

    Storage.setExecutionSettings session.Store executionSettings

    { session with
        User = account.Name
        AccountHost = account.Host
        LoginUser = "event_scheduler"
        ClientHost = "localhost"
        Database = Some entry.Schema
        CustomFunctions = functions
        Variables =
            session.Variables
            |> Map.add "sql_mode" (Some entry.SqlMode)
            |> Map.add "time_zone" (Some entry.TimeZone)
            |> Map.add "character_set_client" (Some entry.CharacterSetClient)
            |> Map.add "character_set_connection" (Some entry.CharacterSetClient)
            |> Map.add "collation_connection" (Some entry.CollationConnection)
            |> Map.add "collation_database" (Some entry.DatabaseCollation) }

let private execute
    (store: Storage.Store)
    (cancellation: CancellationToken)
    (functions: Functions.Registry)
    (entry: SystemCatalog.Event.Entry)
    (started: DateTime)
    final
    =
    async {
        try
            try
                match Auth.tryParseAccount entry.Definer with
                | None ->
                    Log.diagnostic "fsdb: event scheduler %s.%s: invalid definer %s" entry.Schema entry.Name entry.Definer
                | Some account when Auth.tryUserRowForAccount store account |> Option.isNone ->
                    Log.diagnostic
                        "fsdb: event scheduler %s.%s: definer '%s'@'%s' does not exist"
                        entry.Schema
                        entry.Name
                        account.Name
                        account.Host
                | Some account ->
                    let session = executionSession store functions entry account

                    try
                        Storage.queryCancellation.Value <- cancellation
                        let _, result = QueryHandler.executeEventBody session entry.Definition

                        match resultError result with
                        | Some(code, message) ->
                            Log.diagnostic "fsdb: event scheduler %s.%s: ERR %d %s" entry.Schema entry.Name code message
                        | None -> ()
                    finally
                        Storage.queryCancellation.Value <- CancellationToken.None
            with error ->
                Log.diagnostic "fsdb: event scheduler %s.%s: %s" entry.Schema entry.Name error.Message
        finally
            complete store entry started final
    }

let private enabled (store: Storage.Store) =
    Session.tryGlobalVariable store "event_scheduler"
    |> Option.flatten
    |> Option.exists (fun value -> value = "1" || value.Equals("ON", StringComparison.OrdinalIgnoreCase))

let private scan (store: Storage.Store) (state: State) =
    if enabled store then
        let now = Functions.truncateToSecond DateTime.Now
        let functions = lock stateLock (fun () -> state.Functions)

        for entry in eventEntries store do
            match entry.Status, SystemCatalog.Event.timing entry with
            | Event.Status.Enabled, Some timing ->
                match Event.dueOccurrence now entry.LastExecuted timing with
                | Some due ->
                    match claim store entry due with
                    | Some started ->
                        Async.Start(
                            execute
                                store
                                state.Cancellation.Token
                                functions
                                entry
                                started
                                (Event.isFinalOccurrence due timing)
                        )
                    | None -> ()
                | _ -> ()
            | _ -> ()

let rec private run (store: Storage.Store) (state: State) =
    async {
        try
            scan store state
        with error ->
            Log.diagnostic "fsdb: event scheduler: %s" error.Message

        do! Async.Sleep 100
        return! run store state
    }

let acquire (store: Storage.Store) (functions: Functions.Registry) : IDisposable =
    lock stateLock (fun () ->
        match states.TryGetValue store.Lock with
        | true, state ->
            state.References <- state.References + 1
            state.Functions <- functions
        | false, _ ->
            let state =
                { Cancellation = new CancellationTokenSource()
                  References = 1
                  Functions = functions }

            states.Add(store.Lock, state)
            Async.Start(run store state, state.Cancellation.Token)

        new Lease(fun () ->
            lock stateLock (fun () ->
                match states.TryGetValue store.Lock with
                | true, state when state.References = 1 ->
                    states.Remove store.Lock |> ignore
                    state.Cancellation.Cancel()
                | true, state -> state.References <- state.References - 1
                | false, _ -> ()))
        :> IDisposable)
