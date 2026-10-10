/// The embedding facade — the one door into fsdb for host code, SQLite-
/// style: create a `Db`, register custom functions, listen.
module Fsdb.Db

open System.Net
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Threading
open System.Threading.Tasks
open System.Threading.Channels
open Fsdb.Functions
open Fsdb.Value

/// An embeddable fsdb instance: its storage plus whatever custom functions
/// have been registered so far. Immutable, like every other piece of fsdb's
/// state — `registerScalar`/`registerAggregate` return a new `Db` so calls
/// chain with `|>`. `DataDir` is `None` for the default pure in-memory mode
/// (`withDataDir` sets it) — kept on the record mainly so callers can see
/// whether durability is on, `listen`/`registerScalar` don't need it.
type Db =
    { Store: Storage.Store
      Functions: Registry
      DataDir: string option
      Transport: ServerOptions.Settings }

/// A fresh, empty database: no data, no custom functions, no durability.
let create () : Db =
    { Store = Storage.create ()
      Functions = Functions.empty
      DataDir = None
      Transport = ServerOptions.defaults }

let private configureFullTextStopwords enabled (store: Storage.Store) =
    store.FullTextStopwordsEnabled <- enabled
    Session.setGlobalVariable store "innodb_ft_enable_stopword" (Some(if enabled then "ON" else "OFF"))

let private configureFullTextStopwordTables (tables: StorageOptions.StopwordTables) store =
    Session.setGlobalVariable store "innodb_ft_user_stopword_table" tables.UserTable
    Session.setGlobalVariable store "innodb_ft_server_stopword_table" tables.ServerTable

/// Opts into durability under `dataDir`. Loads whatever
/// state is already there (a snapshot plus any WAL entries after it, or
/// nothing for a fresh directory) and subscribes the result to keep writing
/// every future commit to its WAL, so this replaces `db.Store` rather than
/// reusing the one `create` made. Chains like every other `Db` builder:
/// `Db.create () |> Db.withDataDir "/var/lib/fsdb" |> Db.listen ...`.
let withDataDir (dataDir: string) (db: Db) : Db =
    if db.Store.OnCommit.Count > 0 then
        invalidOp "Configure durable storage before registering commit subscribers."
    let store = Persistence.load dataDir
    store.VirtualTables <- db.Store.VirtualTables
    Storage.configureNgramTokenSize db.Store.NgramTokenSize store
    Storage.configureFullTextWordLengths db.Store.FullTextWordLengths store
    let stopwordsEnabled = Session.tryGlobalVariable db.Store "innodb_ft_enable_stopword" <> Some(Some "OFF")
    configureFullTextStopwords stopwordsEnabled store
    configureFullTextStopwordTables
        { UserTable = Session.tryGlobalVariable db.Store "innodb_ft_user_stopword_table" |> Option.flatten
          ServerTable = Session.tryGlobalVariable db.Store "innodb_ft_server_stopword_table" |> Option.flatten } store
    Session.tryGlobalVariable db.Store "ft_query_expansion_limit"
    |> Option.iter (Session.setGlobalVariable store "ft_query_expansion_limit")
    Persistence.attach dataDir store
    { db with Store = store; DataDir = Some dataDir }

/// Reports the startup expansion limit; InnoDB-style search does not use the MyISAM seed cap.
let withFullTextQueryExpansionLimit (limit: int) (db: Db) : Db =
    let limit = StorageOptions.normalizeQueryExpansionLimit (int64 limit)
    Session.setGlobalVariable db.Store "ft_query_expansion_limit" (Some(string limit))
    db

/// Selects word lengths before opening sessions; existing postings retain their original bounds.
let withFullTextWordLengths (lengths: StorageOptions.WordLengths) (db: Db) : Db =
    let normalized: StorageOptions.WordLengths =
        { Minimum = StorageOptions.normalizeMinimumWordLength (int64 lengths.Minimum)
          Maximum = StorageOptions.normalizeMaximumWordLength (int64 lengths.Maximum) }
    Storage.configureFullTextWordLengths normalized db.Store
    db

