/// Per-connection session state: current database and session variables.
module Fsdb.Session

open System
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open Fsdb.Ast
open Fsdb.Protocol
open Fsdb.Storage
open Fsdb.Value

/// Session variable defaults good enough to satisfy mysql CLI / PDO on
/// connect. Grows as real clients ask for more `@@vars` / SHOW VARIABLES.
/// Variables backed by a `Limits` knob are deliberately absent — see
/// `liveDefaults`, which layers those on top so what the server reports can
/// never drift from what it enforces.
let defaultVariables: Map<string, string option> =
    Map.ofList
        [ "version", ServerVersion
          "version_comment", "fsdb"
          "version_compile_os", "osx"
          "sql_mode", "STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION"
          "character_set_client", "utf8mb4"
          "character_set_connection", "utf8mb4"
          "character_set_results", "utf8mb4"
          "character_set_server", "utf8mb4"
          "collation_connection", "utf8mb4_general_ci"
          "collation_server", "utf8mb4_general_ci"
          "collation_database", "utf8mb4_general_ci"
          "autocommit", "1"
          "system_time_zone", "UTC"
          "time_zone", "SYSTEM"
          "auto_increment_increment", "1"
          // mysqldump's preamble reads these three before ever `SET`-ing
          // anything (`SET @OLD_FOREIGN_KEY_CHECKS=@@FOREIGN_KEY_CHECKS`,
          // same for unique_checks/sql_notes) — real defaults, so a fresh
          // session already knows them instead of only picking them up
          // once something sets them.
          "foreign_key_checks", "1"
          "unique_checks", "1"
          "sql_notes", "1"
          "transaction_isolation", "REPEATABLE-READ"
          "lower_case_table_names", "0"
          "have_ssl", "DISABLED"
          "init_connect", ""
          "license", "GPL"
          "net_write_timeout", "60"
          "performance_schema", "0"
          "query_cache_size", "0"
          "query_cache_type", "OFF" ]
        |> Map.map (fun _ v -> Some v)

/// `defaultVariables` with every `Limits` knob layered over it — the base a
/// new session, `@@GLOBAL.x`, and SHOW GLOBAL VARIABLES all read from, so
/// `max_allowed_packet` and `wait_timeout` report whatever the server was
/// actually configured to enforce rather than a second copy of the number
/// that drifts from the first. Recomputed per call rather than cached: a
/// knob configured after this module's static initializer ran would
/// otherwise never become visible, and folding a handful of entries onto a
/// `Map` is not worth caching.
let private liveDefaults () : Map<string, string option> =
    Limits.variables () |> List.fold (fun m (name, value) -> Map.add name (Some value) m) defaultVariables

/// GLOBAL-scope system variable overrides (`SET GLOBAL x = y` / `SET
/// @@GLOBAL.x = y`), shared by every session on the same underlying
/// `Store` — keyed off `Store.Lock`, the one field every per-connection
/// `Store` clone (`create` below) shares by reference with the real store,
/// since `Storage.Store` has no such map of its own (adding one there
/// means widening every `{ store with ... }` clone site instead of one
/// lookup table here). `ConditionalWeakTable` needs no explicit
/// store-teardown hook and can never outlive the store it's keyed on.
let private globalVariablesByStore =
    ConditionalWeakTable<obj, ConcurrentDictionary<string, string option>>()

let private globalVariablesOf (store: Store) : ConcurrentDictionary<string, string option> =
    globalVariablesByStore.GetValue(store.Lock, (fun _ -> ConcurrentDictionary()))

/// Applies `SET GLOBAL name = value` (or `SET @@GLOBAL.name = value`):
/// visible to every session created on this store afterwards (`create`
/// seeds `Variables` from this map) and to `SELECT @@GLOBAL.name` on any
/// existing one, but never touches the issuing session's own `Variables` —
/// real MySQL's GLOBAL/SESSION split.
let setGlobalVariable (store: Store) (name: string) (value: string option) : unit =
    (globalVariablesOf store).[name.ToLowerInvariant()] <- value

