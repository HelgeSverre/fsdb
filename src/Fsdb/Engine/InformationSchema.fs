/// `information_schema` virtual tables projected from `Storage.Catalog` at
/// query time. `FROM information_schema.<table>` resolves here instead of
/// materializing metadata in the catalog.
module Fsdb.InformationSchema

open System
open System.Text.Json
open System.Text.RegularExpressions
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Engine
open Fsdb.Sql

let private eqI left right = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

let private col (name: string) (ty: ColumnType) : ColumnDef =
    { Name = name
      Type = ty
      NumericDisplay = None
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
let private requiredCol name ty = { col name ty with Nullable = false }

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

let columnTypeTextOfColumn (column: ColumnDef) : string =
    match column.Type, column.NumericDisplay with
    | (TTinyInt _ | TSmallInt _ | TMediumInt _ | TInt _ | TBigInt _), Some { Width = Some width; ZeroFill = true } ->
        let name =
            match column.Type with
            | TTinyInt _ -> "tinyint"
            | TSmallInt _ -> "smallint"
            | TMediumInt _ -> "mediumint"
            | TInt _ -> "int"
            | _ -> "bigint"

        sprintf "%s(%d) unsigned zerofill" name width
    | TDecimal(precision, scale, _), Some { ZeroFill = true } ->
        sprintf "decimal(%d,%d) unsigned zerofill" precision scale
    | (TFloat _ | TDouble _), Some display ->
        let name =
            match column.Type with
            | TFloat _ -> "float"
            | _ -> "double"

        let unsigned =
            match column.Type with
            | TFloat true
            | TDouble true -> " unsigned"
            | _ -> ""

        let size =
            match display.Width, display.Decimals with
            | Some width, Some decimals -> sprintf "(%d,%d)" width decimals
            | _ -> ""

        name + size + (if display.ZeroFill then " unsigned zerofill" else unsigned)
    | _ -> columnTypeText column.Type

/// MySQL reports declared binary/text widths directly, while ENUM and SET
/// expose the longest value they can render rather than their storage size.
let private charMaxLength (ty: ColumnType) : int64 option =
    let length (text: string) = text.EnumerateRunes() |> Seq.length |> int64

    match ty with
    | TChar n
    | TVarchar n
    | TBinary n
    | TVarBinary n -> Some(int64 n)
    | TTinyText
    | TTinyBlob -> Some 255L
    | TText
    | TBlob -> Some 65535L
    | TMediumText
    | TMediumBlob -> Some 16777215L
    | TLongText
    | TLongBlob -> Some 4294967295L
    | TEnum values ->
        values
        |> List.map length
        |> function
            | [] -> Some 0L
            | lengths -> Some(List.max lengths)
    | TSet values ->
        values
        |> List.map length
        |> function
            | [] -> Some 0L
            | lengths -> Some(List.sum lengths + int64 lengths.Length - 1L)
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
    | TBigInt unsigned -> Some((if unsigned then 20L else 19L), 0L)
    | TBit width -> Some(int64 width, 0L)
    | TDecimal(p, s, _) -> Some(int64 p, int64 s)
    | TFloat _ -> Some(12L, 0L)
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

/// Declared character widths expand by the charset's maximum encoded width;
/// the TEXT/BLOB families already carry byte ceilings in their type.
let private charOctetLength charset (ty: ColumnType) : int64 option =
    charMaxLength ty
    |> Option.map (fun n ->
        match ty with
        | TChar _
        | TVarchar _
        | TEnum _
        | TSet _ -> n * int64 (Collation.maxBytesPerCharacter charset)
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
       (charOctetLength c.Charset c.Type |> Option.map VInt |> Option.defaultValue VNull)
       (precision |> Option.map VInt |> Option.defaultValue VNull)
       (scale |> Option.map VInt |> Option.defaultValue VNull)
       (datetimePrecision c.Type |> Option.map VInt |> Option.defaultValue VNull)
       (if isStringy c.Type then vs (c.Charset |> Option.defaultValue "utf8mb4") else VNull)
       (if isStringy c.Type then vs (c.Collation |> Option.defaultValue "utf8mb4_0900_ai_ci") else VNull)
       vs (columnTypeTextOfColumn c)
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
              KeyColumns = indexColumns columns
              Unique = true
              Visible = true
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
    | Some Uppercase -> Some(sprintf "upper(`%s`)" (keyColumn.Name.Replace("`", "``")))
    | Some(Expression expression) -> Some(exprToSql expression)
    | None -> None

let private indexDirectionText (keyColumn: IndexColumn) =
    if keyColumn.Direction = Desc then "D" else "A"

let private indexVisibilityText (index: IndexDef) =
    if index.Visible then "YES" else "NO"

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
                   (if ix.Kind = FullTextIndex then VNull else vs (indexDirectionText keyColumn))
                   vi 0
                   (effectivePrefixLength t keyColumn |> Option.map vi |> Option.defaultValue VNull)
                   VNull
                   vs nullable
                   vs (if ix.Kind = FullTextIndex then "FULLTEXT" else "BTREE")
                   vs ""
                   vs ""
                   vs (indexVisibilityText ix)
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
                let referencedSchema = fk.RefDatabase |> Option.defaultValue dbName

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
                       vs referencedSchema
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
            let referencedSchema = fk.RefDatabase |> Option.defaultValue dbName

            [| vs "def"
               vs dbName
               vs fk.Name
               vs "def"
               vs referencedSchema
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
type private Viewer =
    { Store: Store
      Account: Fsdb.Auth.Account
      ActiveRoles: Fsdb.Auth.Account list }

let private currentViewer = System.Threading.AsyncLocal<Viewer option>()

let currentViewerAccount () = currentViewer.Value |> Option.map _.Account

/// Runs a query with information-schema visibility scoped to one account.
let withViewer store account activeRoles (body: unit -> 'a) : 'a =
    DynamicScope.withValue
        currentViewer
        (Some
            { Store = store
              Account = account
              ActiveRoles = activeRoles })
        body

let private viewerHasPrivilege privilege target =
    match currentViewer.Value with
    | None -> true
    | Some viewer ->
        Fsdb.Auth.checkForAccountWithRoles viewer.Store viewer.Account viewer.ActiveRoles [ privilege, target ]
        |> Result.isOk

/// Whether the current viewer may read PROCESS-restricted metadata.
let canViewProcessMetadata () = viewerHasPrivilege "PROCESS" Fsdb.Auth.Global

/// The account a viewer is limited to, or `None` when it may see all rows
/// (embedded/internal, or it holds `priv`).
let private restrictedTo (priv: string) : Fsdb.Auth.Account option =
    match currentViewer.Value with
    | Some viewer when
        not (
            Fsdb.Auth.hasGlobalPrivForAccountWithRoles
                viewer.Store
                viewer.Account
                viewer.ActiveRoles
                priv
        )
        ->
        Some viewer.Account
    | _ -> None

/// Stamped by `Server.listen` — `SHOW STATUS`'s `Uptime` baseline.
let mutable serverStartedAt = DateTime.Now

type private AtomicCounter() =
    let mutable value = 0L

    member _.Increment() = Threading.Interlocked.Increment(&value) |> ignore
    member _.Value = Threading.Interlocked.Read(&value)
    member _.Reset() = Threading.Interlocked.Exchange(&value, 0L) |> ignore

[<Struct>]
type StatusCommand = private StatusCommand of string

[<RequireQualifiedAccess>]
module StatusCommand =
    let private create suffix = StatusCommand(sprintf "Com_%s" suffix)

    let adminCommands = create "admin_commands"
    let alterDatabase = create "alter_db"
    let alterEvent = create "alter_event"
    let alterServer = create "alter_server"
    let alterTable = create "alter_table"
    let alterUser = create "alter_user"
    let alterUserDefaultRole = create "alter_user_default_role"
    let analyze = create "analyze"
    let beginTransaction = create "begin"
    let callProcedure = create "call_procedure"
    let changeDatabase = create "change_db"
    let check = create "check"
    let checksum = create "checksum"
    let commit = create "commit"
    let createDatabase = create "create_db"
    let createEvent = create "create_event"
    let createFunction = create "create_function"
    let createIndex = create "create_index"
    let createProcedure = create "create_procedure"
    let createRole = create "create_role"
    let createServer = create "create_server"
    let createTable = create "create_table"
    let createTrigger = create "create_trigger"
    let createUser = create "create_user"
    let createView = create "create_view"
    let deallocateSql = create "dealloc_sql"
    let delete = create "delete"
    let deleteMulti = create "delete_multi"
    let doStatement = create "do"
    let dropDatabase = create "drop_db"
    let dropEvent = create "drop_event"
    let dropFunction = create "drop_function"
    let dropIndex = create "drop_index"
    let dropProcedure = create "drop_procedure"
    let dropRole = create "drop_role"
    let dropServer = create "drop_server"
    let dropTable = create "drop_table"
    let dropTrigger = create "drop_trigger"
    let dropUser = create "drop_user"
    let dropView = create "drop_view"
    let emptyQuery = create "empty_query"
    let executeSql = create "execute_sql"
    let flush = create "flush"
    let getDiagnostics = create "get_diagnostics"
    let grant = create "grant"
    let grantRoles = create "grant_roles"
    let handlerClose = create "ha_close"
    let handlerOpen = create "ha_open"
    let handlerRead = create "ha_read"
    let insert = create "insert"
    let insertSelect = create "insert_select"
    let kill = create "kill"
    let load = create "load"
    let lockTables = create "lock_tables"
    let optimize = create "optimize"
    let prepareSql = create "prepare_sql"
    let releaseSavepoint = create "release_savepoint"
    let renameTable = create "rename_table"
    let renameUser = create "rename_user"
    let repair = create "repair"
    let replace = create "replace"
    let replaceSelect = create "replace_select"
    let revoke = create "revoke"
    let revokeRoles = create "revoke_roles"
    let rollback = create "rollback"
    let rollbackToSavepoint = create "rollback_to_savepoint"
    let savepoint = create "savepoint"
    let select = create "select"
    let setOption = create "set_option"
    let setPassword = create "set_password"
    let setRole = create "set_role"
    let showBinaryLogs = create "show_binlogs"
    let showCharacterSets = create "show_charsets"
    let showCollations = create "show_collations"
    let showCreateDatabase = create "show_create_db"
    let showCreateEvent = create "show_create_event"
    let showCreateFunction = create "show_create_func"
    let showCreateProcedure = create "show_create_proc"
    let showCreateTable = create "show_create_table"
    let showCreateTrigger = create "show_create_trigger"
    let showCreateUser = create "show_create_user"
    let showDatabases = create "show_databases"
    let showEngineStatus = create "show_engine_status"
    let showErrors = create "show_errors"
    let showEvents = create "show_events"
    let showFields = create "show_fields"
    let showFunctionStatus = create "show_function_status"
    let showGrants = create "show_grants"
    let showIndexes = create "show_keys"
    let showBinaryLogStatus = create "show_binary_log_status"
    let showOpenTables = create "show_open_tables"
    let showPlugins = create "show_plugins"
    let showPrivileges = create "show_privileges"
    let showProcedureStatus = create "show_procedure_status"
    let showProcesslist = create "show_processlist"
    let showReplicaStatus = create "show_replica_status"
    let showStatus = create "show_status"
    let showStorageEngines = create "show_storage_engines"
    let showTableStatus = create "show_table_status"
    let showTables = create "show_tables"
    let showTriggers = create "show_triggers"
    let showVariables = create "show_variables"
    let showWarnings = create "show_warnings"
    let shutdown = create "shutdown"
    let statementClose = create "stmt_close"
    let statementExecute = create "stmt_execute"
    let statementFetch = create "stmt_fetch"
    let statementPrepare = create "stmt_prepare"
    let statementReset = create "stmt_reset"
    let statementSendLongData = create "stmt_send_long_data"
    let truncate = create "truncate"
    let unlockTables = create "unlock_tables"
    let update = create "update"
    let updateMulti = create "update_multi"
    let xaCommit = create "xa_commit"
    let xaEnd = create "xa_end"
    let xaPrepare = create "xa_prepare"
    let xaRecover = create "xa_recover"
    let xaRollback = create "xa_rollback"
    let xaStart = create "xa_start"

let private reportedCommandNames =
    [ "admin_commands"
      "assign_to_keycache"
      "alter_db"
      "alter_event"
      "alter_function"
      "alter_instance"
      "alter_procedure"
      "alter_resource_group"
      "alter_server"
      "alter_table"
      "alter_tablespace"
      "alter_user"
      "alter_user_default_role"
      "analyze"
      "begin"
      "binlog"
      "call_procedure"
      "change_db"
      "change_repl_filter"
      "change_replication_source"
      "check"
      "checksum"
      "clone"
      "commit"
      "create_db"
      "create_event"
      "create_function"
      "create_index"
      "create_procedure"
      "create_role"
      "create_server"
      "create_table"
      "create_resource_group"
      "create_trigger"
      "create_udf"
      "create_user"
      "create_view"
      "create_spatial_reference_system"
      "dealloc_sql"
      "delete"
      "delete_multi"
      "do"
      "drop_db"
      "drop_event"
      "drop_function"
      "drop_index"
      "drop_procedure"
      "drop_resource_group"
      "drop_role"
      "drop_server"
      "drop_spatial_reference_system"
      "drop_table"
      "drop_trigger"
      "drop_user"
      "drop_view"
      "empty_query"
      "execute_sql"
      "explain_other"
      "flush"
      "get_diagnostics"
      "grant"
      "grant_roles"
      "ha_close"
      "ha_open"
      "ha_read"
      "help"
      "import"
      "insert"
      "insert_select"
      "install_component"
      "install_plugin"
      "kill"
      "load"
      "lock_instance"
      "lock_tables"
      "optimize"
      "preload_keys"
      "prepare_sql"
      "purge"
      "purge_before_date"
      "release_savepoint"
      "rename_table"
      "rename_user"
      "repair"
      "replace"
      "replace_select"
      "reset"
      "resignal"
      "restart"
      "revoke"
      "revoke_all"
      "revoke_roles"
      "rollback"
      "rollback_to_savepoint"
      "savepoint"
      "select"
      "set_option"
      "set_password"
      "set_resource_group"
      "set_role"
      "signal"
      "show_binlog_events"
      "show_binlogs"
      "show_charsets"
      "show_collations"
      "show_create_db"
      "show_create_event"
      "show_create_func"
      "show_create_proc"
      "show_create_table"
      "show_create_trigger"
      "show_databases"
      "show_engine_logs"
      "show_engine_mutex"
      "show_engine_status"
      "show_events"
      "show_errors"
      "show_fields"
      "show_function_code"
      "show_function_status"
      "show_grants"
      "show_keys"
      "show_binary_log_status"
      "show_open_tables"
      "show_parse_tree"
      "show_plugins"
      "show_privileges"
      "show_procedure_code"
      "show_procedure_status"
      "show_processlist"
      "show_profile"
      "show_profiles"
      "show_relaylog_events"
      "show_replicas"
      "show_replica_status"
      "show_status"
      "show_storage_engines"
      "show_table_status"
      "show_tables"
      "show_triggers"
      "show_variables"
      "show_warnings"
      "show_create_user"
      "shutdown"
      "replica_start"
      "replica_stop"
      "group_replication_start"
      "group_replication_stop"
      "stmt_execute"
      "stmt_close"
      "stmt_fetch"
      "stmt_prepare"
      "stmt_reset"
      "stmt_send_long_data"
      "truncate"
      "uninstall_component"
      "uninstall_plugin"
      "unlock_instance"
      "unlock_tables"
      "update"
      "update_multi"
      "xa_commit"
      "xa_end"
      "xa_prepare"
      "xa_recover"
      "xa_rollback"
      "xa_start"
      "stmt_reprepare" ]
    |> List.map (sprintf "Com_%s")

type StatusCounters internal (id: int64) =
    let questionCount = AtomicCounter()

    let commandCounters =
        reportedCommandNames
        |> List.map (fun name -> name, AtomicCounter())
        |> Map.ofList

    member internal _.RecordQuestion() = questionCount.Increment()
    member internal _.Id = id
    member internal _.Questions = questionCount.Value
    member internal _.RecordCommand name = commandCounters.[name].Increment()
    member internal _.CommandCount name = commandCounters.[name].Value

    member internal _.ResetQuestions() = questionCount.Reset()

    member internal _.ResetCommands() =
        commandCounters |> Map.iter (fun _ counter -> counter.Reset())

    member internal this.Reset() =
        this.ResetQuestions()
        this.ResetCommands()

let private processStatusCounters = StatusCounters 0L
let private statusCounterId = ref 0L

let private sessionStatusCounters =
    Collections.Concurrent.ConcurrentDictionary<int64, WeakReference<StatusCounters>>()

let createStatusCounters () =
    let id = Threading.Interlocked.Increment statusCounterId
    let counters = StatusCounters id
    sessionStatusCounters.[id] <- WeakReference<StatusCounters>(counters)
    counters

let releaseStatusCounters (counters: StatusCounters) =
    sessionStatusCounters.TryRemove counters.Id |> ignore

let resetSessionStatuses () =
    sessionStatusCounters
    |> Seq.iter (fun (KeyValue(id, reference)) ->
        match reference.TryGetTarget() with
        | true, counters -> counters.Reset()
        | _ -> sessionStatusCounters.TryRemove id |> ignore)

let recordQuestion (sessionCounters: StatusCounters) =
    processStatusCounters.RecordQuestion()
    sessionCounters.RecordQuestion()

let questions () = processStatusCounters.Questions

let recordCommand (sessionCounters: StatusCounters) (StatusCommand name) =
    processStatusCounters.RecordCommand name
    sessionCounters.RecordCommand name

let resetQuestions () = processStatusCounters.ResetQuestions()

let resetCommandCounts () = processStatusCounters.ResetCommands()

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
// Stored-object catalogs projected from their row-backed mysql tables.
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
                    && select.Locking.IsEmpty
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

let private viewTableUsageColumns =
    [ col "VIEW_CATALOG" (TVarchar 64)
      col "VIEW_SCHEMA" (TVarchar 64)
      col "VIEW_NAME" (TVarchar 64)
      col "TABLE_CATALOG" (TVarchar 64)
      col "TABLE_SCHEMA" (TVarchar 64)
      col "TABLE_NAME" (TVarchar 64) ]

let private viewerCanShowView schema name =
    match currentViewer.Value with
    | None -> true
    | Some viewer ->
        Fsdb.Auth.checkForAccountWithRoles
            viewer.Store
            viewer.Account
            viewer.ActiveRoles
            [ "SHOW VIEW", Fsdb.Auth.OnTable(schema, name) ]
        |> Result.isOk

let private viewTableUsageRows (catalog: Catalog) =
    let visible viewSchema viewName tableSchema tableName =
        match currentViewer.Value with
        | None -> true
        | Some viewer ->
            viewerCanShowView viewSchema viewName
            && Fsdb.Auth.canSeeTableForAccountWithRoles
                viewer.Store
                viewer.Account
                viewer.ActiveRoles
                tableSchema
                tableName

    viewCatalogEntries catalog
    |> List.collect (fun view ->
        match Parser.parseViewDefinition view.Definition with
        | Error _ -> []
        | Ok definition ->
            Fsdb.Auth.requiredPrivileges view.Schema definition.Statement
            |> List.choose (function
                | "SELECT", Fsdb.Auth.OnTable(tableSchema, tableName) ->
                    Some(tableSchema, tableName)
                | _ -> None)
            |> List.distinctBy (fun (schema, table) -> schema.ToLowerInvariant(), table.ToLowerInvariant())
            |> List.filter (fun (schema, table) -> visible view.Schema view.Name schema table)
            |> List.map (fun (schema, table) ->
                [| vs "def"; vs view.Schema; vs view.Name; vs "def"; vs schema; vs table |]))

let private viewRoutineUsageColumns =
    [ col "TABLE_CATALOG" (TVarchar 64)
      col "TABLE_SCHEMA" (TVarchar 64)
      col "TABLE_NAME" (TVarchar 64)
      col "SPECIFIC_CATALOG" (TVarchar 64)
      col "SPECIFIC_SCHEMA" (TVarchar 64)
      requiredCol "SPECIFIC_NAME" (TVarchar 64) ]

let rec private calledFunctionsInExpression (expression: Expr) : string list =
    let direct =
        Fsdb.Sql.Expression.collect
            (function
            | FuncCall(name, _) -> Some name
            | _ -> None)
            expression

    direct
    @ (Fsdb.Sql.Expression.collectSubqueries expression
       |> List.collect calledFunctionsInSelect)

and private calledFunctionsInFromItem (item: FromItem) : string list =
    match item with
    | FromTable _ -> []
    | FromSubquery(query, _)
    | FromLateral(query, _) -> calledFunctionsInSelectOrUnion query
    | FromJsonTable(source, _, _, _) -> calledFunctionsInExpression source

and private calledFunctionsInSelect (select: SelectStmt) : string list =
    let expressions =
        (select.Projections |> List.map fst)
        @ Option.toList select.Where
        @ Option.toList select.Having
        @ select.GroupBy
        @ (select.OrderBy |> List.map fst)
        @ Option.toList select.Limit
        @ Option.toList select.Offset
        @ (select.Joins |> List.map _.On)
        @ (select.Windows
           |> List.collect (snd >> OverSpec >> Fsdb.Sql.Expression.overExpressions))

    (expressions |> List.collect calledFunctionsInExpression)
    @ (select.From |> Option.map calledFunctionsInFromItem |> Option.defaultValue [])
    @ (select.Joins |> List.collect (_.Table >> calledFunctionsInFromItem))
    @ (select.Ctes |> List.collect (_.Body >> calledFunctionsInSelectOrUnion))

and private calledFunctionsInSelectOrUnion (query: SelectOrUnion) : string list =
    match query with
    | PlainSelect select -> calledFunctionsInSelect select
    | UnionSelect(first, rest, orderBy, limit, offset) ->
        calledFunctionsInSelect first
        @ (rest |> List.collect (snd >> calledFunctionsInSelect))
        @ (orderBy |> List.collect (fst >> calledFunctionsInExpression))
        @ (limit |> Option.map calledFunctionsInExpression |> Option.defaultValue [])
        @ (offset |> Option.map calledFunctionsInExpression |> Option.defaultValue [])

let private viewRoutineUsageRows (catalog: Catalog) =
    let functions =
        mysqlTable catalog "functions"
        |> Option.map (fun table ->
            table.RowsArray
            |> Seq.choose SystemCatalog.StoredFunction.tryRead
            |> List.ofSeq)
        |> Option.defaultValue []

    let resolve (viewSchema: string) (name: string) =
        let separator = name.IndexOf('.')

        let schema, functionName, qualified =
            if separator < 0 then
                viewSchema, name, false
            else
                name.Substring(0, separator), name.Substring(separator + 1), true

        let native =
            not qualified
            && (Functions.lookup functionName Functions.builtins |> Option.isSome
                || Functions.lookupAggregate functionName Functions.builtins |> Option.isSome)

        if native then
            None
        else
            functions
            |> List.tryFind (fun routine -> eqI routine.Schema schema && eqI routine.Name functionName)
            |> Option.map (fun routine -> routine.Schema, routine.Name)

    let canSee viewSchema viewName routineSchema =
        match currentViewer.Value with
        | None -> true
        | Some viewer ->
            viewerCanShowView viewSchema viewName
            && (Fsdb.Auth.checkForAccountWithRoles
                    viewer.Store
                    viewer.Account
                    viewer.ActiveRoles
                    [ "EXECUTE", Fsdb.Auth.OnDb routineSchema ]
                |> Result.isOk)

    viewCatalogEntries catalog
    |> List.collect (fun view ->
        match Parser.parseViewDefinition view.Definition with
        | Error _ -> []
        | Ok definition ->
            let calls =
                match definition.Statement with
                | Select select -> calledFunctionsInSelect select
                | Union(first, rest, orderBy, limit, offset) ->
                    calledFunctionsInSelectOrUnion(UnionSelect(first, rest, orderBy, limit, offset))
                | _ -> []

            calls
            |> List.choose (resolve view.Schema)
            |> List.distinctBy (fun (schema, name) -> schema.ToLowerInvariant(), name.ToLowerInvariant())
            |> List.filter (fun (schema, _) -> canSee view.Schema view.Name schema)
            |> List.map (fun (schema, name) ->
                [| vs "def"; vs view.Schema; vs view.Name; vs "def"; vs schema; vs name |]))

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

let private routineAccess schema definer =
    match currentViewer.Value, Fsdb.Auth.tryParseAccount definer with
    | None, _ -> true, true
    | Some _, None -> false, false
    | Some viewer, Some owner ->
        let ownsRoutine = Fsdb.Auth.sameAccount viewer.Account owner
        let seesDefinitions =
            ownsRoutine
            || Fsdb.Auth.hasGlobalPrivForAccountWithRoles viewer.Store viewer.Account viewer.ActiveRoles "SELECT"
            || (Fsdb.Auth.checkForAccountWithRoles
                    viewer.Store
                    viewer.Account
                    viewer.ActiveRoles
                    [ "ALTER ROUTINE", Fsdb.Auth.OnDb schema ]
                |> Result.isOk)

        let canExecute =
            Fsdb.Auth.checkForAccountWithRoles
                viewer.Store
                viewer.Account
                viewer.ActiveRoles
                [ "EXECUTE", Fsdb.Auth.OnDb schema ]
            |> Result.isOk

        seesDefinitions || canExecute, seesDefinitions

let private routineVisible schema definer = routineAccess schema definer |> fst
let private routineDefinitionVisible schema definer = routineAccess schema definer |> snd

let private routinesRows (catalog: Catalog) =
    let procedures =
        mysqlTable catalog "routines"
        |> Option.map (fun table ->
            table.RowsArray
            |> Seq.choose SystemCatalog.Routine.tryRead
            |> Seq.filter (fun routine -> routineVisible routine.Schema routine.Definer)
            |> Seq.map (fun routine ->
                let created = routine.Created |> Option.map VDateTime |> Option.defaultValue VNull

                [| vs routine.Name; vs "def"; vs routine.Schema; vs routine.Name; vs "PROCEDURE"; vs ""; VNull; VNull
                   VNull; VNull; VNull; VNull; VNull; VNull; vs "SQL"
                   (if routineDefinitionVisible routine.Schema routine.Definer then vs routine.Definition else VNull)
                   VNull; vs "SQL"; vs "SQL"
                   vs "NO"; vs "CONTAINS SQL"; VNull; vs routine.SecurityType; created; created; vs routine.SqlMode
                   vs ""; vs routine.Definer; vs routine.CharacterSetClient; vs routine.CollationConnection
                   vs routine.DatabaseCollation |])
            |> List.ofSeq)
        |> Option.defaultValue []

    let functions =
        mysqlTable catalog "functions"
        |> Option.map (fun table ->
            table.RowsArray
            |> Seq.choose SystemCatalog.StoredFunction.tryRead
            |> Seq.filter (fun routine -> routineVisible routine.Schema routine.Definer)
            |> Seq.map (fun routine ->
                let created = routine.Created |> Option.map VDateTime |> Option.defaultValue VNull
                let columnType = Parser.parseColumnType routine.ReturnType |> Result.toOption
                let dataType = columnType |> Option.map dataTypeName |> Option.defaultValue routine.ReturnType
                let characterLength = columnType |> Option.bind charMaxLength
                let numeric = columnType |> Option.bind numericPrecisionScale
                let temporal = columnType |> Option.bind datetimePrecision
                let isCharacter = columnType |> Option.exists isStringy

                [| vs routine.Name; vs "def"; vs routine.Schema; vs routine.Name; vs "FUNCTION"; vs dataType
                   characterLength |> Option.map VInt |> Option.defaultValue VNull
                   columnType |> Option.bind (charOctetLength None) |> Option.map VInt |> Option.defaultValue VNull
                   numeric |> Option.map (fst >> VInt) |> Option.defaultValue VNull
                   numeric |> Option.map (snd >> VInt) |> Option.defaultValue VNull
                   temporal |> Option.map VInt |> Option.defaultValue VNull
                   if isCharacter then vs "utf8mb4" else VNull
                   if isCharacter then vs routine.CollationConnection else VNull
                   vs routine.ReturnType; vs "SQL"
                   (if routineDefinitionVisible routine.Schema routine.Definer then vs routine.Definition else VNull)
                   VNull; vs "SQL"; vs "SQL"
                   vs (if routine.Deterministic then "YES" else "NO"); vs routine.SqlDataAccess; VNull
                   vs routine.SecurityType; created; created; vs routine.SqlMode; vs ""; vs routine.Definer
                   vs routine.CharacterSetClient; vs routine.CollationConnection; vs routine.DatabaseCollation |])
            |> List.ofSeq)
        |> Option.defaultValue []

    procedures @ functions

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

let private parameterCharacterMetadata fallbackCollation (parameter: StoredProgram.Parameter) =
    let binaryCharset =
        parameter.Charset
        |> Option.exists (fun charset -> charset.Equals("binary", StringComparison.OrdinalIgnoreCase))

    if isStringy parameter.ColumnType && not binaryCharset then
        let charset =
            parameter.Charset
            |> Option.orElseWith (fun () -> parameter.Collation |> Option.map Collation.charsetOfCollation)
            |> Option.defaultWith (fun () -> Collation.charsetOfCollation fallbackCollation)

        let collation =
            parameter.Collation
            |> Option.orElseWith (fun () ->
                parameter.Charset
                |> Option.map Collation.defaultNameForCharset)
            |> Option.defaultValue fallbackCollation

        Some charset, Some collation
    else
        None, None

let private parameterRow
    routineType
    schema
    name
    fallbackCollation
    ordinal
    mode
    parameterName
    (parameter: StoredProgram.Parameter)
    =
    let characterLength = charMaxLength parameter.ColumnType
    let charset, collation = parameterCharacterMetadata fallbackCollation parameter

    let octetLength = charOctetLength charset parameter.ColumnType

    let numeric = numericPrecisionScale parameter.ColumnType

    [| vs "def"
       vs schema
       vs name
       VInt(int64 ordinal)
       mode |> Option.map vs |> Option.defaultValue VNull
       parameterName |> Option.map vs |> Option.defaultValue VNull
       vs (dataTypeName parameter.ColumnType)
       characterLength |> Option.map VInt |> Option.defaultValue VNull
       octetLength |> Option.map VInt |> Option.defaultValue VNull
       numeric |> Option.map (fst >> VInt) |> Option.defaultValue VNull
       numeric |> Option.map (snd >> VInt) |> Option.defaultValue VNull
       datetimePrecision parameter.ColumnType |> Option.map VInt |> Option.defaultValue VNull
       charset |> Option.map vs |> Option.defaultValue VNull
       collation |> Option.map vs |> Option.defaultValue VNull
       vs (columnTypeText parameter.ColumnType)
       vs routineType |]

let private parameterMode =
    function
    | StoredProgram.In -> "IN"
    | StoredProgram.Out -> "OUT"
    | StoredProgram.InOut -> "INOUT"

type private ParameterRoutine =
    { RoutineType: string
      Schema: string
      Name: string
      Definer: string
      Parameters: string
      ReturnType: string option
      SqlMode: string
      CollationConnection: string }

let private parametersRows (catalog: Catalog) =
    let options sqlMode = SqlMode.parserOptionsFor sqlMode

    let procedures =
        mysqlTable catalog "routines"
        |> Option.map (fun table ->
            table.RowsArray
            |> Seq.choose SystemCatalog.Routine.tryRead
            |> Seq.map (fun routine ->
                { RoutineType = "PROCEDURE"
                  Schema = routine.Schema
                  Name = routine.Name
                  Definer = routine.Definer
                  Parameters = routine.Parameters
                  ReturnType = None
                  SqlMode = routine.SqlMode
                  CollationConnection = routine.CollationConnection })
            |> List.ofSeq)
        |> Option.defaultValue []

    let functions =
        mysqlTable catalog "functions"
        |> Option.map (fun table ->
            table.RowsArray
            |> Seq.choose SystemCatalog.StoredFunction.tryRead
            |> Seq.map (fun routine ->
                { RoutineType = "FUNCTION"
                  Schema = routine.Schema
                  Name = routine.Name
                  Definer = routine.Definer
                  Parameters = routine.Parameters
                  ReturnType = Some routine.ReturnType
                  SqlMode = routine.SqlMode
                  CollationConnection = routine.CollationConnection })
            |> List.ofSeq)
        |> Option.defaultValue []

    procedures @ functions
    |> List.filter (fun routine -> routineVisible routine.Schema routine.Definer)
    |> List.collect (fun routine ->
        let parsedParameters =
            StoredProgram.parseParameters (options routine.SqlMode) routine.Parameters

        let resultRows =
            match routine.ReturnType with
            | None -> Ok []
            | Some returnType ->
                Parser.parseRoutineParameterTypeWithOptions (options routine.SqlMode) returnType
                |> Result.map (fun (columnType, charset, collation) ->
                    let parameter: StoredProgram.Parameter =
                        { Name = ""
                          DisplayName = ""
                          ColumnType = columnType
                          Charset = charset
                          Collation = collation
                          Mode = StoredProgram.In }

                    [ parameterRow
                          routine.RoutineType
                          routine.Schema
                          routine.Name
                          routine.CollationConnection
                          0
                          None
                          None
                          parameter ])

        match resultRows, parsedParameters with
        | Ok result, Ok parameters ->
            result
            @ (parameters
               |> List.mapi (fun index parameter ->
                   parameterRow
                       routine.RoutineType
                       routine.Schema
                       routine.Name
                       routine.CollationConnection
                       (index + 1)
                       (Some(parameterMode parameter.Mode))
                       (Some parameter.DisplayName)
                       parameter))
        | _ -> [])

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
           triggerCreatedValue trigger; vs trigger.SqlMode; vs trigger.Definer; vs trigger.CharacterSetClient
           vs trigger.CollationConnection; vs trigger.DatabaseCollation |])

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
    mysqlTable catalog "events"
    |> Option.map (fun table ->
        table.RowsArray
        |> Seq.choose SystemCatalog.Event.tryRead
        |> Seq.map (fun event ->
            let eventType, intervalValue, intervalField =
                match event.IntervalValue, event.IntervalField with
                | Some value, Some field -> vs "RECURRING", vs value, vs field
                | _ -> vs "ONE TIME", VNull, VNull

            let created = event.Created |> Option.map VDateTime |> Option.defaultValue VNull
            let lastAltered = event.LastAltered |> Option.map VDateTime |> Option.defaultValue VNull
            let lastExecuted = event.LastExecuted |> Option.map VDateTime |> Option.defaultValue VNull

            [| vs "def"; vs event.Schema; vs event.Name; vs event.Definer; vs event.TimeZone; vs "SQL"; vs event.Definition
               eventType; event.ExecuteAt |> Option.map VDateTime |> Option.defaultValue VNull
               intervalValue; intervalField; vs event.SqlMode
               event.Starts |> Option.map VDateTime |> Option.defaultValue VNull
               event.Ends |> Option.map VDateTime |> Option.defaultValue VNull
               vs (Fsdb.Sql.Event.statusText event.Status); vs event.OnCompletion; created; lastAltered; lastExecuted
               vs event.Comment; VInt event.Originator; vs event.CharacterSetClient; vs event.CollationConnection
               vs event.DatabaseCollation |])
        |> List.ofSeq)
    |> Option.defaultValue []

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
    |> List.collect (fun (dbName, t) ->
        let row partitionName ordinal methodName expression rowCount =
            [| vs "def"
               vs dbName
               vs t.OriginalName
               partitionName
               VNull
               ordinal
               VNull
               methodName
               VNull
               expression
               VNull
               VNull
               vi rowCount
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
               VNull |]

        match t.Partitioning with
        | None -> [ row VNull VNull VNull VNull t.RowsArray.Length ]
        | Some partitioning ->
            let counts = Array.zeroCreate<int> (int partitioning.Count)

            match partitioning.Expression with
            | Col name
            | QualifiedCol(_, name) ->
                match resolveColumn t.Columns name with
                | Ok index ->
                    for stored in t.RowsArray do
                        let partition = hashPartitionIndex partitioning stored.[index]
                        counts.[int partition] <- counts.[int partition] + 1
                | Error _ -> ()
            | _ -> ()

            [ for index in 0u .. partitioning.Count - 1u ->
                  row
                      (vs (sprintf "p%d" index))
                      (vi (int index + 1))
                      (vs (if partitioning.Linear then "LINEAR HASH" else "HASH"))
                      (vs (exprToSql partitioning.Expression))
                      counts.[int index] ])

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
                let user = rowText row userIdx
                let host = rowText row hostIdx
                let grantee = sprintf "'%s'@'%s'" user host
                let grantable = if rowText row grantIdx = "Y" then "YES" else "NO"

                let granted =
                    Fsdb.Auth.staticPrivileges
                    |> List.filter (fun d ->
                        colIdx t d.UserCol |> Option.map (fun i -> rowText row i = "Y") |> Option.defaultValue false)

                let staticRows =
                    let privNames = if granted.IsEmpty then [ "USAGE" ] else granted |> List.map (fun d -> d.Sql)
                    privNames |> List.map (fun p -> [| vs grantee; vs "def"; vs p; vs grantable |])

                let dynamicRows =
                    match mysqlTable catalog "global_grants" with
                    | None -> []
                    | Some grants ->
                        match colIdx grants "USER", colIdx grants "HOST", colIdx grants "PRIV", colIdx grants "WITH_GRANT_OPTION" with
                        | Some dynamicUser, Some dynamicHost, Some privilege, Some option ->
                            grants.Rows
                            |> List.filter (fun grant ->
                                Fsdb.Auth.sameAccount
                                    (Fsdb.Auth.account (rowText grant dynamicUser) (rowText grant dynamicHost))
                                    (Fsdb.Auth.account user host))
                            |> List.choose (fun grant ->
                                let name = rowText grant privilege

                                if Privileges.contains name then
                                    Some
                                        [| vs grantee
                                           vs "def"
                                           vs (name.ToUpperInvariant())
                                           vs (if rowText grant option = "Y" then "YES" else "NO") |]
                                else
                                    None)
                            |> List.sortBy (fun row -> rowText row 2)
                        | _ -> []

                staticRows @ dynamicRows)
        | _ -> []

let private userAttributesRows (catalog: Catalog) =
    match mysqlTable catalog "user" with
    | None -> []
    | Some table ->
        match colIdx table "User", colIdx table "Host", colIdx table "User_attributes" with
        | Some user, Some host, Some attributes ->
            let ownOnly = restrictedTo "SELECT"

            table.Rows
            |> List.filter (fun row ->
                match ownOnly with
                | Some viewer ->
                    Fsdb.Auth.sameAccount
                        (Fsdb.Auth.account (rowText row user) (rowText row host))
                        viewer
                | None -> true)
            |> List.map (fun row ->
                let attribute =
                    match Fsdb.Auth.accountAttributeText table.Columns row with
                    | Some json -> vs json
                    | None -> row.[attributes]

                [| row.[user]; row.[host]; attribute |])
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

let private columnPrivilegesRows (catalog: Catalog) : Value[] list =
    match mysqlTable catalog "columns_priv", mysqlTable catalog "tables_priv" with
    | Some columnsTable, Some tablesTable ->
        match
            colIdx columnsTable "User",
            colIdx columnsTable "Host",
            colIdx columnsTable "Db",
            colIdx columnsTable "Table_name",
            colIdx columnsTable "Column_name",
            colIdx columnsTable "Column_priv",
            colIdx tablesTable "User",
            colIdx tablesTable "Host",
            colIdx tablesTable "Db",
            colIdx tablesTable "Table_name",
            colIdx tablesTable "Table_priv"
        with
        | Some userIndex,
          Some hostIndex,
          Some databaseIndex,
          Some tableIndex,
          Some columnIndex,
          Some privilegesIndex,
          Some tableUserIndex,
          Some tableHostIndex,
          Some tableDatabaseIndex,
          Some tableNameIndex,
          Some tablePrivilegesIndex ->
            let ownOnly = restrictedTo "SELECT"

            let grantableTables =
                tablesTable.Rows
                |> List.choose (fun row ->
                    if
                        Fsdb.Auth.setMembers (rowText row tablePrivilegesIndex)
                        |> List.exists (eqI "Grant")
                    then
                        Some(
                            rowText row tableUserIndex,
                            rowText row tableHostIndex,
                            rowText row tableDatabaseIndex,
                            rowText row tableNameIndex
                        )
                    else
                        None)

            let isGrantable user host database table =
                grantableTables
                |> List.exists (fun (candidateUser, candidateHost, candidateDatabase, candidateTable) ->
                    eqI candidateUser user
                    && eqI candidateHost host
                    && eqI candidateDatabase database
                    && eqI candidateTable table)

            let privilegeOrder = [ "Insert"; "References"; "Select"; "Update" ]

            columnsTable.Rows
            |> List.filter (fun row ->
                match ownOnly with
                | Some account ->
                    Fsdb.Auth.sameAccount
                        (Fsdb.Auth.account (rowText row userIndex) (rowText row hostIndex))
                        account
                | None -> true)
            |> List.collect (fun row ->
                let user = rowText row userIndex
                let host = rowText row hostIndex
                let database = rowText row databaseIndex
                let table = rowText row tableIndex
                let column = rowText row columnIndex
                let privileges = Fsdb.Auth.setMembers (rowText row privilegesIndex)
                let grantable = if isGrantable user host database table then "YES" else "NO"
                let grantee = sprintf "'%s'@'%s'" user host

                privilegeOrder
                |> List.filter (fun privilege -> privileges |> List.exists (eqI privilege))
                |> List.map (fun privilege ->
                    [| vs grantee
                       vs "def"
                       vs database
                       vs table
                       vs column
                       vs (privilege.ToUpperInvariant())
                       vs grantable |]))
        | _ -> []
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

let private enabledRolesColumns =
    [ strCol "ROLE_NAME"
      strCol "ROLE_HOST"
      strCol "IS_DEFAULT"
      strCol "IS_MANDATORY" ]

let private applicableRolesColumns =
    [ strCol "USER"
      strCol "HOST"
      strCol "GRANTEE"
      strCol "GRANTEE_HOST"
      strCol "ROLE_NAME"
      strCol "ROLE_HOST"
      strCol "IS_GRANTABLE"
      strCol "IS_DEFAULT"
      strCol "IS_MANDATORY" ]

let private administrableRoleAuthorizationsColumns =
    [ col "USER" (TVarchar 97)
      col "HOST" (TVarchar 256)
      col "GRANTEE" (TVarchar 97)
      col "GRANTEE_HOST" (TVarchar 256)
      col "ROLE_NAME" (TVarchar 255)
      col "ROLE_HOST" (TVarchar 256)
      requiredCol "IS_GRANTABLE" (TVarchar 3)
      col "IS_DEFAULT" (TVarchar 3)
      requiredCol "IS_MANDATORY" (TVarchar 3) ]

let private roleTableGrantsColumns =
    let privileges =
        [ "Select"
          "Insert"
          "Update"
          "Delete"
          "Create"
          "Drop"
          "Grant"
          "References"
          "Index"
          "Alter"
          "Create View"
          "Show view"
          "Trigger" ]

    [ col "GRANTOR" (TVarchar 97)
      col "GRANTOR_HOST" (TVarchar 256)
      requiredCol "GRANTEE" (TChar 32)
      requiredCol "GRANTEE_HOST" (TChar 255)
      requiredCol "TABLE_CATALOG" (TVarchar 3)
      requiredCol "TABLE_SCHEMA" (TChar 64)
      requiredCol "TABLE_NAME" (TChar 64)
      requiredCol "PRIVILEGE_TYPE" (TSet privileges)
      requiredCol "IS_GRANTABLE" (TVarchar 3) ]

let private roleColumnGrantsColumns =
    [ col "GRANTOR" (TVarchar 97)
      col "GRANTOR_HOST" (TVarchar 256)
      requiredCol "GRANTEE" (TChar 32)
      requiredCol "GRANTEE_HOST" (TChar 255)
      requiredCol "TABLE_CATALOG" (TVarchar 3)
      requiredCol "TABLE_SCHEMA" (TChar 64)
      requiredCol "TABLE_NAME" (TChar 64)
      requiredCol "COLUMN_NAME" (TChar 64)
      requiredCol "PRIVILEGE_TYPE" (TSet [ "Select"; "Insert"; "Update"; "References" ])
      requiredCol "IS_GRANTABLE" (TVarchar 3) ]

let private roleRoutineGrantsColumns =
    [ col "GRANTOR" (TVarchar 97)
      col "GRANTOR_HOST" (TVarchar 256)
      requiredCol "GRANTEE" (TChar 32)
      requiredCol "GRANTEE_HOST" (TChar 255)
      requiredCol "SPECIFIC_CATALOG" (TVarchar 3)
      requiredCol "SPECIFIC_SCHEMA" (TChar 64)
      requiredCol "SPECIFIC_NAME" (TChar 64)
      requiredCol "ROUTINE_CATALOG" (TVarchar 3)
      requiredCol "ROUTINE_SCHEMA" (TChar 64)
      requiredCol "ROUTINE_NAME" (TChar 64)
      requiredCol "PRIVILEGE_TYPE" (TSet [ "Execute"; "Alter Routine"; "Grant" ])
      requiredCol "IS_GRANTABLE" (TVarchar 3) ]

let private isDefaultRole store account role =
    Fsdb.Auth.defaultRolesForAccount store account
    |> List.exists (Fsdb.Auth.sameAccount role)

let private enabledRolesRows () =
    match currentViewer.Value with
    | None -> []
    | Some viewer ->
        viewer.ActiveRoles
        |> List.distinctBy (fun role -> role.Name, role.Host.ToLowerInvariant())
        |> List.sortBy (fun role -> role.Name, role.Host.ToLowerInvariant())
        |> List.map (fun role ->
            [| vs role.Name
               vs role.Host
               vs (if isDefaultRole viewer.Store viewer.Account role then "YES" else "NO")
               vs (if Fsdb.Auth.isMandatoryRole viewer.Store role then "YES" else "NO") |])

let private applicableRolesRows () =
    match currentViewer.Value with
    | None -> []
    | Some viewer ->
        let direct = Fsdb.Auth.directRoleGrantsForAccount viewer.Store viewer.Account

        let mandatory =
            Fsdb.Auth.mandatoryRoles viewer.Store
            |> List.filter (fun role -> Fsdb.Auth.tryUserRowForAccount viewer.Store role |> Option.isSome)
            |> List.filter (fun role -> direct |> List.exists (fun grant -> Fsdb.Auth.sameAccount grant.Role role) |> not)
            |> List.map (fun role ->
                { Role = role
                  Grantee = viewer.Account
                  AdminOption = false }
                : Fsdb.Auth.RoleGrant)

        let roots = direct @ mandatory

        let rec collect visited pending grants =
            match pending with
            | [] -> grants
            | grantee :: rest when visited |> List.exists (Fsdb.Auth.sameAccount grantee) ->
                collect visited rest grants
            | grantee :: rest ->
                let direct = Fsdb.Auth.directRoleGrantsForAccount viewer.Store grantee

                collect
                    (grantee :: visited)
                    (rest @ (direct |> List.map _.Role))
                    (grants @ direct)

        collect [] (roots |> List.map _.Role) roots
        |> List.map (fun grant ->
            [| vs viewer.Account.Name
               vs viewer.Account.Host
               vs grant.Grantee.Name
               vs grant.Grantee.Host
               vs grant.Role.Name
               vs grant.Role.Host
               vs (if grant.AdminOption then "YES" else "NO")
               vs (if isDefaultRole viewer.Store viewer.Account grant.Role then "YES" else "NO")
               vs
                   (if
                        Fsdb.Auth.sameAccount grant.Grantee viewer.Account
                        && Fsdb.Auth.isMandatoryRole viewer.Store grant.Role
                    then
                        "YES"
                    else
                        "NO") |])

let private administrableRoleAuthorizationsRows () =
    applicableRolesRows ()
    |> List.filter (fun row -> eqI (rowText row 6) "YES")

let private grantorValues value =
    match Fsdb.Auth.tryParseAccount value with
    | Some grantor -> vs grantor.Name, vs grantor.Host
    | None -> VNull, VNull

let private activeRoleClosure () =
    currentViewer.Value
    |> Option.map (fun viewer -> Fsdb.Auth.roleClosure viewer.Store viewer.ActiveRoles)
    |> Option.defaultValue []

let private belongsToActiveRole roles user host =
    let account = Fsdb.Auth.account user host
    roles |> List.exists (Fsdb.Auth.sameAccount account)

let private roleTableGrantsRows (catalog: Catalog) =
    match mysqlTable catalog "tables_priv" with
    | None -> []
    | Some table ->
        match
            colIdx table "User",
            colIdx table "Host",
            colIdx table "Db",
            colIdx table "Table_name",
            colIdx table "Grantor",
            colIdx table "Table_priv"
        with
        | Some user, Some host, Some database, Some tableName, Some grantor, Some privileges ->
            let roles = activeRoleClosure ()

            table.Rows
            |> List.choose (fun row ->
                let userName = rowText row user
                let hostName = rowText row host
                let privilegeSet = rowText row privileges

                if belongsToActiveRole roles userName hostName && not (String.IsNullOrEmpty privilegeSet) then
                    let grantorName, grantorHost = grantorValues (rowText row grantor)
                    let grantable = Fsdb.Auth.setMembers privilegeSet |> List.exists (eqI "Grant")

                    Some
                        [| grantorName
                           grantorHost
                           vs userName
                           vs hostName
                           vs "def"
                           vs (rowText row database)
                           vs (rowText row tableName)
                           vs privilegeSet
                           vs (if grantable then "YES" else "NO") |]
                else
                    None)
        | _ -> []

let private roleColumnGrantsRows (catalog: Catalog) =
    match mysqlTable catalog "columns_priv", mysqlTable catalog "tables_priv" with
    | Some columnsTable, Some tablesTable ->
        match
            colIdx columnsTable "User",
            colIdx columnsTable "Host",
            colIdx columnsTable "Db",
            colIdx columnsTable "Table_name",
            colIdx columnsTable "Column_name",
            colIdx columnsTable "Column_priv",
            colIdx tablesTable "User",
            colIdx tablesTable "Host",
            colIdx tablesTable "Db",
            colIdx tablesTable "Table_name",
            colIdx tablesTable "Grantor",
            colIdx tablesTable "Table_priv"
        with
        | Some user,
          Some host,
          Some database,
          Some tableName,
          Some columnName,
          Some privileges,
          Some tableUser,
          Some tableHost,
          Some tableDatabase,
          Some grantTable,
          Some grantor,
          Some tablePrivileges ->
            let roles = activeRoleClosure ()

            let grantRow userName hostName databaseName tableNameValue =
                tablesTable.Rows
                |> List.tryFind (fun row ->
                    eqI (rowText row tableUser) userName
                    && eqI (rowText row tableHost) hostName
                    && eqI (rowText row tableDatabase) databaseName
                    && eqI (rowText row grantTable) tableNameValue)

            columnsTable.Rows
            |> List.choose (fun row ->
                let userName = rowText row user
                let hostName = rowText row host
                let databaseName = rowText row database
                let tableNameValue = rowText row tableName
                let privilegeSet = rowText row privileges

                if belongsToActiveRole roles userName hostName && not (String.IsNullOrEmpty privilegeSet) then
                    let tableGrant = grantRow userName hostName databaseName tableNameValue

                    let grantorName, grantorHost =
                        tableGrant
                        |> Option.map (fun grant -> grantorValues (rowText grant grantor))
                        |> Option.defaultValue (VNull, VNull)

                    let grantable =
                        tableGrant
                        |> Option.exists (fun grant ->
                            Fsdb.Auth.setMembers (rowText grant tablePrivileges)
                            |> List.exists (eqI "Grant"))

                    Some
                        [| grantorName
                           grantorHost
                           vs userName
                           vs hostName
                           vs "def"
                           vs databaseName
                           vs tableNameValue
                           vs (rowText row columnName)
                           vs privilegeSet
                           vs (if grantable then "YES" else "NO") |]
                else
                    None)
        | _ -> []
    | _ -> []

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

let private columnStatisticsColumns =
    [ requiredCol "SCHEMA_NAME" (TVarchar 64)
      requiredCol "TABLE_NAME" (TVarchar 64)
      requiredCol "COLUMN_NAME" (TVarchar 64)
      requiredCol "HISTOGRAM" TJson ]

let private optimizerTraceColumns =
    [ requiredCol "QUERY" (TVarchar 65535)
      requiredCol "TRACE" (TVarchar 65535)
      requiredCol "MISSING_BYTES_BEYOND_MAX_MEM_SIZE" (TInt false)
      requiredCol "INSUFFICIENT_PRIVILEGES" TBool ]

let private profilingColumns =
    [ requiredCol "QUERY_ID" (TInt false)
      requiredCol "SEQ" (TInt false)
      requiredCol "STATE" (TVarchar 30)
      requiredCol "DURATION" (TDecimal(905, 0, false))
      col "CPU_USER" (TDecimal(905, 0, false))
      col "CPU_SYSTEM" (TDecimal(905, 0, false))
      intCol "CONTEXT_VOLUNTARY"
      intCol "CONTEXT_INVOLUNTARY"
      intCol "BLOCK_OPS_IN"
      intCol "BLOCK_OPS_OUT"
      intCol "MESSAGES_SENT"
      intCol "MESSAGES_RECEIVED"
      intCol "PAGE_FAULTS_MAJOR"
      intCol "PAGE_FAULTS_MINOR"
      intCol "SWAPS"
      col "SOURCE_FUNCTION" (TVarchar 30)
      col "SOURCE_FILE" (TVarchar 20)
      intCol "SOURCE_LINE" ]

let private resourceGroupsColumns =
    [ requiredCol "RESOURCE_GROUP_NAME" (TVarchar 64)
      requiredCol "RESOURCE_GROUP_TYPE" (TEnum [ "SYSTEM"; "USER" ])
      requiredCol "RESOURCE_GROUP_ENABLED" TBool
      col "VCPU_IDS" TBlob
      requiredCol "THREAD_PRIORITY" (TInt false) ]

let private tablespacesExtensionsColumns =
    [ requiredCol "TABLESPACE_NAME" (TVarchar 268); col "ENGINE_ATTRIBUTE" TJson ]

let private filesColumns =
    [ col "FILE_ID" (TBigInt false)
      col "FILE_NAME" TText
      col "FILE_TYPE" (TVarchar 256)
      requiredCol "TABLESPACE_NAME" (TVarchar 268)
      requiredCol "TABLE_CATALOG" (TVarchar 0)
      col "TABLE_SCHEMA" (TVarBinary 0)
      col "TABLE_NAME" (TVarBinary 0)
      col "LOGFILE_GROUP_NAME" (TVarchar 256)
      col "LOGFILE_GROUP_NUMBER" (TBigInt false)
      requiredCol "ENGINE" (TVarchar 64)
      col "FULLTEXT_KEYS" (TVarBinary 0)
      col "DELETED_ROWS" (TVarBinary 0)
      col "UPDATE_COUNT" (TVarBinary 0)
      col "FREE_EXTENTS" (TBigInt false)
      col "TOTAL_EXTENTS" (TBigInt false)
      col "EXTENT_SIZE" (TBigInt false)
      col "INITIAL_SIZE" (TBigInt false)
      col "MAXIMUM_SIZE" (TBigInt false)
      col "AUTOEXTEND_SIZE" (TBigInt false)
      col "CREATION_TIME" (TVarBinary 0)
      col "LAST_UPDATE_TIME" (TVarBinary 0)
      col "LAST_ACCESS_TIME" (TVarBinary 0)
      col "RECOVER_TIME" (TVarBinary 0)
      col "TRANSACTION_COUNTER" (TVarBinary 0)
      col "VERSION" (TBigInt false)
      col "ROW_FORMAT" (TVarchar 256)
      col "TABLE_ROWS" (TVarBinary 0)
      col "AVG_ROW_LENGTH" (TVarBinary 0)
      col "DATA_LENGTH" (TVarBinary 0)
      col "MAX_DATA_LENGTH" (TVarBinary 0)
      col "INDEX_LENGTH" (TVarBinary 0)
      col "DATA_FREE" (TBigInt false)
      col "CREATE_TIME" (TVarBinary 0)
      col "UPDATE_TIME" (TVarBinary 0)
      col "CHECK_TIME" (TVarBinary 0)
      col "CHECKSUM" (TVarBinary 0)
      col "STATUS" (TVarchar 256)
      col "EXTRA" (TVarchar 256) ]

let private keywordsColumns =
    [ col "WORD" (TVarchar 128)
      col "RESERVED" (TInt false) ]

let private keywordsRows =
    InformationSchemaKeywords.rows
    |> List.map (fun (word, reserved) -> [| vs word; vi (if reserved then 1 else 0) |])

let private pluginsColumns =
    [ requiredCol "PLUGIN_NAME" (TVarchar 64)
      requiredCol "PLUGIN_VERSION" (TVarchar 20)
      requiredCol "PLUGIN_STATUS" (TVarchar 10)
      requiredCol "PLUGIN_TYPE" (TVarchar 80)
      requiredCol "PLUGIN_TYPE_VERSION" (TVarchar 20)
      col "PLUGIN_LIBRARY" (TVarchar 64)
      col "PLUGIN_LIBRARY_VERSION" (TVarchar 20)
      col "PLUGIN_AUTHOR" (TVarchar 64)
      col "PLUGIN_DESCRIPTION" (TVarchar 65535)
      col "PLUGIN_LICENSE" (TVarchar 80)
      requiredCol "LOAD_OPTION" (TVarchar 64) ]

let private pluginsRows =
    [ [| vs "mysql_native_password"
         vs "1.1"
         vs "ACTIVE"
         vs "AUTHENTICATION"
         vs "2.1"
         VNull
         VNull
         vs "fsdb"
         vs "Native MySQL authentication"
         vs "GPL"
         vs "ON" |] ]

let private innodbFtDefaultStopwordColumns =
    [ { requiredCol "value" (TVarchar 18) with
          Charset = Some "utf8mb3"
          Collation = Some "utf8mb3_general_ci" } ]

let private innodbFtDefaultStopwordRows =
    Fsdb.FullText.defaultStopwords |> List.map (fun value -> [| vs value |])

let private innodbInternalTextColumn name length nullable =
    { col name (TVarchar length) with
        Nullable = nullable
        Charset = Some "utf8mb3"
        Collation = Some "utf8mb3_tolower_ci" }

let private innodbForeignColumns =
    [ innodbInternalTextColumn "ID" 129 true
      innodbInternalTextColumn "FOR_NAME" 129 true
      innodbInternalTextColumn "REF_NAME" 129 true
      { requiredCol "N_COLS" (TBigInt false) with Default = Some(DConst(VInt 0L)) }
      { requiredCol "TYPE" (TBigInt true) with Default = Some(DConst(VUInt 0UL)) } ]

let private innodbForeignColsColumns =
    [ innodbInternalTextColumn "ID" 129 true
      innodbInternalTextColumn "FOR_COL_NAME" 64 false
      innodbInternalTextColumn "REF_COL_NAME" 64 false
      requiredCol "POS" (TInt true) ]

let private foreignActionBits deleteAction updateAction =
    let actionBit cascade setNull noAction =
        function
        | Some action when eqI action "CASCADE" -> cascade
        | Some action when eqI action "SET NULL" -> setNull
        | Some action when eqI action "RESTRICT" -> 0UL
        | _ -> noAction

    actionBit 1UL 2UL 16UL deleteAction ||| actionBit 4UL 8UL 32UL updateAction

let private innodbForeignEntries catalog =
    allTables catalog
    |> List.collect (fun (database, table) ->
        table.ForeignKeys
        |> List.map (fun foreignKey ->
            let referencedDatabase = foreignKey.RefDatabase |> Option.defaultValue database
            let id = sprintf "%s/%s" database foreignKey.Name
            let foreignTable = sprintf "%s/%s" database (normalizeTableName table.OriginalName)
            let referencedTable = sprintf "%s/%s" referencedDatabase (normalizeTableName foreignKey.RefTable)
            id, foreignTable, referencedTable, foreignKey))

let private innodbForeignRows catalog =
    innodbForeignEntries catalog
    |> List.map (fun (id, foreignTable, referencedTable, foreignKey) ->
        [| vs id
           vs foreignTable
           vs referencedTable
           vi foreignKey.Columns.Length
           VUInt(foreignActionBits foreignKey.OnDelete foreignKey.OnUpdate) |])

let private innodbForeignColsRows catalog =
    innodbForeignEntries catalog
    |> List.collect (fun (constraintId, _, _, foreignKey) ->
        foreignKey.Columns
        |> List.mapi (fun index foreignColumn ->
            foreignKey.RefColumns
            |> List.tryItem index
            |> Option.map (fun referencedColumn ->
                [| vs constraintId; vs foreignColumn; vs referencedColumn; VUInt(uint64 (index + 1)) |]))
        |> List.choose id)

let private userAttributesColumns =
    [ requiredCol "USER" (TChar 32)
      requiredCol "HOST" (TChar 255)
      col "ATTRIBUTE" TLongText ]

let private columnsExtensionsColumns =
    [ requiredCol "TABLE_CATALOG" (TVarchar 64)
      requiredCol "TABLE_SCHEMA" (TVarchar 64)
      requiredCol "TABLE_NAME" (TVarchar 64)
      col "COLUMN_NAME" (TVarchar 64)
      col "ENGINE_ATTRIBUTE" TJson
      col "SECONDARY_ENGINE_ATTRIBUTE" TJson ]

let private schemataExtensionsColumns =
    [ col "CATALOG_NAME" (TVarchar 64)
      col "SCHEMA_NAME" (TVarchar 64)
      col "OPTIONS" (TVarchar 256) ]

let private tablesExtensionsColumns =
    [ requiredCol "TABLE_CATALOG" (TVarchar 64)
      requiredCol "TABLE_SCHEMA" (TVarchar 64)
      requiredCol "TABLE_NAME" (TVarchar 64)
      col "ENGINE_ATTRIBUTE" TJson
      col "SECONDARY_ENGINE_ATTRIBUTE" TJson ]

let private tableConstraintsExtensionsColumns =
    [ requiredCol "CONSTRAINT_CATALOG" (TVarchar 64)
      requiredCol "CONSTRAINT_SCHEMA" (TVarchar 64)
      requiredCol "CONSTRAINT_NAME" (TVarchar 64)
      requiredCol "TABLE_NAME" (TVarchar 64)
      col "ENGINE_ATTRIBUTE" TJson
      col "SECONDARY_ENGINE_ATTRIBUTE" TJson ]

let private stGeometryColumnsColumns =
    [ col "TABLE_CATALOG" (TVarchar 64)
      col "TABLE_SCHEMA" (TVarchar 64)
      col "TABLE_NAME" (TVarchar 64)
      col "COLUMN_NAME" (TVarchar 64)
      col "SRS_NAME" (TVarchar 80)
      col "SRS_ID" (TInt true)
      col "GEOMETRY_TYPE_NAME" TLongText ]

let private stSpatialReferenceSystemsColumns =
    [ requiredCol "SRS_NAME" (TVarchar 80)
      requiredCol "SRS_ID" (TInt true)
      col "ORGANIZATION" (TVarchar 256)
      col "ORGANIZATION_COORDSYS_ID" (TInt true)
      requiredCol "DEFINITION" (TVarchar 4096)
      col "DESCRIPTION" (TVarchar 2048) ]

let private stSpatialReferenceSystemsRows =
    [ [| vs ""; VUInt 0UL; VNull; VNull; vs ""; VNull |] ]

let private stUnitsOfMeasureColumns =
    [ col "UNIT_NAME" (TVarchar 255)
      col "UNIT_TYPE" (TVarchar 7)
      col "CONVERSION_FACTOR" (TDouble false)
      col "DESCRIPTION" (TVarchar 255) ]

let private stUnitsOfMeasureRows =
    [ "British chain (Benoit 1895 A)", 20.1167824
      "British chain (Benoit 1895 B)", 20.116782494375872
      "British chain (Sears 1922 truncated)", 20.116756
      "British chain (Sears 1922)", 20.116765121552632
      "British foot (1865)", 0.30480083333333335
      "British foot (1936)", 0.3048007491
      "British foot (Benoit 1895 A)", 0.3047997333333333
      "British foot (Benoit 1895 B)", 0.30479973476327077
      "British foot (Sears 1922 truncated)", 0.30479933333333337
      "British foot (Sears 1922)", 0.3047994715386762
      "British link (Benoit 1895 A)", 0.201167824
      "British link (Benoit 1895 B)", 0.2011678249437587
      "British link (Sears 1922 truncated)", 0.20116756
      "British link (Sears 1922)", 0.2011676512155263
      "British yard (Benoit 1895 A)", 0.9143992
      "British yard (Benoit 1895 B)", 0.9143992042898124
      "British yard (Sears 1922 truncated)", 0.914398
      "British yard (Sears 1922)", 0.9143984146160288
      "centimetre", 0.01
      "chain", 20.1168
      "Clarke's chain", 20.1166195164
      "Clarke's foot", 0.3047972654
      "Clarke's link", 0.201166195164
      "Clarke's yard", 0.9143917962
      "fathom", 1.8288
      "foot", 0.3048
      "German legal metre", 1.0000135965
      "Gold Coast foot", 0.3047997101815088
      "Indian foot", 0.30479951024814694
      "Indian foot (1937)", 0.30479841
      "Indian foot (1962)", 0.3047996
      "Indian foot (1975)", 0.3047995
      "Indian yard", 0.9143985307444408
      "Indian yard (1937)", 0.91439523
      "Indian yard (1962)", 0.9143988
      "Indian yard (1975)", 0.9143985
      "kilometre", 1000.0
      "link", 0.201168
      "metre", 1.0
      "millimetre", 0.001
      "nautical mile", 1852.0
      "Statute mile", 1609.344
      "US survey chain", 20.11684023368047
      "US survey foot", 0.30480060960121924
      "US survey link", 0.2011684023368047
      "US survey mile", 1609.3472186944375
      "yard", 0.9144 ]
    |> List.map (fun (name, factor) -> [| vs name; vs "LINEAR"; VDouble factor; vs "" |])

/// Every virtual table this module serves, name -> columns — `scan`'s
/// dispatch source and the self-listing the `TABLES`/`COLUMNS` views and
/// `SHOW TABLES FROM information_schema` append, so listing and resolution
/// can't drift.
let private virtualTableDefs : (string * ColumnDef list) list =
    [ "ADMINISTRABLE_ROLE_AUTHORIZATIONS", administrableRoleAuthorizationsColumns
      "APPLICABLE_ROLES", applicableRolesColumns
      "CHARACTER_SETS", characterSetsColumns
      "CHECK_CONSTRAINTS", checkConstraintsColumns
      "COLLATIONS", collationsColumns
      "COLLATION_CHARACTER_SET_APPLICABILITY", collationCharacterSetApplicabilityColumns
      "COLUMNS", columnsColumns
      "COLUMNS_EXTENSIONS", columnsExtensionsColumns
      "COLUMN_PRIVILEGES", columnPrivilegesColumns
      "COLUMN_STATISTICS", columnStatisticsColumns
      "ENABLED_ROLES", enabledRolesColumns
      "ENGINES", enginesColumns
      "EVENTS", eventsColumns
      "FILES", filesColumns
      "INNODB_FOREIGN", innodbForeignColumns
      "INNODB_FOREIGN_COLS", innodbForeignColsColumns
      "INNODB_FT_DEFAULT_STOPWORD", innodbFtDefaultStopwordColumns
      "KEY_COLUMN_USAGE", keyColumnUsageColumns
      "KEYWORDS", keywordsColumns
      "OPTIMIZER_TRACE", optimizerTraceColumns
      "PARAMETERS", parametersColumns
      "PARTITIONS", partitionsColumns
      "PLUGINS", pluginsColumns
      "PROFILING", profilingColumns
      "PROCESSLIST", processlistColumns
      "REFERENTIAL_CONSTRAINTS", referentialConstraintsColumns
      "RESOURCE_GROUPS", resourceGroupsColumns
      "ROLE_COLUMN_GRANTS", roleColumnGrantsColumns
      "ROLE_ROUTINE_GRANTS", roleRoutineGrantsColumns
      "ROLE_TABLE_GRANTS", roleTableGrantsColumns
      "ROUTINES", routinesColumns
      "SCHEMATA", schemataColumns
      "SCHEMATA_EXTENSIONS", schemataExtensionsColumns
      "SCHEMA_PRIVILEGES", schemaPrivilegesColumns
      "STATISTICS", statisticsColumns
      "ST_GEOMETRY_COLUMNS", stGeometryColumnsColumns
      "ST_SPATIAL_REFERENCE_SYSTEMS", stSpatialReferenceSystemsColumns
      "ST_UNITS_OF_MEASURE", stUnitsOfMeasureColumns
      "TABLES", tablesColumns
      "TABLES_EXTENSIONS", tablesExtensionsColumns
      "TABLESPACES_EXTENSIONS", tablespacesExtensionsColumns
      "TABLE_CONSTRAINTS", tableConstraintsColumns
      "TABLE_CONSTRAINTS_EXTENSIONS", tableConstraintsExtensionsColumns
      "TABLE_PRIVILEGES", tablePrivilegesColumns
      "TRIGGERS", triggersColumns
      "USER_ATTRIBUTES", userAttributesColumns
      "USER_PRIVILEGES", userPrivilegesColumns
      "VIEW_ROUTINE_USAGE", viewRoutineUsageColumns
      "VIEW_TABLE_USAGE", viewTableUsageColumns
      "VIEWS", viewsColumns ]
    @ InnoDbMetadata.tableDefs

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

// Built once: `virtualTableDefs` never changes after startup, and materializing
// these rows dominated every COLUMNS scan. Readers never mutate row arrays,
// matching the sharing contract `Storage.scanList` already has.
let private selfColumnsRowsCached : Lazy<Value[] list> =
    lazy
        (virtualTableDefs
         |> List.collect (fun (name, cols) ->
             cols |> List.mapi (fun i c -> columnRowWith "select" "information_schema" name i "" c)))

let private allColumnRows catalog viewColumns =
    columnsRows catalog viewColumns @ selfColumnsRowsCached.Value

let private columnsExtensionsRows catalog viewColumns =
    allColumnRows catalog viewColumns
    |> List.map (fun row -> [| row.[0]; row.[1]; row.[2]; row.[3]; VNull; VNull |])

let private schemataExtensionsRows catalog =
    schemataRows catalog
    |> List.map (fun row -> [| row.[0]; row.[1]; vs "" |])

let private tablesExtensionsRows catalog =
    tablesRows catalog @ selfTablesRows ()
    |> List.map (fun row -> [| row.[0]; row.[1]; row.[2]; VNull; VNull |])

let private tableConstraintsExtensionsRows catalog =
    tableConstraintsRows catalog
    |> List.filter (fun row -> not (eqI (rowText row 5) "CHECK"))
    |> List.map (fun row -> [| row.[0]; row.[1]; row.[2]; row.[4]; VNull; VNull |])

let private geometryDataTypes =
    set
        [ "geometry"
          "point"
          "linestring"
          "polygon"
          "multipoint"
          "multilinestring"
          "multipolygon"
          "geomcollection" ]

let private stGeometryColumnsRows catalog viewColumns =
    allColumnRows catalog viewColumns
    |> List.choose (fun row ->
        let dataType = rowText row 7

        if geometryDataTypes.Contains(dataType) then
            Some [| row.[0]; row.[1]; row.[2]; row.[3]; VNull; row.[21]; vs dataType |]
        else
            None)

let private scopeRowsToViewer (tableName: string) (columns: ColumnDef list) (rows: Value[] list) : Value[] list =
    match currentViewer.Value with
    | None -> rows
    | Some viewer ->
        let columnIndex name =
            columns
            |> List.tryFindIndex (fun column -> String.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))

        let schemaIndex =
            [ "TABLE_SCHEMA"; "CONSTRAINT_SCHEMA"; "TRIGGER_SCHEMA"; "EVENT_SCHEMA"; "ROUTINE_SCHEMA"; "SCHEMA_NAME" ]
            |> List.tryPick columnIndex

        let tableIndex = [ "TABLE_NAME"; "EVENT_OBJECT_TABLE" ] |> List.tryPick columnIndex
        let columnNameIndex = columnIndex "COLUMN_NAME"
        let privilegesIndex = columnIndex "PRIVILEGES"

        let visibleSchema (row: Value[]) =
            schemaIndex
            |> Option.map (fun index ->
                row.[index]
                |> Value.toText
                |> Option.map (
                    Fsdb.Auth.canSeeDatabaseForAccountWithRoles
                        viewer.Store
                        viewer.Account
                        viewer.ActiveRoles
                )
                |> Option.defaultValue false)
            |> Option.defaultValue true

        let visibleObject (row: Value[]) =
            let visibleTable =
                match schemaIndex, tableIndex with
                | Some dbIndex, Some nameIndex ->
                    Fsdb.Auth.canSeeTableForAccountWithRoles
                        viewer.Store
                        viewer.Account
                        viewer.ActiveRoles
                        (rowText row dbIndex)
                        (rowText row nameIndex)
                | _ -> true

            let visibleColumn =
                match tableName, schemaIndex, tableIndex, columnNameIndex with
                | metadataTable, Some dbIndex, Some nameIndex, Some columnIndex
                    when metadataTable = "COLUMNS"
                         || metadataTable = "COLUMNS_EXTENSIONS"
                         || metadataTable = "ST_GEOMETRY_COLUMNS" ->
                    Fsdb.Auth.canSeeColumnForAccountWithRoles
                        viewer.Store
                        viewer.Account
                        viewer.ActiveRoles
                        (rowText row dbIndex)
                        (rowText row nameIndex)
                        (rowText row columnIndex)
                | _ -> true

            visibleTable
            && visibleColumn
            && (match tableName with
                | "VIEWS" ->
                    Fsdb.Auth.checkForAccountWithRoles
                        viewer.Store
                        viewer.Account
                        viewer.ActiveRoles
                        [ "SHOW VIEW", Fsdb.Auth.OnTable(rowText row 1, rowText row 2) ]
                    |> Result.isOk
                | "TRIGGERS" ->
                    Fsdb.Auth.checkForAccountWithRoles
                        viewer.Store
                        viewer.Account
                        viewer.ActiveRoles
                        [ "TRIGGER", Fsdb.Auth.OnTable(rowText row 1, rowText row 6) ]
                    |> Result.isOk
                | "EVENTS" ->
                    Fsdb.Auth.checkForAccountWithRoles
                        viewer.Store
                        viewer.Account
                        viewer.ActiveRoles
                        [ "EVENT", Fsdb.Auth.OnDb(rowText row 1) ]
                    |> Result.isOk
                | _ -> true)

        rows
        |> List.filter (fun row -> visibleSchema row && visibleObject row)
        |> List.map (fun row ->
            match tableName, schemaIndex, tableIndex, columnNameIndex, privilegesIndex with
            | "COLUMNS", Some dbIndex, Some nameIndex, Some columnIndex, Some privilegeIndex ->
                let scoped = Array.copy row

                scoped.[privilegeIndex] <-
                    Fsdb.Auth.columnPrivilegesForAccountWithRoles
                        viewer.Store
                        viewer.Account
                        viewer.ActiveRoles
                        (rowText row dbIndex)
                        (rowText row nameIndex)
                        (rowText row columnIndex)
                    |> String.concat ","
                    |> vs

                scoped
            | _ -> row)


