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

let private storedProgramProtectedTables = System.Threading.AsyncLocal<Set<string * string>>()
let private storedFunctionSession = System.Threading.AsyncLocal<Session option>()
let private storedFunctionCalls = System.Threading.AsyncLocal<(string * string) list option>()
let private creatingTable = System.Threading.AsyncLocal<string option>()

let private insideFunctionOrTrigger (session: Session) =
    session.RoutineStack
    |> List.exists (fun (kind, _, _) -> kind = "FUNCTION" || kind = "TRIGGER")

let private triggerTableKey (database: string) table =
    database.ToLowerInvariant(), normalizeTableName table

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

let private hasKeywordPrefix (keyword: string) (text: string) =
    text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
    && (text.Length = keyword.Length || Char.IsWhiteSpace text.[keyword.Length])

/// Preserves the distinction between an unknown variable and a known SQL
/// NULL value.
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

let rec private containsResultSet =
    function
    | ResultSet _ -> true
    | MultipleResults results -> results |> List.exists (fst >> containsResultSet)
    | Affected _
    | Err _ -> false

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

let private isSessionScope (scope: string) : bool =
    scope.IndexOf("SESSION", StringComparison.OrdinalIgnoreCase) >= 0

let private globalScopeOnlyVariables =
    Set.ofList
        [ "activate_all_roles_on_login"
          "connect_timeout"
          "ft_query_expansion_limit"
          "innodb_ft_max_token_size"
          "innodb_ft_min_token_size"
          "mandatory_roles"
          "protocol_compression_algorithms" ]

let private readOnlySystemVariables =
    Set.ofList
        [ "ft_query_expansion_limit"
          "innodb_ft_max_token_size"
          "innodb_ft_min_token_size"
          "protocol_compression_algorithms" ]

let private globalOnlyVariables =
    Set.union
        globalScopeOnlyVariables
        (Set.ofList
            [ "event_scheduler"
              "default_password_lifetime"
              "local_infile"
              "max_allowed_packet"
              "max_connections"
              "max_prepared_stmt_count"
              "net_write_timeout" ])

/// The outer option preserves error 1193 for unknown system variables while
/// the inner option represents SQL NULL.
let private lookupAtRef (session: Session) (sigil: string) (scope: string) (name: string) : string option option =
    let name = name.ToLowerInvariant()

    if sigil = "@@" && not (isGlobalScope scope) && name = "warning_count" then
        Some(Some(string session.Diagnostics.Length))
    elif sigil = "@@" && not (isGlobalScope scope) && name = "error_count" then
        Some(Some(string (session.Diagnostics |> List.filter (fun condition -> condition.Level = Diagnostics.Error) |> List.length)))
    elif sigil = "@@" then
        if isGlobalScope scope || globalScopeOnlyVariables.Contains name then
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
          "connect_timeout"
          "cte_max_recursion_depth"
          "foreign_key_checks"
          "ft_query_expansion_limit"
          "group_concat_max_len"
          "interactive_timeout"
          "innodb_buffer_pool_size"
          "innodb_ft_max_token_size"
          "innodb_ft_min_token_size"
          "local_infile"
          "lower_case_table_names"
          "max_allowed_packet"
          "max_connections"
          "max_heap_table_size"
          "max_prepared_stmt_count"
          "max_sp_recursion_depth"
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
            let name = name.ToLowerInvariant()

            if isSessionScope scope && globalScopeOnlyVariables.Contains name then
                Error(1238, sprintf "Variable '%s' is a GLOBAL variable" name)
            else
                match lookupAtRef session "@@" scope name with
                | Some value -> Ok(systemVariableValue name value)
                | None -> Error(1193, sprintf "Unknown system variable '%s'" name)
      MaxUserVariables = maxUserVariables }

let private expressionVariables (session: Session) = expressionVariablesFor session session.UserVariables

let private accountOf (session: Session) = Auth.account session.User session.AccountHost

let private checkSessionAccess session store required =
    Auth.checkForAccountWithRoles store (accountOf session) session.ActiveRoles required

let private hasSessionGlobalPrivilege session privilege =
    Auth.hasGlobalPrivForAccountWithRoles session.Store (accountOf session) session.ActiveRoles privilege

let private checkAnySessionGlobalPrivilege session display privileges =
    if privileges |> List.exists (hasSessionGlobalPrivilege session) then
        Ok()
    else
        Error(
            1227,
            sprintf "Access denied; you need (at least one of) the %s privilege(s) for this operation" display
        )

let private canSessionSeeDatabase session store database =
    Auth.canSeeDatabaseForAccountWithRoles store (accountOf session) session.ActiveRoles database

let private canSessionSeeTable session store database table =
    Auth.canSeeTableForAccountWithRoles store (accountOf session) session.ActiveRoles database table

let private checkTableMaintenanceAccess session store database table operation =
    match operation with
    | "check" ->
        if canSessionSeeTable session store database table then
            Ok()
        else
            Error(1142, sprintf "SELECT command denied to user '%s'@'localhost' for table '%s'" session.User table)
    | _ ->
        let required = [ "SELECT"; "INSERT" ]
        let denied =
            required
            |> List.choose (fun privilege ->
                match checkSessionAccess session store [ privilege, Auth.OnTable(database, table) ] with
                | Ok() -> None
                | Error error -> Some(privilege, error))

        match denied with
        | [] -> Ok()
        | [ _, error ] -> Error error
        | privileges ->
            Error(
                1142,
                sprintf
                    "%s command denied to user '%s'@'localhost' for table '%s'"
                    (privileges |> List.map fst |> String.concat ", ")
                    session.User
                    table
            )

let private maintenanceRow table operation messageType message =
    [ Some table; Some operation; Some messageType; Some message ]

let private maintenanceResult rows =
    ResultSet([ "Table"; "Op"; "Msg_type"; "Msg_text" ], rows)

let private lockWaitTimeout (session: Session) =
    lookupVar session "innodb_lock_wait_timeout"
    |> Option.flatten
    |> Option.bind (fun value ->
        match Int32.TryParse value with
        | true, seconds -> Some(TimeSpan.FromSeconds(float seconds))
        | _ -> None)
    |> Option.defaultWith Limits.lockWaitTimeout

let private canInspectRoutine (session: Session) schema definer =
    match Auth.tryParseAccount definer with
    | None -> false
    | Some owner ->
        let viewer = accountOf session

        Auth.sameAccount viewer owner
        || Auth.hasGlobalPrivForAccountWithRoles session.Store viewer session.ActiveRoles "SELECT"
        || (Auth.checkForAccountWithRoles
                session.Store
                viewer
                session.ActiveRoles
                [ "ALTER ROUTINE", Auth.OnDb schema ]
            |> Result.isOk)

let private canSeeRoutine session schema definer =
    canInspectRoutine session schema definer
    || (checkSessionAccess session session.Store [ "EXECUTE", Auth.OnDb schema ] |> Result.isOk)

type private AdvisoryLock =
    { Owner: int
      Count: int }

type private AdvisoryLockTable =
    { Locks: System.Collections.Generic.Dictionary<string, AdvisoryLock>
      OwnedNames: System.Collections.Generic.Dictionary<int, System.Collections.Generic.HashSet<string>> }

let private advisoryLocksByStore =
    ConditionalWeakTable<obj, AdvisoryLockTable>()

let private advisoryLocks (session: Session) =
    advisoryLocksByStore.GetValue(
        session.Store.Lock,
        fun _ ->
            { Locks = System.Collections.Generic.Dictionary(StringComparer.Ordinal)
              OwnedNames = System.Collections.Generic.Dictionary() }
    )

let private advisoryNamesForOwner (table: AdvisoryLockTable) owner =
    match table.OwnedNames.TryGetValue owner with
    | true, names -> names
    | false, _ ->
        let names = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        table.OwnedNames.[owner] <- names
        names

let private forgetAdvisoryName (table: AdvisoryLockTable) owner name =
    match table.OwnedNames.TryGetValue owner with
    | true, names ->
        names.Remove name |> ignore

        if names.Count = 0 then
            table.OwnedNames.Remove owner |> ignore
    | false, _ -> ()

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
                let table = advisoryLocks session

                lock table (fun () ->
                    let rec acquire () =
                        match table.Locks.TryGetValue name with
                        | false, _ ->
                            let owned = advisoryNamesForOwner table session.ConnectionId

                            if owned.Count >= Limits.maxAdvisoryLocksPerSession then
                                raise (Functions.SqlError(1235, "This version of MySQL doesn't yet support more user-level locks in one session"))

                            table.Locks.[name] <- { Owner = session.ConnectionId; Count = 1 }
                            owned.Add name |> ignore
                            VInt 1L
                        | true, current when current.Owner = session.ConnectionId ->
                            table.Locks.[name] <- { current with Count = current.Count + 1 }
                            VInt 1L
                        | _ when not infinite && deadline.Elapsed.TotalSeconds >= seconds -> VInt 0L
                        | _ ->
                            Storage.queryCancellation.Value.ThrowIfCancellationRequested()

                            let waitMilliseconds =
                                if infinite then 50 else max 1 (min 50 (int ((seconds - deadline.Elapsed.TotalSeconds) * 1000.0)))

                            Threading.Monitor.Wait(table, waitMilliseconds) |> ignore
                            acquire ()

                    acquire ())
    | _ -> raise (Functions.SqlError(1582, "Incorrect parameter count in the call to native function 'GET_LOCK'"))

let private releaseAdvisoryLock (session: Session) = function
    | [ value ] ->
        match advisoryLockName value with
        | None -> VNull
        | Some name ->
            let table = advisoryLocks session

            lock table (fun () ->
                match table.Locks.TryGetValue name with
                | false, _ -> VNull
                | true, current when current.Owner <> session.ConnectionId -> VInt 0L
                | true, current when current.Count > 1 ->
                    table.Locks.[name] <- { current with Count = current.Count - 1 }
                    VInt 1L
                | true, _ ->
                    table.Locks.Remove name |> ignore
                    forgetAdvisoryName table session.ConnectionId name
                    Threading.Monitor.PulseAll table
                    VInt 1L)
    | _ -> raise (Functions.SqlError(1582, "Incorrect parameter count in the call to native function 'RELEASE_LOCK'"))

let private releaseAllAdvisoryLocks (session: Session) =
    let table = advisoryLocks session

    lock table (fun () ->
        let owned =
            match table.OwnedNames.TryGetValue session.ConnectionId with
            | true, names ->
                names
                |> Seq.choose (fun name ->
                    match table.Locks.TryGetValue name with
                    | true, entry -> Some(name, entry.Count)
                    | false, _ -> None)
                |> List.ofSeq
            | false, _ -> []

        owned |> List.iter (fst >> table.Locks.Remove >> ignore)
        table.OwnedNames.Remove session.ConnectionId |> ignore

        if not owned.IsEmpty then
            Threading.Monitor.PulseAll table

        owned |> List.sumBy snd)