/// Selects the ngram size (clamped to 1–10) before opening sessions or serving traffic.
/// Existing postings retain their original tokenizer until the index is rebuilt.
let withNgramTokenSize (size: int) (db: Db) : Db =
    Storage.configureNgramTokenSize (StorageOptions.normalizeNgramTokenSize (int64 size)) db.Store
    db

/// Selects the initial full-text stopword setting before opening sessions.
/// Existing indexes retain their captured policy until rebuilt.
let withFullTextStopwords (enabled: bool) (db: Db) : Db =
    configureFullTextStopwords enabled db.Store
    db

/// Seeds custom stopword sources before opening sessions; tables are resolved when indexes are built.
let withFullTextStopwordTables (tables: StorageOptions.StopwordTables) (db: Db) : Db =
    configureFullTextStopwordTables tables db.Store
    db

/// Routes fsdb's diagnostic output (connection drops, WAL replay warnings,
/// server-side query errors) through `f` instead of stderr. Returns `db`
/// unchanged — the sink is process-global (`Log`), not per-`Db` state — so
/// this chains like every other builder purely for a consistent call style.
let withLogger (f: string -> unit) (db: Db) : Db =
    Log.useSink f
    db

/// Enables TLS with a certificate whose private key has already been loaded by the host.
let withTlsCertificate (certificate: X509Certificate2) (db: Db) : Db =
    { db with Transport = db.Transport |> ServerOptions.withCertificate certificate }

/// Trusts client certificates issued by `certificateAuthority` for account `REQUIRE X509` checks.
let withClientCertificateAuthority (certificateAuthority: X509Certificate2) (db: Db) : Db =
    { db with
        Transport = db.Transport |> ServerOptions.withClientCertificateAuthority certificateAuthority }

/// Refuses plaintext MySQL sessions; a TLS certificate is required before serving.
let requireSecureTransport (db: Db) : Db =
    { db with Transport = db.Transport |> ServerOptions.requireSecureTransport }

/// Uses a host-supplied RSA private key for plaintext full authentication.
let withAuthenticationRsaKey plugin (privateKey: RSA) (db: Db) : Db =
    { db with
        Transport = db.Transport |> ServerOptions.withAuthenticationRsaKey plugin privateKey }

/// Restricts server-side file statements to `directory`.
let withSecureFileDirectory (directory: string) (db: Db) : Db =
    { db with Transport = db.Transport |> ServerOptions.withSecureFileDirectory directory }

/// Allows server-side file statements to use any path visible to the host process.
let allowUnrestrictedServerFiles (db: Db) : Db =
    { db with Transport = db.Transport |> ServerOptions.allowUnrestrictedServerFiles }

/// Registers a scalar function under `name`, e.g.
/// `db |> Db.registerScalar "slugify" (function ...)`. Free to override a
/// built-in of the same name — `QueryHandler.registryFor` layers custom
/// functions over the built-ins, under session-bound ones like `DATABASE()`.
let registerScalar (name: string) (fn: Scalar) (db: Db) : Db =
    { db with Functions = registerScalar name fn db.Functions }

/// Registers an aggregate function under `name`, e.g.
/// `db |> Db.registerAggregate "median" (fun values -> ...)`.
let registerAggregate (name: string) (fn: Aggregate) (db: Db) : Db =
    { db with Functions = registerAggregate name fn db.Functions }

/// Registers a rich (`QueryContext`-aware) scalar function — the shape a
/// network-calling extension needs, e.g.
/// `db |> Db.registerFunction (ScalarFunction.create "LLM_EMBED" embed |> ScalarFunction.effectful)`.
/// `registerScalar` stays the sugar for context-free functions.
let registerFunction (fn: ScalarFunction) (db: Db) : Db =
    { db with Functions = registerExtension fn db.Functions }

/// Registers a read-only virtual table into the `fsdb` schema —
/// `db |> Db.registerTable (VirtualTable.create "models" [ VirtualTable.text "name" ] listModels)`
/// makes `SELECT * FROM fsdb.models` work. The registry is an overlay on
/// the real `fsdb` database (also the default one): a registered name wins
/// over a same-named real table, other real tables resolve unchanged, and
/// re-registering a name replaces it (names are case-insensitive). Register
/// before serving traffic. Tables configured before `withDataDir` are retained.
let registerTable (table: VirtualTable) (db: Db) : Db =
    db.Store.VirtualTables <- Map.add (table.Name.ToLowerInvariant()) table db.Store.VirtualTables
    db

