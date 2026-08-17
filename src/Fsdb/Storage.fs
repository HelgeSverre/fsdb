/// In-memory multi-database catalog: snapshot reads, serialized writes.
/// A `Catalog` is an immutable `Map`, so every read is a lock-free snapshot
/// and every write swaps in a brand new `Catalog` under a lock.
module Fsdb.Storage

open System
open System.Collections.Concurrent
open System.Collections.Generic
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
    | ExpressionError(code, message) -> code, message
    | DuplicateKey(keyName, value) -> 1062, sprintf "Duplicate entry '%s' for key '%s'" value keyName
    | ForeignKeyRestrict fkName ->
        1451, sprintf "Cannot delete or update a parent row: a foreign key constraint fails (`%s`)" fkName
    | ForeignKeyParentMissing fkName ->
        1452, sprintf "Cannot add or update a child row: a foreign key constraint fails (`%s`)" fkName

/// A table's rows, newest last. `OriginalName` keeps the as-created casing
/// for information_schema, even though the catalog keys tables by their
/// lowercased name. `Indexes`' `UNIQUE` entries (plus the primary key) are
/// enforced on every `INSERT`/`UPDATE`/`upsertRows` (see
/// `findUniqueCollision`); `ForeignKeys` are enforced on
/// `INSERT`/`UPDATE`/`DELETE` (see `checkFkParents`/`cascadeDelete`), gated
/// by `Store.ForeignKeyChecks`. Non-`UNIQUE` plain indexes remain metadata
/// only — nothing in this engine does index-accelerated lookup yet, every
/// scan is a full table scan.
type Table =
    { OriginalName: string
      Columns: ColumnDef list
      Rows: Value[] list
      NextAutoId: int64
      Indexes: IndexDef list
      ForeignKeys: ForeignKeyDef list }

/// Table names are case-insensitive, keyed by their lowercased form.
type Database = Map<string, Table>

/// Database names, as given, to a `Database`.
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
/// locks when parallel write throughput matters. A transaction that writes
/// across more than one database only holds the gate for the database
/// active when it entered — a cross-database transaction can still race a
/// concurrent writer in its *other* databases; upgrade to acquiring every
/// database a transaction actually touches if that ever matters (Laravel's
/// per-worker-database test parallelism, the case this exists for, never
/// does).
///
/// `ForeignKeyChecks` gates every FK enforcement in this module (cascading
/// deletes, `RESTRICT`, parent-existence checks on insert/update) — the
/// storage-level mirror of MySQL's session `FOREIGN_KEY_CHECKS` variable.
/// A single store-wide flag rather than per-session, since `Store` has no
/// session concept; `QueryHandler`'s `SET FOREIGN_KEY_CHECKS = 0|1` probe
/// (and Laravel's `Schema::disableForeignKeyConstraints`, which sends
/// exactly that) calls `setForeignKeyChecks`.
type Store =
    { mutable Catalog: Catalog
      mutable ForeignKeyChecks: bool
      /// The storage-level mirror of MySQL's session `sql_mode`
      /// STRICT_TRANS_TABLES/STRICT_ALL_TABLES: `true` rejects a value that
      /// doesn't fit its column's type with error 1366 (`coerceValue`'s
      /// default); `false` coerces to `coerceValue`'s non-strict fallback
      /// instead — 0 for a numeric column, NULL for a nullable temporal one
      /// (still a hard 1366 on a NOT NULL temporal column; see
      /// `coerceValue`'s doc for why). Store-wide, but re-derived from the
      /// *current* session's own `sql_mode` by `QueryHandler.executeStatement`
      /// before every statement, so it never leaks between connections or
      /// runs stale inside a transaction — see the note there and on
      /// `beginTransactionSnapshot`. `QueryHandler`'s `SET SESSION sql_mode =
      /// ...` probe (and Laravel's `'strict' => false` connection config,
      /// which sends `SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'`) calls
      /// `setStrictMode` directly too, so `SELECT @@sql_mode` right after a
      /// `SET` on the same connection reflects it immediately.
      mutable StrictMode: bool
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
      /// lazily by `enterTransactionGate`) rather than one store-wide —
      /// unrelated databases no longer block on each other's open
      /// transactions. A transaction acquires its database's gate on its
      /// first real database statement (not BEGIN) and releases it at
      /// COMMIT/ROLLBACK, which prevents the snapshot merger from replacing
      /// a concurrent writer's changes to the same table. SemaphoreSlim is
      /// intentionally used instead of Monitor because a connection's
      /// BEGIN, statements, and COMMIT may resume on different threads.
      TransactionGates: ConcurrentDictionary<string, SemaphoreSlim>
      /// Serializes `OnCommit`'s dispatch only (see `emit`) — every other
      /// write serializes per-database via `TransactionGates` and swaps
      /// `Catalog` lock-free (`Interlocked.CompareExchange`, see `withWrite`),
      /// so writers to different databases never wait on each other here.
      /// `OnCommit`'s one subscriber (`Persistence.attach`'s WAL appender)
      /// isn't safe to call concurrently from two databases' writer threads
      /// at once — it appends to one shared file and tracks rotation state
      /// in a plain `ref` — so its calls stay ordered by this lock, same as
      /// `Persistence.snapshotNow`'s own use of it.
      Lock: obj }

