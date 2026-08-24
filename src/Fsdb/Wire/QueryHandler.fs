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
    | VBit(width, value) -> "X'" + Convert.ToHexString(bitBytes width value) + "'"
    | VDouble d -> d.ToString(Globalization.CultureInfo.InvariantCulture)
    | VDecimal d -> d.ToString(Globalization.CultureInfo.InvariantCulture)
    | VBytes bytes -> "X'" + Convert.ToHexString(bytes) + "'"
    | VDate _
    | VDateTime _
    | VTime _
    | VZeroDate _
    | VZeroDateTime _
    | VString _
    | VJson _ -> "'" + escapeSqlString (v |> toText |> Option.defaultValue "") + "'"
    | VGeometry geometry -> "ST_GeomFromWKB(X'" + Convert.ToHexString(geometryToWkb geometry) + "', " + string geometry.Srid + ")"

/// Whether a scope prefix means GLOBAL. The SET grammar includes the `@@`
/// sigil while expression parsing keeps it separate, so GLOBAL can begin at
/// either position.
let private isGlobalScope (scope: string) : bool =
    scope.IndexOf("GLOBAL", StringComparison.OrdinalIgnoreCase) >= 0

/// The raw (unflattened) lookup behind `resolveAtRef` — kept separate so
/// `handleAtVarSelect` can still tell "unknown system variable" (outer
/// `None`) apart from "known, currently NULL" when deciding whether to
/// raise 1193, the one case `resolveAtRef`'s flattened `string option`
/// can't distinguish.
let private lookupAtRef (session: Session) (sigil: string) (scope: string) (name: string) : string option option =
    let name = name.ToLowerInvariant()

    if sigil = "@@" && not (isGlobalScope scope) && name = "warning_count" then
        Some(Some(string session.Diagnostics.Length))
    elif sigil = "@@" && not (isGlobalScope scope) && name = "error_count" then
        Some(Some(string (session.Diagnostics |> List.filter (fun condition -> condition.Level = Diagnostics.Error) |> List.length)))
    elif sigil = "@@" then
        if isGlobalScope scope then
            Session.tryGlobalVariable session.Store name
        else
            lookupVar session name
    else
        Some(session.UserVariables |> Map.tryFind name |> Option.bind toText)

/// Resolves one `@@name`/`@name` reference to its current value. A system
/// variable (`@@`) is looked up in `Session.Variables` (or the store's
/// GLOBAL overrides for an explicit `@@GLOBAL.` qualifier); a user variable
/// (`@`) in `Session.UserVariables`, where "never `SET`" and "`SET` to
/// NULL" both collapse to `None` via `Option.flatten` — real MySQL reads
/// both back as NULL, and callers here don't need to tell them apart.
let private resolveAtRef (session: Session) (sigil: string) (scope: string) (name: string) : string option =
    lookupAtRef session sigil scope name |> Option.flatten

let private maxUserVariables = 65536

let private expressionVariablesFor (session: Session) (userVariables: Map<string, Value>) : Executor.VariableContext =
    { UserVariables = ref userVariables
      ReadSystemVariable =
        fun scope name ->
            match lookupAtRef session "@@" scope name with
            | Some value -> Ok value
            | None -> Error(1193, sprintf "Unknown system variable '%s'" name)
      MaxUserVariables = maxUserVariables }

let private expressionVariables (session: Session) = expressionVariablesFor session session.UserVariables

let private accountOf (session: Session) = Auth.account session.User session.AccountHost

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
    let blockEncryptionMode = lookupVar session "block_encryption_mode" |> Option.flatten |> Option.defaultValue "aes-128-ecb"
    let loginUser = if session.LoginUser = "" then session.User else session.LoginUser

    registry
    |> Functions.registerScalar "AES_ENCRYPT" (Functions.aesEncrypt blockEncryptionMode)
    |> Functions.registerScalar "AES_DECRYPT" (Functions.aesDecrypt blockEncryptionMode)
    |> Functions.registerScalar "DATABASE" database
    |> Functions.registerScalar "SCHEMA" database
    |> Functions.registerScalar "LAST_INSERT_ID" (fun _ -> VInt session.LastGeneratedId)
    |> Functions.registerScalar "ROW_COUNT" (fun _ -> VInt session.LastRowCount)
    |> Functions.registerScalar "FOUND_ROWS" (fun _ -> VUInt session.FoundRows)
    |> Functions.registerScalar
        "VERSION"
        (fun _ -> lookupVar session "version" |> Option.flatten |> Option.map VString |> Option.defaultValue VNull)
    |> Functions.registerScalar "CONNECTION_ID" (fun _ -> VInt(int64 session.ConnectionId))
    |> Functions.registerScalar "CURRENT_USER" (fun _ -> VString(session.User + "@" + session.AccountHost))
    |> Functions.registerScalar "USER" (fun _ -> VString(loginUser + "@" + session.ClientHost))
    |> Functions.registerScalar "SESSION_USER" (fun _ -> VString(loginUser + "@" + session.ClientHost))

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
let private showEngineInnodbStatusRe = Regex(@"^SHOW\s+ENGINE\s+INNODB\s+STATUS\s*$", RegexOptions.IgnoreCase)
let private showPluginsRe = Regex(@"^SHOW\s+PLUGINS\s*$", RegexOptions.IgnoreCase)
let private showBinaryLogsRe = Regex(@"^SHOW\s+(?:BINARY|MASTER)\s+LOGS\s*$", RegexOptions.IgnoreCase)
let private showBinaryLogStatusRe = Regex(@"^SHOW\s+BINARY\s+LOG\s+STATUS\s*$", RegexOptions.IgnoreCase)
let private showReplicaStatusRe =
    Regex(@"^SHOW\s+REPLICA\s+STATUS(?:\s+FOR\s+CHANNEL\s+'[^']*')?\s*$", RegexOptions.IgnoreCase)
let private maintenanceTableRe = Regex(@"^(ANALYZE|CHECK|OPTIMIZE|REPAIR)\s+TABLE\s+(.+?)\s*$", RegexOptions.IgnoreCase)
let private showOpenTablesRe = Regex(@"^SHOW\s+OPEN\s+TABLES(?:\s+(?:FROM|IN)\s+(\S+))?(?:\s+LIKE\s+'([^']*)')?\s*$", RegexOptions.IgnoreCase)
let private showCreateDatabaseRe =
    Regex(@"^SHOW\s+CREATE\s+(?:DATABASE|SCHEMA)(?:\s+IF\s+NOT\s+EXISTS)?\s+(\S+)\s*$", RegexOptions.IgnoreCase)
let private showCharsetRe = Regex(@"^SHOW\s+(?:CHARACTER\s+SET|CHARSET)(\s|$)", RegexOptions.IgnoreCase)
let private showPrivilegesRe = Regex(@"^SHOW\s+PRIVILEGES\s*$", RegexOptions.IgnoreCase)
let private showProcesslistRe = Regex(@"^SHOW\s+(FULL\s+)?PROCESSLIST\s*$", RegexOptions.IgnoreCase)
let private showCreateUserRe = Regex(@"^SHOW\s+CREATE\s+USER\s+(.+?)\s*;?$", RegexOptions.IgnoreCase)
let private showCreateProgramRe =
    Regex(@"^SHOW\s+CREATE\s+(PROCEDURE|FUNCTION|EVENT)\s+(\S+)\s*;?$", RegexOptions.IgnoreCase)

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

let private tableAccessDenied (session: Session) (table: string) =
    Err(1142, sprintf "SELECT command denied to user '%s'@'localhost' for table '%s'" session.User table)

let private showTableResult (session: Session) (dbName: string) (table: string) (result: InformationSchema.ShowResult) =
    let canSee = Auth.canSeeTableForAccount (Session.currentStore session) (accountOf session) dbName table

    match result with
    | Error _ -> showResult result
    | Ok _ when canSee -> showResult result
    | Ok _ -> tableAccessDenied session table

let private visibleTableRows (session: Session) (dbName: string) (rows: string option list list) =
    let canSee = Auth.canSeeTableForAccount (Session.currentStore session) (accountOf session) dbName

    rows |> List.filter (function Some table :: _ -> canSee table | _ -> false)

