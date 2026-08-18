/// In-memory multi-database catalog: snapshot reads, serialized writes.
/// A `Catalog` is an immutable `Map`, so every read is a lock-free snapshot
/// and every write swaps in a brand new `Catalog` under a lock.
module Fsdb.Storage

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Collections.Immutable
open System.Globalization
open System.Threading
open Fsdb.Ast
open Fsdb.Value

/// Raised by `enterTransactionGate` when a database's write gate doesn't
/// clear within `innodb_lock_wait_timeout`'s default — caught by
/// `QueryHandler.handle`'s catch-all and reported as MySQL error 1205
/// rather than hanging the connection forever.
exception LockWaitTimeout of dbName: string

/// Storage-layer failures, mapped to MySQL error codes by `toMySqlError`.
/// `ExpressionError` carries an already-formed MySQL (code, message) pair
/// through from `Executor`'s row-level expression evaluation (e.g. an
/// `UPDATE ... SET` right-hand side) — `Storage` doesn't know that
/// vocabulary, but `updateRows`'s `updater` can now fail per row instead of
/// silently writing a `VNull`, and its failure needs to travel the same
/// `Result<_, StorageError>` path every other write error does.
type StorageError =
    | NoSuchDatabase of name: string
    | DatabaseExists of name: string
    | TableExists of name: string
    | NoSuchTable of name: string
    | UnknownColumn of name: string
    | ColumnCountMismatch of expected: int * actual: int
    | NotNullViolation of column: string
    | InvalidValueForColumn of column: string * value: string
    /// An ENUM (or SET) column rejected a value — MySQL's own 1265
    /// "Data truncated" (SQLSTATE 01000), distinct from the 1366 incorrect-
    /// value error other column types raise in strict mode.
    | DataTruncatedForColumn of column: string
    | ExpressionError of code: int * message: string
    /// A unique index (or the primary key, reported as `"PRIMARY"`) already
    /// has a row with this value.
    | DuplicateKey of keyName: string * value: string
    /// `DELETE`/parent-row `UPDATE` blocked by a child row through a
    /// `RESTRICT`/`NO ACTION` (or unspecified) `ON DELETE` foreign key.
    | ForeignKeyRestrict of fkName: string
    /// `INSERT`/`UPDATE` of a child row whose foreign key columns don't
    /// match any row in the referenced table.
    | ForeignKeyParentMissing of fkName: string

/// MySQL error code + message for a `StorageError`, ready for the wire
/// protocol's ERR packet.
let toMySqlError (err: StorageError) : int * string =
    match err with
    | NoSuchDatabase name -> 1049, sprintf "Unknown database '%s'" name
    | DatabaseExists name -> 1007, sprintf "Can't create database '%s'; database exists" name
    | TableExists name -> 1050, sprintf "Table '%s' already exists" name
    | NoSuchTable name -> 1146, sprintf "Table '%s' doesn't exist" name
    | UnknownColumn name -> 1054, sprintf "Unknown column '%s' in field list" name
    | ColumnCountMismatch(expected, actual) ->
        1136, sprintf "Column count doesn't match value count at row 1 (expected %d, got %d)" expected actual
    | NotNullViolation column -> 1048, sprintf "Column '%s' cannot be null" column
    | InvalidValueForColumn(column, value) -> 1366, sprintf "Incorrect value: '%s' for column '%s'" value column
    | DataTruncatedForColumn column -> 1265, sprintf "Data truncated for column '%s' at row 1" column
    | ExpressionError(code, message) -> code, message
    | DuplicateKey(keyName, value) -> 1062, sprintf "Duplicate entry '%s' for key '%s'" value keyName
    | ForeignKeyRestrict fkName ->
        1451, sprintf "Cannot delete or update a parent row: a foreign key constraint fails (`%s`)" fkName
    | ForeignKeyParentMissing fkName ->
        1452, sprintf "Cannot add or update a child row: a foreign key constraint fails (`%s`)" fkName

/// A table's rows, newest last. `OriginalName` keeps the as-created casing
/// for information_schema, even though the catalog keys tables by their
/// lowercased name. `Indexes`' `UNIQUE` entries (plus the primary key) are
/// enforced via `UniqueIndex` on every `INSERT`/`UPDATE`/`upsertRows`/
/// `DELETE`; `ForeignKeys` are enforced on `INSERT`/`UPDATE`/`DELETE` (see
/// `checkFkParents`/`cascadeDelete`, also `UniqueIndex`-accelerated on the
/// parent side), gated by `Store.ForeignKeyChecks`. Non-`UNIQUE` plain
/// indexes remain metadata only, and every WHERE that doesn't reduce to a
/// single PK/UNIQUE equality (see `tryUniqueLookup`) is still a full table
/// scan.
type Table =
    { OriginalName: string
      Columns: ColumnDef list
      /// `ImmutableArray`, not `Value[] list`: every write path below
      /// (`insertCore`, `updateRows`, `upsertRows`, `deleteRows`) needs to
      /// hand out a new immutable snapshot of a table's rows without
      /// mutating whatever `Database`/`Catalog` value still references the
      /// old one (`Table` shares that discipline with `UniqueIndex`, see
      /// below) — but a `list` can only grow/shrink by an O(n) rebuild of
      /// every cons cell, while `ImmutableArray.Add`/`.SetItem` are a single
      /// `Array.Copy` over reference-sized slots, no per-row heap traffic.
      /// Insertion order is the scan order (index `0` is the oldest row);
      /// deletion compacts (see `deleteRows`), so `RowsArray.Length` is
      /// always the table's real row count, never a tombstoned/padded one.
      /// External readers that want a plain list use the `Rows` member.
      RowsArray: ImmutableArray<Value[]>
      NextAutoId: int64
      Indexes: IndexDef list
      ForeignKeys: ForeignKeyDef list
      /// The table's own declared `[DEFAULT] CHARSET`/`COLLATE` options
      /// (`None` = server default) — rendered by `SHOW CREATE TABLE`, and
      /// distinct from the baked-in per-column defaults.
      TableCharset: string option
      TableCollation: string option
      /// Hash index over every PRIMARY KEY / UNIQUE `Indexes` entry, kept in
      /// sync with `Rows` by every write path below (`insertCore`,
      /// `updateRows`, `upsertRows`, `deleteRows`'s `cascadeDeleteVisited`)
      /// instead of rebuilt from a full scan on every call — that rebuild is
      /// exactly the O(table size) tax this index exists to remove from point
      /// SELECT, unique-collision checks, and FK parent-existence checks
      /// (see `uniqueKeyGroups`/`encodeConstraintKey`/`tryUniqueLookup`).
      /// Outer map keyed by the unique group's name (`"PRIMARY"` or the
      /// index's own name, matching `uniqueKeyGroups`); inner map from
      /// `encodeConstraintKey`'s collation-correct key to that row's
      /// *position in `Rows`*, not a copy of the row itself — a stale
      /// position would be a correctness bug (pointing past an UPDATE that
      /// moved the row, or at a DELETE-compacted slot that now holds a
      /// different row), so every write path that changes `Rows`'s length or
      /// reorders it (`insertCore`'s append, `deleteRows`'s compaction)
      /// rekeys every index entry whose position shifted, not just the
      /// touched row's own. `Map`, not a `Dictionary`, on purpose: `Table` is
      /// itself a value swapped in and out of `Catalog`'s snapshots (see
      /// `Catalog`'s doc), so a transaction's private snapshot or a
      /// concurrent reader's in-flight `scan` needs this index frozen at
      /// exactly the version it read — `Map.add`/`Map.remove` share
      /// structure with whatever earlier version still holds a reference,
      /// the same property every other piece of `Catalog` state already
      /// leans on, where a mutable `Dictionary` would need an explicit
      /// clone on every write to keep it. A row with a NULL anywhere in the
      /// group has no entry at all — MySQL never treats a NULL unique
      /// column as a collision, so it isn't indexed (`encodeConstraintKey`
      /// already returns `None` for one). Structural, not carried over the
      /// wire: absent from the WAL/snapshot encoding, rebuilt instead by
      /// `reindexTable` wherever `Rows` is written outside these checked
      /// paths (`Persistence`'s replay/snapshot-load — see its doc).
      UniqueIndex: Map<string, Map<string, int>> }

    /// `RowsArray` as a plain list, in scan order — a fresh O(row count)
    /// copy on every access, for external validators/tools that walk a
    /// snapshot with `List.*`; every hot path reads `RowsArray` directly.
    member this.Rows: Value[] list = List.ofSeq this.RowsArray

/// Table names are case-insensitive, keyed by their lowercased form.
type Database = Map<string, Table>

/// Database names, as given, to a `Database`. Only ever materialized as a
/// whole `Map` for callers that genuinely need a point-in-time view spanning
/// every database at once (`Store.Catalog`, see its doc) — the live mutable
/// state backing it is sharded per database (`Store.Databases`), not one
/// value of this type.
type Catalog = Map<string, Database>

/// One committed change to the catalog, for a physical WAL. Data changes
/// (`RowsInserted`/`RowsUpdated`/`RowsDeleted`) carry the actual `Value`s
/// written, never SQL text — `INSERT ... VALUES (NOW(), UUID())` replayed as
/// SQL would produce different values the second time, so replay must be
/// "write exactly this row" rather than "re-run this expression". DDL
/// (`SchemaChanged`) is deterministic by nature (CREATE/ALTER/DROP/TRUNCATE/
/// RENAME never depend on when they run), so it's logged logically as the
/// parsed `Statement` instead. `TransactionCommitted` wraps every event a
/// multi-statement transaction buffered, emitted once at COMMIT — see
/// `beginTransactionSnapshot`/`commitTransactionEvents`.
type CommitEvent =
    | RowsInserted of db: string * table: string * rows: Value[] list
    | RowsUpdated of db: string * table: string * changes: (Value[] * Value[]) list
    | RowsDeleted of db: string * table: string * rows: Value[] list
    | SchemaChanged of db: string * Statement
    | TransactionCommitted of CommitEvent list

let defaultDatabase = "fsdb"

let private stripBackticks (s: string) = s.Trim().Trim('`')

/// Splits a `` `db`.`table` `` (or bare `table`) name into its two parts,
/// defaulting the database to `defaultDb` — the one place every qualified
/// name resolves through, whether it came from the real parser
/// (`Parser.qualifiedTableName`, via `Executor.execute`) or a text-probed
/// `SHOW ...`/`DESCRIBE` statement (`QueryHandler.dispatch`). Strips
/// backticks per component, *after* splitting on `.`, not before —
/// `` `shop`.`users` ``.Trim('`') first leaves `` shop`.`users `` (the
/// backticks straddling the dot survive), which then splits wrong.
let splitQualified (defaultDb: string) (name: string) : string * string =
    match name.Trim().Split('.') with
    | [| db; tbl |] -> stripBackticks db, stripBackticks tbl
    | _ -> defaultDb, stripBackticks name

/// ponytail: one coarse transaction/write gate *per database* (see
/// `TransactionGates`) rather than row/table locks. This preserves committed
/// state under contention but serializes every write within one database,
/// transactional or not; replace it with row versions or sharded async
/// locks when parallel write throughput matters. A transaction acquires the
/// gate of every database its statements actually touch
/// (`QueryHandler.targetDatabases`), not just its session default.
///
/// `ForeignKeyChecks` gates every FK enforcement in this module (cascading
/// deletes, `RESTRICT`, parent-existence checks on insert/update) — the
/// storage-level mirror of MySQL's session `FOREIGN_KEY_CHECKS` variable.
/// Per-session in practice: `Session.create` gives every connection its own
/// clone of the master `Store` (sharing `Databases`/`Lock`/`TransactionGates`
/// but with private `ForeignKeyChecks`/`StrictMode`/`ConnectionCollation`
/// cells), so one connection's `SET` can't flip another's. `QueryHandler`'s
/// `SET FOREIGN_KEY_CHECKS = 0|1` probe (and Laravel's
/// `Schema::disableForeignKeyConstraints`, which sends exactly that) calls
/// `setForeignKeyChecks` on that clone.
type Store =
    { /// Every database's table map, each independently guarded by its own
      /// `Database ref` cell (locked via that same cell — see `withDatabase`)
      /// — sharded so one database's writes never contend with another's.
      /// A `ConcurrentDictionary` so `CREATE`/`DROP DATABASE` (adding/
      /// removing a whole entry) is itself a lock-free, atomic dictionary
      /// operation that never contends with an unrelated database's row
      /// writer either (`createDatabase`/`dropDatabase`, `ensureDatabase`).
      /// Sharding means database A's writers never see database B's writes at
      /// all, let alone race them — a single store-wide `Catalog` reference
      /// would be one `Interlocked.CompareExchange` target where every write
      /// to *any* database invalidates every other writer's compare-exchange
      /// and forces a full write retry under contention. Not exposed beyond
      /// this module (no other module needs to
      /// touch it directly — everything goes through `Store.Catalog`,
      /// `scan`, `tryUniqueLookup`, or the write ops below).
      Databases: ConcurrentDictionary<string, Database ref>
      mutable ForeignKeyChecks: bool
      /// The storage-level mirror of MySQL's session `sql_mode`
      /// STRICT_TRANS_TABLES/STRICT_ALL_TABLES: `true` rejects a value that
      /// doesn't fit its column's type with error 1366 (`coerceValue`'s
      /// default); `false` coerces to `coerceValue`'s non-strict fallback
      /// instead — 0 for a numeric column, NULL for a nullable temporal one
      /// (still a hard 1366 on a NOT NULL temporal column; see
      /// `coerceValue`'s doc for why). Lives on this session's own private
      /// `Store` clone (see the `ForeignKeyChecks` note above) and is
      /// re-derived from the *current* session's own `sql_mode` by
      /// `QueryHandler.executeParsed` before every statement, so it neither
      /// leaks between connections nor runs stale inside a transaction — see
      /// the note there and on `beginTransactionSnapshot`. `QueryHandler`'s
      /// `SET SESSION sql_mode =
      /// ...` probe (and Laravel's `'strict' => false` connection config,
      /// which sends `SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'`) calls
      /// `setStrictMode` directly too, so `SELECT @@sql_mode` right after a
      /// `SET` on the same connection reflects it immediately.
      mutable StrictMode: bool
      /// The storage-level mirror of MySQL's session
      /// `collation_connection` — the collation literal-vs-literal string
      /// comparisons (and literal ORDER BY/LIKE operands) resolve under.
      /// On this session's own private `Store` clone like `StrictMode`,
      /// re-derived from the *current* session's own variable by
      /// `QueryHandler` before every statement, so it never leaks between
      /// connections. Column comparisons are unaffected — a column's own
      /// `COLLATE` always wins.
      mutable ConnectionCollation: Collation.Collation
      /// Fires once per committed write, under `Lock`, right after the
      /// catalog swap that made it visible — `None` (the default) means no
      /// subscriber, so every write path's event-construction work still
      /// happens (it's cheap: the row values were already computed for the
      /// write itself) but delivery is a no-op. Set directly (e.g.
      /// `store.OnCommit <- Some handler`) before serving traffic; nothing
      /// here mutates it mid-flight.
      mutable OnCommit: (CommitEvent -> unit) option
      /// `Some` only for a transaction's private snapshot `Store` (see
      /// `beginTransactionSnapshot`): while set, every write path appends its
      /// event here instead of calling `OnCommit`, so nothing is visible
      /// outside the transaction until `commitTransactionEvents` flushes the
      /// buffer as one `TransactionCommitted` on the real store — a ROLLBACK
      /// just discards the snapshot, buffer and all.
      mutable PendingEvents: ResizeArray<CommitEvent> option
      /// Serializes the lifetime of active transactions and individual
      /// autocommit mutations, one `SemaphoreSlim` per database (created
      /// lazily by `enterTransactionGate`) rather than one store-wide, so
      /// unrelated databases never block on each other's open transactions.
      /// A transaction acquires its database's gate on its
      /// first real database statement (not BEGIN) and releases it at
      /// COMMIT/ROLLBACK, which prevents the snapshot merger from replacing
      /// a concurrent writer's changes to the same table. SemaphoreSlim is
      /// intentionally used instead of Monitor because a connection's
      /// BEGIN, statements, and COMMIT may resume on different threads.
      TransactionGates: ConcurrentDictionary<string, SemaphoreSlim>
      /// Serializes `OnCommit`'s dispatch only (see `emit`) — every other
      /// write locks only its own database's `Database ref` cell (see
      /// `withDatabase`), so writers to different databases never wait on
      /// each other here either. `OnCommit`'s one subscriber
      /// (`Persistence.attach`'s WAL appender) isn't safe to call
      /// concurrently from two databases' writer threads at once — it
      /// appends to one shared file and tracks rotation state in a plain
      /// `ref` — so its calls stay ordered by this lock, same as
      /// `Persistence.snapshotNow`'s own use of it.
      Lock: obj }

    /// Assembles a point-in-time `Catalog` (whole-map) view across every
    /// database's independent slot — an O(number of databases) allocation,
    /// not O(rows), paid only by callers that genuinely need a snapshot
    /// spanning every database at once: a transaction's BEGIN/savepoint
    /// base, `information_schema`, and WAL/snapshot persistence. A
    /// live-traffic hot path (`scan`, `tryUniqueLookup`, `databaseExists`)
    /// reads `Databases` directly instead, to avoid paying this rebuild once
    /// per row/query.
    ///
    /// ponytail: this reads each database's slot one at a time while other
    /// databases' writers keep running, so it isn't a single atomic instant
    /// across every database — a concurrent cross-database observer could in
    /// principle see database A's tables as of slightly after database B's.
    /// The one place this could ever surface is a single `information_schema`
    /// `SELECT` spanning multiple databases mid another database's unrelated
    /// commit; each database's own transactional/snapshot correctness (what
    /// every torture lane actually checks) is untouched — this relaxation
    /// only affects a *global* view stitched from independent databases, not
    /// any one database's own consistency. Upgrade to a store-wide epoch
    /// counter if a caller ever needs `information_schema` to be
    /// linearizable across databases too.
    member this.Catalog
        with get () : Catalog = this.Databases |> Seq.map (fun kv -> kv.Key, kv.Value.Value) |> Map.ofSeq
        /// Wholesale-replaces every database's slot from `catalog` in one go
        /// — correct only when nothing else can be reading/writing
        /// `Databases` concurrently: `Persistence.load`'s snapshot/WAL replay
        /// (before the server starts accepting connections), a
        /// transaction's own private snapshot store resetting itself to an
        /// earlier savepoint (`QueryHandler.rollbackToSavepoint` — that
        /// snapshot is only ever touched by its one owning connection, never
        /// shared), and test code building a store's contents directly.
        /// Never used on the *live, shared* store; see `mergeCatalogInto`/
        /// `bumpAutoIncrementsInto` for the concurrency-safe, per-database
        /// merges real commits use instead.
        and set (catalog: Catalog) =
            this.Databases.Clear()

            for KeyValue(dbName, db) in catalog do
                this.Databases.[dbName] <- ref db