let private inspectAdvisoryLock (session: Session) inUse = function
    | [ value ] ->
        match advisoryLockName value with
        | None -> VNull
        | Some name ->
            let table = advisoryLocks session

            lock table (fun () ->
                match table.Locks.TryGetValue name with
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

        match extension.Signature with
        | None -> Functions.registerScalar name invoke registry
        | Some signature ->
            let parameters = signature.Parameters |> List.map ColumnWire.parameterMetadataOfType
            let result = ColumnWire.metadataOfType signature.Result
            Functions.registerScalarWithSignature name parameters result invoke registry

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
    let defaultWeekFormat =
        lookupVar session "default_week_format"
        |> Option.flatten
        |> Option.bind (fun value ->
            match Int32.TryParse value with
            | true, mode -> Some mode
            | _ -> None)
        |> Option.defaultValue 0

    let loginUser = if session.LoginUser = "" then session.User else session.LoginUser

    registry
    |> Functions.registerStringScalar
        "AES_ENCRYPT"
        (fun index -> index < 2)
        (Functions.FixedCollation("binary", 4))
        (Functions.aesEncrypt blockEncryptionMode)
    |> Functions.registerStringScalar
        "AES_DECRYPT"
        (fun index -> index < 2)
        (Functions.FixedCollation("binary", 4))
        (Functions.aesDecrypt blockEncryptionMode)
    |> Functions.registerScalar "DATE_FORMAT" (Functions.dateFormatFn timeLocale)
    |> Functions.registerScalar "DAYNAME" (Functions.dayNameFn timeLocale)
    |> Functions.registerScalar "MONTHNAME" (Functions.monthNameFn timeLocale)
    |> Functions.registerScalar "FROM_UNIXTIME" (Functions.fromUnixTimeFn timeLocale)
    |> Functions.registerScalar "WEEK" (Functions.weekFn defaultWeekFormat)
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
    |> Functions.registerScalar "CURRENT_ROLE" (fun _ -> VString(Auth.formatCurrentRoles session.ActiveRoles))
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

// Catalog introspection stays text-probed because it bypasses Executor;
// InformationSchema owns its result rendering.

let private showStatusRe =
    Regex(@"^SHOW\s+(?:(SESSION|GLOBAL)\s+)?STATUS(\s|$)", RegexOptions.IgnoreCase)

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
let private partitionMaintenanceRe =
    Regex(@"^ALTER\s+TABLE\s+.+?\s+(?:ANALYZE|CHECK|OPTIMIZE|REPAIR)\s+PARTITION(?:\s|$)", RegexOptions.IgnoreCase)
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
    let canSee = canSessionSeeTable session (Session.currentStore session) dbName table

    match result with
    | Error _ -> showResult result
    | Ok _ when canSee -> showResult result
    | Ok _ -> tableAccessDenied session table

let private visibleTableRows (session: Session) (dbName: string) (rows: string option list list) =
    let canSee = canSessionSeeTable session (Session.currentStore session) dbName

    rows |> List.filter (function Some table :: _ -> canSee table | _ -> false)

let private inspectAccount (session: Session) (wanted: Auth.Account) (render: unit -> QueryResult) =
    let viewer = accountOf session

    if Auth.sameAccount wanted viewer || hasSessionGlobalPrivilege session "SELECT" then
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
    let visible = rows |> List.filter (function | [ Some db ] -> canSessionSeeDatabase session store db | _ -> false)
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

let private showColumnsFieldFilterRe =
    Regex(
        @"\s+WHERE\s+`?Field`?\s*=\s*(?<value>'(?:\\.|''|[^'])*')\s*$",
        RegexOptions.IgnoreCase ||| RegexOptions.NonBacktracking,
        Limits.regexpMatchTimeout
    )

let private showColumnsFieldFilter (sql: string) =
    let matched = showColumnsFieldFilterRe.Match sql

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
    Regex(
        @"^(SESSION\s+|GLOBAL\s+|@@SESSION\.|@@GLOBAL\.|@@)?(`[^`]+`|\w+)\s*=\s*(.+)$",
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline
    )

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

/// Evaluates a SET expression against private state so the statement remains
/// atomic when any assignment fails.
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
            let privileges = Auth.requiredPrivilegesForExpression dbName expression

            match checkSessionAccess session store privileges with
            | Error(code, message) -> Error(Err(code, message))
            | Ok() ->
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
let private splitSetAssignments (options: Parser.ParserOptions) (sql: string) : Result<string list, string> =
    Parser.splitNonEmptyTopLevelCommaSeparatedWithOptions options sql
    |> Result.bind (function
        | first :: rest ->
            let prefix = Regex.Match(first, @"^SET\s+", RegexOptions.IgnoreCase)

            if prefix.Success then
                let assignment = first.Substring(prefix.Length).Trim()
                if assignment = "" then Error "SET requires an assignment" else Ok(assignment :: rest)
            else
                Error "SET requires an assignment"
        | [] -> Error "SET requires an assignment")

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
    | SetRoutineRecursionDepthAction of depth: int * isGlobal: bool * warning: string option
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

let private normalizeRoutineRecursionDepth =
    let bounded original value =
        let depth = min value 255UL |> int
        let warning =
            if uint64 depth = value then
                None
            else
                Some(sprintf "Truncated incorrect max_sp_recursion_depth value: '%s'" original)

        Ok(depth, warning)

    function
    | VInt value when value < 0L ->
        Ok(0, Some(sprintf "Truncated incorrect max_sp_recursion_depth value: '%d'" value))
    | VInt value -> bounded (string value) (uint64 value)
    | VUInt value -> bounded (string value) value
    | _ -> Error(Err(1232, "Incorrect argument type to variable 'max_sp_recursion_depth'"))

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

let private normalizeOnOff name value =
    match value |> toText |> Option.map (_.Trim().ToUpperInvariant()) with
    | Some("1" | "ON") -> Ok "ON"
    | Some("0" | "OFF") -> Ok "OFF"
    | Some value -> Error(Err(1231, sprintf "Variable '%s' can't be set to the value of '%s'" name value))
    | None -> Error(Err(1231, sprintf "Variable '%s' can't be set to the value of 'NULL'" name))

let private normalizeEnumVariable name values value =
    let raw = value |> toText |> Option.map (_.Trim().ToUpperInvariant())

    values
    |> List.indexed
    |> List.tryPick (fun (ordinal, canonical) ->
        raw
        |> Option.filter (fun candidate -> candidate = string ordinal || candidate = canonical)
        |> Option.map (fun _ -> canonical))
    |> function
        | Some canonical -> Ok canonical
        | None ->
            Error(
                Err(
                    1231,
                    sprintf
                        "Variable '%s' can't be set to the value of '%s'"
                        name
                        (raw |> Option.defaultValue "NULL")
                )
            )

let private normalizeTransactionTrackingInfo =
    normalizeEnumVariable "session_track_transaction_info" [ "OFF"; "STATE"; "CHARACTERISTICS" ]

let private normalizeCompletionType =
    normalizeEnumVariable "completion_type" [ "NO_CHAIN"; "CHAIN"; "RELEASE" ]

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

let private pendingTransactionCharacteristics (session: Session) =
    [ session.PendingTransactionIsolation
      |> Option.map (fun isolation -> sprintf "SET TRANSACTION ISOLATION LEVEL %s;" (transactionIsolationName isolation))
      session.PendingTransactionReadOnly
      |> Option.map (fun readOnly -> if readOnly then "SET TRANSACTION READ ONLY;" else "SET TRANSACTION READ WRITE;") ]
    |> List.choose id
    |> String.concat " "

let private explicitTransactionCharacteristics (readOnly: bool option) (session: Session) =
    let isolation =
        session.PendingTransactionIsolation
        |> Option.map (fun value -> sprintf "SET TRANSACTION ISOLATION LEVEL %s; " (transactionIsolationName value))
        |> Option.defaultValue ""

    let access =
        readOnly
        |> Option.orElse session.PendingTransactionReadOnly
        |> Option.map (fun value -> if value then " READ ONLY" else " READ WRITE")
        |> Option.defaultValue ""

    isolation + "START TRANSACTION" + access + ";"

let private transactionIsolationScope (prefix: string) =
    match prefix.Trim().ToUpperInvariant() with
    | "@@" -> NextTransactionIsolation
    | "GLOBAL"
    | "@@GLOBAL." -> GlobalIsolation
    | _ -> SessionIsolation

/// Parses one SET fragment without mutating the session. Nested assignments
/// remain visible to subsequent right-hand sides in the same statement.
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
                    let rhs = varMatch.Groups.[3].Value
                    let usesDefault = rhs.Trim().Equals("DEFAULT", StringComparison.OrdinalIgnoreCase)

                    let resolved =
                        if name = "max_sp_recursion_depth" && not usesDefault then
                            resolveUserSetRhs session userVariables sql rhs
                        else
                            resolveSystemSetRhs session userVariables sql rhs

                    match resolved with
                    | Error result -> Error result
                    | Ok(_, sideEffects) when usesDefault && name = "max_sp_recursion_depth" ->
                        let depth =
                            if isGlobal then
                                0
                            else
                                Session.tryGlobalVariable session.Store name
                                |> Option.flatten
                                |> Option.bind (fun value ->
                                    match Int32.TryParse value with
                                    | true, depth -> Some depth
                                    | false, _ -> None)
                                |> Option.defaultValue 0

                        Ok(SetRoutineRecursionDepthAction(depth, isGlobal, None), sideEffects)
                    | Ok(_, sideEffects)
                        when usesDefault
                             && (name = "activate_all_roles_on_login"
                                 || name = "event_scheduler"
                                 || name = "mandatory_roles") ->
                        Ok(SetVarAction(name, Session.defaultVariables.[name], isGlobal), sideEffects)
                    | Ok(_, sideEffects)
                        when usesDefault
                             && (name = "completion_type" || name = "session_track_transaction_info") ->
                        let value =
                            if isGlobal then
                                Session.defaultVariables.[name]
                            else
                                Session.tryGlobalVariable session.Store name |> Option.defaultValue Session.defaultVariables.[name]

                        Ok(SetVarAction(name, value, isGlobal), sideEffects)
                    | Ok(value, sideEffects) when name = "max_sp_recursion_depth" ->
                        normalizeRoutineRecursionDepth value
                        |> Result.map (fun (depth, warning) ->
                            SetRoutineRecursionDepthAction(depth, isGlobal, warning), sideEffects)
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
                    | Ok(value, sideEffects) when name = "event_scheduler" || name = "activate_all_roles_on_login" ->
                        normalizeOnOff name value
                        |> Result.map (fun value -> SetVarAction(name, Some value, isGlobal), sideEffects)
                    | Ok(value, sideEffects) when name = "session_track_transaction_info" ->
                        normalizeTransactionTrackingInfo value
                        |> Result.map (fun value -> SetVarAction(name, Some value, isGlobal), sideEffects)
                    | Ok(value, sideEffects) when name = "completion_type" ->
                        normalizeCompletionType value
                        |> Result.map (fun value -> SetVarAction(name, Some value, isGlobal), sideEffects)
                    | Ok(value, sideEffects) when name = "mandatory_roles" ->
                        match toText value with
                        | Some value when Session.tryParseMandatoryRoles value |> Option.isSome ->
                            Ok(SetVarAction(name, Some value, isGlobal), sideEffects)
                        | Some value -> Error(Err(1231, sprintf "Variable 'mandatory_roles' can't be set to the value of '%s'" value))
                        | None -> Error(Err(1231, "Variable 'mandatory_roles' can't be set to the value of 'NULL'"))
                    | Ok(VNull, sideEffects) when nullableSystemVars.Contains name -> Ok(SetVarAction(name, None, isGlobal), sideEffects)
                    | Ok(VNull, _) -> Error(Err(1231, sprintf "Variable '%s' can't be set to the value of 'NULL'" name))
                    | Ok(value, sideEffects) -> Ok(SetVarAction(name, toText value, isGlobal), sideEffects)
            else
                Error(syntaxError sql)

/// Applies one parsed `SetAction`, including store settings derived from
/// `foreign_key_checks` and `sql_mode`.
let private applySetAction (session: Session) (action: SetAction) : Session =
    match action with
    | SetNamesAction(charset, collation) ->
        match charset.ToLowerInvariant() with
        | "utf8" -> Diagnostics.deprecatedUtf8Alias ()
        | "utf8mb3" -> Diagnostics.deprecatedUtf8mb3 ()
        | _ -> ()

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
    | SetRoutineRecursionDepthAction(depth, true, warning) ->
        warning |> Option.iter (Diagnostics.warning 1292)
        Session.setGlobalVariable session.Store "max_sp_recursion_depth" (Some(string depth))
        session
    | SetRoutineRecursionDepthAction(depth, false, warning) ->
        warning |> Option.iter (Diagnostics.warning 1292)
        markRoutineVariables [ "max_sp_recursion_depth" ]

        { session with
            Variables = Map.add "max_sp_recursion_depth" (Some(string depth)) session.Variables }
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
    | SetTransactionIsolationAction(GlobalIsolation, _) when not (hasSessionGlobalPrivilege session "SUPER") ->
        Error(Err(1227, "Access denied; you need (at least one of) the SUPER privilege(s) for this operation"))
    | SetTransactionIsolationAction(NextTransactionIsolation, _) when session.Tx.IsSome ->
        Error(Err(1568, "Transaction characteristics can't be changed while a transaction is in progress"))
    | SetTransactionIsolationAction(_, (ReadUncommitted | ReadCommitted | RepeatableRead | Serializable)) -> Ok()
    | SetVarAction(_, _, true) when not (hasSessionGlobalPrivilege session "SUPER") ->
        Error(Err(1227, "Access denied; you need (at least one of) the SUPER privilege(s) for this operation"))
    | SetRoutineRecursionDepthAction(_, true, _) when not (hasSessionGlobalPrivilege session "SUPER") ->
        Error(Err(1227, "Access denied; you need (at least one of) the SUPER privilege(s) for this operation"))
    | SetVarAction(name, _, true) when readOnlySystemVariables.Contains name ->
        Error(Err(1238, sprintf "Variable '%s' is a read only variable" name))
    | SetVarAction("session_track_system_variables", Some value, _)
        when value.Length > Limits.maxTrackedSystemVariablesLength
             || (value |> Seq.filter ((=) ',') |> Seq.length) >= Limits.maxTrackedSystemVariableNames ->
        Error(Err(1231, "Variable 'session_track_system_variables' exceeds its resource limit"))
    | SetVarAction(name, Some value, true) when Limits.isReportableSetting name ->
        Limits.validateSetting name value |> Result.mapError (fun message -> Err(1232, message))
    | SetVarAction(name, _, false) when globalOnlyVariables.Contains name ->
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
        |> Result.mapError (fun _ -> syntaxError sql)
        |> Result.bind (fun fragments ->
            fragments
            |> List.fold
                (fun state fragment ->
                    state
                    |> Result.bind (fun (actions, sideEffects) ->
                        parseSetFragment sql session sideEffects fragment
                        |> Result.map (fun (action, nextSideEffects) -> action :: actions, nextSideEffects)))
                (Ok([], session.UserVariables)))

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
                let updated =
                    if
                        actions
                        |> List.exists (function
                            | SetTransactionIsolationAction(NextTransactionIsolation, _) -> true
                            | _ -> false)
                    then
                        Session.setTransactionCharacteristics (pendingTransactionCharacteristics updated) updated
                    else
                        updated
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

// Transaction control stays outside the data-statement AST because it changes
// connection state without entering Executor.

let private beginTx = Regex(@"^(?:BEGIN(?:\s+WORK)?|START\s+TRANSACTION(?:\s+READ\s+(ONLY|WRITE))?)$", RegexOptions.IgnoreCase)

let private transactionCompletion =
    Regex(
        @"^
          (?<command>COMMIT|ROLLBACK)
          (?:\s+WORK)?
          (?<chain>\s+AND\s+(?<noChain>NO\s+)?CHAIN)?
          (?<release>\s+(?<noRelease>NO\s+)?RELEASE)?
          $",
        RegexOptions.IgnoreCase ||| RegexOptions.IgnorePatternWhitespace
    )

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
    || hasSessionGlobalPrivilege session "SUPER"

let private setPasswordRe =
    Regex(@"^SET\s+PASSWORD\s*(?:FOR\s+([^=\s]+)\s*)?=\s*'([^']*)'\s*;?$", RegexOptions.IgnoreCase)

let private alterCurrentUserPasswordRe =
    Regex(
        @"^ALTER\s+USER\s+(?:USER|CURRENT_USER)\s*\(\s*\)\s+IDENTIFIED\s+BY\s+'([^']*)'\s*;?$",
        RegexOptions.IgnoreCase
    )

let private showGrantsRe =
    Regex(
        @"^SHOW\s+GRANTS(?:\s+FOR\s+(.+?))?(?:\s+USING\s+(.+?))?\s*;?$",
        RegexOptions.IgnoreCase ||| RegexOptions.NonBacktracking,
        Limits.regexpMatchTimeout
    )

/// `FLUSH [LOCAL] PRIVILEGES` — a no-op OK: privilege reads always hit the
/// live mysql.* rows, there's no cache to flush.
let private flushPrivilegesRe = Regex(@"^FLUSH\s+(?:LOCAL\s+)?PRIVILEGES\s*;?$", RegexOptions.IgnoreCase)
let private flushUserResourcesRe = Regex(@"^FLUSH\s+USER_RESOURCES\s*;?$", RegexOptions.IgnoreCase)
let private flushStatusRe = Regex(@"^FLUSH\s+STATUS\s*;?$", RegexOptions.IgnoreCase)

let private applyRoleStatement session =
    function
    | SetRole selection ->
        match Auth.resolveRoleSelection session.Store (accountOf session) selection with
        | Ok roles -> { session with ActiveRoles = roles }, Affected 0UL
        | Error(code, message) -> session, Err(code, message)
    | SetDefaultRole(selection, users) ->
        let ownsEveryTarget =
            users
            |> List.forall (fun (name, host) -> Auth.sameAccount (accountOf session) (Auth.account name host))

        let access =
            if ownsEveryTarget then
                Ok()
            else
                checkSessionAccess session session.Store [ "CREATE USER", Auth.Global ]

        access
        |> Result.bind (fun () -> Auth.setDefaultRoles session.Store selection users)
        |> function
            | Ok() -> session, Affected 0UL
            | Error(code, message) -> session, Err(code, message)
    | _ -> session, Err(1105, "Internal role statement dispatch error")

let private isRoleSessionStatement =
    function
    | SetRole _
    | SetDefaultRole _ -> true
    | _ -> false
let private flushIdentifierPattern = @"(?:`(?:``|[^`])+`|[\p{L}_$][\p{L}\p{N}_$]*)"

let private flushTableNamePattern =
    sprintf @"%s(?:\s*\.\s*%s)?" flushIdentifierPattern flushIdentifierPattern

let private flushTableListPattern =
    sprintf @"%s(?:\s*,\s*%s)*" flushTableNamePattern flushTableNamePattern

let private flushTablesRe =
    Regex(
        sprintf
            @"^FLUSH\s+(?:(?:NO_WRITE_TO_BINLOG|LOCAL)\s+)?TABLES(?:\s+%s(?:\s+(?:WITH\s+READ\s+LOCK|FOR\s+EXPORT))?)?\s*;?$"
            flushTableListPattern,
        RegexOptions.IgnoreCase
    )

let private flushTableLocksRe =
    Regex(@"\s+(?:WITH\s+READ\s+LOCK|FOR\s+EXPORT)\s*;?$", RegexOptions.IgnoreCase)

let private flushOptimizerCostsRe = Regex(@"^FLUSH\s+OPTIMIZER_COSTS\s*;?$", RegexOptions.IgnoreCase)
let private flushLogsRe =
    Regex(@"^FLUSH\s+(?:(?:BINARY|ENGINE|ERROR|GENERAL|RELAY|SLOW)\s+)?LOGS\s*;?$", RegexOptions.IgnoreCase)
let private lockTablesRe = Regex(@"^LOCK\s+TABLES(?:\s|$)", RegexOptions.IgnoreCase)
let private unlockTablesRe = Regex(@"^UNLOCK\s+TABLES?\s*$", RegexOptions.IgnoreCase)

let private setTransactionIsolation =
    Regex(
        @"^SET\s+(?:(SESSION|GLOBAL)\s+)?TRANSACTION\s+ISOLATION\s+LEVEL\s+(REPEATABLE\s+READ|READ\s+COMMITTED|READ\s+UNCOMMITTED|SERIALIZABLE)$",
        RegexOptions.IgnoreCase
    )

let private setTransactionAccess =
    Regex(@"^SET\s+(SESSION\s+)?TRANSACTION\s+READ\s+(ONLY|WRITE)$", RegexOptions.IgnoreCase)

let private setCharacterSet = Regex(@"^SET\s+CHARACTER\s+SET\s+'?(\w+)'?$", RegexOptions.IgnoreCase)
let private setDefaultRoleStatement = Regex(@"^SET\s+DEFAULT\s+ROLE(?:\s|$)", RegexOptions.IgnoreCase)
let private setRoleStatement = Regex(@"^SET\s+ROLE(?:\s|$)", RegexOptions.IgnoreCase)

let private xaTransactions =
    ConditionalWeakTable<ConcurrentDictionary<string, Database ref>, ConcurrentDictionary<Xa.Xid, int>>()

let private xaEntries (store: Store) =
    xaTransactions.GetValue(store.Databases, fun _ -> ConcurrentDictionary<Xa.Xid, int>())

let private removeXaAssociation (session: Session) xid =
    let entries = xaEntries session.Store

    lock entries (fun () ->
        match entries.TryGetValue xid with
        | true, owner when owner = session.ConnectionId -> entries.TryRemove xid |> ignore
        | _ -> ())

let private removeTransactionView (session: Session) =
    TransactionRegistry.remove session.Store session.ConnectionId

let private enabledSessionFlag name (session: Session) =
    lookupVar session name
    |> Option.flatten
    |> Option.forall ((<>) "0")

let private syncTransactionView (session: Session) =
    match session.Tx with
    | Some transaction when transaction.Seeded ->
        let rowsModified = Storage.transactionRollbackWork transaction.Snapshot |> max 0L |> uint64

        match Storage.transactionId transaction.Snapshot with
        | Some transactionId ->
            TransactionRegistry.publish
                session.Store
                session.ConnectionId
                { BaseCatalog = transaction.BaseCatalog
                  Snapshot = transaction.Snapshot
                  Metadata =
                    { Id = transactionId
                      Started = DateTime.Now
                      Isolation = transactionIsolationName transaction.Isolation
                      ReadOnly = transaction.ReadOnly
                      UniqueChecks = enabledSessionFlag "unique_checks" session
                      ForeignKeyChecks = enabledSessionFlag "foreign_key_checks" session
                      RowsModified = rowsModified
                      LockStructs = Storage.transactionLockStructCount transaction.Snapshot } }
        | None -> removeTransactionView session
    | _ -> removeTransactionView session

    session

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

let private readUncommittedBase (session: Session) =
    let _, initial = Storage.beginTransactionSnapshotWithBase session.Store
    let mutable snapshot = initial

    TransactionRegistry.others session.Store session.ConnectionId
    |> List.iter (fun view ->
        let candidate = Storage.beginTransactionSnapshotFromCatalog session.Store snapshot.Catalog

        try
            Storage.mergeCatalogInto candidate view.BaseCatalog view.Snapshot.Catalog
            snapshot <- candidate
        with :? Storage.LockWaitTimeout ->
            ())

    snapshot.Catalog, snapshot

let private rebaseReadUncommittedSnapshot (session: Session) (tx: Transaction) =
    let baseCatalog, transactionSnapshot = readUncommittedBase session
    let snapshot = transactionSnapshot |> Storage.carryTransactionLocks tx.Snapshot
    Storage.mergeCatalogInto snapshot tx.BaseCatalog tx.Snapshot.Catalog

    match tx.Snapshot.PendingEvents, snapshot.PendingEvents with
    | Some source, Some target -> target.AddRange source
    | _ -> ()

    baseCatalog, snapshot

/// Publishes a supplied transaction so local COMMIT and detached XA completion
/// share one merge, durability, and lock-release path.
let private publishTransaction (publishFlat: bool) (session: Session) (tx: Transaction) =
    if not tx.Seeded then
        Storage.releaseTransactionLocks tx.Snapshot
    else
        let timeout = lockWaitTimeout session

        match tx.Isolation with
        | Serializable ->
            Storage.commitSerializableCatalogIntoWithTimeout timeout publishFlat session.Store tx.BaseCatalog tx.Snapshot
        | _ -> Storage.commitCatalogIntoWithTimeout timeout publishFlat session.Store tx.BaseCatalog tx.Snapshot

        Storage.releaseTransactionLocks tx.Snapshot

/// Commits the open transaction by publishing its private catalog. Ordinary
/// isolation levels merge disjoint row changes; SERIALIZABLE validates the
/// transaction's read snapshot before publication. No open transaction is a
/// no-op, matching MySQL.
let private commitSessionWith (publishFlat: bool) (session: Session) : Session =
    match session.Tx with
    | Some tx ->
        publishTransaction publishFlat session tx
        removeTransactionView session
        Session.endTransactionTracking { session with Tx = None; Cursors = Map.empty }
    | None -> { session with Cursors = Map.empty }

let private commitSession (session: Session) : Session = commitSessionWith false session

/// Discards the open transaction's snapshot — a no-op, matching real MySQL,
/// if there isn't one open — except for each table's AUTO_INCREMENT
/// counter, which MySQL never rolls back (an id an aborted INSERT consumed
/// stays burned). Bumps the shared store's counter up to the snapshot's if
/// the snapshot ran it ahead (`Storage.bumpAutoIncrementsInto`, same
/// CAS-safe merge as `commitSession`); leaves everything else (rows,
/// schema) alone.
let private rollbackSession (session: Session) : Session =
    let hadTransaction = session.Tx.IsSome
    session.Tx
    |> Option.bind _.Xa
    |> Option.iter (fst >> removeXaAssociation session)

    match session.Tx with
    | Some tx when not tx.Seeded -> Storage.releaseTransactionLocks tx.Snapshot
    | Some tx ->
        Storage.bumpAutoIncrementsInto session.Store tx.Snapshot.Catalog
        Storage.releaseTransactionLocks tx.Snapshot
    | None -> ()

    removeTransactionView session

    let session = { session with Tx = None; Cursors = Map.empty }
    if hadTransaction then Session.endTransactionTracking session else session

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

let private beginTransaction kind characteristics (readOnly: bool) (session: Session) : Session =
    let session = commitSession session
    let isolation = configuredIsolation session
    let snapshot = Storage.beginTransactionContext session.Store

    Session.beginTransactionTracking
        kind
        characteristics
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
                      NextSavepointSeq = 0
                      Xa = None } }

