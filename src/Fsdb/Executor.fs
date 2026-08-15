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

/// Splits a `Parser.qualifiedTableName`-produced "db.table" (or bare
/// "table") string back into its two parts, defaulting the database to the
/// session's current one — the other end of that parser's encoding, so a
/// qualified name resolves the same way at every statement site instead of
/// each one growing its own split.
let private splitQualified (defaultDb: string) (name: string) : string * string =
    match name.Split('.') with
    | [| db; tbl |] -> db, tbl
    | _ -> defaultDb, name

/// `Storage.traverse`, generalized over any error type since this module
/// threads both `StorageError` and `EvalError` — kept as a local alias
/// rather than a per-call-site `Storage.traverse` since it reads as a
/// domain operation here, not a storage one.
let private traverseList = Storage.traverse

/// Column name (case-insensitive) to its index in a row array.
let private columnIndexOf (columns: ColumnDef list) : Map<string, int> =
    columns |> List.mapi (fun i c -> c.Name.ToLowerInvariant(), i) |> Map.ofList

/// Whole-table (no `GROUP BY` yet — that's M5) aggregate recognition: a
/// `FuncCall` whose name is registered as an aggregate on `registry` (see
/// `Functions.Registry.Aggregates`) rather than a hardcoded name set here —
/// M6's `registerAggregate` extension point is the same one `Functions`
/// itself uses for COUNT/SUM/AVG/MIN/MAX.
let private isAggregateCall (registry: Registry) (expr: Expr) : bool =
    match expr with
    | FuncCall(name, _) -> Functions.lookupAggregate name registry |> Option.isSome
    | _ -> false

/// Whether `expr` contains an aggregate call *anywhere*, not just at the
/// top level — `SELECT COUNT(*) + 1 FROM t` or a `WHERE`-style predicate
/// nesting one inside a `HAVING`-shaped expression both need this to switch
/// `runSelect` onto `runAggregateSelect`'s path, the same walk
/// `substituteValuesFunc` already does for `VALUES(col)` rewriting.
let rec private containsAggregate (registry: Registry) (expr: Expr) : bool =
    match expr with
    | FuncCall(_, args) -> isAggregateCall registry expr || args |> List.exists (containsAggregate registry)
    | BinOp(_, a, b) -> containsAggregate registry a || containsAggregate registry b
    | Not e
    | IsNull e
    | IsNotNull e -> containsAggregate registry e
    | Like(e, p) -> containsAggregate registry e || containsAggregate registry p
    | In(e, xs) -> containsAggregate registry e || xs |> List.exists (containsAggregate registry)
    | Between(e, lo, hi) -> containsAggregate registry e || containsAggregate registry lo || containsAggregate registry hi
    | Cast(e, _) -> containsAggregate registry e
    | Lit _
    | Col _
    | QualifiedCol _
    | Star
    | Exists _ -> false

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
    | Cast(e, _) -> sprintf "cast(%s as ...)" (exprLabel e)
    | Star -> "*"
    | Exists _ -> "exists"

/// Translates a SQL LIKE pattern to a .NET regex source: `%` -> `.*`, `_` ->
/// `.`, and a backslash-escaped `\%`/`\_` (MySQL's own escape for a literal
/// wildcard character, which `Parser.stringChar` deliberately leaves in the
/// string unresolved) matches that character literally instead. Anchored
/// with `\A`/`\z` rather than `^`/`$` — `$` alone matches before a trailing
/// newline, which would let `'ab\n' LIKE 'ab'` falsely match — and callers
/// must pass `RegexOptions.Singleline` so `.` spans newlines too (`%`/`_`
/// are unqualified wildcards in MySQL, not "everything but a newline"). Not
/// private: `QueryHandler`'s `SHOW VARIABLES LIKE` reuses the same LIKE
/// semantics rather than keeping a second copy.
let likeToRegex (pattern: string) : string =
    let sb = System.Text.StringBuilder()
    let mutable i = 0

    while i < pattern.Length do
        match pattern.[i] with
        | '\\' when i + 1 < pattern.Length && (pattern.[i + 1] = '%' || pattern.[i + 1] = '_') ->
            sb.Append(Regex.Escape(string pattern.[i + 1])) |> ignore
            i <- i + 2
        | '%' ->
            sb.Append(".*") |> ignore
            i <- i + 1
        | '_' ->
            sb.Append(".") |> ignore
            i <- i + 1
        | c ->
            sb.Append(Regex.Escape(string c)) |> ignore
            i <- i + 1

    @"\A" + sb.ToString() + @"\z"

