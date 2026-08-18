/// `information_schema` as virtual tables projected from `Storage.Catalog`
/// at query time — nothing here is ever materialized into the catalog
/// itself; `scan` below builds a table's columns/rows fresh from whatever
/// the catalog looks like right now. Wired into `Executor.execute`'s
/// `Select` case: a `FROM information_schema.<table>` (case-insensitive,
/// qualified or via `USE information_schema`) resolves here instead of
/// `Storage.scan`.
module Fsdb.InformationSchema

open System
open System.Text.RegularExpressions
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage

let private col (name: string) (ty: ColumnType) : ColumnDef =
    { Name = name
      Type = ty
      Nullable = true
      Default = None
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      Generated = None
      Collation = None
      Charset = None
      OnUpdateCurrentTimestamp = false }

let private strCol name = col name (TVarchar 255)
let private intCol name = col name (TInt false)

/// Every real (non-`information_schema`) database in the catalog, flattened
/// to `(dbName, table)` pairs — the source rows every virtual table below
/// filters/reshapes from.
let private allTables (catalog: Catalog) : (string * Table) list =
    catalog
    |> Map.toList
    |> List.collect (fun (dbName, db) -> db |> Map.toList |> List.map (fun (_, table) -> dbName, table))

/// MySQL's `information_schema.columns.data_type` — the bare type name,
/// no length/unsigned/precision.
let private dataTypeName (ty: ColumnType) : string =
    match ty with
    | TTinyInt _ -> "tinyint"
    | TSmallInt _ -> "smallint"
    | TMediumInt _ -> "mediumint"
    | TInt _ -> "int"
    | TBigInt _ -> "bigint"
    | TChar _ -> "char"
    | TVarchar _ -> "varchar"
    | TTinyText -> "tinytext"
    | TText -> "text"
    | TMediumText -> "mediumtext"
    | TLongText -> "longtext"
    | TBinary _ -> "binary"
    | TVarBinary _ -> "varbinary"
    | TTinyBlob -> "tinyblob"
    | TBlob -> "blob"
    | TMediumBlob -> "mediumblob"
    | TLongBlob -> "longblob"
    | TEnum _ -> "enum"
    | TSet _ -> "set"
    | TDecimal _ -> "decimal"
    | TDouble -> "double"
    | TFloat -> "float"
    | TDate -> "date"
    // `data_type` is the bare name — the `(N)` fsp only shows up in
    // `column_type` below, never here (MySQL-verified).
    | TDateTime _ -> "datetime"
    | TTimestamp _ -> "timestamp"
    | TTime _ -> "time"
    | TYear -> "year"
    | TJson -> "json"

/// `information_schema.columns.column_type` — the full declared type text
/// (`int unsigned`, `varchar(255)`, `enum('a','b')`, ...), the same text
/// Laravel's `getColumns()`/`SHOW CREATE TABLE` echo back.
let columnTypeText (ty: ColumnType) : string =
    let quotedList vs = vs |> List.map (sprintf "'%s'") |> String.concat ","
    let unsigned u = if u then " unsigned" else ""

    match ty with
    | TTinyInt u -> "tinyint" + unsigned u
    | TSmallInt u -> "smallint" + unsigned u
    | TMediumInt u -> "mediumint" + unsigned u
    | TInt u -> "int" + unsigned u
    | TBigInt u -> "bigint" + unsigned u
    | TChar n -> sprintf "char(%d)" n
    | TVarchar n -> sprintf "varchar(%d)" n
    | TTinyText -> "tinytext"
    | TText -> "text"
    | TMediumText -> "mediumtext"
    | TLongText -> "longtext"
    | TBinary n -> sprintf "binary(%d)" n
    | TVarBinary n -> sprintf "varbinary(%d)" n
    | TTinyBlob -> "tinyblob"
    | TBlob -> "blob"
    | TMediumBlob -> "mediumblob"
    | TLongBlob -> "longblob"
    | TEnum vs -> sprintf "enum(%s)" (quotedList vs)
    | TSet vs -> sprintf "set(%s)" (quotedList vs)
    | TDecimal(p, s) -> sprintf "decimal(%d,%d)" p s
    | TDouble -> "double"
    | TFloat -> "float"
    | TDate -> "date"
    // `datetime(6)` when fsp > 0, bare `datetime` at fsp 0 — the exact
    // strings `SHOW COLUMNS`/`information_schema.columns.column_type` report
    // (MySQL-verified for all three temporal types).
    | TDateTime fsp -> if fsp > 0 then sprintf "datetime(%d)" fsp else "datetime"
    | TTimestamp fsp -> if fsp > 0 then sprintf "timestamp(%d)" fsp else "timestamp"
    | TTime fsp -> if fsp > 0 then sprintf "time(%d)" fsp else "time"
    | TYear -> "year(4)"
    | TJson -> "json"

/// `character_maximum_length` — only meaningful for the string-ish types;
/// MySQL's fixed per-type ceilings for the `TEXT`/`BLOB` family (`TINYTEXT`
/// = 255, `TEXT` = 65535, ...), the declared length for `CHAR`/`VARCHAR`.
let private charMaxLength (ty: ColumnType) : int64 option =
    match ty with
    | TChar n
    | TVarchar n -> Some(int64 n)
    | TTinyText
    | TTinyBlob -> Some 255L
    | TText
    | TBlob -> Some 65535L
    | TMediumText
    | TMediumBlob -> Some 16777215L
    | TLongText
    | TLongBlob -> Some 4294967295L
    | _ -> None