/// Reads a GLOBAL-scope variable, falling back to the same compiled-in
/// default `create` seeds a new session from, so `SELECT @@GLOBAL.x` for a
/// variable nobody ever `SET GLOBAL`-ed answers with that default instead
/// of "unknown". `None` means genuinely unknown (`SELECT @@GLOBAL.bogus`);
/// `Some None` means known and currently NULL.
let tryGlobalVariable (store: Store) (name: string) : string option option =
    let name = name.ToLowerInvariant()

    match (globalVariablesOf store).TryGetValue name with
    | true, v -> Some v
    | false, _ -> liveDefaults () |> Map.tryFind name

/// The GLOBAL variable space as a whole — compiled-in defaults with every
/// `SET GLOBAL` override applied; `SHOW GLOBAL VARIABLES`' row source.
let globalVariablesSnapshot (store: Store) : Map<string, string option> =
    globalVariablesOf store
    |> Seq.fold (fun m (kv: System.Collections.Generic.KeyValuePair<string, string option>) -> Map.add kv.Key kv.Value m) (liveDefaults ())

/// A server-side prepared statement (COM_STMT_PREPARE / COM_STMT_EXECUTE).
/// `Ast` is the parsed statement for everything the grammar produces —
/// EXECUTE binds parameters as `Value`s into it (see
/// `QueryHandler.bindPlaceholders`) instead of splicing SQL literals back
/// into text and re-parsing. It's `None` for the text-probed forms the
/// grammar doesn't produce (SET/SHOW/transaction control), which still
/// substitute into `Sql` and re-probe.
///
/// `LastParamTypes` caches the (type id, unsigned) pairs from the most
/// recent EXECUTE that actually sent them — COM_STMT_EXECUTE's
/// new-params-bound-flag lets a client omit them on a later EXECUTE and
/// reuse what it sent before.
type PreparedStmt =
    { Ast: Statement option
      Sql: string
      ParamCount: int
      LastParamTypes: (byte * bool) list option }

/// One open transaction. `Snapshot` is a private `Store` — its own
/// `Catalog`, its own lock — seeded from the shared store's catalog when the
/// transaction executes its first real database statement; every statement
/// inside the transaction reads/writes this
/// snapshot instead of the shared store, so concurrent connections see
/// nothing from it until COMMIT merges `Snapshot.Catalog` back into the
/// shared store's (see `QueryHandler.commitSession`). That's real
/// repeatable-read isolation for the reading side. `BaseCatalog` is that
/// same seed, kept alongside `Snapshot` untouched by any write this
/// transaction makes — COMMIT diffs `Snapshot.Catalog` against it,
/// table-by-table, to tell "this transaction wrote table X" apart from
/// "table X just happened to be in the snapshot", so a concurrent write to
/// an *untouched* table survives the commit instead of being silently
/// overwritten by a stale copy. `GateLease` serializes writing transactions
/// from their first write through COMMIT/ROLLBACK; read-only transactions
/// retain repeatable-read snapshots without taking a gate.
type Transaction =
    { Snapshot: Store
      BaseCatalog: Catalog
      /// Every database's write gate this transaction currently holds,
      /// keyed by database name — not just `session.Database`: a qualified
      /// `INSERT/UPDATE INTO otherdb.t` needs `otherdb`'s own gate too, or a
      /// concurrent writer to `otherdb` can race this transaction's merge
      /// back at COMMIT (see `Storage.mergeDatabaseSlot`'s doc). Acquired
      /// lazily, one database at a time as each statement in the
      /// transaction first names it, then all held until COMMIT/ROLLBACK.
      /// Empty immediately after BEGIN lets multiple connections begin
      /// concurrently and matches InnoDB's default behavior of establishing
      /// a consistent snapshot on the first statement rather than the BEGIN
      /// packet.
      GateLease: Map<string, IDisposable>
      /// Set by the first database statement, which is the one that seeds
      /// `Snapshot`/`BaseCatalog`. Not derivable from `GateLease.IsEmpty`
      /// any more: a transaction that has only run reads holds no gate at
      /// all and must still seed exactly once, or every read would
      /// re-snapshot and repeatable read would be a lie.
      Seeded: bool
      /// Each savepoint's establishment order (see `NextSavepointSeq`), its
      /// catalog, and how many events `Snapshot.PendingEvents` had buffered
      /// at that point — `ROLLBACK TO SAVEPOINT` truncates the buffer back
      /// to that length too, so a physical WAL never sees events for writes
      /// the savepoint rollback just undid. The order lets `ROLLBACK TO
      /// SAVEPOINT`/`RELEASE SAVEPOINT` drop every savepoint established
      /// *after* the named one, matching real MySQL — a plain `Map` alone
      /// has no notion of "after", since re-`SAVEPOINT`-ing an existing name
      /// moves it, not creates a second entry.
      Savepoints: Map<string, int * Catalog * int>
      /// Monotonically increasing counter, one `SAVEPOINT` = one tick —
      /// never reused even if the savepoint it tagged is later dropped, so
      /// two savepoints established back-to-back with no write between them
      /// (same buffered-event count) still compare correctly by this alone.
      NextSavepointSeq: int }