/// Neither of these recurse into `evalExpr`, so they're plain top-level
/// `let`s rather than tied into its `rec ... and` group.
let private boolToValue (b: bool) : Value = VInt(if b then 1L else 0L)

let private likeOp (subject: Value) (pattern: Value) : Value =
    match subject, pattern with
    | VNull, _
    | _, VNull -> VNull
    | _ ->
        let text = subject |> toText |> Option.defaultValue ""
        let pat = pattern |> toText |> Option.defaultValue ""
        boolToValue (Regex.IsMatch(text, likeToRegex pat, RegexOptions.IgnoreCase ||| RegexOptions.Singleline))

/// The three pieces of context `evalExpr` needs to resolve a `Col`/`FuncCall`
/// against, bundled into one record rather than three loose parameters
/// threaded through every call site — M5's aggregates add a fourth
/// (per-group accumulated results the outer expression binds against),
/// which becomes a field here instead of a fourth parameter at every one of
/// those call sites.
/// `Store`/`DbName` are only read by the `Exists` case (a nested `SELECT`
/// needs a whole store/database to run against, not just the current row),
/// but every `EvalContext` carries them rather than splitting into a second
/// "context with subquery support" type — `Exists` can appear inside any
/// expression (a `WHERE`, a projection, ...), so every call site would need
/// to know which one to build.
type private EvalContext =
    { Registry: Registry
      ColumnIndex: Map<string, int>
      /// The lowercased alias-or-table-name a `QualifiedCol` must match to
      /// resolve — `None` when there's no table in scope at all (e.g. a
      /// literal `INSERT ... VALUES` row). Single-table only for now (see
      /// `QualifiedCol`'s match in `evalExpr`); grows into a set once JOINs
      /// bring more than one source into scope.
      Qualifier: string option
      Row: Value[]
      Store: Store
      DbName: string }