/// `numeric_precision`/`numeric_scale` — MySQL's standard precision per
/// integer width, or the declared `(p, s)` for `DECIMAL`.
let private numericPrecisionScale (ty: ColumnType) : (int64 * int64) option =
    match ty with
    | TTinyInt _ -> Some(3L, 0L)
    | TSmallInt _ -> Some(5L, 0L)
    | TMediumInt _ -> Some(7L, 0L)
    | TInt _ -> Some(10L, 0L)
    | TBigInt _ -> Some(19L, 0L)
    | TDecimal(p, s) -> Some(int64 p, int64 s)
    | TFloat -> Some(10L, 0L)
    | TDouble -> Some(22L, 0L)
    | _ -> None

/// `datetime_precision` — the fsp for a temporal type (0 for a bare
/// `DATETIME`), `NULL` for everything else. MySQL populates it for `DATE`
/// too (as 0); this only reports it for the fractional-second types, which
/// is what a client reads a `DATETIME(6)`'s precision off of.
let private datetimePrecision (ty: ColumnType) : int64 option =
    match ty with
    | TDateTime fsp
    | TTimestamp fsp
    | TTime fsp -> Some(int64 fsp)
    | _ -> None

let isStringy (ty: ColumnType) : bool =
    match ty with
    | TChar _
    | TVarchar _
    | TTinyText
    | TText
    | TMediumText
    | TLongText
    | TEnum _
    | TSet _ -> true
    | _ -> false

/// `column_key`: `PRI` for a primary-key column, else `UNI`/`MUL` if it's
/// the first column of a unique/plain index — MySQL's own rule for which of
/// several indexes over the same leading column "wins" is more involved
/// than this; good enough for what Laravel's schema introspection actually
/// reads.
let columnKey (table: Table) (c: ColumnDef) : string =
    if c.PrimaryKey then
        "PRI"
    else
        let leads (ix: IndexDef) = (List.tryHead ix.Columns |> Option.map (fun n -> String.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase))) = Some true

        match table.Indexes |> List.tryFind leads with
        | Some ix when ix.Unique -> "UNI"
        | Some _ -> "MUL"
        | None -> ""

let private vs (s: string) = VString s
let private vi (i: int) = VInt(int64 i)
let private vopt (s: string option) = s |> Option.map VString |> Option.defaultValue VNull

let private tablesColumns =
    [ strCol "table_schema"
      strCol "table_name"
      strCol "table_type"
      strCol "engine"
      intCol "table_rows"
      intCol "auto_increment"
      strCol "table_collation"
      strCol "table_comment"
      // Fake but present: Laravel's `compileTables` projects `(data_length +
      // index_length) as size` — this in-memory engine has no real page
      // storage to report, so both are a constant stand-in rather than
      // absent columns that would 1054 on that expression.
      intCol "data_length"
      intCol "index_length"
      // Always empty: `Ast`'s `CREATE TABLE` has no notion of extra table
      // options (`ROW_FORMAT=...` etc.) to echo back. Present only so
      // Doctrine DBAL's `getListTableMetadataSQL` (behind Laravel's
      // `Blueprint::change()`) doesn't 1054 projecting it.
      strCol "create_options" ]

let private tablesRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.map (fun (dbName, t) ->
        // NULL, not 1 (`NextAutoId`'s idle starting value), for a table
        // that never declared an AUTO_INCREMENT column at all — matching
        // real MySQL, which only reports a next-value once a table actually
        // has one.
        let autoIncrement =
            if t.Columns |> List.exists (fun c -> c.AutoIncrement) then VInt t.NextAutoId else VNull

        [| vs dbName
           vs t.OriginalName
           vs "BASE TABLE"
           vs "InnoDB"
           vi (t.RowsArray.Length)
           autoIncrement
           vs (t.TableCollation |> Option.defaultValue "utf8mb4_0900_ai_ci")
           vs ""
           vi 16384
           vi 0
           vs "" |])

let private columnsColumns =
    [ strCol "table_schema"
      strCol "table_name"
      strCol "column_name"
      intCol "ordinal_position"
      strCol "column_default"
      strCol "is_nullable"
      strCol "data_type"
      strCol "column_type"
      intCol "character_maximum_length"
      intCol "numeric_precision"
      intCol "numeric_scale"
      intCol "datetime_precision"
      strCol "column_key"
      strCol "extra"
      // NULL for a non-string column, `utf8mb4` for a string one — same
      // split `collation_name` right below already makes.
      strCol "character_set_name"
      strCol "collation_name"
      // `Ast.ColumnDef` doesn't track a column comment — always empty,
      // present only so Laravel's `compileColumns` (which projects it
      // unconditionally) doesn't 1054.
      strCol "column_comment"
      strCol "generation_expression" ]

