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
    | VBytes bytes -> "X'" + Convert.ToHexString(bytes) + "'"
    | VDate _
    | VDateTime _
    | VString _
    | VJson _ -> "'" + escapeSqlString (v |> toText |> Option.defaultValue "") + "'"

/// Matches `@@var` (system, optionally `session.`/`global.`-qualified) or
/// `@var` (user-defined), optionally aliased, optionally followed by a
/// trailing `LIMIT n` (mysql CLI probes `@@version_comment` this way at
/// connect time). Group 1 is the sigil (`"@"` or `"@@"`), so a single regex
/// covers both — `resolveAtRef` below is the only place that branches on
/// which.
let private atVarItem =
    Regex(@"^(@@?)(?:SESSION\.|GLOBAL\.)?(\w+)(?:\s+AS\s+(\S+))?(?:\s+LIMIT\s+\d+)?$", RegexOptions.IgnoreCase)

/// Resolves one `@@name`/`@name` reference to its current value. A system
/// variable (`@@`) is looked up in `Session.Variables`; a user variable
/// (`@`) in `Session.UserVariables`, where "never `SET`" and "`SET` to
/// NULL" both collapse to `None` via `Option.flatten` — real MySQL reads
/// both back as NULL, and callers here don't need to tell them apart.
let private resolveAtRef (session: Session) (sigil: string) (name: string) : string option =
    if sigil = "@@" then
        lookupVar session name |> Option.flatten
    else
        session.UserVariables |> Map.tryFind (name.ToLowerInvariant()) |> Option.flatten

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
            |> Array.tryFind (fun m -> m.Groups.[1].Value = "@@" && lookupVar session m.Groups.[2].Value |> Option.isNone)

        match unknownSysVar with
        | Some m -> Err(1193, sprintf "Unknown system variable '%s'" m.Groups.[2].Value)
        | None ->
            let cols =
                parsed
                |> Array.map (fun m ->
                    if m.Groups.[3].Success then
                        m.Groups.[3].Value
                    else
                        m.Groups.[1].Value + m.Groups.[2].Value)
                |> Array.toList

            let vals =
                parsed
                |> Array.map (fun m -> resolveAtRef session m.Groups.[1].Value m.Groups.[2].Value)
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
                likeToRegex likeMatch.Groups.[1].Value,
                RegexOptions.IgnoreCase ||| RegexOptions.Singleline
            )
        else
            true

    let rows =
        session.Variables
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

let private likeSuffix (sql: string) : string option =
    let m = Regex.Match(sql, @"LIKE\s+'([^']*)'\s*$", RegexOptions.IgnoreCase)
    if m.Success then Some m.Groups.[1].Value else None

let private stripBackticks (s: string) = s.Trim().Trim('`')

/// Lifts an `InformationSchema.ShowResult` into `QueryResult` — the one spot
/// `Ok`/`Error` become `ResultSet`/`Err`, so every `SHOW ...` case in
/// `runProbe` below reads as plain data flow instead of repeating the match.
let private showResult: InformationSchema.ShowResult -> QueryResult =
    function
    | Ok(cols, rows) -> ResultSet(cols, rows)
    | Error(code, msg) -> Err(code, msg)