/// As `store.Catalog <- catalog`, spelled as a function for callers that
/// prefer piping/partial application over the property-set syntax.
let setCatalog (store: Store) (catalog: Catalog) : unit = store.Catalog <- catalog

let create () : Store =
    let databases = ConcurrentDictionary<string, Database ref>()
    databases.[defaultDatabase] <- ref Map.empty

    { Databases = databases
      ForeignKeyChecks = true
      StrictMode = true
      ConnectionCollation = Collation.defaultCollation
      OnCommit = None
      PendingEvents = None
      TransactionGates = ConcurrentDictionary<string, SemaphoreSlim>()
      Lock = obj () }

type private TransactionGateLease(gate: SemaphoreSlim) =
    let mutable released = 0

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&released, 1) = 0 then
                gate.Release() |> ignore

/// How long a connection waits for `dbName`'s write gate before giving up —
/// matches `innodb_lock_wait_timeout`'s MySQL default (50s) rather than
/// waiting forever. `enterTransactionGate` takes this as a parameter rather
/// than hardcoding it so a test can pass a short one instead of a real
/// 50-second wait to exercise the timeout path.
let defaultLockWaitTimeout = TimeSpan.FromSeconds 50.0

/// Acquires `dbName`'s coarse transaction/write gate (see `Store`'s doc for
/// why it's per-database, not store-wide). The returned lease is idempotent
/// so normal COMMIT/ROLLBACK and connection cleanup can both dispose it
/// safely without over-releasing the semaphore. Raises `LockWaitTimeout`
/// instead of blocking forever if the gate doesn't clear within `timeout` —
/// a stuck transaction elsewhere degrades the waiter to a retryable MySQL
/// error (`QueryHandler.handle` maps it to 1205) rather than wedging the
/// connection.
let enterTransactionGate (store: Store) (dbName: string) (timeout: TimeSpan) : IDisposable =
    let gate = store.TransactionGates.GetOrAdd(dbName, (fun _ -> new SemaphoreSlim(1, 1)))

    if not (gate.Wait timeout) then
        raise (LockWaitTimeout dbName)

    new TransactionGateLease(gate) :> IDisposable

/// Delivers `event` (if any): buffers it if `store` is a transaction
/// snapshot (`PendingEvents` — private to the transaction's own thread, so
/// no locking needed), otherwise hands it to `OnCommit` under `store.Lock`
/// if someone's listening (see the doc on `Lock`). Always called after the
/// catalog swap that made the event's write visible has already landed.
let private emit (store: Store) (event: CommitEvent option) : unit =
    match event with
    | None -> ()
    | Some e ->
        match store.PendingEvents with
        | Some buffer -> buffer.Add e
        | None -> store.OnCommit |> Option.iter (fun f -> lock store.Lock (fun () -> f e))

/// A private per-transaction catalog snapshot seeded from `store`'s current
/// catalog (see `Session.Transaction`) — writes against it stay invisible
/// to `store` until `commitTransactionEvents` flushes its buffered events
/// and the caller merges its catalog back in; a ROLLBACK just drops it.
/// Only buffers events at all when `store` actually has a subscriber —
/// otherwise every write during the transaction skips straight past
/// `emit`'s buffer check, same zero-overhead-when-nobody's-listening
/// property as the non-transactional path.
let beginTransactionSnapshot (store: Store) : Store =
    // A fresh `Database ref` cell per database, each wrapping the *value*
    // `store`'s cell holds right now — sharing the immutable `Database` Map
    // (cheap, structural sharing) but never the mutable cell itself, so a
    // write against the snapshot's copy can never be seen by (or race) a
    // concurrent write against the live store's own cell for the same
    // database.
    let databases = ConcurrentDictionary<string, Database ref>()

    for KeyValue(dbName, slot) in store.Databases do
        databases.[dbName] <- ref slot.Value

    { Databases = databases
      ForeignKeyChecks = store.ForeignKeyChecks
      // Not seeded from `store.StrictMode` — `QueryHandler.executeStatement`
      // re-derives it from the session's own `sql_mode` before every
      // statement (see the note there), so whatever this starts as is
      // always overwritten before a transaction's first real statement
      // runs.
      StrictMode = true
      ConnectionCollation = store.ConnectionCollation
      OnCommit = None
      // Allocate a buffer whenever `store` itself would ever deliver an
      // event — either it has a real `OnCommit` subscriber, or `store` is
      // *itself* a transaction snapshot with its own `PendingEvents` already
      // buffering. The latter is exactly `Executor`'s multi-table
      // `UPDATE`/`DELETE` path: it opens a private snapshot of the
      // *transaction's own* snapshot store to isolate one statement's
      // writes, then commits that snapshot back via
      // `commitTransactionEvents`. A snapshot's `OnCommit` is always `None`
      // (only the real store has one), so `PendingEvents.IsSome` is the only
      // signal that events built here must be buffered rather than dropped
      // by `emit`'s no-buffer-no-subscriber branch.
      PendingEvents = if store.OnCommit.IsSome || store.PendingEvents.IsSome then Some(ResizeArray()) else None
      TransactionGates = store.TransactionGates
      Lock = obj () }

/// Flushes a committed transaction's buffered events onto `store`, if it
/// buffered any — a no-op for an empty or subscriber-less snapshot. Call
/// after merging `snapshot`'s catalog back in (see
/// `QueryHandler.commitSession`); there's no rollback counterpart, since
/// discarding `snapshot` discards its buffer too.
///
/// `store` itself being a transaction snapshot (its own `PendingEvents` is
/// `Some` — the nested-statement case `beginTransactionSnapshot`'s doc
/// describes) appends `snapshot`'s raw events onto `store`'s own buffer
/// flat, rather than wrapping them in a nested `TransactionCommitted` —
/// `Persistence.applyEvent` replays a `TransactionCommitted`'s member events
/// one level deep, so a `TransactionCommitted [ TransactionCommitted [...] ]`
/// would need it to recurse (or would silently drop the inner layer) for no
/// reason: every event this transaction's real COMMIT eventually flushes to
/// the WAL is already flat by construction, wrapped exactly once at the
/// real top-level commit. Only when `store` is the real, live store (no
/// `PendingEvents` of its own) does this wrap `snapshot`'s events in one
/// `TransactionCommitted`.
let commitTransactionEvents (store: Store) (snapshot: Store) : unit =
    match snapshot.PendingEvents with
    | Some buffer when buffer.Count > 0 ->
        match store.PendingEvents with
        | Some targetBuffer -> targetBuffer.AddRange buffer
        | None -> emit store (Some(TransactionCommitted(List.ofSeq buffer)))
    | _ -> ()

/// `SET FOREIGN_KEY_CHECKS = 0|1` — wired from `QueryHandler`'s `SET` probe
/// (see the note on `Store.ForeignKeyChecks`).
let setForeignKeyChecks (store: Store) (enabled: bool) : unit =
    lock store.Lock (fun () -> store.ForeignKeyChecks <- enabled)

/// `SET SESSION sql_mode = ...` — wired from `QueryHandler`'s `SET` probe
/// (see the note on `Store.StrictMode`). `strict` is whether the new mode
/// still contains STRICT_TRANS_TABLES/STRICT_ALL_TABLES.
let setConnectionCollation (store: Store) (collation: Collation.Collation) : unit = store.ConnectionCollation <- collation

let setStrictMode (store: Store) (strict: bool) : unit =
    lock store.Lock (fun () -> store.StrictMode <- strict)

/// Table names are keyed case-insensitively by their lowercased form —
/// public because `Persistence`'s WAL replay looks tables up in `Catalog`
/// directly (bypassing this module's checked write paths on purpose; see
/// the note on `Persistence.applyEvent`), so it needs the same key.
let normalizeTableName (name: string) = name.ToLowerInvariant()

/// `CREATE DATABASE name` — unlike `ensureDatabase` (silent no-op used by
/// handshake auto-create/first-write auto-vivify), this errors 1007 if it
/// already exists; `Executor` swallows that error for `IF NOT EXISTS`, same
/// pattern as `createTable`. `ConcurrentDictionary.TryAdd` is itself atomic,
/// so two concurrent `CREATE DATABASE`s racing for the same name always
/// leave exactly one winner and one honest `DatabaseExists` — no CAS/retry
/// loop needed here at all, and (unlike the whole-catalog CAS this replaced)
/// touching one database's dictionary entry can never be invalidated by an
/// unrelated database's concurrent row writer.
let createDatabase (store: Store) (dbName: string) : Result<unit, StorageError> =
    if store.Databases.TryAdd(dbName, ref Map.empty) then
        emit store (Some(SchemaChanged(dbName, CreateDatabase(dbName, false))))
        Ok()
    else
        Error(DatabaseExists dbName)

/// `DROP DATABASE name` — same atomicity argument as `createDatabase`, via
/// `ConcurrentDictionary.TryRemove`.
let dropDatabase (store: Store) (dbName: string) : Result<unit, StorageError> =
    match store.Databases.TryRemove dbName with
    | true, _ ->
        emit store (Some(SchemaChanged(dbName, DropDatabase(dbName, false))))
        Ok()
    | false, _ -> Error(NoSuchDatabase dbName)