let private inspectAccount (session: Session) (wanted: Auth.Account) (render: unit -> QueryResult) =
    let viewer = accountOf session

    if Auth.sameAccount wanted viewer || Auth.hasGlobalPrivForAccount session.Store viewer "SELECT" then
        session, render ()
    else
        session, Err(1142, sprintf "SELECT command denied to user '%s'@'localhost' for table 'user'" session.User)

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

    let result =
        InformationSchema.showTables store.Catalog fsdbTables dbName full (likeSuffix sql)
        |> Result.map (fun (columns, rows) -> columns, visibleTableRows session dbName rows)

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
    let visible = rows |> List.filter (function | [ Some db ] -> Auth.canSeeDatabaseForAccount store (accountOf session) db | _ -> false)
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

    InformationSchema.showTableStatus (Session.currentStore session).Catalog dbName (likeSuffix sql)
    |> Result.map (fun (columns, rows) -> columns, visibleTableRows session dbName rows)
    |> showResult

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

/// Best-effort name extraction for the "this looks like an assignment but
/// neither `setVar` nor the user-variable parser matched it" error below.
let private setVarNameForError = Regex(@"^(?:SESSION\s+|GLOBAL\s+)?(\S+?)\s*=", RegexOptions.IgnoreCase)

let private quotedSetLiteral = Regex("^(['\"])(.*)\\1$", RegexOptions.Singleline)
let private bareSetIdentifier = Regex("^\\w+$")

let private literalSetRhs (rhs: string) : Value option =
    let rhs = rhs.Trim()
    let quoted = quotedSetLiteral.Match rhs

    if quoted.Success then
        // MySQL leaves the final escaped quote's preceding slash in this
        // SET-specific spelling; Parser's generic string grammar cannot
        // preserve it.
        Some(VString quoted.Groups.[2].Value)
    elif String.Equals(rhs, "NULL", StringComparison.OrdinalIgnoreCase) then
        Some VNull
    else
        None

/// Evaluates a `SET` user-variable right-hand side through the ordinary
/// expression grammar. A private variable map preserves SET's all-or-
/// nothing application when a later fragment fails.
let private resolveUserSetRhs
    (session: Session)
    (userVariables: Map<string, Value>)
    (sql: string)
    (rhs: string)
    : Result<Value * Map<string, Value>, QueryResult> =
    match literalSetRhs rhs with
    | Some value -> Ok(value, userVariables)
    | None ->
        match Parser.parseExpression rhs with
        | Error _ -> Error(syntaxError sql)
        | Ok expression ->
            let variables = expressionVariablesFor session userVariables
            let store = Session.currentStore session
            let dbName = session.Database |> Option.defaultValue defaultDatabase

            Executor.withVariableContext variables (fun () ->
                Executor.evaluateExpression store (registryFor session) dbName expression
                |> Result.map (fun value -> value, variables.UserVariables.Value))

let private resolveSystemSetRhs
    (session: Session)
    (userVariables: Map<string, Value>)
    (sql: string)
    (rhs: string)
    : Result<Value * Map<string, Value>, QueryResult> =
    match literalSetRhs rhs with
    | Some value -> Ok(value, userVariables)
    | None when bareSetIdentifier.IsMatch(rhs.Trim()) -> Ok(VString(rhs.Trim()), userVariables)
    | None ->
        resolveUserSetRhs session userVariables sql rhs

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

let private hasSqlMode (name: string) (value: string) =
    value.Split(',')
    |> Array.exists (fun mode -> String.Equals(mode.Trim(), name, StringComparison.OrdinalIgnoreCase))

let private applySqlMode (store: Store) (value: string) =
    setStrictMode store (isStrictSqlMode value)
    setZeroDateModes store (hasSqlMode "NO_ZERO_DATE" value) (hasSqlMode "NO_ZERO_IN_DATE" value)

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

    let mutable index = 0

    while index < body.Length do
        let c = body.[index]

        match quoteChar with
        | Some q when escaped ->
            escaped <- false
            current.Append c |> ignore
            index <- index + 1
        | Some q when q <> '`' && c = '\\' ->
            escaped <- true
            current.Append c |> ignore
            index <- index + 1
        | Some q when c = q && index + 1 < body.Length && body.[index + 1] = q ->
            current.Append c |> ignore
            current.Append body.[index + 1] |> ignore
            index <- index + 2
        | Some q when c = q ->
            quoteChar <- None
            current.Append c |> ignore
            index <- index + 1
        | Some _ ->
            current.Append c |> ignore
            index <- index + 1
        | None ->
            match c with
            | '\'' | '"' | '`' ->
                quoteChar <- Some c
                hasContent <- true
                current.Append c |> ignore
                index <- index + 1
            | '(' ->
                parenDepth <- parenDepth + 1
                hasContent <- true
                current.Append c |> ignore
                index <- index + 1
            | ')' ->
                parenDepth <- max 0 (parenDepth - 1)
                hasContent <- true
                current.Append c |> ignore
                index <- index + 1
            | ',' when parenDepth = 0 ->
                if hasContent then
                    parts.Add(current.ToString())

                current.Clear() |> ignore
                hasContent <- false
                index <- index + 1
            | _ ->
                hasContent <- hasContent || not (Char.IsWhiteSpace c)
                current.Append c |> ignore
                index <- index + 1

    if hasContent then
        parts.Add(current.ToString())

    parts |> Seq.map (fun s -> s.Trim()) |> Seq.filter (fun s -> s <> "") |> List.ofSeq

/// One `SET` fragment's parsed effect, applied only once every fragment in
/// the statement has parsed successfully (see `handleSet`) — mirrors real
/// MySQL executing a multi-assignment `SET` all-or-nothing rather than
/// left-to-right with partial effect.
type private TransactionIsolationScope =
    | SessionIsolation
    | NextTransactionIsolation
    | GlobalIsolation

type private SetAction =
    | SetNamesAction of charset: string * collation: string option
    | SetVarAction of name: string * value: string option * isGlobal: bool
    | SetTransactionIsolationAction of scope: TransactionIsolationScope * isolation: TransactionIsolation
    | SetUserVarAction of name: string * value: Value

/// System variables real MySQL accepts an explicit `NULL` for (rather than
/// the 1231 "can't be set to the value of NULL" every other variable
/// raises) — connector handshakes reset the results charset exactly this
/// way. Grows as real clients ask for more.
let private nullableSystemVars = Set.ofList [ "character_set_results" ]

let private globalOnlyLimitVariables =
    Set.ofList [ "max_allowed_packet"; "max_connections"; "max_prepared_stmt_count"; "net_write_timeout"; "local_infile" ]

let private normalizeIsolationLevel (raw: string) : string =
    Regex.Replace(raw.Trim().ToUpperInvariant(), @"\s+", "-")

let private transactionIsolationOf (value: string) : Result<TransactionIsolation, QueryResult> =
    match normalizeIsolationLevel value with
    | "READ-UNCOMMITTED" -> Ok ReadUncommitted
    | "READ-COMMITTED" -> Ok ReadCommitted
    | "REPEATABLE-READ" -> Ok RepeatableRead
    | "SERIALIZABLE" -> Ok Serializable
    | _ -> Error(Err(1231, sprintf "Variable 'transaction_isolation' can't be set to the value of '%s'" value))

let private transactionIsolationName =
    function
    | ReadUncommitted -> "READ UNCOMMITTED"
    | ReadCommitted -> "READ COMMITTED"
    | RepeatableRead -> "REPEATABLE READ"
    | Serializable -> "SERIALIZABLE"

let private transactionIsolationValue =
    function
    | ReadUncommitted -> "READ-UNCOMMITTED"
    | ReadCommitted -> "READ-COMMITTED"
    | RepeatableRead -> "REPEATABLE-READ"
    | Serializable -> "SERIALIZABLE"

let private transactionIsolationScope (prefix: string) =
    match prefix.Trim().ToUpperInvariant() with
    | "@@" -> NextTransactionIsolation
    | "GLOBAL"
    | "@@GLOBAL." -> GlobalIsolation
    | _ -> SessionIsolation