let private showTablesRe =
    Regex(@"^SHOW\s+(FULL\s+)?TABLES(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private handleShowTables (session: Session) (sql: string) : QueryResult =
    let m = showTablesRe.Match sql
    let full = m.Groups.[1].Success
    let dbName = if m.Groups.[3].Success then stripBackticks m.Groups.[3].Value else session.Database |> Option.defaultValue defaultDatabase

    InformationSchema.showTables (Session.currentStore session).Catalog dbName full (likeSuffix sql) |> showResult

let private handleShowDatabases (session: Session) (sql: string) : QueryResult =
    InformationSchema.showDatabases (Session.currentStore session).Catalog (likeSuffix sql) |> ResultSet

let private showColumnsRe =
    Regex(@"^SHOW\s+(FULL\s+)?COLUMNS\s+FROM\s+(\S+)(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private describeRe = Regex(@"^(?:DESCRIBE|DESC)\s+(\S+)\s*$", RegexOptions.IgnoreCase)

let private showCreateTableRe = Regex(@"^SHOW\s+CREATE\s+TABLE\s+(\S+)\s*$", RegexOptions.IgnoreCase)

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
let private setNames = Regex(@"^NAMES\s+'?(\w+)'?", RegexOptions.IgnoreCase)

let private setVar =
    Regex(@"^(?:SESSION\s+|GLOBAL\s+|@@(?:SESSION\.|GLOBAL\.)?)?(\w+)\s*=\s*(.+)$", RegexOptions.IgnoreCase)

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
/// Anything else is a literal, unquoted as before.
let private resolveSetRhs (session: Session) (rhs: string) : string option =
    let rhs = rhs.Trim()
    let m = atVarItem.Match rhs

    if m.Success then
        resolveAtRef session m.Groups.[1].Value m.Groups.[2].Value
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
    let mutable parenDepth = 0

    for c in body do
        match quoteChar with
        | Some q when c = q ->
            quoteChar <- None
            current.Append c |> ignore
        | Some _ -> current.Append c |> ignore
        | None ->
            match c with
            | '\'' | '"' ->
                quoteChar <- Some c
                current.Append c |> ignore
            | '(' ->
                parenDepth <- parenDepth + 1
                current.Append c |> ignore
            | ')' ->
                parenDepth <- max 0 (parenDepth - 1)
                current.Append c |> ignore
            | ',' when parenDepth = 0 ->
                parts.Add(current.ToString())
                current.Clear() |> ignore
            | _ -> current.Append c |> ignore

    parts.Add(current.ToString())
    parts |> Seq.map (fun s -> s.Trim()) |> Seq.filter (fun s -> s <> "") |> List.ofSeq

/// One `SET` fragment's parsed effect, applied only once every fragment in
/// the statement has parsed successfully (see `handleSet`) — mirrors real
/// MySQL executing a multi-assignment `SET` all-or-nothing rather than
/// left-to-right with partial effect.
type private SetAction =
    | SetNamesAction of charset: string
    | SetVarAction of name: string * value: string option
    | SetUserVarAction of name: string * value: string option

/// System variables real MySQL accepts an explicit `NULL` for (rather than
/// the 1231 "can't be set to the value of NULL" every other variable
/// raises) — connector handshakes reset the results charset exactly this
/// way. Grows as real clients ask for more.
let private nullableSystemVars = Set.ofList [ "character_set_results" ]

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
        Ok(SetNamesAction namesMatch.Groups.[1].Value)
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
                let name = varMatch.Groups.[1].Value.ToLowerInvariant()

                match resolveSetRhs session varMatch.Groups.[2].Value with
                | Some value when name = "collation_connection" ->
                    match Collation.tryFind value with
                    | Some _ -> Ok(SetVarAction(name, Some value))
                    | None -> Error(Err(1273, sprintf "Unknown collation: '%s'" value))
                | Some value -> Ok(SetVarAction(name, Some value))
                | None when nullableSystemVars.Contains name -> Ok(SetVarAction(name, None))
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
    | SetNamesAction charset ->
        { session with
            Variables =
                session.Variables
                |> Map.add "character_set_client" (Some charset)
                |> Map.add "character_set_connection" (Some charset)
                |> Map.add "character_set_results" (Some charset) }
    | SetVarAction(name, value) ->
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
    | Ok actions -> (actions |> List.fold applySetAction session), Affected 0UL

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

/// MySqlConnector's real `BeginTransaction[Async]` handshake explicitly
/// selects REPEATABLE READ before it sends START TRANSACTION. That is the
/// isolation model FSDB's private transaction snapshot actually implements,
/// so accepting this exact setting is truthful. Other isolation levels stay
/// unsupported rather than being acknowledged without matching semantics.
let private setRepeatableRead =
    Regex(
        @"^SET\s+(?:SESSION\s+)?TRANSACTION\s+ISOLATION\s+LEVEL\s+REPEATABLE\s+READ$",
        RegexOptions.IgnoreCase
    )

/// Commits the open transaction (if any) by merging its snapshot catalog
/// back into the shared store's (`Storage.mergeCatalogInto`, a CAS-safe
/// three-way merge against whatever the live catalog is *right now* — not a
/// stale copy from BEGIN — so a concurrent write to another database, or an
/// untouched table in this one, during the transaction's lifetime survives)
/// — a no-op, matching real MySQL, if there isn't one open.
let private commitSession (session: Session) : Session =
    match session.Tx with
    | Some tx ->
        try
            Storage.mergeCatalogInto session.Store tx.BaseCatalog tx.Snapshot.Catalog
            Storage.commitTransactionEvents session.Store tx.Snapshot
        finally
            tx.GateLease |> Option.iter (fun lease -> lease.Dispose())

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
        try
            Storage.bumpAutoIncrementsInto session.Store tx.Snapshot.Catalog
        finally
            tx.GateLease |> Option.iter (fun lease -> lease.Dispose())
    | None -> ()

    { session with Tx = None }

/// Starts a new transaction with a provisional snapshot. The real snapshot
/// is rebound under the transaction gate by `startTransactionStatement` at
/// the first database statement, matching InnoDB's default deferred
/// consistent-snapshot timing. MySQL implicitly commits an already-open
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
                  GateLease = None
                  Savepoints = Map.empty } }