/// Applies `f` to `dbName`'s current table map and swaps the result into
/// that database's own `Database ref` cell, under that cell's own lock (see
/// below). `QueryHandler` already serializes every write to the *same*
/// database through its per-database `TransactionGates`
/// (`enterTransactionGate`) before it ever reaches here, so in the common
/// case this lock is uncontended — but this module doesn't *rely* on that
/// gate alone for correctness (see `mergeDatabaseSlot`'s doc for why a
/// second, storage-level guarantee matters). Crucially, a completely
/// disjoint slot per database (see `Store.Databases`'s doc) means a write to
/// database A never even touches, let alone blocks on, database B's slot —
/// the cross-database contention a single store-wide `Catalog` CAS used to
/// cause is gone by construction.
let private withDatabase
    (store: Store)
    (dbName: string)
    (f: Database -> Result<Database * 'a, StorageError>)
    : Result<'a, StorageError> =
    match store.Databases.TryGetValue dbName with
    | false, _ -> Error(NoSuchDatabase dbName)
    | true, slot ->
        // `slot` (the `Database ref` cell itself) doubles as this
        // database's own mutual-exclusion object — `lock` (a re-entrant
        // .NET `Monitor`) rather than `Interlocked.CompareExchange`: two
        // writers to *different* databases still never block on each other
        // (they lock different `slot`s), but two writers to the *same*
        // database are unconditionally mutually exclusive here, in
        // `Storage` itself, rather than relying entirely on `QueryHandler`'s
        // `TransactionGates` to have already kept them apart — a second,
        // independent line of defense against exactly the lost-update hazard
        // `mergeDatabaseSlot`'s doc describes, should a caller ever reach
        // this module without holding the right gate.
        lock slot (fun () ->
            match f slot.Value with
            | Error e -> Error e
            | Ok(db', result) ->
                slot.Value <- db'
                Ok result)

/// Every database name appearing as a key in `m` — the shared set-of-keys
/// step `mergeDatabaseSlot`/`mergeCatalogInto`/`bumpAutoIncrementsInto`'s
/// per-database merges all need.
let private keysOf (m: Map<string, 'a>) : Set<string> = m |> Map.toList |> List.map fst |> Set.ofList

/// Three-way merges one isolated unit of work's private before/after view of
/// a *single* database (`baseDb`/`batchDb`) into that database's own live
/// `slot`, table-by-table: a table the batch actually wrote (its snapshot
/// copy differs from `baseDb`, the database as it stood when the batch
/// started) wins outright; one it dropped is removed; one it never touched
/// is left exactly as the slot's *current* live value already has it, so a
/// concurrent writer's change to that table survives instead of being
/// silently discarded by a stale copy.
///
/// This "unconditionally take the batch's own final table" rule for a table
/// the batch *did* touch is only safe if nothing else could have written
/// that same table between `baseDb` being captured and this merge running —
/// otherwise the batch's own precomputed `batchT` (built from its own stale
/// `baseDb`) would silently clobber whatever that other writer landed,
/// a genuine lost update, not just a wasted retry. `QueryHandler`'s
/// per-database `TransactionGates` is *supposed* to guarantee that
/// exclusivity, but this module doesn't take that on faith: `lock slot`
/// below makes this database's own cell a real mutual-exclusion point
/// inside `Storage` itself, shared with `withDatabase`'s plain writes — so
/// even if a caller ever reached this module without holding the right
/// gate, two writers to the *same* database still can't interleave here,
/// while writers to *different* databases (locking different cells) remain
/// fully independent.
let private mergeDatabaseSlot (slot: Database ref) (baseDb: Database) (batchDb: Database) : unit =
    // Same `lock slot` as `withDatabase` — this database's own mutual
    // exclusion, independent of whatever gate the caller believes it's
    // already holding (see `withDatabase`'s doc).
    lock slot (fun () ->
        let liveDb = slot.Value
        let tableKeys = Set.unionMany [ keysOf baseDb; keysOf batchDb; keysOf liveDb ]

        slot.Value <-
            tableKeys
            |> Set.fold
                (fun tacc tableName ->
                    match Map.tryFind tableName baseDb, Map.tryFind tableName batchDb with
                    | Some _, None -> Map.remove tableName tacc // dropped by the batch
                    | None, Some t -> Map.add tableName t tacc // created by the batch
                    | Some baseT, Some batchT when baseT <> batchT -> Map.add tableName batchT tacc // modified by the batch
                    | _ -> tacc // untouched by the batch — keep whatever's live
                )
                liveDb)

/// Merges `batchCatalog` (built from `baseCatalog` by some isolated unit of
/// work — a committing transaction, or a multi-table statement's private
/// snapshot store) into `store`'s live databases, one at a time via
/// `mergeDatabaseSlot`/`Databases.TryRemove`/`GetOrAdd` — only for the
/// database(s) the batch actually saw (almost always exactly one; see
/// `Store.TransactionGates`'s doc on cross-database transactions), never
/// iterating or contending on any database the batch never touched. Shared
/// by `QueryHandler`'s transaction commit and `Executor`'s multi-table
/// `UPDATE`, both of which run a private snapshot store before merging its
/// catalog back.
/// A commit's merge (`baseCatalog`/`batchCatalog` -> live) is a
/// *three-way* merge keyed off `baseCatalog` — a snapshot from whenever this
/// batch began, which can be arbitrarily stale by the time it commits. Two
/// merges racing the same table each decide "mine changed it, take mine"
/// purely from their own (stale) base/batch pair, with no way to notice the
/// other one *also* changed that table in between — the loser's whole-table
/// `batchT` silently clobbers the winner's already-landed row, a genuine
/// lost update, not just a wasted retry. Per-database `TransactionGates` are
/// supposed to make this impossible for the common case (one transaction at
/// a time per database), but this module has no way to prove every caller
/// actually holds the right gate for every database a batch's snapshot might
/// mention (a batch's `baseCatalog`/`batchCatalog` are always *whole-store*
/// snapshots, not scoped to the database(s) it meant to touch) — so the merge
/// step itself, not just the gate, needs to be the backstop. `store.Lock`
/// (otherwise only used to serialize `OnCommit` dispatch) makes the merge a
/// true store-wide critical section: two commits can still run their actual
/// row/table writes fully in parallel across databases (`withDatabase`
/// itself takes no lock at all), but they queue for this one relatively
/// cheap step (an O(touched tables) map merge, not O(rows)) rather than ever
/// racing each other's three-way decision.
let mergeCatalogInto (store: Store) (baseCatalog: Catalog) (batchCatalog: Catalog) : unit =
    let dbKeys = Set.union (keysOf baseCatalog) (keysOf batchCatalog)

    for dbName in dbKeys do
        match Map.tryFind dbName baseCatalog, Map.tryFind dbName batchCatalog with
        | Some _, None -> store.Databases.TryRemove dbName |> ignore // the batch dropped this database
        | None, Some batchDb -> store.Databases.[dbName] <- ref batchDb // the batch created this database
        | None, None -> () // unreachable: dbKeys only ever holds keys from baseCatalog/batchCatalog
        // `baseCatalog`/`batchCatalog` are always *whole-store* snapshots
        // (`Store.Catalog`, taken at BEGIN and at commit time), so `dbKeys`
        // includes every database in the store, not just the one(s) this
        // batch actually wrote — most of the time `baseDb = batchDb` here,
        // meaning the batch never touched this database at all. Skipping
        // those isn't just an optimization: a no-op merge attempt would
        // still take that database's lock, which is exactly the kind of
        // unrelated-database contention this whole sharded design exists to
        // avoid paying on every commit.
        | Some baseDb, Some batchDb when baseDb = batchDb -> ()
        | Some baseDb, Some batchDb ->
            // Existed both before and after the batch, and the batch really
            // did write here. `GetOrAdd` rather than a plain lookup: if a
            // concurrent writer dropped this database entirely while the
            // batch was running, merge back into a fresh empty slot instead
            // of silently losing the batch's own writes — the same fallback
            // the old whole-catalog merge's `Option.defaultValue Map.empty`
            // gave a database missing from the live catalog. `mergeDatabaseSlot`
            // itself takes this database's own lock (see its doc) — two
            // commits to *different* databases still merge fully in
            // parallel here.
            let slot = store.Databases.GetOrAdd(dbName, (fun _ -> ref Map.empty))
            mergeDatabaseSlot slot baseDb batchDb

/// Bumps the live catalog's AUTO_INCREMENT counters up to a discarded
/// transaction's snapshot wherever it ran one ahead — MySQL never rolls
/// back a burned id (see `QueryHandler.rollbackSession`'s doc) — leaving
/// everything else (rows, schema) exactly as the live catalog has it. Under
/// `store.Lock`, same as `mergeCatalogInto` and for the same reason (a true
/// store-wide critical section for the relatively rare merge/bump step, not
/// the per-row write hot path) — `ROLLBACK` is rarer still than `COMMIT`, so
/// this is even cheaper to serialize.
let bumpAutoIncrementsInto (store: Store) (snapshotCatalog: Catalog) : unit =
    // `snapshotCatalog` is always a *whole-store* snapshot (`Store.Catalog`,
    // taken at BEGIN), so it holds every database, not just the one(s) the
    // rolled-back transaction actually wrote to. Only ever touch a
    // database's slot when some table in it genuinely needs bumping
    // (`mergedDb` differs from what's live, checked by reference — the
    // `Map.fold` below only calls `Map.add` on an actual bump) — touching an
    // untouched database's slot (and its lock) for no reason is exactly the
    // unrelated-database work sharding `Store.Databases` exists to avoid.
    let bumpSlot (slot: Database ref) (snapshotDb: Database) =
        // Same per-database `lock slot` as `withDatabase`/`mergeDatabaseSlot`.
        lock slot (fun () ->
            let liveDb = slot.Value

            let mergedDb =
                snapshotDb
                |> Map.fold
                    (fun acc tableName (snapshotTable: Table) ->
                        match Map.tryFind tableName acc with
                        | Some(liveTable: Table) when snapshotTable.NextAutoId > liveTable.NextAutoId ->
                            Map.add tableName { liveTable with NextAutoId = snapshotTable.NextAutoId } acc
                        | _ -> acc)
                    liveDb

            if not (obj.ReferenceEquals(mergedDb, liveDb)) then
                slot.Value <- mergedDb)

    for KeyValue(dbName, snapshotDb) in snapshotCatalog do
        match store.Databases.TryGetValue dbName with
        | false, _ -> () // dropped since the snapshot was taken — nothing to bump
        | true, slot -> bumpSlot slot snapshotDb

/// Pure catalog-level version of `bumpAutoIncrementsInto`'s "never roll back
/// a burned AUTO_INCREMENT id" rule — returns `target` with every table's
/// `NextAutoId` raised to `current`'s wherever `current` ran it further
/// ahead (a table missing from `target` is left out, same as
/// `bumpAutoIncrementsInto`'s "dropped since the snapshot" case). Used by
/// `QueryHandler.rollbackToSavepoint`, which discards a transaction
/// snapshot's catalog wholesale back to an earlier savepoint (`setCatalog`)
/// but must still keep whatever id a statement *after* that savepoint
/// burned — MySQL never rolls back a burned id, savepoint rollback
/// included, same as a full `ROLLBACK` (see `bumpAutoIncrementsInto`'s doc).
let bumpAutoIncrements (current: Catalog) (target: Catalog) : Catalog =
    current
    |> Map.fold
        (fun acc dbName (currentDb: Database) ->
            match Map.tryFind dbName acc with
            | None -> acc
            | Some targetDb ->
                let mergedDb =
                    currentDb
                    |> Map.fold
                        (fun tacc tableName (currentTable: Table) ->
                            match Map.tryFind tableName tacc with
                            | Some(targetTable: Table) when currentTable.NextAutoId > targetTable.NextAutoId ->
                                Map.add tableName { targetTable with NextAutoId = currentTable.NextAutoId } tacc
                            | _ -> tacc)
                        targetDb

                Map.add dbName mergedDb acc)
        target

let private tryGetTable (db: Database) (tableName: string) : Result<Table, StorageError> =
    match Map.tryFind (normalizeTableName tableName) db with
    | Some t -> Ok t
    | None -> Error(NoSuchTable tableName)

/// Auto-creates a database the first time a real table is written into it
/// (`withDatabase`), and for the database a client names at connect time
/// (`mysql -D foo`/PDO's `dbname=foo` DSN, a zero-setup convenience for a
/// fresh in-memory server); a no-op if it already exists. Deliberately
/// *not* used by mid-session `USE`/`COM_INIT_DB` — those check
/// `databaseExists` and report a real 1049 instead, matching MySQL (see
/// `QueryHandler`'s `Use` probe). `ConcurrentDictionary.TryAdd` is already
/// atomic, so unlike the old whole-catalog CAS this needs no retry loop.
let ensureDatabase (store: Store) (dbName: string) : unit =
    store.Databases.TryAdd(dbName, ref Map.empty) |> ignore

/// Whether `dbName` is a real catalog entry, or the always-present virtual
/// `information_schema` — what `USE`/`COM_INIT_DB` check to match real
/// MySQL's `ERROR 1049 Unknown database` instead of silently accepting (and
/// then auto-vivifying on first write, via `ensureDatabase`) a typo'd or
/// missing name. Checks `Databases` directly rather than going through the
/// whole-catalog `Store.Catalog` view — this is on the per-statement hot
/// path, not just a diagnostic.
let databaseExists (store: Store) (dbName: string) : bool =
    String.Equals(dbName, "information_schema", StringComparison.OrdinalIgnoreCase)
    || store.Databases.ContainsKey dbName

/// Index of a column by name, case-insensitive.
let resolveColumn (columns: ColumnDef list) (name: string) : Result<int, StorageError> =
    columns
    |> List.tryFindIndex (fun c -> String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
    |> function
        | Some i -> Ok i
        | None -> Error(UnknownColumn name)

/// Ambient per-thread cancellation for the query currently executing on
/// this thread — armed by the connection loop's disconnect watcher
/// (`Server.withCancellationWatch`) right before dispatching a statement and
/// cleared right after. `traverse` below (and `Executor.traverseSeq`, the
/// non-equi join's lazy nested loop) is the only reader: a client that
/// vanishes mid-query flips this token, and the next periodic check unwinds
/// the row fold instead of computing into a closed connection. A plain
/// per-thread field rather than something threaded through every one of
/// `traverse`'s ~50 call sites — there is exactly one query running per
/// thread at a time, so "current thread's token" is all a check needs.
let queryCancellation = new ThreadLocal<CancellationToken>(fun () -> CancellationToken.None)

/// How often a row-pipeline fold checks `queryCancellation` — a modulo and
/// an occasional `IsCancellationRequested` read, cheap enough against a
/// row's own `evalExpr` cost to be unmeasurable, frequent enough that a
/// killed client's query unwinds within a few hundred rows rather than
/// running to completion.
let cancellationCheckInterval = 256

/// Applies `f` to each element, short-circuiting on the first `Error` —
/// generalized over any error type (not just `StorageError`) and public, so
/// `Executor` reuses this tail-recursive traversal instead of keeping its
/// own non-tail-recursive copy. The single choke point every row-pipeline
/// fold in `Executor` (WHERE, projection, grouping, window functions,
/// mutation joins) routes through — see `queryCancellation`.
let traverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
    let token = queryCancellation.Value

    let rec loop i acc =
        function
        | [] -> Ok(List.rev acc)
        | x :: rest ->
            if i % cancellationCheckInterval = 0 then
                token.ThrowIfCancellationRequested()

            match f x with
            | Ok y -> loop (i + 1) (y :: acc) rest
            | Error e -> Error e

    loop 0 [] xs

let private parseNumeric (s: string) : float option =
    match Double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, d -> Some d
    | false, _ -> None

/// MySQL-style coercion of a value to a column's declared type
/// (`'12' -> 12` for an INT column); error 1366 when it's not possible and
/// `strict` (the session's STRICT_TRANS_TABLES/STRICT_ALL_TABLES, see
/// `Store.StrictMode`) is set — MySQL's actual default. Off (Laravel's
/// `'strict' => false` connection config, which sends
/// `SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'`), an otherwise-rejected
/// value coerces to MySQL's non-strict fallback instead: 0 for a numeric
/// column, NULL for a nullable temporal one. ponytail: a NOT NULL temporal
/// column still hard-fails non-strict too — real MySQL's fallback there is
/// the zero date `'0000-00-00'`, which `VDate`/`VDateTime` (backed by
/// `DateOnly`/`DateTime`, no year zero) can't represent; add a zero-date
/// sentinel if a NOT NULL date/datetime column ever needs this path.
/// NULL always passes through untouched — nullability is checked
/// separately.
let coerceValue (strict: bool) (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    let fail () =
        Error(InvalidValueForColumn(col.Name, v |> toText |> Option.defaultValue "NULL"))

    /// Charset write-time semantics, MySQL-verified: `ascii` rejects
    /// non-ASCII with 1366 in strict mode (lossy '?' otherwise); `latin1`
    /// (cp1252) lossy-maps anything unencodable to '?' even in strict mode
    /// (its all-256-slots table has no unassigned code points to reject);
    /// the rest (utf8mb4/None) pass through unchanged.
    let charsetChecked (text: string) : Result<string, StorageError> =
        match col.Charset with
        | Some "ascii" ->
            if text |> String.forall (fun c -> int c < 0x80) then
                Ok text
            elif strict then
                fail ()
            else
                Ok(Collation.Charset.transcodeAscii text)
        | Some "latin1" -> Ok(Collation.Charset.transcodeLatin1 text)
        | _ -> Ok text

    /// Non-strict's numeric fallback: 0, always representable.
    let numericFallback (zero: unit -> Value) = if strict then fail () else Ok(zero ())

    /// Non-strict's temporal fallback: MySQL's zero date, which
    /// `VDate`/`VDateTime` can't represent (see the type's doc comment) — NULL
    /// stands in for it on a nullable column, otherwise this still hard-fails.
    let temporalFallback () =
        if strict || not col.Nullable then fail () else Ok VNull

    match v with
    | VNull -> Ok VNull
    | _ ->
        match col.Type with
        | TInt _
        | TBigInt _
        | TSmallInt _
        | TMediumInt _
        | TTinyInt _
        | TYear ->
            match v with
            | VInt i -> Ok(VInt i)
            | VDouble d -> Ok(VInt(int64 d))
            | VDecimal d -> Ok(VInt(int64 d))
            | VString s ->
                match parseNumeric s with
                | Some d -> Ok(VInt(int64 d))
                | None -> numericFallback (fun () -> VInt 0L)
            | _ -> numericFallback (fun () -> VInt 0L)
        | TDouble
        | TFloat ->
            match v with
            | VDouble d -> Ok(VDouble d)
            | VInt i -> Ok(VDouble(float i))
            | VDecimal d -> Ok(VDouble(float d))
            | VString s ->
                match parseNumeric s with
                | Some d -> Ok(VDouble d)
                | None -> numericFallback (fun () -> VDouble 0.0)
            | _ -> numericFallback (fun () -> VDouble 0.0)
        | TDecimal(_, scale) ->
            // MySQL pads/rounds every stored value to the column's declared
            // scale (`DECIMAL(10,2)` stores `100` as `100.00`), and later
            // reads it back at that same scale — `.NET`'s `decimal` carries
            // its own scale, but round-tripping through `Math.Round` alone
            // doesn't widen it (`Math.Round(100m, 2)` is still `100`, not
            // `100.00`), so go through a fixed-point string format instead,
            // which both rounds and pads in one step.
            let rescale (d: decimal) =
                Decimal.Parse(d.ToString("F" + string scale, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)

            match v with
            | VDecimal d -> Ok(VDecimal(rescale d))
            | VInt i -> Ok(VDecimal(rescale (decimal i)))
            | VDouble d -> Ok(VDecimal(rescale (decimal d)))
            | VString s ->
                match parseNumeric s with
                | Some d -> Ok(VDecimal(rescale (decimal d)))
                | None -> numericFallback (fun () -> VDecimal(rescale 0M))
            | _ -> numericFallback (fun () -> VDecimal(rescale 0M))
        | TChar _
        | TVarchar _
        | TTinyText
        | TText
        | TMediumText
        | TLongText
        | TSet _
        | TTime
        | TJson -> charsetChecked (v |> toText |> Option.defaultValue "") |> Result.map VString
        | TBinary _
        | TVarBinary _
        | TTinyBlob
        | TBlob
        | TMediumBlob
        | TLongBlob ->
            match v with
            | VBytes bytes -> Ok(VBytes bytes)
            // A character literal assigned to a binary column is encoded
            // using the connection's effective utf8mb4 character set. Raw
            // `X'...'` literals already arrive as VBytes and bypass this
            // conversion, preserving every byte including invalid UTF-8.
            | VString text -> Ok(VBytes(Text.Encoding.UTF8.GetBytes text))
            | _ -> Ok(VBytes(v |> toText |> Option.defaultValue "" |> Text.Encoding.UTF8.GetBytes))
        | TEnum values ->
            // MySQL's own error for a rejected ENUM value is 1265 "Data
            // truncated" (SQLSTATE 01000), not the 1366 incorrect-value
            // error other column types raise in strict mode.
            let enumFail () = Error(DataTruncatedForColumn col.Name)

            match v with
            | VString s ->
                match values |> List.tryFind (fun allowed -> String.Equals(allowed, s, StringComparison.OrdinalIgnoreCase)) with
                // MySQL stores the declaration index and reads back the
                // *declared* spelling — canonicalize the casing rather than
                // round-tripping the input's ('ADMIN' comes back 'admin').
                | Some canonical -> Ok(VString canonical)
                | None ->
                    // A quoted number that isn't a declared label is still a
                    // 1-based index (MySQL: "If the numeric value is quoted,
                    // it is still interpreted as an index if there is no
                    // matching string in the list of enumeration members").
                    match Int64.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture) with
                    | true, i when i >= 1L && i <= int64 (List.length values) -> Ok(VString values.[int i - 1])
                    | _ -> enumFail ()
            // MySQL also accepts a 1-based index into the declared value list.
            | VInt i when i >= 1L && i <= int64 (List.length values) -> Ok(VString values.[int i - 1])
            | _ -> enumFail ()
        | TDate ->
            match v with
            | VDate d -> Ok(VDate d)
            | VDateTime dt -> Ok(VDate(DateOnly.FromDateTime dt))
            | VString s ->
                // A plain date parses directly; a full datetime string
                // (real MySQL accepts one into a DATE column too, keeping
                // just the date part and silently dropping the time — e.g.
                // Eloquent's `date` cast round-tripping a `Carbon` instance
                // through its full `'Y-m-d H:i:s'` string form) falls back
                // to `DateTime.TryParse` and truncates.
                match DateOnly.TryParse(s.Trim(), CultureInfo.InvariantCulture) with
                | true, d -> Ok(VDate d)
                | false, _ ->
                    match DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None) with
                    | true, dt -> Ok(VDate(DateOnly.FromDateTime dt))
                    | false, _ -> temporalFallback ()
            | _ -> temporalFallback ()
        | TDateTime
        | TTimestamp ->
            match v with
            | VDateTime dt -> Ok(VDateTime dt)
            | VDate d -> Ok(VDateTime(d.ToDateTime(TimeOnly.MinValue)))
            | VString s ->
                match DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, dt -> Ok(VDateTime dt)
                | false, _ -> temporalFallback ()
            | _ -> temporalFallback ()

/// Evaluates a column's `DEFAULT` clause into the value to insert when none
/// was provided — `CURRENT_TIMESTAMP` evaluates fresh here (insert time),
/// rather than being carried around as a stored marker value.
let private evalDefault (d: ColumnDefault option) : Value =
    match d with
    | None -> VNull
    | Some(DConst v) -> v
    // Precision 0, same as MySQL's `DEFAULT CURRENT_TIMESTAMP` and `NOW()`:
    // truncate the sub-second part `Value.toText` would otherwise render.
    | Some DCurrentTimestamp -> VDateTime(Functions.truncateToSecond DateTime.Now)

/// Coerces a value to its column's type and rejects NULL for a non-nullable
/// column.
let private coerceAndCheck (strict: bool) (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    match v with
    | VNull when not col.Nullable -> Error(NotNullViolation col.Name)
    | _ -> coerceValue strict col v

/// The `(keyName, column indices)` groups that must be unique: the primary
/// key (if any, named `"PRIMARY"` the way MySQL reports it in error 1062,
/// and treated as one group across however many columns it spans) plus
/// every `UNIQUE` index, named after itself.
let private uniqueKeyGroups (table: Table) : (string * int list) list =
    let pk =
        table.Columns |> List.indexed |> List.choose (fun (i, c) -> if c.PrimaryKey then Some i else None)

    let fromIndexes =
        table.Indexes
        |> List.filter (fun ix -> ix.Unique)
        |> List.choose (fun ix -> ix.Columns |> traverse (resolveColumn table.Columns) |> Result.toOption |> Option.map (fun idxs -> ix.Name, idxs))

    (if pk.IsEmpty then [] else [ "PRIMARY", pk ]) @ fromIndexes

/// Stable equality key for values already coerced into a table column's
/// declared type. Strings use the same case-insensitive, PAD SPACE semantics
/// as Value.compare; every other same-typed value uses an exact encoding.
/// NULL is deliberately absent because a UNIQUE key containing any NULL
/// never collides under MySQL's semantics.
let private encodeConstraintKey (columns: ColumnDef list) (indices: int list) (row: Value[]) : string option =
    let collationOf index =
        columns.[index].Collation
        |> Option.bind Collation.tryFind
        |> Option.defaultValue Collation.defaultCollation

    let encode (index: int) =
        match row.[index] with
        | VNull -> None
        | VInt value -> Some("I" + string value)
        | VDouble value ->
            let normalized = if value = 0.0 then 0.0 else value
            Some("D" + normalized.ToString("R", CultureInfo.InvariantCulture))
        | VDecimal value -> Some("M" + value.ToString("G29", CultureInfo.InvariantCulture))
        // The column's own collation key — case/accent folding per the
        // declared COLLATE (utf8mb4_bin stays byte-distinct, a PAD SPACE
        // collation trims). Same rules as WHERE equality, so the index and
        // the comparison can never disagree.
        | VString value -> Some("S" + (collationOf index).KeyOf value)
        | VBytes value -> Some("B" + Convert.ToHexString value)
        | VDate value -> Some("T" + string value.DayNumber)
        | VDateTime value -> Some("V" + string value.Ticks)
        | VJson value -> Some("J" + value.TrimEnd(' ').ToUpperInvariant())

    let encoded = indices |> List.map encode

    if encoded |> List.exists Option.isNone then
        None
    else
        encoded
        |> List.choose id
        |> List.map (fun value -> string value.Length + ":" + value)
        |> String.concat ""
        |> Some

let private constraintLookup columns indices rows =
    let lookup = HashSet<string>(StringComparer.Ordinal)

    for row in rows do
        encodeConstraintKey columns indices row |> Option.iter (lookup.Add >> ignore)

    lookup

/// `table.UniqueIndex` recomputed from scratch against its current `Rows` —
/// the one full-scan rebuild this index still needs, used only at
/// structural boundaries (`createTable`, `truncate`, `alterTable` — column
/// positions may have shifted — and `Persistence`'s replay/snapshot-load,
/// which write `Rows` directly, bypassing every checked path below that
/// otherwise maintains the index incrementally). `Map.ofList` keeping the
/// last entry for a repeated key is a non-issue here: a well-formed table
/// never has two rows actually colliding on a real PK/UNIQUE group.
let private rebuildUniqueIndex (table: Table) : Map<string, Map<string, int>> =
    uniqueKeyGroups table
    |> List.map (fun (name, idxs) ->
        let inner =
            table.RowsArray
            |> Seq.indexed
            |> Seq.choose (fun (i, row) -> encodeConstraintKey table.Columns idxs row |> Option.map (fun k -> k, i))
            |> Map.ofSeq

        name, inner)
    |> Map.ofList

/// Bumped once per `reindexTable` call — the full-scan rebuild it wraps is
/// the O(table size) cost a replay must pay a constant number of times, not
/// once per replayed event. `AsyncLocal`, not a plain mutable: Expecto runs
/// tests in parallel, each
/// on its own async flow, so a test that increments this only sees its own
/// count instead of racing every other test's `createTable`/`truncate`/WAL
/// replay in the suite. Lets a test assert "reindexed a constant number of
/// times", not race a wall-clock threshold under machine load.
let private reindexCallCountLocal = System.Threading.AsyncLocal<int>()

let reindexCallCount () = reindexCallCountLocal.Value

/// Public because `Persistence`'s WAL replay (`mapTableRows`) and snapshot
/// load (`decodeTable`) both write `Rows` directly — same reason
/// `normalizeTableName` is public — so they need to rebuild the index
/// themselves afterward instead of maintaining it incrementally the way
/// every write path below does.
let reindexTable (table: Table) : Table =
    reindexCallCountLocal.Value <- reindexCallCountLocal.Value + 1
    { table with UniqueIndex = rebuildUniqueIndex table }

/// Removes/adds one row's entry in every unique group's map, the
/// incremental update every write path below makes instead of
/// `rebuildUniqueIndex`'s full rescan. `removed` is the row's old values
/// (only its key matters — removal doesn't touch a position); `added` is its
/// new values paired with the `Rows` position they land at. The same logical
/// row for an `UPDATE` (its before/after values, same position), just one
/// side for a plain `INSERT`/`DELETE`, and both for `upsertRows`'
/// matched-row case.
let private reindexRow
    (columns: ColumnDef list)
    (uniqueGroups: (string * int list) list)
    (removed: Value[] option)
    (added: (int * Value[]) option)
    (index: Map<string, Map<string, int>>)
    : Map<string, Map<string, int>> =
    uniqueGroups
    |> List.fold
        (fun accIndex (name, idxs) ->
            let group = Map.find name accIndex
            let group = removed |> Option.fold (fun g r -> encodeConstraintKey columns idxs r |> Option.fold (fun g' k -> Map.remove k g') g) group
            let group = added |> Option.fold (fun g (pos, a) -> encodeConstraintKey columns idxs a |> Option.fold (fun g' k -> Map.add k pos g') g) group
            Map.add name group accIndex)
        index

/// The parent table's persistent unique-key index for exactly the column
/// order `refIdxs` resolves to, if one of its PK/UNIQUE groups matches —
/// the hash-index fast path `checkFkParent` uses for "does a parent row
/// with this key exist". `None` when no such group exists (every real
/// MySQL FK references a unique/PK constraint of the parent in matching
/// column order, so this only misses on stale/malformed FK metadata) —
/// `checkFkParent` falls back to a full scan in that case, same as before
/// this index existed.
let private parentUniqueIndex (parent: Table) (refIdxs: int list) : Map<string, int> option =
    uniqueKeyGroups parent |> List.tryPick (fun (name, idxs) -> if idxs = refIdxs then Map.tryFind name parent.UniqueIndex else None)

/// A per-statement FK parent-key membership test, either a live `HashSet`
/// that a self-FK extends row by row (`Mutable`) or a snapshot of the
/// parent's own `UniqueIndex` reused as-is (`Fixed`) — `insertCore`'s
/// `foreignKeyLookups` picks whichever fits per FK; see its doc.
type private ParentKeySource =
    | Mutable of HashSet<string>
    | Fixed of Map<string, int>

let private parentKeySourceContains (key: string) (source: ParentKeySource) : bool =
    match source with
    | Mutable set -> set.Contains key
    | Fixed idx -> Map.containsKey key idx

let private parentKeySourceAdd (key: string) (source: ParentKeySource) : unit =
    match source with
    | Mutable set -> set.Add key |> ignore
    | Fixed _ -> () // A non-self FK's parent can't change mid-statement.

/// `col = literal`'s columns and candidate rows via `dbName.tableName`'s
/// PK/UNIQUE hash index, when `columnName` names a single-column PK/UNIQUE
/// group and `literal` already has that column's exact stored `Value`
/// shape — checked by round-tripping it through `coerceValue` and requiring
/// the result back unchanged, rather than hand-listing which `Value`
/// variant goes with which `ColumnType`: a literal that survives
/// `coerceValue` untouched is, by construction, already in the one shape
/// `insertCore` would ever have stored for that column (every other branch
/// of `coerceValue` changes the value's shape or its content), so
/// `encodeConstraintKey`'s encoding of it can't help but match however the
/// index encoded a real row's value there — no separate proof that the two
/// encodings agree is needed. `None` for anything this can't prove safe
/// (multi-column groups, a literal that needs real coercion, a NULL
/// literal's own encoding is `None` too but that correctly yields `Some []`
/// — an `= NULL` conjunct can never match any row) — the caller's own full
/// scan stays correct in every one of those cases, this is a pure,
/// optional narrowing. `Executor.tryPointLookup` is the only caller. Returns
/// each candidate's `Rows` position alongside its values — `Executor`'s
/// `UPDATE`/`DELETE` narrowing threads that position straight into
/// `updateRows`/`deleteRows` so they can replace/remove it in place instead
/// of re-deriving its position with a full scan.
let tryUniqueLookup
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (literal: Value)
    : (ColumnDef list * (int * Value[]) list) option =
    // Reads `dbName`'s slot directly, same reason `scan` does — this is a
    // per-row-lookup hot path, not somewhere to pay `Store.Catalog`'s
    // whole-catalog rebuild.
    let table =
        match store.Databases.TryGetValue dbName with
        | false, _ -> None
        | true, slot -> Map.tryFind (normalizeTableName tableName) slot.Value

    match table with
    | None -> None
    | Some table ->
        match resolveColumn table.Columns columnName with
        | Error _ -> None
        | Ok idx ->
            match uniqueKeyGroups table |> List.tryFind (fun (_, idxs) -> idxs = [ idx ]) with
            | None -> None
            | Some(groupName, _) ->
                match coerceValue store.StrictMode table.Columns.[idx] literal with
                | Ok coerced when coerced = literal ->
                    let rows =
                        match encodeConstraintKey table.Columns [ idx ] [| literal |] with
                        | None -> []
                        | Some key ->
                            table.UniqueIndex
                            |> Map.tryFind groupName
                            |> Option.bind (Map.tryFind key)
                            |> Option.map (fun pos -> pos, table.RowsArray.[pos])
                            |> Option.toList

                    Some(table.Columns, rows)
                | _ -> None

/// Verifies every foreign key `fks` (a child table's own `ForeignKeys`) has
/// a matching parent row for `row`'s values, per MySQL's MATCH SIMPLE
/// semantics: a foreign key with any `NULL` column doesn't need a parent at
/// all. Malformed FK metadata (a column name that no longer resolves, e.g.
/// after a `DROP COLUMN` that didn't also drop the FK) or a since-dropped
/// referenced table/column is treated as "not enforceable" rather than
/// blocking every write — `information_schema` can still show the stale FK,
/// same as MySQL leaves a dangling constraint visible after `DROP TABLE ...
/// FOREIGN_KEY_CHECKS=0`.
let private checkFkParent (db: Database) (childColumns: ColumnDef list) (row: Value[]) (fk: ForeignKeyDef) : Result<unit, StorageError> =
    match fk.Columns |> traverse (resolveColumn childColumns) with
    | Error _ -> Ok()
    | Ok idxs ->
        let values = idxs |> List.map (fun i -> row.[i])

        if values |> List.exists ((=) VNull) then
            Ok()
        else
            match Map.tryFind (normalizeTableName fk.RefTable) db with
            | None -> Ok()
            | Some parent ->
                match fk.RefColumns |> traverse (resolveColumn parent.Columns) with
                | Error _ -> Ok()
                | Ok refIdxs ->
                    // The hash-index fast path when the parent's referenced
                    // key is itself PK/UNIQUE (always true for a real FK) —
                    // `values` is already in `refIdxs`' own order, so
                    // encoding it at positions `[0 .. n-1]` reproduces
                    // exactly the key `parentUniqueIndex` stored it under.
                    // Falls back to the full scan only for stale/malformed
                    // FK metadata that doesn't resolve to a real unique
                    // group.
                    let found =
                        match parentUniqueIndex parent refIdxs with
                        | Some index -> encodeConstraintKey parent.Columns refIdxs (Array.ofList values) |> Option.map index.ContainsKey |> Option.defaultValue false
                        | None -> parent.RowsArray |> Seq.exists (fun prow -> List.forall2 (fun i v -> compare prow.[i] v = 0) refIdxs values)

                    if found then Ok() else Error(ForeignKeyParentMissing fk.Name)

let private checkFkParents (db: Database) (childColumns: ColumnDef list) (fks: ForeignKeyDef list) (row: Value[]) : Result<unit, StorageError> =
    fks |> traverse (checkFkParent db childColumns row) |> Result.map ignore

/// As `withDatabase`, one level deeper: look up `tableName` within the
/// database too, and re-key the updated table back under its normalized
/// name.
let private withTable
    (store: Store)
    (dbName: string)
    (tableName: string)
    (f: Table -> Result<Table * 'a, StorageError>)
    : Result<'a, StorageError> =
    withDatabase store dbName (fun db ->
        tryGetTable db tableName
        |> Result.bind (fun table -> f table |> Result.map (fun (table', result) -> Map.add (normalizeTableName tableName) table' db, result)))

let createTable
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: ColumnDef list)
    (indexes: IndexDef list)
    (foreignKeys: ForeignKeyDef list)
    (tableCharset: string option)
    (tableCollation: string option)
    : Result<unit, StorageError> =
    ensureDatabase store dbName

    let result =
        withDatabase store dbName (fun db ->
            let key = normalizeTableName tableName

            if Map.containsKey key db then
                Error(TableExists tableName)
            else
                let table =
                    { OriginalName = tableName
                      Columns = columns
                      RowsArray = ImmutableArray.Empty
                      NextAutoId = 1L
                      Indexes = indexes
                      ForeignKeys = foreignKeys
                      TableCharset = tableCharset
                      TableCollation = tableCollation
                      UniqueIndex = Map.empty }

                Ok(Map.add key (reindexTable table) db, ()))

    if result.IsOk then
        emit store (Some(SchemaChanged(dbName, CreateTable(tableName, columns, indexes, foreignKeys, false, tableCharset, tableCollation))))

    result

let dropTable (store: Store) (dbName: string) (tableName: string) : Result<unit, StorageError> =
    let result =
        withDatabase store dbName (fun db ->
            let key = normalizeTableName tableName

            if Map.containsKey key db then
                Ok(Map.remove key db, ())
            else
                Error(NoSuchTable tableName))

    if result.IsOk then
        emit store (Some(SchemaChanged(dbName, DropTable([ tableName ], false))))

    result

let truncate (store: Store) (dbName: string) (tableName: string) : Result<unit, StorageError> =
    let result = withTable store dbName tableName (fun table -> Ok(reindexTable { table with RowsArray = ImmutableArray.Empty; NextAutoId = 1L }, ()))

    if result.IsOk then
        emit store (Some(SchemaChanged(dbName, Truncate tableName)))

    result

/// Removes column index `idx` from every row — used by `DropColumn`, since
/// `Value[]` has no built-in "remove at" the way a `ResizeArray` would.
let private removeColumnAt (idx: int) (row: Value[]) : Value[] =
    row |> Array.indexed |> Array.filter (fun (i, _) -> i <> idx) |> Array.map snd

/// The value an added column gets filled in with for every row that already
/// exists — its `DEFAULT`, or `NULL` otherwise. ponytail: a `NOT NULL`
/// column with no `DEFAULT` added to a non-empty table silently gets `NULL`
/// in every existing row rather than MySQL's strict-mode 1364 error; add the
/// check once a migration actually exercises that combination against data.
let private addedColumnFill (col: ColumnDef) : Value = evalDefault col.Default

/// Inserts `x` at `idx` (clamped to `xs`'s length, so `idx = List.length xs`
/// appends) — used by `AFTER`/`FIRST` column positioning, since `Columns`
/// and each row's `Value[]` are both plain lists/arrays with no built-in
/// "insert at" the way a `ResizeArray` would have.
let private insertAt (idx: int) (x: 'a) (xs: 'a list) : 'a list =
    let before, after = xs |> List.splitAt (min idx (List.length xs))
    before @ [ x ] @ after

/// Resolves `FIRST`/`AFTER col`/no-clause-given to a concrete 0-based index
/// into `columnsExcludingSelf` (the table's columns with the column being
/// added/moved already removed, so an `AFTER`/`FIRST` offset means the same
/// thing whether this is a brand new column or one already elsewhere in the
/// list) — `fallback` is what `PositionDefault` (no `AFTER`/`FIRST` written)
/// resolves to, which differs by caller: `AddColumn` wants the end of the
/// table (a plain `ADD COLUMN` with no position appends), `ModifyColumn`/
/// `ChangeColumn` want the column's own current index (a plain `MODIFY`/
/// `CHANGE COLUMN` with no position leaves it exactly where it was).
let private resolvePosition (columnsExcludingSelf: ColumnDef list) (fallback: int) (position: ColumnPosition) : Result<int, StorageError> =
    match position with
    | PositionDefault -> Ok fallback
    | PositionFirst -> Ok 0
    | PositionAfter col -> resolveColumn columnsExcludingSelf col |> Result.map (fun idx -> idx + 1)

/// Applies one `Ast.AlterAction` to `table`, returning its replacement and,
/// for `RenameTo`, the new key it should be re-filed under in the database
/// map (`None` means "same key").
let private applyAlterAction (table: Table) (action: AlterAction) : Result<Table * string option, StorageError> =
    match action with
    | AddColumn(col, position) ->
        let fill = addedColumnFill col

        // The table's declared defaults attach to the new string column the
        // same way `CREATE TABLE` bakes them — MySQL-verified: `ALTER TABLE
        // t ADD COLUMN name VARCHAR(10)` on a table declared
        // `COLLATE=utf8mb4_unicode_ci` reports that collation on the new
        // column, while a column-level COLLATE still wins; a plain table
        // lands on the server default.
        let colWithDefaults =
            match col.Type with
            | TChar _
            | TVarchar _
            | TTinyText
            | TText
            | TMediumText
            | TLongText
            | TEnum _
            | TSet _ ->
                { col with
                    Collation =
                        col.Collation
                        |> Option.orElse table.TableCollation
                        |> Option.orElse (Some Collation.defaultCollation.Name)
                    Charset = col.Charset |> Option.orElse table.TableCharset }
            | _ -> col

        resolvePosition table.Columns (List.length table.Columns) position
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> insertAt idx colWithDefaults
                RowsArray = table.RowsArray |> Seq.map (fun r -> r |> Array.toList |> insertAt idx fill |> Array.ofList) |> ImmutableArray.CreateRange },
            None)
    | DropColumn name ->
        resolveColumn table.Columns name
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> List.indexed |> List.filter (fun (i, _) -> i <> idx) |> List.map snd
                RowsArray = table.RowsArray |> Seq.map (removeColumnAt idx) |> ImmutableArray.CreateRange },
            None)
    | ModifyColumn(newDef, position) ->
        // ponytail: replaces the column's definition only — existing rows
        // aren't re-coerced into the new type, so a `MODIFY` that narrows a
        // type can leave a row holding a value that wouldn't itself pass
        // `coerceValue` today. Add a re-coercion pass if a migration's
        // assertions ever depend on it.
        resolveColumn table.Columns newDef.Name
        |> Result.bind (fun oldIdx ->
            let columnsExcludingSelf = table.Columns |> List.indexed |> List.filter (fun (i, _) -> i <> oldIdx) |> List.map snd

            resolvePosition columnsExcludingSelf oldIdx position
            |> Result.map (fun newIdx ->
                { table with
                    Columns = columnsExcludingSelf |> insertAt newIdx newDef
                    RowsArray =
                        table.RowsArray
                        |> Seq.map (fun r ->
                            let v = r.[oldIdx]
                            r |> removeColumnAt oldIdx |> Array.toList |> insertAt newIdx v |> Array.ofList)
                        |> ImmutableArray.CreateRange },
                None))
    | ChangeColumn(oldName, newDef, position) ->
        resolveColumn table.Columns oldName
        |> Result.bind (fun oldIdx ->
            let columnsExcludingSelf = table.Columns |> List.indexed |> List.filter (fun (i, _) -> i <> oldIdx) |> List.map snd

            resolvePosition columnsExcludingSelf oldIdx position
            |> Result.map (fun newIdx ->
                { table with
                    Columns = columnsExcludingSelf |> insertAt newIdx newDef
                    RowsArray =
                        table.RowsArray
                        |> Seq.map (fun r ->
                            let v = r.[oldIdx]
                            r |> removeColumnAt oldIdx |> Array.toList |> insertAt newIdx v |> Array.ofList)
                        |> ImmutableArray.CreateRange },
                None))
    | RenameTo newName -> Ok({ table with OriginalName = newName }, Some(normalizeTableName newName))
    | RenameColumnTo(oldName, newName) ->
        resolveColumn table.Columns oldName
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> List.mapi (fun i c -> if i = idx then { c with Name = newName } else c) },
            None)
    | AddIndex ix when ix.Unique ->
        // `CREATE UNIQUE INDEX`/`ALTER TABLE ... ADD UNIQUE` over rows that
        // already collide must fail with the same 1062 a plain INSERT would
        // give — otherwise `reindexTable` (Map.ofList, last-wins) silently
        // drops every row but one from the new UniqueIndex, and both the
        // fast path and the constraint itself go missing from then on.
        ix.Columns
        |> traverse (resolveColumn table.Columns)
        |> Result.bind (fun idxs ->
            let rec firstCollision seen rows =
                match rows with
                | [] -> None
                | (row: Value[]) :: rest ->
                    match encodeConstraintKey table.Columns idxs row with
                    | Some key when Set.contains key seen ->
                        let value = idxs |> List.map (fun i -> row.[i] |> toText |> Option.defaultValue "NULL") |> String.concat "-"
                        Some(DuplicateKey(ix.Name, value))
                    | Some key -> firstCollision (Set.add key seen) rest
                    | None -> firstCollision seen rest

            match firstCollision Set.empty (List.ofSeq table.RowsArray) with
            | Some e -> Error e
            | None -> Ok({ table with Indexes = table.Indexes @ [ ix ] }, None))
    | AddIndex ix -> Ok({ table with Indexes = table.Indexes @ [ ix ] }, None)
    | DropIndexAction name ->
        Ok(
            { table with
                Indexes = table.Indexes |> List.filter (fun ix -> not (String.Equals(ix.Name, name, StringComparison.OrdinalIgnoreCase))) },
            None
        )
    | AddForeignKey fk -> Ok({ table with ForeignKeys = table.ForeignKeys @ [ fk ] }, None)
    | DropForeignKey name ->
        Ok(
            { table with
                ForeignKeys = table.ForeignKeys |> List.filter (fun fk -> not (String.Equals(fk.Name, name, StringComparison.OrdinalIgnoreCase))) },
            None
        )
    | AddPrimaryKey cols ->
        Ok(
            { table with
                Columns = table.Columns |> List.map (fun c -> if List.contains c.Name cols then { c with PrimaryKey = true } else c) },
            None
        )

