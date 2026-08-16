/// Turns an `Ast.Statement` into effects against `Storage` and rows out.
/// The SELECT pipeline is volcano-style over plain `list`s (see the
/// `ponytail` note on `materialize` below for why not a lazier `seq`):
/// scan -> filter -> order -> limit/offset -> project.
module Fsdb.Executor

open System.Collections.Generic
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

/// The leading numeric run of `s`, the way MySQL's numeric `CAST`/implicit
/// string-to-number conversion reads a string — `"12abc"` yields
/// `Some "12"`, `"abc"` yields `None`. Unlike `Storage.coerceValue`'s
/// `parseNumeric`, which requires the *whole* trimmed string to parse. An
/// integer target reads only an optional sign and digits and stops there —
/// `CAST('1e3' AS SIGNED)` is `1` in MySQL, not `1000` — while a
/// DECIMAL/float target also honors a fraction and exponent
/// (`CAST('1e3' AS DECIMAL(10,2))` is `1000`), so the two targets need
/// different grammars rather than one regex serving both.
let private leadingIntegerPrefixRegex = Regex(@"^\s*[+-]?\d+")
let private leadingFloatPrefixRegex = Regex(@"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?")

let private leadingNumericPrefix (regex: Regex) (s: string) : string option =
    let m = regex.Match s
    if m.Success && m.Value.Trim() <> "" then Some(m.Value.Trim()) else None

/// Column name (case-insensitive) to *every* index it resolves to in a row
/// array — usually exactly one, but a `JOIN` can combine two tables that
/// both have a column of the same name (`SELECT id FROM u JOIN p ON
/// p.uid = u.id`). Keeping every match (rather than `Map.ofList`'s silent
/// "last one wins") is what lets `resolveCol` tell an ambiguous bare
/// reference apart from an unambiguous one and raise error 1052, the same
/// way real MySQL does, instead of silently binding to whichever table
/// happened to be listed last in the `JOIN`.
let private columnIndexOf (columns: ColumnDef list) : Map<string, int list> =
    columns
    |> List.mapi (fun i c -> c.Name.ToLowerInvariant(), i)
    |> List.groupBy fst
    |> List.map (fun (name, xs) -> name, xs |> List.map snd)
    |> Map.ofList

/// Which clause a bare `Col` is being resolved from — the only thing that
/// varies between an ambiguous-column 1052 in a field list, a `WHERE`, a
/// JOIN `ON`, an `ORDER BY`, or a `GROUP BY`/`HAVING` is the four words
/// MySQL puts in the error message, so `resolveCol` takes one of these
/// instead of five near-identical copies of itself.
type private Clause =
    | FieldList
    | WhereClause
    | OnClause
    | OrderClause
    | GroupStatement

let private clauseLabel =
    function
    | FieldList -> "field list"
    | WhereClause -> "where clause"
    | OnClause -> "on clause"
    | OrderClause -> "order clause"
    | GroupStatement -> "group statement"

/// Aggregate-call recognition: a `FuncCall` whose name is registered as an
/// aggregate on `registry` (see `Functions.Registry.Aggregates`) rather than
/// a hardcoded name set here — M6's `registerAggregate` extension point is
/// the same one `Functions` itself uses for COUNT/SUM/AVG/MIN/MAX — plus
/// `GROUP_CONCAT`, which is always recognized directly since its
/// `SEPARATOR`/multi-arg evaluation lives entirely in `evalAggregate` below
/// rather than the registry (see `Parser.groupConcatAtom`'s doc for why it's
/// not just another `registerAggregate` entry).
let private isAggregateCall (registry: Registry) (expr: Expr) : bool =
    match expr with
    | FuncCall(name, _) ->
        System.String.Equals(name, "GROUP_CONCAT", System.StringComparison.OrdinalIgnoreCase)
        || Functions.lookupAggregate name registry |> Option.isSome
    | _ -> false

/// Whether `expr` contains an aggregate call *anywhere*, not just at the
/// top level — `SELECT COUNT(*) + 1 FROM t` or a `WHERE`-style predicate
/// nesting one inside a `HAVING`-shaped expression both need this to switch
/// `runSelect` onto `runGroupedSelect`'s path, the same walk
/// `substituteValuesFunc` already does for `VALUES(col)` rewriting.
let rec private containsAggregate (registry: Registry) (expr: Expr) : bool =
    match expr with
    | FuncCall(_, args) -> isAggregateCall registry expr || args |> List.exists (containsAggregate registry)
    | BinOp(_, a, b) -> containsAggregate registry a || containsAggregate registry b
    | Not e
    | IsNull e
    | IsNotNull e
    | IsTrue e
    | IsFalse e
    | Distinct e
    | OrderBy(e, _) -> containsAggregate registry e
    | Like(e, p, _) -> containsAggregate registry e || containsAggregate registry p
    | Regexp(e, p) -> containsAggregate registry e || containsAggregate registry p
    | In(e, xs) -> containsAggregate registry e || xs |> List.exists (containsAggregate registry)
    | Between(e, lo, hi) -> containsAggregate registry e || containsAggregate registry lo || containsAggregate registry hi
    | Cast(e, _) -> containsAggregate registry e
    | Case(subject, whens, elseBranch) ->
        (subject |> Option.map (containsAggregate registry) |> Option.defaultValue false)
        || whens |> List.exists (fun (c, r) -> containsAggregate registry c || containsAggregate registry r)
        || (elseBranch |> Option.map (containsAggregate registry) |> Option.defaultValue false)
    | Lit _
    | Col _
    | QualifiedCol _
    | Star _
    | RowNumberOver _
    | LagOver _
    // A subquery's own aggregates belong to *its* grouping, not the query
    // this expression sits in — `containsAggregate` only asks whether
    // `runSelect` needs to switch itself onto the grouped path, so these
    // three never contribute regardless of what their nested `SelectStmt`
    // contains.
    | Exists _
    | Subquery _
    | InSubquery _ -> false

/// Every `RowNumberOver`/`LagOver` node inside `expr`, in encounter order —
/// pre-order, same walk shape as the later `collectSubqueries` for the
/// corresponding job. A window function can sit anywhere in a projection's
/// expression tree (`value - LAG(value) OVER (...)`), not just bare at the
/// top level, so both `runSelect`'s dispatch and `runWindowedSelect`'s
/// rewrite need every occurrence rather than only a top-level one.
let rec private collectWindowFuncs (expr: Expr) : Expr list =
    match expr with
    | RowNumberOver _
    | LagOver _ -> [ expr ]
    | FuncCall(_, args) -> args |> List.collect collectWindowFuncs
    | BinOp(_, a, b) -> collectWindowFuncs a @ collectWindowFuncs b
    | Not e
    | IsNull e
    | IsNotNull e
    | IsTrue e
    | IsFalse e
    | Distinct e
    | OrderBy(e, _)
    | Cast(e, _) -> collectWindowFuncs e
    | Like(e, p, _) -> collectWindowFuncs e @ collectWindowFuncs p
    | Regexp(e, p) -> collectWindowFuncs e @ collectWindowFuncs p
    | In(e, xs) -> collectWindowFuncs e @ (xs |> List.collect collectWindowFuncs)
    | Between(e, lo, hi) -> collectWindowFuncs e @ collectWindowFuncs lo @ collectWindowFuncs hi
    | Case(subject, whens, elseBranch) ->
        (subject |> Option.map collectWindowFuncs |> Option.defaultValue [])
        @ (whens |> List.collect (fun (c, r) -> collectWindowFuncs c @ collectWindowFuncs r))
        @ (elseBranch |> Option.map collectWindowFuncs |> Option.defaultValue [])
    | Lit _
    | Col _
    | QualifiedCol _
    | Star _
    | Exists _
    | Subquery _
    | InSubquery _ -> []

/// Replaces every window-function node `collectWindowFuncs` would find with
/// the plain `Col` reference `synthetic` maps it to (structural lookup — a
/// small association list rather than a `Map`, since `Expr` carries no
/// `Comparison` instance for a `Map` key to lean on). `runWindowedSelect`'s
/// rewrite step, generalized to substitute a window function nested inside
/// arithmetic/`CASE`/... in place, not just a bare top-level projection.
let rec private substituteWindowFuncs (synthetic: (Expr * string) list) (expr: Expr) : Expr =
    match synthetic |> List.tryFind (fun (e, _) -> e = expr) with
    | Some(_, name) -> Col name
    | None ->
        let sub = substituteWindowFuncs synthetic

        match expr with
        | FuncCall(name, args) -> FuncCall(name, args |> List.map sub)
        | BinOp(op, a, b) -> BinOp(op, sub a, sub b)
        | Not e -> Not(sub e)
        | IsNull e -> IsNull(sub e)
        | IsNotNull e -> IsNotNull(sub e)
        | IsTrue e -> IsTrue(sub e)
        | IsFalse e -> IsFalse(sub e)
        | Distinct e -> Distinct(sub e)
        | OrderBy(e, dir) -> OrderBy(sub e, dir)
        | Cast(e, ty) -> Cast(sub e, ty)
        | Like(e, p, cs) -> Like(sub e, sub p, cs)
        | Regexp(e, p) -> Regexp(sub e, sub p)
        | In(e, xs) -> In(sub e, xs |> List.map sub)
        | Between(e, lo, hi) -> Between(sub e, sub lo, sub hi)
        | Case(subject, whens, elseBranch) ->
            Case(subject |> Option.map sub, whens |> List.map (fun (c, r) -> sub c, sub r), elseBranch |> Option.map sub)
        // Every occurrence of a `RowNumberOver`/`LagOver` structurally equal
        // to one `collectWindowFuncs` found is already handled by the
        // lookup above; the only way one reaches here is if it isn't one of
        // them, which can't happen given `runWindowedSelect` always builds
        // `synthetic` from `collectWindowFuncs`'s own result — a leaf
        // passthrough is the safe default regardless.
        | Lit _
        | Col _
        | QualifiedCol _
        | Star _
        | RowNumberOver _
        | LagOver _
        | Exists _
        | Subquery _
        | InSubquery _ -> expr

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
    | NullSafeEq -> "<=>"

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
    | IsTrue e -> sprintf "(%s is true)" (exprLabel e)
    | IsFalse e -> sprintf "(%s is false)" (exprLabel e)
    | Like(e, p, _) -> sprintf "(%s like %s)" (exprLabel e) (exprLabel p)
    | Regexp(e, p) -> sprintf "(%s regexp %s)" (exprLabel e) (exprLabel p)
    | In(e, xs) -> sprintf "(%s in (%s))" (exprLabel e) (xs |> List.map exprLabel |> String.concat ",")
    | InSubquery(e, _) -> sprintf "(%s in (...))" (exprLabel e)
    | Between(e, lo, hi) -> sprintf "(%s between %s and %s)" (exprLabel e) (exprLabel lo) (exprLabel hi)
    | Cast(e, _) -> sprintf "cast(%s as ...)" (exprLabel e)
    | Distinct e -> sprintf "distinct %s" (exprLabel e)
    | OrderBy(e, _) -> exprLabel e
    | Case _ -> "case"
    | Star None -> "*"
    | Star(Some q) -> sprintf "%s.*" q
    | RowNumberOver _ -> "row_number() over ()"
    | LagOver(e, _, _, _) -> sprintf "lag(%s) over ()" (exprLabel e)
    | Exists _ -> "exists"
    | Subquery _ -> "(...)"

/// Neither of these recurse into `evalExpr`, so they're plain top-level
/// `let`s rather than tied into its `rec ... and` group.
let private boolToValue (b: bool) : Value = VInt(if b then 1L else 0L)

let private likeOp (caseSensitive: bool) (subject: Value) (pattern: Value) : Value =
    match subject, pattern with
    | VNull, _
    | _, VNull -> VNull
    | _ ->
        let text = subject |> toText |> Option.defaultValue ""
        let pat = pattern |> toText |> Option.defaultValue ""
        let opts = if caseSensitive then RegexOptions.Singleline else RegexOptions.IgnoreCase ||| RegexOptions.Singleline
        boolToValue (Regex.IsMatch(text, likeToRegex pat, opts))

/// `REGEXP`/`RLIKE` — MySQL's default collation makes these case-insensitive
/// too, same as `LIKE`; unlike `LIKE`'s translated wildcard syntax, the
/// pattern is already a real (POSIX-flavored, close enough to .NET's for the
/// common cases Eloquent generates) regex, so it's handed to `Regex`
/// directly rather than through `likeToRegex`.
let private regexpOp (subject: Value) (pattern: Value) : Value =
    match subject, pattern with
    | VNull, _
    | _, VNull -> VNull
    | _ ->
        let text = subject |> toText |> Option.defaultValue ""
        let pat = pattern |> toText |> Option.defaultValue ""
        boolToValue (Regex.IsMatch(text, pat, RegexOptions.IgnoreCase))

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
      ColumnIndex: Map<string, int list>
      /// Per-source-table resolution for `QualifiedCol`: lowercased alias-
      /// or-table-name -> that source's own column list plus the offset its
      /// columns start at within `Row` (`Row` is one table's columns for a
      /// plain `FROM`, or every joined table's columns concatenated
      /// left-to-right for a JOIN — see `Executor.applyJoin`). Empty when
      /// there's no table in scope at all (e.g. a literal `INSERT ...
      /// VALUES` row), so any `QualifiedCol` there is a clean unknown-column
      /// error instead of an index-out-of-range.
      Qualifiers: Map<string, ColumnDef list * int>
      Row: Value[]
      Store: Store
      DbName: string
      /// The enclosing query's own context, if this one belongs to a
      /// subquery (`Exists`/`Subquery`/`InSubquery`) — `None` for every
      /// top-level statement. `Col`/`QualifiedCol` fall back to it when a
      /// name isn't in this context's own `ColumnIndex`/`Qualifiers`, which
      /// is what makes a *correlated* subquery (`WHERE EXISTS (SELECT 1
      /// FROM t2 WHERE t2.parent_id = t1.id)`) resolve `t1.id` at all: it
      /// isn't a column of `t2`, so it falls through to the outer row that
      /// was in scope when the subquery started running. `runSelectStmt`
      /// takes an `EvalContext option` for exactly this and passes it down
      /// to every context it builds while running that subquery, so the
      /// chain composes to any nesting depth.
      Outer: EvalContext option
      /// Which clause a bare `Col` lookup through this context is on behalf
      /// of — only wired up for the ambiguous-column 1052's message
      /// (`resolveCol`); `contextFactory` defaults every context to
      /// `FieldList`, and call sites override it with a record update
      /// (`{ ctx with Clause = WhereClause }`) for the handful of spots
      /// that resolve a `WHERE`/`ON`/`ORDER BY`/`GROUP BY` expression
      /// instead of a projection.
      Clause: Clause }

/// `EvalContext.Qualifiers` for a single unaliased/aliased table in scope —
/// every non-JOIN statement (`UPDATE`, `DELETE`, `INSERT ... ON DUPLICATE
/// KEY UPDATE`) builds it this way, one entry at offset 0.
let private singleQualifier (name: string) (columns: ColumnDef list) : Map<string, ColumnDef list * int> =
    Map.ofList [ name.ToLowerInvariant(), (columns, 0) ]

/// Everything constant for one statement, curried ahead of the row — every
/// `ctxFor`/`ctx` below collapses to one line instead of an eight-line
/// `EvalContext` record literal repeated at each call site.
let private contextFactory
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columnIndex: Map<string, int list>)
    (qualifiers: Map<string, ColumnDef list * int>)
    (outer: EvalContext option)
    : Value[] -> EvalContext =
    fun row ->
        { Registry = registry
          ColumnIndex = columnIndex
          Qualifiers = qualifiers
          Row = row
          Store = store
          DbName = dbName
          Outer = outer
          Clause = FieldList }

/// Resolves a bare column against `ctx`, falling back to
/// `ctx.Outer`/its own outer/... on a miss — see `EvalContext.Outer`. Two or
/// more matches (a `JOIN` of tables that share a column name) is error 1052,
/// not a silent pick of whichever one `columnIndexOf` happened to see last.
let rec private resolveCol (ctx: EvalContext) (name: string) : Result<Value, EvalError> =
    match Map.tryFind (name.ToLowerInvariant()) ctx.ColumnIndex with
    | Some [ i ] -> Ok ctx.Row.[i]
    | Some(_ :: _ :: _) -> Error(1052, sprintf "Column '%s' in %s is ambiguous" name (clauseLabel ctx.Clause))
    | Some [] | None ->
        match ctx.Outer with
        | Some parent -> resolveCol { parent with Clause = ctx.Clause } name
        | None -> Error(unknownColumn name)

/// The `QualifiedCol` counterpart of `resolveCol` — same outer-context
/// fallback, checked against `ctx.Qualifiers` instead of `ctx.ColumnIndex`.
let rec private resolveQualifiedCol (ctx: EvalContext) (table: string) (col: string) : Result<Value, EvalError> =
    match Map.tryFind (table.ToLowerInvariant()) ctx.Qualifiers with
    | Some(cols, offset) ->
        match cols |> List.tryFindIndex (fun c -> System.String.Equals(c.Name, col, System.StringComparison.OrdinalIgnoreCase)) with
        | Some idx -> Ok ctx.Row.[offset + idx]
        | None -> Error(unknownColumn (sprintf "%s.%s" table col))
    | None ->
        match ctx.Outer with
        | Some parent -> resolveQualifiedCol parent table col
        | None -> Error(unknownColumn (sprintf "%s.%s" table col))

