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
    | Distinct e -> containsAggregate registry e
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
    // A subquery's own aggregates belong to *its* grouping, not the query
    // this expression sits in — `containsAggregate` only asks whether
    // `runSelect` needs to switch itself onto the grouped path, so these
    // three never contribute regardless of what their nested `SelectStmt`
    // contains.
    | Exists _
    | Subquery _
    | InSubquery _ -> false

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
    | Case _ -> "case"
    | Star None -> "*"
    | Star(Some q) -> sprintf "%s.*" q
    | RowNumberOver _ -> "row_number() over ()"
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
      Outer: EvalContext option }

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
          Outer = outer }

/// Resolves a bare column against `ctx`, falling back to
/// `ctx.Outer`/its own outer/... on a miss — see `EvalContext.Outer`. Two or
/// more matches (a `JOIN` of tables that share a column name) is error 1052,
/// not a silent pick of whichever one `columnIndexOf` happened to see last.
let rec private resolveCol (ctx: EvalContext) (name: string) : Result<Value, EvalError> =
    match Map.tryFind (name.ToLowerInvariant()) ctx.ColumnIndex with
    | Some [ i ] -> Ok ctx.Row.[i]
    | Some(_ :: _ :: _) -> Error(1052, sprintf "Column '%s' in field list is ambiguous" name)
    | Some [] | None ->
        match ctx.Outer with
        | Some parent -> resolveCol parent name
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
    // Only reachable if a `RowNumberOver` ever escapes `runWindowedSelect`'s
    // rewrite (which replaces every top-level one with a plain `Col`
    // reference before any of this runs) — e.g. nested inside another
    // expression, which real MySQL itself rejects for a window function.
    | RowNumberOver _ -> Error(1054, "Invalid use of a group function")
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
    | Distinct e -> eval e
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
            // cast's target type rather than a second coercion table.
            let castCol: ColumnDef =
                { Name = "CAST"
                  Type = ty
                  Nullable = true
                  Default = None
                  AutoIncrement = false
                  PrimaryKey = false
                  Unique = false
                  Generated = None }

            match Storage.coerceValue castCol v with
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
    | FromSubquery(select, _alias) ->
        match runSelectStmt store registry dbName select None with
        | ResultSet(cols, _), _, typedRows -> Ok(deriveColumns cols, typedRows)
        | Err(code, message), _, _ -> Error(Err(code, message))
        | Affected _, _, _ -> Error(Err(1064, "derived table did not return a resultset"))

/// `EvalContext.Qualifiers` for every source (the `FROM` table, and each
/// `JOIN` after it) already resolved into `sources`, ordered the same
/// left-to-right way their columns are laid out in a combined row —
/// offsets accumulate across the fold, so source *n*'s columns start right
/// where source *n-1*'s end.
and private qualifierRanges (sources: (string * ColumnDef list) list) : Map<string, ColumnDef list * int> =
    sources
    |> List.fold (fun (offset, quals) (qualifier, cols) -> offset + List.length cols, Map.add (qualifier.ToLowerInvariant()) (cols, offset) quals) (0, Map.empty)
    |> snd

