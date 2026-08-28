/// `information_schema` virtual tables projected from `Storage.Catalog` at
/// query time. `FROM information_schema.<table>` resolves here instead of
/// materializing metadata in the catalog.
module Fsdb.InformationSchema

open System
open System.Text.RegularExpressions
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Engine

let private col (name: string) (ty: ColumnType) : ColumnDef =
    { Name = name
      Type = ty
      Nullable = true
      Default = None
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      Generated = None
      Comment = ""
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

let private mysqlTable (catalog: Catalog) table =
    catalog |> Map.tryFind "mysql" |> Option.bind (Map.tryFind table)

/// Purely resolves a stored view's output shape. The executor supplies this
/// because it owns view-definition parsing and expression type inference.
type ViewColumns = string -> string -> ColumnDef list option

let private viewCatalogEntries (catalog: Catalog) : SystemCatalog.View.Entry list =
    mysqlTable catalog "views"
    |> Option.map (fun table ->
        table.RowsArray
        |> Seq.choose SystemCatalog.View.tryRead
        |> List.ofSeq)
    |> Option.defaultValue []

/// MySQL's `information_schema.columns.data_type` — the bare type name,
/// no length/unsigned/precision.
let private dataTypeName (ty: ColumnType) : string =
    match ty with
    | TTinyInt _
    | TBool -> "tinyint"
    | TSmallInt _ -> "smallint"
    | TMediumInt _ -> "mediumint"
    | TInt _ -> "int"
    | TBigInt _ -> "bigint"
    | TBit _ -> "bit"
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
    | TDouble _ -> "double"
    | TFloat _ -> "float"
    | TDate -> "date"
    // `data_type` is the bare name — the `(N)` fsp only shows up in
    // `column_type` below, never here (MySQL-verified).
    | TDateTime _ -> "datetime"
    | TTimestamp _ -> "timestamp"
    | TTime _ -> "time"
    | TYear -> "year"
    | TJson -> "json"
    | TGeometry GeometryCollection -> "geomcollection"
    | TGeometry kind -> geometryTypeName kind |> _.ToLowerInvariant()
    | TVector _ -> "vector"

/// `information_schema.columns.column_type` — the full declared type text
/// (`int unsigned`, `varchar(255)`, `enum('a','b')`, ...), the same text
/// Laravel's `getColumns()`/`SHOW CREATE TABLE` echo back.
let columnTypeText (ty: ColumnType) : string =
    let quotedList vs = vs |> List.map (sprintf "'%s'") |> String.concat ","
    let unsigned u = if u then " unsigned" else ""

    match ty with
    | TTinyInt u -> "tinyint" + unsigned u
    // MySQL spells BOOLEAN back as the `tinyint(1)` it is a synonym for.
    | TBool -> "tinyint(1)"
    | TSmallInt u -> "smallint" + unsigned u
    | TMediumInt u -> "mediumint" + unsigned u
    | TInt u -> "int" + unsigned u
    | TBigInt u -> "bigint" + unsigned u
    | TBit width -> sprintf "bit(%d)" width
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
    | TDecimal(p, s, unsigned) ->
        sprintf "decimal(%d,%d)%s" p s (if unsigned then " unsigned" else "")
    | TDouble unsigned -> if unsigned then "double unsigned" else "double"
    | TFloat unsigned -> if unsigned then "float unsigned" else "float"
    | TDate -> "date"
    // `datetime(6)` when fsp > 0, bare `datetime` at fsp 0 — the exact
    // strings `SHOW COLUMNS`/`information_schema.columns.column_type` report
    // (MySQL-verified for all three temporal types).
    | TDateTime fsp -> if fsp > 0 then sprintf "datetime(%d)" fsp else "datetime"
    | TTimestamp fsp -> if fsp > 0 then sprintf "timestamp(%d)" fsp else "timestamp"
    | TTime fsp -> if fsp > 0 then sprintf "time(%d)" fsp else "time"
    | TYear -> "year(4)"
    | TJson -> "json"
    | TGeometry GeometryCollection -> "geomcollection"
    | TGeometry kind -> geometryTypeName kind |> _.ToLowerInvariant()
    // Always with the dimension — a bare `VECTOR` declaration reports its
    // implicit 2048, the way MySQL 9 echoes it back.
    | TVector dim -> sprintf "vector(%d)" dim

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
    | TTinyInt _
    | TBool -> Some(3L, 0L)
    | TSmallInt _ -> Some(5L, 0L)
    | TMediumInt _ -> Some(7L, 0L)
    | TInt _ -> Some(10L, 0L)
    | TBigInt _ -> Some(19L, 0L)
    | TBit width -> Some(int64 width, 0L)
    | TDecimal(p, s, _) -> Some(int64 p, int64 s)
    | TFloat _ -> Some(10L, 0L)
    | TDouble _ -> Some(22L, 0L)
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
/// the first column of the first declared unique/plain index that leads with
/// this column.
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

/// MySQL 8.4's full `TABLES` column set, in MySQL's order — clients
/// `SELECT *` here (phpMyAdmin's table-status query aliases every one of
/// these), so the shape must match, not just the columns fsdb has real
/// numbers for.
let private tablesColumns =
    [ strCol "TABLE_CATALOG"
      strCol "TABLE_SCHEMA"
      strCol "TABLE_NAME"
      strCol "TABLE_TYPE"
      strCol "ENGINE"
      intCol "VERSION"
      strCol "ROW_FORMAT"
      intCol "TABLE_ROWS"
      intCol "AVG_ROW_LENGTH"
      // Constant page-size stand-ins: this in-memory engine has no real
      // page storage to report, but Laravel's `compileTables` projects
      // `(data_length + index_length) as size`, so the columns must exist.
      intCol "DATA_LENGTH"
      intCol "MAX_DATA_LENGTH"
      intCol "INDEX_LENGTH"
      intCol "DATA_FREE"
      intCol "AUTO_INCREMENT"
      col "CREATE_TIME" (TDateTime 0)
      col "UPDATE_TIME" (TDateTime 0)
      col "CHECK_TIME" (TDateTime 0)
      strCol "TABLE_COLLATION"
      intCol "CHECKSUM"
      strCol "CREATE_OPTIONS"
      strCol "TABLE_COMMENT" ]

let private truncateToSecond (d: DateTime) = d.AddTicks(-(d.Ticks % TimeSpan.TicksPerSecond))

let private tablesRows (catalog: Catalog) : Value[] list =
    let baseTables =
        allTables catalog
        |> List.map (fun (dbName, t) ->
            // NULL, not 1 (`NextAutoId`'s idle starting value), for a table
            // that never declared an AUTO_INCREMENT column at all — matching
            // real MySQL, which only reports a next-value once a table actually
            // has one.
            let autoIncrement =
                if t.Columns |> List.exists (fun c -> c.AutoIncrement) then VInt t.NextAutoId else VNull

            [| vs "def"
               vs dbName
               vs t.OriginalName
               vs "BASE TABLE"
               vs "InnoDB"
               vi 10
               vs "Dynamic"
               vi (t.RowsArray.Length)
               vi 0
               vi 16384
               vi 0
               vi 0
               vi 0
               autoIncrement
               VDateTime(truncateToSecond t.CreateTime)
               // NULL like real InnoDB, which doesn't maintain them either.
               VNull
               VNull
               vs (t.TableCollation |> Option.defaultValue "utf8mb4_0900_ai_ci")
               VNull
               vs ""
               vs t.TableComment |])

    let views =
        viewCatalogEntries catalog
        |> List.map (fun view ->
            [| vs "def"; vs view.Schema; vs view.Name; vs "VIEW"; VNull; VNull; VNull; VNull; VNull; VNull; VNull
               VNull; VNull; VNull; (view.Created |> Option.map (truncateToSecond >> VDateTime) |> Option.defaultValue VNull)
               VNull; VNull; VNull; VNull; vs ""; vs "VIEW" |])

    baseTables @ views

/// MySQL 8.4's full `COLUMNS` column set, in MySQL's order — same
/// `SELECT *` contract as `tablesColumns`.
let private columnsColumns =
    [ strCol "TABLE_CATALOG"
      strCol "TABLE_SCHEMA"
      strCol "TABLE_NAME"
      strCol "COLUMN_NAME"
      intCol "ORDINAL_POSITION"
      strCol "COLUMN_DEFAULT"
      strCol "IS_NULLABLE"
      strCol "DATA_TYPE"
      intCol "CHARACTER_MAXIMUM_LENGTH"
      intCol "CHARACTER_OCTET_LENGTH"
      intCol "NUMERIC_PRECISION"
      intCol "NUMERIC_SCALE"
      intCol "DATETIME_PRECISION"
      // NULL for a non-string column, the charset/collation for a string
      // one — same split real MySQL makes.
      strCol "CHARACTER_SET_NAME"
      strCol "COLLATION_NAME"
      strCol "COLUMN_TYPE"
      strCol "COLUMN_KEY"
      strCol "EXTRA"
      strCol "PRIVILEGES"
      strCol "COLUMN_COMMENT"
      strCol "GENERATION_EXPRESSION"
      intCol "SRS_ID" ]

/// `CHARACTER_OCTET_LENGTH` — the byte capacity behind
/// `CHARACTER_MAXIMUM_LENGTH`: x4 for utf8mb4 CHAR/VARCHAR, equal for the
/// fixed-capacity TEXT/BLOB/BINARY families.
let private charOctetLength (ty: ColumnType) : int64 option =
    charMaxLength ty
    |> Option.map (fun n ->
        match ty with
        | TChar _
        | TVarchar _ -> n * 4L
        | _ -> n)

/// Renders a generated-column expression back to SQL for
/// `GENERATION_EXPRESSION` / `SHOW CREATE TABLE`, in MySQL's normalized
/// style: backticked column refs, paren-wrapped binops, lowercased function
/// names. Total over every `Expr` case — the subquery/window shapes can't
/// appear in a generated expression, but must render rather than throw.
/// String literals have no charset introducer (MySQL prints
/// `_latin1'x'`, this prints `'x'`) — add it if a tool ever diffs the text.
let rec exprToSql (e: Expr) : string =
    let opText =
        function
        | And -> "and"
        | Or -> "or"
        | Xor -> "xor"
        | Eq -> "="
        | Neq -> "<>"
        | Lt -> "<"
        | Lte -> "<="
        | Gt -> ">"
        | Gte -> ">="
        | Add -> "+"
        | Sub -> "-"
        | SignedSub -> "-"
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
    | MatchAgainst(cols, q, _) ->
        let columnSql column =
            column.Qualifier
            |> Option.map (fun qualifier -> sprintf "`%s`.`%s`" qualifier column.Name)
            |> Option.defaultWith (fun () -> sprintf "`%s`" column.Name)

        sprintf "match (%s) against (%s)" (cols |> List.map columnSql |> String.concat ",") (exprToSql q)
    | Placeholder _ -> "?"
    | UserVariable variable -> variable.Sql
    | SystemVariable(scope, name) -> "@@" + (scope |> Option.map (fun value -> value.ToLowerInvariant() + ".") |> Option.defaultValue "") + name
    | AssignUserVariable(variable, value) -> sprintf "%s := %s" variable.Sql (exprToSql value)
    | Col n -> sprintf "`%s`" n
    | QualifiedCol(t, c) -> sprintf "`%s`.`%s`" t c
    | Row values -> sprintf "(%s)" (values |> List.map exprToSql |> String.concat ",")
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
    | FuncCall(name, [ Cast(value, TChar length) ]) when name.Equals("WEIGHT_STRING", System.StringComparison.OrdinalIgnoreCase) ->
        sprintf "weight_string(%s as char(%d))" (exprToSql value) length
    | FuncCall(name, [ Cast(value, TBinary length) ]) when name.Equals("WEIGHT_STRING", System.StringComparison.OrdinalIgnoreCase) ->
        sprintf "weight_string(%s as binary(%d))" (exprToSql value) length
    | FuncCall(name, args) -> sprintf "%s(%s)" (name.ToLowerInvariant()) (args |> List.map exprToSql |> String.concat ",")
    | Distinct e -> sprintf "distinct %s" (exprToSql e)
    | OrderBy(e, _) -> exprToSql e
    | Cast(e, TBigInt true) -> sprintf "cast(%s as unsigned)" (exprToSql e)
    | Cast(e, TBigInt false) -> sprintf "cast(%s as signed)" (exprToSql e)
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
    | InSubquery _
    | QuantifiedComparison _ -> "(...)"
    // A window function can't appear in a generated-column expression at
    // all (MySQL rejects it at DDL time), so one spelling covers the case.
    | WindowOver _ -> "window function() over ()"

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
          if c.Default |> Option.exists (function DExpression _ -> true | _ -> false) then "DEFAULT_GENERATED"
          if c.OnUpdateCurrentTimestamp then
              sprintf "on update CURRENT_TIMESTAMP%s" (onUpdateFspSuffix c) ]
        |> String.concat " "

