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

/// Raw map lookup: the outer `option` is "is `name` even a known variable"
/// (unchanged since `Session.Variables` grew NULL-capable values) — `None`
/// there is the true "unknown variable" case (1193 below), while `Some
/// None` is a known variable currently holding SQL NULL. Callers that only
/// want the value (collapsing "unknown" and "known but NULL" the way
/// `SELECT @@x` does for both sigils) flatten it themselves.
let private lookupVar (session: Session) (name: string) : string option option =
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
        | '-' when i + 1 < n && sql.[i + 1] = '-' && (i + 2 >= n || System.Char.IsWhiteSpace sql.[i + 2]) ->
            // MySQL only treats `--` as a comment when whitespace/EOL follows
            // (`5--3` is arithmetic) — same rule as `Parser.stripVersionComments`.
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
exception PlaceholderCountMismatch of expected: int * got: int

let substitutePlaceholders (sql: string) (literals: string list) : string =
    let positions = placeholderPositions sql

    if positions.Length <> literals.Length then
        raise (PlaceholderCountMismatch(positions.Length, literals.Length))

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
    | VUInt u -> string u
    | VDouble d -> d.ToString(Globalization.CultureInfo.InvariantCulture)
    | VDecimal d -> d.ToString(Globalization.CultureInfo.InvariantCulture)
    | VBytes bytes -> "X'" + Convert.ToHexString(bytes) + "'"
    | VDate _
    | VDateTime _
    | VString _
    | VJson _ -> "'" + escapeSqlString (v |> toText |> Option.defaultValue "") + "'"

/// Matches `@@var` (system, optionally `session.`/`global.`-qualified) or
/// `@var` (user-defined), optionally aliased, optionally followed by a
/// trailing `LIMIT n` (mysql CLI probes `@@version_comment` this way at
/// connect time). Group 1 is the sigil (`"@"` or `"@@"`); group 2 the
/// `SESSION.`/`GLOBAL.` qualifier (empty for neither) — `resolveAtRef`
/// below is the only place that branches on either.
let private atVarItem =
    Regex(@"^(@@?)(SESSION\.|GLOBAL\.)?(\w+)(?:\s+AS\s+(\S+))?(?:\s+LIMIT\s+\d+)?$", RegexOptions.IgnoreCase)

/// Whether a matched scope prefix means GLOBAL — `Contains`, not
/// `StartsWith`: `atVarItem`'s scope group is bare (`"GLOBAL."`) but
/// `setVar`'s includes the `@@` sigil (`"@@GLOBAL."`), so the literal
/// "GLOBAL" can start at either position 0 or 2.
let private isGlobalScope (scope: string) : bool =
    scope.IndexOf("GLOBAL", StringComparison.OrdinalIgnoreCase) >= 0

/// The raw (unflattened) lookup behind `resolveAtRef` — kept separate so
/// `handleAtVarSelect` can still tell "unknown system variable" (outer
/// `None`) apart from "known, currently NULL" when deciding whether to
/// raise 1193, the one case `resolveAtRef`'s flattened `string option`
/// can't distinguish.
let private lookupAtRef (session: Session) (sigil: string) (scope: string) (name: string) : string option option =
    if sigil = "@@" then
        if isGlobalScope scope then
            Session.tryGlobalVariable session.Store name
        else
            lookupVar session name
    else
        Some(session.UserVariables |> Map.tryFind (name.ToLowerInvariant()) |> Option.flatten)

/// Resolves one `@@name`/`@name` reference to its current value. A system
/// variable (`@@`) is looked up in `Session.Variables` (or the store's
/// GLOBAL overrides for an explicit `@@GLOBAL.` qualifier); a user variable
/// (`@`) in `Session.UserVariables`, where "never `SET`" and "`SET` to
/// NULL" both collapse to `None` via `Option.flatten` — real MySQL reads
/// both back as NULL, and callers here don't need to tell them apart.
let private resolveAtRef (session: Session) (sigil: string) (scope: string) (name: string) : string option =
    lookupAtRef session sigil scope name |> Option.flatten

/// `SELECT @@version`, `SELECT @foo`, `SELECT @@version AS v, @foo` etc.
/// A referenced *system* variable that isn't known is a loud 1193
/// ER_UNKNOWN_SYSTEM_VARIABLE, matching real MySQL — but a *user* variable
/// is never "unknown" in real MySQL (any `@name` is legal); one that was
/// never `SET` just reads back as NULL, same as `resolveAtRef` above
/// already gives an unset one.
let private handleAtVarSelect (session: Session) (sql: string) : QueryResult =
    let exprs = sql.Substring("SELECT".Length).Trim()
    let items = exprs.Split(',') |> Array.map (fun s -> s.Trim())
    let parsed = items |> Array.map atVarItem.Match

    if parsed |> Array.forall (fun m -> m.Success) then
        let unknownSysVar =
            parsed
            |> Array.tryFind (fun m ->
                m.Groups.[1].Value = "@@"
                && lookupAtRef session m.Groups.[1].Value m.Groups.[2].Value m.Groups.[3].Value |> Option.isNone)

        match unknownSysVar with
        | Some m -> Err(1193, sprintf "Unknown system variable '%s'" m.Groups.[3].Value)
        | None ->
            let cols =
                parsed
                |> Array.map (fun m ->
                    if m.Groups.[4].Success then
                        m.Groups.[4].Value
                    else
                        m.Groups.[1].Value + m.Groups.[2].Value + m.Groups.[3].Value)
                |> Array.toList

            let vals =
                parsed
                |> Array.map (fun m -> resolveAtRef session m.Groups.[1].Value m.Groups.[2].Value m.Groups.[3].Value)
                |> Array.toList

            ResultSet(cols, [ vals ])
    else
        syntaxError sql

let private likeSuffix (sql: string) : string option =
    let m = Regex.Match(sql, @"LIKE\s+'([^']*)'\s*$", RegexOptions.IgnoreCase)
    if m.Success then Some m.Groups.[1].Value else None

/// `WHERE Variable_name = '...'` — SHOW STATUS/VARIABLES' other filter form,
/// folded into the same name-pattern the LIKE path uses (an exact name is a
/// wildcard-free pattern).
let private whereVariableName (sql: string) : string option =
    let m =
        Regex.Match(sql, @"WHERE\s+`?Variable_name`?\s*=\s*'([^']*)'\s*$", RegexOptions.IgnoreCase)

    if m.Success then Some m.Groups.[1].Value else None

let private statusFilter (sql: string) : string option =
    likeSuffix sql |> Option.orElse (whereVariableName sql)

/// `SHOW [SESSION|GLOBAL] VARIABLES [LIKE 'pattern']` — the GLOBAL form
/// reads the store-wide space (defaults + `SET GLOBAL` overrides) instead of
/// this session's values.
let private handleShowVariables (session: Session) (isGlobal: bool) (sql: string) : QueryResult =
    let pattern = statusFilter sql

    let matches (name: string) =
        match pattern with
        | Some p -> Regex.IsMatch(name, likeToRegex p, RegexOptions.IgnoreCase ||| RegexOptions.Singleline)
        | None -> true

    let source =
        if isGlobal then
            Session.globalVariablesSnapshot (Session.currentStore session)
        else
            session.Variables

    let rows =
        source
        |> Map.toList
        |> List.filter (fst >> matches)
        |> List.sortBy fst
        |> List.map (fun (k, v) -> [ Some k; v ])

    ResultSet([ "Variable_name"; "Value" ], rows)

// ---------------------------------------------------------------------------
// SHOW TABLES / DATABASES / COLUMNS / CREATE TABLE / INDEX / TABLE STATUS,
// and DESCRIBE — matched by text probe like SHOW VARIABLES above, since
// they're catalog-introspection statements read straight off `Storage`
// rather than something `Executor` evaluates rows through. Just the probe
// regexes and raw-SQL argument extraction live here; the actual
// `(columns, rows)`/`(code, message)` rendering is `InformationSchema`'s
// (colocated with its `information_schema`-view row-builders), reached via
// `showResult` below.
// ---------------------------------------------------------------------------

let private stripBackticks (s: string) = s.Trim().Trim('`')

let private showStatusRe =
    Regex(@"^SHOW\s+(?:SESSION\s+|GLOBAL\s+)?STATUS(\s|$)", RegexOptions.IgnoreCase)

let private showVariablesRe =
    Regex(@"^SHOW\s+(SESSION\s+|GLOBAL\s+)?VARIABLES(\s|$)", RegexOptions.IgnoreCase)

let private showEnginesRe = Regex(@"^SHOW\s+(?:STORAGE\s+)?ENGINES\s*$", RegexOptions.IgnoreCase)
let private showCharsetRe = Regex(@"^SHOW\s+(?:CHARACTER\s+SET|CHARSET)(\s|$)", RegexOptions.IgnoreCase)
let private showPrivilegesRe = Regex(@"^SHOW\s+PRIVILEGES\s*$", RegexOptions.IgnoreCase)
let private showProcesslistRe = Regex(@"^SHOW\s+(FULL\s+)?PROCESSLIST\s*$", RegexOptions.IgnoreCase)

let private showTriggersRe =
    Regex(@"^SHOW\s+TRIGGERS(?:\s+(?:FROM|IN)\s+(\S+))?", RegexOptions.IgnoreCase)

let private showEventsRe =
    Regex(@"^SHOW\s+EVENTS(?:\s+(?:FROM|IN)\s+(\S+))?", RegexOptions.IgnoreCase)

let private showRoutineStatusRe =
    Regex(@"^SHOW\s+(?:PROCEDURE|FUNCTION)\s+STATUS(\s|$)", RegexOptions.IgnoreCase)

let private killRe = Regex(@"^KILL\s+(?:(QUERY|CONNECTION)\s+)?(\d+)\s*$", RegexOptions.IgnoreCase)

/// `ALTER TABLE t DISABLE|ENABLE KEYS` — a MyISAM index-maintenance toggle
/// InnoDB itself treats as a no-op; mysqldump wraps every table's data with
/// it (`/*!40000 ... */`), so restores need the OK.
let private alterKeysRe =
    Regex(@"^ALTER\s+TABLE\s+(\S+)\s+(?:DISABLE|ENABLE)\s+KEYS\s*$", RegexOptions.IgnoreCase)

