/// In-memory multi-database catalog: snapshot reads, serialized writes.
/// A `Catalog` is an immutable `Map`, so every read is a lock-free snapshot
/// and every write swaps in a brand new `Catalog` under a lock.
module Fsdb.Storage

open System
open Fsdb.Ast
open Fsdb.Value

/// Storage-layer failures, mapped to MySQL error codes by `toMySqlError`.
type StorageError =
    | NoSuchDatabase of name: string
    | TableExists of name: string
    | NoSuchTable of name: string
    | UnknownColumn of name: string
    | ColumnCountMismatch of expected: int * actual: int
    | NotNullViolation of column: string
    | InvalidValueForColumn of column: string * value: string

/// MySQL error code + message for a `StorageError`, ready for the wire
/// protocol's ERR packet.
let toMySqlError (err: StorageError) : int * string =
    match err with
    | NoSuchDatabase name -> 1049, sprintf "Unknown database '%s'" name
    | TableExists name -> 1050, sprintf "Table '%s' already exists" name
    | NoSuchTable name -> 1146, sprintf "Table '%s' doesn't exist" name
    | UnknownColumn name -> 1054, sprintf "Unknown column '%s' in field list" name
    | ColumnCountMismatch(expected, actual) ->
        1136, sprintf "Column count doesn't match value count at row 1 (expected %d, got %d)" expected actual
    | NotNullViolation column -> 1048, sprintf "Column '%s' cannot be null" column
    | InvalidValueForColumn(column, value) -> 1366, sprintf "Incorrect value: '%s' for column '%s'" value column

/// A table's rows, newest last. `OriginalName` keeps the as-created casing
/// for information_schema, even though the catalog keys tables by their
/// lowercased name.
type Table =
    { OriginalName: string
      Columns: ColumnDef list
      Rows: Value[] list
      NextAutoId: int64 }

/// Table names are case-insensitive, keyed by their lowercased form.
type Database = Map<string, Table>

/// Database names, as given, to a `Database`.
type Catalog = Map<string, Database>

let defaultDatabase = "fsdb"

/// ponytail: one global write lock for the whole catalog rather than
/// per-table locks — fine until write throughput across unrelated tables
/// actually matters, at which point shard the lock per table.
type Store = { mutable Catalog: Catalog; Lock: obj }

let create () : Store =
    { Catalog = Map.ofList [ defaultDatabase, Map.empty ]
      Lock = obj () }

let private normalizeTableName (name: string) = name.ToLowerInvariant()

/// Applies `f` to the current catalog and atomically swaps it in on
/// success. All writes serialize through this one lock.
let private withWrite (store: Store) (f: Catalog -> Result<Catalog * 'a, StorageError>) : Result<'a, StorageError> =
    lock store.Lock (fun () ->
        match f store.Catalog with
        | Ok(catalog', result) ->
            store.Catalog <- catalog'
            Ok result
        | Error e -> Error e)

let private tryGetDatabase (catalog: Catalog) (dbName: string) : Result<Database, StorageError> =
    match Map.tryFind dbName catalog with
    | Some db -> Ok db
    | None -> Error(NoSuchDatabase dbName)

let private tryGetTable (db: Database) (tableName: string) : Result<Table, StorageError> =
    match Map.tryFind (normalizeTableName tableName) db with
    | Some t -> Ok t
    | None -> Error(NoSuchTable tableName)

/// Auto-creates a database on first use (e.g. `USE`, `CREATE TABLE`); a
/// no-op if it already exists.
let ensureDatabase (store: Store) (dbName: string) : unit =
    lock store.Lock (fun () ->
        if not (Map.containsKey dbName store.Catalog) then
            store.Catalog <- Map.add dbName Map.empty store.Catalog)

/// Index of a column by name, case-insensitive.
let resolveColumn (columns: ColumnDef list) (name: string) : Result<int, StorageError> =
    columns
    |> List.tryFindIndex (fun c -> String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
    |> function
        | Some i -> Ok i
        | None -> Error(UnknownColumn name)

let createTable (store: Store) (dbName: string) (tableName: string) (columns: ColumnDef list) : Result<unit, StorageError> =
    ensureDatabase store dbName

    withWrite store (fun catalog ->
        match tryGetDatabase catalog dbName with
        | Error e -> Error e
        | Ok db ->
            let key = normalizeTableName tableName

            if Map.containsKey key db then
                Error(TableExists tableName)
            else
                let table =
                    { OriginalName = tableName
                      Columns = columns
                      Rows = []
                      NextAutoId = 1L }

                Ok(Map.add dbName (Map.add key table db) catalog, ()))

let dropTable (store: Store) (dbName: string) (tableName: string) : Result<unit, StorageError> =
    withWrite store (fun catalog ->
        match tryGetDatabase catalog dbName with
        | Error e -> Error e
        | Ok db ->
            let key = normalizeTableName tableName

            if Map.containsKey key db then
                Ok(Map.add dbName (Map.remove key db) catalog, ())
            else
                Error(NoSuchTable tableName))

let truncate (store: Store) (dbName: string) (tableName: string) : Result<unit, StorageError> =
    withWrite store (fun catalog ->
        match tryGetDatabase catalog dbName with
        | Error e -> Error e
        | Ok db ->
            match tryGetTable db tableName with
            | Error e -> Error e
            | Ok table ->
                let table' = { table with Rows = []; NextAutoId = 1L }
                Ok(Map.add dbName (Map.add (normalizeTableName tableName) table' db) catalog, ()))

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