let private bitDefaultText (value: uint64) =
    let width = if value = 0UL then 1 else 64 - System.Numerics.BitOperations.LeadingZeroCount value

    Array.init width (fun index -> if value &&& (1UL <<< (width - index - 1)) = 0UL then '0' else '1')
    |> String
    |> sprintf "b'%s'"

let defaultText (c: ColumnDef) : string option =
    let bitValue =
        function
        | VBit(_, value)
        | VUInt value -> Some value
        | VInt value when value >= 0L -> Some(uint64 value)
        | VDecimal value when value >= 0m && value <= decimal UInt64.MaxValue ->
            Some(uint64 (Math.Round(value, 0, MidpointRounding.AwayFromZero)))
        | VDouble value when value >= 0.0 && value < 1.8446744073709552e19 ->
            Some(uint64 (Math.Round(value, 0, MidpointRounding.AwayFromZero)))
        | VBytes bytes -> Value.bitValue bytes
        | _ -> None

    match c.Type, c.Default with
    | TBit _, Some(DConst value) -> value |> bitValue |> Option.map bitDefaultText
    | _, None -> None
    | _, Some(DConst value) -> value |> toText
    | _, Some DCurrentTimestamp -> Some "CURRENT_TIMESTAMP"
    | _, Some(DExpression expression) -> Some("(" + exprToSql expression + ")")

/// One `COLUMNS` row — shared by real tables (with the table's key
/// metadata and full DML privileges) and information_schema's own
/// self-listing (no keys, select-only, like real SYSTEM VIEWs).
let private columnRowWith (privileges: string) (dbName: string) (tableName: string) (i: int) (key: string) (c: ColumnDef) : Value[] =
    let precision, scale = numericPrecisionScale c.Type |> Option.map (fun (p, s) -> Some p, Some s) |> Option.defaultValue (None, None)

    [| vs "def"
       vs dbName
       vs tableName
       vs c.Name
       vi (i + 1)
       vopt (defaultText c)
       // A primary key column is implicitly NOT NULL in MySQL even
       // without an explicit `NOT NULL` — `Ast.ColumnDef.Nullable`
       // only tracks the explicit modifier (`Storage`
       // itself still doesn't reject a NULL insert into an implicit
       // PK column; add that enforcement too if a migration's
       // assertions ever depend on it, not just this metadata view).
       vs (if c.PrimaryKey || not c.Nullable then "NO" else "YES")
       vs (dataTypeName c.Type)
       (charMaxLength c.Type |> Option.map VInt |> Option.defaultValue VNull)
       (charOctetLength c.Type |> Option.map VInt |> Option.defaultValue VNull)
       (precision |> Option.map VInt |> Option.defaultValue VNull)
       (scale |> Option.map VInt |> Option.defaultValue VNull)
       (datetimePrecision c.Type |> Option.map VInt |> Option.defaultValue VNull)
       (if isStringy c.Type then vs (c.Charset |> Option.defaultValue "utf8mb4") else VNull)
       (if isStringy c.Type then vs (c.Collation |> Option.defaultValue "utf8mb4_0900_ai_ci") else VNull)
       vs (columnTypeText c.Type)
       vs key
       vs (extraText c)
       vs privileges
       vs c.Comment
       vs (c.Generated |> Option.map (fst >> exprToSql) |> Option.defaultValue "")
       VNull |]

let private columnRow = columnRowWith "select,insert,update,references"

let private columnsRows (catalog: Catalog) (viewColumns: ViewColumns option) : Value[] list =
    let baseRows =
        allTables catalog
        |> List.collect (fun (dbName, t) ->
            t.Columns |> List.mapi (fun i c -> columnRow dbName t.OriginalName i (columnKey t c) c))

    let viewRows =
        match viewColumns with
        | None -> []
        | Some resolve ->
            viewCatalogEntries catalog
            |> List.collect (fun view ->
                resolve view.Schema view.Name
                |> Option.defaultValue []
                |> List.mapi (fun i c -> columnRow view.Schema view.Name i "" c))

    baseRows @ viewRows

let private statisticsColumns =
    [ strCol "TABLE_CATALOG"
      strCol "TABLE_SCHEMA"
      strCol "TABLE_NAME"
      intCol "NON_UNIQUE"
      strCol "INDEX_SCHEMA"
      strCol "INDEX_NAME"
      intCol "SEQ_IN_INDEX"
      strCol "COLUMN_NAME"
      strCol "COLLATION"
      intCol "CARDINALITY"
      intCol "SUB_PART"
      strCol "PACKED"
      strCol "NULLABLE"
      strCol "INDEX_TYPE"
      strCol "COMMENT"
      strCol "INDEX_COMMENT"
      strCol "IS_VISIBLE"
      strCol "EXPRESSION" ]

let private isPrimaryIndex (index: IndexDef) =
    String.Equals(index.Name, "PRIMARY", StringComparison.OrdinalIgnoreCase)

let private indexesIncludingPrimary (table: Table) =
    if table.Indexes |> List.exists isPrimaryIndex then
        table.Indexes
    else
        match Storage.primaryKeyColumns table with
        | [] -> table.Indexes
        | columns ->
            { Name = "PRIMARY"
              KeyColumns = columns |> List.map (fun name -> { Name = name; PrefixLength = None; Transform = None })
              Unique = true
              Kind = BTree }
            :: table.Indexes

let private effectivePrefixLength (table: Table) (keyColumn: IndexColumn) =
    match keyColumn.PrefixLength with
    | None -> None
    | Some prefix ->
        table.Columns
        |> List.tryFind (fun column -> column.Name.Equals(keyColumn.Name, StringComparison.OrdinalIgnoreCase))
        |> Option.bind (fun column ->
            match column.Type with
            | TChar length
            | TVarchar length
            | TBinary length
            | TVarBinary length when prefix >= length -> None
            | _ -> Some prefix)

let private indexExpression (keyColumn: IndexColumn) =
    match keyColumn.Transform with
    | Some Lowercase -> Some(sprintf "lower(`%s`)" (keyColumn.Name.Replace("`", "``")))
    | Some(Expression expression) -> Some(exprToSql expression)
    | None -> None

/// One row per `(index, column)` pair.
let private statisticsRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.collect (fun (dbName, t) ->
        indexesIncludingPrimary t
        |> List.collect (fun ix ->
            ix.KeyColumns
            |> List.mapi (fun i keyColumn ->
                let expression = indexExpression keyColumn
                let colName = if expression.IsSome then VNull else vs keyColumn.Name
                let nullable =
                    t.Columns
                    |> List.tryFind (fun c -> c.Name = keyColumn.Name)
                    |> Option.map (fun c -> if c.PrimaryKey || not c.Nullable then "" else "YES")
                    |> Option.defaultValue ""

                // A FULLTEXT entry reports no sort collation and type
                // FULLTEXT, like real MySQL's STATISTICS rows.
                [| vs "def"
                   vs dbName
                   vs t.OriginalName
                   vi (if ix.Unique then 0 else 1)
                   vs dbName
                   vs ix.Name
                   vi (i + 1)
                   colName
                   (if ix.Kind = FullTextIndex then VNull else vs "A")
                   vi 0
                   (effectivePrefixLength t keyColumn |> Option.map vi |> Option.defaultValue VNull)
                   VNull
                   vs nullable
                   vs (if ix.Kind = FullTextIndex then "FULLTEXT" else "BTREE")
                   vs ""
                   vs ""
                   vs "YES"
                   (expression |> Option.map vs |> Option.defaultValue VNull) |])))

let private keyColumnUsageColumns =
    [ strCol "CONSTRAINT_CATALOG"
      strCol "CONSTRAINT_SCHEMA"
      strCol "CONSTRAINT_NAME"
      strCol "TABLE_CATALOG"
      strCol "TABLE_SCHEMA"
      strCol "TABLE_NAME"
      strCol "COLUMN_NAME"
      intCol "ORDINAL_POSITION"
      intCol "POSITION_IN_UNIQUE_CONSTRAINT"
      strCol "REFERENCED_TABLE_SCHEMA"
      strCol "REFERENCED_TABLE_NAME"
      strCol "REFERENCED_COLUMN_NAME" ]

let private keyColumnUsageRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.collect (fun (dbName, t) ->
        let pkRows =
            Storage.primaryKeyColumns t
            |> List.mapi (fun i columnName ->
                [| vs "def"
                   vs dbName
                   vs "PRIMARY"
                   vs "def"
                   vs dbName
                   vs t.OriginalName
                   vs columnName
                   vi (i + 1)
                   VNull
                   VNull
                   VNull
                   VNull |])

        let fkRows =
            t.ForeignKeys
            |> List.collect (fun fk ->
                fk.Columns
                |> List.mapi (fun i colName ->
                    let refCol = fk.RefColumns |> List.tryItem i |> Option.defaultValue ""

                    [| vs "def"
                       vs dbName
                       vs fk.Name
                       vs "def"
                       vs dbName
                       vs t.OriginalName
                       vs colName
                       vi (i + 1)
                       vi (i + 1)
                       vs dbName
                       vs fk.RefTable
                       vs refCol |]))

        pkRows @ fkRows)

let private referentialConstraintsColumns =
    [ strCol "CONSTRAINT_CATALOG"
      strCol "CONSTRAINT_SCHEMA"
      strCol "CONSTRAINT_NAME"
      strCol "UNIQUE_CONSTRAINT_CATALOG"
      strCol "UNIQUE_CONSTRAINT_SCHEMA"
      strCol "UNIQUE_CONSTRAINT_NAME"
      strCol "MATCH_OPTION"
      strCol "UPDATE_RULE"
      strCol "DELETE_RULE"
      strCol "TABLE_NAME"
      strCol "REFERENCED_TABLE_NAME" ]