/// Renders a generated-column expression back to SQL for
/// `GENERATION_EXPRESSION` / `SHOW CREATE TABLE`, in MySQL's normalized
/// style: backticked column refs, paren-wrapped binops, lowercased function
/// names. Total over every `Expr` case — the subquery/window shapes can't
/// appear in a generated expression, but must render rather than throw.
/// ponytail: no charset introducer on string literals (MySQL prints
/// `_latin1'x'`, this prints `'x'`) — add it if a tool ever diffs the text.
let rec private exprToSql (e: Expr) : string =
    let opText =
        function
        | And -> "and"
        | Or -> "or"
        | Eq -> "="
        | Neq -> "<>"
        | Lt -> "<"
        | Lte -> "<="
        | Gt -> ">"
        | Gte -> ">="
        | Add -> "+"
        | Sub -> "-"
        | Mul -> "*"
        | Div -> "/"
        | IntDiv -> "DIV"
        | NullSafeEq -> "<=>"

    let litText (v: Value) =
        match v with
        | VNull -> "NULL"
        | VString s -> "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'"
        | v -> v |> toText |> Option.defaultValue "NULL"

    match e with
    | Lit v -> litText v
    | Placeholder _ -> "?"
    | Col n -> sprintf "`%s`" n
    | QualifiedCol(t, c) -> sprintf "`%s`.`%s`" t c
    | BinOp(op, a, b) -> sprintf "(%s %s %s)" (exprToSql a) (opText op) (exprToSql b)
    | Not e -> sprintf "(not(%s))" (exprToSql e)
    | IsNull e -> sprintf "(%s is null)" (exprToSql e)
    | IsNotNull e -> sprintf "(%s is not null)" (exprToSql e)
    | IsTrue e -> sprintf "(%s is true)" (exprToSql e)
    | IsFalse e -> sprintf "(%s is false)" (exprToSql e)
    | Like(e, p, _, _) -> sprintf "(%s like %s)" (exprToSql e) (exprToSql p)
    | Regexp(e, p) -> sprintf "(%s regexp %s)" (exprToSql e) (exprToSql p)
    | In(e, cs) -> sprintf "(%s in (%s))" (exprToSql e) (cs |> List.map exprToSql |> String.concat ",")
    | Between(e, lo, hi) -> sprintf "(%s between %s and %s)" (exprToSql e) (exprToSql lo) (exprToSql hi)
    | FuncCall(name, args) -> sprintf "%s(%s)" (name.ToLowerInvariant()) (args |> List.map exprToSql |> String.concat ",")
    | Distinct e -> sprintf "distinct %s" (exprToSql e)
    | OrderBy(e, _) -> exprToSql e
    | Cast(e, ty) -> sprintf "cast(%s as %s)" (exprToSql e) (columnTypeText ty)
    | Collate(e, c) -> sprintf "(%s collate %s)" (exprToSql e) c
    | Case(subject, whens, elseBranch) ->
        let subj = subject |> Option.map (exprToSql >> sprintf " %s") |> Option.defaultValue ""
        let whenText = whens |> List.map (fun (w, t) -> sprintf " when %s then %s" (exprToSql w) (exprToSql t)) |> String.concat ""
        let elseText = elseBranch |> Option.map (exprToSql >> sprintf " else %s") |> Option.defaultValue ""
        sprintf "(case%s%s%s end)" subj whenText elseText
    // Shapes a generated expression can't contain — legal-ish placeholders.
    | Star q -> (q |> Option.map (sprintf "`%s`.") |> Option.defaultValue "") + "*"
    | Exists _ -> "exists(...)"
    | Subquery _
    | InSubquery _ -> "(...)"
    | RowNumberOver _ -> "row_number() over ()"
    | LagOver _ -> "lag() over ()"
    | RankOver(dense, _, _) -> (if dense then "dense_rank" else "rank") + "() over ()"
    | PercentRankOver _ -> "percent_rank() over ()"
    | NTileOver _ -> "ntile() over ()"

/// The `(N)` suffix MySQL appends to `on update CURRENT_TIMESTAMP` for a
/// column declared with a nonzero fractional-seconds precision — empty for
/// fsp 0, matching how it renders the bare keyword with no `(0)`.
let private onUpdateFspSuffix (c: ColumnDef) : string =
    match c.Type with
    | TDateTime fsp
    | TTimestamp fsp
    | TTime fsp when fsp > 0 -> sprintf "(%d)" fsp
    | _ -> ""

/// `EXTRA` / SHOW COLUMNS `Extra` for a column — MySQL says
/// `VIRTUAL GENERATED`/`STORED GENERATED` for generated columns (uppercase),
/// `auto_increment` and `on update CURRENT_TIMESTAMP` (lowercase keyword,
/// uppercase `CURRENT_TIMESTAMP`) otherwise, space-joined when both apply.
let private extraText (c: ColumnDef) : string =
    match c.Generated with
    | Some(_, Virtual) -> "VIRTUAL GENERATED"
    | Some(_, Stored) -> "STORED GENERATED"
    | None ->
        [ if c.AutoIncrement then "auto_increment"
          if c.OnUpdateCurrentTimestamp then
              sprintf "on update CURRENT_TIMESTAMP%s" (onUpdateFspSuffix c) ]
        |> String.concat " "

let defaultText (d: ColumnDefault option) : string option =
    match d with
    | None -> None
    | Some(DConst v) -> v |> toText
    | Some DCurrentTimestamp -> Some "CURRENT_TIMESTAMP"