/// Parses one comma-split fragment into the variable(s) it would assign,
/// without touching `session`/`Store` — `handleSet` only applies any of
/// these once every fragment in the statement has parsed. Top-level targets
/// stay deferred, while nested `:=` assignments become visible to later
/// right-hand sides.
let private parseSetFragment
    (sql: string)
    (session: Session)
    (userVariables: Map<string, Value>)
    (fragment: string)
    : Result<SetAction * Map<string, Value>, QueryResult> =
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
        | _ -> Ok(SetNamesAction(namesMatch.Groups.[1].Value, explicitCollation), userVariables)
    else
        match Parser.parseUserVariableSetAssignment fragment with
        | Ok(variable, rhs) ->
            match UserVariableRef.validationError variable with
            | Some message -> Error(Err(3061, message))
            | None ->
                resolveUserSetRhs session userVariables sql rhs
                |> Result.map (fun (value, sideEffects) -> SetUserVarAction(variable.Name, value), sideEffects)
        | Error _ ->
            let varMatch = setVar.Match fragment

            if varMatch.Success then
                let isGlobal = isGlobalScope varMatch.Groups.[1].Value
                let name = varMatch.Groups.[2].Value.ToLowerInvariant()

                if Session.tryGlobalVariable session.Store name |> Option.isNone then
                    Error(Err(1193, sprintf "Unknown system variable '%s'" name))
                else
                    match resolveSystemSetRhs session userVariables sql varMatch.Groups.[3].Value with
                    | Error result -> Error result
                    | Ok(VString value, sideEffects) when name = "block_encryption_mode" ->
                        match Functions.tryBlockEncryptionMode value with
                        | Some canonical -> Ok(SetVarAction(name, Some canonical, isGlobal), sideEffects)
                        | None -> Error(Err(1231, sprintf "Variable '%s' can't be set to the value of '%s'" name value))
                    | Ok(VString value, sideEffects) when name = "transaction_isolation" ->
                        transactionIsolationOf value
                        |> Result.map (fun isolation ->
                            SetTransactionIsolationAction(transactionIsolationScope varMatch.Groups.[1].Value, isolation), sideEffects)
                    | Ok(_, _) when name = "transaction_isolation" ->
                        Error(Err(1231, "Variable 'transaction_isolation' can't be set to the value of 'NULL'"))
                    | Ok(VString value, sideEffects) when name = "collation_connection" ->
                        match Collation.tryFind value with
                        | Some _ -> Ok(SetVarAction(name, Some value, isGlobal), sideEffects)
                        | None -> Error(Err(1273, sprintf "Unknown collation: '%s'" value))
                    | Ok(VNull, sideEffects) when nullableSystemVars.Contains name -> Ok(SetVarAction(name, None, isGlobal), sideEffects)
                    | Ok(VNull, _) -> Error(Err(1231, sprintf "Variable '%s' can't be set to the value of 'NULL'" name))
                    | Ok(value, sideEffects) -> Ok(SetVarAction(name, toText value, isGlobal), sideEffects)
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
            value |> Option.iter (applySqlMode session.Store)

        if name = "collation_connection" then
            value
            |> Option.iter (fun v ->
                match Collation.tryFind v with
                | Some col -> setConnectionCollation session.Store col
                | None -> ())

        { session with Variables = Map.add name value session.Variables }
    | SetTransactionIsolationAction(scope, isolation) ->
        match scope with
        | SessionIsolation ->
            { session with
                PendingTransactionIsolation = None
                Variables = Map.add "transaction_isolation" (Some(transactionIsolationValue isolation)) session.Variables }
        | NextTransactionIsolation -> { session with PendingTransactionIsolation = Some isolation }
        | GlobalIsolation ->
            Session.setGlobalVariable session.Store "transaction_isolation" (Some(transactionIsolationValue isolation))
            session
    | SetUserVarAction(name, value) -> { session with UserVariables = Map.add name value session.UserVariables }

let private validateSetAction (session: Session) (action: SetAction) : Result<unit, QueryResult> =
    match action with
    | SetTransactionIsolationAction(GlobalIsolation, _) when not (Auth.hasGlobalPrivForAccount session.Store (accountOf session) "SUPER") ->
        Error(Err(1227, "Access denied; you need (at least one of) the SUPER privilege(s) for this operation"))
    | SetTransactionIsolationAction(NextTransactionIsolation, _) when session.Tx.IsSome ->
        Error(Err(1568, "Transaction characteristics can't be changed while a transaction is in progress"))
    | SetTransactionIsolationAction(_, (RepeatableRead | ReadCommitted | Serializable)) -> Ok()
    | SetTransactionIsolationAction(_, isolation) ->
        Error(Err(1235, sprintf "This version of MySQL doesn't yet support '%s transaction isolation'" (transactionIsolationName isolation)))
    | SetVarAction(_, _, true) when not (Auth.hasGlobalPrivForAccount session.Store (accountOf session) "SUPER") ->
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
    let parsed =
        splitSetAssignments sql
        |> List.fold
            (fun state fragment ->
                state
                |> Result.bind (fun (actions, sideEffects) ->
                    parseSetFragment sql session sideEffects fragment
                    |> Result.map (fun (action, nextSideEffects) -> action :: actions, nextSideEffects)))
            (Ok([], session.UserVariables))

    match parsed with
    | Error result -> session, result
    | Ok(actions, sideEffects) ->
        let actions = List.rev actions

        let userVariables =
            actions
            |> List.fold (fun variables action -> match action with SetUserVarAction(name, value) -> Map.add name value variables | _ -> variables) sideEffects

        if userVariables.Count > maxUserVariables then
            session, Err(1105, "Too many user-defined variables")
        else
            match actions |> traverse (validateSetAction session) with
            | Error result -> session, result
            | Ok _ ->
                let updated = actions |> List.fold applySetAction session
                { updated with UserVariables = userVariables }, Affected 0UL

// ---------------------------------------------------------------------------
// Transactions: BEGIN/COMMIT/ROLLBACK, SET autocommit, SAVEPOINT. Matched by
// text probe (like SET/SHOW above) rather than taught to the grammar —
// these are session-control statements, not something `Executor` evaluates
// rows against. See `Session.Transaction` for how real (not no-op) snapshot
// isolation is implemented cheaply on top of `Storage.Store`'s already-public
// mutable fields.
// ---------------------------------------------------------------------------

let private beginTx = Regex(@"^(?:BEGIN(?:\s+WORK)?|START\s+TRANSACTION(?:\s+READ\s+(ONLY|WRITE))?)$", RegexOptions.IgnoreCase)
let private commitTx = Regex(@"^COMMIT(?:\s+WORK)?(?:\s+AND\s+(NO\s+)?CHAIN)?$", RegexOptions.IgnoreCase)
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
let private accountRefOf (userRef: string) =
    let at = userRef.LastIndexOf '@'
    let unquote (value: string) = value.Trim().Trim([| '\''; '`'; '"' |])
    let name = if at < 0 then unquote userRef else unquote userRef[.. at - 1]
    let host = if at < 0 then "%" else unquote userRef[(at + 1) ..]
    Auth.account name host

let private setPasswordRe =
    Regex(@"^SET\s+PASSWORD\s*(?:FOR\s+([^=\s]+)\s*)?=\s*'([^']*)'\s*;?$", RegexOptions.IgnoreCase)

/// `SHOW GRANTS [FOR 'user'@'host' | FOR CURRENT_USER[()]]`.
let private showGrantsRe = Regex(@"^SHOW\s+GRANTS(?:\s+FOR\s+(.+?))?\s*;?$", RegexOptions.IgnoreCase)

/// `FLUSH [LOCAL] PRIVILEGES` — a no-op OK: privilege reads always hit the
/// live mysql.* rows, there's no cache to flush.
let private flushPrivilegesRe = Regex(@"^FLUSH\s+(?:LOCAL\s+)?PRIVILEGES\s*;?$", RegexOptions.IgnoreCase)
let private flushStatusRe = Regex(@"^FLUSH\s+STATUS\s*;?$", RegexOptions.IgnoreCase)
let private flushTablesRe = Regex(@"^FLUSH\s+TABLES\s*;?$", RegexOptions.IgnoreCase)
let private flushLogsRe = Regex(@"^FLUSH\s+LOGS\s*;?$", RegexOptions.IgnoreCase)