/// Resolves one `information_schema` table name (case-insensitive) to its
/// columns and freshly-projected rows, or `None` if `name` isn't one of the
/// virtual tables this module knows about (a real 1146 from `Executor`, same
/// as any other unknown table).
let scan (catalog: Catalog) (name: string) (viewColumns: ViewColumns option) : (ColumnDef list * Value[] list) option =
    let upper = name.ToUpperInvariant()

    let rows =
        match upper with
        | "ADMINISTRABLE_ROLE_AUTHORIZATIONS" -> Some(administrableRoleAuthorizationsRows ())
        | "APPLICABLE_ROLES" -> Some(applicableRolesRows ())
        | "TABLES" -> Some(tablesRows catalog @ selfTablesRows ())
        | "COLUMNS" -> Some(columnsRows catalog viewColumns @ selfColumnsRowsCached.Value)
        | "COLUMNS_EXTENSIONS" -> Some(columnsExtensionsRows catalog viewColumns)
        | "COLUMN_STATISTICS" -> Some []
        | "STATISTICS" -> Some(statisticsRows catalog)
        | "KEY_COLUMN_USAGE" -> Some(keyColumnUsageRows catalog)
        | "REFERENTIAL_CONSTRAINTS" -> Some(referentialConstraintsRows catalog)
        | "CHECK_CONSTRAINTS" -> Some(checkConstraintsRows catalog)
        | "TABLE_CONSTRAINTS" -> Some(tableConstraintsRows catalog)
        | "COLLATION_CHARACTER_SET_APPLICABILITY" -> Some collationCharacterSetApplicabilityRows
        | "COLLATIONS" -> Some collationsRows
        | "CHARACTER_SETS" -> Some characterSetsRows
        | "SCHEMATA" -> Some(schemataRows catalog)
        | "SCHEMATA_EXTENSIONS" -> Some(schemataExtensionsRows catalog)
        | "PROCESSLIST" -> Some(processlistRows ())
        | "PARTITIONS" -> Some(partitionsRows catalog)
        | "USER_PRIVILEGES" -> Some(userPrivilegesRows catalog)
        | "SCHEMA_PRIVILEGES" -> Some(schemaPrivilegesRows catalog)
        | "TABLE_PRIVILEGES" -> Some(tablePrivilegesRows catalog)
        | "COLUMN_PRIVILEGES" -> Some(columnPrivilegesRows catalog)
        | "ENGINES" -> Some enginesRows
        | "ENABLED_ROLES" -> Some(enabledRolesRows ())
        | "TRIGGERS" -> Some(triggersRows catalog)
        | "VIEWS" -> Some(viewsRows catalog)
        | "ROUTINES" -> Some(routinesRows catalog)
        | "PARAMETERS" -> Some(parametersRows catalog)
        | "EVENTS" -> Some(eventsRows catalog)
        | "FILES" -> Some []
        | "INNODB_FOREIGN" -> Some(innodbForeignRows catalog)
        | "INNODB_FOREIGN_COLS" -> Some(innodbForeignColsRows catalog)
        | "INNODB_FT_DEFAULT_STOPWORD" -> Some innodbFtDefaultStopwordRows
        | _ when InnoDbMetadata.contains upper -> InnoDbMetadata.tryRows catalog upper
        | "KEYWORDS" -> Some keywordsRows
        | "PLUGINS" -> Some pluginsRows
        | "OPTIMIZER_TRACE"
        | "PROFILING"
        | "RESOURCE_GROUPS"
        | "TABLESPACES_EXTENSIONS" -> Some []
        | "ROLE_COLUMN_GRANTS" -> Some(roleColumnGrantsRows catalog)
        | "ROLE_ROUTINE_GRANTS" -> Some []
        | "ROLE_TABLE_GRANTS" -> Some(roleTableGrantsRows catalog)
        | "ST_GEOMETRY_COLUMNS" -> Some(stGeometryColumnsRows catalog viewColumns)
        | "ST_SPATIAL_REFERENCE_SYSTEMS" -> Some stSpatialReferenceSystemsRows
        | "ST_UNITS_OF_MEASURE" -> Some stUnitsOfMeasureRows
        | "TABLES_EXTENSIONS" -> Some(tablesExtensionsRows catalog)
        | "TABLE_CONSTRAINTS_EXTENSIONS" -> Some(tableConstraintsExtensionsRows catalog)
        | "USER_ATTRIBUTES" -> Some(userAttributesRows catalog)
        | "VIEW_ROUTINE_USAGE" -> Some(viewRoutineUsageRows catalog)
        | "VIEW_TABLE_USAGE" -> Some(viewTableUsageRows catalog)
        | _ -> None

    rows
    |> Option.bind (fun rows ->
        virtualTableDefs
        |> List.tryFind (fst >> (=) upper)
        |> Option.map (fun (_, cols) -> cols, scopeRowsToViewer upper cols rows))