/// Applies `actions` in order against `tableName`, re-filing it under a new
/// key if any action renamed it (`RENAME TO`/`RENAME [TABLE]`).
let alterTable (store: Store) (dbName: string) (tableName: string) (actions: AlterAction list) : Result<unit, StorageError> =
    let result =
        withDatabase store dbName (fun db ->
            tryGetTable db tableName
            |> Result.bind (fun table ->
                let origKey = normalizeTableName tableName

                let step acc action =
                    acc
                    |> Result.bind (fun (key, tbl) ->
                        applyAlterAction tbl action
                        |> Result.map (fun (tbl', newKey) -> (newKey |> Option.defaultValue key), tbl'))

                actions
                |> List.fold step (Ok(origKey, table))
                // Column positions/count may have shifted (`ADD`/`DROP`/
                // `MODIFY COLUMN`), so a full rebuild rather than an
                // incremental patch — ALTER isn't a hot path.
                |> Result.map (fun (finalKey, finalTable) -> Map.remove origKey db |> Map.add finalKey (reindexTable finalTable), ())))

    if result.IsOk then
        emit store (Some(SchemaChanged(dbName, AlterTable(tableName, actions))))

    result

let renameTable (store: Store) (dbName: string) (oldName: string) (newName: string) : Result<unit, StorageError> =
    alterTable store dbName oldName [ RenameTo newName ]

/// One column's value for one row being inserted, threaded through
/// `processRow`'s fold: the column's final coerced value, the updated
/// AUTO_INCREMENT counter, and the id assigned to this row's AUTO_INCREMENT
/// column (if any) paired with whether `nextAutoId` generated it or it was
/// supplied explicitly — `insertCore` needs that distinction to compute the
/// statement's `last_insert_id` the way real MySQL does (see its doc).
let private processRow
    (strict: bool)
    (nextAutoId: int64)
    (rawRow: Value option list)
    (columns: ColumnDef list)
    : Result<Value list * int64 * (bool * int64) option, StorageError> =
    let step acc (col: ColumnDef, provided: Value option) =
        match acc with
        | Error e -> Error e
        | Ok(valuesRev, nextAutoId, assignedId) ->
            let pending = provided |> Option.defaultValue (evalDefault col.Default)

            if col.AutoIncrement then
                match pending with
                | VNull -> Ok(VInt nextAutoId :: valuesRev, nextAutoId + 1L, Some(true, nextAutoId))
                | _ ->
                    match coerceValue strict col pending with
                    | Error e -> Error e
                    | Ok(VInt i) -> Ok(VInt i :: valuesRev, max nextAutoId (i + 1L), Some(false, i))
                    | Ok _ -> Error(InvalidValueForColumn(col.Name, "auto_increment"))
            else
                match coerceAndCheck strict col pending with
                | Ok v -> Ok(v :: valuesRev, nextAutoId, assignedId)
                | Error e -> Error e

    List.zip columns rawRow
    |> List.fold step (Ok([], nextAutoId, None))
    |> Result.map (fun (valuesRev, nextAutoId, assignedId) -> List.rev valuesRev, nextAutoId, assignedId)

/// Resolves `columns` (the explicit column list, or `None` for "all columns
/// in table order") to indices against `table`.
let private resolveInsertColumns (table: Table) (columns: string list option) : Result<int list, StorageError> =
    match columns with
    | None -> Ok [ 0 .. table.Columns.Length - 1 ]
    | Some names -> names |> traverse (resolveColumn table.Columns)

/// Shared core of `insertRows` and `insertRowsIgnore`: builds each row via
/// `processRow`, then checks it against the table's unique keys (including
/// rows already accepted earlier in this same statement, since two rows in
/// one multi-row `INSERT` can collide with each other) and, when `checkFks`
/// is set, its foreign keys' parents. A row's own shape (wrong column count)
/// is always a hard error — `INSERT IGNORE` downgrades constraint
/// violations per MySQL, not malformed statements — everything else is
/// skipped rather than failing the batch when `ignoreErrors` is set.
///
/// The statement's reported `last_insert_id` (what `PDO::lastInsertId()`/
/// `mysql_insert_id()` read off the OK packet, and what `Eloquent::create()`
/// relies on to know a just-inserted row's id) follows real MySQL's rule,
/// verified against a real MySQL 8.4 instance rather than assumed: the
/// *first* row whose AUTO_INCREMENT column was actually generated (not
/// supplied), or — only when no row in the statement generated one — the
/// *last* row's explicitly-supplied value. A single-row `INSERT` that
/// supplies its own id (e.g. a factory pre-assigning `id` before `create()`)
/// is the common case this exists for: with only "the first generated
/// value" tracked (as fsdb used to), that row's `last_insert_id` came back
/// 0 instead of the id it was actually given, and every caller reading it
/// back (`Eloquent`'s own model, here) silently got a wrong id instead.
///
/// That OK-packet value is also returned separately as `generatedId` — the
/// first *actually generated* id, or `None` if every row supplied its own —
/// because the SQL function `LAST_INSERT_ID()` has a narrower rule than the
/// OK packet: it only ever reflects a generated id, never an explicitly
/// supplied one, and holds its previous value across a statement that
/// generated none at all (see `QueryHandler`'s `LAST_INSERT_ID` doc).
let private insertCore
    (checkFks: bool)
    (strict: bool)
    (ignoreErrors: bool)
    (db: Database)
    (tableKey: string)
    (rowsIn: Value list list)
    (idxs: int list)
    : Result<Database * (int64 * int64 option * int * Value[] list), StorageError> =
    let table = Map.find tableKey db
    let uniqueGroups = uniqueKeyGroups table

    // Parent keys are immutable for the duration of this INSERT (except a
    // self-FK, see below). Build one compact lookup per ordinary FK instead
    // of rescanning its parent table for every child candidate. A
    // non-self FK whose target columns are a full PK/UNIQUE group reuses
    // that group's already-maintained `UniqueIndex` (`parentUniqueIndex`) —
    // O(log n) per probe, no per-statement scan of the parent at all.
    // A self-FK still needs `constraintLookup`'s mutable `HashSet`: its
    // parent IS this table, and a multi-row INSERT must see rows accepted
    // earlier in the same statement, which only the mutable path extends
    // as it goes (the `Add` loop below). The same HashSet fallback covers
    // an FK whose target columns aren't a full PK/UNIQUE group.
    let foreignKeyLookups =
        if not checkFks then
            Map.empty
        else
            table.ForeignKeys
            |> List.choose (fun foreignKey ->
                match
                    foreignKey.Columns |> traverse (resolveColumn table.Columns),
                    db |> Map.tryFind (normalizeTableName foreignKey.RefTable)
                with
                | Ok childIndices, Some parent ->
                    match foreignKey.RefColumns |> traverse (resolveColumn parent.Columns) with
                    | Ok parentIndices ->
                        let isSelf = normalizeTableName foreignKey.RefTable = tableKey
                        let selfParentIndices = if isSelf then Some parentIndices else None

                        let source =
                            if isSelf then
                                Mutable(constraintLookup parent.Columns parentIndices parent.RowsArray)
                            else
                                match parentUniqueIndex parent parentIndices with
                                | Some idx -> Fixed idx
                                | None -> Mutable(constraintLookup parent.Columns parentIndices parent.RowsArray)

                        Some(foreignKey.Name, (childIndices, selfParentIndices, source))
                    | Error _ -> None
                | _ -> None)
            |> Map.ofList

    let hasUnacceleratedSelfForeignKey =
        table.ForeignKeys
        |> List.exists (fun foreignKey ->
            normalizeTableName foreignKey.RefTable = tableKey
            && not (foreignKeyLookups |> Map.containsKey foreignKey.Name))

    let step acc (rowValues: Value list) =
        acc
        |> Result.bind (fun (acceptedRev: Value[] list, nextAutoId, firstAuto, lastExplicit, index: Map<string, Map<string, int>>) ->
            if List.length rowValues <> List.length idxs then
                Error(ColumnCountMismatch(List.length idxs, List.length rowValues))
            else
                let provided = List.zip idxs rowValues |> Map.ofList
                let rawRow = table.Columns |> List.mapi (fun i _ -> Map.tryFind i provided)

                let rowResult =
                    processRow strict nextAutoId rawRow table.Columns
                    |> Result.bind (fun (finalValues, nextAutoId', assigned) ->
                        let candidate = Array.ofList finalValues

                        // O(log n) per unique group via the running index
                        // (seeded from `table.UniqueIndex`, extended below as
                        // each candidate is accepted) instead of a full scan
                        // of `table.RowsArray` per candidate.
                        let uniqueCollision =
                            uniqueGroups
                            |> List.tryPick (fun (name, indices) ->
                                match encodeConstraintKey table.Columns indices candidate with
                                | Some key when Map.find name index |> Map.containsKey key ->
                                    let value =
                                        indices
                                        |> List.map (fun index -> candidate.[index] |> toText |> Option.defaultValue "NULL")
                                        |> String.concat "-"

                                    Some(DuplicateKey(name, value))
                                | _ -> None)

                        match uniqueCollision with
                        | Some e -> Error e
                        | None ->
                            if checkFks then
                                // A self-referencing (or otherwise
                                // same-table) FK's parent needs to see this
                                // same multi-row INSERT's earlier rows too,
                                // not just what was already committed before
                                // the statement started — same reasoning as
                                // the running unique-key `index` just above.
                                // Ordinary parent tables need no overlay.
                                // Only a self-FK needs rows accepted earlier
                                // in this statement made visible, in their
                                // original insertion order.
                                let dbView =
                                    if hasUnacceleratedSelfForeignKey && not acceptedRev.IsEmpty then
                                        Map.add tableKey { table with RowsArray = table.RowsArray.AddRange(List.rev acceptedRev) } db
                                    else
                                        db

                                let checkOneForeignKey foreignKey =
                                    match Map.tryFind foreignKey.Name foreignKeyLookups with
                                    | Some(childIndices, _, parentKeys) ->
                                        match encodeConstraintKey table.Columns childIndices candidate with
                                        | None -> Ok()
                                        | Some key when parentKeySourceContains key parentKeys -> Ok()
                                        | Some _ -> Error(ForeignKeyParentMissing foreignKey.Name)
                                    | None -> checkFkParent dbView table.Columns candidate foreignKey

                                table.ForeignKeys
                                |> traverse checkOneForeignKey
                                |> Result.map (fun _ -> candidate, nextAutoId', assigned)
                            else
                                Ok(candidate, nextAutoId', assigned))

                match rowResult with
                | Ok(candidate, nextAutoId', assigned) ->
                    let firstAuto', lastExplicit' =
                        match assigned with
                        | Some(true, v) -> Option.orElse (Some v) firstAuto, lastExplicit
                        | Some(false, v) -> firstAuto, Some v
                        | None -> firstAuto, lastExplicit

                    for KeyValue(_, (_, selfParentIndices, lookup)) in foreignKeyLookups do
                        selfParentIndices
                        |> Option.bind (fun indices -> encodeConstraintKey table.Columns indices candidate)
                        |> Option.iter (fun key -> parentKeySourceAdd key lookup)

                    // Prepending is O(1); reverse once after the fold so
                    // externally observable insertion and commit-event
                    // order remains unchanged. `candidate` lands at
                    // `table.RowsArray.Length + (rows already accepted this
                    // statement)` — every earlier row already occupies its
                    // own slot, appended rows never shift an existing one.
                    let position = table.RowsArray.Length + List.length acceptedRev
                    Ok(candidate :: acceptedRev, nextAutoId', firstAuto', lastExplicit', reindexRow table.Columns uniqueGroups None (Some(position, candidate)) index)
                | Error _ when ignoreErrors -> Ok(acceptedRev, nextAutoId, firstAuto, lastExplicit, index)
                | Error e -> Error e)

    rowsIn
    |> List.fold step (Ok([], table.NextAutoId, None, None, table.UniqueIndex))
    |> Result.map (fun (acceptedRev, nextAutoId', firstAuto, lastExplicit, index) ->
        let accepted = List.rev acceptedRev
        let firstAssigned = Option.orElse lastExplicit firstAuto
        // A single `Array.Copy`-backed append (`ImmutableArray.AddRange`),
        // not an O(existing table size) `list` rebuild — the unique/FK
        // checks above are already O(log n) per row; this was the last O(n)
        // step a single-row INSERT paid.
        let table' = { table with RowsArray = table.RowsArray.AddRange accepted; NextAutoId = nextAutoId'; UniqueIndex = index }
        Map.add tableKey table' db, (Option.defaultValue 0L firstAssigned, firstAuto, List.length accepted, accepted))

/// Inserts rows built from `columns` and matching value lists, applying
/// defaults, AUTO_INCREMENT assignment, NOT NULL/type-coercion checks, and
/// — new here — unique-key (error 1062) and, when `store.ForeignKeyChecks`
/// is set, foreign-key parent-existence (error 1452) checks. Returns
/// `(lastInsertId, generatedId, affected row count)`; `lastInsertId` is the
/// OK-packet value (see `insertCore`'s doc), `generatedId` is `None` unless
/// this statement actually generated an AUTO_INCREMENT id. Fails the whole
/// statement on the first bad row — see `insertRowsIgnore` for `INSERT
/// IGNORE`'s per-row skip semantics.
let insertRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    : Result<int64 * int64 option * int, StorageError> =
    let key = normalizeTableName tableName

    let result =
        withDatabase store dbName (fun db ->
            tryGetTable db tableName
            |> Result.bind (fun table ->
                resolveInsertColumns table columns
                |> Result.bind (insertCore store.ForeignKeyChecks store.StrictMode false db key rowsIn)))

    match result with
    | Ok(lastId, generatedId, affected, rows) ->
        if not rows.IsEmpty then
            emit store (Some(RowsInserted(dbName, tableName, rows)))

        Ok(lastId, generatedId, affected)
    | Error e -> Error e

/// `INSERT IGNORE`: as `insertRows`, but a row that would violate NOT
/// NULL/unique/foreign-key constraints is skipped instead of failing the
/// statement — MySQL downgrades the error to a warning per row. The
/// returned affected count is only the rows actually inserted;
/// `lastInsertId`/`generatedId` follow the same rule as `insertRows` (`None`
/// if every row was skipped or none generated an id).
let insertRowsIgnore
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    : Result<int64 * int64 option * int, StorageError> =
    let key = normalizeTableName tableName

    let result =
        withDatabase store dbName (fun db ->
            tryGetTable db tableName
            |> Result.bind (fun table ->
                resolveInsertColumns table columns
                |> Result.bind (insertCore store.ForeignKeyChecks store.StrictMode true db key rowsIn)))

    match result with
    | Ok(lastId, generatedId, affected, rows) ->
        if not rows.IsEmpty then
            emit store (Some(RowsInserted(dbName, tableName, rows)))

        Ok(lastId, generatedId, affected)
    | Error e -> Error e

/// Every `(childTableKey, fk)` in `db` whose `fk.RefTable` is `parentKey` —
/// every foreign key elsewhere in the database that a delete from
/// `parentKey` needs to check. Same-database only: `Ast.ForeignKeyDef`
/// carries no database qualifier, so a cross-database FK (rare even in
/// MySQL, and not something Laravel migrations emit) isn't found here.
let private referencingForeignKeys (db: Database) (parentKey: string) : (string * ForeignKeyDef) list =
    db
    |> Map.toList
    |> List.collect (fun (childKey, childTbl) ->
        childTbl.ForeignKeys
        |> List.filter (fun fk -> normalizeTableName fk.RefTable = parentKey)
        |> List.map (fun fk -> childKey, fk))

/// Whether rewriting `tableKey`'s `oldRow` into `newRow` would orphan a
/// child row elsewhere in `db` — `updateRows`'s parent-side counterpart to
/// `checkFkParents`'s child-side check, using the same `referencingForeignKeys`
/// `cascadeDelete` uses for `DELETE`. Only relevant when the update actually
/// changes a column some other table's FK references at all: most `UPDATE`s
/// never touch the referenced key, so this is a no-op the moment `oldKey =
/// newKey`. Always RESTRICT (error 1451) rather than dispatching on the FK's
/// `OnUpdate` clause — this engine doesn't move/blank child rows on UPDATE
/// (unlike `cascadeDelete`'s DELETE-side CASCADE/SET NULL), so refusing the
/// update is the safe default even for a `ON UPDATE CASCADE` FK; upgrade to
/// real cascading UPDATE if a migration ever depends on it. Also
/// `upsertRows`' parent-side counterpart, for the same reason.
let private checkNotOrphaning (db: Database) (tableKey: string) (parentColumns: ColumnDef list) (oldRow: Value[]) (newRow: Value[]) : Result<unit, StorageError> =
    let checkOne (childKey: string, fk: ForeignKeyDef) =
        match fk.RefColumns |> traverse (resolveColumn parentColumns) with
        | Error _ -> Ok() // stale FK metadata — see `checkFkParents`'s note.
        | Ok refIdxs ->
            let oldKey = refIdxs |> List.map (fun i -> oldRow.[i])
            let newKey = refIdxs |> List.map (fun i -> newRow.[i])

            if oldKey = newKey || oldKey |> List.exists ((=) VNull) then
                Ok()
            else
                match Map.tryFind childKey db with
                | None -> Ok()
                | Some childTbl ->
                    match fk.Columns |> traverse (resolveColumn childTbl.Columns) with
                    | Error _ -> Ok()
                    | Ok childIdxs ->
                        let stillReferenced =
                            childTbl.RowsArray
                            |> Seq.exists (fun row -> List.forall2 (fun i v -> compare row.[i] v = 0) childIdxs oldKey)

                        if stillReferenced then Error(ForeignKeyRestrict fk.Name) else Ok()

    referencingForeignKeys db tableKey |> traverse checkOne |> Result.map ignore

/// `INSERT ... ON DUPLICATE KEY UPDATE`: like `insertRows`, but a candidate
/// row that collides with an existing row on any unique key or the primary
/// key is applied to `applyUpdate existingRow candidateRow` instead of being
/// appended. Collision detection goes through the same `UniqueIndex`
/// (collation-aware via `encodeConstraintKey`) as plain `INSERT`'s unique
/// check.
let rec upsertRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    (computeGenerated: Value[] -> Result<Value[], StorageError>)
    (applyUpdate: Value[] -> Value[] -> Result<Value[], StorageError>)
    : Result<int64 * int64 option * int, StorageError> =
        let key = normalizeTableName tableName

        let result =
            withDatabase store dbName (fun db ->
                tryGetTable db tableName
                |> Result.bind (fun table -> upsertRowsInTable store db key table columns rowsIn computeGenerated applyUpdate)
                |> Result.map (fun (table', result) -> Map.add key table' db, result))

        match result with
        | Ok(lastId, generatedId, affected, inserted, updated) ->
            if not inserted.IsEmpty then
                emit store (Some(RowsInserted(dbName, tableName, inserted)))

            if not updated.IsEmpty then
                emit store (Some(RowsUpdated(dbName, tableName, updated)))

            Ok(lastId, generatedId, affected)
        | Error e -> Error e

/// `upsertRows`'s per-table body, pulled out only so it can take `db` (needed
/// for `checkFkParents`/`checkNotOrphaning`, the same FK enforcement
/// `insertRows`/`updateRows` apply) alongside `table`, which `withTable`
/// alone doesn't expose.
and private upsertRowsInTable
    (store: Store)
    (db: Database)
    (key: string)
    (table: Table)
    (columns: string list option)
    (rowsIn: Value list list)
    (computeGenerated: Value[] -> Result<Value[], StorageError>)
    (applyUpdate: Value[] -> Value[] -> Result<Value[], StorageError>)
    : Result<Table * (int64 * int64 option * int * Value[] list * (Value[] * Value[]) list), StorageError> =
                let checkFks = store.ForeignKeyChecks

                let indices =
                    match columns with
                    | None -> Ok [ 0 .. table.Columns.Length - 1 ]
                    | Some names -> names |> traverse (resolveColumn table.Columns)

                indices
                |> Result.bind (fun idxs ->
                    let uniqueGroups = uniqueKeyGroups table

                    // A statement-local working copy of the table's existing
                    // rows, rewritten in place as `ON DUPLICATE KEY UPDATE`
                    // matches land — `current.[pos]` is then an O(1) "what
                    // does this row hold right now" read/write instead of an
                    // O(table size) `List.map`/`List.item` per matched
                    // candidate. Never shared or exposed outside this
                    // function; the fold below still reports every actual
                    // change (`updated`/`inserted`) as pure `Result` data, so
                    // callers see nothing of the mutation.
                    let current : Value[][] = table.RowsArray |> Seq.toArray
                    let newRows = ResizeArray<Value[]>()

                    // The running index (seeded from `table.UniqueIndex`,
                    // rekeyed after every matched/inserted candidate) finds
                    // the one row (if any) sharing a key with `candidate` in
                    // O(log n) per group instead of scanning `current`/
                    // `newRows` — a later candidate in the same batch still
                    // sees an earlier one's rewrite/insert, since both go
                    // through the same rekeying. A position `>= current.Length`
                    // is a row this same batch just inserted.
                    let findMatch (index: Map<string, Map<string, int>>) (candidate: Value[]) : (int * Value[]) option =
                        uniqueGroups
                        |> List.tryPick (fun (name, idxs) ->
                            encodeConstraintKey table.Columns idxs candidate
                            |> Option.bind (fun k -> Map.tryFind k (Map.find name index))
                            |> Option.map (fun pos -> pos, (if pos < current.Length then current.[pos] else newRows.[pos - current.Length])))

                    let step acc (rowValues: Value list) =
                        acc
                        |> Result.bind
                            (fun (nextAutoId,
                                  firstAuto,
                                  lastExplicit,
                                  affected,
                                  inserted: Value[] list,
                                  updated: (Value[] * Value[]) list,
                                  index: Map<string, Map<string, int>>) ->
                                if List.length rowValues <> List.length idxs then
                                    Error(ColumnCountMismatch(List.length idxs, List.length rowValues))
                                else
                                    let provided = List.zip idxs rowValues |> Map.ofList
                                    let rawRow = table.Columns |> List.mapi (fun i _ -> Map.tryFind i provided)

                                    processRow store.StrictMode nextAutoId rawRow table.Columns
                                    |> Result.bind (fun (finalValues, nextAutoId', assigned) ->
                                        // A unique index over a *generated* column (e.g.
                                        // Laravel Pulse's `key_hash BINARY(16) AS
                                        // (unhex(md5(key)))`) is still NULL in the raw
                                        // candidate at this point — `computeGenerated`
                                        // fills it in before `findMatch` runs, so ON
                                        // DUPLICATE KEY UPDATE actually finds the
                                        // collision instead of degrading into a plain
                                        // INSERT that then trips the unique check.
                                        computeGenerated (Array.ofList finalValues)
                                        |> Result.map (fun candidate -> candidate, findMatch index candidate)
                                        |> Result.bind (function
                                            | candidate, Some(pos, existing) ->
                                                applyUpdate existing candidate
                                                |> Result.bind (fun applied ->
                                                    // Same FK enforcement `updateRows` applies:
                                                    // `applied`'s own foreign keys need a live
                                                    // parent (child-side), and if this rewrite
                                                    // changed a column some *other* table's FK
                                                    // references, it can't orphan an existing
                                                    // child (parent-side).
                                                    (if checkFks then
                                                         checkFkParents db table.Columns table.ForeignKeys applied
                                                         |> Result.bind (fun () -> checkNotOrphaning db key table.Columns existing applied)
                                                     else
                                                         Ok())
                                                    |> Result.map (fun () ->
                                                        if pos < current.Length then
                                                            current.[pos] <- applied
                                                        else
                                                            newRows.[pos - current.Length] <- applied

                                                        nextAutoId',
                                                        firstAuto,
                                                        lastExplicit,
                                                        affected + 1,
                                                        inserted,
                                                        (existing, applied) :: updated,
                                                        reindexRow table.Columns uniqueGroups (Some existing) (Some(pos, applied)) index))
                                            | candidate, None ->
                                                // Same FK-parent check `insertCore` applies to
                                                // a plain `INSERT`'s new rows — this candidate
                                                // didn't collide with anything, so it's really
                                                // an insert.
                                                (if checkFks then
                                                     checkFkParents db table.Columns table.ForeignKeys candidate
                                                 else
                                                     Ok())
                                                |> Result.map (fun () ->
                                                    // Same "first generated, else last explicit"
                                                    // `last_insert_id` rule `insertCore` uses —
                                                    // see its doc.
                                                    let firstAuto', lastExplicit' =
                                                        match assigned with
                                                        | Some(true, v) -> Option.orElse (Some v) firstAuto, lastExplicit
                                                        | Some(false, v) -> firstAuto, Some v
                                                        | None -> firstAuto, lastExplicit

                                                    let position = current.Length + newRows.Count
                                                    newRows.Add candidate

                                                    nextAutoId',
                                                    firstAuto',
                                                    lastExplicit',
                                                    affected + 1,
                                                    candidate :: inserted,
                                                    updated,
                                                    reindexRow table.Columns uniqueGroups None (Some(position, candidate)) index))))

                    rowsIn
                    |> List.fold step (Ok(table.NextAutoId, None, None, 0, [], [], table.UniqueIndex))
                    |> Result.map (fun (nextAutoId', firstAuto, lastExplicit, affected, inserted, updated, index) ->
                        let finalRows =
                            if newRows.Count = 0 then
                                ImmutableArray.CreateRange current
                            else
                                let builder = ImmutableArray.CreateBuilder(current.Length + newRows.Count)
                                builder.AddRange current
                                builder.AddRange newRows
                                builder.MoveToImmutable()

                        { table with RowsArray = finalRows; NextAutoId = nextAutoId'; UniqueIndex = index },
                        (Option.defaultValue 0L (Option.orElse lastExplicit firstAuto), firstAuto, affected, List.rev inserted, List.rev updated)))

let private coerceRow (strict: bool) (columns: ColumnDef list) (row: Value[]) : Result<Value[], StorageError> =
    List.zip columns (Array.toList row)
    |> traverse (fun (col, v) -> coerceAndCheck strict col v)
    |> Result.map Array.ofList

/// Deletes `toDelete` (rows already known to belong to `tableKey`, e.g. from
/// `deleteRows`'s WHERE match) from `db`, applying every other table's
/// referencing foreign keys' `OnDelete` action first: `CASCADE` recurses
/// (deleting a parent whose children are themselves parents cascades all
/// the way down), `SET NULL` blanks the child's FK columns, and anything
/// else — `RESTRICT`, `NO ACTION`, or no `ON DELETE` clause at all, all
/// three of which MySQL treats the same way, an immediate check rather than
/// a deferred one — fails the whole delete with error 1451 the moment any
/// matching child row exists. `checkFks = false` (`SET FOREIGN_KEY_CHECKS =
/// 0`) skips all of this and just removes the rows, leaving any children
/// dangling, same as MySQL. Because every step here returns a *new*
/// `Database` rather than mutating one in place, an `Error` partway through
/// (a `RESTRICT` hit on the third referencing table, say) discards
/// everything already computed — `deleteRows`/`withDatabase` only ever
/// commits an `Ok` result, so this is all-or-nothing per statement without
/// needing its own rollback logic.
/// `cascadeDelete`'s real recursion, threading `visited` — every row already
/// scheduled for deletion in this call tree, per table — so a cyclic
/// `CASCADE` (two tables' foreign keys pointing at each other, or a
/// self-referencing one) can't re-discover a row it already scheduled and
/// recurse forever: once a row is in `visited` for its table, a later
/// `matching` set that rediscovers it drops it before recursing, so the
/// cascade's frontier shrinks every call instead of oscillating between the
/// same two rows and StackOverflow-crashing the whole (uncatchable in .NET)
/// process.
let rec private cascadeDeleteVisited
    (checkFks: bool)
    (db: Database)
    (visited: Map<string, Value[] list>)
    (blanked: Map<string, (Value[] * Value[]) list>)
    (tableKey: string)
    (toDelete: Value[] list)
    : Result<Database * Map<string, Value[] list> * Map<string, (Value[] * Value[]) list>, StorageError> =
    let alreadyVisited = visited |> Map.tryFind tableKey |> Option.defaultValue []
    let toDelete = toDelete |> List.filter (fun row -> not (alreadyVisited |> List.exists ((=) row)))

    // Removes one row per entry in `toDelete`, not every structurally-equal
    // row: two identical rows are distinct rows, and a `DELETE ... LIMIT n`
    // (or a cascaded child match) may legitimately match only one of them.
    // Compacts rather than tombstoning — every row after a deleted one shifts
    // down one slot, so `UniqueIndex`'s positions are rebuilt wholesale
    // (`reindexTable`) instead of patched incrementally; `Rows.Length` stays
    // the table's true row count for every other reader (`information_schema`,
    // `Persistence`'s snapshot) instead of splitting into a logical vs.
    // physical length.
    let removeFrom (d: Database) =
        let t = Map.find tableKey d

        let kept, _ =
            t.RowsArray
            |> Seq.fold
                (fun (kept, pending) row ->
                    match pending |> List.tryFindIndex ((=) row) with
                    | Some i -> kept, List.removeAt i pending
                    | None -> row :: kept, pending)
                ([], toDelete)

        Map.add tableKey (reindexTable { t with RowsArray = List.rev kept |> ImmutableArray.CreateRange }) d

    if toDelete.IsEmpty then
        Ok(db, visited, blanked)
    else
        let visited = visited |> Map.add tableKey (alreadyVisited @ toDelete)

        if not checkFks then
            Ok(removeFrom db, visited, blanked)
        else
            let table = Map.find tableKey db

            let applyChild acc (childKey: string, fk: ForeignKeyDef) =
                acc
                |> Result.bind (fun (d, visited, blanked) ->
                    let childTbl = Map.find childKey d

                    match fk.Columns |> traverse (resolveColumn childTbl.Columns), fk.RefColumns |> traverse (resolveColumn table.Columns) with
                    | Error _, _
                    | _, Error _ -> Ok(d, visited, blanked) // stale FK metadata — see `checkFkParents`'s note.
                    | Ok childIdxs, Ok refIdxs ->
                        let parentKeys = toDelete |> List.map (fun row -> refIdxs |> List.map (fun i -> row.[i]))

                        let isChild (row: Value[]) =
                            let key = childIdxs |> List.map (fun i -> row.[i])

                            key |> List.forall ((<>) VNull)
                            && parentKeys |> List.exists (List.forall2 (fun a b -> compare a b = 0) key)

                        let matching = childTbl.RowsArray |> Seq.filter isChild |> List.ofSeq

                        if matching.IsEmpty then
                            Ok(d, visited, blanked)
                        else
                            match fk.OnDelete |> Option.map (fun s -> s.Trim().ToUpperInvariant()) with
                            | Some "CASCADE" -> cascadeDeleteVisited checkFks d visited blanked childKey matching
                            | Some "SET NULL" ->
                                // A `NOT NULL` FK column can't actually be
                                // blanked — real MySQL refuses to create
                                // such a constraint at all (error 1215);
                                // this engine doesn't validate DDL that
                                // strictly, so the equivalent check happens
                                // here instead, failing the delete (1048)
                                // rather than silently writing a `NULL` no
                                // INSERT/UPDATE could ever produce.
                                match childIdxs |> List.tryFind (fun i -> not childTbl.Columns.[i].Nullable) with
                                | Some i -> Error(NotNullViolation childTbl.Columns.[i].Name)
                                | None ->
                                    let childGroups = uniqueKeyGroups childTbl

                                    // Blanking rewrites a row's values in place — unlike a
                                    // delete, every row keeps its position, so the index can
                                    // still be rekeyed incrementally instead of a full rebuild.
                                    // `changes` pairs each blanked row's before/after values —
                                    // the WAL needs the exact same `RowsUpdated` shape a plain
                                    // `UPDATE` reports, or replay resurrects the pre-blank FK
                                    // value.
                                    let blankedRows, changes, index =
                                        childTbl.RowsArray
                                        |> Seq.indexed
                                        |> Seq.fold
                                            (fun (rows, changes, index) (pos, row) ->
                                                if isChild row then
                                                    let row' = Array.copy row
                                                    childIdxs |> List.iter (fun i -> row'.[i] <- VNull)
                                                    row' :: rows, (row, row') :: changes, reindexRow childTbl.Columns childGroups (Some row) (Some(pos, row')) index
                                                else
                                                    row :: rows, changes, index)
                                            ([], [], childTbl.UniqueIndex)

                                    let blanked =
                                        blanked
                                        |> Map.add childKey ((blanked |> Map.tryFind childKey |> Option.defaultValue []) @ List.rev changes)

                                    Ok(Map.add childKey { childTbl with RowsArray = List.rev blankedRows |> ImmutableArray.CreateRange; UniqueIndex = index } d, visited, blanked)
                            | _ -> Error(ForeignKeyRestrict fk.Name))

            referencingForeignKeys db tableKey
            |> List.fold applyChild (Ok(db, visited, blanked))
            |> Result.map (fun (d, visited, blanked) -> removeFrom d, visited, blanked)

/// As `cascadeDeleteVisited`, seeded with empty `visited`/`blanked` — its
/// second return value is every row actually removed, by table key,
/// including `tableKey` itself and every table a `CASCADE` reached, for
/// `deleteRows` to report as `RowsDeleted` events; its third is every
/// `ON DELETE SET NULL` blanked row's before/after values, by table key, for
/// `deleteRows` to report as `RowsUpdated` events the same way a plain
/// `UPDATE` would.
let private cascadeDelete
    (checkFks: bool)
    (db: Database)
    (tableKey: string)
    (toDelete: Value[] list)
    : Result<Database * Map<string, Value[] list> * Map<string, (Value[] * Value[]) list>, StorageError> =
    cascadeDeleteVisited checkFks db Map.empty Map.empty tableKey toDelete

/// Deletes every row matching `predicate`. Returns the number of rows
/// removed. `predicate` returns a `Result` rather than a plain `bool` so a
/// per-row WHERE-evaluation failure (not reachable today — every `Value`
/// operation is total — but a real possibility once functions that can
/// fail per row land) surfaces as an `Error` instead of silently being
/// treated as "didn't match". When `store.ForeignKeyChecks` is set (the
/// default), applies every referencing foreign key's `ON DELETE` action —
/// see `cascadeDelete`.
///
/// ponytail: `predicate` always runs against every row of `table.RowsArray` —
/// unlike `SELECT` (`Executor.tryPointLookup`), a `WHERE <PK/UNIQUE col> =
/// <literal>` DELETE never narrows to `UniqueIndex`'s O(log n) candidates
/// first, so it's still O(table) even for a single-row delete by id. Route
/// `predicate` through `tryUniqueLookup`-style narrowing (same "pure
/// narrowing, never a correctness risk" shape `tryPointLookup` uses) once
/// DELETE latency on a large table actually matters.
let deleteRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (predicate: Value[] -> Result<bool, StorageError>)
    : Result<int, StorageError> =
    let result =
        withDatabase store dbName (fun db ->
            let key = normalizeTableName tableName

            tryGetTable db tableName
            |> Result.bind (fun table ->
                table.RowsArray
                |> List.ofSeq
                |> traverse (fun row -> predicate row |> Result.map (fun keep -> keep, row))
                |> Result.bind (fun flagged ->
                    let toDelete = flagged |> List.filter fst |> List.map snd

                    cascadeDelete store.ForeignKeyChecks db key toDelete
                    |> Result.map (fun (db', removed, blanked) -> db', (toDelete.Length, db, removed, blanked)))))

    match result with
    | Ok(affected, db, removed, blanked) ->
        let originalNameOf tableKey = db |> Map.tryFind tableKey |> Option.map (fun t -> t.OriginalName) |> Option.defaultValue tableKey

        removed
        |> Map.iter (fun tableKey rows -> if not rows.IsEmpty then emit store (Some(RowsDeleted(dbName, originalNameOf tableKey, rows))))

        // `ON DELETE SET NULL`'s blanked child rows — same `RowsUpdated`
        // shape a plain `UPDATE` reports, so replay rewrites them the same
        // way instead of a WAL replay resurrecting the pre-blank FK value
        // (see `cascadeDeleteVisited`'s doc).
        blanked
        |> Map.iter (fun tableKey changes -> if not changes.IsEmpty then emit store (Some(RowsUpdated(dbName, originalNameOf tableKey, changes))))

        Ok affected
    | Error e -> Error e

/// Replaces every row matching `predicate` with `updater row`, coercing the
/// result back to the table's column types, then checking it against the
/// table's unique keys (error 1062, against every *other* row — a no-op
/// `UPDATE` that leaves a row's own unique value unchanged doesn't collide
/// with itself) and, when `store.ForeignKeyChecks` is set, its foreign
/// keys' parents (error 1452). Returns the number of rows actually
/// *changed* — matching but no-op writes (`SET v = v`) don't count, matching
/// MySQL's "Changed: n" rather than "Rows matched: n" — via `Value[]`'s
/// structural equality (F# arrays compare structurally, element by
/// element). As with `deleteRows`, `predicate` and `updater` both return
/// `Result` rather than defaulting a failure away.
///
/// `candidates`, when given, is the exact `(position, row)` set to visit —
/// `Executor`'s point-lookup narrowing (`tryPointLookup`) already resolved
/// these via the PK/UNIQUE index, so this fold doesn't re-scan `table.RowsArray`
/// at all to find them; `predicate` still re-checks each one for
/// correctness (this is a pure narrowing to a superset of the real WHERE
/// match, same discipline `tryPointLookup` documents), so it never pays for
/// rows that weren't candidates. `None` (a WHERE that didn't narrow, or
/// none at all) falls back to visiting every row of `table.RowsArray`,
/// `predicate` deciding which ones qualify — the rewrite lands in a
/// `Builder` (`Rows.ToBuilder()`, one `Array.Copy`) touched only at the
/// positions that actually change, instead of a full `list` rebuild
/// threaded through the whole fold.
let updateRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (candidates: (int * Value[]) list option)
    (predicate: Value[] -> Result<bool, StorageError>)
    (updater: Value[] -> Result<Value[], StorageError>)
    : Result<int, StorageError> =
        let result =
            withDatabase store dbName (fun db ->
                let key = normalizeTableName tableName

                tryGetTable db tableName
                |> Result.bind (fun table ->
                    let uniqueGroups = uniqueKeyGroups table
                    let checkFks = store.ForeignKeyChecks
                    // Statement-local; discarded (never `DrainToImmutable`d)
                    // if the fold below ends in `Error`, so a mid-statement
                    // failure never surfaces a partial rewrite — same
                    // all-or-nothing guarantee the old cons-then-reverse fold
                    // gave, just backed by a mutable builder instead of an
                    // immutable accumulator.
                    let builder = table.RowsArray.ToBuilder()

                    // `index` mirrors `table.UniqueIndex`, rekeyed one row at
                    // a time (by its `Rows` position, stable across this
                    // fold — only values change, not row order) as rows are
                    // rewritten, so a later row's collision check still sees
                    // this same statement's earlier rewrites.
                    let step acc (rowPos, row) =
                        acc
                        |> Result.bind (fun (changesRev: (Value[] * Value[]) list, index: Map<string, Map<string, int>>) ->
                            // `rowPos` may have been captured by a lock-free
                            // point lookup outside any lock
                            // (`tryUniqueLookup`, via `Executor.tryPointLookup`)
                            // — a concurrent write since then (a DELETE
                            // compacting an earlier row, say) can have shifted
                            // `row` to a different slot, or removed it
                            // entirely. Re-locate it by reference identity
                            // under this call's lock before writing anything
                            // at `rowPos`: still there, the fast path stands;
                            // moved, a full-array identity scan finds where;
                            // gone, there's nothing left to update — the same
                            // outcome `predicate` returning false for a row it
                            // no longer sees would give, rather than silently
                            // clobbering whatever unrelated row now occupies
                            // that slot.
                            let rowPos =
                                if rowPos < builder.Count && obj.ReferenceEquals(builder.[rowPos], row) then
                                    Some rowPos
                                else
                                    seq { 0 .. builder.Count - 1 } |> Seq.tryFind (fun i -> obj.ReferenceEquals(builder.[i], row))

                            match rowPos with
                            | None -> Ok(changesRev, index)
                            | Some rowPos ->
                                predicate row
                                |> Result.bind (fun keep ->
                                    if not keep then
                                        Ok(changesRev, index)
                                    else
                                        updater row
                                        |> Result.bind (coerceRow store.StrictMode table.Columns)
                                        |> Result.bind (fun newRow ->
                                            // A group's key only collides against
                                            // some *other* row still holding it —
                                            // `row`'s own position (about to be
                                            // rekeyed below) doesn't count.
                                            let collision =
                                                uniqueGroups
                                                |> List.tryPick (fun (name, idxs) ->
                                                    match encodeConstraintKey table.Columns idxs newRow with
                                                    | Some k ->
                                                        match Map.tryFind k (Map.find name index) with
                                                        | Some pos when pos <> rowPos ->
                                                            let value = idxs |> List.map (fun i -> newRow.[i] |> toText |> Option.defaultValue "NULL") |> String.concat "-"
                                                            Some(DuplicateKey(name, value))
                                                        | _ -> None
                                                    | None -> None)

                                            match collision with
                                            | Some e -> Error e
                                            | None ->
                                                (if checkFks then
                                                     checkFkParents db table.Columns table.ForeignKeys newRow
                                                     |> Result.bind (fun () -> checkNotOrphaning db key table.Columns row newRow)
                                                     |> Result.map (fun () -> newRow)
                                                 else
                                                     Ok newRow)
                                                |> Result.map (fun newRow -> newRow, reindexRow table.Columns uniqueGroups (Some row) (Some(rowPos, newRow)) index))
                                        |> Result.map (fun (newRow, index') ->
                                            builder.[rowPos] <- newRow
                                            (if newRow <> row then (row, newRow) :: changesRev else changesRev), index')))

                    candidates
                    |> Option.defaultWith (fun () -> table.RowsArray |> Seq.indexed |> List.ofSeq)
                    |> List.fold step (Ok([], table.UniqueIndex))
                    // `DrainToImmutable`, not `MoveToImmutable`: the latter
                    // demands Count = Capacity, which an empty table's
                    // builder (capacity-8, count-0) never satisfies.
                    |> Result.map (fun (changesRev, index) -> Map.add key { table with RowsArray = builder.DrainToImmutable(); UniqueIndex = index } db, List.rev changesRev)))

        match result with
        | Ok changes ->
            if not changes.IsEmpty then
                emit store (Some(RowsUpdated(dbName, tableName, changes)))

            Ok changes.Length
        | Error e -> Error e

/// Per-snapshot memo of `RowsArray` as a `Value[] list`, keyed by the
/// `Table` instance itself: `Executor`'s row pipeline is list-based and
/// materializes every scan with `List.ofSeq`, which is free when the seq
/// already IS a list (FSharp.Core's fast path) but an O(row count) cons
/// rebuild per query against a bare `ImmutableArray`. Caching the list per
/// table version restores the free path for repeated scans of an unchanged
/// table; a write swaps in a new `Table` instance, so its entry starts
/// fresh and the old one dies with the old snapshot (weak keys, no
/// lifetime management needed). Thread-safe per `ConditionalWeakTable`'s
/// own contract.
let private rowsListCache = System.Runtime.CompilerServices.ConditionalWeakTable<Table, Value[] list>()

let private rowsList (table: Table) : Value[] list =
    rowsListCache.GetValue(table, fun t -> List.ofSeq t.RowsArray)

/// A snapshot read: the table's columns and its rows as they were at the
/// moment of the call. Lock-free — reads `dbName`'s own slot directly (not
/// the whole-catalog `Store.Catalog` view, which would pay an O(number of
/// databases) rebuild on every single SELECT), and later writes swap in a
/// new `Database` for that slot without mutating this snapshot's row list.
/// The rows come back as the raw array-backed seq, never materialized —
/// several callers (`Executor.withGeneratedRecomputed`, upsert's
/// column-resolution probe) run this once per *write* purely for the
/// columns, so any eager per-call row copy here becomes an O(table) tax on
/// every INSERT/UPDATE. A caller that really consumes the rows as a list
/// uses `scanList` instead.
let scan (store: Store) (dbName: string) (tableName: string) : Result<ColumnDef list * Value[] seq, StorageError> =
    match store.Databases.TryGetValue dbName with
    | false, _ -> Error(NoSuchDatabase dbName)
    | true, slot ->
        match tryGetTable slot.Value tableName with
        | Error e -> Error e
        | Ok table -> Ok(table.Columns, table.RowsArray :> Value[] seq)

/// As `scan`, with the rows as the memoized per-snapshot list (see
/// `rowsList`) — repeated scans of an unchanged table share one list
/// instead of re-copying the array per query. The SELECT pipeline's
/// row-materialization point (`Executor.resolveTableRef`) is the caller.
let scanList (store: Store) (dbName: string) (tableName: string) : Result<ColumnDef list * Value[] list, StorageError> =
    match store.Databases.TryGetValue dbName with
    | false, _ -> Error(NoSuchDatabase dbName)
    | true, slot ->
        match tryGetTable slot.Value tableName with
        | Error e -> Error e
        | Ok table -> Ok(table.Columns, rowsList table)

/// Generated/virtual columns (`CREATE TABLE ... col AS (expr) [STORED |
/// VIRTUAL]`) — `Ast.ColumnDef.Generated` carries the parsed `Expr`, but
/// evaluating it needs `Executor.evalExpr` (a whole registry/row-context
/// this module doesn't have), so the actual recompute-on-write lives in
/// `Executor.recomputeGeneratedColumns`, called after every successful
/// `INSERT`/`UPDATE`. `VIRTUAL` isn't distinguished from `STORED` — both
/// are persisted in `Rows` the same way, since this engine has no separate
/// "recompute on every read" path.

/// `Persistence`'s WAL replay rewrites one table's `Rows` directly with `f`,
/// bypassing every checked write path in this module on purpose (replay
/// re-applies rows that already passed every check once, at commit time —
/// see `Persistence.applyEvent`'s doc). A plain slot mutation, not a CAS:
/// replay only ever runs single-threaded, before `Persistence.attach`
/// subscribes the store to live traffic, so nothing else can be racing this
/// write. A no-op (via `onMissing`) if `dbName`/`tableName` no longer
/// exist — the WAL can reference a table a later, not-yet-replayed DROP
/// TABLE event will remove, so replay tolerates a stale reference here
/// instead of crashing startup over it.
let replaceTablesForReplay (store: Store) (dbName: string) (tableName: string) (f: Value[] list -> Value[] list) (onMissing: string -> unit) : unit =
    let key = normalizeTableName tableName

    match store.Databases.TryGetValue dbName with
    | false, _ -> onMissing (sprintf "unknown database '%s'" dbName)
    | true, slot ->
        match slot.Value |> Map.tryFind key with
        | None -> onMissing (sprintf "unknown table '%s.%s'" dbName tableName)
        | Some table -> slot.Value <- slot.Value |> Map.add key { table with RowsArray = table.RowsArray |> List.ofSeq |> f |> ImmutableArray.CreateRange }

/// Rebuilds every table's `UniqueIndex` from its current `Rows` across the
/// whole store, once — what `Persistence.load` calls after replaying the
/// WAL, since `replaceTablesForReplay` (`RowsUpdated`/`RowsDeleted` replay)
/// deliberately leaves `UniqueIndex` stale per-table rather than paying
/// `reindexTable`'s full-table rescan once per replayed event (see its doc).
/// Same single-threaded, pre-`attach` assumption as `replaceTablesForReplay`.
let reindexAllForReplay (store: Store) : unit =
    for KeyValue(_, slot) in store.Databases do
        slot.Value <- slot.Value |> Map.map (fun _ table -> reindexTable table)
