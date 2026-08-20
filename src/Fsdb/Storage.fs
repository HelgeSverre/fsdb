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
    /// Dropping a system schema (`mysql`) — MySQL's 3552. Guarded here at
    /// the storage layer so every caller (executor, WAL replay) is covered.
    | SystemSchemaAccess of schema: string
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
    | FullTextColumnNotAllowed of column: string
    /// A value doesn't fit the column's numeric range — MySQL's 1264
    /// (SQLSTATE 22003), raised in strict mode when `ALTER ... MODIFY`
    /// narrows an integer type over existing out-of-range rows.
    | OutOfRangeForColumn of column: string
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
    /// A temporal column declared a fractional-seconds precision above 6
    /// (`DATETIME(7)`) — MySQL's 1426, which names the offending column.
    | PrecisionTooBig of column: string * precision: int
    /// An `INSERT` column list or `UPDATE ... SET` explicitly targeted a
    /// generated column — MySQL's 3105.
    | GeneratedColumnAssignment of column: string * table: string
    /// A DATE/DATETIME column's implicit zero value hit NO_ZERO_DATE (on by
    /// default) — MySQL's 1292, raised e.g. by `ALTER TABLE ... ADD COLUMN`
    /// implicitly back-filling a `NOT NULL` temporal column with no
    /// `DEFAULT` on a non-empty table.
    | ZeroTemporalForColumn of typeName: string * literal: string * column: string
    /// A write aimed at a registered `fsdb` virtual table — reads resolve to
    /// the host's overlay, so letting the write through would land it in an
    /// invisible shadowed real table and silently break read-your-writes.
    /// Guarded at the storage layer (like `SystemSchemaAccess`) so every
    /// executor path — DML, DROP, TRUNCATE, ALTER — is covered at once.
    | VirtualTableReadOnly of name: string

/// MySQL error code + message for a `StorageError`, ready for the wire
/// protocol's ERR packet.
let toMySqlError (err: StorageError) : int * string =
    match err with
    | NoSuchDatabase name -> 1049, sprintf "Unknown database '%s'" name
    | SystemSchemaAccess schema -> 3552, sprintf "Access to system schema '%s' is rejected." schema
    | DatabaseExists name -> 1007, sprintf "Can't create database '%s'; database exists" name
    | TableExists name -> 1050, sprintf "Table '%s' already exists" name
    | NoSuchTable name -> 1146, sprintf "Table '%s' doesn't exist" name
    | UnknownColumn name -> 1054, sprintf "Unknown column '%s' in field list" name
    | ColumnCountMismatch(expected, actual) ->
        1136, sprintf "Column count doesn't match value count at row 1 (expected %d, got %d)" expected actual
    | NotNullViolation column -> 1048, sprintf "Column '%s' cannot be null" column
    | InvalidValueForColumn(column, value) -> 1366, sprintf "Incorrect value: '%s' for column '%s'" value column
    | DataTruncatedForColumn column -> 1265, sprintf "Data truncated for column '%s' at row 1" column
    | FullTextColumnNotAllowed column -> 1283, sprintf "Column '%s' cannot be part of FULLTEXT index" column
    | OutOfRangeForColumn column -> 1264, sprintf "Out of range value for column '%s' at row 1" column
    | ExpressionError(code, message) -> code, message
    | DuplicateKey(keyName, value) -> 1062, sprintf "Duplicate entry '%s' for key '%s'" value keyName
    | ForeignKeyRestrict fkName ->
        1451, sprintf "Cannot delete or update a parent row: a foreign key constraint fails (`%s`)" fkName
    | ForeignKeyParentMissing fkName ->
        1452, sprintf "Cannot add or update a child row: a foreign key constraint fails (`%s`)" fkName
    | PrecisionTooBig(column, precision) ->
        1426, sprintf "Too-big precision %d specified for '%s'. Maximum is 6." precision column
    | GeneratedColumnAssignment(column, table) ->
        3105, sprintf "The value specified for generated column '%s' in table '%s' is not allowed." column table
    | ZeroTemporalForColumn(typeName, literal, column) ->
        1292, sprintf "Incorrect %s value: '%s' for column '%s' at row 1" typeName literal column
    // MySQL's ER_OPEN_AS_READONLY — the closest real vocabulary for "this
    // table exists but refuses writes".
    | VirtualTableReadOnly name -> 1036, sprintf "Table '%s' is read only" name

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
      /// When the table was created — surfaced as
      /// `information_schema.tables.CREATE_TIME`. Survives a snapshot;
      /// ponytail: a WAL-only replay re-stamps it at replay time (the
      /// CreateTable commit event carries no clock), refreshed by the next
      /// snapshot.
      CreateTime: DateTime
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
      /// The account whose statement is currently executing, as
      /// `CURRENT_USER()` renders it — per-session like `StrictMode` above
      /// (`Session.create`'s clone gives every connection its own cell), and
      /// re-derived per statement by `QueryHandler.executeParsed`. The
      /// executor needs it where no `Session` reaches: `CREATE TRIGGER`
      /// stamps it as the trigger's definer, and a body then runs under that
      /// definer's privileges rather than the inserting user's.
      mutable SessionUser: string
      /// Host-registered read-only tables in the reserved `fsdb` schema
      /// (`Db.registerTable`) — carried on the store because the store is
      /// what already reaches every `Executor.resolveTableRef` call site.
      /// Mutable for registration only: register before serving traffic
      /// (like `OnCommit` subscription, writes aren't synchronized against
      /// in-flight queries). An *overlay* on the real `fsdb` database
      /// (which is also `defaultDatabase`): a registered name wins over a
      /// same-named real table, other real tables resolve unchanged.
      /// Lower-invariant keys.
      mutable VirtualTables: Map<string, Functions.VirtualTable>
      /// Fires once per committed write, under `Lock`, right after the
      /// catalog swap that made it visible — empty (the default) means no
      /// subscriber, so every write path's event-construction work still
      /// happens (it's cheap: the row values were already computed for the
      /// write itself) but delivery is a no-op. Multi-subscriber
      /// (`Persistence.attach`'s WAL appender and any number of
      /// `Db.onCommit` handlers coexist), called in registration order;
      /// subscribe (`store.OnCommit.Add handler`) before serving traffic —
      /// registration isn't synchronized against in-flight commits, only
      /// dispatch is (see `emit`). A handler must not write back into the
      /// store (re-entry would deadlock on `Lock`).
      OnCommit: ResizeArray<CommitEvent -> unit>
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

// `create` itself lives after `reindexTable` below — it seeds the built-in
// `mysql` system schema, whose bootstrap needs the index rebuild.

type private TransactionGateLease(gate: SemaphoreSlim) =
    let mutable released = 0

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&released, 1) = 0 then
                gate.Release() |> ignore

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

/// Whether `dbName`'s live contents are still the very object `baseCatalog`
/// snapshotted. Every write swaps a database's slot for a brand new `Map`,
/// so reference identity answers "has anyone committed here since" in O(1)
/// where a structural comparison would walk every row.
///
/// `QueryHandler` asks when a transaction that has so far only *read* picks
/// up a write gate: its snapshot predates that gate, so a writer could have
/// landed in between, and merging the stale snapshot over that writer's row
/// is a lost update (see `mergeDatabaseSlot`).
let databaseUnchangedSince (store: Store) (baseCatalog: Catalog) (dbName: string) : bool =
    let live =
        match store.Databases.TryGetValue dbName with
        | true, slot -> Some slot.Value
        | _ -> None

    match Map.tryFind dbName baseCatalog, live with
    | None, None -> true
    | Some baseDb, Some liveDb -> obj.ReferenceEquals(baseDb, liveDb)
    | _ -> false

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
        | None ->
            if store.OnCommit.Count > 0 then
                lock store.Lock (fun () -> for f in store.OnCommit do f e)

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
      SessionUser = store.SessionUser
      VirtualTables = store.VirtualTables
      OnCommit = ResizeArray()
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
      PendingEvents = if store.OnCommit.Count > 0 || store.PendingEvents.IsSome then Some(ResizeArray()) else None
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
    if dbName.ToLowerInvariant() = "mysql" then
        Error(SystemSchemaAccess "mysql")
    else
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
/// cross-database writes can't contend by construction.
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
    // The reserved `fsdb` schema exists exactly while a host has registered
    // virtual tables into it — an empty registry hides it entirely, so
    // `USE fsdb` on a plain server still gets a real 1049.
    || (String.Equals(dbName, "fsdb", StringComparison.OrdinalIgnoreCase)
        && not (Map.isEmpty store.VirtualTables))
    || store.Databases.ContainsKey dbName