/// Finds the declared column occupying one flattened row position. A JOIN
/// can expose the same physical range under more than one qualifier, but
/// every matching range carries the same ColumnDef, so the first is enough.
let private tryColumnDefAt (ctx: EvalContext) (index: int) : ColumnDef option =
    ctx.Qualifiers
    |> Map.toSeq
    |> Seq.tryPick (fun (_, (columns, offset)) ->
        let relative = index - offset
        if relative >= 0 && relative < columns.Length then Some columns.[relative] else None)

/// Recovers the declared column behind a bare/qualified expression without
/// changing expression evaluation itself. This type context is needed only
/// by ORDER BY: ENUM values are stored as their labels for display and
/// equality, but MySQL sorts them by declaration ordinal.
let rec private tryColumnDefForExpr (ctx: EvalContext) (expr: Expr) : ColumnDef option =
    match expr with
    | Col name ->
        match Map.tryFind (name.ToLowerInvariant()) ctx.ColumnIndex with
        | Some [ index ] -> tryColumnDefAt ctx index
        | Some(_ :: _ :: _)
        | Some [] -> None
        | None -> ctx.Outer |> Option.bind (fun outer -> tryColumnDefForExpr outer expr)
    | QualifiedCol(table, col) ->
        match Map.tryFind (table.ToLowerInvariant()) ctx.Qualifiers with
        | Some(columns, _) ->
            columns
            |> List.tryFind (fun column ->
                System.String.Equals(column.Name, col, System.StringComparison.OrdinalIgnoreCase))
        | None -> ctx.Outer |> Option.bind (fun outer -> tryColumnDefForExpr outer expr)
    | _ -> None

/// Converts a displayed ENUM label into its 1-based declaration ordinal
/// for one ORDER BY key. The original row/projection value remains a string;
/// only the private sort key changes. Invalid labels cannot normally reach
/// storage, but ordinal 0 mirrors MySQL's sentinel ordering if one does.
let private orderValueForExpr (ctx: EvalContext) (expr: Expr) (value: Value) : Value =
    match tryColumnDefForExpr ctx expr, value with
    | Some { Type = TEnum declared }, VString label ->
        declared
        |> List.tryFindIndex (fun item -> System.String.Equals(item, label, System.StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun index -> VInt(int64 (index + 1)))
        |> Option.defaultValue (VInt 0L)
    | _ -> value

/// `Star(Some qualifier)` (`t.*`) resolution — same shape as
/// `resolveQualifiedCol`, but hands back every one of that qualifier's own
/// `(name, value)` pairs instead of a single column, so a `JOIN`'s `t.*`
/// expands to just `t`'s own columns rather than every joined table's
/// columns concatenated (which is what `evalProjection`'s unqualified
/// `Star None` case still means).
let rec private resolveStarQualifier (ctx: EvalContext) (qualifier: string) : Result<(string * Value) list, EvalError> =
    match Map.tryFind (qualifier.ToLowerInvariant()) ctx.Qualifiers with
    | Some(cols, offset) -> Ok(cols |> List.mapi (fun i c -> c.Name, ctx.Row.[offset + i]))
    | None ->
        match ctx.Outer with
        | Some parent -> resolveStarQualifier parent qualifier
        | None -> Error(unknownColumn (sprintf "%s.*" qualifier))

/// Evaluates one expression against one row. Three-valued logic throughout
/// (comparisons/AND/OR/NOT return `VNull` — SQL's "unknown" — rather than a
/// boolean whenever an operand is `VNull`, per `Value`'s helpers), function
/// calls resolve through `ctx.Registry` (error 1305 if unregistered), and a
/// bare column resolves through `ctx.ColumnIndex` (error 1054 if unknown).
let rec private evalExpr (ctx: EvalContext) (expr: Expr) : Result<Value, EvalError> =
    let eval = evalExpr ctx

    match expr with
    | Lit v -> Ok v
    | Star _ -> Error(1054, "Invalid use of '*'")
    // Only reachable if a `RowNumberOver`/`LagOver` ever escapes
    // `runWindowedSelect`'s rewrite (which substitutes every occurrence,
    // wherever it's nested, for a plain `Col` reference before any of this
    // runs) — real MySQL itself rejects a window function outside a
    // `SELECT`'s own projection/`ORDER BY` list the same way.
    | RowNumberOver _
    | LagOver _ -> Error(1054, "Invalid use of a group function")
    | Col name -> resolveCol ctx name
    | QualifiedCol(table, col) -> resolveQualifiedCol ctx table col
    | Not e -> eval e |> Result.map (fun v -> truthy v |> Option.map (not >> boolToValue) |> Option.defaultValue VNull)
    | IsNull e -> eval e |> Result.map (function VNull -> VInt 1L | _ -> VInt 0L)
    | IsNotNull e -> eval e |> Result.map (function VNull -> VInt 0L | _ -> VInt 1L)
    | IsTrue e -> eval e |> Result.map (fun v -> boolToValue (truthy v = Some true))
    | IsFalse e -> eval e |> Result.map (fun v -> boolToValue (truthy v = Some false))
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
                // `datetime_expr +/- INTERVAL n unit` parses to a plain
                // `BinOp`, same as `1 + 2` — `vb` here is `INTERVAL`'s own
                // encoded marker value (see `Functions.intervalFn`), so it
                // needs the same real date arithmetic `DATE_ADD`/`DATE_SUB`
                // give it rather than falling into generic numeric add/sub.
                | Add when isIntervalValue vb -> tryDateIntervalBinOp 1.0 va vb |> Option.defaultValue (Value.add va vb)
                | Sub when isIntervalValue vb -> tryDateIntervalBinOp -1.0 va vb |> Option.defaultValue (Value.sub va vb)
                | Add -> Value.add va vb
                | Sub -> Value.sub va vb
                | Mul -> Value.mul va vb
                | Div -> Value.div va vb
                | Eq -> compareWith (fun c -> c = 0)
                | Neq -> compareWith (fun c -> c <> 0)
                | Lt -> compareWith (fun c -> c < 0)
                | Lte -> compareWith (fun c -> c <= 0)
                | Gt -> compareWith (fun c -> c > 0)
                | Gte -> compareWith (fun c -> c >= 0)
                // Never unknown, unlike every other comparison here: both
                // sides `NULL` is true, either side (but not both) `NULL` is
                // false, otherwise it's a plain `Eq`.
                | NullSafeEq ->
                    match va, vb with
                    | VNull, VNull -> VInt 1L
                    | VNull, _
                    | _, VNull -> VInt 0L
                    | _ -> boolToValue (Value.compare va vb = 0)))
    | Like(e, p, caseSensitive) ->
        eval e
        |> Result.bind (fun ve -> eval p |> Result.map (fun vp -> likeOp caseSensitive ve vp))
    | Regexp(e, p) ->
        eval e
        |> Result.bind (fun ve -> eval p |> Result.map (fun vp -> regexpOp ve vp))
    | In(e, xs) ->
        eval e
        |> Result.bind (fun ve ->
            match ve with
            | VNull -> Ok VNull
            | _ ->
                xs
                |> traverse eval
                |> Result.map (fun vs ->
                    if vs |> List.exists (fun v -> Value.equals ve v = Some true) then
                        VInt 1L
                    elif vs |> List.exists (function VNull -> true | _ -> false) then
                        VNull
                    else
                        VInt 0L))
    | InSubquery(e, select) ->
        eval e
        |> Result.bind (fun ve ->
            match ve with
            | VNull -> Ok VNull
            | _ ->
                match runSelectStmt ctx.Store ctx.Registry ctx.DbName select (Some ctx) with
                | Err(code, message), _, _ -> Error(code, message)
                | Affected _, _, _ -> Ok VNull
                | ResultSet(_, _), _, typedRows ->
                    // The candidate set is the subquery's first column —
                    // real MySQL requires exactly one, but ponytail: not
                    // enforced here (extra columns are just ignored) rather
                    // than adding an 1241-style check that `Subquery`
                    // already has to have for its own single-value case;
                    // add it here too if a migration's `IN (SELECT a, b
                    // ...)` ever needs the real error instead of silently
                    // matching on `a`. Reads the subquery's own typed
                    // `Value`, not its re-wrapped-as-text `VString` — see
                    // the note on `deriveRows`/`runSelectStmt`'s typed
                    // third component.
                    let candidates = typedRows |> List.map (fun row -> if row.Length > 0 then row.[0] else VNull)

                    if candidates |> List.exists (fun v -> Value.equals ve v = Some true) then
                        Ok(VInt 1L)
                    elif candidates |> List.exists (function VNull -> true | _ -> false) then
                        Ok VNull
                    else
                        Ok(VInt 0L))
    | Distinct e
    | OrderBy(e, _) -> eval e
    | Case(subject, whens, elseBranch) ->
        let fallback () =
            match elseBranch with
            | Some e -> eval e
            | None -> Ok VNull

        match subject with
        | Some se ->
            eval se
            |> Result.bind (fun sv ->
                let rec tryWhens =
                    function
                    | [] -> fallback ()
                    | (whenExpr, resExpr) :: rest ->
                        eval whenExpr
                        |> Result.bind (fun wv -> if Value.equals sv wv = Some true then eval resExpr else tryWhens rest)

                tryWhens whens)
        | None ->
            let rec tryWhens =
                function
                | [] -> fallback ()
                | (condExpr, resExpr) :: rest ->
                    eval condExpr
                    |> Result.bind (fun cv -> if truthy cv = Some true then eval resExpr else tryWhens rest)

            tryWhens whens
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
        | Some fn -> args |> traverse eval |> Result.map fn
    | Cast(e, ty) ->
        eval e
        |> Result.bind (fun v ->
            // Reuses `Storage.coerceValue` against a throwaway column of the
            // cast's target type rather than a second coercion table, always
            // non-strict: MySQL's own CAST never raises 1366 for an
            // out-of-range/unparseable conversion, independent of
            // STRICT_TRANS_TABLES — `CAST('abc' AS SIGNED)` is `0` (with a
            // warning), `CAST('abc' AS DATE)` is `NULL`, under the session's
            // *default* strict sql_mode included. A numeric target still
            // needs its own leading-numeric-prefix parse first
            // (`leadingNumericPrefix`): `coerceValue`'s own `parseNumeric`
            // requires the *whole* string to parse, so `CAST('12abc' AS
            // SIGNED)` (MySQL: `12`) would otherwise fall all the way to the
            // non-strict `0` fallback instead of `12`.
            let castCol: ColumnDef =
                { Name = "CAST"
                  Type = ty
                  Nullable = true
                  Default = None
                  AutoIncrement = false
                  PrimaryKey = false
                  Unique = false
                  Generated = None }

            let v =
                match v, ty with
                | VString s, (TTinyInt _ | TSmallInt _ | TMediumInt _ | TInt _ | TBigInt _ | TYear) ->
                    VString(leadingNumericPrefix leadingIntegerPrefixRegex s |> Option.defaultValue "")
                | VString s, (TDouble | TFloat | TDecimal _) ->
                    VString(leadingNumericPrefix leadingFloatPrefixRegex s |> Option.defaultValue "")
                | _ -> v

            match Storage.coerceValue false castCol v with
            | Ok v' -> Ok v'
            | Error err -> Error(Storage.toMySqlError err))
    | Exists select ->
        match runSelectStmt ctx.Store ctx.Registry ctx.DbName select (Some ctx) with
        | ResultSet(_, rows), _, _ -> Ok(boolToValue (not (List.isEmpty rows)))
        | Err(code, message), _, _ -> Error(code, message)
        // A `SELECT` under `EXISTS (...)` is never an `INSERT`/`UPDATE`/
        // `DELETE` (the parser's `selectStmtRecord` only builds `SelectStmt`
        // records, nothing else reaches here), so `Affected` can't occur.
        | Affected _, _, _ -> Ok VNull
    | Subquery select ->
        // Reads the subquery's own typed `Value`, not a `VString` re-wrap
        // of its text resultset — a bare-text round trip would make e.g.
        // `(SELECT MAX(n) FROM t) > (SELECT MIN(n) FROM t)` compare
        // lexicographically instead of numerically.
        match runSelectStmt ctx.Store ctx.Registry ctx.DbName select (Some ctx) with
        | Err(code, message), _, _ -> Error(code, message)
        | Affected _, _, _ -> Ok VNull
        | ResultSet(cols, _), _, _ when List.length cols <> 1 -> Error(1241, "Operand should contain 1 column(s)")
        | ResultSet(_, []), _, _ -> Ok VNull
        | ResultSet(_, [ _ ]), _, [ row ] -> Ok row.[0]
        | ResultSet(_, _), _, _ -> Error(1242, "Subquery returns more than 1 row")

/// Evaluates an ORDER BY expression and applies the column-type-specific
/// sort representation. Expressions such as CAST(enum_col AS CHAR) remain
/// ordinary lexical strings; only a direct ENUM column reference uses its
/// declaration ordinal, matching MySQL.
and private evalOrderKey (ctx: EvalContext) (expr: Expr) : Result<Value, EvalError> =
    let orderCtx = { ctx with Clause = OrderClause }
    evalExpr orderCtx expr |> Result.map (orderValueForExpr orderCtx expr)

/// Resolves one `TableRef` (a real table, or `information_schema`'s virtual
/// one) to its columns and rows — the one place both the base `FROM` and
/// every `JOIN` target resolve through, so there's exactly one
/// `information_schema` special case rather than one per call site.
and private resolveTableRef (store: Store) (dbName: string) (tableRef: TableRef) : Result<ColumnDef list * Value[] list, QueryResult> =
    let tableDb = tableRef.Database |> Option.defaultValue dbName

    if System.String.Equals(tableDb, "information_schema", System.StringComparison.OrdinalIgnoreCase) then
        match InformationSchema.scan store.Catalog tableRef.Table with
        | Some(columns, rows) -> Ok(columns, rows)
        | None -> Error(storageErr (NoSuchTable tableRef.Table))
    else
        match scan store tableDb tableRef.Table with
        | Error e -> Error(storageErr e)
        | Ok(columns, rows) -> Ok(columns, List.ofSeq rows)

/// Synthetic, all-nullable-text `ColumnDef`s for a derived table's columns —
/// `runSelectStmt`'s own resultset has no real per-column `ColumnType` to
/// recover (its `byte list` is the MySQL *wire* type, not an `Ast`
/// `ColumnType`); `TText` is close enough since every consumer (comparisons,
/// `Value.compare`, ...) already coerces through `toText`/`toDouble` rather
/// than trusting the declared type. The *rows* underneath these synthetic
/// columns are still `runSelectStmt`'s real typed `Value`s, though — see
/// `resolveFromItem` below — only the column metadata is synthesized.
and private deriveColumns (names: string list) : ColumnDef list =
    names
    |> List.map (fun n ->
        { Name = n
          Type = TText
          Nullable = true
          Default = None
          AutoIncrement = false
          PrimaryKey = false
          Unique = false
          Generated = None })

/// Resolves one `FromItem` — a real/virtual table via `resolveTableRef`, or
/// a derived table by running its subquery (uncorrelated: a plain derived
/// table can't see the outer query's columns, only `LATERAL` ones could,
/// which this engine doesn't support) and using its typed rows directly
/// (`runSelectStmt`'s third component — see its doc) under synthetic
/// `deriveColumns` column metadata, so e.g. `SELECT MAX(y.n) FROM (SELECT n
/// FROM t) y` still compares `y.n` numerically instead of falling back to a
/// lexicographic `VString` comparison.
and private resolveFromItem (store: Store) (registry: Registry) (dbName: string) (item: FromItem) : Result<ColumnDef list * Value[] list, QueryResult> =
    match item with
    | FromTable tableRef -> resolveTableRef store dbName tableRef
    | FromSubquery(body, _alias) ->
        let result, _, typedRows =
            match body with
            | PlainSelect select -> runSelectStmt store registry dbName select None
            | UnionSelect(first, rest, orderBy, limit, offset) -> runUnionStmt store registry dbName first rest orderBy limit offset

        match result with
        | ResultSet(cols, _) -> Ok(deriveColumns cols, typedRows)
        | Err(code, message) -> Error(Err(code, message))
        | Affected _ -> Error(Err(1064, "derived table did not return a resultset"))

/// The qualifier a `FROM`/`JOIN` source's columns resolve `qualifier.col`
/// against: a real table's alias (or its own name), or a derived table's
/// mandatory alias.
and private fromItemQualifier (item: FromItem) : string =
    match item with
    | FromTable t -> t.Alias |> Option.defaultValue t.Table
    | FromSubquery(_, alias) -> alias

/// `EvalContext.Qualifiers` for every source (the `FROM` table, and each
/// `JOIN` after it) already resolved into `sources`, ordered the same
/// left-to-right way their columns are laid out in a combined row —
/// offsets accumulate across the fold, so source *n*'s columns start right
/// where source *n-1*'s end.
and private qualifierRanges (sources: (string * ColumnDef list) list) : Map<string, ColumnDef list * int> =
    sources
    |> List.fold (fun (offset, quals) (qualifier, cols) -> offset + List.length cols, Map.add (qualifier.ToLowerInvariant()) (cols, offset) quals) (0, Map.empty)
    |> snd

/// The one `WHERE` predicate every scan and mutation path shares — no
/// clause matches everything, otherwise SQL's three-valued truthiness
/// (`evalExpr` against the row under `WhereClause`, `NULL`/`false` both
/// meaning "no match", only an explicit `true` keeping the row).
and private whereMatches (ctxFor: Value[] -> EvalContext) (where: Expr option) (row: Value[]) : Result<bool, EvalError> =
    match where with
    | None -> Ok true
    | Some expr -> evalExpr { ctxFor row with Clause = WhereClause } expr |> Result.map (fun v -> truthy v = Some true)