let private columnsRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.collect (fun (dbName, t) ->
        t.Columns
        |> List.mapi (fun i c ->
            let precision, scale = numericPrecisionScale c.Type |> Option.map (fun (p, s) -> Some p, Some s) |> Option.defaultValue (None, None)

            [| vs dbName
               vs t.OriginalName
               vs c.Name
               vi (i + 1)
               vopt (defaultText c.Default)
               // A primary key column is implicitly NOT NULL in MySQL even
               // without an explicit `NOT NULL` — `Ast.ColumnDef.Nullable`
               // only tracks the explicit modifier (ponytail: `Storage`
               // itself still doesn't reject a NULL insert into an implicit
               // PK column; add that enforcement too if a migration's
               // assertions ever depend on it, not just this metadata view).
               vs (if c.PrimaryKey || not c.Nullable then "NO" else "YES")
               vs (dataTypeName c.Type)
               vs (columnTypeText c.Type)
               (charMaxLength c.Type |> Option.map VInt |> Option.defaultValue VNull)
               (precision |> Option.map VInt |> Option.defaultValue VNull)
               (scale |> Option.map VInt |> Option.defaultValue VNull)
               (datetimePrecision c.Type |> Option.map VInt |> Option.defaultValue VNull)
               vs (columnKey t c)
               vs (extraText c)
               (if isStringy c.Type then vs (c.Charset |> Option.defaultValue "utf8mb4") else VNull)
               (if isStringy c.Type then vs (c.Collation |> Option.defaultValue "utf8mb4_0900_ai_ci") else VNull)
               vs ""
               vs (c.Generated |> Option.map (fst >> exprToSql) |> Option.defaultValue "") |]))

let private statisticsColumns =
    [ strCol "table_schema"
      strCol "table_name"
      intCol "non_unique"
      strCol "index_name"
      intCol "seq_in_index"
      strCol "column_name"
      strCol "collation"
      intCol "cardinality"
      strCol "index_type"
      // A prefix index's `(N)` length (`INDEX(col(10))`) — always NULL
      // (whole-column) since `Ast.IndexDef` doesn't track a prefix length
      // to begin with. Doctrine DBAL's `selectIndexColumns` (behind
      // Laravel's `Blueprint::change()`) projects it unconditionally.
      intCol "sub_part" ]

/// One row per `(index, column)` pair — the primary key surfaces as a
/// synthesized index literally named `PRIMARY`, same as real MySQL, since
/// `Ast.ColumnDef.PrimaryKey` doesn't otherwise have an `IndexDef` of its
/// own.
let private statisticsRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.collect (fun (dbName, t) ->
        let pkCols = t.Columns |> List.filter (fun c -> c.PrimaryKey) |> List.map (fun c -> c.Name)

        let primaryIndex =
            if pkCols.IsEmpty then [] else [ { Name = "PRIMARY"; Columns = pkCols; Unique = true } ]

        primaryIndex @ t.Indexes
        |> List.collect (fun ix ->
            ix.Columns
            |> List.mapi (fun i colName ->
                [| vs dbName
                   vs t.OriginalName
                   vi (if ix.Unique then 0 else 1)
                   vs ix.Name
                   vi (i + 1)
                   vs colName
                   vs "A"
                   vi 0
                   vs "BTREE"
                   VNull |])))

let private keyColumnUsageColumns =
    [ strCol "constraint_schema"
      strCol "constraint_name"
      strCol "table_schema"
      strCol "table_name"
      strCol "column_name"
      intCol "ordinal_position"
      strCol "referenced_table_schema"
      strCol "referenced_table_name"
      strCol "referenced_column_name" ]

let private keyColumnUsageRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.collect (fun (dbName, t) ->
        let pkRows =
            t.Columns
            |> List.filter (fun c -> c.PrimaryKey)
            |> List.mapi (fun i c ->
                [| vs dbName; vs "PRIMARY"; vs dbName; vs t.OriginalName; vs c.Name; vi (i + 1); VNull; VNull; VNull |])

        let fkRows =
            t.ForeignKeys
            |> List.collect (fun fk ->
                fk.Columns
                |> List.mapi (fun i colName ->
                    let refCol = fk.RefColumns |> List.tryItem i |> Option.defaultValue ""

                    [| vs dbName
                       vs fk.Name
                       vs dbName
                       vs t.OriginalName
                       vs colName
                       vi (i + 1)
                       vs dbName
                       vs fk.RefTable
                       vs refCol |]))

        pkRows @ fkRows)

let private referentialConstraintsColumns =
    [ strCol "constraint_schema"
      strCol "constraint_name"
      strCol "unique_constraint_schema"
      strCol "unique_constraint_name"
      strCol "update_rule"
      strCol "delete_rule"
      strCol "table_name"
      strCol "referenced_table_name" ]

let private referentialConstraintsRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.collect (fun (dbName, t) ->
        t.ForeignKeys
        |> List.map (fun fk ->
            [| vs dbName
               vs fk.Name
               vs dbName
               vs "PRIMARY"
               // MySQL's actual default when a `FOREIGN KEY` declares no
               // `ON UPDATE`/`ON DELETE` clause is `NO ACTION`, not
               // `RESTRICT` (the two enforce identically, but
               // `information_schema` reports the former).
               vs (fk.OnUpdate |> Option.defaultValue "NO ACTION")
               vs (fk.OnDelete |> Option.defaultValue "NO ACTION")
               vs t.OriginalName
               vs fk.RefTable |]))

let private tableConstraintsColumns =
    [ strCol "constraint_catalog"
      strCol "constraint_schema"
      strCol "constraint_name"
      strCol "table_schema"
      strCol "table_name"
      strCol "constraint_type"
      strCol "enforced" ]