let private xaRmFail state =
    Err(1399, sprintf "XAER_RMFAIL: The command cannot be executed when global transaction is in the  %s state" state)

let private xaUnknown = Err(1397, "XAER_NOTA: Unknown XID")

let private xaStateName = function
    | Active -> "ACTIVE"
    | Idle -> "IDLE"

let private xaAssociation (session: Session) =
    session.Tx |> Option.bind _.Xa

let private startXa xid session =
    match session.Tx with
    | Some tx ->
        match tx.Xa with
        | Some(_, state) -> session, xaRmFail (xaStateName state)
        | None -> session, Err(1400, "XAER_OUTSIDE: Some work is done outside global transaction")
    | None when TableLocks.holdsExplicit session.Store session.ConnectionId ->
        session, Err(1400, "XAER_OUTSIDE: Some work is done outside global transaction")
    | None ->
        let entries = xaEntries session.Store

        lock entries (fun () ->
            let duplicate =
                entries.Keys |> Seq.exists (Xa.sameBranch xid)
                || Storage.preparedXas session.Store
                   |> List.exists (fst >> Xa.sameBranch xid)

            if duplicate then
                session, Err(1440, "XAER_DUPID: The XID already exists")
            else
                entries.[xid] <- session.ConnectionId
                let started =
                    beginTransaction ExplicitTrackedTransaction "" false session

                match started.Tx with
                | Some transaction ->
                    { started with Tx = Some { transaction with Xa = Some(xid, Active) } }, Affected 0UL
                | None -> session, xaRmFail "NON-EXISTING")

let private endXa xid session =
    match xaAssociation session with
    | Some(current, Active) when current = xid ->
        { session with Tx = session.Tx |> Option.map (fun tx -> { tx with Xa = Some(xid, Idle) }) }, Affected 0UL
    | Some(_, Active) -> session, xaUnknown
    | Some(_, state) -> session, xaRmFail (xaStateName state)
    | None -> session, xaRmFail "NON-EXISTING"

let private prepareXa xid session =
    match session.Tx, xaAssociation session with
    | _, Some(_, Active) -> session, xaRmFail "ACTIVE"
    | Some transaction, Some(current, Idle) when current = xid ->
        let validateWholeSnapshot = transaction.Isolation = Serializable
        let baseCatalog, snapshot =
            if transaction.Seeded then
                transaction.BaseCatalog, transaction.Snapshot
            else
                let catalog, snapshot = Storage.beginTransactionSnapshotWithBase session.Store
                catalog, Storage.carryTransactionLocks transaction.Snapshot snapshot

        if Storage.prepareXa session.Store xid validateWholeSnapshot baseCatalog snapshot then
            removeXaAssociation session xid
            removeTransactionView session
            { session with Tx = None; Cursors = Map.empty }, Affected 0UL
        else
            session, Err(1440, "XAER_DUPID: The XID already exists")
    | _, Some(_, Idle) -> session, xaUnknown
    | _ -> session, xaRmFail "NON-EXISTING"

let private completePreparedXa commit xid session =
    if commit then
        if Storage.commitPreparedXaWithTimeout (lockWaitTimeout session) session.Store xid then
            session, Affected 0UL
        else
            session, xaUnknown
    else
        match Storage.rollbackPreparedXa session.Store xid with
        | Some prepared ->
            Storage.bumpAutoIncrementsInto session.Store prepared.Catalog
            session, Affected 0UL
        | None -> session, xaUnknown

let private commitXa xid onePhase session =
    match session.Tx, xaAssociation session with
    | Some transaction, Some(current, Idle) when current = xid && onePhase ->
        publishTransaction false session transaction
        removeXaAssociation session xid
        removeTransactionView session
        { session with Tx = None; Cursors = Map.empty }, Affected 0UL
    | _, Some(_, state) -> session, xaRmFail (xaStateName state)
    | _ -> completePreparedXa true xid session

let private rollbackXa xid session =
    match session.Tx, xaAssociation session with
    | Some transaction, Some(current, Idle) when current = xid ->
        removeXaAssociation session xid

        if transaction.Seeded then
            Storage.bumpAutoIncrementsInto session.Store transaction.Snapshot.Catalog

        Storage.releaseTransactionLocks transaction.Snapshot
        removeTransactionView session
        { session with Tx = None; Cursors = Map.empty }, Affected 0UL
    | _, Some(_, state) -> session, xaRmFail (xaStateName state)
    | _ -> completePreparedXa false xid session

let private recoverXa convertXid session =
    if not (hasSessionGlobalPrivilege session "XA_RECOVER_ADMIN") then
        session, Err(1227, "Access denied; you need (at least one of) the XA_RECOVER_ADMIN privilege(s) for this operation")
    else
        let rows =
            Storage.preparedXas session.Store
            |> Seq.map (fun (xid, _) ->
                let bytes = Xa.data xid
                let data =
                    if convertXid then
                        "0x" + Convert.ToHexString bytes
                    else
                        Text.Encoding.Latin1.GetString bytes

                [ Some(string xid.FormatId)
                  Some(string xid.GlobalId.Length)
                  Some(string xid.BranchQualifier.Length)
                  Some data ])
            |> Seq.sortBy (fun row -> row.[3])
            |> List.ofSeq

        session, ResultSet([ "formatID"; "gtrid_length"; "bqual_length"; "data" ], rows)

let private runXa (parserOptions: Parser.ParserOptions) (session: Session) sql =
    let charset =
        session.Variables
        |> Map.tryFind "character_set_client"
        |> Option.flatten
        |> Option.defaultValue "utf8mb4"

    match Xa.parse parserOptions.NoBackslashEscapes charset sql with
    | Error detail -> session, parserError sql detail
    | Ok command ->
        let statusCommand =
            match command with
            | Xa.Start _ -> InformationSchema.StatusCommand.xaStart
            | Xa.End _ -> InformationSchema.StatusCommand.xaEnd
            | Xa.Prepare _ -> InformationSchema.StatusCommand.xaPrepare
            | Xa.Commit _ -> InformationSchema.StatusCommand.xaCommit
            | Xa.Rollback _ -> InformationSchema.StatusCommand.xaRollback
            | Xa.Recover _ -> InformationSchema.StatusCommand.xaRecover

        InformationSchema.recordCommand session.StatusCounters statusCommand

        let executed, result =
            match command with
            | Xa.Start(_, true)
            | Xa.End(_, true) -> session, Err(1398, "XAER_INVAL: Invalid arguments (or unsupported command)")
            | Xa.Start(xid, false) -> startXa xid session
            | Xa.End(xid, false) -> endXa xid session
            | Xa.Prepare xid -> prepareXa xid session
            | Xa.Commit(xid, onePhase) -> commitXa xid onePhase session
            | Xa.Rollback xid -> rollbackXa xid session
            | Xa.Recover convertXid -> recoverXa convertXid session

        let metadata =
            match command, result with
            | Xa.Recover _, ResultSet _ ->
                let number =
                    { Value.columnMetadata TypeLongLong with
                        ColumnLength = 12u
                        Flags = NotNullFlag ||| BinaryFlag ||| NumFlag
                        CollationId = Some 63us }

                let data =
                    { Value.columnMetadata TypeVarString with
                        ColumnLength = 1032u
                        Flags = NotNullFlag
                        Decimals = 31uy
                        CollationId =
                            Collation.idAndSortlen
                            |> Map.tryFind "utf8mb4_0900_ai_ci"
                            |> Option.map (fst >> uint16) }

                [ number; number; number; data ]
            | _ -> []

        { executed with LastResultColumnMetadata = completeResultMetadata executed result metadata }, result

/// Seeds fixed snapshots and refreshes statement-scoped isolation views.
let startTransactionStatement (session: Session) : Session =
    match session.Tx with
    | Some tx when not tx.Seeded && tx.Isolation = ReadUncommitted ->
        let baseCatalog, transactionSnapshot = readUncommittedBase session
        let snapshot = transactionSnapshot |> Storage.carryTransactionLocks tx.Snapshot

        { session with
            Tx =
                Some
                    { tx with
                        Snapshot = snapshot
                        BaseCatalog = baseCatalog
                        Seeded = true } }
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
    | Some tx when tx.Isolation = ReadCommitted || tx.Isolation = ReadUncommitted ->
        let baseCatalog, snapshot =
            if tx.Isolation = ReadUncommitted then
                rebaseReadUncommittedSnapshot session tx
            else
                rebaseTransactionSnapshot session tx

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
            let baseCatalog, snapshot =
                if transaction.Isolation = ReadUncommitted then
                    rebaseReadUncommittedSnapshot session transaction
                else
                    rebaseTransactionSnapshot session transaction

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
    TableLocks.releaseExplicit session.Store session.ConnectionId
    InformationSchema.releaseStatusCounters session.StatusCounters

let private savepointNotFound (name: string) : QueryResult =
    Err(1305, sprintf "SAVEPOINT %s does not exist" name)

/// SAVEPOINT starts an implicit transaction when needed. Redefinition gets a
/// fresh sequence so later rollback and release operations preserve MySQL's
/// establishment order.
let private savepoint (name: string) (session: Session) : Session * QueryResult =
    let session =
        if session.Tx.IsNone then
            beginTransaction
                ImplicitTrackedTransaction
                (pendingTransactionCharacteristics session)
                (configuredReadOnly session)
                session
        else
            session

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
                                  PendingEventCount = eventCount
                                  RollbackWork = Storage.transactionRollbackWork tx.Snapshot }
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
        Storage.restoreTransactionRollbackWork tx.Snapshot savepoint.RollbackWork
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
    | LoadData _
    | Update _
    | Delete _
    | CreateTableAs _ -> true
    | _ -> false

let private ignoresDataChangeErrors =
    function
    | Insert(_, _, _, _, true)
    | InsertSelect(_, _, _, _, true)
    | LoadData { Ignore = true }
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
    | AlterUser(name, host, Some _, _, options) when
        options = AccountOptions.empty && Auth.sameAccount (Auth.account name host) (accountOf session)
        ->
        []
    | statement -> Auth.requiredPrivilegesInStore store dbName statement

let private statementContainsLockingReadWhere predicate statement =
    let rec bodyContains =
        function
        | PlainSelect select -> selectContains select
        | UnionSelect(first, rest, orderBy, limit, offset) ->
            selectContains first
            || rest |> List.exists (snd >> selectContains)
            || orderBy |> List.exists (fst >> expressionContains)
            || limit |> Option.exists expressionContains
            || offset |> Option.exists expressionContains

    and sourceContains =
        function
        | FromSubquery(body, _)
        | FromLateral(body, _) -> bodyContains body
        | FromJsonTable(source, _, _, _) -> expressionContains source
        | FromTable _ -> false

    and expressionContains expression =
        Expression.collectSubqueries expression |> List.exists selectContains

    and selectContains (select: SelectStmt) =
        (select.Locking |> List.exists predicate)
        || (select.Ctes |> List.exists (_.Body >> bodyContains))
        || (select.From |> Option.exists sourceContains)
        || (select.Joins |> List.exists (fun join -> sourceContains join.Table || expressionContains join.On))
        || (select.Projections |> List.exists (fst >> expressionContains))
        || (select.Where |> Option.exists expressionContains)
        || (select.GroupBy |> List.exists expressionContains)
        || (select.Having |> Option.exists expressionContains)
        || (select.OrderBy |> List.exists (fst >> expressionContains))
        || (select.Windows
            |> List.exists (snd >> OverSpec >> Expression.overExpressions >> List.exists expressionContains))
        || (select.Limit |> Option.exists expressionContains)
        || (select.Offset |> Option.exists expressionContains)

    match statement with
    | Select select -> selectContains select
    | Union(first, rest, orderBy, limit, offset) ->
        bodyContains (UnionSelect(first, rest, orderBy, limit, offset))
    | _ -> false

