/// In-memory multi-database catalog: snapshot reads, serialized writes.
/// A `Catalog` is an immutable `Map`, so every read is a lock-free snapshot
/// and every write swaps in a brand new `Catalog` under a lock.
module Fsdb.Storage

open System
open System.Globalization
open Fsdb.Ast
open Fsdb.Value

/// Storage-layer failures, mapped to MySQL error codes by `toMySqlError`.
/// `ExpressionError` carries an already-formed MySQL (code, message) pair
/// through from `Executor`'s row-level expression evaluation (e.g. an
/// `UPDATE ... SET` right-hand side) — `Storage` doesn't know that
/// vocabulary, but `updateRows`'s `updater` can now fail per row instead of
/// silently writing a `VNull`, and its failure needs to travel the same
/// `Result<_, StorageError>` path every other write error does.
type StorageError =
    | NoSuchDatabase of name: string
    | TableExists of name: string
    | NoSuchTable of name: string
    | UnknownColumn of name: string
    | ColumnCountMismatch of expected: int * actual: int
    | NotNullViolation of column: string
    | InvalidValueForColumn of column: string * value: string
    | ExpressionError of code: int * message: string

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
    | ExpressionError(code, message) -> code, message

/// A table's rows, newest last. `OriginalName` keeps the as-created casing
/// for information_schema, even though the catalog keys tables by their
/// lowercased name. `Indexes`/`ForeignKeys` are metadata only — see the
/// ponytail notes on `Ast.IndexDef`/`Ast.ForeignKeyDef` for what's not
/// enforced yet.
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

let private traverseResult (f: 'a -> Result<'b, StorageError>) (xs: 'a list) : Result<'b list, StorageError> = traverse f xs

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
                | None -> fail ()
            | _ -> fail ()
        | TDouble
        | TFloat ->
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
        | TChar _
        | TVarchar _
        | TTinyText
        | TText
        | TMediumText
        | TLongText
        | TBinary _
        | TVarBinary _
        | TTinyBlob
        | TBlob
        | TMediumBlob
        | TLongBlob
        | TSet _
        | TTime
        | TJson -> Ok(VString(v |> toText |> Option.defaultValue ""))
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

let dropTable (store: Store) (dbName: string) (tableName: string) : Result<unit, StorageError> =
    withDatabase store dbName (fun db ->
        let key = normalizeTableName tableName

        if Map.containsKey key db then
            Ok(Map.remove key db, ())
        else
            Error(NoSuchTable tableName))

let truncate (store: Store) (dbName: string) (tableName: string) : Result<unit, StorageError> =
    withTable store dbName tableName (fun table -> Ok({ table with Rows = []; NextAutoId = 1L }, ()))

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

/// Applies one `Ast.AlterAction` to `table`, returning its replacement and,
/// for `RenameTo`, the new key it should be re-filed under in the database
/// map (`None` means "same key").
let private applyAlterAction (table: Table) (action: AlterAction) : Result<Table * string option, StorageError> =
    match action with
    | AddColumn col ->
        let fill = addedColumnFill col
        Ok(
            { table with
                Columns = table.Columns @ [ col ]
                Rows = table.Rows |> List.map (fun r -> Array.append r [| fill |]) },
            None
        )
    | DropColumn name ->
        resolveColumn table.Columns name
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> List.indexed |> List.filter (fun (i, _) -> i <> idx) |> List.map snd
                Rows = table.Rows |> List.map (removeColumnAt idx) },
            None)
    | ModifyColumn newDef ->
        // ponytail: replaces the column's definition only — existing rows
        // aren't re-coerced into the new type, so a `MODIFY` that narrows a
        // type can leave a row holding a value that wouldn't itself pass
        // `coerceValue` today. Add a re-coercion pass if a migration's
        // assertions ever depend on it.
        resolveColumn table.Columns newDef.Name
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> List.mapi (fun i c -> if i = idx then newDef else c) },
            None)
    | ChangeColumn(oldName, newDef) ->
        resolveColumn table.Columns oldName
        |> Result.map (fun idx ->
            { table with
                Columns = table.Columns |> List.mapi (fun i c -> if i = idx then newDef else c) },
            None)
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