let private referentialConstraintsRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.collect (fun (dbName, t) ->
        t.ForeignKeys
        |> List.map (fun fk ->
            [| vs "def"
               vs dbName
               vs fk.Name
               vs "def"
               vs dbName
               vs "PRIMARY"
               vs "NONE"
               // MySQL's actual default when a `FOREIGN KEY` declares no
               // `ON UPDATE`/`ON DELETE` clause is `NO ACTION`, not
               // `RESTRICT` (the two enforce identically, but
               // `information_schema` reports the former).
               vs (fk.OnUpdate |> Option.defaultValue "NO ACTION")
               vs (fk.OnDelete |> Option.defaultValue "NO ACTION")
               vs t.OriginalName
               vs fk.RefTable |]))

let private tableConstraintsColumns =
    [ strCol "CONSTRAINT_CATALOG"
      strCol "CONSTRAINT_SCHEMA"
      strCol "CONSTRAINT_NAME"
      strCol "TABLE_SCHEMA"
      strCol "TABLE_NAME"
      strCol "CONSTRAINT_TYPE"
      strCol "ENFORCED" ]

let private storedCheckRows (catalog: Catalog) : Value[] list =
    mysqlTable catalog "check_constraints"
    |> Option.map (fun table -> table.RowsArray |> List.ofSeq)
    |> Option.defaultValue []

let private checkConstraintsColumns =
    [ col "CONSTRAINT_CATALOG" (TVarchar 64)
      col "CONSTRAINT_SCHEMA" (TVarchar 64)
      { col "CONSTRAINT_NAME" (TVarchar 64) with Nullable = false }
      { col "CHECK_CLAUSE" TLongText with Nullable = false } ]

let private checkConstraintsRows (catalog: Catalog) : Value[] list =
    storedCheckRows catalog
    |> List.map (fun row -> [| vs "def"; row.[1]; row.[0]; row.[3] |])

/// One row per named `PRIMARY KEY`/`UNIQUE`/`FOREIGN KEY` constraint —
/// unlike `STATISTICS` (above), a plain non-unique `INDEX` has no row here,
/// matching real MySQL: an index and a constraint are different things,
/// and only the latter three kinds are constraints. `Migrations` code that
/// probes "does this named constraint still exist" before dropping it
/// (e.g. after a column rename left a foreign key's autogenerated name
/// stale) is what this exists for.
let private tableConstraintsRows (catalog: Catalog) : Value[] list =
    let structural =
        allTables catalog
        |> List.collect (fun (dbName, t) ->
        let pkRows =
            if Storage.primaryKeyColumns t |> List.isEmpty |> not then
                [ [| vs "def"; vs dbName; vs "PRIMARY"; vs dbName; vs t.OriginalName; vs "PRIMARY KEY"; vs "YES" |] ]
            else
                []

        let uniqueRows =
            t.Indexes
            |> List.filter (fun index -> index.Unique && not (isPrimaryIndex index))
            |> List.map (fun ix ->
                [| vs "def"; vs dbName; vs ix.Name; vs dbName; vs t.OriginalName; vs "UNIQUE"; vs "YES" |])

        let fkRows =
            t.ForeignKeys
            |> List.map (fun fk ->
                [| vs "def"; vs dbName; vs fk.Name; vs dbName; vs t.OriginalName; vs "FOREIGN KEY"; vs "YES" |])

        pkRows @ uniqueRows @ fkRows)

    let checks =
        storedCheckRows catalog
        |> List.map (fun row -> [| vs "def"; row.[1]; row.[0]; row.[1]; row.[2]; vs "CHECK"; row.[4] |])

    structural @ checks

let private schemataColumns =
    [ strCol "CATALOG_NAME"
      strCol "SCHEMA_NAME"
      strCol "DEFAULT_CHARACTER_SET_NAME"
      strCol "DEFAULT_COLLATION_NAME"
      strCol "SQL_PATH"
      strCol "DEFAULT_ENCRYPTION" ]

let private schemataRows (catalog: Catalog) : Value[] list =
    let real = catalog |> Map.toList |> List.map fst

    "information_schema" :: real
    |> List.distinct
    |> List.map (fun dbName -> [| vs "def"; vs dbName; vs "utf8mb4"; vs "utf8mb4_unicode_ci"; VNull; vs "NO" |])

let private collationCharacterSetApplicabilityColumns =
    [ strCol "COLLATION_NAME"; strCol "CHARACTER_SET_NAME" ]

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
    Collation.registry
    |> Map.toList
    |> List.map (fun (name, _) -> [| vs name; vs (Collation.charsetOfCollation name) |])

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
      "utf8mb3", "utf8mb3_general_ci", "UTF-8 Unicode", "3"
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
        name, Collation.charsetOfCollation name, id, sortlen, (if col.PadSpace then "PAD SPACE" else "NO PAD"))

/// Each charset's default collation, mirroring `characterSetsRows` — drives
/// `IS_DEFAULT` in the COLLATIONS view.
let private defaultCollationPerCharset =
    Set.ofList [ "utf8mb4_0900_ai_ci"; "utf8mb3_general_ci"; "latin1_swedish_ci"; "ascii_general_ci"; "binary" ]

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
           vs (if Set.contains name defaultCollationPerCharset then "Yes" else "")
           vs "Yes"
           vs (string sortlen)
           vs pad |])


// ---------------------------------------------------------------------------
// Live-connection registry — `Server` registers each connection here so
// `information_schema.PROCESSLIST`, `SHOW PROCESSLIST`, `Threads_connected`,
// and `KILL` report/act on real sessions instead of stubs.
// ---------------------------------------------------------------------------

type ProcessEntry =
    { Id: int64
      Account: Fsdb.Auth.Account
      User: string
      Host: string
      mutable Db: string option
      mutable Command: string
      mutable State: string
      mutable StateSince: DateTime
      mutable Info: string option
      /// Cancels the query currently executing on this connection, if any —
      /// `KILL QUERY`'s hook, wired by `Server` around each statement.
      mutable CancelQuery: (unit -> unit) option
      /// Tears the connection down — `KILL CONNECTION`'s hook.
      mutable CloseConnection: (unit -> unit) option }

let private processes = System.Collections.Concurrent.ConcurrentDictionary<int64, ProcessEntry>()

/// The store + user on whose behalf a SELECT into `information_schema` is
/// running, set by `QueryHandler` around statement execution. `None` (the
/// embedded/internal default) means unrestricted. PROCESSLIST and the
/// privilege views read it to scope rows to the caller unless the caller
/// holds the privilege that reveals everyone (`PROCESS`, or a mysql-schema
/// read for the grant views) — the same information hiding real MySQL does.
let currentViewer = System.Threading.AsyncLocal<(Store * Fsdb.Auth.Account) option>()

