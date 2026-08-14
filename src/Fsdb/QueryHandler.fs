/// Query dispatcher: a handful of connection-setup forms mysql CLI/PDO send
/// (`@@vars`, `SET`, `SHOW`, `USE`, literal `SELECT <n>`) are still matched
/// on trimmed/uppercased query text, since they're session-variable probes
/// rather than real SQL the grammar needs to know about. Everything else
/// goes through `Parser.parse -> Executor.execute`.
module Fsdb.QueryHandler

open System
open System.Text.RegularExpressions
open Fsdb.Value
open Fsdb.Session
open Fsdb.Storage

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

let private literalSelect = Regex(@"^SELECT\s+(-?\d+)$", RegexOptions.IgnoreCase)

/// `SELECT 1`, `SELECT 42`.
let private handleLiteralSelect (sql: string) : QueryResult =
    let m = literalSelect.Match sql

    if m.Success then
        let v = m.Groups.[1].Value
        ResultSet([ v ], [ [ Some v ] ])
    else
        syntaxError sql

let private likeToRegex (pattern: string) =
    "^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$"

/// `SHOW VARIABLES` / `SHOW VARIABLES LIKE 'pattern'`.
let private handleShowVariables (session: Session) (sql: string) : QueryResult =
    let likeMatch = Regex.Match(sql, @"LIKE\s+'([^']*)'", RegexOptions.IgnoreCase)

    let matches (name: string) =
        if likeMatch.Success then
            Regex.IsMatch(name, likeToRegex likeMatch.Groups.[1].Value, RegexOptions.IgnoreCase)
        else
            true

    let rows =
        session.Variables
        |> Map.toList
        |> List.filter (fst >> matches)
        |> List.sortBy fst
        |> List.map (fun (k, v) -> [ Some k; Some v ])

    ResultSet([ "Variable_name"; "Value" ], rows)

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
        let lastInsertId, result = Executor.execute session.Store (registryFor session) dbName session.LastInsertId stmt
        { session with LastInsertId = lastInsertId }, result
    | Result.Error _ when upper.StartsWith "SELECT" && upper.Contains "@@" -> session, handleAtVarSelect session sql
    | Result.Error _ -> session, syntaxError sql

let private dispatch (session: Session) (rawSql: string) : Session * QueryResult =
    let sql = rawSql.Trim().TrimEnd(';').Trim()
    let upper = sql.ToUpperInvariant()

    match upper with
    | _ when upper.StartsWith "SET " -> handleSet session sql
    | "SELECT DATABASE()" -> session, ResultSet([ "DATABASE()" ], [ [ session.Database ] ])
    | "SHOW DATABASES" -> session, ResultSet([ "Database" ], [ [ Some "information_schema" ] ])
    | _ when upper.StartsWith "USE " ->
        { session with Database = Some(sql.Substring(4).Trim().Trim('`')) }, Affected 0UL
    | _ when upper.StartsWith "SHOW VARIABLES" -> session, handleShowVariables session sql
    | _ when literalSelect.IsMatch sql -> session, handleLiteralSelect sql
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
        dispatch session rawSql
    with ex ->
        session, Err(1105, sprintf "Internal error: %s" ex.Message) // ER_UNKNOWN_ERROR
