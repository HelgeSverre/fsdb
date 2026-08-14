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

let handle (session: Session) (rawSql: string) : QueryResult =
    let sql = rawSql.Trim().TrimEnd(';').Trim()
    let upper = sql.ToUpperInvariant()

    match upper with
    | _ when upper.StartsWith "SET " -> Affected 0UL
    | "SELECT DATABASE()" -> ResultSet([ "DATABASE()" ], [ [ session.Database ] ])
    | "SHOW DATABASES" -> ResultSet([ "Database" ], [ [ Some "information_schema" ] ])
    | _ when upper.StartsWith "USE " ->
        session.Database <- Some(sql.Substring(4).Trim().Trim('`'))
        Affected 0UL
    | _ when upper.StartsWith "SHOW VARIABLES" -> handleShowVariables session sql
    | _ when upper.StartsWith "SELECT" && upper.Contains "@@" -> handleAtVarSelect session sql
    | _ when upper.StartsWith "SELECT" -> handleLiteralSelect sql
    | _ -> syntaxError sql
