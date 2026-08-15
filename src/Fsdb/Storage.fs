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

/// ponytail: one global write lock for the whole catalog rather than
/// per-table locks — fine until write throughput across unrelated tables
/// actually matters, at which point shard the lock per table.
///
/// `ForeignKeyChecks` gates every FK enforcement in this module (cascading
/// deletes, `RESTRICT`, parent-existence checks on insert/update) — the
/// storage-level mirror of MySQL's session `FOREIGN_KEY_CHECKS` variable.
/// It's a single store-wide flag rather than per-session because `Store`
/// has no session concept; Integrate: `QueryHandler`'s `SET
/// FOREIGN_KEY_CHECKS = 0|1` (and Laravel's
/// `Schema::disableForeignKeyConstraints`, which sends exactly that) should
/// call `setForeignKeyChecks` — see the comment above `tryProbe`'s `SetVar`
/// case in QueryHandler.fs, which already anticipates this.
type Store =
    { mutable Catalog: Catalog
      mutable ForeignKeyChecks: bool
      Lock: obj }

let create () : Store =
    { Catalog = Map.ofList [ defaultDatabase, Map.empty ]
      ForeignKeyChecks = true
      Lock = obj () }

/// `SET FOREIGN_KEY_CHECKS = 0|1` — Integrate wires this from
/// `QueryHandler`'s `SET` probe (see the note on `Store.ForeignKeyChecks`).
let setForeignKeyChecks (store: Store) (enabled: bool) : unit =
    lock store.Lock (fun () -> store.ForeignKeyChecks <- enabled)

let private normalizeTableName (name: string) = name.ToLowerInvariant()

/// `CREATE DATABASE name` — unlike `ensureDatabase` (silent no-op used by
/// `USE`/handshake auto-create), this errors 1007 if it already exists;
/// `Executor` swallows that error for `IF NOT EXISTS`, same pattern as
/// `createTable`.
let createDatabase (store: Store) (dbName: string) : Result<unit, StorageError> =
    lock store.Lock (fun () ->
        if Map.containsKey dbName store.Catalog then
            Error(DatabaseExists dbName)
        else
            store.Catalog <- Map.add dbName Map.empty store.Catalog
            Ok())

let dropDatabase (store: Store) (dbName: string) : Result<unit, StorageError> =
    lock store.Lock (fun () ->
        if Map.containsKey dbName store.Catalog then
            store.Catalog <- Map.remove dbName store.Catalog
            Ok()
        else
            Error(NoSuchDatabase dbName))

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