/// `SHOW [FULL] TABLES ... WHERE ...` filters — phpMyAdmin's DisableIS
/// listing sends `Table_type IN (...)`, mysqldump-era tooling sends
/// `Tables_in_<db> = '...'`. Any other filter column is a real 1054, the
/// same error MySQL gives for an unknown column in a SHOW filter.
let private showTablesWhereTypeRe =
    Regex(
        @"WHERE\s+`?Table_type`?\s+(?:IN\s*\(([^)]*)\)|=\s*'([^']*)')\s*$",
        RegexOptions.IgnoreCase
    )

let private showTablesWhereNameRe =
    Regex(@"WHERE\s+`?Tables_in_\w+`?\s*=\s*'([^']*)'\s*$", RegexOptions.IgnoreCase)

let private showTablesWhereColumnRe = Regex(@"WHERE\s+`?(\w+)`?", RegexOptions.IgnoreCase)


/// Lifts an `InformationSchema.ShowResult` into `QueryResult` — the one spot
/// `Ok`/`Error` become `ResultSet`/`Err`, so every `SHOW ...` case in
/// `runProbe` below reads as plain data flow instead of repeating the match.
let private showResult: InformationSchema.ShowResult -> QueryResult =
    function
    | Ok(cols, rows) -> ResultSet(cols, rows)
    | Error(code, msg) -> Err(code, msg)

/// `LIMIT [offset,] n` is accepted but applied trivially: fsdb never
/// actually buffers a diagnostics area, so `SHOW WARNINGS`/`SHOW ERRORS`
/// always answer empty regardless of the limit — real clients (mysql CLI,
/// mysqli's `mysqli_report`) send this form routinely and only care that it
/// doesn't 1064.
let private showWarningsRe =
    Regex(@"^SHOW\s+WARNINGS(\s+LIMIT\s+\d+(\s*,\s*\d+)?)?$", RegexOptions.IgnoreCase)

let private showErrorsRe =
    Regex(@"^SHOW\s+ERRORS(\s+LIMIT\s+\d+(\s*,\s*\d+)?)?$", RegexOptions.IgnoreCase)

let private showCountWarningsRe = Regex(@"^SHOW\s+COUNT\(\*\)\s+WARNINGS$", RegexOptions.IgnoreCase)
let private showCountErrorsRe = Regex(@"^SHOW\s+COUNT\(\*\)\s+ERRORS$", RegexOptions.IgnoreCase)