/// Evaluates one expression against one row. Three-valued logic throughout
/// (comparisons/AND/OR/NOT return `VNull` — SQL's "unknown" — rather than a
/// boolean whenever an operand is `VNull`, per `Value`'s helpers), function
/// calls resolve through `ctx.Registry` (error 1305 if unregistered), and a
/// bare column resolves through `ctx.ColumnIndex` (error 1054 if unknown).
let rec private evalExpr (ctx: EvalContext) (expr: Expr) : Result<Value, EvalError> =
    let eval = evalExpr ctx

    match expr with
    | Lit v -> Ok v
    | Star -> Error(1054, "Invalid use of '*'")
    | Col name ->
        match Map.tryFind (name.ToLowerInvariant()) ctx.ColumnIndex with
        | Some i -> Ok ctx.Row.[i]
        | None -> Error(unknownColumn name)
    | QualifiedCol(table, col) ->
        match ctx.Qualifier with
        | Some q when System.String.Equals(q, table, System.StringComparison.OrdinalIgnoreCase) -> eval (Col col)
        | _ -> Error(unknownColumn (sprintf "%s.%s" table col))
    | Not e -> eval e |> Result.map (fun v -> truthy v |> Option.map (not >> boolToValue) |> Option.defaultValue VNull)
    | IsNull e -> eval e |> Result.map (function VNull -> VInt 1L | _ -> VInt 0L)
    | IsNotNull e -> eval e |> Result.map (function VNull -> VInt 0L | _ -> VInt 1L)
    | BinOp(op, a, b) ->
        // And/Or already evaluate both operands (no short-circuit, since
        // SQL's three-valued logic needs both sides to tell "false" apart
        // from "unknown"), so every `BinOp` collapses into one total match
        // on `op` here — all 12 `Ast.Op` cases handled in the one place,
        // rather than two more `failwith`-guarded helpers each only
        // partially matching the same type.
        eval a
        |> Result.bind (fun va ->
            eval b
            |> Result.map (fun vb ->
                let compareWith (pred: int -> bool) : Value =
                    match va, vb with
                    | VNull, _
                    | _, VNull -> VNull
                    | _ -> boolToValue (pred (Value.compare va vb))

                match op with
                | And ->
                    match truthy va, truthy vb with
                    | Some false, _
                    | _, Some false -> VInt 0L
                    | Some true, Some true -> VInt 1L
                    | _ -> VNull
                | Or ->
                    match truthy va, truthy vb with
                    | Some true, _
                    | _, Some true -> VInt 1L
                    | Some false, Some false -> VInt 0L
                    | _ -> VNull
                | Add -> Value.add va vb
                | Sub -> Value.sub va vb
                | Mul -> Value.mul va vb
                | Div -> Value.div va vb
                | Eq -> compareWith (fun c -> c = 0)
                | Neq -> compareWith (fun c -> c <> 0)
                | Lt -> compareWith (fun c -> c < 0)
                | Lte -> compareWith (fun c -> c <= 0)
                | Gt -> compareWith (fun c -> c > 0)
                | Gte -> compareWith (fun c -> c >= 0)))
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
        match Functions.lookup name ctx.Registry with
        | None -> Error(unknownFunction name)
        | Some fn -> args |> traverseList eval |> Result.map fn
    | Cast(e, ty) ->
        eval e
        |> Result.bind (fun v ->
            // Reuses `Storage.coerceValue` against a throwaway column of the
            // cast's target type rather than a second coercion table.
            let castCol: ColumnDef =
                { Name = "CAST"
                  Type = ty
                  Nullable = true
                  Default = None
                  AutoIncrement = false
                  PrimaryKey = false
                  Unique = false }

            match Storage.coerceValue castCol v with
            | Ok v' -> Ok v'
            | Error err -> Error(Storage.toMySqlError err))
    | Exists select ->
        match runSelectStmt ctx.Store ctx.Registry ctx.DbName select with
        | ResultSet(_, rows) -> Ok(boolToValue (not (List.isEmpty rows)))
        | Err(code, message) -> Error(code, message)
        // A `SELECT` under `EXISTS (...)` is never an `INSERT`/`UPDATE`/
        // `DELETE` (the parser's `selectStmtRecord` only builds `SelectStmt`
        // records, nothing else reaches here), so `Affected` can't occur.
        | Affected _ -> Ok VNull

/// Resolves a `SELECT`'s `FROM` (a real table, `information_schema`'s
/// virtual one, or none) and runs `select` against it — the `Statement`
/// case's `Select` branch and `Exists`' nested subquery both fund into this
/// one place rather than each re-implementing the `information_schema`
/// special case.
and private runSelectStmt (store: Store) (registry: Registry) (dbName: string) (select: SelectStmt) : QueryResult =
    match select.From with
    | None -> runSelect store registry dbName [] [ [||] ] select
    | Some tableRef ->
        let tableDb = tableRef.Database |> Option.defaultValue dbName

        if System.String.Equals(tableDb, "information_schema", System.StringComparison.OrdinalIgnoreCase) then
            match InformationSchema.scan store.Catalog tableRef.Table with
            | Some(columns, rows) -> runSelect store registry dbName columns rows select
            | None -> storageErr (NoSuchTable tableRef.Table)
        else
            match scan store tableDb tableRef.Table with
            | Error e -> storageErr e
            | Ok(columns, rows) -> runSelect store registry dbName columns (List.ofSeq rows) select

/// The lowercased alias-or-table-name a `QualifiedCol` in this `select`'s
/// scope must match — `None` for a `FROM`-less `SELECT`. Single-table only,
/// same limit as `EvalContext.Qualifier`.
and private qualifierOf (select: SelectStmt) : string option =
    select.From |> Option.map (fun t -> (t.Alias |> Option.defaultValue t.Table).ToLowerInvariant())