/// Runs `f` with the viewer scoped to `store`/`user`, restoring the previous
/// value afterwards — for probe-path SHOW handlers that build their result
/// outside the executor (which sets the viewer itself).
let withViewer (store: Store) (user: Fsdb.Auth.Account) (f: unit -> 'a) : 'a =
    let previous = currentViewer.Value
    currentViewer.Value <- Some(store, user)

    try
        f ()
    finally
        currentViewer.Value <- previous

/// The account a viewer is limited to, or `None` when it may see all rows
/// (embedded/internal, or it holds `priv`).
let private restrictedTo (priv: string) : Fsdb.Auth.Account option =
    match currentViewer.Value with
    | Some(store, user) when not (Fsdb.Auth.hasGlobalPrivForAccount store user priv) -> Some user
    | _ -> None

/// Stamped by `Server.listen` — `SHOW STATUS`'s `Uptime` baseline.
let mutable serverStartedAt = DateTime.Now

type private AtomicCounter() =
    let mutable value = 0L

    member _.Increment() = Threading.Interlocked.Increment(&value) |> ignore
    member _.Value = Threading.Interlocked.Read(&value)
    member _.Reset() = Threading.Interlocked.Exchange(&value, 0L) |> ignore

let private questionCount = AtomicCounter()
let private deleteCommandCount = AtomicCounter()
let private insertCommandCount = AtomicCounter()
let private replaceCommandCount = AtomicCounter()
let private selectCommandCount = AtomicCounter()
let private updateCommandCount = AtomicCounter()

type StatusCommand =
    | DeleteCommand
    | InsertCommand
    | ReplaceCommand
    | SelectCommand
    | UpdateCommand

let private commandName = function
    | DeleteCommand -> "Com_delete"
    | InsertCommand -> "Com_insert"
    | ReplaceCommand -> "Com_replace"
    | SelectCommand -> "Com_select"
    | UpdateCommand -> "Com_update"

let private reportedCommands =
    [ DeleteCommand; InsertCommand; ReplaceCommand; SelectCommand; UpdateCommand ]

let private commandCounter = function
    | DeleteCommand -> deleteCommandCount
    | InsertCommand -> insertCommandCount
    | ReplaceCommand -> replaceCommandCount
    | SelectCommand -> selectCommandCount
    | UpdateCommand -> updateCommandCount

let recordQuestion () = questionCount.Increment()
let questions () = questionCount.Value
let resetQuestions () = questionCount.Reset()
let recordCommand command = (commandCounter command).Increment()

let resetCommandCounts () =
    reportedCommands |> List.iter (fun command -> (commandCounter command).Reset())

let private commandCount command = (commandCounter command).Value

let registerProcessAs (id: int64) (account: Fsdb.Auth.Account) (user: string) (host: string) : ProcessEntry =
    let entry =
        { Id = id
          Account = account
          User = user
          Host = host
          Db = None
          Command = "Sleep"
          State = ""
          StateSince = DateTime.Now
          Info = None
          CancelQuery = None
          CloseConnection = None }

    processes.[id] <- entry
    entry

let registerProcess (id: int64) (user: string) (host: string) =
    registerProcessAs id (Fsdb.Auth.account user "%") user host

let unregisterProcess (id: int64) = processes.TryRemove id |> ignore

let tryFindProcess (id: int64) : ProcessEntry option =
    match processes.TryGetValue id with
    | true, p -> Some p
    | _ -> None

let listProcesses () : ProcessEntry list =
    processes.Values |> Seq.sortBy (fun p -> p.Id) |> List.ofSeq

let connectedThreads () = processes.Count

let private processlistColumns =
    [ intCol "ID"
      strCol "USER"
      strCol "HOST"
      strCol "DB"
      strCol "COMMAND"
      intCol "TIME"
      strCol "STATE"
      strCol "INFO" ]

/// Live connections the current viewer may see — its own only, unless it
/// holds `PROCESS`. Shared by the `information_schema.processlist` view and
/// `SHOW PROCESSLIST`.
let private visibleProcesses () : ProcessEntry list =
    let all = listProcesses ()

    match restrictedTo "PROCESS" with
    | Some account -> all |> List.filter (fun p -> Fsdb.Auth.sameAccount p.Account account)
    | None -> all

/// `(ID, USER, HOST, DB, COMMAND, TIME, STATE, INFO)` per visible connection —
/// the `information_schema.processlist` row source.
let private processlistRows () : Value[] list =
    visibleProcesses ()
    |> List.map (fun ptmp ->
        [| VInt ptmp.Id
           vs ptmp.User
           vs ptmp.Host
           vopt ptmp.Db
           vs ptmp.Command
           vi (int (DateTime.Now - ptmp.StateSince).TotalSeconds)
           vs ptmp.State
           vopt ptmp.Info |])

// ---------------------------------------------------------------------------
// Stored-object catalogs. Views and triggers project their row-backed mysql
// catalogs; routines and events remain genuinely empty.
// ---------------------------------------------------------------------------

let private viewsColumns =
    [ strCol "TABLE_CATALOG"
      strCol "TABLE_SCHEMA"
      strCol "TABLE_NAME"
      strCol "VIEW_DEFINITION"
      strCol "CHECK_OPTION"
      strCol "IS_UPDATABLE"
      strCol "DEFINER"
      strCol "SECURITY_TYPE"
      strCol "CHARACTER_SET_CLIENT"
      strCol "COLLATION_CONNECTION" ]

let private isUpdatableView (catalog: Catalog) (schema: string) (definition: string) =
    let views = viewCatalogEntries catalog

    let isPhysicalTable defaultSchema (source: TableRef) =
        let sourceSchema = source.Database |> Option.defaultValue defaultSchema

        catalog
        |> Map.tryFind sourceSchema
        |> Option.bind (Map.tryFind (source.Table.ToLowerInvariant()))
        |> Option.isSome

    let rec check seen schema definition =
        let sourceAllowsUpdates (source: TableRef) =
            let sourceSchema = source.Database |> Option.defaultValue schema
            let key = sourceSchema.ToLowerInvariant(), source.Table.ToLowerInvariant()

            if isPhysicalTable schema source then
                true
            else
                views
                |> List.tryFind (fun view ->
                    view.Schema.Equals(sourceSchema, StringComparison.OrdinalIgnoreCase)
                    && view.Name.Equals(source.Table, StringComparison.OrdinalIgnoreCase))
                |> Option.exists (fun view -> not (Set.contains key seen) && check (Set.add key seen) sourceSchema view.Definition)

        match Fsdb.Parser.parse definition with
        | Ok(Select select) ->
            match select.From with
            | Some(FromTable source) ->
                let shapeAllowsUpdates =
                    (select.Joins.IsEmpty
                     || (select.Joins
                         |> List.forall (fun join ->
                             join.Kind = InnerJoin
                             && match join.Table with FromTable _ -> true | _ -> false)))
                    && not select.Distinct
                    && not select.CalculateFoundRows
                    && select.GroupBy.IsEmpty
                    && not select.Rollup
                    && select.Windows.IsEmpty
                    && select.Ctes.IsEmpty
                    && select.Having.IsNone
                    && select.OrderBy.IsEmpty
                    && select.Limit.IsNone
                    && select.Offset.IsNone
                    && not select.Locking
                    && (select.Projections
                        |> List.exists (fun (expression, _) ->
                            match expression with
                            | Col _
                            | QualifiedCol _ -> true
                            | _ -> false))

                if not shapeAllowsUpdates then
                    false
                elif not select.Joins.IsEmpty then
                    sourceAllowsUpdates source
                    || (select.Joins
                        |> List.exists (fun join ->
                            match join.Table with
                            | FromTable table -> sourceAllowsUpdates table
                            | _ -> false))
                else
                    sourceAllowsUpdates source
            | _ -> false
        | _ -> false

    check Set.empty schema definition

let private viewsRows (catalog: Catalog) : Value[] list =
    viewCatalogEntries catalog
    |> List.map (fun view ->
        [| vs "def"; vs view.Schema; vs view.Name; vs view.Definition; vs view.CheckOption; vs (if isUpdatableView catalog view.Schema view.Definition then "YES" else "NO"); vs view.Definer
           vs view.SecurityType; vs "utf8mb4"; vs "utf8mb4_0900_ai_ci" |])

let private routinesColumns =
    [ strCol "SPECIFIC_NAME"
      strCol "ROUTINE_CATALOG"
      strCol "ROUTINE_SCHEMA"
      strCol "ROUTINE_NAME"
      strCol "ROUTINE_TYPE"
      strCol "DATA_TYPE"
      intCol "CHARACTER_MAXIMUM_LENGTH"
      intCol "CHARACTER_OCTET_LENGTH"
      intCol "NUMERIC_PRECISION"
      intCol "NUMERIC_SCALE"
      intCol "DATETIME_PRECISION"
      strCol "CHARACTER_SET_NAME"
      strCol "COLLATION_NAME"
      strCol "DTD_IDENTIFIER"
      strCol "ROUTINE_BODY"
      strCol "ROUTINE_DEFINITION"
      strCol "EXTERNAL_NAME"
      strCol "EXTERNAL_LANGUAGE"
      strCol "PARAMETER_STYLE"
      strCol "IS_DETERMINISTIC"
      strCol "SQL_DATA_ACCESS"
      strCol "SQL_PATH"
      strCol "SECURITY_TYPE"
      col "CREATED" (TDateTime 0)
      col "LAST_ALTERED" (TDateTime 0)
      strCol "SQL_MODE"
      strCol "ROUTINE_COMMENT"
      strCol "DEFINER"
      strCol "CHARACTER_SET_CLIENT"
      strCol "COLLATION_CONNECTION"
      strCol "DATABASE_COLLATION" ]

let private routinesRows (catalog: Catalog) =
    mysqlTable catalog "routines"
    |> Option.map (fun table ->
        table.RowsArray
        |> Seq.choose SystemCatalog.Routine.tryRead
        |> Seq.map (fun routine ->
            let created = routine.Created |> Option.map VDateTime |> Option.defaultValue VNull

            [| vs routine.Name; vs "def"; vs routine.Schema; vs routine.Name; vs "PROCEDURE"; vs ""; VNull; VNull
               VNull; VNull; VNull; VNull; VNull; VNull; vs "SQL"; vs routine.Definition; VNull; vs "SQL"; vs "SQL"
               vs "NO"; vs "CONTAINS SQL"; VNull; vs routine.SecurityType; created; created
               vs "ONLY_FULL_GROUP_BY,STRICT_TRANS_TABLES,NO_ZERO_IN_DATE,NO_ZERO_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION"
               vs ""; vs routine.Definer; vs "utf8mb4"; vs "utf8mb4_0900_ai_ci"; vs "utf8mb4_0900_ai_ci" |])
        |> List.ofSeq)
    |> Option.defaultValue []

let private parametersColumns =
    [ strCol "SPECIFIC_CATALOG"
      strCol "SPECIFIC_SCHEMA"
      strCol "SPECIFIC_NAME"
      intCol "ORDINAL_POSITION"
      strCol "PARAMETER_MODE"
      strCol "PARAMETER_NAME"
      strCol "DATA_TYPE"
      intCol "CHARACTER_MAXIMUM_LENGTH"
      intCol "CHARACTER_OCTET_LENGTH"
      intCol "NUMERIC_PRECISION"
      intCol "NUMERIC_SCALE"
      intCol "DATETIME_PRECISION"
      strCol "CHARACTER_SET_NAME"
      strCol "COLLATION_NAME"
      strCol "DTD_IDENTIFIER"
      strCol "ROUTINE_TYPE" ]

let private triggersColumns =
    [ strCol "TRIGGER_CATALOG"
      strCol "TRIGGER_SCHEMA"
      strCol "TRIGGER_NAME"
      strCol "EVENT_MANIPULATION"
      strCol "EVENT_OBJECT_CATALOG"
      strCol "EVENT_OBJECT_SCHEMA"
      strCol "EVENT_OBJECT_TABLE"
      intCol "ACTION_ORDER"
      strCol "ACTION_CONDITION"
      strCol "ACTION_STATEMENT"
      strCol "ACTION_ORIENTATION"
      strCol "ACTION_TIMING"
      strCol "ACTION_REFERENCE_OLD_TABLE"
      strCol "ACTION_REFERENCE_NEW_TABLE"
      strCol "ACTION_REFERENCE_OLD_ROW"
      strCol "ACTION_REFERENCE_NEW_ROW"
      col "CREATED" (TDateTime 2)
      strCol "SQL_MODE"
      strCol "DEFINER"
      strCol "CHARACTER_SET_CLIENT"
      strCol "COLLATION_CONNECTION"
      strCol "DATABASE_COLLATION" ]

let private triggerCatalogRows (catalog: Catalog) : SystemCatalog.Trigger.Entry list =
    mysqlTable catalog "triggers"
    |> Option.map (fun t ->
        t.RowsArray
        |> Seq.choose SystemCatalog.Trigger.tryRead
        |> List.ofSeq)
    |> Option.defaultValue []

// The sql_mode/charset constants every trigger row renders — fsdb doesn't
// capture those per trigger; values match the server's own advertised
// defaults (charset/collation as write-probed on MySQL 8.4.11). The definer
// is per-trigger and comes from the catalog row.
let private triggerSqlMode = "STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION"

let private triggerCreatedText (trigger: SystemCatalog.Trigger.Entry) =
    trigger.Created
    |> Option.bind (VDateTime >> Value.toTextFsp 2)
    |> Option.defaultValue ""

let private triggerCreatedValue trigger = triggerCreatedText trigger |> VString

/// `information_schema.TRIGGERS` rows off the trigger catalog — constant
/// cells (ORIENTATION ROW and OLD/NEW row refs) exactly as
/// write-probed on MySQL 8.4.11.
let private triggersRows (catalog: Catalog) : Value[] list =
    triggerCatalogRows catalog
    |> List.map (fun trigger ->
        let oldRow = if trigger.Event = "INSERT" then VNull else vs "OLD"
        let newRow = if trigger.Event = "DELETE" then VNull else vs "NEW"

        [| vs "def"; vs trigger.Schema; vs trigger.Name; vs trigger.Event; vs "def"; vs trigger.Schema; vs trigger.Table
           VInt trigger.Order; VNull; vs trigger.Body; vs "ROW"; vs trigger.Timing; VNull; VNull; oldRow; newRow
           triggerCreatedValue trigger; vs triggerSqlMode; vs trigger.Definer; vs "utf8mb4"; vs "utf8mb4_0900_ai_ci"
           vs "utf8mb4_0900_ai_ci" |])

let private eventsColumns =
    [ strCol "EVENT_CATALOG"
      strCol "EVENT_SCHEMA"
      strCol "EVENT_NAME"
      strCol "DEFINER"
      strCol "TIME_ZONE"
      strCol "EVENT_BODY"
      strCol "EVENT_DEFINITION"
      strCol "EVENT_TYPE"
      col "EXECUTE_AT" (TDateTime 0)
      strCol "INTERVAL_VALUE"
      strCol "INTERVAL_FIELD"
      strCol "SQL_MODE"
      col "STARTS" (TDateTime 0)
      col "ENDS" (TDateTime 0)
      strCol "STATUS"
      strCol "ON_COMPLETION"
      col "CREATED" (TDateTime 0)
      col "LAST_ALTERED" (TDateTime 0)
      col "LAST_EXECUTED" (TDateTime 0)
      strCol "EVENT_COMMENT"
      intCol "ORIGINATOR"
      strCol "CHARACTER_SET_CLIENT"
      strCol "COLLATION_CONNECTION"
      strCol "DATABASE_COLLATION" ]

let private eventsRows (catalog: Catalog) =
    let recurringSchedule =
        Regex(
            @"^EVERY\s+(?<value>.+?)\s+(?<field>YEAR_MONTH|DAY_HOUR|DAY_MINUTE|DAY_SECOND|HOUR_MINUTE|HOUR_SECOND|MINUTE_SECOND|YEAR|QUARTER|MONTH|WEEK|DAY|HOUR|MINUTE|SECOND)(?:\s|$)",
            RegexOptions.IgnoreCase
        )

    mysqlTable catalog "events"
    |> Option.map (fun table ->
        table.RowsArray
        |> Seq.choose SystemCatalog.Event.tryRead
        |> Seq.map (fun event ->
            let recurring = recurringSchedule.Match event.Schedule
            let eventType, intervalValue, intervalField =
                if recurring.Success then
                    vs "RECURRING", vs recurring.Groups.["value"].Value, vs (recurring.Groups.["field"].Value.ToUpperInvariant())
                else
                    vs "ONE TIME", VNull, VNull

            let created = event.Created |> Option.map VDateTime |> Option.defaultValue VNull

            [| vs "def"; vs event.Schema; vs event.Name; vs event.Definer; vs "SYSTEM"; vs "SQL"; vs event.Definition
               eventType; VNull; intervalValue; intervalField; vs ""; VNull; VNull; vs event.Status; vs "NOT PRESERVE"
               created; created; VNull; vs ""; VInt 1L; vs "utf8mb4"; vs "utf8mb4_0900_ai_ci"; vs "utf8mb4_0900_ai_ci" |])
        |> List.ofSeq)
    |> Option.defaultValue []

/// One row per user table with NULL partition fields — what real MySQL
/// emits for every unpartitioned table.
let private partitionsColumns =
    [ strCol "TABLE_CATALOG"
      strCol "TABLE_SCHEMA"
      strCol "TABLE_NAME"
      strCol "PARTITION_NAME"
      strCol "SUBPARTITION_NAME"
      intCol "PARTITION_ORDINAL_POSITION"
      intCol "SUBPARTITION_ORDINAL_POSITION"
      strCol "PARTITION_METHOD"
      strCol "SUBPARTITION_METHOD"
      strCol "PARTITION_EXPRESSION"
      strCol "SUBPARTITION_EXPRESSION"
      strCol "PARTITION_DESCRIPTION"
      intCol "TABLE_ROWS"
      intCol "AVG_ROW_LENGTH"
      intCol "DATA_LENGTH"
      intCol "MAX_DATA_LENGTH"
      intCol "INDEX_LENGTH"
      intCol "DATA_FREE"
      col "CREATE_TIME" (TDateTime 0)
      col "UPDATE_TIME" (TDateTime 0)
      col "CHECK_TIME" (TDateTime 0)
      intCol "CHECKSUM"
      strCol "PARTITION_COMMENT"
      strCol "NODEGROUP"
      strCol "TABLESPACE_NAME" ]

let private partitionsRows (catalog: Catalog) : Value[] list =
    allTables catalog
    |> List.map (fun (dbName, t) ->
        [| vs "def"
           vs dbName
           vs t.OriginalName
           VNull
           VNull
           VNull
           VNull
           VNull
           VNull
           VNull
           VNull
           VNull
           vi (t.RowsArray.Length)
           vi 0
           vi 16384
           vi 0
           vi 0
           vi 0
           VDateTime(truncateToSecond t.CreateTime)
           VNull
           VNull
           VNull
           vs ""
           vs ""
           VNull |])

// ---------------------------------------------------------------------------
// Privilege views — projected straight off the `mysql` system schema's rows
// (`user`/`db`/`tables_priv`, see `Storage`'s bootstrap and `Auth`), the
// same source SHOW GRANTS renders and the executor enforces, so the three
// can't disagree. COLUMN_PRIVILEGES stays genuinely empty: fsdb has no
// column-level grants.
// ---------------------------------------------------------------------------

let private colIdx (t: Table) (name: string) : int option =
    resolveColumn t.Columns name |> Result.toOption

let private rowText (row: Value[]) (i: int) : string =
    match row.[i] with
    | VString s -> s
    | _ -> ""

let private userPrivilegesColumns =
    [ strCol "GRANTEE"; strCol "TABLE_CATALOG"; strCol "PRIVILEGE_TYPE"; strCol "IS_GRANTABLE" ]

/// Each account's global privileges off `mysql.user` (an account with none
/// gets the single `USAGE` row, same as MySQL).
let private userPrivilegesRows (catalog: Catalog) : Value[] list =
    match mysqlTable catalog "user" with
    | None -> []
    | Some t ->
        match colIdx t "User", colIdx t "Host", colIdx t "Grant_priv" with
        | Some userIdx, Some hostIdx, Some grantIdx ->
            let ownOnly = restrictedTo "SELECT"

            t.Rows
            |> List.filter (fun row ->
                match ownOnly with
                | Some account -> Fsdb.Auth.sameAccount (Fsdb.Auth.account (rowText row userIdx) (rowText row hostIdx)) account
                | None -> true)
            |> List.collect (fun row ->
                let grantee = sprintf "'%s'@'%s'" (rowText row userIdx) (rowText row hostIdx)
                let grantable = if rowText row grantIdx = "Y" then "YES" else "NO"

                let granted =
                    Fsdb.Auth.staticPrivileges
                    |> List.filter (fun d ->
                        colIdx t d.UserCol |> Option.map (fun i -> rowText row i = "Y") |> Option.defaultValue false)

                let privNames = if granted.IsEmpty then [ "USAGE" ] else granted |> List.map (fun d -> d.Sql)
                privNames |> List.map (fun p -> [| vs grantee; vs "def"; vs p; vs grantable |]))
        | _ -> []

/// Per-database grants off `mysql.db`.
let private schemaPrivilegesRows (catalog: Catalog) : Value[] list =
    match mysqlTable catalog "db" with
    | None -> []
    | Some t ->
        match colIdx t "User", colIdx t "Host", colIdx t "Db", colIdx t "Grant_priv" with
        | Some u, Some h, Some d, Some g ->
            let ownOnly = restrictedTo "SELECT"

            t.Rows
            |> List.filter (fun row ->
                match ownOnly with
                | Some account -> Fsdb.Auth.sameAccount (Fsdb.Auth.account (rowText row u) (rowText row h)) account
                | None -> true)
            |> List.collect (fun row ->
                let grantee = sprintf "'%s'@'%s'" (rowText row u) (rowText row h)
                let grantable = if rowText row g = "Y" then "YES" else "NO"

                Fsdb.Auth.staticPrivileges
                |> List.filter (fun p ->
                    p.DbCol.IsSome
                    && (colIdx t p.DbCol.Value |> Option.map (fun i -> rowText row i = "Y") |> Option.defaultValue false))
                |> List.map (fun p -> [| vs grantee; vs "def"; vs (rowText row d); vs p.Sql; vs grantable |]))
        | _ -> []

/// Per-table grants off `mysql.tables_priv`'s `Table_priv` SET strings.
let private tablePrivilegesRows (catalog: Catalog) : Value[] list =
    match mysqlTable catalog "tables_priv" with
    | None -> []
    | Some t ->
        match colIdx t "User", colIdx t "Host", colIdx t "Db", colIdx t "Table_name", colIdx t "Table_priv" with
        | Some u, Some h, Some d, Some tn, Some tp ->
            let ownOnly = restrictedTo "SELECT"

            t.Rows
            |> List.filter (fun row ->
                match ownOnly with
                | Some account -> Fsdb.Auth.sameAccount (Fsdb.Auth.account (rowText row u) (rowText row h)) account
                | None -> true)
            |> List.collect (fun row ->
                let members = Fsdb.Auth.setMembers (rowText row tp)
                let hasMember s = members |> List.exists (fun m -> String.Equals(m, s, StringComparison.OrdinalIgnoreCase))
                let grantee = sprintf "'%s'@'%s'" (rowText row u) (rowText row h)
                let grantable = if hasMember "Grant" then "YES" else "NO"

                Fsdb.Auth.staticPrivileges
                |> List.filter (fun p -> p.TablePriv |> Option.map hasMember |> Option.defaultValue false)
                |> List.map (fun p ->
                    [| vs grantee; vs "def"; vs (rowText row d); vs (rowText row tn); vs p.Sql; vs grantable |]))
        | _ -> []

let private schemaPrivilegesColumns =
    [ strCol "GRANTEE"; strCol "TABLE_CATALOG"; strCol "TABLE_SCHEMA"; strCol "PRIVILEGE_TYPE"; strCol "IS_GRANTABLE" ]

let private tablePrivilegesColumns =
    [ strCol "GRANTEE"
      strCol "TABLE_CATALOG"
      strCol "TABLE_SCHEMA"
      strCol "TABLE_NAME"
      strCol "PRIVILEGE_TYPE"
      strCol "IS_GRANTABLE" ]

let private columnPrivilegesColumns =
    [ strCol "GRANTEE"
      strCol "TABLE_CATALOG"
      strCol "TABLE_SCHEMA"
      strCol "TABLE_NAME"
      strCol "COLUMN_NAME"
      strCol "PRIVILEGE_TYPE"
      strCol "IS_GRANTABLE" ]

/// The one storage engine fsdb reports for every table — `SHOW ENGINES`'
/// twin lives in `showEngines` below off this same row.
let private enginesColumns =
    [ strCol "ENGINE"; strCol "SUPPORT"; strCol "COMMENT"; strCol "TRANSACTIONS"; strCol "XA"; strCol "SAVEPOINTS" ]

let private enginesRows: Value[] list =
    [ [| vs "InnoDB"
         vs "DEFAULT"
         vs "Supports transactions, row-level locking, and foreign keys"
         vs "YES"
         vs "YES"
         vs "YES" |] ]

/// Every virtual table this module serves, name -> columns — `scan`'s
/// dispatch source and the self-listing the `TABLES`/`COLUMNS` views and
/// `SHOW TABLES FROM information_schema` append, so listing and resolution
/// can't drift.
let private virtualTableDefs : (string * ColumnDef list) list =
    [ "CHARACTER_SETS", characterSetsColumns
      "CHECK_CONSTRAINTS", checkConstraintsColumns
      "COLLATIONS", collationsColumns
      "COLLATION_CHARACTER_SET_APPLICABILITY", collationCharacterSetApplicabilityColumns
      "COLUMNS", columnsColumns
      "COLUMN_PRIVILEGES", columnPrivilegesColumns
      "ENGINES", enginesColumns
      "EVENTS", eventsColumns
      "KEY_COLUMN_USAGE", keyColumnUsageColumns
      "PARAMETERS", parametersColumns
      "PARTITIONS", partitionsColumns
      "PROCESSLIST", processlistColumns
      "REFERENTIAL_CONSTRAINTS", referentialConstraintsColumns
      "ROUTINES", routinesColumns
      "SCHEMATA", schemataColumns
      "SCHEMA_PRIVILEGES", schemaPrivilegesColumns
      "STATISTICS", statisticsColumns
      "TABLES", tablesColumns
      "TABLE_CONSTRAINTS", tableConstraintsColumns
      "TABLE_PRIVILEGES", tablePrivilegesColumns
      "TRIGGERS", triggersColumns
      "USER_PRIVILEGES", userPrivilegesColumns
      "VIEWS", viewsColumns ]

/// information_schema's own tables as `TABLES` rows — `SYSTEM VIEW`, NULL
/// engine/format/collation like real MySQL's.
let private selfTablesRows () : Value[] list =
    virtualTableDefs
    |> List.map (fun (name, _) ->
        [| vs "def"
           vs "information_schema"
           vs name
           vs "SYSTEM VIEW"
           VNull
           vi 10
           VNull
           VNull
           VNull
           VNull
           VNull
           VNull
           VNull
           VNull
           VDateTime(truncateToSecond serverStartedAt)
           VNull
           VNull
           VNull
           VNull
           vs ""
           vs "" |])

// Built once: `virtualTableDefs` never changes after startup, and these
// ~330 rows were most of every COLUMNS scan's cost (readers never mutate
// row arrays, same sharing contract `Storage.scanList` already has).
let private selfColumnsRowsCached : Lazy<Value[] list> =
    lazy
        (virtualTableDefs
         |> List.collect (fun (name, cols) ->
             cols |> List.mapi (fun i c -> columnRowWith "select" "information_schema" name i "" c)))

let private scopeRowsToViewer (tableName: string) (columns: ColumnDef list) (rows: Value[] list) : Value[] list =
    match currentViewer.Value with
    | None -> rows
    | Some(store, user) ->
        let columnIndex name =
            columns
            |> List.tryFindIndex (fun column -> String.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))

        let schemaIndex =
            [ "TABLE_SCHEMA"; "CONSTRAINT_SCHEMA"; "TRIGGER_SCHEMA"; "EVENT_SCHEMA"; "ROUTINE_SCHEMA"; "SCHEMA_NAME" ]
            |> List.tryPick columnIndex

        let tableIndex = [ "TABLE_NAME"; "EVENT_OBJECT_TABLE" ] |> List.tryPick columnIndex

        let visibleSchema (row: Value[]) =
            schemaIndex
            |> Option.map (fun index -> row.[index] |> Value.toText |> Option.map (Fsdb.Auth.canSeeDatabaseForAccount store user) |> Option.defaultValue false)
            |> Option.defaultValue true

        let visibleObject (row: Value[]) =
            let visibleTable =
                match schemaIndex, tableIndex with
                | Some dbIndex, Some nameIndex -> Fsdb.Auth.canSeeTableForAccount store user (rowText row dbIndex) (rowText row nameIndex)
                | _ -> true

            visibleTable
            && (match tableName with
                | "VIEWS" ->
                    Fsdb.Auth.checkForAccount store user [ "SHOW VIEW", Fsdb.Auth.OnTable(rowText row 1, rowText row 2) ] |> Result.isOk
                | "TRIGGERS" ->
                    Fsdb.Auth.checkForAccount store user [ "TRIGGER", Fsdb.Auth.OnTable(rowText row 1, rowText row 6) ] |> Result.isOk
                | _ -> true)

        rows |> List.filter (fun row -> visibleSchema row && visibleObject row)