/// Applies one `JOIN` clause against whatever's already in scope
/// (`sourcesSoFar`/`rowsSoFar`, built by the `FROM` table and any earlier
/// `JOIN`s in the same list): resolves the joined table, evaluates `join.On`
/// against every (left row, right row) pair, then combines the matched pairs
/// with whatever `join.Kind` needs added on top — `LEFT`/`RIGHT` also keep
/// the side that matched nothing, `NULL`-padded on the other side; `INNER`
/// and `CROSS` (the latter's `On` is always the literal-true `join.On` the
/// parser gives it) keep only the matches. Indices (not row references)
/// track which left/right rows matched anything, so outer-join padding is
/// correct even if two rows happen to be structurally equal. A nested loop,
/// not a hash join — fine at the row counts a migration/test table holds;
/// ponytail: revisit if a JOIN over a realistically large table ever shows
/// up in a profile.
and private applyJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (outer: EvalContext option)
    ((sourcesSoFar, rowsSoFar): (string * ColumnDef list) list * Value[] list)
    (join: Join)
    : Result<(string * ColumnDef list) list * Value[] list, QueryResult> =
    match resolveTableRef store dbName join.Table with
    | Error e -> Error e
    | Ok(joinColumns, joinRows) ->
        let joinQualifier = join.Table.Alias |> Option.defaultValue join.Table.Table
        let newSources = sourcesSoFar @ [ joinQualifier, joinColumns ]
        let qualifiers = qualifierRanges newSources
        let combinedColumnsSoFar = sourcesSoFar |> List.collect snd
        let leftNullPadding = combinedColumnsSoFar |> List.map (fun _ -> VNull) |> Array.ofList
        let rightNullPadding = joinColumns |> List.map (fun _ -> VNull) |> Array.ofList

        let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumnsSoFar @ joinColumns)) qualifiers outer

        let leftIndexed = rowsSoFar |> List.indexed
        let rightIndexed = joinRows |> List.indexed

        let pairs = [ for li, l in leftIndexed do for ri, r in rightIndexed -> li, ri, l, r ]

        pairs
        |> traverse (fun (li, ri, l, r) ->
            let combined = Array.append l r
            evalExpr (ctxFor combined) join.On |> Result.map (fun v -> li, ri, (truthy v = Some true), combined))
        |> Result.mapError Err
        |> Result.map (fun flagged ->
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

            newSources, combinedRows)

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
            let baseQualifier =
                match fromItem with
                | FromTable t -> t.Alias |> Option.defaultValue t.Table
                | FromSubquery(_, alias) -> alias

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
    : QueryResult * byte list =
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
    | Err(code, message), _, _ -> Err(code, message), []
    | Affected _, _, _ -> Err(1064, "UNION branch did not return a resultset"), []
    | ResultSet(firstCols, firstRows), firstTypes, firstTyped ->
        match rest |> List.fold combine (Ok(firstCols, List.zip firstRows firstTyped)) with
        | Error e -> e, []
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

            ResultSet(cols, sortedPaired |> List.map fst |> applyLimitOffset limit offset), firstTypes

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
        // `registry.Aggregates` — see `isAggregateCall`'s doc.
        let distinct, innerExpr = unwrapDistinct arg

        let separator =
            match rest with
            | [ Lit(VString s) ] -> s
            | _ -> ","

        evalNonNull innerExpr
        |> Result.map (fun nonNull ->
            let deduped = if distinct then List.distinct nonNull else nonNull

            if deduped.IsEmpty then
                VNull
            else
                deduped |> List.map (toText >> Option.defaultValue "") |> String.concat separator |> VString)
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
    // `RowNumberOver` never reaches a grouped SELECT — `runSelect` sends
    // any `RowNumberOver` projection to `runWindowedSelect` before the
    // GROUP BY/aggregate check that would otherwise land here even gets
    // evaluated (see `runSelect`'s dispatch) — but a leaf passthrough here
    // is the same "nothing to pre-evaluate" answer `Star`'s already is if
    // that ever changes.
    | RowNumberOver _
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
/// it unresolved fails with 1054. Walks every subexpression substituting a
/// `Col` that names a projection alias with that projection's own
/// expression; same shape as `substituteValuesFunc`'s rewrite.
and private resolveAliasesDeep (projections: Projection list) (expr: Expr) : Expr =
    let sub = resolveAliasesDeep projections

    match expr with
    | Col _ -> resolvePositionalOrAlias projections expr
    | FuncCall(name, args) -> FuncCall(name, args |> List.map sub)
    | BinOp(op, a, b) -> BinOp(op, sub a, sub b)
    | Not e -> Not(sub e)
    | IsNull e -> IsNull(sub e)
    | IsNotNull e -> IsNotNull(sub e)
    | IsTrue e -> IsTrue(sub e)
    | IsFalse e -> IsFalse(sub e)
    | Distinct e -> Distinct(sub e)
    | Like(e, p, cs) -> Like(sub e, sub p, cs)
    | Regexp(e, p) -> Regexp(sub e, sub p)
    | In(e, xs) -> In(sub e, xs |> List.map sub)
    | Between(e, lo, hi) -> Between(sub e, sub lo, sub hi)
    | Cast(e, ty) -> Cast(sub e, ty)
    | Case(subject, whens, elseBranch) ->
        Case(subject |> Option.map sub, whens |> List.map (fun (c, r) -> sub c, sub r), elseBranch |> Option.map sub)
    | Lit _
    | QualifiedCol _
    | Star _
    | RowNumberOver _
    // A subquery is its own scope — nothing inside it can be *this*
    // query's projection alias.
    | Exists _
    | Subquery _
    | InSubquery _ -> expr

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

    let resolveRef = resolvePositionalOrAlias select.Projections
    let groupExprs = select.GroupBy |> List.map resolveRef

    let matches (row: Value[]) : Result<bool, EvalError> =
        match select.Where with
        | None -> Ok true
        | Some expr -> evalExpr (ctxFor row) expr |> Result.map (fun v -> truthy v = Some true)

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
            // `resolveAliasesDeep` resolves a `SELECT ... AS alias`
            // anywhere inside the condition — `GROUP BY`/`ORDER BY` only
            // need `resolveRef`'s shallow, top-level check since a key
            // there is almost always a bare alias/column, but `HAVING`'s
            // condition is a full boolean expression (`HAVING c > 1`, not
            // just `HAVING c`), and MySQL allows a projection alias nested
            // anywhere inside it (e.g. Eloquent's `having('aggregate_alias',
            // ...)`).
            rewriteAggregates registry ctxFor groupRows (resolveAliasesDeep select.Projections h)
            |> Result.bind (evalExpr (ctxFor (representativeOf groupRows)))
            |> Result.map (fun v -> truthy v = Some true)

    let orderKeysOf (groupRows: Value[] list) : Result<Value list, EvalError> =
        let representative = representativeOf groupRows

        select.OrderBy
        |> traverse (fun (expr, _) ->
            rewriteAggregates registry ctxFor groupRows (resolveRef expr)
            |> Result.bind (evalExpr (ctxFor representative)))

    // Schema probe: type-checks WHERE/GROUP BY/HAVING/ORDER BY/projections
    // against an all-NULL row first, the same reasoning as `probeRow`'s
    // other use — an unknown column/function is a schema error independent
    // of whether any row happens to match, or a real `GROUP BY` happens to
    // produce zero groups.
    match matches (probeRow columns)
          |> Result.bind (fun _ -> groupExprs |> traverse (evalExpr (ctxFor (probeRow columns))) |> Result.map ignore)
          |> Result.bind (fun _ -> havingOk [])
          |> Result.bind (fun _ -> orderKeysOf [])
          |> Result.bind (fun _ -> projectGroup []) with
    | Error(code, message) -> Err(code, message), [], []
    | Ok probeProjected ->
        let colNames = probeProjected |> List.map fst

        match rows |> traverse (fun row -> matches row |> Result.map (fun keep -> if keep then Some row else None)) with
        | Error(code, message) -> Err(code, message), [], []
        | Ok maybeMatched ->
            let matched = maybeMatched |> List.choose id

            let buildGroups () : Result<Value[] list list, EvalError> =
                if groupExprs.IsEmpty then
                    Ok [ matched ]
                else
                    matched
                    |> traverse (fun row -> groupExprs |> traverse (evalExpr (ctxFor row)) |> Result.map (fun key -> key, row))
                    |> Result.map (fun keyed -> keyed |> List.groupBy fst |> List.map (snd >> List.map snd))

            match buildGroups () with
            | Error(code, message) -> Err(code, message), [], []
            | Ok groups ->
                let processGroup (groupRows: Value[] list) : Result<((string * Value) list * Value list) option, EvalError> =
                    havingOk groupRows
                    |> Result.bind (fun keep ->
                        if not keep then
                            Ok None
                        else
                            projectGroup groupRows
                            |> Result.bind (fun proj -> orderKeysOf groupRows |> Result.map (fun keys -> Some(proj, keys))))

                match groups |> traverse processGroup with
                | Error(code, message) -> Err(code, message), [], []
                | Ok maybeRows ->
                    let kept = maybeRows |> List.choose id

                    let sorted =
                        kept |> List.sortWith (fun (_, ka) (_, kb) -> compareByOrderKeys (List.map snd select.OrderBy) ka kb)

                    let paired =
                        sorted
                        |> List.map (fun (proj, _) -> proj |> List.map (snd >> toText), proj |> List.map snd |> Array.ofList)

                    let dedupedPaired = if select.Distinct then paired |> List.distinctBy fst else paired
                    let types = columnTypesOf (List.length colNames) (sorted |> List.map fst)
                    let limited = dedupedPaired |> applyLimitOffset select.Limit select.Offset
                    ResultSet(colNames, limited |> List.map fst), types, limited |> List.map snd

