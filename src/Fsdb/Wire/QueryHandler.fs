/// Session-aware command dispatch from SQL text to parser and executor.
module Fsdb.QueryHandler

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Runtime.CompilerServices
open System.Text
open System.Text.RegularExpressions
open Fsdb.Engine
open Fsdb.Value
open Fsdb.Ast
open Fsdb.Session
open Fsdb.Storage
open Fsdb.InformationSchema
open Fsdb.Sql

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

let private parserError (sql: string) (detail: string) =
    let temporal =
        Regex.Match(
            detail,
            @"(?:^|\r?\n)\s*(?<message>Incorrect (?:DATE|DATETIME|TIME) value: '[^\r\n]*')\s*$"
        )

    if temporal.Success then Err(1525, temporal.Groups.["message"].Value) else syntaxError sql

/// Raw map lookup: the outer `option` is "is `name` even a known variable"
/// (unchanged since `Session.Variables` grew NULL-capable values) — `None`
/// there is the true "unknown variable" case (1193 below), while `Some
/// None` is a known variable currently holding SQL NULL. Callers that only
/// want the value (collapsing "unknown" and "known but NULL" the way
/// `SELECT @@x` does for both sigils) flatten it themselves.
let private lookupVar (session: Session) (name: string) : string option option =
    session.Variables |> Map.tryFind (name.ToLowerInvariant())

let private completeResultMetadata (session: Session) (result: QueryResult) (metadata: ColumnMetadata list) =
    match result with
    | ResultSet(columns, _) when metadata.Length <> columns.Length ->
        let collationId =
            lookupVar session "collation_connection"
            |> Option.flatten
            |> Option.bind (fun name -> Collation.idAndSortlen |> Map.tryFind (name.ToLowerInvariant()))
            |> Option.map (fst >> uint16)

        List.replicate
            columns.Length
            { Value.columnMetadata TypeVarString with
                CollationId = collationId }
    | _ -> metadata

type private TerminalResult =
    | TerminalAffected of uint64
    | TerminalResultSet of string option list list
    | TerminalError of int * string

let rec private terminalResult =
    function
    | MultipleResults results ->
        results
        |> List.tryLast
        |> Option.map (fst >> terminalResult)
        |> Option.defaultValue (TerminalAffected 0UL)
    | Affected count -> TerminalAffected count
    | ResultSet(_, rows) -> TerminalResultSet rows
    | Err(code, message) -> TerminalError(code, message)

let rec private terminalErrorInfo result =
    match Executor.errorInfo result with
    | Some error -> Some error
    | None ->
        match result with
        | MultipleResults results -> results |> List.tryLast |> Option.bind (fst >> terminalErrorInfo)
        | _ -> None

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
let private placeholderPositionsWithOptions (options: Parser.ParserOptions) (sql: string) : int list =
    let n = sql.Length
    let positions = ResizeArray<int>()
    let mutable i = 0

    while i < n do
        match sql.[i] with
        | ('\'' | '"' | '`') as quote ->
            let allowBackslashEscape = quote <> '`' && not options.NoBackslashEscapes
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

let placeholderPositions (sql: string) : int list =
    placeholderPositionsWithOptions Parser.defaultOptions sql

/// Replaces each top-level `?` in `sql` (per `placeholderPositions`) with
/// the corresponding entry of `literals`, in the order both appear.
/// COM_STMT_EXECUTE's own bound-parameter count check guarantees the
/// lengths already match — this is the one substitution path prepared
/// statements without an AST use.
exception PlaceholderCountMismatch of expected: int * got: int

let private substitutePlaceholdersWithOptions
    (options: Parser.ParserOptions)
    (sql: string)
    (literals: string list)
    : string =
    let positions = placeholderPositionsWithOptions options sql

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

let substitutePlaceholders (sql: string) (literals: string list) : string =
    substitutePlaceholdersWithOptions Parser.defaultOptions sql literals

/// Renders a bound parameter value as a SQL literal safe to splice into the
/// stored statement text. Default mode escapes backslash, quotes, and line
/// endings with backslashes; `NO_BACKSLASH_ESCAPES` doubles quotes and keeps
/// every other character literal. In default mode, CR/LF use `\r`/`\n`, not
/// left as raw bytes — `Parser.quotedStringChar` already round-trips those
/// two escapes back to CR/LF, but a raw CR spliced into the SQL text gets
/// silently normalized away by FParsec's CharStream on re-parse (it treats
/// bare `\r`/`\r\n` as line endings), corrupting any multi-line value
/// (e.g. an HTML textarea's CRLF body) on the way through a prepared
/// statement.
let private escapeSqlString (options: Parser.ParserOptions) (s: string) : string =
    if options.NoBackslashEscapes then
        s.Replace("'", "''")
    else
        s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "\\r").Replace("\n", "\\n")

let private valueToSqlLiteralWithOptions (options: Parser.ParserOptions) (v: Value) : string =
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
    | VJson _ -> "'" + escapeSqlString options (v |> toText |> Option.defaultValue "") + "'"
    | VGeometry geometry -> "ST_GeomFromWKB(X'" + Convert.ToHexString(geometryToWkb geometry) + "', " + string geometry.Srid + ")"

let valueToSqlLiteral (v: Value) : string =
    valueToSqlLiteralWithOptions Parser.defaultOptions v

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

let private numericSystemVariables =
    Set.ofList
        [ "auto_increment_increment"
          "autocommit"
          "cte_max_recursion_depth"
          "foreign_key_checks"
          "group_concat_max_len"
          "interactive_timeout"
          "innodb_buffer_pool_size"
          "local_infile"
          "lower_case_table_names"
          "max_allowed_packet"
          "max_connections"
          "max_heap_table_size"
          "max_prepared_stmt_count"
          "net_read_timeout"
          "net_write_timeout"
          "performance_schema"
          "query_cache_size"
          "sql_notes"
          "tmp_table_size"
          "transaction_read_only"
          "tx_read_only"
          "unique_checks"
          "wait_timeout" ]

let private systemVariableValue (name: string) =
    function
    | Some(value: string) when Set.contains (name.ToLowerInvariant()) numericSystemVariables ->
        match UInt64.TryParse value with
        | true, number -> Some(VUInt number)
        | false, _ -> Some(VString value)
    | Some value -> Some(VString value)
    | None -> None

let private expressionVariablesFor (session: Session) (userVariables: Map<string, Value>) : Executor.VariableContext =
    { UserVariables = ref userVariables
      ReadSystemVariable =
        fun scope name ->
            match lookupAtRef session "@@" scope name with
            | Some value -> Ok(systemVariableValue name value)
            | None -> Error(1193, sprintf "Unknown system variable '%s'" name)
      MaxUserVariables = maxUserVariables }

let private expressionVariables (session: Session) = expressionVariablesFor session session.UserVariables

let private accountOf (session: Session) = Auth.account session.User session.AccountHost

let private canInspectRoutine (session: Session) schema definer =
    match Auth.tryParseAccount definer with
    | None -> false
    | Some owner ->
        let viewer = accountOf session

        Auth.sameAccount viewer owner
        || Auth.hasGlobalPrivForAccount session.Store viewer "SELECT"
        || (Auth.checkForAccount session.Store viewer [ "ALTER ROUTINE", Auth.OnDb schema ] |> Result.isOk)

let private canSeeRoutine session schema definer =
    canInspectRoutine session schema definer
    || (Auth.checkForAccount session.Store (accountOf session) [ "EXECUTE", Auth.OnDb schema ] |> Result.isOk)

type private AdvisoryLock =
    { Owner: int
      Count: int }

let private advisoryLocksByStore =
    ConditionalWeakTable<obj, System.Collections.Generic.Dictionary<string, AdvisoryLock>>()

let private advisoryLocks (session: Session) =
    advisoryLocksByStore.GetValue(
        session.Store.Lock,
        fun _ -> System.Collections.Generic.Dictionary(StringComparer.Ordinal)
    )

let private advisoryLockName = function
    | VNull -> None
    | value ->
        let name = Value.toText value |> Option.defaultValue ""

        if name.EnumerateRunes() |> Seq.length > 64 then
            raise (Functions.SqlError(3057, "User-level lock name is too long"))

        Some name

let private getAdvisoryLock (session: Session) = function
    | [ name; timeout ] ->
        match advisoryLockName name, timeout with
        | None, _
        | _, VNull -> VNull
        | Some name, timeout ->
            let seconds = Value.toDouble timeout

            if Double.IsNaN seconds then
                VNull
            else
                let infinite = seconds < 0.0 || Double.IsPositiveInfinity seconds
                let deadline = Stopwatch.StartNew()
                let locks = advisoryLocks session

                lock locks (fun () ->
                    let rec acquire () =
                        match locks.TryGetValue name with
                        | false, _ ->
                            locks.[name] <- { Owner = session.ConnectionId; Count = 1 }
                            VInt 1L
                        | true, current when current.Owner = session.ConnectionId ->
                            locks.[name] <- { current with Count = current.Count + 1 }
                            VInt 1L
                        | _ when not infinite && deadline.Elapsed.TotalSeconds >= seconds -> VInt 0L
                        | _ ->
                            Storage.queryCancellation.Value.ThrowIfCancellationRequested()

                            let waitMilliseconds =
                                if infinite then 50 else max 1 (min 50 (int ((seconds - deadline.Elapsed.TotalSeconds) * 1000.0)))

                            Threading.Monitor.Wait(locks, waitMilliseconds) |> ignore
                            acquire ()

                    acquire ())
    | _ -> raise (Functions.SqlError(1582, "Incorrect parameter count in the call to native function 'GET_LOCK'"))

let private releaseAdvisoryLock (session: Session) = function
    | [ value ] ->
        match advisoryLockName value with
        | None -> VNull
        | Some name ->
            let locks = advisoryLocks session

            lock locks (fun () ->
                match locks.TryGetValue name with
                | false, _ -> VNull
                | true, current when current.Owner <> session.ConnectionId -> VInt 0L
                | true, current when current.Count > 1 ->
                    locks.[name] <- { current with Count = current.Count - 1 }
                    VInt 1L
                | true, _ ->
                    locks.Remove name |> ignore
                    Threading.Monitor.PulseAll locks
                    VInt 1L)
    | _ -> raise (Functions.SqlError(1582, "Incorrect parameter count in the call to native function 'RELEASE_LOCK'"))

let private releaseAllAdvisoryLocks (session: Session) =
    let locks = advisoryLocks session

    lock locks (fun () ->
        let owned =
            locks
            |> Seq.filter (fun pair -> pair.Value.Owner = session.ConnectionId)
            |> Seq.map (fun pair -> pair.Key, pair.Value.Count)
            |> List.ofSeq

        owned |> List.iter (fst >> locks.Remove >> ignore)

        if not owned.IsEmpty then
            Threading.Monitor.PulseAll locks

        owned |> List.sumBy snd)