/// Resolves one `information_schema` table name (case-insensitive) to its
/// columns and freshly-projected rows, or `None` if `name` isn't one of the
/// virtual tables this module knows about (a real 1146 from `Executor`, same
/// as any other unknown table).
let scan (catalog: Catalog) (name: string) (viewColumns: ViewColumns option) : (ColumnDef list * Value[] list) option =
    let upper = name.ToUpperInvariant()

    let rows =
        match upper with
        | "TABLES" -> Some(tablesRows catalog @ selfTablesRows ())
        | "COLUMNS" -> Some(columnsRows catalog viewColumns @ selfColumnsRowsCached.Value)
        | "STATISTICS" -> Some(statisticsRows catalog)
        | "KEY_COLUMN_USAGE" -> Some(keyColumnUsageRows catalog)
        | "REFERENTIAL_CONSTRAINTS" -> Some(referentialConstraintsRows catalog)
        | "CHECK_CONSTRAINTS" -> Some(checkConstraintsRows catalog)
        | "TABLE_CONSTRAINTS" -> Some(tableConstraintsRows catalog)
        | "COLLATION_CHARACTER_SET_APPLICABILITY" -> Some collationCharacterSetApplicabilityRows
        | "COLLATIONS" -> Some collationsRows
        | "CHARACTER_SETS" -> Some characterSetsRows
        | "SCHEMATA" -> Some(schemataRows catalog)
        | "PROCESSLIST" -> Some(processlistRows ())
        | "PARTITIONS" -> Some(partitionsRows catalog)
        | "USER_PRIVILEGES" -> Some(userPrivilegesRows catalog)
        | "SCHEMA_PRIVILEGES" -> Some(schemaPrivilegesRows catalog)
        | "TABLE_PRIVILEGES" -> Some(tablePrivilegesRows catalog)
        | "ENGINES" -> Some enginesRows
        // Events remain a real empty set (COLUMN_PRIVILEGES too: no
        // column-level grants exist).
        | "TRIGGERS" -> Some(triggersRows catalog)
        | "VIEWS" -> Some(viewsRows catalog)
        | "ROUTINES" -> Some(routinesRows catalog)
        | "PARAMETERS"
        | "COLUMN_PRIVILEGES" -> Some []
        | "EVENTS" -> Some(eventsRows catalog)
        | _ -> None

    rows
    |> Option.bind (fun rows ->
        virtualTableDefs
        |> List.tryFind (fst >> (=) upper)
        |> Option.map (fun (_, cols) -> cols, scopeRowsToViewer upper cols rows))

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
/// `fsdbTables` is the host-extension overlay's table names (empty when
/// nothing is registered) — for the `fsdb` schema they list as SYSTEM VIEW
/// beside the real tables, a same-named real table deduped away because
/// resolution prefers the virtual one.
let showTables (catalog: Catalog) (fsdbTables: string list) (dbName: string) (full: bool) (likeOpt: string option) : ShowResult =
    let renderTyped (entries: (string * string) list) =
        let entries =
            entries
            |> List.distinctBy (fun (n, _) -> n.ToLowerInvariant())
            |> List.filter (fst >> likeFilter likeOpt)
            |> List.sortBy fst

        let col = sprintf "Tables_in_%s" dbName

        if full then
            Ok([ col; "Table_type" ], entries |> List.map (fun (n, t) -> [ Some n; Some t ]))
        else
            Ok([ col ], entries |> List.map (fun (n, _) -> [ Some n ]))

    let render (names: string list) (tableType: string) =
        renderTyped (names |> List.map (fun n -> n, tableType))

    let schemaEntries () =
        match Map.tryFind dbName catalog with
        | None -> None
        | Some db ->
            let tables = db |> Map.toList |> List.map (fun (_, table) -> table.OriginalName, "BASE TABLE")

            let views =
                viewCatalogEntries catalog
                |> List.filter (fun view -> System.String.Equals(view.Schema, dbName, System.StringComparison.OrdinalIgnoreCase))
                |> List.map (fun view -> view.Name, "VIEW")

            Some(tables @ views)

    // The virtual database is browsable like `USE information_schema`
    // already is — its tables are the `scan` registry's, typed SYSTEM VIEW.
    if dbName.ToLowerInvariant() = "information_schema" then
        render (virtualTableDefs |> List.map fst) "SYSTEM VIEW"
    elif dbName.ToLowerInvariant() = "fsdb" && not fsdbTables.IsEmpty then
        renderTyped (
            (fsdbTables |> List.map (fun n -> n, "SYSTEM VIEW"))
            @ (schemaEntries () |> Option.defaultValue [])
        )
    else
        match schemaEntries () with
        | None -> Error(1049, sprintf "Unknown database '%s'" dbName)
        | Some entries -> renderTyped entries

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