/// One row per named `PRIMARY KEY`/`UNIQUE`/`FOREIGN KEY` constraint —
/// unlike `STATISTICS` (above), a plain non-unique `INDEX` has no row here,
/// matching real MySQL: an index and a constraint are different things,
/// and only the latter three kinds are constraints. `Migrations` code that
/// probes "does this named constraint still exist" before dropping it
/// (e.g. after a column rename left a foreign key's autogenerated name
/// stale) is what this exists for.
let private tableConstraintsRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.collect (fun (dbName, t) ->
        let pkRows =
            if t.Columns |> List.exists (fun c -> c.PrimaryKey) then
                [ [| vs "def"; vs dbName; vs "PRIMARY"; vs dbName; vs t.OriginalName; vs "PRIMARY KEY"; vs "YES" |] ]
            else
                []

        let uniqueRows =
            t.Indexes
            |> List.filter (fun ix -> ix.Unique)
            |> List.map (fun ix ->
                [| vs "def"; vs dbName; vs ix.Name; vs dbName; vs t.OriginalName; vs "UNIQUE"; vs "YES" |])

        let fkRows =
            t.ForeignKeys
            |> List.map (fun fk ->
                [| vs "def"; vs dbName; vs fk.Name; vs dbName; vs t.OriginalName; vs "FOREIGN KEY"; vs "YES" |])

        pkRows @ uniqueRows @ fkRows)

let private schemataColumns =
    [ strCol "catalog_name"
      strCol "schema_name"
      strCol "default_character_set_name"
      strCol "default_collation_name" ]

let private schemataRows (catalog: Catalog) : Value[] list =
    let real = catalog |> Map.toList |> List.map fst

    "information_schema" :: real
    |> List.distinct
    |> List.map (fun dbName -> [| vs "def"; vs dbName; vs "utf8mb4"; vs "utf8mb4_unicode_ci" |])

let private collationCharacterSetApplicabilityColumns =
    [ strCol "collation_name"; strCol "character_set_name" ]

/// Real MySQL's version lists all ~280 collations the server ships with,
/// regardless of whether anything actually uses them. This only needs to
/// cover collations fsdb itself can ever hand back (`tablesRows`'s/
/// `columnsRows`'s hardcoded `utf8mb4_unicode_ci`, the session defaults in
/// `Session.defaultVariables`, and MySQL 8's own server default) — enough
/// for Doctrine DBAL's `getListTableMetadataSQL`
/// (`... JOIN information_schema.COLLATION_CHARACTER_SET_APPLICABILITY
/// ccsa ON ccsa.COLLATION_NAME = t.TABLE_COLLATION`, behind Laravel's
/// `Blueprint::change()`) to find a match, not a full reference table.
let private collationCharacterSetApplicabilityRows: Value[] list =
    [ "utf8mb4_unicode_ci", "utf8mb4"
      "utf8mb4_general_ci", "utf8mb4"
      "utf8mb4_bin", "utf8mb4"
      "utf8mb4_0900_ai_ci", "utf8mb4" ]
    |> List.map (fun (collation, charset) -> [| vs collation; vs charset |])

/// The character sets fsdb knows about, with MySQL 8.4's defaults — the
/// metadata a schema-compare tool (Doctrine's platform setup, DB-browser
/// dropdowns) reads to learn which charsets exist and what each defaults
/// to.
let private characterSetsColumns =
    [ strCol "CHARACTER_SET_NAME"
      strCol "DEFAULT_COLLATE_NAME"
      strCol "DESCRIPTION"
      strCol "MAXLEN" ]

let private characterSetsRows: Value[] list =
    [ "utf8mb4", "utf8mb4_0900_ai_ci", "UTF-8 Unicode", "4"
      "latin1", "latin1_swedish_ci", "cp1252 West European", "1"
      "ascii", "ascii_general_ci", "US ASCII", "1"
      "binary", "binary", "Binary pseudo charset", "1" ]
    |> List.map (fun (cs, defaultCollation, description, maxlen) ->
        [| vs cs; vs defaultCollation; vs description; vs maxlen |])

/// One row per registered collation — name, its utf8mb4 charset, MySQL's
/// real id/sortlen (`Collation.idAndSortlen`), and its pad attribute —
/// shared by the `information_schema.COLLATIONS` view and `SHOW COLLATION`
/// so the two can't disagree.
let private registeredCollationRows : (string * string * int * int * string) list =
    Collation.registry
    |> Map.toList
    |> List.map (fun (name, col) ->
        let id, sortlen = Collation.idAndSortlen |> Map.tryFind name |> Option.defaultValue (0, 0)
        name, "utf8mb4", id, sortlen, (if col.PadSpace then "PAD SPACE" else "NO PAD"))

let private collationsColumns =
    [ strCol "COLLATION_NAME"
      strCol "CHARACTER_SET_NAME"
      strCol "ID"
      strCol "IS_DEFAULT"
      strCol "IS_COMPILED"
      strCol "SORTLEN"
      strCol "PAD_ATTRIBUTE" ]

let private collationsRows: Value[] list =
    registeredCollationRows
    |> List.map (fun (name, charset, id, sortlen, pad) ->
        [| vs name
           vs charset
           vs (string id)
           vs (if name = "utf8mb4_0900_ai_ci" then "Yes" else "")
           vs "Yes"
           vs (string sortlen)
           vs pad |])