/// Index of a column by name, case-insensitive.
let resolveColumn (columns: ColumnDef list) (name: string) : Result<int, StorageError> =
    columns
    |> List.tryFindIndex (fun c -> String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
    |> function
        | Some i -> Ok i
        | None -> Error(UnknownColumn name)

/// `resolveColumn` for a write target: a generated column can't be
/// explicitly assigned (MySQL 3105).
let resolveAssignableColumn (columns: ColumnDef list) (tableName: string) (name: string) : Result<int, StorageError> =
    resolveColumn columns name
    |> Result.bind (fun i ->
        if (List.item i columns).Generated.IsSome then
            Error(GeneratedColumnAssignment ((List.item i columns).Name, tableName))
        else
            Ok i)

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

/// `List.fold` with `traverse`'s periodic `queryCancellation` check — for
/// the mutation folds (`updateRows`, `upsertRows`) whose per-row `step` can
/// call an arbitrarily slow registered function (`SET ocr_text = ocr(pdf)`):
/// without it a killed client's batch write only unwinds by the *accident*
/// that each row's `coerceRow`/`processRow` happens to route through
/// `traverse` (whose check fires on its first element) — this fold owns its
/// own check instead of leaning on that. Throwing mid-fold is safe — both
/// folds accumulate into statement-local state (`updateRows`'s builder,
/// upsert's working copy) that is only committed when the fold completes,
/// so cancellation stays all-or-nothing. (`deleteRows` needs nothing: its
/// row scan already routes through `traverse`.)
let foldWithCancellation (step: 'acc -> 'a -> 'acc) (init: 'acc) (xs: 'a list) : 'acc =
    let token = queryCancellation.Value

    xs
    |> List.fold
        (fun (i, acc) x ->
            if i % cancellationCheckInterval = 0 then
                token.ThrowIfCancellationRequested()

            i + 1, step acc x)
        (0, init)
    |> snd

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

    match col.Type, v with
    | TDecimal(precision, _), _ when precision < 1 || precision > 65 ->
        Error(ExpressionError(1426, sprintf "Too-big precision %d specified for '%s'. Maximum is 65." precision col.Name))
    | TDecimal(_, scale), _ when scale < 0 || scale > 30 ->
        Error(ExpressionError(1425, sprintf "Too big scale %d specified for column '%s'. Maximum is 30." scale col.Name))
    | TDecimal(precision, scale), _ when scale > precision ->
        Error(ExpressionError(1427, sprintf "For decimal(M,D), M must be >= D (column '%s')." col.Name))
    | _, VNull -> Ok VNull
    | _ ->
        match col.Type with
        // `BIGINT UNSIGNED` is the one integer column whose domain `VInt`
        // cannot hold; everything narrower (`INT UNSIGNED`'s 4294967295 and
        // down) fits a signed 64-bit value exactly and stays `VInt`.
        //
        // MySQL range-checks the value: 1264 under STRICT_TRANS_TABLES,
        // clamped to the domain's edge otherwise. `INSERT INTO t VALUES (-1)`
        // into a BIGINT UNSIGNED must not silently store 0.
        //
        // ponytail: only this branch enforces its range — the signed integer
        // types below still clamp unconditionally (inherited behaviour). Give
        // the whole integer family one shared range check when that gap is
        // addressed.
        | TBigInt true ->
            // 1264 ("Out of range value"), MySQL's code for a value outside
            // the column's numeric domain — not `fail`'s 1366, which is the
            // *uncoercible* case (a non-numeric string).
            let outOfRange () = Error(OutOfRangeForColumn col.Name)

            let narrow (d: decimal) =
                if d >= 0m && d <= decimal UInt64.MaxValue then
                    Ok(VUInt(uint64 d))
                elif strict then
                    outOfRange ()
                else
                    Ok(VUInt(uint64 (max 0m (min d (decimal UInt64.MaxValue)))))

            match v with
            | VUInt u -> Ok(VUInt u)
            | VInt i -> narrow (decimal i)
            | VDouble d ->
                // `decimal d` itself overflows outside ±7.9e28, so the range
                // verdict comes from the `double` before any conversion.
                if d >= 0.0 && d < 1.8446744073709552e19 then
                    narrow (Math.Truncate(decimal d))
                elif strict then
                    outOfRange ()
                else
                    Ok(VUInt(if d < 0.0 then 0UL else UInt64.MaxValue))
            | VDecimal d -> narrow (Math.Truncate d)
            | VString s ->
                match parseNumeric s with
                | Some d ->
                    if d >= 0.0 && d < 1.8446744073709552e19 then
                        narrow (Math.Truncate(decimal d))
                    elif strict then
                        outOfRange ()
                    else
                        Ok(VUInt(if d < 0.0 then 0UL else UInt64.MaxValue))
                | None -> numericFallback (fun () -> VUInt 0UL)
            | _ -> numericFallback (fun () -> VUInt 0UL)
        | TInt _
        | TBigInt _
        | TSmallInt _
        | TMediumInt _
        | TTinyInt _
        | TBool
        | TYear ->
            match v with
            | VInt i -> Ok(VInt i)
            // The signed counterpart of `CAST(x AS UNSIGNED)`'s wrap:
            // `CAST(CAST(18446744073709551615 AS UNSIGNED) AS SIGNED)` is -1.
            | VUInt u -> Ok(VInt(int64 u))
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
            | VUInt u -> Ok(VDouble(float u))
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
            | VUInt u -> Ok(VDecimal(rescale (decimal u)))
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
        | TLongText -> charsetChecked (v |> toText |> Option.defaultValue "") |> Result.map VString
        // A JSON column's value must carry its JSON-ness in the `Value` itself:
        // `Value.compare` puts a `VJson` operand's whole comparison into the
        // JSON domain (type precedence, then content), and a `VString` there
        // would instead be treated as the JSON *string* scalar it isn't — so
        // `json_col = CAST('"a"' AS JSON)` would be false and ORDER BY would
        // sort documents as text.
        | TJson -> charsetChecked (v |> toText |> Option.defaultValue "") |> Result.map VJson
        | TSet values ->
            // Same 1265 "Data truncated" MySQL raises for a rejected ENUM
            // value, not TChar/TVarchar's plain string coercion — a SET only
            // accepts its own declared members. `''` is always legal (the
            // empty set); MySQL stores members deduplicated, reordered into
            // declaration order regardless of input order (oracle-verified:
            // `'b,a'` and `'a,a,b'` both read back `'a,b'`), so canonical
            // form is derived from `values`' own order rather than the
            // input's. Non-strict mode doesn't fail — it silently drops
            // whichever input members (string or, for a numeric bitmask,
            // bits) aren't declared, keeping the rest.
            let setFail () = Error(DataTruncatedForColumn col.Name)

            let canonicalize (matched: Set<string>) =
                values |> List.filter (fun m -> matched.Contains(m.ToUpperInvariant())) |> String.concat ","

            match v with
            | VString "" -> Ok(VString "")
            | VString s ->
                let parts = s.Split(',') |> Array.toList

                let resolve part = values |> List.tryFind (fun allowed -> String.Equals(allowed, part, StringComparison.OrdinalIgnoreCase))

                if parts |> List.forall (resolve >> Option.isSome) then
                    Ok(VString(canonicalize (parts |> List.choose resolve |> List.map (fun m -> m.ToUpperInvariant()) |> Set.ofList)))
                elif strict then
                    setFail ()
                else
                    Ok(VString(canonicalize (parts |> List.choose resolve |> List.map (fun m -> m.ToUpperInvariant()) |> Set.ofList)))
            | VInt i ->
                let maxValid = (1L <<< List.length values) - 1L

                if i >= 0L && i <= maxValid then
                    let members = values |> List.indexed |> List.filter (fun (bit, _) -> i &&& (1L <<< bit) <> 0L) |> List.map snd
                    Ok(VString(String.concat "," members))
                elif strict then
                    setFail ()
                else
                    let masked = i &&& maxValid
                    let members = values |> List.indexed |> List.filter (fun (bit, _) -> masked &&& (1L <<< bit) <> 0L) |> List.map snd
                    Ok(VString(String.concat "," members))
            | _ -> setFail ()
        | TTime fsp ->
            // TIME is stored pre-formatted as a `VString` (there's no `VTime`
            // value) — so unlike DATETIME, its fsp lives *in the string* and
            // the resultset renderer just passes it through. Round the
            // fraction to `fsp` digits and re-render with exactly that many
            // (a `TIME(6)` on a whole second shows `.000000`, matching MySQL),
            // parsing the input as a `TimeSpan`; anything unparseable keeps
            // its raw text unchanged.
            let raw = v |> toText |> Option.defaultValue ""

            match TimeSpan.TryParse(raw.Trim(), CultureInfo.InvariantCulture) with
            | true, ts ->
                let a = TimeSpan(Functions.roundTicksToFsp fsp (abs ts.Ticks)).Duration()
                let sign = if ts.Ticks < 0L then "-" else ""
                let baseStr = sprintf "%s%02d:%02d:%02d" sign (int (floor a.TotalHours)) a.Minutes a.Seconds

                let text =
                    if fsp <= 0 then
                        baseStr
                    else
                        let micros = (a.Ticks % TimeSpan.TicksPerSecond) / 10L
                        sprintf "%s.%s" baseStr ((sprintf "%06d" micros).Substring(0, min fsp 6))

                charsetChecked text |> Result.map VString
            | false, _ -> charsetChecked raw |> Result.map VString
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
        | TVector dim ->
            // A vector column accepts exactly its dimension in little-endian
            // float32 bytes (dim × 4) — the shape STRING_TO_VECTOR (or an
            // `X'...'` literal) produces. No string/number coercion and no
            // non-strict fallback: MySQL 9 refuses anything else regardless
            // of sql_mode, so this always fails in the 1366 shape.
            match v with
            | VBytes bytes when bytes.Length = dim * 4 -> Ok(VBytes bytes)
            | _ -> Error(InvalidValueForColumn(col.Name, v |> toText |> Option.defaultValue ""))
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
        | TDateTime fsp
        | TTimestamp fsp ->
            // Round the sub-second part to the column's declared fsp so the
            // stored ticks already reflect the precision — MySQL rounds (half
            // up), it does not truncate: `DATETIME(0)` of `.6` stores `:01`,
            // and a `.9999995` into `(6)` carries all the way to the next day
            // (both oracle-verified). The resultset renderer then shows
            // exactly `fsp` digits off these rounded ticks.
            let round dt = VDateTime(Functions.roundDateTimeToFsp fsp dt)

            match v with
            | VDateTime dt -> Ok(round dt)
            | VDate d -> Ok(round (d.ToDateTime(TimeOnly.MinValue)))
            | VString s ->
                match DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, dt -> Ok(round dt)
                | false, _ -> temporalFallback ()
            | _ -> temporalFallback ()

/// `NOW()` rounded to `col`'s own declared fsp — a `TIMESTAMP(6)` column
/// keeps microseconds, a bare `DATETIME`/`TIMESTAMP` truncates to whole
/// seconds. Shared by `DEFAULT CURRENT_TIMESTAMP` (`evalDefault`, insert
/// time) and `ON UPDATE CURRENT_TIMESTAMP` (`Executor`, update time) since
/// both evaluate the same "current time at this column's precision" rule.
let currentTimestampForColumn (col: ColumnDef) : Value =
    let fsp =
        match col.Type with
        | TDateTime fsp
        | TTimestamp fsp
        | TTime fsp -> fsp
        | _ -> 0

    VDateTime(Functions.roundDateTimeToFsp fsp DateTime.Now)

/// Evaluates a column's `DEFAULT` clause into the value to insert when none
/// was provided — `CURRENT_TIMESTAMP` evaluates fresh here (insert time),
/// rather than being carried around as a stored marker value.
let evalDefault (col: ColumnDef) : Value =
    match col.Default with
    | None -> VNull
    | Some(DConst v) -> v
    | Some DCurrentTimestamp -> currentTimestampForColumn col

/// Coerces a value to its column's type and rejects NULL for a non-nullable
/// column.
let private coerceAndCheck (strict: bool) (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    match v with
    | VNull when not col.Nullable -> Error(NotNullViolation col.Name)
    | _ -> coerceValue strict col v

let private coerceRow (strict: bool) (columns: ColumnDef list) (row: Value[]) : Result<Value[], StorageError> =
    List.zip columns (Array.toList row)
    |> traverse (fun (column, value) -> coerceAndCheck strict column value)
    |> Result.map Array.ofList

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
        // Same "I" prefix as `VInt`: a `BIGINT UNSIGNED` key and a signed
        // one that hold the same number must land on the same key, or a
        // unique index would let both through as distinct rows.
        | VUInt value -> Some("I" + string (decimal value))
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

// ---------------------------------------------------------------------------
// The built-in `mysql` system schema: real stored tables (not virtual
// projections), so `USE mysql`, `SELECT ... FROM mysql.user`, SHOW TABLES,
// direct DML, and WAL/snapshot persistence all ride the ordinary catalog
// paths with zero special-casing. Column shapes follow MySQL 8.4
// (oracle-verified); only the 5 functionally-relevant tables of MySQL's 38
// exist. Bootstrapped by `create` below and re-ensured after a snapshot
// load (`ensureMysqlSchema`) so pre-feature snapshots pick it up.
// ponytail: no charset/collation fidelity on these columns (MySQL uses
// utf8mb3_bin/ascii here) — add if a client diff ever cares.
// ---------------------------------------------------------------------------

let private sysCol (name: string) (ty: ColumnType) (nullable: bool) (dflt: Value option) : ColumnDef =
    { Name = name
      Type = ty
      Nullable = nullable
      Default = dflt |> Option.map DConst
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      OnUpdateCurrentTimestamp = false
      Generated = None
      Collation = None
      Charset = None }

let private privCol (name: string) = sysCol name (TEnum [ "N"; "Y" ]) false (Some(VString "N"))

let private keyCol (name: string) (len: int) =
    { sysCol name (TChar len) false (Some(VString "")) with PrimaryKey = true }

/// mysql.user's 51 columns, in MySQL 8.4's exact order.
let private mysqlUserColumns: ColumnDef list =
    [ keyCol "Host" 255; keyCol "User" 32 ]
    @ ([ "Select"; "Insert"; "Update"; "Delete"; "Create"; "Drop"; "Reload"; "Shutdown"; "Process"; "File"
         "Grant"; "References"; "Index"; "Alter"; "Show_db"; "Super"; "Create_tmp_table"; "Lock_tables"
         "Execute"; "Repl_slave"; "Repl_client"; "Create_view"; "Show_view"; "Create_routine"
         "Alter_routine"; "Create_user"; "Event"; "Trigger"; "Create_tablespace" ]
       |> List.map (fun p -> privCol (p + "_priv")))
    @ [ sysCol "ssl_type" (TEnum [ ""; "ANY"; "X509"; "SPECIFIED" ]) false (Some(VString ""))
        // Real MySQL declares these blobs NOT NULL with no default; giving
        // them an empty-bytes default keeps partial-column inserts (the shape
        // CREATE USER writes) working without special-casing.
        sysCol "ssl_cipher" TBlob false (Some(VBytes [||]))
        sysCol "x509_issuer" TBlob false (Some(VBytes [||]))
        sysCol "x509_subject" TBlob false (Some(VBytes [||]))
        sysCol "max_questions" (TInt true) false (Some(VInt 0L))
        sysCol "max_updates" (TInt true) false (Some(VInt 0L))
        sysCol "max_connections" (TInt true) false (Some(VInt 0L))
        sysCol "max_user_connections" (TInt true) false (Some(VInt 0L))
        sysCol "plugin" (TChar 64) false (Some(VString "caching_sha2_password"))
        sysCol "authentication_string" TText true None
        sysCol "password_expired" (TEnum [ "N"; "Y" ]) false (Some(VString "N"))
        sysCol "password_last_changed" (TTimestamp 0) true None
        sysCol "password_lifetime" (TSmallInt true) true None
        sysCol "account_locked" (TEnum [ "N"; "Y" ]) false (Some(VString "N"))
        privCol "Create_role_priv"
        privCol "Drop_role_priv"
        sysCol "Password_reuse_history" (TSmallInt true) true None
        sysCol "Password_reuse_time" (TSmallInt true) true None
        sysCol "Password_require_current" (TEnum [ "N"; "Y" ]) true None
        sysCol "User_attributes" TJson true None ]

/// mysql.db's 22 columns (per-database privilege rows).
let private mysqlDbColumns: ColumnDef list =
    [ keyCol "Host" 255; keyCol "Db" 64; keyCol "User" 32 ]
    @ ([ "Select"; "Insert"; "Update"; "Delete"; "Create"; "Drop"; "Grant"; "References"; "Index"; "Alter"
         "Create_tmp_table"; "Lock_tables"; "Create_view"; "Show_view"; "Create_routine"; "Alter_routine"
         "Execute"; "Event"; "Trigger" ]
       |> List.map (fun p -> privCol (p + "_priv")))

let private tablePrivSet =
    [ "Select"; "Insert"; "Update"; "Delete"; "Create"; "Drop"; "Grant"; "References"; "Index"; "Alter"
      "Create View"; "Show view"; "Trigger" ]

let private columnPrivSet = [ "Select"; "Insert"; "Update"; "References" ]

let private mysqlTablesPrivColumns: ColumnDef list =
    [ keyCol "Host" 255
      keyCol "Db" 64
      keyCol "User" 32
      keyCol "Table_name" 64
      sysCol "Grantor" (TVarchar 288) false (Some(VString ""))
      sysCol "Timestamp" (TTimestamp 0) true None
      sysCol "Table_priv" (TSet tablePrivSet) false (Some(VString ""))
      sysCol "Column_priv" (TSet columnPrivSet) false (Some(VString "")) ]

let private mysqlColumnsPrivColumns: ColumnDef list =
    [ keyCol "Host" 255
      keyCol "Db" 64
      keyCol "User" 32
      keyCol "Table_name" 64
      keyCol "Column_name" 64
      sysCol "Timestamp" (TTimestamp 0) true None
      sysCol "Column_priv" (TSet columnPrivSet) false (Some(VString "")) ]

let private mysqlGlobalGrantsColumns: ColumnDef list =
    [ keyCol "USER" 32
      keyCol "HOST" 255
      keyCol "PRIV" 32
      sysCol "WITH_GRANT_OPTION" (TEnum [ "N"; "Y" ]) false (Some(VString "N")) ]

/// The bootstrap `root`@`%` row: every static privilege 'Y', empty
/// authentication_string (= no password; the handshake accepts only an
/// empty offered password for it), remaining columns their type's rest
/// state.
let private rootUserRow: Value[] =
    mysqlUserColumns
    |> List.map (fun c ->
        match c.Name with
        | "Host" -> VString "%"
        | "User" -> VString "root"
        | "plugin" -> VString "mysql_native_password"
        | "authentication_string" -> VString ""
        | n when n.EndsWith "_priv" -> VString "Y"
        | _ ->
            match c.Default with
            | Some(DConst v) -> v
            | _ ->
                match c.Type with
                | TBlob -> VBytes [||]
                | _ -> VNull)
    |> List.toArray

let private sysTable (name: string) (columns: ColumnDef list) (rows: Value[] list) : Table =
    let table =
        { OriginalName = name
          Columns = columns
          RowsArray = ImmutableArray.CreateRange rows
          NextAutoId = 1L
          Indexes = []
          ForeignKeys = []
          TableCharset = None
          TableCollation = None
          CreateTime = DateTime.Now
          UniqueIndex = Map.empty }

    // `rebuildUniqueIndex` directly, not `reindexTable`: the latter bumps the
    // replay-cost counter tests use to catch per-event reindexing, and
    // bootstrap isn't replay.
    { table with UniqueIndex = rebuildUniqueIndex table }

/// A registered virtual table dressed up as a rowless catalog `Table`, so
/// the `SHOW COLUMNS`/`DESCRIBE`/`SHOW CREATE TABLE`/`SHOW INDEX` renderers
/// (which all resolve through `InformationSchema.findTable`, catalog-only)
/// can describe what `SHOW TABLES` already advertises — MySQL never lists a
/// table it can't describe. Rows deliberately absent: introspection reads
/// metadata, and data reads go through the executor's overlay.
let virtualTableStub (vt: Functions.VirtualTable) : Table = sysTable vt.Name vt.Columns []

/// `mysql.triggers` — fsdb's row-backed trigger catalog (real MySQL keeps
/// triggers in the data dictionary; a plain system table means trigger
/// rows ride ordinary WAL row events with zero codec changes, the same
/// persistence route `mysql.user` accounts take). One row per trigger;
/// `action_statement` is the raw body text after `FOR EACH ROW`, re-parsed
/// at fire time (see `Ast.CreateTrigger`).
let mysqlTriggersColumns: ColumnDef list =
    [ keyCol "trigger_name" 64
      keyCol "trigger_schema" 64
      sysCol "event_table" (TChar 64) false (Some(VString ""))
      sysCol "action_timing" (TChar 6) false (Some(VString "AFTER"))
      sysCol "event_manipulation" (TChar 6) false (Some(VString "INSERT"))
      sysCol "action_statement" TText false (Some(VString ""))
      sysCol "created" (TDateTime 2) true None
      // The account a body runs as (`user@%`, MySQL's CHAR(93) width) —
      // bodies are privilege-checked against this at fire time, not against
      // the inserting session, so a trigger can't lend its invoker the
      // definer's reach. Appended last so the fixed cell positions every
      // existing reader uses stay put.
      sysCol "definer" (TChar 93) false (Some(VString "")) ]

let private mysqlSystemDatabase () : Database =
    [ "user", sysTable "user" mysqlUserColumns [ rootUserRow ]
      "db", sysTable "db" mysqlDbColumns []
      "tables_priv", sysTable "tables_priv" mysqlTablesPrivColumns []
      "columns_priv", sysTable "columns_priv" mysqlColumnsPrivColumns []
      "global_grants", sysTable "global_grants" mysqlGlobalGrantsColumns []
      "triggers", sysTable "triggers" mysqlTriggersColumns [] ]
    |> Map.ofList

/// Re-seeds the `mysql` system schema when it's absent — called after a
/// snapshot load replaces the whole catalog (a snapshot written before this
/// schema existed doesn't carry it). A no-op when `mysql` is already there,
/// so a snapshot that *does* carry it (users/grants included) wins — except
/// for individual system tables added after that snapshot was written
/// (`triggers`), which are seeded empty into the existing schema so DDL and
/// WAL replay against them can't hit `NoSuchTable`.
let ensureMysqlSchema (store: Store) : unit =
    store.Databases.TryAdd("mysql", ref (mysqlSystemDatabase ())) |> ignore

    let dbRef = store.Databases.["mysql"]

    if not (Map.containsKey "triggers" dbRef.Value) then
        dbRef.Value <- Map.add "triggers" (sysTable "triggers" mysqlTriggersColumns []) dbRef.Value
    else
        // A snapshot written before `definer` existed carries 7-cell trigger
        // rows: widen the table and pad them with the empty definer, which
        // refuses to fire. Failing closed is the point — reading empty as
        // "skip the check" or "run as root" is the escalation the column
        // exists to stop, and a loud error is one DROP/CREATE from fixed.
        let t = dbRef.Value.["triggers"]

        if t.Columns.Length < mysqlTriggersColumns.Length then
            let pad = List.skip t.Columns.Length mysqlTriggersColumns
            let fill = pad |> List.map (fun c -> match c.Default with Some(DConst v) -> v | _ -> VNull) |> Array.ofList

            dbRef.Value <-
                Map.add
                    "triggers"
                    { t with
                        Columns = t.Columns @ pad
                        RowsArray = t.RowsArray |> Seq.map (fun r -> Array.append r fill) |> ImmutableArray.CreateRange }
                    dbRef.Value

let create () : Store =
    let databases = ConcurrentDictionary<string, Database ref>()
    databases.[defaultDatabase] <- ref Map.empty
    databases.["mysql"] <- ref (mysqlSystemDatabase ())

    { Databases = databases
      ForeignKeyChecks = true
      StrictMode = true
      ConnectionCollation = Collation.defaultCollation
      SessionUser = "root"
      VirtualTables = Map.empty
      OnCommit = ResizeArray()
      PendingEvents = None
      TransactionGates = ConcurrentDictionary<string, SemaphoreSlim>()
      Lock = obj () }

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
/// `checkFkParent` falls back to a full scan in that case.
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
/// The `(table, unique key name, column index)` a `columnName = literal`
/// equality would probe — the guard chain `tryUniqueLookup` (the actual row
/// fetch) and `Executor`'s `EXPLAIN` `key`/`ref` reporting both read, so
/// EXPLAIN reports an index exactly when execution would use it.
let tryUniqueKeyProbe
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (literal: Value)
    : (Table * string * int) option =
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
                | Ok coerced when coerced = literal -> Some(table, groupName, idx)
                | _ -> None

let tryUniqueLookup
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (literal: Value)
    : (ColumnDef list * (int * Value[]) list) option =
    tryUniqueKeyProbe store dbName tableName columnName literal
    |> Option.map (fun (table, groupName, idx) ->
        // `encodeConstraintKey` indexes its row by the column's absolute
        // position, so the literal has to sit at `idx` of a full-width row —
        // a bare `[| literal |]` throws for any key column past position 0.
        let probeRow = Array.create (List.length table.Columns) VNull
        probeRow.[idx] <- literal

        let rows =
            match encodeConstraintKey table.Columns [ idx ] probeRow with
            | None -> []
            | Some key ->
                table.UniqueIndex
                |> Map.tryFind groupName
                |> Option.bind (Map.tryFind key)
                |> Option.map (fun pos -> pos, table.RowsArray.[pos])
                |> Option.toList

        table.Columns, rows)

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
                    // key is itself PK/UNIQUE (always true for a real FK).
                    // `parentUniqueIndex` is keyed by `encodeConstraintKey`
                    // over full parent rows at the columns' *absolute*
                    // positions, so the compact `values` have to sit at
                    // `refIdxs` of a full-width probe row — encoding a bare
                    // `[| values |]` throws for any referenced key column past
                    // position 0. Falls back to the full scan only for
                    // stale/malformed FK metadata.
                    let found =
                        match parentUniqueIndex parent refIdxs with
                        | Some index ->
                            let probeRow = Array.create (List.length parent.Columns) VNull
                            List.iter2 (fun i v -> probeRow.[i] <- v) refIdxs values
                            encodeConstraintKey parent.Columns refIdxs probeRow |> Option.map index.ContainsKey |> Option.defaultValue false
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

/// Validates type parameters at DDL time, where the real column name is in
/// scope for MySQL-compatible errors. Runtime coercion repeats DECIMAL's
/// bounds because CAST and JSON_TABLE create synthetic column definitions.
let private validateColumnType (c: ColumnDef) : Result<unit, StorageError> =
    match c.Type with
    | TDateTime fsp
    | TTimestamp fsp
    | TTime fsp when fsp > 6 -> Error(PrecisionTooBig(c.Name, fsp))
    | TDecimal(precision, _) when precision < 1 || precision > 65 ->
        Error(ExpressionError(1426, sprintf "Too-big precision %d specified for '%s'. Maximum is 65." precision c.Name))
    | TDecimal(_, scale) when scale < 0 || scale > 30 ->
        Error(ExpressionError(1425, sprintf "Too big scale %d specified for column '%s'. Maximum is 30." scale c.Name))
    | TDecimal(precision, scale) when scale > precision ->
        Error(ExpressionError(1427, sprintf "For decimal(M,D), M must be >= D (column '%s')." c.Name))
    // VECTOR shares the parse-anything-validate-at-DDL discipline: the
    // parser accepts any dimension, MySQL 9's 1..16383 range is enforced
    // here with real MySQL's 1074 shape for an over-long column.
    | TVector dim when dim < 1 || dim > 16383 ->
        Error(ExpressionError(1074, sprintf "Column length too big for column '%s' (max = 16383); use BLOB or TEXT instead" c.Name))
    | _ -> Ok()

/// MySQL 9 forbids a VECTOR column in any key — primary, unique, or plain
/// index (a 16KB-per-row float blob is nothing an index can order). Same
/// message shape as JSON's ER_JSON_USED_AS_KEY, whose code this borrows
/// since the 8.4 oracle can't arbitrate the real MySQL 9 number.
let private checkVectorKeyColumns (columns: ColumnDef list) (indexes: IndexDef list) : Result<unit, StorageError> =
    let vectorKeyError name =
        Error(ExpressionError(3152, sprintf "VECTOR column '%s' cannot be used in key specification." name))

    let isVector (c: ColumnDef) =
        match c.Type with
        | TVector _ -> true
        | _ -> false

    match columns |> List.tryFind (fun c -> isVector c && (c.PrimaryKey || c.Unique)) with
    | Some c -> vectorKeyError c.Name
    | None ->
        indexes
        |> List.collect (fun ix -> ix.Columns)
        |> List.tryFind (fun name ->
            columns
            |> List.exists (fun c -> isVector c && String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        |> function
            | Some name -> vectorKeyError name
            | None -> Ok()

/// FULLTEXT indexes only cover text columns — CHAR/VARCHAR and the TEXT
/// family — matching MySQL's 1283 for anything else.
let private checkFullTextColumns (columns: ColumnDef list) (ix: IndexDef) : Result<unit, StorageError> =
    if ix.Kind <> FullTextIndex then
        Ok()
    else
        let isTextual (t: ColumnType) =
            match t with
            | TChar _
            | TVarchar _
            | TTinyText
            | TText
            | TMediumText
            | TLongText -> true
            | _ -> false

        ix.Columns
        |> List.tryFind (fun name ->
            columns
            |> List.tryFind (fun c -> String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun c -> not (isTextual c.Type))
            |> Option.defaultValue false)
        |> function
            | Some bad -> Error(FullTextColumnNotAllowed bad)
            | None -> Ok()

let createTableSeeded
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: ColumnDef list)
    (indexes: IndexDef list)
    (foreignKeys: ForeignKeyDef list)
    (tableCharset: string option)
    (tableCollation: string option)
    (autoIncrementSeed: int64 option)
    : Result<unit, StorageError> =
    ensureDatabase store dbName

    let result =
        withDatabase store dbName (fun db ->
            let key = normalizeTableName tableName

            match columns |> traverse validateColumnType with
            | Error e -> Error e
            | Ok _ ->

            match checkVectorKeyColumns columns indexes with
            | Error e -> Error e
            | Ok() ->

            if Map.containsKey key db then
                Error(TableExists tableName)
            else

            match indexes |> List.tryPick (fun ix -> match checkFullTextColumns columns ix with Error e -> Some e | Ok() -> None) with
            | Some e -> Error e
            | None ->
                let table =
                    { OriginalName = tableName
                      Columns = columns
                      RowsArray = ImmutableArray.Empty
                      NextAutoId = autoIncrementSeed |> Option.defaultValue 1L
                      Indexes = indexes
                      ForeignKeys = foreignKeys
                      TableCharset = tableCharset
                      TableCollation = tableCollation
                      CreateTime = DateTime.Now
                      UniqueIndex = Map.empty }

                Ok(Map.add key (reindexTable table) db, ()))

    if result.IsOk then
        emit store (Some(SchemaChanged(dbName, CreateTable(tableName, columns, indexes, foreignKeys, false, tableCharset, tableCollation, autoIncrementSeed))))

    result


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
    createTableSeeded store dbName tableName columns indexes foreignKeys tableCharset tableCollation None

/// The docs promise the `fsdb` overlay is read-only, so the engine has to
/// keep that promise too: without this, a write to a registered name lands
/// in the shadowed real table while reads keep answering from the overlay —
/// silent loss of read-your-writes. Every mutation entry point below calls
/// this before resolving its target (the `SystemSchemaAccess` discipline);
/// creating/registering a shadowed real table stays allowed — only writes
/// addressed to the registered name are refused.
let private virtualWriteGuard (store: Store) (dbName: string) (tableName: string) : Result<unit, StorageError> =
    if
        String.Equals(dbName, defaultDatabase, StringComparison.OrdinalIgnoreCase)
        && Map.containsKey (normalizeTableName tableName) store.VirtualTables
    then
        Error(VirtualTableReadOnly tableName)
    else
        Ok()

let dropTable (store: Store) (dbName: string) (tableName: string) : Result<unit, StorageError> =
    let result =
        withDatabase store dbName (fun db ->
            let key = normalizeTableName tableName

            match virtualWriteGuard store dbName tableName with
            | Error e -> Error e
            | Ok() when Map.containsKey key db -> Ok(Map.remove key db, ())
            | Ok() -> Error(NoSuchTable tableName))

    if result.IsOk then
        emit store (Some(SchemaChanged(dbName, DropTable([ tableName ], false))))

    result

let truncate (store: Store) (dbName: string) (tableName: string) : Result<unit, StorageError> =
    // MySQL implements TRUNCATE as drop-and-recreate, so CREATE_TIME resets.
    let result =
        virtualWriteGuard store dbName tableName
        |> Result.bind (fun () ->
            withTable store dbName tableName (fun table ->
                Ok(reindexTable { table with RowsArray = ImmutableArray.Empty; NextAutoId = 1L; CreateTime = DateTime.Now }, ())))

    if result.IsOk then
        emit store (Some(SchemaChanged(dbName, Truncate tableName)))

    result

/// Removes column index `idx` from every row — used by `DropColumn`, since
/// `Value[]` has no built-in "remove at" the way a `ResizeArray` would.
let private removeColumnAt (idx: int) (row: Value[]) : Value[] =
    row |> Array.indexed |> Array.filter (fun (i, _) -> i <> idx) |> Array.map snd

/// A `NOT NULL` column's implicit type default when `ADD COLUMN` gives it no
/// explicit `DEFAULT` — MySQL back-fills existing rows with this rather than
/// rejecting the statement (oracle-verified on 8.4: `ALTER TABLE t ADD
/// COLUMN c INT NOT NULL` on a non-empty table succeeds, filling `0`).
/// Expressed as a seed fed through `coerceValue` rather than a literal
/// `Value` per case, so DECIMAL's declared scale, ENUM's declared casing
/// (`1` coerces to the first member), and TIME's fsp padding all come out
/// already correct instead of being re-derived here. DATE/DATETIME/
/// TIMESTAMP's implicit default is MySQL's zero date/datetime, which
/// `VDate`/`VDateTime` (`DateOnly`/`DateTime`, no year zero) can't
/// represent; that's also exactly the value NO_ZERO_DATE (on by default)
/// rejects with 1292, which is what this reports for DATE/DATETIME — the
/// real behavior under the default strict setup. TIMESTAMP is MySQL's own
/// special case past NO_ZERO_DATE (it fills the zero epoch and succeeds
/// even in strict mode); this still reports 1292 for it too, a known gap
/// tied to the same representability limit.
let private implicitZeroSeed (col: ColumnDef) : Result<Value, StorageError> =
    match col.Type with
    | TChar _
    | TVarchar _
    | TTinyText
    | TText
    | TMediumText
    | TLongText
    | TSet _
    | TJson -> Ok(VString "")
    | TBinary _
    | TVarBinary _
    | TTinyBlob
    | TBlob
    | TMediumBlob
    | TLongBlob -> Ok(VBytes [||])
    // The all-zeros vector at the column's declared dimension — the only
    // value `coerceValue`'s exact-length check would accept as a seed.
    | TVector dim -> Ok(VBytes(Array.zeroCreate (dim * 4)))
    | TEnum _ -> Ok(VInt 1L)
    | TTime _ -> Ok(VString "00:00:00")
    | TDate -> Error(ZeroTemporalForColumn("date", "0000-00-00", col.Name))
    | TDateTime _
    | TTimestamp _ -> Error(ZeroTemporalForColumn("datetime", "0000-00-00 00:00:00", col.Name))
    | _ -> Ok(VInt 0L)

/// The value an added column gets filled in with for every row that already
/// exists — its `DEFAULT` if it has one, `NULL` for a nullable column with
/// none, and otherwise (`NOT NULL`, no `DEFAULT`) the type's own implicit
/// zero value, coerced/rescaled the same way a written value would be (see
/// `implicitZeroSeed`).
let private addedColumnFill (strict: bool) (col: ColumnDef) : Result<Value, StorageError> =
    match col.Default with
    | Some _ -> Ok(evalDefault col)
    | None ->
        if col.Nullable then
            Ok VNull
        else
            implicitZeroSeed col |> Result.bind (coerceValue strict col)

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
let private applyAlterAction (strict: bool) (table: Table) (action: AlterAction) : Result<Table * string option, StorageError> =
    // MODIFY/CHANGE re-coerce every existing row into the new definition —
    // MySQL's copy-alter semantics. `coerceValue` gives temporal fsp
    // narrowing its half-up rounding for free; on top of it, ALTER enforces
    // the narrowing checks the oracle showed: a string longer than the new
    // CHAR/VARCHAR length is 1265 "Data truncated" (not INSERT's 1406) in
    // strict mode (silently truncated non-strict), an integer outside the
    // new type's range is 1264 "Out of range" in strict mode (clamped
    // non-strict). The first failing row aborts the whole ALTER, leaving
    // the table untouched.
    let recoerce (newDef: ColumnDef) (v: Value) : Result<Value, StorageError> =
        coerceValue strict newDef v
        |> Result.bind (fun coerced ->
            match newDef.Type, coerced with
            | (TChar n | TVarchar n), VString s when String.length s > n ->
                if strict then Error(DataTruncatedForColumn newDef.Name) else Ok(VString(s.Substring(0, n)))
            | intType, VInt i ->
                let range =
                    match intType with
                    | TTinyInt unsigned -> Some(if unsigned then 0L, 255L else -128L, 127L)
                    | TBool -> Some(-128L, 127L)
                    | TSmallInt unsigned -> Some(if unsigned then 0L, 65535L else -32768L, 32767L)
                    | TMediumInt unsigned -> Some(if unsigned then 0L, 16777215L else -8388608L, 8388607L)
                    | TInt unsigned -> Some(if unsigned then 0L, 4294967295L else -2147483648L, 2147483647L)
                    | _ -> None

                match range with
                | Some(lo, hi) when i < lo || i > hi ->
                    if strict then Error(OutOfRangeForColumn newDef.Name) else Ok(VInt(max lo (min hi i)))
                | _ -> Ok coerced
            | _ -> Ok coerced)

    // Reject a too-big fsp on any column this action introduces (1426),
    // before it can reach the table — the DDL-time counterpart to
    // `createTable`'s own `validateColumnType` pass.
    let fspCheck =
        match action with
        | AddColumn(col, _) ->
            validateColumnType col
            |> Result.bind (fun () -> checkVectorKeyColumns [ col ] [])
        // Existing indexes reference the column by its pre-ALTER name, so a
        // type change into an already-indexed column must be checked under
        // the old name — otherwise MODIFY/CHANGE is a back door into a
        // `KEY` over VECTOR that CREATE would have refused.
        | ModifyColumn(col, _)
        | ChangeColumn(_, col, _) ->
            let oldName =
                match action with
                | ChangeColumn(oldName, _, _) -> oldName
                | _ -> col.Name

            validateColumnType col
            |> Result.bind (fun () -> checkVectorKeyColumns [ { col with Name = oldName } ] table.Indexes)
        // The key-introducing actions must refuse a VECTOR column the same
        // way CREATE TABLE does — otherwise ALTER is a back door into the
        // very keys `checkVectorKeyColumns` exists to forbid.
        | AddIndex ix -> checkVectorKeyColumns table.Columns [ ix ]
        | AddPrimaryKey cols ->
            checkVectorKeyColumns
                (table.Columns |> List.map (fun c -> if List.contains c.Name cols then { c with PrimaryKey = true } else c))
                []
        | _ -> Ok()

    match fspCheck with
    | Error e -> Error e
    | Ok() ->

    match action with
    | AddColumn(col, position) ->
        // Only actually needed to fill a row when there's at least one —
        // MySQL never evaluates (and so never errors on) a `NOT NULL`
        // column's implicit zero value against an empty table.
        let fill = if table.RowsArray.IsEmpty then Ok VNull else addedColumnFill strict col

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

        fill
        |> Result.bind (fun fill ->
            resolvePosition table.Columns (List.length table.Columns) position
            |> Result.map (fun idx ->
                { table with
                    Columns = table.Columns |> insertAt idx colWithDefaults
                    RowsArray = table.RowsArray |> Seq.map (fun r -> r |> Array.toList |> insertAt idx fill |> Array.ofList) |> ImmutableArray.CreateRange },
                None))
    | DropColumn name ->
        resolveColumn table.Columns name
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> List.indexed |> List.filter (fun (i, _) -> i <> idx) |> List.map snd
                RowsArray = table.RowsArray |> Seq.map (removeColumnAt idx) |> ImmutableArray.CreateRange },
            None)
    | ModifyColumn(newDef, position)
    | ChangeColumn(_, newDef, position) ->
        let oldName =
            match action with
            | ChangeColumn(oldName, _, _) -> oldName
            | _ -> newDef.Name

        resolveColumn table.Columns oldName
        |> Result.bind (fun oldIdx ->
            let columnsExcludingSelf = table.Columns |> List.indexed |> List.filter (fun (i, _) -> i <> oldIdx) |> List.map snd

            resolvePosition columnsExcludingSelf oldIdx position
            |> Result.bind (fun newIdx ->
                table.RowsArray
                |> List.ofSeq
                |> traverse (fun (r: Value[]) ->
                    recoerce newDef r.[oldIdx]
                    |> Result.map (fun v -> r |> removeColumnAt oldIdx |> Array.toList |> insertAt newIdx v |> Array.ofList))
                |> Result.bind (fun rows ->
                    let candidate =
                        { table with
                            Columns = columnsExcludingSelf |> insertAt newIdx newDef
                            RowsArray = ImmutableArray.CreateRange rows }

                    // A narrowing re-coercion that folds two unique-key
                    // values together must fail with 1062 (MySQL errors even
                    // non-strict) — otherwise `reindexTable`'s last-wins
                    // rebuild would silently drop rows from the index.
                    let collision =
                        uniqueKeyGroups candidate
                        |> List.tryPick (fun (name, idxs) ->
                            let rec loop seen remaining =
                                match remaining with
                                | [] -> None
                                | (row: Value[]) :: rest ->
                                    match encodeConstraintKey candidate.Columns idxs row with
                                    | Some key when Set.contains key seen ->
                                        let value = idxs |> List.map (fun i -> row.[i] |> toText |> Option.defaultValue "NULL") |> String.concat "-"
                                        Some(DuplicateKey(name, value))
                                    | Some key -> loop (Set.add key seen) rest
                                    | None -> loop seen rest

                            loop Set.empty rows)

                    match collision with
                    | Some e -> Error e
                    | None -> Ok(candidate, None))))
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
    | AddIndex ix ->
        checkFullTextColumns table.Columns ix
        |> Result.map (fun () -> { table with Indexes = table.Indexes @ [ ix ] }, None)
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
    | SetAutoIncrement value ->
        // Forward only, like InnoDB: a value below what existing rows
        // already claimed leaves the counter where it is.
        Ok({ table with NextAutoId = max value table.NextAutoId }, None)

/// Applies `actions` in order against `tableName`, re-filing it under a new
/// key if any action renamed it (`RENAME TO`/`RENAME [TABLE]`).
let alterTable (store: Store) (dbName: string) (tableName: string) (actions: AlterAction list) : Result<unit, StorageError> =
    let result =
        withDatabase store dbName (fun db ->
            virtualWriteGuard store dbName tableName
            |> Result.bind (fun () -> tryGetTable db tableName)
            |> Result.bind (fun table ->
                let origKey = normalizeTableName tableName

                let step acc action =
                    acc
                    |> Result.bind (fun (key, tbl) ->
                        applyAlterAction store.StrictMode tbl action
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

/// `RENAME TABLE a TO b, c TO d` — every pair inside one catalog swap and one
/// WAL event, because MySQL's RENAME TABLE is atomic across its pairs and
/// per-pair `alterTable` calls are not: N events mean a crash can replay half
/// a rename, leaving `a` renamed and `c` untouched.
/// ponytail: atomic *per database*. A cross-database rename still emits one
/// event per database, since each database is its own catalog cell — spanning
/// them would need a lock above the per-database one.
let renameTables (store: Store) (dbName: string) (pairs: (string * string) list) : Result<unit, StorageError> =
    let result =
        withDatabase store dbName (fun db ->
            let step acc (oldName, newName) =
                acc
                |> Result.bind (fun db ->
                    virtualWriteGuard store dbName oldName
                    |> Result.bind (fun () -> tryGetTable db oldName)
                    |> Result.bind (fun table ->
                        applyAlterAction store.StrictMode table (RenameTo newName)
                        |> Result.map (fun (table', newKey) ->
                            let origKey = normalizeTableName oldName
                            let key = newKey |> Option.defaultValue origKey
                            db |> Map.remove origKey |> Map.add key (reindexTable table'))))

            pairs |> List.fold step (Ok db) |> Result.map (fun db' -> db', ()))

    if result.IsOk then
        emit store (Some(SchemaChanged(dbName, RenameTable pairs)))

    result

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
            let pending = provided |> Option.defaultValue (evalDefault col)

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
    // ponytail: a bare `INSERT INTO t VALUES (...)` still accepts a value in
    // a generated column's slot and recomputes over it (MySQL demands the
    // `DEFAULT` keyword per cell, which the parser doesn't have) — add that
    // once the parser learns `DEFAULT` in a VALUES row.
    | None -> Ok [ 0 .. table.Columns.Length - 1 ]
    | Some names -> names |> traverse (resolveAssignableColumn table.Columns table.OriginalName)

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
/// value" tracked, that row's `last_insert_id` would come back
/// 0 instead of the id it was actually given, and every caller reading it
/// back (`Eloquent`'s own model, here) would silently get a wrong id.
///
/// That OK-packet value is also returned separately as `generatedId` — the
/// first *actually generated* id, or `None` if every row supplied its own —
/// because the SQL function `LAST_INSERT_ID()` has a narrower rule than the
/// OK packet: it only ever reflects a generated id, never an explicitly
/// supplied one, and holds its previous value across a statement that
/// generated none at all (see `QueryHandler`'s `LAST_INSERT_ID` doc).
/// What one INSERT/UPSERT actually did — the OK-packet numbers plus the
/// concrete rows written (AFTER INSERT triggers bind NEW.* from these).
/// `insertRows`/`insertRowsIgnore`/`upsertRows` always built these rows
/// internally for the WAL emit; the record just stops discarding them.
type InsertOutcome =
    { LastInsertId: int64
      GeneratedId: int64 option
      Affected: int
      InsertedRows: Value[] list }

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
    : Result<InsertOutcome, StorageError> =
    let key = normalizeTableName tableName

    let result =
        withDatabase store dbName (fun db ->
            virtualWriteGuard store dbName tableName
            |> Result.bind (fun () -> tryGetTable db tableName)
            |> Result.bind (fun table ->
                resolveInsertColumns table columns
                |> Result.bind (insertCore store.ForeignKeyChecks store.StrictMode false db key rowsIn)))

    match result with
    | Ok(lastId, generatedId, affected, rows) ->
        if not rows.IsEmpty then
            emit store (Some(RowsInserted(dbName, tableName, rows)))

        Ok { LastInsertId = lastId; GeneratedId = generatedId; Affected = affected; InsertedRows = rows }
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
    : Result<InsertOutcome, StorageError> =
    let key = normalizeTableName tableName

    let result =
        withDatabase store dbName (fun db ->
            virtualWriteGuard store dbName tableName
            |> Result.bind (fun () -> tryGetTable db tableName)
            |> Result.bind (fun table ->
                resolveInsertColumns table columns
                |> Result.bind (insertCore store.ForeignKeyChecks store.StrictMode true db key rowsIn)))

    match result with
    | Ok(lastId, generatedId, affected, rows) ->
        if not rows.IsEmpty then
            emit store (Some(RowsInserted(dbName, tableName, rows)))

        Ok { LastInsertId = lastId; GeneratedId = generatedId; Affected = affected; InsertedRows = rows }
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
/// child row elsewhere in `db`, applying every referencing foreign key's
/// `ON UPDATE` action first — `updateRows`/`upsertRows`'s parent-side
/// counterpart to `checkFkParents`'s child-side check, mirroring
/// `cascadeDeleteVisited`'s per-child dispatch and cycle guard but keyed off
/// `fk.OnUpdate` and rewriting the child's FK columns in place rather than
/// deleting the child row. Only relevant when the update actually changes a
/// column some other table's FK references at all: most `UPDATE`s never
/// touch the referenced key, so this is a no-op the moment `oldKey = newKey`.
/// `CASCADE` rewrites every matching child row's FK columns to `newRow`'s key
/// and recurses (a rewritten child row can itself be a parent of further
/// tables); a cascade that loops back into any table already on the current
/// cascade path fails 1451, matching MySQL. `SET NULL` blanks them, failing 1048 instead if any of
/// the FK columns is itself `NOT NULL` (real MySQL refuses to create such a
/// constraint at all, error 1215; this engine doesn't validate DDL that
/// strictly, so the equivalent check happens here — same as
/// `cascadeDeleteVisited`'s `SET NULL` branch); anything else (`RESTRICT`,
/// `NO ACTION`, `SET DEFAULT`, or no `ON UPDATE` clause) fails 1451 the
/// moment a matching child row exists. `checkFks = false` short-circuits to
/// a no-op, same as `cascadeDelete`.
let rec private cascadeUpdateVisited
    (checkFks: bool)
    (db: Database)
    (visited: Map<string, Value[] list>)
    (changes: Map<string, (Value[] * Value[]) list>)
    (tableKey: string)
    (parentColumns: ColumnDef list)
    (oldRow: Value[])
    (newRow: Value[])
    : Result<Database * Map<string, Value[] list> * Map<string, (Value[] * Value[]) list>, StorageError> =
    cascadeUpdateVisitedFrom checkFks db visited changes (Set.singleton tableKey) tableKey parentColumns oldRow newRow

/// `cascadeUpdateVisited`'s actual body, with `path` — every table on the
/// current cascade chain, root included — threaded through the recursion.
/// MySQL 8.4 refuses an ON UPDATE cascade that recurses back into a table
/// already on its cascade path with 1451, whether directly self-referencing
/// or through intermediate tables.
and private cascadeUpdateVisitedFrom
    (checkFks: bool)
    (db: Database)
    (visited: Map<string, Value[] list>)
    (changes: Map<string, (Value[] * Value[]) list>)
    (path: Set<string>)
    (tableKey: string)
    (parentColumns: ColumnDef list)
    (oldRow: Value[])
    (newRow: Value[])
    : Result<Database * Map<string, Value[] list> * Map<string, (Value[] * Value[]) list>, StorageError> =
    if not checkFks then
        Ok(db, visited, changes)
    else
        let checkOne acc (childKey: string, fk: ForeignKeyDef) =
            acc
            |> Result.bind (fun (d, visited, changes) ->
                match fk.RefColumns |> traverse (resolveColumn parentColumns) with
                | Error _ -> Ok(d, visited, changes) // stale FK metadata — see `checkFkParents`'s note.
                | Ok refIdxs ->
                    let oldKey = refIdxs |> List.map (fun i -> oldRow.[i])
                    let newKey = refIdxs |> List.map (fun i -> newRow.[i])

                    if oldKey = newKey || oldKey |> List.exists ((=) VNull) then
                        Ok(d, visited, changes)
                    else
                        match Map.tryFind childKey d with
                        | None -> Ok(d, visited, changes)
                        | Some childTbl ->
                            match fk.Columns |> traverse (resolveColumn childTbl.Columns) with
                            | Error _ -> Ok(d, visited, changes)
                            | Ok childIdxs ->
                                let alreadyVisited = visited |> Map.tryFind childKey |> Option.defaultValue []

                                let isChild (row: Value[]) =
                                    let key = childIdxs |> List.map (fun i -> row.[i])

                                    key |> List.forall ((<>) VNull)
                                    && List.forall2 (fun a b -> compare a b = 0) key oldKey
                                    && not (alreadyVisited |> List.exists ((=) row))

                                let matching = childTbl.RowsArray |> Seq.filter isChild |> List.ofSeq

                                if matching.IsEmpty then
                                    Ok(d, visited, changes)
                                // A cascade looping back into a table already on the cascade
                                // path fails 1451, matching MySQL 8.4. A loop back into the
                                // root would also land in a `Database` copy the caller's own
                                // final `Map.add` silently clobbers, corrupting referential
                                // integrity and desyncing the WAL from memory.
                                elif Set.contains childKey path then
                                    Error(ForeignKeyRestrict fk.Name)
                                else
                                    match fk.OnUpdate |> Option.map (fun s -> s.Trim().ToUpperInvariant()) with
                                    | Some "CASCADE" ->
                                        let visited' = visited |> Map.add childKey (alreadyVisited @ matching)
                                        let childGroups = uniqueKeyGroups childTbl

                                        let rewritten, rowChanges, index =
                                            childTbl.RowsArray
                                            |> Seq.indexed
                                            |> Seq.fold
                                                (fun (rows, chg, index) (pos, row) ->
                                                    if isChild row then
                                                        let row' = Array.copy row
                                                        List.iter2 (fun i v -> row'.[i] <- v) childIdxs newKey
                                                        row' :: rows, (row, row') :: chg, reindexRow childTbl.Columns childGroups (Some row) (Some(pos, row')) index
                                                    else
                                                        row :: rows, chg, index)
                                                ([], [], childTbl.UniqueIndex)

                                        let d' = Map.add childKey { childTbl with RowsArray = List.rev rewritten |> ImmutableArray.CreateRange; UniqueIndex = index } d
                                        let changes' = changes |> Map.add childKey ((changes |> Map.tryFind childKey |> Option.defaultValue []) @ List.rev rowChanges)

                                        List.rev rowChanges
                                        |> List.fold
                                            (fun acc (oldC, newC) ->
                                                acc
                                                |> Result.bind (fun (d, visited, changes) ->
                                                    cascadeUpdateVisitedFrom checkFks d visited changes (Set.add childKey path) childKey childTbl.Columns oldC newC))
                                            (Ok(d', visited', changes'))
                                    | Some "SET NULL" ->
                                        match childIdxs |> List.tryFind (fun i -> not childTbl.Columns.[i].Nullable) with
                                        | Some i -> Error(NotNullViolation childTbl.Columns.[i].Name)
                                        | None ->
                                            let childGroups = uniqueKeyGroups childTbl

                                            let blanked, rowChanges, index =
                                                childTbl.RowsArray
                                                |> Seq.indexed
                                                |> Seq.fold
                                                    (fun (rows, chg, index) (pos, row) ->
                                                        if isChild row then
                                                            let row' = Array.copy row
                                                            childIdxs |> List.iter (fun i -> row'.[i] <- VNull)
                                                            row' :: rows, (row, row') :: chg, reindexRow childTbl.Columns childGroups (Some row) (Some(pos, row')) index
                                                        else
                                                            row :: rows, chg, index)
                                                    ([], [], childTbl.UniqueIndex)

                                            let d' = Map.add childKey { childTbl with RowsArray = List.rev blanked |> ImmutableArray.CreateRange; UniqueIndex = index } d
                                            let changes' = changes |> Map.add childKey ((changes |> Map.tryFind childKey |> Option.defaultValue []) @ List.rev rowChanges)

                                            Ok(d', visited |> Map.add childKey (alreadyVisited @ matching), changes')
                                    | _ -> Error(ForeignKeyRestrict fk.Name))

        referencingForeignKeys db tableKey |> List.fold checkOne (Ok(db, visited, changes))

/// `INSERT ... ON DUPLICATE KEY UPDATE`: like `insertRows`, but a candidate
/// row that collides with an existing row on any unique key or the primary
/// key is applied to `applyUpdate existingRow candidateRow` instead of being
/// appended. Collision detection goes through the same `UniqueIndex`
/// (collation-aware via `encodeConstraintKey`) as plain `INSERT`'s unique
/// check.
/// A matched row `applyUpdate` actually changes counts 2 toward the
/// returned total (MySQL counts the attempted insert plus the update);
/// `foundRows` is the session's negotiated CLIENT_FOUND_ROWS capability —
/// a matched row `applyUpdate` leaves unchanged (every column still equal
/// to what it already held) counts 1 when set, same as MySQL's
/// `affected_rows` for a no-op `ON DUPLICATE KEY UPDATE` match, and 0
/// when not.
let rec upsertRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    (computeGenerated: Value[] -> Result<Value[], StorageError>)
    (applyUpdate: Value[] -> Value[] -> Result<Value[], StorageError>)
    (foundRows: bool)
    : Result<InsertOutcome, StorageError> =
        let key = normalizeTableName tableName

        let result =
            withDatabase store dbName (fun db ->
                virtualWriteGuard store dbName tableName
                |> Result.bind (fun () -> tryGetTable db tableName)
                |> Result.bind (fun table -> upsertRowsInTable store db key table columns rowsIn computeGenerated applyUpdate foundRows)
                |> Result.map (fun (db', cascaded, summary) -> db', (summary, cascaded, db)))

        match result with
        | Ok((lastId, generatedId, affected, inserted, updated), cascaded, db) ->
            if not inserted.IsEmpty then
                emit store (Some(RowsInserted(dbName, tableName, inserted)))

            if not updated.IsEmpty then
                emit store (Some(RowsUpdated(dbName, tableName, updated)))

            // `ON UPDATE CASCADE`/`SET NULL` child rewrites this candidate
            // batch's `ON DUPLICATE KEY UPDATE` triggered — same `RowsUpdated`
            // shape a plain `UPDATE` reports (see `cascadeUpdateVisited`'s doc).
            let originalNameOf tableKey = db |> Map.tryFind tableKey |> Option.map (fun t -> t.OriginalName) |> Option.defaultValue tableKey

            cascaded
            |> Map.iter (fun tableKey changes -> if not changes.IsEmpty then emit store (Some(RowsUpdated(dbName, originalNameOf tableKey, changes))))

            Ok { LastInsertId = lastId; GeneratedId = generatedId; Affected = affected; InsertedRows = inserted }
        | Error e -> Error e

/// `upsertRows`'s per-table body, pulled out only so it can take `db` (needed
/// for `checkFkParents`/`cascadeUpdateVisited`, the same FK enforcement
/// `insertRows`/`updateRows` apply) alongside `table`, which `withTable`
/// alone doesn't expose. Besides its usual summary tuple, returns the
/// database with every `ON UPDATE CASCADE`/`SET NULL` child rewrite already
/// applied, and those rewrites' before/after values by table key for
/// `upsertRows` to report as their own `RowsUpdated` events.
and private upsertRowsInTable
    (store: Store)
    (db: Database)
    (key: string)
    (table: Table)
    (columns: string list option)
    (rowsIn: Value list list)
    (computeGenerated: Value[] -> Result<Value[], StorageError>)
    (applyUpdate: Value[] -> Value[] -> Result<Value[], StorageError>)
    (foundRows: bool)
    : Result<Database * Map<string, (Value[] * Value[]) list> * (int64 * int64 option * int * Value[] list * (Value[] * Value[]) list), StorageError> =
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
                                  index: Map<string, Map<string, int>>,
                                  cascadeDb: Database,
                                  visited: Map<string, Value[] list>,
                                  cascaded: Map<string, (Value[] * Value[]) list>) ->
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
                                                |> Result.bind (coerceRow store.StrictMode table.Columns)
                                                |> Result.bind (fun applied ->
                                                    let collision =
                                                        uniqueGroups
                                                        |> List.tryPick (fun (name, idxs) ->
                                                            match encodeConstraintKey table.Columns idxs applied with
                                                            | Some key ->
                                                                match Map.tryFind key (Map.find name index) with
                                                                | Some otherPos when otherPos <> pos ->
                                                                    let value =
                                                                        idxs
                                                                        |> List.map (fun i -> applied.[i] |> toText |> Option.defaultValue "NULL")
                                                                        |> String.concat "-"

                                                                    Some(DuplicateKey(name, value))
                                                                | _ -> None
                                                            | None -> None)

                                                    match collision with
                                                    | Some error -> Error error
                                                    | None -> Ok applied)
                                                |> Result.bind (fun applied ->
                                                    // Same FK enforcement `updateRows` applies:
                                                    // `applied`'s own foreign keys need a live
                                                    // parent (child-side), and if this rewrite
                                                    // changed a column some *other* table's FK
                                                    // references, it can't orphan an existing
                                                    // child (parent-side) — or, per `ON UPDATE`,
                                                    // cascades/blanks that child instead.
                                                    (if checkFks then
                                                         checkFkParents cascadeDb table.Columns table.ForeignKeys applied
                                                         |> Result.bind (fun () -> cascadeUpdateVisited true cascadeDb visited cascaded key table.Columns existing applied)
                                                     else
                                                         Ok(cascadeDb, visited, cascaded))
                                                    |> Result.map (fun (cascadeDb', visited', cascaded') ->
                                                        if pos < current.Length then
                                                            current.[pos] <- applied
                                                        else
                                                            newRows.[pos - current.Length] <- applied

                                                        // MySQL's `ON DUPLICATE KEY UPDATE`
                                                        // row-count rule: a match that actually
                                                        // changes the row counts as 2 (one for the
                                                        // attempted insert, one for the update); a
                                                        // no-op match (every column still equal to
                                                        // what it already held) counts as 1 only
                                                        // when the client negotiated
                                                        // CLIENT_FOUND_ROWS, else 0.
                                                        let changed = applied <> existing
                                                        let weight =
                                                            if changed then 2
                                                            elif foundRows then 1
                                                            else 0

                                                        // A no-op match stays out of `updated`:
                                                        // MySQL's row-based binlog logs nothing
                                                        // for a no-op ODKU, and emitting a
                                                        // before=after RowsUpdated here would let
                                                        // an onCommit-driven pipeline whose drain
                                                        // upsert no-ops re-fire itself forever.
                                                        nextAutoId',
                                                        firstAuto,
                                                        lastExplicit,
                                                        affected + weight,
                                                        inserted,
                                                        (if changed then (existing, applied) :: updated else updated),
                                                        reindexRow table.Columns uniqueGroups (Some existing) (Some(pos, applied)) index,
                                                        cascadeDb',
                                                        visited',
                                                        cascaded'))
                                            | candidate, None ->
                                                // Same FK-parent check `insertCore` applies to
                                                // a plain `INSERT`'s new rows — this candidate
                                                // didn't collide with anything, so it's really
                                                // an insert.
                                                (if checkFks then
                                                     checkFkParents cascadeDb table.Columns table.ForeignKeys candidate
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
                                                    reindexRow table.Columns uniqueGroups None (Some(position, candidate)) index,
                                                    cascadeDb,
                                                    visited,
                                                    cascaded))))

                    rowsIn
                    |> foldWithCancellation step (Ok(table.NextAutoId, None, None, 0, [], [], table.UniqueIndex, db, Map.empty, Map.empty))
                    |> Result.map (fun (nextAutoId', firstAuto, lastExplicit, affected, inserted, updated, index, cascadeDb, _visited, cascaded) ->
                        let finalRows =
                            if newRows.Count = 0 then
                                ImmutableArray.CreateRange current
                            else
                                let builder = ImmutableArray.CreateBuilder(current.Length + newRows.Count)
                                builder.AddRange current
                                builder.AddRange newRows
                                builder.MoveToImmutable()

                        Map.add key { table with RowsArray = finalRows; NextAutoId = nextAutoId'; UniqueIndex = index } cascadeDb,
                        cascaded,
                        (Option.defaultValue 0L (Option.orElse lastExplicit firstAuto), firstAuto, affected, List.rev inserted, List.rev updated)))

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

/// `REPLACE` inserts each candidate after deleting every row that conflicts
/// with it on a primary or unique key. A candidate can therefore affect more
/// than two rows when separate unique keys point at separate stored rows.
/// Delete-side foreign-key actions run before the insert, and commit events
/// retain candidate order so WAL replay observes the same intermediate keys.
let replaceRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    (computeGenerated: Value[] -> Result<Value[], StorageError>)
    : Result<InsertOutcome, StorageError> =
    let key = normalizeTableName tableName

    let result =
        withDatabase store dbName (fun initialDb ->
            virtualWriteGuard store dbName tableName
            |> Result.bind (fun () -> tryGetTable initialDb tableName)
            |> Result.bind (fun initialTable ->
                resolveInsertColumns initialTable columns
                |> Result.bind (fun idxs ->
                    let originalNameOf tableKey =
                        initialDb
                        |> Map.tryFind tableKey
                        |> Option.map (fun table -> table.OriginalName)
                        |> Option.defaultValue tableKey

                    let commitEvents
                        (removed: Map<string, Value[] list>)
                        (blanked: Map<string, (Value[] * Value[]) list>)
                        (writeEvent: CommitEvent option)
                        =
                        let cascades =
                            [ for KeyValue(tableKey, rows) in removed do
                                  if not rows.IsEmpty then
                                      RowsDeleted(dbName, originalNameOf tableKey, rows)

                              for KeyValue(tableKey, changes) in blanked do
                                  if not changes.IsEmpty then
                                      RowsUpdated(dbName, originalNameOf tableKey, changes) ]

                        cascades @ Option.toList writeEvent

                    let step acc rowValues =
                        acc
                        |> Result.bind (fun (db,
                                             nextAutoId,
                                             firstAuto,
                                             lastExplicit,
                                             affected,
                                             inserted,
                                             events) ->
                            let table = Map.find key db

                            if List.length rowValues <> List.length idxs then
                                Error(ColumnCountMismatch(List.length idxs, List.length rowValues))
                            else
                                let provided = List.zip idxs rowValues |> Map.ofList
                                let rawRow = table.Columns |> List.mapi (fun i _ -> Map.tryFind i provided)

                                processRow store.StrictMode nextAutoId rawRow table.Columns
                                |> Result.bind (fun (finalValues, nextAutoId', assigned) ->
                                    computeGenerated (Array.ofList finalValues)
                                    |> Result.bind (fun candidate ->
                                        let uniqueGroups = uniqueKeyGroups table

                                        let conflicts =
                                            uniqueGroups
                                            |> List.indexed
                                            |> List.choose (fun (groupIndex, (name, indices)) ->
                                                encodeConstraintKey table.Columns indices candidate
                                                |> Option.bind (fun encoded ->
                                                    Map.tryFind encoded (Map.find name table.UniqueIndex)
                                                    |> Option.map (fun position -> groupIndex, position, table.RowsArray.[position])))
                                            |> List.fold
                                                (fun (seen, matches) ((_, position, _) as matched) ->
                                                    if Set.contains position seen then
                                                        seen, matches
                                                    else
                                                        Set.add position seen, matched :: matches)
                                                (Set.empty, [])
                                            |> snd
                                            |> List.rev

                                        let optimizedConflict =
                                            match List.tryLast conflicts with
                                            | Some(groupIndex, position, existing)
                                                when groupIndex = uniqueGroups.Length - 1
                                                     && (referencingForeignKeys db key).IsEmpty ->
                                                Some(position, existing)
                                            | _ -> None

                                        let deletedMatches =
                                            match optimizedConflict with
                                            | Some _ -> conflicts |> List.take (conflicts.Length - 1)
                                            | None -> conflicts

                                        let deletedConflicts = deletedMatches |> List.map (fun (_, _, row) -> row)

                                        cascadeDelete store.ForeignKeyChecks db key deletedConflicts
                                        |> Result.bind (fun (deletedDb, removed, blanked) ->
                                            let target = Map.find key deletedDb
                                            let target', writeEvent, weight =
                                                match optimizedConflict with
                                                | Some(position, existing) ->
                                                    let position =
                                                        position
                                                        - (deletedMatches
                                                           |> List.sumBy (fun (_, deletedPosition, _) -> if deletedPosition < position then 1 else 0))

                                                    let changed = existing <> candidate

                                                    { target with
                                                        RowsArray = target.RowsArray.SetItem(position, candidate)
                                                        NextAutoId = nextAutoId'
                                                        UniqueIndex =
                                                            reindexRow
                                                                target.Columns
                                                                (uniqueKeyGroups target)
                                                                (Some existing)
                                                                (Some(position, candidate))
                                                                target.UniqueIndex },
                                                    (if changed then Some(RowsUpdated(dbName, tableName, [ existing, candidate ])) else None),
                                                    deletedConflicts.Length + 1 + (if changed then 1 else 0)
                                                | None ->
                                                    let position = target.RowsArray.Length

                                                    { target with
                                                        RowsArray = target.RowsArray.Add candidate
                                                        NextAutoId = nextAutoId'
                                                        UniqueIndex =
                                                            reindexRow
                                                                target.Columns
                                                                (uniqueKeyGroups target)
                                                                None
                                                                (Some(position, candidate))
                                                                target.UniqueIndex },
                                                    Some(RowsInserted(dbName, tableName, [ candidate ])),
                                                    deletedConflicts.Length + 1

                                            let db' = Map.add key target' deletedDb

                                            (if store.ForeignKeyChecks then
                                                 checkFkParents db' target.Columns target.ForeignKeys candidate
                                             else
                                                 Ok())
                                            |> Result.map (fun () ->
                                                let firstAuto', lastExplicit' =
                                                    match assigned with
                                                    | Some(true, value) -> Option.orElse (Some value) firstAuto, lastExplicit
                                                    | Some(false, value) -> firstAuto, Some value
                                                    | None -> firstAuto, lastExplicit

                                                db',
                                                nextAutoId',
                                                firstAuto',
                                                lastExplicit',
                                                affected + weight,
                                                candidate :: inserted,
                                                events @ commitEvents removed blanked writeEvent)))))

                    rowsIn
                    |> foldWithCancellation
                        step
                        (Ok(initialDb, initialTable.NextAutoId, None, None, 0, [], []))
                    |> Result.map (fun (db, _, firstAuto, lastExplicit, affected, inserted, events) ->
                        let outcome =
                            { LastInsertId = Option.defaultValue 0L (Option.orElse lastExplicit firstAuto)
                              GeneratedId = firstAuto
                              Affected = affected
                              InsertedRows = List.rev inserted }

                        db, (outcome, events)))))

    match result with
    | Ok(outcome, events) ->
        events |> List.iter (Some >> emit store)
        Ok outcome
    | Error error -> Error error

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

            virtualWriteGuard store dbName tableName
            |> Result.bind (fun () -> tryGetTable db tableName)
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

                virtualWriteGuard store dbName tableName
                |> Result.bind (fun () -> tryGetTable db tableName)
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
                        |> Result.bind
                            (fun (changesRev: (Value[] * Value[]) list,
                                  index: Map<string, Map<string, int>>,
                                  cascadeDb: Database,
                                  visited: Map<string, Value[] list>,
                                  cascaded: Map<string, (Value[] * Value[]) list>) ->
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
                            | None -> Ok(changesRev, index, cascadeDb, visited, cascaded)
                            | Some rowPos ->
                                predicate row
                                |> Result.bind (fun keep ->
                                    if not keep then
                                        Ok(changesRev, index, cascadeDb, visited, cascaded)
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
                                                // `ON UPDATE CASCADE`/`SET NULL` rewrite/blank
                                                // any child row this rewrite would otherwise
                                                // orphan; anything else fails 1451.
                                                (if checkFks then
                                                     checkFkParents cascadeDb table.Columns table.ForeignKeys newRow
                                                     |> Result.bind (fun () -> cascadeUpdateVisited true cascadeDb visited cascaded key table.Columns row newRow)
                                                 else
                                                     Ok(cascadeDb, visited, cascaded))
                                                |> Result.map (fun (cascadeDb', visited', cascaded') ->
                                                    newRow, reindexRow table.Columns uniqueGroups (Some row) (Some(rowPos, newRow)) index, cascadeDb', visited', cascaded'))
                                        |> Result.map (fun (newRow, index', cascadeDb', visited', cascaded') ->
                                            builder.[rowPos] <- newRow
                                            (if newRow <> row then (row, newRow) :: changesRev else changesRev), index', cascadeDb', visited', cascaded')))

                    candidates
                    |> Option.defaultWith (fun () -> table.RowsArray |> Seq.indexed |> List.ofSeq)
                    |> foldWithCancellation step (Ok([], table.UniqueIndex, db, Map.empty, Map.empty))
                    // `DrainToImmutable`, not `MoveToImmutable`: the latter
                    // demands Count = Capacity, which an empty table's
                    // builder (capacity-8, count-0) never satisfies.
                    |> Result.map (fun (changesRev, index, cascadeDb, _visited, cascaded) ->
                        Map.add key { table with RowsArray = builder.DrainToImmutable(); UniqueIndex = index } cascadeDb, (List.rev changesRev, cascaded, db))))

        match result with
        | Ok(changes, cascaded, db) ->
            if not changes.IsEmpty then
                emit store (Some(RowsUpdated(dbName, tableName, changes)))

            // `ON UPDATE CASCADE`/`SET NULL` child rewrites this statement
            // triggered — same `RowsUpdated` shape a plain `UPDATE` reports
            // (see `cascadeUpdateVisited`'s doc).
            let originalNameOf tableKey = db |> Map.tryFind tableKey |> Option.map (fun t -> t.OriginalName) |> Option.defaultValue tableKey

            cascaded
            |> Map.iter (fun tableKey chg -> if not chg.IsEmpty then emit store (Some(RowsUpdated(dbName, originalNameOf tableKey, chg))))

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
/// `INSERT`/`UPDATE`. `VIRTUAL` and `STORED` are tracked
/// (`Ast.GeneratedKind`, surfaced in metadata) but both materialize into
/// `Rows` the same way, since this engine has no separate "recompute on
/// every read" path.

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

/// Puts already-committed rows back exactly as they were, for WAL replay.
/// Deliberately not `insertRows`: that enforces unique keys against indexes
/// `load` leaves stale until the whole WAL has been applied, so an INSERT
/// reusing a primary key an earlier DELETE freed replays as a duplicate-key
/// warning and the row is lost. These rows were accepted once already;
/// re-adjudicating them is the bug, not the safeguard.
///
/// Carries `NextAutoId` past anything the replayed rows used, so a later
/// insert can't reissue an id the WAL already handed out.
let appendRowsForReplay (store: Store) (dbName: string) (tableName: string) (rows: Value[] list) (onMissing: string -> unit) : unit =
    let key = normalizeTableName tableName

    match store.Databases.TryGetValue dbName with
    | false, _ -> onMissing (sprintf "unknown database '%s'" dbName)
    | true, slot ->
        match slot.Value |> Map.tryFind key with
        | None -> onMissing (sprintf "unknown table '%s.%s'" dbName tableName)
        | Some table ->
            let nextAutoId =
                match table.Columns |> List.tryFindIndex (fun column -> column.AutoIncrement) with
                | None -> table.NextAutoId
                | Some index ->
                    rows
                    |> List.fold
                        (fun next row ->
                            match row.[index] with
                            | VInt value when value < Int64.MaxValue -> max next (value + 1L)
                            | VUInt value when value < uint64 Int64.MaxValue -> max next (int64 value + 1L)
                            | _ -> next)
                        table.NextAutoId

            let updated =
                { table with
                    RowsArray = table.RowsArray.AddRange rows
                    NextAutoId = nextAutoId }

            slot.Value <- slot.Value |> Map.add key updated

/// Rebuilds every table's `UniqueIndex` from its current `Rows` across the
/// whole store, once — what `Persistence.load` calls after replaying the
/// WAL, since `replaceTablesForReplay` (`RowsUpdated`/`RowsDeleted` replay)
/// deliberately leaves `UniqueIndex` stale per-table rather than paying
/// `reindexTable`'s full-table rescan once per replayed event (see its doc).
/// Same single-threaded, pre-`attach` assumption as `replaceTablesForReplay`.
let reindexAllForReplay (store: Store) : unit =
    for KeyValue(_, slot) in store.Databases do
        slot.Value <- slot.Value |> Map.map (fun _ table -> reindexTable table)