type Session =
    { ConnectionId: int
      /// The account name the client authenticated as at handshake —
      /// `CURRENT_USER()`/`USER()`/`SHOW GRANTS` and privilege checks read
      /// it. `"root"` for a session built directly (every test).
      /// ponytail: name only, no host part — every account is `'name'@'%'`
      /// and the connecting host renders as `localhost`; add real host
      /// matching if remote-host account rules are ever needed.
      User: string
      Database: string option
      /// Real, known system variables. `string option` per value (not just
      /// `string`) distinguishes a variable MySQL accepts NULL for (e.g.
      /// `SET character_set_results = NULL`) from one holding an ordinary
      /// string — the map lookup's own `None` for a key that isn't in the
      /// map at all still means "unknown variable" (1193);
      /// only a *present* key can hold `None`. See `UserVariables` below
      /// for the analogous convention on user-defined variables.
      Variables: Map<string, string option>
      /// `SET @name = ...` user-defined variables — unlike `Variables`
      /// (real, known system variables), any `@name` is legal in real
      /// MySQL, so there's no default set and no "unknown variable" error
      /// path for these. `string option` per value (not just `string`)
      /// distinguishes "SET to NULL" from the map lookup itself already
      /// telling `None` apart from `Some` — both mean "reads back as
      /// NULL", so callers reading a value back collapse the two with
      /// `Option.flatten` rather than needing to branch on which.
      UserVariables: Map<string, string option>
      /// The single shared catalog every connection reads/writes through —
      /// `Session` itself stays an immutable per-connection value; `Store`
      /// is the one mutable boundary (see `Storage.Store`). Always the
      /// shared store, even inside a transaction — use `currentStore` to
      /// get the store statements should actually run against.
      Store: Store
      /// The OK packet's `last_insert_id` (what `PDO::lastInsertId()`/
      /// `mysql_insert_id()` read) for this session's most recent statement:
      /// the first AUTO_INCREMENT id it generated, or else the last one it
      /// explicitly supplied. 0 until the first INSERT that assigns either.
      /// See `LastGeneratedId` for the narrower value the SQL function
      /// `LAST_INSERT_ID()` reads — the two diverge for an INSERT that
      /// supplies its own id instead of letting AUTO_INCREMENT generate one.
      LastInsertId: int64
      /// The AUTO_INCREMENT id actually *generated* (never explicitly
      /// supplied) by this session's most recent statement that generated
      /// one, for the SQL function `LAST_INSERT_ID()`. 0 until the first
      /// such INSERT; unlike `LastInsertId`, a statement that generates
      /// none — including one that supplies its own id — leaves this
      /// unchanged rather than resetting it, matching real MySQL.
      LastGeneratedId: int64
      /// Per-column MySQL wire types for the most recent statement's
      /// `ResultSet`, if any — `[]` for anything else (an `Affected`/`Err`
      /// result, or a `ResultSet` this session's dispatch path didn't
      /// bother typing, e.g. `SHOW ...`/session-variable probes). `Server`
      /// reads this right after `QueryHandler.handle` to build the
      /// resultset's column-definition packets; threaded through `Session`
      /// like `LastInsertId` instead of widening `handle`'s own return
      /// type, since dozens of tests destructure `QueryHandler.handle`'s
      /// plain `Session * QueryResult` pair directly.
      LastResultColumnMetadata: ColumnMetadata list
      /// `Some` between BEGIN/START TRANSACTION and COMMIT/ROLLBACK.
      Tx: Transaction option
      /// Prepared statements registered by this connection's COM_STMT_PREPARE
      /// calls, by statement id. Threaded through the connection loop like
      /// the rest of `Session` rather than a mutable dict at the `Server`
      /// boundary — every other piece of per-connection state (database,
      /// variables, transaction) already lives here as plain immutable data,
      /// and a prepared statement is exactly that: state scoped to one
      /// connection, not shared across them.
      Statements: Map<int, PreparedStmt>
      /// The next id COM_STMT_PREPARE will assign.
      NextStmtId: int
      /// Bytes buffered by COM_STMT_SEND_LONG_DATA, keyed by (statement id,
      /// param index), appended to on each call and consumed (then cleared)
      /// by the next COM_STMT_EXECUTE or COM_STMT_RESET for that statement.
      LongData: Map<int * int, byte[]>
      /// Total bytes held in `LongData` for constant-time limit checks.
      LongDataBytes: int64
      /// (statement id, param index) pairs whose COM_STMT_SEND_LONG_DATA
      /// chunks together exceeded `Limits.maxAllowedPacket` — the
      /// send-long-data command itself never gets a reply (per protocol),
      /// so the overflow surfaces as ER_NET_PACKET_TOO_LARGE (1153) on the
      /// next COM_STMT_EXECUTE for that statement instead of silently
      /// truncating the parameter's data. Cleared alongside `LongData` by
      /// that EXECUTE, or by COM_STMT_RESET/COM_STMT_CLOSE.
      LongDataOverflow: Set<int * int>
      /// Custom functions registered on the embedding `Db` this session's
      /// connection was accepted on (see `Fsdb.Db.registerScalar`/
      /// `registerAggregate`) — empty for a session built directly (every
      /// test). `QueryHandler.registryFor` layers these over the built-ins,
      /// under session-bound overrides like `DATABASE()`.
      CustomFunctions: Fsdb.Functions.Registry
      /// Effective capabilities negotiated at handshake (`Server`'s
      /// `resp.Capabilities &&& ServerCapabilities`) — 0 for a session built
      /// directly (every test that doesn't set it). Only `ClientFoundRows`
      /// is read back out of this today, to pick matched- vs changed-row
      /// counts for UPDATE, multi-table UPDATE, and INSERT ... ON DUPLICATE
      /// KEY UPDATE (see `Executor.execute`'s `foundRows` param).
      Capabilities: uint32 }