and private applyLimitOffset (limit: int option) (offset: int option) (rows: 'a list) : 'a list =
    let afterOffset =
        match offset with
        | Some o -> rows |> List.skip (min o (List.length rows))
        | None -> rows

    match limit with
    | Some l -> afterOffset |> List.truncate (max 0 l)
    | None -> afterOffset

/// One projection's `(column name, value)` pairs — a list because `SELECT
/// *` expands to every column of the row.
and private evalProjection (ctx: EvalContext) (columns: ColumnDef list) (proj: Projection) : Result<(string * Value) list, EvalError> =
    match proj with
    | Star, _ -> Ok(columns |> List.mapi (fun i c -> c.Name, ctx.Row.[i]))
    | expr, aliasOpt ->
        evalExpr ctx expr
        |> Result.map (fun v -> [ aliasOpt |> Option.defaultValue (exprLabel expr), v ])

/// An all-`VNull` row shaped like `columns` — used only to type-check a
/// statement's expressions (unknown column/function) independent of the
/// actual data, since those errors are about the schema, not row values.
/// Without this, a table with zero matching (or zero total) rows would
/// silently skip evaluating its WHERE/ORDER BY/projection at all and never
/// surface a real error.
and private probeRow (columns: ColumnDef list) : Value[] = Array.create (List.length columns) VNull

/// One aggregate call's value over the rows a `WHERE` already filtered to.
/// `COUNT(*)` counts rows directly (`*` isn't a valid `Expr`, so there's
/// nothing to evaluate per row); every other form evaluates its one
/// argument per row first, drops the `NULL`s (SQL's aggregate rule), then
/// folds via whatever `registry` has registered for `name` (see
/// `Functions.Registry.Aggregates`) — except `COUNT(expr)`, which (unlike
/// `SUM`/`AVG`/`MIN`/`MAX`) yields `0` rather than `NULL` on an empty
/// non-NULL set, so it still folds even when there's nothing left to fold.
and private evalAggregate
    (registry: Registry)
    (ctxFor: Value[] -> EvalContext)
    (rows: Value[] list)
    (name: string)
    (args: Expr list)
    : Result<Value, EvalError> =
    let isCount = System.String.Equals(name, "COUNT", System.StringComparison.OrdinalIgnoreCase)

    match args with
    | [ Star ] when isCount -> Ok(VInt(int64 (List.length rows)))
    | [ arg ] ->
        match Functions.lookupAggregate name registry with
        | None -> Error(unknownFunction name)
        | Some fold ->
            rows
            |> traverseList (fun row -> evalExpr (ctxFor row) arg)
            |> Result.map (fun vs ->
                let nonNull = vs |> List.filter (function VNull -> false | _ -> true)
                if isCount || not nonNull.IsEmpty then fold nonNull else VNull)
    // `isAggregateCall` (the only caller that routes here) already narrowed
    // to single-argument aggregate calls.
    | _ -> Ok VNull

/// Pre-evaluates every aggregate subtree of `expr` (anywhere it appears —
/// nested in arithmetic, a function argument, ...) against `rows` into a
/// `Lit`, so the caller can evaluate what's left as an ordinary per-row
/// expression against one representative row. Same shape as
/// `substituteValuesFunc`'s rewrite walk. Without this, `SELECT COUNT(*) +
/// 1 FROM t` fails: the top-level node is `BinOp(Add, FuncCall("COUNT",
/// [Star]), Lit 1)`, not a bare `FuncCall`, so plain per-row evaluation
/// would try (and fail) to look `COUNT` up as a scalar function.
and private rewriteAggregates
    (registry: Registry)
    (ctxFor: Value[] -> EvalContext)
    (rows: Value[] list)
    (expr: Expr)
    : Result<Expr, EvalError> =
    let sub = rewriteAggregates registry ctxFor rows

    match expr with
    | FuncCall(name, args) when isAggregateCall registry expr -> evalAggregate registry ctxFor rows name args |> Result.map Lit
    | FuncCall(name, args) -> args |> traverseList sub |> Result.map (fun args' -> FuncCall(name, args'))
    | BinOp(op, a, b) -> sub a |> Result.bind (fun a' -> sub b |> Result.map (fun b' -> BinOp(op, a', b')))
    | Not e -> sub e |> Result.map Not
    | IsNull e -> sub e |> Result.map IsNull
    | IsNotNull e -> sub e |> Result.map IsNotNull
    | Like(e, p) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Like(e', p')))
    | In(e, xs) -> sub e |> Result.bind (fun e' -> xs |> traverseList sub |> Result.map (fun xs' -> In(e', xs')))
    | Between(e, lo, hi) ->
        sub e |> Result.bind (fun e' -> sub lo |> Result.bind (fun lo' -> sub hi |> Result.map (fun hi' -> Between(e', lo', hi'))))
    | Cast(e, ty) -> sub e |> Result.map (fun e' -> Cast(e', ty))
    | Lit _
    | Col _
    | QualifiedCol _
    | Star
    | Exists _ -> Ok expr