/// `SHOW PRIVILEGES` — MySQL 8.4.11's exact 73 rows (oracle-verified): the
/// 33 static privileges with their contexts/comments, then the dynamic
/// privileges (Server Admin, empty comment). Static data — fsdb enforces
/// only the static set, but clients enumerate this list as-is.
let showPrivileges () : ShowResult =
    let staticRows =
        [ "Alter", "Tables", "To alter the table"
          "Alter routine", "Functions,Procedures", "To alter or drop stored functions/procedures"
          "Create", "Databases,Tables,Indexes", "To create new databases and tables"
          "Create routine", "Databases", "To use CREATE FUNCTION/PROCEDURE"
          "Create role", "Server Admin", "To create new roles"
          "Create temporary tables", "Databases", "To use CREATE TEMPORARY TABLE"
          "Create view", "Tables", "To create new views"
          "Create user", "Server Admin", "To create new users"
          "Delete", "Tables", "To delete existing rows"
          "Drop", "Databases,Tables", "To drop databases, tables, and views"
          "Drop role", "Server Admin", "To drop roles"
          "Event", "Server Admin", "To create, alter, drop and execute events"
          "Execute", "Functions,Procedures", "To execute stored routines"
          "File", "File access on server", "To read and write files on the server"
          "Grant option", "Databases,Tables,Functions,Procedures", "To give to other users those privileges you possess"
          "Index", "Tables", "To create or drop indexes"
          "Insert", "Tables", "To insert data into tables"
          "Lock tables", "Databases", "To use LOCK TABLES (together with SELECT privilege)"
          "Process", "Server Admin", "To view the plain text of currently executing queries"
          "Proxy", "Server Admin", "To make proxy user possible"
          "References", "Databases,Tables", "To have references on tables"
          "Reload", "Server Admin", "To reload or refresh tables, logs and privileges"
          "Replication client", "Server Admin", "To ask where the slave or master servers are"
          "Replication slave", "Server Admin", "To read binary log events from the master"
          "Select", "Tables", "To retrieve rows from table"
          "Show databases", "Server Admin", "To see all databases with SHOW DATABASES"
          "Show view", "Tables", "To see views with SHOW CREATE VIEW"
          "Shutdown", "Server Admin", "To shut down the server"
          "Super", "Server Admin", "To use KILL thread, SET GLOBAL, CHANGE REPLICATION SOURCE, etc."
          "Trigger", "Tables", "To use triggers"
          "Create tablespace", "Server Admin", "To create/alter/drop tablespaces"
          "Update", "Tables", "To update existing rows"
          "Usage", "Server Admin", "No privileges - allow connect only" ]

    let dynamicRows =
        [ "FIREWALL_EXEMPT"; "AUDIT_ABORT_EXEMPT"; "ALLOW_NONEXISTENT_DEFINER"; "SENSITIVE_VARIABLES_OBSERVER"
          "AUTHENTICATION_POLICY_ADMIN"; "GROUP_REPLICATION_STREAM"; "FLUSH_PRIVILEGES"; "PASSWORDLESS_USER_ADMIN"
          "FLUSH_TABLES"; "FLUSH_OPTIMIZER_COSTS"; "OPTIMIZE_LOCAL_TABLE"; "INNODB_REDO_LOG_ENABLE"
          "APPLICATION_PASSWORD_ADMIN"; "REPLICATION_APPLIER"; "SESSION_VARIABLES_ADMIN"; "TELEMETRY_LOG_ADMIN"
          "AUDIT_ADMIN"; "TABLE_ENCRYPTION_ADMIN"; "SERVICE_CONNECTION_ADMIN"; "FLUSH_USER_RESOURCES"
          "REPLICATION_SLAVE_ADMIN"; "CLONE_ADMIN"; "CONNECTION_ADMIN"; "SYSTEM_USER"; "ENCRYPTION_KEY_ADMIN"
          "RESOURCE_GROUP_ADMIN"; "SHOW_ROUTINE"; "XA_RECOVER_ADMIN"; "SET_ANY_DEFINER"; "ROLE_ADMIN"
          "PERSIST_RO_VARIABLES_ADMIN"; "BINLOG_ADMIN"; "BINLOG_ENCRYPTION_ADMIN"; "BACKUP_ADMIN"
          "GROUP_REPLICATION_ADMIN"; "TRANSACTION_GTID_TAG"; "RESOURCE_GROUP_USER"; "SYSTEM_VARIABLES_ADMIN"
          "FLUSH_STATUS"; "INNODB_REDO_LOG_ARCHIVE" ]
        |> List.map (fun n -> n, "Server Admin", "")

    Ok(
        [ "Privilege"; "Context"; "Comment" ],
        staticRows @ dynamicRows |> List.map (fun (p, c, m) -> [ Some p; Some c; Some m ])
    )