let renameTable (store: Store) (dbName: string) (oldName: string) (newName: string) : Result<unit, StorageError> =
    alterTable store dbName oldName [ RenameTo newName ]

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
    withTable store dbName tableName (fun table ->
        let indices =
            match columns with
            | None -> Ok [ 0 .. table.Columns.Length - 1 ]
            | Some names -> names |> traverseResult (resolveColumn table.Columns)

        indices
        |> Result.bind (fun idxs ->
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

            rowsIn
            |> List.fold step (Ok([], table.NextAutoId, None))
            |> Result.map (fun (newRowsRev, nextAutoId', firstAssigned) ->
                { table with
                    Rows = table.Rows @ List.rev newRowsRev
                    NextAutoId = nextAutoId' },
                (Option.defaultValue 0L firstAssigned, List.length newRowsRev))))

/// The column-index groups that must be unique: the primary key (if any,
/// treated as one unique group across however many columns it spans) plus
/// every `UNIQUE` index. Used by `upsertRows` to find the row (if any) an
/// incoming `INSERT ... ON DUPLICATE KEY UPDATE` row collides with.
let private uniqueKeyColumnSets (table: Table) : int list list =
    let pk =
        table.Columns |> List.indexed |> List.choose (fun (i, c) -> if c.PrimaryKey then Some i else None)

    let fromIndexes =
        table.Indexes
        |> List.filter (fun ix -> ix.Unique)
        |> List.choose (fun ix -> ix.Columns |> traverseResult (resolveColumn table.Columns) |> Result.toOption)

    (if pk.IsEmpty then [] else [ pk ]) @ fromIndexes

/// `INSERT ... ON DUPLICATE KEY UPDATE`: like `insertRows`, but a candidate
/// row that collides with an existing row on any unique key or the primary
/// key is applied to `applyUpdate existingRow candidateRow` instead of being
/// appended. ponytail: matches on plain `Value[]` equality rather than
/// MySQL's collation-aware string comparison — good enough for the typical
/// numeric/exact-string unique keys Laravel migrations declare.
let upsertRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    (applyUpdate: Value[] -> Value[] -> Result<Value[], StorageError>)
    : Result<int64 * int, StorageError> =
    withTable store dbName tableName (fun table ->
        let indices =
            match columns with
            | None -> Ok [ 0 .. table.Columns.Length - 1 ]
            | Some names -> names |> traverseResult (resolveColumn table.Columns)

        indices
        |> Result.bind (fun idxs ->
            let keySets = uniqueKeyColumnSets table

            let findMatch (rows: Value[] list) (candidate: Value[]) =
                rows
                |> List.tryFind (fun existing ->
                    keySets |> List.exists (fun ks -> ks |> List.forall (fun i -> existing.[i] = candidate.[i])))

            let step acc (rowValues: Value list) =
                acc
                |> Result.bind (fun (rowsAcc: Value[] list, nextAutoId, firstAssigned, affected) ->
                    if List.length rowValues <> List.length idxs then
                        Error(ColumnCountMismatch(List.length idxs, List.length rowValues))
                    else
                        let provided = List.zip idxs rowValues |> Map.ofList
                        let rawRow = table.Columns |> List.mapi (fun i _ -> Map.tryFind i provided)

                        processRow nextAutoId rawRow table.Columns
                        |> Result.bind (fun (finalValues, nextAutoId', assignedId) ->
                            let candidate = Array.ofList finalValues

                            match findMatch rowsAcc candidate with
                            | Some existing ->
                                applyUpdate existing candidate
                                |> Result.map (fun updated ->
                                    (rowsAcc |> List.map (fun r -> if r = existing then updated else r)),
                                    nextAutoId',
                                    firstAssigned,
                                    affected + 1)
                            | None -> Ok(rowsAcc @ [ candidate ], nextAutoId', Option.orElse assignedId firstAssigned, affected + 1)))

            rowsIn
            |> List.fold step (Ok(table.Rows, table.NextAutoId, None, 0))
            |> Result.map (fun (rows', nextAutoId', firstAssigned, affected) ->
                { table with Rows = rows'; NextAutoId = nextAutoId' }, (Option.defaultValue 0L firstAssigned, affected))))

let private coerceRow (columns: ColumnDef list) (row: Value[]) : Result<Value[], StorageError> =
    List.zip columns (Array.toList row)
    |> traverseResult (fun (col, v) -> coerceAndCheck col v)
    |> Result.map Array.ofList

/// Deletes every row matching `predicate`. Returns the number of rows
/// removed. `predicate` returns a `Result` rather than a plain `bool` so a
/// per-row WHERE-evaluation failure (not reachable today — every `Value`
/// operation is total — but a real possibility once functions that can
/// fail per row land) surfaces as an `Error` instead of silently being
/// treated as "didn't match".
let deleteRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (predicate: Value[] -> Result<bool, StorageError>)
    : Result<int, StorageError> =
    withTable store dbName tableName (fun table ->
        table.Rows
        |> traverseResult (fun row -> predicate row |> Result.map (fun keep -> keep, row))
        |> Result.map (fun flagged ->
            let kept = flagged |> List.filter (fst >> not) |> List.map snd
            { table with Rows = kept }, flagged |> List.filter fst |> List.length))

/// Replaces every row matching `predicate` with `updater row`, coercing
/// the result back to the table's column types. Returns the number of rows
/// actually *changed* — matching but no-op writes (`SET v = v`) don't count,
/// matching MySQL's "Changed: n" rather than "Rows matched: n" — via `Value[]`'s
/// structural equality (F# arrays compare structurally, element by element).
/// As with `deleteRows`, `predicate` and `updater` both return `Result`
/// rather than defaulting a failure away.
let updateRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (predicate: Value[] -> Result<bool, StorageError>)
    (updater: Value[] -> Result<Value[], StorageError>)
    : Result<int, StorageError> =
    withTable store dbName tableName (fun table ->
        let applyToRow row =
            predicate row
            |> Result.bind (fun keep ->
                if keep then
                    updater row
                    |> Result.bind (coerceRow table.Columns)
                    |> Result.map (fun r -> r, r <> row)
                else
                    Ok(row, false))

        table.Rows
        |> traverseResult applyToRow
        |> Result.map (fun rowsWithFlags ->
            { table with Rows = rowsWithFlags |> List.map fst }, rowsWithFlags |> List.filter snd |> List.length))

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