let create () : Store =
    { Catalog = Map.ofList [ defaultDatabase, Map.empty ]
      ForeignKeyChecks = true
      StrictMode = true
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
    { Catalog = store.Catalog
      ForeignKeyChecks = store.ForeignKeyChecks
      // Not seeded from `store.StrictMode` — `QueryHandler.executeStatement`
      // re-derives it from the session's own `sql_mode` before every
      // statement (see the note there), so whatever this starts as is
      // always overwritten before a transaction's first real statement
      // runs.
      StrictMode = true
      OnCommit = None
      PendingEvents = if store.OnCommit.IsSome then Some(ResizeArray()) else None
      TransactionGates = store.TransactionGates
      Lock = obj () }

/// Flushes a committed transaction's buffered events onto the real `store`
/// as one `TransactionCommitted`, if it buffered any — a no-op for an empty
/// or subscriber-less snapshot. Call under `store.Lock`, after merging
/// `snapshot`'s catalog back in (see `QueryHandler.commitSession`); there's
/// no rollback counterpart, since discarding `snapshot` discards its buffer
/// too.
let commitTransactionEvents (store: Store) (snapshot: Store) : unit =
    match snapshot.PendingEvents with
    | Some buffer when buffer.Count > 0 -> emit store (Some(TransactionCommitted(List.ofSeq buffer)))
    | _ -> ()

/// `SET FOREIGN_KEY_CHECKS = 0|1` — wired from `QueryHandler`'s `SET` probe
/// (see the note on `Store.ForeignKeyChecks`).
let setForeignKeyChecks (store: Store) (enabled: bool) : unit =
    lock store.Lock (fun () -> store.ForeignKeyChecks <- enabled)

/// `SET SESSION sql_mode = ...` — wired from `QueryHandler`'s `SET` probe
/// (see the note on `Store.StrictMode`). `strict` is whether the new mode
/// still contains STRICT_TRANS_TABLES/STRICT_ALL_TABLES.
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
/// pattern as `createTable`.
/// Retries `body` against a freshly re-read `store.Catalog` if its
/// `Interlocked.CompareExchange` swap loses a race to a concurrent writer on
/// a *different* database — see `withWrite`'s doc (defined below, once
/// `Catalog`'s write ops start needing the general form) for why a retry
/// here only ever means that, never self-contention.
let rec private createOrDropDatabase (store: Store) (body: Catalog -> Result<Catalog * CommitEvent, StorageError>) : Result<unit, StorageError> =
    let current = store.Catalog

    match body current with
    | Error e -> Error e
    | Ok(catalog', event) ->
        if obj.ReferenceEquals(Interlocked.CompareExchange(&store.Catalog, catalog', current), current) then
            emit store (Some event)
            Ok()
        else
            createOrDropDatabase store body

let createDatabase (store: Store) (dbName: string) : Result<unit, StorageError> =
    createOrDropDatabase store (fun catalog ->
        if Map.containsKey dbName catalog then
            Error(DatabaseExists dbName)
        else
            Ok(Map.add dbName Map.empty catalog, SchemaChanged(dbName, CreateDatabase(dbName, false))))

let dropDatabase (store: Store) (dbName: string) : Result<unit, StorageError> =
    createOrDropDatabase store (fun catalog ->
        if Map.containsKey dbName catalog then
            Ok(Map.remove dbName catalog, SchemaChanged(dbName, DropDatabase(dbName, false)))
        else
            Error(NoSuchDatabase dbName))

/// Applies `f` to the current catalog and atomically swaps the result in via
/// `Interlocked.CompareExchange`, retrying against a fresh catalog if a
/// concurrent writer's swap for a *different* database raced ahead in
/// between. No lock: `QueryHandler` already serializes every write to the
/// *same* database through its per-database `TransactionGates`
/// (`enterTransactionGate`) before it ever reaches here, so two calls into
/// this function for the same database never run concurrently — a retry
/// here only ever means "another database's writer got there first", which
/// this function alone can't avoid (the whole point is that unrelated
/// databases don't wait on each other) but costs only a cheap re-run of `f`
/// against the fresh catalog.
let rec private withWrite (store: Store) (f: Catalog -> Result<Catalog * 'a, StorageError>) : Result<'a, StorageError> =
    let current = store.Catalog

    match f current with
    | Error e -> Error e
    | Ok(catalog', result) ->
        if obj.ReferenceEquals(Interlocked.CompareExchange(&store.Catalog, catalog', current), current) then
            Ok result
        else
            withWrite store f

/// As `withWrite`, but for callers that already have the replacement
/// catalog in hand (`f` never fails) rather than computing it from the live
/// one — `mergeCatalogInto`/`bumpAutoIncrementsInto`'s shared CAS retry.
let rec private swapCatalog (store: Store) (f: Catalog -> Catalog) : unit =
    let current = store.Catalog
    let updated = f current

    if not (obj.ReferenceEquals(Interlocked.CompareExchange(&store.Catalog, updated, current), current)) then
        swapCatalog store f

/// Three-way merges an isolated unit of work's private snapshot catalog
/// back into a live one: for every (database, table) appearing in any of
/// the three, a table the batch actually wrote (its snapshot copy differs
/// from `baseCatalog`, the catalog it started from) wins; one it dropped
/// (present at the start, gone from the snapshot) is removed; one it never
/// touched is left exactly as `liveCatalog` (the shared store's catalog
/// *right now*, not as of the batch's start) already has it, so a
/// concurrent write to that table by another database's writer during the
/// batch's lifetime survives instead of being silently discarded by a stale
/// copy of it. Same three-way logic one level up for whole databases the
/// batch created/dropped. Shared by `QueryHandler`'s transaction commit and
/// `Executor`'s multi-table `UPDATE`, both of which run a private snapshot
/// store before merging its catalog back.
let mergeCatalogs (baseCatalog: Catalog) (batchCatalog: Catalog) (liveCatalog: Catalog) : Catalog =
    let keysOf (m: Map<string, 'a>) = m |> Map.toList |> List.map fst |> Set.ofList
    let dbKeys = Set.unionMany [ keysOf baseCatalog; keysOf batchCatalog; keysOf liveCatalog ]

    dbKeys
    |> Set.fold
        (fun acc dbName ->
            match Map.tryFind dbName baseCatalog, Map.tryFind dbName batchCatalog with
            | Some _, None -> Map.remove dbName acc // the batch dropped this database
            | None, Some batchDb -> Map.add dbName batchDb acc // the batch created this database
            | None, None -> acc // the batch never saw this database; leave the live entry alone
            | Some baseDb, Some batchDb ->
                // Existed both before and after the batch (whether or not it
                // touched any table in it) — merge table-by-table against
                // the *live* catalog's current version of the database, not
                // the batch's, so a concurrent write to an untouched table
                // survives.
                let liveDb = Map.tryFind dbName liveCatalog |> Option.defaultValue Map.empty
                let tableKeys = Set.unionMany [ keysOf baseDb; keysOf batchDb; keysOf liveDb ]

                let mergedDb =
                    tableKeys
                    |> Set.fold
                        (fun tacc tableName ->
                            match Map.tryFind tableName baseDb, Map.tryFind tableName batchDb with
                            | Some _, None -> Map.remove tableName tacc // dropped by the batch
                            | None, Some t -> Map.add tableName t tacc // created by the batch
                            | Some baseT, Some batchT when baseT <> batchT -> Map.add tableName batchT tacc // modified by the batch
                            | _ -> tacc // untouched by the batch — keep whatever's live
                        )
                        liveDb

                Map.add dbName mergedDb acc)
        liveCatalog

/// Merges `batchCatalog` (built from `baseCatalog` by some isolated unit of
/// work — a committing transaction, or a multi-table statement's private
/// snapshot store) into `store`'s live catalog via `mergeCatalogs`, retrying
/// against a fresh live catalog if a concurrent writer to an unrelated
/// database swapped in between (see `swapCatalog`).
let mergeCatalogInto (store: Store) (baseCatalog: Catalog) (batchCatalog: Catalog) : unit =
    swapCatalog store (mergeCatalogs baseCatalog batchCatalog)

/// Bumps the live catalog's AUTO_INCREMENT counters up to a discarded
/// transaction's snapshot wherever it ran one ahead — MySQL never rolls
/// back a burned id (see `QueryHandler.rollbackSession`'s doc) — leaving
/// everything else (rows, schema) exactly as the live catalog has it. Same
/// CAS retry as `mergeCatalogInto`.
let bumpAutoIncrementsInto (store: Store) (snapshotCatalog: Catalog) : unit =
    swapCatalog store (fun liveCatalog ->
        snapshotCatalog
        |> Map.fold
            (fun liveCatalog dbName snapshotDb ->
                match Map.tryFind dbName liveCatalog with
                | None -> liveCatalog
                | Some liveDb ->
                    let mergedDb =
                        snapshotDb
                        |> Map.fold
                            (fun acc tableName (snapshotTable: Table) ->
                                match Map.tryFind tableName acc with
                                | Some(liveTable: Table) when snapshotTable.NextAutoId > liveTable.NextAutoId ->
                                    Map.add tableName { liveTable with NextAutoId = snapshotTable.NextAutoId } acc
                                | _ -> acc)
                            liveDb

                    Map.add dbName mergedDb liveCatalog)
            liveCatalog)

let private tryGetDatabase (catalog: Catalog) (dbName: string) : Result<Database, StorageError> =
    match Map.tryFind dbName catalog with
    | Some db -> Ok db
    | None -> Error(NoSuchDatabase dbName)

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
/// `QueryHandler`'s `Use` probe).
let rec ensureDatabase (store: Store) (dbName: string) : unit =
    let current = store.Catalog

    if not (Map.containsKey dbName current) then
        let updated = Map.add dbName Map.empty current

        if not (obj.ReferenceEquals(Interlocked.CompareExchange(&store.Catalog, updated, current), current)) then
            ensureDatabase store dbName

/// Whether `dbName` is a real catalog entry, or the always-present virtual
/// `information_schema` — what `USE`/`COM_INIT_DB` check to match real
/// MySQL's `ERROR 1049 Unknown database` instead of silently accepting (and
/// then auto-vivifying on first write, via `ensureDatabase`) a typo'd or
/// missing name.
let databaseExists (store: Store) (dbName: string) : bool =
    String.Equals(dbName, "information_schema", StringComparison.OrdinalIgnoreCase)
    || Map.containsKey dbName store.Catalog

/// Index of a column by name, case-insensitive.
let resolveColumn (columns: ColumnDef list) (name: string) : Result<int, StorageError> =
    columns
    |> List.tryFindIndex (fun c -> String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
    |> function
        | Some i -> Ok i
        | None -> Error(UnknownColumn name)

/// Applies `f` to each element, short-circuiting on the first `Error` —
/// generalized over any error type (not just `StorageError`) and public, so
/// `Executor` reuses this tail-recursive traversal instead of keeping its
/// own non-tail-recursive copy.
let traverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
    let rec loop acc =
        function
        | [] -> Ok(List.rev acc)
        | x :: rest ->
            match f x with
            | Ok y -> loop (y :: acc) rest
            | Error e -> Error e

    loop [] xs

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
        | TJson -> Ok(VString(v |> toText |> Option.defaultValue ""))
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
            match v with
            | VString s when values |> List.exists (fun allowed -> String.Equals(allowed, s, StringComparison.OrdinalIgnoreCase)) ->
                Ok(VString s)
            // MySQL also accepts a 1-based index into the declared value list.
            | VInt i when i >= 1L && i <= int64 (List.length values) -> Ok(VString values.[int i - 1])
            | _ -> fail ()
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
    | Some DCurrentTimestamp -> VDateTime DateTime.Now

/// Coerces a value to its column's type and rejects NULL for a non-nullable
/// column.
let private coerceAndCheck (strict: bool) (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    match v with
    | VNull when not col.Nullable -> Error(NotNullViolation col.Name)
    | _ -> coerceValue strict col v

/// Runs `f` against `dbName`'s database, swapping the updated database back
/// into the catalog on success. Every write op boils down to "look up a
/// database, then a plain update" — this is the one seam `withWrite`'s
/// callers actually vary on, factored out so each op below is just its own
/// two lines of logic instead of a hand-rolled hierarchy of hasErrord binds.
let private withDatabase
    (store: Store)
    (dbName: string)
    (f: Database -> Result<Database * 'a, StorageError>)
    : Result<'a, StorageError> =
    withWrite store (fun catalog ->
        tryGetDatabase catalog dbName
        |> Result.bind (fun db -> f db |> Result.map (fun (db', result) -> Map.add dbName db' catalog, result)))

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
                      Rows = []
                      NextAutoId = 1L
                      Indexes = indexes
                      ForeignKeys = foreignKeys }

                Ok(Map.add key table db, ()))

    if result.IsOk then
        emit store (Some(SchemaChanged(dbName, CreateTable(tableName, columns, indexes, foreignKeys, false))))

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
    let result = withTable store dbName tableName (fun table -> Ok({ table with Rows = []; NextAutoId = 1L }, ()))

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

        resolvePosition table.Columns (List.length table.Columns) position
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> insertAt idx col
                Rows = table.Rows |> List.map (fun r -> r |> Array.toList |> insertAt idx fill |> Array.ofList) },
            None)
    | DropColumn name ->
        resolveColumn table.Columns name
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> List.indexed |> List.filter (fun (i, _) -> i <> idx) |> List.map snd
                Rows = table.Rows |> List.map (removeColumnAt idx) },
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
                    Rows =
                        table.Rows
                        |> List.map (fun r ->
                            let v = r.[oldIdx]
                            r |> removeColumnAt oldIdx |> Array.toList |> insertAt newIdx v |> Array.ofList) },
                None))
    | ChangeColumn(oldName, newDef, position) ->
        resolveColumn table.Columns oldName
        |> Result.bind (fun oldIdx ->
            let columnsExcludingSelf = table.Columns |> List.indexed |> List.filter (fun (i, _) -> i <> oldIdx) |> List.map snd

            resolvePosition columnsExcludingSelf oldIdx position
            |> Result.map (fun newIdx ->
                { table with
                    Columns = columnsExcludingSelf |> insertAt newIdx newDef
                    Rows =
                        table.Rows
                        |> List.map (fun r ->
                            let v = r.[oldIdx]
                            r |> removeColumnAt oldIdx |> Array.toList |> insertAt newIdx v |> Array.ofList) },
                None))
    | RenameTo newName -> Ok({ table with OriginalName = newName }, Some(normalizeTableName newName))
    | RenameColumnTo(oldName, newName) ->
        resolveColumn table.Columns oldName
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> List.mapi (fun i c -> if i = idx then { c with Name = newName } else c) },
            None)
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
                |> Result.map (fun (finalKey, finalTable) -> Map.remove origKey db |> Map.add finalKey finalTable, ())))

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

/// The `(keyName, column indices)` groups that must be unique: the primary
/// key (if any, named `"PRIMARY"` the way MySQL reports it in error 1062,
/// and treated as one group across however many columns it spans) plus
/// every `UNIQUE` index, named after itself. Used by `upsertRows` to find
/// the row (if any) an incoming `INSERT ... ON DUPLICATE KEY UPDATE` row
/// collides with, and by `findUniqueCollision` for plain `INSERT`/`UPDATE`.
let private uniqueKeyGroups (table: Table) : (string * int list) list =
    let pk =
        table.Columns |> List.indexed |> List.choose (fun (i, c) -> if c.PrimaryKey then Some i else None)

    let fromIndexes =
        table.Indexes
        |> List.filter (fun ix -> ix.Unique)
        |> List.choose (fun ix -> ix.Columns |> traverse (resolveColumn table.Columns) |> Result.toOption |> Option.map (fun idxs -> ix.Name, idxs))

    (if pk.IsEmpty then [] else [ "PRIMARY", pk ]) @ fromIndexes

/// Whether `a` and `b` collide on unique-key group `idxs`: every column
/// compares equal under `Value.compare`'s collation-aware rules (so
/// `'Alice' = 'alice'` and `'a' = 'a '` collide, matching MySQL's default
/// collation), *unless* any column in the group is `NULL` on either side —
/// MySQL's unique indexes treat `NULL` as distinct from every other `NULL`,
/// so a `NULL` anywhere in the group means "no collision" rather than "not
/// equal, so no collision" (the difference matters for `IS NULL` groups: two
/// all-NULL rows still don't collide).
let private rowsCollideOn (idxs: int list) (a: Value[]) (b: Value[]) : bool =
    idxs |> List.forall (fun i -> a.[i] <> VNull && b.[i] <> VNull && compare a.[i] b.[i] = 0)

/// The first unique-key violation `candidate` has against `existingRows`, if
/// any, as the `DuplicateKey` error 1062 wraps (the colliding key's name and
/// a MySQL-style `-`-joined value for composite keys).
let private findUniqueCollision (groups: (string * int list) list) (existingRows: Value[] list) (candidate: Value[]) : StorageError option =
    existingRows
    |> List.tryPick (fun existing ->
        groups
        |> List.tryPick (fun (name, idxs) ->
            if rowsCollideOn idxs existing candidate then
                let value =
                    idxs |> List.map (fun i -> candidate.[i] |> toText |> Option.defaultValue "NULL") |> String.concat "-"

                Some(DuplicateKey(name, value))
            else
                None))

/// Stable equality key for values already coerced into a table column's
/// declared type. Strings use the same case-insensitive, PAD SPACE semantics
/// as Value.compare; every other same-typed value uses an exact encoding.
/// NULL is deliberately absent because a UNIQUE key containing any NULL
/// never collides under MySQL's semantics.
let private encodeConstraintKey (indices: int list) (row: Value[]) : string option =
    let encode =
        function
        | VNull -> None
        | VInt value -> Some("I" + string value)
        | VDouble value ->
            let normalized = if value = 0.0 then 0.0 else value
            Some("D" + normalized.ToString("R", CultureInfo.InvariantCulture))
        | VDecimal value -> Some("M" + value.ToString("G29", CultureInfo.InvariantCulture))
        | VString value -> Some("S" + value.TrimEnd(' ').ToUpperInvariant())
        | VBytes value -> Some("B" + Convert.ToHexString value)
        | VDate value -> Some("T" + string value.DayNumber)
        | VDateTime value -> Some("V" + string value.Ticks)
        | VJson value -> Some("J" + value.TrimEnd(' ').ToUpperInvariant())

    let encoded = indices |> List.map (fun index -> encode row.[index])

    if encoded |> List.exists Option.isNone then
        None
    else
        encoded
        |> List.choose id
        |> List.map (fun value -> string value.Length + ":" + value)
        |> String.concat ""
        |> Some

let private constraintLookup indices rows =
    let lookup = HashSet<string>(StringComparer.Ordinal)

    for row in rows do
        encodeConstraintKey indices row |> Option.iter (lookup.Add >> ignore)

    lookup

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
                    let found =
                        parent.Rows
                        |> List.exists (fun prow -> List.forall2 (fun i v -> compare prow.[i] v = 0) refIdxs values)

                    if found then Ok() else Error(ForeignKeyParentMissing fk.Name)

let private checkFkParents (db: Database) (childColumns: ColumnDef list) (fks: ForeignKeyDef list) (row: Value[]) : Result<unit, StorageError> =
    fks |> traverse (checkFkParent db childColumns row) |> Result.map ignore

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
    let uniqueLookups =
        uniqueGroups
        |> List.map (fun (name, indices) -> name, indices, constraintLookup indices table.Rows)

    // Parent keys are immutable for the duration of this INSERT. Build one
    // compact lookup per ordinary FK instead of rescanning its parent table
    // for every child candidate. A self-FK also records its parent-column
    // indices so every accepted row can extend the lookup immediately.
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
                        let selfParentIndices =
                            if normalizeTableName foreignKey.RefTable = tableKey then Some parentIndices else None

                        Some(foreignKey.Name, (childIndices, selfParentIndices, constraintLookup parentIndices parent.Rows))
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
        |> Result.bind (fun (acceptedRev: Value[] list, nextAutoId, firstAuto, lastExplicit) ->
            if List.length rowValues <> List.length idxs then
                Error(ColumnCountMismatch(List.length idxs, List.length rowValues))
            else
                let provided = List.zip idxs rowValues |> Map.ofList
                let rawRow = table.Columns |> List.mapi (fun i _ -> Map.tryFind i provided)

                let rowResult =
                    processRow strict nextAutoId rawRow table.Columns
                    |> Result.bind (fun (finalValues, nextAutoId', assigned) ->
                        let candidate = Array.ofList finalValues

                        // Avoid constructing `table.Rows @ accepted` for
                        // every candidate. On an unkeyed volume table there
                        // is nothing to inspect at all; on a keyed table the
                        // two existing-row partitions can be searched in
                        // turn without copying either one.
                        let uniqueCollision =
                            uniqueLookups
                            |> List.tryPick (fun (name, indices, lookup) ->
                                match encodeConstraintKey indices candidate with
                                | Some key when lookup.Contains key ->
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
                                // `findUniqueCollision` just above.
                                // Ordinary parent tables need no overlay.
                                // Only a self-FK needs rows accepted earlier
                                // in this statement made visible, in their
                                // original insertion order.
                                let dbView =
                                    if hasUnacceleratedSelfForeignKey && not acceptedRev.IsEmpty then
                                        Map.add tableKey { table with Rows = table.Rows @ List.rev acceptedRev } db
                                    else
                                        db

                                let checkOneForeignKey foreignKey =
                                    match Map.tryFind foreignKey.Name foreignKeyLookups with
                                    | Some(childIndices, _, parentKeys) ->
                                        match encodeConstraintKey childIndices candidate with
                                        | None -> Ok()
                                        | Some key when parentKeys.Contains key -> Ok()
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

                    for _, indices, lookup in uniqueLookups do
                        encodeConstraintKey indices candidate |> Option.iter (lookup.Add >> ignore)

                    for KeyValue(_, (_, selfParentIndices, lookup)) in foreignKeyLookups do
                        selfParentIndices
                        |> Option.bind (fun indices -> encodeConstraintKey indices candidate)
                        |> Option.iter (lookup.Add >> ignore)

                    // Prepending is O(1); reverse once after the fold so
                    // externally observable insertion and commit-event
                    // order remains unchanged.
                    Ok(candidate :: acceptedRev, nextAutoId', firstAuto', lastExplicit')
                | Error _ when ignoreErrors -> Ok(acceptedRev, nextAutoId, firstAuto, lastExplicit)
                | Error e -> Error e)

    rowsIn
    |> List.fold step (Ok([], table.NextAutoId, None, None))
    |> Result.map (fun (acceptedRev, nextAutoId', firstAuto, lastExplicit) ->
        let accepted = List.rev acceptedRev
        let firstAssigned = Option.orElse lastExplicit firstAuto
        let table' = { table with Rows = table.Rows @ accepted; NextAutoId = nextAutoId' }
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

/// `INSERT ... ON DUPLICATE KEY UPDATE`: like `insertRows`, but a candidate
/// row that collides with an existing row on any unique key or the primary
/// key is applied to `applyUpdate existingRow candidateRow` instead of being
/// appended. Collision detection is collation-aware (`rowsCollideOn`), same
/// as plain `INSERT`'s unique check.
let upsertRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    (computeGenerated: Value[] -> Result<Value[], StorageError>)
    (applyUpdate: Value[] -> Value[] -> Result<Value[], StorageError>)
    : Result<int64 * int64 option * int, StorageError> =
        let result =
            withTable store dbName tableName (fun table ->
                let indices =
                    match columns with
                    | None -> Ok [ 0 .. table.Columns.Length - 1 ]
                    | Some names -> names |> traverse (resolveColumn table.Columns)

                indices
                |> Result.bind (fun idxs ->
                    let keySets = uniqueKeyGroups table |> List.map snd

                    let findMatch (rows: Value[] list) (candidate: Value[]) =
                        rows |> List.tryFind (fun existing -> keySets |> List.exists (fun ks -> rowsCollideOn ks existing candidate))

                    let step acc (rowValues: Value list) =
                        acc
                        |> Result.bind
                            (fun (rowsAcc: Value[] list, nextAutoId, firstAuto, lastExplicit, affected, inserted: Value[] list, updated: (Value[] * Value[]) list) ->
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
                                        // fills it in before `findMatch`/`rowsCollideOn`
                                        // run, so ON DUPLICATE KEY UPDATE actually finds
                                        // the collision instead of degrading into a
                                        // plain INSERT that then trips the unique check.
                                        computeGenerated (Array.ofList finalValues)
                                        |> Result.map (fun candidate ->
                                            match findMatch rowsAcc candidate with
                                            | Some existing -> Choice1Of2(existing, candidate)
                                            | None -> Choice2Of2 candidate)
                                        |> Result.bind (function
                                            | Choice1Of2(existing, candidate) ->
                                                applyUpdate existing candidate
                                                |> Result.map (fun applied ->
                                                    (rowsAcc |> List.map (fun r -> if r = existing then applied else r)),
                                                    nextAutoId',
                                                    firstAuto,
                                                    lastExplicit,
                                                    affected + 1,
                                                    inserted,
                                                    (existing, applied) :: updated)
                                            | Choice2Of2 candidate ->
                                                // Same "first generated, else last explicit"
                                                // `last_insert_id` rule `insertCore` uses —
                                                // see its doc.
                                                let firstAuto', lastExplicit' =
                                                    match assigned with
                                                    | Some(true, v) -> Option.orElse (Some v) firstAuto, lastExplicit
                                                    | Some(false, v) -> firstAuto, Some v
                                                    | None -> firstAuto, lastExplicit

                                                Ok(
                                                    rowsAcc @ [ candidate ],
                                                    nextAutoId',
                                                    firstAuto',
                                                    lastExplicit',
                                                    affected + 1,
                                                    candidate :: inserted,
                                                    updated
                                                ))))

                    rowsIn
                    |> List.fold step (Ok(table.Rows, table.NextAutoId, None, None, 0, [], []))
                    |> Result.map (fun (rows', nextAutoId', firstAuto, lastExplicit, affected, inserted, updated) ->
                        { table with Rows = rows'; NextAutoId = nextAutoId' },
                        (Option.defaultValue 0L (Option.orElse lastExplicit firstAuto), firstAuto, affected, List.rev inserted, List.rev updated))))

        match result with
        | Ok(lastId, generatedId, affected, inserted, updated) ->
            if not inserted.IsEmpty then
                emit store (Some(RowsInserted(dbName, tableName, inserted)))

            if not updated.IsEmpty then
                emit store (Some(RowsUpdated(dbName, tableName, updated)))

            Ok(lastId, generatedId, affected)
        | Error e -> Error e

let private coerceRow (strict: bool) (columns: ColumnDef list) (row: Value[]) : Result<Value[], StorageError> =
    List.zip columns (Array.toList row)
    |> traverse (fun (col, v) -> coerceAndCheck strict col v)
    |> Result.map Array.ofList

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
/// real cascading UPDATE if a migration ever depends on it.
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
                            childTbl.Rows
                            |> List.exists (fun row -> List.forall2 (fun i v -> compare row.[i] v = 0) childIdxs oldKey)

                        if stillReferenced then Error(ForeignKeyRestrict fk.Name) else Ok()

    referencingForeignKeys db tableKey |> traverse checkOne |> Result.map ignore

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
    (tableKey: string)
    (toDelete: Value[] list)
    : Result<Database * Map<string, Value[] list>, StorageError> =
    let alreadyVisited = visited |> Map.tryFind tableKey |> Option.defaultValue []
    let toDelete = toDelete |> List.filter (fun row -> not (alreadyVisited |> List.exists ((=) row)))

    // Removes one row per entry in `toDelete`, not every structurally-equal
    // row: two identical rows are distinct rows, and a `DELETE ... LIMIT n`
    // (or a cascaded child match) may legitimately match only one of them.
    let removeFrom (d: Database) =
        let t = Map.find tableKey d

        let kept, _ =
            t.Rows
            |> List.fold
                (fun (kept, pending) row ->
                    match pending |> List.tryFindIndex ((=) row) with
                    | Some i -> kept, List.removeAt i pending
                    | None -> row :: kept, pending)
                ([], toDelete)

        Map.add tableKey { t with Rows = List.rev kept } d

    if toDelete.IsEmpty then
        Ok(db, visited)
    else
        let visited = visited |> Map.add tableKey (alreadyVisited @ toDelete)

        if not checkFks then
            Ok(removeFrom db, visited)
        else
            let table = Map.find tableKey db

            let applyChild acc (childKey: string, fk: ForeignKeyDef) =
                acc
                |> Result.bind (fun (d, visited) ->
                    let childTbl = Map.find childKey d

                    match fk.Columns |> traverse (resolveColumn childTbl.Columns), fk.RefColumns |> traverse (resolveColumn table.Columns) with
                    | Error _, _
                    | _, Error _ -> Ok(d, visited) // stale FK metadata — see `checkFkParents`'s note.
                    | Ok childIdxs, Ok refIdxs ->
                        let parentKeys = toDelete |> List.map (fun row -> refIdxs |> List.map (fun i -> row.[i]))

                        let isChild (row: Value[]) =
                            let key = childIdxs |> List.map (fun i -> row.[i])

                            key |> List.forall ((<>) VNull)
                            && parentKeys |> List.exists (List.forall2 (fun a b -> compare a b = 0) key)

                        let matching = childTbl.Rows |> List.filter isChild

                        if matching.IsEmpty then
                            Ok(d, visited)
                        else
                            match fk.OnDelete |> Option.map (fun s -> s.Trim().ToUpperInvariant()) with
                            | Some "CASCADE" -> cascadeDeleteVisited checkFks d visited childKey matching
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
                                    let blanked row =
                                        if isChild row then
                                            let row' = Array.copy row
                                            childIdxs |> List.iter (fun i -> row'.[i] <- VNull)
                                            row'
                                        else
                                            row

                                    Ok(Map.add childKey { childTbl with Rows = childTbl.Rows |> List.map blanked } d, visited)
                            | _ -> Error(ForeignKeyRestrict fk.Name))

            referencingForeignKeys db tableKey
            |> List.fold applyChild (Ok(db, visited))
            |> Result.map (fun (d, visited) -> removeFrom d, visited)

/// As `cascadeDeleteVisited`, seeded with an empty `visited` — its second
/// return value is every row actually removed, by table key, including
/// `tableKey` itself and every table a `CASCADE` reached, for `deleteRows`
/// to report as `RowsDeleted` events. ponytail: `ON DELETE SET NULL`
/// blanks child rows in place rather than deleting them, so those changes
/// aren't in `visited` and don't get their own `RowsUpdated` event yet —
/// add that (thread a similar accumulator through the `"SET NULL"` branch
/// above) once a migration's FK actually uses it.
let private cascadeDelete (checkFks: bool) (db: Database) (tableKey: string) (toDelete: Value[] list) : Result<Database * Map<string, Value[] list>, StorageError> =
    cascadeDeleteVisited checkFks db Map.empty tableKey toDelete

/// Deletes every row matching `predicate`. Returns the number of rows
/// removed. `predicate` returns a `Result` rather than a plain `bool` so a
/// per-row WHERE-evaluation failure (not reachable today — every `Value`
/// operation is total — but a real possibility once functions that can
/// fail per row land) surfaces as an `Error` instead of silently being
/// treated as "didn't match". When `store.ForeignKeyChecks` is set (the
/// default), applies every referencing foreign key's `ON DELETE` action —
/// see `cascadeDelete`.
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
                table.Rows
                |> traverse (fun row -> predicate row |> Result.map (fun keep -> keep, row))
                |> Result.bind (fun flagged ->
                    let toDelete = flagged |> List.filter fst |> List.map snd

                    cascadeDelete store.ForeignKeyChecks db key toDelete
                    |> Result.map (fun (db', removed) -> db', (toDelete.Length, db, removed)))))

    match result with
    | Ok(affected, db, removed) ->
        removed
        |> Map.iter (fun tableKey rows ->
            if not rows.IsEmpty then
                let originalName = db |> Map.tryFind tableKey |> Option.map (fun t -> t.OriginalName) |> Option.defaultValue tableKey
                emit store (Some(RowsDeleted(dbName, originalName, rows))))

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
let updateRows
    (store: Store)
    (dbName: string)
    (tableName: string)
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
                    let original = Array.ofList table.Rows

                    // Folds left-to-right, threading the rows already written this
                    // statement (`doneRows`, holding their *new* values) alongside
                    // the rows not yet reached (their still-*original* values) —
                    // mirrors `insertCore`'s `table.Rows @ accepted` pattern, so a
                    // multi-row `UPDATE` that moves several rows onto the same
                    // unique value collides with a sibling row this same statement
                    // already rewrote, not just against the frozen pre-statement
                    // snapshot. `changes` collects only the rows actually rewritten
                    // (before, after) — for `RowsUpdated`, and for `changedCount`
                    // (its length), matching MySQL's "Changed: n" rather than "Rows
                    // matched: n".
                    let step acc (i, row) =
                        acc
                        |> Result.bind (fun (doneRows: Value[] list, changes: (Value[] * Value[]) list) ->
                            predicate row
                            |> Result.bind (fun keep ->
                                if not keep then
                                    Ok(doneRows @ [ row ], changes)
                                else
                                    updater row
                                    |> Result.bind (coerceRow store.StrictMode table.Columns)
                                    |> Result.bind (fun newRow ->
                                        let notYetProcessed =
                                            original |> Array.indexed |> Array.filter (fun (j, _) -> j > i) |> Array.map snd |> List.ofArray

                                        let others = doneRows @ notYetProcessed

                                        match findUniqueCollision uniqueGroups others newRow with
                                        | Some e -> Error e
                                        | None ->
                                            if checkFks then
                                                checkFkParents db table.Columns table.ForeignKeys newRow
                                                |> Result.bind (fun () -> checkNotOrphaning db key table.Columns row newRow)
                                                |> Result.map (fun () -> newRow)
                                            else
                                                Ok newRow)
                                    |> Result.map (fun newRow ->
                                        doneRows @ [ newRow ], (if newRow <> row then changes @ [ row, newRow ] else changes))))

                    original
                    |> List.ofArray
                    |> List.indexed
                    |> List.fold step (Ok([], []))
                    |> Result.map (fun (rows', changes) -> Map.add key { table with Rows = rows' } db, changes)))

        match result with
        | Ok changes ->
            if not changes.IsEmpty then
                emit store (Some(RowsUpdated(dbName, tableName, changes)))

            Ok changes.Length
        | Error e -> Error e

/// A snapshot read: the table's columns and its rows as they were at the
/// moment of the call. Lock-free — reads a single reference field, and
/// later writes swap in a new `Catalog` without mutating this snapshot's
/// row list.
let scan (store: Store) (dbName: string) (tableName: string) : Result<ColumnDef list * Value[] seq, StorageError> =
    let catalog = store.Catalog

    match tryGetDatabase catalog dbName with
    | Error e -> Error e
    | Ok db ->
        match tryGetTable db tableName with
        | Error e -> Error e
        | Ok table -> Ok(table.Columns, Seq.ofList table.Rows)

/// Generated/virtual columns (`CREATE TABLE ... col AS (expr) [STORED |
/// VIRTUAL]`) — `Ast.ColumnDef.Generated` carries the parsed `Expr`, but
/// evaluating it needs `Executor.evalExpr` (a whole registry/row-context
/// this module doesn't have), so the actual recompute-on-write lives in
/// `Executor.recomputeGeneratedColumns`, called after every successful
/// `INSERT`/`UPDATE`. `VIRTUAL` isn't distinguished from `STORED` — both
/// are persisted in `Rows` the same way, since this engine has no separate
/// "recompute on every read" path.