/// Resolves one `information_schema` table name (case-insensitive) to its
/// columns and freshly-projected rows, or `None` if `name` isn't one of the
/// virtual tables this module knows about (a real 1146 from `Executor`, same
/// as any other unknown table).
let scan (catalog: Catalog) (name: string) : (ColumnDef list * Value[] list) option =
    match name.ToUpperInvariant() with
    | "TABLES" -> Some(tablesColumns, tablesRows catalog)
    | "COLUMNS" -> Some(columnsColumns, columnsRows catalog)
    | "STATISTICS" -> Some(statisticsColumns, statisticsRows catalog)
    | "KEY_COLUMN_USAGE" -> Some(keyColumnUsageColumns, keyColumnUsageRows catalog)
    | "REFERENTIAL_CONSTRAINTS" -> Some(referentialConstraintsColumns, referentialConstraintsRows catalog)
    | "TABLE_CONSTRAINTS" -> Some(tableConstraintsColumns, tableConstraintsRows catalog)
    | "COLLATION_CHARACTER_SET_APPLICABILITY" ->
        Some(collationCharacterSetApplicabilityColumns, collationCharacterSetApplicabilityRows)
    | "COLLATIONS" -> Some(collationsColumns, collationsRows)
    | "CHARACTER_SETS" -> Some(characterSetsColumns, characterSetsRows)
    | "SCHEMATA" -> Some(schemataColumns, schemataRows catalog)
    | _ -> None

// ---------------------------------------------------------------------------
// `SHOW TABLES / DATABASES / COLUMNS / CREATE TABLE / INDEX / TABLE STATUS`,
// and `DESCRIBE` — MySQL's older, differently-shaped sibling of the
// `information_schema` views above. `QueryHandler` still owns recognizing
// these forms by text probe (regex over the raw SQL) and extracting their
// arguments (db/table/LIKE pattern); once it has those, everything here is
// "given a `Catalog` and already-parsed arguments, render the
// `(columns, text rows)` `Executor.QueryResult.ResultSet` wants, or the
// `(code, message)` an unknown database/table fails with" — colocated with
// `information_schema`'s own row-builders (`tablesRows`/`columnsRows`/
// `statisticsRows` above) since it's the same catalog-introspection job
// under MySQL's other output shape, reusing the same per-column formatting
// helpers (`columnTypeText`/`columnKey`/`defaultText`/`isStringy`) rather
// than a second copy of them.
// ---------------------------------------------------------------------------

/// A `SHOW ...`/`DESCRIBE` result, ready for `QueryHandler` to lift straight
/// into `ResultSet`/`Err`.
type ShowResult = Result<string list * (string option list) list, int * string>

/// Optional case-insensitive `LIKE 'pattern'` filter shared by every `SHOW
/// ...` rendering function below — `None` (no `LIKE` clause given) always
/// matches.
let private likeFilter (likeOpt: string option) (name: string) : bool =
    match likeOpt with
    | None -> true
    | Some pattern -> Regex.IsMatch(name, likeToRegex pattern, RegexOptions.IgnoreCase ||| RegexOptions.Singleline)

/// Looks a table up straight off the catalog (rather than through
/// `Storage.scan`, which only hands back columns/rows) since `SHOW
/// COLUMNS`/`SHOW CREATE TABLE`/`SHOW INDEX` all need the whole
/// `Storage.Table` — indexes and foreign keys included, not just its column
/// list.
let findTable (catalog: Catalog) (dbName: string) (tableName: string) : Result<Table, int * string> =
    match Map.tryFind dbName catalog with
    | None -> Error(1049, sprintf "Unknown database '%s'" dbName)
    | Some db ->
        match Map.tryFind (tableName.ToLowerInvariant()) db with
        | Some t -> Ok t
        | None -> Error(1146, sprintf "Table '%s' doesn't exist" tableName)

/// `SHOW [FULL] TABLES [FROM db] [LIKE 'pattern']`.
let showTables (catalog: Catalog) (dbName: string) (full: bool) (likeOpt: string option) : ShowResult =
    match Map.tryFind dbName catalog with
    | None -> Error(1049, sprintf "Unknown database '%s'" dbName)
    | Some db ->
        let names =
            db |> Map.toList |> List.map (fun (_, t) -> t.OriginalName) |> List.filter (likeFilter likeOpt) |> List.sort

        let col = sprintf "Tables_in_%s" dbName

        if full then
            Ok([ col; "Table_type" ], names |> List.map (fun n -> [ Some n; Some "BASE TABLE" ]))
        else
            Ok([ col ], names |> List.map (fun n -> [ Some n ]))

/// `SHOW COLLATION [LIKE 'pattern']` — the registered collations with
/// `SHOW`'s column labels (MySQL's `Collation/Charset/Id/...`, distinct
/// from `information_schema.COLLATIONS`' `collation_name/...`).
let showCollation (likeOpt: string option) : ShowResult =
    let rows =
        registeredCollationRows
        |> List.filter (fun (name, _, _, _, _) -> likeFilter likeOpt name)
        |> List.sortBy (fun (name, _, _, _, _) -> name)
        |> List.map (fun (name, charset, id, sortlen, pad) ->
            [ Some name
              Some charset
              Some(string id)
              Some(if name = "utf8mb4_0900_ai_ci" then "Yes" else "")
              Some "Yes"
              Some(string sortlen)
              Some pad ])

    Ok(
        [ "Collation"; "Charset"; "Id"; "Default"; "Compiled"; "Sortlen"; "Pad_attribute" ],
        rows
    )