let create (connectionId: int) (store: Store) : Session =
    // Overlays every `SET GLOBAL`-ed override onto the compiled-in
    // defaults — a fresh session inherits whatever GLOBAL state is live on
    // this store, matching real MySQL's "new sessions pick up the current
    // global value" semantics (see `tryGlobalVariable`/`setGlobalVariable`).
    let variables =
        (globalVariablesOf store) |> Seq.fold (fun acc (KeyValue(k, v)) -> Map.add k v acc) (liveDefaults ())

    { ConnectionId = connectionId
      User = "root"
      Database = None
      Variables = variables
      UserVariables = Map.empty
      // A per-connection clone of `store`, not `store` itself. `Databases`,
      // `TransactionGates`, `Lock`, and `OnCommit` are reference-typed, so the
      // clone still shares the one real catalog and all of its cross-connection
      // synchronization (the shared `Lock` still serializes WAL appends) — but
      // `StrictMode`/`ForeignKeyChecks`/`ConnectionCollation` get their own
      // independent mutable cells. Those three are re-derived from this
      // session's own variables before every statement
      // (`QueryHandler.executeParsed`); without this clone they'd be the literal
      // same fields every other connection's re-derivation also flips, and
      // nothing serializes those session-specific assignments across connections.
      Store = { store with StrictMode = store.StrictMode }
      LastInsertId = 0L
      LastGeneratedId = 0L
      LastResultColumnMetadata = []
      Tx = None
      Statements = Map.empty
      NextStmtId = 1
      LongData = Map.empty
      LongDataBytes = 0L
      LongDataOverflow = Set.empty
      CustomFunctions = Fsdb.Functions.empty
      Capabilities = 0u }

/// The catalog store all statements on this session currently execute
/// against: the shared store outside a transaction, or the transaction's
/// private snapshot inside one (see `Transaction`).
let currentStore (session: Session) : Store =
    session.Tx |> Option.map (fun tx -> tx.Snapshot) |> Option.defaultValue session.Store