/// Projects one table's COLUMNS rows without constructing the rest of the
/// catalog or the entire information_schema self-description.
let scanColumnsForTable
    (catalog: Catalog)
    (schemaName: string)
    (tableName: string)
    (viewColumns: ViewColumns option)
    : ColumnDef list * Value[] list =
    let equals left right = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

    let tableRows =
        catalog
        |> Map.toSeq
        |> Seq.tryFind (fst >> equals schemaName)
        |> Option.bind (fun (databaseName, database) ->
            database
            |> Map.tryFind (tableName.ToLowerInvariant())
            |> Option.map (fun table -> databaseName, table))
        |> Option.map (fun (databaseName, table) ->
            table.Columns
            |> List.mapi (fun index column -> columnRow databaseName table.OriginalName index (columnKey table column) column))
        |> Option.defaultValue []

    let viewRows =
        viewCatalogEntries catalog
        |> List.tryFind (fun view -> equals view.Schema schemaName && equals view.Name tableName)
        |> Option.bind (fun view ->
            viewColumns
            |> Option.bind (fun resolve -> resolve view.Schema view.Name)
            |> Option.map (List.mapi (fun index column -> columnRow view.Schema view.Name index "" column)))
        |> Option.defaultValue []

    let selfRows =
        if equals schemaName "information_schema" then
            virtualTableDefs
            |> List.tryFind (fst >> equals tableName)
            |> Option.map (fun (name, columns) ->
                columns
                |> List.mapi (fun index column -> columnRowWith "select" "information_schema" name index "" column))
            |> Option.defaultValue []
        else
            []

    let rows = tableRows @ viewRows @ selfRows
    columnsColumns, scopeRowsToViewer "COLUMNS" columnsColumns rows

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

