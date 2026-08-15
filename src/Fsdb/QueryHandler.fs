/// Query dispatcher: a handful of connection-setup forms mysql CLI/PDO send
/// (`@@vars`, `SET`, `SHOW`) are still matched on trimmed/uppercased query
/// text, since they're session-variable probes rather than real SQL the
/// grammar needs to know about. Everything else — including `SELECT 1` and
/// `SELECT DATABASE()`, which the grammar and function registry already
/// handle byte-for-byte the same way — goes through
/// `Parser.parse -> Executor.execute`.
module Fsdb.QueryHandler

open System
open System.Text
open System.Text.RegularExpressions
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Storage
open Fsdb.InformationSchema

/// The wire-facing result shape is `Executor.QueryResult` itself — both the
/// parser-driven path and the text-probe special cases below construct the
/// same type, so there's exactly one definition of it.
type QueryResult = Fsdb.Executor.QueryResult

open Fsdb.Executor

let private syntaxError (sql: string) =
    // Truncate: this message gets echoed straight into an ERR packet, and an
    // unbounded echo of the query text is a reachable way to blow past
    // writePacketAsync's single-packet framing (see the Packet.fs framing
    // fix for the real root cause of >16 MiB payloads).
    let truncated = sql.Substring(0, min sql.Length 1024)

    Err(
        1064,
        sprintf
            "You have an error in your SQL syntax; check the manual that corresponds to your fsdb version for the right syntax to use near '%s'"
            truncated
    )

let private lookupVar (session: Session) (name: string) : string option =
    session.Variables |> Map.tryFind (name.ToLowerInvariant())

/// Finds every top-level `?` placeholder in `sql` — one that isn't inside a
/// `'...'`/`"..."` string literal, a `` `...` `` backtick identifier, or a
/// `-- `/`#`/`/* ... */` comment — and returns its char offset, in order.
/// Shared by COM_STMT_PREPARE (which only needs the count, for
/// COM_STMT_PREPARE_OK's param count) and COM_STMT_EXECUTE (which needs the
/// positions themselves, via `substitutePlaceholders`), so there's exactly
/// one definition of "what counts as a placeholder". Backslash escapes a
/// following quote inside `'`/`"` strings (MySQL's default
/// NO_BACKSLASH_ESCAPES-off behavior); backtick identifiers only escape via
/// a doubled backtick, matching MySQL's identifier-quoting rules.
let placeholderPositions (sql: string) : int list =
    let n = sql.Length
    let positions = ResizeArray<int>()
    let mutable i = 0

    while i < n do
        match sql.[i] with
        | ('\'' | '"' | '`') as quote ->
            let allowBackslashEscape = quote <> '`'
            i <- i + 1
            let mutable closed = false

            while not closed && i < n do
                if allowBackslashEscape && sql.[i] = '\\' && i + 1 < n then
                    i <- i + 2
                elif sql.[i] = quote then
                    if i + 1 < n && sql.[i + 1] = quote then
                        i <- i + 2
                    else
                        i <- i + 1
                        closed <- true
                else
                    i <- i + 1

            if not closed then
                i <- n
        | '-' when i + 1 < n && sql.[i + 1] = '-' ->
            let idx = sql.IndexOf('\n', i)
            i <- if idx = -1 then n else idx + 1
        | '#' ->
            let idx = sql.IndexOf('\n', i)
            i <- if idx = -1 then n else idx + 1
        | '/' when i + 1 < n && sql.[i + 1] = '*' ->
            let idx = sql.IndexOf("*/", i + 2)
            i <- if idx = -1 then n else idx + 2
        | '?' ->
            positions.Add i
            i <- i + 1
        | _ -> i <- i + 1

    List.ofSeq positions

/// Replaces each top-level `?` in `sql` (per `placeholderPositions`) with
/// the corresponding entry of `literals`, in the order both appear.
/// COM_STMT_EXECUTE's own bound-parameter count check guarantees the
/// lengths already match — this is the one substitution path prepared
/// statements use (see the `PreparedStmt` ponytail note in Session.fs for
/// why it's textual rather than a typed plan).
let substitutePlaceholders (sql: string) (literals: string list) : string =
    let positions = placeholderPositions sql
    let sb = StringBuilder()
    let mutable last = 0

    List.iter2
        (fun pos (lit: string) ->
            sb.Append(sql.Substring(last, pos - last)) |> ignore
            sb.Append(lit) |> ignore
            last <- pos + 1)
        positions
        literals

    sb.Append(sql.Substring last) |> ignore
    sb.ToString()