let private showTablesRe =
    Regex(@"^SHOW\s+(FULL\s+)?TABLES(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private handleShowTables (session: Session) (sql: string) : QueryResult =
    let m = showTablesRe.Match sql
    let full = m.Groups.[1].Success
    let dbName = if m.Groups.[3].Success then stripBackticks m.Groups.[3].Value else session.Database |> Option.defaultValue defaultDatabase

    let store = Session.currentStore session

    let fsdbTables =
        store.VirtualTables |> Map.toList |> List.map (fun (_, vt) -> vt.Name)

    let result = InformationSchema.showTables store.Catalog fsdbTables dbName full (likeSuffix sql)

    let typeMatch = showTablesWhereTypeRe.Match sql
    let nameMatch = showTablesWhereNameRe.Match sql

    if typeMatch.Success then
        // Every table in a real database is a BASE TABLE and every
        // information_schema one a SYSTEM VIEW, so the filter reduces to
        // "does the allowed set contain this database's one type".
        let allowed =
            if typeMatch.Groups.[1].Success then
                typeMatch.Groups.[1].Value.Split(',')
                |> Array.map (fun t -> t.Trim().Trim('\''))
                |> Array.toList
            else
                [ typeMatch.Groups.[2].Value ]
            |> List.map (fun t -> t.ToUpperInvariant())

        let thisDbType =
            if dbName.ToLowerInvariant() = "information_schema" then "SYSTEM VIEW" else "BASE TABLE"

        // A FULL row carries its own Table_type (the `fsdb` schema mixes
        // SYSTEM VIEW overlay tables with BASE TABLE real ones), so filter
        // per row when it's there and fall back to the per-database type
        // for the bare name-only shape.
        result
        |> Result.map (fun (cols, rows) ->
            cols,
            rows
            |> List.filter (fun row ->
                match row with
                | [ _; Some t ] -> List.contains (t.ToUpperInvariant()) allowed
                | _ -> List.contains thisDbType allowed))
        |> showResult
    elif nameMatch.Success then
        let wanted = nameMatch.Groups.[1].Value

        result
        |> Result.map (fun (cols, rows) -> cols, rows |> List.filter (fun row -> List.tryHead row = Some(Some wanted)))
        |> showResult
    elif showTablesWhereColumnRe.IsMatch sql then
        let column = (showTablesWhereColumnRe.Match sql).Groups.[1].Value
        Err(1054, sprintf "Unknown column '%s' in 'where clause'" column)
    else
        result |> showResult

let private handleShowDatabases (session: Session) (sql: string) : QueryResult =
    let store = Session.currentStore session

    let columns, rows = InformationSchema.showDatabases store.Catalog (not (Map.isEmpty store.VirtualTables)) (likeSuffix sql)
    let visible = rows |> List.filter (function | [ Some db ] -> Auth.canSeeDatabase store session.User db | _ -> false)
    ResultSet(columns, visible)

/// The catalog `SHOW COLUMNS`/`DESCRIBE`/`SHOW CREATE TABLE`/`SHOW INDEX`
/// should resolve against: a registered `fsdb` virtual table isn't in the
/// real catalog (and the real `fsdb` database may not even exist while the
/// registry keeps the schema alive), yet anything `SHOW TABLES` lists must
/// be describable — clients/ORMs introspect via DESCRIBE. Splicing the
/// overlay in as a one-table catalog reuses the existing renderers
/// unchanged instead of teaching each one about the registry.
let private catalogWithOverlay (session: Session) (dbName: string) (table: string) : Storage.Catalog =
    let store = Session.currentStore session

    if String.Equals(dbName, Storage.defaultDatabase, StringComparison.OrdinalIgnoreCase) then
        match Map.tryFind (table.ToLowerInvariant()) store.VirtualTables with
        | Some vt -> Map.ofList [ dbName, Map.ofList [ table.ToLowerInvariant(), Storage.virtualTableStub vt ] ]
        | None -> store.Catalog
    else
        store.Catalog

let private showColumnsRe =
    Regex(@"^SHOW\s+(FULL\s+)?COLUMNS\s+FROM\s+(\S+)(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private describeRe = Regex(@"^(?:DESCRIBE|DESC)\s+(\S+)\s*$", RegexOptions.IgnoreCase)

let private showCreateTableRe = Regex(@"^SHOW\s+CREATE\s+TABLE\s+(\S+)\s*$", RegexOptions.IgnoreCase)
let private showCreateViewRe = Regex(@"^SHOW\s+CREATE\s+VIEW\s+(\S+)\s*$", RegexOptions.IgnoreCase)

let private showIndexRe =
    Regex(@"^SHOW\s+(?:INDEX|INDEXES|KEYS)\s+FROM\s+(\S+)(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private showTableStatusRe = Regex(@"^SHOW\s+TABLE\s+STATUS(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private handleShowTableStatus (session: Session) (sql: string) : QueryResult =
    let m = showTableStatusRe.Match sql
    let dbName = if m.Groups.[2].Success then stripBackticks m.Groups.[2].Value else session.Database |> Option.defaultValue defaultDatabase

    InformationSchema.showTableStatus (Session.currentStore session).Catalog dbName (likeSuffix sql) |> showResult

/// Returns the (possibly updated) session alongside the result: statements
/// like USE and SET change session state, and threading it through the
/// return value keeps `handle` a pure function of its inputs instead of
/// mutating the session out from under the caller.
///
/// These match one already-comma-split assignment (`splitSetAssignments`
/// strips the leading `SET` keyword before splitting), not a whole `SET`
/// statement.
let private setNames = Regex(@"^NAMES\s+'?(\w+)'?(?:\s+COLLATE\s+'?(\w+)'?)?", RegexOptions.IgnoreCase)

/// Group 1 (optional) is the scope prefix — `"SESSION "`, `"GLOBAL "`,
/// `"@@SESSION."`, `"@@GLOBAL."`, or bare `"@@"` — read by `parseSetFragment`
/// via `isGlobalScope` to route `GLOBAL`-scoped assignments to the store's
/// global-variable map instead of this session's own `Variables`.
let private setVar =
    Regex(@"^(SESSION\s+|GLOBAL\s+|@@SESSION\.|@@GLOBAL\.|@@)?(\w+)\s*=\s*(.+)$", RegexOptions.IgnoreCase)

/// `SET @name = ...` — a user-defined variable assignment, distinct from
/// `setVar`'s system-variable form (bare `\w+`, or `@@`-prefixed): real
/// MySQL never validates a user variable's name, so this always succeeds
/// where `setVar` would otherwise report it "unknown".
let private setUserVar = Regex(@"^@(\w+)\s*=\s*(.+)$", RegexOptions.IgnoreCase)

/// Best-effort name extraction for the "this looks like an assignment but
/// neither `setVar` nor `setUserVar` matched it" error below.
let private setVarNameForError = Regex(@"^(?:SESSION\s+|GLOBAL\s+)?(\S+?)\s*=", RegexOptions.IgnoreCase)

let private unquote (v: string) =
    let v = v.Trim()

    if v.Length >= 2 && (v.StartsWith "'" && v.EndsWith "'" || v.StartsWith "\"" && v.EndsWith "\"") then
        v.Substring(1, v.Length - 2)
    else
        v

/// Resolves a `SET` fragment's right-hand side: a bare `@@sysvar`/`@uservar`
/// reference reads that variable's current value (via `resolveAtRef`,
/// shared with `SELECT @@x`/`SELECT @x`), matching real MySQL's
/// `SET x = @@y` / `SET x = @y` — the mysqldump preamble/postamble idiom of
/// saving a setting into a user variable and restoring it later
/// (`SET @OLD_SQL_MODE=@@SQL_MODE` ... `SET SQL_MODE=@OLD_SQL_MODE`) needs
/// exactly this, not full expression evaluation. A literal `NULL` resolves
/// to `None` too, same as an unset variable reference — `SET @x = NULL`
/// must leave `@x` a real SQL NULL, not the four-character string `"NULL"`.
/// Anything else is a literal, taken unquoted.
let private resolveSetRhs (session: Session) (rhs: string) : string option =
    let rhs = rhs.Trim()
    let m = atVarItem.Match rhs

    if m.Success then
        resolveAtRef session m.Groups.[1].Value m.Groups.[2].Value m.Groups.[3].Value
    else if String.Equals(rhs, "NULL", StringComparison.OrdinalIgnoreCase) then
        // Checked against the raw (still-quoted) rhs: `SET @x = 'NULL'` is
        // the four-character string, not the NULL literal — only a bare,
        // unquoted NULL keyword means "no value".
        None
    else
        Some(unquote rhs)

/// Whether a `sql_mode` value (comma-separated, as stored in
/// `Session.Variables`) still contains STRICT_TRANS_TABLES/STRICT_ALL_TABLES
/// — shared by `handleSet` (which records a new `sql_mode`) and
/// `executeStatement` (which re-derives it from the *current* session before
/// every statement, since `Storage.Store.StrictMode` is store-wide state
/// shared by every connection; see the note on `Storage.Store.StrictMode`).
let private isStrictSqlMode (value: string) : bool =
    value.Split(',')
    |> Array.exists (fun m ->
        let m = m.Trim()
        String.Equals(m, "STRICT_TRANS_TABLES", StringComparison.OrdinalIgnoreCase)
        || String.Equals(m, "STRICT_ALL_TABLES", StringComparison.OrdinalIgnoreCase))

/// `SET a = 1, b = 2` is one statement assigning several variables — real
/// clients use it (Laravel's `MySqlConnector::configureConnection` sends
/// `SET NAMES 'utf8mb4', SESSION sql_mode='...'` as one call). Splits on
/// commas outside quotes and outside parens, after stripping the leading
/// `SET` keyword, so neither a quoted value with its own commas
/// (`sql_mode`'s comma-separated mode list) nor a function call's argument
/// list (`SET @@SESSION.sql_mode = CONCAT(@@sql_mode, ',ANSI_QUOTES')`) gets
/// split apart.
let private splitSetAssignments (sql: string) : string list =
    let body = Regex.Replace(sql, @"^SET\s+", "", RegexOptions.IgnoreCase)
    let parts = ResizeArray()
    let current = StringBuilder()
    let mutable quoteChar = None
    let mutable escaped = false
    let mutable parenDepth = 0
    let mutable hasContent = false

    for c in body do
        match quoteChar with
        | Some _ when escaped ->
            escaped <- false
            current.Append c |> ignore
        | Some _ when c = '\\' ->
            escaped <- true
            current.Append c |> ignore
        | Some q when c = q ->
            quoteChar <- None
            current.Append c |> ignore
        | Some _ -> current.Append c |> ignore
        | None ->
            match c with
            | '\'' | '"' ->
                quoteChar <- Some c
                hasContent <- true
                current.Append c |> ignore
            | '(' ->
                parenDepth <- parenDepth + 1
                hasContent <- true
                current.Append c |> ignore
            | ')' ->
                parenDepth <- max 0 (parenDepth - 1)
                hasContent <- true
                current.Append c |> ignore
            | ',' when parenDepth = 0 ->
                if hasContent then
                    parts.Add(current.ToString())

                current.Clear() |> ignore
                hasContent <- false
            | _ ->
                hasContent <- hasContent || not (Char.IsWhiteSpace c)
                current.Append c |> ignore

    if hasContent then
        parts.Add(current.ToString())

    parts |> Seq.map (fun s -> s.Trim()) |> Seq.filter (fun s -> s <> "") |> List.ofSeq

/// One `SET` fragment's parsed effect, applied only once every fragment in
/// the statement has parsed successfully (see `handleSet`) — mirrors real
/// MySQL executing a multi-assignment `SET` all-or-nothing rather than
/// left-to-right with partial effect.
type private SetAction =
    | SetNamesAction of charset: string * collation: string option
    | SetVarAction of name: string * value: string option * isGlobal: bool
    | SetUserVarAction of name: string * value: string option

/// System variables real MySQL accepts an explicit `NULL` for (rather than
/// the 1231 "can't be set to the value of NULL" every other variable
/// raises) — connector handshakes reset the results charset exactly this
/// way. Grows as real clients ask for more.
let private nullableSystemVars = Set.ofList [ "character_set_results" ]

let private globalOnlyLimitVariables =
    Set.ofList [ "max_allowed_packet"; "max_connections"; "max_prepared_stmt_count"; "net_write_timeout" ]
let private maxUserVariables = 65536

/// Parses one comma-split fragment into the variable(s) it would assign,
/// without touching `session`/`Store` — `handleSet` only applies any of
/// these once every fragment in the statement has parsed. Reads `session`
/// only to resolve a `@@x`/`@y` right-hand side (`resolveSetRhs`) against
/// its state *before* this `SET` statement — a later fragment in the same
/// multi-assignment doesn't see an earlier fragment's not-yet-applied
/// write, consistent with the whole statement being all-or-nothing below.
let private parseSetFragment (sql: string) (session: Session) (fragment: string) : Result<SetAction, QueryResult> =
    let namesMatch = setNames.Match fragment

    if namesMatch.Success then
        let explicitCollation =
            if namesMatch.Groups.[2].Success then
                Some namesMatch.Groups.[2].Value
            else
                None

        // MySQL rejects a `SET NAMES x COLLATE unknown` outright (1273),
        // same as the collation_connection assignment path below.
        match explicitCollation |> Option.map Collation.tryFind with
        | Some None -> Error(Err(1273, sprintf "Unknown collation: '%s'" namesMatch.Groups.[2].Value))
        | _ -> Ok(SetNamesAction(namesMatch.Groups.[1].Value, explicitCollation))
    else
        let userVarMatch = setUserVar.Match fragment

        if userVarMatch.Success then
            Ok(
                SetUserVarAction(
                    userVarMatch.Groups.[1].Value.ToLowerInvariant(),
                    resolveSetRhs session userVarMatch.Groups.[2].Value
                )
            )
        else
            let varMatch = setVar.Match fragment

            if varMatch.Success then
                let isGlobal = isGlobalScope varMatch.Groups.[1].Value
                let name = varMatch.Groups.[2].Value.ToLowerInvariant()

                match resolveSetRhs session varMatch.Groups.[3].Value with
                | Some value when name = "collation_connection" ->
                    match Collation.tryFind value with
                    | Some _ -> Ok(SetVarAction(name, Some value, isGlobal))
                    | None -> Error(Err(1273, sprintf "Unknown collation: '%s'" value))
                | Some value -> Ok(SetVarAction(name, Some value, isGlobal))
                | None when nullableSystemVars.Contains name -> Ok(SetVarAction(name, None, isGlobal))
                | None -> Error(Err(1231, sprintf "Variable '%s' can't be set to the value of 'NULL'" name))
            else
                match setVarNameForError.Match fragment with
                | m when m.Success -> Error(Err(1193, sprintf "Unknown system variable '%s'" m.Groups.[1].Value))
                | _ -> Error(syntaxError sql)

/// Applies one already-parsed `SetAction` to `session`, including the
/// `Store`-level side effects (`setForeignKeyChecks`/`setStrictMode`)
/// `foreign_key_checks`/`sql_mode` trigger.
let private applySetAction (session: Session) (action: SetAction) : Session =
    match action with
    | SetNamesAction(charset, collation) ->
        // `SET NAMES x` also drives `collation_connection` in MySQL — the
        // charset's default collation, or the explicit `COLLATE` when
        // written (Laravel's connector sends `SET NAMES utf8mb4 COLLATE
        // utf8mb4_unicode_ci`). latin1/ascii's real defaults
        // (latin1_swedish_ci/ascii_general_ci) aren't registered; the
        // server default ai_ci stands in, the same approximation column
        // charsets use. `binary` compares byte-wise, i.e. utf8mb4_bin.
        let connectionCollation =
            match collation |> Option.bind Collation.tryFind with
            | Some col -> Some col
            | None ->
                match charset.ToLowerInvariant() with
                | "binary" -> Collation.tryFind "utf8mb4_bin"
                | "utf8mb4"
                | "utf8"
                | "latin1"
                | "ascii" -> Some Collation.defaultCollation
                | _ -> None

        connectionCollation |> Option.iter (setConnectionCollation session.Store)

        { session with
            Variables =
                session.Variables
                |> Map.add "character_set_client" (Some charset)
                |> Map.add "character_set_connection" (Some charset)
                |> Map.add "character_set_results" (Some charset)
                |> Map.add
                    "collation_connection"
                    (connectionCollation |> Option.map (fun c -> c.Name)) }
    | SetVarAction(name, value, true) ->
        // `SET GLOBAL`/`SET @@GLOBAL.` writes only the store-wide override —
        // it never touches this session's own `Variables` (a live session
        // keeps whatever it already had; only a session created afterwards
        // picks the new value up, via `Session.create`'s seeding), and none
        // of the per-connection `Store` side effects below apply either:
        // `foreign_key_checks`/`sql_mode`/`collation_connection` are
        // inherently per-session state, not something a GLOBAL write should
        // reach into this connection's own mutable cells to flip.
        match value with
        | Some value when Limits.isReportableSetting name -> Limits.applySetting name value |> ignore
        | _ -> Session.setGlobalVariable session.Store name value

        session
    | SetVarAction(name, value, false) ->
        // Neither `foreign_key_checks` nor `sql_mode` is in
        // `nullableSystemVars`, so `value` is always `Some` here — but the
        // type is shared with every other system variable, so this still
        // has to handle `None`.
        if name = "foreign_key_checks" then
            value |> Option.iter (fun v -> setForeignKeyChecks session.Store (v.Trim() <> "0"))

        if name = "sql_mode" then
            value |> Option.iter (fun v -> setStrictMode session.Store (isStrictSqlMode v))

        if name = "collation_connection" then
            value
            |> Option.iter (fun v ->
                match Collation.tryFind v with
                | Some col -> setConnectionCollation session.Store col
                | None -> ())

        { session with Variables = Map.add name value session.Variables }
    | SetUserVarAction(name, value) -> { session with UserVariables = Map.add name value session.UserVariables }

let private validateSetAction (session: Session) (action: SetAction) : Result<unit, QueryResult> =
    match action with
    | SetVarAction(_, _, true) when not (Auth.hasGlobalPriv session.Store session.User "SUPER") ->
        Error(Err(1227, "Access denied; you need (at least one of) the SUPER privilege(s) for this operation"))
    | SetVarAction(name, Some value, true) when Limits.isReportableSetting name ->
        Limits.validateSetting name value |> Result.mapError (fun message -> Err(1232, message))
    | SetVarAction(name, _, false) when globalOnlyLimitVariables.Contains name ->
        Error(Err(1229, sprintf "Variable '%s' is a GLOBAL variable and should be set with SET GLOBAL" name))
    | _ -> Ok()

/// `SET NAMES x`, `SET [SESSION|@@session.]var = value`, and `SET @var =
/// value` update `Session.Variables`/`Session.UserVariables` so a later
/// `SELECT @@var`/`SELECT @var`/`SHOW VARIABLES` reflects them, one
/// comma-split assignment at a time (`splitSetAssignments`). Two-phase:
/// every fragment is parsed first (`parseSetFragment`), and only if *all*
/// of them parse does any of them apply (`applySetAction`) — an assignment
/// recognizably shaped like one but matched by neither `setVar` nor
/// `setUserVar` aborts the whole statement with a loud 1193 and no partial
/// effect, same as real MySQL abandoning a multi-assignment `SET` on its
/// first bad name without acting on the assignments before it.
let private handleSet (session: Session) (sql: string) : Session * QueryResult =
    match splitSetAssignments sql |> traverse (parseSetFragment sql session) with
    | Error result -> session, result
    | Ok actions ->
        let userVariableNames =
            actions
            |> List.choose (function SetUserVarAction(name, _) -> Some name | _ -> None)
            |> Set.ofList

        let resultingUserVariableCount =
            userVariableNames
            |> Set.fold (fun count name -> if Map.containsKey name session.UserVariables then count else count + 1) session.UserVariables.Count

        if resultingUserVariableCount > maxUserVariables then
            session, Err(1105, "Too many user-defined variables")
        else
            match actions |> traverse (validateSetAction session) with
            | Error result -> session, result
            | Ok _ -> (actions |> List.fold applySetAction session), Affected 0UL

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

/// `SET PASSWORD [FOR user] = 'pw'` — probed ahead of the generic `SET `
/// check, which would otherwise treat PASSWORD as a session variable. The
/// optional user part captures everything before `=` (`'bob'@'%'`, `bob`);
/// `runProbe` strips the quoting/host.
/// The account name out of a probe-captured `'name'@'host'` / `name@host` /
/// bare `name` reference — accounts match by name only (see `Auth`), so the
/// host part is dropped along with any quoting.
let private userNameOf (userRef: string) : string =
    userRef.Split('@').[0].Trim().Trim([| '\''; '`'; '"' |])

let private setPasswordRe =
    Regex(@"^SET\s+PASSWORD\s*(?:FOR\s+([^=\s]+)\s*)?=\s*'([^']*)'\s*;?$", RegexOptions.IgnoreCase)

/// `SHOW GRANTS [FOR 'user'@'host' | FOR CURRENT_USER[()]]`.
let private showGrantsRe = Regex(@"^SHOW\s+GRANTS(?:\s+FOR\s+(.+?))?\s*;?$", RegexOptions.IgnoreCase)

/// `FLUSH [LOCAL] PRIVILEGES` — a no-op OK: privilege reads always hit the
/// live mysql.* rows, there's no cache to flush.
let private flushPrivilegesRe = Regex(@"^FLUSH\s+(?:LOCAL\s+)?PRIVILEGES\s*;?$", RegexOptions.IgnoreCase)

/// MySqlConnector's real `BeginTransaction[Async]` handshake explicitly
/// selects REPEATABLE READ before it sends START TRANSACTION — the
/// isolation model FSDB's private transaction snapshot actually implements.
/// READ COMMITTED/READ UNCOMMITTED are accepted too: FSDB's actual
/// isolation (one snapshot per transaction, ~REPEATABLE READ) is *stronger*
/// than either, so recording the level a client asked for is truthful even
/// though every transaction still runs the same snapshot underneath.
/// SERIALIZABLE is matched here too, but only so `runProbe` can reject it
/// outright (see its ponytail note) instead of it falling through to the
/// generic `SET name = value` parser and dying with a confusing 1064.
let private setTransactionIsolation =
    Regex(
        @"^SET\s+(?:SESSION\s+)?TRANSACTION\s+ISOLATION\s+LEVEL\s+(REPEATABLE\s+READ|READ\s+COMMITTED|READ\s+UNCOMMITTED|SERIALIZABLE)$",
        RegexOptions.IgnoreCase
    )

/// MySQL's `transaction_isolation` value is the level name with spaces
/// replaced by hyphens (`"REPEATABLE READ"` -> `"REPEATABLE-READ"`).
let private normalizeIsolationLevel (raw: string) : string =
    Regex.Replace(raw.Trim().ToUpperInvariant(), @"\s+", "-")

let private registryFor (session: Session) : Functions.Registry =
    let collapseExtension name (extension: Functions.ScalarFunction) registry =
        let invoke args =
            let context: Functions.QueryContext =
                { Database = session.Database
                  User = session.User
                  Cancellation = Storage.queryCancellation.Value }

            extension.Fn context args

        Functions.registerScalar name invoke registry

    let registry =
        session.CustomFunctions.Scalars
        |> Map.fold (fun current name fn -> Functions.registerScalar name fn current) Functions.builtins
        |> fun current ->
            session.CustomFunctions.Aggregates
            |> Map.fold (fun registry name fn -> Functions.registerAggregate name fn registry) current
        |> fun current ->
            session.CustomFunctions.Extensions
            |> Map.fold (fun registry name extension -> collapseExtension name extension registry) current
        |> fun current -> { current with Extensions = session.CustomFunctions.Extensions }

    let database _ = session.Database |> Option.map VString |> Option.defaultValue VNull

    registry
    |> Functions.registerScalar "DATABASE" database
    |> Functions.registerScalar "SCHEMA" database
    |> Functions.registerScalar "LAST_INSERT_ID" (fun _ -> VInt session.LastGeneratedId)
    |> Functions.registerScalar
        "VERSION"
        (fun _ -> lookupVar session "version" |> Option.flatten |> Option.map VString |> Option.defaultValue VNull)
    |> Functions.registerScalar "CONNECTION_ID" (fun _ -> VInt(int64 session.ConnectionId))
    |> Functions.registerScalar "CURRENT_USER" (fun _ -> VString(session.User + "@%"))
    |> Functions.registerScalar "USER" (fun _ -> VString(session.User + "@localhost"))
    |> Functions.registerScalar "SESSION_USER" (fun _ -> VString(session.User + "@localhost"))

/// Commits the open transaction (if any) by merging its snapshot catalog
/// back into the shared store's (`Storage.mergeCatalogInto`, a CAS-safe
/// three-way merge against whatever the live catalog is *right now* — not a
/// stale copy from BEGIN — so a concurrent write to another database, or an
/// untouched table in this one, during the transaction's lifetime survives)
/// — a no-op, matching real MySQL, if there isn't one open.
let private commitSession (session: Session) : Session =
    match session.Tx with
    | Some tx ->
        let dbName = session.Database |> Option.defaultValue defaultDatabase
        let registry = registryFor session
        let foundRows = session.Capabilities &&& Fsdb.Protocol.ClientFoundRows <> 0u

        let replay () =
            let baseCatalog = session.Store.Catalog
            let snapshot = Storage.beginTransactionSnapshot session.Store
            setStrictMode snapshot (lookupVar session "sql_mode" |> Option.flatten |> Option.map isStrictSqlMode |> Option.defaultValue true)
            snapshot.SessionUser <- session.User

            let mutable ids = tx.ReplayStartIds

            for statement in tx.Statements do
                let nextIds, result = Executor.execute snapshot registry dbName ids foundRows statement

                match result with
                | Err _ -> raise (Storage.LockWaitTimeout dbName)
                | _ -> ids <- nextIds

            baseCatalog, snapshot

        let committedSnapshot =
            let timeout =
                lookupVar session "innodb_lock_wait_timeout"
                |> Option.flatten
                |> Option.bind (fun value -> match Int32.TryParse value with | true, seconds -> Some(TimeSpan.FromSeconds(float seconds)) | _ -> None)
                |> Option.defaultWith Limits.lockWaitTimeout

            if not (Threading.Monitor.TryEnter(session.Store.Lock, timeout)) then
                raise (Storage.LockWaitTimeout dbName)

            try
                try
                    Storage.mergeCatalogInto session.Store tx.BaseCatalog tx.Snapshot.Catalog
                    tx.Snapshot
                with Storage.LockWaitTimeout _ ->
                    let baseCatalog, snapshot = replay ()
                    Storage.mergeCatalogInto session.Store baseCatalog snapshot.Catalog
                    snapshot
            finally
                Threading.Monitor.Exit session.Store.Lock

        Storage.commitTransactionEvents session.Store committedSnapshot

        { session with Tx = None }
    | None -> session

/// Discards the open transaction's snapshot — a no-op, matching real MySQL,
/// if there isn't one open — except for each table's AUTO_INCREMENT
/// counter, which MySQL never rolls back (an id an aborted INSERT consumed
/// stays burned). Bumps the shared store's counter up to the snapshot's if
/// the snapshot ran it ahead (`Storage.bumpAutoIncrementsInto`, same
/// CAS-safe merge as `commitSession`); leaves everything else (rows,
/// schema) alone.
let private rollbackSession (session: Session) : Session =
    match session.Tx with
    | Some tx ->
        Storage.bumpAutoIncrementsInto session.Store tx.Snapshot.Catalog
    | None -> ()

    { session with Tx = None }

/// Starts a new transaction with a provisional snapshot. The real snapshot
/// is rebound at the first database statement, matching InnoDB's default
/// deferred consistent-snapshot timing. MySQL implicitly commits an already-open
/// transaction before starting another one, so this does too.
let private beginTransaction (session: Session) : Session =
    let session = commitSession session
    let baseCatalog = session.Store.Catalog
    let snapshot = Storage.beginTransactionSnapshot session.Store

    { session with
        Tx =
            Some
                { Snapshot = snapshot
                  BaseCatalog = baseCatalog
                  Statements = []
                  ReplayStartIds = session.LastInsertId, session.LastGeneratedId
                  Seeded = false
                  Savepoints = Map.empty
                  NextSavepointSeq = 0 } }

/// Establishes the repeatable-read snapshot at the first database statement.
/// Writes remain private until commit, where row-level three-way merging
/// combines disjoint changes and rejects overlapping ones.
let startTransactionStatement (session: Session) : Session =
    match session.Tx with
    | Some tx when not tx.Seeded ->
        let baseCatalog = session.Store.Catalog
        let snapshot = Storage.beginTransactionSnapshot session.Store

        let savepoints =
            tx.Savepoints
            |> Map.map (fun _ (seq, _, eventCount, statementCount) -> seq, baseCatalog, eventCount, statementCount)

        { session with
            Tx =
                Some
                    { tx with
                        Snapshot = snapshot
                        BaseCatalog = baseCatalog
                        Statements = []
                        ReplayStartIds = session.LastInsertId, session.LastGeneratedId
                        Seeded = true
                        Savepoints = savepoints } }
    | Some _ -> session
    | None -> session

/// Rolls an abandoned connection's transaction back.
let closeSession (session: Session) : unit =
    rollbackSession session |> ignore

let private savepointNotFound (name: string) : QueryResult =
    Err(1305, sprintf "SAVEPOINT %s does not exist" name)

/// `SAVEPOINT name` outside an explicit transaction implicitly starts one,
/// matching real MySQL. Re-issuing an existing name deletes the old
/// savepoint and sets a new one in its place (also real MySQL behavior) —
/// `Map.add` already does the "replace" half; giving it a fresh
/// `NextSavepointSeq` tick does the "moves to the end of the establishment
/// order" half, so a savepoint set *before* this one but named earlier
/// doesn't wrongly get cascade-dropped by a later `ROLLBACK TO`/`RELEASE`
/// naming something established before this redefinition.
let private savepoint (name: string) (session: Session) : Session * QueryResult =
    let session = if session.Tx.IsNone then beginTransaction session else session

    match session.Tx with
    | Some tx ->
        let eventCount = tx.Snapshot.PendingEvents |> Option.map (fun b -> b.Count) |> Option.defaultValue 0
        let seq = tx.NextSavepointSeq

        { session with
            Tx =
                Some
                    { tx with
                        Savepoints = Map.add name (seq, tx.Snapshot.Catalog, eventCount, tx.Statements.Length) tx.Savepoints
                        NextSavepointSeq = seq + 1 } },
        Affected 0UL
    | None -> session, Affected 0UL // unreachable: beginTransaction always sets Tx

let private rollbackToSavepoint (name: string) (session: Session) : Session * QueryResult =
    match session.Tx |> Option.bind (fun tx -> Map.tryFind name tx.Savepoints |> Option.map (fun seed -> tx, seed)) with
    | Some(tx, (seq, catalog, eventCount, statementCount)) ->
        // Real MySQL never rolls back a burned AUTO_INCREMENT id — not even
        // a savepoint rollback (`bumpAutoIncrementsInto`'s doc covers the
        // full-ROLLBACK case, which is a separate code path from this one).
        // `catalog` is the savepoint's own stale copy of every `NextAutoId`,
        // so bump it back up to whatever this transaction ran ahead to
        // since, before wholesale-replacing the snapshot's catalog with it.
        let catalog = Storage.bumpAutoIncrements tx.Snapshot.Catalog catalog
        Storage.setCatalog tx.Snapshot catalog
        // Drop every event this transaction buffered after the savepoint —
        // otherwise a WAL replay would apply writes the savepoint rollback
        // just undid.
        tx.Snapshot.PendingEvents
        |> Option.iter (fun buffer -> if buffer.Count > eventCount then buffer.RemoveRange(eventCount, buffer.Count - eventCount))

        // Real MySQL also destroys every savepoint established *after* the
        // one rolled back to — the named savepoint itself survives (a
        // second `ROLLBACK TO` naming it again is legal).
        let survivors = tx.Savepoints |> Map.filter (fun _ (s, _, _, _) -> s <= seq)

        { session with
            Tx =
                Some
                    { tx with
                        Savepoints = survivors
                        Statements = tx.Statements |> List.truncate statementCount } },
        Affected 0UL
    | None -> session, savepointNotFound name

/// Drops the named savepoint and, matching real MySQL, every savepoint
/// established after it too.
let private releaseSavepoint (name: string) (session: Session) : Session * QueryResult =
    match session.Tx |> Option.bind (fun tx -> Map.tryFind name tx.Savepoints |> Option.map (fun seed -> tx, seed)) with
    | Some(tx, (seq, _, _, _)) ->
        let survivors = tx.Savepoints |> Map.filter (fun _ (s, _, _, _) -> s < seq)
        { session with Tx = Some { tx with Savepoints = survivors } }, Affected 0UL
    | None -> session, savepointNotFound name

let private handleSetAutocommit (value: string) (session: Session) : Session * QueryResult =
    let session = { session with Variables = Map.add "autocommit" (Some value) session.Variables }

    let session =
        if value = "0" then
            (if session.Tx.IsNone then beginTransaction session else session)
        else
            commitSession session

    session, Affected 0UL

/// Parses and executes anything that isn't one of the text-probe special
/// cases above. A parse failure that also looks like a `SELECT @@...`/
/// `SELECT @...` falls back to the `@`-probe path — tried only *after* the
/// real parser, so a query that merely contains the text `@` somewhere
/// (inside a string literal, e.g. `WHERE email = 'a@b.com'`) parses
/// normally instead of being hijacked into the probe path and rejected.
/// Anything else is a 1064 syntax error with SQLSTATE 42000 (the mapping
/// `errPayload` already has for that code).
/// Executes an already-parsed `Statement` — shared by COM_QUERY (parse then
/// execute) and COM_STMT_EXECUTE (bind placeholders then execute), so the
/// prepared path reuses this one execution body instead of splicing literals
/// back into SQL text and re-parsing.
let private executeParsed (session: Session) (stmt: Statement) : Session * QueryResult =
    // A SELECT into information_schema.processlist / the privilege views
    // scopes its rows to this session's user (unless it holds the revealing
    // privilege) — see `InformationSchema.currentViewer`.
    InformationSchema.currentViewer.Value <- Some(session.Store, session.User)
    let dbName = session.Database |> Option.defaultValue defaultDatabase

    let execute session =
        let store = Session.currentStore session

        // Privilege enforcement — the one gate every parsed statement goes
        // through (probes are exempt, see `Auth.requiredPrivileges`'s doc).
        match Auth.check store session.User (Auth.requiredPrivilegesInStore store dbName stmt) with
        | Error(code, msg) -> session, Err(code, msg)
        | Ok() ->

        // `Store.StrictMode` is store-wide, not per-session (see its doc
        // comment) — re-derive it from *this* session's own `sql_mode`
        // right before every statement, so another connection's `SET
        // SESSION sql_mode = ...` (which only ever touches its own
        // `Session.Variables`) can't leak into this one's coercion
        // behavior, and a transaction never runs on the stale StrictMode
        // its snapshot happened to be seeded with at BEGIN time.
        setStrictMode
            store
            (lookupVar session "sql_mode" |> Option.flatten |> Option.map isStrictSqlMode |> Option.defaultValue true)

        // Same reasoning for the executing account: `CREATE TRIGGER` stamps
        // it as the definer, and a trigger body is checked against that
        // definer rather than whoever's INSERT fired it.
        store.SessionUser <- session.User
        let registry = registryFor session

        let withRecursionDepth body =
            let sessionLimit =
                lookupVar session "cte_max_recursion_depth"
                |> Option.flatten
                |> Option.bind (fun value ->
                    match Int64.TryParse value with
                    | true, parsed -> Some parsed
                    | _ -> None)
                |> Option.defaultValue Limits.cteMaxRecursionDepth

            // A session may tighten the administrator's process-wide cap,
            // but cannot turn it off or raise it. A global zero remains the
            // explicit trusted-operator opt-out supported by MySQL.
            let limit =
                if Limits.cteMaxRecursionDepth = 0L then sessionLimit
                elif sessionLimit = 0L then Limits.cteMaxRecursionDepth
                else min sessionLimit Limits.cteMaxRecursionDepth

            Executor.withCteRecursionDepth limit body

        let withExecutionLimits body =
            let groupConcatLimit =
                lookupVar session "group_concat_max_len"
                |> Option.flatten
                |> Option.bind (fun value -> match Int32.TryParse value with | true, parsed when parsed >= 4 -> Some parsed | _ -> None)
                |> Option.defaultValue 1024

            Executor.withGroupConcatMaxLen groupConcatLimit (fun () -> withRecursionDepth body)

        // `SELECT`/`UNION` go through `Executor`'s type-preserving entry
        // points instead of the plain `execute` every other statement uses
        // — those are the only two statement kinds that reach the wire as
        // a `ResultSet`, and only they still have the typed `Value`s
        // (rather than already-rendered text) the metadata pass needs. See
        // `Session.LastResultColumnMetadata`'s doc for why this rides along
        // on `session` instead of widening this function's own return type.
        let lastInsertId, lastGeneratedId, result, columnMetadata =
            match stmt with
            | Select select ->
                let result, types = withExecutionLimits (fun () -> Executor.runTopLevelSelect store registry dbName select)
                session.LastInsertId, session.LastGeneratedId, result, types
            | Union(first, rest, orderBy, limit, offset) ->
                let result, types, _ =
                    withExecutionLimits (fun () -> Executor.runUnionStmt store registry dbName first rest orderBy limit offset)

                session.LastInsertId, session.LastGeneratedId, result, types
            | _ ->
                let foundRows = session.Capabilities &&& Fsdb.Protocol.ClientFoundRows <> 0u

                let (lastInsertId, lastGeneratedId), result =
                    withExecutionLimits (fun () ->
                        Executor.execute store registry dbName (session.LastInsertId, session.LastGeneratedId) foundRows stmt)

                lastInsertId, lastGeneratedId, result, []

        let session =
            { session with
                LastInsertId = lastInsertId
                LastGeneratedId = lastGeneratedId
                LastResultColumnMetadata = columnMetadata }

        let session =
            match session.Tx, result, stmt with
            | Some tx, Affected _, (Select _ | Union _ | Explain _) -> session
            | Some tx, Affected _, _ -> { session with Tx = Some { tx with Statements = tx.Statements @ [ stmt ] } }
            | _ -> session

        session, result

    match session.Tx with
    | Some _ -> execute (startTransactionStatement session)
    | None -> execute session

let private executeStatement (session: Session) (sql: string) (upper: string) : Session * QueryResult =
    match Parser.parse sql with
    | Result.Ok stmt -> executeParsed session stmt
    | Result.Error _ when upper.StartsWith "SELECT" && upper.Contains "@" ->
        { session with LastResultColumnMetadata = [] }, handleAtVarSelect session sql
    | Result.Error _ -> { session with LastResultColumnMetadata = [] }, syntaxError sql

/// Every statement form `dispatch` recognizes purely by text probe
/// (SET/USE/SHOW/transaction control) rather than `Parser.parse` — one DU
/// case per form, so `tryProbe` (the recognizer) and `runProbe` (what to do
/// once recognized) are two separate functions over the same closed set
/// instead of the same ordered if/elif chain written out twice by hand.
/// `prepareStatement` only needs `tryProbe`'s `.IsSome`, since PDO's default
/// `ATTR_EMULATE_PREPARES = false` means even a plain `SET
/// FOREIGN_KEY_CHECKS=0` (Laravel's `Schema::disableForeignKeyConstraints`)
/// goes through COM_STMT_PREPARE, and the grammar itself has no `SET`/`SHOW`
/// production to validate it against.
type private Probe =
    | SetAutocommit of value: string
    | SetTransactionIsolation of level: string
    | SetPassword of user: string option * password: string
    | SetVar
    | RollbackTo of savepoint: string
    | Begin
    | Commit
    | Rollback
    | Savepoint of name: string
    | Release of name: string
    | Use of dbName: string
    | ShowVariables of isGlobal: bool
    | ShowStatus
    | ShowEngines
    | ShowCharset
    | ShowProcesslist of full: bool
    | ShowTriggers of db: string option
    | ShowEvents of db: string option
    | ShowRoutineStatus
    | Kill of queryOnly: bool * id: int64
    | AlterKeysNoop of table: string
    | ShowWarnings
    | ShowErrors
    | ShowMessageCount of isError: bool
    | ShowDatabases
    | ShowTableStatus
    | ShowTables
    | ShowCreate of name: string
    | ShowCreateView of name: string
    | ShowColumns of full: bool * name: string * dbOverride: string option
    | Describe of name: string
    | ShowIndex of name: string * dbOverride: string option
    | ShowCollation
    | ShowGrants of user: string option
    | ShowPrivileges
    | FlushPrivileges

/// The one ordered list of text-probed forms — matching `Probe`'s cases
/// exactly (the compiler enforces `runProbe` covers every one of them), so
/// COM_QUERY (`dispatch`) and COM_STMT_PREPARE (`prepareStatement`) can
/// never disagree about which statements are text-probed vs. parsed the way
/// two independently-written predicates could drift.
let private tryProbe (sql: string) (upper: string) : Probe option =
    if setAutocommit.IsMatch sql then
        Some(SetAutocommit((setAutocommit.Match sql).Groups.[1].Value))
    elif setTransactionIsolation.IsMatch sql then
        Some(SetTransactionIsolation((setTransactionIsolation.Match sql).Groups.[1].Value))
    elif setPasswordRe.IsMatch sql then
        let m = setPasswordRe.Match sql
        Some(SetPassword((if m.Groups.[1].Success then Some m.Groups.[1].Value else None), m.Groups.[2].Value))
    elif upper.StartsWith "SET " then
        Some SetVar
    elif rollbackToSavepointStmt.IsMatch sql then
        Some(RollbackTo((rollbackToSavepointStmt.Match sql).Groups.[2].Value))
    elif beginTx.IsMatch upper then
        Some Begin
    elif commitTx.IsMatch upper then
        Some Commit
    elif rollbackTx.IsMatch upper then
        Some Rollback
    elif savepointStmt.IsMatch sql then
        Some(Savepoint((savepointStmt.Match sql).Groups.[1].Value))
    elif releaseSavepointStmt.IsMatch sql then
        Some(Release((releaseSavepointStmt.Match sql).Groups.[1].Value))
    elif upper.StartsWith "USE " then
        Some(Use(sql.Substring(4).Trim().Trim('`')))
    elif showVariablesRe.IsMatch sql then
        let scope = (showVariablesRe.Match sql).Groups.[1].Value
        Some(ShowVariables(scope.Trim().ToUpperInvariant() = "GLOBAL"))
    elif showStatusRe.IsMatch sql then
        Some ShowStatus
    elif showEnginesRe.IsMatch sql then
        Some ShowEngines
    elif showCharsetRe.IsMatch sql then
        Some ShowCharset
    elif showPrivilegesRe.IsMatch sql then
        Some ShowPrivileges
    elif showGrantsRe.IsMatch sql then
        let m = showGrantsRe.Match sql
        Some(ShowGrants(if m.Groups.[1].Success then Some m.Groups.[1].Value else None))
    elif flushPrivilegesRe.IsMatch sql then
        Some FlushPrivileges
    elif showProcesslistRe.IsMatch sql then
        Some(ShowProcesslist((showProcesslistRe.Match sql).Groups.[1].Success))
    elif showTriggersRe.IsMatch sql then
        let m = showTriggersRe.Match sql
        Some(ShowTriggers(if m.Groups.[1].Success then Some(stripBackticks m.Groups.[1].Value) else None))
    elif showEventsRe.IsMatch sql then
        let m = showEventsRe.Match sql
        Some(ShowEvents(if m.Groups.[1].Success then Some(stripBackticks m.Groups.[1].Value) else None))
    elif showRoutineStatusRe.IsMatch sql then
        Some ShowRoutineStatus
    elif killRe.IsMatch sql then
        let m = killRe.Match sql
        Some(Kill(m.Groups.[1].Value.ToUpperInvariant() = "QUERY", int64 m.Groups.[2].Value))
    elif alterKeysRe.IsMatch sql then
        Some(AlterKeysNoop(stripBackticks (alterKeysRe.Match sql).Groups.[1].Value))
    elif showCountWarningsRe.IsMatch sql then
        Some(ShowMessageCount false)
    elif showCountErrorsRe.IsMatch sql then
        Some(ShowMessageCount true)
    elif showWarningsRe.IsMatch sql then
        Some ShowWarnings
    elif showErrorsRe.IsMatch sql then
        Some ShowErrors
    elif upper.StartsWith "SHOW DATABASES" then
        Some ShowDatabases
    elif upper.StartsWith "SHOW TABLE STATUS" then
        Some ShowTableStatus
    elif upper.StartsWith "SHOW COLLATION" then
        Some ShowCollation
    elif upper.StartsWith "SHOW TABLES" || upper.StartsWith "SHOW FULL TABLES" then
        Some ShowTables
    elif showCreateViewRe.IsMatch sql then
        Some(ShowCreateView((showCreateViewRe.Match sql).Groups.[1].Value))
    elif showCreateTableRe.IsMatch sql then
        Some(ShowCreate((showCreateTableRe.Match sql).Groups.[1].Value))
    elif showColumnsRe.IsMatch sql then
        let m = showColumnsRe.Match sql
        let dbOverride = if m.Groups.[4].Success then Some m.Groups.[4].Value else None
        Some(ShowColumns(m.Groups.[1].Success, m.Groups.[2].Value, dbOverride))
    elif describeRe.IsMatch sql then
        Some(Describe((describeRe.Match sql).Groups.[1].Value))
    elif showIndexRe.IsMatch sql then
        let m = showIndexRe.Match sql
        let dbOverride = if m.Groups.[3].Success then Some m.Groups.[3].Value else None
        Some(ShowIndex(m.Groups.[1].Value, dbOverride))
    else
        None

/// What each `Probe` case actually does, given the session and the
/// (trimmed) SQL text `tryProbe` matched against — a couple of cases
/// (`SetVar`'s comma/quoting, the `SHOW ...`s' own `LIKE` suffix) still
/// re-derive a little from `sql` themselves rather than `Probe` carrying
/// every last capture group, since that parsing already lives in
/// `handleSet`/`handleShowVariables`/etc. and shouldn't move twice.
let private runProbe (session: Session) (sql: string) (probe: Probe) : Session * QueryResult =
    let session =
        match probe with
        | ShowDatabases
        | ShowTableStatus
        | ShowTables
        | ShowCreate _
        | ShowCreateView _
        | ShowColumns _
        | Describe _
        | ShowIndex _ -> startTransactionStatement session
        | _ -> session

    match probe with
    | SetAutocommit value -> handleSetAutocommit value session
    | SetTransactionIsolation level ->
        match normalizeIsolationLevel level with
        | "SERIALIZABLE" ->
            // ponytail: fsdb's transaction model is one fixed snapshot per
            // transaction (~REPEATABLE READ) with optimistic row conflicts,
            // not the range-locking SERIALIZABLE actually needs — a
            // client that asked for stronger isolation than fsdb gives must
            // see a clear error, not a silent downgrade. Upgrade path: real
            // predicate/range locking, if a client ever genuinely needs it.
            session, Err(1235, "This version of MySQL doesn't yet support 'SERIALIZABLE transaction isolation'")
        | normalized -> { session with Variables = Map.add "transaction_isolation" (Some normalized) session.Variables }, Affected 0UL
    | SetPassword(userOpt, password) ->
        // `FOR 'name'@'host'` — only the name matters (accounts are matched
        // by name, see `Auth`); no FOR clause means the session's own user.
        let name = userOpt |> Option.map userNameOf |> Option.defaultValue session.User
        let store = Session.currentStore session

        // MySQL's rule: changing your own password is free, anyone else's
        // needs CREATE USER — probes bypass `executeParsed`'s enforcement
        // gate, so this one carries its own check.
        let required = if name = session.User then [] else [ "CREATE USER", Auth.Global ]

        match Auth.check store session.User required |> Result.bind (fun () -> Auth.setPassword store name "%" password) with
        | Ok() -> session, Affected 0UL
        | Error(code, msg) -> session, Err(code, msg)
    | SetVar -> handleSet session sql
    | RollbackTo name -> rollbackToSavepoint name session
    | Begin -> beginTransaction session, Affected 0UL
    | Commit -> commitSession session, Affected 0UL
    | Rollback -> rollbackSession session, Affected 0UL
    | Savepoint name -> savepoint name session
    | Release name -> releaseSavepoint name session
    | Use dbName ->
        if Storage.databaseExists (Session.currentStore session) dbName then
            { session with Database = Some dbName }, Affected 0UL
        else
            let code, msg = Storage.toMySqlError (Storage.NoSuchDatabase dbName)
            session, Err(code, msg)
    | ShowVariables isGlobal -> session, handleShowVariables session isGlobal sql
    | ShowStatus -> session, InformationSchema.showStatus (statusFilter sql) |> showResult
    | ShowEngines -> session, InformationSchema.showEngines () |> showResult
    | ShowCharset -> session, InformationSchema.showCharacterSet (likeSuffix sql) |> showResult
    | ShowPrivileges -> session, InformationSchema.showPrivileges () |> showResult
    | ShowProcesslist full ->
        let result =
            InformationSchema.withViewer session.Store session.User (fun () -> InformationSchema.showProcesslist full |> showResult)

        session, result
    | ShowTriggers db ->
        // `FROM db` when given, else the session's current database — same
        // resolution MySQL applies.
        let dbName = db |> Option.defaultValue (session.Database |> Option.defaultValue defaultDatabase)
        session, InformationSchema.showTriggers (Session.currentStore session).Catalog dbName |> showResult
    | ShowEvents db -> session, InformationSchema.showEvents (Session.currentStore session).Catalog db |> showResult
    | ShowRoutineStatus -> session, InformationSchema.showRoutineStatus () |> showResult
    | Kill(queryOnly, id) ->
        let canSeeAll = Auth.hasGlobalPriv session.Store session.User "PROCESS"
        let canKillAll = Auth.hasGlobalPriv session.Store session.User "SUPER"

        match InformationSchema.tryFindProcess id with
        // A caller without PROCESS can't see another user's connection, so
        // it can neither name nor kill it — MySQL reports the id as unknown.
        | Some target when target.User <> session.User && not canSeeAll ->
            session, Err(1094, sprintf "Unknown thread id: %d" id)
        | None -> session, Err(1094, sprintf "Unknown thread id: %d" id)
        | Some target when target.User <> session.User && not canKillAll ->
            session, Err(1095, sprintf "You are not owner of thread %d" id)
        | Some target ->
            if queryOnly then
                target.CancelQuery |> Option.iter (fun cancel -> cancel ())
            else
                target.CloseConnection |> Option.iter (fun close -> close ())

            session, Affected 0UL
    | AlterKeysNoop name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name

        match InformationSchema.findTable (Session.currentStore session).Catalog dbName table with
        | Ok _ -> session, Affected 0UL
        | Error(code, msg) -> session, Err(code, msg)
    | ShowWarnings
    | ShowErrors -> session, ResultSet([ "Level"; "Code"; "Message" ], [])
    | ShowMessageCount isError ->
        let col = if isError then "@@session.error_count" else "@@session.warning_count"
        session, ResultSet([ col ], [ [ Some "0" ] ])
    | ShowDatabases -> session, handleShowDatabases session sql
    | ShowTableStatus -> session, handleShowTableStatus session sql
    | ShowCollation -> session, InformationSchema.showCollation (likeSuffix sql) |> showResult
    | ShowTables -> session, handleShowTables session sql
    | ShowCreate name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        session, InformationSchema.showCreateTable (catalogWithOverlay session dbName table) dbName table |> showResult
    | ShowCreateView name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, view = splitQualified sessionDb name
        session, InformationSchema.showCreateView (Session.currentStore session).Catalog dbName view |> showResult
    | ShowColumns(full, name, dbOverride) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        let dbName = dbOverride |> Option.map stripBackticks |> Option.defaultValue dbName
        session, InformationSchema.showColumns (catalogWithOverlay session dbName table) full dbName table (likeSuffix sql) |> showResult
    | Describe name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        session, InformationSchema.showColumns (catalogWithOverlay session dbName table) false dbName table None |> showResult
    | ShowIndex(name, dbOverride) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        let dbName = dbOverride |> Option.map stripBackticks |> Option.defaultValue dbName
        session, InformationSchema.showIndex (catalogWithOverlay session dbName table) dbName table |> showResult
    | ShowGrants userOpt ->
        // `FOR 'name'@'host'` — the name is what matters (accounts match by
        // name, see `Auth`); no FOR, or FOR CURRENT_USER[()], means the
        // session's own user.
        let name =
            match userOpt with
            | None -> session.User
            | Some u when u.Trim().ToUpperInvariant().StartsWith "CURRENT_USER" -> session.User
            | Some u -> userNameOf u

        if name <> session.User && not (Auth.hasGlobalPriv session.Store session.User "SELECT") then
            // Seeing another account's grants reads `mysql.user`; without
            // SELECT there, MySQL denies with 1142 on that table.
            session, Err(1142, sprintf "SELECT command denied to user '%s'@'localhost' for table 'user'" session.User)
        else
            match Auth.renderGrants (Session.currentStore session) name with
            | Ok(header, lines) -> session, ResultSet([ header ], lines |> List.map (fun l -> [ Some l ]))
            | Error(code, msg) -> session, Err(code, msg)
    | FlushPrivileges -> session, Affected 0UL
let rec mapPlaceholders (replace: int -> Expr) (stmt: Statement) : Statement =
    let rec mapExpr (e: Expr) : Expr =
        match e with
        | Placeholder i -> replace i
        | MatchAgainst(cols, q, mode) -> MatchAgainst(cols, mapExpr q, mode)
        | Lit _
        | Col _
        | QualifiedCol _
        | Star _ -> e
        | BinOp(op, a, b) -> BinOp(op, mapExpr a, mapExpr b)
        | Not x -> Not(mapExpr x)
        | IsNull x -> IsNull(mapExpr x)
        | IsNotNull x -> IsNotNull(mapExpr x)
        | IsTrue x -> IsTrue(mapExpr x)
        | IsFalse x -> IsFalse(mapExpr x)
        | Like(x, p, cs, esc) -> Like(mapExpr x, mapExpr p, cs, esc)
        | Regexp(x, p) -> Regexp(mapExpr x, mapExpr p)
        | In(x, xs) -> In(mapExpr x, List.map mapExpr xs)
        | InSubquery(x, s) -> InSubquery(mapExpr x, mapSelect s)
        | Between(x, lo, hi) -> Between(mapExpr x, mapExpr lo, mapExpr hi)
        | FuncCall(name, args) -> FuncCall(name, List.map mapExpr args)
        | WindowOver(fn, over) -> WindowOver(mapWindowFn fn, mapOver over)
        | Distinct x -> Distinct(mapExpr x)
        | OrderBy(x, d) -> OrderBy(mapExpr x, d)
        | Cast(x, t) -> Cast(mapExpr x, t)
        | Collate(x, c) -> Collate(mapExpr x, c)
        | Case(subject, whens, elseBranch) ->
            Case(Option.map mapExpr subject, whens |> List.map (fun (c, r) -> mapExpr c, mapExpr r), Option.map mapExpr elseBranch)
        | Exists s -> Exists(mapSelect s)
        | Subquery s -> Subquery(mapSelect s)

    and mapOrderKey (x, d) = mapExpr x, d

    and mapWindowFn (fn: WindowFn) : WindowFn =
        match fn with
        | WinRowNumber
        | WinRank _
        | WinPercentRank
        | WinCumeDist -> fn
        | WinNTile n -> WinNTile(mapExpr n)
        | WinLagLead(lead, x, offset, deflt) ->
            WinLagLead(lead, mapExpr x, Option.map mapExpr offset, Option.map mapExpr deflt)
        | WinFirstValue x -> WinFirstValue(mapExpr x)
        | WinLastValue x -> WinLastValue(mapExpr x)
        | WinNthValue(x, n) -> WinNthValue(mapExpr x, mapExpr n)
        | WinAggregate(name, args) -> WinAggregate(name, List.map mapExpr args)

    and mapWindowSpec (spec: WindowSpec) : WindowSpec =
        let mapBound bound =
            match bound with
            | BoundPreceding e -> BoundPreceding(mapExpr e)
            | BoundFollowing e -> BoundFollowing(mapExpr e)
            | other -> other

        { PartitionBy = List.map mapExpr spec.PartitionBy
          OrderBy = List.map mapOrderKey spec.OrderBy
          Frame = spec.Frame |> Option.map (fun f -> { f with Start = mapBound f.Start; End = mapBound f.End }) }

    and mapOver (over: OverClause) : OverClause =
        match over with
        | OverSpec spec -> OverSpec(mapWindowSpec spec)
        | OverName _ -> over

    and mapFromItem (fi: FromItem) : FromItem =
        match fi with
        | FromTable _ -> fi
        | FromSubquery(sou, alias) -> FromSubquery(mapSelectOrUnion sou, alias)
        | FromJsonTable(source, path, cols, alias) -> FromJsonTable(mapExpr source, path, cols, alias)
        | FromLateral(sou, alias) -> FromLateral(mapSelectOrUnion sou, alias)

    and mapJoin (j: Join) : Join = { j with On = mapExpr j.On; Table = mapFromItem j.Table }

    and mapSelect (s: SelectStmt) : SelectStmt =
        { s with
            Projections = s.Projections |> List.map (fun (e, alias) -> mapExpr e, alias)
            From = Option.map mapFromItem s.From
            Joins = List.map mapJoin s.Joins
            Where = Option.map mapExpr s.Where
            GroupBy = List.map mapExpr s.GroupBy
            Windows = s.Windows |> List.map (fun (n, spec) -> n, mapWindowSpec spec)
            Ctes = s.Ctes |> List.map (fun cte -> { cte with Body = mapSelectOrUnion cte.Body })
            Having = Option.map mapExpr s.Having
            OrderBy = List.map mapOrderKey s.OrderBy
            Limit = Option.map mapExpr s.Limit
            Offset = Option.map mapExpr s.Offset }

    and mapSelectOrUnion (sou: SelectOrUnion) : SelectOrUnion =
        match sou with
        | PlainSelect s -> PlainSelect(mapSelect s)
        | UnionSelect(first, rest, orderBy, limit, offset) ->
            UnionSelect(
                mapSelect first,
                rest |> List.map (fun (b, s) -> b, mapSelect s),
                List.map mapOrderKey orderBy,
                Option.map mapExpr limit,
                Option.map mapExpr offset
            )

    let mapAssignment (a: Assignment) = { a with Value = mapExpr a.Value }

    match stmt with
    | Select s -> Select(mapSelect s)
    | Union(first, rest, orderBy, limit, offset) ->
        Union(
            mapSelect first,
            rest |> List.map (fun (b, s) -> b, mapSelect s),
            List.map mapOrderKey orderBy,
            Option.map mapExpr limit,
            Option.map mapExpr offset
        )
    | Insert(table, columns, rows, onDup, ignore) ->
        Insert(table, columns, rows |> List.map (List.map mapExpr), onDup |> List.map (fun (c, e) -> c, mapExpr e), ignore)
    | InsertSelect(table, columns, select, onDup, ignore) ->
        InsertSelect(table, columns, mapSelect select, onDup |> List.map (fun (c, e) -> c, mapExpr e), ignore)
    | Replace(table, columns, rows) -> Replace(table, columns, rows |> List.map (List.map mapExpr))
    | ReplaceSelect(table, columns, select) -> ReplaceSelect(table, columns, mapSelect select)
    | ReplaceSet(table, assignments) -> ReplaceSet(table, assignments |> List.map (fun (name, value) -> name, mapExpr value))
    | Update u ->
        Update
            { u with
                Assignments = List.map mapAssignment u.Assignments
                Where = Option.map mapExpr u.Where
                OrderBy = List.map mapOrderKey u.OrderBy
                Joins = List.map mapJoin u.Joins
                Limit = Option.map mapExpr u.Limit }
    | Delete d ->
        Delete
            { d with
                Where = Option.map mapExpr d.Where
                OrderBy = List.map mapOrderKey d.OrderBy
                Joins = List.map mapJoin d.Joins
                Limit = Option.map mapExpr d.Limit }
    | Explain s -> Explain(mapPlaceholders replace s)
    | _ -> stmt

/// Binds parameter `Value`s into a parsed `Statement`, replacing every
/// `Placeholder i` with `Lit values.[i]`.
let bindPlaceholders (stmt: Statement) (values: Value list) : Statement =
    mapPlaceholders (fun i -> Lit(List.item i values)) stmt

/// Renumbers surviving `Placeholder` nodes densely in traversal (= source)
/// order, returning the statement and the true parameter count. FParsec's
/// `attempt` rewinds the input but not the parse-time placeholder counter,
/// so a backtracked atom (`CONVERT(?, x)`) can leave a gap/overcount in the
/// AST indices; renumbering here is the source of truth `bindPlaceholders`
/// then binds against, so COM_STMT_PREPARE_OK advertises the right count and
/// each value lands on the right `?`.
let renumberPlaceholders (stmt: Statement) : Statement * int =
    let next = ref 0

    let renumbered =
        mapPlaceholders
            (fun _ ->
                let n = next.Value
                next.Value <- n + 1
                Placeholder n)
            stmt

    renumbered, next.Value



/// Parses and validates SQL for COM_STMT_PREPARE without executing it: a
/// parse failure is the same 1064 (code, message) pair a COM_QUERY syntax
/// error gets, so `Server` doesn't need its own copy of that formatting.
/// `Ok` carries the parsed `Statement` (with `Placeholder` nodes where the
/// `?`s were) plus the placeholder count for COM_STMT_PREPARE_OK.
///
/// `None` for the text-probed forms the grammar doesn't produce
/// (SET/SHOW/transaction control) — those still execute textually through
/// `Sql`, so their placeholder count is the plain `placeholderPositions`
/// count rather than a parser one.
let prepareStatement (sql: string) : Result<Statement option * int, int * string> =
    let trimmed = sql.Trim().TrimEnd(';').Trim()
    let upper = trimmed.ToUpperInvariant()

    if (tryProbe trimmed upper).IsSome then
        Result.Ok(None, placeholderPositions sql |> List.length)
    else
        match Parser.parse sql with
        | Result.Ok stmt ->
            let renumbered, count = renumberPlaceholders stmt

            // A `?` the AST binder can't reach — e.g. in a generated-column
            // DDL expression, which `bindPlaceholders` leaves untouched —
            // shows up in the source text but not the renumbered count. It
            // would survive unbound into execution (and `FailFast` a
            // --data-dir server via `Persistence.encodeExpr`), so reject the
            // prepare as a 1064, same as the COM_QUERY guard in `dispatch`.
            if (placeholderPositions sql |> List.length) <> count then
                match syntaxError sql with
                | Err(code, msg) -> Result.Error(code, msg)
                | _ -> Result.Error(1064, "syntax error")
            else
                Result.Ok(Some renumbered, count)
        | Result.Error _ ->
            match syntaxError sql with
            | Err(code, msg) -> Result.Error(code, msg)
            | _ -> Result.Error(1064, "syntax error")

/// Binds prepared-statement parameter `Value`s into a parsed `Statement`,
/// replacing every `Placeholder i` with `Lit values.[i]`. Total — after this
/// no `Placeholder` survives and the statement executes through the ordinary
/// path. This walk is the one place that must touch every expression position
/// in the AST, so a placeholder in any legal spot gets bound; DDL is left as
/// the `_` pass-through since MySQL never accepts a `?` there.
let private dispatch (session: Session) (rawSql: string) : Session * QueryResult =
    let sql = (Parser.stripVersionComments rawSql).Trim().TrimEnd(';').Trim()

    // A mysqldump preamble/postamble is a run of `/*!NNNNN ... */;` lines;
    // once the version comment above strips down to nothing (or this was a
    // plain `/* ... */`/`-- ...` comment to begin with), what's left is a
    // no-op, same as real MySQL's `Query OK, 0 rows affected` for it —
    // not a syntax error.
    if Parser.isBlank sql then
        session, Affected 0UL
    elif not (placeholderPositions sql |> List.isEmpty) then
        // A `?` outside a string/comment is a bind parameter, only legal via
        // COM_STMT_PREPARE — over COM_QUERY it's a 1064 in MySQL. Rejecting
        // here also stops a `?` in a spot the prepared binder never reaches
        // (a generated-column DDL expression) from surviving unbound into
        // Storage/Persistence, where it would `FailFast` a --data-dir server.
        session, syntaxError sql
    else
        let upper = sql.ToUpperInvariant()

        match tryProbe sql upper with
        | Some probe ->
            // Every probe-handled form (SHOW/SET/session-variable SELECT/...)
            // is its own small synthetic `ResultSet` of plain strings — none of
            // them go through `executeStatement`'s typed path, so clear
            // whatever `LastResultColumnMetadata` a previous statement on this
            // session left behind rather than risk it surviving (via `Server`'s
            // VAR_STRING-length-mismatch fallback, a same-column-count
            // coincidence is all it'd take) onto an unrelated resultset.
            let session, result = runProbe session sql probe
            { session with LastResultColumnMetadata = [] }, result
        | None -> executeStatement session sql upper

/// No SQL engine failure should ever escape as a raw .NET exception — the
/// only two paths into `dispatch` (the parser, well guarded, and
/// `Storage.coerceValue`'s numeric casts, which are not) both funnel into
/// `Executor`, and `Server`'s connection loop only catches
/// `PacketTooLargeException`, so anything else here would otherwise unwind
/// straight to the socket read loop and silently drop the connection with
/// no ERR packet. Verified reachable: `INSERT INTO t VALUES (1e300)` into a
/// DECIMAL column throws `OverflowException` from `decimal d`.
///
/// `Storage.LockWaitTimeout` covers both an explicit gate timeout and an
/// optimistic commit conflict; both are retryable 1205 errors.
let handle (session: Session) (rawSql: string) : Session * QueryResult =
    try
        match dispatch session rawSql with
        | _, Err(code, msg) as result ->
            Log.diagnostic "fsdb: ERR %d %s -- query: %s" code msg (Log.redactSql rawSql)
            result
        | result -> result
    with
    | Storage.LockWaitTimeout dbName ->
        Log.diagnostic "fsdb: ERR 1205 lock wait timeout on database %s -- query: %s" dbName (Log.redactSql rawSql)
        session, Err(1205, "Lock wait timeout exceeded; try restarting transaction")
    // A killed client mid-query (`Storage.queryCancellation`, armed by
    // `Server.withCancellationWatch`) — not an internal error to report
    // back to a client that's already gone, so this re-raises rather than
    // folding into the generic `Err(1105, ...)` below: `Server`'s command
    // loop catches it, skips the now-pointless reply, and logs the one
    // clean line itself.
    | :? OperationCanceledException ->
        reraise ()
    // Arithmetic that left the `BIGINT UNSIGNED` domain (`Value.narrowUnsigned`).
    // MySQL fails the statement but keeps the transaction, and so does this:
    // `executeStatement` already released this statement's gate leases on the
    // way out, and no rows were written (the throw happens during expression
    // evaluation), so there is nothing to abort.
    //
    // ponytail: MySQL's message names the offending expression
    // (`... out of range in '(cast(-(1) as unsigned) + 1)'`) and this doesn't —
    // there's no AST printer to render it. Add one if a client parses the text.
    | Value.UnsignedOutOfRange ->
        Log.diagnostic "fsdb: ERR 1690 unsigned out of range -- query: %s" (Log.redactSql rawSql)
        session, Err(1690, "BIGINT UNSIGNED value is out of range")
    // An extension function's *chosen* error code (`Functions.SqlError`) —
    // delivered verbatim instead of the generic 1105, but with the exact
    // same transaction-abort semantics as any other throw: a failing
    // effectful function mid-transaction must not leave a half-applied
    // snapshot reachable by a later COMMIT.
    | Functions.SqlError(code, msg) ->
        Log.diagnostic "fsdb: ERR %d %s -- query: %s" code msg (Log.redactSql rawSql)
        { session with Tx = None }, Err(code, msg)
    | ex ->
        Log.diagnostic "fsdb: EXN %s -- query: %s" ex.Message (Log.redactSql rawSql)
        { session with Tx = None }, Err(1105, "Internal error") // ER_UNKNOWN_ERROR

/// Executes a prepared statement with its bound parameter values. Parser-
/// produced statements bind the values into the parsed AST and run it
/// directly (no re-parse, no SQL escaping); the text-probed forms
/// (SET/SHOW/transaction control, which have no AST) still substitute into
/// `Sql` and go through the ordinary text path.
let executePrepared (session: Session) (stmt: PreparedStmt) (values: Value list) : Session * QueryResult =
    // The AST path calls `executeParsed` directly, which — unlike `handle` —
    // doesn't convert a stray .NET exception into an `Err`, so a bound value
    // that overflows a temporal/numeric op (or any bug) would otherwise drop
    // the connection with no ERR packet. Give it the same 1105 safety net
    // `handle` gives the text path.
    try
        match stmt.Ast with
        | Some ast -> executeParsed session (bindPlaceholders ast values)
        | None -> handle session (substitutePlaceholders stmt.Sql (values |> List.map valueToSqlLiteral))
    with
    | PlaceholderCountMismatch(expected, got) ->
        session, Err(1210, sprintf "Incorrect arguments to EXECUTE (expected %d, got %d)" expected got)
    | Value.UnsignedOutOfRange -> session, Err(1690, "BIGINT UNSIGNED value is out of range")
    | :? OperationCanceledException -> reraise ()
    | ex ->
        Log.diagnostic "fsdb: EXN %s -- prepared statement" ex.Message
        session, Err(1105, "Internal error")