/// `SHOW PRIVILEGES` follows MySQL 8.4.11's static privilege descriptions,
/// followed by its dynamic privileges. fsdb enforces the static set, but
/// clients enumerate the complete list.
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
        Privileges.dynamic
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

        let visibleColumn (column: ColumnDef) =
            match currentViewer.Value with
            | None -> true
            | Some viewer ->
                Fsdb.Auth.canSeeColumnForAccountWithRoles
                    viewer.Store
                    viewer.Account
                    viewer.ActiveRoles
                    dbName
                    tableName
                    column.Name

        let privilegesOf (column: ColumnDef) =
            match currentViewer.Value with
            | None -> "select,insert,update,references"
            | Some viewer ->
                Fsdb.Auth.columnPrivilegesForAccountWithRoles
                    viewer.Store
                    viewer.Account
                    viewer.ActiveRoles
                    dbName
                    tableName
                    column.Name
                |> String.concat ","

        let cols: ColumnDef list =
            columns
            |> List.filter (fun column -> visibleColumn column && likeFilter likeOpt column.Name)

        if full then
            let rows =
                cols
                |> List.map (fun (c: ColumnDef) ->
                    [ Some c.Name
                      Some(columnTypeTextOfColumn c)
                      // The column's declared/inherited collation —
                      // matching `information_schema.columns.collation_name`
                      // (`columnsRows` above).
                      (if isStringy c.Type then Some(c.Collation |> Option.defaultValue "utf8mb4_0900_ai_ci") else None)
                      Some(isNullable c)
                      Some(keyOf c)
                      defaultCol c
                      Some(extra c)
                      Some(privilegesOf c)
                      Some c.Comment ])

            [ "Field"; "Type"; "Collation"; "Null"; "Key"; "Default"; "Extra"; "Privileges"; "Comment" ], rows
        else
            let rows =
                cols
                |> List.map (fun (c: ColumnDef) ->
                    [ Some c.Name; Some(columnTypeTextOfColumn c); Some(isNullable c); Some(keyOf c); defaultCol c; Some(extra c) ])

            [ "Field"; "Type"; "Null"; "Key"; "Default"; "Extra" ], rows)