let private inspectAdvisoryLock (session: Session) inUse = function
    | [ value ] ->
        match advisoryLockName value with
        | None -> VNull
        | Some name ->
            let locks = advisoryLocks session

            lock locks (fun () ->
                match locks.TryGetValue name with
                | true, current when inUse -> VInt(int64 current.Owner)
                | true, _ -> VInt 0L
                | false, _ when inUse -> VNull
                | false, _ -> VInt 1L)
    | _ ->
        let name = if inUse then "IS_USED_LOCK" else "IS_FREE_LOCK"
        raise (Functions.SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" name))

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
            session.CustomFunctions.ScalarMetadata
            |> Map.fold
                (fun registry name metadata ->
                    match Functions.lookup name registry with
                    | Some scalar -> Functions.registerScalarWithMetadata name metadata scalar registry
                    | None -> registry)
                current
        |> fun current ->
            session.CustomFunctions.Aggregates
            |> Map.fold (fun registry name fn -> Functions.registerAggregate name fn registry) current
        |> fun current ->
            session.CustomFunctions.Extensions
            |> Map.fold (fun registry name extension -> collapseExtension name extension registry) current
        |> fun current -> { current with Extensions = session.CustomFunctions.Extensions }

    let database _ = session.Database |> Option.map VString |> Option.defaultValue VNull
    let blockEncryptionMode = lookupVar session "block_encryption_mode" |> Option.flatten |> Option.defaultValue "aes-128-ecb"
    let timeLocale =
        lookupVar session "lc_time_names"
        |> Option.flatten
        |> Option.bind Functions.tryTimeLocale
        |> Option.defaultValue Functions.defaultTimeLocale

    let loginUser = if session.LoginUser = "" then session.User else session.LoginUser

    registry
    |> Functions.registerTextScalar "AES_ENCRYPT" (fun index -> index < 2) (Functions.aesEncrypt blockEncryptionMode)
    |> Functions.registerTextScalar "AES_DECRYPT" (fun index -> index < 2) (Functions.aesDecrypt blockEncryptionMode)
    |> Functions.registerScalar "DATE_FORMAT" (Functions.dateFormatFn timeLocale)
    |> Functions.registerScalar "DAYNAME" (Functions.dayNameFn timeLocale)
    |> Functions.registerScalar "MONTHNAME" (Functions.monthNameFn timeLocale)
    |> Functions.registerScalar "FROM_UNIXTIME" (Functions.fromUnixTimeFn timeLocale)
    |> Functions.registerScalar "DATABASE" database
    |> Functions.registerScalar "SCHEMA" database
    |> Functions.registerScalar "LAST_INSERT_ID" (fun _ -> VInt session.LastGeneratedId)
    |> Functions.registerScalar "ROW_COUNT" (fun _ -> VInt session.LastRowCount)
    |> Functions.registerScalar "FOUND_ROWS" (fun _ -> VUInt session.FoundRows)
    |> Functions.registerScalar
        "VERSION"
        (fun _ -> lookupVar session "version" |> Option.flatten |> Option.map VString |> Option.defaultValue VNull)
    |> Functions.registerScalar "CONNECTION_ID" (fun _ -> VInt(int64 session.ConnectionId))
    |> Functions.registerScalar "GET_LOCK" (getAdvisoryLock session)
    |> Functions.registerScalar "RELEASE_LOCK" (releaseAdvisoryLock session)
    |> Functions.registerScalar "IS_FREE_LOCK" (inspectAdvisoryLock session false)
    |> Functions.registerScalar "IS_USED_LOCK" (inspectAdvisoryLock session true)
    |> Functions.registerScalar "RELEASE_ALL_LOCKS" (fun args ->
        if args.IsEmpty then
            VInt(int64 (releaseAllAdvisoryLocks session))
        else
            raise (Functions.SqlError(1582, "Incorrect parameter count in the call to native function 'RELEASE_ALL_LOCKS'")))
    |> Functions.registerScalar "CURRENT_USER" (fun _ -> VString(Auth.formatAccount (accountOf session)))
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

let private stripIdentifierQuotes (s: string) =
    let text = s.Trim()

    if
        text.Length >= 2
        && ((text.[0] = '`' && text.[text.Length - 1] = '`')
            || (text.[0] = '"' && text.[text.Length - 1] = '"'))
    then
        text.Substring(1, text.Length - 2)
    else
        text

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

let private showCreateTriggerRe =
    Regex(@"^SHOW\s+CREATE\s+TRIGGER\s+(\S+)\s*;?$", RegexOptions.IgnoreCase)

let private showTriggersRe =
    Regex(@"^SHOW\s+TRIGGERS(?:\s+(?:FROM|IN)\s+(\S+))?", RegexOptions.IgnoreCase)

let private showEventsRe =
    Regex(@"^SHOW\s+EVENTS(?:\s+(?:FROM|IN)\s+(\S+))?", RegexOptions.IgnoreCase)

let private showRoutineStatusRe =
    Regex(@"^SHOW\s+(PROCEDURE|FUNCTION)\s+STATUS(?:\s|$)", RegexOptions.IgnoreCase)

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
    let dbName = if m.Groups.[3].Success then stripIdentifierQuotes m.Groups.[3].Value else session.Database |> Option.defaultValue defaultDatabase

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

let private overlayCatalog (catalog: Storage.Catalog) (overlay: Storage.Catalog) =
    overlay
    |> Map.fold (fun result db tables ->
        result
        |> Map.change db (fun current ->
            current
            |> Option.defaultValue Map.empty
            |> fun existing -> Some(Map.fold (fun acc name table -> Map.add name table acc) existing tables))) catalog

/// The catalog `SHOW COLUMNS`/`DESCRIBE`/`SHOW CREATE TABLE`/`SHOW INDEX`
/// should resolve against. Session-local and virtual tables aren't in the
/// shared catalog, but clients still introspect them through these forms.
let private catalogWithOverlay (session: Session) (dbName: string) (table: string) : Storage.Catalog =
    let store = Session.currentStore session
    let catalog = overlayCatalog store.Catalog session.TemporaryCatalog

    if String.Equals(dbName, Storage.defaultDatabase, StringComparison.OrdinalIgnoreCase) then
        match Map.tryFind (table.ToLowerInvariant()) store.VirtualTables with
        | Some vt ->
            catalog
            |> Map.change (dbName.ToLowerInvariant()) (fun current ->
                current
                |> Option.defaultValue Map.empty
                |> Map.add (table.ToLowerInvariant()) (Storage.virtualTableStub vt)
                |> Some)
        | None -> catalog
    else
        catalog

let private showColumnsRe =
    Regex(@"^SHOW\s+(FULL\s+)?COLUMNS\s+FROM\s+(\S+)(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private showColumnsFieldFilter (sql: string) =
    let matched =
        Regex.Match(
            sql,
            @"\s+WHERE\s+`?Field`?\s*=\s*(?<value>'(?:\\.|''|[^'])*')\s*$",
            RegexOptions.IgnoreCase
        )

    if not matched.Success then
        None
    else
        match Parser.parseExpression matched.Groups.["value"].Value with
        | Ok(Lit(VString field)) -> Some field
        | _ -> None

let private describeRe = Regex(@"^(?:DESCRIBE|DESC)\s+(\S+)\s*$", RegexOptions.IgnoreCase)

let private showCreateTableRe = Regex(@"^SHOW\s+CREATE\s+TABLE\s+(\S+)\s*$", RegexOptions.IgnoreCase)
let private showCreateViewRe = Regex(@"^SHOW\s+CREATE\s+VIEW\s+(\S+)\s*$", RegexOptions.IgnoreCase)

let private showIndexRe =
    Regex(@"^SHOW\s+(?:INDEX|INDEXES|KEYS)\s+(?:FROM|IN)\s+(\S+)(\s+(?:FROM|IN)\s+(\S+))?", RegexOptions.IgnoreCase)

let private showIndexTextFilter (column: string) (sql: string) =
    let matched =
        Regex.Match(
            sql,
            sprintf @"(?:\s+WHERE|\s+AND)\s+`?%s`?\s*=\s*(?<value>'(?:\\.|''|[^'])*')" (Regex.Escape column),
            RegexOptions.IgnoreCase
        )

    if not matched.Success then
        None
    else
        match Parser.parseExpression matched.Groups.["value"].Value with
        | Ok(Lit(VString value)) -> Some value
        | _ -> None

let private showTableStatusRe = Regex(@"^SHOW\s+TABLE\s+STATUS(\s+FROM\s+(\S+))?", RegexOptions.IgnoreCase)

let private handleShowTableStatus (session: Session) (sql: string) : QueryResult =
    let m = showTableStatusRe.Match sql
    let dbName = if m.Groups.[2].Success then stripIdentifierQuotes m.Groups.[2].Value else session.Database |> Option.defaultValue defaultDatabase

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
    Regex(@"^(SESSION\s+|GLOBAL\s+|@@SESSION\.|@@GLOBAL\.|@@)?(`[^`]+`|\w+)\s*=\s*(.+)$", RegexOptions.IgnoreCase)

/// Best-effort name extraction for the "this looks like an assignment but
/// neither `setVar` nor the user-variable parser matched it" error below.
let private setVarNameForError = Regex(@"^(?:SESSION\s+|GLOBAL\s+)?(\S+?)\s*=", RegexOptions.IgnoreCase)

let private quotedSetLiteral = Regex("^(['\"])(.*)\\1$", RegexOptions.Singleline)
let private bareSetIdentifier = Regex("^\\w+$")

let private literalSetRhs (options: Parser.ParserOptions) (rhs: string) : Value option =
    let rhs = rhs.Trim()
    let quoted = quotedSetLiteral.Match rhs

    if options.NoBackslashEscapes then
        None
    elif quoted.Success then
        // MySQL leaves the final escaped quote's preceding slash in this
        // SET-specific spelling; Parser's generic string grammar cannot
        // preserve it.
        Some(VString quoted.Groups.[2].Value)
    elif String.Equals(rhs, "NULL", StringComparison.OrdinalIgnoreCase) then
        Some VNull
    else
        None

let private parserOptionsForSession (session: Session) =
    lookupVar session "sql_mode"
    |> Option.flatten
    |> Option.defaultValue ""
    |> SqlMode.parserOptionsFor

/// Evaluates a `SET` user-variable right-hand side through the ordinary
/// expression grammar. A private variable map preserves SET's all-or-
/// nothing application when a later fragment fails.
let private resolveUserSetRhs
    (session: Session)
    (userVariables: Map<string, Value>)
    (sql: string)
    (rhs: string)
    : Result<Value * Map<string, Value>, QueryResult> =
    let options = parserOptionsForSession session

    match literalSetRhs options rhs with
    | Some value -> Ok(value, userVariables)
    | None ->
        match Parser.parseExpressionWithOptions options rhs with
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
    match literalSetRhs (parserOptionsForSession session) rhs with
    | Some value -> Ok(value, userVariables)
    | None when bareSetIdentifier.IsMatch(rhs.Trim()) -> Ok(VString(rhs.Trim()), userVariables)
    | None ->
        resolveUserSetRhs session userVariables sql rhs

/// `SET a = 1, b = 2` is one statement assigning several variables — real
/// clients use it (Laravel's `MySqlConnector::configureConnection` sends
/// `SET NAMES 'utf8mb4', SESSION sql_mode='...'` as one call). Splits on
/// commas outside quotes and outside parens, after stripping the leading
/// `SET` keyword, so neither a quoted value with its own commas
/// (`sql_mode`'s comma-separated mode list) nor a function call's argument
/// list (`SET @@SESSION.sql_mode = CONCAT(@@sql_mode, ',ANSI_QUOTES')`) gets
/// split apart.
let private splitSetAssignments (options: Parser.ParserOptions) (sql: string) : string list =
    let body = Regex.Replace(sql, @"^SET\s+", "", RegexOptions.IgnoreCase)
    Parser.splitTopLevelCommaSeparatedWithOptions options body

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

let private routineVariableChanges = System.Threading.AsyncLocal<Set<string> option>()

let private markRoutineVariables names =
    match routineVariableChanges.Value with
    | Some changed -> routineVariableChanges.Value <- Some(Set.union changed (Set.ofList names))
    | None -> ()

let private captureRoutineVariableChanges body =
    let parent = routineVariableChanges.Value

    let result, changed =
        DynamicScope.withValue routineVariableChanges (Some Set.empty) (fun () ->
            let result = body ()
            result, routineVariableChanges.Value |> Option.defaultValue Set.empty)

    parent
    |> Option.iter (fun enclosing -> routineVariableChanges.Value <- Some(Set.union enclosing changed))

    result, changed

let private connectionVariableNames =
    [ "character_set_client"; "character_set_connection"; "character_set_results"; "collation_connection" ]

let private applyConnectionEncoding (session: Session) charset (collation: Collation.Collation option) =
    markRoutineVariables connectionVariableNames
    setConnectionCharset session.Store charset
    collation |> Option.iter (setConnectionCollation session.Store)

    { session with
        Variables =
            session.Variables
            |> Map.add "character_set_client" (Some charset)
            |> Map.add "character_set_connection" (Some charset)
            |> Map.add "character_set_results" (Some charset)
            |> Map.add "collation_connection" (collation |> Option.map _.Name) }

/// System variables real MySQL accepts an explicit `NULL` for (rather than
/// the 1231 "can't be set to the value of NULL" every other variable
/// raises) — connector handshakes reset the results charset exactly this
/// way. Grows as real clients ask for more.
let private nullableSystemVars = Set.ofList [ "character_set_results" ]

let private globalOnlyLimitVariables =
    Set.ofList
        [ "event_scheduler"
          "local_infile"
          "max_allowed_packet"
          "max_connections"
          "max_prepared_stmt_count"
          "net_write_timeout" ]

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
                let name = stripIdentifierQuotes varMatch.Groups.[2].Value |> _.ToLowerInvariant()

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
                    | Ok(VString value, sideEffects) when name = "lc_time_names" ->
                        match Functions.tryTimeLocale value with
                        | Some _ -> Ok(SetVarAction(name, Some value, isGlobal), sideEffects)
                        | None -> Error(Err(1649, sprintf "Unknown locale: '%s'" value))
                    | Ok(value, sideEffects) when name = "event_scheduler" ->
                        match value |> toText |> Option.map (_.Trim().ToUpperInvariant()) with
                        | Some("1" | "ON") -> Ok(SetVarAction(name, Some "ON", isGlobal), sideEffects)
                        | Some("0" | "OFF") -> Ok(SetVarAction(name, Some "OFF", isGlobal), sideEffects)
                        | Some value -> Error(Err(1231, sprintf "Variable 'event_scheduler' can't be set to the value of '%s'" value))
                        | None -> Error(Err(1231, "Variable 'event_scheduler' can't be set to the value of 'NULL'"))
                    | Ok(VNull, sideEffects) when nullableSystemVars.Contains name -> Ok(SetVarAction(name, None, isGlobal), sideEffects)
                    | Ok(VNull, _) -> Error(Err(1231, sprintf "Variable '%s' can't be set to the value of 'NULL'" name))
                    | Ok(value, sideEffects) -> Ok(SetVarAction(name, toText value, isGlobal), sideEffects)
            else
                match setVarNameForError.Match fragment with
                | m when m.Success -> Error(Err(1193, sprintf "Unknown system variable '%s'" m.Groups.[1].Value))
                | _ -> Error(syntaxError sql)

/// Applies one parsed `SetAction`, including store settings derived from
/// `foreign_key_checks` and `sql_mode`.
let private applySetAction (session: Session) (action: SetAction) : Session =
    match action with
    | SetNamesAction(charset, collation) ->
        // `SET NAMES` uses the charset default unless COLLATE is explicit.
        let connectionCollation =
            match collation |> Option.bind Collation.tryFind with
            | Some col -> Some col
            | None ->
                match charset.ToLowerInvariant() with
                | "binary" -> Collation.tryFind "utf8mb4_bin"
                | "utf8mb4" -> Some Collation.defaultCollation
                | "utf8"
                | "utf8mb3" -> Collation.tryFind "utf8mb3_general_ci"
                | "latin1" -> Collation.tryFind "latin1_swedish_ci"
                | "ascii" -> Collation.tryFind "ascii_general_ci"
                | _ -> None

        applyConnectionEncoding session charset connectionCollation
    | SetVarAction(name, value, true) ->
        // Global assignments seed new sessions without changing this one.
        match value with
        | Some value when Limits.isReportableSetting name -> Limits.applySetting name value |> ignore
        | _ -> Session.setGlobalVariable session.Store name value

        session
    | SetVarAction(name, value, false) ->
        markRoutineVariables [ name ]

        // The shared action type also carries nullable system variables.
        if name = "foreign_key_checks" then
            value |> Option.iter (fun v -> setForeignKeyChecks session.Store (v.Trim() <> "0"))

        if name = "sql_mode" then
            value |> Option.iter (setSqlMode session.Store)

        if name = "character_set_client" then
            value |> Option.iter (setConnectionCharset session.Store)

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
    | SetVarAction(name, Some value, false) when Limits.isReportableSetting name ->
        Limits.validateSetting name value |> Result.mapError (fun message -> Err(1232, message))
    | _ -> Ok()

/// Applies every assignment only after the whole `SET` parses; MySQL leaves
/// all variables unchanged when any assignment is invalid.
let private handleSet (session: Session) (sql: string) : Session * QueryResult =
    let options = parserOptionsForSession session

    let parsed =
        splitSetAssignments options sql
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
                let updated = { updated with UserVariables = userVariables }
                let changedSystemVariables =
                    actions
                    |> List.collect (function
                        | SetNamesAction _ -> connectionVariableNames
                        | SetVarAction(name, _, false) -> [ name ]
                        | SetTransactionIsolationAction(SessionIsolation, _) -> [ "transaction_isolation" ]
                        | _ -> [])

                let changesSession =
                    actions
                    |> List.exists (function
                        | SetVarAction(_, _, true)
                        | SetTransactionIsolationAction(GlobalIsolation, _) -> false
                        | SetVarAction("session_track_state_change", _, false) -> false
                        | _ -> true)

                Session.trackSystemVariableAssignments changesSession changedSystemVariables updated, Affected 0UL

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

let private requestedDefinerAccount session = function
    | None -> accountOf session
    | Some(value: string) when Regex.IsMatch(value, @"^CURRENT_USER(?:\(\))?$", RegexOptions.IgnoreCase) -> accountOf session
    | Some(value: string) -> accountRefOf value

let private canUseRequestedDefiner session requested =
    let definer = requestedDefinerAccount session requested

    Auth.sameAccount definer (accountOf session)
    || Auth.hasGlobalPrivForAccount session.Store (accountOf session) "SUPER"

let private setPasswordRe =
    Regex(@"^SET\s+PASSWORD\s*(?:FOR\s+([^=\s]+)\s*)?=\s*'([^']*)'\s*;?$", RegexOptions.IgnoreCase)

let private alterCurrentUserPasswordRe =
    Regex(
        @"^ALTER\s+USER\s+(?:USER|CURRENT_USER)\s*\(\s*\)\s+IDENTIFIED\s+BY\s+'([^']*)'\s*;?$",
        RegexOptions.IgnoreCase
    )

/// `SHOW GRANTS [FOR 'user'@'host' | FOR CURRENT_USER[()]]`.
let private showGrantsRe = Regex(@"^SHOW\s+GRANTS(?:\s+FOR\s+(.+?))?\s*;?$", RegexOptions.IgnoreCase)

/// `FLUSH [LOCAL] PRIVILEGES` — a no-op OK: privilege reads always hit the
/// live mysql.* rows, there's no cache to flush.
let private flushPrivilegesRe = Regex(@"^FLUSH\s+(?:LOCAL\s+)?PRIVILEGES\s*;?$", RegexOptions.IgnoreCase)
let private flushUserResourcesRe = Regex(@"^FLUSH\s+USER_RESOURCES\s*;?$", RegexOptions.IgnoreCase)
let private flushStatusRe = Regex(@"^FLUSH\s+STATUS\s*;?$", RegexOptions.IgnoreCase)
let private flushTablesRe = Regex(@"^FLUSH\s+TABLES\s*;?$", RegexOptions.IgnoreCase)
let private flushLogsRe = Regex(@"^FLUSH\s+LOGS\s*;?$", RegexOptions.IgnoreCase)
let private lockTablesRe =
    Regex(
        @"^LOCK\s+TABLES\s+\S+(?:\s+(?:AS\s+)?[A-Za-z_][A-Za-z0-9_$]*)?\s+(?:READ(?:\s+LOCAL)?|WRITE)(?:\s*,\s*\S+(?:\s+(?:AS\s+)?[A-Za-z_][A-Za-z0-9_$]*)?\s+(?:READ(?:\s+LOCAL)?|WRITE))*\s*$",
        RegexOptions.IgnoreCase
    )
let private unlockTablesRe = Regex(@"^UNLOCK\s+TABLES\s*$", RegexOptions.IgnoreCase)

let private setTransactionIsolation =
    Regex(
        @"^SET\s+(?:(SESSION|GLOBAL)\s+)?TRANSACTION\s+ISOLATION\s+LEVEL\s+(REPEATABLE\s+READ|READ\s+COMMITTED|READ\s+UNCOMMITTED|SERIALIZABLE)$",
        RegexOptions.IgnoreCase
    )

let private setTransactionAccess =
    Regex(@"^SET\s+(SESSION\s+)?TRANSACTION\s+READ\s+(ONLY|WRITE)$", RegexOptions.IgnoreCase)

let private setCharacterSet = Regex(@"^SET\s+CHARACTER\s+SET\s+'?(\w+)'?$", RegexOptions.IgnoreCase)
let private setRoleNone = Regex(@"^SET\s+ROLE\s+NONE$", RegexOptions.IgnoreCase)

let private rebaseTransactionSnapshot (session: Session) (tx: Transaction) : Catalog * Store =
    let baseCatalog, transactionSnapshot = Storage.beginTransactionSnapshotWithBase session.Store
    let snapshot =
        transactionSnapshot
        |> Storage.carryTransactionLocks tx.Snapshot

    Storage.mergeCatalogInto snapshot tx.BaseCatalog tx.Snapshot.Catalog

    match tx.Snapshot.PendingEvents, snapshot.PendingEvents with
    | Some source, Some target -> target.AddRange source
    | _ -> ()

    baseCatalog, snapshot

let private lockWaitTimeout (session: Session) =
    lookupVar session "innodb_lock_wait_timeout"
    |> Option.flatten
    |> Option.bind (fun value ->
        match Int32.TryParse value with
        | true, seconds -> Some(TimeSpan.FromSeconds(float seconds))
        | _ -> None)
    |> Option.defaultWith Limits.lockWaitTimeout

/// Commits the open transaction by publishing its private catalog. Ordinary
/// isolation levels merge disjoint row changes; SERIALIZABLE validates the
/// transaction's read snapshot before publication. No open transaction is a
/// no-op, matching MySQL.
let private commitSession (session: Session) : Session =
    match session.Tx with
    | Some tx when not tx.Seeded ->
        Storage.releaseTransactionLocks tx.Snapshot
        { session with Tx = None; Cursors = Map.empty }
    | Some tx ->
        let timeout = lockWaitTimeout session

        match tx.Isolation with
        | Serializable -> Storage.commitSerializableCatalogIntoWithTimeout timeout session.Store tx.BaseCatalog tx.Snapshot
        | _ -> Storage.commitCatalogIntoWithTimeout timeout session.Store tx.BaseCatalog tx.Snapshot

        Storage.releaseTransactionLocks tx.Snapshot

        { session with Tx = None; Cursors = Map.empty }
    | None -> { session with Cursors = Map.empty }

/// Discards the open transaction's snapshot — a no-op, matching real MySQL,
/// if there isn't one open — except for each table's AUTO_INCREMENT
/// counter, which MySQL never rolls back (an id an aborted INSERT consumed
/// stays burned). Bumps the shared store's counter up to the snapshot's if
/// the snapshot ran it ahead (`Storage.bumpAutoIncrementsInto`, same
/// CAS-safe merge as `commitSession`); leaves everything else (rows,
/// schema) alone.
let private rollbackSession (session: Session) : Session =
    match session.Tx with
    | Some tx when not tx.Seeded -> Storage.releaseTransactionLocks tx.Snapshot
    | Some tx ->
        Storage.bumpAutoIncrementsInto session.Store tx.Snapshot.Catalog
        Storage.releaseTransactionLocks tx.Snapshot
    | None -> ()

    { session with Tx = None; Cursors = Map.empty }

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
    let snapshot = Storage.beginTransactionContext session.Store

    { session with
        PendingTransactionReadOnly = None
        PendingTransactionIsolation = None
        Tx =
            Some
                { Snapshot = snapshot
                  BaseCatalog = Map.empty
                  Isolation = isolation
                  ReadOnly = readOnly
                  Seeded = false
                  Savepoints = Map.empty
                  NextSavepointSeq = 0 } }

/// Seeds repeatable-read snapshots and refreshes read-committed views.
let startTransactionStatement (session: Session) : Session =
    match session.Tx with
    | Some tx when not tx.Seeded ->
        let baseCatalog, transactionSnapshot = Storage.beginTransactionSnapshotWithBase session.Store
        let snapshot =
            transactionSnapshot
            |> Storage.carryTransactionLocks tx.Snapshot

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
    | Some tx when tx.Isolation = ReadCommitted ->
        let baseCatalog, snapshot = rebaseTransactionSnapshot session tx

        { session with
            Tx =
                Some
                    { tx with
                        Snapshot = snapshot
                        BaseCatalog = baseCatalog
                        Seeded = true } }
    | Some _ -> session
    | None -> session

let private prepareTransactionWrite (statement: Statement) (session: Session) : Session =
    match session.Tx with
    | None -> session
    | Some transaction ->
        let dbName = session.Database |> Option.defaultValue defaultDatabase

        match Executor.transactionWriteTargets transaction.Snapshot dbName statement with
        | None -> session
        | Some(_, _, targets) when targets.RowIds.IsEmpty && targets.Keys.IsEmpty -> session
        | Some(database, table, targets) ->
            Storage.acquireTransactionWriteTargets
                (lockWaitTimeout session)
                transaction.Snapshot
                database
                table
                targets.RowIds
                targets.Keys
            let baseCatalog, snapshot = rebaseTransactionSnapshot session transaction

            { session with
                Tx =
                    Some
                        { transaction with
                            Snapshot = snapshot
                            BaseCatalog = baseCatalog
                            Seeded = true } }

/// Rolls an abandoned connection's transaction back.
let closeSession (session: Session) : unit =
    releaseAllAdvisoryLocks session |> ignore
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
        if value = "0" then session else commitSession session

    Session.trackSystemVariableAssignments true [ "autocommit" ] session, Affected 0UL

/// Parses and executes anything that isn't one of the text-probe special
/// cases above. Anything else is a 1064 syntax error with SQLSTATE 42000.
/// Executes an already-parsed `Statement` — shared by COM_QUERY (parse then
/// execute) and COM_STMT_EXECUTE (bind placeholders then execute), so the
/// prepared path reuses this one execution body instead of splicing literals
/// back into SQL text and re-parsing.
let private isDataChangeStatement =
    function
    | Insert _
    | InsertSelect _
    | Replace _
    | ReplaceSelect _
    | ReplaceSet _
    | Update _
    | Delete _
    | CreateTableAs _ -> true
    | _ -> false

let private ignoresDataChangeErrors =
    function
    | Insert(_, _, _, _, true)
    | InsertSelect(_, _, _, _, true)
    | Update { Ignore = true } -> true
    | _ -> false

let private divisionByZeroPolicy (store: Store) (statement: Statement) =
    if not store.ExecutionSettings.SqlMode.ErrorForDivisionByZero then
        Diagnostics.DivisionByZeroPolicy.Silent
    elif store.ExecutionSettings.SqlMode.Strict && isDataChangeStatement statement && not (ignoresDataChangeErrors statement) then
        Diagnostics.DivisionByZeroPolicy.Fail
    else
        Diagnostics.DivisionByZeroPolicy.Warn

let private requiredPrivilegesForStatement session store dbName = function
    | AlterUser(name, host, Some _, _, _) when Auth.sameAccount (Auth.account name host) (accountOf session) -> []
    | statement -> Auth.requiredPrivilegesInStore store dbName statement

let private executeParsedStatement (session: Session) (stmt: Statement) : Session * QueryResult =
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

    let dbName = session.Database |> Option.defaultValue defaultDatabase

    let execute session =
        let store = Session.currentStore session
        let requiredPrivileges = requiredPrivilegesForStatement session store dbName stmt

        // Privilege enforcement — the one gate every parsed statement goes
        // through (probes are exempt, see `Auth.requiredPrivileges`'s doc).
        let access =
            match session.Tx, stmt with
            | Some tx, (Select _ | Union _ | Explain _ | ChecksumTables _) when tx.ReadOnly ->
                Auth.checkForAccount store (accountOf session) requiredPrivileges
            | Some tx, _ when tx.ReadOnly -> Error(1792, "Cannot execute statement in a READ ONLY transaction")
            | _ -> Auth.checkForAccount store (accountOf session) requiredPrivileges

        match access with
        | Error(code, msg) -> session, Err(code, msg)
        | Ok() ->

        // Transaction stores begin with default strictness. Derive the full
        // mode value from the session before each statement.
        lookupVar session "sql_mode"
        |> Option.flatten
        |> Option.iter (setSqlMode store)

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

        let evaluate () =
            Executor.withVariableContext variables (fun () ->
                match stmt with
                | Select select ->
                    let result, types, calculatedFoundRows, rows =
                        withExecutionLimits (fun () -> Executor.runTopLevelSelect store registry dbName select)

                    let result, types =
                        if select.IntoVariables.IsEmpty then
                            result, types
                        elif select.IntoVariables.Length <> select.Projections.Length then
                            Err(1222, "The used SELECT statements have a different number of columns"), []
                        else
                            match rows with
                            | [] ->
                                Diagnostics.warning 1329 "No data - zero rows fetched, selected, or processed"
                                Affected 0UL, []
                            | [ row ] ->
                                let assigned =
                                    List.zip select.IntoVariables (Array.toList row)
                                    |> List.fold
                                        (fun state (variable, value) ->
                                            state
                                            |> Result.bind (fun variables ->
                                                match UserVariableRef.validationError variable with
                                                | Some message -> Error(3061, message)
                                                | None when Map.containsKey variable.Name variables || variables.Count < maxUserVariables ->
                                                    Ok(Map.add variable.Name value variables)
                                                | None -> Error(1105, "Too many user-defined variables")))
                                        (Ok variables.UserVariables.Value)

                                match assigned with
                                | Ok assigned ->
                                    variables.UserVariables.Value <- assigned
                                    Affected 0UL, []
                                | Error(code, message) -> Err(code, message), []
                            | _ -> Err(1172, "Result consisted of more than one row"), []

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

        let lastInsertId, lastGeneratedId, result, columnMetadata, calculatedFoundRows =
            Diagnostics.withDivisionByZeroPolicy (divisionByZeroPolicy store stmt) evaluate

        let columnMetadata = completeResultMetadata session result columnMetadata

        let session =
            { session with
                LastInsertId = lastInsertId
                LastGeneratedId = lastGeneratedId
                LastResultColumnMetadata = columnMetadata
                PendingFoundRows = calculatedFoundRows
                UserVariables = variables.UserVariables.Value }

        session, result

    match session.Tx with
    | Some _ ->
        let executed, result =
            session
            |> startTransactionStatement
            |> prepareTransactionWrite stmt
            |> execute

        let canAllocateAutoIncrement =
            match stmt with
            | Insert _
            | InsertSelect _
            | Replace _
            | ReplaceSelect _
            | ReplaceSet _
            | Update _
            | Delete _ -> true
            | _ -> false

        if canAllocateAutoIncrement then
            executed.Tx
            |> Option.iter (fun transaction -> Storage.bumpAutoIncrementsInto executed.Store transaction.Snapshot.Catalog)

        executed, result
    | None -> execute session

let private executeParsedCore (session: Session) (stmt: Statement) : Session * QueryResult =
    InformationSchema.withViewer (Session.currentStore session) (accountOf session) (fun () -> executeParsedStatement session stmt)

type private TemporaryAction =
    | CreateTemporary
    | DropTemporary

let private causesImplicitCommit = function
    | CreateDatabase _
    | DropDatabase _
    | AlterDatabase _
    | CreateTable _
    | CreateTableLike _
    | CreateTableAs _
    | DropTable _
    | AlterTable _
    | RenameTable _
    | CreateIndex _
    | DropIndexStmt _
    | Truncate _
    | CreateUser _
    | DropUser _
    | RenameUser _
    | AlterUser _
    | CreateRole _
    | DropRole _
    | Grant _
    | Revoke _
    | CreateTrigger _
    | DropTrigger _
    | CreateView _
    | DropView _ -> true
    | _ -> false

let private startsTransaction = function
    | Insert _
    | InsertSelect _
    | Replace _
    | ReplaceSelect _
    | ReplaceSet _
    | Select _
    | Union _
    | Update _
    | Delete _
    | ChecksumTables _
    | Explain _ -> true
    | _ -> false

let private autocommitDisabled (session: Session) =
    lookupVar session "autocommit" |> Option.flatten = Some "0"

let private implicitCommitDatabases dbName stmt =
    Auth.requiredPrivileges dbName stmt
    |> List.choose (function
        | _, Auth.OnDb database
        | _, Auth.OnTable(database, _) -> Some database
        | _ -> None)
    |> Set.ofList
    |> Set.add "mysql"

let private changesCatalogMembership = function
    | CreateDatabase _
    | DropDatabase _
    | AlterDatabase _ -> true
    | _ -> false

let private tableKey (db: string) (table: string) = db.ToLowerInvariant(), normalizeTableName table

let private temporaryKeys (catalog: Catalog) =
    catalog
    |> Map.toSeq
    |> Seq.collect (fun (db, tables) -> tables |> Map.keys |> Seq.map (fun table -> tableKey db table))
    |> Set.ofSeq

let private hasTemporaryTable (catalog: Catalog) (db: string) (table: string) =
    catalog
    |> Map.tryFind (db.ToLowerInvariant())
    |> Option.exists (Map.containsKey (normalizeTableName table))

let private setCatalogTable (catalog: Catalog) (db: string) (table: string) (value: Table option) =
    let db = db.ToLowerInvariant()
    let table = normalizeTableName table

    Map.change
        db
        (fun current ->
            let tables = Option.defaultValue Map.empty current

            let tables =
                match value with
                | Some item -> Map.add table item tables
                | None -> Map.remove table tables

            if Map.isEmpty tables then None else Some tables)
        catalog

let private catalogTable (catalog: Catalog) (db: string, table: string) =
    catalog |> Map.tryFind (db.ToLowerInvariant()) |> Option.bind (Map.tryFind (normalizeTableName table))

let private temporaryTargets (dbName: string) (action: TemporaryAction option) (stmt: Statement) =
    match action, stmt with
    | Some CreateTemporary, CreateTable table -> [ splitQualified dbName table.Name ]
    | Some CreateTemporary, CreateTableLike(name, _, _)
    | Some CreateTemporary, CreateTableAs(name, _, _) -> [ splitQualified dbName name ]
    | Some DropTemporary, DropTable(names, _) -> names |> List.map (splitQualified dbName)
    | _ -> []

let private statementUsesTemporary (catalog: Catalog) (dbName: string) (stmt: Statement) =
    Auth.requiredPrivileges dbName stmt
    |> List.exists (function
        | _, Auth.OnTable(db, table) -> hasTemporaryTable catalog db table
        | _ -> false)

let rec private filterTemporaryEvent keys event =
    let isTemporary db table = Set.contains (tableKey db table) keys

    match event with
    | RowsInserted(db, table, _)
    | RowsUpdated(db, table, _)
    | RowsDeleted(db, table, _) when isTemporary db table -> None
    | SchemaChanged(db, statement)
    | SchemaChangedAt(db, statement, _) ->
        let touchesTemporary =
            Auth.requiredPrivileges db statement
            |> List.exists (function
                | _, Auth.OnTable(targetDb, table) -> isTemporary targetDb table
                | _ -> false)

        if touchesTemporary then None else Some event
    | TransactionCommitted events ->
        let retained = events |> List.choose (filterTemporaryEvent keys)
        if retained.IsEmpty then None else Some(TransactionCommitted retained)
    | _ -> Some event

let private executeWithTemporaryCatalog (action: TemporaryAction option) (session: Session) (stmt: Statement) =
    let dbName = session.Database |> Option.defaultValue defaultDatabase
    let baseStore = Session.currentStore session
    let baseCatalog = baseStore.Catalog
    let beforeKeys = temporaryKeys session.TemporaryCatalog
    let targets = temporaryTargets dbName action stmt |> List.map (fun (db, table) -> tableKey db table) |> Set.ofList

    // The private root lets temporary names shadow shared tables without
    // publishing their schema, rows, or commit events.
    let combined = overlayCatalog baseCatalog session.TemporaryCatalog

    let combined =
        targets
        |> Set.fold (fun catalog (db, table) -> if Set.contains (db, table) beforeKeys then catalog else setCatalogTable catalog db table None) combined

    let working = Storage.beginTransactionSnapshotFromCatalog baseStore combined
    let workingSession = { session with Store = working; Tx = None }
    let executed, result = executeParsedCore workingSession stmt

    match result with
    | Err _ -> { executed with Store = session.Store; Tx = session.Tx }, result
    | _ ->
        let afterKeys =
            match action with
            | Some CreateTemporary -> Set.union beforeKeys targets
            | Some DropTemporary -> Set.difference beforeKeys targets
            | None -> beforeKeys

        let temporaryCatalog =
            afterKeys
            |> Set.fold
                (fun catalog (db, table) ->
                    match catalogTable working.Catalog (db, table) with
                    | Some value -> setCatalogTable catalog db table (Some value)
                    | None -> catalog)
                Map.empty

        let overlayKeys = Set.unionMany [ beforeKeys; afterKeys; targets ]

        let permanentCatalog =
            overlayKeys
            |> Set.fold (fun catalog (db, table) -> setCatalogTable catalog db table (catalogTable baseCatalog (db, table))) working.Catalog

        working.Catalog <- permanentCatalog

        working.PendingEvents
        |> Option.iter (fun events ->
            let retained = events |> Seq.choose (filterTemporaryEvent overlayKeys) |> Seq.toArray
            events.Clear()
            events.AddRange retained)

        Storage.commitCatalogInto baseStore baseCatalog working

        { executed with
            Store = session.Store
            Tx = session.Tx
            TemporaryCatalog = temporaryCatalog },
        result

let private executeParsedWithTemporaryAction (action: TemporaryAction option) (session: Session) (stmt: Statement) =
    let dbName = session.Database |> Option.defaultValue defaultDatabase
    let usesTemporary = action.IsSome || statementUsesTemporary session.TemporaryCatalog dbName stmt

    if usesTemporary then
        executeWithTemporaryCatalog action session stmt
    elif causesImplicitCommit stmt then
        let session = commitSession session

        if changesCatalogMembership stmt then
            executeParsedCore session stmt
        else
            Storage.withDatabaseLocks
                (lockWaitTimeout session)
                session.Store
                (implicitCommitDatabases dbName stmt)
                (fun () -> executeParsedCore session stmt)
    else
        let session =
            if session.Tx.IsNone && autocommitDisabled session && startsTransaction stmt then
                beginTransaction (configuredReadOnly session) session
            else
                session

        executeParsedCore session stmt

let private executeParsed session stmt = executeParsedWithTemporaryAction None session stmt

let private parsedStatementCapacity = 16384
let private parsedStatementCandidateCapacity = parsedStatementCapacity * 2
let private cacheableSqlLength = 512

type private BoundedConcurrentCache<'key, 'value when 'key: equality>(capacity: int) =
    let entries = ConcurrentDictionary<'key, 'value>()
    let order = ConcurrentQueue<'key>()

    let trim () =
        let mutable canTrim = true

        while canTrim && entries.Count > capacity do
            match order.TryDequeue() with
            | true, oldest -> entries.TryRemove oldest |> ignore
            | false, _ -> canTrim <- false

    member _.TryGetValue key = entries.TryGetValue key

    member _.TryAdd(key, value) =
        if entries.TryAdd(key, value) then
            order.Enqueue key
            trim ()
            true
        else
            false

let private parsedStatements =
    BoundedConcurrentCache<struct (Parser.ParserOptions * string), Statement>(parsedStatementCapacity)

let private parsedStatementCandidates =
    BoundedConcurrentCache<int, byte>(parsedStatementCandidateCapacity)

let private isRepeatedStatement (options: Parser.ParserOptions) (sql: string) =
    // The fingerprint only decides admission. Cache lookups still use the
    // complete SQL and mode, so collisions cannot select the wrong AST.
    let fingerprint = HashCode.Combine(options, StringComparer.Ordinal.GetHashCode sql)

    if parsedStatementCandidates.TryAdd(fingerprint, 0uy) then
        false
    else
        true

let private parseStatement (options: Parser.ParserOptions) (sql: string) =
    let key = struct (options, sql)

    match parsedStatements.TryGetValue key with
    | true, statement -> Result.Ok statement
    | false, _ ->
        match Parser.parseWithOptions options sql with
        | Result.Ok statement as parsed ->
            let cacheable =
                sql.Length <= cacheableSqlLength
                && isRepeatedStatement options sql

            if cacheable then
                parsedStatements.TryAdd(key, statement) |> ignore

            parsed
        | Result.Error _ as error -> error

let private executeStatement (session: Session) (normalizedSql: string) (parserSql: string) : Session * QueryResult =
    let parserOptions = parserOptionsForSession session

    let action, parsedSql =
        if Regex.IsMatch(normalizedSql, @"^\s*CREATE\s+TEMPORARY\s+TABLE\b", RegexOptions.IgnoreCase) then
            Some CreateTemporary, Regex.Replace(normalizedSql, @"^(\s*CREATE\s+)TEMPORARY\s+", "$1", RegexOptions.IgnoreCase)
        elif Regex.IsMatch(normalizedSql, @"^\s*DROP\s+TEMPORARY\s+TABLE\b", RegexOptions.IgnoreCase) then
            Some DropTemporary, Regex.Replace(normalizedSql, @"^(\s*DROP\s+)TEMPORARY\s+", "$1", RegexOptions.IgnoreCase)
        else
            None, parserSql

    match parseStatement parserOptions parsedSql with
    | Result.Ok stmt -> executeParsedWithTemporaryAction action session stmt
    | Result.Error detail -> { session with LastResultColumnMetadata = [] }, parserError parserSql detail

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
    | SetRoleNone
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
    | ShowRoutineStatus of kind: string
    | Kill of queryOnly: bool * id: int64
    | AlterKeysNoop of table: string
    | ShowConditions of errorsOnly: bool
    | ShowMessageCount of isError: bool
    | ShowDatabases
    | ShowTableStatus
    | ShowTables
    | ShowCreate of name: string
    | ShowCreateView of name: string
    | ShowCreateTrigger of name: string
    | ShowColumns of full: bool * name: string * dbOverride: string option
    | Describe of name: string
    | ShowIndex of name: string * dbOverride: string option
    | ShowCollation
    | ShowGrants of user: string option
    | ShowCreateUser of user: string
    | ShowCreateProgram of kind: string * name: string
    | ShowPrivileges
    | FlushPrivileges
    | FlushUserResources
    | FlushStatus
    | FlushTables
    | FlushLogs
    | LockTables
    | UnlockTables

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
    elif setRoleNone.IsMatch sql then
        Some SetRoleNone
    elif alterCurrentUserPasswordRe.IsMatch sql then
        Some(SetPassword(None, (alterCurrentUserPasswordRe.Match sql).Groups.[1].Value))
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
                (if m.Groups.[1].Success then Some(stripIdentifierQuotes m.Groups.[1].Value) else None),
                (if m.Groups.[2].Success then Some m.Groups.[2].Value else None)
            )
        )
    elif showCreateDatabaseRe.IsMatch sql then
        Some(ShowCreateDatabase(stripIdentifierQuotes (showCreateDatabaseRe.Match sql).Groups.[1].Value))
    elif showCharsetRe.IsMatch sql then
        Some ShowCharset
    elif showPrivilegesRe.IsMatch sql then
        Some ShowPrivileges
    elif showCreateUserRe.IsMatch sql then
        Some(ShowCreateUser((showCreateUserRe.Match sql).Groups.[1].Value))
    elif showCreateProgramRe.IsMatch sql then
        let matched = showCreateProgramRe.Match sql
        Some(ShowCreateProgram(matched.Groups.[1].Value.ToUpperInvariant(), matched.Groups.[2].Value))
    elif showCreateTriggerRe.IsMatch sql then
        Some(ShowCreateTrigger((showCreateTriggerRe.Match sql).Groups.[1].Value))
    elif showGrantsRe.IsMatch sql then
        let m = showGrantsRe.Match sql
        Some(ShowGrants(if m.Groups.[1].Success then Some m.Groups.[1].Value else None))
    elif flushPrivilegesRe.IsMatch sql then
        Some FlushPrivileges
    elif flushUserResourcesRe.IsMatch sql then
        Some FlushUserResources
    elif flushStatusRe.IsMatch sql then
        Some FlushStatus
    elif flushTablesRe.IsMatch sql then
        Some FlushTables
    elif flushLogsRe.IsMatch sql then
        Some FlushLogs
    elif lockTablesRe.IsMatch sql then
        Some LockTables
    elif unlockTablesRe.IsMatch sql then
        Some UnlockTables
    elif showProcesslistRe.IsMatch sql then
        Some(ShowProcesslist((showProcesslistRe.Match sql).Groups.[1].Success))
    elif showTriggersRe.IsMatch sql then
        let m = showTriggersRe.Match sql
        Some(ShowTriggers(if m.Groups.[1].Success then Some(stripIdentifierQuotes m.Groups.[1].Value) else None))
    elif showEventsRe.IsMatch sql then
        let m = showEventsRe.Match sql
        Some(ShowEvents(if m.Groups.[1].Success then Some(stripIdentifierQuotes m.Groups.[1].Value) else None))
    elif showRoutineStatusRe.IsMatch sql then
        Some(ShowRoutineStatus((showRoutineStatusRe.Match sql).Groups.[1].Value.ToUpperInvariant()))
    elif killRe.IsMatch sql then
        let m = killRe.Match sql
        Some(Kill(m.Groups.[1].Value.ToUpperInvariant() = "QUERY", int64 m.Groups.[2].Value))
    elif alterKeysRe.IsMatch sql then
        Some(AlterKeysNoop(stripIdentifierQuotes (alterKeysRe.Match sql).Groups.[1].Value))
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
            | Ok() ->
                let updated = applySetAction session action
                let names = if scope = SessionIsolation then [ "transaction_isolation" ] else []
                Session.trackSystemVariableAssignments (scope <> GlobalIsolation) names updated, Affected 0UL
    | SetTransactionAccess(sessionScope, readOnly) ->
        let value = if readOnly then "1" else "0"

        if sessionScope then
            let updated =
                { session with
                    Variables =
                        session.Variables
                        |> Map.add "transaction_read_only" (Some value)
                        |> Map.add "tx_read_only" (Some value) }

            Session.trackSystemVariableAssignments true [ "transaction_read_only"; "tx_read_only" ] updated, Affected 0UL
        else
            let updated = { session with PendingTransactionReadOnly = Some readOnly }
            Session.trackSystemVariableAssignments true [] updated, Affected 0UL
    | SetRoleNone -> session, Affected 0UL
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
            let updated = applyConnectionEncoding session charset (Collation.tryFind collation)
            Session.trackSystemVariableAssignments true connectionVariableNames updated, Affected 0UL
    | SetPassword(userOpt, password) ->
        // No FOR clause selects the session's authenticated account.
        let wanted = userOpt |> Option.map accountRefOf |> Option.defaultValue (accountOf session)
        let store = Session.currentStore session

        // MySQL's rule: changing your own password is free, anyone else's
        // needs CREATE USER — probes bypass `executeParsed`'s enforcement
        // gate, so this one carries its own check.
        let required = if Auth.sameAccount wanted (accountOf session) then [] else [ "CREATE USER", Auth.Global ]

        match Auth.checkForAccount store (accountOf session) required |> Result.bind (fun () -> Auth.setPassword store wanted.Name wanted.Host password) with
        | Ok() -> { session with PasswordExpired = false }, Affected 0UL
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
            Session.trackSchemaAssignment dbName ({ session with Database = Some dbName }), Affected 0UL
        else
            let code, msg = Storage.toMySqlError (Storage.NoSuchDatabase dbName)
            session, Err(code, msg)
    | ShowVariables isGlobal -> session, handleShowVariables session isGlobal sql
    | ShowStatus ->
        session,
        InformationSchema.showStatus
            (session.Capabilities &&& Protocol.ClientCompress <> 0u)
            session.TransportMetrics.BytesReceived
            session.TransportMetrics.BytesSent
            session.TlsCipher
            session.TlsVersion
            (statusFilter sql)
        |> showResult
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
    | ShowRoutineStatus kind ->
        session,
        InformationSchema.withViewer (Session.currentStore session) (accountOf session) (fun () ->
            InformationSchema.showRoutineStatus (Session.currentStore session).Catalog kind)
        |> showResult
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
        let showCreate =
            if hasTemporaryTable session.TemporaryCatalog dbName table then
                InformationSchema.showCreateTemporaryTable
            else
                InformationSchema.showCreateTable

        session,
        showCreate (catalogWithOverlay session dbName table) dbName table
        |> showTableResult session dbName table
    | ShowCreateView name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, view = splitQualified sessionDb name
        session,
        InformationSchema.showCreateView (Session.currentStore session).Catalog dbName view
        |> showTableResult session dbName view
    | ShowCreateTrigger name ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, trigger = splitQualified sessionDb name
        session, InformationSchema.showCreateTrigger (Session.currentStore session).Catalog dbName trigger |> showResult
    | ShowColumns(full, name, dbOverride) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        let dbName = dbOverride |> Option.map stripIdentifierQuotes |> Option.defaultValue dbName
        let store = Session.currentStore session
        let viewColumns = Executor.viewColumns store (registryFor session)
        session,
        InformationSchema.showColumns (catalogWithOverlay session dbName table) (Some viewColumns) full dbName table (likeSuffix sql)
        |> Result.map (fun (columns, rows) ->
            match showColumnsFieldFilter sql with
            | None -> columns, rows
            | Some field ->
                columns,
                rows
                |> List.filter (function
                    | Some name :: _ -> String.Equals(name, field, StringComparison.OrdinalIgnoreCase)
                    | _ -> false))
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
        let dbName = dbOverride |> Option.map stripIdentifierQuotes |> Option.defaultValue dbName
        session,
        InformationSchema.showIndex (catalogWithOverlay session dbName table) dbName table
        |> Result.map (fun (columns, rows) ->
            let matches index wanted row =
                wanted
                |> Option.forall (fun value ->
                    row
                    |> List.tryItem index
                    |> Option.flatten
                    |> Option.exists (fun actual -> String.Equals(actual, value, StringComparison.OrdinalIgnoreCase)))

            let keyName = showIndexTextFilter "Key_name" sql
            let columnName = showIndexTextFilter "Column_name" sql
            columns, rows |> List.filter (fun row -> matches 2 keyName row && matches 4 columnName row))
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
        let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName

        if kind = "EVENT" then
            let event =
                match Storage.scanList (Session.currentStore session) "mysql" "events" with
                | Error _ -> None
                | Ok(_, rows) ->
                    rows
                    |> List.choose SystemCatalog.Event.tryRead
                    |> List.tryFind (SystemCatalog.Event.matches database name)

            match event with
            | None -> session, Err(1539, sprintf "Unknown event '%s'" name)
            | Some event ->
                let definer = accountRefOf event.Definer
                let schedule =
                    SystemCatalog.Event.timing event
                    |> Option.map Event.scheduleText
                    |> Option.defaultValue event.Schedule

                let status =
                    match event.Status with
                    | Event.Status.Enabled -> "ENABLE"
                    | Event.Status.Disabled -> "DISABLE"
                    | Event.Status.ReplicaSideDisabled -> "DISABLE ON REPLICA"

                let comment =
                    if event.Comment = "" then
                        ""
                    else
                        let options = SqlMode.parserOptionsFor event.SqlMode
                        let escaped =
                            if options.NoBackslashEscapes then
                                event.Comment.Replace("'", "''")
                            else
                                event.Comment
                                    .Replace("\\", "\\\\")
                                    .Replace("'", "''")
                                    .Replace("\r", "\\r")
                                    .Replace("\n", "\\n")

                        " COMMENT '" + escaped + "'"

                let ddl =
                    sprintf
                        "CREATE DEFINER=`%s`@`%s` EVENT `%s` ON SCHEDULE %s ON COMPLETION %s %s%s DO %s"
                        (definer.Name.Replace("`", "``"))
                        (definer.Host.Replace("`", "``"))
                        (name.Replace("`", "``"))
                        schedule
                        event.OnCompletion
                        status
                        comment
                        event.Definition

                session,
                ResultSet(
                    [ "Event"; "sql_mode"; "time_zone"; "Create Event"; "character_set_client"; "collation_connection"; "Database Collation" ],
                    [ [ Some name; Some event.SqlMode; Some event.TimeZone; Some ddl; Some event.CharacterSetClient
                        Some event.CollationConnection; Some event.DatabaseCollation ] ]
                )
        elif kind = "PROCEDURE" then
            let routine =
                match Storage.scanList (Session.currentStore session) "mysql" "routines" with
                | Error _ -> None
                | Ok(_, rows) ->
                    rows
                    |> List.choose SystemCatalog.Routine.tryRead
                    |> List.tryFind (SystemCatalog.Routine.matches database name)

            match routine with
            | None -> session, Err(1305, sprintf "%s %s does not exist" kind name)
            | Some routine when not (canSeeRoutine session routine.Schema routine.Definer) ->
                session, Err(1305, sprintf "%s %s does not exist" kind name)
            | Some routine ->
                let definer = accountRefOf routine.Definer
                let ddl =
                    if canInspectRoutine session routine.Schema routine.Definer then
                        Some(
                            sprintf
                                "CREATE DEFINER=`%s`@`%s` PROCEDURE `%s`(%s) SQL SECURITY %s %s"
                                definer.Name
                                definer.Host
                                name
                                routine.Parameters
                                routine.SecurityType
                                routine.Definition
                        )
                    else
                        None

                session,
                ResultSet(
                    [ "Procedure"; "sql_mode"; "Create Procedure"; "character_set_client"; "collation_connection"; "Database Collation" ],
                    [ [ Some name; Some routine.SqlMode; ddl; Some routine.CharacterSetClient
                        Some routine.CollationConnection; Some routine.DatabaseCollation ] ]
                )
        elif kind = "FUNCTION" then
            let routine =
                match Storage.scanList (Session.currentStore session) "mysql" "functions" with
                | Error _ -> None
                | Ok(_, rows) ->
                    rows
                    |> List.choose SystemCatalog.StoredFunction.tryRead
                    |> List.tryFind (SystemCatalog.StoredFunction.matches database name)

            match routine with
            | None -> session, Err(1305, sprintf "%s %s does not exist" kind name)
            | Some routine when not (canSeeRoutine session routine.Schema routine.Definer) ->
                session, Err(1305, sprintf "%s %s does not exist" kind name)
            | Some routine ->
                let definer = accountRefOf routine.Definer
                let deterministic = if routine.Deterministic then " DETERMINISTIC" else ""

                let ddl =
                    if canInspectRoutine session routine.Schema routine.Definer then
                        Some(
                            sprintf
                                "CREATE DEFINER=`%s`@`%s` FUNCTION `%s`(%s) RETURNS %s%s %s SQL SECURITY %s %s"
                                definer.Name
                                definer.Host
                                name
                                routine.Parameters
                                routine.ReturnType
                                deterministic
                                routine.SqlDataAccess
                                routine.SecurityType
                                routine.Definition
                        )
                    else
                        None

                session,
                ResultSet(
                    [ "Function"; "sql_mode"; "Create Function"; "character_set_client"; "collation_connection"; "Database Collation" ],
                    [ [ Some name; Some routine.SqlMode; ddl; Some routine.CharacterSetClient
                        Some routine.CollationConnection; Some routine.DatabaseCollation ] ]
                )
        else
            session, Err(1305, sprintf "%s %s does not exist" kind name)
    | FlushPrivileges -> session, Affected 0UL
    | FlushUserResources ->
        match Auth.checkForAccount session.Store (accountOf session) [ "RELOAD", Auth.Global ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() ->
            Auth.resetAllAccountResources session.Store
            session, Affected 0UL
    | FlushStatus ->
        InformationSchema.resetQuestions ()
        InformationSchema.resetCommandCounts ()
        session, Affected 0UL
    | FlushTables
    | FlushLogs -> session, Affected 0UL
    | LockTables
    | UnlockTables -> session, Affected 0UL
let mapPlaceholders (replace: int -> Expr) (statement: Statement) : Statement =
    Fsdb.Sql.Expression.rewriteStatement
        (function
        | Placeholder index -> Some(replace index)
        | _ -> None)
        statement

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
let private prepareStatementWithOptions
    (options: Parser.ParserOptions)
    (sql: string)
    : Result<Statement option * int, int * string> =
    let trimmed = sql.Trim().TrimEnd(';').Trim()
    let upper = trimmed.ToUpperInvariant()

    if upper.StartsWith("LOAD DATA", StringComparison.Ordinal) then
        Result.Error(1295, "This command is not supported in the prepared statement protocol yet")
    elif (tryProbe trimmed upper).IsSome then
        Result.Ok(None, placeholderPositionsWithOptions options sql |> List.length)
    else
        match Parser.parseWithOptions options sql with
        | Result.Ok stmt ->
            let renumbered, count = renumberPlaceholders stmt

            // A `?` the AST binder can't reach — e.g. in a generated-column
            // DDL expression, which `bindPlaceholders` leaves untouched —
            // shows up in the source text but not the renumbered count. It
            // would survive unbound into execution (and `FailFast` a
            // --data-dir server via `Persistence.encodeExpr`), so reject the
            // prepare as a 1064, same as the COM_QUERY guard in `dispatch`.
            if (placeholderPositionsWithOptions options sql |> List.length) <> count then
                match syntaxError sql with
                | Err(code, msg) -> Result.Error(code, msg)
                | _ -> Result.Error(1064, "syntax error")
            else
                Result.Ok(Some renumbered, count)
        | Result.Error detail ->
            match parserError sql detail with
            | Err(code, msg) -> Result.Error(code, msg)
            | _ -> Result.Error(1064, "syntax error")

let prepareStatement (sql: string) : Result<Statement option * int, int * string> =
    prepareStatementWithOptions Parser.defaultOptions sql

let prepareStatementForSession (session: Session) (sql: string) : Result<Statement option * int, int * string> =
    prepareStatementWithOptions (parserOptionsForSession session) sql

let preparedMetadata
    (session: Session)
    (statement: Statement option)
    (parameterCount: int)
    : ColumnMetadata list * Fsdb.Protocol.ColumnDef list =
    let generic = ColumnWire.parameterMetadataOfType(TVarchar 16383)

    match statement with
    | None -> List.replicate parameterCount generic, []
    | Some statement ->
        let store = Session.currentStore session
        let schema = session.Database |> Option.defaultValue defaultDatabase
        let registry = registryFor session

        match Auth.checkForAccount store (accountOf session) (Auth.requiredPrivilegesInStore store schema statement) with
        | Error _ -> List.replicate parameterCount generic, []
        | Ok() ->
            let parameters = PreparedMetadata.parameterDefinitions store registry schema statement parameterCount
            let columns = Executor.statementColumns store registry schema statement |> Option.defaultValue []
            let origins =
                Executor.statementColumnOrigins store schema statement
                |> Option.filter (fun values -> values.Length = columns.Length)
                |> Option.defaultValue (List.replicate columns.Length None)

            let resultColumns =
                List.map2
                    (fun (column: ColumnDef) origin ->
                        { Name = column.Name
                          Metadata =
                            { ColumnWire.metadataOfColumn column with
                                Origin = origin } }
                        : Fsdb.Protocol.ColumnDef)
                    columns
                    origins

            parameters, resultColumns

type private TextPreparedSource =
    | PreparedLiteral of string
    | PreparedVariable of UserVariableRef

type private TextPreparedCommand =
    | PrepareText of name: string * source: TextPreparedSource
    | ExecuteText of name: string * variables: UserVariableRef list
    | DeallocateText of name: string

let private preparedName = @"(?:`(?<name>(?:``|[^`])+)`|(?<name>[A-Za-z_][A-Za-z0-9_$]*))"

let private prepareTextRe =
    Regex(@"^\s*PREPARE\s+" + preparedName + @"\s+FROM\s+(?<source>.+?)\s*$", RegexOptions.IgnoreCase)

let private executeTextRe =
    Regex(@"^\s*EXECUTE\s+" + preparedName + @"(?:\s+USING\s+(?<variables>.+))?\s*$", RegexOptions.IgnoreCase)

let private deallocateTextRe =
    Regex(@"^\s*(?:DEALLOCATE|DROP)\s+PREPARE\s+" + preparedName + @"\s*$", RegexOptions.IgnoreCase)

let private matchedPreparedName (matched: Match) =
    matched.Groups.["name"].Captures
    |> Seq.cast<Capture>
    |> Seq.last
    |> _.Value.Replace("``", "`")
    |> _.ToLowerInvariant()

let private tryTextPreparedCommand (sql: string) : Result<TextPreparedCommand option, QueryResult> =
    let prepared = prepareTextRe.Match(sql)
    let execute = executeTextRe.Match(sql)
    let deallocate = deallocateTextRe.Match(sql)

    if prepared.Success then
        let source = prepared.Groups.["source"].Value

        match Parser.parseExpression source with
        | Ok(Lit(VString text)) -> Ok(Some(PrepareText(matchedPreparedName prepared, PreparedLiteral text)))
        | Ok(UserVariable variable) ->
            Ok(Some(PrepareText(matchedPreparedName prepared, PreparedVariable variable)))
        | _ -> Error(syntaxError source)
    elif execute.Success then
        let variables = execute.Groups.["variables"]

        if not variables.Success then
            Ok(Some(ExecuteText(matchedPreparedName execute, [])))
        else
            match Parser.parse ("SELECT " + variables.Value) with
            | Ok(Select { Projections = projections }) ->
                projections
                |> List.fold
                    (fun state projection ->
                        state
                        |> Result.bind (fun variables ->
                            match projection with
                            | UserVariable variable, None -> Ok(variable :: variables)
                            | _ -> Error(Err(1064, "EXECUTE USING requires user variables"))))
                    (Ok [])
                |> Result.map List.rev
                |> Result.map (fun variables -> Some(ExecuteText(matchedPreparedName execute, variables)))
            | _ -> Error(Err(1064, "Invalid EXECUTE USING clause"))
    elif deallocate.Success then
        Ok(Some(DeallocateText(matchedPreparedName deallocate)))
    else
        Ok None

type private TextRoutineCommand =
    | CreateProcedure of name: string * parameters: string * securityType: string * body: string * definer: string option
    | CreateFunction of
        name: string *
        ifNotExists: bool *
        parameters: string *
        returnType: string *
        characteristics: string *
        body: string *
        definer: string option
    | CallProcedure of name: string * arguments: string
    | DropProcedure of name: string * ifExists: bool
    | DropFunction of name: string * ifExists: bool

let private createProcedureRe =
    Regex(
        """^\s*CREATE\s+(?:DEFINER\s*=\s*(?<definer>(?:CURRENT_USER(?:\(\))?|(?:'[^']*'|`[^`]*`|[A-Za-z0-9_$.-]+)(?:\s*@\s*(?:'[^']*'|`[^`]*`|[A-Za-z0-9_$.:/%-]+))?))\s+)?PROCEDURE\s+(?<name>\S+)\s*\((?<parameters>(?:[^()]|\([^()]*\))*)\)\s+(?:SQL\s+SECURITY\s+(?<security>INVOKER|DEFINER)\s+)?(?<body>.+)$""",
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline
    )

let private callProcedureRe =
    Regex(@"^\s*CALL\s+(?<name>[^\s(]+)(?:\s*\((?<arguments>.*)\))?\s*$", RegexOptions.IgnoreCase ||| RegexOptions.Singleline)

let private dropProcedureRe =
    Regex(@"^\s*DROP\s+PROCEDURE\s+(?<ifExists>IF\s+EXISTS\s+)?(?<name>\S+)\s*$", RegexOptions.IgnoreCase)

let private functionCharacteristicsPattern =
    """(?:(?:LANGUAGE\s+SQL|(?:NOT\s+)?DETERMINISTIC|SQL\s+SECURITY\s+(?:DEFINER|INVOKER)|NO\s+SQL|CONTAINS\s+SQL|READS\s+SQL\s+DATA|MODIFIES\s+SQL\s+DATA|COMMENT\s+'(?:''|\\.|[^'])*')\s*)*"""

let private createFunctionRe =
    Regex(
        """^\s*CREATE\s+(?:DEFINER\s*=\s*(?<definer>(?:CURRENT_USER(?:\(\))?|(?:'[^']*'|\`[^\`]*\`|[A-Za-z0-9_$.-]+)(?:\s*@\s*(?:'[^']*'|\`[^\`]*\`|[A-Za-z0-9_$.:/%-]+))?))\s+)?FUNCTION\s+(?<ifNotExists>IF\s+NOT\s+EXISTS\s+)?(?<name>\S+)\s*\((?<parameters>(?:[^()]|\([^()]*\))*)\)\s+RETURNS\s+(?<returns>[A-Za-z]+(?:\s*\([^)]*\))?(?:\s+UNSIGNED)?)\s*(?<characteristics>"""
        + functionCharacteristicsPattern
        + """)(?<body>(?:BEGIN|RETURN)\b[\s\S]+)$""",
        RegexOptions.IgnoreCase
    )

let private dropFunctionRe =
    Regex(@"^\s*DROP\s+FUNCTION\s+(?<ifExists>IF\s+EXISTS\s+)?(?<name>\S+)\s*$", RegexOptions.IgnoreCase)

let private tryTextRoutineCommand (sql: string) =
    let create = createProcedureRe.Match(sql)
    let createFunction = createFunctionRe.Match(sql)
    let call = callProcedureRe.Match(sql)
    let drop = dropProcedureRe.Match(sql)
    let dropFunction = dropFunctionRe.Match(sql)

    if create.Success then
        let security =
            create.Groups.["security"].Value
            |> function
                | "" -> "DEFINER"
                | value -> value.ToUpperInvariant()

        Some(
            CreateProcedure(
                create.Groups.["name"].Value,
                create.Groups.["parameters"].Value.Trim(),
                security,
                create.Groups.["body"].Value,
                if create.Groups.["definer"].Success then Some(create.Groups.["definer"].Value.Trim()) else None
            )
        )
    elif createFunction.Success then
        Some(
            CreateFunction(
                createFunction.Groups.["name"].Value,
                createFunction.Groups.["ifNotExists"].Success,
                createFunction.Groups.["parameters"].Value.Trim(),
                createFunction.Groups.["returns"].Value.Trim(),
                createFunction.Groups.["characteristics"].Value.Trim(),
                createFunction.Groups.["body"].Value,
                if createFunction.Groups.["definer"].Success then
                    Some(createFunction.Groups.["definer"].Value.Trim())
                else
                    None
            )
        )
    elif call.Success then
        Some(CallProcedure(call.Groups.["name"].Value, call.Groups.["arguments"].Value.Trim()))
    elif drop.Success then
        Some(DropProcedure(drop.Groups.["name"].Value, drop.Groups.["ifExists"].Success))
    elif dropFunction.Success then
        Some(DropFunction(dropFunction.Groups.["name"].Value, dropFunction.Groups.["ifExists"].Success))
    else
        None

let private routineValidationError error =
    let code, message = StoredProgram.validationError error
    Err(code, message)

let private isSupportedStoredProgramText sql =
    (tryProbe sql (sql.TrimStart().ToUpperInvariant()) |> Option.isSome)
    || (tryTextPreparedCommand sql |> Result.exists Option.isSome)
    || (match tryTextRoutineCommand sql with
        | Some(CallProcedure _) -> true
        | _ -> false)

let private parseRoutineDefinition options parameters body =
    match
        StoredProgram.parseParameters options parameters,
        StoredProgram.parseRoutine options isSupportedStoredProgramText body
    with
    | Ok parsedParameters, Ok statements ->
        StoredProgram.validate parsedParameters statements
        |> Result.mapError routineValidationError
        |> Result.map (fun () -> parsedParameters, statements)
    | Error _, _ -> Error(syntaxError parameters)
    | _, Error _ -> Error(syntaxError body)

let private parseFunctionCharacteristics (text: string) =
    let security =
        let matched = Regex.Match(text, @"\bSQL\s+SECURITY\s+(?<value>DEFINER|INVOKER)\b", RegexOptions.IgnoreCase)
        if matched.Success then matched.Groups.["value"].Value.ToUpperInvariant() else "DEFINER"

    let deterministic =
        not (Regex.IsMatch(text, @"\bNOT\s+DETERMINISTIC\b", RegexOptions.IgnoreCase))
        && Regex.IsMatch(text, @"\bDETERMINISTIC\b", RegexOptions.IgnoreCase)

    let dataAccess =
        [ "NO SQL"; "CONTAINS SQL"; "READS SQL DATA"; "MODIFIES SQL DATA" ]
        |> List.tryFind (fun value -> Regex.IsMatch(text, @"\b" + value.Replace(" ", @"\s+") + @"\b", RegexOptions.IgnoreCase))
        |> Option.defaultValue "CONTAINS SQL"

    let residue =
        text
        |> fun value -> Regex.Replace(value, @"\bSQL\s+SECURITY\s+(?:DEFINER|INVOKER)\b", "", RegexOptions.IgnoreCase)
        |> fun value -> Regex.Replace(value, @"\b(?:NOT\s+)?DETERMINISTIC\b", "", RegexOptions.IgnoreCase)
        |> fun value -> Regex.Replace(value, @"\b(?:NO\s+SQL|CONTAINS\s+SQL|READS\s+SQL\s+DATA|MODIFIES\s+SQL\s+DATA)\b", "", RegexOptions.IgnoreCase)
        |> fun value -> Regex.Replace(value, @"\bLANGUAGE\s+SQL\b", "", RegexOptions.IgnoreCase)
        |> fun value -> Regex.Replace(value, @"\bCOMMENT\s+'(?:''|\\.|[^'])*'", "", RegexOptions.IgnoreCase)
        |> _.Trim()

    if residue = "" then
        Ok(security, deterministic, dataAccess)
    else
        Error(syntaxError text)

let private parseFunctionDefinition options parameters returnType body =
    match
        StoredProgram.parseParameters options parameters,
        Parser.parseColumnTypeWithOptions options returnType,
        StoredProgram.parse options body
    with
    | Ok parsedParameters, Ok parsedReturnType, Ok statements
        when parsedParameters |> List.exists (fun parameter -> parameter.Mode <> StoredProgram.In) ->
        Error(syntaxError parameters)
    | Ok parsedParameters, Ok parsedReturnType, Ok statements ->
        StoredProgram.validateFunction parsedParameters statements
        |> Result.mapError routineValidationError
        |> Result.bind (fun () ->
            match statements |> List.collect StoredProgram.executableSqlStatements with
            | (Select _ | Union _) :: _ -> Error(Err(1415, "Not allowed to return a result set from a function"))
            | _ :: _ -> Error(Err(1235, "SQL statements in stored functions are not supported"))
            | [] -> Ok(parsedParameters, parsedReturnType, statements))
    | Error _, _, _ -> Error(syntaxError parameters)
    | _, Error _, _ -> Error(syntaxError returnType)
    | _, _, Error _ when Regex.IsMatch(body, @"\b(?:PREPARE|EXECUTE|DEALLOCATE\s+PREPARE)\b", RegexOptions.IgnoreCase) ->
        Error(Err(1336, "Dynamic SQL is not allowed in stored function or trigger"))
    | _, _, Error _ -> Error(syntaxError body)

let private routineColumn name columnType =
    { Name = name
      Type = columnType
      NumericDisplay = None
      Nullable = true
      Default = None
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      OnUpdateCurrentTimestamp = false
      Generated = None
      Comment = ""
      Collation = None
      Charset = None }

let private coerceRoutineValue (store: Store) column value =
    Storage.coerceValue store.ExecutionSettings.SqlMode.Strict column value
    |> Result.mapError (Storage.toMySqlError >> Err)

let private evaluateRoutineExpression (session: Session) expression =
    let variables = expressionVariables session
    let store = Session.currentStore session
    let database = session.Database |> Option.defaultValue defaultDatabase

    let result =
        Executor.withVariableContext variables (fun () ->
            Executor.evaluateExpression store (registryFor session) database expression)

    { session with UserVariables = variables.UserVariables.Value }, result

type private RoutineOutputTarget =
    | UserVariableOutput of UserVariableRef
    | LocalVariableOutput of string

let private bindRoutineArguments
    database
    name
    (session: Session)
    (parameters: StoredProgram.Parameter list)
    (arguments: Expr list)
    =
    let rec loop
        current
        index
        (values: (StoredProgram.Parameter * Value) list)
        (outputs: (StoredProgram.Parameter * RoutineOutputTarget) list)
        (parameters: StoredProgram.Parameter list)
        (arguments: Expr list)
        =
        match parameters, arguments with
        | [], [] -> Ok(current, List.rev values, List.rev outputs)
        | parameter :: parameterRest, argument :: argumentRest ->
            match parameter.Mode with
            | StoredProgram.In ->
                let next, evaluated = evaluateRoutineExpression current argument

                evaluated
                |> Result.bind (fun value ->
                    loop next (index + 1) ((parameter, value) :: values) outputs parameterRest argumentRest)
            | StoredProgram.Out
            | StoredProgram.InOut ->
                match argument with
                | UserVariable target ->
                    match UserVariableRef.validationError target with
                    | Some message -> Error(Err(3061, message))
                    | None ->
                        let value =
                            if parameter.Mode = StoredProgram.Out then
                                VNull
                            else
                                current.UserVariables |> Map.tryFind target.Name |> Option.defaultValue VNull

                        loop
                            current
                            (index + 1)
                            ((parameter, value) :: values)
                            ((parameter, UserVariableOutput target) :: outputs)
                            parameterRest
                            argumentRest
                | Col name ->
                    match Executor.currentRoutineVariables () |> Option.bind (Map.tryFind name) with
                    | Some local ->
                        let value = if parameter.Mode = StoredProgram.Out then VNull else local.Value

                        loop
                            current
                            (index + 1)
                            ((parameter, value) :: values)
                            ((parameter, LocalVariableOutput name) :: outputs)
                            parameterRest
                            argumentRest
                    | None ->
                        Error(
                            Err(
                                1414,
                                sprintf
                                    "OUT or INOUT argument %d for routine %s.%s is not a variable or NEW pseudo-variable in BEFORE trigger"
                                    index
                                    database
                                    name
                            )
                        )
                | _ ->
                    Error(
                        Err(
                            1414,
                            sprintf
                                "OUT or INOUT argument %d for routine %s.%s is not a variable or NEW pseudo-variable in BEFORE trigger"
                                index
                                database
                                name
                        )
                    )
        | _ -> Error(Err(1318, sprintf "Incorrect number of arguments for PROCEDURE %s.%s" database name))

    loop session 1 [] [] parameters arguments

let private applyUserVariableOutputs variables outputs =
    outputs
    |> List.fold
        (fun state (name, value) ->
            state
            |> Result.bind (fun current ->
                if Map.containsKey name current || current.Count < maxUserVariables then
                    Ok(Map.add name value current)
                else
                    Error(Err(1105, "Too many user-defined variables"))))
        (Ok variables)

let private applyProcedureOutputs store variables routineVariables outputs =
    outputs
    |> List.fold
        (fun state (target, value) ->
            state
            |> Result.bind (fun (currentVariables, currentLocals) ->
                match target with
                | UserVariableOutput variable ->
                    if Map.containsKey variable.Name currentVariables || currentVariables.Count < maxUserVariables then
                        Ok(Map.add variable.Name value currentVariables, currentLocals)
                    else
                        Error(Err(1105, "Too many user-defined variables"))
                | LocalVariableOutput name ->
                    match currentLocals |> Option.bind (Map.tryFind name) with
                    | None -> Error(Err(1414, sprintf "OUT or INOUT target %s is not a local variable" name))
                    | Some local ->
                        coerceRoutineValue store local.Column value
                        |> Result.map (fun value ->
                            currentVariables,
                            currentLocals
                            |> Option.map (Map.add name { local with Value = value }))))
        (Ok(variables, routineVariables))

type private RoutineRun =
    { Session: Session
      Locals: Map<string, Executor.RoutineVariable>
      Results: (QueryResult * ColumnMetadata list) list
      AffectedRows: uint64
      Error: QueryResult option
      Flow: StoredProgram.Flow }

type private RoutineScope =
    { Conditions: Map<string, StoredProgram.ConditionValue>
      Statements: StoredProgram.Statement list
      ActiveError: SqlState.Error option
      StackedDiagnostics: StoredProgram.DiagnosticsSnapshot option }

let private executionErrorResult =
    function
    | Value.UnsignedOutOfRange -> Some(Err(1690, "BIGINT UNSIGNED value is out of range"))
    | Value.SignedOutOfRange -> Some(Err(1690, "BIGINT value is out of range"))
    | Functions.SqlError(code, message) -> Some(Err(code, message))
    | _ -> None

let private runRoutineStatements
    (store: Store)
    (executeSql: Session -> Ast.Statement -> Session * QueryResult)
    (executeText: Session -> string -> Session * QueryResult)
    (initial: Session)
    (initialLocals: Map<string, Executor.RoutineVariable>)
    (statements: StoredProgram.Statement list)
    =
    let evaluate locals current expression =
        Executor.withRoutineVariables locals (fun () -> evaluateRoutineExpression current expression)

    let currentDiagnostics: StoredProgram.DiagnosticsSnapshot ref =
        ref
            { Conditions = []
              RowCount = 0L }
    let propagatedConditions = ResizeArray<Diagnostics.Condition>()
    let cursors = ref Map.empty<string, StoredProgram.Cursor>

    let updateDiagnostics generated result =
        let conditions =
            match Executor.errorInfo result with
            | Some error -> generated @ [ Diagnostics.fromError error ]
            | None -> generated

        let rowCount =
            match result with
            | Affected count -> int64 count
            | ResultSet _
            | Err _
            | MultipleResults _ -> -1L

        currentDiagnostics.Value <-
            { Conditions = conditions
              RowCount = rowCount }

    let executeWithDiagnostics current execute =
        let execute () =
            try
                execute ()
            with error ->
                match executionErrorResult error with
                | Some result -> current, result
                | None -> reraise ()

        let (next, result), generated = Diagnostics.capture execute
        updateDiagnostics generated result
        generated |> List.iter propagatedConditions.Add
        next, result

    let clearDiagnostics () =
        currentDiagnostics.Value <-
            { Conditions = []
              RowCount = 0L }

    let evaluateSignalInformation locals current information =
        let rec loop current values =
            function
            | [] -> current, Ok(List.rev values)
            | (name, expression) :: rest ->
                let next, evaluated = evaluate locals current expression

                match evaluated with
                | Error error -> next, Error error
                | Ok value -> loop next ((name, value) :: values) rest

        loop current [] information

    let failed session locals results affectedRows error =
        { Session = session
          Locals = locals
          Results = List.rev results
          AffectedRows = affectedRows
          Error = Some error
          Flow = StoredProgram.Flow.Complete }

    let flowing session locals results affectedRows flow =
        { Session = session
          Locals = locals
          Results = List.rev results
          AffectedRows = affectedRows
          Error = None
          Flow = flow }

    let rec run scope current locals results affectedRows =
        function
        | [] ->
            { Session = current
              Locals = locals
              Results = List.rev results
              AffectedRows = affectedRows
              Error = None
              Flow = StoredProgram.Flow.Complete }
        | statement :: rest ->
            match statement with
            | StoredProgram.Sql sql ->
                executeWithDiagnostics current (fun () ->
                    Executor.withRoutineVariables locals (fun () -> executeSql current sql))
                ||> continueAfterSql scope locals results affectedRows rest
            | StoredProgram.TextSql sql ->
                let routineState = ref locals

                let next, result =
                    executeWithDiagnostics current (fun () ->
                        Executor.withRoutineVariableState routineState (fun () -> executeText current sql))

                continueAfterSql scope routineState.Value results affectedRows rest next result
            | StoredProgram.Declare declaration ->
                let next, evaluated =
                    match declaration.InitialValue with
                    | Some expression -> evaluate locals current expression
                    | None -> current, Ok VNull

                let column = routineColumn declaration.Name declaration.ColumnType

                match evaluated |> Result.bind (coerceRoutineValue store column) with
                | Error error -> handleQueryResult scope next locals results affectedRows rest error
                | Ok value ->
                    let locals =
                        Map.add declaration.Name { Executor.RoutineVariable.Column = column; Value = value } locals

                    run scope next locals results affectedRows rest
            | StoredProgram.DeclareCondition _
            | StoredProgram.DeclareHandler _ -> run scope current locals results affectedRows rest
            | StoredProgram.DeclareCursor(name, query) ->
                cursors.Value <- Map.add name (StoredProgram.cursor query) cursors.Value

                run scope current locals results affectedRows rest
            | StoredProgram.OpenCursor name ->
                match StoredProgram.tryOpenCursor name cursors.Value with
                | Result.Error error ->
                    handleQueryResult scope current locals results affectedRows rest (ErrInfo error)
                | Ok cursor ->
                    let next, opened =
                        executeWithDiagnostics current (fun () ->
                            Executor.withRoutineVariables locals (fun () -> executeSql current cursor.Query))

                    match opened with
                    | ResultSet(columns, rows) ->
                        let binaryValue index (value: string option) =
                            match value, List.tryItem index next.LastResultColumnMetadata with
                            | Some text, Some metadata
                                when metadata.Flags &&& BinaryFlag <> 0us
                                     && (metadata.TypeId = TypeString
                                         || metadata.TypeId = TypeVarString
                                         || metadata.TypeId = TypeBlob) ->
                                VBytes(Encoding.Latin1.GetBytes text)
                            | Some text, _ -> VString text
                            | None, _ -> VNull

                        let rows =
                            rows
                            |> List.map (List.mapi binaryValue >> List.toArray)
                            |> List.toArray

                        cursors.Value <- StoredProgram.setCursorRows name columns.Length rows cursors.Value

                        run scope next locals results affectedRows rest
                    | error -> handleQueryResult scope next locals results affectedRows rest error
            | StoredProgram.FetchCursor(name, targets) ->
                match StoredProgram.tryFetchCursorRow name targets.Length cursors.Value with
                | Result.Error error ->
                    handleQueryResult scope current locals results affectedRows rest (ErrInfo error)
                | Ok(row, nextCursors) ->
                    cursors.Value <- nextCursors

                    let assigned, generated =
                        Diagnostics.capture (fun () ->
                            List.zip targets (Array.toList row)
                            |> List.fold
                                (fun assigned (target, value) ->
                                    assigned
                                    |> Result.bind (fun locals ->
                                        match Map.tryFind target locals with
                                        | None -> Error(Err(1327, sprintf "Undeclared variable: %s" target))
                                        | Some local ->
                                            coerceRoutineValue store local.Column value
                                            |> Result.map (fun value ->
                                                Map.add target { local with Value = value } locals)))
                                (Ok locals))

                    match assigned with
                    | Ok locals ->
                        updateDiagnostics generated (Affected 0UL)
                        generated |> List.iter propagatedConditions.Add
                        run scope current locals results affectedRows rest
                    | Error error ->
                        updateDiagnostics generated error
                        generated |> List.iter propagatedConditions.Add
                        handleQueryResult scope current locals results affectedRows rest error
            | StoredProgram.CloseCursor name ->
                match StoredProgram.tryCloseCursor name cursors.Value with
                | Result.Error error ->
                    handleQueryResult scope current locals results affectedRows rest (ErrInfo error)
                | Ok nextCursors ->
                    cursors.Value <- nextCursors
                    updateDiagnostics [] (Affected 0UL)
                    run scope current locals results affectedRows rest
            | StoredProgram.GetDiagnostics diagnostics ->
                runDiagnostics scope diagnostics current locals results affectedRows rest
            | StoredProgram.Signal(condition, information) ->
                runSignal scope None (Some condition) information current locals results affectedRows rest
            | StoredProgram.Resignal(condition, information) ->
                runSignal scope scope.ActiveError condition information current locals results affectedRows rest
            | StoredProgram.SetLocal(localName, expression) ->
                match Map.tryFind localName locals with
                | None ->
                    handleQueryResult
                        scope
                        current
                        locals
                        results
                        affectedRows
                        rest
                        (Err(1193, sprintf "Unknown system variable '%s'" localName))
                | Some local ->
                    let next, evaluated = evaluate locals current expression

                    match evaluated |> Result.bind (coerceRoutineValue store local.Column) with
                    | Error error -> handleQueryResult scope next locals results affectedRows rest error
                    | Ok value ->
                        let locals = Map.add localName { local with Value = value } locals
                        clearDiagnostics ()
                        run scope next locals results affectedRows rest
            | StoredProgram.Return expression ->
                let next, evaluated = evaluate locals current expression

                match evaluated with
                | Error error -> handleQueryResult scope next locals results affectedRows rest error
                | Ok value -> flowing next locals results affectedRows (StoredProgram.Flow.Return value)
            | StoredProgram.If(condition, whenTrue, whenFalse) ->
                let next, evaluated = evaluate locals current condition

                match evaluated with
                | Error error -> handleQueryResult scope next locals results affectedRows rest error
                | Ok value ->
                    let branch = if Value.truthy value = Some true then whenTrue else whenFalse
                    run scope next locals [] 0UL branch |> continueAfterNested scope rest results affectedRows
            | StoredProgram.Block(label, body) ->
                let beforeCursors = cursors.Value
                let blockScope =
                    { Conditions = StoredProgram.conditionDefinitions scope.Conditions body
                      Statements = body
                      ActiveError = scope.ActiveError
                      StackedDiagnostics = scope.StackedDiagnostics }

                let blockRun = run blockScope current locals [] 0UL body
                cursors.Value <- StoredProgram.restoreOuterCursors body beforeCursors cursors.Value
                let flow =
                    match blockRun.Flow, label with
                    | StoredProgram.Flow.Leave target, Some label when target = label -> StoredProgram.Flow.Complete
                    | StoredProgram.Flow.ExitHandler, _ -> StoredProgram.Flow.Complete
                    | flow, _ -> flow

                { blockRun with
                    Locals = StoredProgram.restoreOuterScope body locals blockRun.Locals
                    Flow = flow }
                |> continueAfterNested scope rest results affectedRows
            | StoredProgram.Case(selector, branches, otherwise) ->
                let branchSelector = StoredProgram.caseBranchIndexExpression selector branches

                let next, selected = evaluate locals current branchSelector

                match selected with
                | Error error -> handleQueryResult scope next locals results affectedRows rest error
                | Ok(VInt index) when index >= 0L ->
                    branches
                    |> List.item (int index)
                    |> snd
                    |> run scope next locals [] 0UL
                    |> continueAfterNested scope rest results affectedRows
                | Ok _ ->
                    match otherwise with
                    | None ->
                        handleQueryResult
                            scope
                            next
                            locals
                            results
                            affectedRows
                            rest
                            (Err(1339, "Case not found for CASE statement"))
                    | Some body ->
                        run scope next locals [] 0UL body
                        |> continueAfterNested scope rest results affectedRows
            | StoredProgram.While(label, condition, body) ->
                let rec iterate current locals results affectedRows =
                    Storage.queryCancellation.Value.ThrowIfCancellationRequested()
                    let next, evaluated = evaluate locals current condition

                    match evaluated with
                    | Error error -> handleQueryResult scope next locals results affectedRows rest error
                    | Ok value when Value.truthy value <> Some true -> run scope next locals results affectedRows rest
                    | Ok _ ->
                        let bodyRun = run scope next locals [] 0UL body
                        let results = List.rev bodyRun.Results @ results
                        let affectedRows = affectedRows + bodyRun.AffectedRows

                        match bodyRun.Error, bodyRun.Flow with
                        | Some error, _ -> failed bodyRun.Session bodyRun.Locals results affectedRows error
                        | None, StoredProgram.Flow.Leave target when label = Some target ->
                            run scope bodyRun.Session bodyRun.Locals results affectedRows rest
                        | None, StoredProgram.Flow.Iterate target when label = Some target ->
                            iterate bodyRun.Session bodyRun.Locals results affectedRows
                        | None, StoredProgram.Flow.Complete -> iterate bodyRun.Session bodyRun.Locals results affectedRows
                        | None, flow -> flowing bodyRun.Session bodyRun.Locals results affectedRows flow

                iterate current locals results affectedRows
            | StoredProgram.Repeat(label, body, until) ->
                let rec iterate current locals results affectedRows =
                    Storage.queryCancellation.Value.ThrowIfCancellationRequested()
                    let bodyRun = run scope current locals [] 0UL body
                    let results = List.rev bodyRun.Results @ results
                    let affectedRows = affectedRows + bodyRun.AffectedRows

                    match bodyRun.Error, bodyRun.Flow with
                    | Some error, _ -> failed bodyRun.Session bodyRun.Locals results affectedRows error
                    | None, StoredProgram.Flow.Leave target when label = Some target ->
                        run scope bodyRun.Session bodyRun.Locals results affectedRows rest
                    | None, StoredProgram.Flow.Iterate target when label = Some target ->
                        iterate bodyRun.Session bodyRun.Locals results affectedRows
                    | None, StoredProgram.Flow.Complete ->
                        let next, evaluated = evaluate bodyRun.Locals bodyRun.Session until

                        match evaluated with
                        | Error error ->
                            handleQueryResult scope next bodyRun.Locals results affectedRows rest error
                        | Ok value when Value.truthy value = Some true ->
                            run scope next bodyRun.Locals results affectedRows rest
                        | Ok _ -> iterate next bodyRun.Locals results affectedRows
                    | None, flow -> flowing bodyRun.Session bodyRun.Locals results affectedRows flow

                iterate current locals results affectedRows
            | StoredProgram.Loop(label, body) ->
                let rec iterate current locals results affectedRows =
                    Storage.queryCancellation.Value.ThrowIfCancellationRequested()
                    let bodyRun = run scope current locals [] 0UL body
                    let results = List.rev bodyRun.Results @ results
                    let affectedRows = affectedRows + bodyRun.AffectedRows

                    match bodyRun.Error, bodyRun.Flow with
                    | Some error, _ -> failed bodyRun.Session bodyRun.Locals results affectedRows error
                    | None, StoredProgram.Flow.Leave target when label = Some target ->
                        run scope bodyRun.Session bodyRun.Locals results affectedRows rest
                    | None, StoredProgram.Flow.Iterate target when label = Some target ->
                        iterate bodyRun.Session bodyRun.Locals results affectedRows
                    | None, StoredProgram.Flow.Complete -> iterate bodyRun.Session bodyRun.Locals results affectedRows
                    | None, flow -> flowing bodyRun.Session bodyRun.Locals results affectedRows flow

                iterate current locals results affectedRows
            | StoredProgram.Leave label -> flowing current locals results affectedRows (StoredProgram.Flow.Leave label)
            | StoredProgram.Iterate label -> flowing current locals results affectedRows (StoredProgram.Flow.Iterate label)

    and continueAfterNested scope rest results affectedRows nested =
        let results = List.rev nested.Results @ results
        let affectedRows = affectedRows + nested.AffectedRows

        match nested.Error, nested.Flow with
        | Some result, _ ->
            match Executor.errorInfo result with
            | Some error -> handleCondition scope nested.Session nested.Locals results affectedRows rest error
            | None -> failed nested.Session nested.Locals results affectedRows result
        | None, StoredProgram.Flow.Complete -> run scope nested.Session nested.Locals results affectedRows rest
        | None, flow -> flowing nested.Session nested.Locals results affectedRows flow

    and continueAfterResultCollection scope locals results affectedRows rest next =
        function
        | [] -> run scope next locals results affectedRows rest
        | (result, metadata) :: remaining ->
            match result with
            | Err _ ->
                match Executor.errorInfo result with
                | Some error -> handleCondition scope next locals results affectedRows rest error
                | None -> failed next locals results affectedRows result
            | ResultSet _ ->
                continueAfterResultCollection
                    scope
                    locals
                    ((result, metadata) :: results)
                    affectedRows
                    rest
                    next
                    remaining
            | Affected count ->
                continueAfterResultCollection scope locals results (affectedRows + count) rest next remaining
            | MultipleResults nested ->
                continueAfterResultCollection scope locals results affectedRows rest next (nested @ remaining)

    and continueAfterSql scope locals results affectedRows rest next result =
        match result with
        | Err _ ->
            match Executor.errorInfo result with
            | Some error -> handleCondition scope next locals results affectedRows rest error
            | None -> failed next locals results affectedRows result
        | ResultSet _ -> run scope next locals ((result, next.LastResultColumnMetadata) :: results) affectedRows rest
        | Affected count -> run scope next locals results (affectedRows + count) rest
        | MultipleResults nested ->
            continueAfterResultCollection scope locals results affectedRows rest next nested

    and handleQueryResult scope current locals results affectedRows rest result =
        match Executor.errorInfo result with
        | Some error -> handleCondition scope current locals results affectedRows rest error
        | None -> failed current locals results affectedRows result

    and runSignal scope original condition information current locals results affectedRows rest =
        let next, evaluated = evaluateSignalInformation locals current information

        match evaluated with
        | Error result -> handleQueryResult scope next locals results affectedRows rest result
        | Ok information ->
            match StoredProgram.signalError scope.Conditions original condition information with
            | Error(code, message) ->
                handleCondition scope next locals results affectedRows rest (SqlState.create code message)
            | Ok error -> handleCondition scope next locals results affectedRows rest error

    and runDiagnostics scope diagnostics current locals results affectedRows rest =
        let snapshot =
            match diagnostics.Area with
            | StoredProgram.Current -> Ok currentDiagnostics.Value
            | StoredProgram.Stacked ->
                match scope.StackedDiagnostics with
                | Some snapshot -> Ok snapshot
                | None -> Error(ErrState(3004, "0Z002", "GET STACKED DIAGNOSTICS when handler not active"))

        match snapshot with
        | Error result -> handleQueryResult scope current locals results affectedRows rest result
        | Ok snapshot ->
            let next, conditionNumber =
                match diagnostics.Request with
                | StoredProgram.ConditionInformation(expression, _) ->
                    let next, evaluated = evaluate locals current expression
                    next, evaluated |> Result.map StoredProgram.tryDiagnosticsConditionNumber
                | StoredProgram.StatementInformation _ -> current, Ok None

            match conditionNumber with
            | Error result -> handleQueryResult scope next locals results affectedRows rest result
            | Ok conditionNumber ->
                match StoredProgram.diagnosticsAssignments snapshot diagnostics.Request conditionNumber with
                | None ->
                    currentDiagnostics.Value <-
                        { Conditions = [ Diagnostics.invalidConditionNumber ]
                          RowCount = -1L }

                    run scope next locals results affectedRows rest
                | Some assignments ->
                    applyDiagnosticsAssignments next locals assignments
                    |> function
                        | Error result -> handleQueryResult scope next locals results affectedRows rest result
                        | Ok(next, locals) -> run scope next locals results affectedRows rest

    and applyDiagnosticsAssignments
        (current: Session)
        (locals: Map<string, Executor.RoutineVariable>)
        assignments
        =
        let rec apply (current: Session) locals =
            function
            | [] -> Ok(current, locals)
            | (StoredProgram.LocalVariable name, value) :: rest ->
                match Map.tryFind name locals with
                | None -> Error(Err(1327, sprintf "Undeclared variable: %s" name))
                | Some local ->
                    match coerceRoutineValue store local.Column value with
                    | Error result -> Error result
                    | Ok value ->
                        apply current (Map.add name { local with Value = value } locals) rest
            | (StoredProgram.UserVariable variable, value) :: rest ->
                match UserVariableRef.validationError variable with
                | Some message -> Error(Err(3061, message))
                | None
                    when not (Map.containsKey variable.Name current.UserVariables)
                         && current.UserVariables.Count >= maxUserVariables ->
                    Error(Err(1105, "Too many user-defined variables"))
                | None ->
                    let current =
                        { current with
                            UserVariables = Map.add variable.Name value current.UserVariables }

                    apply current locals rest

        apply current locals assignments

    and handleCondition scope current locals results affectedRows rest error =
        currentDiagnostics.Value <- StoredProgram.diagnosticsForError currentDiagnostics.Value error

        match StoredProgram.tryHandler scope.Conditions scope.Statements error with
        | None when StoredProgram.isWarning error ->
            propagatedConditions.Add(Diagnostics.fromWarning error)
            run scope current locals results affectedRows rest
        | None -> failed current locals results affectedRows (ErrInfo error)
        | Some(action, body) ->
            let handlerScope =
                { scope with
                    Statements = []
                    ActiveError = Some error
                    StackedDiagnostics = Some currentDiagnostics.Value }

            let handled = run handlerScope current locals [] 0UL [ body ]
            let results = List.rev handled.Results @ results
            let affectedRows = affectedRows + handled.AffectedRows

            match handled.Error, handled.Flow with
            | Some result, _ -> failed handled.Session handled.Locals results affectedRows result
            | None, StoredProgram.Flow.Complete ->
                match action with
                | StoredProgram.HandlerAction.Continue ->
                    run scope handled.Session handled.Locals results affectedRows rest
                | StoredProgram.HandlerAction.Exit ->
                    flowing handled.Session handled.Locals results affectedRows StoredProgram.Flow.ExitHandler
            | None, flow -> flowing handled.Session handled.Locals results affectedRows flow

    let scope =
        { Conditions = StoredProgram.conditionDefinitions Map.empty statements
          Statements = statements
          ActiveError = None
          StackedDiagnostics = None }

    let outcome = run scope initial initialLocals [] 0UL statements

    if outcome.Error.IsNone then
        propagatedConditions |> Seq.iter Diagnostics.record

    outcome

let private storedExecutionSettings sqlMode characterSetClient collationConnection : ExecutionSettings =
    { SqlModeText = sqlMode
      SqlMode = SqlMode.settingsFor sqlMode
      ConnectionCharset = characterSetClient
      ConnectionCollation =
        collationConnection
        |> Collation.tryFind
        |> Option.defaultValue Collation.defaultCollation }

let private storedExecutionVariables sqlMode characterSetClient collationConnection variables =
    variables
    |> Map.add "sql_mode" (Some sqlMode)
    |> Map.add "character_set_client" (Some characterSetClient)
    |> Map.add "character_set_connection" (Some characterSetClient)
    |> Map.add "collation_connection" (Some collationConnection)

let private restoreRoutineVariables original changed variables =
    [ "sql_mode"; "character_set_client"; "character_set_connection"; "collation_connection" ]
    |> List.fold
        (fun restored name ->
            if Set.contains name changed then
                restored
            else
                match Map.tryFind name original with
                | Some value -> Map.add name value restored
                | None -> Map.remove name restored)
        variables

let private mergeRoutineExecutionSettings original changed result =
    let changedAny names = names |> List.exists (fun name -> Set.contains name changed)

    { SqlModeText = if changedAny [ "sql_mode" ] then result.SqlModeText else original.SqlModeText
      SqlMode = if changedAny [ "sql_mode" ] then result.SqlMode else original.SqlMode
      ConnectionCharset =
        if changedAny [ "character_set_client"; "character_set_connection" ] then
            result.ConnectionCharset
        else
            original.ConnectionCharset
      ConnectionCollation =
        if changedAny [ "collation_connection" ] then
            result.ConnectionCollation
        else
            original.ConnectionCollation }

let private invalidDiagnosticsCondition session =
    { session with Diagnostics = [ Diagnostics.invalidConditionNumber ] }, Affected 0UL

let private runTextDiagnostics session (diagnostics: StoredProgram.DiagnosticsStatement) =
    match diagnostics.Area with
    | StoredProgram.Stacked ->
        session, ErrState(3004, "0Z002", "GET STACKED DIAGNOSTICS when handler not active")
    | StoredProgram.Current ->
        let snapshot: StoredProgram.DiagnosticsSnapshot =
            { Conditions = session.Diagnostics
              RowCount = session.LastRowCount }

        let next, conditionNumber =
            match diagnostics.Request with
            | StoredProgram.ConditionInformation(expression, _) ->
                let next, result = evaluateRoutineExpression session expression
                next, result |> Result.map StoredProgram.tryDiagnosticsConditionNumber
            | StoredProgram.StatementInformation _ -> session, Ok None

        match conditionNumber with
        | Error result -> next, result
        | Ok conditionNumber ->
            match StoredProgram.diagnosticsAssignments snapshot diagnostics.Request conditionNumber with
            | None -> invalidDiagnosticsCondition next
            | Some assignments ->
                assignments
                |> traverse (fun (target, value) ->
                    match target with
                    | StoredProgram.UserVariable variable ->
                        match UserVariableRef.validationError variable with
                        | Some message -> Error(Err(3061, message))
                        | None -> Ok(variable.Name, value)
                    | StoredProgram.LocalVariable name -> Error(Err(1327, sprintf "Undeclared variable: %s" name)))
                |> Result.bind (applyUserVariableOutputs next.UserVariables)
                |> function
                    | Error result -> next, result
                    | Ok variables -> { next with UserVariables = variables }, Affected 0UL

let private validEventBody options (body: string) =
    match StoredProgram.parseRoutine options isSupportedStoredProgramText body with
    | Ok statements -> StoredProgram.validate [] statements |> Result.isOk
    | Error _ -> false

let private evaluateEventTiming (session: Session) options (schedule: string) =
    let statementTime = Functions.truncateToSecond DateTime.Now
    let currentTimeFunctions = set [ "CURRENT_TIMESTAMP"; "LOCALTIME"; "LOCALTIMESTAMP"; "NOW" ]

    let stabilizeCurrentTime =
        Expression.rewrite (function
            | FuncCall(name, _) when currentTimeFunctions.Contains(name.ToUpperInvariant()) ->
                Some(Lit(VDateTime statementTime))
            | _ -> None)

    let evaluate current expression =
        match Parser.parseExpressionWithOptions options expression with
        | Error _ -> current, Error(syntaxError expression)
        | Ok parsed ->
            let next, result = evaluateRoutineExpression current (stabilizeCurrentTime parsed)
            next, result |> Result.mapError id

    let evaluateDateTime current expression error =
        let next, result = evaluate current expression

        next,
        result
        |> Result.bind (fun value ->
            Functions.tryDateTimeValue value
            |> Option.map (Functions.truncateToSecond >> Ok)
            |> Option.defaultValue (Error error))

    match Event.tryParseSchedule options schedule with
    | None -> session, Error(syntaxError schedule)
    | Some(Event.ScheduleSpec.At expression) ->
        let error = Err(1525, sprintf "Incorrect AT value: '%s'" expression)
        let next, evaluated = evaluateDateTime session expression error
        next, evaluated |> Result.map Event.Timing.OneTime
    | Some(Event.ScheduleSpec.Every(valueExpression, field, startsExpression, endsExpression)) ->
        let next, intervalValue = evaluate session valueExpression

        match intervalValue |> Result.bind (toText >> Option.map Ok >> Option.defaultValue (Error(Err(1542, "INTERVAL is either not positive or too big")))) with
        | Error error -> next, Error error
        | Ok intervalValue ->
            let next, starts =
                match startsExpression with
                | None -> next, Ok statementTime
                | Some expression ->
                    evaluateDateTime
                        next
                        expression
                        (Err(1543, "ENDS is either invalid or before STARTS"))

            match starts with
            | Error error -> next, Error error
            | Ok starts ->
                let next, ends =
                    match endsExpression with
                    | None -> next, Ok None
                    | Some expression ->
                        let next, result =
                            evaluateDateTime
                                next
                                expression
                                (Err(1543, "ENDS is either invalid or before STARTS"))

                        next, result |> Result.map Some

                match ends with
                | Error error -> next, Error error
                | Ok(Some ends) when ends < starts -> next, Error(Err(1543, "ENDS is either invalid or before STARTS"))
                | Ok ends ->
                    match Event.tryRecurringTiming intervalValue field starts ends with
                    | Some timing -> next, Ok timing
                    | None -> next, Error(Err(1542, "INTERVAL is either not positive or too big"))

let private storedFunctionSession = System.Threading.AsyncLocal<Session option>()
let private storedFunctionCalls = System.Threading.AsyncLocal<(string * string) list option>()

let private raiseFunctionError result =
    match Executor.errorInfo result with
    | Some error -> raise (Diagnostics.EvaluationError(error.Code, error.Message))
    | None -> raise (Diagnostics.EvaluationError(1105, "Stored function execution failed"))

let rec private invokeStoredFunction
    (declaredSession: Session)
    (routine: SystemCatalog.StoredFunction.Entry)
    (arguments: Value list)
    =
    let caller = storedFunctionSession.Value |> Option.defaultValue declaredSession
    let caller =
        match Executor.currentScalarExecutionAccount () with
        | Some account ->
            { caller with
                User = account.Name
                AccountHost = account.Host }
        | None -> caller

    let caller = withStoredFunctions caller
    let calls = storedFunctionCalls.Value |> Option.defaultValue []
    let key = routine.Schema.ToLowerInvariant(), routine.Name.ToLowerInvariant()

    if List.contains key calls then
        raise (Diagnostics.EvaluationError(1424, "Recursive stored functions and triggers are not allowed"))

    match Auth.checkForAccount caller.Store (accountOf caller) [ "EXECUTE", Auth.OnDb routine.Schema ] with
    | Error(code, message) -> raise (Diagnostics.EvaluationError(code, message))
    | Ok() -> ()

    let executionAccount =
        if routine.SecurityType.Equals("INVOKER", StringComparison.OrdinalIgnoreCase) then
            accountOf caller
        else
            match Auth.tryParseAccount routine.Definer with
            | Some account when Auth.tryUserRowForAccount caller.Store account |> Option.isSome -> account
            | _ -> raise (Diagnostics.EvaluationError(1449, sprintf "The user specified as a definer ('%s') does not exist" routine.Definer))

    let options = SqlMode.parserOptionsFor routine.SqlMode

    let parameters, returnType, statements =
        match parseFunctionDefinition options routine.Parameters routine.ReturnType routine.Definition with
        | Ok definition -> definition
        | Error error -> raiseFunctionError error

    let expressionPrivileges =
        let expressions =
            statements
            |> List.collect StoredProgram.expressions
            |> List.collect (Auth.requiredPrivilegesForExpression routine.Schema)

        let cursorQueries =
            statements
            |> List.collect StoredProgram.sqlStatements
            |> List.collect (Auth.requiredPrivilegesInStore caller.Store routine.Schema)

        List.distinct (expressions @ cursorQueries)

    match Auth.checkForAccount caller.Store executionAccount expressionPrivileges with
    | Error(code, message) -> raise (Diagnostics.EvaluationError(code, message))
    | Ok() -> ()

    if parameters.Length <> arguments.Length then
        raise (
            Diagnostics.EvaluationError(
                1318,
                sprintf
                    "Incorrect number of arguments for FUNCTION %s.%s; expected %d, got %d"
                    routine.Schema
                    routine.Name
                    parameters.Length
                    arguments.Length
            )
        )

    let executionStore = Session.currentStore caller
    let capturedSettings =
        storedExecutionSettings routine.SqlMode routine.CharacterSetClient routine.CollationConnection

    let executionSession =
        { caller with
            User = executionAccount.Name
            AccountHost = executionAccount.Host
            Database = Some routine.Schema
            RoutineStack = ("FUNCTION", routine.Schema, routine.Name) :: caller.RoutineStack
            Variables =
                storedExecutionVariables
                    routine.SqlMode
                    routine.CharacterSetClient
                    routine.CollationConnection
                    caller.Variables }

    let initializeParameter ((parameter: StoredProgram.Parameter), value) =
        let column = routineColumn parameter.Name parameter.ColumnType

        coerceRoutineValue executionStore column value
        |> Result.map (fun value -> parameter.Name, { Executor.RoutineVariable.Column = column; Value = value })

    let locals =
        match List.zip parameters arguments |> traverse initializeParameter with
        | Ok initialized -> Map.ofList initialized
        | Error error -> raiseFunctionError error

    let run () =
        runRoutineStatements
            executionStore
            executeParsed
            (fun current _ -> current, Err(1235, "SQL statements in stored functions are not supported"))
            executionSession
            locals
            statements

    let outcome =
        DynamicScope.withValue storedFunctionCalls (Some(key :: calls)) (fun () ->
            DynamicScope.withValue storedFunctionSession (Some executionSession) (fun () ->
                Storage.withExecutionSettings executionStore capturedSettings run))

    match outcome.Error, outcome.Results, outcome.Flow with
    | Some error, _, _ -> raiseFunctionError error
    | None, _ :: _, _ -> raise (Diagnostics.EvaluationError(1415, "Not allowed to return a result set from a function"))
    | None, [], StoredProgram.Flow.Return value ->
        let column = routineColumn routine.Name returnType

        match coerceRoutineValue executionStore column value with
        | Ok value -> value
        | Error error -> raiseFunctionError error
    | None, [], _ ->
        raise (
            Diagnostics.EvaluationError(
                1321,
                sprintf "FUNCTION %s.%s ended without RETURN" routine.Schema routine.Name
            )
        )

and private withStoredFunctions current =
    let functions =
        match Storage.scanList current.Store "mysql" "functions" with
        | Ok(_, rows) -> rows |> List.choose SystemCatalog.StoredFunction.tryRead
        | Error _ -> []

    let register name routine registry =
        let invoke = invokeStoredFunction current routine

        match Parser.parseColumnTypeWithOptions (SqlMode.parserOptionsFor routine.SqlMode) routine.ReturnType with
        | Error _ -> Functions.registerScalar name invoke registry
        | Ok columnType ->
            let metadata =
                let metadata = ColumnWire.metadataOfType columnType

                match columnType with
                | TChar _
                | TVarchar _
                | TTinyText
                | TText
                | TMediumText
                | TLongText
                | TEnum _
                | TSet _ ->
                    let collationId =
                        Collation.idAndSortlen
                        |> Map.tryFind routine.CollationConnection
                        |> Option.map (fst >> uint16)

                    { metadata with CollationId = collationId }
                | _ -> metadata

            Functions.registerScalarWithMetadata name metadata invoke registry

    let registry =
        functions
        |> List.fold
            (fun registry routine -> register (routine.Schema + "." + routine.Name) routine registry)
            current.CustomFunctions

    { current with CustomFunctions = registry }

let private withStoredFunctionRegistry session execute =
    let customFunctions = session.CustomFunctions
    let decorated = withStoredFunctions session
    let executed, result = execute decorated
    { executed with CustomFunctions = customFunctions }, result

let private normalizeDispatchedSql parserOptions rawSql =
    (Parser.stripVersionCommentsWithOptions parserOptions rawSql).Trim().TrimEnd(';').Trim()

/// Binds prepared-statement parameter `Value`s into a parsed `Statement`,
/// replacing every `Placeholder i` with `Lit values.[i]`. Total — after this
/// no `Placeholder` survives and the statement executes through the ordinary
/// path. This walk is the one place that must touch every expression position
/// in the AST, so a placeholder in any legal spot gets bound; DDL is left as
/// the `_` pass-through since MySQL never accepts a `?` there.
let rec private dispatch (session: Session) (rawSql: string) : Session * QueryResult =
    let parserOptions = parserOptionsForSession session
    let sql = normalizeDispatchedSql parserOptions rawSql

    dispatchNormalized session rawSql parserOptions sql

and private dispatchNormalized session rawSql parserOptions sql =

    // A mysqldump preamble/postamble is a run of `/*!NNNNN ... */;` lines;
    // once the version comment above strips down to nothing (or this was a
    // plain `/* ... */`/`-- ...` comment to begin with), what's left is a
    // no-op, same as real MySQL's `Query OK, 0 rows affected` for it —
    // not a syntax error.
    let runTextPrepared command =
        match command with
        | PrepareText(name, source) ->
            let session = { session with TextStatements = Map.remove name session.TextStatements }

            let sql =
                match source with
                | PreparedLiteral text -> Some text
                | PreparedVariable variable ->
                    session.UserVariables |> Map.tryFind variable.Name |> Option.bind toText

            match sql with
            | None -> session, syntaxError "PREPARE source"
            | Some sql ->
                match prepareStatementForSession session sql with
                | Error(code, message) -> session, Err(code, message)
                | Ok(ast, count) when session.TextStatements.Count + session.Statements.Count < Limits.maxPreparedStmtCount ->
                    let statement =
                        { Ast = ast
                          Sql = sql
                          ParamCount = count
                          LastParamTypes = None }

                    { session with TextStatements = Map.add name statement session.TextStatements }, Affected 0UL
                | Ok _ ->
                    session,
                    Err(
                        1461,
                        sprintf
                            "Can't create more than max_prepared_stmt_count statements (current value: %d)"
                            Limits.maxPreparedStmtCount
                    )
        | ExecuteText(name, variables) ->
            match Map.tryFind name session.TextStatements with
            | None -> session, Err(1243, sprintf "Unknown prepared statement handler (%s) given to EXECUTE" name)
            | Some statement when statement.ParamCount <> variables.Length ->
                session,
                Err(
                    1210,
                    sprintf "Incorrect arguments to EXECUTE (expected %d, got %d)" statement.ParamCount variables.Length
                )
            | Some statement ->
                let values =
                    variables
                    |> List.map (fun variable -> session.UserVariables |> Map.tryFind variable.Name |> Option.defaultValue VNull)

                match statement.Ast with
                | Some ast ->
                    withStoredFunctionRegistry session (fun current ->
                        executeParsed current (bindPlaceholders ast values))
                | None ->
                    dispatch
                        session
                        (substitutePlaceholdersWithOptions
                            parserOptions
                            statement.Sql
                            (values |> List.map (valueToSqlLiteralWithOptions parserOptions)))
        | DeallocateText name ->
            if Map.containsKey name session.TextStatements then
                { session with TextStatements = Map.remove name session.TextStatements }, Affected 0UL
            else
                session, Err(1243, sprintf "Unknown prepared statement handler (%s) given to DEALLOCATE PREPARE" name)

    let routineEntries () =
        match Storage.scanList session.Store "mysql" "routines" with
        | Ok(_, rows) -> rows |> List.choose SystemCatalog.Routine.tryRead
        | Error _ -> []

    let functionEntries () =
        match Storage.scanList session.Store "mysql" "functions" with
        | Ok(_, rows) -> rows |> List.choose SystemCatalog.StoredFunction.tryRead
        | Error _ -> []

    let runTextRoutine session command =
        let authorize privilege database =
            Auth.checkForAccount session.Store (accountOf session) [ privilege, Auth.OnDb database ]

        match command with
        | CreateProcedure(qualifiedName, parameters, securityType, body, requestedDefiner) ->
            let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName
            let definer = requestedDefinerAccount session requestedDefiner
            let mayChooseDefiner = canUseRequestedDefiner session requestedDefiner

            match authorize "CREATE ROUTINE" database with
            | Error(code, message) -> session, Err(code, message)
            | Ok() when not mayChooseDefiner ->
                session,
                Err(1227, "Access denied; you need (at least one of) the SUPER or SET_ANY_DEFINER privilege(s) for this operation")
            | Ok() when routineEntries () |> List.exists (SystemCatalog.Routine.matches database name) ->
                session, Err(1304, sprintf "PROCEDURE %s already exists" name)
            | Ok() ->
                let options = SqlMode.parserOptionsFor session.Store.ExecutionSettings.SqlModeText

                match parseRoutineDefinition options parameters body with
                | Error error -> session, error
                | Ok _ ->
                    if Auth.tryUserRowForAccount session.Store definer |> Option.isNone then
                        Diagnostics.note
                            1449
                            (sprintf "The user specified as a definer ('%s'@'%s') does not exist" definer.Name definer.Host)

                    match
                        Storage.insertRows
                            session.Store
                            "mysql"
                            "routines"
                            (Some
                                [ "routine_schema"; "routine_name"; "routine_definition"; "created"; "definer"
                                  "parameter_definition"; "security_type"; "sql_mode"; "character_set_client"
                                  "collation_connection"; "database_collation" ])
                            [ [ VString database
                                VString name
                                VString body
                                VDateTime DateTime.Now
                                VString(Auth.formatAccount definer)
                                VString parameters
                                VString securityType
                                VString session.Store.ExecutionSettings.SqlModeText
                                VString session.Store.ExecutionSettings.ConnectionCharset
                                VString session.Store.ExecutionSettings.ConnectionCollation.Name
                                VString Collation.defaultCollation.Name ] ]
                    with
                    | Ok _ -> session, Affected 0UL
                    | Error error ->
                        let code, message = Storage.toMySqlError error
                        session, Err(code, message)
        | CreateFunction(qualifiedName, ifNotExists, parameters, returnType, characteristics, body, requestedDefiner) ->
            let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName
            let definer = requestedDefinerAccount session requestedDefiner
            let mayChooseDefiner = canUseRequestedDefiner session requestedDefiner

            match Storage.databaseExists (Session.currentStore session) database, authorize "CREATE ROUTINE" database with
            | false, _ -> session, Err(1049, sprintf "Unknown database '%s'" database)
            | true, Error(code, message) -> session, Err(code, message)
            | true, Ok() when not mayChooseDefiner ->
                session,
                Err(1227, "Access denied; you need (at least one of) the SUPER or SET_ANY_DEFINER privilege(s) for this operation")
            | true, Ok() when functionEntries () |> List.exists (SystemCatalog.StoredFunction.matches database name) && ifNotExists ->
                Diagnostics.note 1304 (sprintf "FUNCTION %s already exists" name)
                session, Affected 0UL
            | true, Ok() when functionEntries () |> List.exists (SystemCatalog.StoredFunction.matches database name) ->
                session, Err(1304, sprintf "FUNCTION %s already exists" name)
            | true, Ok() ->
                let options = SqlMode.parserOptionsFor session.Store.ExecutionSettings.SqlModeText

                match parseFunctionCharacteristics characteristics, parseFunctionDefinition options parameters returnType body with
                | Error error, _
                | _, Error error -> session, error
                | Ok(securityType, deterministic, dataAccess), Ok(_, parsedReturnType, _) ->
                    if Auth.tryUserRowForAccount session.Store definer |> Option.isNone then
                        Diagnostics.note
                            1449
                            (sprintf "The user specified as a definer ('%s'@'%s') does not exist" definer.Name definer.Host)

                    match
                        Storage.insertRows
                            session.Store
                            "mysql"
                            "functions"
                            (Some
                                [ "function_schema"; "function_name"; "return_type"; "function_definition"; "created"
                                  "definer"; "parameter_definition"; "security_type"; "is_deterministic"; "sql_data_access"
                                  "sql_mode"; "character_set_client"; "collation_connection"; "database_collation" ])
                            [ [ VString database
                                VString name
                                VString(InformationSchema.columnTypeText parsedReturnType)
                                VString body
                                VDateTime DateTime.Now
                                VString(Auth.formatAccount definer)
                                VString parameters
                                VString securityType
                                VString(if deterministic then "YES" else "NO")
                                VString dataAccess
                                VString session.Store.ExecutionSettings.SqlModeText
                                VString session.Store.ExecutionSettings.ConnectionCharset
                                VString session.Store.ExecutionSettings.ConnectionCollation.Name
                                VString Collation.defaultCollation.Name ] ]
                    with
                    | Ok _ -> session, Affected 0UL
                    | Error error ->
                        let code, message = Storage.toMySqlError error
                        session, Err(code, message)
        | CallProcedure(qualifiedName, arguments) ->
            let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName
            let recursive =
                session.RoutineStack
                |> List.exists (fun (activeType, activeDatabase, activeName) ->
                    activeType = "PROCEDURE"
                    &&
                    activeDatabase.Equals(database, StringComparison.OrdinalIgnoreCase)
                    && activeName.Equals(name, StringComparison.OrdinalIgnoreCase))

            match recursive, routineEntries () |> List.tryFind (SystemCatalog.Routine.matches database name) with
            | true, _ ->
                session,
                Err(
                    1456,
                    sprintf
                        "Recursive limit 0 (as set by the max_sp_recursion_depth variable) was exceeded for routine %s"
                        name
                )
            | false, None -> session, Err(1305, sprintf "PROCEDURE %s does not exist" name)
            | false, Some routine ->
                match authorize "EXECUTE" database with
                | Error(code, message) -> session, Err(code, message)
                | Ok() ->
                    let callerOptions = parserOptionsForSession session
                    let routineOptions = SqlMode.parserOptionsFor routine.SqlMode

                    match
                        parseRoutineDefinition routineOptions routine.Parameters routine.Definition,
                        StoredProgram.parseArguments callerOptions arguments
                    with
                    | Error error, _ -> session, error
                    | _, Error _ -> session, syntaxError arguments
                    | Ok(parameters, statements), Ok callArguments when parameters.Length <> callArguments.Length ->
                        session,
                        Err(
                            1318,
                            sprintf
                                "Incorrect number of arguments for PROCEDURE %s.%s; expected %d, got %d"
                                database
                                name
                                parameters.Length
                                callArguments.Length
                        )
                    | Ok(parameters, statements), Ok callArguments ->
                        let executionAccount =
                            if routine.SecurityType.Equals("INVOKER", StringComparison.OrdinalIgnoreCase) then
                                Ok(accountOf session)
                            else
                                match Auth.tryParseAccount routine.Definer with
                                | Some account when Auth.tryUserRowForAccount session.Store account |> Option.isSome -> Ok account
                                | _ -> Error(Err(1449, sprintf "The user specified as a definer ('%s') does not exist" routine.Definer))

                        match executionAccount, bindRoutineArguments database name session parameters callArguments with
                        | Error error, _
                        | _, Error error -> session, error
                        | Ok account, Ok(callerSession, parameterValues, outputs) ->
                            let executionStore = Session.currentStore callerSession
                            let originalSettings = Storage.executionSettings executionStore
                            let capturedSettings =
                                storedExecutionSettings routine.SqlMode routine.CharacterSetClient routine.CollationConnection
                            let executionSession =
                                { callerSession with
                                    User = account.Name
                                    AccountHost = account.Host
                                    Database = Some routine.Schema
                                    RoutineStack = ("PROCEDURE", routine.Schema, routine.Name) :: callerSession.RoutineStack
                                    Variables =
                                        storedExecutionVariables
                                            routine.SqlMode
                                            routine.CharacterSetClient
                                            routine.CollationConnection
                                            callerSession.Variables }

                            let mutable resultingSettings = capturedSettings

                            let executeBody () =
                                let initializeVariable ((parameter: StoredProgram.Parameter), value) =
                                    let column = routineColumn parameter.Name parameter.ColumnType

                                    coerceRoutineValue executionStore column value
                                    |> Result.map (fun value -> parameter.Name, { Executor.RoutineVariable.Column = column; Value = value })

                                match parameterValues |> traverse initializeVariable with
                                | Error error -> executionSession, error
                                | Ok initialized ->
                                    let locals = Map.ofList initialized

                                    let run =
                                        runRoutineStatements
                                            executionStore
                                            executeParsed
                                            dispatch
                                            executionSession
                                            locals
                                            statements

                                    let executed = run.Session
                                    let results = run.Results
                                    let affectedRows = run.AffectedRows

                                    let outputValues =
                                        outputs
                                        |> List.choose (fun (parameter, target) ->
                                            run.Locals
                                            |> Map.tryFind parameter.Name
                                            |> Option.map (fun local -> target, local.Value))

                                    let updatedOutputs =
                                        match run.Error with
                                        | Some error -> Error error
                                        | None ->
                                            applyProcedureOutputs
                                                executionStore
                                                executed.UserVariables
                                                (Executor.currentRoutineVariables ())
                                                outputValues

                                    match updatedOutputs with
                                    | Ok(_, Some locals) -> Executor.replaceRoutineVariables locals
                                    | _ -> ()

                                    let result =
                                        match results, updatedOutputs with
                                        | [], Error error -> error
                                        | results, Error error -> MultipleResults(results @ [ error, [] ])
                                        | [], Ok _ -> Affected affectedRows
                                        | results, Ok _ -> MultipleResults(results @ [ Affected affectedRows, [] ])

                                    let executed =
                                        match updatedOutputs with
                                        | Error _ -> executed
                                        | Ok(variables, _) -> { executed with UserVariables = variables }

                                    executed, result

                            let (executed, result), changed =
                                Storage.withExecutionSettings executionStore capturedSettings (fun () ->
                                    let outcome, changed = captureRoutineVariableChanges executeBody
                                    resultingSettings <- Storage.executionSettings executionStore
                                    outcome, changed)

                            mergeRoutineExecutionSettings originalSettings changed resultingSettings
                            |> Storage.setExecutionSettings executionStore

                            { executed with
                                User = session.User
                                AccountHost = session.AccountHost
                                Database = session.Database
                                RoutineStack = session.RoutineStack
                                Variables = restoreRoutineVariables session.Variables changed executed.Variables },
                            result
        | DropProcedure(qualifiedName, ifExists) ->
            let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName
            let exists = routineEntries () |> List.exists (SystemCatalog.Routine.matches database name)

            match authorize "ALTER ROUTINE" database with
            | Error(code, message) -> session, Err(code, message)
            | Ok() when not exists && not ifExists -> session, Err(1305, sprintf "PROCEDURE %s does not exist" name)
            | Ok() when not exists -> session, Affected 0UL
            | Ok() ->
                match Storage.deleteRows session.Store "mysql" "routines" (SystemCatalog.Routine.rowMatches database name >> Ok) with
                | Ok _ -> session, Affected 0UL
                | Error error ->
                    let code, message = Storage.toMySqlError error
                    session, Err(code, message)
        | DropFunction(qualifiedName, ifExists) ->
            let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName
            let exists = functionEntries () |> List.exists (SystemCatalog.StoredFunction.matches database name)

            match authorize "ALTER ROUTINE" database with
            | Error(code, message) -> session, Err(code, message)
            | Ok() when not exists && not ifExists -> session, Err(1305, sprintf "FUNCTION %s does not exist" name)
            | Ok() when not exists -> session, Affected 0UL
            | Ok() ->
                match Storage.deleteRows session.Store "mysql" "functions" (SystemCatalog.StoredFunction.rowMatches database name >> Ok) with
                | Ok _ -> session, Affected 0UL
                | Error error ->
                    let code, message = Storage.toMySqlError error
                    session, Err(code, message)

    let eventEntries session =
        match Storage.scanList session.Store "mysql" "events" with
        | Ok(_, rows) -> rows |> List.choose SystemCatalog.Event.tryRead
        | Error _ -> []

    let runTextEvent session command =
        let authorize database =
            Auth.checkForAccount session.Store (accountOf session) [ "EVENT", Auth.OnDb database ]

        let resolveDefiner (requested: string option) =
            let definer = requestedDefinerAccount session requested

            if canUseRequestedDefiner session requested then
                Ok definer
            else
                Error(
                    1227,
                    "Access denied; you need (at least one of) the SUPER or SET_ANY_DEFINER privilege(s) for this operation"
                )

        let executionContext () =
            let timeZone = session.Variables |> Map.tryFind "time_zone" |> Option.flatten |> Option.defaultValue "SYSTEM"

            session.Store.ExecutionSettings.SqlModeText,
            timeZone,
            session.Store.ExecutionSettings.ConnectionCharset,
            session.Store.ExecutionSettings.ConnectionCollation.Name,
            (session.Variables
             |> Map.tryFind "collation_database"
             |> Option.flatten
             |> Option.defaultValue Collation.defaultCollation.Name)

        let sameEvent leftSchema leftName rightSchema rightName =
            String.Equals(leftSchema, rightSchema, StringComparison.OrdinalIgnoreCase)
            && String.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase)

        match command with
        | Event.Create creation ->
            let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) creation.Name
            let exists = eventEntries session |> List.exists (SystemCatalog.Event.matches database name)

            if creation.Comment.EnumerateRunes() |> Seq.length > 2048 then
                session, Err(3507, "Failed to update events dictionary object.")
            else
                match authorize database, resolveDefiner creation.Definer with
                | Error(code, message), _ -> session, Err(code, message)
                | _, Error(code, message) -> session, Err(code, message)
                | Ok(), Ok definer ->
                    let next, timing = evaluateEventTiming session parserOptions creation.Schedule

                    match timing with
                    | Error result -> next, result
                    | Ok _ when exists && creation.IfNotExists ->
                        Diagnostics.note 1537 (sprintf "Event '%s' already exists" name)
                        next, Affected 0UL
                    | Ok _ when exists -> next, Err(1537, sprintf "Event '%s' already exists" name)
                    | Ok timing ->
                        let now = Functions.truncateToSecond DateTime.Now
                        let status, discard =
                            match timing with
                            | Event.Timing.OneTime executeAt when executeAt < now && creation.OnCompletion = "NOT PRESERVE" ->
                                Diagnostics.note
                                    1588
                                    "Event execution time is in the past and ON COMPLETION NOT PRESERVE is set. The event was dropped immediately after creation."

                                creation.Status, true
                            | Event.Timing.OneTime executeAt when executeAt < now ->
                                Diagnostics.note 1544 "Event execution time is in the past. Event has been disabled"
                                Event.Status.Disabled, false
                            | _ -> creation.Status, false

                        if discard then
                            next, Affected 0UL
                        else
                            if Auth.tryUserRowForAccount next.Store definer |> Option.isNone then
                                Diagnostics.note
                                    1449
                                    (sprintf "The user specified as a definer ('%s'@'%s') does not exist" definer.Name definer.Host)

                            let created = DateTime.Now
                            let sqlMode, timeZone, characterSetClient, collationConnection, databaseCollation = executionContext ()
                            let executeAt, intervalValue, intervalField, starts, ends = Event.timingFields timing
                            let dateValue = Option.map VDateTime >> Option.defaultValue VNull
                            let textValue = Option.map VString >> Option.defaultValue VNull

                            match
                                Storage.insertRows
                                    next.Store
                                    "mysql"
                                    "events"
                                    (Some
                                        [ "event_schema"; "event_name"; "schedule_definition"; "event_definition"; "created"
                                          "definer"; "status"; "on_completion"; "event_comment"; "last_altered"; "last_executed"
                                          "sql_mode"; "time_zone"; "character_set_client"; "collation_connection"
                                          "database_collation"; "originator"; "execute_at"; "interval_value"; "interval_field"
                                          "starts"; "ends" ])
                                    [ [ VString database
                                        VString name
                                        VString creation.Schedule
                                        VString creation.Body
                                        VDateTime created
                                        VString(Auth.formatAccount definer)
                                        (status |> Event.statusText |> VString)
                                        VString creation.OnCompletion
                                        VString creation.Comment
                                        VDateTime created
                                        VNull
                                        VString sqlMode
                                        VString timeZone
                                        VString characterSetClient
                                        VString collationConnection
                                        VString databaseCollation
                                        VUInt 1UL
                                        dateValue executeAt
                                        textValue intervalValue
                                        textValue intervalField
                                        dateValue starts
                                        dateValue ends ] ]
                            with
                            | Ok _ -> next, Affected 0UL
                            | Error error ->
                                let code, message = Storage.toMySqlError error
                                next, Err(code, message)
        | Event.Alter alteration ->
            let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) alteration.Name
            let renamedDatabase, renamedName =
                alteration.RenameTo
                |> Option.map (splitQualified database)
                |> Option.defaultValue (database, name)

            let existing = eventEntries session |> List.tryFind (SystemCatalog.Event.matches database name)
            let targetExists =
                not (sameEvent database name renamedDatabase renamedName)
                && (eventEntries session |> List.exists (SystemCatalog.Event.matches renamedDatabase renamedName))

            match
                Storage.databaseExists (Session.currentStore session) database,
                Storage.databaseExists (Session.currentStore session) renamedDatabase,
                authorize database,
                (if sameEvent database name renamedDatabase renamedName then Ok() else authorize renamedDatabase),
                resolveDefiner alteration.Definer,
                existing
            with
            | false, _, _, _, _, _ -> session, Err(1049, sprintf "Unknown database '%s'" database)
            | _, false, _, _, _, _ -> session, Err(1049, sprintf "Unknown database '%s'" renamedDatabase)
            | _, _, Error(code, message), _, _, _
            | _, _, _, Error(code, message), _, _
            | _, _, _, _, Error(code, message), _ -> session, Err(code, message)
            | _, _, _, _, _, None -> session, Err(1539, sprintf "Unknown event '%s'" name)
            | _ when targetExists -> session, Err(1537, sprintf "Event '%s' already exists" renamedName)
            | _ when alteration.Comment |> Option.exists (fun comment -> comment.EnumerateRunes() |> Seq.length > 2048) ->
                session, Err(3507, "Failed to update events dictionary object.")
            | _, _, _, _, Ok definer, Some current ->
                let next, timing =
                    match alteration.Schedule with
                    | None -> session, Ok None
                    | Some schedule ->
                        let next, result = evaluateEventTiming session parserOptions schedule
                        next, result |> Result.map Some

                match timing with
                | Error result -> next, result
                | Ok timing ->
                    let effectiveTiming = timing |> Option.orElseWith (fun () -> SystemCatalog.Event.timing current)
                    let effectiveCompletion = alteration.OnCompletion |> Option.defaultValue current.OnCompletion
                    let now = Functions.truncateToSecond DateTime.Now

                    match effectiveTiming with
                    | Some(Event.Timing.OneTime executeAt) when executeAt < now && effectiveCompletion = "NOT PRESERVE" ->
                        next,
                        Err(
                            1589,
                            "Event execution time is in the past and ON COMPLETION NOT PRESERVE is set. The event was not changed. Specify a time in the future."
                        )
                    | _ ->
                        let status =
                            match effectiveTiming with
                            | Some(Event.Timing.OneTime executeAt) when executeAt < now ->
                                Diagnostics.note 1544 "Event execution time is in the past. Event has been disabled"
                                Some Event.Status.Disabled
                            | _ -> alteration.Status

                        if Auth.tryUserRowForAccount next.Store definer |> Option.isNone then
                            Diagnostics.note
                                1449
                                (sprintf "The user specified as a definer ('%s'@'%s') does not exist" definer.Name definer.Host)

                        let sqlMode, timeZone, characterSetClient, collationConnection, databaseCollation = executionContext ()

                        let update (row: Value[]) =
                            let updated = Array.copy row
                            updated.[0] <- VString renamedDatabase
                            updated.[1] <- VString renamedName
                            alteration.Schedule |> Option.iter (fun value -> updated.[2] <- VString value)
                            alteration.Body |> Option.iter (fun value -> updated.[3] <- VString value)
                            updated.[5] <- VString(Auth.formatAccount definer)
                            status |> Option.iter (Event.statusText >> VString >> fun value -> updated.[6] <- value)
                            alteration.OnCompletion |> Option.iter (fun value -> updated.[7] <- VString value)
                            alteration.Comment |> Option.iter (fun value -> updated.[8] <- VString value)
                            updated.[9] <- VDateTime DateTime.Now
                            updated.[11] <- VString sqlMode
                            updated.[12] <- VString timeZone
                            updated.[13] <- VString characterSetClient
                            updated.[14] <- VString collationConnection
                            updated.[15] <- VString databaseCollation

                            timing
                            |> Option.iter (fun value ->
                                let executeAt, intervalValue, intervalField, starts, ends = Event.timingFields value
                                updated.[10] <- VNull
                                updated.[17] <- executeAt |> Option.map VDateTime |> Option.defaultValue VNull
                                updated.[18] <- intervalValue |> Option.map VString |> Option.defaultValue VNull
                                updated.[19] <- intervalField |> Option.map VString |> Option.defaultValue VNull
                                updated.[20] <- starts |> Option.map VDateTime |> Option.defaultValue VNull
                                updated.[21] <- ends |> Option.map VDateTime |> Option.defaultValue VNull)

                            Ok updated

                        match
                            Storage.updateRows
                                next.Store
                                "mysql"
                                "events"
                                None
                                (SystemCatalog.Event.rowMatches database name >> Ok)
                                update
                        with
                        | Ok _ -> next, Affected 0UL
                        | Error error ->
                            let code, message = Storage.toMySqlError error
                            next, Err(code, message)
        | Event.Drop(qualifiedName, ifExists) ->
            let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName
            let exists = eventEntries session |> List.exists (SystemCatalog.Event.matches database name)

            match authorize database with
            | Error(code, message) -> session, Err(code, message)
            | Ok() when not exists && not ifExists -> session, Err(1539, sprintf "Unknown event '%s'" name)
            | Ok() when not exists -> session, Affected 0UL
            | Ok() ->
                match Storage.deleteRows session.Store "mysql" "events" (SystemCatalog.Event.rowMatches database name >> Ok) with
                | Ok _ -> session, Affected 0UL
                | Error error ->
                    let code, message = Storage.toMySqlError error
                    session, Err(code, message)

    if Parser.isBlank sql then
        session, Affected 0UL
    else
        match StoredProgram.parseDiagnostics parserOptions sql with
        | Error _ -> session, syntaxError sql
        | Ok(Some diagnostics) -> runTextDiagnostics session diagnostics
        | Ok None ->
            match Event.tryCommand parserOptions (validEventBody parserOptions) sql with
            | Some command -> runTextEvent (commitSession session) command
            | None ->
                match tryTextRoutineCommand sql with
                | Some(CallProcedure _ as command) ->
                    withStoredFunctionRegistry session (fun current -> runTextRoutine current command)
                | Some command -> runTextRoutine (commitSession session) command
                | None ->
                    match tryTextPreparedCommand sql with
                    | Error result -> session, result
                    | Ok(Some command) -> runTextPrepared command
                    | Ok None when not (placeholderPositionsWithOptions parserOptions sql |> List.isEmpty) ->
                        // A `?` outside a string/comment is a bind parameter, only
                        // legal via COM_STMT_PREPARE. Rejecting it here also keeps
                        // unreachable placeholders out of persisted expressions.
                        session, syntaxError sql
                    | Ok None ->
                        let upper = sql.ToUpperInvariant()

                        match tryProbe sql upper with
                        | Some probe ->
                            // Probe results contain rendered strings rather than
                            // values from which descriptors can be inferred.
                            let session, result = runProbe session sql probe
                            { session with LastResultColumnMetadata = completeResultMetadata session result [] }, result
                        | None -> withStoredFunctionRegistry session (fun current -> executeStatement current sql rawSql)

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
        match terminalResult result with
        | TerminalAffected count ->
            { session with
                LastRowCount = int64 count
                FoundRows = 0UL
                PendingFoundRows = None }
        | TerminalResultSet rows ->
            { session with
                LastRowCount = -1L
                FoundRows = session.PendingFoundRows |> Option.defaultValue (uint64 rows.Length)
                PendingFoundRows = None }
        | TerminalError _ -> { session with LastRowCount = -1L; PendingFoundRows = None }

    session, result