/// `SELECT ..., ROW_NUMBER() OVER (PARTITION BY p ORDER BY o) [AS alias]
/// FROM ...` (see `Ast.Expr.RowNumberOver`'s doc) — computed once here,
/// against the WHERE-filtered rows (real MySQL computes a window function
/// after `WHERE`, before `SELECT`/`ORDER BY`/`LIMIT`), then handed back to
/// the ordinary (non-windowed) `runSelect` path as one more real column: a
/// synthetic trailing `ColumnDef` appended to `columns`/each row, with
/// every projection's `Star`/`RowNumberOver` rewritten into plain `Col`
/// references first — expanding `Star` explicitly here (rather than
/// leaving it for `runSelect`'s own `Star` handling) keeps the synthetic
/// column out of a bare `SELECT *`'s expansion, so it shows up only where
/// `RowNumberOver` itself was written.
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
    match select.Projections |> List.tryPick (fst >> function RowNumberOver(p, o) -> Some(p, o) | _ -> None) with
    | None -> Err(1064, "runWindowedSelect called without a RowNumberOver projection"), [], []
    | Some(partitionBy, windowOrderBy) ->
        let columnIndex = columnIndexOf columns

        let ctxFor = contextFactory store registry dbName columnIndex qualifiers outer

        let matches (row: Value[]) : Result<bool, EvalError> =
            match select.Where with
            | None -> Ok true
            | Some expr -> evalExpr (ctxFor row) expr |> Result.map (fun v -> truthy v = Some true)

        let keyOf (exprs: Expr list) (row: Value[]) : Result<Value list, EvalError> =
            exprs |> traverse (evalExpr (ctxFor row))

        match rows |> traverse (fun row -> matches row |> Result.map (fun keep -> if keep then Some row else None)) with
        | Error(code, message) -> Err(code, message), [], []
        | Ok maybeMatched ->
            let matched = maybeMatched |> List.choose id

            let keyed =
                matched
                |> traverse (fun row ->
                    keyOf partitionBy row
                    |> Result.bind (fun partKey -> keyOf (windowOrderBy |> List.map fst) row |> Result.map (fun ordKey -> partKey, ordKey, row)))

            match keyed with
            | Error(code, message) -> Err(code, message), [], []
            | Ok keyed ->
                // One row number per partition, 1-based, assigned in the
                // window's own ORDER BY order — grouping by partition key
                // preserves each group's original relative order (`List.groupBy`
                // is stable), which only matters as a tiebreak among rows the
                // window ORDER BY doesn't otherwise distinguish.
                let rowNumberByIndex =
                    keyed
                    |> List.indexed
                    |> List.groupBy (fun (_, (partKey, _, _)) -> partKey)
                    |> List.collect (fun (_, group) ->
                        group
                        |> List.sortWith (fun (_, (_, ka, _)) (_, (_, kb, _)) -> compareByOrderKeys (windowOrderBy |> List.map snd) ka kb)
                        |> List.mapi (fun rank (origIdx, _) -> origIdx, int64 (rank + 1)))
                    |> Map.ofList

                let syntheticName = "__fsdb_row_number__"

                let syntheticColumn: ColumnDef =
                    { Name = syntheticName
                      Type = TBigInt false
                      Nullable = false
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None }

                let extendedColumns = columns @ [ syntheticColumn ]

                let extendedRows =
                    matched
                    |> List.mapi (fun idx row -> Array.append row [| VInt(Map.find idx rowNumberByIndex) |])

                let rewriteProjection (expr: Expr, aliasOpt: string option) : (Expr * string option) list =
                    match expr with
                    | RowNumberOver _ -> [ Col syntheticName, (aliasOpt |> Option.orElse (Some syntheticName)) ]
                    | Star None -> columns |> List.map (fun c -> Col c.Name, None)
                    | Star(Some qualifier) ->
                        match Map.tryFind (qualifier.ToLowerInvariant()) qualifiers with
                        | Some(cols, _) -> cols |> List.map (fun c -> Col c.Name, None)
                        | None -> [ expr, aliasOpt ]
                    | _ -> [ expr, aliasOpt ]

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
    elif projections |> List.exists (fst >> function RowNumberOver _ -> true | _ -> false) then
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

    // ORDER BY may name a `SELECT ... AS alias` or a 1-based projection
    // position (`SELECT COUNT(*) AS n FROM t ORDER BY n` / `ORDER BY 1`) —
    // resolve those first against the projection list before falling back
    // to `evalExpr`'s normal column lookup.
    let resolveOrderExpr = resolvePositionalOrAlias projections

    let matches (row: Value[]) : Result<bool, EvalError> =
        match whereExpr with
        | None -> Ok true
        | Some expr -> evalExpr (ctxFor row) expr |> Result.map (fun v -> truthy v = Some true)

    let orderKeys (row: Value[]) : Result<Value list, EvalError> =
        orderBy |> traverse (fun (expr, _) -> evalExpr (ctxFor row) (resolveOrderExpr expr))

    let projectRow (row: Value[]) : Result<(string * Value) list, EvalError> =
        projections
        |> traverse (evalProjection (ctxFor row) columns)
        |> Result.map List.concat

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
    (columnIndex: Map<string, int list>)
    (qualifiers: Map<string, ColumnDef list * int>)
    (assignments: (int * Expr) list)
    (row: Value[])
    : Result<Value[], StorageError> =
    let ctx = contextFactory store registry dbName columnIndex qualifiers None row

    assignments
    |> traverse (fun (idx, expr) -> evalExpr ctx expr |> Result.map (fun v -> idx, v))
    |> Result.mapError ExpressionError
    |> Result.map (fun idxVals ->
        let newRow = Array.copy row
        for idx, v in idxVals do
            newRow.[idx] <- v
        newRow)

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
            |> Result.bind (fun v -> coerceValue col v)
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
    // `VALUES(col)` only ever occurs directly in an `ON DUPLICATE KEY
    // UPDATE` assignment, never inside a subquery's own text — nothing to
    // substitute inside one, so it's left as-is like `Exists` always was.
    | Exists _
    | Subquery _
    | InSubquery _ -> expr

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
        lastInsertId, (runUnionStmt store registry dbName first rest orderBy limit offset |> fst)

    | Update(table, assignments, whereExpr) ->
        let db, table = splitQualified dbName table

        match scan store db table with
        | Error e -> lastInsertId, storageErr e
        | Ok(columns, rows) ->
            let columnIndex = columnIndexOf columns

            match assignments |> traverse (fun (name, expr) -> resolveColumn columns name |> Result.map (fun i -> i, expr)) with
            | Error e -> lastInsertId, storageErr e
            | Ok indexedAssignments ->
                let qualifiers = singleQualifier table columns

                let ctxFor = contextFactory store registry dbName columnIndex qualifiers None

                let check row =
                    match whereExpr with
                    | None -> Ok true
                    | Some expr -> evalExpr (ctxFor row) expr |> Result.map (fun v -> truthy v = Some true)

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
                    let predicate row = check row |> Result.mapError ExpressionError
                    let updater row = applyAssignments store registry dbName columnIndex qualifiers indexedAssignments row

                    match updateRows store db table predicate updater |> withGeneratedRecomputed store registry dbName db table with
                    | Ok affected -> lastInsertId, Affected(uint64 affected)
                    | Error e -> lastInsertId, storageErr e

    | Delete(table, whereExpr, limit) ->
        let db, table = splitQualified dbName table

        match scan store db table with
        | Error e -> lastInsertId, storageErr e
        | Ok(columns, _) ->
            let columnIndex = columnIndexOf columns

            let ctxFor = contextFactory store registry dbName columnIndex (singleQualifier table columns) None

            let check row =
                match whereExpr with
                | None -> Ok true
                | Some expr -> evalExpr (ctxFor row) expr |> Result.map (fun v -> truthy v = Some true)

            match check (probeRow columns) with
            | Error(code, message) -> lastInsertId, Err(code, message)
            | Ok _ ->
                // `LIMIT n` caps how many *matching* rows get deleted —
                // MySQL's own `DELETE ... LIMIT` (with no `ORDER BY`, which
                // this grammar doesn't accept here anyway) picks an
                // unspecified subset, so stopping at the first `n` matches
                // in scan order is a legal choice of "unspecified" too.
                let remaining = ref (limit |> Option.defaultValue System.Int32.MaxValue)

                let predicate row =
                    check row
                    |> Result.mapError ExpressionError
                    |> Result.map (fun isMatch ->
                        if isMatch && remaining.Value > 0 then
                            remaining.Value <- remaining.Value - 1
                            true
                        else
                            false)

                match deleteRows store db table predicate with
                | Ok affected -> lastInsertId, Affected(uint64 affected)
                | Error e -> lastInsertId, storageErr e