/// `SHOW DATABASES [LIKE 'pattern']`.
let showDatabases (catalog: Catalog) (likeOpt: string option) : string list * (string option list) list =
    let names =
        "information_schema" :: (catalog |> Map.toList |> List.map fst)
        |> List.distinct
        |> List.filter (likeFilter likeOpt)
        |> List.sort

    [ "Database" ], names |> List.map (fun n -> [ Some n ])

/// `SHOW [FULL] COLUMNS FROM t [FROM db] [LIKE 'pattern']` and
/// `DESCRIBE`/`DESC t` (which are just `SHOW COLUMNS`'s narrower 5-column
/// form under a different name).
let showColumns (catalog: Catalog) (full: bool) (dbName: string) (tableName: string) (likeOpt: string option) : ShowResult =
    findTable catalog dbName tableName
    |> Result.map (fun t ->
        let isNullable (c: ColumnDef) = if c.PrimaryKey || not c.Nullable then "NO" else "YES"
        let defaultCol (c: ColumnDef) = defaultText c.Default
        let extra = extraText

        let cols = t.Columns |> List.filter (fun c -> likeFilter likeOpt c.Name)

        if full then
            let rows =
                cols
                |> List.map (fun c ->
                    [ Some c.Name
                      Some(columnTypeText c.Type)
                      // The column's declared/inherited collation —
                      // matching `information_schema.columns.collation_name`
                      // (`columnsRows` above).
                      (if isStringy c.Type then Some(c.Collation |> Option.defaultValue "utf8mb4_0900_ai_ci") else None)
                      Some(isNullable c)
                      Some(columnKey t c)
                      defaultCol c
                      Some(extra c)
                      Some "select,insert,update,references"
                      Some "" ])

            [ "Field"; "Type"; "Collation"; "Null"; "Key"; "Default"; "Extra"; "Privileges"; "Comment" ], rows
        else
            let rows =
                cols
                |> List.map (fun c ->
                    [ Some c.Name; Some(columnTypeText c.Type); Some(isNullable c); Some(columnKey t c); defaultCol c; Some(extra c) ])

            [ "Field"; "Type"; "Null"; "Key"; "Default"; "Extra" ], rows)

let private backtick (s: string) = "`" + s + "`"
let private backtickCols = List.map backtick >> String.concat ", "

/// Reconstructs plausible `CREATE TABLE` DDL from a table's stored metadata
/// for `SHOW CREATE TABLE` — not the original DDL text (nothing keeps that
/// around), a fresh rendering of the same columns/indexes/foreign keys, the
/// same way real MySQL's `SHOW CREATE TABLE` itself re-derives its output
/// from the catalog rather than echoing verbatim source.
let private showCreateTableDDL (t: Table) : string =
    let columnLine (c: ColumnDef) =
        let notNull = if c.PrimaryKey || not c.Nullable then "NOT NULL" else ""

        // MySQL prints no DEFAULT clause at all on a generated column.
        let generatedPart =
            match c.Generated with
            | Some(e, k) -> sprintf "GENERATED ALWAYS AS (%s) %s" (exprToSql e) (match k with Virtual -> "VIRTUAL" | Stored -> "STORED")
            | None -> ""

        let defaultPart =
            match defaultText c.Default with
            | _ when c.Generated.IsSome -> ""
            | Some d when c.Default = Some DCurrentTimestamp -> sprintf "DEFAULT %s" d
            | Some d -> sprintf "DEFAULT '%s'" d
            | None -> if c.PrimaryKey || not c.Nullable then "" else "DEFAULT NULL"

        let onUpdatePart =
            if c.OnUpdateCurrentTimestamp then
                sprintf "ON UPDATE CURRENT_TIMESTAMP%s" (onUpdateFspSuffix c)
            else
                ""

        let extra = if c.AutoIncrement then "AUTO_INCREMENT" else ""

        // MySQL renders both CHARACTER SET and COLLATE whenever a column
        // declares either (verified: a column with only COLLATE utf8mb4_bin
        // shows `CHARACTER SET utf8mb4 COLLATE utf8mb4_bin`) — and never
        // on non-string columns (an INT column shows plain `id int` even
        // under a table-level COLLATE). ponytail: a charset/collation the
        // column merely *inherits* from the table's declaration renders
        // here too, where MySQL shows only the collation when it differs
        // from the charset's default (`varchar(10) COLLATE utf8mb4_unicode_ci`
        // without the `CHARACTER SET`) — semantically equivalent DDL, since
        // baking it into the column is exactly what the parser does.
        let charsetCollate =
            if not (isStringy c.Type) then
                []
            else
                match c.Charset, c.Collation with
                // The baked-in server default renders as nothing, exactly
                // like MySQL's plain `varchar(20)` — the column declared
                // neither a charset nor a collation and inherited nothing.
                | None, Some "utf8mb4_0900_ai_ci" -> []
                | None, None -> []
                | cs, col -> [ sprintf "CHARACTER SET %s" (cs |> Option.defaultValue "utf8mb4"); sprintf "COLLATE %s" (col |> Option.defaultValue "utf8mb4_0900_ai_ci") ]

        [ backtick c.Name; columnTypeText c.Type ]
        @ charsetCollate
        @ [ generatedPart; notNull; defaultPart; onUpdatePart; extra ]
        |> List.filter ((<>) "")
        |> String.concat " "

    let pkCols = t.Columns |> List.filter (fun c -> c.PrimaryKey) |> List.map (fun c -> c.Name)
    let pkLine = if pkCols.IsEmpty then [] else [ sprintf "PRIMARY KEY (%s)" (backtickCols pkCols) ]

    let indexLines =
        t.Indexes
        |> List.map (fun ix -> sprintf "%sKEY %s (%s)" (if ix.Unique then "UNIQUE " else "") (backtick ix.Name) (backtickCols ix.Columns))

    // The table's own declared defaults (server defaults when unset) —
    // MySQL renders these in the table options even when a column carries
    // its own COLLATE.
    let tableCharset = t.TableCharset |> Option.defaultValue "utf8mb4"
    let tableCollation = t.TableCollation |> Option.defaultValue "utf8mb4_0900_ai_ci"

    let fkLines =
        t.ForeignKeys
        |> List.map (fun fk ->
            let onDelete = fk.OnDelete |> Option.map (sprintf " ON DELETE %s") |> Option.defaultValue ""
            let onUpdate = fk.OnUpdate |> Option.map (sprintf " ON UPDATE %s") |> Option.defaultValue ""

            sprintf
                "CONSTRAINT %s FOREIGN KEY (%s) REFERENCES %s (%s)%s%s"
                (backtick fk.Name)
                (backtickCols fk.Columns)
                (backtick fk.RefTable)
                (backtickCols fk.RefColumns)
                onDelete
                onUpdate)

    let lines = (t.Columns |> List.map columnLine) @ pkLine @ indexLines @ fkLines

    sprintf
        "CREATE TABLE %s (\n  %s\n) ENGINE=InnoDB DEFAULT CHARSET=%s COLLATE=%s"
        (backtick t.OriginalName)
        (String.concat ",\n  " lines)
        tableCharset
        tableCollation