/// Applies one `JOIN` clause against whatever's already in scope
/// (`sourcesSoFar`/`rowsSoFar`, built by the `FROM` table and any earlier
/// `JOIN`s in the same list): resolves the joined table, evaluates `join.On`
/// against every (left row, right row) pair, then combines the matched pairs
/// with whatever `join.Kind` needs added on top — `LEFT`/`RIGHT` also keep
/// the side that matched nothing, `NULL`-padded on the other side; `INNER`
/// and `CROSS` (the latter's `On` is always the literal-true `join.On` the
/// parser gives it) keep only the matches. A qualified integer equality
/// predicate uses a right-side hash lookup; other predicates retain the
/// general nested-loop evaluator. Indices (not row references)
/// track which left/right rows matched anything, so outer-join padding is
/// correct even if two rows happen to be structurally equal.
and private applyJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (outer: EvalContext option)
    ((sourcesSoFar, rowsSoFar): (string * ColumnDef list) list * Value[] list)
    (join: Join)
    : Result<(string * ColumnDef list) list * Value[] list, QueryResult> =
    match resolveFromItem store registry dbName join.Table with
    | Error e -> Error e
    | Ok(joinColumns, joinRows) ->
        let joinQualifier = fromItemQualifier join.Table
        let newSources = sourcesSoFar @ [ joinQualifier, joinColumns ]
        let qualifiers = qualifierRanges newSources
        let combinedColumnsSoFar = sourcesSoFar |> List.collect snd
        let leftNullPadding = combinedColumnsSoFar |> List.map (fun _ -> VNull) |> Array.ofList
        let rightNullPadding = joinColumns |> List.map (fun _ -> VNull) |> Array.ofList

        let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumnsSoFar @ joinColumns)) qualifiers outer

        let leftIndexed = rowsSoFar |> List.indexed
        let rightIndexed = joinRows |> List.indexed

        let isIntegerType =
            function
            | TTinyInt _
            | TSmallInt _
            | TMediumInt _
            | TInt _
            | TBigInt _
            | TYear -> true
            | _ -> false

        let resolveQualified (qualifier: string) (column: string) =
            qualifiers
            |> Map.tryFind (qualifier.ToLowerInvariant())
            |> Option.bind (fun (columns, offset) ->
                columns
                |> List.tryFindIndex (fun definition -> System.String.Equals(definition.Name, column, System.StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun index -> offset + index, columns.[index].Type))

        let tryIntegerEqualityKeys =
            let tryPair left right =
                match left, right with
                | QualifiedCol(leftQualifier, leftColumn), QualifiedCol(rightQualifier, rightColumn) ->
                    match resolveQualified leftQualifier leftColumn, resolveQualified rightQualifier rightColumn with
                    | Some(leftIndex, leftType), Some(rightIndex, rightType)
                        when leftIndex < combinedColumnsSoFar.Length
                             && rightIndex >= combinedColumnsSoFar.Length
                             && isIntegerType leftType
                             && isIntegerType rightType ->
                        Some(leftIndex, rightIndex - combinedColumnsSoFar.Length)
                    | Some(rightIndex, rightType), Some(leftIndex, leftType)
                        when leftIndex < combinedColumnsSoFar.Length
                             && rightIndex >= combinedColumnsSoFar.Length
                             && isIntegerType leftType
                             && isIntegerType rightType ->
                        Some(leftIndex, rightIndex - combinedColumnsSoFar.Length)
                    | _ -> None
                | _ -> None

            match join.On with
            | BinOp(Eq, left, right) -> tryPair left right
            | _ -> None

        let buildCombinedRows (flagged: (int * int * bool * Value[]) list) =
            let matched = flagged |> List.filter (fun (_, _, ok, _) -> ok)
            let matchedCombined = matched |> List.map (fun (_, _, _, c) -> c)
            let matchedLeft = matched |> List.map (fun (li, _, _, _) -> li) |> Set.ofList
            let matchedRight = matched |> List.map (fun (_, ri, _, _) -> ri) |> Set.ofList

            let leftOnly =
                leftIndexed
                |> List.filter (fst >> matchedLeft.Contains >> not)
                |> List.map (fun (_, l) -> Array.append l rightNullPadding)

            let rightOnly =
                rightIndexed
                |> List.filter (fst >> matchedRight.Contains >> not)
                |> List.map (fun (_, r) -> Array.append leftNullPadding r)

            let combinedRows =
                match join.Kind with
                | InnerJoin
                | CrossJoin -> matchedCombined
                | LeftJoin -> matchedCombined @ leftOnly
                | RightJoin -> matchedCombined @ rightOnly

            newSources, combinedRows

        match tryIntegerEqualityKeys with
        | Some(leftKeyIndex, rightKeyIndex)
            when rowsSoFar
                 |> List.forall (fun row -> row.[leftKeyIndex] = VNull || match row.[leftKeyIndex] with VInt _ -> true | _ -> false)
                 && joinRows
                    |> List.forall (fun row -> row.[rightKeyIndex] = VNull || match row.[rightKeyIndex] with VInt _ -> true | _ -> false) ->
            let rightByKey = Dictionary<int64, ResizeArray<int * Value[]>>()

            for rightIndex, rightRow in rightIndexed do
                match rightRow.[rightKeyIndex] with
                | VInt key ->
                    match rightByKey.TryGetValue key with
                    | true, matches -> matches.Add(rightIndex, rightRow)
                    | false, _ ->
                        let matches = ResizeArray<int * Value[]>()
                        matches.Add(rightIndex, rightRow)
                        rightByKey.Add(key, matches)
                | _ -> ()

            let flagged = ResizeArray<int * int * bool * Value[]>()

            for leftIndex, leftRow in leftIndexed do
                match leftRow.[leftKeyIndex] with
                | VInt key ->
                    match rightByKey.TryGetValue key with
                    | true, matches ->
                        for rightIndex, rightRow in matches do
                            flagged.Add(leftIndex, rightIndex, true, Array.append leftRow rightRow)
                    | false, _ -> ()
                | _ -> ()

            Ok(buildCombinedRows (List.ofSeq flagged))
        | _ ->
            let pairs = [ for li, l in leftIndexed do for ri, r in rightIndexed -> li, ri, l, r ]

            pairs
            |> traverse (fun (li, ri, l, r) ->
                let combined = Array.append l r
                evalExpr { ctxFor combined with Clause = OnClause } join.On |> Result.map (fun v -> li, ri, (truthy v = Some true), combined))
            |> Result.mapError Err
            |> Result.map buildCombinedRows

/// Like `applyJoin`, but for a multi-table `UPDATE`/`DELETE ... JOIN`
/// rather than a `SELECT`: alongside the flattened row `evalExpr` needs,
/// each combined row also keeps every source's own physical `Value[]`
/// (`None` on an outer-join side that matched nothing — there's no real row
/// there to update/delete). A separate, smaller nested-loop join from
/// `applyJoin` rather than threading identity through the shared `SELECT`
/// path — that path is the hot, heavily-tested read path; duplicating the
/// (much smaller) join loop here keeps it untouched. ponytail: real tables
/// only (no derived-table join source) — MySQL itself doesn't allow a
/// derived table as a multi-table `UPDATE`/`DELETE` target anyway; a
/// derived-table join *source* (`UPDATE t1 JOIN (SELECT ...) dt ON ...`)
/// is real MySQL syntax this rejects with 1064 rather than silently
/// mishandling, add it if a migration's `UPDATE`/`DELETE` actually needs one.
and private applyMutationJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    ((sourcesSoFar, rowsSoFar): (string * TableRef * ColumnDef list) list * (Value[] option list * Value[]) list)
    (join: Join)
    : Result<(string * TableRef * ColumnDef list) list * (Value[] option list * Value[]) list, QueryResult> =
    match join.Table with
    | FromSubquery _ ->
        Error(Err(1064, "a derived table (subquery) isn't supported as a multi-table UPDATE/DELETE JOIN source"))
    | FromTable tableRef ->
        match resolveTableRef store dbName tableRef with
        | Error e -> Error e
        | Ok(joinColumns, joinRows) ->
            let joinQualifier = tableRef.Alias |> Option.defaultValue tableRef.Table
            let newSources = sourcesSoFar @ [ joinQualifier, tableRef, joinColumns ]
            let qualifiers = qualifierRanges (newSources |> List.map (fun (q, _, c) -> q, c))
            let combinedColumnsSoFar = sourcesSoFar |> List.map (fun (_, _, c) -> c) |> List.collect id
            let leftFlatPadding = combinedColumnsSoFar |> List.map (fun _ -> VNull) |> Array.ofList
            let rightFlatPadding = joinColumns |> List.map (fun _ -> VNull) |> Array.ofList
            let leftIdentityPadding = sourcesSoFar |> List.map (fun _ -> None)

            let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumnsSoFar @ joinColumns)) qualifiers None

            let leftIndexed = rowsSoFar |> List.indexed
            let rightIndexed = joinRows |> List.indexed

            let pairs = [ for li, l in leftIndexed do for ri, r in rightIndexed -> li, ri, l, r ]

            pairs
            |> traverse (fun (li, ri, (lIdent, lFlat), r) ->
                let combinedFlat = Array.append lFlat r
                evalExpr { ctxFor combinedFlat with Clause = OnClause } join.On
                |> Result.map (fun v -> li, ri, (truthy v = Some true), (lIdent @ [ Some r ], combinedFlat)))
            |> Result.mapError Err
            |> Result.map (fun flagged ->
                let matched = flagged |> List.filter (fun (_, _, ok, _) -> ok)
                let matchedRows = matched |> List.map (fun (_, _, _, row) -> row)
                let matchedLeft = matched |> List.map (fun (li, _, _, _) -> li) |> Set.ofList
                let matchedRight = matched |> List.map (fun (_, ri, _, _) -> ri) |> Set.ofList

                let leftOnly =
                    leftIndexed
                    |> List.filter (fst >> matchedLeft.Contains >> not)
                    |> List.map (fun (_, (lIdent, lFlat)) -> lIdent @ [ None ], Array.append lFlat rightFlatPadding)

                let rightOnly =
                    rightIndexed
                    |> List.filter (fst >> matchedRight.Contains >> not)
                    |> List.map (fun (_, r) -> leftIdentityPadding @ [ Some r ], Array.append leftFlatPadding r)

                let rows =
                    match join.Kind with
                    | InnerJoin
                    | CrossJoin -> matchedRows
                    | LeftJoin -> matchedRows @ leftOnly
                    | RightJoin -> matchedRows @ rightOnly

                newSources, rows)

/// Resolves `from :: joins` into the same `(sources, rows)` shape
/// `applyMutationJoin` builds up — the multi-table `UPDATE`/`DELETE`
/// counterpart to `runSelectStmt`'s `FROM`/`JOIN` resolution.
and private runMutationJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (from: TableRef)
    (joins: Join list)
    : Result<(string * TableRef * ColumnDef list) list * (Value[] option list * Value[]) list, QueryResult> =
    match resolveTableRef store dbName from with
    | Error e -> Error e
    | Ok(cols, rows) ->
        let baseQualifier = from.Alias |> Option.defaultValue from.Table
        let initial = [ baseQualifier, from, cols ], (rows |> List.map (fun r -> [ Some r ], r))
        joins |> List.fold (fun acc j -> acc |> Result.bind (fun st -> applyMutationJoin store registry dbName st j)) (Ok initial)

/// Resolves a `SELECT`'s `FROM` (a real table, `information_schema`'s
/// virtual one, a derived table, or none) plus every `JOIN` after it, and
/// runs `select` against the combined result — the `Statement` case's
/// `Select` branch and every subquery form (`Exists`/`Subquery`/
/// `InSubquery`/a derived table's own `FROM`) all fund into this one place
/// rather than each re-implementing the join-materialization logic.
/// `outer` is `None` for a top-level statement and `Some` when this is
/// itself a subquery — see `EvalContext.Outer`. Its per-column MySQL wire
/// types (the `byte list`; see `columnTypesOf`) are only available here —
/// by the time a `SELECT`'s rows reach `execute`'s return value they're
/// already the wire's flat `string option list` text, with no `Value`
/// left to read a type off of — but this can't be `public` itself (`outer:
/// EvalContext option` would leak the `private` `EvalContext` type through
/// a public signature); `runTopLevelSelect` near `execute` below is the
/// type-preserving public entry point `QueryHandler` calls instead.
and private runSelectStmt
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * byte list * Value[] list =
    match select.From with
    | None -> runSelect store registry dbName [] Map.empty [ [||] ] select outer
    | Some fromItem ->
        match resolveFromItem store registry dbName fromItem with
        | Error e -> e, [], []
        | Ok(baseColumns, baseRows) ->
            let baseQualifier = fromItemQualifier fromItem

            let initial : Result<(string * ColumnDef list) list * Value[] list, QueryResult> = Ok([ baseQualifier, baseColumns ], baseRows)

            match select.Joins |> List.fold (fun acc join -> acc |> Result.bind (fun combined -> applyJoin store registry dbName outer combined join)) initial with
            | Error e -> e, [], []
            | Ok(sources, rows) -> runSelect store registry dbName (sources |> List.collect snd) (qualifierRanges sources) rows select outer

/// Per-column MySQL wire type for a freshly-projected resultset, read off
/// the first non-NULL `Value` in each column across `rows` — a plain
/// data-driven read of the same typed values the row already carries
/// (see `Value.mysqlTypeOf`), not a separate static type-inference pass,
/// so it's correct for a literal, a cast, or an aggregate the same way it
/// is for a bare column reference. Falls back to VAR_STRING for a column
/// that's NULL in every row (or there are no rows at all) — NULL
/// round-trips the same regardless of the declared type, so there's
/// nothing to lose by guessing wrong there.
and private columnTypesOf (colCount: int) (rows: (string * Value) list list) : byte list =
    [ for i in 0 .. colCount - 1 ->
          rows
          |> List.tryPick (fun row ->
              match snd row.[i] with
              | VNull -> None
              | v -> Some(Value.mysqlTypeOf v))
          |> Option.defaultValue Value.TypeVarString ]