let private setTransactionIsolation =
    Regex(
        @"^SET\s+(?:(SESSION|GLOBAL)\s+)?TRANSACTION\s+ISOLATION\s+LEVEL\s+(REPEATABLE\s+READ|READ\s+COMMITTED|READ\s+UNCOMMITTED|SERIALIZABLE)$",
        RegexOptions.IgnoreCase
    )

let private setTransactionAccess =
    Regex(@"^SET\s+(SESSION\s+)?TRANSACTION\s+READ\s+(ONLY|WRITE)$", RegexOptions.IgnoreCase)

let private setCharacterSet = Regex(@"^SET\s+CHARACTER\s+SET\s+'?(\w+)'?$", RegexOptions.IgnoreCase)

let private rebaseTransactionSnapshot (session: Session) (tx: Transaction) : Catalog * Store =
    let baseCatalog = session.Store.Catalog
    let snapshot = Storage.beginTransactionSnapshot session.Store

    Storage.mergeCatalogInto snapshot tx.BaseCatalog tx.Snapshot.Catalog

    match tx.Snapshot.PendingEvents, snapshot.PendingEvents with
    | Some source, Some target -> target.AddRange source
    | _ -> ()

    baseCatalog, snapshot

/// Commits the open transaction by publishing its private catalog. Ordinary
/// isolation levels merge disjoint row changes; SERIALIZABLE validates the
/// transaction's read snapshot before publication. No open transaction is a
/// no-op, matching MySQL.
let private commitSession (session: Session) : Session =
    match session.Tx with
    | Some tx ->
        let dbName = session.Database |> Option.defaultValue defaultDatabase

        let committedSnapshot =
            let timeout =
                lookupVar session "innodb_lock_wait_timeout"
                |> Option.flatten
                |> Option.bind (fun value -> match Int32.TryParse value with | true, seconds -> Some(TimeSpan.FromSeconds(float seconds)) | _ -> None)
                |> Option.defaultWith Limits.lockWaitTimeout

            if not (Threading.Monitor.TryEnter(session.Store.Lock, timeout)) then
                raise (Storage.LockWaitTimeout dbName)

            try
                match tx.Isolation with
                | Serializable -> Storage.mergeSerializableCatalogInto session.Store tx.BaseCatalog tx.Snapshot.Catalog
                | _ -> Storage.mergeCatalogInto session.Store tx.BaseCatalog tx.Snapshot.Catalog

                tx.Snapshot
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
let private configuredReadOnly (session: Session) =
    session.PendingTransactionReadOnly
    |> Option.defaultWith (fun () -> lookupVar session "transaction_read_only" |> Option.flatten = Some "1")

let private configuredIsolation (session: Session) =
    session.PendingTransactionIsolation
    |> Option.defaultWith (fun () ->
        match lookupVar session "transaction_isolation" |> Option.flatten with
        | Some value ->
            match transactionIsolationOf value with
            | Ok isolation -> isolation
            | Error _ -> RepeatableRead
        | None -> RepeatableRead)

let private beginTransaction (readOnly: bool) (session: Session) : Session =
    let session = commitSession session
    let isolation = configuredIsolation session
    let baseCatalog = session.Store.Catalog
    let snapshot = Storage.beginTransactionSnapshot session.Store

    { session with
        PendingTransactionReadOnly = None
        PendingTransactionIsolation = None
        Tx =
            Some
                { Snapshot = snapshot
                  BaseCatalog = baseCatalog
                  Isolation = isolation
                  ReadOnly = readOnly
                  Seeded = false
                  Savepoints = Map.empty
                  NextSavepointSeq = 0 } }