/// Subscribes `handler` to every committed write. Delivery is synchronous and
/// ordered; handlers must stay fast and must not write back into the store.
/// Subscribe after `withDataDir`, which replaces the store.
let onCommit (handler: Storage.CommitEvent -> unit) (db: Db) : Db =
    lock db.Store.CommitLock (fun () -> db.Store.OnCommit.Add handler)
    db

/// An ordered, non-blocking commit queue. Dispose it to unregister from the store.
/// The queue is unbounded; consumers should drain it continuously.
type CommitSubscription internal (store: Storage.Store) =
    let channel = Channel.CreateUnbounded<Storage.CommitEvent>(UnboundedChannelOptions(SingleReader = false, SingleWriter = false))
    let enqueue event = channel.Writer.TryWrite event |> ignore
    let mutable disposed = false

    do lock store.CommitLock (fun () -> store.OnCommit.Add enqueue)

    member _.Reader : ChannelReader<Storage.CommitEvent> = channel.Reader

    interface System.IDisposable with
        member _.Dispose() =
            lock store.CommitLock (fun () ->
                if not disposed then
                    disposed <- true
                    store.OnCommit.Remove enqueue |> ignore
                    channel.Writer.TryComplete() |> ignore)

/// Subscribes to committed changes without running host code in the commit path.
let subscribeCommits (db: Db) : CommitSubscription = new CommitSubscription(db.Store)

/// Distinguishes in-process `connect` sessions from each other (for
/// `CONNECTION_ID()`); negative so they can never collide with the
/// wire-protocol server's own positive connection ids.
let private connectionCounter = ref 0

/// An in-process connection — no socket, no wire protocol, just
/// `QueryHandler.handle` over its own private session, so per-connection
/// state (`USE`, variables, open transaction) persists across `Query` calls
/// exactly as it would on a real connection.
type Connection internal (db: Db) =
    let gate = obj ()
    let mutable disposed = false
    let mutable session =
        { (Session.create (System.Threading.Interlocked.Decrement connectionCounter) db.Store
           |> Session.withServerOptions db.Transport) with
            CustomFunctions = db.Functions }

    member private _.Run(action: Session.Session -> Session.Session * QueryHandler.QueryResult) =
        lock gate (fun () ->
            if disposed then raise (System.ObjectDisposedException(nameof Connection))
            let updated, result = action session
            session <- updated
            result)

    member this.Query(sql: string) : QueryHandler.QueryResult =
        this.Run(fun session ->
            match QueryHandler.tryPrepareLoad session sql with
            | Error result -> session, result
            | Ok(Some load) when not load.Local -> QueryHandler.executeServerLoad session load
            | Ok(Some _) ->
                session,
                Executor.Err(
                    3948,
                    "Loading local data is disabled; this must be enabled on both the client and server sides"
                )
            | Ok None -> QueryHandler.handle session sql)

    /// Executes positional `?` parameters through the same prepared-statement binder as the wire protocol.
    member this.Execute(sql: string, parameters: Value list) : QueryHandler.QueryResult =
        this.Run(fun session ->
            let preparedSession, prepared = QueryHandler.prepareStatementWithDiagnostics session sql
            match prepared with
            | Error(code, message) -> preparedSession, Executor.Err(code, message)
            | Ok(ast, count) ->
                let statement = QueryHandler.createPreparedStatement preparedSession sql ast count
                QueryHandler.executePrepared preparedSession statement parameters)

    /// Returns engine values for SELECT statements, preserving SQL NULL as `None`.
    member this.QueryValues(sql: string, parameters: Value list) : Result<string list * Value option list list, SqlState.Error> =
        let result = this.Execute(sql, parameters)
        let detachValue =
            function
            | VBytes bytes -> VBytes(Array.copy bytes)
            | VBinaryLiteral bytes -> VBinaryLiteral(Array.copy bytes)
            | VEncodedString(charset, bytes) -> VEncodedString(charset, Array.copy bytes)
            | value -> value
        match Executor.typedRows result with
        | Some(columns, rows) ->
            Ok(columns, rows |> List.map (Array.toList >> List.map (function VNull -> None | value -> Some(detachValue value))))
        | None ->
            match Executor.errorInfo result with
            | Some error -> Error error
            | None -> Error(SqlState.create 1105 "Statement did not return typed rows")

    /// Runs a query on a worker thread and propagates cancellation to the engine and extensions.
    member this.QueryAsync(sql: string, cancellation: CancellationToken) : Task<QueryHandler.QueryResult> =
        Task.Run((fun () ->
            DynamicScope.withThreadValue Storage.queryCancellation cancellation (fun () -> this.Query sql)), cancellation)

    /// Async positional prepared execution with the same cancellation behavior as `QueryAsync`.
    member this.ExecuteAsync(sql: string, parameters: Value list, cancellation: CancellationToken) : Task<QueryHandler.QueryResult> =
        Task.Run((fun () ->
            DynamicScope.withThreadValue Storage.queryCancellation cancellation (fun () -> this.Execute(sql, parameters))), cancellation)

    /// Async typed query with positional parameters.
    member this.QueryValuesAsync(sql: string, parameters: Value list, cancellation: CancellationToken) : Task<Result<string list * Value option list list, SqlState.Error>> =
        Task.Run((fun () ->
            DynamicScope.withThreadValue Storage.queryCancellation cancellation (fun () -> this.QueryValues(sql, parameters))), cancellation)

    interface System.IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    disposed <- true
                    QueryHandler.closeSession session)