and private applyLimitOffset (limit: int option) (offset: int option) (rows: 'a list) : 'a list =
    let afterOffset =
        match offset with
        | Some o -> rows |> List.skip (min o (List.length rows))
        | None -> rows

    match limit with
    | Some l -> afterOffset |> List.truncate (max 0 l)
    | None -> afterOffset

/// Compares two rows by their pre-evaluated `ORDER BY` keys: a total order
/// per key via `Value.compare` (NULLs first), folded left-to-right so the
/// first key that differs between two rows decides, later keys only
/// breaking ties. The one comparator every `ORDER BY` sort site (plain
/// `SELECT`, grouped, windowed, `UNION`) shares, instead of each carrying
/// its own copy of the same fold.
and private compareByOrderKeys (dirs: Direction list) (ka: Value list) (kb: Value list) : int =
    List.zip3 dirs ka kb
    |> List.fold
        (fun acc (dir, va, vb) ->
            if acc <> 0 then
                acc
            else
                match dir with
                | Asc -> Value.compare va vb
                | Desc -> -(Value.compare va vb))
        0

/// A `UNION` statement's combined resultset, plus its column types —
/// pulled out of `execute`'s `Union` arm (which just calls this and
/// discards the second half) so `QueryHandler.executeStatement` can call
/// it directly too for the wire-level types. Public (unlike
/// `runSelectStmt`): it has no `EvalContext` in its own signature, so
/// nothing stops `QueryHandler` from calling it straight instead of
/// through a `runTopLevelSelect`-style wrapper.
///
/// Each branch runs as an independent, uncorrelated `SELECT` (`outer =
/// None`) — `Union` only ever occurs as a top-level statement (see
/// `Ast.Union`'s doc), never nested inside another query's expression, so
/// there's no outer row to thread through. The combined resultset's
/// column types are just the first branch's — ponytail: real MySQL
/// reconciles each column's type across every branch (e.g. an INT column
/// unioned with a DECIMAL one comes back DECIMAL), this only ever reports
/// the first branch's types; add real reconciliation if a mixed-type
/// UNION ever needs it.
and runUnionStmt
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (first: SelectStmt)
    (rest: (bool * SelectStmt) list)
    (orderBy: OrderKey list)
    (limit: int option)
    (offset: int option)
    : QueryResult * byte list * Value[] list =
    let runBranch (select: SelectStmt) = runSelectStmt store registry dbName select None

    // Each branch's text row paired with its own typed row, kept aligned
    // through combining/deduping so the `ORDER BY` below can compare typed
    // values instead of re-wrapping the text back into a lexicographically-
    // comparing `VString` (`SELECT n FROM t UNION SELECT n FROM t ORDER BY
    // n` sorting "10" before "2" otherwise).
    let combine
        (acc: Result<string list * ((string option list) * Value[]) list, QueryResult>)
        (isAll: bool, select: SelectStmt)
        =
        acc
        |> Result.bind (fun (cols, rowsSoFar) ->
            match runBranch select with
            | Err(code, message), _, _ -> Error(Err(code, message))
            | Affected _, _, _ -> Error(Err(1064, "UNION branch did not return a resultset"))
            | ResultSet(branchCols, _), _, _ when List.length branchCols <> List.length cols ->
                Error(Err(1222, "The used SELECT statements have a different number of columns"))
            | ResultSet(_, branchRows), _, branchTyped ->
                let branchPaired = List.zip branchRows branchTyped
                let combined = if isAll then rowsSoFar @ branchPaired else (rowsSoFar @ branchPaired) |> List.distinctBy fst
                Ok(cols, combined))

    match runSelectStmt store registry dbName first None with
    | Err(code, message), _, _ -> Err(code, message), [], []
    | Affected _, _, _ -> Err(1064, "UNION branch did not return a resultset"), [], []
    | ResultSet(firstCols, firstRows), firstTypes, firstTyped ->
        match rest |> List.fold combine (Ok(firstCols, List.zip firstRows firstTyped)) with
        | Error e -> e, [], []
        | Ok(cols, allPaired) ->
            // `ORDER BY`/`LIMIT` on the combined result — same
            // alias/positional resolution as an ordinary `SELECT`, and now
            // the same typed `Value.compare` sort too, via each row's own
            // paired typed values rather than re-parsing its text.
            let projections = cols |> List.map (fun c -> Col c, None)
            let resolveOrder = resolvePositionalOrAlias projections

            let orderKeyOf (typedRow: Value[]) (expr: Expr) : Value =
                match resolveOrder expr with
                | Col name ->
                    match cols |> List.tryFindIndex (fun c -> System.String.Equals(c, name, System.StringComparison.OrdinalIgnoreCase)) with
                    | Some i when i < typedRow.Length -> typedRow.[i]
                    | _ -> VNull
                | _ -> VNull

            let sortedPaired =
                if orderBy.IsEmpty then
                    allPaired
                else
                    allPaired
                    |> List.sortWith (fun (_, ta) (_, tb) ->
                        compareByOrderKeys
                            (orderBy |> List.map snd)
                            (orderBy |> List.map (fst >> orderKeyOf ta))
                            (orderBy |> List.map (fst >> orderKeyOf tb)))

            let limitedPaired = sortedPaired |> applyLimitOffset limit offset
            ResultSet(cols, limitedPaired |> List.map fst), firstTypes, limitedPaired |> List.map snd

/// One projection's `(column name, value)` pairs — a list because `SELECT
/// *` expands to every column of the row.
and private evalProjection (ctx: EvalContext) (columns: ColumnDef list) (proj: Projection) : Result<(string * Value) list, EvalError> =
    match proj with
    | Star None, _ -> Ok(columns |> List.mapi (fun i c -> c.Name, ctx.Row.[i]))
    | Star(Some qualifier), _ -> resolveStarQualifier ctx qualifier
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
    let isGroupConcat = System.String.Equals(name, "GROUP_CONCAT", System.StringComparison.OrdinalIgnoreCase)

    // `COUNT(DISTINCT x)`/`SUM(DISTINCT x)`/... all unwrap the same way:
    // dedupe the per-row values (after dropping `NULL`s) before folding,
    // regardless of which aggregate wraps the `DISTINCT`.
    let unwrapDistinct =
        function
        | Distinct e -> true, e
        | e -> false, e

    let evalNonNull (expr: Expr) : Result<Value list, EvalError> =
        rows |> traverse (fun row -> evalExpr (ctxFor row) expr) |> Result.map (List.filter (function VNull -> false | _ -> true))

    match args with
    | [ Star _ ] when isCount -> Ok(VInt(int64 (List.length rows)))
    | arg :: rest when isGroupConcat ->
        // `GROUP_CONCAT` folds entirely here rather than through
        // `registry.Aggregates` — see `isAggregateCall`'s doc. `rest` holds
        // zero or more `OrderBy` markers (see `Parser.groupConcatAtom`)
        // followed by an optional `SEPARATOR` literal.
        let distinct, innerExpr = unwrapDistinct arg

        let orderKeys = rest |> List.choose (function OrderBy(e, d) -> Some(e, d) | _ -> None)

        let separator =
            match rest |> List.tryPick (function Lit(VString s) -> Some s | _ -> None) with
            | Some s -> s
            | None -> ","

        let evalRow (row: Value[]) : Result<(Value * Value list) option, EvalError> =
            let ctx = ctxFor row
            evalExpr ctx innerExpr
            |> Result.bind (function
                | VNull -> Ok None
                | v -> orderKeys |> traverse (fst >> evalOrderKey ctx) |> Result.map (fun keys -> Some(v, keys)))

        rows
        |> traverse evalRow
        |> Result.map (fun results ->
            let present = results |> List.choose id
            let ordered =
                if orderKeys.IsEmpty then
                    present
                else
                    present |> List.sortWith (fun (_, ka) (_, kb) -> compareByOrderKeys (List.map snd orderKeys) ka kb)
            let deduped = if distinct then List.distinctBy fst ordered else ordered

            if deduped.IsEmpty then
                VNull
            else
                deduped |> List.map (fst >> toText >> Option.defaultValue "") |> String.concat separator |> VString)
    | [ arg ] ->
        let distinct, innerExpr = unwrapDistinct arg

        match Functions.lookupAggregate name registry with
        | None -> Error(unknownFunction name)
        | Some fold ->
            evalNonNull innerExpr
            |> Result.map (fun nonNull ->
                let deduped = if distinct then List.distinct nonNull else nonNull
                if isCount || not deduped.IsEmpty then fold deduped else VNull)
    | Distinct firstExpr :: rest when isCount ->
        // `COUNT(DISTINCT a, b)` — `distinctArg` (the call-argument parser)
        // attaches `Distinct` only to the first comma-separated argument,
        // but MySQL's `DISTINCT` here scopes over the whole tuple `(a, b)`,
        // not just `a`. Evaluate every argument per row, drop a row if
        // *any* column of it is NULL (SQL's usual "NULL drops the row from
        // an aggregate" rule, applied to the whole tuple), dedupe the
        // tuples, and count what's left.
        let allArgs = firstExpr :: rest

        rows
        |> traverse (fun row -> allArgs |> traverse (evalExpr (ctxFor row)))
        |> Result.map (fun tuples ->
            tuples
            |> List.filter (List.exists (function VNull -> true | _ -> false) >> not)
            |> List.distinct
            |> List.length
            |> int64
            |> VInt)
    // `isAggregateCall` (the only caller that routes here) already narrowed
    // to single-argument aggregate calls (`GROUP_CONCAT`'s optional
    // `SEPARATOR`, and now `COUNT(DISTINCT a, b)`, aside) — anything else
    // multi-argument (e.g. `SUM(DISTINCT a, b)`, which MySQL itself
    // rejects) is a syntax error, not a silent NULL.
    | _ -> Error(1064, sprintf "Incorrect parameter count in the call to native function '%s'" name)

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
    | FuncCall(name, args) -> args |> traverse sub |> Result.map (fun args' -> FuncCall(name, args'))
    | BinOp(op, a, b) -> sub a |> Result.bind (fun a' -> sub b |> Result.map (fun b' -> BinOp(op, a', b')))
    | Not e -> sub e |> Result.map Not
    | IsNull e -> sub e |> Result.map IsNull
    | IsNotNull e -> sub e |> Result.map IsNotNull
    | IsTrue e -> sub e |> Result.map IsTrue
    | IsFalse e -> sub e |> Result.map IsFalse
    | Distinct e -> sub e |> Result.map Distinct
    | OrderBy(e, dir) -> sub e |> Result.map (fun e' -> OrderBy(e', dir))
    | Like(e, p, cs) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Like(e', p', cs)))
    | Regexp(e, p) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Regexp(e', p')))
    | In(e, xs) -> sub e |> Result.bind (fun e' -> xs |> traverse sub |> Result.map (fun xs' -> In(e', xs')))
    | Between(e, lo, hi) ->
        sub e |> Result.bind (fun e' -> sub lo |> Result.bind (fun lo' -> sub hi |> Result.map (fun hi' -> Between(e', lo', hi'))))
    | Cast(e, ty) -> sub e |> Result.map (fun e' -> Cast(e', ty))
    | Case(subject, whens, elseBranch) ->
        let subOpt = function
            | Some e -> sub e |> Result.map Some
            | None -> Ok None

        subOpt subject
        |> Result.bind (fun subject' ->
            whens
            |> traverse (fun (c, r) -> sub c |> Result.bind (fun c' -> sub r |> Result.map (fun r' -> c', r')))
            |> Result.bind (fun whens' -> subOpt elseBranch |> Result.map (fun else' -> Case(subject', whens', else'))))
    | Lit _
    | Col _
    | QualifiedCol _
    | Star _
    // A `RowNumberOver`/`LagOver` never reaches a grouped SELECT —
    // `runSelect` sends any select with one to `runWindowedSelect` before
    // the GROUP BY/aggregate check that would otherwise land here even gets
    // evaluated (see `runSelect`'s dispatch) — but a leaf passthrough here
    // is the same "nothing to pre-evaluate" answer `Star`'s already is if
    // that ever changes.
    | RowNumberOver _
    | LagOver _
    // A subquery is its own scope with its own grouping — nothing inside it
    // is one of *this* query's aggregate calls to pre-evaluate, even though
    // (via `EvalContext.Outer`) it can still read this query's columns.
    | Exists _
    | Subquery _
    | InSubquery _ -> Ok expr

/// Resolves an `ORDER BY`/`GROUP BY` key that names a `SELECT ... AS alias`
/// (`... ORDER BY n`) or a 1-based projection position (`... ORDER BY 2`,
/// `GROUP BY 1`) against `projections`, falling back to the expression
/// as-is for anything else (an ordinary column, or an expression that just
/// happens not to match any alias).
and private resolvePositionalOrAlias (projections: Projection list) (expr: Expr) : Expr =
    match expr with
    | Lit(VInt n) when n >= 1L && n <= int64 (List.length projections) -> fst projections.[int n - 1]
    | Col name ->
        projections
        |> List.tryPick (function
            | e, Some alias when System.String.Equals(alias, name, System.StringComparison.OrdinalIgnoreCase) -> Some e
            | _ -> None)
        |> Option.defaultValue expr
    | _ -> expr

/// `resolvePositionalOrAlias`'s recursive counterpart — `GROUP BY`/`ORDER
/// BY` keys are almost always a bare alias/column/position at the top
/// level, but `HAVING`'s condition is a full boolean expression with the
/// alias nested somewhere inside it (`HAVING c > 1`, not just `HAVING c`),
/// so a shallow top-level check misses it entirely: `Col "c"` there isn't a
/// real column at all, only the `SELECT` list's own alias, and evaluating
/// it unresolved fails with 1054.
///
/// GROUP BY / HAVING's own column-name priority — the mirror image of
/// ORDER BY's (see `resolveOrderKey`'s doc): a bare name is checked against
/// the FROM-table columns first, and only falls back to the SELECT list's
/// own alias/position when it isn't a FROM-table column at all (real MySQL
/// documents this FROM-first order for GROUP BY/HAVING). A FROM-table match
/// present in more than one joined table is error 1052 "group statement",
/// same wording for both clauses.
and private resolveGroupOrHavingCol (columnIndex: Map<string, int list>) (projections: Projection list) (name: string) : Result<Expr, EvalError> =
    match Map.tryFind (name.ToLowerInvariant()) columnIndex with
    | Some [ _ ] -> Ok(Col name)
    | Some(_ :: _ :: _) -> Error(1052, sprintf "Column '%s' in group statement is ambiguous" name)
    | Some [] | None -> Ok(resolvePositionalOrAlias projections (Col name))

/// `GROUP BY`'s key list: each key is a bare top-level expression, never
/// searched inside a larger tree (unlike `HAVING`'s condition — see
/// `resolveHavingRef`), so a `Col` only ever needs the shallow check above;
/// anything else (a position number, or an expression that's neither) goes
/// through `resolvePositionalOrAlias` unchanged.
and private resolveGroupByRef (columnIndex: Map<string, int list>) (projections: Projection list) (expr: Expr) : Result<Expr, EvalError> =
    match expr with
    | Col name -> resolveGroupOrHavingCol columnIndex projections name
    | _ -> Ok(resolvePositionalOrAlias projections expr)

/// `resolveGroupByRef`'s recursive counterpart for `HAVING`: `HAVING c > 1`'s
/// alias `c` is nested inside a `BinOp`, not bare, so a shallow top-level
/// check misses it. Same shape as `substituteValuesFunc`'s rewrite, but
/// `Result`-threaded since a `Col` can now fail with the ambiguous-FROM-table
/// 1052.
and private resolveHavingRef (columnIndex: Map<string, int list>) (projections: Projection list) (expr: Expr) : Result<Expr, EvalError> =
    let sub = resolveHavingRef columnIndex projections

    match expr with
    | Col name -> resolveGroupOrHavingCol columnIndex projections name
    | FuncCall(name, args) -> args |> traverse sub |> Result.map (fun args' -> FuncCall(name, args'))
    | BinOp(op, a, b) -> sub a |> Result.bind (fun a' -> sub b |> Result.map (fun b' -> BinOp(op, a', b')))
    | Not e -> sub e |> Result.map Not
    | IsNull e -> sub e |> Result.map IsNull
    | IsNotNull e -> sub e |> Result.map IsNotNull
    | IsTrue e -> sub e |> Result.map IsTrue
    | IsFalse e -> sub e |> Result.map IsFalse
    | Distinct e -> sub e |> Result.map Distinct
    | OrderBy(e, dir) -> sub e |> Result.map (fun e' -> OrderBy(e', dir))
    | Like(e, p, cs) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Like(e', p', cs)))
    | Regexp(e, p) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Regexp(e', p')))
    | In(e, xs) -> sub e |> Result.bind (fun e' -> xs |> traverse sub |> Result.map (fun xs' -> In(e', xs')))
    | Between(e, lo, hi) ->
        sub e |> Result.bind (fun e' -> sub lo |> Result.bind (fun lo' -> sub hi |> Result.map (fun hi' -> Between(e', lo', hi'))))
    | Cast(e, ty) -> sub e |> Result.map (fun e' -> Cast(e', ty))
    | Case(subject, whens, elseBranch) ->
        let subOpt =
            function
            | Some e -> sub e |> Result.map Some
            | None -> Ok None

        subOpt subject
        |> Result.bind (fun subject' ->
            whens
            |> traverse (fun (c, r) -> sub c |> Result.bind (fun c' -> sub r |> Result.map (fun r' -> c', r')))
            |> Result.bind (fun whens' -> subOpt elseBranch |> Result.map (fun else' -> Case(subject', whens', else'))))
    | Lit _
    | QualifiedCol _
    | Star _
    | RowNumberOver _
    | LagOver _
    // A subquery is its own scope — nothing inside it can be *this*
    // query's projection alias.
    | Exists _
    | Subquery _
    | InSubquery _ -> Ok expr

/// `ORDER BY`'s 1-based projection position (`ORDER BY 2`) — split out from
/// `resolvePositionalOrAlias` because ORDER BY's alias case now goes
/// through `resolveOrderKey`'s output-column matching instead (which needs
/// to see the ambiguous-alias case `resolvePositionalOrAlias`'s
/// first-match `tryPick` would otherwise hide).
and private resolveOrderPosition (projections: Projection list) (expr: Expr) : Expr =
    match expr with
    | Lit(VInt n) when n >= 1L && n <= int64 (List.length projections) -> fst projections.[int n - 1]
    | _ -> expr

/// `ORDER BY`'s alias-then-FROM-table priority (see `resolveGroupOrHavingCol`'s
/// doc for the opposite order GROUP BY/HAVING use): tries the bare name
/// against `outputCols` — the SELECT list's own output columns, explicit
/// aliases and every name `*`/`t.*` expanded into, in row order — first;
/// exactly one match binds directly to that column's already-computed
/// value, more than one is error 1052 "order clause", and zero falls
/// through to `resolveCol` against the FROM-table columns instead (also
/// tagged `OrderClause`, not `FieldList`).
and private resolveOrderKey
    (ctx: EvalContext)
    (projections: Projection list)
    (outputCols: (string * Value) list)
    (expr: Expr)
    : Result<Value, EvalError> =
    match expr with
    | Col name ->
        match outputCols |> List.filter (fun (n, _) -> System.String.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)) with
        | [ (_, v) ] ->
            // An output alias retains its source expression's declared
            // type for sorting (`SELECT role AS r ... ORDER BY r`). A
            // computed alias has no direct ENUM column and stays lexical.
            let sourceExpr =
                projections
                |> List.choose (fun (projectionExpr, alias) ->
                    alias
                    |> Option.filter (fun aliasName ->
                        System.String.Equals(aliasName, name, System.StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun _ -> projectionExpr))
                |> function
                    | [ projectionExpr ] -> projectionExpr
                    | _ -> expr

            Ok(orderValueForExpr { ctx with Clause = OrderClause } sourceExpr v)
        | _ :: _ :: _ -> Error(1052, sprintf "Column '%s' in order clause is ambiguous" name)
        | [] -> evalOrderKey ctx (Col name)
    | e -> evalOrderKey ctx e

/// The `GROUP BY`/aggregate path: `select.GroupBy` (resolved through
/// `resolvePositionalOrAlias` for positional/alias references first)
/// partitions the `WHERE`-filtered rows into groups — structural equality on
/// each row's evaluated `Value list` key is already exactly SQL's "NULLs
/// group together" rule, so no custom comparer is needed — and an empty
/// `GroupBy` collapses everything into one synthetic group (even an empty
/// one, so `SELECT COUNT(*) FROM t` on an empty `t` still returns one row
/// with `0` rather than no rows, matching a real whole-table aggregate; a
/// real `GROUP BY` with nothing to group correctly produces zero rows
/// instead). Every projection/`HAVING`/`ORDER BY` expression runs through
/// `rewriteAggregates` per group before evaluating what's left against that
/// group's first row — MySQL's `ONLY_FULL_GROUP_BY`-off behavior for a bare
/// non-aggregated column, equivalent to wrapping it in `ANY_VALUE`.
/// Whether a `GROUP BY` key is a plain column list — real MySQL's own
/// "GROUP BY optimization using an index" (see `groupByIsIndexOrdered`
/// below) only ever applies to grouping by actual columns, never by an
/// expression.
and private groupByColumnNames (groupExprs: Expr list) : string list option =
    let asColumnName =
        function
        | Col name
        | QualifiedCol(_, name) -> Some name
        | _ -> None

    let names = groupExprs |> List.map asColumnName
    if names |> List.forall Option.isSome then Some(names |> List.map Option.get) else None

/// Column names pinned to a constant by a top-level `WHERE col = <literal>`
/// equality, recursing through `AND` — the only shape MySQL's own GROUP BY
/// index optimization looks for. An `OR`, a non-`=` comparison, or an
/// equality against anything but a literal doesn't pin its column, which
/// only ever makes `indexSortsGroupBy` below miss a real optimization
/// opportunity (fsdb stays unsorted where MySQL would've sorted), never
/// the other way around.
and private whereEqualityPinnedColumns (whereExpr: Expr option) : Set<string> =
    let rec walk expr acc =
        match expr with
        | BinOp(And, l, r) -> walk r (walk l acc)
        | BinOp(Eq, Col name, Lit _)
        | BinOp(Eq, Lit _, Col name)
        | BinOp(Eq, QualifiedCol(_, name), Lit _)
        | BinOp(Eq, Lit _, QualifiedCol(_, name)) -> Set.add (name.ToLowerInvariant()) acc
        | _ -> acc

    whereExpr |> Option.map (fun e -> walk e Set.empty) |> Option.defaultValue Set.empty

/// Whether `groupCols` (lowercased, in `GROUP BY` order) sits right after
/// `indexCols`' (lowercased, in index-declaration order) longest leading
/// run of columns every one of which is in `pinned` — mirrors real MySQL's
/// documented "GROUP BY optimization using an index": an equality
/// condition on every index column before the grouped ones lets an ordered
/// index/range scan feed rows already sorted by the group key, skipping
/// the temp-table pass that would otherwise dedupe them in whatever order
/// the table scan happened to visit them (fsdb's own first-occurrence
/// default, since it never does real index-accelerated access — see
/// `Storage.Table.Indexes`'s doc).
and private indexSortsGroupBy (pinned: Set<string>) (groupCols: string list) (indexCols: string list) : bool =
    let rec dropPinnedPrefix cols =
        match cols with
        | c :: rest when Set.contains c pinned -> dropPinnedPrefix rest
        | _ -> cols

    let remaining = indexCols |> List.map (fun c -> c.ToLowerInvariant()) |> dropPinnedPrefix

    List.length remaining >= List.length groupCols
    && List.truncate (List.length groupCols) remaining = groupCols

/// Whether a bare `GROUP BY` (no `ORDER BY` to override it) comes back
/// sorted by the group key ascending, the way real MySQL 8.4 does whenever
/// an index makes that free (`indexSortsGroupBy`) — checked once per query
/// rather than per group. Conservatively `false` (fsdb's oracle-verified
/// default: first-occurrence order) for anything this simple index-metadata
/// check can't answer: a join, a derived table, or a `GROUP BY` on
/// something other than a plain column list. Verified against real MySQL
/// 8.4: an unindexed key keeps first-occurrence order, a single-column
/// index leading with the group column (or a composite one, once a WHERE
/// equality pins every column ahead of it) sorts ascending.
and private groupByIsIndexOrdered (store: Store) (dbName: string) (select: SelectStmt) (groupExprs: Expr list) : bool =
    match select.From, select.Joins, groupByColumnNames groupExprs with
    | Some(FromTable tref), [], Some groupCols ->
        let groupColsLower = groupCols |> List.map (fun c -> c.ToLowerInvariant())
        let tableDb = tref.Database |> Option.defaultValue dbName

        match InformationSchema.findTable store.Catalog tableDb tref.Table with
        | Error _ -> false
        | Ok table ->
            let pinned = whereEqualityPinnedColumns select.Where
            let pkColumns = table.Columns |> List.filter (fun c -> c.PrimaryKey) |> List.map (fun c -> c.Name)

            (if pkColumns.IsEmpty then [] else [ pkColumns ]) @ (table.Indexes |> List.map (fun ix -> ix.Columns))
            |> List.exists (indexSortsGroupBy pinned groupColsLower)
    | _ -> false

and private runGroupedSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (qualifiers: Map<string, ColumnDef list * int>)
    (rows: Value[] list)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * byte list * Value[] list =
    let columnIndex = columnIndexOf columns

    let ctxFor = contextFactory store registry dbName columnIndex qualifiers outer

    let matches = whereMatches ctxFor select.Where

    let representativeOf (groupRows: Value[] list) : Value[] = groupRows |> List.tryHead |> Option.defaultValue (probeRow columns)

    let projectGroup (groupRows: Value[] list) : Result<(string * Value) list, EvalError> =
        let representative = representativeOf groupRows

        select.Projections
        |> traverse (fun (expr, aliasOpt) ->
            match expr with
            | Star None -> Ok(columns |> List.mapi (fun i c -> c.Name, representative.[i]))
            | Star(Some qualifier) -> resolveStarQualifier (ctxFor representative) qualifier
            | _ ->
                rewriteAggregates registry ctxFor groupRows expr
                |> Result.bind (evalExpr (ctxFor representative))
                |> Result.map (fun v -> [ aliasOpt |> Option.defaultValue (exprLabel expr), v ]))
        |> Result.map List.concat

    let havingOk (groupRows: Value[] list) : Result<bool, EvalError> =
        match select.Having with
        | None -> Ok true
        | Some h ->
            // `resolveHavingRef` resolves a `SELECT ... AS alias` anywhere
            // inside the condition (`HAVING`'s condition is a full boolean
            // expression, not just a bare alias — MySQL allows a projection
            // alias nested anywhere inside it, e.g. Eloquent's
            // `having('aggregate_alias', ...)`), FROM-table columns first.
            resolveHavingRef columnIndex select.Projections h
            |> Result.bind (rewriteAggregates registry ctxFor groupRows)
            |> Result.bind (evalExpr { ctxFor (representativeOf groupRows) with Clause = GroupStatement })
            |> Result.map (fun v -> truthy v = Some true)

    // ORDER BY's alias-first priority (the opposite of GROUP BY/HAVING's
    // FROM-first one — see `resolveOrderKey`'s doc) resolves against this
    // group's own already-projected output columns (`outputCols`, from
    // `projectGroup`) rather than the group's raw rows.
    let orderKeysOf (outputCols: (string * Value) list) (groupRows: Value[] list) : Result<Value list, EvalError> =
        let representative = representativeOf groupRows
        let ctx = ctxFor representative

        select.OrderBy
        |> traverse (fun (expr, _) ->
            match resolveOrderPosition select.Projections expr with
            | Col name ->
                match outputCols |> List.filter (fun (n, _) -> System.String.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)) with
                | [ (_, v) ] ->
                    let sourceExpr =
                        select.Projections
                        |> List.choose (fun (projectionExpr, alias) ->
                            alias
                            |> Option.filter (fun aliasName ->
                                System.String.Equals(aliasName, name, System.StringComparison.OrdinalIgnoreCase))
                            |> Option.map (fun _ -> projectionExpr))
                        |> function
                            | [ projectionExpr ] -> projectionExpr
                            | _ -> Col name

                    Ok(orderValueForExpr { ctx with Clause = OrderClause } sourceExpr v)
                | _ :: _ :: _ -> Error(1052, sprintf "Column '%s' in order clause is ambiguous" name)
                | [] ->
                    rewriteAggregates registry ctxFor groupRows (Col name)
                    |> Result.bind (evalOrderKey ctx)
            | e -> rewriteAggregates registry ctxFor groupRows e |> Result.bind (evalOrderKey ctx))

    // Schema probe: type-checks WHERE/GROUP BY/HAVING/ORDER BY/projections
    // against an all-NULL row first, the same reasoning as `probeRow`'s
    // other use — an unknown column/function is a schema error independent
    // of whether any row happens to match, or a real `GROUP BY` happens to
    // produce zero groups.
    match select.GroupBy |> traverse (resolveGroupByRef columnIndex select.Projections) with
    | Error(code, message) -> Err(code, message), [], []
    | Ok groupExprs ->

    match matches (probeRow columns)
          |> Result.bind (fun _ -> groupExprs |> traverse (evalExpr (ctxFor (probeRow columns))) |> Result.map ignore)
          |> Result.bind (fun _ -> havingOk [])
          |> Result.bind (fun _ -> projectGroup [])
          |> Result.bind (fun probeProjected -> orderKeysOf probeProjected [] |> Result.map (fun _ -> probeProjected)) with
    | Error(code, message) -> Err(code, message), [], []
    | Ok probeProjected ->
        let colNames = probeProjected |> List.map fst

        match rows |> traverse (fun row -> matches row |> Result.map (fun keep -> if keep then Some row else None)) with
        | Error(code, message) -> Err(code, message), [], []
        | Ok maybeMatched ->
            let matched = maybeMatched |> List.choose id

            // A bare `GROUP BY` with no `ORDER BY` isn't sorted by the SQL
            // standard, but real MySQL sorts by the group key ascending
            // whenever an index makes that free (see `groupByIsIndexOrdered`)
            // — checked once here rather than per group, since it only
            // depends on the query's shape, not any row's data.
            let indexOrdered =
                select.OrderBy.IsEmpty
                && not groupExprs.IsEmpty
                && groupByIsIndexOrdered store dbName select groupExprs

            // Each group carries its own key alongside its rows so the
            // `indexOrdered` branch below can sort by it; with an explicit
            // `ORDER BY`, or when `indexOrdered` is false, `orderKeysOf`'s
            // keys decide instead and this key goes unused.
            let buildGroups () : Result<(Value list * Value[] list) list, EvalError> =
                if groupExprs.IsEmpty then
                    Ok [ [], matched ]
                else
                    matched
                    |> traverse (fun row -> groupExprs |> traverse (evalExpr (ctxFor row)) |> Result.map (fun key -> key, row))
                    |> Result.map (fun keyed -> keyed |> List.groupBy fst |> List.map (fun (key, rows) -> key, rows |> List.map snd))

            match buildGroups () with
            | Error(code, message) -> Err(code, message), [], []
            | Ok groups ->
                let processGroup
                    (key: Value list, groupRows: Value[] list)
                    : Result<((string * Value) list * Value list * Value list) option, EvalError> =
                    havingOk groupRows
                    |> Result.bind (fun keep ->
                        if not keep then
                            Ok None
                        else
                            projectGroup groupRows
                            |> Result.bind (fun proj -> orderKeysOf proj groupRows |> Result.map (fun keys -> Some(proj, keys, key))))

                match groups |> traverse processGroup with
                | Error(code, message) -> Err(code, message), [], []
                | Ok maybeRows ->
                    let kept = maybeRows |> List.choose id

                    let sorted =
                        if indexOrdered then
                            kept
                            |> List.sortWith (fun (_, _, ka) (_, _, kb) ->
                                let probeCtx = ctxFor (probeRow columns)
                                let enumAware keys = List.map2 (orderValueForExpr probeCtx) groupExprs keys
                                compareByOrderKeys (groupExprs |> List.map (fun _ -> Asc)) (enumAware ka) (enumAware kb))
                        else
                            kept |> List.sortWith (fun (_, ka, _) (_, kb, _) -> compareByOrderKeys (List.map snd select.OrderBy) ka kb)

                    let paired =
                        sorted
                        |> List.map (fun (proj, _, _) -> proj |> List.map (snd >> toText), proj |> List.map snd |> Array.ofList)

                    let dedupedPaired = if select.Distinct then paired |> List.distinctBy fst else paired
                    let types = columnTypesOf (List.length colNames) (sorted |> List.map (fun (proj, _, _) -> proj))
                    let limited = dedupedPaired |> applyLimitOffset select.Limit select.Offset
                    ResultSet(colNames, limited |> List.map fst), types, limited |> List.map snd

/// `SELECT ..., ROW_NUMBER() OVER (...) | LAG(expr) OVER (...) [AS alias],
/// ... FROM ...` (see `Ast.Expr.RowNumberOver`/`LagOver`'s docs) — every
/// distinct window function `collectWindowFuncs` finds anywhere among the
/// projections is computed once here, against the WHERE-filtered rows (real
/// MySQL computes a window function after `WHERE`, before
/// `SELECT`/`ORDER BY`/`LIMIT`), then handed back to the ordinary
/// (non-windowed) `runSelect` path as one more real column each: a
/// synthetic trailing `ColumnDef` per window function, appended to
/// `columns`/each row, with every projection's `Star` expanded and every
/// window-function occurrence (bare, or nested inside arithmetic/`CASE`/...)
/// substituted for the matching synthetic `Col` reference — expanding `Star`
/// explicitly here (rather than leaving it for `runSelect`'s own `Star`
/// handling) keeps the synthetic columns out of a bare `SELECT *`'s
/// expansion, so they show up only where a window function itself was
/// written.
and private runWindowedSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (qualifiers: Map<string, ColumnDef list * int>)
    (rows: Value[] list)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * byte list * Value[] list =
    let windowFuncs = select.Projections |> List.collect (fst >> collectWindowFuncs) |> List.distinct

    if windowFuncs.IsEmpty then
        Err(1064, "runWindowedSelect called without a window-function projection"), [], []
    else

    let columnIndex = columnIndexOf columns
    let ctxFor = contextFactory store registry dbName columnIndex qualifiers outer
    let matches = whereMatches ctxFor select.Where

    match rows |> traverse (fun row -> matches row |> Result.map (fun keep -> if keep then Some row else None)) with
    | Error(code, message) -> Err(code, message), [], []
    | Ok maybeMatched ->
        let matched = maybeMatched |> List.choose id

        // One partitioned-and-ordered pass per distinct window function —
        // grouping by partition key preserves each group's original
        // relative order (`List.groupBy` is stable), which only matters as
        // a tiebreak among rows the window's own ORDER BY doesn't otherwise
        // distinguish.
        let computeColumn (windowFunc: Expr) : Result<Value[], EvalError> =
            let partitionBy, windowOrderBy =
                match windowFunc with
                | RowNumberOver(p, o) -> p, o
                | LagOver(_, _, p, o) -> p, o
                | _ -> [], []

            let keyOf (exprs: Expr list) (row: Value[]) : Result<Value list, EvalError> =
                exprs |> traverse (evalExpr (ctxFor row))

            let orderKeyOf (exprs: Expr list) (row: Value[]) : Result<Value list, EvalError> =
                exprs |> traverse (evalOrderKey (ctxFor row))

            matched
            |> traverse (fun row ->
                keyOf partitionBy row
                |> Result.bind (fun partKey ->
                    orderKeyOf (windowOrderBy |> List.map fst) row |> Result.map (fun ordKey -> partKey, ordKey, row)))
            |> Result.bind (fun keyed ->
                let partitions =
                    keyed
                    |> List.indexed
                    |> List.groupBy (fun (_, (partKey, _, _)) -> partKey)
                    |> List.map (fun (_, group) ->
                        group
                        |> List.sortWith (fun (_, (_, ka, _)) (_, (_, kb, _)) -> compareByOrderKeys (windowOrderBy |> List.map snd) ka kb)
                        |> Array.ofList)

                match windowFunc with
                | RowNumberOver _ ->
                    partitions
                    |> Array.ofList
                    |> Array.collect (Array.mapi (fun rank (origIdx, _) -> origIdx, VInt(int64 (rank + 1))))
                    |> Ok
                | LagOver(lagExpr, offset, _, _) ->
                    // `pos - offset` indexes back within the same
                    // partition's ORDER BY-sorted rows; before the
                    // partition's start (no such predecessor) is NULL, same
                    // as real MySQL's `LAG`.
                    partitions
                    |> traverse (fun group ->
                        group
                        |> Array.mapi (fun pos (origIdx, _) ->
                            let srcPos = pos - int offset

                            if srcPos < 0 then
                                Ok(origIdx, VNull)
                            else
                                let (_, (_, _, srcRow)) = group.[srcPos]
                                evalExpr (ctxFor srcRow) lagExpr |> Result.map (fun v -> origIdx, v))
                        |> Array.toList
                        |> traverse id)
                    |> Result.map (List.collect id >> Array.ofList)
                | _ -> Ok [||])
            |> Result.map (fun pairs ->
                let byIndex = pairs |> Array.toList |> Map.ofList
                matched |> List.mapi (fun i _ -> Map.find i byIndex) |> Array.ofList)

        match windowFuncs |> traverse computeColumn with
        | Error(code, message) -> Err(code, message), [], []
        | Ok computedColumns ->
            let synthetic =
                windowFuncs |> List.mapi (fun i wf -> wf, sprintf "__fsdb_window_%d__" i)

            let syntheticColumns =
                synthetic
                |> List.map (fun (wf, name) ->
                    { Name = name
                      // The row's actual `Value` (a real int for
                      // `RowNumberOver`, `lagExpr`'s own runtime type for
                      // `LagOver`) drives the wire type downstream (see
                      // `columnTypesOf`), so this declared type is never
                      // read for anything but `Nullable`.
                      Type = TBigInt false
                      Nullable = (match wf with LagOver _ -> true | _ -> false)
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None })

            let extendedColumns = columns @ syntheticColumns

            let extendedRows =
                matched
                |> List.mapi (fun idx row -> Array.append row (computedColumns |> List.map (fun col -> col.[idx]) |> Array.ofList))

            let rewriteProjection (expr: Expr, aliasOpt: string option) : (Expr * string option) list =
                match expr with
                | Star None -> columns |> List.map (fun c -> Col c.Name, None)
                | Star(Some qualifier) ->
                    match Map.tryFind (qualifier.ToLowerInvariant()) qualifiers with
                    | Some(cols, _) -> cols |> List.map (fun c -> Col c.Name, None)
                    | None -> [ expr, aliasOpt ]
                | _ ->
                    // A bare (unwrapped) window-function projection with no
                    // explicit alias defaults its label to the synthetic
                    // column's own name, same as before generalizing this
                    // to arbitrary nesting — anything wrapping one falls
                    // through to `runSelect`'s ordinary unaliased-label
                    // handling instead.
                    let alias =
                        aliasOpt
                        |> Option.orElse (synthetic |> List.tryFind (fun (wf, _) -> wf = expr) |> Option.map snd)

                    [ substituteWindowFuncs synthetic expr, alias ]

            let select' =
                { select with Projections = select.Projections |> List.collect rewriteProjection }

            runSelect store registry dbName extendedColumns qualifiers extendedRows select' outer

and private runSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (qualifiers: Map<string, ColumnDef list * int>)
    (rows: Value[] list)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * byte list * Value[] list =
    let projections, whereExpr, orderBy, limit, offset =
        select.Projections, select.Where, select.OrderBy, select.Limit, select.Offset

    // A `SELECT` with no `FROM` at all has no columns to expand `*`/`t.*`
    // against — real MySQL rejects it as 1096 rather than emitting a
    // resultset with zero columns, which isn't a legal text-resultset
    // packet and aborts the client's whole session.
    if select.From.IsNone && projections |> List.exists (fst >> function Star _ -> true | _ -> false) then
        Err(1096, "No tables used"), [], []
    elif projections |> List.exists (fst >> collectWindowFuncs >> List.isEmpty >> not) then
        runWindowedSelect store registry dbName columns qualifiers rows select outer
    elif
        not select.GroupBy.IsEmpty
        || select.Having.IsSome
        || projections |> List.exists (fst >> containsAggregate registry)
        || orderBy |> List.exists (fst >> containsAggregate registry)
    then
        runGroupedSelect store registry dbName columns qualifiers rows select outer
    else

    let columnIndex = columnIndexOf columns

    let ctxFor = contextFactory store registry dbName columnIndex qualifiers outer

    // ORDER BY may name a 1-based projection position (`ORDER BY 1`) —
    // resolve that first against the projection list; `resolveOrderKey`
    // below handles the alias/output-column case (and its `*`/`t.*`
    // expansion) itself.
    let resolveOrderExpr = resolveOrderPosition projections

    let matches = whereMatches ctxFor whereExpr

    let projectRow (row: Value[]) : Result<(string * Value) list, EvalError> =
        projections
        |> traverse (evalProjection (ctxFor row) columns)
        |> Result.map List.concat

    let orderKeys (row: Value[]) : Result<Value list, EvalError> =
        orderBy
        |> traverse (fun (expr, _) ->
            projectRow row
            |> Result.bind (fun outputCols -> resolveOrderKey (ctxFor row) projections outputCols (resolveOrderExpr expr)))

    let sortRows (keyed: (Value list * Value[]) list) : (Value list * Value[]) list =
        keyed |> List.sortWith (fun (ka, _) (kb, _) -> compareByOrderKeys (List.map snd orderBy) ka kb)

    let probe = probeRow columns

    match matches probe |> Result.bind (fun _ -> orderKeys probe) |> Result.bind (fun _ -> projectRow probe) with
    | Error(code, message) -> Err(code, message), [], []
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

        match rows |> traverse keepWithOrderKeys with
        | Error(code, message) -> Err(code, message), [], []
        | Ok maybeKeyed ->
            let keyed = maybeKeyed |> List.choose id

            // Projects every sorted row *before* LIMIT/OFFSET (rather than
            // after, as a non-DISTINCT `SELECT` could) so `DISTINCT` can
            // dedupe on the projected columns while still honoring ORDER
            // BY's row order (first occurrence wins) — deduping post-LIMIT
            // would undercount, and deduping on the raw pre-projection row
            // would miss two source rows that only agree on the columns
            // actually selected.
            match keyed |> sortRows |> List.map snd |> traverse projectRow with
            | Error(code, message) -> Err(code, message), [], []
            | Ok projectedRows ->
                // Pairs each row's text projection with its own typed
                // projection, kept aligned through DISTINCT/LIMIT so a
                // derived table (`resolveFromItem`), a scalar subquery
                // (`evalExpr`'s `Subquery` case), or `UNION`'s sort can read
                // the real `Value` instead of re-wrapping the text as a
                // lexicographically-comparing `VString`.
                let paired = projectedRows |> List.map (fun row -> row |> List.map (snd >> toText), row |> List.map snd |> Array.ofList)
                let dedupedPaired = if select.Distinct then paired |> List.distinctBy fst else paired
                let types = columnTypesOf (List.length colNames) projectedRows
                let limited = dedupedPaired |> applyLimitOffset limit offset
                ResultSet(colNames, limited |> List.map fst), types, limited |> List.map snd

/// A reference-identity set of physical rows — `HashIdentity.Reference`
/// rather than `Value[]`'s own structural equality, so two rows that happen
/// to hold identical values are still distinguished, and so the set can be
/// built from a `scan` snapshot taken *before* `Storage.updateRows`/
/// `deleteRows` re-reads the table under its own lock and still match the
/// exact same array instances (true as long as nothing else writes to the
/// table in between — the same single-statement, single-connection
/// assumption every other `scan`-then-mutate call site here already makes).
let private referenceSet (rows: Value[] list) : System.Collections.Generic.HashSet<Value[]> =
    System.Collections.Generic.HashSet<Value[]>(rows, HashIdentity.Reference)

/// The physical rows a single-table `UPDATE`/`DELETE` actually mutates:
/// every row `matches` (the `WHERE`, or everything when there's none),
/// ordered by `orderBy` and capped at `limit` — computed up front, against
/// each row's original values, so `ORDER BY`/`LIMIT` see a stable snapshot
/// rather than a moving target as rows get rewritten. Empty `orderBy` with
/// no `limit` is every matching row in scan order (an ordinary `UPDATE`/
/// `DELETE`'s existing behavior); `limit` alone (no `ORDER BY`) still caps
/// the count, in whatever order `rows` was already in — MySQL calls that
/// order "unspecified" for a `LIMIT` with no `ORDER BY`, so scan order is as
/// legitimate a choice as any.
let private selectMutationTargets
    (ctxFor: Value[] -> EvalContext)
    (rows: Value[] list)
    (matches: Value[] -> Result<bool, EvalError>)
    (orderBy: OrderKey list)
    (limit: int option)
    : Result<Value[] list, EvalError> =
    rows
    |> traverse (fun row -> matches row |> Result.map (fun m -> m, row))
    |> Result.bind (fun flagged ->
        let matched = flagged |> List.filter fst |> List.map snd

        if orderBy.IsEmpty then
            Ok matched
        else
            let dirs = orderBy |> List.map snd

            matched
            |> traverse (fun row ->
                orderBy
                |> traverse (fun (e, _) -> evalOrderKey (ctxFor row) e)
                |> Result.map (fun keys -> keys, row))
            |> Result.map (fun keyed -> keyed |> List.sortWith (fun (ka, _) (kb, _) -> compareByOrderKeys dirs ka kb) |> List.map snd))
    |> Result.map (fun ordered ->
        match limit with
        | Some l -> ordered |> List.truncate (max 0 l)
        | None -> ordered)

/// Assigns `assignments` (already resolved to column indices) to a copy of
/// `row`, left-to-right — each right-hand side is evaluated against the row
/// *as mutated by every earlier assignment in the same statement*, matching
/// MySQL's documented `UPDATE` evaluation order (`SET a = 10, b = a` sets
/// `b` to the *new* `a`, not the pre-statement one). A failing right-hand
/// side propagates as an `Error` (as a `StorageError` so it can travel
/// through `Storage.updateRows`'s `updater`) instead of silently writing
/// `VNull` — the difference between "UPDATE failed" and quiet data
/// corruption once a SET expression can fail per row.
let private applyAssignments
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columnIndex: Map<string, int list>)
    (qualifiers: Map<string, ColumnDef list * int>)
    (assignments: (int * Expr) list)
    (row: Value[])
    : Result<Value[], StorageError> =
    assignments
    |> List.fold
        (fun acc (idx, expr) ->
            acc
            |> Result.bind (fun current ->
                evalExpr (contextFactory store registry dbName columnIndex qualifiers None current) expr
                |> Result.mapError ExpressionError
                |> Result.map (fun v ->
                    let newRow = Array.copy current
                    newRow.[idx] <- v
                    newRow)))
        (Ok(Array.copy row))

