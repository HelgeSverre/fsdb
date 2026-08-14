/// `information_schema` as virtual tables projected from `Storage.Catalog`
/// at query time — nothing here is ever materialized into the catalog
/// itself; `scan` below builds a table's columns/rows fresh from whatever
/// the catalog looks like right now. Wired into `Executor.execute`'s
/// `Select` case: a `FROM information_schema.<table>` (case-insensitive,
/// qualified or via `USE information_schema`) resolves here instead of
/// `Storage.scan`.
module Fsdb.InformationSchema

open System
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
      Unique = false }

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
    | TDateTime -> "datetime"
    | TTimestamp -> "timestamp"
    | TTime -> "time"
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
    | TDateTime -> "datetime"
    | TTimestamp -> "timestamp"
    | TTime -> "time"
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
      intCol "index_length" ]

let private tablesRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.map (fun (dbName, t) ->
        [| vs dbName
           vs t.OriginalName
           vs "BASE TABLE"
           vs "InnoDB"
           vi (List.length t.Rows)
           VInt t.NextAutoId
           vs "utf8mb4_unicode_ci"
           vs ""
           vi 16384
           vi 0 |])

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
      strCol "column_key"
      strCol "extra"
      strCol "collation_name"
      // `Ast.ColumnDef` doesn't track a column comment or a generated-column
      // expression — both are always empty/NULL, present only so Laravel's
      // `compileColumns` (which projects them unconditionally) doesn't 1054.
      strCol "column_comment"
      strCol "generation_expression" ]

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
               vs (columnKey t c)
               vs (if c.AutoIncrement then "auto_increment" else "")
               (if isStringy c.Type then vs "utf8mb4_unicode_ci" else VNull)
               vs ""
               vs "" |]))

let private statisticsColumns =
    [ strCol "table_schema"
      strCol "table_name"
      intCol "non_unique"
      strCol "index_name"
      intCol "seq_in_index"
      strCol "column_name"
      strCol "collation"
      intCol "cardinality"
      strCol "index_type" ]

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
                   vs "BTREE" |])))

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
               vs (fk.OnUpdate |> Option.defaultValue "RESTRICT")
               vs (fk.OnDelete |> Option.defaultValue "RESTRICT")
               vs t.OriginalName
               vs fk.RefTable |]))

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
    | "SCHEMATA" -> Some(schemataColumns, schemataRows catalog)
    | _ -> None