/// Renders a bound parameter value as a SQL literal safe to splice into the
/// stored statement text — the string-escaping mirrors MySQL's default
/// (`NO_BACKSLASH_ESCAPES` off) rules: backslash and single quote both
/// escape with a leading backslash. CR/LF are escaped too (`\r`/`\n`), not
/// left as raw bytes — `Parser.quotedStringChar` already round-trips those
/// two escapes back to CR/LF, but a raw CR spliced into the SQL text gets
/// silently normalized away by FParsec's CharStream on re-parse (it treats
/// bare `\r`/`\r\n` as line endings), corrupting any multi-line value
/// (e.g. an HTML textarea's CRLF body) on the way through a prepared
/// statement.
let private escapeSqlString (s: string) : string =
    s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n")

let valueToSqlLiteral (v: Value) : string =
    match v with
    | VNull -> "NULL"
    | VInt i -> string i
    | VDouble d -> d.ToString(Globalization.CultureInfo.InvariantCulture)
    | VDecimal d -> d.ToString(Globalization.CultureInfo.InvariantCulture)
    | VDate _
    | VDateTime _
    | VString _
    | VBytes _
    | VJson _ -> "'" + escapeSqlString (v |> toText |> Option.defaultValue "") + "'"

/// Matches `@@var` or `@@session.var` / `@@global.var`, optionally aliased,
/// optionally followed by a trailing `LIMIT n` (mysql CLI probes
/// `@@version_comment` this way at connect time).
let private atVarItem =
    Regex(@"^@@(?:SESSION\.|GLOBAL\.)?(\w+)(?:\s+AS\s+(\S+))?(?:\s+LIMIT\s+\d+)?$", RegexOptions.IgnoreCase)

/// `SELECT @@version`, `SELECT @@version AS v, @@sql_mode` etc. Errors with
/// 1193 ER_UNKNOWN_SYSTEM_VARIABLE (matching real MySQL) if any referenced
/// variable isn't known, instead of silently returning an empty string.
let private handleAtVarSelect (session: Session) (sql: string) : QueryResult =
    let exprs = sql.Substring("SELECT".Length).Trim()
    let items = exprs.Split(',') |> Array.map (fun s -> s.Trim())
    let parsed = items |> Array.map atVarItem.Match

    if parsed |> Array.forall (fun m -> m.Success) then
        let unknown =
            parsed
            |> Array.tryFind (fun m -> lookupVar session m.Groups.[1].Value |> Option.isNone)

        match unknown with
        | Some m -> Err(1193, sprintf "Unknown system variable '%s'" m.Groups.[1].Value)
        | None ->
            let cols =
                parsed
                |> Array.map (fun m ->
                    if m.Groups.[2].Success then
                        m.Groups.[2].Value
                    else
                        "@@" + m.Groups.[1].Value)
                |> Array.toList

            let vals =
                parsed
                |> Array.map (fun m -> lookupVar session m.Groups.[1].Value)
                |> Array.toList

            ResultSet(cols, [ vals ])
    else
        syntaxError sql

/// `SHOW VARIABLES` / `SHOW VARIABLES LIKE 'pattern'`.
let private handleShowVariables (session: Session) (sql: string) : QueryResult =
    let likeMatch = Regex.Match(sql, @"LIKE\s+'([^']*)'", RegexOptions.IgnoreCase)

    let matches (name: string) =
        if likeMatch.Success then
            Regex.IsMatch(
                name,
                Executor.likeToRegex likeMatch.Groups.[1].Value,
                RegexOptions.IgnoreCase ||| RegexOptions.Singleline
            )
        else
            true

    let rows =
        session.Variables
        |> Map.toList
        |> List.filter (fst >> matches)
        |> List.sortBy fst
        |> List.map (fun (k, v) -> [ Some k; Some v ])

    ResultSet([ "Variable_name"; "Value" ], rows)