/// The ungrouped-aggregate path (`SELECT MAX(x) FROM t`, no `GROUP BY`):
/// the whole `WHERE`-filtered row set collapses to one output row. `ORDER
/// BY`/`LIMIT` are moot on a single row, so unlike `runSelect` this doesn't
/// apply them.
and private runAggregateSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (rows: Value[] list)
    (select: SelectStmt)
    : QueryResult =
    let columnIndex = columnIndexOf columns
    let qualifier = qualifierOf select

    let ctxFor (row: Value[]) : EvalContext =
        { Registry = registry
          ColumnIndex = columnIndex
          Qualifier = qualifier
          Row = row
          Store = store
          DbName = dbName }

    let matches (row: Value[]) : Result<bool, EvalError> =
        match select.Where with
        | None -> Ok true
        | Some expr -> evalExpr (ctxFor row) expr |> Result.map (fun v -> truthy v = Some true)

    match rows |> traverseList (fun row -> matches row |> Result.map (fun keep -> if keep then Some row else None)) with
    | Error(code, message) -> Err(code, message)
    | Ok maybeMatched ->
        let matched = maybeMatched |> List.choose id

        // A non-aggregate projection alongside an aggregate one (legal
        // under non-strict `sql_mode`, which is what this engine accepts)
        // has no well-defined row to read from once many rows collapse
        // into one — ponytail: picks the first matched row (or an all-NULL
        // probe row if none matched) rather than real `GROUP BY` per-group
        // semantics; add that (M5) once a query needs more than one group.
        let representativeRow = matched |> List.tryHead |> Option.defaultValue (probeRow columns)

        let projectOne ((expr, aliasOpt): Projection) : Result<string * Value, EvalError> =
            let label = aliasOpt |> Option.defaultValue (exprLabel expr)

            match expr with
            | Star -> Error(1054, "Invalid use of '*'")
            | _ ->
                rewriteAggregates registry ctxFor matched expr
                |> Result.bind (evalExpr (ctxFor representativeRow))
                |> Result.map (fun v -> label, v)

        match select.Projections |> traverseList projectOne with
        | Error(code, message) -> Err(code, message)
        | Ok pairs -> ResultSet(pairs |> List.map fst, [ pairs |> List.map (snd >> toText) ])

