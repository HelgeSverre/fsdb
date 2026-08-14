/// In-memory multi-database catalog: snapshot reads, serialized writes.
/// A `Catalog` is an immutable `Map`, so every read is a lock-free snapshot
/// and every write swaps in a brand new `Catalog` under a lock.
module Fsdb.Storage

open System
open System.Globalization
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

/// Applies `f` to each element, short-circuiting on the first `Error`.
let private traverseResult (f: 'a -> Result<'b, StorageError>) (xs: 'a list) : Result<'b list, StorageError> =
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
/// (`'12' -> 12` for an INT column); Error 1366 when it's not possible.
/// NULL always passes through untouched — nullability is checked
/// separately.
let coerceValue (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    let fail () =
        Error(InvalidValueForColumn(col.Name, v |> toText |> Option.defaultValue "NULL"))

    match v with
    | VNull -> Ok VNull
    | _ ->
        match col.Type with
        | TInt
        | TBigInt _
        | TTinyInt
        | TBool ->
            match v with
            | VInt i -> Ok(VInt i)
            | VDouble d -> Ok(VInt(int64 d))
            | VDecimal d -> Ok(VInt(int64 d))
            | VString s ->
                match parseNumeric s with
                | Some d -> Ok(VInt(int64 d))
                | None -> fail ()
            | _ -> fail ()
        | TDouble ->
            match v with
            | VDouble d -> Ok(VDouble d)
            | VInt i -> Ok(VDouble(float i))
            | VDecimal d -> Ok(VDouble(float d))
            | VString s ->
                match parseNumeric s with
                | Some d -> Ok(VDouble d)
                | None -> fail ()
            | _ -> fail ()
        | TDecimal _ ->
            match v with
            | VDecimal d -> Ok(VDecimal d)
            | VInt i -> Ok(VDecimal(decimal i))
            | VDouble d -> Ok(VDecimal(decimal d))
            | VString s ->
                match parseNumeric s with
                | Some d -> Ok(VDecimal(decimal d))
                | None -> fail ()
            | _ -> fail ()
        | TVarchar _
        | TText
        | TJson -> Ok(VString(v |> toText |> Option.defaultValue ""))
        | TDate ->
            match v with
            | VDate d -> Ok(VDate d)
            | VDateTime dt -> Ok(VDate(DateOnly.FromDateTime dt))
            | VString s ->
                match DateOnly.TryParse(s.Trim(), CultureInfo.InvariantCulture) with
                | true, d -> Ok(VDate d)
                | false, _ -> fail ()
            | _ -> fail ()
        | TDateTime
        | TTimestamp ->
            match v with
            | VDateTime dt -> Ok(VDateTime dt)
            | VDate d -> Ok(VDateTime(d.ToDateTime(TimeOnly.MinValue)))
            | VString s ->
                match DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, dt -> Ok(VDateTime dt)
                | false, _ -> fail ()
            | _ -> fail ()

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
let private coerceAndCheck (col: ColumnDef) (v: Value) : Result<Value, StorageError> =
    match v with
    | VNull when not col.Nullable -> Error(NotNullViolation col.Name)
    | _ -> coerceValue col v

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

/// One column's value for one row being inserted, threaded through
/// `processRow`'s fold: the column's final coerced value, the updated
/// AUTO_INCREMENT counter, and the id assigned to this row's
/// AUTO_INCREMENT column (if any).
let private processRow
    (nextAutoId: int64)
    (rawRow: Value option list)
    (columns: ColumnDef list)
    : Result<Value list * int64 * int64 option, StorageError> =
    let step acc (col: ColumnDef, provided: Value option) =
        match acc with
        | Error e -> Error e
        | Ok(valuesRev, nextAutoId, assignedId) ->
            let pending = provided |> Option.defaultValue (evalDefault col.Default)

            if col.AutoIncrement then
                match pending with
                | VNull -> Ok(VInt nextAutoId :: valuesRev, nextAutoId + 1L, Some nextAutoId)
                | _ ->
                    match coerceValue col pending with
                    | Error e -> Error e
                    | Ok(VInt i) -> Ok(VInt i :: valuesRev, max nextAutoId (i + 1L), assignedId)
                    | Ok _ -> Error(InvalidValueForColumn(col.Name, "auto_increment"))
            else
                match coerceAndCheck col pending with
                | Ok v -> Ok(v :: valuesRev, nextAutoId, assignedId)
                | Error e -> Error e

    List.zip columns rawRow
    |> List.fold step (Ok([], nextAutoId, None))
    |> Result.map (fun (valuesRev, nextAutoId, assignedId) -> List.rev valuesRev, nextAutoId, assignedId)

/// Inserts rows built from `columns` (the explicit column list, or `None`
/// for "all columns in table order") and matching value lists, applying
/// defaults, AUTO_INCREMENT assignment, and NOT NULL/type-coercion checks.
/// Returns `(lastInsertId, affected row count)`; `lastInsertId` is the
/// first AUTO_INCREMENT id assigned by this statement, or 0 if none was.
let insertRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    : Result<int64 * int, StorageError> =
    withWrite store (fun catalog ->
        match tryGetDatabase catalog dbName with
        | Error e -> Error e
        | Ok db ->
            match tryGetTable db tableName with
            | Error e -> Error e
            | Ok table ->
                let indices =
                    match columns with
                    | None -> Ok [ 0 .. table.Columns.Length - 1 ]
                    | Some names -> names |> traverseResult (resolveColumn table.Columns)

                match indices with
                | Error e -> Error e
                | Ok idxs ->
                    let step acc (rowValues: Value list) =
                        match acc with
                        | Error e -> Error e
                        | Ok(rowsRev, nextAutoId, firstAssigned) ->
                            if List.length rowValues <> List.length idxs then
                                Error(ColumnCountMismatch(List.length idxs, List.length rowValues))
                            else
                                let provided = List.zip idxs rowValues |> Map.ofList
                                let rawRow = table.Columns |> List.mapi (fun i _ -> Map.tryFind i provided)

                                match processRow nextAutoId rawRow table.Columns with
                                | Error e -> Error e
                                | Ok(finalValues, nextAutoId', assignedId) ->
                                    Ok(Array.ofList finalValues :: rowsRev, nextAutoId', Option.orElse assignedId firstAssigned)

                    match rowsIn |> List.fold step (Ok([], table.NextAutoId, None)) with
                    | Error e -> Error e
                    | Ok(newRowsRev, nextAutoId', firstAssigned) ->
                        let table' =
                            { table with
                                Rows = table.Rows @ List.rev newRowsRev
                                NextAutoId = nextAutoId' }

                        Ok(
                            Map.add dbName (Map.add (normalizeTableName tableName) table' db) catalog,
                            (Option.defaultValue 0L firstAssigned, List.length newRowsRev)
                        ))

let private coerceRow (columns: ColumnDef list) (row: Value[]) : Result<Value[], StorageError> =
    List.zip columns (Array.toList row)
    |> traverseResult (fun (col, v) -> coerceAndCheck col v)
    |> Result.map Array.ofList

/// Deletes every row matching `predicate`. Returns the number of rows
/// removed.
let deleteRows (store: Store) (dbName: string) (tableName: string) (predicate: Value[] -> bool) : Result<int, StorageError> =
    withWrite store (fun catalog ->
        match tryGetDatabase catalog dbName with
        | Error e -> Error e
        | Ok db ->
            match tryGetTable db tableName with
            | Error e -> Error e
            | Ok table ->
                let kept, removed = table.Rows |> List.partition (predicate >> not)
                let table' = { table with Rows = kept }
                Ok(Map.add dbName (Map.add (normalizeTableName tableName) table' db) catalog, List.length removed))

/// Replaces every row matching `predicate` with `updater row`, coercing
/// the result back to the table's column types. Returns the number of rows
/// actually *changed* — matching but no-op writes (`SET v = v`) don't count,
/// matching MySQL's "Changed: n" rather than "Rows matched: n" — via `Value[]`'s
/// structural equality (F# arrays compare structurally, element by element).
let updateRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (predicate: Value[] -> bool)
    (updater: Value[] -> Value[])
    : Result<int, StorageError> =
    withWrite store (fun catalog ->
        match tryGetDatabase catalog dbName with
        | Error e -> Error e
        | Ok db ->
            match tryGetTable db tableName with
            | Error e -> Error e
            | Ok table ->
                let applyToRow row =
                    if predicate row then
                        updater row |> coerceRow table.Columns |> Result.map (fun r -> r, r <> row)
                    else
                        Ok(row, false)

                match table.Rows |> traverseResult applyToRow with
                | Error e -> Error e
                | Ok rowsWithFlags ->
                    let table' =
                        { table with Rows = rowsWithFlags |> List.map fst }

                    let affected = rowsWithFlags |> List.filter snd |> List.length
                    Ok(Map.add dbName (Map.add (normalizeTableName tableName) table' db) catalog, affected))

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