/// `SHOW CREATE TABLE t`.
let showCreateTable (catalog: Catalog) (dbName: string) (tableName: string) : ShowResult =
    findTable catalog dbName tableName
    |> Result.map (fun t -> [ "Table"; "Create Table" ], [ [ Some t.OriginalName; Some(showCreateTableDDL t) ] ])

/// `SHOW INDEX|INDEXES|KEYS FROM t [FROM db]` — one row per index column,
/// same shape `STATISTICS` (above) projects, just scoped to one table and
/// under `SHOW`'s own (differently-cased) column names.
let showIndex (catalog: Catalog) (dbName: string) (tableName: string) : ShowResult =
    findTable catalog dbName tableName
    |> Result.map (fun t ->
        let pkCols = t.Columns |> List.filter (fun c -> c.PrimaryKey) |> List.map (fun c -> c.Name)
        let primaryIndex = if pkCols.IsEmpty then [] else [ { Name = "PRIMARY"; Columns = pkCols; Unique = true } ]

        let rows =
            primaryIndex @ t.Indexes
            |> List.collect (fun ix ->
                ix.Columns
                |> List.mapi (fun i colName ->
                    [ Some t.OriginalName
                      Some(if ix.Unique then "0" else "1")
                      Some ix.Name
                      Some(string (i + 1))
                      Some colName
                      Some "A"
                      Some "0"
                      None
                      None
                      Some "YES"
                      Some "BTREE"
                      Some ""
                      Some "" ]))

        [ "Table"
          "Non_unique"
          "Key_name"
          "Seq_in_index"
          "Column_name"
          "Collation"
          "Cardinality"
          "Sub_part"
          "Packed"
          "Null"
          "Index_type"
          "Comment"
          "Index_comment" ],
        rows)

/// `SHOW TABLE STATUS [FROM db] [LIKE 'pattern']`.
let showTableStatus (catalog: Catalog) (dbName: string) (likeOpt: string option) : ShowResult =
    match Map.tryFind dbName catalog with
    | None -> Error(1049, sprintf "Unknown database '%s'" dbName)
    | Some db ->
        let rows =
            db
            |> Map.toList
            |> List.map snd
            |> List.filter (fun t -> likeFilter likeOpt t.OriginalName)
            |> List.sortBy (fun t -> t.OriginalName)
            |> List.map (fun t ->
                // `Data_length`/`Avg_row_length` are the rows' actual
                // in-memory text payload size — this engine has no 16 KiB
                // pages, so real bytes beat InnoDB's page-count fiction.
                let dataLength =
                    t.RowsArray
                    |> Seq.sumBy (fun r -> r |> Array.sumBy (fun v -> (Value.toText v |> Option.map String.length |> Option.defaultValue 0) + 1))

                let avgRowLength = if t.RowsArray.Length = 0 then 0 else dataLength / t.RowsArray.Length

                [ Some t.OriginalName
                  Some "InnoDB"
                  Some "10"
                  Some "Dynamic"
                  Some(string (t.RowsArray.Length))
                  Some(string avgRowLength)
                  Some(string dataLength)
                  Some "0"
                  Some "0"
                  Some "0"
                  (if t.Columns |> List.exists (fun c -> c.AutoIncrement) then Some(string t.NextAutoId) else None)
                  None
                  None
                  None
                  Some(t.TableCollation |> Option.defaultValue "utf8mb4_0900_ai_ci")
                  None
                  Some ""
                  Some "" ])

        Ok(
            [ "Name"
              "Engine"
              "Version"
              "Row_format"
              "Rows"
              "Avg_row_length"
              "Data_length"
              "Max_data_length"
              "Index_length"
              "Data_free"
              "Auto_increment"
              "Create_time"
              "Update_time"
              "Check_time"
              "Collation"
              "Checksum"
              "Create_options"
              "Comment" ],
            rows
        )