let private backtick (s: string) = "`" + s.Replace("`", "``") + "`"

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

        [ backtick c.Name; columnTypeTextOfColumn c ]
        @ charsetCollate
        @ [ generatedPart; notNull; defaultPart; onUpdatePart; extra; if c.Comment = "" then "" else sprintf "COMMENT '%s'" (showCreateString c.Comment) ]
        |> List.filter ((<>) "")
        |> String.concat " "

    let indexColumnsText (index: IndexDef) =
        index.KeyColumns
        |> List.map (fun column ->
            let key =
                match indexExpression column with
                | Some expression -> "(" + expression + ")"
                | None ->
                    let length = column.PrefixLength |> Option.map (sprintf "(%d)") |> Option.defaultValue ""
                    backtick column.Name + length

            if column.Direction = Desc then key + " DESC" else key)
        |> String.concat ","

    let pkLine =
        indexesIncludingPrimary t
        |> List.tryFind isPrimaryIndex
        |> Option.map (fun index -> sprintf "PRIMARY KEY (%s)" (indexColumnsText index))
        |> Option.toList

    let indexLines =
        t.Indexes
        |> List.filter (not << isPrimaryIndex)
        |> List.map (fun ix ->
            let prefix =
                if ix.Unique then "UNIQUE "
                elif ix.Kind = FullTextIndex then "FULLTEXT "
                else ""

            sprintf "%sKEY %s (%s)%s" prefix (backtick ix.Name) (indexColumnsText ix) (if ix.Visible then "" else " /*!80000 INVISIBLE */"))

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
            let referencedTable =
                match fk.RefDatabase with
                | Some database -> sprintf "%s.%s" (backtick database) (backtick fk.RefTable)
                | None -> backtick fk.RefTable

            sprintf
                "CONSTRAINT %s FOREIGN KEY (%s) REFERENCES %s (%s)%s%s"
                (backtick fk.Name)
                (backtickCols fk.Columns)
                referencedTable
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

    let partitioning =
        t.Partitioning
        |> Option.map (fun value ->
            sprintf
                "\nPARTITION BY %sHASH (%s)\nPARTITIONS %d"
                (if value.Linear then "LINEAR " else "")
                (exprToSql value.Expression)
                value.Count)
        |> Option.defaultValue ""

    sprintf
        "CREATE %sTABLE %s (\n  %s\n) ENGINE=InnoDB DEFAULT CHARSET=%s COLLATE=%s%s%s"
        (if temporary then "TEMPORARY " else "")
        (backtick t.OriginalName)
        (String.concat ",\n  " lines)
        tableCharset
        tableCollation
        tableComment
        partitioning

