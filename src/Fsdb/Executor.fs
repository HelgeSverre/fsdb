/// Turns an `Ast.Statement` into effects against `Storage` and rows out.
/// The SELECT pipeline is volcano-style over plain `list`s (see the
/// `ponytail` note on `materialize` below for why not a lazier `seq`):
/// scan -> filter -> order -> limit/offset -> project.
module Fsdb.Executor

open System.Text.RegularExpressions
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions

/// Mirrors the wire layer's text-resultset shape (columns as names, rows as
/// text-protocol option strings) so `QueryHandler` can hand a parsed
/// statement straight through without a translation layer of its own.
type QueryResult =
    | ResultSet of columns: string list * rows: (string option list) list
    | Affected of affectedRows: uint64
    | Err of code: int * message: string

/// An expression-evaluation failure: a MySQL error code and message, the
/// same shape `Storage.toMySqlError` produces, so both error sources funnel
/// into `Err` the same way.
type private EvalError = int * string

let private unknownColumn (name: string) : EvalError =
    1054, sprintf "Unknown column '%s' in 'field list'" name

let private unknownFunction (name: string) : EvalError =
    1305, sprintf "FUNCTION %s does not exist" name

let private storageErr (e: StorageError) : QueryResult =
    let code, message = toMySqlError e
    Err(code, message)