let private statementContainsLockingRead =
    statementContainsLockingReadWhere (fun _ -> true)

let private statementContainsUpdateLock =
    statementContainsLockingReadWhere (fun locking -> locking.Strength = UpdateLock)

let rec private statementStatusCommand = function
    | CreateDatabase _ -> Some InformationSchema.StatusCommand.createDatabase
    | DropDatabase _ -> Some InformationSchema.StatusCommand.dropDatabase
    | AlterDatabase _ -> Some InformationSchema.StatusCommand.alterDatabase
    | CreateServer _ -> Some InformationSchema.StatusCommand.createServer
    | AlterServer _ -> Some InformationSchema.StatusCommand.alterServer
    | DropServer _ -> Some InformationSchema.StatusCommand.dropServer
    | CreateTable _
    | CreateTableLike _
    | CreateTableAs _ -> Some InformationSchema.StatusCommand.createTable
    | DropTable _ -> Some InformationSchema.StatusCommand.dropTable
    | AlterTable _ -> Some InformationSchema.StatusCommand.alterTable
    | RenameTable _ -> Some InformationSchema.StatusCommand.renameTable
    | CreateIndex _ -> Some InformationSchema.StatusCommand.createIndex
    | DropIndexStmt _ -> Some InformationSchema.StatusCommand.dropIndex
    | Insert _ -> Some InformationSchema.StatusCommand.insert
    | InsertSelect _ -> Some InformationSchema.StatusCommand.insertSelect
    | Replace _
    | ReplaceSet _ -> Some InformationSchema.StatusCommand.replace
    | ReplaceSelect _ -> Some InformationSchema.StatusCommand.replaceSelect
    | LoadData _ -> Some InformationSchema.StatusCommand.load
    | Select _
    | Union _ -> Some InformationSchema.StatusCommand.select
    | Do _ -> Some InformationSchema.StatusCommand.doStatement
    | Update statement when statement.Joins.IsEmpty -> Some InformationSchema.StatusCommand.update
    | Update _ -> Some InformationSchema.StatusCommand.updateMulti
    | Delete statement when statement.Joins.IsEmpty && statement.Targets.Length = 1 ->
        Some InformationSchema.StatusCommand.delete
    | Delete _ -> Some InformationSchema.StatusCommand.deleteMulti
    | Truncate _ -> Some InformationSchema.StatusCommand.truncate
    | CreateUser _ -> Some InformationSchema.StatusCommand.createUser
    | DropUser _ -> Some InformationSchema.StatusCommand.dropUser
    | RenameUser _ -> Some InformationSchema.StatusCommand.renameUser
    | AlterUser _ -> Some InformationSchema.StatusCommand.alterUser
    | CreateRole _ -> Some InformationSchema.StatusCommand.createRole
    | DropRole _ -> Some InformationSchema.StatusCommand.dropRole
    | GrantRoles _ -> Some InformationSchema.StatusCommand.grantRoles
    | RevokeRoles _ -> Some InformationSchema.StatusCommand.revokeRoles
    | SetRole _ -> Some InformationSchema.StatusCommand.setRole
    | SetDefaultRole _ -> Some InformationSchema.StatusCommand.alterUserDefaultRole
    | Grant _
    | GrantProxy _ -> Some InformationSchema.StatusCommand.grant
    | Revoke _
    | RevokeProxy _ -> Some InformationSchema.StatusCommand.revoke
    | CreateTrigger _ -> Some InformationSchema.StatusCommand.createTrigger
    | DropTrigger _ -> Some InformationSchema.StatusCommand.dropTrigger
    | CreateView _ -> Some InformationSchema.StatusCommand.createView
    | DropView _ -> Some InformationSchema.StatusCommand.dropView
    | ChecksumTables _ -> Some InformationSchema.StatusCommand.checksum
    | Explain(_, statement) -> statementStatusCommand statement
    | SetTriggerNew _ -> None

let private executeParsedStatement (session: Session) (stmt: Statement) : Session * QueryResult =
    stmt
    |> statementStatusCommand
    |> Option.iter (InformationSchema.recordCommand session.StatusCounters)

    let dbName = session.Database |> Option.defaultValue defaultDatabase

    let authorizationStore = session.Store
    let requiredPrivileges = requiredPrivilegesForStatement session authorizationStore dbName stmt

    // Transaction write preparation acquires locks that outlive the statement,
    // so authorization must be decided against the live account catalog first.
    let access =
        let statementAccess =
            match stmt with
            | Grant(privileges, level, _, _)
            | Revoke(privileges, level, _) ->
                Auth.checkDynamicGrantOptionsForAccount
                    authorizationStore
                    (accountOf session)
                    session.ActiveRoles
                    (privileges |> List.map _.Name)
                    (Auth.targetOfLevel dbName level)
            | GrantProxy(proxied, _, _)
            | RevokeProxy(proxied, _) ->
                Auth.checkProxyGrantAuthority authorizationStore (accountOf session) (Auth.account (fst proxied) (snd proxied))
            | GrantRoles(roles, _, _)
            | RevokeRoles(roles, _) ->
                Auth.checkRoleGrantAuthorityForAccount authorizationStore (accountOf session) session.ActiveRoles roles
            | _ -> Ok()

        let transactionAccess () =
            match session.Tx, stmt with
            | Some tx, (Select _ | Union _) when tx.ReadOnly && statementContainsUpdateLock stmt ->
                Error(1792, "Cannot execute statement in a READ ONLY transaction")
            | Some tx, (Select _ | Union _ | Explain _ | ChecksumTables _) when tx.ReadOnly ->
                Auth.checkForAccountWithRoles authorizationStore (accountOf session) session.ActiveRoles requiredPrivileges
            | Some tx, _ when tx.ReadOnly -> Error(1792, "Cannot execute statement in a READ ONLY transaction")
            | _ -> Auth.checkForAccountWithRoles authorizationStore (accountOf session) session.ActiveRoles requiredPrivileges

        statementAccess
        |> Result.bind transactionAccess

    let execute session =
        let store = Session.currentStore session
        let lockingReadView () =
            match session.Tx with
            | Some transaction when statementContainsLockingRead stmt ->
                Some(fun () -> rebaseTransactionSnapshot session transaction |> snd)
            | _ -> None

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

        let evaluateWithLockingView () =
            match lockingReadView () with
            | Some current -> Executor.withLockingReadStore current (lockWaitTimeout session) evaluate
            | None -> evaluate ()

        let lastInsertId, lastGeneratedId, result, columnMetadata, calculatedFoundRows =
            DynamicScope.withValue storedFunctionSession (Some session) (fun () ->
                Diagnostics.withDivisionByZeroPolicy (divisionByZeroPolicy store stmt) evaluateWithLockingView)

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
            match access with
            | Error _ -> execute session
            | Ok() ->
                session
                |> startTransactionStatement
                |> prepareTransactionWrite stmt
                |> syncTransactionView
                |> execute

        let canAllocateAutoIncrement =
            match stmt with
            | Insert _
            | InsertSelect _
            | Replace _
            | ReplaceSelect _
            | ReplaceSet _
            | LoadData _
            | Update _
            | Delete _ -> true
            | _ -> false

        if canAllocateAutoIncrement then
            executed.Tx
            |> Option.iter (fun transaction -> Storage.bumpAutoIncrementsInto executed.Store transaction.Snapshot.Catalog)

        executed, result
    | None -> execute session

let private executeWithStatementAccess session accesses execute =
    match
        TableLocks.withStatementAccess
            (lockWaitTimeout session)
            session.Store
            session.ConnectionId
            accesses
            execute
    with
    | Ok result -> result
    | Error(code, message) -> session, Err(code, message)

type private StatementLockBoundary =
    | AcquireStatementLock
    | StatementLockHeld

let private replicationUnsafeFunctions =
    Set.ofList [ "RAND"; "RANDOM_BYTES"; "SYSDATE"; "UUID"; "UUID_SHORT" ]

let private isReplicationUnsafe statement =
    Expression.statementExists
        (function
        | FuncCall(name, _) -> replicationUnsafeFunctions.Contains(name.ToUpperInvariant())
        | _ -> false)
        statement

let private executeParsedCoreWith lockBoundary (session: Session) (stmt: Statement) : Session * QueryResult =
    let store = Session.currentStore session
    let database = session.Database |> Option.defaultValue defaultDatabase
    let accesses = TableLocks.accessesForStatement store session.TemporaryCatalog database stmt
    let protectedTables = DynamicScope.valueOrDefault Set.empty storedProgramProtectedTables
    let statementTables =
        accesses
        |> List.map (fun access -> triggerTableKey access.Database access.Table)
        |> Set.ofList

    let writtenTable = accesses |> List.tryFind (fun access -> access.Mode = TableLocks.WriteAccess)
    let protectedWrite =
        accesses
        |> List.tryFind (fun access ->
            access.Mode = TableLocks.WriteAccess
            && Set.contains (triggerTableKey access.Database access.Table) protectedTables)

    match creatingTable.Value, insideFunctionOrTrigger session, writtenTable, protectedWrite with
    | Some target, true, Some access, _ ->
        session,
        Err(
            1746,
            sprintf "Can't update table '%s' while '%s' is being created." access.Table target
        )
    | _, _, _, Some access ->
        session,
        Err(
            1442,
            sprintf
                "Can't update table '%s' in stored function/trigger because it is already used by statement which invoked this stored function/trigger."
                access.Table
        )
    | _ ->
        let execute () =
            DynamicScope.withValue storedProgramProtectedTables (Set.union protectedTables statementTables) (fun () ->
                InformationSchema.withViewer
                    store
                    (accountOf session)
                    session.ActiveRoles
                    (fun () -> executeParsedStatement session stmt))

        let executed, result =
            match lockBoundary with
            | AcquireStatementLock -> executeWithStatementAccess session accesses execute
            | StatementLockHeld -> execute ()

        let executed =
            match terminalErrorInfo result with
            | Some _ -> executed
            | None ->
                let readTransactional = accesses |> List.exists (fun access -> access.Mode = TableLocks.ReadAccess)
                let wroteTransactional = accesses |> List.exists (fun access -> access.Mode = TableLocks.WriteAccess)

                Session.trackTransactionActivity
                    readTransactional
                    wroteTransactional
                    (isReplicationUnsafe stmt)
                    executed

        executed, result

let private executeParsedCore session stmt = executeParsedCoreWith AcquireStatementLock session stmt
let private executeParsedCoreUnderLock session stmt = executeParsedCoreWith StatementLockHeld session stmt

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
    | CreateServer _
    | AlterServer _
    | DropServer _
    | CreateRole _
    | DropRole _
    | Grant _
    | Revoke _
    | GrantProxy _
    | RevokeProxy _
    | GrantRoles _
    | RevokeRoles _
    | SetDefaultRole _
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
    | LoadData _
    | Select _
    | Union _
    | Update _
    | Delete _
    | ChecksumTables _
    | Explain _ -> true
    | Do _ -> true
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
    | Some CreateTemporary, CreateTableAs(name, _, _, _) -> [ splitQualified dbName name ]
    | Some DropTemporary, DropTable(names, _) -> names |> List.map (splitQualified dbName)
    | _ -> []

let private moveTemporaryKey sourceDb sourceTable targetTable keys =
    let source = tableKey sourceDb sourceTable

    if Set.contains source keys then
        keys |> Set.remove source |> Set.add (tableKey sourceDb targetTable)
    else
        keys

let private temporaryKeysAfterStatement dbName beforeKeys stmt =
    match stmt with
    | RenameTable pairs ->
        pairs
        |> List.fold
            (fun keys (sourceName, targetName) ->
                let sourceDb, sourceTable = splitQualified dbName sourceName
                let _, targetTable = splitQualified dbName targetName
                moveTemporaryKey sourceDb sourceTable targetTable keys)
            beforeKeys
    | AlterTable(sourceName, actions) ->
        let sourceDb, sourceTable = splitQualified dbName sourceName

        actions
        |> List.choose (function RenameTo target -> Some target | _ -> None)
        |> List.tryLast
        |> Option.map (fun target -> moveTemporaryKey sourceDb sourceTable target beforeKeys)
        |> Option.defaultValue beforeKeys
    | _ -> beforeKeys

let private temporaryRenameSourceKinds dbName beforeKeys pairs =
    pairs
    |> List.fold
        (fun (hasTemporary, hasPermanent, keys) (sourceName, targetName) ->
            let sourceDb, sourceTable = splitQualified dbName sourceName
            let _, targetTable = splitQualified dbName targetName
            let source = tableKey sourceDb sourceTable

            if Set.contains source keys then
                true,
                hasPermanent,
                keys |> Set.remove source |> Set.add (tableKey sourceDb targetTable)
            else
                hasTemporary, true, keys)
        (false, false, beforeKeys)

let private mixesTemporaryAndPermanentRenames dbName beforeKeys = function
    | RenameTable pairs ->
        let hasTemporary, hasPermanent, _ = temporaryRenameSourceKinds dbName beforeKeys pairs
        hasTemporary && hasPermanent
    | _ -> false

let private changesOnlyTemporaryCatalog dbName beforeKeys action stmt =
    match action, stmt with
    | Some _, _ -> true
    | None, AlterTable(sourceName, _) ->
        let sourceDb, sourceTable = splitQualified dbName sourceName
        Set.contains (tableKey sourceDb sourceTable) beforeKeys
    | None, RenameTable pairs ->
        let hasTemporary, hasPermanent, _ = temporaryRenameSourceKinds dbName beforeKeys pairs
        hasTemporary && not hasPermanent
    | _ -> false

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
    | RowsDeleted(db, table, _)
    | AutoIncrementAdvanced(db, table, _) when isTemporary db table -> None
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
    let executed, result = executeParsedCoreUnderLock workingSession stmt

    match result with
    | Err _ -> { executed with Store = session.Store; Tx = session.Tx }, result
    | _ ->
        let afterKeys =
            match action with
            | Some CreateTemporary -> Set.union beforeKeys targets
            | Some DropTemporary -> Set.difference beforeKeys targets
            | None -> temporaryKeysAfterStatement dbName beforeKeys stmt

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
            if changesOnlyTemporaryCatalog dbName beforeKeys action stmt then
                baseCatalog
            else
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
    let beforeKeys = temporaryKeys session.TemporaryCatalog

    let statementAccesses () =
        TableLocks.accessesForStatement
            (Session.currentStore session)
            session.TemporaryCatalog
            dbName
            stmt

    let executed, result =
        if mixesTemporaryAndPermanentRenames dbName beforeKeys stmt then
            session, Err(1105, "RENAME TABLE cannot mix temporary and permanent tables")
        elif usesTemporary && xaAssociation session |> Option.isSome then
            session, Err(4091, "XA: Temporary tables cannot be accessed inside XA transactions when xa_detach_on_prepare=ON")
        elif usesTemporary then
            executeWithStatementAccess session (statementAccesses ()) (fun () ->
                executeWithTemporaryCatalog action session stmt)
        elif causesImplicitCommit stmt && xaAssociation session |> Option.isSome then
            let state = xaAssociation session |> Option.map (snd >> xaStateName) |> Option.defaultValue "NON-EXISTING"
            session, xaRmFail state
        elif causesImplicitCommit stmt && insideFunctionOrTrigger session then
            session, Err(1422, "Explicit or implicit commit is not allowed in stored function or trigger.")
        elif causesImplicitCommit stmt then
            let session = commitSession session
            TableLocks.releaseExplicit session.Store session.ConnectionId

            let execute () =
                if changesCatalogMembership stmt then
                    executeParsedCore session stmt
                else
                    Storage.withDatabaseLocks
                        (lockWaitTimeout session)
                        session.Store
                        (implicitCommitDatabases dbName stmt)
                        (fun () -> executeParsedCore session stmt)

            match stmt with
            | CreateTableAs(name, _, _, _) ->
                DynamicScope.withValue creatingTable (Some(splitQualified dbName name |> snd)) execute
            | _ -> execute ()
        else
            let session =
                if session.Tx.IsNone && autocommitDisabled session && startsTransaction stmt then
                    beginTransaction
                        ImplicitTrackedTransaction
                        (pendingTransactionCharacteristics session)
                        (configuredReadOnly session)
                        session
                else
                    session

            if session.Tx.IsSome || not (startsTransaction stmt) then
                executeParsedCore session stmt
            else
                let mutable working =
                    beginTransaction
                        ImplicitTrackedTransaction
                        (pendingTransactionCharacteristics session)
                        (configuredReadOnly session)
                        session

                let executeAutocommit () =
                    try
                        let executed, result = executeParsedCoreUnderLock working stmt
                        working <- executed

                        match terminalErrorInfo result with
                        | Some _ -> rollbackSession executed, result
                        | None -> commitSessionWith true executed, result
                    with _ ->
                        rollbackSession working |> ignore
                        reraise ()

                executeWithStatementAccess session (statementAccesses ()) executeAutocommit

    TableHandler.invalidate session stmt executed result, result

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