/// `SHOW CREATE TABLE t`.
let showCreateTable (catalog: Catalog) (dbName: string) (tableName: string) : ShowResult =
    findTable catalog dbName tableName
    |> Result.map (fun t -> [ "Table"; "Create Table" ], [ [ Some t.OriginalName; Some(showCreateTableDDL false catalog dbName t) ] ])

let showCreateTemporaryTable (catalog: Catalog) (dbName: string) (tableName: string) : ShowResult =
    findTable catalog dbName tableName
    |> Result.map (fun t -> [ "Table"; "Create Table" ], [ [ Some t.OriginalName; Some(showCreateTableDDL true catalog dbName t) ] ])

let private quotedDefiner (definer: string) =
    let account = Auth.tryParseAccount definer |> Option.defaultValue (Auth.account "" "%")
    sprintf "%s@%s" (backtick account.Name) (backtick account.Host)

let private storedViewColumns (serialized: string) =
    try
        match JsonSerializer.Deserialize<string[]>(serialized) with
        | null -> []
        | columns -> List.ofArray columns
    with :? JsonException ->
        []

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

            let security = if view.SecurityType.Equals("INVOKER", System.StringComparison.OrdinalIgnoreCase) then "INVOKER" else "DEFINER"

            let columns =
                match storedViewColumns view.ColumnNames with
                | [] -> ""
                | names -> sprintf " (%s)" (names |> List.map backtick |> String.concat ", ")

            let ddl =
                sprintf
                    "CREATE ALGORITHM=%s DEFINER=%s SQL SECURITY %s VIEW %s%s AS %s%s"
                    view.Algorithm
                    (quotedDefiner view.Definer)
                    security
                    (backtick view.Name)
                    columns
                    view.Definition
                    checkOption

            Ok(
                [ "View"; "Create View"; "character_set_client"; "collation_connection" ],
                [ [ Some view.Name; Some ddl; Some "utf8mb4"; Some "utf8mb4_0900_ai_ci" ] ]
            )

