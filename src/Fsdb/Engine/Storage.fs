/// In-memory multi-database catalog with lock-free immutable read snapshots.
/// Writers prepare private roots concurrently and publish through the owning
/// database slot; indexed point updates additionally coordinate stable rows.
module Fsdb.Storage

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Collections.Immutable
open System.Globalization
open System.Text
open System.Threading
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Temporal

/// Raised when an optimistic transaction merge finds a row or schema that
/// changed after the transaction snapshot was taken. `QueryHandler` reports
/// it as MySQL error 1205 so callers can retry the transaction.
exception LockWaitTimeout of dbName: string

/// Storage-layer failures, mapped to MySQL error codes by `toMySqlError`.
/// `ExpressionError` carries an already-formed MySQL (code, message) pair
/// through from `Executor`'s row-level expression evaluation (e.g. an
/// `UPDATE ... SET` right-hand side). `Storage` doesn't know that
/// vocabulary, so the failure travels through the same
/// `Result<_, StorageError>` path as every other write error.
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
    | DataTooLongForColumn of column: string * row: int
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
    | InvalidDefaultValue of column: string
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
    | DataTooLongForColumn(column, row) -> 1406, sprintf "Data too long for column '%s' at row %d" column row
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
    | InvalidDefaultValue column -> 1067, sprintf "Invalid default value for '%s'" column
    // MySQL's ER_OPEN_AS_READONLY — the closest real vocabulary for "this
    // table exists but refuses writes".
    | VirtualTableReadOnly name -> 1036, sprintf "Table '%s' is read only" name

let private compareIndexedValues (collationName: string option) (left: Value) (right: Value) =
    match left, right with
    | VString left, VString right ->
        collationName
        |> Option.bind Collation.tryFind
        |> Option.defaultValue Collation.defaultCollation
        |> fun collation -> collation.Compare left right
    | _ -> compareTotal left right

let private compareIndexedKeys (collations: string option list) (left: Value list) (right: Value list) =
    List.zip3 collations left right
    |> List.fold (fun comparison (collation, left, right) ->
        if comparison = 0 then compareIndexedValues collation left right else comparison) 0

[<CustomEquality; CustomComparison>]
type SecondaryOrderEntry = private { CollationNames: string option list; Values: Value list; RowId: RowId } with

    override this.Equals other =
        match other with
        | :? SecondaryOrderEntry as other ->
            this.CollationNames = other.CollationNames
            && compareIndexedKeys this.CollationNames this.Values other.Values = 0
            && this.RowId = other.RowId
        | _ -> false

    override this.GetHashCode() = hash this.CollationNames

    interface IComparable with
        member this.CompareTo other =
            match other with
            | :? SecondaryOrderEntry as other ->
                match Operators.compare this.CollationNames other.CollationNames with
                | 0 ->
                    match compareIndexedKeys this.CollationNames this.Values other.Values with
                    | 0 -> Operators.compare this.RowId other.RowId
                    | comparison -> comparison
                | comparison -> comparison
            | _ -> invalidArg "other" "SecondaryOrderEntry expected"

type SecondaryOrder = Map<string, ImmutableSortedSet<SecondaryOrderEntry>>
type FullTextIndexes = Map<string, FullText.Index<RowId>>

type private SecondaryOrderSlice =
    { IndexName: string
      ColumnIndices: int list
      Entries: ImmutableSortedSet<SecondaryOrderEntry>
      First: int
      AfterLast: int }

/// A table's rows, newest last. `OriginalName` keeps the as-created casing
/// for information_schema, even though the catalog keys tables by their
/// lowercased name. `Indexes`' `UNIQUE` entries (plus the primary key) are
/// enforced via `UniqueIndex` on every `INSERT`/`UPDATE`/`upsertRows`/
/// `DELETE`; `ForeignKeys` are enforced on `INSERT`/`UPDATE`/`DELETE` (see
/// `checkFkParents`/`cascadeDelete`, also `UniqueIndex`-accelerated on the
/// parent side), gated by `Store.ForeignKeyChecks`.
type Table =
    { OriginalName: string
      Columns: ColumnDef list
      /// Published pages are immutable so captured catalog roots remain
      /// valid while a write copies only the pages it changes.
      RowsArray: RowStore<Value[]>
      NextAutoId: int64
      Indexes: IndexDef list
      ForeignKeys: ForeignKeyDef list
      /// The table's own declared `[DEFAULT] CHARSET`/`COLLATE` options
      /// (`None` = server default) — rendered by `SHOW CREATE TABLE`, and
      /// distinct from the baked-in per-column defaults.
      TableCharset: string option
      TableCollation: string option
      TableComment: string
      /// When the table was created — surfaced as
      /// `information_schema.tables.CREATE_TIME` and retained by both WAL
      /// and snapshot recovery.
      CreateTime: DateTime
      /// Primary and unique keys resolve to stable row identities. Persistent
      /// maps preserve catalog snapshots; keys containing NULL are absent,
      /// matching MySQL uniqueness semantics.
      UniqueIndex: Map<string, Map<string, RowId>>
      /// Non-unique B-tree keys map equality keys to stable row
      /// identities. Buckets avoid per-row tree-position lookup for equality.
      SecondaryIndex: Map<string, Map<string, Set<RowId>>>
      /// Lexicographic B-tree entries support bounded ordered seeks across
      /// primary, unique, and non-unique keys.
      SecondaryOrder: SecondaryOrder
      /// FULLTEXT indexes retain token postings and row-local positions.
      /// They are derived from rows and rebuilt after persistence recovery.
      FullTextIndexes: FullTextIndexes }

    /// `RowsArray` as a plain list, in scan order — a fresh O(row count)
    /// copy on every access, for external validators/tools that walk a
    /// snapshot with `List.*`; every hot path reads `RowsArray` directly.
    member this.Rows: Value[] list = List.ofSeq this.RowsArray

let private isPrimaryIndex (index: IndexDef) =
    String.Equals(index.Name, "PRIMARY", StringComparison.OrdinalIgnoreCase)

let primaryKeyColumns (table: Table) =
    table.Indexes
    |> List.tryFind isPrimaryIndex
    |> Option.map _.Columns
    |> Option.defaultWith (fun () ->
        table.Columns
        |> List.choose (fun column -> if column.PrimaryKey then Some column.Name else None))

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
/// (`SchemaChanged`) is logged logically as the parsed `Statement` instead.
/// `SchemaChangedAt` additionally retains clocks assigned by CREATE and
/// TRUNCATE.
/// `TransactionCommitted` wraps every event a
/// multi-statement transaction buffered, emitted once at COMMIT — see
/// `beginTransactionSnapshot`/`commitCatalogInto`.
type CommitEvent =
    | RowsInserted of db: string * table: string * rows: Value[] list
    | RowsUpdated of db: string * table: string * changes: (Value[] * Value[]) list
    | RowsDeleted of db: string * table: string * rows: Value[] list
    | SchemaChanged of db: string * Statement
    | SchemaChangedAt of db: string * statement: Statement * createTime: DateTime
    | TransactionCommitted of CommitEvent list

type DurableCommitSink =
    { DataDirectory: string
      Enqueue: CommitEvent list -> (unit -> unit)
      EnqueueCheckpoint: unit -> (unit -> unit) }

type DurableCommitSlot = { mutable Sink: DurableCommitSink option }

let defaultDatabase = "fsdb"

let private stripIdentifierQuotes (s: string) =
    let text = s.Trim()

    if
        text.Length >= 2
        && ((text.[0] = '`' && text.[text.Length - 1] = '`')
            || (text.[0] = '"' && text.[text.Length - 1] = '"'))
    then
        text.Substring(1, text.Length - 2)
    else
        text

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
    | [| db; tbl |] -> stripIdentifierQuotes db, stripIdentifierQuotes tbl
    | _ -> defaultDb, stripIdentifierQuotes name

type RowLockStripe =
    { SyncRoot: obj
      mutable Owner: int64 option }

type TransactionLockContext =
    { Owner: int64
      HeldStripes: Collections.Generic.HashSet<RowLockStripe> }

let private rowLockStripeCount = 4096

let private createRowLockStripe () =
    { SyncRoot = obj ()
      Owner = None }

/// Shared catalog state plus session-local coercion and transaction settings.
/// Session clones share reference-typed synchronization fields but copy the
/// mutable SQL-mode, FK, and collation values.
type Store =
    { /// Each mutable cell is the lock and publication point for one database.
      Databases: ConcurrentDictionary<string, Database ref>
      mutable ForeignKeyChecks: bool
      /// Re-derived from the session's sql_mode before each statement.
      mutable StrictMode: bool
      mutable NoZeroDate: bool
      mutable NoZeroInDate: bool
      mutable OnlyFullGroupBy: bool
      mutable NoAutoValueOnZero: bool
      mutable ErrorForDivisionByZero: bool
      /// Applies only when no column or explicit COLLATE supplies precedence.
      mutable ConnectionCollation: Collation.Collation
      /// Lowercase names overlay real tables in the reserved fsdb schema.
      mutable VirtualTables: Map<string, Functions.VirtualTable>
      /// Synchronous ordered subscribers run after catalog publication.
      /// Handlers must not re-enter the store while `CommitLock` is held.
      OnCommit: ResizeArray<CommitEvent -> unit>
      /// Shared indirection lets durability attach after session clones exist.
      Durability: DurableCommitSlot
      /// Transaction snapshots buffer physical events until catalog merge.
      mutable PendingEvents: ResizeArray<CommitEvent> option
      /// Serializes catalog membership and serializable publication.
      Lock: obj
      /// Orders durable enqueue and observer delivery without covering fsync.
      CommitLock: obj
      /// Indexed updates coordinate stable row identities before reading
      /// their current values. Each nested dictionary belongs to one table.
      /// Logical ownership may span transaction statements without depending
      /// on managed thread affinity.
      RowLocks: ConcurrentDictionary<string, ConcurrentDictionary<int, RowLockStripe>>
      /// A separate namespace prevents key hashes from aliasing held rows.
      KeyLocks: ConcurrentDictionary<string, ConcurrentDictionary<int, RowLockStripe>>
      RowLockSequence: int64 array
      TransactionLocks: TransactionLockContext option }

    /// Materializes one catalog root in O(database count), with row structures
    /// shared immutably. Hot single-database paths read `Databases` directly.
    /// Slots are sampled independently, so cross-database snapshots
    /// are not linearizable; add a store epoch if a consumer requires that.
    member this.Catalog
        with get () : Catalog = this.Databases |> Seq.map (fun kv -> kv.Key, kv.Value.Value) |> Map.ofSeq
        /// Whole-catalog replacement is safe only during startup, private
        /// transaction rollback, or isolated test setup.
        and set (catalog: Catalog) =
            this.Databases.Clear()

            for KeyValue(dbName, db) in catalog do
                this.Databases.[dbName] <- ref db

let setCatalog (store: Store) (catalog: Catalog) : unit = store.Catalog <- catalog

// The mysql bootstrap requires reindexTable, so create is defined below it.

let private hasCommitConsumer (store: Store) =
    store.Durability.Sink.IsSome || store.OnCommit.Count > 0

let private collectsCommitEvents (store: Store) =
    store.PendingEvents.IsSome || hasCommitConsumer store

let private prepareEvents (store: Store) (events: CommitEvent list) : unit -> unit =
    match events, store.PendingEvents with
    | [], _ -> ignore
    | events, Some buffer ->
        buffer.AddRange events
        ignore
    | events, None when hasCommitConsumer store ->
        let durableAction, observerError =
            lock store.CommitLock (fun () ->
                let action =
                    match store.Durability.Sink with
                    | Some sink -> sink.Enqueue events
                    | None -> ignore

                let error =
                    try
                        for event in events do
                            for observer in store.OnCommit do
                                observer event

                        None
                    with error ->
                        Some error

                action, error)

        fun () ->
            durableAction ()
            observerError |> Option.iter raise
    | _ -> ignore

let private prepareResultEvents (store: Store) (eventsOf: 'a -> CommitEvent list) (result: 'a) : unit -> unit =
    if collectsCommitEvents store then
        prepareEvents store (eventsOf result)
    else
        ignore

/// Buffers private transaction events or publishes live ones in durable order.
let private emit (store: Store) (event: CommitEvent option) : unit =
    event |> Option.toList |> prepareEvents store |> fun acknowledge -> acknowledge ()

/// Creates a structurally shared private catalog with independent publication
/// cells. Event buffering is omitted when no outer subscriber needs it.
let private transactionSnapshotFromCatalog (store: Store) (catalog: Catalog) : Store =
    let databases = ConcurrentDictionary<string, Database ref>()

    for KeyValue(dbName, database) in catalog do
        databases.[dbName] <- ref database

    { Databases = databases
      ForeignKeyChecks = store.ForeignKeyChecks
      // QueryHandler derives the transaction's effective mode before use.
      StrictMode = true
      NoZeroDate = store.NoZeroDate
      NoZeroInDate = store.NoZeroInDate
      OnlyFullGroupBy = store.OnlyFullGroupBy
      NoAutoValueOnZero = store.NoAutoValueOnZero
      ErrorForDivisionByZero = store.ErrorForDivisionByZero
      ConnectionCollation = store.ConnectionCollation
      VirtualTables = store.VirtualTables
      OnCommit = ResizeArray()
      Durability = store.Durability
      // Nested statement snapshots inherit the outer transaction's buffering.
      PendingEvents = if collectsCommitEvents store then Some(ResizeArray()) else None
      Lock = obj ()
      CommitLock = store.CommitLock
      RowLocks = store.RowLocks
      KeyLocks = store.KeyLocks
      RowLockSequence = store.RowLockSequence
      TransactionLocks = store.TransactionLocks }

let beginTransactionSnapshot (store: Store) : Store =
    transactionSnapshotFromCatalog store store.Catalog

let beginTransactionSnapshotFromCatalog (store: Store) (catalog: Catalog) : Store =
    transactionSnapshotFromCatalog store catalog

let beginTransactionSnapshotWithBase (store: Store) : Catalog * Store =
    let catalog = store.Catalog
    catalog, transactionSnapshotFromCatalog store catalog

let beginTransactionContext (store: Store) : Store =
    let owner = Threading.Interlocked.Increment(&store.RowLockSequence.[0])

    { store with
        OnCommit = ResizeArray()
        PendingEvents = if collectsCommitEvents store then Some(ResizeArray()) else None
        Lock = obj ()
        TransactionLocks =
            Some
                { Owner = owner
                  HeldStripes = Collections.Generic.HashSet<RowLockStripe>(HashIdentity.Reference) } }

let beginTransactionWithBase (store: Store) : Catalog * Store =
    let catalog, snapshot = beginTransactionSnapshotWithBase store
    let owner = Threading.Interlocked.Increment(&store.RowLockSequence.[0])

    catalog,
    { snapshot with
        TransactionLocks =
            Some
                { Owner = owner
                  HeldStripes = Collections.Generic.HashSet<RowLockStripe>(HashIdentity.Reference) } }

let carryTransactionLocks (source: Store) (snapshot: Store) : Store =
    { snapshot with TransactionLocks = source.TransactionLocks }

let private releaseLockStripes (context: TransactionLockContext) (stripes: seq<RowLockStripe>) =
    for stripe in stripes do
        lock stripe.SyncRoot (fun () ->
            if stripe.Owner = Some context.Owner then
                stripe.Owner <- None
                lock context.HeldStripes (fun () -> context.HeldStripes.Remove stripe |> ignore)
                Threading.Monitor.PulseAll stripe.SyncRoot)

let releaseTransactionLocks (store: Store) : unit =
    match store.TransactionLocks with
    | None -> ()
    | Some context ->
        let stripes = lock context.HeldStripes (fun () -> context.HeldStripes |> Seq.toArray)
        releaseLockStripes context stripes

let private prepareTransactionEvents (store: Store) (snapshot: Store) : unit -> unit =
    match snapshot.PendingEvents with
    | Some buffer when buffer.Count > 0 ->
        match store.PendingEvents with
        | Some targetBuffer ->
            targetBuffer.AddRange buffer
            ignore
        | None -> prepareEvents store [ TransactionCommitted(List.ofSeq buffer) ]
    | _ -> ignore

let setForeignKeyChecks (store: Store) (enabled: bool) : unit =
    lock store.Lock (fun () -> store.ForeignKeyChecks <- enabled)

let setConnectionCollation (store: Store) (collation: Collation.Collation) : unit = store.ConnectionCollation <- collation

let setStrictMode (store: Store) (strict: bool) : unit =
    lock store.Lock (fun () -> store.StrictMode <- strict)

let setZeroDateModes (store: Store) (noZeroDate: bool) (noZeroInDate: bool) : unit =
    lock store.Lock (fun () ->
        store.NoZeroDate <- noZeroDate
        store.NoZeroInDate <- noZeroInDate)

let setOnlyFullGroupBy (store: Store) (enabled: bool) : unit =
    lock store.Lock (fun () -> store.OnlyFullGroupBy <- enabled)

let setNoAutoValueOnZero (store: Store) (enabled: bool) : unit =
    lock store.Lock (fun () -> store.NoAutoValueOnZero <- enabled)

let setErrorForDivisionByZero (store: Store) (enabled: bool) : unit =
    lock store.Lock (fun () -> store.ErrorForDivisionByZero <- enabled)

/// Table names are keyed case-insensitively by their lowercased form —
/// public because `Persistence`'s WAL replay looks tables up in `Catalog`
/// directly (bypassing this module's checked write paths on purpose; see
/// the note on `Persistence.applyEvent`), so it needs the same key.
let normalizeTableName (name: string) = name.ToLowerInvariant()

let private lockNamespaceKey (databaseName: string) (tableName: string) =
    databaseName.ToLowerInvariant() + "\u0000" + normalizeTableName tableName

/// `CREATE DATABASE name` errors 1007 when the name exists. The store lock
/// coordinates catalog membership with SERIALIZABLE snapshot validation;
/// row writes remain sharded by database.
let createDatabase (store: Store) (dbName: string) : Result<unit, StorageError> =
    let slot = ref Map.empty

    let published =
        lock store.Lock (fun () ->
            lock slot (fun () ->
                if store.Databases.TryAdd(dbName, slot) then
                    Ok(prepareEvents store [ SchemaChanged(dbName, CreateDatabase(dbName, false)) ])
                else
                    Error(DatabaseExists dbName)))

    published |> Result.map (fun acknowledge -> acknowledge ())

/// `DROP DATABASE name` uses the same catalog-membership lock as creation.
let dropDatabase (store: Store) (dbName: string) : Result<unit, StorageError> =
    if dbName.ToLowerInvariant() = "mysql" then
        Error(SystemSchemaAccess "mysql")
    else
        let published =
            lock store.Lock (fun () ->
                match store.Databases.TryGetValue dbName with
                | false, _ -> Error(NoSuchDatabase dbName)
                | true, slot ->
                    lock slot (fun () ->
                        match store.Databases.TryRemove dbName with
                        | true, _ -> Ok(prepareEvents store [ SchemaChanged(dbName, DropDatabase(dbName, false)) ])
                        | false, _ -> Error(NoSuchDatabase dbName)))

        published |> Result.map (fun acknowledge -> acknowledge ())