let private abortTransaction (session: Session) =
    session.Tx |> Option.iter (fun transaction -> Storage.releaseTransactionLocks transaction.Snapshot)
    { session with Tx = None }

let private recoverExecutionError (session: Session) (description: string) (error: exn) : Session * QueryResult =
    match error with
    | Storage.LockWaitTimeout dbName ->
        Log.diagnostic "fsdb: ERR 1205 lock wait timeout on database %s -- %s" dbName description
        session, Err(1205, "Lock wait timeout exceeded; try restarting transaction")
    // MySQL's 1690 message names the offending expression; fsdb
    // needs an AST printer before it can do the same without reconstructing SQL.
    | Value.UnsignedOutOfRange ->
        Log.diagnostic "fsdb: ERR 1690 unsigned out of range -- %s" description
        session, Err(1690, "BIGINT UNSIGNED value is out of range")
    | Value.SignedOutOfRange ->
        Log.diagnostic "fsdb: ERR 1690 signed out of range -- %s" description
        session, Err(1690, "BIGINT value is out of range")
    // Extension functions may fail after an effect, so their chosen SQL error
    // must not leave a transaction containing partially applied state.
    | Functions.SqlError(code, message) ->
        Log.diagnostic "fsdb: ERR %d %s -- %s" code message description
        abortTransaction session, Err(code, message)
    | ex ->
        Log.diagnostic "fsdb: EXN %s -- %s" ex.Message description
        abortTransaction session, Err(1105, "Internal error")