/// Establishes the transaction's actual repeatable-read snapshot at its
/// first database statement and holds the coarse write/transaction gate for
/// the rest of its lifetime. BEGIN itself remains non-blocking, so several
/// clients can enter a transaction concurrently; only their first real
/// reads/writes serialize. Re-seeding here also ensures a transaction that
/// sat idle after BEGIN sees commits that completed before its first read,
/// matching InnoDB's default consistent-snapshot timing.
let startTransactionStatement (session: Session) : Session =
    match session.Tx with
    | Some tx when tx.GateLease.IsNone ->
        let lease = Storage.enterTransactionGate session.Store (session.Database |> Option.defaultValue defaultDatabase) defaultLockWaitTimeout

        try
            let baseCatalog = session.Store.Catalog
            let snapshot = Storage.beginTransactionSnapshot session.Store

            let savepoints =
                tx.Savepoints
                |> Map.map (fun _ (_, eventCount) -> baseCatalog, eventCount)

            { session with
                Tx =
                    Some
                        { Snapshot = snapshot
                          BaseCatalog = baseCatalog
                          GateLease = Some lease
                          Savepoints = savepoints } }
        with _ ->
            lease.Dispose()
            reraise ()
    | _ -> session

/// Rolls an abandoned connection's transaction back and, critically,
/// releases its transaction gate. The lease is idempotent, so calling this
/// with a session snapshot that was already committed is harmless.
let closeSession (session: Session) : unit =
    rollbackSession session |> ignore

let private savepointNotFound (name: string) : QueryResult =
    Err(1305, sprintf "SAVEPOINT %s does not exist" name)

/// `SAVEPOINT name` outside an explicit transaction implicitly starts one,
/// matching real MySQL.
let private savepoint (name: string) (session: Session) : Session * QueryResult =
    let session = if session.Tx.IsNone then beginTransaction session else session

    match session.Tx with
    | Some tx ->
        let eventCount = tx.Snapshot.PendingEvents |> Option.map (fun b -> b.Count) |> Option.defaultValue 0
        { session with Tx = Some { tx with Savepoints = Map.add name (tx.Snapshot.Catalog, eventCount) tx.Savepoints } }, Affected 0UL
    | None -> session, Affected 0UL // unreachable: beginTransaction always sets Tx