and private runSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (rows: Value[] list)
    (select: SelectStmt)
    : QueryResult =
    let projections, whereExpr, orderBy, limit, offset =
        select.Projections, select.Where, select.OrderBy, select.Limit, select.Offset

    // A `SELECT` with no `FROM` at all has no columns to expand `*`/`t.*`
    // against — real MySQL rejects it as 1096 rather than emitting a
    // resultset with zero columns, which isn't a legal text-resultset
    // packet and aborts the client's whole session.
    if select.From.IsNone && projections |> List.exists (fst >> (=) Star) then
        Err(1096, "No tables used")
    elif projections |> List.exists (fst >> containsAggregate registry) then
        runAggregateSelect store registry dbName columns rows select
    else

    let columnIndex = columnIndexOf columns
    let qualifier = qualifierOf select

    let ctxFor (row: Value[]) : EvalContext =
        { Registry = registry
          ColumnIndex = columnIndex
          Qualifier = qualifier
          Row = row
          Store = store
          DbName = dbName }

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
        | Some expr -> evalExpr (ctxFor row) expr |> Result.map (fun v -> truthy v = Some true)

    let orderKeys (row: Value[]) : Result<Value list, EvalError> =
        orderBy |> traverseList (fun (expr, _) -> evalExpr (ctxFor row) (resolveOrderExpr expr))

    let projectRow (row: Value[]) : Result<(string * Value) list, EvalError> =
        projections
        |> traverseList (evalProjection (ctxFor row) columns)
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

        // A row-level WHERE/ORDER BY failure (not reachable today, but a
        // real possibility once a function can fail per row rather than
        // just at the schema level the probe above already checks) must
        // surface as an `Err`, not be silently treated as "row excluded"
        // or "sorts as if no keys" — thread the `Result` through instead of
        // defaulting it away.
        let keepWithOrderKeys (row: Value[]) : Result<(Value list * Value[]) option, EvalError> =
            matches row
            |> Result.bind (fun keep -> if keep then orderKeys row |> Result.map (fun keys -> Some(keys, row)) else Ok None)

        match rows |> traverseList keepWithOrderKeys with
        | Error(code, message) -> Err(code, message)
        | Ok maybeKeyed ->
            let keyed = maybeKeyed |> List.choose id

            match keyed |> sortRows |> List.map snd |> applyLimitOffset limit offset |> traverseList projectRow with
            | Error(code, message) -> Err(code, message)
            | Ok projectedRows -> ResultSet(colNames, projectedRows |> List.map (List.map (snd >> toText)))

/// Assigns `assignments` (already resolved to column indices) to a copy of
/// `row`, evaluating each right-hand side against the row's original
/// (pre-assignment) values. A failing right-hand side propagates as an
/// `Error` (as a `StorageError` so it can travel through
/// `Storage.updateRows`'s `updater`) instead of silently writing `VNull` —
/// the difference between "UPDATE failed" and quiet data corruption once a
/// SET expression can fail per row.
let private applyAssignments
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columnIndex: Map<string, int>)
    (qualifier: string option)
    (assignments: (int * Expr) list)
    (row: Value[])
    : Result<Value[], StorageError> =
    let ctx =
        { Registry = registry
          ColumnIndex = columnIndex
          Qualifier = qualifier
          Row = row
          Store = store
          DbName = dbName }

    assignments
    |> traverseList (fun (idx, expr) -> evalExpr ctx expr |> Result.map (fun v -> idx, v))
    |> Result.mapError ExpressionError
    |> Result.map (fun idxVals ->
        let newRow = Array.copy row
        for idx, v in idxVals do
            newRow.[idx] <- v
        newRow)

/// Rewrites `VALUES(col)` calls (MySQL's way of referring, inside an
/// `INSERT ... ON DUPLICATE KEY UPDATE` assignment, to the value that row
/// would have inserted) into the literal `candidate` value for that column —
/// `funcCallAtom` already parses `VALUES(col)` as an ordinary `FuncCall`
/// since it just looks like one syntactically, so this is a plain
/// pre-evaluation rewrite rather than new grammar.
let rec private substituteValuesFunc (columnIndex: Map<string, int>) (candidate: Value[]) (expr: Expr) : Expr =
    let sub = substituteValuesFunc columnIndex candidate

    match expr with
    | FuncCall(name, [ Col c ]) when System.String.Equals(name, "VALUES", System.StringComparison.OrdinalIgnoreCase) ->
        match Map.tryFind (c.ToLowerInvariant()) columnIndex with
        | Some i -> Lit candidate.[i]
        | None -> expr
    | FuncCall(name, args) -> FuncCall(name, args |> List.map sub)
    | BinOp(op, a, b) -> BinOp(op, sub a, sub b)
    | Not e -> Not(sub e)
    | IsNull e -> IsNull(sub e)
    | IsNotNull e -> IsNotNull(sub e)
    | Like(e, p) -> Like(sub e, sub p)
    | In(e, xs) -> In(sub e, xs |> List.map sub)
    | Between(e, lo, hi) -> Between(sub e, sub lo, sub hi)
    | Cast(e, ty) -> Cast(sub e, ty)
    | Lit _
    | Col _
    | QualifiedCol _
    | Star
    | Exists _ -> expr