let private preservesDiagnostics parserOptions (sql: string) =
    let upper = sql.Trim().ToUpperInvariant()

    match StoredProgram.parseDiagnostics parserOptions sql with
    | Ok(Some _) -> true
    | _ ->
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
        match terminalErrorInfo result with
        | Some error -> generated @ [ Diagnostics.fromError error ]
        | None -> generated

    let session = if preserve then session else { session with Diagnostics = generated }
    recordResult (session, result)

let private countsAsAccountUpdate = function
    | CreateDatabase _
    | DropDatabase _
    | AlterDatabase _
    | CreateTable _
    | CreateTableLike _
    | CreateTableAs _
    | DropTable _
    | AlterTable _
    | RenameTable _
    | CreateIndex _
    | DropIndexStmt _
    | Insert _
    | InsertSelect _
    | Replace _
    | ReplaceSelect _
    | ReplaceSet _
    | Update _
    | Delete _
    | Truncate _
    | CreateUser _
    | DropUser _
    | RenameUser _
    | AlterUser _
    | CreateRole _
    | DropRole _
    | Grant _
    | Revoke _
    | CreateTrigger _
    | SetTriggerNew _
    | DropTrigger _
    | CreateView _
    | DropView _ -> true
    | Select _
    | Do _
    | Union _
    | ChecksumTables _
    | Explain _ -> false