/// `SHOW DATABASES [LIKE 'pattern']`. `fsdbVisible` lists the reserved
/// `fsdb` extension schema — true exactly when a host has registered
/// virtual tables into it, so a plain server never advertises it.
let showDatabases (catalog: Catalog) (fsdbVisible: bool) (likeOpt: string option) : string list * (string option list) list =
    let names =
        "information_schema" :: (if fsdbVisible then [ "fsdb" ] else []) @ (catalog |> Map.toList |> List.map fst)
        |> List.distinct
        |> List.filter (likeFilter likeOpt)
        |> List.sort

    [ "Database" ], names |> List.map (fun n -> [ Some n ])

/// `SHOW [FULL] COLUMNS FROM t [FROM db] [LIKE 'pattern']` and
/// `DESCRIBE`/`DESC t` (which are just `SHOW COLUMNS`'s narrower 5-column
/// form under a different name).
let showColumns (catalog: Catalog) (viewColumns: ViewColumns option) (full: bool) (dbName: string) (tableName: string) (likeOpt: string option) : ShowResult =
    let columns =
        match findTable catalog dbName tableName with
        | Ok table -> Ok(table.Columns, fun (column: ColumnDef) -> columnKey table column)
        | Error error ->
            match viewColumns |> Option.bind (fun resolve -> resolve dbName tableName) with
            | Some columns -> Ok(columns, fun (_: ColumnDef) -> "")
            | None -> Error error

    columns
    |> Result.map (fun (columns, keyOf) ->
        let isNullable (c: ColumnDef) = if c.PrimaryKey || not c.Nullable then "NO" else "YES"
        let defaultCol (c: ColumnDef) = defaultText c
        let extra = extraText

        let cols: ColumnDef list = columns |> List.filter (fun c -> likeFilter likeOpt c.Name)

        if full then
            let rows =
                cols
                |> List.map (fun (c: ColumnDef) ->
                    [ Some c.Name
                      Some(columnTypeText c.Type)
                      // The column's declared/inherited collation —
                      // matching `information_schema.columns.collation_name`
                      // (`columnsRows` above).
                      (if isStringy c.Type then Some(c.Collation |> Option.defaultValue "utf8mb4_0900_ai_ci") else None)
                      Some(isNullable c)
                      Some(keyOf c)
                      defaultCol c
                      Some(extra c)
                      Some "select,insert,update,references"
                      Some c.Comment ])

            [ "Field"; "Type"; "Collation"; "Null"; "Key"; "Default"; "Extra"; "Privileges"; "Comment" ], rows
        else
            let rows =
                cols
                |> List.map (fun (c: ColumnDef) ->
                    [ Some c.Name; Some(columnTypeText c.Type); Some(isNullable c); Some(keyOf c); defaultCol c; Some(extra c) ])

            [ "Field"; "Type"; "Null"; "Key"; "Default"; "Extra" ], rows)

let private backtick (s: string) = "`" + s + "`"

let private showCreateString (s: string) =
    s.Replace("\\", "\\\\").Replace("'", "''").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\000", "\\0").Replace("\x1A", "\\Z")

// Joined with a bare comma — byte-for-byte what MySQL's own SHOW CREATE
// TABLE emits for multi-column key lists.
let private backtickCols = List.map backtick >> String.concat ","

/// Reconstructs plausible `CREATE TABLE` DDL from a table's stored metadata
/// for `SHOW CREATE TABLE` — not the original DDL text (nothing keeps that
/// around), a fresh rendering of the same columns/indexes/foreign keys, the
/// same way real MySQL's `SHOW CREATE TABLE` itself re-derives its output
/// from the catalog rather than echoing verbatim source.
let private showCreateTableDDL (temporary: bool) (catalog: Catalog) (dbName: string) (t: Table) : string =
    let columnLine (c: ColumnDef) =
        let notNull = if c.PrimaryKey || not c.Nullable then "NOT NULL" else ""

        // MySQL prints no DEFAULT clause at all on a generated column.
        let generatedPart =
            match c.Generated with
            | Some(e, k) -> sprintf "GENERATED ALWAYS AS (%s) %s" (exprToSql e) (match k with Virtual -> "VIRTUAL" | Stored -> "STORED")
            | None -> ""

        // TEXT/BLOB/JSON columns can't carry a default, so MySQL's own
        // SHOW CREATE TABLE omits the `DEFAULT NULL` it prints for other
        // nullable columns.
        let defaultless =
            match c.Type with
            | TTinyText
            | TText
            | TMediumText
            | TLongText
            | TTinyBlob
            | TBlob
            | TMediumBlob
            | TLongBlob
            | TJson
            | TGeometry _
            | TVector _ -> true
            | _ -> false

        let defaultPart =
            match defaultText c with
            | _ when c.Generated.IsSome -> ""
            | Some d when c.Default = Some DCurrentTimestamp -> sprintf "DEFAULT %s" d
            | Some d when c.Default |> Option.exists (function DExpression _ -> true | _ -> false) -> sprintf "DEFAULT %s" d
            | Some d when (match c.Type with TBit _ -> true | _ -> false) -> sprintf "DEFAULT %s" d
            | Some d -> sprintf "DEFAULT '%s'" d
            | None -> if c.PrimaryKey || not c.Nullable || defaultless then "" else "DEFAULT NULL"

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
        // under a table-level COLLATE). A charset/collation the
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
        @ [ generatedPart; notNull; defaultPart; onUpdatePart; extra; if c.Comment = "" then "" else sprintf "COMMENT '%s'" (showCreateString c.Comment) ]
        |> List.filter ((<>) "")
        |> String.concat " "

    let pkCols = Storage.primaryKeyColumns t
    let pkLine = if pkCols.IsEmpty then [] else [ sprintf "PRIMARY KEY (%s)" (backtickCols pkCols) ]

    let indexLines =
        t.Indexes
        |> List.filter (not << isPrimaryIndex)
        |> List.map (fun ix ->
            let prefix =
                if ix.Unique then "UNIQUE "
                elif ix.Kind = FullTextIndex then "FULLTEXT "
                else ""

            let columns =
                ix.KeyColumns
                |> List.map (fun column ->
                    match indexExpression column with
                    | Some expression -> "(" + expression + ")"
                    | None ->
                        let length = column.PrefixLength |> Option.map (sprintf "(%d)") |> Option.defaultValue ""
                        backtick column.Name + length)
                |> String.concat ","

            sprintf "%sKEY %s (%s)" prefix (backtick ix.Name) columns)

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

    let checkLines =
        storedCheckRows catalog
        |> List.filter (fun row ->
            System.String.Equals(Value.toText row.[1] |> Option.defaultValue "", dbName, System.StringComparison.OrdinalIgnoreCase)
            && System.String.Equals(Value.toText row.[2] |> Option.defaultValue "", t.OriginalName, System.StringComparison.OrdinalIgnoreCase))
        |> List.sortBy (fun row -> Value.toText row.[0] |> Option.defaultValue "" |> _.ToLowerInvariant())
        |> List.map (fun row ->
            let name = Value.toText row.[0] |> Option.defaultValue ""
            let clause = Value.toText row.[3] |> Option.defaultValue ""
            let enforced = Value.toText row.[4] |> Option.defaultValue "YES"
            let enforcement = if enforced.Equals("NO", System.StringComparison.OrdinalIgnoreCase) then " /*!80016 NOT ENFORCED */" else ""
            sprintf "CONSTRAINT %s CHECK (%s)%s" (backtick name) clause enforcement)

    let lines = (t.Columns |> List.map columnLine) @ pkLine @ indexLines @ fkLines @ checkLines
    let tableComment =
        if t.TableComment = "" then "" else sprintf " COMMENT='%s'" (showCreateString t.TableComment)

    sprintf
        "CREATE %sTABLE %s (\n  %s\n) ENGINE=InnoDB DEFAULT CHARSET=%s COLLATE=%s%s"
        (if temporary then "TEMPORARY " else "")
        (backtick t.OriginalName)
        (String.concat ",\n  " lines)
        tableCharset
        tableCollation
        tableComment

/// `SHOW CREATE TABLE t`.
let showCreateTable (catalog: Catalog) (dbName: string) (tableName: string) : ShowResult =
    findTable catalog dbName tableName
    |> Result.map (fun t -> [ "Table"; "Create Table" ], [ [ Some t.OriginalName; Some(showCreateTableDDL false catalog dbName t) ] ])

let showCreateTemporaryTable (catalog: Catalog) (dbName: string) (tableName: string) : ShowResult =
    findTable catalog dbName tableName
    |> Result.map (fun t -> [ "Table"; "Create Table" ], [ [ Some t.OriginalName; Some(showCreateTableDDL true catalog dbName t) ] ])

/// `SHOW CREATE VIEW v` for the read-only stored-query subset.
let showCreateView (catalog: Catalog) (dbName: string) (viewName: string) : ShowResult =
    viewCatalogEntries catalog
    |> List.tryFind (fun view ->
        System.String.Equals(view.Schema, dbName, System.StringComparison.OrdinalIgnoreCase)
        && System.String.Equals(view.Name, viewName, System.StringComparison.OrdinalIgnoreCase))
    |> function
        | None -> Error(1146, sprintf "Table '%s.%s' doesn't exist" dbName viewName)
        | Some view ->
            let checkOption =
                if view.CheckOption.Equals("NONE", System.StringComparison.OrdinalIgnoreCase) then
                    ""
                else
                    sprintf " WITH %s CHECK OPTION" view.CheckOption

            let security =
                if view.SecurityType.Equals("INVOKER", System.StringComparison.OrdinalIgnoreCase) then
                    " SQL SECURITY INVOKER"
                else
                    ""

            Ok(
                [ "View"; "Create View"; "character_set_client"; "collation_connection" ],
                [ [ Some view.Name
                    Some(sprintf "CREATE%s VIEW `%s` AS %s%s" security view.Name view.Definition checkOption)
                    Some "utf8mb4"
                    Some "utf8mb4_0900_ai_ci" ] ]
            )