/// `SHOW CREATE TRIGGER trigger_name`.
let showCreateTrigger (catalog: Catalog) (dbName: string) (triggerName: string) : ShowResult =
    triggerCatalogRows catalog
    |> List.tryFind (fun trigger ->
        String.Equals(trigger.Schema, dbName, StringComparison.OrdinalIgnoreCase)
        && String.Equals(trigger.Name, triggerName, StringComparison.OrdinalIgnoreCase))
    |> function
        | None -> Error(1360, sprintf "Trigger does not exist")
        | Some trigger when not (viewerHasPrivilege "TRIGGER" (Fsdb.Auth.OnTable(trigger.Schema, trigger.Table))) ->
            Error(1227, "Access denied; you need (at least one of) the TRIGGER privilege(s) for this operation")
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
                [ [ Some trigger.Name; Some trigger.SqlMode; Some ddl; Some trigger.CharacterSetClient
                    Some trigger.CollationConnection; Some trigger.DatabaseCollation; Some(triggerCreatedText trigger) ] ]
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
                      (if ix.Kind = FullTextIndex then None else Some(indexDirectionText keyColumn))
                      Some "0"
                      (effectivePrefixLength t keyColumn |> Option.map string)
                      None
                      Some "YES"
                      Some(if ix.Kind = FullTextIndex then "FULLTEXT" else "BTREE")
                      Some ""
                      Some ""
                      Some(indexVisibilityText ix)
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