type private AccountStatement =
    | ProbedAccountStatement of Probe
    | ParsedAccountStatement of Statement
    | TextAccountUpdate of authorized: bool
    | UnknownAccountStatement

let private textRoutineUpdateAuthorization session = function
    | CallProcedure _ -> None
    | CreateProcedure(qualifiedName, _, _, _, requestedDefiner)
    | CreateFunction(qualifiedName, _, _, _, _, _, requestedDefiner) ->
        let database, _ = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName

        Auth.checkForAccount session.Store (accountOf session) [ "CREATE ROUTINE", Auth.OnDb database ]
        |> Result.map (fun () -> canUseRequestedDefiner session requestedDefiner)
        |> Result.defaultValue false
        |> Some
    | DropProcedure(qualifiedName, _)
    | DropFunction(qualifiedName, _) ->
        let database, _ = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName

        Auth.checkForAccount session.Store (accountOf session) [ "ALTER ROUTINE", Auth.OnDb database ]
        |> Result.isOk
        |> Some

let private textEventUpdateIsAuthorized session = function
    | Event.Create creation ->
        let database, _ = splitQualified (session.Database |> Option.defaultValue defaultDatabase) creation.Name

        Auth.checkForAccount session.Store (accountOf session) [ "EVENT", Auth.OnDb database ]
        |> Result.map (fun () -> canUseRequestedDefiner session creation.Definer)
        |> Result.defaultValue false
    | Event.Alter alteration ->
        let database, _ = splitQualified (session.Database |> Option.defaultValue defaultDatabase) alteration.Name

        Auth.checkForAccount session.Store (accountOf session) [ "EVENT", Auth.OnDb database ]
        |> Result.map (fun () -> canUseRequestedDefiner session alteration.Definer)
        |> Result.defaultValue false
    | Event.Drop(qualifiedName, _) ->
        let database, _ = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName
        Auth.checkForAccount session.Store (accountOf session) [ "EVENT", Auth.OnDb database ] |> Result.isOk