/// Applies `f` to `dbName`'s current table map and swaps the result into
/// that database's own `Database ref` cell, under that cell's own lock (see
/// below). A completely
/// disjoint slot per database (see `Store.Databases`'s doc) means a write to
/// database A never even touches, let alone blocks on, database B's slot —
/// cross-database writes can't contend by construction.
let private withDatabasePublishing
    (store: Store)
    (dbName: string)
    (eventsOf: 'a -> CommitEvent list)
    (f: Database -> Result<Database * 'a, StorageError>)
    : Result<'a, StorageError> =
    match store.Databases.TryGetValue dbName with
    | false, _ -> Error(NoSuchDatabase dbName)
    | true, slot ->
        // The database cell is also its mutation lock. Different databases
        // use different cells, while writers within one database publish
        // their immutable replacement maps atomically.
        let published =
            lock slot (fun () ->
                let attached =
                    match store.Databases.TryGetValue dbName with
                    | true, current -> obj.ReferenceEquals(current, slot)
                    | false, _ -> false

                if not attached then
                    Error(NoSuchDatabase dbName)
                else
                    let original = slot.Value

                    match f original with
                    | Error e -> Error e
                    | Ok(db', result) ->
                        let current = slot.Value

                        slot.Value <-
                            if LanguagePrimitives.PhysicalEquality original current then
                                db'
                            else
                                // Trigger bodies may re-enter this slot while the outer
                                // statement still owns its immutable starting root.
                                let keys =
                                    Set.union
                                        (original |> Map.toSeq |> Seq.map fst |> Set.ofSeq)
                                        (db' |> Map.toSeq |> Seq.map fst |> Set.ofSeq)

                                keys
                                |> Set.fold
                                    (fun published key ->
                                        match Map.tryFind key original, Map.tryFind key db' with
                                        | Some before, Some after when LanguagePrimitives.PhysicalEquality before after -> published
                                        | _, Some after -> Map.add key after published
                                        | Some _, None -> Map.remove key published
                                        | None, None -> published)
                                    current

                        Ok(result, prepareResultEvents store eventsOf result))

        match published with
        | Error error -> Error error
        | Ok(result, acknowledge) ->
            acknowledge ()
            Ok result

let private withDatabase store dbName f =
    withDatabasePublishing store dbName (fun _ -> []) f

/// Holds the named database cells in lexical order while `action` prepares
/// and publishes a schema change. DML already uses these cells for its short
/// publication step, so the ordering prevents both stale DDL snapshots and
/// lock-order cycles when one statement also updates `mysql` metadata.
let withDatabaseLocks (timeout: TimeSpan) (store: Store) (dbNames: string seq) (action: unit -> 'a) : 'a =
    let slots =
        dbNames
        |> Seq.map _.ToLowerInvariant()
        |> Set.ofSeq
        |> Seq.choose (fun dbName ->
            match store.Databases.TryGetValue dbName with
            | true, slot -> Some(dbName, slot)
            | false, _ -> None)
        |> List.ofSeq

    let rec acquire = function
        | [] -> action ()
        | (dbName, slot) :: rest ->
            if not (Monitor.TryEnter(slot, timeout)) then
                raise (LockWaitTimeout dbName)

            try
                acquire rest
            finally
                Monitor.Exit slot

    acquire slots

/// Every database name appearing as a key in `m` — the shared set-of-keys
/// step `mergeDatabaseSlot`/`mergeCatalogInto`/`bumpAutoIncrementsInto`'s
/// per-database merges all need.
let private keysOf (m: Map<string, 'a>) : Set<string> = m |> Map.toList |> List.map fst |> Set.ofList

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

/// A lock-free physical-table snapshot for access paths that must keep their
/// scan fallback and index candidates on the same catalog root.
let tableSnapshot (store: Store) (dbName: string) (tableName: string) : Result<Table, StorageError> =
    match store.Databases.TryGetValue dbName with
    | false, _ -> Error(NoSuchDatabase dbName)
    | true, slot -> tryGetTable slot.Value tableName

/// Auto-creates a database the first time a real table is written into it
/// (`withDatabase`), and for the database a client names at connect time
/// (`mysql -D foo`/PDO's `dbname=foo` DSN, a zero-setup convenience for a
/// fresh in-memory server); a no-op if it already exists. Deliberately
/// *not* used by mid-session `USE`/`COM_INIT_DB` — those check
/// `databaseExists` and report a real 1049 instead, matching MySQL (see
/// `QueryHandler`'s `Use` probe). `ConcurrentDictionary.TryAdd` publishes
/// the database atomically without a retry loop.
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
        let column = List.item i columns

        if column.Generated.IsSome then
            Error(GeneratedColumnAssignment(column.Name, tableName))
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

let private parseDecimal (s: string) : decimal option =
    match Decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
    | true, value -> Some value
    | false, _ -> None

/// MySQL-style coercion of a value to a column's declared type
/// (`'12' -> 12` for an INT column); error 1366 when it's not possible and
/// `strict` (the session's STRICT_TRANS_TABLES/STRICT_ALL_TABLES, see
/// `Store.StrictMode`) is set — MySQL's actual default. Off (Laravel's
/// `'strict' => false` connection config, which sends
/// `SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'`), an otherwise-rejected
/// value coerces to MySQL's non-strict fallback instead: 0 for a numeric
/// column, NULL for a nullable temporal one.
/// NULL always passes through untouched — nullability is checked
/// separately.
type TemporalCoercionMode =
    { Strict: bool
      NoZeroDate: bool
      NoZeroInDate: bool }

let temporalCoercionMode (store: Store) =
    { Strict = store.StrictMode
      NoZeroDate = store.NoZeroDate
      NoZeroInDate = store.NoZeroInDate }

let private truncateRunes (length: int) (text: string) =
    let runes = text.EnumerateRunes() |> Seq.toArray

    if runes.Length <= length then
        None
    else
        runes |> Seq.truncate length |> Seq.map _.ToString() |> String.concat "" |> Some

let private coerceValueWithModeAndLengths (enforceLengths: bool) (mode: TemporalCoercionMode) (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    let strict = mode.Strict
    let fail () =
        Error(InvalidValueForColumn(col.Name, v |> toText |> Option.defaultValue "NULL"))

    let escapedUtf8Suffix (text: string) (converted: string) =
        let firstChanged =
            Seq.zip text converted
            |> Seq.tryFindIndex (fun (original, replacement) -> original <> replacement)
            |> Option.defaultValue 0

        text.Substring(firstChanged)
        |> Text.Encoding.UTF8.GetBytes
        |> Array.map (fun b -> if b < 0x80uy then string (char b) else sprintf "\\x%02X" b)
        |> String.concat ""

    let charsetChecked (text: string) : Result<string, StorageError> =
        let converted =
            match col.Charset with
            | Some "ascii" -> Collation.Charset.transcodeAscii text
            | Some "latin1" -> Collation.Charset.transcodeLatin1 text
            | _ -> text

        if text = converted then
            Ok text
        else
            let value = escapedUtf8Suffix text converted
            let message = sprintf "Incorrect string value: '%s' for column '%s' at row %d" value col.Name (Diagnostics.currentRowNumber ())

            if strict then
                Error(ExpressionError(1366, message))
            else
                Diagnostics.warning 1366 message
                Ok converted

    let truncationWarning () =
        Diagnostics.warning 1265 (sprintf "Data truncated for column '%s' at row %d" col.Name (Diagnostics.currentRowNumber ()))

    let truncateText length (text: string) =
        match truncateRunes length text with
        | None -> Ok text
        | Some _ when not enforceLengths -> Ok text
        | Some _ when strict ->
            Error(DataTooLongForColumn(col.Name, Diagnostics.currentRowNumber ()))
        | Some truncated ->
            truncationWarning ()
            Ok truncated

    let truncateBytes length (bytes: byte[]) =
        if not enforceLengths || bytes.Length <= length then
            Ok bytes
        elif strict then
            Error(DataTooLongForColumn(col.Name, Diagnostics.currentRowNumber ()))
        else
            truncationWarning ()
            Ok(bytes.[.. length - 1])

    let padBinary length (bytes: byte[]) =
        if bytes.Length = length then
            bytes
        else
            Array.append bytes (Array.zeroCreate (length - bytes.Length))

    let warning code message =
        Diagnostics.warning code (sprintf "%s at row %d" message (Diagnostics.currentRowNumber ()))

    /// Non-strict's numeric fallback: 0, always representable.
    let numericFallback (kind: string option) (zero: unit -> Value) =
        if strict then
            fail ()
        else
            kind
            |> Option.iter (fun kind ->
                warning 1366 (sprintf "Incorrect %s value: '%s' for column '%s'" kind (v |> toText |> Option.defaultValue "NULL") col.Name))

            Ok(zero ())

    let temporalFallback () =
        if strict then
            fail ()
        else
            warning 1265 (sprintf "Data truncated for column '%s'" col.Name)

            match col.Type with
            | TDate ->
                tryZeroDate 0 0 0
                |> Option.map (VZeroDate >> Ok)
                |> Option.defaultWith fail
            | TDateTime _
            | TTimestamp _ ->
                tryZeroDate 0 0 0
                |> Option.bind (fun date -> tryZeroDateTime date 0 0 0 0)
                |> Option.map (VZeroDateTime >> Ok)
                |> Option.defaultWith fail
            | _ -> fail ()

    let outOfRange () = Error(OutOfRangeForColumn col.Name)

    let integerRange =
        function
        | TTinyInt unsigned -> if unsigned then 0M, 255M else -128M, 127M
        | TBool -> -128M, 127M
        | TSmallInt unsigned -> if unsigned then 0M, 65535M else -32768M, 32767M
        | TMediumInt unsigned -> if unsigned then 0M, 16777215M else -8388608M, 8388607M
        | TInt unsigned -> if unsigned then 0M, 4294967295M else -2147483648M, 2147483647M
        | TBigInt false -> decimal Int64.MinValue, decimal Int64.MaxValue
        | ty -> invalidArg (nameof ty) "expected a signed or narrow integer type"

    let narrowInteger ty value =
        let lo, hi = integerRange ty

        let finish number =
            if number >= lo && number <= hi then
                Ok(VInt(int64 number))
            elif strict then
                outOfRange ()
            else
                warning 1264 (sprintf "Out of range value for column '%s'" col.Name)
                Ok(VInt(int64 (max lo (min hi number))))

        match value with
        | VInt number -> finish (decimal number)
        | VUInt number -> finish (decimal number)
        | VBit(_, number) -> finish (decimal number)
        | VDecimal number -> finish (Math.Truncate number)
        | VDouble number ->
            if Double.IsNaN number then
                numericFallback (Some "integer") (fun () -> VInt 0L)
            elif number < float lo || number > float hi then
                if strict then outOfRange () else finish (if number < 0.0 then lo else hi)
            else
                finish (Math.Truncate(decimal number))
        | VString text ->
            match parseDecimal text, parseNumeric text with
            | Some number, _ -> finish (Math.Truncate number)
            | None, Some number when number < float lo || number > float hi ->
                if strict then outOfRange () else finish (if number < 0.0 then lo else hi)
            | None, Some number -> finish (Math.Truncate(decimal number))
            | None, None -> numericFallback (Some "integer") (fun () -> VInt 0L)
        | _ -> numericFallback (Some "integer") (fun () -> VInt 0L)

    match col.Type, v with
    | TDecimal(precision, _, _), _ when precision < 1 || precision > 65 ->
        Error(ExpressionError(1426, sprintf "Too-big precision %d specified for '%s'. Maximum is 65." precision col.Name))
    | TDecimal(_, scale, _), _ when scale < 0 || scale > 30 ->
        Error(ExpressionError(1425, sprintf "Too big scale %d specified for column '%s'. Maximum is 30." scale col.Name))
    | TDecimal(precision, scale, _), _ when scale > precision ->
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
        | TBigInt true ->
            // 1264 ("Out of range value"), MySQL's code for a value outside
            // the column's numeric domain — not `fail`'s 1366, which is the
            // *uncoercible* case (a non-numeric string).
            let narrow (d: decimal) =
                if d >= 0m && d <= decimal UInt64.MaxValue then
                    Ok(VUInt(uint64 d))
                elif strict then
                    outOfRange ()
                else
                    warning 1264 (sprintf "Out of range value for column '%s'" col.Name)
                    Ok(VUInt(uint64 (max 0m (min d (decimal UInt64.MaxValue)))))

            match v with
            | VUInt u -> Ok(VUInt u)
            | VBit(_, value) -> Ok(VUInt value)
            | VInt i -> narrow (decimal i)
            | VDouble d ->
                // `decimal d` itself overflows outside ±7.9e28, so the range
                // verdict comes from the `double` before any conversion.
                if d >= 0.0 && d < 1.8446744073709552e19 then
                    narrow (Math.Truncate(decimal d))
                elif strict then
                    outOfRange ()
                else
                    warning 1264 (sprintf "Out of range value for column '%s'" col.Name)
                    Ok(VUInt(if d < 0.0 then 0UL else UInt64.MaxValue))
            | VDecimal d -> narrow (Math.Truncate d)
            | VString s ->
                match parseDecimal s, parseNumeric s with
                | Some d, _ -> narrow (Math.Truncate d)
                | None, Some d ->
                    if d >= 0.0 && d < 1.8446744073709552e19 then
                        narrow (Math.Truncate(decimal d))
                    elif strict then
                        outOfRange ()
                    else
                        warning 1264 (sprintf "Out of range value for column '%s'" col.Name)
                        Ok(VUInt(if d < 0.0 then 0UL else UInt64.MaxValue))
                | None, None -> numericFallback (Some "integer") (fun () -> VUInt 0UL)
            | _ -> numericFallback (Some "integer") (fun () -> VUInt 0UL)
        | TBit width ->
            let maxValue = if width = 64 then UInt64.MaxValue else (1UL <<< width) - 1UL

            let tooLarge () =
                if strict then
                    Error(DataTooLongForColumn(col.Name, Diagnostics.currentRowNumber ()))
                else
                    warning 1264 (sprintf "Out of range value for column '%s'" col.Name)
                    Ok(VBit(width, maxValue))

            let finish value =
                if value <= maxValue then
                    Ok(VBit(width, value))
                else
                    tooLarge ()

            let bytes value =
                match bitValue value with
                | Some value -> finish value
                | None -> tooLarge ()

            let decimalValue value =
                if value < 0m then
                    if strict then
                        Error(OutOfRangeForColumn col.Name)
                    else
                        warning 1264 (sprintf "Out of range value for column '%s'" col.Name)
                        Ok(VBit(width, 0UL))
                else
                    let rounded = Math.Round(value, 0, MidpointRounding.AwayFromZero)

                    if rounded > decimal UInt64.MaxValue then
                        tooLarge ()
                    else
                        finish(uint64 rounded)

            match v with
            | VBit(_, value) -> finish value
            | VBytes value -> bytes value
            | VString value -> value |> Text.Encoding.UTF8.GetBytes |> bytes
            | VUInt value -> finish value
            | VInt value -> if value < 0L then tooLarge () else finish(uint64 value)
            | VDecimal value -> decimalValue value
            | VDouble value ->
                if Double.IsNaN value || value < 0.0 || value >= 1.8446744073709552e19 then
                    if value < 0.0 && strict then Error(OutOfRangeForColumn col.Name) else tooLarge ()
                else
                    decimalValue(decimal value)
            | _ -> tooLarge ()
        | (TInt _ | TBigInt false | TSmallInt _ | TMediumInt _ | TTinyInt _ | TBool) as integerType ->
            narrowInteger integerType v
        | TYear ->
            match v with
            | VInt i -> Ok(VInt i)
            | VUInt u -> Ok(VInt(int64 u))
            | VBit(_, value) -> Ok(VInt(int64 value))
            | VDouble d -> Ok(VInt(int64 d))
            | VDecimal d -> Ok(VInt(int64 d))
            | VString s ->
                match parseNumeric s with
                | Some d -> Ok(VInt(int64 d))
                | None -> numericFallback None (fun () -> VInt 0L)
            | _ -> numericFallback None (fun () -> VInt 0L)
        | TDouble unsigned
        | TFloat unsigned ->
            let unsignedUnderflow () =
                if strict then
                    outOfRange ()
                else
                    warning 1264 (sprintf "Out of range value for column '%s'" col.Name)
                    Ok(VDouble 0.0)

            match v with
            | VDouble d when unsigned && d < 0.0 -> unsignedUnderflow ()
            | VDouble d -> Ok(VDouble d)
            | VInt i when unsigned && i < 0L -> unsignedUnderflow ()
            | VInt i -> Ok(VDouble(float i))
            | VUInt u -> Ok(VDouble(float u))
            | VBit(_, value) -> Ok(VDouble(float value))
            | VDecimal d when unsigned && d < 0M -> unsignedUnderflow ()
            | VDecimal d -> Ok(VDouble(float d))
            | VString s ->
                match parseNumeric s with
                | Some d when unsigned && d < 0.0 -> unsignedUnderflow ()
                | Some d -> Ok(VDouble d)
                | None -> numericFallback None (fun () -> VDouble 0.0)
            | _ -> numericFallback None (fun () -> VDouble 0.0)
        | TDecimal(_, scale, unsigned) ->
            // MySQL pads/rounds every stored value to the column's declared
            // scale (`DECIMAL(10,2)` stores `100` as `100.00`), and later
            // reads it back at that same scale — `.NET`'s `decimal` carries
            // its own scale, but round-tripping through `Math.Round` alone
            // doesn't widen it (`Math.Round(100m, 2)` is still `100`, not
            // `100.00`), so go through a fixed-point string format instead,
            // which both rounds and pads in one step.
            let rescale (d: decimal) =
                let scaled = Decimal.Parse(d.ToString("F" + string scale, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)

                if scaled <> d then
                    Diagnostics.note 1265 (sprintf "Data truncated for column '%s' at row %d" col.Name (Diagnostics.currentRowNumber ()))

                scaled

            let finish value =
                if not unsigned || value >= 0m then
                    Ok(VDecimal(rescale value))
                elif strict then
                    outOfRange ()
                else
                    warning 1264 (sprintf "Out of range value for column '%s'" col.Name)
                    Ok(VDecimal(rescale 0m))

            match v with
            | VDecimal d -> finish d
            | VInt i -> finish (decimal i)
            | VUInt u -> finish (decimal u)
            | VBit(_, value) -> finish (decimal value)
            | VDouble d -> finish (decimal d)
            | VString s ->
                match parseDecimal s with
                | Some d -> finish d
                | None -> numericFallback (Some "decimal") (fun () -> VDecimal(rescale 0M))
            | _ -> numericFallback (Some "decimal") (fun () -> VDecimal(rescale 0M))
        | TChar length
        | TVarchar length ->
            charsetChecked (v |> toText |> Option.defaultValue "")
            |> Result.bind (fun text ->
                let text =
                    match col.Type with
                    | TChar _ when enforceLengths -> text.TrimEnd([| ' ' |])
                    | _ -> text

                truncateText length text)
            |> Result.map VString
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
        | TGeometry requiredKind ->
            match v with
            | VGeometry geometry when requiredKind = Geometry || geometryKind geometry.Shape = requiredKind -> Ok(VGeometry geometry)
            | _ -> fail ()
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

            let maxValid = if List.length values = 64 then UInt64.MaxValue else (1UL <<< List.length values) - 1UL

            let fromMask mask =
                values
                |> List.indexed
                |> List.filter (fun (bit, _) -> mask &&& (1UL <<< bit) <> 0UL)
                |> List.map snd
                |> String.concat ","

            let numericSet mask valid =
                if valid then
                    Ok(VString(fromMask mask))
                elif strict then
                    setFail ()
                else
                    warning 1265 (sprintf "Data truncated for column '%s'" col.Name)
                    Ok(VString(fromMask (mask &&& maxValid)))

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
                    warning 1265 (sprintf "Data truncated for column '%s'" col.Name)
                    Ok(VString(canonicalize (parts |> List.choose resolve |> List.map (fun m -> m.ToUpperInvariant()) |> Set.ofList)))
            | VInt value -> numericSet (uint64 value) (value >= 0L && uint64 value <= maxValid)
            | VBit(_, value) -> numericSet value (value <= maxValid)
            | _ -> setFail ()
        | TTime fsp ->
            let raw = v |> toText |> Option.defaultValue ""

            let invalid () =
                Error(ExpressionError(1292, sprintf "Incorrect time value: '%s' for column '%s' at row %d" raw col.Name (Diagnostics.currentRowNumber ())))

            let truncated () =
                if strict then
                    invalid ()
                else
                    warning 1265 (sprintf "Data truncated for column '%s'" col.Name)
                    Ok(VTime(timeValueOrClamp 0L))

            let outOfRange () =
                if strict then
                    invalid ()
                else
                    warning 1264 (sprintf "Out of range value for column '%s'" col.Name)
                    Ok(VTime(timeValueOrClamp 0L))

            let finish ticks =
                let rounded = roundTimeTicksToFsp fsp ticks

                if abs rounded <= maxTimeTicks then
                    Ok(VTime(timeValueOrClamp rounded))
                elif strict then
                    invalid ()
                else
                    warning 1264 (sprintf "Out of range value for column '%s'" col.Name)
                    Ok(VTime(timeValueOrClamp rounded))

            let parsed =
                match v with
                | VTime value -> ParsedTime(timeTicks value)
                | VDateTime value -> ParsedTime(value.TimeOfDay.Ticks)
                | VZeroDateTime value ->
                    let _, hour, minute, second, micros = zeroDateTimeParts value
                    ParsedTime(((int64 hour * 3600L + int64 minute * 60L + int64 second) * TimeSpan.TicksPerSecond) + int64 micros * 10L)
                | VDate _
                | VZeroDate _ -> ParsedTime 0L
                | _ -> parseTimeInput raw

            match parsed with
            | ParsedTime ticks -> finish ticks
            | TimeComponentsOutOfRange -> outOfRange ()
            | NotATime -> truncated ()
        | TBinary length
        | TVarBinary length ->
            let bytes =
                tryRawBytes v
                |> Option.defaultWith (fun () ->
                    match v with
                    | VString text -> Text.Encoding.UTF8.GetBytes text
                    | _ -> v |> toText |> Option.defaultValue "" |> Text.Encoding.UTF8.GetBytes)

            truncateBytes length bytes
            |> Result.map (fun bytes ->
                if enforceLengths then
                    match col.Type with
                    | TBinary _ -> padBinary length bytes
                    | _ -> bytes
                else
                    bytes)
            |> Result.map VBytes
        | TTinyBlob
        | TBlob
        | TMediumBlob
        | TLongBlob ->
            tryRawBytes v
            |> Option.map (VBytes >> Ok)
            |> Option.defaultWith (fun () ->
                match v with
                | VString text -> Ok(VBytes(Text.Encoding.UTF8.GetBytes text))
                | _ -> Ok(VBytes(v |> toText |> Option.defaultValue "" |> Text.Encoding.UTF8.GetBytes)))
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
                    | _ when strict -> enumFail ()
                    | _ ->
                        warning 1265 (sprintf "Data truncated for column '%s'" col.Name)
                        Ok(VString "")
            // MySQL also accepts a 1-based index into the declared value list.
            | VInt i when i >= 1L && i <= int64 (List.length values) -> Ok(VString values.[int i - 1])
            | VBit(_, value) when value >= 1UL && value <= uint64 (List.length values) -> Ok(VString values.[int value - 1])
            | _ when strict -> enumFail ()
            | _ ->
                warning 1265 (sprintf "Data truncated for column '%s'" col.Name)
                Ok(VString "")
        | TDate ->
            let zeroDateError () =
                Error(ZeroTemporalForColumn("date", v |> toText |> Option.defaultValue "0000-00-00", col.Name))

            let zeroDateFallback warningCode =
                if strict then
                    zeroDateError ()
                else
                    warning warningCode (sprintf "Out of range value for column '%s'" col.Name)
                    tryZeroDate 0 0 0
                    |> Option.map (VZeroDate >> Ok)
                    |> Option.defaultWith zeroDateError

            let zeroDateResult date =
                let year, month, day = zeroDateParts date
                let rejected = if year = 0 && month = 0 && day = 0 then mode.NoZeroDate else mode.NoZeroInDate

                if not rejected then
                    Ok(VZeroDate date)
                elif strict then
                    zeroDateError ()
                else
                    zeroDateFallback 1264

            let invalidZeroDateResult year month day =
                let warningCode = if month > 12 || day > 31 then 1265 else 1264
                zeroDateFallback warningCode

            match v with
            | VDate d -> Ok(VDate d)
            | VDateTime dt -> Ok(VDate(DateOnly.FromDateTime dt))
            | VZeroDate d -> zeroDateResult d
            | VZeroDateTime dt -> zeroDateResult (zeroDateOfDateTime dt)
            | VString s ->
                match tryParseZeroDate (s.Trim()) with
                | Some d -> zeroDateResult d
                | None ->
                    match tryParseZeroDateTime (s.Trim()) with
                    | Some dt -> zeroDateResult (zeroDateOfDateTime dt)
                    | None ->
                        match tryParseDateParts (s.Trim()) with
                        | Some(year, month, day) when year = 0 || month = 0 || day = 0 -> invalidZeroDateResult year month day
                        | _ ->
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
            let zeroDateError () =
                Error(ZeroTemporalForColumn("datetime", v |> toText |> Option.defaultValue "0000-00-00 00:00:00", col.Name))

            let zeroDateFallback warningCode =
                if strict then
                    zeroDateError ()
                else
                    warning warningCode (sprintf "Out of range value for column '%s'" col.Name)
                    tryZeroDate 0 0 0
                    |> Option.bind (fun zero -> tryZeroDateTime zero 0 0 0 0)
                    |> Option.map (VZeroDateTime >> Ok)
                    |> Option.defaultWith zeroDateError

            let zeroDateResult dateTime =
                let date, _, _, _, _ = zeroDateTimeParts dateTime
                let year, month, day = zeroDateParts date
                let rejected = if year = 0 && month = 0 && day = 0 then mode.NoZeroDate else mode.NoZeroInDate

                if not rejected then
                    Ok(VZeroDateTime dateTime)
                elif strict then
                    zeroDateError ()
                else
                    zeroDateFallback 1264

            let invalidZeroDateResult year month day =
                let warningCode = if month > 12 || day > 31 then 1265 else 1264
                zeroDateFallback warningCode

            match v with
            | VDateTime dt -> Ok(round dt)
            | VDate d -> Ok(round (d.ToDateTime(TimeOnly.MinValue)))
            | VZeroDate d ->
                match tryZeroDateTime d 0 0 0 0 with
                | Some dt -> zeroDateResult dt
                | None -> zeroDateError ()
            | VZeroDateTime dt -> zeroDateResult dt
            | VString s ->
                match tryParseZeroDateTime (s.Trim()) with
                | Some dt -> zeroDateResult dt
                | None ->
                    match tryParseZeroDate (s.Trim()) with
                    | Some d ->
                        match tryZeroDateTime d 0 0 0 0 with
                        | Some dt -> zeroDateResult dt
                        | None -> zeroDateError ()
                    | None ->
                        let datePart = s.Trim().Split([| ' '; 'T' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead

                        match datePart |> Option.bind tryParseDateParts with
                        | Some(year, month, day) when year = 0 || month = 0 || day = 0 -> invalidZeroDateResult year month day
                        | _ ->
                            match DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None) with
                            | true, dt -> Ok(round dt)
                            | false, _ -> temporalFallback ()
            | _ -> temporalFallback ()

let coerceValueWithMode (mode: TemporalCoercionMode) (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    coerceValueWithModeAndLengths false mode col v

let private coerceStoredValueWithMode (mode: TemporalCoercionMode) (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    coerceValueWithModeAndLengths true mode col v

let coerceValue (strict: bool) (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    coerceStoredValueWithMode
        { Strict = strict
          NoZeroDate = true
          NoZeroInDate = true }
        col
        v

let private supportsCurrentTimestamp (col: ColumnDef) =
    match col.Type with
    | TDateTime _
    | TTimestamp _ -> true
    | _ -> false

let private normalizeDefault (mode: TemporalCoercionMode) (col: ColumnDef) : Result<ColumnDef, StorageError> =
    match col.Default with
    | Some DCurrentTimestamp when not (supportsCurrentTimestamp col) -> Error(InvalidDefaultValue col.Name)
    | Some(DConst value) ->
        let text = value |> toText |> Option.defaultValue ""

        let widthOverflow =
            match col.Type, value with
            | _, VNull -> false
            | TChar length, _ -> truncateRunes length (text.TrimEnd([| ' ' |])) |> Option.isSome
            | TVarchar length, _ -> truncateRunes length text |> Option.isSome
            | (TBinary length | TVarBinary length), _ ->
                let bytes =
                    match value with
                    | VBytes bytes -> bytes
                    | VString text -> Text.Encoding.UTF8.GetBytes text
                    | _ -> text |> Text.Encoding.UTF8.GetBytes

                bytes.Length > length
            | _ -> false

        let charsetLoss =
            match col.Type, col.Charset with
            | (TChar _ | TVarchar _ | TTinyText | TText | TMediumText | TLongText | TJson), Some "ascii" ->
                Collation.Charset.transcodeAscii text <> text
            | (TChar _ | TVarchar _ | TTinyText | TText | TMediumText | TLongText | TJson), Some "latin1" ->
                Collation.Charset.transcodeLatin1 text <> text
            | _ -> false

        let defaultIsInvalid = widthOverflow || charsetLoss

        if defaultIsInvalid then
            Error(InvalidDefaultValue col.Name)
        else
            coerceStoredValueWithMode mode col value
            |> Result.map (fun value -> { col with Default = Some(DConst value) })
            |> Result.mapError (function
                | ZeroTemporalForColumn _
                | DataTooLongForColumn _ -> InvalidDefaultValue col.Name
                | error -> error)
    | Some(DExpression _) -> Ok col
    | _ -> Ok col

/// `NOW()` rounded to `col`'s own declared fsp — a `TIMESTAMP(6)` column
/// keeps microseconds, a bare `DATETIME`/`TIMESTAMP` truncates to whole
/// seconds. Shared by `DEFAULT CURRENT_TIMESTAMP` (`evalDefault`, insert
/// time) and `ON UPDATE CURRENT_TIMESTAMP` (`Executor`, update time) since
/// both evaluate the same "current time at this column's precision" rule.
let currentTimestampForColumn (col: ColumnDef) : Value =
    let fsp =
        match col.Type with
        | TDateTime fsp
        | TTimestamp fsp -> fsp
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
    | Some(DExpression _) -> VNull

let private coerceAndCheck (mode: TemporalCoercionMode) (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    match v with
    | VNull when not col.Nullable || col.PrimaryKey -> Error(NotNullViolation col.Name)
    | _ -> coerceStoredValueWithMode mode col v

let private coerceRow (mode: TemporalCoercionMode) (columns: ColumnDef list) (row: Value[]) : Result<Value[], StorageError> =
    List.zip columns (Array.toList row)
    |> traverse (fun (column, value) -> coerceAndCheck mode column value)
    |> Result.map Array.ofList

/// The `(keyName, column indices)` groups that must be unique: the primary
/// key (if any, named `"PRIMARY"` the way MySQL reports it in error 1062,
/// and treated as one group across however many columns it spans) plus
/// every `UNIQUE` index, named after itself.
type private UniqueKeyGroup =
    { Name: string
      Indices: int list
      PrefixLengths: int option list
      Transforms: IndexTransform option list }

let private indexesWholeColumns group =
    group.PrefixLengths |> List.forall Option.isNone
    && group.Transforms |> List.forall Option.isNone

let private uniqueKeyGroups (table: Table) : UniqueKeyGroup list =
    let primary =
        primaryKeyColumns table
        |> traverse (resolveColumn table.Columns)
        |> Result.toOption
        |> Option.filter (not << List.isEmpty)
        |> Option.map (fun indices ->
            { Name = "PRIMARY"
              Indices = indices
              PrefixLengths = List.replicate indices.Length None
              Transforms = List.replicate indices.Length None })

    let fromIndexes =
        table.Indexes
        |> List.filter (fun index -> index.Unique && not (isPrimaryIndex index))
        |> List.choose (fun index ->
            index.KeyColumns
            |> traverse (fun column -> resolveColumn table.Columns column.Name |> Result.map (fun resolved -> resolved, column.PrefixLength, column.Transform))
            |> Result.toOption
            |> Option.map (fun columns ->
                { Name = index.Name
                  Indices = columns |> List.map (fun (index, _, _) -> index)
                  PrefixLengths = columns |> List.map (fun (_, prefix, _) -> prefix)
                  Transforms = columns |> List.map (fun (_, _, transform) -> transform) }))

    Option.toList primary @ fromIndexes

type private SecondaryKeyGroup =
    { Name: string
      Indices: int list }

let private secondaryKeyGroups (table: Table) : SecondaryKeyGroup list =
    table.Indexes
    |> List.choose (fun index ->
        match index.Kind, index.Unique, index.KeyColumns |> List.forall (fun column -> column.PrefixLength.IsNone && column.Transform.IsNone) with
        | BTree, false, true ->
            index.Columns
            |> traverse (resolveColumn table.Columns)
            |> Result.toOption
            |> Option.bind (fun indices ->
                if indices.IsEmpty then
                    None
                else
                    Some
                        { Name = index.Name
                          Indices = indices })
        | _ -> None)

let private orderedKeyGroups (table: Table) : SecondaryKeyGroup list =
    let unique =
        uniqueKeyGroups table
        |> List.choose (fun group ->
            if indexesWholeColumns group then
                Some
                    { Name = group.Name
                      Indices = group.Indices }
            else
                None)

    unique @ secondaryKeyGroups table

type private FullTextKeyGroup =
    { Name: string
      Indices: int list
      CollationSpec: Collation.Collation }

let private fullTextKeyGroups (table: Table) : FullTextKeyGroup list =
    table.Indexes
    |> List.choose (fun index ->
        if index.Kind <> FullTextIndex then
            None
        else
            index.Columns
            |> traverse (resolveColumn table.Columns)
            |> Result.toOption
            |> Option.bind (function
                | [] -> None
                | first :: _ as indices ->
                    Some
                        { Name = index.Name
                          Indices = indices
                          CollationSpec =
                            table.Columns.[first].Collation
                            |> Option.bind Collation.tryFind
                            |> Option.defaultValue Collation.defaultCollation }))

let private fullTextDocument (indices: int list) (row: Value[]) =
    indices
    |> List.map (fun index -> Value.toText row.[index] |> Option.defaultValue "")
    |> String.concat " "

/// Stable equality key for values already coerced into a table column's
/// declared type. Strings use the same case-insensitive, PAD SPACE semantics
/// as Value.compare; every other same-typed value uses an exact encoding.
/// NULL has its own key; `encodeConstraintKey` omits it for MySQL UNIQUE
/// semantics.
let private encodeEqualityKey (columns: ColumnDef list) (indices: int list) (row: Value[]) : string =
    let collationOf index =
        columns.[index].Collation
        |> Option.bind Collation.tryFind
        |> Option.defaultValue Collation.defaultCollation

    let encode (index: int) =
        match row.[index] with
        | VNull -> "N"
        | VInt value -> "I" + string value
        // Same "I" prefix as `VInt`: a `BIGINT UNSIGNED` key and a signed
        // one that hold the same number must land on the same key, or a
        // unique index would let both through as distinct rows.
        | VUInt value -> "I" + string (decimal value)
        | VBit(_, value) -> "I" + string (decimal value)
        | VDouble value ->
            let normalized = if value = 0.0 then 0.0 else value
            "D" + normalized.ToString("R", CultureInfo.InvariantCulture)
        | VDecimal value -> "M" + value.ToString("G29", CultureInfo.InvariantCulture)
        // The column's own collation key — case/accent folding per the
        // declared COLLATE (utf8mb4_bin stays byte-distinct, a PAD SPACE
        // collation trims). Same rules as WHERE equality, so the index and
        // the comparison can never disagree.
        | VString value -> "S" + (collationOf index).KeyOf value
        | VBytes value -> "B" + Convert.ToHexString value
        | VDate value -> "T" + string value.DayNumber
        | VDateTime value -> "V" + string value.Ticks
        | VTime value -> "H" + string (timeTicks value)
        | VZeroDate _
        | VZeroDateTime _ -> toWire row.[index]
        | VJson value -> "J" + value.TrimEnd(' ').ToUpperInvariant()
        | VGeometry value -> "G" + Convert.ToHexString(geometryToMySqlBinary value)

    indices
        |> List.map encode
        |> List.map (fun value -> string value.Length + ":" + value)
        |> String.concat ""

let private encodeConstraintKey (columns: ColumnDef list) (indices: int list) (row: Value[]) : string option =
    if indices |> List.exists (fun index -> row.[index] = VNull) then
        None
    else
        Some(encodeEqualityKey columns indices row)

let private encodeUniqueKey (columns: ColumnDef list) (group: UniqueKeyGroup) (row: Value[]) : string option =
    if group.Indices |> List.exists (fun index -> row.[index] = VNull) then
        None
    elif indexesWholeColumns group then
        Some(encodeEqualityKey columns group.Indices row)
    else
        let keyRow = Array.copy row

        List.zip3 group.Indices group.PrefixLengths group.Transforms
        |> List.iter (fun (index, prefixLength, transform) ->
            match transform, keyRow.[index] with
            | Some Lowercase, VString text -> keyRow.[index] <- VString(text.ToLowerInvariant())
            | _ -> ()

            match prefixLength, keyRow.[index] with
            | Some length, VString text ->
                keyRow.[index] <- VString(truncateRunes length text |> Option.defaultValue text)
            | Some length, VBytes bytes -> keyRow.[index] <- VBytes(Array.truncate length bytes)
            | _ -> ())

        Some(encodeEqualityKey columns group.Indices keyRow)

type WriteLockTargets =
    { RowIds: RowId list
      Keys: string list }

let tryInsertLockTargets
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rows: Value list list)
    : WriteLockTargets option =
    let findInTable table =
        let indices =
            match columns with
            | None -> Some [ 0 .. table.Columns.Length - 1 ]
            | Some names -> names |> traverse (resolveColumn table.Columns) |> Result.toOption

        indices
        |> Option.bind (fun indices ->
            if rows |> List.exists (fun row -> row.Length <> indices.Length) then
                None
            else
                let supplied = Set.ofList indices
                let groups = uniqueKeyGroups table |> List.filter (fun group -> Set.isSubset (Set.ofList group.Indices) supplied)
                let keyIndices = groups |> Seq.collect _.Indices |> Set.ofSeq

                rows
                |> traverse (fun values ->
                    let candidate = Array.create table.Columns.Length VNull

                    List.zip indices values
                    |> List.filter (fun (index, _) -> Set.contains index keyIndices)
                    |> traverse (fun (index, value) ->
                        Diagnostics.suppress (fun () -> coerceValueWithMode (temporalCoercionMode store) table.Columns.[index] value)
                        |> Result.map (fun coerced -> candidate.[index] <- coerced))
                    |> Result.map (fun _ ->
                        groups
                        |> List.choose (fun group ->
                            encodeUniqueKey table.Columns group candidate
                            |> Option.map (fun key -> group.Name, key, group.Name + "\u0000" + key))))
                |> Result.toOption
                |> Option.map (fun encodedKeys ->
                    let encodedKeys = encodedKeys |> List.concat |> List.distinct

                    let rowIds =
                        encodedKeys
                        |> List.choose (fun (groupName, key, _) ->
                            table.UniqueIndex
                            |> Map.tryFind groupName
                            |> Option.bind (Map.tryFind key))
                        |> List.distinct

                    { RowIds = rowIds
                      Keys = encodedKeys |> List.map (fun (_, _, lockKey) -> lockKey) }))

    match store.Databases.TryGetValue dbName with
    | true, slot -> tryGetTable slot.Value tableName |> Result.toOption |> Option.bind findInTable
    | false, _ -> None

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
let private rebuildUniqueIndex (table: Table) : Map<string, Map<string, RowId>> =
    uniqueKeyGroups table
    |> List.map (fun group ->
        let inner =
            table.RowsArray.Indexed
            |> Seq.choose (fun (rowId, row) -> encodeUniqueKey table.Columns group row |> Option.map (fun key -> key, rowId))
            |> Map.ofSeq

        group.Name, inner)
    |> Map.ofList

let private rebuildSecondaryIndex (table: Table) : Map<string, Map<string, Set<RowId>>> =
    secondaryKeyGroups table
    |> List.map (fun group ->
        let buckets =
            table.RowsArray.Indexed
            |> Seq.fold
                (fun buckets (rowId, row) ->
                    let key = encodeEqualityKey table.Columns group.Indices row
                    let rows = buckets |> Map.tryFind key |> Option.defaultValue Set.empty
                    Map.add key (Set.add rowId rows) buckets)
                Map.empty

        group.Name, buckets)
    |> Map.ofList

let private rebuildSecondaryOrder (table: Table) : SecondaryOrder =
    orderedKeyGroups table
    |> List.map (fun group ->
        let entries: ImmutableSortedSet<SecondaryOrderEntry> =
            table.RowsArray.Indexed
            |> Seq.fold
                (fun entries (rowId, row) ->
                    entries.Add
                        { CollationNames = group.Indices |> List.map (fun index -> table.Columns.[index].Collation)
                          Values = group.Indices |> List.map (fun index -> row.[index])
                          RowId = rowId })
                ImmutableSortedSet<SecondaryOrderEntry>.Empty

        group.Name, entries)
    |> Map.ofList

let private rebuildFullTextIndexes (table: Table) : FullTextIndexes =
    fullTextKeyGroups table
    |> List.map (fun group ->
        group.Name,
        (table.RowsArray.Indexed
         |> Seq.map (fun (rowId, row) -> rowId, fullTextDocument group.Indices row)
         |> FullText.buildIndexWith group.CollationSpec))
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

/// Public because snapshot loading restores stored rows without persisting
/// derived indexes. WAL replay maintains them incrementally, then performs
/// one final rebuild so older snapshot formats remain compatible.
let reindexTable (table: Table) : Table =
    reindexCallCountLocal.Value <- reindexCallCountLocal.Value + 1
    { table with
        UniqueIndex = rebuildUniqueIndex table
        SecondaryIndex = rebuildSecondaryIndex table
        SecondaryOrder = rebuildSecondaryOrder table
        FullTextIndexes = rebuildFullTextIndexes table }

let private sameTableSchema (left: Table) (right: Table) =
    left.OriginalName = right.OriginalName
    && left.Columns = right.Columns
    && left.Indexes = right.Indexes
    && left.ForeignKeys = right.ForeignKeys
    && left.TableCharset = right.TableCharset
    && left.TableCollation = right.TableCollation
    && left.CreateTime = right.CreateTime

let private reindexRow
    (columns: ColumnDef list)
    (uniqueGroups: UniqueKeyGroup list)
    (secondaryGroups: SecondaryKeyGroup list)
    (removed: (RowId * Value[]) option)
    (added: (RowId * Value[]) option)
    (uniqueIndex: Map<string, Map<string, RowId>>)
    (secondaryIndex: Map<string, Map<string, Set<RowId>>>)
    (secondaryOrder: SecondaryOrder)
    : Map<string, Map<string, RowId>> * Map<string, Map<string, Set<RowId>>> * SecondaryOrder =
    let uniqueIndex =
        uniqueGroups
        |> List.fold
            (fun accIndex keyGroup ->
                let group = Map.find keyGroup.Name accIndex
                let group = removed |> Option.fold (fun g (_, row) -> encodeUniqueKey columns keyGroup row |> Option.fold (fun g' k -> Map.remove k g') g) group
                let group = added |> Option.fold (fun g (rowId, row) -> encodeUniqueKey columns keyGroup row |> Option.fold (fun g' k -> Map.add k rowId g') g) group
                Map.add keyGroup.Name group accIndex)
            uniqueIndex

    let secondaryIndex =
        secondaryGroups
        |> List.fold
            (fun accIndex keyGroup ->
                let group = Map.find keyGroup.Name accIndex
                let group =
                    removed
                    |> Option.fold (fun g (rowId, row) ->
                        let key = encodeEqualityKey columns keyGroup.Indices row
                        match Map.tryFind key g with
                        | None -> g
                        | Some rows ->
                            let remaining = Set.remove rowId rows
                            if remaining.IsEmpty then Map.remove key g else Map.add key remaining g) group
                let group =
                    added
                    |> Option.fold (fun g (rowId, row) ->
                        let key = encodeEqualityKey columns keyGroup.Indices row
                        let rows = g |> Map.tryFind key |> Option.defaultValue Set.empty
                        Map.add key (Set.add rowId rows) g) group
                Map.add keyGroup.Name group accIndex)
            secondaryIndex

    let secondaryOrder =
        let orderedGroups =
            (uniqueGroups
             |> List.choose (fun group ->
                 if indexesWholeColumns group then
                     Some
                         { Name = group.Name
                           Indices = group.Indices }
                 else
                     None))
            @ secondaryGroups

        orderedGroups
        |> List.fold
            (fun indexes keyGroup ->
                let entry rowId (row: Value[]) =
                    { CollationNames = keyGroup.Indices |> List.map (fun index -> columns.[index].Collation)
                      Values = keyGroup.Indices |> List.map (fun index -> row.[index])
                      RowId = rowId }

                let entries = Map.find keyGroup.Name indexes

                let entries =
                    removed
                    |> Option.fold
                        (fun (entries: ImmutableSortedSet<SecondaryOrderEntry>) (rowId, row) -> entries.Remove(entry rowId row))
                        entries

                let entries =
                    added
                    |> Option.fold
                        (fun (entries: ImmutableSortedSet<SecondaryOrderEntry>) (rowId, row) -> entries.Add(entry rowId row))
                        entries

                Map.add keyGroup.Name entries indexes)
            secondaryOrder

    uniqueIndex, secondaryIndex, secondaryOrder

let private publishRows (before: Table) (after: Table) : Table =
    let compactedRows = after.RowsArray.CompactIfNeeded()

    let after =
        if obj.ReferenceEquals(compactedRows, after.RowsArray) then
            after
        else
            { after with RowsArray = compactedRows }

    if before.Indexes <> after.Indexes || before.Columns <> after.Columns then
        { after with FullTextIndexes = rebuildFullTextIndexes after }
    else
        let changes = after.RowsArray.ChangesFrom before.RowsArray |> Array.ofSeq

        let indexes =
            fullTextKeyGroups after
            |> List.fold
                (fun indexes group ->
                    let update index (rowId, removed: Value[] option, added: Value[] option) =
                        let changed =
                            match removed, added with
                            | Some left, Some right -> group.Indices |> List.exists (fun column -> left.[column] <> right.[column])
                            | _ -> true

                        if not changed then
                            index
                        else
                            index
                            |> fun current -> removed |> Option.fold (fun current _ -> FullText.removeDocument rowId current) current
                            |> fun current -> added |> Option.fold (fun current row -> FullText.addDocument rowId (fullTextDocument group.Indices row) current) current

                    let index = changes |> Array.fold update (Map.find group.Name indexes)
                    Map.add group.Name index indexes)
                before.FullTextIndexes

        { after with FullTextIndexes = indexes }

let private mergeRows (dbName: string) (baseTable: Table) (batchTable: Table) (liveTable: Table) : Table =
    let conflict () = raise (LockWaitTimeout dbName)
    let rows = liveTable.RowsArray.ToBuilder()
    let uniqueGroups = uniqueKeyGroups liveTable
    let secondaryGroups = secondaryKeyGroups liveTable
    let mutable uniqueIndex = liveTable.UniqueIndex
    let mutable secondaryIndex = liveTable.SecondaryIndex
    let mutable secondaryOrder = liveTable.SecondaryOrder

    let collides rowId row =
        uniqueGroups
        |> List.exists (fun group ->
            match encodeUniqueKey liveTable.Columns group row with
            | Some key -> Map.tryFind key uniqueIndex.[group.Name] |> Option.exists ((<>) rowId)
            | None -> false)

    let publish removed added =
        let updatedUnique, updatedSecondary, updatedOrder =
            reindexRow liveTable.Columns uniqueGroups secondaryGroups removed added uniqueIndex secondaryIndex secondaryOrder

        uniqueIndex <- updatedUnique
        secondaryIndex <- updatedSecondary
        secondaryOrder <- updatedOrder

    for rowId, before, after in batchTable.RowsArray.ChangesFrom baseTable.RowsArray do
        match before, after with
        | Some baseRow, replacement ->
            match rows.TryFind rowId with
            | Some liveRow when liveRow = baseRow ->
                match replacement with
                | Some row when not (collides rowId row) ->
                    rows.[rowId] <- row
                    publish (Some(rowId, baseRow)) (Some(rowId, row))
                | Some _ -> conflict ()
                | None ->
                    rows.Remove rowId |> ignore
                    publish (Some(rowId, baseRow)) None
            | _ -> conflict ()
        | None, Some row ->
            let rowId = rows.Add row

            if collides rowId row then
                conflict ()

            publish None (Some(rowId, row))
        | None, None -> ()

    publishRows liveTable
        { liveTable with
            RowsArray = rows.DrainToImmutable()
            NextAutoId = max liveTable.NextAutoId batchTable.NextAutoId
            UniqueIndex = uniqueIndex
            SecondaryIndex = secondaryIndex
            SecondaryOrder = secondaryOrder }

let private validateMergedForeignKeys (dbName: string) (db: Database) : unit =
    let conflict () = raise (LockWaitTimeout dbName)

    for KeyValue(_, table) in db do
        for foreignKey in table.ForeignKeys do
            match Map.tryFind (normalizeTableName foreignKey.RefTable) db with
            | None -> conflict ()
            | Some parent ->
                match foreignKey.Columns |> traverse (resolveColumn table.Columns), foreignKey.RefColumns |> traverse (resolveColumn parent.Columns) with
                | Ok childIndices, Ok parentIndices ->
                    for row in table.RowsArray do
                        let childKey = childIndices |> List.map (fun index -> row.[index])

                        if childKey |> List.forall ((<>) VNull) then
                            let parentExists =
                                parent.RowsArray
                                |> Seq.exists (fun parentRow ->
                                    let parentKey = parentIndices |> List.map (fun index -> parentRow.[index])
                                    List.forall2 (fun left right -> compare left right = 0) childKey parentKey)

                            if not parentExists then
                                conflict ()
                | _ -> conflict ()

let private withWriteLocksFor
    (timeout: TimeSpan)
    (store: Store)
    (dbName: string)
    (tableName: string)
    (rowIds: RowId list)
    (keys: string list)
    body
    =
    let tableKey = lockNamespaceKey dbName tableName
    let rowLocks = store.RowLocks.GetOrAdd(tableKey, (fun _ -> ConcurrentDictionary()))
    let keyLocks = store.KeyLocks.GetOrAdd(tableKey, (fun _ -> ConcurrentDictionary()))

    let stripeIndex rowId =
        int64 (RowId.value rowId) % int64 rowLockStripeCount |> int

    let keyStripeIndex key =
        (StringComparer.Ordinal.GetHashCode key &&& Int32.MaxValue) % rowLockStripeCount

    let rowStripes =
        rowIds
        |> List.map stripeIndex
        |> List.distinct
        |> List.sort
        |> List.map (fun index -> rowLocks.GetOrAdd(index, (fun _ -> createRowLockStripe ())))

    let keyStripes =
        keys
        |> List.map keyStripeIndex
        |> List.distinct
        |> List.sort
        |> List.map (fun index -> keyLocks.GetOrAdd(index, (fun _ -> createRowLockStripe ())))

    let stripes = rowStripes @ keyStripes
    let context, releaseAfter =
        match store.TransactionLocks with
        | Some context -> context, false
        | None ->
            { Owner = Threading.Interlocked.Increment(&store.RowLockSequence.[0])
              HeldStripes = Collections.Generic.HashSet<RowLockStripe>(HashIdentity.Reference) },
            true

    let deadline = DateTime.UtcNow + timeout
    let claimed = ResizeArray<RowLockStripe>()

    let acquire stripe =
        lock stripe.SyncRoot (fun () ->
            let rec waitForOwner () =
                match stripe.Owner with
                | None ->
                    stripe.Owner <- Some context.Owner
                    lock context.HeldStripes (fun () -> context.HeldStripes.Add stripe |> ignore)
                    claimed.Add stripe
                | Some owner when owner = context.Owner -> ()
                | Some _ ->
                    let remaining = deadline - DateTime.UtcNow

                    if remaining <= TimeSpan.Zero || not (Threading.Monitor.Wait(stripe.SyncRoot, remaining)) then
                        raise (LockWaitTimeout dbName)

                    waitForOwner ()

            waitForOwner ())

    try
        try
            stripes |> List.iter acquire
            body ()
        with _ ->
            if not releaseAfter then
                releaseLockStripes context claimed

            reraise ()
    finally
        if releaseAfter then
            let temporary = { store with TransactionLocks = Some context }
            releaseTransactionLocks temporary

let private withRowLocks store dbName tableName rowIds body =
    withWriteLocksFor (Fsdb.Limits.lockWaitTimeout ()) store dbName tableName rowIds [] body

let private withInsertLocks store dbName tableName rowIds keys body =
    withWriteLocksFor (Fsdb.Limits.lockWaitTimeout ()) store dbName tableName rowIds keys body

let acquireTransactionWriteTargets
    (timeout: TimeSpan)
    (store: Store)
    (dbName: string)
    (tableName: string)
    (rowIds: RowId list)
    (keys: string list)
    : unit =
    match store.TransactionLocks with
    | Some _ -> withWriteLocksFor timeout store dbName tableName rowIds keys ignore
    | None -> invalidArg (nameof store) "transaction write claims require a transaction snapshot"

// mysql.* uses ordinary stored tables, including DML and persistence paths.
// Column shapes follow MySQL 8.4.
// These columns do not preserve charset/collation fidelity (MySQL uses
// utf8mb3_bin/ascii here) — add if a client diff ever cares.

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
      Comment = ""
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
          RowsArray = RowStore.ofSeq rows
          NextAutoId = 1L
          Indexes = []
          ForeignKeys = []
          TableCharset = None
          TableCollation = None
          TableComment = ""
          CreateTime = DateTime.Now
          UniqueIndex = Map.empty
          SecondaryIndex = Map.empty
          SecondaryOrder = Map.empty
          FullTextIndexes = Map.empty }

    // `rebuildUniqueIndex` directly, not `reindexTable`: the latter bumps the
    // replay-cost counter tests use to catch per-event reindexing, and
    // bootstrap isn't replay.
    { table with
        UniqueIndex = rebuildUniqueIndex table
        SecondaryIndex = rebuildSecondaryIndex table
        SecondaryOrder = rebuildSecondaryOrder table
        FullTextIndexes = rebuildFullTextIndexes table }

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
      sysCol "definer" (TChar 93) false (Some(VString ""))
      sysCol "action_order" (TInt false) false (Some(VInt 1L)) ]

/// `mysql.views` — fsdb's row-backed view catalog. Definitions are stored as
/// SQL text and resolved through the ordinary SELECT executor, so the rows
/// ride WAL/snapshot persistence without a separate object codec.
let mysqlViewsColumns: ColumnDef list =
    [ keyCol "view_name" 64
      keyCol "view_schema" 64
      sysCol "view_definition" TText false (Some(VString ""))
      sysCol "column_names" TText false (Some(VString ""))
      sysCol "created" (TDateTime 2) true None
      sysCol "definer" (TChar 93) false (Some(VString ""))
      sysCol "check_option" (TChar 8) false (Some(VString "NONE"))
      sysCol "security_type" (TChar 7) false (Some(VString "DEFINER")) ]

let mysqlRoutinesColumns: ColumnDef list =
    [ keyCol "routine_schema" 64
      keyCol "routine_name" 64
      sysCol "routine_definition" TText false (Some(VString ""))
      sysCol "created" (TDateTime 2) false None
      sysCol "definer" (TChar 93) false (Some(VString ""))
      sysCol "parameter_definition" TText false (Some(VString ""))
      sysCol "security_type" (TChar 7) false (Some(VString "DEFINER")) ]

let mysqlEventsColumns: ColumnDef list =
    [ keyCol "event_schema" 64
      keyCol "event_name" 64
      sysCol "schedule_definition" TText false (Some(VString ""))
      sysCol "event_definition" TText false (Some(VString ""))
      sysCol "created" (TDateTime 2) false None
      sysCol "definer" (TChar 93) false (Some(VString ""))
      sysCol "status" (TChar 8) false (Some(VString "ENABLED")) ]

/// Row-backed CHECK definitions. Keeping these beside views/triggers avoids
/// changing the binary Table snapshot layout: ordinary row WAL events carry
/// every definition, while the executor binds and evaluates the clause
/// against the final candidate row before publication.
let mysqlCheckConstraintsColumns: ColumnDef list =
    [ keyCol "constraint_name" 64
      keyCol "constraint_schema" 64
      keyCol "table_name" 64
      sysCol "check_clause" TText false (Some(VString ""))
      sysCol "enforced" (TChar 3) false (Some(VString "YES"))
      sysCol "column_name" (TVarchar 64) true None
      sysCol "generated_name" (TChar 3) false (Some(VString "NO"))
      sysCol "ordinal_position" (TInt true) false (Some(VInt 1L)) ]

let private mysqlSystemDatabase () : Database =
    [ "user", sysTable "user" mysqlUserColumns [ rootUserRow ]
      "db", sysTable "db" mysqlDbColumns []
      "tables_priv", sysTable "tables_priv" mysqlTablesPrivColumns []
      "columns_priv", sysTable "columns_priv" mysqlColumnsPrivColumns []
      "global_grants", sysTable "global_grants" mysqlGlobalGrantsColumns []
      "triggers", sysTable "triggers" mysqlTriggersColumns []
      "views", sysTable "views" mysqlViewsColumns []
      "routines", sysTable "routines" mysqlRoutinesColumns []
      "events", sysTable "events" mysqlEventsColumns []
      "check_constraints", sysTable "check_constraints" mysqlCheckConstraintsColumns [] ]
    |> Map.ofList

/// Restores catalog tables absent from snapshots written by older versions.
let ensureMysqlSchema (store: Store) : unit =
    store.Databases.TryAdd("mysql", ref (mysqlSystemDatabase ())) |> ignore

    let dbRef = store.Databases.["mysql"]

    let ensureTable name columns =
        match Map.tryFind name dbRef.Value with
        | None -> dbRef.Value <- Map.add name (sysTable name columns []) dbRef.Value
        | Some table when table.Columns.Length < columns.Length ->
            let addedColumns = List.skip table.Columns.Length columns
            let defaultValues =
                addedColumns
                |> List.map (fun column -> match column.Default with Some(DConst value) -> value | _ -> VNull)
                |> Array.ofList

            dbRef.Value <-
                Map.add
                    name
                    { table with
                        Columns = table.Columns @ addedColumns
                        RowsArray = table.RowsArray |> RowStore.map (fun row -> Array.append row defaultValues) }
                    dbRef.Value
        | Some _ -> ()

    // Old trigger rows receive an empty definer and therefore fail closed;
    // treating a missing identity as root would turn catalog migration into
    // a privilege escalation.
    [ "triggers", mysqlTriggersColumns
      "views", mysqlViewsColumns
      "routines", mysqlRoutinesColumns
      "events", mysqlEventsColumns
      "check_constraints", mysqlCheckConstraintsColumns ]
    |> List.iter (fun (name, columns) -> ensureTable name columns)

let create () : Store =
    let databases = ConcurrentDictionary<string, Database ref>()
    databases.[defaultDatabase] <- ref Map.empty
    databases.["mysql"] <- ref (mysqlSystemDatabase ())

    { Databases = databases
      ForeignKeyChecks = true
      StrictMode = true
      NoZeroDate = true
      NoZeroInDate = true
      OnlyFullGroupBy = true
      NoAutoValueOnZero = false
      ErrorForDivisionByZero = true
      ConnectionCollation = Collation.defaultCollation
      VirtualTables = Map.empty
      OnCommit = ResizeArray()
      Durability = { Sink = None }
      PendingEvents = None
      Lock = obj ()
      CommitLock = obj ()
      RowLocks = ConcurrentDictionary(StringComparer.OrdinalIgnoreCase)
      KeyLocks = ConcurrentDictionary(StringComparer.OrdinalIgnoreCase)
      RowLockSequence = [| 0L |]
      TransactionLocks = None }

/// The parent table's persistent unique-key index for exactly the column
/// order `refIdxs` resolves to, if one of its PK/UNIQUE groups matches —
/// the hash-index fast path `checkFkParent` uses for "does a parent row
/// with this key exist". `None` when no such group exists (every real
/// MySQL FK references a unique/PK constraint of the parent in matching
/// column order, so this only misses on stale/malformed FK metadata) —
/// `checkFkParent` falls back to a full scan in that case.
let private parentUniqueIndex (parent: Table) (refIdxs: int list) : Map<string, RowId> option =
    uniqueKeyGroups parent
    |> List.tryPick (fun group ->
        if group.Indices = refIdxs && indexesWholeColumns group then
            Map.tryFind group.Name parent.UniqueIndex
        else
            None)

/// A per-statement FK parent-key membership test, either a live `HashSet`
/// that a self-FK extends row by row (`Mutable`) or a snapshot of the
/// parent's own `UniqueIndex` reused as-is (`Fixed`) — `insertCore`'s
/// `foreignKeyLookups` picks whichever fits per FK; see its doc.
type private ParentKeySource =
    | Mutable of HashSet<string>
    | Fixed of Map<string, RowId>

let private parentKeySourceContains (key: string) (source: ParentKeySource) : bool =
    match source with
    | Mutable set -> set.Contains key
    | Fixed idx -> Map.containsKey key idx

let private parentKeySourceAdd (key: string) (source: ParentKeySource) : unit =
    match source with
    | Mutable set -> set.Add key |> ignore
    | Fixed _ -> () // A non-self FK's parent can't change mid-statement.

let private tableAt (store: Store) (dbName: string) (tableName: string) : Table option =
    tableSnapshot store dbName tableName |> Result.toOption

/// The equality index a one-column probe can use, independent of a specific
/// probe value. Unique keys take precedence over ordinary B-tree buckets.
let tryEqualityIndex (table: Table) (columnName: string) : (string * int * bool) option =
    match resolveColumn table.Columns columnName with
    | Error _ -> None
    | Ok index ->
        uniqueKeyGroups table
        |> List.tryPick (fun group ->
            if group.Indices = [ index ] && indexesWholeColumns group then
                Some(group.Name, index, true)
            else
                None)
        |> Option.orElseWith (fun () ->
            secondaryKeyGroups table
            |> List.tryPick (fun group ->
                if group.Indices = [ index ] && Map.containsKey group.Name table.SecondaryIndex then
                    Some(group.Name, index, false)
                else
                    None))

let private exactProbeValue (store: Store) (table: Table) (index: int) (value: Value) : Value option =
    match Diagnostics.suppress (fun () -> coerceValueWithMode (temporalCoercionMode store) table.Columns.[index] value) with
    | Ok coerced when coerced = value -> Some value
    | _ -> None

type TransientEqualityLookup =
    { TableColumns: ColumnDef list
      FindRows: Value -> (RowId * Value[]) list option }

let tryBuildTransientEqualityLookup (store: Store) (dbName: string) (tableName: string) (columnName: string) : TransientEqualityLookup option =
    tableAt store dbName tableName
    |> Option.bind (fun table ->
        resolveColumn table.Columns columnName
        |> Result.toOption
        |> Option.map (fun index ->
            let rowsByKey = Collections.Generic.Dictionary<string, ResizeArray<RowId * Value[]>>(StringComparer.Ordinal)

            for rowId, row in table.RowsArray.Indexed do
                let key = encodeEqualityKey table.Columns [ index ] row

                match rowsByKey.TryGetValue key with
                | true, rows -> rows.Add(rowId, row)
                | _ -> rowsByKey.[key] <- ResizeArray [ rowId, row ]

            let rowsFor value =
                exactProbeValue store table index value
                |> Option.map (fun value ->
                    let probe = Array.create table.Columns.Length VNull
                    probe.[index] <- value

                    match rowsByKey.TryGetValue(encodeEqualityKey table.Columns [ index ] probe) with
                    | true, rows -> List.ofSeq rows
                    | _ -> [])

            { TableColumns = table.Columns
              FindRows = rowsFor }))

let private tryUniqueKeyProbeInTable (store: Store) (table: Table) (columnName: string) (literal: Value) : (string * int) option =
    tryEqualityIndex table columnName
    |> Option.bind (fun (name, index, unique) ->
        if unique then exactProbeValue store table index literal |> Option.map (fun _ -> name, index) else None)

let tryUniqueKeyProbe
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (literal: Value)
    : (Table * string * int) option =
    tableAt store dbName tableName
    |> Option.bind (fun table -> tryUniqueKeyProbeInTable store table columnName literal |> Option.map (fun (name, index) -> table, name, index))

let tryUniqueLookup
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (literal: Value)
    : (ColumnDef list * (RowId * Value[]) list) option =
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

/// A one-column ordinary B-tree equality probe after the literal has passed
/// the same exact-value coercion guard as unique probes.
let private trySecondaryKeyProbeInTable (store: Store) (table: Table) (columnName: string) (literal: Value) : (string * int) option =
    tryEqualityIndex table columnName
    |> Option.bind (fun (name, index, unique) ->
        if not unique then exactProbeValue store table index literal |> Option.map (fun _ -> name, index) else None)

let trySecondaryKeyProbe
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (literal: Value)
    : (Table * string * int) option =
    tableAt store dbName tableName
    |> Option.bind (fun table -> trySecondaryKeyProbeInTable store table columnName literal |> Option.map (fun (name, index) -> table, name, index))

/// Candidate rows for a one-column ordinary B-tree equality probe, in stable
/// row-store scan order.
let private trySecondaryLookupInTable
    (store: Store)
    (table: Table)
    (columnName: string)
    (literal: Value)
    : (ColumnDef list * (RowId * Value[]) list) option =
    trySecondaryKeyProbeInTable store table columnName literal
    |> Option.bind (fun (indexName, index) ->
        match literal with
        | VNull -> Some(table.Columns, [])
        | _ ->
            let probeRow = Array.create (List.length table.Columns) VNull
            probeRow.[index] <- literal
            let key = encodeEqualityKey table.Columns [ index ] probeRow

            table.SecondaryIndex
            |> Map.tryFind indexName
            |> Option.map (fun buckets ->
                let rows =
                    buckets
                    |> Map.tryFind key
                    |> Option.defaultValue Set.empty
                    |> Seq.choose (fun rowId -> table.RowsArray.TryFind rowId |> Option.map (fun row -> rowId, row))
                    |> List.ofSeq

                table.Columns, rows))

let trySecondaryLookup
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (literal: Value)
    : (ColumnDef list * (RowId * Value[]) list) option =
    tableAt store dbName tableName
    |> Option.bind (fun table -> trySecondaryLookupInTable store table columnName literal)

type EqualityLookup =
    { IndexName: string
      ColumnIndices: int list
      Unique: bool
      LookupColumns: ColumnDef list
      LookupRows: (RowId * Value[]) list }

type EqualityIndex =
    { Name: string
      ColumnIndices: int list
      Unique: bool }

/// Uses a fully-bound composite key. Residual predicate evaluation remains
/// responsible for contradictory or repeated equalities.
let tryCompositeEqualityLookupInTable
    (store: Store)
    (table: Table)
    (equalities: (string * Value) list)
    : EqualityLookup option =
    let literalFor index =
        equalities
        |> List.tryPick (fun (name, value) ->
            if System.String.Equals(name, table.Columns.[index].Name, System.StringComparison.OrdinalIgnoreCase) then
                exactProbeValue store table index value
            else
                None)

    let probe (name: string, indices: int list, unique: bool) =
        if List.length indices < 2 then
            None
        else
            indices
            |> traverse (fun index ->
                match literalFor index with
                | Some value -> Ok value
                | None -> Error())
            |> Result.toOption
            |> Option.map (fun values ->
                let row = Array.create table.Columns.Length VNull
                List.zip indices values |> List.iter (fun (index, value) -> row.[index] <- value)

                let rows =
                    if values |> List.contains VNull then
                        []
                    elif unique then
                        encodeConstraintKey table.Columns indices row
                        |> Option.bind (fun key -> Map.tryFind name table.UniqueIndex |> Option.bind (Map.tryFind key))
                        |> Option.bind (fun rowId -> table.RowsArray.TryFind rowId |> Option.map (fun value -> rowId, value))
                        |> Option.toList
                    else
                        let key = encodeEqualityKey table.Columns indices row

                        table.SecondaryIndex
                        |> Map.tryFind name
                        |> Option.bind (Map.tryFind key)
                        |> Option.defaultValue Set.empty
                        |> Seq.choose (fun rowId -> table.RowsArray.TryFind rowId |> Option.map (fun value -> rowId, value))
                        |> List.ofSeq

                { IndexName = name
                  ColumnIndices = indices
                  Unique = unique
                  LookupColumns = table.Columns
                  LookupRows = rows })

    let unique =
        uniqueKeyGroups table
        |> List.choose (fun group ->
            if indexesWholeColumns group then
                Some(group.Name, group.Indices, true)
            else
                None)

    let secondary =
        secondaryKeyGroups table
        |> List.map (fun group -> group.Name, group.Indices, false)

    unique @ secondary |> List.tryPick probe

let tryCompositeEqualityLookup
    (store: Store)
    (dbName: string)
    (tableName: string)
    (equalities: (string * Value) list)
    : EqualityLookup option =
    tableAt store dbName tableName
    |> Option.bind (fun table -> tryCompositeEqualityLookupInTable store table equalities)

let tryEqualityIndexForColumns (table: Table) (columnNames: string list) : EqualityIndex option =
    columnNames
    |> traverse (resolveColumn table.Columns)
    |> Result.toOption
    |> Option.bind (fun requested ->
        let matches (_, (indices: int list), _) =
            indices.Length = requested.Length && Set.ofList indices = Set.ofList requested

        let unique =
            uniqueKeyGroups table
            |> List.choose (fun group ->
                if indexesWholeColumns group then
                    Some(group.Name, group.Indices, true)
                else
                    None)
        let secondary = secondaryKeyGroups table |> List.map (fun group -> group.Name, group.Indices, false)
        unique @ secondary
        |> List.tryFind matches
        |> Option.map (fun (name, indices, unique) ->
            { Name = name
              ColumnIndices = indices
              Unique = unique }))

let tryEqualityLookupForIndex
    (store: Store)
    (table: Table)
    (index: EqualityIndex)
    (values: Value list)
    : (RowId * Value[]) list option =
    if index.ColumnIndices.Length <> values.Length then
        None
    else
        List.zip index.ColumnIndices values
        |> traverse (fun (columnIndex, value) ->
            match exactProbeValue store table columnIndex value with
            | Some exact -> Ok(columnIndex, exact)
            | None -> Error())
        |> Result.toOption
        |> Option.map (fun exactValues ->
            let probeRow = Array.create table.Columns.Length VNull
            exactValues |> List.iter (fun (columnIndex, value) -> probeRow.[columnIndex] <- value)

            if exactValues |> List.exists (snd >> (=) VNull) then
                []
            elif index.Unique then
                encodeConstraintKey table.Columns index.ColumnIndices probeRow
                |> Option.bind (fun key -> table.UniqueIndex |> Map.tryFind index.Name |> Option.bind (Map.tryFind key))
                |> Option.bind (fun rowId -> table.RowsArray.TryFind rowId |> Option.map (fun row -> rowId, row))
                |> Option.toList
            else
                let key = encodeEqualityKey table.Columns index.ColumnIndices probeRow

                table.SecondaryIndex
                |> Map.tryFind index.Name
                |> Option.bind (Map.tryFind key)
                |> Option.defaultValue Set.empty
                |> Seq.choose (fun rowId -> table.RowsArray.TryFind rowId |> Option.map (fun row -> rowId, row))
                |> List.ofSeq)

let private trySecondaryOrderSliceInTable
    (store: Store)
    (table: Table)
    (columnName: string)
    (lower: (Value * bool) option)
    (upper: (Value * bool) option)
    (requireBound: bool)
    : SecondaryOrderSlice option =
    tryEqualityIndex table columnName
    |> Option.bind (fun (indexName, index, _) ->
        let normalizeBound = function
            | None -> Some None
            | Some(VNull, _) -> None
            | Some(value, inclusive) -> exactProbeValue store table index value |> Option.map (fun value -> Some(value, inclusive))

        match normalizeBound lower, normalizeBound upper with
        | Some lower, Some upper when not requireBound || lower.IsSome || upper.IsSome ->
            table.SecondaryOrder
            |> Map.tryFind indexName
            |> Option.map (fun entries ->
                    let entry value rowId =
                        { CollationNames = [ table.Columns.[index].Collation ]
                          Values = [ value ]
                          RowId = rowId }

                    let insertionIndex entry =
                        let position = entries.IndexOf entry
                        if position < 0 then ~~~position else position

                    let firstNonNull = insertionIndex (entry VNull (RowId.create Int32.MaxValue))

                    let first =
                        match lower with
                        | None -> if lower.IsSome || upper.IsSome then firstNonNull else 0
                        | Some(value, true) -> insertionIndex (entry value (RowId.create Int32.MinValue))
                        | Some(value, false) -> insertionIndex (entry value (RowId.create Int32.MaxValue))

                    let afterLast =
                        match upper with
                        | None -> entries.Count
                        | Some(value, true) -> insertionIndex (entry value (RowId.create Int32.MaxValue))
                        | Some(value, false) -> insertionIndex (entry value (RowId.create Int32.MinValue))

                    { IndexName = indexName
                      ColumnIndices = [ index ]
                      Entries = entries
                      First = max 0 first
                      AfterLast = max 0 (min entries.Count afterLast) })
        | _ -> None)

let private trySecondaryRangeLookupInTable
    (store: Store)
    (table: Table)
    (columnName: string)
    (lower: (Value * bool) option)
    (upper: (Value * bool) option)
    : (string * int * ColumnDef list * (RowId * Value[]) list) option =
    trySecondaryOrderSliceInTable store table columnName lower upper true
    |> Option.bind (fun slice ->
        slice.ColumnIndices
        |> List.tryExactlyOne
        |> Option.map (fun columnIndex ->
            let count = max 0 (slice.AfterLast - slice.First)

            let rows =
                Seq.init count (fun offset -> slice.Entries.[slice.First + offset])
                |> Seq.sortBy (fun entry -> RowId.value entry.RowId)
                |> Seq.choose (fun entry -> table.RowsArray.TryFind entry.RowId |> Option.map (fun row -> entry.RowId, row))
                |> List.ofSeq

            slice.IndexName, columnIndex, table.Columns, rows))

let trySecondaryRangeLookup
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (lower: (Value * bool) option)
    (upper: (Value * bool) option)
    : (string * int * ColumnDef list * (RowId * Value[]) list) option =
    tableAt store dbName tableName
    |> Option.bind (fun table -> trySecondaryRangeLookupInTable store table columnName lower upper)

let private orderedEntries (direction: Direction) (slice: SecondaryOrderSlice) : SecondaryOrderEntry seq =
    match direction with
    | Asc ->
        let count = max 0 (slice.AfterLast - slice.First)
        Seq.init count (fun offset -> slice.Entries.[slice.First + offset])
    | Desc ->
        seq {
            let mutable after = slice.AfterLast

            while after > slice.First do
                let last = after - 1
                let value = slice.Entries.[last]
                let mutable first = last

                while
                    first > slice.First
                    && compareIndexedKeys value.CollationNames slice.Entries.[first - 1].Values value.Values = 0 do
                    first <- first - 1

                for position in first .. last do
                    yield slice.Entries.[position]

                after <- first
        }

let trySecondaryOrderedLookup
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (lower: (Value * bool) option)
    (upper: (Value * bool) option)
    (direction: Direction)
    : (string * int * ColumnDef list * int * Value[] seq) option =
    tableAt store dbName tableName
    |> Option.bind (fun table ->
        trySecondaryOrderSliceInTable store table columnName lower upper false
        |> Option.bind (fun slice ->
            slice.ColumnIndices
            |> List.tryExactlyOne
            |> Option.map (fun columnIndex ->
                let rows =
                    orderedEntries direction slice
                    |> Seq.choose (fun entry -> table.RowsArray.TryFind entry.RowId)

                slice.IndexName, columnIndex, table.Columns, max 0 (slice.AfterLast - slice.First), rows)))

type OrderedLookup =
    { OrderedIndexName: string
      OrderedColumnIndices: int list
      OrderedColumns: ColumnDef list
      OrderedRowCount: int
      OrderedRows: Value[] seq }

let tryCompositeOrderedLookup
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnNames: string list)
    (direction: Direction)
    : OrderedLookup option =
    tableAt store dbName tableName
    |> Option.bind (fun table ->
        columnNames
        |> traverse (resolveColumn table.Columns)
        |> Result.toOption
        |> Option.bind (fun indices ->
            orderedKeyGroups table
            |> List.tryFind (fun group -> group.Indices = indices && indices.Length > 1)
            |> Option.bind (fun group ->
                table.SecondaryOrder
                |> Map.tryFind group.Name
                |> Option.map (fun entries ->
                    let slice =
                        { IndexName = group.Name
                          ColumnIndices = indices
                          Entries = entries
                          First = 0
                          AfterLast = entries.Count }

                    { OrderedIndexName = group.Name
                      OrderedColumnIndices = indices
                      OrderedColumns = table.Columns
                      OrderedRowCount = entries.Count
                      OrderedRows =
                        orderedEntries direction slice
                        |> Seq.choose (fun entry -> table.RowsArray.TryFind entry.RowId) }))))

/// The equality-index probe in the order execution considers it: a unique
/// key first for each WHERE equality, then an ordinary B-tree bucket.
let tryEqualityKeyProbe
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (literal: Value)
    : (Table * string * int * bool) option =
    tableAt store dbName tableName
    |> Option.bind (fun table ->
        tryEqualityIndex table columnName
        |> Option.bind (fun (name, index, unique) ->
            exactProbeValue store table index literal |> Option.map (fun _ -> table, name, index, unique)))

/// Exact-value coercion keeps an index key equivalent to a stored value;
/// callers scan when that proof is unavailable.
let tryEqualityLookupInTable
    (store: Store)
    (table: Table)
    (columnName: string)
    (literal: Value)
    : (ColumnDef list * (RowId * Value[]) list) option =
    tryEqualityIndex table columnName
    |> Option.bind (fun (_, _, unique) ->
        if unique then
            tryUniqueKeyProbeInTable store table columnName literal
            |> Option.map (fun (indexName, index) ->
                let probeRow = Array.create (List.length table.Columns) VNull
                probeRow.[index] <- literal

                let rows =
                    match encodeConstraintKey table.Columns [ index ] probeRow with
                    | None -> []
                    | Some key ->
                        table.UniqueIndex
                        |> Map.tryFind indexName
                        |> Option.bind (Map.tryFind key)
                        |> Option.map (fun rowId -> rowId, table.RowsArray.[rowId])
                        |> Option.toList

                table.Columns, rows)
        else
            trySecondaryLookupInTable store table columnName literal)

let tryEqualityLookup
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columnName: string)
    (literal: Value)
    : (ColumnDef list * (RowId * Value[]) list) option =
    tableAt store dbName tableName
    |> Option.bind (fun table -> tryEqualityLookupInTable store table columnName literal)

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
    let bytesPerCharacter =
        match c.Charset |> Option.map _.ToLowerInvariant() with
        | Some "ascii"
        | Some "latin1" -> 1
        | Some "utf8mb3"
        | Some "utf8" -> 3
        | _ -> 4

    let maxVarcharLength = 65535 / bytesPerCharacter

    if c.OnUpdateCurrentTimestamp && not (supportsCurrentTimestamp c) then
        Error(ExpressionError(1294, sprintf "Invalid ON UPDATE clause for '%s' column" c.Name))
    else
        match c.Type with
        | TChar length when length < 1 || length > 255 ->
            Error(ExpressionError(1074, sprintf "Column length too big for column '%s' (max = 255); use BLOB or TEXT instead" c.Name))
        | TVarchar length when length < 1 || length > maxVarcharLength ->
            Error(
                ExpressionError(
                    1074,
                    sprintf "Column length too big for column '%s' (max = %d); use BLOB or TEXT instead" c.Name maxVarcharLength
                )
            )
        | TBit width when width < 1 -> Error(ExpressionError(3013, sprintf "Invalid size for column '%s'." c.Name))
        | TBit width when width > 64 ->
            Error(ExpressionError(1439, sprintf "Display width out of range for column '%s' (max = 64)" c.Name))
        | TDateTime fsp
        | TTimestamp fsp
        | TTime fsp when fsp > 6 -> Error(PrecisionTooBig(c.Name, fsp))
        | TDecimal(precision, _, _) when precision < 1 || precision > 65 ->
            Error(ExpressionError(1426, sprintf "Too-big precision %d specified for '%s'. Maximum is 65." precision c.Name))
        | TDecimal(_, scale, _) when scale < 0 || scale > 30 ->
            Error(ExpressionError(1425, sprintf "Too big scale %d specified for column '%s'. Maximum is 30." scale c.Name))
        | TDecimal(precision, scale, _) when scale > precision ->
            Error(ExpressionError(1427, sprintf "For decimal(M,D), M must be >= D (column '%s')." c.Name))
    // VECTOR shares the parse-anything-validate-at-DDL discipline: the
    // parser accepts any dimension, MySQL 9's 1..16383 range is enforced
    // here with real MySQL's 1074 shape for an over-long column.
        | TVector dim when dim < 1 || dim > 16383 ->
            Error(ExpressionError(1074, sprintf "Column length too big for column '%s' (max = 16383); use BLOB or TEXT instead" c.Name))
        | _ when c.Comment.EnumerateRunes() |> Seq.length > 1024 ->
            Error(ExpressionError(1629, sprintf "Comment for field '%s' is too long (max = 1024)" c.Name))
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

let private checkGeometryKeyColumns (columns: ColumnDef list) (indexes: IndexDef list) : Result<unit, StorageError> =
    let isGeometry (column: ColumnDef) =
        match column.Type with
        | TGeometry _ -> true
        | _ -> false

    match columns |> List.tryFind (fun column -> isGeometry column && (column.PrimaryKey || column.Unique)) with
    | Some _ -> Error(ExpressionError(3728, "Spatial indexes can't be primary or unique indexes."))
    | None ->
        indexes
        |> List.collect _.Columns
        |> List.tryFind (fun name -> columns |> List.exists (fun column -> isGeometry column && String.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase)))
        |> function
            | Some _ -> Error(ExpressionError(1235, "This version of MySQL doesn't yet support 'SPATIAL indexes'"))
            | None -> Ok()

let private checkIndexLengths (columns: ColumnDef list) (indexes: IndexDef list) : Result<unit, StorageError> =
    let bytesPerCharacter (column: ColumnDef) =
        match column.Charset |> Option.map _.ToLowerInvariant() with
        | Some "ascii"
        | Some "latin1" -> 1
        | Some "utf8mb3"
        | Some "utf8" -> 3
        | _ -> 4

    let fullLength column =
        match column.Type with
        | TChar length
        | TVarchar length -> Some(length * bytesPerCharacter column)
        | TBinary length
        | TVarBinary length -> Some length
        | TTinyInt _
        | TBool -> Some 1
        | TSmallInt _ -> Some 2
        | TMediumInt _ -> Some 3
        | TInt _
        | TFloat _ -> Some 4
        | TBigInt _
        | TDouble _ -> Some 8
        | TDecimal(precision, _, _) -> Some((precision + 2) / 2)
        | TBit width -> Some((width + 7) / 8)
        | TDate -> Some 3
        | TDateTime _
        | TTimestamp _ -> Some 8
        | TTime _ -> Some 3
        | TYear -> Some 1
        | TEnum _ -> Some 2
        | TSet values -> Some((values.Length + 7) / 8)
        | _ -> None

    let partLength (index: IndexDef) (column: IndexColumn) =
        match column.Transform with
        | Some(Expression _) when index.Unique -> Error(ExpressionError(1235, "This version of fsdb doesn't yet support unique expression indexes other than LOWER(column)"))
        | Some(Expression _) -> Ok 0
        | _ ->
            match columns |> List.tryFind (fun definition -> String.Equals(definition.Name, column.Name, StringComparison.OrdinalIgnoreCase)) with
            | None -> Error(ExpressionError(1072, sprintf "Key column '%s' doesn't exist in table" column.Name))
            | Some definition when column.Transform = Some Lowercase ->
                match definition.Type with
                | TChar _
                | TVarchar _ -> fullLength definition |> Option.defaultValue 0 |> Ok
                | _ -> Error(ExpressionError(3757, "Cannot create a functional index on this expression."))
            | Some definition ->
                match column.PrefixLength, fullLength definition with
                | Some prefix, _ when prefix < 1 -> Error(ExpressionError(1089, "Incorrect prefix key"))
                | Some prefix, _ ->
                    let multiplier =
                        match definition.Type with
                        | TChar _
                        | TVarchar _
                        | TTinyText
                        | TText
                        | TMediumText
                        | TLongText -> bytesPerCharacter definition
                        | _ -> 1

                    Ok(prefix * multiplier)
                | None, Some length -> Ok length
                | None, None when index.Kind = FullTextIndex -> Ok 0
                | None, None ->
                    Error(ExpressionError(1170, sprintf "BLOB/TEXT column '%s' used in key specification without a key length" definition.Name))

    indexes
    |> traverse (fun index ->
        index.KeyColumns
        |> traverse (partLength index)
        |> Result.bind (fun lengths ->
            if index.Kind = BTree && List.sum lengths > 3072 then
                Error(ExpressionError(1071, "Specified key was too long; max key length is 3072 bytes"))
            else
                Ok()))
    |> Result.map ignore

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

        let indexedColumns =
            ix.Columns
            |> List.choose (fun name ->
                columns
                |> List.tryFind (fun column -> String.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase)))

        match indexedColumns |> List.tryFind (fun column -> not (isTextual column.Type)) with
        | Some bad -> Error(FullTextColumnNotAllowed bad.Name)
        | None ->
            match indexedColumns with
            | []
            | [ _ ] -> Ok()
            | first :: rest ->
                let collationName (column: ColumnDef) =
                    column.Collation |> Option.defaultValue Collation.defaultCollation.Name

                match
                    rest
                    |> List.tryFind (fun column ->
                        not (String.Equals(collationName first, collationName column, StringComparison.OrdinalIgnoreCase)))
                with
                | Some bad -> Error(FullTextColumnNotAllowed bad.Name)
                | None -> Ok()

let private validateForeignKeyDefinition
    (checkForeignKeys: bool)
    (db: Database)
    (tableName: string)
    (childColumns: ColumnDef list)
    (childIndexes: IndexDef list)
    (foreignKey: ForeignKeyDef)
    : Result<unit, StorageError> =
    let equal (left: string) (right: string) = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)
    let setNull action = action |> Option.exists (fun value -> equal value "SET NULL")

    let findColumn (name: string) (columns: ColumnDef list) =
        columns |> List.tryFind (fun column -> equal column.Name name)

    let invalidDefinition () =
        Error(
            ExpressionError(
                1239,
                sprintf
                    "Incorrect foreign key definition for '%s': Key reference and table reference don't match"
                    foreignKey.Name
            )
        )

    let nonNullableChild =
        foreignKey.Columns
        |> List.tryPick (fun name ->
            findColumn name childColumns
            |> Option.filter (fun column -> not column.Nullable)
            |> Option.map _.Name)

    let missingChild = foreignKey.Columns |> List.tryFind (fun name -> findColumn name childColumns |> Option.isNone)

    match missingChild, List.length foreignKey.Columns = List.length foreignKey.RefColumns, nonNullableChild with
    | Some column, _, _ -> Error(ExpressionError(1072, sprintf "Key column '%s' doesn't exist in table" column))
    | None, false, _ -> invalidDefinition ()
    | None, true, Some column when setNull foreignKey.OnDelete || setNull foreignKey.OnUpdate ->
        Error(
            ExpressionError(
                1830,
                sprintf
                    "Column '%s' cannot be NOT NULL: needed in a foreign key constraint '%s' SET NULL"
                    column
                    foreignKey.Name
            )
        )
    | None, true, _ ->
        let parent =
            if equal tableName foreignKey.RefTable then
                Some(childColumns, childIndexes)
            else
                Map.tryFind (normalizeTableName foreignKey.RefTable) db
                |> Option.map (fun table -> table.Columns, table.Indexes)

        match parent with
        | None when not checkForeignKeys -> Ok()
        | None -> Error(ExpressionError(1824, sprintf "Failed to open the referenced table '%s'" foreignKey.RefTable))
        | Some(parentColumns, parentIndexes) ->
            match foreignKey.RefColumns |> List.tryFind (fun name -> findColumn name parentColumns |> Option.isNone) with
            | Some column ->
                Error(
                    ExpressionError(
                        3734,
                        sprintf
                            "Failed to add the foreign key constraint. Missing column '%s' for constraint '%s' in the referenced table '%s'"
                            column
                            foreignKey.Name
                            foreignKey.RefTable
                    )
                )
            | None when not checkForeignKeys -> Ok()
            | None ->
                let sameColumns left right = List.forall2 equal left right

                let primary =
                    parentIndexes
                    |> List.tryFind isPrimaryIndex
                    |> Option.map _.Columns
                    |> Option.defaultWith (fun () ->
                        parentColumns
                        |> List.choose (fun column -> if column.PrimaryKey then Some column.Name else None))

                let uniqueKeys =
                    [ if not primary.IsEmpty then primary
                      yield!
                          parentIndexes
                          |> List.filter (fun index -> index.Unique && not (isPrimaryIndex index))
                          |> List.map _.Columns
                      yield! parentColumns |> List.filter _.Unique |> List.map (fun column -> [ column.Name ]) ]

                if uniqueKeys
                   |> List.exists (fun columns -> List.length columns = List.length foreignKey.RefColumns && sameColumns columns foreignKey.RefColumns) then
                    Ok()
                else
                    Error(
                        ExpressionError(
                            6125,
                            sprintf
                                "Failed to add the foreign key constraint. Missing unique key for constraint '%s' in the referenced table '%s'"
                                foreignKey.Name
                                foreignKey.RefTable
                        )
                    )

let private normalizePrimaryKeyNullability (columns: ColumnDef list) =
    columns
    |> List.map (fun column ->
        if column.PrimaryKey then
            { column with Nullable = false }
        else
            column)

let private validateTableComment (tableName: string) (comment: string) =
    if comment.EnumerateRunes() |> Seq.length > 2048 then
        Error(ExpressionError(1628, sprintf "Comment for table '%s' is too long (max = 2048)" tableName))
    else
        Ok comment

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
    (tableComment: string option)
    : Result<unit, StorageError> =
    ensureDatabase store dbName
    let columns = normalizePrimaryKeyNullability columns

    let createEvent (createTime, columns) =
        let statement =
            CreateTable
                { Name = tableName
                  Columns = columns
                  Indexes = indexes
                  ForeignKeys = foreignKeys
                  Checks = []
                  IfNotExists = false
                  Charset = tableCharset
                  Collation = tableCollation
                  AutoIncrementSeed = autoIncrementSeed
                  Comment = tableComment }

        [ SchemaChangedAt(dbName, statement, createTime) ]

    let result =
        tableComment
        |> Option.defaultValue ""
        |> validateTableComment tableName
        |> Result.bind (fun tableComment ->
            columns
            |> traverse (normalizeDefault (temporalCoercionMode store))
            |> Result.bind (fun columns ->
                withDatabasePublishing store dbName createEvent (fun db ->
                    let key = normalizeTableName tableName

                    match columns |> traverse validateColumnType with
                    | Error e -> Error e
                    | Ok _ ->
                        match checkVectorKeyColumns columns indexes, checkGeometryKeyColumns columns indexes, checkIndexLengths columns indexes with
                        | Error e, _, _
                        | _, Error e, _
                        | _, _, Error e -> Error e
                        | Ok(), Ok(), Ok() ->
                            if Map.containsKey key db then
                                Error(TableExists tableName)
                            else
                                match foreignKeys |> traverse (validateForeignKeyDefinition store.ForeignKeyChecks db tableName columns indexes) with
                                | Error e -> Error e
                                | Ok _ ->
                                    match indexes |> List.tryPick (fun ix -> match checkFullTextColumns columns ix with Error e -> Some e | Ok() -> None) with
                                    | Some e -> Error e
                                    | None ->
                                        let createTime = DateTime.Now

                                        let table =
                                            { OriginalName = tableName
                                              Columns = columns
                                              RowsArray = RowStore.empty
                                              NextAutoId = autoIncrementSeed |> Option.defaultValue 1L
                                              Indexes = indexes
                                              ForeignKeys = foreignKeys
                                              TableCharset = tableCharset
                                              TableCollation = tableCollation
                                              TableComment = tableComment
                                              CreateTime = createTime
                                              UniqueIndex = Map.empty
                                              SecondaryIndex = Map.empty
                                              SecondaryOrder = Map.empty
                                              FullTextIndexes = Map.empty }

                                        Ok(Map.add key (reindexTable table) db, (createTime, columns)))))

    result |> Result.map ignore


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
    createTableSeeded store dbName tableName columns indexes foreignKeys tableCharset tableCollation None None

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
    withDatabasePublishing
        store
        dbName
        (fun () -> [ SchemaChanged(dbName, DropTable([ tableName ], false)) ])
        (fun db ->
            let key = normalizeTableName tableName

            match virtualWriteGuard store dbName tableName with
            | Error e -> Error e
            | Ok() when Map.containsKey key db -> Ok(Map.remove key db, ())
            | Ok() -> Error(NoSuchTable tableName))

let truncate (store: Store) (dbName: string) (tableName: string) : Result<unit, StorageError> =
    // MySQL implements TRUNCATE as drop-and-recreate, so CREATE_TIME resets.
    withDatabasePublishing
        store
        dbName
        (fun createTime -> [ SchemaChangedAt(dbName, Truncate tableName, createTime) ])
        (fun db ->
            virtualWriteGuard store dbName tableName
            |> Result.bind (fun () -> tryGetTable db tableName)
            |> Result.map (fun table ->
                let createTime = DateTime.Now
                let table = reindexTable { table with RowsArray = RowStore.empty; NextAutoId = 1L; CreateTime = createTime }
                Map.add (normalizeTableName tableName) table db, createTime))
    |> Result.map ignore

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
/// already correct instead of being re-derived here.
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
    | TGeometry _ -> Error(ExpressionError(1364, sprintf "Field '%s' doesn't have a default value" col.Name))
    | TEnum _ -> Ok(VInt 1L)
    | TTime _ -> Ok(VTime(timeValueOrClamp 0L))
    | TDate ->
        tryZeroDate 0 0 0
        |> Option.map (VZeroDate >> Ok)
        |> Option.defaultWith (fun () -> Error(ZeroTemporalForColumn("date", "0000-00-00", col.Name)))
    | TDateTime _
    | TTimestamp _ ->
        tryZeroDate 0 0 0
        |> Option.bind (fun date -> tryZeroDateTime date 0 0 0 0)
        |> Option.map (VZeroDateTime >> Ok)
        |> Option.defaultWith (fun () -> Error(ZeroTemporalForColumn("datetime", "0000-00-00 00:00:00", col.Name)))
    | _ -> Ok(VInt 0L)

/// The value an added column gets filled in with for every row that already
/// exists — its `DEFAULT` if it has one, `NULL` for a nullable column with
/// none, and otherwise (`NOT NULL`, no `DEFAULT`) the type's own implicit
/// zero value, coerced/rescaled the same way a written value would be (see
/// `implicitZeroSeed`).
let private addedColumnFill (mode: TemporalCoercionMode) (col: ColumnDef) : Result<Value, StorageError> =
    match col.Default with
    | Some _ -> coerceStoredValueWithMode mode col (evalDefault col)
    | None ->
        if col.Nullable then
            Ok VNull
        else
            implicitZeroSeed col |> Result.bind (coerceStoredValueWithMode mode col)

/// Inserts `x` at `idx` (clamped to `xs`'s length, so `idx = List.length xs`
/// appends) — used by `AFTER`/`FIRST` column positioning, since `Columns`
/// and each row's `Value[]` are both plain lists/arrays with no built-in
/// "insert at" the way a `ResizeArray` would have.
let private insertAt (idx: int) (x: 'a) (xs: 'a list) : 'a list =
    let before, after = xs |> List.splitAt (min idx (List.length xs))
    before @ [ x ] @ after

let private renameIndexColumn oldName newName (indexes: IndexDef list) =
    let rename (column: IndexColumn) =
        if String.Equals(column.Name, oldName, StringComparison.OrdinalIgnoreCase) then
            { column with Name = newName }
        else
            column

    indexes
    |> List.map (fun index ->
        { index with
            KeyColumns = index.KeyColumns |> List.map rename })

let private removeIndexColumn columnName (indexes: IndexDef list) =
    indexes
    |> List.choose (fun index ->
        let columns =
            index.KeyColumns
            |> List.filter (fun (column: IndexColumn) -> not (String.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase)))

        if columns.IsEmpty then None else Some { index with KeyColumns = columns })

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

let private tryDuplicateConstraintValue (columns: ColumnDef list) (indices: int list) (rows: Value[] seq) =
    let rec loop seen remaining =
        match remaining with
        | [] -> None
        | row :: rest ->
            match encodeConstraintKey columns indices row with
            | Some key when Set.contains key seen ->
                indices
                |> List.map (fun index -> row.[index] |> toText |> Option.defaultValue "NULL")
                |> String.concat "-"
                |> Some
            | Some key -> loop (Set.add key seen) rest
            | None -> loop seen rest

    rows |> List.ofSeq |> loop Set.empty

let private tryDuplicateUniqueValue (columns: ColumnDef list) (group: UniqueKeyGroup) (rows: Value[] seq) =
    let rec loop seen remaining =
        match remaining with
        | [] -> None
        | row :: rest ->
            match encodeUniqueKey columns group row with
            | Some key when Set.contains key seen ->
                group.Indices
                |> List.map (fun index -> row.[index] |> toText |> Option.defaultValue "NULL")
                |> String.concat "-"
                |> Some
            | Some key -> loop (Set.add key seen) rest
            | None -> loop seen rest

    rows |> List.ofSeq |> loop Set.empty

/// Applies one `Ast.AlterAction` to `table`, returning its replacement and,
/// for `RenameTo`, the new key it should be re-filed under in the database
/// map (`None` means "same key").
let private applyAlterAction (mode: TemporalCoercionMode) (table: Table) (action: AlterAction) : Result<Table * string option, StorageError> =
    let strict = mode.Strict
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
        let value =
            match newDef.Type, v with
            | (TChar n | TVarchar n), VString s ->
                let text =
                    match newDef.Type with
                    | TChar _ -> s.TrimEnd([| ' ' |])
                    | _ -> s

                match truncateRunes n text with
                | Some truncated when strict -> Error(DataTruncatedForColumn newDef.Name)
                | Some truncated -> Ok(VString truncated)
                | None -> Ok(VString text)
            | _ -> Ok v

        value
        |> Result.bind (coerceValueWithMode mode newDef)
        |> Result.bind (fun coerced ->
            match newDef.Type, coerced with
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
            |> Result.bind (fun () -> checkGeometryKeyColumns [ col ] [])
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
            |> Result.bind (fun () -> checkGeometryKeyColumns [ { col with Name = oldName } ] table.Indexes)
        // The key-introducing actions must refuse a VECTOR column the same
        // way CREATE TABLE does — otherwise ALTER is a back door into the
        // very keys `checkVectorKeyColumns` exists to forbid.
        | AddIndex ix ->
            checkVectorKeyColumns table.Columns [ ix ]
            |> Result.bind (fun () -> checkGeometryKeyColumns table.Columns [ ix ])
        | AddPrimaryKey cols ->
            checkVectorKeyColumns
                (table.Columns |> List.map (fun c -> if List.contains c.Name cols then { c with PrimaryKey = true } else c))
                []
            |> Result.bind (fun () ->
                checkGeometryKeyColumns
                    (table.Columns |> List.map (fun c -> if List.contains c.Name cols then { c with PrimaryKey = true } else c))
                    [])
        | DropPrimaryKey -> Ok()
        | _ -> Ok()

    let defaultCheck =
        match action with
        | AddColumn(col, _)
        | ModifyColumn(col, _)
        | ChangeColumn(_, col, _) -> normalizeDefault mode col |> Result.map ignore
        | SetDefault(column, value) ->
            resolveColumn table.Columns column
            |> Result.bind (fun index -> normalizeDefault mode { table.Columns.[index] with Default = value } |> Result.map ignore)
        | _ -> Ok()

    match fspCheck, defaultCheck with
    | Error error, _
    | _, Error error -> Error error
    | Ok(), Ok() ->

    match action with
    | AddColumn(col, position) ->
        // Only actually needed to fill a row when there's at least one —
        // MySQL never evaluates (and so never errors on) a `NOT NULL`
        // column's implicit zero value against an empty table.
        let fill = if table.RowsArray.IsEmpty then Ok VNull else addedColumnFill mode col

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
                    RowsArray = table.RowsArray |> RowStore.map (fun row -> row |> Array.toList |> insertAt idx fill |> Array.ofList) },
                None))
    | DropColumn name ->
        resolveColumn table.Columns name
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> List.indexed |> List.filter (fun (i, _) -> i <> idx) |> List.map snd
                RowsArray = table.RowsArray |> RowStore.map (removeColumnAt idx)
                Indexes = removeIndexColumn name table.Indexes },
            None)
    | ModifyColumn(newDef, position)
    | ChangeColumn(_, newDef, position) ->
        let oldName =
            match action with
            | ChangeColumn(oldName, _, _) -> oldName
            | _ -> newDef.Name

        resolveColumn table.Columns oldName
        |> Result.bind (fun oldIdx ->
            let oldDef = table.Columns.[oldIdx]

            let newDef =
                { newDef with
                    PrimaryKey = oldDef.PrimaryKey || newDef.PrimaryKey
                    Unique = oldDef.Unique || newDef.Unique }

            let columnsExcludingSelf = table.Columns |> List.indexed |> List.filter (fun (i, _) -> i <> oldIdx) |> List.map snd

            resolvePosition columnsExcludingSelf oldIdx position
            |> Result.bind (fun newIdx ->
                table.RowsArray.Indexed
                |> List.ofSeq
                |> traverse (fun (rowId, r: Value[]) ->
                    recoerce newDef r.[oldIdx]
                    |> Result.map (fun value -> rowId, (r |> removeColumnAt oldIdx |> Array.toList |> insertAt newIdx value |> Array.ofList)))
                |> Result.bind (fun rows ->
                    let rowStore = table.RowsArray.ToBuilder()

                    for rowId, row in rows do
                        rowStore.[rowId] <- row

                    let candidate =
                        { table with
                            Columns = columnsExcludingSelf |> insertAt newIdx newDef
                            RowsArray = rowStore.DrainToImmutable()
                            Indexes = renameIndexColumn oldName newDef.Name table.Indexes }

                    // A narrowing re-coercion that folds two unique-key
                    // values together must fail with 1062 (MySQL errors even
                    // non-strict) — otherwise `reindexTable`'s last-wins
                    // rebuild would silently drop rows from the index.
                    let collision =
                        uniqueKeyGroups candidate
                        |> List.tryPick (fun group ->
                            rows
                            |> Seq.map snd
                            |> tryDuplicateUniqueValue candidate.Columns group
                            |> Option.map (fun value -> DuplicateKey(group.Name, value)))

                    match collision with
                    | Some e -> Error e
                    | None -> Ok(candidate, None))))
    | RenameTo newName -> Ok({ table with OriginalName = newName }, Some(normalizeTableName newName))
    | RenameColumnTo(oldName, newName) ->
        resolveColumn table.Columns oldName
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> List.mapi (fun i c -> if i = idx then { c with Name = newName } else c)
                Indexes = renameIndexColumn oldName newName table.Indexes },
            None)
    | AddIndex ix when ix.Unique ->
        // `CREATE UNIQUE INDEX`/`ALTER TABLE ... ADD UNIQUE` over rows that
        // already collide must fail with the same 1062 a plain INSERT would
        // give — otherwise `reindexTable` (Map.ofList, last-wins) silently
        // drops every row but one from the new UniqueIndex, and both the
        // fast path and the constraint itself go missing from then on.
        checkIndexLengths table.Columns [ ix ]
        |> Result.bind (fun () -> ix.Columns |> traverse (resolveColumn table.Columns))
        |> Result.bind (fun idxs ->
            let group =
                { Name = ix.Name
                  Indices = idxs
                  PrefixLengths = ix.KeyColumns |> List.map _.PrefixLength
                  Transforms = ix.KeyColumns |> List.map _.Transform }

            match tryDuplicateUniqueValue table.Columns group table.RowsArray with
            | Some value -> Error(DuplicateKey(ix.Name, value))
            | None -> Ok({ table with Indexes = table.Indexes @ [ ix ] }, None))
    | AddIndex ix ->
        checkIndexLengths table.Columns [ ix ]
        |> Result.bind (fun () -> checkFullTextColumns table.Columns ix)
        |> Result.map (fun () -> { table with Indexes = table.Indexes @ [ ix ] }, None)
    | DropIndexAction name ->
        Ok(
            { table with
                Indexes = table.Indexes |> List.filter (fun ix -> not (String.Equals(ix.Name, name, StringComparison.OrdinalIgnoreCase))) },
            None
        )
    | RenameIndex(oldName, newName) ->
        let equal left right = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

        match table.Indexes |> List.tryFind (fun index -> equal index.Name oldName) with
        | None -> Error(ExpressionError(1176, sprintf "Key '%s' doesn't exist in table '%s'" oldName table.OriginalName))
        | Some _ when table.Indexes |> List.exists (fun index -> equal index.Name newName) ->
            Error(ExpressionError(1061, sprintf "Duplicate key name '%s'" newName))
        | Some _ ->
            let indexes =
                table.Indexes
                |> List.map (fun index -> if equal index.Name oldName then { index with Name = newName } else index)

            Ok(
                { table with
                    Indexes = indexes },
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
        if table.Columns |> List.exists _.PrimaryKey then
            Error(ExpressionError(1068, "Multiple primary key defined"))
        else
            cols
            |> traverse (resolveColumn table.Columns)
            |> Result.bind (fun indices ->
                let rows = table.RowsArray :> Value[] seq

                if rows |> Seq.exists (fun row -> indices |> List.exists (fun index -> row.[index] = VNull)) then
                    Error(ExpressionError(1138, "Invalid use of NULL value"))
                else
                    match tryDuplicateConstraintValue table.Columns indices rows with
                    | Some value -> Error(DuplicateKey("PRIMARY", value))
                    | None ->
                        let primaryIndices = Set.ofList indices

                        let columns =
                            table.Columns
                            |> List.mapi (fun index column ->
                                if Set.contains index primaryIndices then
                                    { column with PrimaryKey = true; Nullable = false }
                                else
                                    column)

                        let primary =
                            { Name = "PRIMARY"
                              KeyColumns = cols |> List.map (fun name -> { Name = name; PrefixLength = None; Transform = None })
                              Unique = true
                              Kind = BTree }

                        Ok(
                            { table with
                                Columns = columns
                                Indexes = primary :: table.Indexes },
                            None
                        ))
    | DropPrimaryKey ->
        if table.Columns |> List.exists _.PrimaryKey then
            let columns = table.Columns |> List.map (fun column -> { column with PrimaryKey = false })
            let indexes = table.Indexes |> List.filter (not << isPrimaryIndex)

            Ok(
                { table with
                    Columns = columns
                    Indexes = indexes },
                None
            )
        else
            Error(ExpressionError(1091, "Can't DROP 'PRIMARY'; check that column/key exists"))
    | SetDefault(column, value) ->
        resolveColumn table.Columns column
        |> Result.map (fun index ->
            let columns =
                table.Columns
                |> List.mapi (fun i definition ->
                    if i = index then
                        { definition with Default = value }
                    else
                        definition)

            { table with
                Columns = columns },
            None)
    | ConvertCharset(charset, requestedCollation) ->
        let charset = charset.ToLowerInvariant()

        let defaultCollation =
            match charset with
            | "utf8mb4" -> "utf8mb4_0900_ai_ci"
            | "utf8"
            | "utf8mb3" -> "utf8mb3_general_ci"
            | "latin1" -> "latin1_swedish_ci"
            | "ascii" -> "ascii_general_ci"
            | _ -> "binary"

        let collation = requestedCollation |> Option.defaultValue defaultCollation

        let compatible =
            match charset with
            | "utf8mb4" -> collation.StartsWith("utf8mb4_", StringComparison.OrdinalIgnoreCase)
            | "utf8"
            | "utf8mb3" ->
                collation.StartsWith("utf8mb3_", StringComparison.OrdinalIgnoreCase)
                || collation.StartsWith("utf8_", StringComparison.OrdinalIgnoreCase)
            | "latin1" -> collation.StartsWith("latin1_", StringComparison.OrdinalIgnoreCase)
            | "ascii" -> collation.StartsWith("ascii_", StringComparison.OrdinalIgnoreCase)
            | "binary" -> String.Equals(collation, "binary", StringComparison.OrdinalIgnoreCase)
            | _ -> false

        if not compatible then
            Error(ExpressionError(1253, sprintf "COLLATION '%s' is not valid for CHARACTER SET '%s'" collation charset))
        else
            let isTextColumn column =
                match column.Type with
                | TChar _
                | TVarchar _
                | TTinyText
                | TText
                | TMediumText
                | TLongText
                | TEnum _
                | TSet _ -> true
                | _ -> false

            let columns =
                table.Columns
                |> List.map (fun column ->
                    if isTextColumn column then
                        { column with
                            Charset = Some charset
                            Collation = Some collation }
                    else
                        column)

            let changedColumns =
                columns
                |> List.indexed
                |> List.filter (snd >> isTextColumn)

            let builder = table.RowsArray.ToBuilder()

            table.RowsArray.Indexed
            |> List.ofSeq
            |> traverse (fun (rowId, row) ->
                let updated = Array.copy row

                changedColumns
                |> traverse (fun (index, column) ->
                    recoerce column row.[index]
                    |> Result.map (fun value -> updated.[index] <- value))
                |> Result.map (fun _ -> builder.[rowId] <- updated))
            |> Result.map (fun _ ->
                { table with
                    Columns = columns
                    RowsArray = builder.DrainToImmutable()
                    TableCharset = Some charset
                    TableCollation = Some collation },
                None)
    | SetAutoIncrement value ->
        let nextAfterExisting =
            table.Columns
            |> List.tryFindIndex _.AutoIncrement
            |> Option.map (fun index ->
                table.RowsArray
                |> Seq.choose (fun row ->
                    match row.[index] with
                    | VInt stored when stored >= 0L -> Some stored
                    | VUInt stored when stored <= uint64 Int64.MaxValue -> Some(int64 stored)
                    | _ -> None)
                |> Seq.fold max 0L
                |> fun highest -> if highest = Int64.MaxValue then highest else highest + 1L)
            |> Option.defaultValue 1L

        Ok({ table with NextAutoId = max 1L (max value nextAfterExisting) }, None)
    | SetTableComment comment ->
        validateTableComment table.OriginalName comment
        |> Result.map (fun valid -> { table with TableComment = valid }, None)
    | SetEngine _ -> Ok(table, None)
    | AddCheck _
    | DropCheck _
    | SetCheckEnforced _ -> Ok(table, None)

/// Applies `actions` in order against `tableName`, re-filing it under a new
/// key if any action renamed it (`RENAME TO`/`RENAME [TABLE]`).
let alterTable (store: Store) (dbName: string) (tableName: string) (actions: AlterAction list) : Result<unit, StorageError> =
    withDatabasePublishing
        store
        dbName
        (fun () -> [ SchemaChanged(dbName, AlterTable(tableName, actions)) ])
        (fun db ->
            virtualWriteGuard store dbName tableName
            |> Result.bind (fun () -> tryGetTable db tableName)
            |> Result.bind (fun table ->
                let origKey = normalizeTableName tableName

                let step acc action =
                    acc
                    |> Result.bind (fun (key, tbl) ->
                        let validation =
                            match action with
                            | AddForeignKey foreignKey ->
                                validateForeignKeyDefinition store.ForeignKeyChecks db tbl.OriginalName tbl.Columns tbl.Indexes foreignKey
                            | _ -> Ok()

                        validation
                        |> Result.bind (fun () -> applyAlterAction (temporalCoercionMode store) tbl action)
                        |> Result.map (fun (tbl', newKey) -> (newKey |> Option.defaultValue key), tbl'))

                let validateAutoIncrementKey (_, finalTable: Table) =
                    let indexed column =
                        column.PrimaryKey
                        || column.Unique
                        || (finalTable.Indexes
                            |> List.exists (fun index ->
                                index.Columns
                                |> List.tryHead
                                |> Option.exists (fun name -> System.String.Equals(name, column.Name, System.StringComparison.OrdinalIgnoreCase))))

                    match finalTable.Columns |> List.tryFind (fun column -> column.AutoIncrement && not (indexed column)) with
                    | Some _ ->
                        Error(ExpressionError(1075, "Incorrect table definition; there can be only one auto column and it must be defined as a key"))
                    | None -> Ok()

                actions
                |> List.fold step (Ok(origKey, table))
                |> Result.bind (fun state -> validateAutoIncrementKey state |> Result.map (fun () -> state))
                // Column positions/count may have shifted (`ADD`/`DROP`/
                // `MODIFY COLUMN`), so a full rebuild rather than an
                // incremental patch — ALTER isn't a hot path.
                |> Result.map (fun (finalKey, finalTable) -> Map.remove origKey db |> Map.add finalKey (reindexTable finalTable), ())))

let renameTable (store: Store) (dbName: string) (oldName: string) (newName: string) : Result<unit, StorageError> =
    alterTable store dbName oldName [ RenameTo newName ]

/// `RENAME TABLE a TO b, c TO d` — every pair inside one catalog swap and one
/// WAL event, because MySQL's RENAME TABLE is atomic across its pairs and
/// per-pair `alterTable` calls are not: N events mean a crash can replay half
/// a rename, leaving `a` renamed and `c` untouched.
/// Atomicity is per database. A cross-database rename still emits one
/// event per database, since each database is its own catalog cell — spanning
/// them would need a lock above the per-database one.
let renameTables (store: Store) (dbName: string) (pairs: (string * string) list) : Result<unit, StorageError> =
    withDatabasePublishing
        store
        dbName
        (fun () -> [ SchemaChanged(dbName, RenameTable pairs) ])
        (fun db ->
            let step acc (oldName, newName) =
                acc
                |> Result.bind (fun db ->
                    virtualWriteGuard store dbName oldName
                    |> Result.bind (fun () -> tryGetTable db oldName)
                    |> Result.bind (fun table ->
                        applyAlterAction (temporalCoercionMode store) table (RenameTo newName)
                        |> Result.map (fun (table', newKey) ->
                            let origKey = normalizeTableName oldName
                            let key = newKey |> Option.defaultValue origKey
                            db |> Map.remove origKey |> Map.add key (reindexTable table'))))
            pairs |> List.fold step (Ok db) |> Result.map (fun db' -> db', ()))

/// One column's value for one row being inserted, threaded through
/// `processRow`'s fold: the column's final coerced value, the updated
/// AUTO_INCREMENT counter, and the id assigned to this row's AUTO_INCREMENT
/// column (if any) paired with whether `nextAutoId` generated it or it was
/// supplied explicitly. The omitted-column set lets the executor distinguish
/// a functional default from an explicitly supplied NULL before triggers run.
let private generatesAutoValue
    (mode: TemporalCoercionMode)
    (generateAutoOnZero: bool)
    (column: ColumnDef)
    (value: Value)
    =
    match value with
    | VNull -> true
    | _ when not generateAutoOnZero -> false
    | _ ->
        match Diagnostics.suppress (fun () -> coerceStoredValueWithMode mode column value) with
        | Ok(VInt 0L)
        | Ok(VUInt 0UL) -> true
        | _ -> false

let private processRow
    (mode: TemporalCoercionMode)
    (generateAutoOnZero: bool)
    (nextAutoId: int64)
    (rawRow: Value option list)
    (columns: ColumnDef list)
    : Result<Value list * int64 * (bool * int64) option * Set<int>, StorageError> =
    let nextAfterExplicit current value =
        if value = Int64.MaxValue then Int64.MaxValue else max current (value + 1L)

    let generate valuesRev =
        Ok(VInt nextAutoId :: valuesRev, nextAutoId + 1L, Some(true, nextAutoId))

    let step acc (col: ColumnDef, provided: Value option) =
        match acc with
        | Error e -> Error e
        | Ok(valuesRev, nextAutoId, assignedId) ->
            let missingRequired =
                provided.IsNone
                && col.Default.IsNone
                && col.Generated.IsNone
                && not col.Nullable
                && not col.AutoIncrement

            let pending =
                if missingRequired && not mode.Strict then
                    Diagnostics.warning 1364 (sprintf "Field '%s' doesn't have a default value" col.Name)
                    implicitZeroSeed col
                elif missingRequired then
                    Error(ExpressionError(1364, sprintf "Field '%s' doesn't have a default value" col.Name))
                else
                    Ok(provided |> Option.defaultValue (evalDefault col))

            match pending with
            | Error error -> Error error
            | Ok pending when col.AutoIncrement ->
                if generatesAutoValue mode generateAutoOnZero col pending then
                    generate valuesRev
                else
                    match coerceStoredValueWithMode mode col pending with
                    | Error e -> Error e
                    | Ok(VInt i) -> Ok(VInt i :: valuesRev, nextAfterExplicit nextAutoId i, Some(false, i))
                    | Ok(VUInt value) when value <= uint64 Int64.MaxValue ->
                        let id = int64 value
                        Ok(VUInt value :: valuesRev, nextAfterExplicit nextAutoId id, Some(false, id))
                    | Ok _ -> Error(InvalidValueForColumn(col.Name, "auto_increment"))
            | Ok pending when provided.IsNone && (match col.Default with Some(DExpression _) -> true | _ -> false) ->
                Ok(pending :: valuesRev, nextAutoId, assignedId)
            | Ok pending ->
                coerceAndCheck mode col pending
                |> Result.map (fun value -> value :: valuesRev, nextAutoId, assignedId)

    List.zip columns rawRow
    |> List.fold step (Ok([], nextAutoId, None))
    |> Result.map (fun (valuesRev, nextAutoId, assignedId) ->
        let omitted = rawRow |> List.indexed |> List.choose (fun (index, value) -> if value.IsNone then Some index else None) |> Set.ofList
        List.rev valuesRev, nextAutoId, assignedId, omitted)

/// Resolves `columns` (the explicit column list, or `None` for "all columns
/// in table order") to indices against `table`.
let private resolveInsertColumns (table: Table) (columns: string list option) : Result<int list, StorageError> =
    match columns with
    // A bare `INSERT INTO t VALUES (...)` still accepts a non-DEFAULT
    // value in a generated column's slot and recomputes over it. MySQL accepts
    // only DEFAULT there.
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
      InsertedRows: Value[] list
      IgnoredErrors: StorageError list }

let private insertCore
    (checkFks: bool)
    (mode: TemporalCoercionMode)
    (generateAutoOnZero: bool)
    (ignoreErrors: bool)
    (db: Database)
    (tableKey: string)
    (rowsIn: Value list list)
    (idxs: int list)
    (prepare: Set<int> -> Value[] -> Result<Value[], StorageError>)
    : Result<Database * (int64 * int64 option * int * Value[] list * StorageError list), StorageError> =
    let table = Map.find tableKey db
    let uniqueGroups = uniqueKeyGroups table
    let secondaryGroups = secondaryKeyGroups table

    let reservedAutoNext =
        if not ignoreErrors then
            table.NextAutoId
        else
            match table.Columns |> List.tryFindIndex _.AutoIncrement with
            | None -> table.NextAutoId
            | Some autoIndex ->
                let generatedAttempts =
                    rowsIn
                    |> List.sumBy (fun values ->
                        match idxs |> List.tryFindIndex ((=) autoIndex) with
                        | None -> 1L
                        | Some valueIndex when valueIndex < values.Length ->
                            let value = values.[valueIndex]
                            let column = table.Columns.[autoIndex]
                            if generatesAutoValue mode generateAutoOnZero column value then 1L else 0L
                        | _ -> 0L)

                table.NextAutoId + generatedAttempts

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

    let rows = table.RowsArray.ToBuilder()

    let step acc (rowValues: Value list) =
        acc
        |> Result.bind (fun ((acceptedRev: Value[] list), (ignoredErrorsRev: StorageError list), nextAutoId, firstAuto, lastExplicit, index: Map<string, Map<string, RowId>>, secondaryIndex, secondaryOrder) ->
            if List.length rowValues <> List.length idxs then
                Error(ColumnCountMismatch(List.length idxs, List.length rowValues))
            else
                let provided = List.zip idxs rowValues |> Map.ofList
                let rawRow = table.Columns |> List.mapi (fun i _ -> Map.tryFind i provided)

                let rowResult =
                    processRow mode generateAutoOnZero nextAutoId rawRow table.Columns
                    |> Result.bind (fun (finalValues, nextAutoId', assigned, omitted) ->
                        let candidate = Array.ofList finalValues

                        prepare omitted candidate
                        |> Result.bind (fun candidate ->

                        // O(log n) per unique group via the running index
                        // (seeded from `table.UniqueIndex`, extended below as
                        // each candidate is accepted) instead of a full scan
                        // of `table.RowsArray` per candidate.
                        let uniqueCollision =
                            uniqueGroups
                            |> List.tryPick (fun group ->
                                match encodeUniqueKey table.Columns group candidate with
                                | Some key when Map.find group.Name index |> Map.containsKey key ->
                                    let value =
                                        group.Indices
                                        |> List.map (fun index -> candidate.[index] |> toText |> Option.defaultValue "NULL")
                                        |> String.concat "-"

                                    Some(DuplicateKey(group.Name, value))
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

                                let checkOneForeignKey (foreignKey: ForeignKeyDef) =
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
                                Ok(candidate, nextAutoId', assigned)))

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

                    let rowId = rows.Add candidate
                    let index, secondaryIndex, secondaryOrder =
                        reindexRow table.Columns uniqueGroups secondaryGroups None (Some(rowId, candidate)) index secondaryIndex secondaryOrder

                    Ok(candidate :: acceptedRev, ignoredErrorsRev, nextAutoId', firstAuto', lastExplicit', index, secondaryIndex, secondaryOrder)
                | Error error when ignoreErrors ->
                    Ok(acceptedRev, error :: ignoredErrorsRev, nextAutoId, firstAuto, lastExplicit, index, secondaryIndex, secondaryOrder)
                | Error e -> Error e)

    rowsIn
    |> List.indexed
    |> List.fold
        (fun state (rowNumber, rowValues) ->
            Diagnostics.withRowNumber (rowNumber + 1) (fun () -> step state rowValues))
        (Ok([], [], table.NextAutoId, None, None, table.UniqueIndex, table.SecondaryIndex, table.SecondaryOrder))
    |> Result.map (fun (acceptedRev, ignoredErrorsRev, nextAutoId', firstAuto, lastExplicit, index, secondaryIndex, secondaryOrder) ->
        let accepted = List.rev acceptedRev
        let firstAssigned = Option.orElse lastExplicit firstAuto
        let table' =
            publishRows table
                { table with
                    RowsArray = rows.DrainToImmutable()
                    NextAutoId = max nextAutoId' reservedAutoNext
                    UniqueIndex = index
                    SecondaryIndex = secondaryIndex
                    SecondaryOrder = secondaryOrder }
        Map.add tableKey table' db, (Option.defaultValue 0L firstAssigned, firstAuto, List.length accepted, accepted, List.rev ignoredErrorsRev))

/// Inserts rows built from `columns` and matching value lists, applying
/// defaults, AUTO_INCREMENT assignment, NOT NULL/type-coercion checks, and
/// — new here — unique-key (error 1062) and, when `store.ForeignKeyChecks`
/// is set, foreign-key parent-existence (error 1452) checks. Returns
/// `(lastInsertId, generatedId, affected row count)`; `lastInsertId` is the
/// OK-packet value (see `insertCore`'s doc), `generatedId` is `None` unless
/// this statement actually generated an AUTO_INCREMENT id. Fails the whole
/// statement on the first bad row — see `insertRowsIgnore` for `INSERT
/// IGNORE`'s per-row skip semantics.
let private insertRowsPreparedCore
    (ignoreErrors: bool)
    (prepare: Set<int> -> Value[] -> Result<Value[], StorageError>)
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    : Result<InsertOutcome, StorageError> =
    let key = normalizeTableName tableName

    let publish () =
        withDatabasePublishing
            store
            dbName
            (fun (_, _, _, (rows: Value[] list), _) ->
                if rows.IsEmpty then [] else [ RowsInserted(dbName, tableName, rows) ])
            (fun db ->
                virtualWriteGuard store dbName tableName
                |> Result.bind (fun () -> tryGetTable db tableName)
                |> Result.bind (fun table ->
                    resolveInsertColumns table columns
                    |> Result.bind (fun indices ->
                        insertCore
                            store.ForeignKeyChecks
                            (temporalCoercionMode store)
                            (not store.NoAutoValueOnZero)
                            ignoreErrors
                            db
                            key
                            rowsIn
                            indices
                            prepare)))

    let result =
        match tryInsertLockTargets store dbName tableName columns rowsIn with
        | Some targets when not targets.Keys.IsEmpty -> withInsertLocks store dbName tableName targets.RowIds targets.Keys publish
        | _ -> publish ()

    match result with
    | Ok(lastId, generatedId, affected, rows, ignoredErrors) ->
        Ok {
            LastInsertId = lastId
            GeneratedId = generatedId
            Affected = affected
            InsertedRows = rows
            IgnoredErrors = ignoredErrors
        }
    | Error e -> Error e

let insertRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    : Result<InsertOutcome, StorageError> =
    insertRowsPreparedCore false (fun _ row -> Ok row) store dbName tableName columns rowsIn

let insertRowsPrepared
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    (prepare: Set<int> -> Value[] -> Result<Value[], StorageError>)
    : Result<InsertOutcome, StorageError> =
    insertRowsPreparedCore false prepare store dbName tableName columns rowsIn

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
    insertRowsPreparedCore true (fun _ row -> Ok row) store dbName tableName columns rowsIn

let insertRowsIgnorePrepared
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    (prepare: Set<int> -> Value[] -> Result<Value[], StorageError>)
    : Result<InsertOutcome, StorageError> =
    insertRowsPreparedCore true prepare store dbName tableName columns rowsIn

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
/// cascade path fails 1451, matching MySQL. `SET NULL` blanks them, with a
/// defensive 1048 check for invalid persisted metadata; anything else
/// (`RESTRICT`,
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
                                        let secondaryGroups = secondaryKeyGroups childTbl
                                        let rows = childTbl.RowsArray.ToBuilder()

                                        let rowChanges, index, secondaryIndex, secondaryOrder =
                                            childTbl.RowsArray.Indexed
                                            |> Seq.fold
                                                (fun (changes, index, secondaryIndex, secondaryOrder) (rowId, row) ->
                                                    if isChild row then
                                                        let row' = Array.copy row
                                                        List.iter2 (fun i v -> row'.[i] <- v) childIdxs newKey
                                                        rows.[rowId] <- row'
                                                        let index, secondaryIndex, secondaryOrder = reindexRow childTbl.Columns childGroups secondaryGroups (Some(rowId, row)) (Some(rowId, row')) index secondaryIndex secondaryOrder
                                                        (row, row') :: changes, index, secondaryIndex, secondaryOrder
                                                    else
                                                        changes, index, secondaryIndex, secondaryOrder)
                                                ([], childTbl.UniqueIndex, childTbl.SecondaryIndex, childTbl.SecondaryOrder)

                                        let child = publishRows childTbl { childTbl with RowsArray = rows.DrainToImmutable(); UniqueIndex = index; SecondaryIndex = secondaryIndex; SecondaryOrder = secondaryOrder }
                                        let d' = Map.add childKey child d
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
                                            let secondaryGroups = secondaryKeyGroups childTbl
                                            let rows = childTbl.RowsArray.ToBuilder()

                                            let rowChanges, index, secondaryIndex, secondaryOrder =
                                                childTbl.RowsArray.Indexed
                                                |> Seq.fold
                                                    (fun (changes, index, secondaryIndex, secondaryOrder) (rowId, row) ->
                                                        if isChild row then
                                                            let row' = Array.copy row
                                                            childIdxs |> List.iter (fun i -> row'.[i] <- VNull)
                                                            rows.[rowId] <- row'
                                                            let index, secondaryIndex, secondaryOrder = reindexRow childTbl.Columns childGroups secondaryGroups (Some(rowId, row)) (Some(rowId, row')) index secondaryIndex secondaryOrder
                                                            (row, row') :: changes, index, secondaryIndex, secondaryOrder
                                                        else
                                                            changes, index, secondaryIndex, secondaryOrder)
                                                    ([], childTbl.UniqueIndex, childTbl.SecondaryIndex, childTbl.SecondaryOrder)

                                            let child = publishRows childTbl { childTbl with RowsArray = rows.DrainToImmutable(); UniqueIndex = index; SecondaryIndex = secondaryIndex; SecondaryOrder = secondaryOrder }
                                            let d' = Map.add childKey child d
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
    (prepare: Set<int> -> Value[] -> Result<Value[], StorageError>)
    (applyUpdate: Value[] -> Value[] -> Result<Value[], StorageError>)
    (foundRows: bool)
    : Result<InsertOutcome, StorageError> =
    upsertRowsWithOrdinal
        store
        dbName
        tableName
        columns
        rowsIn
        prepare
        (fun _ existing candidate -> applyUpdate existing candidate)
        foundRows

and upsertRowsWithOrdinal
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    (prepare: Set<int> -> Value[] -> Result<Value[], StorageError>)
    (applyUpdate: int -> Value[] -> Value[] -> Result<Value[], StorageError>)
    (foundRows: bool)
    : Result<InsertOutcome, StorageError> =
        let key = normalizeTableName tableName

        let eventsOf
            ((_, _, _, (inserted: Value[] list), (updated: (Value[] * Value[]) list)),
             (cascaded: Map<string, (Value[] * Value[]) list>),
             (db: Database))
            =
            let originalNameOf tableKey =
                db
                |> Map.tryFind tableKey
                |> Option.map (fun table -> table.OriginalName)
                |> Option.defaultValue tableKey

            [ if not inserted.IsEmpty then
                  RowsInserted(dbName, tableName, inserted)

              if not updated.IsEmpty then
                  RowsUpdated(dbName, tableName, updated)

              for KeyValue(tableKey, changes) in cascaded do
                  if not changes.IsEmpty then
                      RowsUpdated(dbName, originalNameOf tableKey, changes) ]

        let publish () =
            withDatabasePublishing store dbName eventsOf (fun db ->
                virtualWriteGuard store dbName tableName
                |> Result.bind (fun () -> tryGetTable db tableName)
                |> Result.bind (fun table -> upsertRowsInTable store db key table columns rowsIn prepare applyUpdate foundRows)
                |> Result.map (fun (db', cascaded, summary) -> db', (summary, cascaded, db)))

        let result =
            match tryInsertLockTargets store dbName tableName columns rowsIn with
            | Some targets when not targets.Keys.IsEmpty -> withInsertLocks store dbName tableName targets.RowIds targets.Keys publish
            | _ -> publish ()

        match result with
        | Ok((lastId, generatedId, affected, inserted, updated), cascaded, db) ->
            Ok {
                LastInsertId = lastId
                GeneratedId = generatedId
                Affected = affected
                InsertedRows = inserted
                IgnoredErrors = []
            }
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
    (prepare: Set<int> -> Value[] -> Result<Value[], StorageError>)
    (applyUpdate: int -> Value[] -> Value[] -> Result<Value[], StorageError>)
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
                    let secondaryGroups = secondaryKeyGroups table

                    let rows = table.RowsArray.ToBuilder()

                    // The running index (seeded from `table.UniqueIndex`,
                    // rekeyed after every matched/inserted candidate) finds
                    // the one row (if any) sharing a key with `candidate` in
                    let findMatch (index: Map<string, Map<string, RowId>>) (candidate: Value[]) : (RowId * Value[]) option =
                        uniqueGroups
                        |> List.tryPick (fun group ->
                            encodeUniqueKey table.Columns group candidate
                            |> Option.bind (fun key -> Map.tryFind key (Map.find group.Name index))
                            |> Option.map (fun rowId -> rowId, rows.[rowId]))

                    let step acc (ordinal, rowValues: Value list) =
                        acc
                        |> Result.bind
                            (fun (nextAutoId,
                                  firstAuto,
                                  lastExplicit,
                                  affected,
                                  inserted: Value[] list,
                                  updated: (Value[] * Value[]) list,
                                  index: Map<string, Map<string, RowId>>,
                                  secondaryIndex: Map<string, Map<string, Set<RowId>>>,
                                  secondaryOrder: SecondaryOrder,
                                  cascadeDb: Database,
                                  visited: Map<string, Value[] list>,
                                  cascaded: Map<string, (Value[] * Value[]) list>) ->
                                if List.length rowValues <> List.length idxs then
                                    Error(ColumnCountMismatch(List.length idxs, List.length rowValues))
                                else
                                    let provided = List.zip idxs rowValues |> Map.ofList
                                    let rawRow = table.Columns |> List.mapi (fun i _ -> Map.tryFind i provided)

                                    processRow
                                        (temporalCoercionMode store)
                                        (not store.NoAutoValueOnZero)
                                        nextAutoId
                                        rawRow
                                        table.Columns
                                    |> Result.bind (fun (finalValues, nextAutoId', assigned, omitted) ->
                                        // A unique index over a *generated* column (e.g.
                                        // Laravel Pulse's `key_hash BINARY(16) AS
                                        // (unhex(md5(key)))`) is still NULL in the raw
                                        // candidate at this point — `computeGenerated`
                                        // fills it in before `findMatch` runs, so ON
                                        // DUPLICATE KEY UPDATE actually finds the
                                        // collision instead of degrading into a plain
                                        // INSERT that then trips the unique check.
                                        prepare omitted (Array.ofList finalValues)
                                        |> Result.map (fun candidate -> candidate, findMatch index candidate)
                                        |> Result.bind (function
                                            | candidate, Some(pos, existing) ->
                                                applyUpdate ordinal existing candidate
                                                |> Result.bind (coerceRow (temporalCoercionMode store) table.Columns)
                                                |> Result.bind (fun applied ->
                                                    let collision =
                                                        uniqueGroups
                                                        |> List.tryPick (fun group ->
                                                            match encodeUniqueKey table.Columns group applied with
                                                            | Some key ->
                                                                match Map.tryFind key (Map.find group.Name index) with
                                                                | Some otherPos when otherPos <> pos ->
                                                                    let value =
                                                                        group.Indices
                                                                        |> List.map (fun i -> applied.[i] |> toText |> Option.defaultValue "NULL")
                                                                        |> String.concat "-"

                                                                    Some(DuplicateKey(group.Name, value))
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
                                                        rows.[pos] <- applied

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
                                                        let index, secondaryIndex, secondaryOrder = reindexRow table.Columns uniqueGroups secondaryGroups (Some(pos, existing)) (Some(pos, applied)) index secondaryIndex secondaryOrder

                                                        nextAutoId',
                                                        firstAuto,
                                                        lastExplicit,
                                                        affected + weight,
                                                        inserted,
                                                        (if changed then (existing, applied) :: updated else updated),
                                                        index,
                                                        secondaryIndex,
                                                        secondaryOrder,
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

                                                    let rowId = rows.Add candidate

                                                    let index, secondaryIndex, secondaryOrder = reindexRow table.Columns uniqueGroups secondaryGroups None (Some(rowId, candidate)) index secondaryIndex secondaryOrder

                                                    nextAutoId',
                                                    firstAuto',
                                                    lastExplicit',
                                                    affected + 1,
                                                    candidate :: inserted,
                                                    updated,
                                                    index,
                                                    secondaryIndex,
                                                    secondaryOrder,
                                                    cascadeDb,
                                                    visited,
                                                    cascaded))))

                    rowsIn
                    |> List.indexed
                    |> foldWithCancellation step (Ok(table.NextAutoId, None, None, 0, [], [], table.UniqueIndex, table.SecondaryIndex, table.SecondaryOrder, db, Map.empty, Map.empty))
                    |> Result.map (fun (nextAutoId', firstAuto, lastExplicit, affected, inserted, updated, index, secondaryIndex, secondaryOrder, cascadeDb, _visited, cascaded) ->
                        let finalRows = rows.DrainToImmutable()

                        let updatedTable = publishRows table { table with RowsArray = finalRows; NextAutoId = nextAutoId'; UniqueIndex = index; SecondaryIndex = secondaryIndex; SecondaryOrder = secondaryOrder }
                        Map.add key updatedTable cascadeDb,
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

    let removeFrom (d: Database) =
        let t = Map.find tableKey d
        let rows = t.RowsArray.ToBuilder()
        let uniqueGroups = uniqueKeyGroups t
        let secondaryGroups = secondaryKeyGroups t

        let index, secondaryIndex, secondaryOrder, _ =
            t.RowsArray.Indexed
            |> Seq.fold
                (fun (index, secondaryIndex, secondaryOrder, pending) (rowId, row) ->
                    match pending |> List.tryFindIndex ((=) row) with
                    | Some pendingIndex ->
                        rows.Remove rowId |> ignore
                        let index, secondaryIndex, secondaryOrder = reindexRow t.Columns uniqueGroups secondaryGroups (Some(rowId, row)) None index secondaryIndex secondaryOrder
                        index, secondaryIndex, secondaryOrder, List.removeAt pendingIndex pending
                    | None -> index, secondaryIndex, secondaryOrder, pending)
                (t.UniqueIndex, t.SecondaryIndex, t.SecondaryOrder, toDelete)

        let updated = publishRows t { t with RowsArray = rows.DrainToImmutable(); UniqueIndex = index; SecondaryIndex = secondaryIndex; SecondaryOrder = secondaryOrder }
        Map.add tableKey updated d

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
                                match childIdxs |> List.tryFind (fun i -> not childTbl.Columns.[i].Nullable) with
                                | Some i -> Error(NotNullViolation childTbl.Columns.[i].Name)
                                | None ->
                                    let childGroups = uniqueKeyGroups childTbl
                                    let secondaryGroups = secondaryKeyGroups childTbl

                                    // Blanking retains row identities, so unique indexes can
                                    // be rekeyed without a full rebuild.
                                    // `changes` pairs each blanked row's before/after values —
                                    // the WAL needs the exact same `RowsUpdated` shape a plain
                                    // `UPDATE` reports, or replay resurrects the pre-blank FK
                                    // value.
                                    let rows = childTbl.RowsArray.ToBuilder()

                                    let changes, index, secondaryIndex, secondaryOrder =
                                        childTbl.RowsArray.Indexed
                                        |> Seq.fold
                                            (fun (changes, index, secondaryIndex, secondaryOrder) (rowId, row) ->
                                                if isChild row then
                                                    let row' = Array.copy row
                                                    childIdxs |> List.iter (fun i -> row'.[i] <- VNull)
                                                    rows.[rowId] <- row'
                                                    let index, secondaryIndex, secondaryOrder = reindexRow childTbl.Columns childGroups secondaryGroups (Some(rowId, row)) (Some(rowId, row')) index secondaryIndex secondaryOrder
                                                    (row, row') :: changes, index, secondaryIndex, secondaryOrder
                                                else
                                                    changes, index, secondaryIndex, secondaryOrder)
                                            ([], childTbl.UniqueIndex, childTbl.SecondaryIndex, childTbl.SecondaryOrder)

                                    let blanked =
                                        blanked
                                        |> Map.add childKey ((blanked |> Map.tryFind childKey |> Option.defaultValue []) @ List.rev changes)

                                    let child = publishRows childTbl { childTbl with RowsArray = rows.DrainToImmutable(); UniqueIndex = index; SecondaryIndex = secondaryIndex; SecondaryOrder = secondaryOrder }
                                    Ok(Map.add childKey child d, visited, blanked)
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
    (prepare: Set<int> -> Value[] -> Result<Value[], StorageError>)
    : Result<InsertOutcome, StorageError> =
    let key = normalizeTableName tableName

    let result =
        withDatabasePublishing store dbName snd (fun initialDb ->
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

                                processRow
                                    (temporalCoercionMode store)
                                    (not store.NoAutoValueOnZero)
                                    nextAutoId
                                    rawRow
                                    table.Columns
                                |> Result.bind (fun (finalValues, nextAutoId', assigned, omitted) ->
                                    prepare omitted (Array.ofList finalValues)
                                    |> Result.bind (fun candidate ->
                                        let uniqueGroups = uniqueKeyGroups table

                                        let conflicts =
                                            uniqueGroups
                                            |> List.choose (fun group ->
                                                encodeUniqueKey table.Columns group candidate
                                                |> Option.bind (fun encoded ->
                                                    Map.tryFind encoded (Map.find group.Name table.UniqueIndex)
                                                    |> Option.map (fun rowId -> rowId, table.RowsArray.[rowId])))
                                            |> List.fold
                                                (fun (seen, matches) ((rowId, _) as matched) ->
                                                    if Set.contains rowId seen then
                                                        seen, matches
                                                    else
                                                        Set.add rowId seen, matched :: matches)
                                                (Set.empty, [])
                                            |> snd
                                            |> List.rev

                                        let optimizedConflict =
                                            match conflicts with
                                            | [ rowId, existing ] when (referencingForeignKeys db key).IsEmpty -> Some(rowId, existing)
                                            | _ -> None

                                        let deletedMatches =
                                            match optimizedConflict with
                                            | Some _ -> []
                                            | None -> conflicts

                                        let deletedConflicts = deletedMatches |> List.map snd

                                        cascadeDelete store.ForeignKeyChecks db key deletedConflicts
                                        |> Result.bind (fun (deletedDb, removed, blanked) ->
                                            let target = Map.find key deletedDb
                                            let target', writeEvent, weight =
                                                match optimizedConflict with
                                                | Some(rowId, existing) ->
                                                    let changed = existing <> candidate
                                                    let uniqueIndex, secondaryIndex, secondaryOrder =
                                                        reindexRow
                                                            target.Columns
                                                            (uniqueKeyGroups target)
                                                            (secondaryKeyGroups target)
                                                            (Some(rowId, existing))
                                                            (Some(rowId, candidate))
                                                            target.UniqueIndex
                                                            target.SecondaryIndex
                                                            target.SecondaryOrder

                                                    publishRows target
                                                        { target with
                                                            RowsArray = target.RowsArray.SetItem(rowId, candidate)
                                                            NextAutoId = nextAutoId'
                                                            UniqueIndex = uniqueIndex
                                                            SecondaryIndex = secondaryIndex
                                                            SecondaryOrder = secondaryOrder },
                                                    (if changed then Some(RowsUpdated(dbName, tableName, [ existing, candidate ])) else None),
                                                    deletedConflicts.Length + 1 + (if changed then 1 else 0)
                                                | None ->
                                                    let rowId, rows = target.RowsArray.Append candidate
                                                    let uniqueIndex, secondaryIndex, secondaryOrder =
                                                        reindexRow
                                                            target.Columns
                                                            (uniqueKeyGroups target)
                                                            (secondaryKeyGroups target)
                                                            None
                                                            (Some(rowId, candidate))
                                                            target.UniqueIndex
                                                            target.SecondaryIndex
                                                            target.SecondaryOrder

                                                    publishRows target
                                                        { target with
                                                            RowsArray = rows
                                                            NextAutoId = nextAutoId'
                                                            UniqueIndex = uniqueIndex
                                                            SecondaryIndex = secondaryIndex
                                                            SecondaryOrder = secondaryOrder },
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
                              InsertedRows = List.rev inserted
                              IgnoredErrors = [] }

                        db, (outcome, events)))))

    match result with
    | Ok(outcome, _) -> Ok outcome
    | Error error -> Error error

/// Deletes every candidate matching `predicate`. Returns the number of rows
/// removed. `predicate` returns a `Result` rather than a plain `bool` so a
/// per-row WHERE-evaluation failure (not reachable today — every `Value`
/// operation is total — but a real possibility once functions that can
/// fail per row land) surfaces as an `Error` instead of silently being
/// treated as "didn't match". When `store.ForeignKeyChecks` is set (the
/// default), applies every referencing foreign key's `ON DELETE` action —
/// see `cascadeDelete`. `None` candidates scan the table; supplied row
/// identities are resolved from the current table root and rechecked by
/// `predicate` before removal.
let private deleteRowsCore
    (store: Store)
    (dbName: string)
    (tableName: string)
    (candidates: (RowId * Value[]) list option)
    (predicate: Value[] -> Result<bool, StorageError>)
    : Result<int, StorageError> =
    let eventsOf
        (_,
         (db: Database),
         (removed: Map<string, Value[] list>),
         (blanked: Map<string, (Value[] * Value[]) list>))
        =
        let originalNameOf tableKey =
            db
            |> Map.tryFind tableKey
            |> Option.map (fun table -> table.OriginalName)
            |> Option.defaultValue tableKey

        [ for KeyValue(tableKey, rows) in removed do
              if not rows.IsEmpty then
                  RowsDeleted(dbName, originalNameOf tableKey, rows)

          for KeyValue(tableKey, changes) in blanked do
              if not changes.IsEmpty then
                  RowsUpdated(dbName, originalNameOf tableKey, changes) ]

    let apply =
        withDatabasePublishing store dbName eventsOf (fun db ->
            let key = normalizeTableName tableName

            virtualWriteGuard store dbName tableName
            |> Result.bind (fun () -> tryGetTable db tableName)
            |> Result.bind (fun table ->
                let rows =
                    candidates
                    |> Option.map (fun candidates ->
                        candidates
                        |> List.map fst
                        |> List.distinct
                        |> List.choose (fun rowId -> table.RowsArray.TryFind rowId))
                    |> Option.defaultWith (fun () -> table.RowsArray |> List.ofSeq)

                rows
                |> traverse (fun row -> predicate row |> Result.map (fun keep -> keep, row))
                |> Result.bind (fun flagged ->
                    let toDelete = flagged |> List.filter fst |> List.map snd

                    cascadeDelete store.ForeignKeyChecks db key toDelete
                    |> Result.map (fun (db', removed, blanked) -> db', (toDelete.Length, db, removed, blanked)))))

    let result =
        match candidates with
        | None -> apply
        | Some candidates ->
            candidates
            |> List.map fst
            |> fun rowIds -> withRowLocks store dbName tableName rowIds (fun () -> apply)

    match result with
    | Ok(affected, _, _, _) -> Ok affected
    | Error e -> Error e

let deleteRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (predicate: Value[] -> Result<bool, StorageError>)
    : Result<int, StorageError> =
    deleteRowsCore store dbName tableName None predicate

let deleteRowsCandidates
    (store: Store)
    (dbName: string)
    (tableName: string)
    (candidates: (RowId * Value[]) list)
    (predicate: Value[] -> Result<bool, StorageError>)
    : Result<int, StorageError> =
    deleteRowsCore store dbName tableName (Some candidates) predicate

let private canMergePointUpdate tableKey rowIds (baseDb: Database) (batchDb: Database) (liveDb: Database) =
    let unchangedOtherTables =
        Set.union (keysOf baseDb) (keysOf batchDb)
        |> Set.forall (fun key ->
            key = tableKey
            || match Map.tryFind key baseDb, Map.tryFind key batchDb with
               | Some baseTable, Some batchTable -> obj.ReferenceEquals(baseTable, batchTable)
               | None, None -> true
               | _ -> false)

    match Map.tryFind tableKey baseDb, Map.tryFind tableKey batchDb with
    | Some baseTable, Some batchTable when sameTableSchema baseTable batchTable && unchangedOtherTables ->
        let protectedColumns =
            [ for foreignKey in baseTable.ForeignKeys do
                  yield! foreignKey.Columns

              for _, foreignKey in referencingForeignKeys liveDb tableKey do
                  yield! foreignKey.RefColumns ]
            |> List.map (resolveColumn baseTable.Columns)

        match protectedColumns |> traverse id with
        | Error _ -> false
        | Ok indices ->
            rowIds
            |> List.forall (fun rowId ->
                match baseTable.RowsArray.TryFind rowId, batchTable.RowsArray.TryFind rowId with
                | Some before, Some after -> indices |> List.forall (fun index -> before.[index] = after.[index])
                | _ -> false)
    | _ -> false

let private mergePointUpdate dbName tableKey rowIds (baseDb: Database) (batchDb: Database) (liveDb: Database) =
    let conflict () = raise (LockWaitTimeout dbName)

    match Map.tryFind tableKey baseDb, Map.tryFind tableKey batchDb, Map.tryFind tableKey liveDb with
    | Some baseTable, Some batchTable, Some liveTable when sameTableSchema liveTable baseTable ->
        let rows = liveTable.RowsArray.ToBuilder()
        let uniqueGroups = uniqueKeyGroups liveTable
        let secondaryGroups = secondaryKeyGroups liveTable
        let mutable index = liveTable.UniqueIndex
        let mutable secondaryIndex = liveTable.SecondaryIndex
        let mutable secondaryOrder = liveTable.SecondaryOrder

        for rowId in rowIds do
            match baseTable.RowsArray.TryFind rowId, batchTable.RowsArray.TryFind rowId, rows.TryFind rowId with
            | Some before, Some after, Some live when live = before ->
                let collision =
                    uniqueGroups
                    |> List.exists (fun group ->
                        match encodeUniqueKey liveTable.Columns group after with
                        | Some key -> Map.tryFind key index.[group.Name] |> Option.exists ((<>) rowId)
                        | None -> false)

                if collision then
                    conflict ()

                rows.[rowId] <- after
                let uniqueIndex, updatedSecondaryIndex, updatedSecondaryOrder = reindexRow liveTable.Columns uniqueGroups secondaryGroups (Some(rowId, before)) (Some(rowId, after)) index secondaryIndex secondaryOrder
                index <- uniqueIndex
                secondaryIndex <- updatedSecondaryIndex
                secondaryOrder <- updatedSecondaryOrder
            | _ -> conflict ()

        let mergedTable =
            publishRows liveTable
                { liveTable with
                    RowsArray = rows.DrainToImmutable()
                    NextAutoId = max liveTable.NextAutoId batchTable.NextAutoId
                    UniqueIndex = index
                    SecondaryIndex = secondaryIndex
                    SecondaryOrder = secondaryOrder }

        Map.add tableKey mergedTable liveDb
    | _ -> conflict ()

let private tryPointUpdate (baseDb: Database) (batchDb: Database) (liveDb: Database) =
    let changedTables =
        Set.union (keysOf baseDb) (keysOf batchDb)
        |> Seq.choose (fun tableKey ->
            match Map.tryFind tableKey baseDb, Map.tryFind tableKey batchDb with
            | Some baseTable, Some batchTable when obj.ReferenceEquals(baseTable, batchTable) -> None
            | Some baseTable, Some batchTable when sameTableSchema baseTable batchTable ->
                let changes = batchTable.RowsArray.ChangesFrom baseTable.RowsArray |> List.ofSeq

                if changes |> List.forall (function _, Some _, Some _ -> true | _ -> false) then
                    Some(tableKey, changes |> List.map (fun (rowId, _, _) -> rowId))
                else
                    Some(tableKey, [])
            | _ -> Some(tableKey, []))
        |> List.ofSeq

    match changedTables with
    | [ tableKey, rowIds ] when not rowIds.IsEmpty && canMergePointUpdate tableKey rowIds baseDb batchDb liveDb -> Some(tableKey, rowIds)
    | _ -> None

let private mergeDatabaseSlotPublishing
    (timeout: TimeSpan)
    (dbName: string)
    (slot: Database ref)
    (baseDb: Database)
    (batchDb: Database)
    (prepare: unit -> (unit -> unit))
    : unit -> unit =
    if not (Monitor.TryEnter(slot, timeout)) then
        raise (LockWaitTimeout dbName)

    try
        let liveDb = slot.Value

        if obj.ReferenceEquals(liveDb, baseDb) then
            slot.Value <- batchDb
        else
            match tryPointUpdate baseDb batchDb liveDb with
            | Some(tableKey, rowIds) -> slot.Value <- mergePointUpdate dbName tableKey rowIds baseDb batchDb liveDb
            | None ->
                let tableKeys = Set.union (keysOf baseDb) (keysOf batchDb)

                let merged =
                    tableKeys
                    |> Set.fold
                        (fun acc tableName ->
                            match Map.tryFind tableName baseDb, Map.tryFind tableName batchDb, Map.tryFind tableName liveDb with
                            | Some baseTable, Some batchTable, _ when obj.ReferenceEquals(baseTable, batchTable) -> acc
                            | Some baseTable, None, Some liveTable when obj.ReferenceEquals(liveTable, baseTable) -> Map.remove tableName acc
                            | None, Some batchTable, None -> Map.add tableName batchTable acc
                            | Some baseTable, Some batchTable, Some liveTable when obj.ReferenceEquals(liveTable, baseTable) -> Map.add tableName batchTable acc
                            | Some baseTable, Some batchTable, Some liveTable when sameTableSchema baseTable batchTable ->
                                Map.add tableName (mergeRows dbName baseTable batchTable liveTable) acc
                            | _ -> raise (LockWaitTimeout dbName))
                        liveDb

                validateMergedForeignKeys dbName merged
                slot.Value <- merged

        prepare ()
    finally
        Monitor.Exit slot

let private mergeDatabaseSlot (timeout: TimeSpan) (dbName: string) (slot: Database ref) (baseDb: Database) (batchDb: Database) : unit =
    mergeDatabaseSlotPublishing timeout dbName slot baseDb batchDb (fun () -> ignore) |> fun acknowledge -> acknowledge ()

/// Merges a private statement or transaction snapshot into the live catalog.
/// Rows changed from the same base row conflict; disjoint row changes combine
/// under the database slot lock without a transaction-wide gate.
let mergeCatalogIntoWithTimeout (timeout: TimeSpan) (store: Store) (baseCatalog: Catalog) (batchCatalog: Catalog) : unit =
    let dbKeys = Set.union (keysOf baseCatalog) (keysOf batchCatalog)

    for dbName in dbKeys do
        match Map.tryFind dbName baseCatalog, Map.tryFind dbName batchCatalog with
        | Some _, None ->
            match store.Databases.TryGetValue dbName with
            | true, slot when obj.ReferenceEquals(slot.Value, Map.find dbName baseCatalog) -> store.Databases.TryRemove dbName |> ignore
            | _ -> raise (LockWaitTimeout dbName)
        | None, Some batchDb ->
            if not (store.Databases.TryAdd(dbName, ref batchDb)) then
                raise (LockWaitTimeout dbName)
        | None, None -> ()
        | Some baseDb, Some batchDb when obj.ReferenceEquals(baseDb, batchDb) -> ()
        | Some baseDb, Some batchDb ->
            let slot = store.Databases.GetOrAdd(dbName, (fun _ -> ref Map.empty))
            mergeDatabaseSlot timeout dbName slot baseDb batchDb

let mergeCatalogInto (store: Store) (baseCatalog: Catalog) (batchCatalog: Catalog) : unit =
    mergeCatalogIntoWithTimeout (Fsdb.Limits.lockWaitTimeout ()) store baseCatalog batchCatalog

let private commitCatalogIntoWith
    (timeout: TimeSpan)
    (validateWholeSnapshot: bool)
    (store: Store)
    (baseCatalog: Catalog)
    (snapshot: Store)
    : unit =
    match store.PendingEvents with
    | Some _ ->
        mergeCatalogIntoWithTimeout timeout store baseCatalog snapshot.Catalog
        prepareTransactionEvents store snapshot |> fun acknowledge -> acknowledge ()
    | None ->
        let batchCatalog = snapshot.Catalog

        let changedKeys =
            Set.union (keysOf baseCatalog) (keysOf batchCatalog)
            |> Set.filter (fun dbName ->
                match Map.tryFind dbName baseCatalog, Map.tryFind dbName batchCatalog with
                | Some baseDb, Some batchDb -> not (obj.ReferenceEquals(baseDb, batchDb))
                | None, None -> false
                | _ -> true)

        if not changedKeys.IsEmpty then
            let needsCatalogLock =
                validateWholeSnapshot
                || changedKeys
                   |> Seq.exists (fun dbName ->
                       Map.containsKey dbName baseCatalog <> Map.containsKey dbName batchCatalog)

            let publish () =
                let existingKeys = if validateWholeSnapshot then keysOf baseCatalog else changedKeys

                let newKeys =
                    changedKeys
                    |> Set.filter (fun dbName ->
                        Map.containsKey dbName baseCatalog |> not
                        && Map.containsKey dbName batchCatalog)

                let lockedKeys = Set.union existingKeys newKeys

                let databases =
                    lockedKeys
                    |> Seq.map (fun dbName ->
                        match store.Databases.TryGetValue dbName with
                        | true, slot -> dbName, slot
                        | false, _ when Set.contains dbName newKeys -> dbName, ref Map.empty
                        | false, _ -> raise (LockWaitTimeout dbName))
                    |> Seq.toList

                let rec withSlots remaining publish =
                    match remaining with
                    | [] -> publish ()
                    | (dbName, slot) :: tail ->
                        if not (Monitor.TryEnter(slot, timeout)) then
                            raise (LockWaitTimeout dbName)

                        try
                            withSlots tail publish
                        finally
                            Monitor.Exit slot

                withSlots databases (fun () ->
                    for dbName, slot in databases do
                        if not (Set.contains dbName newKeys) then
                            match store.Databases.TryGetValue dbName with
                            | true, current when obj.ReferenceEquals(current, slot) -> ()
                            | _ -> raise (LockWaitTimeout dbName)

                    if validateWholeSnapshot then
                        for dbName, slot in databases do
                            match Map.tryFind dbName baseCatalog with
                            | Some baseDb when not (obj.ReferenceEquals(slot.Value, baseDb)) -> raise (LockWaitTimeout dbName)
                            | _ -> ()

                    for dbName in changedKeys do
                        match Map.tryFind dbName baseCatalog, Map.tryFind dbName batchCatalog with
                        | Some baseDb, None ->
                            match store.Databases.TryGetValue dbName with
                            | true, slot when obj.ReferenceEquals(slot.Value, baseDb) ->
                                store.Databases.TryRemove dbName |> ignore
                            | _ -> raise (LockWaitTimeout dbName)
                        | None, Some batchDb ->
                            let _, slot = databases |> List.find (fun (name, _) -> name = dbName)
                            slot.Value <- batchDb

                            if not (store.Databases.TryAdd(dbName, slot)) then
                                raise (LockWaitTimeout dbName)
                        | Some _, Some batchDb when validateWholeSnapshot ->
                            let _, slot = databases |> List.find (fun (name, _) -> name = dbName)
                            slot.Value <- batchDb
                        | Some baseDb, Some batchDb ->
                            let _, slot = databases |> List.find (fun (name, _) -> name = dbName)
                            mergeDatabaseSlot timeout dbName slot baseDb batchDb
                        | None, None -> ()

                    prepareTransactionEvents store snapshot)

            let acknowledge =
                if needsCatalogLock then lock store.Lock publish else publish ()

            acknowledge ()

let commitCatalogIntoWithTimeout (timeout: TimeSpan) (store: Store) (baseCatalog: Catalog) (snapshot: Store) : unit =
    commitCatalogIntoWith timeout false store baseCatalog snapshot

let commitCatalogInto (store: Store) (baseCatalog: Catalog) (snapshot: Store) : unit =
    commitCatalogIntoWith (Fsdb.Limits.lockWaitTimeout ()) false store baseCatalog snapshot

let commitSerializableCatalogIntoWithTimeout (timeout: TimeSpan) (store: Store) (baseCatalog: Catalog) (snapshot: Store) : unit =
    commitCatalogIntoWith timeout true store baseCatalog snapshot

let commitSerializableCatalogInto (store: Store) (baseCatalog: Catalog) (snapshot: Store) : unit =
    commitSerializableCatalogIntoWithTimeout (Fsdb.Limits.lockWaitTimeout ()) store baseCatalog snapshot

let private withPointUpdateDatabase
    (store: Store)
    (dbName: string)
    (tableName: string)
    (rowIds: RowId list)
    (eventsOf: 'a -> CommitEvent list)
    (operation: Database -> Result<Database * 'a, StorageError>)
    : Result<'a, StorageError> =
    match store.Databases.TryGetValue dbName with
    | false, _ -> Error(NoSuchDatabase dbName)
    | true, slot ->
        let rowIds = List.distinct rowIds
        let tableKey = normalizeTableName tableName
        let baseDb = slot.Value

        operation baseDb
        |> Result.bind (fun (batchDb, result) ->
            let published =
                lock slot (fun () ->
                    let attached =
                        match store.Databases.TryGetValue dbName with
                        | true, current -> obj.ReferenceEquals(current, slot)
                        | false, _ -> false

                    let liveDb = slot.Value

                    if not attached then
                        Error(NoSuchDatabase dbName)
                    elif obj.ReferenceEquals(liveDb, baseDb) then
                        slot.Value <- batchDb
                        Ok(prepareResultEvents store eventsOf result)
                    elif not rowIds.IsEmpty && canMergePointUpdate tableKey rowIds baseDb batchDb liveDb then
                        slot.Value <- mergePointUpdate dbName tableKey rowIds baseDb batchDb liveDb
                        Ok(prepareResultEvents store eventsOf result)
                    else
                        Ok(
                            mergeDatabaseSlotPublishing
                                (Fsdb.Limits.lockWaitTimeout ())
                                dbName
                                slot
                                baseDb
                                batchDb
                                (fun () -> prepareResultEvents store eventsOf result)
                        ))

            published
            |> Result.map (fun acknowledge ->
                acknowledge ()
                result))

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
/// `candidates`, when given, is the exact `(RowId, row)` set to visit —
/// `Executor`'s point-lookup narrowing (`tryPointLookup`) already resolved
/// these via the PK/UNIQUE index, so this fold doesn't re-scan `table.RowsArray`
/// at all to find them; `predicate` still re-checks each one for
/// correctness (this is a pure narrowing to a superset of the real WHERE
/// match, same discipline `tryPointLookup` documents), so it never pays for
/// rows that weren't candidates. `None` (a WHERE that didn't narrow, or
/// none at all) falls back to visiting every row of `table.RowsArray`,
/// `predicate` deciding which ones qualify. The private builder copies only
/// pages containing changed rows.
let updateRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (candidates: (RowId * Value[]) list option)
    (predicate: Value[] -> Result<bool, StorageError>)
    (updater: Value[] -> Result<Value[], StorageError>)
    : Result<int, StorageError> =
    let eventsOf
        ((changes: (Value[] * Value[]) list),
         (cascaded: Map<string, (Value[] * Value[]) list>),
         (db: Database))
        =
        let originalNameOf tableKey =
            db
            |> Map.tryFind tableKey
            |> Option.map (fun table -> table.OriginalName)
            |> Option.defaultValue tableKey

        [ if not changes.IsEmpty then
              RowsUpdated(dbName, tableName, changes)

          for KeyValue(tableKey, updates) in cascaded do
              if not updates.IsEmpty then
                  RowsUpdated(dbName, originalNameOf tableKey, updates) ]

    let apply candidateRows db =
        let key = normalizeTableName tableName

        virtualWriteGuard store dbName tableName
        |> Result.bind (fun () -> tryGetTable db tableName)
        |> Result.bind (fun table ->
            let uniqueGroups = uniqueKeyGroups table
            let secondaryGroups = secondaryKeyGroups table
            let checkFks = store.ForeignKeyChecks
            // Failed statements discard the private builder, keeping
            // partial page rewrites outside the published catalog.
            let builder = table.RowsArray.ToBuilder()

            let step acc (rowId, row) =
                acc
                |> Result.bind
                    (fun (changesRev: (Value[] * Value[]) list,
                          index: Map<string, Map<string, RowId>>,
                          secondaryIndex: Map<string, Map<string, Set<RowId>>>,
                          secondaryOrder: SecondaryOrder,
                          cascadeDb: Database,
                          visited: Map<string, Value[] list>,
                          cascaded: Map<string, (Value[] * Value[]) list>) ->
                            let rowId =
                                match builder.TryFind rowId with
                                | Some current when obj.ReferenceEquals(current, row) -> Some rowId
                                | _ -> None

                            match rowId with
                            | None -> Ok(changesRev, index, secondaryIndex, secondaryOrder, cascadeDb, visited, cascaded)
                            | Some rowId ->
                                predicate row
                                |> Result.bind (fun keep ->
                                    if not keep then
                                        Ok(changesRev, index, secondaryIndex, secondaryOrder, cascadeDb, visited, cascaded)
                                    else
                                        updater row
                                        |> Result.bind (coerceRow (temporalCoercionMode store) table.Columns)
                                        |> Result.bind (fun newRow ->
                                            // A group's key only collides against
                                            // some *other* row still holding it —
                                            // `row`'s own identity (about to be
                                            // rekeyed below) doesn't count.
                                            let collision =
                                                uniqueGroups
                                                |> List.tryPick (fun group ->
                                                    match encodeUniqueKey table.Columns group newRow with
                                                    | Some k ->
                                                        match Map.tryFind k (Map.find group.Name index) with
                                                        | Some otherRowId when otherRowId <> rowId ->
                                                            let value = group.Indices |> List.map (fun i -> newRow.[i] |> toText |> Option.defaultValue "NULL") |> String.concat "-"
                                                            Some(DuplicateKey(group.Name, value))
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
                                                    let index, secondaryIndex, secondaryOrder = reindexRow table.Columns uniqueGroups secondaryGroups (Some(rowId, row)) (Some(rowId, newRow)) index secondaryIndex secondaryOrder
                                                    newRow, index, secondaryIndex, secondaryOrder, cascadeDb', visited', cascaded'))
                                        |> Result.map (fun (newRow, index', secondaryIndex', secondaryOrder', cascadeDb', visited', cascaded') ->
                                            builder.[rowId] <- newRow
                                            (if newRow <> row then (row, newRow) :: changesRev else changesRev), index', secondaryIndex', secondaryOrder', cascadeDb', visited', cascaded')))

            candidateRows
            |> Option.defaultWith (fun () -> table.RowsArray.Indexed |> List.ofSeq)
            |> foldWithCancellation step (Ok([], table.UniqueIndex, table.SecondaryIndex, table.SecondaryOrder, db, Map.empty, Map.empty))
            |> Result.map (fun (changesRev, index, secondaryIndex, secondaryOrder, cascadeDb, _visited, cascaded) ->
                let updated = publishRows table { table with RowsArray = builder.DrainToImmutable(); UniqueIndex = index; SecondaryIndex = secondaryIndex; SecondaryOrder = secondaryOrder }
                Map.add key updated cascadeDb, (List.rev changesRev, cascaded, db)))

    let result =
        match candidates with
        | None -> withDatabasePublishing store dbName eventsOf (apply None)
        | Some rows ->
            let rowIds = rows |> List.map fst

            withRowLocks store dbName tableName rowIds (fun () ->
                let operation db =
                    let refreshed =
                        db
                        |> Map.tryFind (normalizeTableName tableName)
                        |> Option.map (fun table ->
                            rowIds
                            |> List.distinct
                            |> List.choose (fun rowId -> table.RowsArray.TryFind rowId |> Option.map (fun row -> rowId, row)))
                        |> Option.defaultValue []

                    apply (Some refreshed) db

                withPointUpdateDatabase store dbName tableName rowIds eventsOf operation)

    match result with
    | Ok(changes, _, _) -> Ok changes.Length
    | Error e -> Error e

/// Per-snapshot memo of `RowsArray` as a `Value[] list`, keyed by the
/// `Table` instance itself: `Executor`'s row pipeline is list-based and
/// materializes every scan with `List.ofSeq`. Caching avoids rebuilding
/// the list for repeated scans of an unchanged table. Weak keys keep the
/// cache lifetime aligned with the immutable table root.
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

/// WAL replay runs before live traffic can observe the store, so replay can
/// publish directly without the checked write paths or their synchronization.
let private changeTableForReplay
    (store: Store)
    (dbName: string)
    (tableName: string)
    (change: Table -> Table)
    (onMissing: string -> unit)
    : unit =
    let key = normalizeTableName tableName

    match store.Databases.TryGetValue dbName with
    | false, _ -> onMissing (sprintf "unknown database '%s'" dbName)
    | true, slot ->
        match slot.Value |> Map.tryFind key with
        | None -> onMissing (sprintf "unknown table '%s.%s'" dbName tableName)
        | Some table -> slot.Value <- slot.Value |> Map.add key (change table)

let private replayRowIds (table: Table) (targets: Value[] list) : RowId option list =
    let uniqueGroups = uniqueKeyGroups table

    let tryIndexed target =
        uniqueGroups
        |> List.tryPick (fun group ->
            encodeUniqueKey table.Columns group target
            |> Option.bind (fun key -> table.UniqueIndex |> Map.tryFind group.Name |> Option.bind (Map.tryFind key)))
        |> Option.filter (fun rowId -> table.RowsArray.TryFind rowId = Some target)

    let indexed = targets |> List.map tryIndexed

    if indexed |> List.forall Option.isSome then
        indexed
    else
        let rec locate found rows targets =
            match rows, targets with
            | _, [] -> List.rev found
            | [], remaining -> List.rev found @ List.replicate remaining.Length None
            | (rowId, row) :: remainingRows, target :: remainingTargets when row = target ->
                locate (Some rowId :: found) remainingRows remainingTargets
            | _ :: remainingRows, _ -> locate found remainingRows targets

        locate [] (List.ofSeq table.RowsArray.Indexed) targets

let private reindexReplayRow table uniqueGroups secondaryGroups removed added uniqueIndex secondaryIndex secondaryOrder =
    reindexRow
        table.Columns
        uniqueGroups
        secondaryGroups
        removed
        added
        uniqueIndex
        secondaryIndex
        secondaryOrder

/// Applies already-validated WAL updates while preserving stable row ids.
let updateRowsForReplay
    (store: Store)
    (dbName: string)
    (tableName: string)
    (changes: (Value[] * Value[]) list)
    (onMissing: string -> unit)
    : unit =
    let update (table: Table) =
        let located = replayRowIds table (changes |> List.map fst)
        let rows = table.RowsArray.ToBuilder()
        let uniqueGroups = uniqueKeyGroups table
        let secondaryGroups = secondaryKeyGroups table
        let mutable uniqueIndex = table.UniqueIndex
        let mutable secondaryIndex = table.SecondaryIndex
        let mutable secondaryOrder = table.SecondaryOrder

        for rowId, (before, after) in List.zip located changes do
            match rowId with
            | None -> ()
            | Some rowId ->
                rows.[rowId] <- after

                let nextUnique, nextSecondary, nextOrder =
                    reindexReplayRow table uniqueGroups secondaryGroups (Some(rowId, before)) (Some(rowId, after)) uniqueIndex secondaryIndex secondaryOrder

                uniqueIndex <- nextUnique
                secondaryIndex <- nextSecondary
                secondaryOrder <- nextOrder

        publishRows table
            { table with
                RowsArray = rows.DrainToImmutable()
                UniqueIndex = uniqueIndex
                SecondaryIndex = secondaryIndex
                SecondaryOrder = secondaryOrder }

    changeTableForReplay store dbName tableName update onMissing

/// Applies already-validated WAL deletes while preserving stable row ids.
let deleteRowsForReplay
    (store: Store)
    (dbName: string)
    (tableName: string)
    (targets: Value[] list)
    (onMissing: string -> unit)
    : unit =
    let delete (table: Table) =
        let located = replayRowIds table targets
        let rows = table.RowsArray.ToBuilder()
        let uniqueGroups = uniqueKeyGroups table
        let secondaryGroups = secondaryKeyGroups table
        let mutable uniqueIndex = table.UniqueIndex
        let mutable secondaryIndex = table.SecondaryIndex
        let mutable secondaryOrder = table.SecondaryOrder

        for rowId, row in List.zip located targets do
            match rowId with
            | None -> ()
            | Some rowId ->
                rows.Remove rowId |> ignore

                let nextUnique, nextSecondary, nextOrder =
                    reindexReplayRow table uniqueGroups secondaryGroups (Some(rowId, row)) None uniqueIndex secondaryIndex secondaryOrder

                uniqueIndex <- nextUnique
                secondaryIndex <- nextSecondary
                secondaryOrder <- nextOrder

        publishRows table
            { table with
                RowsArray = rows.DrainToImmutable()
                UniqueIndex = uniqueIndex
                SecondaryIndex = secondaryIndex
                SecondaryOrder = secondaryOrder }

    changeTableForReplay store dbName tableName delete onMissing

/// Restores metadata that is assigned at creation rather than derived from
/// table contents.
let setTableCreateTimeForReplay
    (store: Store)
    (dbName: string)
    (tableName: string)
    (createTime: DateTime)
    (onMissing: string -> unit)
    : unit =
    let key = normalizeTableName tableName

    match store.Databases.TryGetValue dbName with
    | false, _ -> onMissing (sprintf "unknown database '%s'" dbName)
    | true, slot ->
        match slot.Value |> Map.tryFind key with
        | None -> onMissing (sprintf "unknown table '%s.%s'" dbName tableName)
        | Some table -> slot.Value <- slot.Value |> Map.add key { table with CreateTime = createTime }

/// Puts already-committed rows back exactly as they were, for WAL replay.
/// Deliberately not `insertRows`: these rows passed validation when committed,
/// and replay must not reject writes made with relaxed constraint settings.
///
/// Carries `NextAutoId` past anything the replayed rows used, so a later
/// insert can't reissue an id the WAL already handed out.
let appendRowsForReplay (store: Store) (dbName: string) (tableName: string) (rows: Value[] list) (onMissing: string -> unit) : unit =
    let append (table: Table) =
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

        let builder = table.RowsArray.ToBuilder()
        let uniqueGroups = uniqueKeyGroups table
        let secondaryGroups = secondaryKeyGroups table
        let mutable uniqueIndex = table.UniqueIndex
        let mutable secondaryIndex = table.SecondaryIndex
        let mutable secondaryOrder = table.SecondaryOrder

        for row in rows do
            let rowId = builder.Add row

            let nextUnique, nextSecondary, nextOrder =
                reindexReplayRow table uniqueGroups secondaryGroups None (Some(rowId, row)) uniqueIndex secondaryIndex secondaryOrder

            uniqueIndex <- nextUnique
            secondaryIndex <- nextSecondary
            secondaryOrder <- nextOrder

        publishRows table
            { table with
                RowsArray = builder.DrainToImmutable()
                NextAutoId = nextAutoId
                UniqueIndex = uniqueIndex
                SecondaryIndex = secondaryIndex
                SecondaryOrder = secondaryOrder }

    changeTableForReplay store dbName tableName append onMissing

/// Rebuilds derived indexes once after loading a snapshot and its WAL tail.
/// This also repairs snapshots written by older versions whose index formats
/// are not persisted.
let reindexAllForReplay (store: Store) : unit =
    for KeyValue(_, slot) in store.Databases do
        slot.Value <- slot.Value |> Map.map (fun _ table -> reindexTable table)