let private rollbackToSavepoint (name: string) (session: Session) : Session * QueryResult =
    match session.Tx |> Option.bind (fun tx -> Map.tryFind name tx.Savepoints |> Option.map (fun seed -> tx, seed)) with
    | Some(tx, (catalog, eventCount)) ->
        Storage.setCatalog tx.Snapshot catalog
        // Drop every event this transaction buffered after the savepoint —
        // otherwise a WAL replay would apply writes the savepoint rollback
        // just undid.
        tx.Snapshot.PendingEvents
        |> Option.iter (fun buffer -> if buffer.Count > eventCount then buffer.RemoveRange(eventCount, buffer.Count - eventCount))

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
    let session = { session with Variables = Map.add "autocommit" (Some value) session.Variables }

    let session =
        if value = "0" then
            (if session.Tx.IsNone then beginTransaction session else session)
        else
            commitSession session

    session, Affected 0UL

/// The function registry for one statement: `Functions.builtins`, then
/// `session.CustomFunctions` (an embedding `Db`'s `registerScalar`/
/// `registerAggregate` calls — free to override a built-in), then the
/// session-dependent entries that can't be plain `Value list -> Value`
/// closures until they're given a session to close over (`DATABASE()`
/// reads `session.Database`, `LAST_INSERT_ID()` reads `session.LastGeneratedId`
/// — deliberately not `LastInsertId`, see that field's doc for why the two
/// diverge — `VERSION()` just reuses the same `@@version` value `SELECT
/// @@version` already serves) — those go last so they always win.
let private registryFor (session: Session) : Functions.Registry =
    let withCustom =
        session.CustomFunctions.Scalars
        |> Map.fold (fun r name fn -> Functions.registerScalar name fn r) Functions.builtins
        |> fun r -> session.CustomFunctions.Aggregates |> Map.fold (fun r name fn -> Functions.registerAggregate name fn r) r

    let databaseFn _ = session.Database |> Option.map VString |> Option.defaultValue VNull

    withCustom
    |> Functions.registerScalar "DATABASE" databaseFn
    |> Functions.registerScalar "SCHEMA" databaseFn
    |> Functions.registerScalar "LAST_INSERT_ID" (fun _ -> VInt session.LastGeneratedId)
    |> Functions.registerScalar
        "VERSION"
        (fun _ -> lookupVar session "version" |> Option.flatten |> Option.map VString |> Option.defaultValue VNull)
    |> Functions.registerScalar "CONNECTION_ID" (fun _ -> VInt(int64 session.ConnectionId))
    |> Functions.registerScalar "CURRENT_USER" (fun _ -> VString "fsdb@localhost")
    |> Functions.registerScalar "USER" (fun _ -> VString "fsdb@localhost")