/// Executes one parsed statement against `store`, threading the session's
/// AUTO_INCREMENT bookkeeping through as a plain value rather than a
/// `Session` (this module knows nothing about sessions or connections —
/// `QueryHandler` is the layer that owns that). Returns the (possibly
/// updated) `lastInsertId` alongside the result.
let execute (store: Store) (registry: Registry) (dbName: string) (lastInsertId: int64) (stmt: Statement) : int64 * QueryResult =
    match stmt with
    | CreateDatabase(name, ifNotExists) ->
        match Storage.createDatabase store name with
        | Ok() -> lastInsertId, Affected 0UL
        | Error(DatabaseExists _) when ifNotExists -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | DropDatabase(name, ifExists) ->
        match Storage.dropDatabase store name with
        | Ok() -> lastInsertId, Affected 0UL
        | Error(NoSuchDatabase _) when ifExists -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | CreateTable(name, columns, indexes, foreignKeys, ifNotExists) ->
        let db, name = splitQualified dbName name

        match createTable store db name columns indexes foreignKeys with
        | Ok() -> lastInsertId, Affected 0UL
        | Error(TableExists _) when ifNotExists -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | DropTable(names, ifExists) ->
        let dropOne name =
            let db, name = splitQualified dbName name

            match dropTable store db name with
            | Ok() -> Ok()
            | Error(NoSuchTable _) when ifExists -> Ok()
            | Error e -> Error e

        match names |> traverseList dropOne with
        | Ok _ -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | AlterTable(table, actions) ->
        let db, table = splitQualified dbName table

        match alterTable store db table actions with
        | Ok() -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | RenameTable pairs ->
        // A cross-database `RENAME TABLE a.t TO b.t` only takes the target
        // name's table part — ponytail: doesn't actually move the table
        // between catalogs, add that once a migration renames across
        // databases rather than within one.
        let renameOne (oldName, newName) =
            let db, oldTable = splitQualified dbName oldName
            let _, newTable = splitQualified dbName newName
            renameTable store db oldTable newTable

        match pairs |> traverseList renameOne with
        | Ok _ -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | CreateIndex(name, table, columns, unique) ->
        let db, table = splitQualified dbName table

        match alterTable store db table [ AddIndex { Name = name; Columns = columns; Unique = unique } ] with
        | Ok() -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | DropIndexStmt(name, table) ->
        let db, table = splitQualified dbName table

        match alterTable store db table [ DropIndexAction name ] with
        | Ok() -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | Truncate table ->
        let db, table = splitQualified dbName table

        match truncate store db table with
        | Ok() -> lastInsertId, Affected 0UL
        | Error e -> lastInsertId, storageErr e

    | Insert(table, columns, rowsExprs, onDuplicateUpdate, _ignoreDuplicates) ->
        // INSERT ... VALUES expressions aren't evaluated against any row
        // (no table columns are in scope), just literals/functions — an
        // empty column index turns a stray `Col` reference into a clean
        // 1054 rather than an index-out-of-range. `INSERT IGNORE`'s flag is
        // accepted by the parser but not otherwise acted on here — ponytail:
        // MySQL's `IGNORE` downgrades a would-be error per row to a warning
        // and skips just that row; this engine still fails the whole
        // statement, add per-row suppression once a migration/test actually
        // depends on partial success rather than just the syntax parsing.
        let db, table = splitQualified dbName table

        let literalCtx =
            { Registry = registry
              ColumnIndex = Map.empty
              Qualifier = None
              Row = [||]
              Store = store
              DbName = dbName }

        match rowsExprs |> traverseList (traverseList (evalExpr literalCtx)) with
        | Error(code, message) -> lastInsertId, Err(code, message)
        | Ok rowsValues ->
            let cols = if columns.IsEmpty then None else Some columns

            if onDuplicateUpdate.IsEmpty then
                match insertRows store db table cols rowsValues with
                | Ok(newLastId, affected) ->
                    (if newLastId <> 0L then newLastId else lastInsertId), Affected(uint64 affected)
                | Error e -> lastInsertId, storageErr e
            else
                match scan store db table with
                | Error e -> lastInsertId, storageErr e
                | Ok(tableColumns, _) ->
                    let columnIndex = columnIndexOf tableColumns

                    let applyUpdate (existing: Value[]) (candidate: Value[]) : Result<Value[], StorageError> =
                        let ctx =
                            { Registry = registry
                              ColumnIndex = columnIndex
                              Qualifier = Some(table.ToLowerInvariant())
                              Row = existing
                              Store = store
                              DbName = dbName }

                        onDuplicateUpdate
                        |> traverseList (fun (name, expr) ->
                            match resolveColumn tableColumns name with
                            | Error e -> Error e
                            | Ok idx ->
                                match evalExpr ctx (substituteValuesFunc columnIndex candidate expr) with
                                | Ok v -> Ok(idx, v)
                                | Error err -> Error(ExpressionError err))
                        |> Result.map (fun idxVals ->
                            let newRow = Array.copy existing
                            for idx, v in idxVals do
                                newRow.[idx] <- v
                            newRow)

                    match upsertRows store db table cols rowsValues applyUpdate with
                    | Ok(newLastId, affected) ->
                        (if newLastId <> 0L then newLastId else lastInsertId), Affected(uint64 affected)
                    | Error e -> lastInsertId, storageErr e

    | Select select -> lastInsertId, runSelectStmt store registry dbName select

    | Update(table, assignments, whereExpr) ->
        let db, table = splitQualified dbName table

        match scan store db table with
        | Error e -> lastInsertId, storageErr e
        | Ok(columns, rows) ->
            let columnIndex = columnIndexOf columns

            match assignments |> traverseList (fun (name, expr) -> resolveColumn columns name |> Result.map (fun i -> i, expr)) with
            | Error e -> lastInsertId, storageErr e
            | Ok indexedAssignments ->
                let qualifier = Some(table.ToLowerInvariant())

                let ctxFor row =
                    { Registry = registry
                      ColumnIndex = columnIndex
                      Qualifier = qualifier
                      Row = row
                      Store = store
                      DbName = dbName }

                let check row =
                    match whereExpr with
                    | None -> Ok true
                    | Some expr -> evalExpr (ctxFor row) expr |> Result.map (fun v -> truthy v = Some true)

                let checkAssignments row =
                    indexedAssignments |> traverseList (fun (_, expr) -> evalExpr (ctxFor row) expr)

                // Type-check WHERE/SET against a synthetic all-NULL row
                // first — same reasoning as `runSelect`'s `probeRow`: an
                // unknown column/function is a schema error, not a data
                // one, and shouldn't depend on whether any row happens to
                // match (or exist at all).
                match check (probeRow columns) |> Result.bind (fun _ -> checkAssignments (probeRow columns)) with
                | Error(code, message) -> lastInsertId, Err(code, message)
                | Ok _ ->
                    let predicate row = check row |> Result.mapError ExpressionError
                    let updater row = applyAssignments store registry dbName columnIndex qualifier indexedAssignments row

                    match updateRows store db table predicate updater with
                    | Ok affected -> lastInsertId, Affected(uint64 affected)
                    | Error e -> lastInsertId, storageErr e

    | Delete(table, whereExpr) ->
        let db, table = splitQualified dbName table

        match scan store db table with
        | Error e -> lastInsertId, storageErr e
        | Ok(columns, _) ->
            let columnIndex = columnIndexOf columns

            let ctxFor row =
                { Registry = registry
                  ColumnIndex = columnIndex
                  Qualifier = Some(table.ToLowerInvariant())
                  Row = row
                  Store = store
                  DbName = dbName }

            let check row =
                match whereExpr with
                | None -> Ok true
                | Some expr -> evalExpr (ctxFor row) expr |> Result.map (fun v -> truthy v = Some true)

            match check (probeRow columns) with
            | Error(code, message) -> lastInsertId, Err(code, message)
            | Ok _ ->
                let predicate row = check row |> Result.mapError ExpressionError

                match deleteRows store db table predicate with
                | Ok affected -> lastInsertId, Affected(uint64 affected)
                | Error e -> lastInsertId, storageErr e