/// Verifies every foreign key `fks` (a child table's own `ForeignKeys`) has
/// a matching parent row for `row`'s values, per MySQL's MATCH SIMPLE
/// semantics: a foreign key with any `NULL` column doesn't need a parent at
/// all. Malformed FK metadata (a column name that no longer resolves, e.g.
/// after a `DROP COLUMN` that didn't also drop the FK) or a since-dropped
/// referenced table/column is treated as "not enforceable" rather than
/// blocking every write — `information_schema` can still show the stale FK,
/// same as MySQL leaves a dangling constraint visible after `DROP TABLE ...
/// FOREIGN_KEY_CHECKS=0`.
let private checkFkParents (db: Database) (childColumns: ColumnDef list) (fks: ForeignKeyDef list) (row: Value[]) : Result<unit, StorageError> =
    let checkOne (fk: ForeignKeyDef) =
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

    fks |> traverse checkOne |> Result.map ignore

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
let private insertCore
    (checkFks: bool)
    (ignoreErrors: bool)
    (db: Database)
    (tableKey: string)
    (rowsIn: Value list list)
    (idxs: int list)
    : Result<Database * (int64 * int), StorageError> =
    let table = Map.find tableKey db
    let uniqueGroups = uniqueKeyGroups table

    let step acc (rowValues: Value list) =
        acc
        |> Result.bind (fun (accepted: Value[] list, nextAutoId, firstAssigned) ->
            if List.length rowValues <> List.length idxs then
                Error(ColumnCountMismatch(List.length idxs, List.length rowValues))
            else
                let provided = List.zip idxs rowValues |> Map.ofList
                let rawRow = table.Columns |> List.mapi (fun i _ -> Map.tryFind i provided)

                let rowResult =
                    processRow nextAutoId rawRow table.Columns
                    |> Result.bind (fun (finalValues, nextAutoId', assignedId) ->
                        let candidate = Array.ofList finalValues

                        match findUniqueCollision uniqueGroups (table.Rows @ accepted) candidate with
                        | Some e -> Error e
                        | None ->
                            if checkFks then
                                checkFkParents db table.Columns table.ForeignKeys candidate
                                |> Result.map (fun () -> candidate, nextAutoId', assignedId)
                            else
                                Ok(candidate, nextAutoId', assignedId))

                match rowResult with
                | Ok(candidate, nextAutoId', assignedId) -> Ok(accepted @ [ candidate ], nextAutoId', Option.orElse assignedId firstAssigned)
                | Error _ when ignoreErrors -> Ok(accepted, nextAutoId, firstAssigned)
                | Error e -> Error e)

    rowsIn
    |> List.fold step (Ok([], table.NextAutoId, None))
    |> Result.map (fun (accepted, nextAutoId', firstAssigned) ->
        let table' = { table with Rows = table.Rows @ accepted; NextAutoId = nextAutoId' }
        Map.add tableKey table' db, (Option.defaultValue 0L firstAssigned, List.length accepted))

/// Inserts rows built from `columns` and matching value lists, applying
/// defaults, AUTO_INCREMENT assignment, NOT NULL/type-coercion checks, and
/// — new here — unique-key (error 1062) and, when `store.ForeignKeyChecks`
/// is set, foreign-key parent-existence (error 1452) checks. Returns
/// `(lastInsertId, affected row count)`; `lastInsertId` is the first
/// AUTO_INCREMENT id assigned by this statement, or 0 if none was. Fails the
/// whole statement on the first bad row — see `insertRowsIgnore` for `INSERT
/// IGNORE`'s per-row skip semantics.
let insertRows
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    : Result<int64 * int, StorageError> =
    withDatabase store dbName (fun db ->
        let key = normalizeTableName tableName

        tryGetTable db tableName
        |> Result.bind (fun table ->
            resolveInsertColumns table columns
            |> Result.bind (insertCore store.ForeignKeyChecks false db key rowsIn)))

/// `INSERT IGNORE`: as `insertRows`, but a row that would violate NOT
/// NULL/unique/foreign-key constraints is skipped instead of failing the
/// statement — MySQL downgrades the error to a warning per row. The
/// returned affected count is only the rows actually inserted;
/// `lastInsertId` is the first one assigned, same as `insertRows` (0 if
/// every row was skipped).
let insertRowsIgnore
    (store: Store)
    (dbName: string)
    (tableName: string)
    (columns: string list option)
    (rowsIn: Value list list)
    : Result<int64 * int, StorageError> =
    withDatabase store dbName (fun db ->
        let key = normalizeTableName tableName

        tryGetTable db tableName
        |> Result.bind (fun table ->
            resolveInsertColumns table columns
            |> Result.bind (insertCore store.ForeignKeyChecks true db key rowsIn)))

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
    (applyUpdate: Value[] -> Value[] -> Result<Value[], StorageError>)
    : Result<int64 * int, StorageError> =
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
    |> traverse (fun (col, v) -> coerceAndCheck col v)
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
let rec private cascadeDelete (checkFks: bool) (db: Database) (tableKey: string) (toDelete: Value[] list) : Result<Database, StorageError> =
    let removeFrom (d: Database) =
        let t = Map.find tableKey d
        let isDeleted row = toDelete |> List.exists ((=) row)
        Map.add tableKey { t with Rows = t.Rows |> List.filter (isDeleted >> not) } d

    if toDelete.IsEmpty then
        Ok db
    elif not checkFks then
        Ok(removeFrom db)
    else
        let table = Map.find tableKey db

        let applyChild dbAcc (childKey: string, fk: ForeignKeyDef) =
            dbAcc
            |> Result.bind (fun d ->
                let childTbl = Map.find childKey d

                match fk.Columns |> traverse (resolveColumn childTbl.Columns), fk.RefColumns |> traverse (resolveColumn table.Columns) with
                | Error _, _
                | _, Error _ -> Ok d // stale FK metadata — see `checkFkParents`'s note.
                | Ok childIdxs, Ok refIdxs ->
                    let parentKeys = toDelete |> List.map (fun row -> refIdxs |> List.map (fun i -> row.[i]))

                    let isChild (row: Value[]) =
                        let key = childIdxs |> List.map (fun i -> row.[i])

                        key |> List.forall ((<>) VNull)
                        && parentKeys |> List.exists (List.forall2 (fun a b -> compare a b = 0) key)

                    let matching = childTbl.Rows |> List.filter isChild

                    if matching.IsEmpty then
                        Ok d
                    else
                        match fk.OnDelete |> Option.map (fun s -> s.Trim().ToUpperInvariant()) with
                        | Some "CASCADE" -> cascadeDelete checkFks d childKey matching
                        | Some "SET NULL" ->
                            let blanked row =
                                if isChild row then
                                    let row' = Array.copy row
                                    childIdxs |> List.iter (fun i -> row'.[i] <- VNull)
                                    row'
                                else
                                    row

                            Ok(Map.add childKey { childTbl with Rows = childTbl.Rows |> List.map blanked } d)
                        | _ -> Error(ForeignKeyRestrict fk.Name))

        referencingForeignKeys db tableKey
        |> List.fold applyChild (Ok db)
        |> Result.map removeFrom

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
    withDatabase store dbName (fun db ->
        let key = normalizeTableName tableName

        tryGetTable db tableName
        |> Result.bind (fun table ->
            table.Rows
            |> traverse (fun row -> predicate row |> Result.map (fun keep -> keep, row))
            |> Result.bind (fun flagged ->
                let toDelete = flagged |> List.filter fst |> List.map snd
                cascadeDelete store.ForeignKeyChecks db key toDelete |> Result.map (fun db' -> db', toDelete.Length))))

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
    withDatabase store dbName (fun db ->
        let key = normalizeTableName tableName

        tryGetTable db tableName
        |> Result.bind (fun table ->
            let uniqueGroups = uniqueKeyGroups table
            let checkFks = store.ForeignKeyChecks
            let original = Array.ofList table.Rows

            let applyToRow i row =
                predicate row
                |> Result.bind (fun keep ->
                    if not keep then
                        Ok(row, false)
                    else
                        updater row
                        |> Result.bind (coerceRow table.Columns)
                        |> Result.bind (fun newRow ->
                            let others =
                                original |> Array.indexed |> Array.filter (fun (j, _) -> j <> i) |> Array.map snd |> List.ofArray

                            match findUniqueCollision uniqueGroups others newRow with
                            | Some e -> Error e
                            | None ->
                                if checkFks then
                                    checkFkParents db table.Columns table.ForeignKeys newRow
                                    |> Result.map (fun () -> newRow)
                                else
                                    Ok newRow)
                        |> Result.map (fun newRow -> newRow, newRow <> row))

            original
            |> List.ofArray
            |> List.indexed
            |> traverse (fun (i, row) -> applyToRow i row)
            |> Result.map (fun rowsWithFlags ->
                let table' = { table with Rows = rowsWithFlags |> List.map fst }
                Map.add key table' db, rowsWithFlags |> List.filter snd |> List.length)))

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
/// VIRTUAL]`) — **not enforced by this module**. `Ast.ColumnDef` has no
/// field carrying the `AS (expr)` generation expression (only `Default`,
/// which is a fixed value or `CURRENT_TIMESTAMP`, evaluated once at insert
/// time — a generated column instead recomputes from *other columns in the
/// same row*, every insert/update, which needs a real expression
/// evaluator), so `Storage` has no way to know which columns are generated
/// or what to compute — that's `Executor`'s `evalExpr`, not this module's.
/// Read-back today falls through `evalDefault`, i.e. `NULL`.
///
/// This is as far as `Storage` can go without an `Ast.fs`/`Executor.fs`
/// change (out of scope here — those are owned by the Parser/Executor/Ast
/// agent). Integrate, once `ColumnDef` carries e.g. `Generated: (Expr *
/// stored: bool) option`:
///   1. Add the field to `Ast.ColumnDef` (and thread it through the parser's
///      `CREATE TABLE`/column-definition grammar).
///   2. After `insertRows`/`updateRows` succeeds, for each column with
///      `Generated = Some(expr, _)`, re-run `updateRows` on the affected
///      row(s) with an `updater` built from `applyGeneratedColumns below,
///      passing a `compute` closure that evaluates `expr` via `evalExpr`
///      against the row (generated columns may reference other generated
///      columns, so evaluate in a dependency-safe order — a single
///      left-to-right pass over `columns` is enough unless a migration
///      actually chains them).
///   3. A `VIRTUAL` (not `STORED`) generated column additionally needs to
///      recompute on every `scan`/read rather than be persisted — this
///      table stores every column's value in `Rows` either way, so that's
///      an `Executor`-side read-path concern, not a `Storage` one.
///
/// `applyGeneratedColumns` is the reusable piece in the meantime: given a
/// `compute` callback (`Executor`'s evaluator, closed over each column's
/// `Expr` however it ends up looked up) and the names of the generated
/// columns, it rewrites just those columns of `row`, leaving every other
/// value untouched. `compute row col` sees `row` with every column's
/// *current* value (so a generated column can read a plain column's newly
/// inserted/updated value) and returns `col`'s recomputed value.
let applyGeneratedColumns (compute: Value[] -> ColumnDef -> Value) (generatedColumnNames: string list) (columns: ColumnDef list) (row: Value[]) : Value[] =
    let row' = Array.copy row

    generatedColumnNames
    |> List.iter (fun name ->
        match resolveColumn columns name with
        | Ok idx -> row'.[idx] <- compute row' columns.[idx]
        | Error _ -> ())

    row'