// ---------------------------------------------------------------------------
// SHOW TABLES / DATABASES / COLUMNS / CREATE TABLE / INDEX / TABLE STATUS,
// and DESCRIBE — matched by text probe like SHOW VARIABLES above, since
// they're catalog-introspection statements read straight off `Storage`
// rather than something `Executor` evaluates rows through. Column shapes
// mirror real MySQL's `SHOW ...` output closely enough for mysql CLI and
// Laravel's non-`information_schema` probes (`Schema::hasTable`, etc.) to
// read what they expect.
// ---------------------------------------------------------------------------

let private likeSuffix (sql: string) : string option =
    let m = Regex.Match(sql, @"LIKE\s+'([^']*)'\s*$", RegexOptions.IgnoreCase)
    if m.Success then Some m.Groups.[1].Value else None

let private likeFilter (likeOpt: string option) (name: string) : bool =
    match likeOpt with
    | None -> true
    | Some pattern -> Regex.IsMatch(name, Executor.likeToRegex pattern, RegexOptions.IgnoreCase ||| RegexOptions.Singleline)

let private stripBackticks (s: string) = s.Trim().Trim('`')

/// `db.table` (or bare `table`) as `SHOW COLUMNS`/`SHOW INDEX`/`SHOW CREATE
/// TABLE` accept it directly, alongside their own `FROM db` clause — the
/// same split `Executor.splitQualified` does for statements the real parser
/// handles, duplicated here in miniature since these are still text-probed
/// rather than parsed.
let private splitDbTable (defaultDb: string) (name: string) : string * string =
    match name.Split('.') with
    | [| db; tbl |] -> db, tbl
    | _ -> defaultDb, name