/// Parses and executes anything that isn't one of the text-probe special
/// cases above. A parse failure that also looks like a `SELECT @@...`/
/// `SELECT @...` falls back to the `@`-probe path — tried only *after* the
/// real parser, so a query that merely contains the text `@` somewhere
/// (inside a string literal, e.g. `WHERE email = 'a@b.com'`) parses
/// normally instead of being hijacked into the probe path and rejected.
/// Anything else is a 1064 syntax error with SQLSTATE 42000 (the mapping
/// `errPayload` already has for that code).
let private executeStatement (session: Session) (sql: string) (upper: string) : Session * QueryResult =
    match Parser.parse sql with
    | Result.Ok stmt ->
        let execute session =
            let store = Session.currentStore session
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
            let registry = registryFor session
            let dbName = session.Database |> Option.defaultValue defaultDatabase

            // `SELECT`/`UNION` go through `Executor`'s type-preserving entry
            // points instead of the plain `execute` every other statement uses
            // — those are the only two statement kinds that reach the wire as
            // a `ResultSet`, and only they still have the typed `Value`s
            // (rather than already-rendered text) `columnTypes` needs. See
            // `Session.LastResultColumnTypes`'s doc for why this rides along
            // on `session` instead of widening this function's own return type.
            let lastInsertId, lastGeneratedId, result, columnTypes =
                match stmt with
                | Select select ->
                    let result, types = Executor.runTopLevelSelect store registry dbName select
                    session.LastInsertId, session.LastGeneratedId, result, types
                | Union(first, rest, orderBy, limit, offset) ->
                    let result, types, _ = Executor.runUnionStmt store registry dbName first rest orderBy limit offset
                    session.LastInsertId, session.LastGeneratedId, result, types
                | _ ->
                    let (lastInsertId, lastGeneratedId), result =
                        Executor.execute store registry dbName (session.LastInsertId, session.LastGeneratedId) stmt

                    lastInsertId, lastGeneratedId, result, []

            { session with
                LastInsertId = lastInsertId
                LastGeneratedId = lastGeneratedId
                LastResultColumnTypes = columnTypes },
            result

        match session.Tx with
        | Some _ ->
            // `startTransactionStatement` acquires this database's
            // transaction gate on a transaction's first real statement and
            // returns it embedded in the new `Session` it hands back — the
            // *only* place that lease is referenced until `execute` itself
            // returns. If `execute` throws (a genuine bug, or
            // `Storage.queryCancellation` unwinding a killed client's query
            // — see `Server.watchForDisconnect`), that new `Session` is
            // never returned to anyone: every catcher above this only has
            // the *original*, pre-statement `session`, whose `Tx.GateLease`
            // doesn't reflect a lease newly acquired by *this* call.
            // Disposing it here — unconditionally, whether newly acquired
            // or already held from an earlier statement in the same
            // transaction — is safe (`TransactionGateLease.Dispose` is
            // idempotent) and guarantees the lease is never simply lost.
            // This alone isn't the whole fix, though: `handle`'s exception
            // handlers still need to abort the *whole* transaction (not
            // just free its gate), since a `Session` that keeps a stale
            // `BaseCatalog`/`Snapshot` alive after its gate was released
            // mid-transaction could otherwise still reach a later COMMIT —
            // see `abortTransactionGate`'s doc.
            let started = startTransactionStatement session

            try
                execute started
            with _ ->
                started.Tx |> Option.iter (fun tx -> tx.GateLease |> Option.iter (fun lease -> lease.Dispose()))
                reraise ()
        | None ->
            match stmt with
            | Select _
            | Union _
            | Explain _ -> execute session
            | _ ->
                use _gate = Storage.enterTransactionGate session.Store (session.Database |> Option.defaultValue defaultDatabase) defaultLockWaitTimeout
                execute session
    | Result.Error _ when upper.StartsWith "SELECT" && upper.Contains "@" ->
        { session with LastResultColumnTypes = [] }, handleAtVarSelect session sql
    | Result.Error _ -> { session with LastResultColumnTypes = [] }, syntaxError sql

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
    | SetRepeatableRead
    | SetVar
    | RollbackTo of savepoint: string
    | Begin
    | Commit
    | Rollback
    | Savepoint of name: string
    | Release of name: string
    | Use of dbName: string
    | ShowVariables
    | ShowWarnings
    | ShowDatabases
    | ShowTableStatus
    | ShowTables
    | ShowCreate of name: string
    | ShowColumns of full: bool * name: string * dbOverride: string option
    | Describe of name: string
    | ShowIndex of name: string * dbOverride: string option