let showPlugins () : ShowResult =
    let rows =
        pluginsRows
        |> List.map (fun row ->
            [ Value.toText row.[0]
              Value.toText row.[2]
              Value.toText row.[3]
              Value.toText row.[5]
              Value.toText row.[9] ])

    Ok([ "Name"; "Status"; "Type"; "Library"; "License" ], rows)

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
    (isGlobal: bool)
    (sessionCounters: StatusCounters)
    (compression: bool)
    (bytesReceived: int64)
    (bytesSent: int64)
    (sslCipher: string option)
    (sslVersion: string option)
    (likeOpt: string option)
    : ShowResult =
    let statusCounters = if isGlobal then processStatusCounters else sessionCounters

    let rows =
        [ "Bytes_received", string bytesReceived
          "Bytes_sent", string bytesSent
          "Compression", if compression then "ON" else "OFF"
          "Ssl_cipher", sslCipher |> Option.defaultValue ""
          "Ssl_version", sslVersion |> Option.defaultValue ""
          "Questions", string statusCounters.Questions
          "Threads_connected", string (connectedThreads ())
          "Uptime", string (int (DateTime.Now - serverStartedAt).TotalSeconds)
          for name in reportedCommandNames do
              name, string (statusCounters.CommandCount name) ]
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
            |> List.filter (fun trigger -> viewerHasPrivilege "TRIGGER" (Fsdb.Auth.OnTable(trigger.Schema, trigger.Table)))
            |> List.map (fun trigger ->
                [ Some trigger.Name; Some trigger.Event; Some trigger.Table; Some trigger.Body; Some trigger.Timing
                  Some(triggerCreatedText trigger); Some trigger.SqlMode; Some trigger.Definer
                  Some trigger.CharacterSetClient; Some trigger.CollationConnection; Some trigger.DatabaseCollation ])

        Ok(
            [ "Trigger"; "Event"; "Table"; "Statement"; "Timing"; "Created"; "sql_mode"; "Definer"
              "character_set_client"; "collation_connection"; "Database Collation" ],
            rows
        )

let showEvents (catalog: Catalog) (dbName: string option) : ShowResult =
    match dbName with
    | Some db when db.ToLowerInvariant() <> "information_schema" && not (Map.containsKey db catalog) ->
        Error(1049, sprintf "Unknown database '%s'" db)
    | _ ->
        let rows =
            eventsRows catalog
            |> List.filter (fun row -> dbName |> Option.forall (fun db -> String.Equals(toText row.[1] |> Option.defaultValue "", db, StringComparison.OrdinalIgnoreCase)))
            |> List.filter (fun row -> viewerHasPrivilege "EVENT" (Fsdb.Auth.OnDb(toText row.[1] |> Option.defaultValue "")))
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
let showRoutineStatus (catalog: Catalog) kind : ShowResult =
    let rows =
        routinesRows catalog
        |> List.filter (fun row ->
            String.Equals(toText row.[4] |> Option.defaultValue "", kind, StringComparison.OrdinalIgnoreCase))
        |> List.map (fun row ->
            [ toText row.[2]; toText row.[3]; toText row.[4]; Some "SQL"; toText row.[27]; toText row.[24]
              toText row.[23]; Some "DEFINER"; Some ""; Some "utf8mb4"; Some "utf8mb4_0900_ai_ci"
              Some "utf8mb4_0900_ai_ci" ])

    Ok(
        [ "Db"; "Name"; "Type"; "Language"; "Definer"; "Modified"; "Created"; "Security_type"; "Comment"
          "character_set_client"; "collation_connection"; "Database Collation" ],
        rows
    )