let rec private parsedAccountStatement session parserOptions depth sql =
    match tryProbe sql (sql.ToUpperInvariant()) with
    | Some probe -> ProbedAccountStatement probe
    | None ->
        match parseStatement parserOptions sql with
        | Ok statement -> ParsedAccountStatement statement
        | Error _ ->
            match Event.tryCommand parserOptions (validEventBody parserOptions) sql with
            | Some command -> TextAccountUpdate(textEventUpdateIsAuthorized session command)
            | None ->
                match tryTextRoutineCommand sql with
                | Some command ->
                    command
                    |> textRoutineUpdateAuthorization session
                    |> Option.map TextAccountUpdate
                    |> Option.defaultValue UnknownAccountStatement
                | None when depth > 0 ->
                    match tryTextPreparedCommand sql with
                    | Ok(Some(ExecuteText(name, _))) ->
                        match session.TextStatements |> Map.tryFind name with
                        | Some { Ast = Some statement } -> ParsedAccountStatement statement
                        | Some statement -> parsedAccountStatement session parserOptions (depth - 1) statement.Sql
                        | None -> UnknownAccountStatement
                    | _ -> UnknownAccountStatement
                | None -> UnknownAccountStatement

let private resetsOwnPassword session = function
    | ProbedAccountStatement(SetPassword(user, _)) ->
        user
        |> Option.map accountRefOf
        |> Option.defaultValue (accountOf session)
        |> Auth.sameAccount (accountOf session)
    | ParsedAccountStatement(AlterUser(name, host, Some _, _, _)) ->
        Auth.sameAccount (Auth.account name host) (accountOf session)
    | _ -> false