/// Seeds repeatable-read snapshots and refreshes read-committed views.
let startTransactionStatement (session: Session) : Session =
    match session.Tx with
    | Some tx when tx.Isolation = ReadCommitted ->
        let baseCatalog, snapshot = rebaseTransactionSnapshot session tx

        { session with
            Tx =
                Some
                    { tx with
                        Snapshot = snapshot
                        BaseCatalog = baseCatalog
                        Seeded = true } }
    | Some tx when not tx.Seeded ->
        let baseCatalog = session.Store.Catalog
        let snapshot = Storage.beginTransactionSnapshot session.Store

        let savepoints =
            tx.Savepoints
            |> Map.map (fun _ savepoint ->
                { savepoint with
                    BaseCatalog = baseCatalog
                    Catalog = baseCatalog })

        { session with
            Tx =
                Some
                    { tx with
                        Snapshot = snapshot
                        BaseCatalog = baseCatalog
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
    let session = if session.Tx.IsNone then beginTransaction (configuredReadOnly session) session else session

    match session.Tx with
    | Some tx ->
        let eventCount = tx.Snapshot.PendingEvents |> Option.map (fun b -> b.Count) |> Option.defaultValue 0
        let seq = tx.NextSavepointSeq

        { session with
            Tx =
                Some
                    { tx with
                        Savepoints =
                            Map.add
                                name
                                { Sequence = seq
                                  BaseCatalog = tx.BaseCatalog
                                  Catalog = tx.Snapshot.Catalog
                                  PendingEventCount = eventCount }
                                tx.Savepoints
                        NextSavepointSeq = seq + 1 } },
        Affected 0UL
    | None -> session, Affected 0UL // unreachable: beginTransaction always sets Tx

let private rollbackToSavepoint (name: string) (session: Session) : Session * QueryResult =
    match session.Tx |> Option.bind (fun tx -> Map.tryFind name tx.Savepoints |> Option.map (fun seed -> tx, seed)) with
    | Some(tx, savepoint) ->
        // Real MySQL never rolls back a burned AUTO_INCREMENT id — not even
        // a savepoint rollback (`bumpAutoIncrementsInto`'s doc covers the
        // full-ROLLBACK case, which is a separate code path from this one).
        // `catalog` is the savepoint's own stale copy of every `NextAutoId`,
        // so bump it back up to whatever this transaction ran ahead to
        // since, before wholesale-replacing the snapshot's catalog with it.
        let catalog = Storage.bumpAutoIncrements tx.Snapshot.Catalog savepoint.Catalog
        Storage.setCatalog tx.Snapshot catalog
        // Drop every event this transaction buffered after the savepoint —
        // otherwise a WAL replay would apply writes the savepoint rollback
        // just undid.
        tx.Snapshot.PendingEvents
        |> Option.iter (fun buffer ->
            if buffer.Count > savepoint.PendingEventCount then
                buffer.RemoveRange(savepoint.PendingEventCount, buffer.Count - savepoint.PendingEventCount))

        // Real MySQL also destroys every savepoint established *after* the
        // one rolled back to — the named savepoint itself survives (a
        // second `ROLLBACK TO` naming it again is legal).
        let survivors = tx.Savepoints |> Map.filter (fun _ candidate -> candidate.Sequence <= savepoint.Sequence)

        { session with
            Tx =
                Some
                    { tx with
                        BaseCatalog = savepoint.BaseCatalog
                        Savepoints = survivors } },
        Affected 0UL
    | None -> session, savepointNotFound name

/// Drops the named savepoint and, matching real MySQL, every savepoint
/// established after it too.
let private releaseSavepoint (name: string) (session: Session) : Session * QueryResult =
    match session.Tx |> Option.bind (fun tx -> Map.tryFind name tx.Savepoints |> Option.map (fun seed -> tx, seed)) with
    | Some(tx, savepoint) ->
        let survivors = tx.Savepoints |> Map.filter (fun _ candidate -> candidate.Sequence < savepoint.Sequence)
        { session with Tx = Some { tx with Savepoints = survivors } }, Affected 0UL
    | None -> session, savepointNotFound name

let private handleSetAutocommit (value: string) (session: Session) : Session * QueryResult =
    let session = { session with Variables = Map.add "autocommit" (Some value) session.Variables }

    let session =
        if value = "0" then
            (if session.Tx.IsNone then beginTransaction (configuredReadOnly session) session else session)
        else
            commitSession session

    session, Affected 0UL

/// Parses and executes anything that isn't one of the text-probe special
/// cases above. Anything else is a 1064 syntax error with SQLSTATE 42000.
/// Executes an already-parsed `Statement` — shared by COM_QUERY (parse then
/// execute) and COM_STMT_EXECUTE (bind placeholders then execute), so the
/// prepared path reuses this one execution body instead of splicing literals
/// back into SQL text and re-parsing.
let private executeParsed (session: Session) (stmt: Statement) : Session * QueryResult =
    match stmt with
    | Select _
    | Union _ -> InformationSchema.recordCommand InformationSchema.SelectCommand
    | Insert _
    | InsertSelect _ -> InformationSchema.recordCommand InformationSchema.InsertCommand
    | Replace _
    | ReplaceSelect _
    | ReplaceSet _ -> InformationSchema.recordCommand InformationSchema.ReplaceCommand
    | Update _ -> InformationSchema.recordCommand InformationSchema.UpdateCommand
    | Delete _ -> InformationSchema.recordCommand InformationSchema.DeleteCommand
    | _ -> ()

    // A SELECT into information_schema.processlist / the privilege views
    // scopes its rows to this session's user (unless it holds the revealing
    // privilege) — see `InformationSchema.currentViewer`.
    InformationSchema.currentViewer.Value <- Some(session.Store, accountOf session)
    let dbName = session.Database |> Option.defaultValue defaultDatabase

    let execute session =
        let store = Session.currentStore session

        // Privilege enforcement — the one gate every parsed statement goes
        // through (probes are exempt, see `Auth.requiredPrivileges`'s doc).
        let access =
            match session.Tx, stmt with
            | Some tx, (Select _ | Union _ | Explain _) when tx.ReadOnly ->
                Auth.checkForAccount store (accountOf session) (Auth.requiredPrivilegesInStore store dbName stmt)
            | Some tx, _ when tx.ReadOnly -> Error(1792, "Cannot execute statement in a READ ONLY transaction")
            | _ -> Auth.checkForAccount store (accountOf session) (Auth.requiredPrivilegesInStore store dbName stmt)

        match access with
        | Error(code, msg) -> session, Err(code, msg)
        | Ok() ->

        // `Store.StrictMode` is store-wide, not per-session (see its doc
        // comment) — re-derive it from *this* session's own `sql_mode`
        // right before every statement, so another connection's `SET
        // SESSION sql_mode = ...` (which only ever touches its own
        // `Session.Variables`) can't leak into this one's coercion
        // behavior, and a transaction never runs on the stale StrictMode
        // its snapshot happened to be seeded with at BEGIN time.
        lookupVar session "sql_mode"
        |> Option.flatten
        |> Option.iter (applySqlMode store)

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
        let variables = expressionVariables session

        let lastInsertId, lastGeneratedId, result, columnMetadata, calculatedFoundRows =
            Executor.withVariableContext variables (fun () ->
                match stmt with
                | Select select ->
                    let result, types, calculatedFoundRows =
                        withExecutionLimits (fun () -> Executor.runTopLevelSelect store registry dbName select)

                    session.LastInsertId, session.LastGeneratedId, result, types, calculatedFoundRows
                | Union(first, rest, orderBy, limit, offset) ->
                    let result, types, calculatedFoundRows =
                        withExecutionLimits (fun () -> Executor.runTopLevelUnion store registry dbName first rest orderBy limit offset)

                    session.LastInsertId, session.LastGeneratedId, result, types, calculatedFoundRows
                | _ ->
                    let foundRows = session.Capabilities &&& Fsdb.Protocol.ClientFoundRows <> 0u

                    let (lastInsertId, lastGeneratedId), result =
                        withExecutionLimits (fun () ->
                            Executor.executeAs store registry dbName (session.LastInsertId, session.LastGeneratedId) foundRows (accountOf session) stmt)

                    lastInsertId, lastGeneratedId, result, [], None)

        let session =
            { session with
                LastInsertId = lastInsertId
                LastGeneratedId = lastGeneratedId
                LastResultColumnMetadata = columnMetadata
                PendingFoundRows = calculatedFoundRows
                UserVariables = variables.UserVariables.Value }

        session, result

    match session.Tx with
    | Some _ -> execute (startTransactionStatement session)
    | None -> execute session

let private executeStatement (session: Session) (sql: string) (upper: string) : Session * QueryResult =
    match Parser.parse sql with
    | Result.Ok stmt -> executeParsed session stmt
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
    | SetTransactionIsolation of scope: TransactionIsolationScope * level: string
    | SetTransactionAccess of sessionScope: bool * readOnly: bool
    | SetCharacterSet of charset: string
    | SetPassword of user: string option * password: string
    | SetVar
    | RollbackTo of savepoint: string
    | Begin of readOnly: bool option
    | Commit of chain: bool
    | Rollback
    | Savepoint of name: string
    | Release of name: string
    | Use of dbName: string
    | ShowVariables of isGlobal: bool
    | ShowStatus
    | ShowEngines
    | ShowEngineInnodbStatus
    | ShowPlugins
    | ShowBinaryLogs
    | ShowBinaryLogStatus
    | ShowReplicaStatus
    | MaintainTables of operation: string * tables: string list
    | ShowOpenTables of db: string option * pattern: string option
    | ShowCreateDatabase of name: string
    | ShowCharset
    | ShowProcesslist of full: bool
    | ShowTriggers of db: string option
    | ShowEvents of db: string option
    | ShowRoutineStatus
    | Kill of queryOnly: bool * id: int64
    | AlterKeysNoop of table: string
    | ShowConditions of errorsOnly: bool
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
    | ShowCreateUser of user: string
    | ShowCreateProgram of kind: string * name: string
    | ShowPrivileges
    | FlushPrivileges
    | FlushStatus
    | FlushTables
    | FlushLogs

/// The one ordered list of text-probed forms — matching `Probe`'s cases
/// exactly (the compiler enforces `runProbe` covers every one of them), so
/// COM_QUERY (`dispatch`) and COM_STMT_PREPARE (`prepareStatement`) can
/// never disagree about which statements are text-probed vs. parsed the way
/// two independently-written predicates could drift.
let private tryProbe (sql: string) (upper: string) : Probe option =
    if setAutocommit.IsMatch sql then
        Some(SetAutocommit((setAutocommit.Match sql).Groups.[1].Value))
    elif setTransactionIsolation.IsMatch sql then
        let m = setTransactionIsolation.Match sql

        let scope =
            match m.Groups.[1].Value.ToUpperInvariant() with
            | "SESSION" -> SessionIsolation
            | "GLOBAL" -> GlobalIsolation
            | _ -> NextTransactionIsolation

        Some(SetTransactionIsolation(scope, m.Groups.[2].Value))
    elif setTransactionAccess.IsMatch sql then
        let m = setTransactionAccess.Match sql
        Some(SetTransactionAccess(m.Groups.[1].Success, m.Groups.[2].Value.Equals("ONLY", StringComparison.OrdinalIgnoreCase)))
    elif setCharacterSet.IsMatch sql then
        Some(SetCharacterSet((setCharacterSet.Match sql).Groups.[1].Value))
    elif setPasswordRe.IsMatch sql then
        let m = setPasswordRe.Match sql
        Some(SetPassword((if m.Groups.[1].Success then Some m.Groups.[1].Value else None), m.Groups.[2].Value))
    elif upper.StartsWith "SET " then
        Some SetVar
    elif rollbackToSavepointStmt.IsMatch sql then
        Some(RollbackTo((rollbackToSavepointStmt.Match sql).Groups.[2].Value))
    elif beginTx.IsMatch upper then
        let mode = (beginTx.Match upper).Groups.[1]
        Some(Begin(if mode.Success then Some(mode.Value = "ONLY") else None))
    elif commitTx.IsMatch upper then
        Some(Commit(Regex.IsMatch(upper, @"AND\s+CHAIN$", RegexOptions.IgnoreCase)))
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
    elif showEngineInnodbStatusRe.IsMatch sql then
        Some ShowEngineInnodbStatus
    elif showPluginsRe.IsMatch sql then
        Some ShowPlugins
    elif showBinaryLogsRe.IsMatch sql then
        Some ShowBinaryLogs
    elif showBinaryLogStatusRe.IsMatch sql then
        Some ShowBinaryLogStatus
    elif showReplicaStatusRe.IsMatch sql then
        Some ShowReplicaStatus
    elif maintenanceTableRe.IsMatch sql then
        let matched = maintenanceTableRe.Match sql
        let tables = matched.Groups.[2].Value.Split(',') |> Array.map (fun table -> table.Trim()) |> List.ofArray
        Some(MaintainTables(matched.Groups.[1].Value.ToLowerInvariant(), tables))
    elif showOpenTablesRe.IsMatch sql then
        let m = showOpenTablesRe.Match sql
        Some(
            ShowOpenTables(
                (if m.Groups.[1].Success then Some(stripBackticks m.Groups.[1].Value) else None),
                (if m.Groups.[2].Success then Some m.Groups.[2].Value else None)
            )
        )
    elif showCreateDatabaseRe.IsMatch sql then
        Some(ShowCreateDatabase(stripBackticks (showCreateDatabaseRe.Match sql).Groups.[1].Value))
    elif showCharsetRe.IsMatch sql then
        Some ShowCharset
    elif showPrivilegesRe.IsMatch sql then
        Some ShowPrivileges
    elif showCreateUserRe.IsMatch sql then
        Some(ShowCreateUser((showCreateUserRe.Match sql).Groups.[1].Value))
    elif showCreateProgramRe.IsMatch sql then
        let matched = showCreateProgramRe.Match sql
        Some(ShowCreateProgram(matched.Groups.[1].Value.ToUpperInvariant(), matched.Groups.[2].Value))
    elif showGrantsRe.IsMatch sql then
        let m = showGrantsRe.Match sql
        Some(ShowGrants(if m.Groups.[1].Success then Some m.Groups.[1].Value else None))
    elif flushPrivilegesRe.IsMatch sql then
        Some FlushPrivileges
    elif flushStatusRe.IsMatch sql then
        Some FlushStatus
    elif flushTablesRe.IsMatch sql then
        Some FlushTables
    elif flushLogsRe.IsMatch sql then
        Some FlushLogs
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
        Some(ShowConditions false)
    elif showErrorsRe.IsMatch sql then
        Some(ShowConditions true)
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
        | ShowOpenTables _
        | ShowCreateDatabase _
        | ShowCreate _
        | ShowCreateView _
        | ShowColumns _
        | Describe _
        | ShowIndex _ -> startTransactionStatement session
        | _ -> session

    match probe with
    | SetAutocommit value -> handleSetAutocommit value session
    | SetTransactionIsolation(scope, level) ->
        match transactionIsolationOf level with
        | Error result -> session, result
        | Ok isolation ->
            let action = SetTransactionIsolationAction(scope, isolation)

            match validateSetAction session action with
            | Error result -> session, result
            | Ok() -> applySetAction session action, Affected 0UL
    | SetTransactionAccess(sessionScope, readOnly) ->
        let value = if readOnly then "1" else "0"

        (if sessionScope then
             { session with
                 Variables =
                     session.Variables
                     |> Map.add "transaction_read_only" (Some value)
                     |> Map.add "tx_read_only" (Some value) }
         else
             { session with PendingTransactionReadOnly = Some readOnly }),
        Affected 0UL
    | SetCharacterSet charset ->
        let charset = charset.ToLowerInvariant()

        let collation =
            match charset with
            | "utf8mb4" -> Some "utf8mb4_general_ci"
            | "utf8"
            | "utf8mb3" -> Some "utf8mb3_general_ci"
            | "latin1" -> Some "latin1_swedish_ci"
            | "ascii" -> Some "ascii_general_ci"
            | "binary" -> Some "binary"
            | _ -> None

        match collation with
        | None -> session, Err(1115, sprintf "Unknown character set: '%s'" charset)
        | Some collation ->
            { session with
                Variables =
                    session.Variables
                    |> Map.add "character_set_client" (Some charset)
                    |> Map.add "character_set_results" (Some charset)
                    |> Map.add "character_set_connection" (Some charset)
                    |> Map.add "collation_connection" (Some collation) },
            Affected 0UL
    | SetPassword(userOpt, password) ->
        // No FOR clause selects the session's authenticated account.
        let wanted = userOpt |> Option.map accountRefOf |> Option.defaultValue (accountOf session)
        let store = Session.currentStore session

        // MySQL's rule: changing your own password is free, anyone else's
        // needs CREATE USER — probes bypass `executeParsed`'s enforcement
        // gate, so this one carries its own check.
        let required = if Auth.sameAccount wanted (accountOf session) then [] else [ "CREATE USER", Auth.Global ]

        match Auth.checkForAccount store (accountOf session) required |> Result.bind (fun () -> Auth.setPassword store wanted.Name wanted.Host password) with
        | Ok() -> session, Affected 0UL
        | Error(code, msg) -> session, Err(code, msg)
    | SetVar -> handleSet session sql
    | RollbackTo name -> rollbackToSavepoint name session
    | Begin readOnly ->
        let access = readOnly |> Option.defaultValue (configuredReadOnly session)
        beginTransaction access session, Affected 0UL
    | Commit chain ->
        let readOnly = session.Tx |> Option.map _.ReadOnly |> Option.defaultValue false
        let committed = commitSession session
        (if chain then beginTransaction readOnly committed else committed), Affected 0UL
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
    | ShowStatus -> session, InformationSchema.showStatus session.TlsCipher session.TlsVersion (statusFilter sql) |> showResult
    | ShowEngines -> session, InformationSchema.showEngines () |> showResult
    | ShowEngineInnodbStatus ->
        session,
        ResultSet(
            [ "Type"; "Name"; "Status" ],
            [ [ Some "InnoDB"; Some ""; Some "fsdb uses an in-memory transactional row store" ] ]
        )
    | ShowPlugins ->
        session,
        ResultSet(
            [ "Name"; "Status"; "Type"; "Library"; "License" ],
            [ [ Some "mysql_native_password"; Some "ACTIVE"; Some "AUTHENTICATION"; None; Some "GPL" ] ]
        )
    | ShowBinaryLogs
    | ShowBinaryLogStatus -> session, Err(1381, "You are not using binary logging")
    | ShowReplicaStatus ->
        session,
        ResultSet(
            [ "Replica_IO_State"
              "Source_Host"
              "Source_User"
              "Source_Port"
              "Connect_Retry"
              "Source_Log_File"
              "Read_Source_Log_Pos"
              "Relay_Log_File"
              "Relay_Log_Pos"
              "Relay_Source_Log_File"
              "Replica_IO_Running"
              "Replica_SQL_Running"
              "Replicate_Do_DB"
              "Replicate_Ignore_DB"
              "Replicate_Do_Table"
              "Replicate_Ignore_Table"
              "Replicate_Wild_Do_Table"
              "Replicate_Wild_Ignore_Table"
              "Last_Errno"
              "Last_Error"
              "Skip_Counter"
              "Exec_Source_Log_Pos"
              "Relay_Log_Space"
              "Until_Condition"
              "Until_Log_File"
              "Until_Log_Pos"
              "Source_SSL_Allowed"
              "Source_SSL_CA_File"
              "Source_SSL_CA_Path"
              "Source_SSL_Cert"
              "Source_SSL_Cipher"
              "Source_SSL_Key"
              "Seconds_Behind_Source"
              "Source_SSL_Verify_Server_Cert"
              "Last_IO_Errno"
              "Last_IO_Error"
              "Last_SQL_Errno"
              "Last_SQL_Error"
              "Replicate_Ignore_Server_Ids"
              "Source_Server_Id"
              "Source_UUID"
              "Source_Info_File"
              "SQL_Delay"
              "SQL_Remaining_Delay"
              "Replica_SQL_Running_State"
              "Source_Retry_Count"
              "Source_Bind"
              "Last_IO_Error_Timestamp"
              "Last_SQL_Error_Timestamp"
              "Source_SSL_Crl"
              "Source_SSL_Crlpath"
              "Retrieved_Gtid_Set"
              "Executed_Gtid_Set"
              "Auto_Position"
              "Replicate_Rewrite_DB"
              "Channel_Name"
              "Source_TLS_Version"
              "Source_public_key_path"
              "Get_Source_public_key"
              "Network_Namespace" ],
            []
        )
    | MaintainTables(operation, tables) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase

        let rows =
            tables
            |> List.collect (fun tableRef ->
                let dbName, tableName = splitQualified sessionDb tableRef
                let qualified = dbName + "." + tableName

                match Storage.scan (Session.currentStore session) dbName tableName with
                | Ok _ when operation = "optimize" ->
                    [ [ Some qualified
                        Some operation
                        Some "note"
                        Some "Table does not support optimize, doing recreate + analyze instead" ]
                      [ Some qualified; Some operation; Some "status"; Some "OK" ] ]
                | Ok _ when operation = "repair" ->
                    [ [ Some qualified
                        Some operation
                        Some "note"
                        Some "The storage engine for the table doesn't support repair" ] ]
                | Ok _ -> [ [ Some qualified; Some operation; Some "status"; Some "OK" ] ]
                | Error _ ->
                    [ [ Some qualified
                        Some operation
                        Some "Error"
                        Some(sprintf "Table '%s.%s' doesn't exist" dbName tableName) ] ])

        session, ResultSet([ "Table"; "Op"; "Msg_type"; "Msg_text" ], rows)
    | ShowOpenTables(db, pattern) ->
        let dbName = db |> Option.defaultValue (session.Database |> Option.defaultValue defaultDatabase)

        let matches (name: string) =
            pattern
            |> Option.map (fun value -> Regex.IsMatch(name, likeToRegex value, RegexOptions.IgnoreCase ||| RegexOptions.Singleline))
            |> Option.defaultValue true

        let rows =
            (Session.currentStore session).Catalog
            |> Map.tryFind (dbName.ToLowerInvariant())
            |> Option.defaultValue Map.empty
            |> Map.toList
            |> List.map (snd >> _.OriginalName)
            |> List.filter matches
            |> List.filter (Auth.canSeeTableForAccount (Session.currentStore session) (accountOf session) dbName)
            |> List.sort
            |> List.map (fun table -> [ Some dbName; Some table; Some "0"; Some "0" ])

        session, ResultSet([ "Database"; "Table"; "In_use"; "Name_locked" ], rows)
    | ShowCreateDatabase name ->
        if Storage.databaseExists (Session.currentStore session) name then
            let quotedName = name.Replace("`", "``")

            session,
            ResultSet(
                [ "Database"; "Create Database" ],
                [ [ Some name
                    Some(sprintf "CREATE DATABASE `%s` /*!40100 DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci */" quotedName) ] ]
            )
        else
            session, Err(1049, sprintf "Unknown database '%s'" name)
    | ShowCharset -> session, InformationSchema.showCharacterSet (likeSuffix sql) |> showResult
    | ShowPrivileges -> session, InformationSchema.showPrivileges () |> showResult
    | ShowProcesslist full ->
        let result =
            InformationSchema.withViewer session.Store (accountOf session) (fun () -> InformationSchema.showProcesslist full |> showResult)

        session, result
    | ShowTriggers db ->
        // `FROM db` when given, else the session's current database — same
        // resolution MySQL applies.
        let dbName = db |> Option.defaultValue (session.Database |> Option.defaultValue defaultDatabase)
        session, InformationSchema.showTriggers (Session.currentStore session).Catalog dbName |> showResult
    | ShowEvents db -> session, InformationSchema.showEvents (Session.currentStore session).Catalog db |> showResult
    | ShowRoutineStatus -> session, InformationSchema.showRoutineStatus () |> showResult
    | Kill(queryOnly, id) ->
        let canSeeAll = Auth.hasGlobalPrivForAccount session.Store (accountOf session) "PROCESS"
        let canKillAll = Auth.hasGlobalPrivForAccount session.Store (accountOf session) "SUPER"

        match InformationSchema.tryFindProcess id with
        // A caller without PROCESS can't see another user's connection, so
        // it can neither name nor kill it — MySQL reports the id as unknown.
        | Some target when not (Auth.sameAccount target.Account (accountOf session)) && not canSeeAll ->
            session, Err(1094, sprintf "Unknown thread id: %d" id)
        | None -> session, Err(1094, sprintf "Unknown thread id: %d" id)
        | Some target when not (Auth.sameAccount target.Account (accountOf session)) && not canKillAll ->
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
    | ShowConditions errorsOnly ->
        let conditions =
            session.Diagnostics
            |> List.filter (fun condition -> not errorsOnly || condition.Level = Diagnostics.Error)

        let limit =
            let matchLimit = Regex.Match(sql, @"\s+LIMIT\s+(\d+)(?:\s*,\s*(\d+))?\s*$", RegexOptions.IgnoreCase)

            if not matchLimit.Success then
                conditions
            else
                let first = int matchLimit.Groups.[1].Value
                let offset, count =
                    if matchLimit.Groups.[2].Success then int matchLimit.Groups.[1].Value, int matchLimit.Groups.[2].Value else 0, first

                conditions |> List.skip (min offset conditions.Length) |> List.truncate count

        let rows =
            limit
            |> List.map (fun condition ->
                let level =
                    match condition.Level with
                    | Diagnostics.Warning -> "Warning"
                    | Diagnostics.Error -> "Error"
                    | Diagnostics.Note -> "Note"

                [ Some level; Some(string condition.Code); Some condition.Message ])

        session, ResultSet([ "Level"; "Code"; "Message" ], rows)
    | ShowMessageCount isError ->
        let col = if isError then "@@session.error_count" else "@@session.warning_count"
        let count =
            if isError then
                session.Diagnostics |> List.filter (fun condition -> condition.Level = Diagnostics.Error) |> List.length
            else
                session.Diagnostics.Length

        session, ResultSet([ col ], [ [ Some(string count) ] ])
    | ShowDatabases -> session, handleShowDatabases session sql
    | ShowTableStatus -> session, handleShowTableStatus session sql
    | ShowCollation -> session, InformationSchema.showCollation (likeSuffix sql) |> showResult
    | ShowTables -> session, handleShowTables session sql
    | ShowCreate name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        session,
        InformationSchema.showCreateTable (catalogWithOverlay session dbName table) dbName table
        |> showTableResult session dbName table
    | ShowCreateView name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, view = splitQualified sessionDb name
        session,
        InformationSchema.showCreateView (Session.currentStore session).Catalog dbName view
        |> showTableResult session dbName view
    | ShowColumns(full, name, dbOverride) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        let dbName = dbOverride |> Option.map stripBackticks |> Option.defaultValue dbName
        let store = Session.currentStore session
        let viewColumns = Executor.viewColumns store (registryFor session)
        session,
        InformationSchema.showColumns (catalogWithOverlay session dbName table) (Some viewColumns) full dbName table (likeSuffix sql)
        |> showTableResult session dbName table
    | Describe name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        let store = Session.currentStore session
        let viewColumns = Executor.viewColumns store (registryFor session)
        session,
        InformationSchema.showColumns (catalogWithOverlay session dbName table) (Some viewColumns) false dbName table None
        |> showTableResult session dbName table
    | ShowIndex(name, dbOverride) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        let dbName = dbOverride |> Option.map stripBackticks |> Option.defaultValue dbName
        session,
        InformationSchema.showIndex (catalogWithOverlay session dbName table) dbName table
        |> showTableResult session dbName table
    | ShowGrants userOpt ->
        // No FOR clause or CURRENT_USER selects the authenticated account.
        let wanted =
            match userOpt with
            | None -> accountOf session
            | Some u when u.Trim().ToUpperInvariant().StartsWith "CURRENT_USER" -> accountOf session
            | Some u -> accountRefOf u

        inspectAccount session wanted (fun () ->
            match Auth.renderGrantsForAccount (Session.currentStore session) wanted with
            | Ok(header, lines) -> ResultSet([ header ], lines |> List.map (fun line -> [ Some line ]))
            | Error(code, msg) -> Err(code, msg))
    | ShowCreateUser userRef ->
        let wanted =
            if Regex.IsMatch(userRef, @"^CURRENT_USER(?:\(\))?$", RegexOptions.IgnoreCase) then
                accountOf session
            else
                accountRefOf userRef

        inspectAccount session wanted (fun () ->
            match Auth.renderCreateUserForAccount (Session.currentStore session) wanted with
            | Ok(header, ddl) -> ResultSet([ header ], [ [ Some ddl ] ])
            | Error(code, msg) -> Err(code, msg))
    | ShowCreateProgram(kind, qualifiedName) ->
        let _, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName

        if kind = "EVENT" then
            session, Err(1539, sprintf "Unknown event '%s'" name)
        else
            session, Err(1305, sprintf "%s %s does not exist" kind name)
    | FlushPrivileges -> session, Affected 0UL
    | FlushStatus ->
        InformationSchema.resetQuestions ()
        InformationSchema.resetCommandCounts ()
        session, Affected 0UL
    | FlushTables
    | FlushLogs -> session, Affected 0UL
let rec mapPlaceholders (replace: int -> Expr) (stmt: Statement) : Statement =
    let rec mapExpr (e: Expr) : Expr =
        match e with
        | Placeholder i -> replace i
        | MatchAgainst(cols, q, mode) -> MatchAgainst(cols, mapExpr q, mode)
        | Lit _
        | UserVariable _
        | SystemVariable _
        | Col _
        | QualifiedCol _
        | Star _ -> e
        | Row values -> Row(List.map mapExpr values)
        | BinOp(op, a, b) -> BinOp(op, mapExpr a, mapExpr b)
        | AssignUserVariable(name, value) -> AssignUserVariable(name, mapExpr value)
        | Not x -> Not(mapExpr x)
        | IsNull x -> IsNull(mapExpr x)
        | IsNotNull x -> IsNotNull(mapExpr x)
        | IsTrue x -> IsTrue(mapExpr x)
        | IsFalse x -> IsFalse(mapExpr x)
        | Like(x, p, cs, esc) -> Like(mapExpr x, mapExpr p, cs, esc)
        | Regexp(x, p) -> Regexp(mapExpr x, mapExpr p)
        | In(x, xs) -> In(mapExpr x, List.map mapExpr xs)
        | InSubquery(x, s) -> InSubquery(mapExpr x, mapSelect s)
        | QuantifiedComparison(x, op, quantifier, s) -> QuantifiedComparison(mapExpr x, op, quantifier, mapSelect s)
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

        { Inherit = spec.Inherit
          PartitionBy = List.map mapExpr spec.PartitionBy
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
    | CreateTableAs(name, query, ifNotExists) -> CreateTableAs(name, mapPlaceholders replace query, ifNotExists)
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

    if upper.StartsWith("LOAD DATA", StringComparison.Ordinal) then
        Result.Error(1295, "This command is not supported in the prepared statement protocol yet")
    elif (tryProbe trimmed upper).IsSome then
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
let private recordResult ((session, result): Session * QueryResult) : Session * QueryResult =
    let session =
        match result with
        | Affected count ->
            { session with
                LastRowCount = int64 count
                FoundRows = 0UL
                PendingFoundRows = None }
        | ResultSet(_, rows) ->
            { session with
                LastRowCount = -1L
                FoundRows = session.PendingFoundRows |> Option.defaultValue (uint64 rows.Length)
                PendingFoundRows = None }
        | Err _ -> { session with LastRowCount = -1L; PendingFoundRows = None }

    session, result

let private recoverExecutionError (session: Session) (description: string) (error: exn) : Session * QueryResult =
    match error with
    | Storage.LockWaitTimeout dbName ->
        Log.diagnostic "fsdb: ERR 1205 lock wait timeout on database %s -- %s" dbName description
        session, Err(1205, "Lock wait timeout exceeded; try restarting transaction")
    // ponytail: MySQL's 1690 message names the offending expression; fsdb
    // needs an AST printer before it can do the same without reconstructing SQL.
    | Value.UnsignedOutOfRange ->
        Log.diagnostic "fsdb: ERR 1690 unsigned out of range -- %s" description
        session, Err(1690, "BIGINT UNSIGNED value is out of range")
    // Extension functions may fail after an effect, so their chosen SQL error
    // must not leave a transaction containing partially applied state.
    | Functions.SqlError(code, message) ->
        Log.diagnostic "fsdb: ERR %d %s -- %s" code message description
        { session with Tx = None }, Err(code, message)
    | ex ->
        Log.diagnostic "fsdb: EXN %s -- %s" ex.Message description
        { session with Tx = None }, Err(1105, "Internal error")

let private preservesDiagnostics (sql: string) : bool =
    let upper = sql.Trim().ToUpperInvariant()

    match tryProbe sql upper with
    | Some(ShowConditions _ | ShowMessageCount _) -> true
    | _ ->
        Regex.IsMatch(
            sql,
            @"^\s*SELECT\s+@@(?:SESSION\.)?(?:WARNING_COUNT|ERROR_COUNT)(?:\s+AS\s+\w+)?\s*$",
            RegexOptions.IgnoreCase
        )

let private recordDiagnostics
    (session: Session)
    (preserve: bool)
    (execute: unit -> Session * QueryResult)
    : Session * QueryResult =
    let session = if preserve then session else { session with Diagnostics = [] }
    let (session, result), generated = Diagnostics.capture execute

    let generated =
        match result with
        | Err(code, message) -> generated @ [ { Diagnostics.Level = Diagnostics.Error; Code = code; Message = message } ]
        | _ -> generated

    let session = if preserve then session else { session with Diagnostics = generated }
    recordResult (session, result)

let handle (session: Session) (rawSql: string) : Session * QueryResult =
    recordDiagnostics session (preservesDiagnostics rawSql) (fun () ->
        try
            match dispatch session rawSql with
            | _, Err(code, msg) as result ->
                Log.diagnostic "fsdb: ERR %d %s -- query: %s" code msg (Log.redactSql rawSql)
                result
            | result -> result
        with
        | :? OperationCanceledException ->
            reraise ()
        | ex -> recoverExecutionError session (sprintf "query: %s" (Log.redactSql rawSql)) ex)

/// Parses and authorizes a LOCAL INFILE command before the server asks the
/// client to send bytes. The file name is never resolved by the server.
let tryPrepareLocalLoad (session: Session) (sql: string) : Result<Parser.LocalLoad option, QueryResult> =
    let normalized = Parser.stripVersionComments sql |> fun value -> value.TrimStart()

    if not (normalized.StartsWith("LOAD DATA", StringComparison.OrdinalIgnoreCase)) then
        Result.Ok None
    else
        match Parser.parseLocalLoad sql with
        | Result.Error _ -> Result.Error(syntaxError sql)
        | Result.Ok load ->
            let charset = load.Charset |> Option.map (fun value -> value.ToLowerInvariant())

            match charset with
            | Some value when value <> "utf8" && value <> "utf8mb4" ->
                Result.Error(Err(1235, sprintf "LOAD DATA CHARACTER SET %s is not supported" value))
            | _ ->
                let statement =
                    if load.Replace then
                        Replace(load.Table, load.Columns, [])
                    else
                        Insert(load.Table, load.Columns, [], [], load.Ignore)

                let dbName = session.Database |> Option.defaultValue defaultDatabase

                match Auth.checkForAccount (Session.currentStore session) (accountOf session) (Auth.requiredPrivilegesInStore (Session.currentStore session) dbName statement) with
                | Ok() -> Result.Ok(Some load)
                | Error(code, message) -> Result.Error(Err(code, message))

/// Inserts already-decoded LOCAL INFILE rows through the ordinary INSERT or
/// REPLACE execution path, retaining its coercion, trigger, and transaction
/// behavior.
let executeLocalLoad (session: Session) (load: Parser.LocalLoad) (rows: Value list list) : Session * QueryResult =
    let statement =
        if load.Replace then
            Replace(load.Table, load.Columns, rows |> List.map (List.map Lit))
        else
            Insert(load.Table, load.Columns, rows |> List.map (List.map Lit), [], load.Ignore)

    recordDiagnostics session false (fun () ->
        try
            executeParsed session statement
        with
        | :? OperationCanceledException -> reraise ()
        | ex -> recoverExecutionError session "LOAD DATA LOCAL INFILE" ex)

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
    match stmt.Ast with
    | None -> handle session (substitutePlaceholders stmt.Sql (values |> List.map valueToSqlLiteral))
    | Some ast ->
        recordDiagnostics session false (fun () ->
            try
                executeParsed session (bindPlaceholders ast values)
            with
            | PlaceholderCountMismatch(expected, got) ->
                session, Err(1210, sprintf "Incorrect arguments to EXECUTE (expected %d, got %d)" expected got)
            | :? OperationCanceledException -> reraise ()
            | ex -> recoverExecutionError session "prepared statement" ex)
