/// Pragmatic query dispatcher for M1/M2: there is no SQL parser yet, so we
/// pattern-match on trimmed/uppercased query text for the handful of
/// statements mysql CLI and PDO send on connect. This whole module shrinks
/// to almost nothing once the real FParsec-based engine lands in M3.
module Fsdb.QueryHandler

open System
open System.Text.RegularExpressions
open Fsdb.Session

type QueryResult =
    | ResultSet of columns: string list * rows: (string option list) list
    | Affected of affectedRows: uint64
    | Err of code: int * message: string

let private syntaxError (sql: string) =
    Err(
        1064,
        sprintf
            "You have an error in your SQL syntax; check the manual that corresponds to your fsdb version for the right syntax to use near '%s'"
            sql
    )

let private lookupVar (session: Session) (name: string) : string =
    session.Variables |> Map.tryFind (name.ToLowerInvariant()) |> Option.defaultValue ""

/// Matches `@@var` or `@@session.var` / `@@global.var`, optionally aliased,
/// optionally followed by a trailing `LIMIT n` (mysql CLI probes
/// `@@version_comment` this way at connect time).
let private atVarItem =
    Regex(@"^@@(?:SESSION\.|GLOBAL\.)?(\w+)(?:\s+AS\s+(\S+))?(?:\s+LIMIT\s+\d+)?$", RegexOptions.IgnoreCase)

/// `SELECT @@version`, `SELECT @@version AS v, @@sql_mode` etc.
let private handleAtVarSelect (session: Session) (sql: string) : QueryResult =
    let exprs = sql.Substring("SELECT".Length).Trim()
    let items = exprs.Split(',') |> Array.map (fun s -> s.Trim())
    let parsed = items |> Array.map atVarItem.Match

    if parsed |> Array.forall (fun m -> m.Success) then
        let cols =
            parsed
            |> Array.map (fun m -> if m.Groups.[2].Success then m.Groups.[2].Value else "@@" + m.Groups.[1].Value)
            |> Array.toList

        let vals =
            parsed
            |> Array.map (fun m -> Some(lookupVar session m.Groups.[1].Value))
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

let handle (session: Session) (rawSql: string) : Session * QueryResult =
    let sql = rawSql.Trim().TrimEnd(';').Trim()
    let upper = sql.ToUpperInvariant()

    match upper with
    | _ when upper.StartsWith "SET " -> handleSet session sql
    | "SELECT DATABASE()" -> session, ResultSet([ "DATABASE()" ], [ [ session.Database ] ])
    | "SHOW DATABASES" -> session, ResultSet([ "Database" ], [ [ Some "information_schema" ] ])
    | _ when upper.StartsWith "USE " ->
        { session with Database = Some(sql.Substring(4).Trim().Trim('`')) }, Affected 0UL
    | _ when upper.StartsWith "SHOW VARIABLES" -> session, handleShowVariables session sql
    | _ when upper.StartsWith "SELECT" && upper.Contains "@@" -> session, handleAtVarSelect session sql
    | _ when upper.StartsWith "SELECT" -> session, handleLiteralSelect sql
    | _ -> session, syntaxError sql