type private CompletionDirective =
    | UseCompletionDefault
    | EnableCompletion
    | DisableCompletion

type private TransactionCompletion =
    { Chain: CompletionDirective
      Release: CompletionDirective }

/// One closed representation keeps direct and prepared execution from
/// recognizing different command sets.
type private Probe =
    | SetAutocommit of value: string
    | SetTransactionIsolation of scope: TransactionIsolationScope * level: string
    | SetTransactionAccess of sessionScope: bool * readOnly: bool
    | SetRoleStatement
    | SetDefaultRoleStatement
    | SetCharacterSet of charset: string
    | SetPassword of user: string option * password: string
    | SetVar
    | RollbackTo of savepoint: string
    | Begin of readOnly: bool option
    | Commit of completion: TransactionCompletion
    | Rollback of completion: TransactionCompletion
    | Savepoint of name: string
    | Release of name: string
    | Use of dbName: string
    | ShowVariables of isGlobal: bool
    | ShowStatus of isGlobal: bool
    | ShowEngines
    | ShowEngineInnodbStatus
    | ShowPlugins
    | ShowBinaryLogs
    | ShowBinaryLogStatus
    | ShowReplicaStatus
    | MaintainTables of operation: string * tables: string list
    | MaintainPartitions of operation: string * table: string * partitions: string list option
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
    | ShowGrants of user: string option * usingRoles: string option
    | ShowCreateUser of user: string
    | ShowCreateProgram of kind: string * name: string
    | ShowPrivileges
    | FlushPrivileges
    | FlushUserResources
    | FlushStatus
    | FlushTables
    | FlushOptimizerCosts
    | FlushLogs
    | LockTables
    | UnlockTables

let private probeCausesImplicitCommit = function
    | SetPassword _
    | MaintainTables _
    | MaintainPartitions _
    | AlterKeysNoop _
    | FlushPrivileges
    | FlushUserResources
    | FlushStatus
    | FlushTables
    | FlushOptimizerCosts
    | FlushLogs
    | LockTables
    | UnlockTables -> true
    | _ -> false

let private probeForbiddenInFunctionOrTrigger probe =
    probeCausesImplicitCommit probe
    || (match probe with
        | SetAutocommit _
        | Begin _
        | Commit _
        | Rollback _
        | RollbackTo _
        | Savepoint _
        | Release _ -> true
        | _ -> false)

let private beginProbeExecution session probe =
    match probe with
    | LockTables
    | UnlockTables -> session
    | _ when probeCausesImplicitCommit probe -> commitSession session
    | _ -> session

let private probeStatusCommand = function
    | SetAutocommit _
    | SetTransactionIsolation _
    | SetTransactionAccess _
    | SetCharacterSet _
    | SetVar -> Some InformationSchema.StatusCommand.setOption
    | SetRoleStatement -> Some InformationSchema.StatusCommand.setRole
    | SetDefaultRoleStatement -> Some InformationSchema.StatusCommand.alterUserDefaultRole
    | SetPassword _ -> Some InformationSchema.StatusCommand.setPassword
    | Begin _ -> Some InformationSchema.StatusCommand.beginTransaction
    | Commit _ -> Some InformationSchema.StatusCommand.commit
    | Rollback _ -> Some InformationSchema.StatusCommand.rollback
    | RollbackTo _ -> Some InformationSchema.StatusCommand.rollbackToSavepoint
    | Savepoint _ -> Some InformationSchema.StatusCommand.savepoint
    | Release _ -> Some InformationSchema.StatusCommand.releaseSavepoint
    | Use _ -> Some InformationSchema.StatusCommand.changeDatabase
    | ShowVariables _ -> Some InformationSchema.StatusCommand.showVariables
    | ShowStatus _ -> Some InformationSchema.StatusCommand.showStatus
    | ShowEngines -> Some InformationSchema.StatusCommand.showStorageEngines
    | ShowEngineInnodbStatus -> Some InformationSchema.StatusCommand.showEngineStatus
    | ShowPlugins -> Some InformationSchema.StatusCommand.showPlugins
    | ShowBinaryLogs -> Some InformationSchema.StatusCommand.showBinaryLogs
    | ShowBinaryLogStatus -> Some InformationSchema.StatusCommand.showBinaryLogStatus
    | ShowReplicaStatus -> Some InformationSchema.StatusCommand.showReplicaStatus
    | MaintainTables(operation, _)
    | MaintainPartitions(operation, _, _) ->
        match operation.ToUpperInvariant() with
        | "ANALYZE" -> Some InformationSchema.StatusCommand.analyze
        | "CHECK" -> Some InformationSchema.StatusCommand.check
        | "OPTIMIZE" -> Some InformationSchema.StatusCommand.optimize
        | "REPAIR" -> Some InformationSchema.StatusCommand.repair
        | _ -> None
    | ShowOpenTables _ -> Some InformationSchema.StatusCommand.showOpenTables
    | ShowCreateDatabase _ -> Some InformationSchema.StatusCommand.showCreateDatabase
    | ShowCharset -> Some InformationSchema.StatusCommand.showCharacterSets
    | ShowProcesslist _ -> Some InformationSchema.StatusCommand.showProcesslist
    | ShowTriggers _ -> Some InformationSchema.StatusCommand.showTriggers
    | ShowEvents _ -> Some InformationSchema.StatusCommand.showEvents
    | ShowRoutineStatus kind when kind.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) ->
        Some InformationSchema.StatusCommand.showFunctionStatus
    | ShowRoutineStatus _ -> Some InformationSchema.StatusCommand.showProcedureStatus
    | Kill _ -> Some InformationSchema.StatusCommand.kill
    | AlterKeysNoop _ -> Some InformationSchema.StatusCommand.alterTable
    | ShowConditions true -> Some InformationSchema.StatusCommand.showErrors
    | ShowConditions false -> Some InformationSchema.StatusCommand.showWarnings
    | ShowMessageCount _ -> Some InformationSchema.StatusCommand.select
    | ShowDatabases -> Some InformationSchema.StatusCommand.showDatabases
    | ShowTableStatus -> Some InformationSchema.StatusCommand.showTableStatus
    | ShowTables -> Some InformationSchema.StatusCommand.showTables
    | ShowCreate _
    | ShowCreateView _ -> Some InformationSchema.StatusCommand.showCreateTable
    | ShowCreateTrigger _ -> Some InformationSchema.StatusCommand.showCreateTrigger
    | ShowColumns _
    | Describe _ -> Some InformationSchema.StatusCommand.showFields
    | ShowIndex _ -> Some InformationSchema.StatusCommand.showIndexes
    | ShowCollation -> Some InformationSchema.StatusCommand.showCollations
    | ShowGrants _ -> Some InformationSchema.StatusCommand.showGrants
    | ShowCreateUser _ -> Some InformationSchema.StatusCommand.showCreateUser
    | ShowCreateProgram(kind, _) when kind.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase) ->
        Some InformationSchema.StatusCommand.showCreateFunction
    | ShowCreateProgram _ -> Some InformationSchema.StatusCommand.showCreateProcedure
    | ShowPrivileges -> Some InformationSchema.StatusCommand.showPrivileges
    | FlushPrivileges
    | FlushUserResources
    | FlushStatus
    | FlushTables
    | FlushOptimizerCosts
    | FlushLogs -> Some InformationSchema.StatusCommand.flush
    | LockTables -> Some InformationSchema.StatusCommand.lockTables
    | UnlockTables -> Some InformationSchema.StatusCommand.unlockTables

let private completionDirective (present: Group) (negated: Group) =
    if not present.Success then
        UseCompletionDefault
    elif negated.Success then
        DisableCompletion
    else
        EnableCompletion

let private tryTransactionCompletion command =
    let matched = transactionCompletion.Match command

    if not matched.Success then
        None
    else
        let completion =
            { Chain = completionDirective matched.Groups.["chain"] matched.Groups.["noChain"]
              Release = completionDirective matched.Groups.["release"] matched.Groups.["noRelease"] }

        match completion.Chain, completion.Release with
        | EnableCompletion, EnableCompletion -> None
        | _ when matched.Groups.["command"].Value.Equals("COMMIT", StringComparison.OrdinalIgnoreCase) -> Some(Commit completion)
        | _ -> Some(Rollback completion)

let private tryProbe (parserOptions: Parser.ParserOptions) (sql: string) : Probe option =
    let command = sql.TrimStart()
    let completion = tryTransactionCompletion command

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
    elif alterCurrentUserPasswordRe.IsMatch sql then
        Some(SetPassword(None, (alterCurrentUserPasswordRe.Match sql).Groups.[1].Value))
    elif setPasswordRe.IsMatch sql then
        let m = setPasswordRe.Match sql
        Some(SetPassword((if m.Groups.[1].Success then Some m.Groups.[1].Value else None), m.Groups.[2].Value))
    elif setDefaultRoleStatement.IsMatch command then
        Some SetDefaultRoleStatement
    elif setRoleStatement.IsMatch command then
        Some SetRoleStatement
    elif command.StartsWith("SET ", StringComparison.OrdinalIgnoreCase) then
        Some SetVar
    elif rollbackToSavepointStmt.IsMatch sql then
        Some(RollbackTo((rollbackToSavepointStmt.Match sql).Groups.[2].Value))
    elif beginTx.IsMatch command then
        let mode = (beginTx.Match command).Groups.[1]
        Some(Begin(if mode.Success then Some(mode.Value = "ONLY") else None))
    elif completion.IsSome then
        completion
    elif savepointStmt.IsMatch sql then
        Some(Savepoint((savepointStmt.Match sql).Groups.[1].Value))
    elif releaseSavepointStmt.IsMatch sql then
        Some(Release((releaseSavepointStmt.Match sql).Groups.[1].Value))
    elif command.StartsWith("USE ", StringComparison.OrdinalIgnoreCase) then
        Some(Use(command.Substring(4).Trim().Trim('`')))
    elif showVariablesRe.IsMatch sql then
        let scope = (showVariablesRe.Match sql).Groups.[1].Value
        Some(ShowVariables(scope.Trim().ToUpperInvariant() = "GLOBAL"))
    elif showStatusRe.IsMatch sql then
        let scope = (showStatusRe.Match sql).Groups.[1]
        Some(ShowStatus(scope.Success && scope.Value.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase)))
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
    elif partitionMaintenanceRe.IsMatch sql then
        match Parser.parsePartitionMaintenanceWithOptions parserOptions sql with
        | Ok(table, operation, partitions) -> Some(MaintainPartitions(operation, table, partitions))
        | Error _ -> None
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
        Some(
            ShowGrants(
                (if m.Groups.[1].Success then Some m.Groups.[1].Value else None),
                (if m.Groups.[2].Success then Some m.Groups.[2].Value else None)
            )
        )
    elif flushPrivilegesRe.IsMatch sql then
        Some FlushPrivileges
    elif flushUserResourcesRe.IsMatch sql then
        Some FlushUserResources
    elif flushStatusRe.IsMatch sql then
        Some FlushStatus
    elif flushTablesRe.IsMatch sql then
        Some FlushTables
    elif flushOptimizerCostsRe.IsMatch sql then
        Some FlushOptimizerCosts
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
    elif command.StartsWith("SHOW DATABASES", StringComparison.OrdinalIgnoreCase) then
        Some ShowDatabases
    elif command.StartsWith("SHOW TABLE STATUS", StringComparison.OrdinalIgnoreCase) then
        Some ShowTableStatus
    elif command.StartsWith("SHOW COLLATION", StringComparison.OrdinalIgnoreCase) then
        Some ShowCollation
    elif
        command.StartsWith("SHOW TABLES", StringComparison.OrdinalIgnoreCase)
        || command.StartsWith("SHOW FULL TABLES", StringComparison.OrdinalIgnoreCase)
    then
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

let private acquireResolvedTableAccesses (session: Session) (accesses: TableLocks.Access list) =
    let privileges =
        accesses
        |> List.choose (fun table ->
            table.ReferenceName
            |> Option.map (fun _ ->
                [ "LOCK TABLES", Auth.OnTable(table.Database, table.Table)
                  "SELECT", Auth.OnTable(table.Database, table.Table) ]))
        |> List.collect id

    match checkSessionAccess session session.Store privileges with
    | Error(code, message) -> session, Err(code, message)
    | Ok() ->
        match TableLocks.acquireExplicit (lockWaitTimeout session) session.Store session.ConnectionId accesses with
        | Ok() ->
            let readTransactional = accesses |> List.exists (fun access -> access.Mode = TableLocks.ReadAccess)
            let wroteTransactional = accesses |> List.exists (fun access -> access.Mode = TableLocks.WriteAccess)
            Session.trackExplicitTableLocks readTransactional wroteTransactional session, Affected 0UL
        | Error(code, message) -> session, Err(code, message)

let private acquireExplicitTableAccesses session requested =
    let session = commitSession session
    TableLocks.releaseExplicit session.Store session.ConnectionId
    let database = session.Database |> Option.defaultValue defaultDatabase

    match TableLocks.explicitAccesses session.Store session.TemporaryCatalog database requested with
    | Error(code, message) -> session, Err(code, message)
    | Ok accesses -> acquireResolvedTableAccesses session accesses

let private acquireExplicitTableLocks session sql =
    match Parser.parseTableLocksWithOptions (parserOptionsForSession session) sql with
    | Error detail -> session, parserError sql detail
    | Ok requested -> acquireExplicitTableAccesses session requested

let private acquireFlushTableLocks session sql =
    match Parser.parseFlushTableLocksWithOptions (parserOptionsForSession session) sql with
    | Error detail -> session, parserError sql detail
    | Ok requested ->
        let database = session.Database |> Option.defaultValue defaultDatabase

        match TableLocks.flushAccesses session.Store session.TemporaryCatalog database requested with
        | Error(code, message) -> session, Err(code, message)
        | Ok accesses -> acquireResolvedTableAccesses session accesses

let private resolveCompletionDirective defaultValue = function
    | UseCompletionDefault -> defaultValue
    | EnableCompletion -> true
    | DisableCompletion -> false

let private applyTransactionCompletion completion characteristics readOnly session =
    let defaultChain, defaultRelease =
        match session.Variables |> Map.tryFind "completion_type" |> Option.flatten with
        | Some "CHAIN" -> true, false
        | Some "RELEASE" -> false, true
        | _ -> false, false

    let session =
        if resolveCompletionDirective defaultChain completion.Chain then
            beginTransaction ExplicitTrackedTransaction characteristics readOnly session
        else
            session

    { session with
        CloseAfterReply = resolveCompletionDirective defaultRelease completion.Release }