/// The one ordered list of text-probed forms — matching `Probe`'s cases
/// exactly (the compiler enforces `runProbe` covers every one of them), so
/// COM_QUERY (`dispatch`) and COM_STMT_PREPARE (`prepareStatement`) can
/// never disagree about which statements are text-probed vs. parsed the way
/// two independently-written predicates could drift.
let private tryProbe (sql: string) (upper: string) : Probe option =
    if setAutocommit.IsMatch sql then
        Some(SetAutocommit((setAutocommit.Match sql).Groups.[1].Value))
    elif setRepeatableRead.IsMatch sql then
        Some SetRepeatableRead
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
    elif upper.StartsWith "SHOW VARIABLES" then
        Some ShowVariables
    elif upper = "SHOW WARNINGS" then
        Some ShowWarnings
    elif upper.StartsWith "SHOW DATABASES" then
        Some ShowDatabases
    elif upper.StartsWith "SHOW TABLE STATUS" then
        Some ShowTableStatus
    elif upper.StartsWith "SHOW TABLES" || upper.StartsWith "SHOW FULL TABLES" then
        Some ShowTables
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
        | ShowColumns _
        | Describe _
        | ShowIndex _ -> startTransactionStatement session
        | _ -> session

    match probe with
    | SetAutocommit value -> handleSetAutocommit value session
    | SetRepeatableRead ->
        { session with Variables = Map.add "transaction_isolation" (Some "REPEATABLE-READ") session.Variables }, Affected 0UL
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
    | ShowVariables -> session, handleShowVariables session sql
    | ShowWarnings -> session, ResultSet([ "Level"; "Code"; "Message" ], [])
    | ShowDatabases -> session, handleShowDatabases session sql
    | ShowTableStatus -> session, handleShowTableStatus session sql
    | ShowTables -> session, handleShowTables session sql
    | ShowCreate name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        session, InformationSchema.showCreateTable (Session.currentStore session).Catalog dbName table |> showResult
    | ShowColumns(full, name, dbOverride) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        let dbName = dbOverride |> Option.map stripBackticks |> Option.defaultValue dbName
        session, InformationSchema.showColumns (Session.currentStore session).Catalog full dbName table (likeSuffix sql) |> showResult
    | Describe name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        session, InformationSchema.showColumns (Session.currentStore session).Catalog false dbName table None |> showResult
    | ShowIndex(name, dbOverride) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        let dbName = dbOverride |> Option.map stripBackticks |> Option.defaultValue dbName
        session, InformationSchema.showIndex (Session.currentStore session).Catalog dbName table |> showResult

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

    if (tryProbe trimmed upper).IsSome then
        Result.Ok placeholderCount
    else
        match Parser.parse probeSql with
        | Result.Ok _ -> Result.Ok placeholderCount
        | Result.Error _ ->
            match syntaxError sql with
            | Err(code, msg) -> Result.Error(code, msg)
            | _ -> Result.Error(1064, "syntax error")

let private dispatch (session: Session) (rawSql: string) : Session * QueryResult =
    let sql = (Parser.stripVersionComments rawSql).Trim().TrimEnd(';').Trim()

    // A mysqldump preamble/postamble is a run of `/*!NNNNN ... */;` lines;
    // once the version comment above strips down to nothing (or this was a
    // plain `/* ... */`/`-- ...` comment to begin with), what's left is a
    // no-op, same as real MySQL's `Query OK, 0 rows affected` for it —
    // not a syntax error.
    if Parser.isBlank sql then
        session, Affected 0UL
    else
        let upper = sql.ToUpperInvariant()

        match tryProbe sql upper with
        | Some probe ->
            // Every probe-handled form (SHOW/SET/session-variable SELECT/...)
            // is its own small synthetic `ResultSet` of plain strings — none of
            // them go through `executeStatement`'s typed path, so clear
            // whatever `LastResultColumnTypes` a previous statement on this
            // session left behind rather than risk it surviving (via `Server`'s
            // VAR_STRING-length-mismatch fallback, a same-column-count
            // coincidence is all it'd take) onto an unrelated resultset.
            let session, result = runProbe session sql probe
            { session with LastResultColumnTypes = [] }, result
        | None -> executeStatement session sql upper