/// Opens an in-process connection to `db` — the sanctioned way for a host
/// to run SQL against its own embedded instance without a socket.
let connect (db: Db) : Connection = new Connection(db)

/// A server started by `serve`. `Address` and `Port` describe the bound
/// listener; `Stop` stops accepting new connections. `IDisposable` lets a
/// host bind it with `use`; stopping twice is a no-op.
type RunningServer =
    { Address: IPAddress
      Port: int
      Stop: unit -> unit }

    interface System.IDisposable with
        member this.Dispose() = this.Stop()

/// Starts the MySQL wire-protocol server on `address:port` and serves
/// connections until the process ends — `db`'s custom functions are
/// available to every statement any connection runs. Kept alongside
/// `serve` for compat: use `serve` when the host needs the bound port or a
/// way to stop.
let listen (address: IPAddress) (port: int) (db: Db) : Async<unit> =
    let listener = Server.startListening address port
    Server.serveWithOptions db.Transport listener db.Store db.Functions

/// Like `listen`, but starts serving on a background async and hands back
/// a stoppable `RunningServer` — pass port 0 for an OS-assigned port and
/// read the actual listener endpoint from `Address` and `Port`.
let serve (address: IPAddress) (port: int) (db: Db) : RunningServer =
    let listener = Server.startListening address port
    Async.Start(Server.serveWithOptions db.Transport listener db.Store db.Functions)

    let endpoint = listener.LocalEndpoint :?> IPEndPoint

    { Address = endpoint.Address
      Port = endpoint.Port
      Stop = fun () -> listener.Stop() }

/// Owns in-process connections and subscriptions, then checkpoints durable storage.
/// Do not share the underlying `Db` with a wire listener or other connections
/// after taking ownership; disposal detaches its durable commit sink.
[<Sealed>]
type DatabaseHost internal (db: Db) =
    let gate = obj ()
    let resources = ResizeArray<System.IDisposable>()
    let mutable disposed = false

    member _.Connect() =
        lock gate (fun () ->
            if disposed then raise (System.ObjectDisposedException(nameof DatabaseHost))
            let connection = connect db
            resources.Add connection
            connection)

    member _.SubscribeCommits() =
        lock gate (fun () ->
            if disposed then raise (System.ObjectDisposedException(nameof DatabaseHost))
            let subscription = subscribeCommits db
            resources.Add subscription
            subscription)

    interface System.IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                if not disposed then
                    disposed <- true
                    for resource in Seq.rev resources do resource.Dispose()
                    resources.Clear()
                    Persistence.detach db.Store)

/// Takes exclusive ownership after configuration; use the returned host in a `use` binding.
let own (db: Db) : DatabaseHost = new DatabaseHost(db)