let private accountStatementCountsAsUpdate = function
    | ProbedAccountStatement(SetPassword _) -> true
    | ParsedAccountStatement statement -> countsAsAccountUpdate statement
    | TextAccountUpdate _ -> true
    | _ -> false

let private accountUpdateIsAuthorized session = function
    | ProbedAccountStatement(SetPassword(user, _)) ->
        let wanted = user |> Option.map accountRefOf |> Option.defaultValue (accountOf session)
        let required = if Auth.sameAccount wanted (accountOf session) then [] else [ "CREATE USER", Auth.Global ]
        Auth.checkForAccount (Session.currentStore session) (accountOf session) required |> Result.isOk
    | ParsedAccountStatement statement ->
        let store = Session.currentStore session
        let database = session.Database |> Option.defaultValue defaultDatabase
        Auth.checkForAccount store (accountOf session) (requiredPrivilegesForStatement session store database statement)
        |> Result.isOk
    | TextAccountUpdate authorized -> authorized
    | ProbedAccountStatement _ -> false
    | UnknownAccountStatement -> false

let handle (session: Session) (rawSql: string) : Session * QueryResult =
    let session = Session.clearSessionStateChanges session
    let parserOptions = parserOptionsForSession session
    let sql = normalizeDispatchedSql parserOptions rawSql
    let account = accountOf session
    let store = Session.currentStore session
    let limits = Auth.tryAccountLimits store account
    let inspectStatement = session.PasswordExpired || (limits |> Option.exists (fun value -> value.MaxUpdates > 0u))
    let accountStatement =
        if inspectStatement then
            parsedAccountStatement session parserOptions 1 sql
        else
            UnknownAccountStatement
    let resetsPassword = resetsOwnPassword session accountStatement
    let countsAsUpdate = accountStatementCountsAsUpdate accountStatement && accountUpdateIsAuthorized session accountStatement

    recordDiagnostics session (preservesDiagnostics parserOptions sql) (fun () ->
        if session.PasswordExpired && not resetsPassword then
            session, Err(1820, "You must reset your password using ALTER USER statement before executing this statement.")
        else
            match
                Auth.tryConsumeAccountStatementWithLimits
                    store
                    account
                    limits
                    countsAsUpdate
            with
            | Error(code, message) -> session, Err(code, message)
            | Ok() ->
                try
                    let executed, result = dispatchNormalized session rawSql parserOptions sql
                    let executed =
                        if resetsPassword && terminalErrorInfo result |> Option.isNone then
                            { executed with PasswordExpired = false }
                        else
                            executed

                    match terminalResult result with
                    | TerminalError(code, msg) ->
                        Log.diagnostic "fsdb: ERR %d %s -- query: %s" code msg (Log.redactSql rawSql)
                        executed, result
                    | _ -> executed, result
                with
                | :? OperationCanceledException ->
                    abortTransaction session |> ignore
                    reraise ()
                | ex -> recoverExecutionError session (sprintf "query: %s" (Log.redactSql rawSql)) ex)