/// Disposes `session.Tx`'s transaction gate lease, if it holds one — called
/// only when a statement failed badly enough to abort the whole transaction
/// (see `handle`'s exception handlers below), never on a normal COMMIT/
/// ROLLBACK (`commitSession`/`rollbackSession` already own that). Freeing
/// the gate isn't itself the fix for a mid-transaction failure — a
/// transaction's whole safety argument is that it holds its database's gate
/// *continuously* from its first real statement through COMMIT/ROLLBACK, so
/// `mergeCatalogInto`'s three-way merge (`BaseCatalog`, captured at BEGIN,
/// against the live catalog *right now*) never races a concurrent
/// committer. A `Session` that frees its gate mid-transaction but keeps
/// `Tx = Some` alive — its `BaseCatalog`/`Snapshot` now stale — could still
/// reach a later COMMIT and silently clobber whatever another transaction
/// committed to this database in the gap: a real lost update, not just a
/// wasted retry. `handle`'s callers always pair this with `Tx = None`, so
/// the broken transaction can never be resumed or committed, only quietly
/// forgotten (matching real MySQL's "current transaction has been rolled
/// back" after a fatal statement error).
let private abortTransactionGate (session: Session) : unit =
    session.Tx |> Option.iter (fun tx -> tx.GateLease |> Option.iter (fun lease -> lease.Dispose()))

/// No SQL engine failure should ever escape as a raw .NET exception — the
/// only two paths into `dispatch` (the parser, well guarded, and
/// `Storage.coerceValue`'s numeric casts, which are not) both funnel into
/// `Executor`, and `Server`'s connection loop only catches
/// `PacketTooLargeException`, so anything else here would otherwise unwind
/// straight to the socket read loop and silently drop the connection with
/// no ERR packet. Verified reachable: `INSERT INTO t VALUES (1e300)` into a
/// DECIMAL column throws `OverflowException` from `decimal d`.
///
/// `Storage.LockWaitTimeout` gets its own real MySQL error code (1205)
/// rather than falling into the generic 1105 below — a stuck transaction
/// elsewhere should look to the client like an ordinary, retryable lock
/// wait timeout, not an internal error; the transaction itself is untouched
/// (the gate was never acquired — `Storage.enterTransactionGate` only
/// raises this when its `Wait` timed out), so unlike the two handlers
/// below, this one leaves `session.Tx` exactly as it was.
let handle (session: Session) (rawSql: string) : Session * QueryResult =
    try
        match dispatch session rawSql with
        | _, Err(code, msg) as result ->
            Log.diagnostic "fsdb: ERR %d %s -- query: %s" code msg rawSql
            result
        | result -> result
    with
    | Storage.LockWaitTimeout dbName ->
        Log.diagnostic "fsdb: ERR 1205 lock wait timeout on database %s -- query: %s" dbName rawSql
        session, Err(1205, "Lock wait timeout exceeded; try restarting transaction")
    // A killed client mid-query (`Storage.queryCancellation`, armed by
    // `Server.withCancellationWatch`) — not an internal error to report
    // back to a client that's already gone, so this re-raises rather than
    // folding into the generic `Err(1105, ...)` below: `Server`'s command
    // loop catches it, skips the now-pointless reply, and logs the one
    // clean line itself. `abortTransactionGate` still runs first — see its
    // doc for why freeing the gate here (not just on the newly-acquired
    // path `executeStatement` already covers) matters even though nothing
    // is returned.
    | :? OperationCanceledException ->
        abortTransactionGate session
        reraise ()
    | ex ->
        Log.diagnostic "fsdb: EXN %s -- query: %s" ex.Message rawSql
        abortTransactionGate session
        { session with Tx = None }, Err(1105, sprintf "Internal error: %s" ex.Message) // ER_UNKNOWN_ERROR