let private showTablesRe =
    Regex(@"^SHOW\s+(FULL\s+)?TABLES(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private handleShowTables (session: Session) (sql: string) : QueryResult =
    let m = showTablesRe.Match sql
    let full = m.Groups.[1].Success
    let dbName = if m.Groups.[3].Success then stripBackticks m.Groups.[3].Value else session.Database |> Option.defaultValue defaultDatabase

    match Map.tryFind dbName (Session.currentStore session).Catalog with
    | None -> Err(1049, sprintf "Unknown database '%s'" dbName)
    | Some db ->
        let names =
            db |> Map.toList |> List.map (fun (_, t) -> t.OriginalName) |> List.filter (likeFilter (likeSuffix sql)) |> List.sort

        let col = sprintf "Tables_in_%s" dbName

        if full then
            ResultSet([ col; "Table_type" ], names |> List.map (fun n -> [ Some n; Some "BASE TABLE" ]))
        else
            ResultSet([ col ], names |> List.map (fun n -> [ Some n ]))

let private handleShowDatabases (session: Session) (sql: string) : QueryResult =
    let catalog = (Session.currentStore session).Catalog

    let names =
        "information_schema" :: (catalog |> Map.toList |> List.map fst)
        |> List.distinct
        |> List.filter (likeFilter (likeSuffix sql))
        |> List.sort

    ResultSet([ "Database" ], names |> List.map (fun n -> [ Some n ]))

/// Looks a table up straight off the catalog snapshot (rather than through
/// `Storage.scan`, which only hands back columns/rows) since `SHOW COLUMNS`/
/// `SHOW CREATE TABLE`/`SHOW INDEX` all need the whole `Storage.Table` —
/// indexes and foreign keys included, not just its column list.
let private findTable (session: Session) (dbName: string) (tableName: string) : Result<Table, int * string> =
    match Map.tryFind dbName (Session.currentStore session).Catalog with
    | None -> Error(1049, sprintf "Unknown database '%s'" dbName)
    | Some db ->
        match Map.tryFind (tableName.ToLowerInvariant()) db with
        | Some t -> Ok t
        | None -> Error(1146, sprintf "Table '%s' doesn't exist" tableName)

let private showColumnsRe =
    Regex(@"^SHOW\s+(FULL\s+)?COLUMNS\s+FROM\s+(\S+)(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private describeRe = Regex(@"^(?:DESCRIBE|DESC)\s+(\S+)\s*$", RegexOptions.IgnoreCase)

/// `SHOW [FULL] COLUMNS FROM t [FROM db]` and `DESCRIBE`/`DESC t` (which are
/// just `SHOW COLUMNS`'s narrower 5-column form under a different name).
let private handleShowColumns (session: Session) (full: bool) (dbName: string) (tableName: string) (likeOpt: string option) : QueryResult =
    match findTable session dbName tableName with
    | Error(code, msg) -> Err(code, msg)
    | Ok t ->
        let isNullable (c: Ast.ColumnDef) = if c.PrimaryKey || not c.Nullable then "NO" else "YES"
        let defaultCol (c: Ast.ColumnDef) = InformationSchema.defaultText c.Default
        let extra (c: Ast.ColumnDef) = if c.AutoIncrement then "auto_increment" else ""

        let cols = t.Columns |> List.filter (fun c -> likeFilter likeOpt c.Name)

        if full then
            let rows =
                cols
                |> List.map (fun c ->
                    [ Some c.Name
                      Some(InformationSchema.columnTypeText c.Type)
                      (if InformationSchema.isStringy c.Type then Some "utf8mb4_unicode_ci" else None)
                      Some(isNullable c)
                      Some(InformationSchema.columnKey t c)
                      defaultCol c
                      Some(extra c)
                      Some "select,insert,update,references"
                      Some "" ])

            ResultSet([ "Field"; "Type"; "Collation"; "Null"; "Key"; "Default"; "Extra"; "Privileges"; "Comment" ], rows)
        else
            let rows =
                cols
                |> List.map (fun c ->
                    [ Some c.Name; Some(InformationSchema.columnTypeText c.Type); Some(isNullable c); Some(InformationSchema.columnKey t c); defaultCol c; Some(extra c) ])

            ResultSet([ "Field"; "Type"; "Null"; "Key"; "Default"; "Extra" ], rows)

let private backtick (s: string) = "`" + s + "`"
let private backtickCols = List.map backtick >> String.concat ", "

/// Reconstructs plausible `CREATE TABLE` DDL from a table's stored metadata
/// for `SHOW CREATE TABLE` — not the original DDL text (nothing keeps that
/// around), a fresh rendering of the same columns/indexes/foreign keys, the
/// same way real MySQL's `SHOW CREATE TABLE` itself re-derives its output
/// from the catalog rather than echoing verbatim source.
let private showCreateTableDDL (t: Table) : string =
    let columnLine (c: Ast.ColumnDef) =
        let notNull = if c.PrimaryKey || not c.Nullable then "NOT NULL" else ""

        let defaultPart =
            match InformationSchema.defaultText c.Default with
            | Some d when c.Default = Some Ast.DCurrentTimestamp -> sprintf "DEFAULT %s" d
            | Some d -> sprintf "DEFAULT '%s'" d
            | None -> if c.PrimaryKey || not c.Nullable then "" else "DEFAULT NULL"

        let extra = if c.AutoIncrement then "AUTO_INCREMENT" else ""

        [ backtick c.Name; InformationSchema.columnTypeText c.Type; notNull; defaultPart; extra ]
        |> List.filter ((<>) "")
        |> String.concat " "

    let pkCols = t.Columns |> List.filter (fun c -> c.PrimaryKey) |> List.map (fun c -> c.Name)
    let pkLine = if pkCols.IsEmpty then [] else [ sprintf "PRIMARY KEY (%s)" (backtickCols pkCols) ]

    let indexLines =
        t.Indexes
        |> List.map (fun ix -> sprintf "%sKEY %s (%s)" (if ix.Unique then "UNIQUE " else "") (backtick ix.Name) (backtickCols ix.Columns))

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
        "CREATE TABLE %s (\n  %s\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci"
        (backtick t.OriginalName)
        (String.concat ",\n  " lines)

let private showCreateTableRe = Regex(@"^SHOW\s+CREATE\s+TABLE\s+(\S+)\s*$", RegexOptions.IgnoreCase)

/// `SHOW INDEX|INDEXES|KEYS FROM t [FROM db]` — one row per index column,
/// same shape `InformationSchema`'s `STATISTICS` table projects, just scoped
/// to one table and under `SHOW`'s own (differently-cased) column names.
let private showIndexRe =
    Regex(@"^SHOW\s+(?:INDEX|INDEXES|KEYS)\s+FROM\s+(\S+)(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private handleShowIndex (session: Session) (dbName: string) (tableName: string) : QueryResult =
    match findTable session dbName tableName with
    | Error(code, msg) -> Err(code, msg)
    | Ok t ->
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

        ResultSet(
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
            rows
        )

let private showTableStatusRe = Regex(@"^SHOW\s+TABLE\s+STATUS(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private handleShowTableStatus (session: Session) (sql: string) : QueryResult =
    let m = showTableStatusRe.Match sql
    let dbName = if m.Groups.[2].Success then stripBackticks m.Groups.[2].Value else session.Database |> Option.defaultValue defaultDatabase

    match Map.tryFind dbName (Session.currentStore session).Catalog with
    | None -> Err(1049, sprintf "Unknown database '%s'" dbName)
    | Some db ->
        let rows =
            db
            |> Map.toList
            |> List.map snd
            |> List.filter (fun t -> likeFilter (likeSuffix sql) t.OriginalName)
            |> List.sortBy (fun t -> t.OriginalName)
            |> List.map (fun t ->
                [ Some t.OriginalName
                  Some "InnoDB"
                  Some "10"
                  Some "Dynamic"
                  Some(string (List.length t.Rows))
                  Some "0"
                  Some "16384"
                  Some "0"
                  Some "0"
                  Some "0"
                  Some(string t.NextAutoId)
                  None
                  None
                  None
                  Some "utf8mb4_unicode_ci"
                  None
                  Some ""
                  Some "" ])

        ResultSet(
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

/// Returns the (possibly updated) session alongside the result: statements
/// like USE and SET change session state, and threading it through the
/// return value keeps `handle` a pure function of its inputs instead of
/// mutating the session out from under the caller.
let private setNames = Regex(@"^SET\s+NAMES\s+'?(\w+)'?", RegexOptions.IgnoreCase)

let private setVar =
    Regex(@"^SET\s+(?:SESSION\s+|GLOBAL\s+|@@(?:SESSION\.|GLOBAL\.)?)?(\w+)\s*=\s*(.+)$", RegexOptions.IgnoreCase)

let private unquote (v: string) =
    let v = v.Trim()

    if v.Length >= 2 && (v.StartsWith "'" && v.EndsWith "'" || v.StartsWith "\"" && v.EndsWith "\"") then
        v.Substring(1, v.Length - 2)
    else
        v

/// `SET NAMES x` and `SET [SESSION|@@session.]var = value` update
/// Session.Variables so a later SELECT @@var / SHOW VARIABLES reflects them.
/// Anything else (multi-assignment SET, GLOBAL persistence, ...) is accepted
/// and ignored — ponytail: single-assignment only, add comma-splitting if a
/// real client needs `SET a = 1, b = 2` in one statement.
let private handleSet (session: Session) (sql: string) : Session * QueryResult =
    let namesMatch = setNames.Match sql

    if namesMatch.Success then
        let charset = namesMatch.Groups.[1].Value

        let vars =
            session.Variables
            |> Map.add "character_set_client" charset
            |> Map.add "character_set_connection" charset
            |> Map.add "character_set_results" charset

        { session with Variables = vars }, Affected 0UL
    else
        let varMatch = setVar.Match sql

        if varMatch.Success then
            let name = varMatch.Groups.[1].Value.ToLowerInvariant()
            let value = unquote varMatch.Groups.[2].Value
            { session with Variables = Map.add name value session.Variables }, Affected 0UL
        else
            session, Affected 0UL

// ---------------------------------------------------------------------------
// Transactions: BEGIN/COMMIT/ROLLBACK, SET autocommit, SAVEPOINT. Matched by
// text probe (like SET/SHOW above) rather than taught to the grammar —
// these are session-control statements, not something `Executor` evaluates
// rows against. See `Session.Transaction` for how real (not no-op) snapshot
// isolation is implemented cheaply on top of `Storage.Store`'s already-public
// mutable fields.
// ---------------------------------------------------------------------------

let private beginTx = Regex(@"^(BEGIN(\s+WORK)?|START\s+TRANSACTION)$", RegexOptions.IgnoreCase)
let private commitTx = Regex(@"^COMMIT(\s+WORK)?$", RegexOptions.IgnoreCase)
let private rollbackTx = Regex(@"^ROLLBACK(\s+WORK)?$", RegexOptions.IgnoreCase)
let private savepointStmt = Regex(@"^SAVEPOINT\s+(\S+)$", RegexOptions.IgnoreCase)
let private rollbackToSavepointStmt = Regex(@"^ROLLBACK(\s+WORK)?\s+TO\s+(?:SAVEPOINT\s+)?(\S+)$", RegexOptions.IgnoreCase)
let private releaseSavepointStmt = Regex(@"^RELEASE\s+SAVEPOINT\s+(\S+)$", RegexOptions.IgnoreCase)

let private setAutocommit =
    Regex(
        @"^SET\s+(?:SESSION\s+|GLOBAL\s+|@@(?:SESSION\.|GLOBAL\.)?)?AUTOCOMMIT\s*=\s*'?(0|1)'?$",
        RegexOptions.IgnoreCase
    )

/// Commits the open transaction (if any) by copying its snapshot catalog
/// back over the shared store's — a no-op, matching real MySQL, if there
/// isn't one open.
let private commitSession (session: Session) : Session =
    match session.Tx with
    | Some tx ->
        lock session.Store.Lock (fun () -> session.Store.Catalog <- tx.Snapshot.Catalog)
        { session with Tx = None }
    | None -> session

/// Discards the open transaction's snapshot without touching the shared
/// store — a no-op, matching real MySQL, if there isn't one open.
let private rollbackSession (session: Session) : Session = { session with Tx = None }

/// Starts a new transaction, snapshotting the shared store's catalog as of
/// right now. MySQL implicitly commits an already-open transaction before
/// starting another one, so this does too rather than silently discarding
/// whatever the first transaction had done.
let private beginTransaction (session: Session) : Session =
    let session = commitSession session
    let snapshot: Store = { Catalog = session.Store.Catalog; Lock = obj () }
    { session with Tx = Some { Snapshot = snapshot; Savepoints = Map.empty } }

let private savepointNotFound (name: string) : QueryResult =
    Err(1305, sprintf "SAVEPOINT %s does not exist" name)

/// `SAVEPOINT name` outside an explicit transaction implicitly starts one,
/// matching real MySQL.
let private savepoint (name: string) (session: Session) : Session * QueryResult =
    let session = if session.Tx.IsNone then beginTransaction session else session

    match session.Tx with
    | Some tx -> { session with Tx = Some { tx with Savepoints = Map.add name tx.Snapshot.Catalog tx.Savepoints } }, Affected 0UL
    | None -> session, Affected 0UL // unreachable: beginTransaction always sets Tx

let private rollbackToSavepoint (name: string) (session: Session) : Session * QueryResult =
    match session.Tx |> Option.bind (fun tx -> Map.tryFind name tx.Savepoints |> Option.map (fun cat -> tx, cat)) with
    | Some(tx, catalog) ->
        tx.Snapshot.Catalog <- catalog
        session, Affected 0UL
    | None -> session, savepointNotFound name

/// Drops one savepoint. ponytail: real MySQL also drops every savepoint
/// established *after* the released one; this only drops the named one —
/// add that if a real client ever relies on the cascade.
let private releaseSavepoint (name: string) (session: Session) : Session * QueryResult =
    match session.Tx with
    | Some tx when Map.containsKey name tx.Savepoints ->
        { session with Tx = Some { tx with Savepoints = Map.remove name tx.Savepoints } }, Affected 0UL
    | _ -> session, savepointNotFound name

let private handleSetAutocommit (value: string) (session: Session) : Session * QueryResult =
    let session = { session with Variables = Map.add "autocommit" value session.Variables }

    let session =
        if value = "0" then
            (if session.Tx.IsNone then beginTransaction session else session)
        else
            commitSession session

    session, Affected 0UL

/// The function registry for one statement: `Functions.builtins` plus the
/// session-dependent entries that can't be plain `Value list -> Value`
/// closures until they're given a session to close over (`DATABASE()`
/// reads `session.Database`, `LAST_INSERT_ID()` reads `session.LastInsertId`,
/// `VERSION()` just reuses the same `@@version` value `SELECT @@version`
/// already serves).
let private registryFor (session: Session) : Functions.Registry =
    Functions.builtins
    |> Functions.registerScalar "DATABASE" (fun _ -> session.Database |> Option.map VString |> Option.defaultValue VNull)
    |> Functions.registerScalar "LAST_INSERT_ID" (fun _ -> VInt session.LastInsertId)
    |> Functions.registerScalar "VERSION" (fun _ -> lookupVar session "version" |> Option.map VString |> Option.defaultValue VNull)

/// Parses and executes anything that isn't one of the text-probe special
/// cases above. A parse failure that also looks like a `SELECT @@...` falls
/// back to the `@@`-probe path — tried only *after* the real parser, so a
/// query that merely contains the text `@@` somewhere (inside a string
/// literal, e.g. `WHERE email = 'a@@b.com'`) parses normally instead of
/// being hijacked into the probe path and rejected. Anything else is a 1064
/// syntax error with SQLSTATE 42000 (the mapping `errPayload` already has
/// for that code).
let private executeStatement (session: Session) (sql: string) (upper: string) : Session * QueryResult =
    match Parser.parse sql with
    | Result.Ok stmt ->
        let dbName = session.Database |> Option.defaultValue defaultDatabase

        let lastInsertId, result =
            Executor.execute (Session.currentStore session) (registryFor session) dbName session.LastInsertId stmt

        { session with LastInsertId = lastInsertId }, result
    | Result.Error _ when upper.StartsWith "SELECT" && upper.Contains "@@" -> session, handleAtVarSelect session sql
    | Result.Error _ -> session, syntaxError sql

/// Every statement form `dispatch` below recognizes purely by text probe
/// (SET/USE/SHOW/transaction control) rather than `Parser.parse` — mirrors
/// `dispatch`'s own leading guards (not the fallback `executeStatement`
/// case), kept as one predicate reusable from `prepareStatement`, since
/// PDO's default `ATTR_EMULATE_PREPARES = false` means even a plain `SET
/// FOREIGN_KEY_CHECKS=0` (Laravel's `Schema::disableForeignKeyConstraints`)
/// goes through COM_STMT_PREPARE, and the grammar itself has no `SET`/`SHOW`
/// production to validate it against.
let private isNonGrammarStatement (sql: string) (upper: string) : bool =
    setAutocommit.IsMatch sql
    || upper.StartsWith "SET "
    || rollbackToSavepointStmt.IsMatch sql
    || beginTx.IsMatch upper
    || commitTx.IsMatch upper
    || rollbackTx.IsMatch upper
    || savepointStmt.IsMatch sql
    || releaseSavepointStmt.IsMatch sql
    || upper.StartsWith "USE "
    || upper.StartsWith "SHOW VARIABLES"
    || upper = "SHOW WARNINGS"
    || upper.StartsWith "SHOW DATABASES"
    || upper.StartsWith "SHOW TABLE STATUS"
    || upper.StartsWith "SHOW TABLES"
    || upper.StartsWith "SHOW FULL TABLES"
    || showCreateTableRe.IsMatch sql
    || showColumnsRe.IsMatch sql
    || describeRe.IsMatch sql
    || showIndexRe.IsMatch sql

/// Parses and validates SQL for COM_STMT_PREPARE without executing it: a
/// parse failure is the same 1064 (code, message) pair a COM_QUERY syntax
/// error gets, so `Server` doesn't need its own copy of that formatting.
/// `Ok` carries the placeholder count `Server` needs for the
/// COM_STMT_PREPARE_OK reply.
///
/// The grammar has no notion of a `?` placeholder token (bound parameters
/// are this module's own textual-substitution concern, not the parser's —
/// see the `PreparedStmt` ponytail note in Session.fs), so validating the
/// statement as given would reject every parameterized query. Standing in
/// `NULL` for each placeholder validates the surrounding SQL is
/// syntactically real without needing the grammar to know placeholders
/// exist; the *stored* statement (what `Server` puts in `PreparedStmt.Sql`)
/// is still the original text with the real `?`s, untouched by this probe.
let prepareStatement (sql: string) : Result<int, int * string> =
    let placeholderCount = placeholderPositions sql |> List.length
    let probeSql = substitutePlaceholders sql (List.replicate placeholderCount "NULL")
    let trimmed = probeSql.Trim().TrimEnd(';').Trim()
    let upper = trimmed.ToUpperInvariant()

    if isNonGrammarStatement trimmed upper then
        Result.Ok placeholderCount
    else
        match Parser.parse probeSql with
        | Result.Ok _ -> Result.Ok placeholderCount
        | Result.Error _ ->
            match syntaxError sql with
            | Err(code, msg) -> Result.Error(code, msg)
            | _ -> Result.Error(1064, "syntax error")

let private dispatch (session: Session) (rawSql: string) : Session * QueryResult =
    let sql = rawSql.Trim().TrimEnd(';').Trim()
    let upper = sql.ToUpperInvariant()

    match upper with
    | _ when setAutocommit.IsMatch sql -> handleSetAutocommit (setAutocommit.Match sql).Groups.[1].Value session
    | _ when upper.StartsWith "SET " -> handleSet session sql
    | _ when rollbackToSavepointStmt.IsMatch sql -> rollbackToSavepoint (rollbackToSavepointStmt.Match sql).Groups.[2].Value session
    | _ when beginTx.IsMatch upper -> beginTransaction session, Affected 0UL
    | _ when commitTx.IsMatch upper -> commitSession session, Affected 0UL
    | _ when rollbackTx.IsMatch upper -> rollbackSession session, Affected 0UL
    | _ when savepointStmt.IsMatch sql -> savepoint (savepointStmt.Match sql).Groups.[1].Value session
    | _ when releaseSavepointStmt.IsMatch sql -> releaseSavepoint (releaseSavepointStmt.Match sql).Groups.[1].Value session
    | _ when upper.StartsWith "USE " ->
        { session with Database = Some(sql.Substring(4).Trim().Trim('`')) }, Affected 0UL
    | _ when upper.StartsWith "SHOW VARIABLES" -> session, handleShowVariables session sql
    | "SHOW WARNINGS" -> session, ResultSet([ "Level"; "Code"; "Message" ], [])
    | _ when upper.StartsWith "SHOW DATABASES" -> session, handleShowDatabases session sql
    | _ when upper.StartsWith "SHOW TABLE STATUS" -> session, handleShowTableStatus session sql
    | _ when upper.StartsWith "SHOW TABLES" || upper.StartsWith "SHOW FULL TABLES" -> session, handleShowTables session sql
    | _ when showCreateTableRe.IsMatch sql ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitDbTable sessionDb (stripBackticks (showCreateTableRe.Match sql).Groups.[1].Value)

        match findTable session dbName table with
        | Error(code, msg) -> session, Err(code, msg)
        | Ok t -> session, ResultSet([ "Table"; "Create Table" ], [ [ Some t.OriginalName; Some(showCreateTableDDL t) ] ])
    | _ when showColumnsRe.IsMatch sql ->
        let m = showColumnsRe.Match sql
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitDbTable sessionDb (stripBackticks m.Groups.[2].Value)
        let dbName = if m.Groups.[4].Success then stripBackticks m.Groups.[4].Value else dbName
        session, handleShowColumns session m.Groups.[1].Success dbName table (likeSuffix sql)
    | _ when describeRe.IsMatch sql ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitDbTable sessionDb (stripBackticks (describeRe.Match sql).Groups.[1].Value)
        session, handleShowColumns session false dbName table None
    | _ when showIndexRe.IsMatch sql ->
        let m = showIndexRe.Match sql
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitDbTable sessionDb (stripBackticks m.Groups.[1].Value)
        let dbName = if m.Groups.[3].Success then stripBackticks m.Groups.[3].Value else dbName
        session, handleShowIndex session dbName table
    | _ -> executeStatement session sql upper

/// No SQL engine failure should ever escape as a raw .NET exception — the
/// only two paths into `dispatch` (the parser, well guarded, and
/// `Storage.coerceValue`'s numeric casts, which are not) both funnel into
/// `Executor`, and `Server`'s connection loop only catches
/// `PacketTooLargeException`, so anything else here would otherwise unwind
/// straight to the socket read loop and silently drop the connection with
/// no ERR packet. Verified reachable: `INSERT INTO t VALUES (1e300)` into a
/// DECIMAL column throws `OverflowException` from `decimal d`.
let handle (session: Session) (rawSql: string) : Session * QueryResult =
    try
        match dispatch session rawSql with
        | _, Err(code, msg) as result ->
            eprintfn "fsdb: ERR %d %s -- query: %s" code msg rawSql
            result
        | result -> result
    with ex ->
        eprintfn "fsdb: EXN %s -- query: %s" ex.Message rawSql
        session, Err(1105, sprintf "Internal error: %s" ex.Message) // ER_UNKNOWN_ERROR