let executeEventBody (session: Session) (body: string) : Session * QueryResult =
    let options = parserOptionsForSession session

    match StoredProgram.parseRoutine options isSupportedStoredProgramText body with
    | Error _ -> session, syntaxError body
    | Ok statements ->
        withStoredFunctionRegistry session (fun current ->
            let outcome =
                runRoutineStatements
                    (Session.currentStore current)
                    executeParsed
                    dispatch
                    current
                    Map.empty
                    statements

            let result =
                match outcome.Results, outcome.Error with
                | [], Some error -> error
                | results, Some error -> MultipleResults(results @ [ error, [] ])
                | [], None -> Affected outcome.AffectedRows
                | results, None -> MultipleResults(results @ [ Affected outcome.AffectedRows, [] ])

            rollbackSession outcome.Session, result)

/// Parses and authorizes a LOCAL INFILE command before the server asks the
/// client to send bytes. The file name is never resolved by the server.
let tryPrepareLocalLoad (session: Session) (sql: string) : Result<Parser.LocalLoad option, QueryResult> =
    let normalized = Parser.stripVersionComments sql |> fun value -> value.TrimStart()

    if not (normalized.StartsWith("LOAD DATA", StringComparison.OrdinalIgnoreCase)) then
        Result.Ok None
    elif session.PasswordExpired then
        Result.Error(Err(1820, "You must reset your password using ALTER USER statement before executing this statement."))
    else
        let store = Session.currentStore session
        let account = accountOf session

        let prepared =
            match Parser.parseLocalLoad sql with
            | Result.Error _ -> Result.Error(syntaxError sql)
            | Result.Ok load ->
                match load.Charset |> Option.map _.ToLowerInvariant() with
                | Some value when value <> "utf8" && value <> "utf8mb4" ->
                    Result.Error(Err(1235, sprintf "LOAD DATA CHARACTER SET %s is not supported" value))
                | _ ->
                    let statement =
                        if load.Replace then
                            Replace(load.Table, load.Columns, [])
                        else
                            Insert(load.Table, load.Columns, [], [], load.Ignore)

                    let database = session.Database |> Option.defaultValue defaultDatabase

                    match Auth.checkForAccount store account (Auth.requiredPrivilegesInStore store database statement) with
                    | Ok() -> Result.Ok(Some load)
                    | Error(code, message) -> Result.Error(Err(code, message))

        let isUpdate =
            match prepared with
            | Result.Ok(Some _) -> true
            | _ -> false

        match Auth.tryConsumeAccountStatementWithLimits store account (Auth.tryAccountLimits store account) isUpdate with
        | Result.Error(code, message) -> Result.Error(Err(code, message))
        | Result.Ok() -> prepared

/// Inserts already-decoded LOCAL INFILE rows through the ordinary INSERT or
/// REPLACE execution path, retaining its coercion, trigger, and transaction
/// behavior.
let executeLocalLoad (session: Session) (load: Parser.LocalLoad) (rows: Value list list) : Session * QueryResult =
    let session = Session.clearSessionStateChanges session
    let statement =
        if load.Replace then
            Replace(load.Table, load.Columns, rows |> List.map (List.map Lit))
        else
            Insert(load.Table, load.Columns, rows |> List.map (List.map Lit), [], load.Ignore)

    recordDiagnostics session false (fun () ->
        try
            withStoredFunctionRegistry session (fun current -> executeParsed current statement)
        with
        | :? OperationCanceledException -> reraise ()
        | ex -> recoverExecutionError session "LOAD DATA LOCAL INFILE" ex)

/// Executes a prepared statement with its bound parameter values. Parser-
/// produced statements bind the values into the parsed AST and run it
/// directly (no re-parse, no SQL escaping); the text-probed forms
/// (SET/SHOW/transaction control, which have no AST) still substitute into
/// `Sql` and go through the ordinary text path.
let executePrepared (session: Session) (stmt: PreparedStmt) (values: Value list) : Session * QueryResult =
    let session = Session.clearSessionStateChanges session

    // The AST path calls `executeParsed` directly, which — unlike `handle` —
    // doesn't convert a stray .NET exception into an `Err`, so a bound value
    // that overflows a temporal/numeric op (or any bug) would otherwise drop
    // the connection with no ERR packet. Give it the same 1105 safety net
    // `handle` gives the text path.
    match stmt.Ast with
    | None ->
        let options = parserOptionsForSession session
        handle
            session
            (substitutePlaceholdersWithOptions
                options
                stmt.Sql
                (values |> List.map (valueToSqlLiteralWithOptions options)))
    | Some ast ->
        recordDiagnostics session false (fun () ->
            try
                let statement = bindPlaceholders ast values
                let resetsPassword = resetsOwnPassword session (ParsedAccountStatement statement)

                if session.PasswordExpired && not resetsPassword then
                    session, Err(1820, "You must reset your password using ALTER USER statement before executing this statement.")
                else
                    let accountStatement = ParsedAccountStatement statement
                    let account = accountOf session
                    let store = Session.currentStore session

                    match
                        Auth.tryConsumeAccountStatementWithLimits
                            store
                            account
                            (Auth.tryAccountLimits store account)
                            (accountStatementCountsAsUpdate accountStatement
                             && accountUpdateIsAuthorized session accountStatement)
                    with
                    | Error(code, message) -> session, Err(code, message)
                    | Ok() ->
                        let executed, result =
                            withStoredFunctionRegistry session (fun current -> executeParsed current statement)

                        (if resetsPassword && terminalErrorInfo result |> Option.isNone then
                             { executed with PasswordExpired = false }
                         else
                             executed),
                        result
            with
            | PlaceholderCountMismatch(expected, got) ->
                session, Err(1210, sprintf "Incorrect arguments to EXECUTE (expected %d, got %d)" expected got)
            | :? OperationCanceledException -> reraise ()
            | ex -> recoverExecutionError session "prepared statement" ex)