/// Applies `f` to each element, short-circuiting on the first `Error` —
/// same shape as `Storage.traverseResult`, generalized over any error type
/// since this module threads both `StorageError` and `EvalError`.
let rec private traverseList (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
    match xs with
    | [] -> Ok []
    | x :: rest ->
        match f x with
        | Error e -> Error e
        | Ok y -> traverseList f rest |> Result.map (fun ys -> y :: ys)

/// Column name (case-insensitive) to its index in a row array.
let private columnIndexOf (columns: ColumnDef list) : Map<string, int> =
    columns |> List.mapi (fun i c -> c.Name.ToLowerInvariant(), i) |> Map.ofList

let private opSymbol =
    function
    | And -> "AND"
    | Or -> "OR"
    | Eq -> "="
    | Neq -> "<>"
    | Lt -> "<"
    | Lte -> "<="
    | Gt -> ">"
    | Gte -> ">="
    | Add -> "+"
    | Sub -> "-"
    | Mul -> "*"
    | Div -> "/"

/// The column name MySQL gives an unaliased projection — exact for columns
/// and literals, a best-effort reconstruction of the source text for
/// everything else (real MySQL echoes the original expression text, which
/// the parser doesn't preserve).
let rec private exprLabel (expr: Expr) : string =
    match expr with
    | Lit v -> v |> toText |> Option.defaultValue "NULL"
    | Col name -> name
    | QualifiedCol(_, col) -> col
    | FuncCall(name, args) -> sprintf "%s(%s)" (name.ToUpperInvariant()) (args |> List.map exprLabel |> String.concat ", ")
    | BinOp(op, a, b) -> sprintf "%s %s %s" (exprLabel a) (opSymbol op) (exprLabel b)
    | Not e -> sprintf "not(%s)" (exprLabel e)
    | IsNull e -> sprintf "(%s is null)" (exprLabel e)
    | IsNotNull e -> sprintf "(%s is not null)" (exprLabel e)
    | Like(e, p) -> sprintf "(%s like %s)" (exprLabel e) (exprLabel p)
    | In(e, xs) -> sprintf "(%s in (%s))" (exprLabel e) (xs |> List.map exprLabel |> String.concat ",")
    | Between(e, lo, hi) -> sprintf "(%s between %s and %s)" (exprLabel e) (exprLabel lo) (exprLabel hi)
    | Star -> "*"

/// Translates a SQL LIKE pattern to a .NET regex source: `%` -> `.*`, `_` ->
/// `.`. Anchored with `\A`/`\z` rather than `^`/`$` — `$` alone matches
/// before a trailing newline, which would let `'ab\n' LIKE 'ab'` falsely
/// match — and callers must pass `RegexOptions.Singleline` so `.` spans
/// newlines too (`%`/`_` are unqualified wildcards in MySQL, not
/// "everything but a newline"). Not private: `QueryHandler`'s `SHOW
/// VARIABLES LIKE` reuses the same LIKE semantics rather than keeping a
/// second copy.
let likeToRegex (pattern: string) =
    @"\A" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + @"\z"

/// Evaluates one expression against one row. Three-valued logic throughout
/// (comparisons/AND/OR/NOT return `VNull` — SQL's "unknown" — rather than a
/// boolean whenever an operand is `VNull`, per `Value`'s helpers), function
/// calls resolve through `registry` (error 1305 if unregistered), and a
/// bare column resolves through `columnIndex` (error 1054 if unknown).
let rec private evalExpr (registry: Registry) (columnIndex: Map<string, int>) (row: Value[]) (expr: Expr) : Result<Value, EvalError> =
    let eval = evalExpr registry columnIndex row

    match expr with
    | Lit v -> Ok v
    | Star -> Error(1054, "Invalid use of '*'")
    | Col name ->
        match Map.tryFind (name.ToLowerInvariant()) columnIndex with
        | Some i -> Ok row.[i]
        | None -> Error(unknownColumn name)
    | QualifiedCol(_, col) -> eval (Col col)
    | Not e -> eval e |> Result.map (fun v -> truthy v |> Option.map (not >> boolToValue) |> Option.defaultValue VNull)
    | IsNull e -> eval e |> Result.map (function VNull -> VInt 1L | _ -> VInt 0L)
    | IsNotNull e -> eval e |> Result.map (function VNull -> VInt 0L | _ -> VInt 1L)
    | BinOp(And, a, b) ->
        eval a
        |> Result.bind (fun va ->
            eval b
            |> Result.map (fun vb ->
                match truthy va, truthy vb with
                | Some false, _
                | _, Some false -> VInt 0L
                | Some true, Some true -> VInt 1L
                | _ -> VNull))
    | BinOp(Or, a, b) ->
        eval a
        |> Result.bind (fun va ->
            eval b
            |> Result.map (fun vb ->
                match truthy va, truthy vb with
                | Some true, _
                | _, Some true -> VInt 1L
                | Some false, Some false -> VInt 0L
                | _ -> VNull))
    | BinOp((Eq | Neq | Lt | Lte | Gt | Gte) as op, a, b) ->
        eval a
        |> Result.bind (fun va -> eval b |> Result.map (fun vb -> compareOp op va vb))
    | BinOp((Add | Sub | Mul | Div) as op, a, b) ->
        eval a |> Result.bind (fun va -> eval b |> Result.map (fun vb -> arithOp op va vb))
    | Like(e, p) ->
        eval e
        |> Result.bind (fun ve -> eval p |> Result.map (fun vp -> likeOp ve vp))
    | In(e, xs) ->
        eval e
        |> Result.bind (fun ve ->
            match ve with
            | VNull -> Ok VNull
            | _ ->
                xs
                |> traverseList eval
                |> Result.map (fun vs ->
                    if vs |> List.exists (fun v -> Value.equals ve v = Some true) then
                        VInt 1L
                    elif vs |> List.exists (function VNull -> true | _ -> false) then
                        VNull
                    else
                        VInt 0L))
    | Between(e, lo, hi) ->
        eval e
        |> Result.bind (fun ve ->
            eval lo
            |> Result.bind (fun vlo ->
                eval hi
                |> Result.map (fun vhi ->
                    match ve, vlo, vhi with
                    | VNull, _, _
                    | _, VNull, _
                    | _, _, VNull -> VNull
                    | _ -> boolToValue (Value.compare ve vlo >= 0 && Value.compare ve vhi <= 0))))
    | FuncCall(name, args) ->
        match Functions.lookup name registry with
        | None -> Error(unknownFunction name)
        | Some fn -> args |> traverseList eval |> Result.map fn

and private boolToValue (b: bool) : Value = VInt(if b then 1L else 0L)

and private compareOp (op: Op) (a: Value) (b: Value) : Value =
    match a, b with
    | VNull, _
    | _, VNull -> VNull
    | _ ->
        let c = Value.compare a b

        boolToValue (
            match op with
            | Eq -> c = 0
            | Neq -> c <> 0
            | Lt -> c < 0
            | Lte -> c <= 0
            | Gt -> c > 0
            | Gte -> c >= 0
            | _ -> failwith "compareOp: not a comparison operator"
        )

and private arithOp (op: Op) (a: Value) (b: Value) : Value =
    match op with
    | Add -> Value.add a b
    | Sub -> Value.sub a b
    | Mul -> Value.mul a b
    | Div -> Value.div a b
    | _ -> failwith "arithOp: not an arithmetic operator"

and private likeOp (subject: Value) (pattern: Value) : Value =
    match subject, pattern with
    | VNull, _
    | _, VNull -> VNull
    | _ ->
        let text = subject |> toText |> Option.defaultValue ""
        let pat = pattern |> toText |> Option.defaultValue ""
        boolToValue (Regex.IsMatch(text, likeToRegex pat, RegexOptions.IgnoreCase ||| RegexOptions.Singleline))

let private applyLimitOffset (limit: int option) (offset: int option) (rows: 'a list) : 'a list =
    let afterOffset =
        match offset with
        | Some o -> rows |> List.skip (min o (List.length rows))
        | None -> rows

    match limit with
    | Some l -> afterOffset |> List.truncate (max 0 l)
    | None -> afterOffset

/// One projection's `(column name, value)` pairs — a list because `SELECT
/// *` expands to every column of the row.
let private evalProjection
    (registry: Registry)
    (columnIndex: Map<string, int>)
    (columns: ColumnDef list)
    (row: Value[])
    (proj: Projection)
    : Result<(string * Value) list, EvalError> =
    match proj with
    | Star, _ -> Ok(columns |> List.mapi (fun i c -> c.Name, row.[i]))
    | expr, aliasOpt ->
        evalExpr registry columnIndex row expr
        |> Result.map (fun v -> [ aliasOpt |> Option.defaultValue (exprLabel expr), v ])

/// An all-`VNull` row shaped like `columns` — used only to type-check a
/// statement's expressions (unknown column/function) independent of the
/// actual data, since those errors are about the schema, not row values.
/// Without this, a table with zero matching (or zero total) rows would
/// silently skip evaluating its WHERE/ORDER BY/projection at all and never
/// surface a real error.
let private probeRow (columns: ColumnDef list) : Value[] = Array.create (List.length columns) VNull

let private runSelect (registry: Registry) (columns: ColumnDef list) (rows: Value[] list) (select: SelectStmt) : QueryResult =
    let projections, whereExpr, orderBy, limit, offset =
        select.Projections, select.Where, select.OrderBy, select.Limit, select.Offset

    // A `SELECT` with no `FROM` at all has no columns to expand `*`/`t.*`
    // against — real MySQL rejects it as 1096 rather than emitting a
    // resultset with zero columns, which isn't a legal text-resultset
    // packet and aborts the client's whole session.
    if select.From.IsNone && projections |> List.exists (fst >> (=) Star) then
        Err(1096, "No tables used")
    else

    let columnIndex = columnIndexOf columns

    // ORDER BY may name a `SELECT ... AS alias` rather than a table column
    // (`SELECT COUNT(*) AS n FROM t ORDER BY n`) — resolve those first
    // against the projection list before falling back to `evalExpr`'s
    // normal column lookup.
    let aliasExprs =
        projections
        |> List.choose (function
            | expr, Some alias -> Some(alias.ToLowerInvariant(), expr)
            | _ -> None)
        |> Map.ofList

    let resolveOrderExpr (expr: Expr) : Expr =
        match expr with
        | Col name -> aliasExprs |> Map.tryFind (name.ToLowerInvariant()) |> Option.defaultValue expr
        | _ -> expr

    let matches (row: Value[]) : Result<bool, EvalError> =
        match whereExpr with
        | None -> Ok true
        | Some expr -> evalExpr registry columnIndex row expr |> Result.map (fun v -> truthy v = Some true)

    let orderKeys (row: Value[]) : Result<Value list, EvalError> =
        orderBy |> traverseList (fun (expr, _) -> evalExpr registry columnIndex row (resolveOrderExpr expr))

    let projectRow (row: Value[]) : Result<(string * Value) list, EvalError> =
        projections
        |> traverseList (evalProjection registry columnIndex columns row)
        |> Result.map List.concat

    // Sorts rows by their pre-evaluated `ORDER BY` keys: a total order per
    // key via `Value.compare` (NULLs first), folded left-to-right so the
    // first key that differs between two rows decides, later keys only
    // breaking ties.
    let sortRows (keyed: (Value list * Value[]) list) : (Value list * Value[]) list =
        keyed
        |> List.sortWith (fun (ka, _) (kb, _) ->
            List.zip3 (List.map snd orderBy) ka kb
            |> List.fold
                (fun acc (dir, va, vb) ->
                    if acc <> 0 then
                        acc
                    else
                        let c = Value.compare va vb
                        match dir with
                        | Asc -> c
                        | Desc -> -c)
                0)

    let probe = probeRow columns

    match matches probe |> Result.bind (fun _ -> orderKeys probe) |> Result.bind (fun _ -> projectRow probe) with
    | Error(code, message) -> Err(code, message)
    | Ok probeProjection ->
        let colNames = probeProjection |> List.map fst

        let keyed =
            rows
            |> List.filter (matches >> Result.defaultValue false)
            |> List.map (fun row -> (orderKeys row |> Result.defaultValue []), row)

        let textRows =
            keyed
            |> sortRows
            |> List.map snd
            |> applyLimitOffset limit offset
            |> List.map (fun row -> projectRow row |> Result.defaultValue [] |> List.map (snd >> toText))

        ResultSet(colNames, textRows)

/// Assigns `assignments` (already resolved to column indices) to a copy of
/// `row`, evaluating each right-hand side against the row's original
/// (pre-assignment) values.
let private applyAssignments
    (registry: Registry)
    (columnIndex: Map<string, int>)
    (assignments: (int * Expr) list)
    (row: Value[])
    : Value[] =
    let newRow = Array.copy row

    for idx, expr in assignments do
        newRow.[idx] <- (evalExpr registry columnIndex row expr |> Result.defaultValue VNull)

    newRow

/// Executes one parsed statement against `store`, threading the session's
/// AUTO_INCREMENT bookkeeping through as a plain value rather than a
/// `Session` (this module knows nothing about sessions or connections —
/// `QueryHandler` is the layer that owns that). Returns the (possibly
/// updated) `lastInsertId` alongside the result.
let execute (store: Store) (registry: Registry) (dbName: string) (lastInsertId: int64) (stmt: Statement) : int64 * QueryResult =
    match stmt with
    | CreateTable(name, columns, ifNotExists) ->
        match createTable store dbName name columns with
        | Ok() -> lastInsertId, Affected 0UL
        | Error(TableExists _) when ifNotExists -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | DropTable(names, ifExists) ->
        let dropOne name =
            match dropTable store dbName name with
            | Ok() -> Ok()
            | Error(NoSuchTable _) when ifExists -> Ok()
            | Error e -> Error e

        match names |> traverseList dropOne with
        | Ok _ -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | Truncate table ->
        match truncate store dbName table with
        | Ok() -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | Insert(table, columns, rowsExprs) ->
        // INSERT ... VALUES expressions aren't evaluated against any row
        // (no table columns are in scope), just literals/functions — an
        // empty column index turns a stray `Col` reference into a clean
        // 1054 rather than an index-out-of-range.
        match rowsExprs |> traverseList (traverseList (evalExpr registry Map.empty [||])) with
        | Error(code, message) -> lastInsertId, Err(code, message)
        | Ok rowsValues ->
            let cols = if columns.IsEmpty then None else Some columns

            match insertRows store dbName table cols rowsValues with
            | Ok(newLastId, affected) ->
                (if newLastId <> 0L then newLastId else lastInsertId), Affected(uint64 affected)
            | Error e -> lastInsertId, storageErr e

    | Select select ->
        match select.From with
        | None -> lastInsertId, runSelect registry [] [ [||] ] select
        | Some tableRef ->
            let tableDb = tableRef.Database |> Option.defaultValue dbName

            match scan store tableDb tableRef.Table with
            | Error e -> lastInsertId, storageErr e
            | Ok(columns, rows) -> lastInsertId, runSelect registry columns (List.ofSeq rows) select

    | Update(table, assignments, whereExpr) ->
        match scan store dbName table with
        | Error e -> lastInsertId, storageErr e
        | Ok(columns, rows) ->
            let columnIndex = columnIndexOf columns

            match assignments |> traverseList (fun (name, expr) -> resolveColumn columns name |> Result.map (fun i -> i, expr)) with
            | Error e -> lastInsertId, storageErr e
            | Ok indexedAssignments ->
                let check row =
                    match whereExpr with
                    | None -> Ok true
                    | Some expr -> evalExpr registry columnIndex row expr |> Result.map (fun v -> truthy v = Some true)

                let checkAssignments row =
                    indexedAssignments |> traverseList (fun (_, expr) -> evalExpr registry columnIndex row expr)

                // Type-check WHERE/SET against a synthetic all-NULL row
                // first — same reasoning as `runSelect`'s `probeRow`: an
                // unknown column/function is a schema error, not a data
                // one, and shouldn't depend on whether any row happens to
                // match (or exist at all).
                match check (probeRow columns) |> Result.bind (fun _ -> checkAssignments (probeRow columns)) with
                | Error(code, message) -> lastInsertId, Err(code, message)
                | Ok _ ->
                    let predicate row = check row |> Result.defaultValue false
                    let updater row = applyAssignments registry columnIndex indexedAssignments row

                    match updateRows store dbName table predicate updater with
                    | Ok affected -> lastInsertId, Affected(uint64 affected)
                    | Error e -> lastInsertId, storageErr e

    | Delete(table, whereExpr) ->
        match scan store dbName table with
        | Error e -> lastInsertId, storageErr e
        | Ok(columns, _) ->
            let columnIndex = columnIndexOf columns

            let check row =
                match whereExpr with
                | None -> Ok true
                | Some expr -> evalExpr registry columnIndex row expr |> Result.map (fun v -> truthy v = Some true)

            match check (probeRow columns) with
            | Error(code, message) -> lastInsertId, Err(code, message)
            | Ok _ ->
                let predicate row = check row |> Result.defaultValue false

                match deleteRows store dbName table predicate with
                | Ok affected -> lastInsertId, Affected(uint64 affected)
                | Error e -> lastInsertId, storageErr e
