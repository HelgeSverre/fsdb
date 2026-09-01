/// The embedding facade — the one door into fsdb for host code, SQLite-
/// style: create a `Db`, register custom functions, listen.
module Fsdb.Db

open System.Net
open System.Security.Cryptography.X509Certificates
open Fsdb.Functions

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

/// Opts into durability under `dataDir`. Loads whatever
/// state is already there (a snapshot plus any WAL entries after it, or
/// nothing for a fresh directory) and subscribes the result to keep writing
/// every future commit to its WAL, so this replaces `db.Store` rather than
/// reusing the one `create` made. Chains like every other `Db` builder:
/// `Db.create () |> Db.withDataDir "/var/lib/fsdb" |> Db.listen ...`.
let withDataDir (dataDir: string) (db: Db) : Db =
    let store = Persistence.load dataDir
    Persistence.attach dataDir store
    { db with Store = store; DataDir = Some dataDir }

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
/// before serving traffic, and after `withDataDir` — that builder replaces
/// `db.Store`, dropping tables registered before it.
let registerTable (table: VirtualTable) (db: Db) : Db =
    db.Store.VirtualTables <- Map.add (table.Name.ToLowerInvariant()) table db.Store.VirtualTables
    db

/// Subscribes `handler` to every committed write. Delivery is synchronous and
/// ordered; handlers must stay fast and must not write back into the store.
/// Subscribe after `withDataDir`, which replaces the store.
let onCommit (handler: Storage.CommitEvent -> unit) (db: Db) : Db =
    lock db.Store.CommitLock (fun () -> db.Store.OnCommit.Add handler)
    db

/// Distinguishes in-process `connect` sessions from each other (for
/// `CONNECTION_ID()`); negative so they can never collide with the
/// wire-protocol server's own positive connection ids.
let private connectionCounter = ref 0

/// An in-process connection — no socket, no wire protocol, just
/// `QueryHandler.handle` over its own private session, so per-connection
/// state (`USE`, variables, open transaction) persists across `Query` calls
/// exactly as it would on a real connection.
type Connection internal (db: Db) =
    let mutable session =
        { Session.create (System.Threading.Interlocked.Decrement connectionCounter) db.Store with
            CustomFunctions = db.Functions }

    member _.Query(sql: string) : QueryHandler.QueryResult =
        let updated, result = QueryHandler.handle session sql
        session <- updated
        result

/// Opens an in-process connection to `db` — the sanctioned way for a host
/// to run SQL against its own embedded instance without a socket.
let connect (db: Db) : Connection = Connection(db)

/// A server started by `serve`: `Port` is the actual bound port (matters
/// when `serve` was given port 0 for an OS-assigned one), `Stop` stops
/// accepting new connections. `IDisposable` so `use running = ...` works;
/// stopping twice is a no-op.
type RunningServer =
    { Port: int
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
/// read it back off `Port`.
let serve (address: IPAddress) (port: int) (db: Db) : RunningServer =
    let listener = Server.startListening address port
    Async.Start(Server.serveWithOptions db.Transport listener db.Store db.Functions)

    { Port = Server.port listener
      Stop = fun () -> listener.Stop() }