let private quotedDefiner (definer: string) =
    let separator = definer.LastIndexOf '@'
    let user, host =
        if separator < 0 then definer, "%" else definer[.. separator - 1], definer[(separator + 1) ..]

    sprintf "`%s`@`%s`" (user.Replace("`", "``")) (host.Replace("`", "``"))

/// `SHOW CREATE TRIGGER trigger_name`.
let showCreateTrigger (catalog: Catalog) (dbName: string) (triggerName: string) : ShowResult =
    triggerCatalogRows catalog
    |> List.tryFind (fun trigger ->
        String.Equals(trigger.Schema, dbName, StringComparison.OrdinalIgnoreCase)
        && String.Equals(trigger.Name, triggerName, StringComparison.OrdinalIgnoreCase))
    |> function
        | None -> Error(1360, sprintf "Trigger does not exist")
        | Some trigger ->
            let ddl =
                sprintf
                    "CREATE DEFINER=%s TRIGGER `%s` %s %s ON `%s` FOR EACH ROW %s"
                    (quotedDefiner trigger.Definer)
                    (trigger.Name.Replace("`", "``"))
                    trigger.Timing
                    trigger.Event
                    (trigger.Table.Replace("`", "``"))
                    trigger.Body

            Ok(
                [ "Trigger"; "sql_mode"; "SQL Original Statement"; "character_set_client"; "collation_connection"
                  "Database Collation"; "Created" ],
                [ [ Some trigger.Name; Some triggerSqlMode; Some ddl; Some "utf8mb4"; Some "utf8mb4_0900_ai_ci"
                    Some "utf8mb4_0900_ai_ci"; Some(triggerCreatedText trigger) ] ]
            )

/// `SHOW INDEX|INDEXES|KEYS FROM t [FROM db]` — one row per index column,
/// same shape `STATISTICS` (above) projects, just scoped to one table and
/// under `SHOW`'s own (differently-cased) column names.
let showIndex (catalog: Catalog) (dbName: string) (tableName: string) : ShowResult =
    findTable catalog dbName tableName
    |> Result.map (fun t ->
        let rows =
            indexesIncludingPrimary t
            |> List.collect (fun ix ->
                ix.KeyColumns
                |> List.mapi (fun i keyColumn ->
                    let expression = indexExpression keyColumn
                    [ Some t.OriginalName
                      Some(if ix.Unique then "0" else "1")
                      Some ix.Name
                      Some(string (i + 1))
                      (if expression.IsSome then None else Some keyColumn.Name)
                      (if ix.Kind = FullTextIndex then None else Some "A")
                      Some "0"
                      (effectivePrefixLength t keyColumn |> Option.map string)
                      None
                      Some "YES"
                      Some(if ix.Kind = FullTextIndex then "FULLTEXT" else "BTREE")
                      Some ""
                      Some ""
                      Some "YES"
                      expression ]))

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
          "Index_comment"
          "Visible"
          "Expression" ],
        rows)

/// `SHOW TABLE STATUS [FROM db] [LIKE 'pattern']`.
let showTableStatus (catalog: Catalog) (dbName: string) (likeOpt: string option) : ShowResult =
    match Map.tryFind dbName catalog with
    | None when dbName.ToLowerInvariant() = "information_schema" ->
        // SYSTEM VIEWs: name plus NULL storage stats, like real MySQL.
        let rows =
            virtualTableDefs
            |> List.map fst
            |> List.filter (likeFilter likeOpt)
            |> List.map (fun n ->
                [ Some n; None; Some "10"; None; None; None; None; None; None; None; None
                  (Value.toText (VDateTime(truncateToSecond serverStartedAt))); None; None; None; None; Some ""; Some "" ])

        Ok(
            [ "Name"; "Engine"; "Version"; "Row_format"; "Rows"; "Avg_row_length"; "Data_length"
              "Max_data_length"; "Index_length"; "Data_free"; "Auto_increment"; "Create_time"
              "Update_time"; "Check_time"; "Collation"; "Checksum"; "Create_options"; "Comment" ],
            rows
        )
    | None -> Error(1049, sprintf "Unknown database '%s'" dbName)
    | Some db ->
        let tableRows =
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
                  (Value.toText (VDateTime(truncateToSecond t.CreateTime)))
                  None
                  None
                  Some(t.TableCollation |> Option.defaultValue "utf8mb4_0900_ai_ci")
                  None
                  Some ""
                  Some t.TableComment ])

        let viewRows =
            viewCatalogEntries catalog
            |> List.filter (fun view -> String.Equals(view.Schema, dbName, StringComparison.OrdinalIgnoreCase))
            |> List.filter (fun view -> likeFilter likeOpt view.Name)
            |> List.map (fun view ->
                [ Some view.Name
                  None
                  None
                  None
                  None
                  None
                  None
                  None
                  None
                  None
                  None
                  (view.Created |> Option.map (truncateToSecond >> VDateTime >> Value.toText) |> Option.flatten)
                  None
                  None
                  None
                  None
                  Some ""
                  Some "VIEW" ])

        let rows = (tableRows @ viewRows) |> List.sortBy List.head

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

/// `SHOW ENGINES` / `SHOW STORAGE ENGINES` — the `ENGINES` view's row under
/// `SHOW`'s labels.
let showEngines () : ShowResult =
    Ok(
        [ "Engine"; "Support"; "Comment"; "Transactions"; "XA"; "Savepoints" ],
        enginesRows |> List.map (fun r -> r |> Array.toList |> List.map Value.toText)
    )

/// `SHOW CHARACTER SET` / `SHOW CHARSET [LIKE 'pattern']`.
let showCharacterSet (likeOpt: string option) : ShowResult =
    let rows =
        characterSetsRows
        |> List.filter (fun r -> match r.[0] with VString n -> likeFilter likeOpt n | _ -> true)
        |> List.map (fun r -> r |> Array.toList |> List.map Value.toText)

    Ok([ "Charset"; "Default collation"; "Description"; "Maxlen" ], rows)

/// `SHOW [FULL] PROCESSLIST` — the registry's rows under `SHOW`'s labels;
/// the non-FULL form truncates `Info` to 100 chars like real MySQL.
let showProcesslist (full: bool) : ShowResult =
    let rows =
        visibleProcesses ()
        |> List.map (fun p ->
            let info =
                p.Info
                |> Option.map (fun q -> if not full && q.Length > 100 then q.Substring(0, 100) else q)

            [ Some(string p.Id)
              Some p.User
              Some p.Host
              p.Db
              Some p.Command
              Some(string (int (DateTime.Now - p.StateSince).TotalSeconds))
              Some p.State
              info ])

    Ok([ "Id"; "User"; "Host"; "db"; "Command"; "Time"; "State"; "Info" ], rows)

let showStatus
    (compression: bool)
    (bytesReceived: int64)
    (bytesSent: int64)
    (sslCipher: string option)
    (sslVersion: string option)
    (likeOpt: string option)
    : ShowResult =
    let rows =
        [ "Bytes_received", string bytesReceived
          "Bytes_sent", string bytesSent
          "Compression", if compression then "ON" else "OFF"
          "Ssl_cipher", sslCipher |> Option.defaultValue ""
          "Ssl_version", sslVersion |> Option.defaultValue ""
          "Questions", string (questions ())
          "Threads_connected", string (connectedThreads ())
          "Uptime", string (int (DateTime.Now - serverStartedAt).TotalSeconds)
          for command in reportedCommands do
              let name = commandName command
              name, string (commandCount command) ]
        |> List.filter (fun (name, _) -> likeFilter likeOpt name)
        |> List.map (fun (name, value) -> [ Some name; Some value ])

    Ok([ "Variable_name"; "Value" ], rows)

/// A `SHOW` over a per-database object catalog fsdb has no objects for
/// (`SHOW TRIGGERS`/`SHOW EVENTS`/`SHOW PROCEDURE STATUS`...) — headers
/// exactly MySQL 8.4's, rows genuinely empty; an unknown database still
/// 1049s.
let private showEmptyOf (catalog: Catalog) (dbName: string option) (headers: string list) : ShowResult =
    match dbName with
    | Some db when db.ToLowerInvariant() <> "information_schema" && not (Map.containsKey db catalog) ->
        Error(1049, sprintf "Unknown database '%s'" db)
    | _ -> Ok(headers, [])

/// `SHOW TRIGGERS [FROM db]` — headers and row shape exactly MySQL 8.4.11's
/// (write-probed), rows off the `mysql.triggers` catalog for `dbName`.
let showTriggers (catalog: Catalog) (dbName: string) : ShowResult =
    if dbName.ToLowerInvariant() <> "information_schema" && not (Map.containsKey dbName catalog) then
        Error(1049, sprintf "Unknown database '%s'" dbName)
    else
        let rows =
            triggerCatalogRows catalog
            |> List.filter (fun trigger -> String.Equals(trigger.Schema, dbName, StringComparison.OrdinalIgnoreCase))
            |> List.map (fun trigger ->
                [ Some trigger.Name; Some trigger.Event; Some trigger.Table; Some trigger.Body; Some trigger.Timing
                  Some(triggerCreatedText trigger); Some triggerSqlMode; Some trigger.Definer; Some "utf8mb4"; Some "utf8mb4_0900_ai_ci"
                  Some "utf8mb4_0900_ai_ci" ])

        Ok(
            [ "Trigger"; "Event"; "Table"; "Statement"; "Timing"; "Created"; "sql_mode"; "Definer"
              "character_set_client"; "collation_connection"; "Database Collation" ],
            rows
        )

let showEvents (catalog: Catalog) (dbName: string option) : ShowResult =
    let rows =
        eventsRows catalog
        |> List.filter (fun row -> dbName |> Option.forall (fun db -> String.Equals(toText row.[1] |> Option.defaultValue "", db, StringComparison.OrdinalIgnoreCase)))
        |> List.map (fun row ->
            [ toText row.[1]; toText row.[2]; toText row.[3]; toText row.[4]; toText row.[7]; toText row.[8]
              toText row.[9]; toText row.[10]; toText row.[12]; toText row.[13]; toText row.[14]; toText row.[20]
              toText row.[21]; toText row.[22]; toText row.[23] ])

    Ok(
        [ "Db"; "Name"; "Definer"; "Time zone"; "Type"; "Execute at"; "Interval value"; "Interval field"
          "Starts"; "Ends"; "Status"; "Originator"; "character_set_client"; "collation_connection"
          "Database Collation" ],
        rows
    )

/// `SHOW PROCEDURE STATUS` / `SHOW FUNCTION STATUS [LIKE|WHERE ...]`.
let showRoutineStatus (catalog: Catalog) : ShowResult =
    let rows =
        routinesRows catalog
        |> List.map (fun row ->
            [ toText row.[2]; toText row.[3]; Some "PROCEDURE"; Some "SQL"; toText row.[27]; toText row.[24]
              toText row.[23]; Some "DEFINER"; Some ""; Some "utf8mb4"; Some "utf8mb4_0900_ai_ci"
              Some "utf8mb4_0900_ai_ci" ])

    Ok(
        [ "Db"; "Name"; "Type"; "Language"; "Definer"; "Modified"; "Created"; "Security_type"; "Comment"
          "character_set_client"; "collation_connection"; "Database Collation" ],
        rows
    )