let private runProbe (session: Session) (sql: string) (probe: Probe) : Session * QueryResult =
    probe
    |> probeStatusCommand
    |> Option.iter (InformationSchema.recordCommand session.StatusCounters)

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
    | SetAutocommit "1" when xaAssociation session |> Option.isSome ->
        let state = xaAssociation session |> Option.map (snd >> xaStateName) |> Option.defaultValue "NON-EXISTING"
        session, xaRmFail state
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
                let updated =
                    if scope = NextTransactionIsolation then
                        Session.setTransactionCharacteristics (pendingTransactionCharacteristics updated) updated
                    else
                        updated
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
            let updated = Session.setTransactionCharacteristics (pendingTransactionCharacteristics updated) updated
            Session.trackSystemVariableAssignments true [] updated, Affected 0UL
    | SetRoleStatement ->
        match Parser.parseWithOptions (parserOptionsForSession session) sql with
        | Ok(SetRole _ as statement) -> applyRoleStatement session statement
        | _ -> session, parserError sql "Invalid SET ROLE statement"
    | SetDefaultRoleStatement ->
        match Parser.parseWithOptions (parserOptionsForSession session) sql with
        | Ok(SetDefaultRole _ as statement) -> applyRoleStatement session statement
        | _ -> session, parserError sql "Invalid SET DEFAULT ROLE statement"
    | SetCharacterSet charset ->
        let charset = Charset.canonicalName charset
        let collation = Charset.defaultCollationName charset

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

        match checkSessionAccess session store required |> Result.bind (fun () -> Auth.setPassword store wanted.Name wanted.Host password) with
        | Ok() -> { session with PasswordExpired = false }, Affected 0UL
        | Error(code, msg) -> session, Err(code, msg)
    | SetVar -> handleSet session sql
    | RollbackTo name -> rollbackToSavepoint name session
    | Begin readOnly ->
        match xaAssociation session with
        | Some(_, state) -> session, xaRmFail (xaStateName state)
        | None ->
            let access = readOnly |> Option.defaultValue (configuredReadOnly session)
            let characteristics = explicitTransactionCharacteristics readOnly session
            TableLocks.releaseExplicit session.Store session.ConnectionId
            beginTransaction ExplicitTrackedTransaction characteristics access session, Affected 0UL
    | Commit completion ->
        match xaAssociation session with
        | Some(_, state) -> session, xaRmFail (xaStateName state)
        | None ->
            let readOnly = session.Tx |> Option.map _.ReadOnly |> Option.defaultValue false
            let characteristics = session.TransactionTracking.Characteristics
            let committed = commitSession session
            applyTransactionCompletion completion characteristics readOnly committed, Affected 0UL
    | Rollback completion ->
        match xaAssociation session with
        | Some(_, state) -> session, xaRmFail (xaStateName state)
        | None ->
            let readOnly = session.Tx |> Option.map _.ReadOnly |> Option.defaultValue false
            let characteristics = session.TransactionTracking.Characteristics
            let rolledBack = rollbackSession session
            applyTransactionCompletion completion characteristics readOnly rolledBack, Affected 0UL
    | Savepoint name -> savepoint name session
    | Release name -> releaseSavepoint name session
    | Use dbName ->
        let store = Session.currentStore session

        if not (Storage.databaseExists store dbName) then
            let code, msg = Storage.toMySqlError (Storage.NoSuchDatabase dbName)
            session, Err(code, msg)
        elif canSessionSeeDatabase session store dbName then
            Session.trackSchemaAssignment dbName ({ session with Database = Some dbName }), Affected 0UL
        else
            session, Err(1044, sprintf "Access denied for user '%s'@'%s' to database '%s'" session.User session.AccountHost dbName)
    | ShowVariables isGlobal -> session, handleShowVariables session isGlobal sql
    | ShowStatus isGlobal ->
        session,
        InformationSchema.showStatus
            isGlobal
            session.StatusCounters
            (session.Compression |> Option.map Compression.Algorithm.name)
            (session.Compression |> Option.map Compression.Algorithm.level |> Option.defaultValue 0)
            session.TransportMetrics.BytesReceived
            session.TransportMetrics.BytesSent
            session.TlsCipher
            session.TlsVersion
            (statusFilter sql)
        |> showResult
    | ShowEngines -> session, InformationSchema.showEngines () |> showResult
    | ShowEngineInnodbStatus ->
        match checkSessionAccess session session.Store [ "PROCESS", Auth.Global ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() ->
            session,
            ResultSet(
                [ "Type"; "Name"; "Status" ],
                [ [ Some "InnoDB"; Some ""; Some "fsdb uses an in-memory transactional row store" ] ]
            )
    | ShowPlugins ->
        session, InformationSchema.showPlugins () |> showResult
    | ShowBinaryLogs
    | ShowBinaryLogStatus ->
        match checkAnySessionGlobalPrivilege session "SUPER, REPLICATION CLIENT" [ "SUPER"; "REPLICATION CLIENT" ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() -> session, Err(1381, "You are not using binary logging")
    | ShowReplicaStatus ->
        match checkAnySessionGlobalPrivilege session "SUPER, REPLICATION CLIENT" [ "SUPER"; "REPLICATION CLIENT" ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() ->
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
    | MaintainPartitions(operation, tableRef, selectedPartitions) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, tableName = splitQualified sessionDb tableRef
        let store = Session.currentStore session

        match checkTableMaintenanceAccess session store dbName tableName operation with
        | Error(code, message) -> session, Err(code, message)
        | Ok() ->
            match InformationSchema.findTable store.Catalog dbName tableName with
            | Error(code, message) -> session, Err(code, message)
            | Ok { Partitioning = None } ->
                session, Err(1505, "Partition management on a not partitioned table is not possible")
            | Ok({ Partitioning = Some partitioning } as table) ->
                let partitionNames = hashPartitionNames partitioning

                let unknown =
                    selectedPartitions
                    |> Option.bind (List.tryFind (fun name -> not (Map.containsKey (name.ToLowerInvariant()) partitionNames)))

                match unknown with
                | Some name -> session, Err(1735, sprintf "Unknown partition '%s' in table '%s'" name table.OriginalName)
                | None ->
                    let qualified = dbName + "." + tableName
                    let rows =
                        if operation = "optimize" then
                            [ maintenanceRow
                                  qualified
                                  operation
                                  "note"
                                  "Table does not support optimize on partitions. All partitions will be rebuilt and analyzed."
                              maintenanceRow qualified operation "status" "OK" ]
                        else
                            [ maintenanceRow qualified operation "status" "OK" ]

                    session, maintenanceResult rows
    | MaintainTables(operation, tables) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let store = Session.currentStore session
        let resolved = tables |> List.map (fun tableRef -> splitQualified sessionDb tableRef)

        match
            resolved
            |> List.tryPick (fun (dbName, tableName) ->
                match checkTableMaintenanceAccess session store dbName tableName operation with
                | Ok() -> None
                | Error error -> Some error)
        with
        | Some(code, message) -> session, Err(code, message)
        | None ->
            let rows =
                resolved
                |> List.collect (fun (dbName, tableName) ->
                    let qualified = dbName + "." + tableName

                    match Storage.scan store dbName tableName with
                    | Ok _ when operation = "optimize" ->
                        [ maintenanceRow qualified operation "note" "Table does not support optimize, doing recreate + analyze instead"
                          maintenanceRow qualified operation "status" "OK" ]
                    | Ok _ when operation = "repair" ->
                        [ maintenanceRow qualified operation "note" "The storage engine for the table doesn't support repair" ]
                    | Ok _ -> [ maintenanceRow qualified operation "status" "OK" ]
                    | Error _ ->
                        [ maintenanceRow qualified operation "Error" (sprintf "Table '%s.%s' doesn't exist" dbName tableName) ])

            session, maintenanceResult rows
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
            |> List.filter (canSessionSeeTable session (Session.currentStore session) dbName)
            |> List.sort
            |> List.map (fun table -> [ Some dbName; Some table; Some "0"; Some "0" ])

        session, ResultSet([ "Database"; "Table"; "In_use"; "Name_locked" ], rows)
    | ShowCreateDatabase name ->
        let store = Session.currentStore session

        if not (Storage.databaseExists store name) then
            session, Err(1049, sprintf "Unknown database '%s'" name)
        elif not (canSessionSeeDatabase session store name) then
            session, Err(1044, sprintf "Access denied for user '%s'@'%s' to database '%s'" session.User session.AccountHost name)
        else
            let quotedName = name.Replace("`", "``")

            session,
            ResultSet(
                [ "Database"; "Create Database" ],
                [ [ Some name
                    Some(sprintf "CREATE DATABASE `%s` /*!40100 DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci */" quotedName) ] ]
            )
    | ShowCharset -> session, InformationSchema.showCharacterSet (likeSuffix sql) |> showResult
    | ShowPrivileges -> session, InformationSchema.showPrivileges () |> showResult
    | ShowProcesslist full ->
        let result =
            InformationSchema.withViewer
                session.Store
                (accountOf session)
                session.ActiveRoles
                (fun () -> InformationSchema.showProcesslist full |> showResult)

        session, result
    | ShowTriggers db ->
        let dbName = db |> Option.defaultValue (session.Database |> Option.defaultValue defaultDatabase)
        let store = Session.currentStore session

        if not (Storage.databaseExists store dbName) then
            session, Err(1049, sprintf "Unknown database '%s'" dbName)
        elif not (canSessionSeeDatabase session store dbName) then
            session, Err(1044, sprintf "Access denied for user '%s'@'%s' to database '%s'" session.User session.AccountHost dbName)
        else
            session,
            InformationSchema.withViewer store (accountOf session) session.ActiveRoles (fun () ->
                InformationSchema.showTriggers store.Catalog dbName)
            |> showResult
    | ShowEvents db ->
        let dbName = db |> Option.defaultValue (session.Database |> Option.defaultValue defaultDatabase)
        let store = Session.currentStore session

        match checkSessionAccess session store [ "EVENT", Auth.OnDb dbName ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() ->
            session,
            InformationSchema.withViewer store (accountOf session) session.ActiveRoles (fun () ->
                InformationSchema.showEvents store.Catalog (Some dbName))
            |> showResult
    | ShowRoutineStatus kind ->
        session,
        InformationSchema.withViewer (Session.currentStore session) (accountOf session) session.ActiveRoles (fun () ->
            InformationSchema.showRoutineStatus (Session.currentStore session).Catalog kind)
        |> showResult
    | Kill(queryOnly, id) ->
        let canSeeAll = hasSessionGlobalPrivilege session "PROCESS"
        let canKillAll = hasSessionGlobalPrivilege session "SUPER"

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

        match checkSessionAccess session session.Store [ "ALTER", Auth.OnTable(dbName, table) ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() ->
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
        let store = Session.currentStore session
        session,
        InformationSchema.withViewer store (accountOf session) session.ActiveRoles (fun () ->
            InformationSchema.showCreateTrigger store.Catalog dbName trigger)
        |> showResult
    | ShowColumns(full, name, dbOverride) ->
        let sessionDb = session.Database |> Option.defaultValue defaultDatabase
        let dbName, table = splitQualified sessionDb name
        let dbName = dbOverride |> Option.map stripIdentifierQuotes |> Option.defaultValue dbName
        let store = Session.currentStore session
        let viewColumns = Executor.viewColumns store (registryFor session)
        session,
        InformationSchema.withViewer store (accountOf session) session.ActiveRoles (fun () ->
            InformationSchema.showColumns
                (catalogWithOverlay session dbName table)
                (Some viewColumns)
                full
                dbName
                table
                (likeSuffix sql))
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
        InformationSchema.withViewer store (accountOf session) session.ActiveRoles (fun () ->
            InformationSchema.showColumns
                (catalogWithOverlay session dbName table)
                (Some viewColumns)
                false
                dbName
                table
                None)
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
    | ShowGrants(userOpt, usingRoles) ->
        // No FOR clause or CURRENT_USER selects the authenticated account.
        let wanted =
            match userOpt with
            | None -> accountOf session
            | Some u when u.Trim().ToUpperInvariant().StartsWith "CURRENT_USER" -> accountOf session
            | Some u -> accountRefOf u

        let roles =
            match usingRoles with
            | None -> Ok None
            | Some text ->
                match Parser.parseWithOptions (parserOptionsForSession session) ("SET ROLE " + text.TrimEnd(';')) with
                | Ok(SetRole(NamedRoles roles)) ->
                    roles
                    |> List.map (fun (name, host) -> Auth.account name host)
                    |> Some
                    |> Ok
                | _ -> Error(syntaxError sql)

        inspectAccount session wanted (fun () ->
            match roles with
            | Error error -> error
            | Ok roles ->
                match Auth.renderGrantsForAccountUsing (Session.currentStore session) wanted roles with
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
            match checkSessionAccess session session.Store [ "EVENT", Auth.OnDb database ] with
            | Error(code, message) -> session, Err(code, message)
            | Ok() ->
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
    | FlushPrivileges ->
        match checkAnySessionGlobalPrivilege session "RELOAD or FLUSH_PRIVILEGES" [ "RELOAD"; "FLUSH_PRIVILEGES" ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() -> session, Affected 0UL
    | FlushUserResources ->
        match checkAnySessionGlobalPrivilege session "RELOAD or FLUSH_USER_RESOURCES" [ "RELOAD"; "FLUSH_USER_RESOURCES" ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() ->
            Auth.resetAllAccountResources session.Store
            session, Affected 0UL
    | FlushStatus ->
        match checkAnySessionGlobalPrivilege session "RELOAD or FLUSH_STATUS" [ "RELOAD"; "FLUSH_STATUS" ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() ->
            InformationSchema.resetSessionStatuses ()
            session, Affected 0UL
    | FlushTables ->
        match checkAnySessionGlobalPrivilege session "RELOAD or FLUSH_TABLES" [ "RELOAD"; "FLUSH_TABLES" ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() when flushTableLocksRe.IsMatch sql && TableLocks.holdsExplicit session.Store session.ConnectionId ->
            session, Err(1192, "Can't execute the given command because you have active locked tables or an active transaction")
        | Ok() when flushTableLocksRe.IsMatch sql -> acquireFlushTableLocks session sql
        | Ok() -> session, Affected 0UL
    | FlushOptimizerCosts ->
        match checkAnySessionGlobalPrivilege session "RELOAD or FLUSH_OPTIMIZER_COSTS" [ "RELOAD"; "FLUSH_OPTIMIZER_COSTS" ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() -> session, Affected 0UL
    | FlushLogs ->
        match checkSessionAccess session session.Store [ "RELOAD", Auth.Global ] with
        | Error(code, message) -> session, Err(code, message)
        | Ok() -> session, Affected 0UL
    | LockTables ->
        match xaAssociation session with
        | Some(_, state) -> session, xaRmFail (xaStateName state)
        | None -> acquireExplicitTableLocks session sql
    | UnlockTables ->
        match xaAssociation session with
        | Some(_, state) -> session, xaRmFail (xaStateName state)
        | None ->
            let session =
                if TableLocks.holdsExplicit session.Store session.ConnectionId then
                    commitSession session
                else
                    session

            TableLocks.releaseExplicit session.Store session.ConnectionId
            Session.endTransactionTracking session, Affected 0UL
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
/// Validates COM_STMT_PREPARE without executing text-probed commands.
let private prepareStatementWithOptions
    (options: Parser.ParserOptions)
    (sql: string)
    : Result<Statement option * int, int * string> =
    let trimmed = sql.Trim().TrimEnd(';').Trim()
    let command = Parser.stripVersionCommentsWithOptions options trimmed

    if hasKeywordPrefix "LOAD DATA" command || hasKeywordPrefix "HANDLER" command || hasKeywordPrefix "XA" command then
        Result.Error(1295, "This command is not supported in the prepared statement protocol yet")
    elif (tryProbe options trimmed).IsSome then
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

        match checkSessionAccess session store (Auth.requiredPrivilegesInStore store schema statement) with
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
                        let metadata =
                            origin
                            |> Option.bind (fun source ->
                                Storage.tableSnapshot store source.Schema source.OriginalTable
                                |> Result.toOption
                                |> Option.map (fun table -> ColumnWire.metadataOfTableColumn table.Indexes column))
                            |> Option.defaultWith (fun () -> ColumnWire.metadataOfColumn column)

                        { Name = column.Name
                          Metadata =
                            { metadata with
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

type private ProcedureCreation =
    { Name: string
      IfNotExists: bool
      Parameters: string
      SecurityType: string
      Body: string
      Definer: string option }

type private FunctionCreation =
    { Name: string
      IfNotExists: bool
      Parameters: string
      ReturnType: string
      Characteristics: string
      Body: string
      Definer: string option }

type private TextRoutineCommand =
    | CreateProcedure of ProcedureCreation
    | CreateFunction of FunctionCreation
    | CallProcedure of name: string * arguments: string
    | DropProcedure of name: string * ifExists: bool
    | DropFunction of name: string * ifExists: bool

let private createProcedureRe =
    Regex(
        """^\s*CREATE\s+(?:DEFINER\s*=\s*(?<definer>(?:CURRENT_USER(?:\(\))?|(?:'[^']*'|`[^`]*`|[A-Za-z0-9_$.-]+)(?:\s*@\s*(?:'[^']*'|`[^`]*`|[A-Za-z0-9_$.:/%-]+))?))\s+)?PROCEDURE\s+(?<ifNotExists>IF\s+NOT\s+EXISTS\s+)?(?<name>\S+)\s*\((?<parameters>(?:[^()]|\([^()]*\))*)\)\s+(?:SQL\s+SECURITY\s+(?<security>INVOKER|DEFINER)\s+)?(?<body>.+)$""",
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline
    )

let private routineIdentifierPattern =
    """(?:`(?:``|[^`])+`|"(?:""|[^"])+"|[\p{L}\p{M}\p{Nd}_$]+)"""

let private callProcedureRe =
    Regex(
        @"^\s*CALL\s+(?<name>"
        + routineIdentifierPattern
        + @"(?:\s*\.\s*"
        + routineIdentifierPattern
        + @")?)(?:\s*\((?<arguments>.*)\))?\s*$",
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline
    )

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
            CreateProcedure
                { Name = create.Groups.["name"].Value
                  IfNotExists = create.Groups.["ifNotExists"].Success
                  Parameters = create.Groups.["parameters"].Value.Trim()
                  SecurityType = security
                  Body = create.Groups.["body"].Value
                  Definer =
                    if create.Groups.["definer"].Success then
                        Some(create.Groups.["definer"].Value.Trim())
                    else
                        None }
        )
    elif createFunction.Success then
        Some(
            CreateFunction
                { Name = createFunction.Groups.["name"].Value
                  IfNotExists = createFunction.Groups.["ifNotExists"].Success
                  Parameters = createFunction.Groups.["parameters"].Value.Trim()
                  ReturnType = createFunction.Groups.["returns"].Value.Trim()
                  Characteristics = createFunction.Groups.["characteristics"].Value.Trim()
                  Body = createFunction.Groups.["body"].Value
                  Definer =
                    if createFunction.Groups.["definer"].Success then
                        Some(createFunction.Groups.["definer"].Value.Trim())
                    else
                        None }
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

let private isSupportedStoredProgramText options sql =
    let probe = tryProbe options sql

    let supportedProbe =
        match probe with
        | Some SetVar ->
            splitSetAssignments options sql
            |> Result.exists (fun fragments ->
                not fragments.IsEmpty
                && (fragments
                    |> List.forall (fun fragment ->
                        setNames.IsMatch fragment
                        || setVar.IsMatch fragment
                        || (Parser.parseUserVariableSetAssignment fragment |> Result.isOk))))
        | Some _ -> true
        | None -> false

    supportedProbe
    || (tryTextPreparedCommand sql |> Result.exists Option.isSome)
    || (match tryTextRoutineCommand sql with
        | Some(CallProcedure _) -> true
        | _ -> false)

let private parseRoutineDefinition options parameters body =
    match
        StoredProgram.parseParameters options parameters,
        StoredProgram.parseRoutine options (isSupportedStoredProgramText options) body
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
        StoredProgram.parseRoutine options (StoredProgram.tryCall options >> Option.isSome) body
    with
    | Ok parsedParameters, Ok parsedReturnType, Ok statements
        when parsedParameters |> List.exists (fun parameter -> parameter.Mode <> StoredProgram.In) ->
        Error(syntaxError parameters)
    | Ok parsedParameters, Ok parsedReturnType, Ok statements ->
        StoredProgram.validateFunction parsedParameters statements
        |> Result.mapError routineValidationError
        |> Result.bind (fun () ->
            if statements |> List.collect StoredProgram.resultSetStatements |> List.isEmpty |> not then
                Error(Err(1415, "Not allowed to return a result set from a function"))
            else
                statements
                |> List.collect StoredProgram.executableSqlStatements
                |> List.tryPick (function
                    | Select _
                    | Union _
                    | Insert _
                    | InsertSelect _
                    | Replace _
                    | ReplaceSelect _
                    | ReplaceSet _
                    | LoadData _
                    | Update _
                    | Delete _
                    | Do _ -> None
                    | _ -> Some(Err(1422, "Explicit or implicit commit is not allowed in stored function or trigger.")))
                |> function
                    | Some error -> Error error
                    | None -> Ok(parsedParameters, parsedReturnType, statements))
    | Error _, _, _ -> Error(syntaxError parameters)
    | _, Error _, _ -> Error(syntaxError returnType)
    | _, _, Error _ when Regex.IsMatch(body, @"\b(?:PREPARE|EXECUTE|DEALLOCATE\s+PREPARE)\b", RegexOptions.IgnoreCase) ->
        Error(Err(1336, "Dynamic SQL is not allowed in stored function or trigger"))
    | _, _, Error _ -> Error(syntaxError body)

let private firstUnsafeStoredRoutineCall (registry: Functions.Registry) statements =
    statements
    |> List.collect StoredProgram.expressions
    |> List.tryPick (fun expression ->
        Expression.tryPick
            (function
            | FuncCall(name, _) ->
                registry.Extensions
                |> Map.tryFind (name.ToUpperInvariant())
                |> Option.filter (fun extension -> extension.DirectOnly || not extension.Deterministic)
                |> Option.map (fun _ -> name)
            | _ -> None)
            expression)

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
      Charset = None
      Srid = None }

let private parameterColumn (parameter: StoredProgram.Parameter) =
    { routineColumn parameter.Name parameter.ColumnType with
        Collation = parameter.Collation
        Charset = parameter.Charset }

let private coerceRoutineValue (store: Store) column value =
    Storage.coerceValue store.ExecutionSettings.SqlMode.Strict column value
    |> Result.mapError (Storage.toMySqlError >> Err)

let private evaluateRoutineExpression (session: Session) expression =
    let variables = expressionVariables session
    let store = Session.currentStore session
    let database = session.Database |> Option.defaultValue defaultDatabase

    let result =
        match checkSessionAccess session store (Auth.requiredPrivilegesForExpression database expression) with
        | Error(code, message) -> Error(Err(code, message))
        | Ok() ->
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

    let valuesOfResultRow (session: Session) (row: string option list) =
        let valueAt index (value: string option) =
            match value, List.tryItem index session.LastResultColumnMetadata with
            | Some text, Some metadata
                when metadata.Flags &&& BinaryFlag <> 0us
                     && (metadata.TypeId = TypeString
                         || metadata.TypeId = TypeVarString
                         || metadata.TypeId = TypeBlob)
                     || metadata.TypeId = TypeBit ->
                VBytes(Encoding.Latin1.GetBytes text)
            | Some text, _ -> VString text
            | None, _ -> VNull

        row |> List.mapi valueAt

    let assignSelectedValues targets values locals =
        List.zip targets values
        |> List.fold
            (fun assigned (target, value) ->
                assigned
                |> Result.bind (fun locals ->
                    match Map.tryFind target locals with
                    | None -> Error(Err(1327, sprintf "Undeclared variable: %s" target))
                    | Some local ->
                        coerceRoutineValue store local.Column value
                        |> Result.map (fun value -> Map.add target { local with Value = value } locals)))
            (Ok locals)

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
            | StoredProgram.SelectInto(sql, targets) ->
                let next, selected =
                    executeWithDiagnostics current (fun () ->
                        Executor.withRoutineVariables locals (fun () -> executeSql current sql))

                match selected with
                | ResultSet(columns, _) when columns.Length <> targets.Length ->
                    handleQueryResult
                        scope
                        next
                        locals
                        results
                        affectedRows
                        rest
                        (Err(1222, "The used SELECT statements have a different number of columns"))
                | ResultSet(_, []) ->
                    let noData =
                        SqlState.createWithState
                            1329
                            "02000"
                            "No data - zero rows fetched, selected, or processed"

                    match StoredProgram.tryHandler scope.Conditions scope.Statements noData with
                    | Some _ -> handleCondition scope next locals results affectedRows rest noData
                    | None ->
                        let warning = Diagnostics.fromWarning noData

                        currentDiagnostics.Value <-
                            { Conditions = [ warning ]
                              RowCount = 0L }
                        propagatedConditions.Add warning
                        run scope next locals results affectedRows rest
                | ResultSet(_, [ row ]) ->
                    let values = valuesOfResultRow next row
                    let assigned, generated = Diagnostics.capture (fun () -> assignSelectedValues targets values locals)

                    match assigned with
                    | Ok locals ->
                        updateDiagnostics generated (Affected 0UL)
                        generated |> List.iter propagatedConditions.Add
                        run scope next locals results affectedRows rest
                    | Error error ->
                        updateDiagnostics generated error
                        generated |> List.iter propagatedConditions.Add
                        handleQueryResult scope next locals results affectedRows rest error
                | ResultSet _ ->
                    handleQueryResult
                        scope
                        next
                        locals
                        results
                        affectedRows
                        rest
                        (Err(1172, "Result consisted of more than one row"))
                | error -> handleQueryResult scope next locals results affectedRows rest error
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
                        let rows =
                            rows
                            |> List.map (valuesOfResultRow next >> List.toArray)
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
    match StoredProgram.parseRoutine options (isSupportedStoredProgramText options) body with
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

let private raiseFunctionError result =
    match Executor.errorInfo result with
    | Some error -> raise (Diagnostics.EvaluationError(error.Code, error.Message))
    | None -> raise (Diagnostics.EvaluationError(1105, "Stored function execution failed"))

let rec private invokeStoredFunction
    executeText
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
                AccountHost = account.Host
                ActiveRoles =
                    if Auth.sameAccount account (accountOf caller) then caller.ActiveRoles else [] }
        | None -> caller

    let caller = withStoredFunctions executeText caller
    let calls = storedFunctionCalls.Value |> Option.defaultValue []
    let key = routine.Schema.ToLowerInvariant(), routine.Name.ToLowerInvariant()

    if List.contains key calls then
        raise (Diagnostics.EvaluationError(1424, "Recursive stored functions and triggers are not allowed"))

    match checkSessionAccess caller caller.Store [ "EXECUTE", Auth.OnDb routine.Schema ] with
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

    match Executor.currentDirectOnlyRestriction (), firstUnsafeStoredRoutineCall caller.CustomFunctions statements with
    | Some what, Some name ->
        raise (Diagnostics.EvaluationError(3102, sprintf "Expression of %s contains a disallowed function: %s" what name))
    | _ -> ()

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

    let expressionAccess =
        if Auth.sameAccount executionAccount (accountOf caller) then
            checkSessionAccess caller caller.Store expressionPrivileges
        else
            Auth.checkForAccount caller.Store executionAccount expressionPrivileges

    match expressionAccess with
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
        let column = parameterColumn parameter

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
            executeText
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

and private withStoredFunctions executeText current =
    let functions =
        match Storage.scanList current.Store "mysql" "functions" with
        | Ok(_, rows) -> rows |> List.choose SystemCatalog.StoredFunction.tryRead
        | Error _ -> []

    let register name routine registry =
        let invoke arguments =
            if Executor.isMetadataProbe () then
                VNull
            else
                invokeStoredFunction executeText current routine arguments

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

let private withStoredFunctionRegistry executeText session execute =
    let customFunctions = session.CustomFunctions
    let decorated = withStoredFunctions executeText session
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

    withTriggerTextExecution session (fun () ->
        dispatchNormalized session rawSql parserOptions sql)

and private withTriggerTextExecution session body =
    let executeTriggerText (context: Executor.TriggerTextExecution) sql =
        let triggerSession =
            { session with
                User = context.TriggerAccount.Name
                AccountHost = context.TriggerAccount.Host
                ActiveRoles = []
                Database = Some context.TriggerDatabase
                UserVariables = context.TriggerUserVariables.Value
                Store = context.TriggerStore
                Tx = None
                CustomFunctions = context.TriggerRegistry
                RoutineStack = ("TRIGGER", context.TriggerDatabase, "") :: session.RoutineStack }

        let executed, result =
            DynamicScope.withValue storedProgramProtectedTables context.TriggerProtectedTables (fun () ->
                dispatch triggerSession sql)

        context.TriggerUserVariables.Value <- executed.UserVariables
        result

    Executor.withTriggerTextExecutor executeTriggerText body

and private dispatchNormalized session rawSql parserOptions sql =

    // A mysqldump preamble/postamble is a run of `/*!NNNNN ... */;` lines;
    // once the version comment above strips down to nothing (or this was a
    // plain `/* ... */`/`-- ...` comment to begin with), what's left is a
    // no-op, same as real MySQL's `Query OK, 0 rows affected` for it —
    // not a syntax error.
    let runTextPrepared command =
        let statusCommand =
            match command with
            | PrepareText _ -> InformationSchema.StatusCommand.prepareSql
            | ExecuteText _ -> InformationSchema.StatusCommand.executeSql
            | DeallocateText _ -> InformationSchema.StatusCommand.deallocateSql

        InformationSchema.recordCommand session.StatusCounters statusCommand

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
                    withStoredFunctionRegistry dispatch session (fun current ->
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
        let statusCommand =
            match command with
            | CreateProcedure _ -> InformationSchema.StatusCommand.createProcedure
            | CreateFunction _ -> InformationSchema.StatusCommand.createFunction
            | CallProcedure _ -> InformationSchema.StatusCommand.callProcedure
            | DropProcedure _ -> InformationSchema.StatusCommand.dropProcedure
            | DropFunction _ -> InformationSchema.StatusCommand.dropFunction

        InformationSchema.recordCommand session.StatusCounters statusCommand

        let authorize privilege database =
            checkSessionAccess session session.Store [ privilege, Auth.OnDb database ]

        match command with
        | CreateProcedure creation ->
            let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) creation.Name
            let definer = requestedDefinerAccount session creation.Definer
            let mayChooseDefiner = canUseRequestedDefiner session creation.Definer
            let exists = routineEntries () |> List.exists (SystemCatalog.Routine.matches database name)

            match authorize "CREATE ROUTINE" database with
            | Error(code, message) -> session, Err(code, message)
            | Ok() when not mayChooseDefiner ->
                session,
                Err(1227, "Access denied; you need (at least one of) the SUPER or SET_ANY_DEFINER privilege(s) for this operation")
            | Ok() when exists && creation.IfNotExists ->
                Diagnostics.note 1304 (sprintf "PROCEDURE %s already exists" name)
                session, Affected 0UL
            | Ok() when exists -> session, Err(1304, sprintf "PROCEDURE %s already exists" name)
            | Ok() ->
                let options = SqlMode.parserOptionsFor session.Store.ExecutionSettings.SqlModeText

                match parseRoutineDefinition options creation.Parameters creation.Body with
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
                                VString creation.Body
                                VDateTime DateTime.Now
                                VString(Auth.formatAccount definer)
                                VString creation.Parameters
                                VString creation.SecurityType
                                VString session.Store.ExecutionSettings.SqlModeText
                                VString session.Store.ExecutionSettings.ConnectionCharset
                                VString session.Store.ExecutionSettings.ConnectionCollation.Name
                                VString Collation.defaultCollation.Name ] ]
                    with
                    | Ok _ -> session, Affected 0UL
                    | Error error ->
                        let code, message = Storage.toMySqlError error
                        session, Err(code, message)
        | CreateFunction creation ->
            let database, name = splitQualified (session.Database |> Option.defaultValue defaultDatabase) creation.Name
            let definer = requestedDefinerAccount session creation.Definer
            let mayChooseDefiner = canUseRequestedDefiner session creation.Definer
            let exists = functionEntries () |> List.exists (SystemCatalog.StoredFunction.matches database name)

            match Storage.databaseExists (Session.currentStore session) database, authorize "CREATE ROUTINE" database with
            | false, _ -> session, Err(1049, sprintf "Unknown database '%s'" database)
            | true, Error(code, message) -> session, Err(code, message)
            | true, Ok() when not mayChooseDefiner ->
                session,
                Err(1227, "Access denied; you need (at least one of) the SUPER or SET_ANY_DEFINER privilege(s) for this operation")
            | true, Ok() when exists && creation.IfNotExists ->
                Diagnostics.note 1304 (sprintf "FUNCTION %s already exists" name)
                session, Affected 0UL
            | true, Ok() when exists -> session, Err(1304, sprintf "FUNCTION %s already exists" name)
            | true, Ok() ->
                let options = SqlMode.parserOptionsFor session.Store.ExecutionSettings.SqlModeText

                match
                    parseFunctionCharacteristics creation.Characteristics,
                    parseFunctionDefinition options creation.Parameters creation.ReturnType creation.Body
                with
                | Error error, _
                | _, Error error -> session, error
                | _, Ok(_, _, statements) when firstUnsafeStoredRoutineCall session.CustomFunctions statements |> Option.isSome ->
                    let functionName = firstUnsafeStoredRoutineCall session.CustomFunctions statements |> Option.get
                    session, Err(3102, sprintf "Stored function '%s' contains a disallowed function: %s" name functionName)
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
                                VString creation.Body
                                VDateTime DateTime.Now
                                VString(Auth.formatAccount definer)
                                VString creation.Parameters
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
            let activeCalls =
                session.RoutineStack
                |> List.filter (fun (activeType, activeDatabase, activeName) ->
                    activeType = "PROCEDURE"
                    && activeDatabase.Equals(database, StringComparison.OrdinalIgnoreCase)
                    && activeName.Equals(name, StringComparison.OrdinalIgnoreCase))
                |> List.length

            let recursionLimit =
                lookupVar session "max_sp_recursion_depth"
                |> Option.flatten
                |> Option.bind (fun value ->
                    match Int32.TryParse value with
                    | true, depth -> Some depth
                    | false, _ -> None)
                |> Option.defaultValue 0

            match routineEntries () |> List.tryFind (SystemCatalog.Routine.matches database name) with
            | Some _ when activeCalls > recursionLimit ->
                session,
                Err(
                    1456,
                    sprintf
                        "Recursive limit %d (as set by the max_sp_recursion_depth variable) was exceeded for routine %s"
                        recursionLimit
                        name
                )
            | None -> session, Err(1305, sprintf "PROCEDURE %s does not exist" name)
            | Some routine ->
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
                                    ActiveRoles =
                                        if Auth.sameAccount account (accountOf callerSession) then callerSession.ActiveRoles else []
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
                                    let column = parameterColumn parameter

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
            | Ok() when not exists ->
                Diagnostics.note 1305 (sprintf "PROCEDURE %s.%s does not exist" database name)
                session, Affected 0UL
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
            | Ok() when not exists ->
                Diagnostics.note 1305 (sprintf "FUNCTION %s.%s does not exist" database name)
                session, Affected 0UL
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
        let statusCommand =
            match command with
            | Event.Create _ -> InformationSchema.StatusCommand.createEvent
            | Event.Alter _ -> InformationSchema.StatusCommand.alterEvent
            | Event.Drop _ -> InformationSchema.StatusCommand.dropEvent

        InformationSchema.recordCommand session.StatusCounters statusCommand

        let authorize database =
            checkSessionAccess session session.Store [ "EVENT", Auth.OnDb database ]

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
                            let entry: SystemCatalog.Event.Entry =
                                { Schema = database
                                  Name = name
                                  Schedule = creation.Schedule
                                  Definition = creation.Body
                                  Created = Some created
                                  Definer = Auth.formatAccount definer
                                  Status = status
                                  OnCompletion = creation.OnCompletion
                                  Comment = creation.Comment
                                  LastAltered = Some created
                                  LastExecuted = None
                                  SqlMode = sqlMode
                                  TimeZone = timeZone
                                  CharacterSetClient = characterSetClient
                                  CollationConnection = collationConnection
                                  DatabaseCollation = databaseCollation
                                  Originator = 1L
                                  ExecuteAt = executeAt
                                  IntervalValue = intervalValue
                                  IntervalField = intervalField
                                  Starts = starts
                                  Ends = ends }

                            match
                                Storage.insertRows
                                    next.Store
                                    "mysql"
                                    "events"
                                    None
                                    [ entry |> SystemCatalog.Event.toRow |> Array.toList ]
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
                            row
                            |> SystemCatalog.Event.mapRow (fun current ->
                                let updated =
                                    { current with
                                        Schema = renamedDatabase
                                        Name = renamedName
                                        Schedule = alteration.Schedule |> Option.defaultValue current.Schedule
                                        Definition = alteration.Body |> Option.defaultValue current.Definition
                                        Definer = Auth.formatAccount definer
                                        Status = status |> Option.defaultValue current.Status
                                        OnCompletion = alteration.OnCompletion |> Option.defaultValue current.OnCompletion
                                        Comment = alteration.Comment |> Option.defaultValue current.Comment
                                        LastAltered = Some DateTime.Now
                                        SqlMode = sqlMode
                                        TimeZone = timeZone
                                        CharacterSetClient = characterSetClient
                                        CollationConnection = collationConnection
                                        DatabaseCollation = databaseCollation }

                                match timing with
                                | None -> updated
                                | Some value ->
                                    let executeAt, intervalValue, intervalField, starts, ends = Event.timingFields value

                                    { updated with
                                        LastExecuted = None
                                        ExecuteAt = executeAt
                                        IntervalValue = intervalValue
                                        IntervalField = intervalField
                                        Starts = starts
                                        Ends = ends })
                            |> Ok

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
            | Ok() when not exists ->
                Diagnostics.note 1305 (sprintf "Event %s does not exist" name)
                session, Affected 0UL
            | Ok() ->
                match Storage.deleteRows session.Store "mysql" "events" (SystemCatalog.Event.rowMatches database name >> Ok) with
                | Ok _ -> session, Affected 0UL
                | Error error ->
                    let code, message = Storage.toMySqlError error
                    session, Err(code, message)

    let isHandler = hasKeywordPrefix "HANDLER" sql
    let isXa = hasKeywordPrefix "XA" sql

    if Parser.isBlank sql then
        InformationSchema.recordCommand session.StatusCounters InformationSchema.StatusCommand.emptyQuery
        session, Affected 0UL
    elif isXa then
        runXa parserOptions session sql
    elif xaAssociation session |> Option.exists (fun (_, state) -> state = Idle) then
        session, xaRmFail "IDLE"
    elif isHandler then
        match Parser.parseHandlerWithOptions parserOptions sql with
        | Ok command ->
            let statusCommand =
                match command with
                | HandlerOpen _ -> InformationSchema.StatusCommand.handlerOpen
                | HandlerRead _ -> InformationSchema.StatusCommand.handlerRead
                | HandlerClose _ -> InformationSchema.StatusCommand.handlerClose

            InformationSchema.recordCommand session.StatusCounters statusCommand

            withStoredFunctionRegistry dispatch session (fun current ->
                TableHandler.run (registryFor current) (lockWaitTimeout current) current command)
        | Error detail -> session, parserError sql detail
    else
        match StoredProgram.parseDiagnostics parserOptions sql with
        | Error _ -> session, syntaxError sql
        | Ok(Some diagnostics) ->
            InformationSchema.recordCommand session.StatusCounters InformationSchema.StatusCommand.getDiagnostics
            runTextDiagnostics session diagnostics
        | Ok None ->
            match Event.tryCommand parserOptions (validEventBody parserOptions) sql with
            | Some _ when xaAssociation session |> Option.isSome -> session, xaRmFail "ACTIVE"
            | Some command -> runTextEvent (commitSession session) command
            | None ->
                match tryTextRoutineCommand sql with
                | Some(CallProcedure _ as command) ->
                    withStoredFunctionRegistry dispatch session (fun current -> runTextRoutine current command)
                | Some _ when xaAssociation session |> Option.isSome -> session, xaRmFail "ACTIVE"
                | Some command -> runTextRoutine (commitSession session) command
                | None ->
                    match tryTextPreparedCommand sql with
                    | Error result -> session, result
                    | Ok(Some _) when insideFunctionOrTrigger session ->
                        session, Err(1336, "Dynamic SQL is not allowed in stored function or trigger")
                    | Ok(Some command) -> runTextPrepared command
                    | Ok None when not (placeholderPositionsWithOptions parserOptions sql |> List.isEmpty) ->
                        // A `?` outside a string/comment is a bind parameter, only
                        // legal via COM_STMT_PREPARE. Rejecting it here also keeps
                        // unreachable placeholders out of persisted expressions.
                        session, syntaxError sql
                    | Ok None ->
                        match tryProbe parserOptions sql with
                        | Some probe when insideFunctionOrTrigger session && probeForbiddenInFunctionOrTrigger probe ->
                            session, Err(1422, "Explicit or implicit commit is not allowed in stored function or trigger.")
                        | Some probe when probeCausesImplicitCommit probe && xaAssociation session |> Option.isSome ->
                            session, xaRmFail "ACTIVE"
                        | Some probe ->
                            // Probe results contain rendered strings rather than
                            // values from which descriptors can be inferred.
                            let session, result = runProbe (beginProbeExecution session probe) sql probe
                            { session with LastResultColumnMetadata = completeResultMetadata session result [] }, result
                        | None -> withStoredFunctionRegistry dispatch session (fun current -> executeStatement current sql rawSql)

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
    let session = if containsResultSet result then Session.trackTransactionResultSet session else session
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
    let hadTransaction = session.Tx.IsSome
    session.Tx
    |> Option.bind _.Xa
    |> Option.iter (fst >> removeXaAssociation session)

    session.Tx |> Option.iter (fun transaction -> Storage.releaseTransactionLocks transaction.Snapshot)
    removeTransactionView session
    let session = { session with Tx = None }
    if hadTransaction then Session.endTransactionTracking session else session

let private recoverExecutionError (session: Session) (description: string) (error: exn) : Session * QueryResult =
    match error with
    | Storage.DeadlockVictim dbName ->
        Log.diagnostic "fsdb: ERR 1213 deadlock on database %s -- %s" dbName description
        abortTransaction session, Err(1213, "Deadlock found when trying to get lock; try restarting transaction")
    | Storage.LockNowait dbName ->
        Log.diagnostic "fsdb: ERR 3572 lock unavailable on database %s -- %s" dbName description
        session, Err(3572, "Statement aborted because lock(s) could not be acquired immediately and NOWAIT is set.")
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
    match StoredProgram.parseDiagnostics parserOptions sql with
    | Ok(Some _) -> true
    | _ when
        showWarningsRe.IsMatch sql
        || showErrorsRe.IsMatch sql
        || showCountWarningsRe.IsMatch sql
        || showCountErrorsRe.IsMatch sql
        -> true
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
    let previous = session
    let session = if preserve then session else { session with Diagnostics = [] }
    let (session, result), generated = Diagnostics.capture execute

    let generated =
        match terminalErrorInfo result with
        | Some error -> generated @ [ Diagnostics.fromError error ]
        | None -> generated

    let session = if preserve then session else { session with Diagnostics = generated }
    let session, result = recordResult (session, result)
    Session.finalizeTransactionTracking previous session, result

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
    | LoadData _
    | Update _
    | Delete _
    | Truncate _
    | CreateUser _
    | DropUser _
    | RenameUser _
    | AlterUser _
    | CreateServer _
    | AlterServer _
    | DropServer _
    | CreateRole _
    | DropRole _
    | Grant _
    | Revoke _
    | GrantProxy _
    | RevokeProxy _
    | GrantRoles _
    | RevokeRoles _
    | SetDefaultRole _
    | CreateTrigger _
    | SetTriggerNew _
    | DropTrigger _
    | CreateView _
    | DropView _ -> true
    | Select _
    | SetRole _
    | Do _
    | Union _
    | ChecksumTables _
    | Explain _ -> false

type private AccountStatement =
    | ProbedAccountStatement of Probe
    | ParsedAccountStatement of Statement
    | TextAccountUpdate of authorized: bool
    | UnknownAccountStatement

let private routineCreationIsAuthorized session qualifiedName requestedDefiner =
    let database, _ = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName

    checkSessionAccess session session.Store [ "CREATE ROUTINE", Auth.OnDb database ]
    |> Result.map (fun () -> canUseRequestedDefiner session requestedDefiner)
    |> Result.defaultValue false

let private textRoutineUpdateAuthorization session = function
    | CallProcedure _ -> None
    | CreateProcedure creation -> routineCreationIsAuthorized session creation.Name creation.Definer |> Some
    | CreateFunction creation -> routineCreationIsAuthorized session creation.Name creation.Definer |> Some
    | DropProcedure(qualifiedName, _)
    | DropFunction(qualifiedName, _) ->
        let database, _ = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName

        checkSessionAccess session session.Store [ "ALTER ROUTINE", Auth.OnDb database ]
        |> Result.isOk
        |> Some

let private textEventUpdateIsAuthorized session = function
    | Event.Create creation ->
        let database, _ = splitQualified (session.Database |> Option.defaultValue defaultDatabase) creation.Name

        checkSessionAccess session session.Store [ "EVENT", Auth.OnDb database ]
        |> Result.map (fun () -> canUseRequestedDefiner session creation.Definer)
        |> Result.defaultValue false
    | Event.Alter alteration ->
        let database, _ = splitQualified (session.Database |> Option.defaultValue defaultDatabase) alteration.Name

        checkSessionAccess session session.Store [ "EVENT", Auth.OnDb database ]
        |> Result.map (fun () -> canUseRequestedDefiner session alteration.Definer)
        |> Result.defaultValue false
    | Event.Drop(qualifiedName, _) ->
        let database, _ = splitQualified (session.Database |> Option.defaultValue defaultDatabase) qualifiedName
        checkSessionAccess session session.Store [ "EVENT", Auth.OnDb database ] |> Result.isOk

let rec private parsedAccountStatement session parserOptions depth sql =
    match tryProbe parserOptions sql with
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
    | ParsedAccountStatement(AlterUser(name, host, Some _, _, options)) when options = AccountOptions.empty ->
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
        checkSessionAccess session (Session.currentStore session) required |> Result.isOk
    | ParsedAccountStatement statement ->
        let store = Session.currentStore session
        let database = session.Database |> Option.defaultValue defaultDatabase
        checkSessionAccess session store (requiredPrivilegesForStatement session store database statement)
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

    let executed, result =
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
                        let executed, result =
                            withTriggerTextExecution session (fun () ->
                                dispatchNormalized session rawSql parserOptions sql)
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

    syncTransactionView executed, result

let executeEventBody (session: Session) (body: string) : Session * QueryResult =
    let options = parserOptionsForSession session

    match StoredProgram.parseRoutine options (isSupportedStoredProgramText options) body with
    | Error _ -> session, syntaxError body
    | Ok statements ->
        withTriggerTextExecution session (fun () ->
            withStoredFunctionRegistry dispatch session (fun current ->
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

                rollbackSession outcome.Session, result))

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
            match Parser.parseLocalLoadWithOptions (parserOptionsForSession session) sql with
            | Result.Error _ -> Result.Error(syntaxError sql)
            | Result.Ok load ->
                match load.Charset |> Option.map _.ToLowerInvariant() with
                | Some value when not (Charset.supportsLoadData value) ->
                    Result.Error(Err(1235, sprintf "LOAD DATA CHARACTER SET %s is not supported" value))
                | _ ->
                    let inputVariables =
                        load.Fields
                        |> List.choose (function
                            | LoadUserVariable variable -> Some variable
                            | LoadColumn _ -> None)

                    match inputVariables |> List.tryPick UserVariableRef.validationError with
                    | Some message -> Result.Error(Err(3061, message))
                    | None ->
                        let newVariables =
                            inputVariables
                            |> List.map _.Name
                            |> Set.ofList
                            |> Set.filter (fun name -> not (session.UserVariables.ContainsKey name))

                        if session.UserVariables.Count + newVariables.Count > maxUserVariables then
                            Result.Error(Err(1105, "Too many user-defined variables"))
                        else
                            let statement =
                                LoadData
                                    { Table = load.Table
                                      Fields = load.Fields
                                      Rows = []
                                      Assignments = load.Assignments
                                      Replace = load.Replace
                                      Ignore = load.Ignore }

                            let database = session.Database |> Option.defaultValue defaultDatabase

                            match checkSessionAccess session store (Auth.requiredPrivilegesInStore store database statement) with
                            | Ok() -> Result.Ok(Some load)
                            | Error(code, message) -> Result.Error(Err(code, message))

        let isUpdate =
            match prepared with
            | Result.Ok(Some _) -> true
            | _ -> false

        match Auth.tryConsumeAccountStatementWithLimits store account (Auth.tryAccountLimits store account) isUpdate with
        | Result.Error(code, message) -> Result.Error(Err(code, message))
        | Result.Ok() -> prepared

/// Keeps the parsed field and SET mappings until the client upload has been
/// decoded; an ordinary INSERT AST cannot represent either mapping.
let executeLocalLoad (session: Session) (load: Parser.LocalLoad) (rows: Value list list) : Session * QueryResult =
    let session = Session.clearSessionStateChanges session
    let statement =
        LoadData
            { Table = load.Table
              Fields = load.Fields
              Rows = rows
              Assignments = load.Assignments
              Replace = load.Replace
              Ignore = load.Ignore }

    let executed, result =
        recordDiagnostics session false (fun () ->
            try
                withTriggerTextExecution session (fun () ->
                    withStoredFunctionRegistry dispatch session (fun current -> executeParsed current statement))
            with
            | :? OperationCanceledException -> reraise ()
            | ex -> recoverExecutionError session "LOAD DATA LOCAL INFILE" ex)

    syncTransactionView executed, result

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
        let executed, result =
            recordDiagnostics session false (fun () ->
                try
                    let statement = bindPlaceholders ast values
                    let resetsPassword = resetsOwnPassword session (ParsedAccountStatement statement)

                    if session.PasswordExpired && not resetsPassword then
                        session, Err(1820, "You must reset your password using ALTER USER statement before executing this statement.")
                    elif isRoleSessionStatement statement then
                        applyRoleStatement session statement
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
                                withTriggerTextExecution session (fun () ->
                                    withStoredFunctionRegistry dispatch session (fun current -> executeParsed current statement))

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

        syncTransactionView executed, result