/// Computes every `Generated` column of `row` (`CREATE TABLE ... col AS
/// (expr)`) fresh from its other columns' current values, leaving every
/// other column untouched — a no-op when `table` has no generated columns.
/// The one place this recomputation actually happens: `recomputeGeneratedColumns`
/// folds it over a whole table's rows after a successful `INSERT`/`UPDATE`,
/// and `upsertRows`'s `computeGenerated` parameter needs it applied to a
/// bare candidate row *before* that row lands, so a unique index spanning a
/// generated column (e.g. Laravel Pulse's `key_hash BINARY(16) AS
/// (unhex(md5(key)))`) sees its real value at collision-detection time
/// instead of a not-yet-computed NULL. Left-to-right column order lets one
/// generated column reference an earlier one in the same row.
let private computeGeneratedRow
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (table: string)
    (columns: ColumnDef list)
    (row: Value[])
    : Result<Value[], StorageError> =
    let generated = columns |> List.choose (fun c -> c.Generated |> Option.map (fun e -> c, e))

    if generated.IsEmpty then
        Ok row
    else
        let row' = Array.copy row

        let ctx = contextFactory store registry dbName (columnIndexOf columns) (singleQualifier table columns) None row'

        // `ctx.Row` holds `row'` by reference, so mutating it in place
        // right after each column's evaluated (rather than collecting then
        // applying afterwards) lets a later generated column's expression
        // see an earlier one's freshly computed value.
        generated
        |> traverse (fun (col, expr) ->
            evalExpr ctx expr
            |> Result.mapError ExpressionError
            |> Result.bind (fun v -> coerceValue store.StrictMode col v)
            |> Result.map (fun v' ->
                match resolveColumn columns col.Name with
                | Ok idx -> row'.[idx] <- v'
                | Error _ -> ()))
        |> Result.map (fun _ -> row')

/// Recomputes every `Generated` column of `table` after a successful
/// `INSERT`/`UPDATE` — called unconditionally from those cases below, a
/// no-op (skips the `updateRows` pass entirely, via `computeGeneratedRow`'s
/// own no-op check) when the table has none. A generated expression is a
/// pure function of the row's other columns, so recomputing it for rows
/// that already have the right value is harmless; that's what buys the
/// "just rerun it over the whole table" simplicity instead of tracking
/// exactly which rows an `INSERT`/`UPDATE` touched.
/// ponytail: O(table size) per write when a table has generated columns
/// (fine for this engine's in-memory scale) — upgrade to recomputing only
/// the affected rows if a migration's table ever gets large enough to make
/// that the bottleneck.
let private recomputeGeneratedColumns
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (db: string)
    (table: string)
    (columns: ColumnDef list)
    : Result<unit, StorageError> =
    if columns |> List.exists (fun c -> c.Generated.IsSome) |> not then
        Ok()
    else
        updateRows store db table (fun _ -> Ok true) (computeGeneratedRow store registry dbName table columns)
        |> Result.map ignore

/// Threads `recomputeGeneratedColumns` onto the tail of an `INSERT`/`UPDATE`
/// result — re-scans `table` for its current columns (cheap: an in-memory
/// `Map` lookup) rather than making every call site pass them down.
let private withGeneratedRecomputed
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (db: string)
    (table: string)
    (result: Result<'a, StorageError>)
    : Result<'a, StorageError> =
    result
    |> Result.bind (fun r ->
        match scan store db table with
        | Ok(cols, _) -> recomputeGeneratedColumns store registry dbName db table cols |> Result.map (fun () -> r)
        | Error _ -> Ok r)

/// Rewrites `VALUES(col)` calls (MySQL's way of referring, inside an
/// `INSERT ... ON DUPLICATE KEY UPDATE` assignment, to the value that row
/// would have inserted) into the literal `candidate` value for that column —
/// `funcCallAtom` already parses `VALUES(col)` as an ordinary `FuncCall`
/// since it just looks like one syntactically, so this is a plain
/// pre-evaluation rewrite rather than new grammar.
let rec private substituteValuesFunc (columnIndex: Map<string, int list>) (candidate: Value[]) (expr: Expr) : Expr =
    let sub = substituteValuesFunc columnIndex candidate

    match expr with
    | FuncCall(name, [ Col c ]) when System.String.Equals(name, "VALUES", System.StringComparison.OrdinalIgnoreCase) ->
        // `candidate` is always the row for the one table this INSERT
        // targets, so there's no cross-table ambiguity to consider here
        // the way `resolveCol` has to for a JOIN — just take the column.
        match Map.tryFind (c.ToLowerInvariant()) columnIndex with
        | Some(i :: _) -> Lit candidate.[i]
        | _ -> expr
    | FuncCall(name, args) -> FuncCall(name, args |> List.map sub)
    | BinOp(op, a, b) -> BinOp(op, sub a, sub b)
    | Not e -> Not(sub e)
    | IsNull e -> IsNull(sub e)
    | IsNotNull e -> IsNotNull(sub e)
    | IsTrue e -> IsTrue(sub e)
    | IsFalse e -> IsFalse(sub e)
    | Distinct e -> Distinct(sub e)
    | OrderBy(e, dir) -> OrderBy(sub e, dir)
    | Like(e, p, cs) -> Like(sub e, sub p, cs)
    | Regexp(e, p) -> Regexp(sub e, sub p)
    | In(e, xs) -> In(sub e, xs |> List.map sub)
    | Between(e, lo, hi) -> Between(sub e, sub lo, sub hi)
    | Cast(e, ty) -> Cast(sub e, ty)
    | Case(subject, whens, elseBranch) ->
        Case(subject |> Option.map sub, whens |> List.map (fun (c, r) -> sub c, sub r), elseBranch |> Option.map sub)
    | Lit _
    | Col _
    | QualifiedCol _
    | Star _
    | RowNumberOver _
    | LagOver _
    // `VALUES(col)` only ever occurs directly in an `ON DUPLICATE KEY
    // UPDATE` assignment, never inside a subquery's own text — nothing to
    // substitute inside one, so it's left as-is like `Exists` always was.
    | Exists _
    | Subquery _
    | InSubquery _ -> expr

// ---------------------------------------------------------------------------
// EXPLAIN — a pure *description* of what this executor would actually do
// (join order, subquery/derived-table/union structure, current row counts),
// never a real index-planner: this engine has no indexes at execution time,
// so `type` is always `ALL` (a full scan) or `system` for a 0/1-row table,
// and `possible_keys`/`key`/`key_len`/`ref` are always NULL — faking index
// usage here would just be a second, disconnected lie about what `execute`
// does. `rows` is the table's *actual* current row count (an in-memory
// `scan`, not an estimate) since this engine can afford that where real
// MySQL's cost-based planner can't.
// ---------------------------------------------------------------------------

/// One row of `EXPLAIN`'s classic 12-column tabular output. `Id`/`Table` are
/// `option` since a few rows render `NULL` there (`UNION RESULT`'s `Id`
/// doesn't belong to any one branch; a from-less `SELECT 1`'s `Table`
/// doesn't name one) — `None` renders `NULL` the same way every other
/// resultset cell already does.
type private ExplainRow =
    { Id: int option
      SelectType: string
      Table: string option
      Type: string option
      Rows: uint64 option
      Extra: string list }

/// Every subquery `expr` embeds, in encounter order — `EXPLAIN`'s source of
/// `SUBQUERY`/`DEPENDENT SUBQUERY` rows, one nested block per subquery form
/// found this way.
let rec private collectSubqueries (expr: Expr) : SelectStmt list =
    match expr with
    | Exists s
    | Subquery s -> [ s ]
    | InSubquery(e, s) -> collectSubqueries e @ [ s ]
    | BinOp(_, a, b) -> collectSubqueries a @ collectSubqueries b
    | Not e
    | IsNull e
    | IsNotNull e
    | IsTrue e
    | IsFalse e
    | Distinct e
    | OrderBy(e, _) -> collectSubqueries e
    | Like(e, p, _) -> collectSubqueries e @ collectSubqueries p
    | Regexp(e, p) -> collectSubqueries e @ collectSubqueries p
    | In(e, xs) -> collectSubqueries e @ (xs |> List.collect collectSubqueries)
    | Between(e, lo, hi) -> collectSubqueries e @ collectSubqueries lo @ collectSubqueries hi
    | Cast(e, _) -> collectSubqueries e
    | Case(subject, whens, elseBranch) ->
        (subject |> Option.map collectSubqueries |> Option.defaultValue [])
        @ (whens |> List.collect (fun (c, r) -> collectSubqueries c @ collectSubqueries r))
        @ (elseBranch |> Option.map collectSubqueries |> Option.defaultValue [])
    | FuncCall(_, args) -> args |> List.collect collectSubqueries
    | Lit _
    | Col _
    | QualifiedCol _
    | Star _
    | RowNumberOver _
    | LagOver _ -> []

/// Whether `expr` contains a subquery form (`Exists`/`Subquery`/
/// `InSubquery`) anywhere inside it — the same walk `collectSubqueries`
/// already does, asked as a yes/no, for `EXPLAIN`'s `SIMPLE` vs. `PRIMARY`.
let private containsSubqueryExpr (expr: Expr) : bool = not (collectSubqueries expr).IsEmpty

/// Every expression position a `SELECT` can hide a subquery in — one list,
/// read both by `explainSelectBlock` (which blocks to emit for each
/// embedded subquery) and by the `SIMPLE`-vs-`PRIMARY` check in
/// `explainStatement`, so the two can't drift out of sync the way a second
/// hand-written copy of this list did (missing `GroupBy`/`OrderBy`).
let private selectSubqueryExprs (select: SelectStmt) : Expr list =
    (select.Projections |> List.map fst)
    @ (select.Where |> Option.toList)
    @ (select.Having |> Option.toList)
    @ select.GroupBy
    @ (select.OrderBy |> List.map fst)

/// Whether any expression in `sub` (its projections/`WHERE`/`HAVING`/
/// `GROUP BY`/`ORDER BY`) references a table qualifier that isn't one of
/// `sub`'s own `FROM`/`JOIN` aliases — `EXPLAIN`'s `DEPENDENT SUBQUERY` vs.
/// plain `SUBQUERY` (a correlated `WHERE EXISTS (SELECT 1 FROM t2 WHERE
/// t2.parent_id = t1.id)` references `t1`, which isn't one of `t2`'s own
/// aliases). ponytail: only catches *qualified* correlation (`t1.id`, not a
/// bare `id` that happens to resolve to the outer row) — good enough for
/// every correlated subquery this codebase's own Laravel-shaped test suite
/// writes, which always qualifies the outer reference.
let private isCorrelated (sub: SelectStmt) : bool =
    let ownAliases =
        ((sub.From |> Option.map fromItemQualifier |> Option.toList) @ (sub.Joins |> List.map (fun j -> fromItemQualifier j.Table)))
        |> List.map (fun s -> s.ToLowerInvariant())
        |> Set.ofList

    let rec references (expr: Expr) : bool =
        match expr with
        | QualifiedCol(t, _) -> not (ownAliases.Contains(t.ToLowerInvariant()))
        | Exists _
        | Subquery _ -> false
        | InSubquery(e, _) -> references e
        | BinOp(_, a, b) -> references a || references b
        | Not e
        | IsNull e
        | IsNotNull e
        | IsTrue e
        | IsFalse e
        | Distinct e
        | OrderBy(e, _) -> references e
        | Like(e, p, _) -> references e || references p
        | Regexp(e, p) -> references e || references p
        | In(e, xs) -> references e || xs |> List.exists references
        | Between(e, lo, hi) -> references e || references lo || references hi
        | Cast(e, _) -> references e
        | Case(subject, whens, elseBranch) ->
            (subject |> Option.map references |> Option.defaultValue false)
            || whens |> List.exists (fun (c, r) -> references c || references r)
            || (elseBranch |> Option.map references |> Option.defaultValue false)
        | FuncCall(_, args) -> args |> List.exists references
        | Lit _
        | Col _
        | Star _
        | RowNumberOver _
        | LagOver _ -> false

    let exprs =
        (sub.Projections |> List.map fst)
        @ (sub.Where |> Option.toList)
        @ (sub.Having |> Option.toList)
        @ sub.GroupBy
        @ (sub.OrderBy |> List.map fst)

    exprs |> List.exists references

/// `EXPLAIN`'s `type`/`rows` pair for one real (or `information_schema`
/// virtual) table: `system` for a table with at most one row, `ALL`
/// otherwise (this executor never does anything but a full scan), and the
/// table's actual current row count — honest, not an estimate, since a
/// `scan` is cheap at this engine's in-memory scale. `EXPLAIN` still
/// describes a real statement, so a table that doesn't exist is 1146 here
/// too, same as it would be if the statement actually ran — not a fake
/// plan with `rows = NULL`.
let private explainTableStats (store: Store) (dbName: string) (tableRef: TableRef) : Result<uint64 option * string, QueryResult> =
    let tableDb = tableRef.Database |> Option.defaultValue dbName

    let rowCountResult =
        if System.String.Equals(tableDb, "information_schema", System.StringComparison.OrdinalIgnoreCase) then
            match InformationSchema.scan store.Catalog tableRef.Table with
            | Some(_, rows) -> Ok(uint64 (List.length rows))
            | None -> Error(storageErr (NoSuchTable tableRef.Table))
        else
            scan store tableDb tableRef.Table |> Result.mapError storageErr |> Result.map (snd >> Seq.length >> uint64)

    rowCountResult |> Result.map (fun n -> Some n, (if n <= 1UL then "system" else "ALL"))

/// One `EXPLAIN` block's table rows (`from` plus every `join`, in order),
/// recursing into a `FROM (SELECT ...) AS alias`'s own block (`DERIVED`)
/// and every subquery `subqueryExprs` embeds (`SUBQUERY`/`DEPENDENT
/// SUBQUERY`) — the shared plumbing `explainSelectBlock` (a real `SELECT`)
/// and `execute`'s `UPDATE`/`DELETE ... JOIN` explain handling both drive,
/// since neither `Ast.UpdateStmt` nor `Ast.DeleteStmt` has a `SelectStmt` to
/// hand this the way a `SELECT`/derived table does.
let rec private explainJoinBlock
    (store: Store)
    (dbName: string)
    (nextId: unit -> int)
    (acc: ResizeArray<ExplainRow>)
    (id: int)
    (selectType: string)
    (from: FromItem option)
    (joins: Join list)
    (extra: string list)
    (subqueryExprs: Expr list)
    : Result<unit, QueryResult> =
    let tableCount = (from |> Option.toList |> List.length) + joins.Length

    let emitTableRow (idx: int) (label: string) (rowCount: uint64 option) (typeLabel: string) =
        acc.Add
            { Id = Some id
              SelectType = selectType
              Table = Some label
              Type = Some typeLabel
              Rows = rowCount
              Extra = (if idx = tableCount - 1 then extra else []) }

    /// One `FromItem`'s row(s): a real table's stats, or a derived table's
    /// `<derivedN>` placeholder plus its own recursive `DERIVED` block.
    let explainFromItem (idx: int) (item: FromItem) : Result<unit, QueryResult> =
        match item with
        | FromTable tref ->
            explainTableStats store dbName tref
            |> Result.map (fun (n, ty) -> emitTableRow idx (tref.Alias |> Option.defaultValue tref.Table) n ty)
        | FromSubquery(PlainSelect sub, _alias) ->
            let derivedId = nextId ()
            emitTableRow idx (sprintf "<derived%d>" derivedId) None "ALL"
            explainSelectBlock store dbName nextId acc derivedId "DERIVED" sub
        | FromSubquery(UnionSelect(first, rest, _, _, _), _alias) ->
            // Same "DERIVED" + "UNION" per-branch shape as a top-level
            // `Union`'s own `EXPLAIN` (see `explainStatement`'s `Union`
            // case) — a derived table's body can be a `UNION` too
            // (`Ast.SelectOrUnion`'s doc), so it gets the same per-branch
            // rows nested one level under its own `<derivedN>` placeholder.
            let derivedId = nextId ()
            emitTableRow idx (sprintf "<derived%d>" derivedId) None "ALL"

            explainSelectBlock store dbName nextId acc derivedId "DERIVED" first
            |> Result.bind (fun () -> rest |> traverse (fun (_, s) -> explainSelectBlock store dbName nextId acc (nextId ()) "UNION" s))
            |> Result.map ignore


    let fromResult = from |> Option.map (explainFromItem 0) |> Option.defaultValue (Ok())

    fromResult
    |> Result.bind (fun () -> joins |> List.indexed |> traverse (fun (i, j) -> explainFromItem (i + 1) j.Table))
    |> Result.map (fun _ ->
        if tableCount = 0 then
            acc.Add
                { Id = Some id
                  SelectType = selectType
                  Table = None
                  Type = None
                  Rows = None
                  Extra = [ "No tables used" ] })
    |> Result.bind (fun () ->
        subqueryExprs
        |> List.collect collectSubqueries
        |> traverse (fun sub ->
            let sid = nextId ()
            let stype = if isCorrelated sub then "DEPENDENT SUBQUERY" else "SUBQUERY"
            explainSelectBlock store dbName nextId acc sid stype sub)
        |> Result.map ignore)

/// One `SELECT`'s (or `FROM (SELECT ...)` derived table's) `EXPLAIN`
/// block — `Extra`'s three flags read straight off the clauses that make
/// them true (`WHERE` -> `Using where`, `ORDER BY` -> `Using filesort`
/// since there's no index to satisfy it without one, `GROUP BY`/`DISTINCT`
/// -> `Using temporary`), then hands the table/subquery walk itself to
/// `explainJoinBlock`.
and private explainSelectBlock
    (store: Store)
    (dbName: string)
    (nextId: unit -> int)
    (acc: ResizeArray<ExplainRow>)
    (id: int)
    (selectType: string)
    (select: SelectStmt)
    : Result<unit, QueryResult> =
    let extra =
        [ if select.Where.IsSome then "Using where"
          if not select.OrderBy.IsEmpty then "Using filesort"
          if not select.GroupBy.IsEmpty || select.Distinct then "Using temporary" ]

    explainJoinBlock store dbName nextId acc id selectType select.From select.Joins extra (selectSubqueryExprs select)

/// Renders every collected `ExplainRow` into `EXPLAIN`'s classic 12-column
/// resultset — `id` ascending, `None -> NULL` in every `option` cell the
/// same way every other resultset already does (see `ExplainRow`'s doc for
/// why `Id`/`Table` are `option` at all). `filtered` is always the honest
/// `100.00` a planner with no statistics can report (this engine has
/// nothing else to estimate it from), present on every real table row and
/// `NULL` only where `type` itself is `NULL` too (a from-less `SELECT 1`, a
/// `UNION RESULT` row).
let private renderExplainRows (rows: ExplainRow list) : QueryResult =
    let columns =
        [ "id"; "select_type"; "table"; "partitions"; "type"; "possible_keys"; "key"; "key_len"; "ref"; "rows"; "filtered"; "Extra" ]

    let renderRow (r: ExplainRow) : string option list =
        [ r.Id |> Option.map string
          Some r.SelectType
          r.Table
          None
          r.Type
          None
          None
          None
          None
          r.Rows |> Option.map string
          (r.Type |> Option.map (fun _ -> "100.00"))
          (if r.Extra.IsEmpty then None else Some(String.concat "; " r.Extra)) ]

    ResultSet(columns, rows |> List.sortBy (fun r -> r.Id |> Option.defaultValue System.Int32.MaxValue) |> List.map renderRow)

/// `EXPLAIN [FORMAT=TRADITIONAL] stmt` — a pure description of what
/// `execute` would do with `stmt`, never actually running it. Still
/// validates `stmt` the way actually running it would, though — real MySQL
/// gives 1146 for a table `EXPLAIN` describes that doesn't exist and 1054
/// for a column it doesn't recognize, not a fake plan; `explainJoinBlock`/
/// `explainTableStats` carry that check for every table an `UPDATE`/
/// `DELETE`/`SELECT`/subquery touches, `runSelectStmt` (read-only, so safe
/// to call purely to typecheck and discard) covers a `SELECT`'s columns the
/// same way `QueryHandler`'s real execution path would.
/// Covers every statement shape real MySQL allows `EXPLAIN` on that this
/// engine has a join/subquery structure to describe (`SELECT`, `UNION`,
/// `UPDATE`, `DELETE`, `INSERT` — plain or `... SELECT`); anything else
/// (DDL, session statements, ...) is the same 1064 real MySQL gives.
let rec private explainStatement (store: Store) (registry: Registry) (dbName: string) (stmt: Statement) : QueryResult =
    let counter = ref 0

    let nextId () =
        counter.Value <- counter.Value + 1
        counter.Value

    let acc = ResizeArray<ExplainRow>()

    let finish (result: Result<unit, QueryResult>) =
        match result with
        | Ok() -> renderExplainRows (List.ofSeq acc)
        | Error e -> e

    /// `INSERT`'s target table never goes through `explainJoinBlock` (an
    /// `INSERT` has no `FROM`), so it needs its own existence check.
    let checkTableExists (table: string) : Result<unit, QueryResult> =
        let db, tname = splitQualified dbName table
        resolveTableRef store dbName { Database = Some db; Table = tname; Alias = None } |> Result.map ignore

    let checkSelect (select: SelectStmt) : Result<unit, QueryResult> =
        match runSelectStmt store registry dbName select None with
        | Err(code, message), _, _ -> Error(Err(code, message))
        | _ -> Ok()

    /// `UPDATE`/`DELETE` have no `SelectStmt` to hand `checkSelect`, so
    /// `exprs` (their `WHERE`, and an `UPDATE`'s `SET` right-hand sides) get
    /// the same "evaluate against a synthetic all-NULL probe row" check the
    /// real single-table `UPDATE`/`DELETE` paths already run before writing
    /// anything — an unknown column is 1054 here too, not a fake plan.
    let checkMutationWhere (fromRef: TableRef) (joins: Join list) (exprs: Expr list) : Result<unit, QueryResult> =
        let resolveJoinSource (j: Join) =
            match j.Table with
            | FromSubquery _ ->
                Error(Err(1064, "a derived table (subquery) isn't supported as a multi-table UPDATE/DELETE JOIN source"))
            | FromTable tref -> resolveTableRef store dbName tref |> Result.map (fun (cols, _) -> fromItemQualifier j.Table, cols)

        resolveTableRef store dbName fromRef
        |> Result.bind (fun (fromCols, _) ->
            joins
            |> traverse resolveJoinSource
            |> Result.map (fun joinSources -> ((fromRef.Alias |> Option.defaultValue fromRef.Table), fromCols) :: joinSources))
        |> Result.bind (fun sources ->
            let allCols = sources |> List.collect snd
            let ctx = contextFactory store registry dbName (columnIndexOf allCols) (qualifierRanges sources) None (probeRow allCols)
            exprs |> traverse (fun e -> evalExpr ctx e |> Result.map ignore) |> Result.map ignore |> Result.mapError Err)

    match stmt with
    | Select select ->
        let id = nextId ()

        let selectType =
            let isDerived = match select.From with Some(FromSubquery _) -> true | _ -> false
            if (selectSubqueryExprs select |> List.exists containsSubqueryExpr) || isDerived then "PRIMARY" else "SIMPLE"

        finish (checkSelect select |> Result.bind (fun () -> explainSelectBlock store dbName nextId acc id selectType select))
    | Union(first, rest, _, _, _) ->
        let id1 = nextId ()

        finish (
            checkSelect first
            |> Result.bind (fun () -> rest |> traverse (fun (_, s) -> checkSelect s) |> Result.map ignore)
            |> Result.bind (fun () -> explainSelectBlock store dbName nextId acc id1 "PRIMARY" first)
            |> Result.bind (fun () ->
                rest
                |> traverse (fun (_, s) ->
                    let sid = nextId ()
                    explainSelectBlock store dbName nextId acc sid "UNION" s |> Result.map (fun () -> sid)))
            |> Result.map (fun restIds ->
                if not restIds.IsEmpty then
                    let label = sprintf "<union%s>" (id1 :: restIds |> List.map string |> String.concat ",")
                    acc.Add { Id = None; SelectType = "UNION RESULT"; Table = Some label; Type = None; Rows = None; Extra = [] })
        )
    | Update u ->
        let id = nextId ()
        let extra = [ if u.Where.IsSome then "Using where"
                      if not u.OrderBy.IsEmpty then "Using filesort" ]
        let subqueryExprs = (u.Where |> Option.toList) @ (u.Assignments |> List.map (fun a -> a.Value)) @ (u.OrderBy |> List.map fst)

        finish (
            checkMutationWhere u.From u.Joins ((u.Where |> Option.toList) @ (u.Assignments |> List.map (fun a -> a.Value)))
            |> Result.bind (fun () -> explainJoinBlock store dbName nextId acc id "UPDATE" (Some(FromTable u.From)) u.Joins extra subqueryExprs)
        )
    | Delete d ->
        let id = nextId ()
        let extra = [ if d.Where.IsSome then "Using where"
                      if not d.OrderBy.IsEmpty then "Using filesort" ]
        let subqueryExprs = d.Where |> Option.toList

        finish (
            checkMutationWhere d.From d.Joins (d.Where |> Option.toList)
            |> Result.bind (fun () -> explainJoinBlock store dbName nextId acc id "DELETE" (Some(FromTable d.From)) d.Joins extra subqueryExprs)
        )
    | Insert(table, _, rowsExprs, _, _) ->
        let id = nextId ()

        finish (
            checkTableExists table
            |> Result.map (fun () -> acc.Add { Id = Some id; SelectType = "INSERT"; Table = Some table; Type = None; Rows = None; Extra = [] })
            |> Result.bind (fun () ->
                List.concat rowsExprs
                |> List.collect collectSubqueries
                |> traverse (fun sub ->
                    let sid = nextId ()
                    explainSelectBlock store dbName nextId acc sid (if isCorrelated sub then "DEPENDENT SUBQUERY" else "SUBQUERY") sub)
                |> Result.map ignore)
        )
    | InsertSelect(table, _, select, _) ->
        let id = nextId ()

        finish (
            checkTableExists table
            |> Result.bind (fun () -> checkSelect select)
            |> Result.map (fun () -> acc.Add { Id = Some id; SelectType = "INSERT"; Table = Some table; Type = None; Rows = None; Extra = [] })
            |> Result.bind (fun () ->
                let sid = nextId ()
                explainSelectBlock store dbName nextId acc sid "SUBQUERY" select)
        )
    | Explain inner -> explainStatement store registry dbName inner
    | _ -> Err(1064, "EXPLAIN is not supported for this statement")

/// A top-level `SELECT`'s resultset plus its per-column MySQL wire types —
/// `QueryHandler.executeStatement`'s type-preserving entry point into
/// `runSelectStmt`, which can't be `public` itself (see the doc there).
/// `outer` is always `None` for a top-level statement, so this needs no
/// `EvalContext` in its own signature.
let runTopLevelSelect (store: Store) (registry: Registry) (dbName: string) (select: SelectStmt) : QueryResult * byte list =
    let result, types, _ = runSelectStmt store registry dbName select None
    result, types

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

        match names |> traverse dropOne with
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

        match pairs |> traverse renameOne with
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

    | Insert(table, columns, rowsExprs, onDuplicateUpdate, ignoreDuplicates) ->
        // INSERT ... VALUES expressions aren't evaluated against any row
        // (no table columns are in scope), just literals/functions — an
        // empty column index turns a stray `Col` reference into a clean
        // 1054 rather than an index-out-of-range.
        let db, table = splitQualified dbName table

        let literalCtx = contextFactory store registry dbName Map.empty Map.empty None [||]

        match rowsExprs |> traverse (traverse (evalExpr literalCtx)) with
        | Error(code, message) -> lastInsertId, Err(code, message)
        | Ok rowsValues ->
            let cols = if columns.IsEmpty then None else Some columns

            if onDuplicateUpdate.IsEmpty then
                let insert = if ignoreDuplicates then insertRowsIgnore else insertRows

                match insert store db table cols rowsValues |> withGeneratedRecomputed store registry dbName db table with
                | Ok(newLastId, affected) ->
                    (if newLastId <> 0L then newLastId else lastInsertId), Affected(uint64 affected)
                | Error e -> lastInsertId, storageErr e
            else
                match scan store db table with
                | Error e -> lastInsertId, storageErr e
                | Ok(tableColumns, _) ->
                    let columnIndex = columnIndexOf tableColumns

                    let applyUpdate (existing: Value[]) (candidate: Value[]) : Result<Value[], StorageError> =
                        let ctx = contextFactory store registry dbName columnIndex (singleQualifier table tableColumns) None existing

                        onDuplicateUpdate
                        |> traverse (fun (name, expr) ->
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

                    let computeGenerated = computeGeneratedRow store registry dbName table tableColumns

                    match upsertRows store db table cols rowsValues computeGenerated applyUpdate |> withGeneratedRecomputed store registry dbName db table with
                    | Ok(newLastId, affected) ->
                        (if newLastId <> 0L then newLastId else lastInsertId), Affected(uint64 affected)
                    | Error e -> lastInsertId, storageErr e

    | InsertSelect(table, columns, select, ignoreDuplicates) ->
        let db, table = splitQualified dbName table

        let selectResult, _, _ = runSelectStmt store registry dbName select None

        match selectResult with
        | Err(code, message) -> lastInsertId, Err(code, message)
        | Affected _ -> lastInsertId, Err(1064, "INSERT ... SELECT source did not return a resultset")
        | ResultSet(_, rows) ->
            // The source rows are already the wire's flat `string option`
            // text (see `Value.mysqlTypeOf`'s callers) rather than the
            // original typed `Value`s — fine here, since `insertRows`/
            // `insertRowsIgnore` coerce every value through the target
            // column's type anyway, the same as a literal `VALUES` row's
            // `Lit(VString ...)` would.
            let rowsValues = rows |> List.map (List.map (function Some s -> VString s | None -> VNull))
            let cols = if columns.IsEmpty then None else Some columns
            let insert = if ignoreDuplicates then insertRowsIgnore else insertRows

            match insert store db table cols rowsValues |> withGeneratedRecomputed store registry dbName db table with
            | Ok(newLastId, affected) -> (if newLastId <> 0L then newLastId else lastInsertId), Affected(uint64 affected)
            | Error e -> lastInsertId, storageErr e

    | Select select ->
        let result, _, _ = runSelectStmt store registry dbName select None
        lastInsertId, result

    | Union(first, rest, orderBy, limit, offset) ->
        let result, _, _ = runUnionStmt store registry dbName first rest orderBy limit offset
        lastInsertId, result

    | Update updateStmt when updateStmt.Joins.IsEmpty ->
        let db, table = (updateStmt.From.Database |> Option.defaultValue dbName), updateStmt.From.Table
        let tableAlias = updateStmt.From.Alias |> Option.defaultValue updateStmt.From.Table

        match scan store db table with
        | Error e -> lastInsertId, storageErr e
        | Ok(columns, rows) ->
            let columnIndex = columnIndexOf columns

            match updateStmt.Assignments |> traverse (fun a -> resolveColumn columns a.Column |> Result.map (fun i -> i, a.Value)) with
            | Error e -> lastInsertId, storageErr e
            | Ok indexedAssignments ->
                let qualifiers = singleQualifier tableAlias columns

                let ctxFor = contextFactory store registry dbName columnIndex qualifiers None

                let check = whereMatches ctxFor updateStmt.Where

                let checkAssignments row =
                    indexedAssignments |> traverse (fun (_, expr) -> evalExpr (ctxFor row) expr)

                // Type-check WHERE/SET against a synthetic all-NULL row
                // first — same reasoning as `runSelect`'s `probeRow`: an
                // unknown column/function is a schema error, not a data
                // one, and shouldn't depend on whether any row happens to
                // match (or exist at all).
                match check (probeRow columns) |> Result.bind (fun _ -> checkAssignments (probeRow columns)) with
                | Error(code, message) -> lastInsertId, Err(code, message)
                | Ok _ ->
                    match selectMutationTargets ctxFor (List.ofSeq rows) check updateStmt.OrderBy updateStmt.Limit with
                    | Error(code, message) -> lastInsertId, Err(code, message)
                    | Ok targetRows ->
                        let targetSet = referenceSet targetRows
                        let predicate row = Ok(targetSet.Contains row)
                        let updater row = applyAssignments store registry dbName columnIndex qualifiers indexedAssignments row

                        match updateRows store db table predicate updater |> withGeneratedRecomputed store registry dbName db table with
                        | Ok affected -> lastInsertId, Affected(uint64 affected)
                        | Error e -> lastInsertId, storageErr e

    | Update updateStmt ->
        // Multi-table `UPDATE t1 JOIN t2 ON ... SET ...` — resolves the
        // whole join, then for each matched combined row, assigns to
        // whichever source table each `SET` target names, claiming a
        // physical row (by reference) the first time a matched row touches
        // it so a row reached through more than one join match is still
        // updated at most once (see `Ast.UpdateStmt`'s doc). Held under one
        // `store.Lock` for the whole statement (read *and* write) so no
        // other write can interleave between the join scan and the apply
        // below — same coarseness `Storage.updateRows` already uses for a
        // single-table `UPDATE`.
        lock store.Lock (fun () ->
            match runMutationJoin store registry dbName updateStmt.From updateStmt.Joins with
            | Error e -> lastInsertId, e
            | Ok(sources, joinedRows) ->
                let sourceIndex = sources |> List.mapi (fun i (q, _, _) -> q.ToLowerInvariant(), i) |> Map.ofList
                let combinedColumns = sources |> List.map (fun (q, _, c) -> q, c)
                let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumns |> List.collect snd)) (qualifierRanges combinedColumns) None

                // Byte offset of each source's columns within the flat
                // combined row `ctxFor` expects — lets the left-to-right
                // fold below patch an assignment's new value straight into
                // a working copy of that row for the next assignment to see.
                let sourceOffsets =
                    combinedColumns |> List.fold (fun (offset, acc) (_, cols) -> offset + List.length cols, acc @ [ offset ]) (0, []) |> snd |> Array.ofList

                let resolveAssignment (a: Assignment) : Result<(int * int * Expr), EvalError> =
                    match a.Table with
                    | Some q ->
                        match Map.tryFind (q.ToLowerInvariant()) sourceIndex with
                        | None -> Error(unknownColumn (sprintf "%s.%s" q a.Column))
                        | Some srcIdx ->
                            let _, _, cols = sources.[srcIdx]

                            resolveColumn cols a.Column
                            |> Result.mapError (fun _ -> unknownColumn (sprintf "%s.%s" q a.Column))
                            |> Result.map (fun colIdx -> srcIdx, colIdx, a.Value)
                    | None ->
                        match
                            sources
                            |> List.indexed
                            |> List.choose (fun (i, (_, _, cols)) -> resolveColumn cols a.Column |> function Ok idx -> Some(i, idx) | Error _ -> None)
                        with
                        | [ (srcIdx, colIdx) ] -> Ok(srcIdx, colIdx, a.Value)
                        | [] -> Error(unknownColumn a.Column)
                        | _ -> Error(1052, sprintf "Column '%s' in field list is ambiguous" a.Column)

                match updateStmt.Assignments |> traverse resolveAssignment with
                | Error(code, message) -> lastInsertId, Err(code, message)
                | Ok resolvedAssignments ->
                    let check = whereMatches ctxFor updateStmt.Where

                    // Two aliases resolving to the same physical table (a
                    // self-join) must share one claim/pending bucket, keyed
                    // by the physical table rather than by source index —
                    // otherwise the same row reached through both aliases
                    // gets written by two separate `Storage.updateRows`
                    // passes below, and the first pass's row-array
                    // replacement (`updateRows` returns *new* `Value[]`s for
                    // every row it changes) breaks the second pass's
                    // by-reference match, silently dropping its write.
                    let physicalKey (tableRef: TableRef) : string * string =
                        (tableRef.Database |> Option.defaultValue dbName), Storage.normalizeTableName tableRef.Table

                    let physicalGroups =
                        sources
                        |> List.map (fun (_, tableRef, _) -> tableRef)
                        |> List.fold (fun acc t -> if acc |> List.exists (fun t' -> physicalKey t' = physicalKey t) then acc else acc @ [ t ]) []
                        |> Array.ofList

                    let sourcePhys =
                        sources
                        |> List.map (fun (_, tableRef, _) -> physicalGroups |> Array.findIndex (fun t -> physicalKey t = physicalKey tableRef))
                        |> Array.ofList

                    // `claims` stays keyed by *source* (one set per alias,
                    // as before `Ast.UpdateStmt`'s "at most once" doc
                    // describes) — a physical row matched twice through the
                    // *same* alias (`t1 JOIN t2` where two `t2` rows both
                    // join the same `t1` row) is only written once by that
                    // alias, but a self-join's two *different* aliases each
                    // get their own claim on the same physical row, so both
                    // land — matching MySQL, where `a` and `b` are
                    // independent roles even when they're the same table.
                    // `pending` is what's keyed by physical table: every
                    // alias's surviving write for a given physical row
                    // accumulates into the *same* entry, so one
                    // `updateRows` pass per physical table applies every
                    // alias's columns for that row together instead of two
                    // passes racing each other's row-array replacement.
                    let claims = sources |> List.map (fun _ -> System.Collections.Generic.HashSet<Value[]>(HashIdentity.Reference)) |> Array.ofList
                    let pending = physicalGroups |> Array.map (fun _ -> System.Collections.Generic.Dictionary<Value[], (int * Value) list>(HashIdentity.Reference))

                    let processRow ((identities, flat): Value[] option list * Value[]) : Result<unit, EvalError> =
                        check flat
                        |> Result.bind (fun isMatch ->
                            if not isMatch then
                                Ok()
                            else
                                // Left-to-right: each assignment's RHS is
                                // evaluated against `working`, patched with
                                // every earlier assignment in this same
                                // statement — matching MySQL's documented
                                // `SET a = x, b = a` evaluation order, now
                                // across tables too.
                                let working = Array.copy flat

                                let rec go assignments =
                                    match assignments with
                                    | [] -> Ok()
                                    | (srcIdx, colIdx, expr) :: rest ->
                                        evalExpr (ctxFor working) expr
                                        |> Result.bind (fun v ->
                                            working.[sourceOffsets.[srcIdx] + colIdx] <- v

                                            match List.item srcIdx identities with
                                            | None -> ()
                                            | Some physRow ->
                                                let physIdx = sourcePhys.[srcIdx]

                                                if not (claims.[srcIdx].Contains physRow) then
                                                    claims.[srcIdx].Add physRow |> ignore
                                                    let existing = match pending.[physIdx].TryGetValue physRow with true, vs -> vs | false, _ -> []
                                                    pending.[physIdx].[physRow] <- existing @ [ colIdx, v ]

                                            go rest)

                                go resolvedAssignments)

                    match joinedRows |> traverse processRow with
                    | Error(code, message) -> lastInsertId, Err(code, message)
                    | Ok _ ->
                        // All per-table writes must succeed together or not
                        // at all — MySQL rolls back the whole statement when
                        // a later table's write violates a constraint,
                        // rather than leaving an earlier table's rows
                        // mutated. `beginTransactionSnapshot` gives an
                        // isolated scratch catalog to write every physical
                        // table's batch into; only merged back into `store`
                        // (as one `TransactionCommitted` WAL entry, not N
                        // separate ones) once every batch has actually
                        // succeeded.
                        let snapshot = Storage.beginTransactionSnapshot store

                        let apply =
                            physicalGroups
                            |> Array.mapi (fun i tableRef ->
                                if pending.[i].Count = 0 then
                                    Ok 0
                                else
                                    let tdb, tname = (tableRef.Database |> Option.defaultValue dbName), tableRef.Table
                                    let predicate row = Ok(pending.[i].ContainsKey row)

                                    let updater row =
                                        match pending.[i].TryGetValue row with
                                        | true, vals ->
                                            let newRow = Array.copy row

                                            for colIdx, v in vals do
                                                newRow.[colIdx] <- v

                                            Ok newRow
                                        | false, _ -> Ok row

                                    updateRows snapshot tdb tname predicate updater |> withGeneratedRecomputed snapshot registry dbName tdb tname)
                            |> Array.toList
                            |> traverse id

                        match apply with
                        | Ok counts ->
                            store.Catalog <- snapshot.Catalog
                            Storage.commitTransactionEvents store snapshot
                            lastInsertId, Affected(uint64 (List.sum counts))
                        | Error e -> lastInsertId, storageErr e)

    | Delete deleteStmt when deleteStmt.Joins.IsEmpty ->
        let db, table = (deleteStmt.From.Database |> Option.defaultValue dbName), deleteStmt.From.Table
        let tableAlias = deleteStmt.From.Alias |> Option.defaultValue deleteStmt.From.Table

        match scan store db table with
        | Error e -> lastInsertId, storageErr e
        | Ok(columns, rows) ->
            let columnIndex = columnIndexOf columns

            let ctxFor = contextFactory store registry dbName columnIndex (singleQualifier tableAlias columns) None

            let check = whereMatches ctxFor deleteStmt.Where

            match check (probeRow columns) with
            | Error(code, message) -> lastInsertId, Err(code, message)
            | Ok _ ->
                match selectMutationTargets ctxFor (List.ofSeq rows) check deleteStmt.OrderBy deleteStmt.Limit with
                | Error(code, message) -> lastInsertId, Err(code, message)
                | Ok targetRows ->
                    let targetSet = referenceSet targetRows
                    let predicate row = Ok(targetSet.Contains row)

                    match deleteRows store db table predicate with
                    | Ok affected -> lastInsertId, Affected(uint64 affected)
                    | Error e -> lastInsertId, storageErr e

    | Delete deleteStmt ->
        // Multi-table `DELETE t1[, t2] FROM t1 JOIN t2 ON ...` / `DELETE
        // FROM t1 USING t1 JOIN t2 ON ...` — resolves the whole join, marks
        // (by reference) every physical row of every named `Targets` table
        // that any matched combined row touches, then removes each target
        // table's marked rows via `Storage.deleteRows`, one call per table.
        // A physical row reached through more than one join match is still
        // only in the set once (a `HashSet`), so it's deleted at most once.
        match runMutationJoin store registry dbName deleteStmt.From deleteStmt.Joins with
        | Error e -> lastInsertId, e
        | Ok(sources, joinedRows) ->
            let sourceIndex = sources |> List.mapi (fun i (q, _, _) -> q.ToLowerInvariant(), i) |> Map.ofList
            let combinedColumns = sources |> List.map (fun (q, _, c) -> q, c)
            let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumns |> List.collect snd)) (qualifierRanges combinedColumns) None

            match
                deleteStmt.Targets
                |> traverse (fun t ->
                    match Map.tryFind (t.ToLowerInvariant()) sourceIndex with
                    | Some i -> Ok i
                    | None -> Error(1109, sprintf "Unknown table '%s' in MULTI DELETE" t))
            with
            | Error(code, message) -> lastInsertId, Err(code, message)
            | Ok targetIndices ->
                let check = whereMatches ctxFor deleteStmt.Where

                let claimedByTarget = targetIndices |> List.map (fun i -> i, System.Collections.Generic.HashSet<Value[]>(HashIdentity.Reference)) |> Map.ofList

                let processRow ((identities, flat): Value[] option list * Value[]) : Result<unit, EvalError> =
                    check flat
                    |> Result.map (fun isMatch ->
                        if isMatch then
                            for i in targetIndices do
                                match List.item i identities with
                                | Some physRow -> claimedByTarget.[i].Add physRow |> ignore
                                | None -> ())

                match joinedRows |> traverse processRow with
                | Error(code, message) -> lastInsertId, Err(code, message)
                | Ok _ ->
                    let apply =
                        targetIndices
                        |> List.distinct
                        |> traverse (fun i ->
                            let _, tableRef, _ = sources.[i]
                            let tdb, tname = (tableRef.Database |> Option.defaultValue dbName), tableRef.Table
                            let set = claimedByTarget.[i]
                            deleteRows store tdb tname (fun row -> Ok(set.Contains row)))

                    match apply with
                    | Ok counts -> lastInsertId, Affected(uint64 (List.sum counts))
                    | Error e -> lastInsertId, storageErr e

    | Explain inner ->
        lastInsertId, explainStatement store registry dbName inner
