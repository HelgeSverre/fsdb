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
/// a hardcoded name set here — the `registerAggregate` extension point is
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
    | Placeholder _ -> false
    | MatchAgainst(_, q, _) -> containsAggregate registry q
    | FuncCall(_, args) -> isAggregateCall registry expr || args |> List.exists (containsAggregate registry)
    | BinOp(_, a, b) -> containsAggregate registry a || containsAggregate registry b
    | Not e
    | IsNull e
    | IsNotNull e
    | IsTrue e
    | IsFalse e
    | Distinct e
    | OrderBy(e, _) -> containsAggregate registry e
    | Like(e, p, _, _) -> containsAggregate registry e || containsAggregate registry p
    | Regexp(e, p) -> containsAggregate registry e || containsAggregate registry p
    | In(e, xs) -> containsAggregate registry e || xs |> List.exists (containsAggregate registry)
    | Between(e, lo, hi) -> containsAggregate registry e || containsAggregate registry lo || containsAggregate registry hi
    | Cast(e, _) -> containsAggregate registry e
    | Collate(e, _) -> containsAggregate registry e
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
    | RankOver _
    | PercentRankOver _
    | NTileOver _
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
    | Placeholder _ -> []
    | MatchAgainst(_, q, _) -> collectWindowFuncs q
    | RowNumberOver _
    | LagOver _
    | RankOver _
    | PercentRankOver _
    | NTileOver _ -> [ expr ]
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
    | Collate(e, _) -> collectWindowFuncs e
    | Like(e, p, _, _) -> collectWindowFuncs e @ collectWindowFuncs p
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

/// Every bare `Col` name inside `expr` — same walk shape as
/// `collectWindowFuncs`. `runWindowedSelect` uses it to spot a `HAVING`
/// clause referencing a windowed projection's alias, which MySQL rejects
/// with a dedicated error (3594) rather than resolving.
/// A synthetic pre-pass column (`__fsdb_window_N__`/`__fsdb_match_N__`):
/// the declared type is only ever read for `Nullable` — the row's actual
/// `Value` drives the wire type downstream (see `columnTypesOf`).
let private syntheticColumn (name: string) (ty: ColumnType) (nullable: bool) : ColumnDef =
    { Name = name
      Type = ty
      Nullable = nullable
      Default = None
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      Generated = None
      Collation = None
      Charset = None
      OnUpdateCurrentTimestamp = false }

/// Every `MATCH ... AGAINST` node in an expression tree — the fulltext
/// pre-pass (`runFullTextSelect`) computes one whole-table score column per
/// distinct node, exactly like `collectWindowFuncs` feeds
/// `runWindowedSelect`.
let rec private collectMatchAgainst (expr: Expr) : Expr list =
    match expr with
    | MatchAgainst _ -> [ expr ]
    | FuncCall(_, args) -> args |> List.collect collectMatchAgainst
    | BinOp(_, a, b) -> collectMatchAgainst a @ collectMatchAgainst b
    | Not e
    | IsNull e
    | IsNotNull e
    | IsTrue e
    | IsFalse e
    | Distinct e
    | OrderBy(e, _)
    | Cast(e, _)
    | Collate(e, _) -> collectMatchAgainst e
    | Like(e, p, _, _) -> collectMatchAgainst e @ collectMatchAgainst p
    | Regexp(e, p) -> collectMatchAgainst e @ collectMatchAgainst p
    | In(e, xs) -> collectMatchAgainst e @ (xs |> List.collect collectMatchAgainst)
    | Between(e, lo, hi) -> collectMatchAgainst e @ collectMatchAgainst lo @ collectMatchAgainst hi
    | Case(subject, whens, elseBranch) ->
        (subject |> Option.map collectMatchAgainst |> Option.defaultValue [])
        @ (whens |> List.collect (fun (c, r) -> collectMatchAgainst c @ collectMatchAgainst r))
        @ (elseBranch |> Option.map collectMatchAgainst |> Option.defaultValue [])
    | Placeholder _
    | Lit _
    | Col _
    | QualifiedCol _
    | Star _
    | Exists _
    | Subquery _
    | InSubquery _
    | RowNumberOver _
    | LagOver _
    | RankOver _
    | PercentRankOver _
    | NTileOver _ -> []

let rec private collectColRefs (expr: Expr) : string list =
    match expr with
    | Col name -> [ name ]
    | MatchAgainst(cols, q, _) -> cols @ collectColRefs q
    | RowNumberOver _
    | LagOver _
    | RankOver _
    | PercentRankOver _
    | NTileOver _ -> []
    | FuncCall(_, args) -> args |> List.collect collectColRefs
    | BinOp(_, a, b) -> collectColRefs a @ collectColRefs b
    | Not e
    | IsNull e
    | IsNotNull e
    | IsTrue e
    | IsFalse e
    | Distinct e
    | OrderBy(e, _)
    | Cast(e, _) -> collectColRefs e
    | Collate(e, _) -> collectColRefs e
    | Like(e, p, _, _) -> collectColRefs e @ collectColRefs p
    | Regexp(e, p) -> collectColRefs e @ collectColRefs p
    | In(e, xs) -> collectColRefs e @ (xs |> List.collect collectColRefs)
    | Between(e, lo, hi) -> collectColRefs e @ collectColRefs lo @ collectColRefs hi
    | Case(subject, whens, elseBranch) ->
        (subject |> Option.map collectColRefs |> Option.defaultValue [])
        @ (whens |> List.collect (fun (c, r) -> collectColRefs c @ collectColRefs r))
        @ (elseBranch |> Option.map collectColRefs |> Option.defaultValue [])
    | Lit _
    | Placeholder _
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
        | Placeholder _ -> expr
        | MatchAgainst(cols, q, mode) -> MatchAgainst(cols, sub q, mode)
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
        | Collate(e, name) -> Collate(sub e, name)
        | Like(e, p, cs, esc) -> Like(sub e, sub p, cs, esc)
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
        | RankOver _
        | PercentRankOver _
        | NTileOver _
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
    | IntDiv -> "DIV"
    | NullSafeEq -> "<=>"

/// The column name MySQL gives an unaliased projection — exact for columns
/// and literals, a best-effort reconstruction of the source text for
/// everything else (real MySQL echoes the original expression text, which
/// the parser doesn't preserve).
let rec private exprLabel (expr: Expr) : string =
    match expr with
    | Lit v -> v |> toText |> Option.defaultValue "NULL"
    | MatchAgainst(cols, q, _) -> sprintf "match (%s) against (%s)" (String.concat "," cols) (exprLabel q)
    | Placeholder _ -> "?"
    | Col name -> name
    | QualifiedCol(_, col) -> col
    | FuncCall(name, args) -> sprintf "%s(%s)" (name.ToUpperInvariant()) (args |> List.map exprLabel |> String.concat ", ")
    | BinOp(op, a, b) -> sprintf "%s %s %s" (exprLabel a) (opSymbol op) (exprLabel b)
    | Not e -> sprintf "not(%s)" (exprLabel e)
    | IsNull e -> sprintf "(%s is null)" (exprLabel e)
    | IsNotNull e -> sprintf "(%s is not null)" (exprLabel e)
    | IsTrue e -> sprintf "(%s is true)" (exprLabel e)
    | IsFalse e -> sprintf "(%s is false)" (exprLabel e)
    | Like(e, p, _, _) -> sprintf "(%s like %s)" (exprLabel e) (exprLabel p)
    | Regexp(e, p) -> sprintf "(%s regexp %s)" (exprLabel e) (exprLabel p)
    | In(e, xs) -> sprintf "(%s in (%s))" (exprLabel e) (xs |> List.map exprLabel |> String.concat ",")
    | InSubquery(e, _) -> sprintf "(%s in (...))" (exprLabel e)
    | Between(e, lo, hi) -> sprintf "(%s between %s and %s)" (exprLabel e) (exprLabel lo) (exprLabel hi)
    | Cast(e, _) -> sprintf "cast(%s as ...)" (exprLabel e)
    | Collate(e, _) -> exprLabel e
    | Distinct e -> sprintf "distinct %s" (exprLabel e)
    | OrderBy(e, _) -> exprLabel e
    | Case _ -> "case"
    | Star None -> "*"
    | Star(Some q) -> sprintf "%s.*" q
    | RowNumberOver _ -> "row_number() over ()"
    | LagOver(e, offset, _, _) -> sprintf "%s(%s) over ()" (if offset < 0L then "lead" else "lag") (exprLabel e)
    | RankOver(dense, _, _) -> sprintf "%s() over ()" (if dense then "dense_rank" else "rank")
    | PercentRankOver _ -> "percent_rank() over ()"
    | NTileOver(n, _, _) -> sprintf "ntile(%d) over ()" n
    | Exists _ -> "exists"
    | Subquery _ -> "(...)"

/// Neither of these recurse into `evalExpr`, so they're plain top-level
/// `let`s rather than tied into its `rec ... and` group.
let private boolToValue (b: bool) : Value = VInt(if b then 1L else 0L)

/// LIKE under the engine's collation: case- and accent-insensitive per
/// character ('ä' LIKE 'a' is true), but never expanding — 'æ' LIKE 'ae' is
/// false while 'æ' = 'ae' is true, exactly as MySQL's per-character LIKE
/// behaves (both verified against 8.4). A small backtracking matcher
/// instead of `likeToRegex`: a regex can't fold per-character weight
/// classes without enumerating every accented variant.
/// `%` matches any run, `_` one character, and the escape character
/// (default backslash, as the parser leaves it unresolved in the pattern)
/// un-wildcards the character after it. `LIKE BINARY` (caseSensitive)
/// compares characters byte-for-byte.
let private likeMatch (escape: char) (charEq: char -> char -> bool) (subject: string) (pattern: string) : bool =
    let rec walk (si: int) (pi: int) : bool =
        if pi >= pattern.Length then
            si >= subject.Length
        else
            match pattern.[pi] with
            | c when c = escape && pi + 1 < pattern.Length && (pattern.[pi + 1] = '%' || pattern.[pi + 1] = '_') ->
                si < subject.Length && charEq subject.[si] pattern.[pi + 1] && walk (si + 1) (pi + 2)
            | '%' -> walk si (pi + 1) || (si < subject.Length && walk (si + 1) pi)
            | '_' -> si < subject.Length && walk (si + 1) (pi + 1)
            | p -> si < subject.Length && charEq subject.[si] p && walk (si + 1) (pi + 1)

    walk 0 0

let private likeOp (coll: Collation.Collation option) (caseSensitive: bool) (escape: char option) (subject: Value) (pattern: Value) : Value =
    match subject, pattern with
    | VNull, _
    | _, VNull -> VNull
    | _ ->
        let text = subject |> toText |> Option.defaultValue ""
        let pat = pattern |> toText |> Option.defaultValue ""
        let col = coll |> Option.defaultValue Collation.defaultCollation
        // MySQL's LIKE never trims trailing spaces, not even under a PAD
        // SPACE collation ('a ' LIKE 'a' is false under utf8mb4_unicode_ci,
        // MySQL-verified) — folding is per character only. LIKE BINARY is
        // byte-for-byte with no folding at all.
        let charEq = if caseSensitive then (=) else col.CharEquals
        boolToValue (likeMatch (escape |> Option.defaultValue '\\') charEq text pat)

/// `REGEXP`/`RLIKE` — case sensitivity follows the operand's collation
/// (case-insensitive under the usual `_ci` default, but case-sensitive
/// under `_bin`/`_cs`, same as `LIKE`/`=`); unlike `LIKE`'s translated
/// wildcard syntax, the pattern is
/// already a real (POSIX-flavored, close enough to .NET's for the common
/// cases Eloquent generates) regex, so it's handed to `Regex` directly
/// rather than through `likeToRegex`. Every match carries a `MatchTimeout`
/// so a catastrophically-backtracking pattern (`'(a+)+$'` against a long
/// non-matching subject) errors out instead of pinning a core forever.
/// No `Singleline`: MySQL's REGEXP `.` does not match a newline by default,
/// so .NET's default (also newline-excluding) `.` is the matching behavior.
let private regexpMatchTimeout = System.TimeSpan.FromSeconds 5.0

let private regexpOp (coll: Collation.Collation option) (subject: Value) (pattern: Value) : Result<Value, EvalError> =
    match subject, pattern with
    | VNull, _
    | _, VNull -> Ok VNull
    | _ ->
        let text = subject |> toText |> Option.defaultValue ""
        let pat = pattern |> toText |> Option.defaultValue ""
        let col = coll |> Option.defaultValue Collation.defaultCollation
        // No separate "REGEXP BINARY" AST flag the way `Like` has one —
        // case sensitivity is read straight off the collation by asking it
        // whether 'a' and 'A' fold together.
        let caseSensitive = not (col.CharEquals 'a' 'A')

        let opts =
            if caseSensitive then RegexOptions.None else RegexOptions.IgnoreCase

        try
            Ok(boolToValue (Regex.IsMatch(text, pat, opts, regexpMatchTimeout)))
        with
        | :? RegexMatchTimeoutException -> Error(1030, "Got error 'regexp match timed out' from regexp")
        | :? System.ArgumentException as ex -> Error(1139, sprintf "Got error '%s' from regexp" ex.Message)

/// The three pieces of context `evalExpr` needs to resolve a `Col`/`FuncCall`
/// against, bundled into one record rather than three loose parameters
/// threaded through every call site — aggregates add a fourth
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

/// The declared fractional-seconds precision (fsp) a temporal `ColumnType`
/// renders at — `Some 0..6` for the three fractional-second types, `None`
/// for anything else. `Some 0` (a bare `DATETIME`) still forces no fraction
/// at render, distinct from `None` (an expression/literal, which falls back
/// to `Value.toText`'s show-the-actual-digits behavior).
let private fspOfType (ty: ColumnType) : int option =
    match ty with
    | TDateTime fsp
    | TTimestamp fsp
    | TTime fsp -> Some fsp
    | _ -> None

/// The fsp an output *expression* renders at, so an explicit precision request
/// shows exactly its digits (an exact-second `.000000` and all): `CAST(x AS
/// DATETIME(6))` takes the cast's declared fsp, and `MAX`/`MIN` of a temporal
/// column inherit that column's fsp (they return one of the stored, already
/// rounded-to-fsp values unchanged). A plain column resolves through its
/// `ColumnDef`; everything else is `None` and falls back to `Value.toText`.
let rec private fspOfExpr (ctx: EvalContext) (expr: Expr) : int option =
    match expr with
    | Cast(_, ty) -> fspOfType ty
    | FuncCall(name, [ arg ]) when (let n = name.ToUpperInvariant() in n = "MAX" || n = "MIN") -> fspOfExpr ctx arg
    | FuncCall(name, args) when (let n = name.ToUpperInvariant() in n = "NOW" || n = "CURRENT_TIMESTAMP") ->
        // `NOW(N)` renders exactly N digits (matching the precision `nowFn`
        // rounds the clock to); bare `NOW()` renders none (precision 0).
        match args with
        | [ Lit v ] -> Some(Value.toDouble v |> int |> max 0 |> min 6)
        | _ -> Some 0
    | _ -> tryColumnDefForExpr ctx expr |> Option.bind (fun c -> fspOfType c.Type)

/// The declared fsp for each output column a projection produces — parallel,
/// in length and order, to the `(name, Value)` list `evalProjection` builds
/// for the same projection (`Star None` → every table column, `t.*` → that
/// qualifier's columns, a bare/qualified column → its own declared type, and
/// `None` for a literal/function/arithmetic that has no schema type). Threaded
/// into the resultset renderer so a `DATETIME(6)` column shows exactly six
/// digits where the bare `VDateTime` alone can't say how many. Mirrors
/// `evalProjection`/`resolveStarQualifier`'s own expansion so the two lists
/// line up column-for-column.
let rec private outputColumnFsps (ctx: EvalContext) (columns: ColumnDef list) (projections: Projection list) : int option list =
    let rec starQualifierCols (ctx: EvalContext) (qualifier: string) : ColumnDef list =
        match Map.tryFind (qualifier.ToLowerInvariant()) ctx.Qualifiers with
        | Some(cols, _) -> cols
        | None -> ctx.Outer |> Option.map (fun o -> starQualifierCols o qualifier) |> Option.defaultValue []

    projections
    |> List.collect (fun proj ->
        match proj with
        | Star None, _ -> columns |> List.map (fun c -> fspOfType c.Type)
        | Star(Some qualifier), _ -> starQualifierCols ctx qualifier |> List.map (fun c -> fspOfType c.Type)
        | expr, _ -> [ fspOfExpr ctx expr ])

/// The MySQL wire type to *override* each output column with when its
/// data-driven type (`columnTypesOf`, from the row `Value`s) disagrees with
/// its declared schema type — currently only a declared `TIME(N)` column,
/// which fsdb stores as a `VString` (so `mysqlTypeOf` would call it
/// `VAR_STRING`) but MySQL sends as its own `TIME` wire type. `Some TypeTime`
/// for a declared-`TIME` output column, `None` otherwise (keep the data-driven
/// type). Mirrors `outputColumnFsps`'s projection expansion so the lists line
/// up column-for-column. DATE/DATETIME already agree (their `Value`s carry the
/// right type), so they need no override here.
let private outputColumnWireOverrides (ctx: EvalContext) (columns: ColumnDef list) (projections: Projection list) : byte option list =
    let rec starQualifierCols (ctx: EvalContext) (qualifier: string) : ColumnDef list =
        match Map.tryFind (qualifier.ToLowerInvariant()) ctx.Qualifiers with
        | Some(cols, _) -> cols
        | None -> ctx.Outer |> Option.map (fun o -> starQualifierCols o qualifier) |> Option.defaultValue []

    let overrideOf (c: ColumnDef) =
        match c.Type with
        | TTime _ -> Some Value.TypeTime
        | _ -> None

    projections
    |> List.collect (fun proj ->
        match proj with
        | Star None, _ -> columns |> List.map overrideOf
        | Star(Some qualifier), _ -> starQualifierCols ctx qualifier |> List.map overrideOf
        | expr, _ -> [ tryColumnDefForExpr ctx expr |> Option.bind overrideOf ])

/// Applies `outputColumnWireOverrides` on top of a data-driven wire-type list,
/// keeping the data-driven type wherever there's no override. Falls back
/// wholesale on a length mismatch (both lists come from the same projection
/// expansion, so they shouldn't disagree) rather than throwing from `map2`.
let private applyWireOverrides (overrides: byte option list) (types: byte list) : byte list =
    if List.length overrides = List.length types then
        List.map2 (fun ov ty -> defaultArg ov ty) overrides types
    else
        types

/// Renders a projection's `(name, Value)` output columns to the resultset's
/// `string option list`, honoring each column's declared fsp (`fsps`, from
/// `outputColumnFsps`) — a temporal column shows exactly its fsp digits, and
/// everything else falls back to `Value.toText`. Falls back wholesale if the
/// two lists somehow disagree in length (they shouldn't — both come from the
/// same projection expansion), rather than throwing from `List.map2`.
let private renderOutputCols (fsps: int option list) (outputCols: (string * Value) list) : string option list =
    if List.length fsps = List.length outputCols then
        List.map2 (fun fspOpt (_, v) -> match fspOpt with Some fsp -> Value.toTextFsp fsp v | None -> Value.toText v) fsps outputCols
    else
        outputCols |> List.map (snd >> toText)

/// The collation a comparison involving `expr` resolves under: an
/// explicit `expr COLLATE name` tag wins, then a string-typed column's
/// declared `COLLATE`, then `None` (the caller falls back to the server
/// default — literal-to-literal semantics).
let private resolvedCollation (ctx: EvalContext) (expr: Expr) : Collation.Collation option =
    match expr with
    | Collate(_, name) -> Collation.tryFind name
    | _ ->
        match tryColumnDefForExpr ctx expr with
        | Some def when
            match def.Type with
            | TChar _ | TVarchar _ | TTinyText | TText | TMediumText | TLongText | TEnum _ | TSet _ | TJson -> true
            | _ -> false
            ->
            match def.Charset with
            // `CHARACTER SET binary` compares byte-for-byte; ascii/latin1
            // columns use their charset's default collation, approximated
            // by the server default ai_ci (latin1_swedish_ci/ascii_general_ci
            // fold case and most accents the same way for the common cases).
            | Some "binary" -> Collation.tryFind "utf8mb4_bin"
            | _ ->
                // A real column always carries a baked-in collation; a
                // stringy def with neither charset nor collation is a
                // synthetic derived-table column (`deriveColumns`), whose
                // value is whatever its source expression produced — so the
                // connection collation is the closest resolution, and for
                // literals the exact one.
                def.Collation
                |> Option.bind Collation.tryFind
                |> Option.orElseWith (fun () -> Some ctx.Store.ConnectionCollation)
        | _ -> None

/// The collation an equality-classified key resolves under: an explicit
/// `expr COLLATE`, then a string column's own collation, then the
/// connection collation (literals) — the same resolution comparisons use.
let private keyCollation (ctx: EvalContext) (expr: Expr) : Collation.Collation =
    resolvedCollation ctx expr |> Option.defaultValue ctx.Store.ConnectionCollation

/// A group/distinct/partition key normalized to collation equality: string
/// values become their collation's canonical key (`KeyOf` is injective per
/// collation), so structural equality of the normalized keys is exactly
/// MySQL's collation equality. Non-string values pass through untouched.
let private collationKeyOf (ctx: EvalContext) (expr: Expr) (value: Value) : Value =
    match value with
    | VString text -> VString((keyCollation ctx expr).KeyOf text)
    | _ -> value

/// Converts a displayed ENUM label into its 1-based declaration ordinal for
/// one ORDER BY key, and tags string-typed columns with the collation their
/// sort must use. The original row/projection value remains a string; only
/// the private sort key changes. Invalid labels cannot normally reach
/// storage, but ordinal 0 mirrors MySQL's sentinel ordering if one does.
let private orderValueForExpr (ctx: EvalContext) (expr: Expr) (value: Value) : Value * Collation.Collation option =
    match tryColumnDefForExpr ctx expr, value with
    | Some { Type = TEnum declared }, VString label ->
        let ordinal =
            declared
            |> List.tryFindIndex (fun item -> System.String.Equals(item, label, System.StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun index -> VInt(int64 (index + 1)))
            |> Option.defaultValue (VInt 0L)
        ordinal, None
    | _ ->
        match value with
        | VString _ -> value, Some(keyCollation ctx expr)
        | _ -> value, None

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

/// Splits an `ON`/`WHERE`-style expression into its top-level `AND`
/// conjuncts — `a AND b AND c` flattens to `[a; b; c]`; anything else (an
/// `OR`, a single predicate) is the one-element list `[expr]`. Only
/// conjunction commutes freely enough to split an equi-join's hash keys
/// from its residual filter (`extractEquiKeys` below) — `OR` can't, so a
/// disjunction stays one opaque conjunct and reports no keys.
let rec private conjuncts (expr: Expr) : Expr list =
    let rec loop acc expr =
        match expr with
        | BinOp(And, l, r) -> loop (loop acc l) r
        | _ -> expr :: acc

    List.rev (loop [] expr)

/// Splits a `JOIN ... ON` expression's `AND`-conjuncts into equi-join key
/// pairs — a `QualifiedCol = QualifiedCol` conjunct with one side resolving
/// into the columns already in scope and the other into the just-joined
/// table's own columns, as `(leftIndex, rightIndex)` with `rightIndex`
/// relative to the joined table's own row — and a residual list of
/// everything else (a range predicate, `a.x + 1 = b.y`, a same-side
/// equality like `a.x = a.y`, ...) that still needs per-candidate-pair
/// evaluation. An empty key list means "no usable equi-join key anywhere in
/// this ON clause" — `applyJoin`/`applyMutationJoin` fall back to the
/// nested loop entirely rather than build a `Dictionary` with nothing to
/// key it on.
let private extractEquiKeys
    (resolveQualified: string -> string -> (int * ColumnType) option)
    (leftColumnCount: int)
    (onExpr: Expr)
    : (int * int) list * Expr list =
    let classify (left: Expr) (right: Expr) =
        match left, right with
        | QualifiedCol(lq, lc), QualifiedCol(rq, rc) ->
            match resolveQualified lq lc, resolveQualified rq rc with
            | Some(ia, _), Some(ib, _) when ia < leftColumnCount && ib >= leftColumnCount -> Some(ia, ib - leftColumnCount)
            | Some(ia, _), Some(ib, _) when ib < leftColumnCount && ia >= leftColumnCount -> Some(ib, ia - leftColumnCount)
            | _ -> None
        | _ -> None

    let keys, residual =
        conjuncts onExpr
        |> List.fold
            (fun (keys, residual) conjunct ->
                match conjunct with
                | BinOp(Eq, l, r) ->
                    match classify l r with
                    | Some pair -> pair :: keys, residual
                    | None -> keys, conjunct :: residual
                | _ -> keys, conjunct :: residual)
            ([], [])

    List.rev keys, List.rev residual

/// Column types the hash join trusts to bucket safely: `Value.compare`
/// coerces *any* pair of these numeric types through `toDouble` (so `1 =
/// 1.0` joins), and folds any pair of these text types through the same
/// case/pad-insensitive collation `compareStrings` uses (so `'Alice' =
/// 'alice'` and `'a' = 'a '` join) — the "non-obvious correctness trap":
/// `keyClassOf` below only decides when it's safe to
/// *attempt* a bucket at all; `JoinKeyComparer` still does the real
/// equality check through `Value.compare` itself.
let private isJoinNumericType =
    function
    | TTinyInt _
    | TSmallInt _
    | TMediumInt _
    | TInt _
    | TBigInt _
    | TYear
    | TDecimal _
    | TDouble
    | TFloat -> true
    | _ -> false

let private isJoinTextType =
    function
    | TChar _
    | TVarchar _
    | TTinyText
    | TText
    | TMediumText
    | TLongText
    | TEnum _
    | TSet _ -> true
    | _ -> false

/// Whether an equi-join key's two column types are hash-safe together —
/// `Some true` (numeric bucket) or `Some false` (text bucket) when both
/// sides agree, `None` when they don't (a `DATE`, a `BLOB`/`JSON`, or a
/// numeric-vs-text mismatch whose cross-type coercion `Value.compare`
/// resolves case by case rather than uniformly). `None` sends the whole
/// join to the nested loop instead of a bucket that could disagree with
/// `Value.compare` about which rows tie.
let private keyClassOf (leftType: ColumnType) (rightType: ColumnType) : bool option =
    if isJoinNumericType leftType && isJoinNumericType rightType then Some true
    elif isJoinTextType leftType && isJoinTextType rightType then Some false
    else None

/// Runtime twin of `keyClassOf`: every value actually occupying a hash key
/// column must be `NULL` or the shape its declared class promises. Guards
/// the gap a *declared* type alone can't close — a derived table's every
/// column reports the synthetic `TText` (`deriveColumns`) no matter what
/// `Value` shape its rows really carry — so a mismatch here falls the whole
/// join back to the nested loop instead of building a bucket that silently
/// drops rows it can't classify.
let private valueMatchesKeyClass (isNumeric: bool) (v: Value) : bool =
    match v with
    | VNull -> true
    | VInt _
    | VDouble _
    | VDecimal _ -> isNumeric
    | VString _
    | VBytes _ -> not isNumeric
    | _ -> false

let private rowsMatchKeyClasses (classes: bool list) (keyIndices: int list) (rows: Value[] seq) : bool =
    rows
    |> Seq.forall (fun row -> List.forall2 (fun idx cls -> valueMatchesKeyClass cls row.[idx]) keyIndices classes)

/// One equi-join key (however many `AND`-ed `=` conjuncts contributed a
/// pair) read off one side's row, `None` if any column is `NULL` — SQL's
/// `NULL = anything` is never true, so a `NULL`-keyed row can never join
/// and is simply never added to (or looked up in) the hash bucket, the same
/// way it would silently fail every `Eq` check in the nested loop.
let private equiKeyOf (keyIndices: int[]) (row: Value[]) : Value[] option =
    match keyIndices with
    | [| i |] ->
        let v = row.[i]
        if v = VNull then None else Some [| v |]
    | _ ->
        let key = keyIndices |> Array.map (fun i -> row.[i])
        if key |> Array.exists (fun v -> v = VNull) then None else Some key

/// `Dictionary<Value[], _>` key comparer for the hash join's build/probe
/// keys — must agree with the equality the nested-loop fallback's `ON`
/// evaluation uses (each key column's own collation for strings, `Value.
/// compare`'s numeric cross-type coercion otherwise), not .NET's ordinal/
/// exact default, or matches the nested loop keeps (`'Alice' = 'alice'` on
/// an ai_ci column, `1 = 1.0`) would silently vanish from the hash path.
/// `GetHashCode` only needs to *agree* with `Equals` — every value that
/// could tie under a column's collation must land in the same bucket — so
/// it hashes a coarser normalized form (the column collation's folded hash,
/// or a `double`) while `Equals` still does the exact per-column check.
/// `keyClassOf`/`rowsMatchKeyClasses` keep this comparer from ever seeing a
/// pair `Value.compare` treats non-uniformly depending on which side each
/// value is on (a parseable-as-date string vs. a `DATE`).
type private JoinKeyComparer(collations: Collation.Collation list) =
    let bucketOf (i: int) (v: Value) : obj =
        match v with
        | VInt _
        | VDouble _
        | VDecimal _ -> box (Value.toDouble v)
        // The collation's hash folds case *and* accents (keeping trailing
        // spaces significant) — 'åge' and 'age' must land in the same bucket
        // for `Equals`' folded per-column check to ever see them. A hash,
        // not `KeyOf`: hashing needs no canonical form, so this avoids a
        // full sort-key materialization per string per row.
        | _ -> box ((collations.[i]).HashOf (Value.toText v |> Option.defaultValue ""))

    interface IEqualityComparer<Value[]> with
        member _.Equals(a: Value[], b: Value[]) =
            a
            |> Array.mapi (fun i x ->
                let y = b.[i]

                match x, y with
                // The key column's own collation decides string equality —
                // a bin column matches byte-for-byte, an ai_ci one folds
                // åge/age — the same rule the nested-loop path's `BinOp Eq`
                // applies, so the two join paths can never disagree.
                | VString sx, VString sy -> (collations.[i]).Equals sx sy
                | _ -> Value.compare x y = 0)
            |> Array.forall id

        member _.GetHashCode(a: Value[]) =
            match a with
            // Single-column key — the common case — hashes its one bucket
            // without the `Array.mapi`/`Array.fold` the multi-column shape
            // needs. Same hash function, no per-key array churn.
            | [| v |] -> (bucketOf 0 v).GetHashCode()
            | _ -> a |> Array.mapi (fun i v -> (bucketOf i v).GetHashCode()) |> Array.fold (fun h x -> h * 31 + x) 17

/// Like `Storage.traverse`, but over a lazy `seq` rather than a strict
/// `list` — short-circuits on the first `Error` without ever visiting (or
/// allocating a combined row for) later elements — and match-only: `f`
/// returns `None` for an element that doesn't belong in the result (the
/// non-equi `JOIN` fallback's `ON` came back false), and only `Some` is
/// added to the accumulator. That's the difference between "streams pairs
/// instead of materializing" actually being true and merely not true: the
/// non-equi fallback below hands this one `(left row, right row)` tuple per
/// candidate pair, and without the `option` filter here, every one of those
/// tuples (and its `Array.append`-combined row) still piled up in `acc`
/// before the caller's own `List.filter (fun (..., ok) -> ok)` threw most of
/// them away — an `a * b * bool` triple, not the pair itself, is small, but
/// at cross-product scale (a 10k x 50k `ON a.x + 1 = b.y` is 500M candidate
/// pairs) "small per element" was still O(left x right) overall. Filtering
/// inside the fold makes memory O(output) instead.
///
/// The non-equi `JOIN` fallback (`applyJoin`'s/`applyMutationJoin`'s "not
/// hashEligible" branch) is also the one caller with no build-side key to
/// hash on, so it's the case most likely to run long enough for
/// `queryCancellation` to matter (see that doc).
let private traverseSeq (f: 'a -> Result<'b option, 'e>) (xs: 'a seq) : Result<'b list, 'e> =
    let token = queryCancellation.Value
    let acc = ResizeArray()
    let mutable error = None
    let mutable i = 0
    use enumerator = xs.GetEnumerator()

    while error.IsNone && enumerator.MoveNext() do
        if i % cancellationCheckInterval = 0 then
            token.ThrowIfCancellationRequested()

        i <- i + 1

        match f enumerator.Current with
        | Ok(Some y) -> acc.Add y
        | Ok None -> ()
        | Error e -> error <- Some e

    match error with
    | Some e -> Error e
    | None -> Ok(List.ofSeq acc)

/// Bounded top-`capacity` selection for `ORDER BY ... LIMIT n [OFFSET m]`
/// (`capacity` = `n + m`, already clamped to a sane `int` by the caller):
/// evaluates each item through `f` and keeps only the `capacity` best in a
/// sorted `ResizeArray`, binary-search-inserting and dropping whichever
/// item currently sorts last the moment a new one beats it. `f`'s output is
/// never accumulated anywhere but this buffer, so peak memory is
/// O(capacity), not O(matched rows) — the buffer also starts empty and
/// grows with `Add`/`Insert` rather than preallocating `capacity` slots up
/// front, so a client-supplied `LIMIT` can't size an allocation before a
/// single row has been read. O(rows * log capacity) comparisons instead of
/// a full O(rows * log rows) sort holding every matched row. Still visits
/// every item in `items` (an `ORDER BY` that needs a real sort sees the
/// whole scan in real MySQL too — confirmed against the oracle: a poison
/// row past a `LIMIT`'s cut still raises once `ORDER BY` forces a
/// filesort), so this bounds what gets *kept*, not what gets *evaluated*.
let private boundedTopN (capacity: int) (cmp: 'b -> 'b -> int) (f: 'a -> Result<'b option, 'e>) (items: 'a seq) : Result<'b list, 'e> =
    if capacity <= 0 then
        Ok []
    else
        let token = queryCancellation.Value
        let buf = ResizeArray<'b>()
        let mutable error = None
        let mutable i = 0
        use enumerator = items.GetEnumerator()

        let insertSorted (item: 'b) =
            let mutable lo, hi = 0, buf.Count
            while lo < hi do
                let mid = (lo + hi) / 2
                if cmp buf.[mid] item <= 0 then lo <- mid + 1 else hi <- mid
            buf.Insert(lo, item)

        while error.IsNone && enumerator.MoveNext() do
            if i % cancellationCheckInterval = 0 then
                token.ThrowIfCancellationRequested()

            i <- i + 1

            match f enumerator.Current with
            | Ok(Some item) ->
                if buf.Count < capacity then
                    insertSorted item
                elif cmp item buf.[buf.Count - 1] < 0 then
                    buf.RemoveAt(buf.Count - 1)
                    insertSorted item
            | Ok None -> ()
            | Error e -> error <- Some e

        match error with
        | Some e -> Error e
        | None -> Ok(List.ofSeq buf)

/// The no-`ORDER BY` `SELECT` pipeline's `WHERE`/`DISTINCT`/`LIMIT`/`OFFSET`
/// stage: pulls rows from `xs` one at a time through `f` and stops the
/// enumerator outright the moment `offset + limit` survivors exist, rather
/// than visiting every row the way `traverseSeq` (which every `ORDER BY`
/// path still needs, since sorting requires seeing everything) does. This
/// is the actual LIMIT short-circuit — verified against a real MySQL oracle
/// that a row-level error past a `LIMIT`'s cut, with no `ORDER BY`, never
/// surfaces, because the row is never evaluated in the first place; this
/// mirrors that by never calling `f` on it. `distinct` dedupes on the
/// projected row's text key (`f`'s returned `string list`, the same
/// encoding `SELECT DISTINCT`'s materialized path already keyed on) before
/// counting a row toward `offset`/`limit`, so `DISTINCT ... LIMIT n` still
/// streams instead of falling back to a full materialize.
let private streamLimited
    (distinct: bool)
    (offset: int)
    (limit: int option)
    (f: 'a -> Result<(string option list * 'b) option, 'e>)
    (xs: 'a seq)
    : Result<(string option list * 'b) list, 'e> =
    let token = queryCancellation.Value
    let seen = if distinct then Some(System.Collections.Generic.HashSet<string option list>(HashIdentity.Structural)) else None
    let acc = ResizeArray()
    let mutable skipped = 0
    let mutable error : 'e option = None
    let mutable i = 0
    use enumerator = xs.GetEnumerator()

    let wantMore () = limit |> Option.forall (fun l -> acc.Count < l)

    while error.IsNone && wantMore () && enumerator.MoveNext() do
        if i % cancellationCheckInterval = 0 then
            token.ThrowIfCancellationRequested()

        i <- i + 1

        match f enumerator.Current with
        | Error e -> error <- Some e
        | Ok None -> ()
        | Ok(Some(key, value)) ->
            let isNew = seen |> Option.forall (fun s -> s.Add key)

            if isNew then
                if skipped < offset then skipped <- skipped + 1 else acc.Add(key, value)

    match error with
    | Some e -> Error e
    | None -> Ok(List.ofSeq acc)

/// The hash-join build/probe loop `applyJoin`/`applyMutationJoin` each need
/// on both sides of their own "build on the smaller side" choice: bucket
/// `build` by `buildKeyOf`'s key into a `Dictionary`, then walk `probe` and
/// yield one `(buildIndex, buildItem, probeIndex, probeItem)` per bucket
/// match. Fully generic over what a "build"/"probe" item actually *is* — a
/// plain `Value[]` row in `applyJoin`, an identity-tracking
/// `Value[] option list * Value[]` pair in `applyMutationJoin` — since it
/// only ever touches an item through `buildKeyOf`/`probeKeyOf`, never its
/// shape. One definition instead of the fill-then-probe loop written out
/// per build-side choice (three times across the two callers).
///
/// The build side fills a `Dictionary` eagerly (unavoidable — that's the
/// whole point of a hash join), but the probe side `yield`s matches lazily:
/// nothing past the last pair a caller actually pulls ever runs. Real only
/// when a caller stops pulling early — `applyJoin`'s `INNER`/`CROSS`,
/// no-residual-conjunct case hands this straight through to `runSelect`'s
/// `WHERE`/`LIMIT` streaming instead of collecting it into a list first.
/// Every other caller still drains the whole thing (`LEFT`/`RIGHT` needs
/// every match to know which rows *didn't* match; a residual `ON` conjunct
/// needs every candidate re-checked), so laziness costs those nothing
/// beyond a `seq`'s per-item overhead over a list comprehension's.
let private hashPairs
    (collations: Collation.Collation list)
    (buildKeyOf: 'b -> Value[] option)
    (probeKeyOf: 'p -> Value[] option)
    (build: (int * 'b) list)
    (probe: (int * 'p) seq)
    : (int * 'b * int * 'p) seq =
    let buckets = Dictionary<Value[], ResizeArray<int * 'b>>(JoinKeyComparer(collations))

    for buildIndex, buildItem in build do
        match buildKeyOf buildItem with
        | Some key ->
            match buckets.TryGetValue key with
            | true, bucket -> bucket.Add(buildIndex, buildItem)
            | false, _ ->
                let bucket = ResizeArray()
                bucket.Add(buildIndex, buildItem)
                buckets.Add(key, bucket)
        | None -> ()

    seq {
        for probeIndex, probeItem in probe do
            match probeKeyOf probeItem with
            | Some key ->
                match buckets.TryGetValue key with
                | true, bucket ->
                    for buildIndex, buildItem in bucket do
                        yield buildIndex, buildItem, probeIndex, probeItem
                | false, _ -> ()
            | None -> ()
    }

/// `hashPairs`' output, narrowed to the candidates whose residual (leftover,
/// non-equi-key) `ON` conjuncts actually hold — the tail every hash-join
/// branch needs after building its candidate list, shared instead of each
/// writing its own `traverse |> Result.mapError |> filter ok |> map`.
/// `extract` picks the piece `residualHolds` evaluates against out of a
/// candidate item that may carry more than just the combined row (see
/// `applyMutationJoin`'s identity-tracking shape).
let private keepMatches
    (residualHolds: 'c -> Result<bool, EvalError>)
    (extract: 'x -> 'c)
    (candidates: (int * int * 'x) list)
    : Result<(int * int * 'x) list, QueryResult> =
    candidates
    |> traverse (fun (li, ri, x) -> residualHolds (extract x) |> Result.map (fun ok -> if ok then Some(li, ri, x) else None))
    |> Result.mapError Err
    |> Result.map (List.choose id)

/// Evaluates one expression against one row. Three-valued logic throughout
/// (comparisons/AND/OR/NOT return `VNull` — SQL's "unknown" — rather than a
/// boolean whenever an operand is `VNull`, per `Value`'s helpers), function
/// calls resolve through `ctx.Registry` (error 1305 if unregistered), and a
/// bare column resolves through `ctx.ColumnIndex` (error 1054 if unknown).
/// MySQL compares an ENUM column against a number by declaration ordinal —
/// `WHERE status = 1`, and `= '1'` too (a quoted number that isn't itself a
/// declared label), match the first declared value, not the text "1".
/// `Some` is that label's 1-based ordinal when `expr` resolves to an
/// ENUM-typed column and `v` is its stored label; `None` lets the caller
/// fall back to the plain comparison.
let private enumOrdinalFor (ctx: EvalContext) (expr: Expr) (v: Value) : Value option =
    match tryColumnDefForExpr ctx expr with
    | Some { Type = TEnum declared } ->
        match v with
        | VString label ->
            declared
            |> List.tryFindIndex (fun item -> System.String.Equals(item, label, System.StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun idx -> VInt(int64 (idx + 1)))
        | _ -> None
    | _ -> None

/// The numeric side of an ENUM comparison: a real integer, or a
/// fully-numeric string (MySQL reads quoted numbers as indices too).
let private ordinalComparand (v: Value) : Value option =
    match v with
    | VInt _ -> Some v
    | VString s ->
        match System.Int64.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture) with
        | true, i -> Some(VInt i)
        | false, _ -> None
    | _ -> None

/// Resolves a `Value * Value` comparison the same way `BinOp`'s `=`/`<`/...
/// cases do: an ENUM column compares by ordinal against a numeric operand
/// (either side may be the column), and a string compare folds through the
/// operand's own `COLLATE`/column collation rather than a raw ordinal
/// `Value.compare`. Shared so `Between` (two `AND`ed range checks) gets
/// the same resolution as BinOp/`IN`.
let private resolvedCompare (ctx: EvalContext) (aExpr: Expr) (va: Value) (bExpr: Expr) (vb: Value) : int =
    let pa, pb =
        match enumOrdinalFor ctx aExpr va, ordinalComparand vb with
        | Some oa, Some nb -> oa, nb
        | _ ->
            match ordinalComparand va, enumOrdinalFor ctx bExpr vb with
            | Some na, Some ob -> na, ob
            | _ -> va, vb

    match pa, pb with
    | VString sa, VString sb ->
        match resolvedCollation ctx aExpr |> Option.orElseWith (fun () -> resolvedCollation ctx bExpr) with
        | Some col -> col.Compare sa sb
        | None -> ctx.Store.ConnectionCollation.Compare sa sb
    | _ -> Value.compare pa pb

let rec private evalExpr (ctx: EvalContext) (expr: Expr) : Result<Value, EvalError> =
    let eval = evalExpr ctx

    match expr with
    | Lit v -> Ok v
    // ponytail: MATCH is only pre-passed for single-table SELECTs (see
    // `runFullTextSelect`); anywhere else — UPDATE/DELETE WHERE, a joined or
    // derived source — real MySQL evaluates it, this reports 1191.
    | MatchAgainst _ -> Error(1191, "Can't find FULLTEXT index matching the column list")
    | Placeholder _ -> Error(1064, "unbound prepared-statement placeholder")
    | Star _ -> Error(1054, "Invalid use of '*'")
    // Only reachable if a `RowNumberOver`/`LagOver` ever escapes
    // `runWindowedSelect`'s rewrite (which substitutes every occurrence,
    // wherever it's nested, for a plain `Col` reference before any of this
    // runs) — real MySQL itself rejects a window function outside a
    // `SELECT`'s own projection/`ORDER BY` list the same way.
    | RowNumberOver _
    | LagOver _
    | RankOver _
    | PercentRankOver _
    | NTileOver _ -> Error(1054, "Invalid use of a group function")
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
                let compareWith (op: Op) : Value =
                    match va, vb with
                    | VNull, _
                    | _, VNull -> VNull
                    | _ ->
                        // ENUM columns compare by ordinal against a numeric
                        // operand (see `enumOrdinalFor`) — either side may be
                        // the column.
                        let pa, pb =
                            match enumOrdinalFor ctx a va, ordinalComparand vb with
                            | Some oa, Some nb -> oa, nb
                            | _ ->
                                match ordinalComparand va, enumOrdinalFor ctx b vb with
                                | Some na, Some ob -> na, ob
                                | _ -> va, vb

                        let pred =
                            match op with
                            | Eq -> fun (c: int) -> c = 0
                            | Neq -> fun c -> c <> 0
                            | Lt -> fun c -> c < 0
                            | Lte -> fun c -> c <= 0
                            | Gt -> fun c -> c > 0
                            | Gte -> fun c -> c >= 0
                            | _ -> fun _ -> false

                        match pa, pb with
                        | VString sa, VString sb ->
                            // A string column's COLLATE (or an explicit
                            // `expr COLLATE name` tag) drives the compare;
                            // two literals fall back to the server default.
                            match resolvedCollation ctx a |> Option.orElseWith (fun () -> resolvedCollation ctx b) with
                            | Some col ->
                                // Equality folds (ai_ci); the range
                                // operators use the full weight order.
                                let c =
                                    match op with
                                    | Eq
                                    | Neq -> if col.Equals sa sb then 0 else 1
                                    | _ -> col.Compare sa sb

                                boolToValue (pred c)
                            | None ->
                                // Two literals (or a string against a
                                // non-column expression): the connection
                                // collation decides.
                                let c =
                                    match op with
                                    | Eq
                                    | Neq -> if ctx.Store.ConnectionCollation.Equals sa sb then 0 else 1
                                    | _ -> ctx.Store.ConnectionCollation.Compare sa sb

                                boolToValue (pred c)
                        | _ -> boolToValue (pred (Value.compare pa pb))

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
                | IntDiv -> Value.intDiv va vb
                | Eq -> compareWith Eq
                | Neq -> compareWith Neq
                | Lt -> compareWith Lt
                | Lte -> compareWith Lte
                | Gt -> compareWith Gt
                | Gte -> compareWith Gte
                // Never unknown, unlike every other comparison here: both
                // sides `NULL` is true, either side (but not both) `NULL` is
                // false, otherwise it's a plain `Eq`.
                | NullSafeEq ->
                    match va, vb with
                    | VNull, VNull -> VInt 1L
                    | VNull, _
                    | _, VNull -> VInt 0L
                    | _ -> boolToValue (Value.compare va vb = 0)))
    | Like(e, p, caseSensitive, escape) ->
        eval e
        |> Result.bind (fun ve -> eval p |> Result.map (fun vp -> likeOp (Some(keyCollation ctx e)) caseSensitive escape ve vp))
    | Regexp(e, p) ->
        eval e
        |> Result.bind (fun ve -> eval p |> Result.bind (fun vp -> regexpOp (Some(keyCollation ctx e)) ve vp))
    | In(e, xs) ->
        eval e
        |> Result.bind (fun ve ->
            match ve with
            | VNull -> Ok VNull
            | _ ->
                xs
                |> traverse eval
                |> Result.map (fun vs ->
                    // `status IN (1, 2)` matches by declaration ordinal,
                    // same as the BinOp comparisons above.
                    let eq (v: Value) =
                        match enumOrdinalFor ctx e ve, ordinalComparand v with
                        | Some oe, Some nv -> Value.equals oe nv
                        | _ -> Value.equals ve v

                    if vs |> List.exists (fun v -> eq v = Some true) then
                        VInt 1L
                    elif vs |> List.exists (function VNull -> true | _ -> false) then
                        VNull
                    else
                        VInt 0L))
    | InSubquery(e, select) ->
        // `ve = NULL` can't short-circuit before running the subquery the
        // way the literal-list `In` case above does: `NULL IN (<empty
        // set>)` is FALSE (an OR over zero disjuncts), not UNKNOWN — only a
        // *non-empty* candidate set makes it UNKNOWN — and emptiness is
        // exactly what the subquery has to run to find out. `NOT IN` over
        // an empty subquery (a common "no matching rows yet" shape) would
        // otherwise wrongly drop every row whose `e` is `NULL`.
        eval e
        |> Result.bind (fun ve ->
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

                match ve with
                | VNull -> if candidates.IsEmpty then Ok(VInt 0L) else Ok VNull
                | _ ->
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
                    | _ ->
                        boolToValue (
                            resolvedCompare ctx e ve lo vlo >= 0
                            && resolvedCompare ctx e ve hi vhi <= 0
                        ))))
    | FuncCall(name, args) ->
        match Functions.lookup name ctx.Registry with
        | None -> Error(unknownFunction name)
        | Some fn -> args |> traverse eval |> Result.map fn
    // `expr COLLATE name` evaluates as its inner expression — the tag
    // only steers which collation comparisons resolve under.
    | Collate(e, _) -> eval e
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
                  Generated = None
                  Collation = None
                  Charset = None
                  OnUpdateCurrentTimestamp = false }

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
and private evalOrderKey (ctx: EvalContext) (expr: Expr) : Result<Value * Collation.Collation option, EvalError> =
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
        match scanList store tableDb tableRef.Table with
        | Error e -> Error(storageErr e)
        | Ok(columns, rows) -> Ok(columns, rows)

/// Pre-filters an `information_schema` scan by the WHERE's top-level
/// `col = 'literal'` equality conjuncts (`TABLE_SCHEMA`/`TABLE_NAME` is what
/// GUI clients send) — building every catalog row only for the executor to
/// discard all but one table's was the `COLUMNS` hotspot. Pure narrowing:
/// the full WHERE still runs over the result, so an equality this drops
/// nothing for costs nothing. `None` when the FROM isn't information_schema
/// or the WHERE has no usable conjunct (plain scan then).
/// ponytail: the pre-filter compares OrdinalIgnoreCase where the WHERE
/// proper compares ai_ci — an accented table name queried by its unaccented
/// spelling would be over-filtered; GUI clients echo names the server gave
/// them, so this stays until something real hits it.
and private tryInformationSchemaNarrow
    (store: Store)
    (dbName: string)
    (tableRef: TableRef)
    (where: Expr option)
    : (ColumnDef list * Value[] list) option =
    let tableDb = tableRef.Database |> Option.defaultValue dbName

    if not (System.String.Equals(tableDb, "information_schema", System.StringComparison.OrdinalIgnoreCase)) then
        None
    else
        let rec eqConjuncts (e: Expr) : (string * string) list =
            match e with
            | BinOp(And, a, b) -> eqConjuncts a @ eqConjuncts b
            | BinOp(Eq, Col c, Lit(VString v))
            | BinOp(Eq, Lit(VString v), Col c)
            | BinOp(Eq, QualifiedCol(_, c), Lit(VString v))
            | BinOp(Eq, Lit(VString v), QualifiedCol(_, c)) -> [ c, v ]
            | _ -> []

        match where |> Option.map eqConjuncts |> Option.defaultValue [] with
        | [] -> None
        | eqs ->
            InformationSchema.scan store.Catalog tableRef.Table
            |> Option.map (fun (cols, rows) ->
                let filters =
                    eqs
                    |> List.choose (fun (name, v) ->
                        cols
                        |> List.tryFindIndex (fun cd ->
                            System.String.Equals(cd.Name, name, System.StringComparison.OrdinalIgnoreCase))
                        |> Option.map (fun i -> i, v))

                let keep (row: Value[]) =
                    filters
                    |> List.forall (fun (i, v) ->
                        match row.[i] with
                        | VString s -> System.String.Equals(s, v, System.StringComparison.OrdinalIgnoreCase)
                        | _ -> false)

                cols, rows |> List.filter keep)

/// Synthetic, all-nullable-text `ColumnDef`s for a derived table's columns —
/// `runSelectStmt`'s own resultset has no real per-column `ColumnType` to
/// recover (its `byte list` is the MySQL *wire* type, not an `Ast`
/// `ColumnType`); `TText` is close enough since every consumer (comparisons,
/// `Value.compare`, ...) already coerces through `toText`/`toDouble` rather
/// than trusting the declared type. The *rows* underneath these synthetic
/// columns are still `runSelectStmt`'s real typed `Value`s, though — see
/// `resolveFromItem` below — only the column metadata is synthesized.
and private deriveColumns (names: string list) (collations: Collation.Collation list) : ColumnDef list =
    List.map2
        (fun n (col: Collation.Collation) ->
            { Name = n
              Type = TText
              Nullable = true
              Default = None
              AutoIncrement = false
              PrimaryKey = false
              Unique = false
              Generated = None
              Collation = Some col.Name
              Charset = None
              OnUpdateCurrentTimestamp = false })
        names
        collations

/// One `SELECT`'s per-output-column collations, resolved the same way
/// `runSelect`'s own DISTINCT keys are (`keyCollation` on the output column
/// name through the select's own context) — a bin column keeps åge/age/ÅGE
/// apart, an ai_ci one folds them, and a literal output (no source column to
/// resolve) falls back to the connection collation. Used by the UNION
/// dedupe and by `resolveFromItem`'s derived-table metadata, so both see the
/// same collation for the same select.
and private selectColumnCollations
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (names: string list)
    : Collation.Collation list =
    let columns, qualifier =
        match select.From with
        | Some fromItem ->
            match resolveFromItem store registry dbName fromItem with
            | Ok(cols, _) -> cols, fromItemQualifier fromItem
            | Error _ -> [], ""
        | None -> [], ""

    let qualifiers =
        if qualifier = "" then
            Map.empty
        else
            singleQualifier qualifier columns

    let ctx = contextFactory store registry dbName (columnIndexOf columns) qualifiers None (probeRow columns)

    names |> List.map (fun name -> keyCollation ctx (Col name))

/// The declared fsp per output column of one UNION branch — the same
/// probe-row context setup as `selectColumnCollations`, resolved through
/// `outputColumnFsps` so a `DATETIME(6)` column or `CAST(... AS DATETIME(1))`
/// reports its declared digits and a bare literal reports `None`. Falls back
/// to all-`None` when the projection expansion doesn't line up with the
/// branch's output columns (e.g. its `FROM` failed to resolve).
and private selectColumnFsps
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (names: string list)
    : int option list =
    let columns, qualifier =
        match select.From with
        | Some fromItem ->
            match resolveFromItem store registry dbName fromItem with
            | Ok(cols, _) -> cols, fromItemQualifier fromItem
            | Error _ -> [], ""
        | None -> [], ""

    let qualifiers =
        if qualifier = "" then
            Map.empty
        else
            singleQualifier qualifier columns

    let ctx = contextFactory store registry dbName (columnIndexOf columns) qualifiers None (probeRow columns)
    let fsps = outputColumnFsps ctx columns select.Projections

    if List.length fsps = List.length names then
        fsps
    else
        names |> List.map (fun _ -> None)

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
        | ResultSet(cols, _) ->
            // The derived columns carry their source collations — a literal
            // resolves to the connection collation, a bin column stays bin,
            // a union aggregates to its strictest branch — so outer
            // comparisons against them use the same collation MySQL would.
            let collations =
                match body with
                | PlainSelect select -> selectColumnCollations store registry dbName select cols
                | UnionSelect(first, rest, _, _, _) ->
                    (first :: (rest |> List.map snd))
                    |> List.map (fun branch -> selectColumnCollations store registry dbName branch cols)
                    |> List.reduce (List.map2 strictestUnionCollation)

            Ok(deriveColumns cols collations, typedRows)
        | Err(code, message) -> Error(Err(code, message))
        | Affected _ -> Error(Err(1064, "derived table did not return a resultset"))

/// MySQL's UNION result column collation aggregates across branches — the
/// strictest wins, so a bin column on any branch makes the combined column
/// byte-wise (verified: `SELECT a_ci ... UNION SELECT b_bin ...` keeps
/// 'åge'/'age' apart in both orders). Among non-bin collations the first
/// operand's stands (ponytail: MySQL's full UCA-level aggregation rules
/// aren't modeled).
and private strictestUnionCollation (a: Collation.Collation) (b: Collation.Collation) : Collation.Collation =
    if a.Name = "utf8mb4_bin" || b.Name = "utf8mb4_bin" then
        Collation.tryFind "utf8mb4_bin" |> Option.defaultValue a
    else
        a

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

/// `NATURAL JOIN`'s name set: every column name the two sides share,
/// matched case-insensitively (MySQL matches `c1(X)` against `c2(x)`), in
/// the left side's declaration order — which is also MySQL's output order
/// for the coalesced columns of a `SELECT *`.
and private naturalCommonNames (leftColumns: ColumnDef list) (rightColumns: ColumnDef list) : string list =
    leftColumns
    |> List.map (fun c -> c.Name)
    |> List.filter (fun name ->
        rightColumns |> List.exists (fun c -> System.String.Equals(c.Name, name, System.StringComparison.OrdinalIgnoreCase)))

/// The equi-key index pairs for a `NATURAL`/`USING` join: `names` resolved
/// case-insensitively against both sides' column lists. `NATURAL` passes
/// `naturalCommonNames`' intersection (never missing); `USING` passes its
/// explicit list, and a listed column absent from either side is MySQL's
/// 1054 "Unknown column ... in 'from clause'".
and private namedEquiKeys (leftColumns: ColumnDef list) (rightColumns: ColumnDef list) (names: string list) : Result<(int * int) list, QueryResult> =
    let indexOf (cols: ColumnDef list) (name: string) =
        cols |> List.tryFindIndex (fun c -> System.String.Equals(c.Name, name, System.StringComparison.OrdinalIgnoreCase))

    names
    |> traverse (fun name ->
        match indexOf leftColumns name, indexOf rightColumns name with
        | Some li, Some ri -> Ok(li, ri)
        | _ -> Error(Err(1054, sprintf "Unknown column '%s' in 'from clause'" name)))

/// Synthesizes the equality conjunction a `NATURAL`/`USING` join's
/// equi-keys imply, for the nested-loop fallback — the hash path matches
/// key values directly and never evaluates `join.On` (which is the
/// always-true literal for these join kinds), so without this the fallback
/// would pair every row. `qualifierOfLeft` maps a left index to its
/// source's qualifier so the synthesized refs stay unambiguous.
and private namedJoinOn
    (leftColumns: ColumnDef list)
    (qualifierOfLeft: int -> string)
    (rightQualifier: string)
    (rightColumns: ColumnDef list)
    (equiKeys: (int * int) list)
    : Expr =
    equiKeys
    |> List.fold
        (fun acc (li, ri) ->
            let cond =
                BinOp(Eq, QualifiedCol(qualifierOfLeft li, leftColumns.[li].Name), QualifiedCol(rightQualifier, rightColumns.[ri].Name))

            match acc with
            | Lit(VInt 1L) -> cond
            | _ -> BinOp(And, acc, cond))
        (Lit(VInt 1L))

/// Applies one `JOIN` clause against whatever's already in scope
/// (`sourcesSoFar`/`rowsSoFar`, built by the `FROM` table and any earlier
/// `JOIN`s in the same list): resolves the joined table, matches (left row,
/// right row) pairs against `join.On`, then combines the matched pairs with
/// whatever `join.Kind` needs added on top — `LEFT`/`RIGHT` also keep the
/// side that matched nothing, `NULL`-padded on the other side; `INNER` and
/// `CROSS` (the latter's `On` is always the literal-true `join.On` the
/// parser gives it) keep only the matches. Indices (not row references)
/// track which left/right rows matched anything, so outer-join padding is
/// correct even if two rows happen to be structurally equal.
///
/// Matching itself is `extractEquiKeys`' choice: an `ON` with at least one
/// extractable `col = col` equi-key (and every key's columns hash-safe
/// together, `keyClassOf`) builds a `Dictionary` on whichever side has
/// fewer rows and probes with the other, applying any residual conjuncts
/// only to the (already key-equal) candidates a bucket lookup returns.
/// Anything else — no equi-key at all, an `OR`, a range, `a.x + 1 = b.y` —
/// falls back to a lazy nested loop over every pair instead: still
/// evaluates `join.On` for each one, but as a `seq` (`traverseSeq`) rather
/// than a materialized cross-product `list`, which is what lets a
/// non-equi join at real table sizes actually finish instead of exhausting
/// memory.
/// The per-key-column collations `JoinKeyComparer` folds under — each key
/// column's own collation (the left side's when it declares one, else the
/// right's), the same resolution the nested-loop path's `ON` equality
/// applies, so the two join paths can never disagree. Non-string key
/// columns fall back to the default (unused for them — the comparer's
/// numeric branch hashes a `double`).
and private joinKeyCollations
    (left: ColumnDef list)
    (right: ColumnDef list)
    (equiKeys: (int * int) list)
    : Collation.Collation list =
    let colOf (c: ColumnDef) =
        if InformationSchema.isStringy c.Type then
            match c.Charset with
            | Some "binary" -> Collation.tryFind "utf8mb4_bin"
            | _ -> c.Collation |> Option.bind Collation.tryFind
        else
            None

    equiKeys
    |> List.map (fun (li, ri) ->
        colOf left.[li]
        |> Option.orElseWith (fun () -> colOf right.[ri])
        |> Option.defaultValue Collation.defaultCollation)

and private applyJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (outer: EvalContext option)
    ((sourcesSoFar, rowsSoFar): (string * ColumnDef list) list * Value[] seq)
    (join: Join)
    : Result<(string * ColumnDef list) list * Value[] seq * string list, QueryResult> =
    match resolveFromItem store registry dbName join.Table with
    | Error e -> Error e
    | Ok(joinColumns, joinRows) ->
        let joinQualifier = fromItemQualifier join.Table
        let newSources = sourcesSoFar @ [ joinQualifier, joinColumns ]
        let qualifiers = qualifierRanges newSources
        let combinedColumnsSoFar = sourcesSoFar |> List.collect snd
        let leftNullPadding = combinedColumnsSoFar |> List.map (fun _ -> VNull) |> Array.ofList
        let rightNullPadding = joinColumns |> List.map (fun _ -> VNull) |> Array.ofList

        // The column names this join coalesces (`SELECT *` shows them once):
        // `NATURAL`'s intersection, or `USING`'s explicit list — empty for a
        // plain `ON` join. Returned to `runSelectStmt` for its star/ref
        // rewrite.
        let coalesceNames =
            match join.Kind with
            | NaturalJoin
            | NaturalLeftJoin
            | NaturalRightJoin -> naturalCommonNames combinedColumnsSoFar joinColumns
            | _ -> join.Using

        let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumnsSoFar @ joinColumns)) qualifiers outer

        // Forces whatever `rowsSoFar` is (a previous `JOIN` in this same
        // list may have handed back a lazy seq — see `hashPairs`' doc) up
        // front: the hash join's build side needs random-access counting
        // either way. Every later use of the left side reads `leftIndexed`,
        // never `rowsSoFar` itself, so a chain of `JOIN`s only pays this
        // force once per link, not once per read.
        let leftIndexed = rowsSoFar |> List.ofSeq |> List.indexed
        // `rightCount` (not a materialized indexed list) sizes the build
        // decision; the streaming inner-join path below probes `joinRows`
        // directly with lazy indices, so the 50k (index, row) tuples are only
        // forced where an outer join or the nested-loop fallback needs them.
        let rightCount = joinRows |> Seq.length

        let resolveQualified (qualifier: string) (column: string) =
            qualifiers
            |> Map.tryFind (qualifier.ToLowerInvariant())
            |> Option.bind (fun (columns, offset) ->
                columns
                |> List.tryFindIndex (fun definition -> System.String.Equals(definition.Name, column, System.StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun index -> offset + index, columns.[index].Type))

        let buildCombinedRows (rightIndexed: (int * Value[]) list) (matched: (int * int * Value[]) list) =
            let matchedCombined = matched |> List.map (fun (_, _, c) -> c)
            let matchedLeft = matched |> List.map (fun (li, _, _) -> li) |> Set.ofList
            let matchedRight = matched |> List.map (fun (_, ri, _) -> ri) |> Set.ofList

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
                | CrossJoin
                | NaturalJoin -> matchedCombined
                | LeftJoin
                | NaturalLeftJoin -> matchedCombined @ leftOnly
                | RightJoin
                | NaturalRightJoin -> matchedCombined @ rightOnly

            newSources, combinedRows

        // `NATURAL`/`USING` equi-keys come straight from the coalesced
        // names; a plain `ON` join keeps the expression-based extraction.
        // An empty name set (a `NATURAL` join with no common columns) falls
        // through to `extractEquiKeys` on the always-true `On`, which finds
        // no keys and drops to the nested loop — MySQL's Cartesian product.
        let equiKeysResult =
            if coalesceNames.IsEmpty then
                Ok(extractEquiKeys resolveQualified combinedColumnsSoFar.Length join.On)
            else
                namedEquiKeys combinedColumnsSoFar joinColumns coalesceNames |> Result.map (fun keys -> keys, [])

        match equiKeysResult with
        | Error e -> Error e
        | Ok(equiKeys, residualConjuncts) ->
            // For a named (NATURAL/USING) join the nested-loop fallback
            // must still enforce the equi-keys — `join.On` is the
            // always-true literal for these kinds, so synthesize the
            // conjunction the hash path matches directly.
            let effectiveOn =
                if coalesceNames.IsEmpty then
                    join.On
                else
                    let qualifierOfLeft idx =
                        let rec find offset =
                            function
                            | [] -> failwith "applyJoin: left column index out of range"
                            | (q, cols) :: rest ->
                                if idx < offset + List.length cols then q
                                else find (offset + List.length cols) rest

                        find 0 sourcesSoFar

                    namedJoinOn combinedColumnsSoFar qualifierOfLeft joinQualifier joinColumns equiKeys

            let keyClasses =
                equiKeys |> List.map (fun (li, ri) -> keyClassOf combinedColumnsSoFar.[li].Type joinColumns.[ri].Type)

            let keyCollations = joinKeyCollations combinedColumnsSoFar joinColumns equiKeys

            let hashEligible =
                not equiKeys.IsEmpty
                && keyClasses |> List.forall Option.isSome
                && rowsMatchKeyClasses (keyClasses |> List.map Option.get) (equiKeys |> List.map fst) (leftIndexed |> Seq.map snd)
                && rowsMatchKeyClasses (keyClasses |> List.map Option.get) (equiKeys |> List.map snd) joinRows

            let residualHolds (combined: Value[]) : Result<bool, EvalError> =
                residualConjuncts
                |> traverse (fun c -> evalExpr { ctxFor combined with Clause = OnClause } c)
                |> Result.map (List.forall (fun v -> truthy v = Some true))

            if hashEligible then
                let leftKeyIndices = equiKeys |> List.map fst |> Array.ofList
                let rightKeyIndices = equiKeys |> List.map snd |> Array.ofList
                let buildOnLeft = leftIndexed.Length <= rightCount

                match join.Kind, residualConjuncts with
                | (InnerJoin | CrossJoin | NaturalJoin), [] ->
                    // Nothing here needs to see every match up front: `INNER`/
                    // `CROSS`/`NATURAL` keep only matched pairs (no
                    // unmatched-side padding to compute, unlike `LEFT`/
                    // `RIGHT`), and an empty residual means every hash-bucket
                    // hit is already a real match (no `ON`-conjunct re-check
                    // that could itself fail past the point a caller stops
                    // pulling). So this is `hashPairs`' lazy `seq` straight
                    // through, `Array.append`-combined but not collected —
                    // `runSelect`'s `WHERE`/`LIMIT` streaming decides how
                    // much of it ever actually runs.
                    let combined : Value[] seq =
                        if buildOnLeft then
                            hashPairs keyCollations (equiKeyOf leftKeyIndices) (equiKeyOf rightKeyIndices) leftIndexed (joinRows |> Seq.indexed)
                            |> Seq.map (fun (_, l, _, r) -> Array.append l r)
                        else
                            hashPairs keyCollations (equiKeyOf rightKeyIndices) (equiKeyOf leftKeyIndices) (joinRows |> Seq.indexed |> List.ofSeq) leftIndexed
                            |> Seq.map (fun (_, r, _, l) -> Array.append l r)

                    Ok(newSources, combined, coalesceNames)
                | _ ->
                    let rightIndexed = joinRows |> Seq.indexed |> List.ofSeq

                    let candidates : (int * int * Value[]) list =
                        if buildOnLeft then
                            hashPairs keyCollations (equiKeyOf leftKeyIndices) (equiKeyOf rightKeyIndices) leftIndexed rightIndexed
                            |> Seq.map (fun (li, l, ri, r) -> li, ri, Array.append l r)
                            |> List.ofSeq
                        else
                            hashPairs keyCollations (equiKeyOf rightKeyIndices) (equiKeyOf leftKeyIndices) rightIndexed leftIndexed
                            |> Seq.map (fun (ri, r, li, l) -> li, ri, Array.append l r)
                            |> List.ofSeq

                    candidates
                    |> keepMatches residualHolds id
                    |> Result.map (buildCombinedRows rightIndexed >> fun (s, r) -> s, r :> Value[] seq, coalesceNames)
            else
                let rightIndexed = joinRows |> Seq.indexed |> List.ofSeq

                let pairs = seq { for li, l in leftIndexed do for ri, r in rightIndexed -> li, ri, l, r }

                pairs
                |> traverseSeq (fun (li, ri, l, r) ->
                    let combined = Array.append l r

                    evalExpr { ctxFor combined with Clause = OnClause } effectiveOn
                    |> Result.map (fun v -> if truthy v = Some true then Some(li, ri, combined) else None))
                |> Result.mapError Err
                |> Result.map (buildCombinedRows rightIndexed >> fun (s, r) -> s, r :> Value[] seq, coalesceNames)

/// Like `applyJoin`, but for a multi-table `UPDATE`/`DELETE ... JOIN`
/// rather than a `SELECT`: alongside the flattened row `evalExpr` needs,
/// each combined row also keeps every source's own physical `Value[]`
/// (`None` on an outer-join side that matched nothing — there's no real row
/// there to update/delete). A separate, smaller near-duplicate of
/// `applyJoin`'s equi-key hash-join/lazy-nested-loop split (see its doc)
/// rather than threading identity through the shared `SELECT` path — that
/// path is the hot, heavily-tested read path; duplicating the join loop
/// here keeps it untouched. ponytail: real tables only (no derived-table
/// join source) — MySQL itself doesn't allow a derived table as a
/// multi-table `UPDATE`/`DELETE` target anyway; a derived-table join
/// *source* (`UPDATE t1 JOIN (SELECT ...) dt ON ...`) is real MySQL syntax
/// this rejects with 1064 rather than silently mishandling, add it if a
/// migration's `UPDATE`/`DELETE` actually needs one.
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
            let leftFlatRows = rowsSoFar |> List.map snd

            let resolveQualified (qualifier: string) (column: string) =
                qualifiers
                |> Map.tryFind (qualifier.ToLowerInvariant())
                |> Option.bind (fun (columns, offset) ->
                    columns
                    |> List.tryFindIndex (fun definition -> System.String.Equals(definition.Name, column, System.StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun index -> offset + index, columns.[index].Type))

            let buildCombinedRows (matched: (int * int * (Value[] option list * Value[])) list) =
                let matchedRows = matched |> List.map (fun (_, _, row) -> row)
                let matchedLeft = matched |> List.map (fun (li, _, _) -> li) |> Set.ofList
                let matchedRight = matched |> List.map (fun (_, ri, _) -> ri) |> Set.ofList

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
                    | CrossJoin
                    | NaturalJoin -> matchedRows
                    | LeftJoin
                    | NaturalLeftJoin -> matchedRows @ leftOnly
                    | RightJoin
                    | NaturalRightJoin -> matchedRows @ rightOnly

                newSources, rows

            let coalesceNames =
                match join.Kind with
                | NaturalJoin
                | NaturalLeftJoin
                | NaturalRightJoin -> naturalCommonNames combinedColumnsSoFar joinColumns
                | _ -> join.Using

            let equiKeysResult =
                if coalesceNames.IsEmpty then
                    Ok(extractEquiKeys resolveQualified combinedColumnsSoFar.Length join.On)
                else
                    namedEquiKeys combinedColumnsSoFar joinColumns coalesceNames |> Result.map (fun keys -> keys, [])

            match equiKeysResult with
            | Error e -> Error e
            | Ok(equiKeys, residualConjuncts) ->
                // For a named (NATURAL/USING) join the nested-loop fallback
                // must still enforce the equi-keys — `join.On` is the
                // always-true literal for these kinds, so synthesize the
                // conjunction the hash path matches directly.
                let effectiveOn =
                    if coalesceNames.IsEmpty then
                        join.On
                    else
                        let qualifierOfLeft idx =
                            let rec find offset =
                                function
                                | [] -> failwith "applyMutationJoin: left column index out of range"
                                | (q, _, cols) :: rest ->
                                    if idx < offset + List.length cols then q
                                    else find (offset + List.length cols) rest

                            find 0 sourcesSoFar

                        namedJoinOn combinedColumnsSoFar qualifierOfLeft joinQualifier joinColumns equiKeys

                let keyClasses =
                    equiKeys |> List.map (fun (li, ri) -> keyClassOf combinedColumnsSoFar.[li].Type joinColumns.[ri].Type)

                let keyCollations = joinKeyCollations combinedColumnsSoFar joinColumns equiKeys

                let hashEligible =
                    not equiKeys.IsEmpty
                    && keyClasses |> List.forall Option.isSome
                    && rowsMatchKeyClasses (keyClasses |> List.map Option.get) (equiKeys |> List.map fst) leftFlatRows
                    && rowsMatchKeyClasses (keyClasses |> List.map Option.get) (equiKeys |> List.map snd) joinRows

                let residualHolds (combinedFlat: Value[]) : Result<bool, EvalError> =
                    residualConjuncts
                    |> traverse (fun c -> evalExpr { ctxFor combinedFlat with Clause = OnClause } c)
                    |> Result.map (List.forall (fun v -> truthy v = Some true))

                if hashEligible then
                    let leftKeyIndices = equiKeys |> List.map fst |> Array.ofList
                    let rightKeyIndices = equiKeys |> List.map snd |> Array.ofList
                    let buildOnLeft = rowsSoFar.Length <= joinRows.Length

                    // Left rows carry their identity list alongside the flat
                    // row `equiKeyOf` needs; right rows (always a real scanned
                    // table — see the ponytail note above) are the flat row
                    // itself. `hashPairs` is generic over both shapes at once —
                    // each side just supplies its own key extractor — so
                    // neither side pays to wrap/unwrap the other's.
                    let leftKeyOf (lIdent: Value[] option list, lFlat: Value[]) = equiKeyOf leftKeyIndices lFlat
                    let rightKeyOf (r: Value[]) = equiKeyOf rightKeyIndices r

                    let candidates : (int * int * (Value[] option list * Value[])) list =
                        if buildOnLeft then
                            hashPairs keyCollations leftKeyOf rightKeyOf leftIndexed rightIndexed
                            |> Seq.map (fun (li, (lIdent, lFlat), ri, r) -> li, ri, (lIdent @ [ Some r ], Array.append lFlat r))
                            |> List.ofSeq
                        else
                            hashPairs keyCollations rightKeyOf leftKeyOf rightIndexed leftIndexed
                            |> Seq.map (fun (ri, r, li, (lIdent, lFlat)) -> li, ri, (lIdent @ [ Some r ], Array.append lFlat r))
                            |> List.ofSeq

                    candidates |> keepMatches residualHolds snd |> Result.map buildCombinedRows
                else
                    let pairs = seq { for li, l in leftIndexed do for ri, r in rightIndexed -> li, ri, l, r }

                    pairs
                    |> traverseSeq (fun (li, ri, (lIdent, lFlat), r) ->
                        let combinedFlat = Array.append lFlat r

                        evalExpr { ctxFor combinedFlat with Clause = OnClause } effectiveOn
                        |> Result.map (fun v -> if truthy v = Some true then Some(li, ri, (lIdent @ [ Some r ], combinedFlat)) else None))
                    |> Result.mapError Err
                    |> Result.map buildCombinedRows

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
/// Replaces every unqualified `Col` whose (case-insensitive) name is in
/// `map` with `map`'s entry — the COALESCE expression a `NATURAL`/`USING`
/// join synthesized for that common column, so WHERE/ORDER BY/GROUP
/// BY/HAVING see MySQL's coalesced value instead of a 1052-ambiguous
/// physical column. Subquery bodies (`Exists`/`Subquery`) are their own
/// scope — their own `runSelectStmt` applies their own rewrite, and walking
/// into them here could capture an inner column with an outer coalesce.
and private rewriteCoalescedCols (map: Map<string, Expr>) (expr: Expr) : Expr =
    let sub = rewriteCoalescedCols map

    match expr with
    | Placeholder _ -> expr
    | MatchAgainst(cols, q, mode) -> MatchAgainst(cols, sub q, mode)
    | Col name ->
        match Map.tryFind (name.ToLowerInvariant()) map with
        | Some repl -> repl
        | None -> expr
    | QualifiedCol _
    | Lit _
    | Star _
    | Exists _
    | Subquery _
    | RowNumberOver _
    | LagOver _
    | RankOver _
    | PercentRankOver _
    | NTileOver _ -> expr
    | BinOp(op, a, b) -> BinOp(op, sub a, sub b)
    | Not e -> Not(sub e)
    | IsNull e -> IsNull(sub e)
    | IsNotNull e -> IsNotNull(sub e)
    | IsTrue e -> IsTrue(sub e)
    | IsFalse e -> IsFalse(sub e)
    | Like(e, p, cs, esc) -> Like(sub e, sub p, cs, esc)
    | Regexp(e, p) -> Regexp(sub e, sub p)
    | In(e, xs) -> In(sub e, xs |> List.map sub)
    | InSubquery(e, s) -> InSubquery(sub e, s)
    | Between(e, lo, hi) -> Between(sub e, sub lo, sub hi)
    | FuncCall(name, args) -> FuncCall(name, args |> List.map sub)
    | Distinct e -> Distinct(sub e)
    | OrderBy(e, dir) -> OrderBy(sub e, dir)
    | Cast(e, ty) -> Cast(sub e, ty)
    | Collate(e, name) -> Collate(sub e, name)
    | Case(subject, whens, elseBranch) ->
        Case(subject |> Option.map sub, whens |> List.map (fun (c, r) -> sub c, sub r), elseBranch |> Option.map sub)

/// Rewrites a select whose joins coalesce columns (`NATURAL`/`USING`) into
/// MySQL's exact shape: `SELECT *` expands to the coalesced common columns
/// first (`COALESCE` of every source occurrence, left to right), then the
/// left side's remaining columns, then the right's — except `RIGHT` joins,
/// which put the right side's remaining columns before the left's (verified
/// against MySQL 8.4). Chained joins fold left-to-right, each coalescing
/// join moving its new commons to the front. Unqualified `Col` references
/// to a coalesced name become the same COALESCE expression.
///
/// `sources` is the resolved `(qualifier, columns)` per source in FROM
/// order (one more entry than `joins`); `namesPerJoin` is `applyJoin`'s
/// coalesced-name list per join, same order.
and private rewriteNaturalSelect
    (select: SelectStmt)
    (sources: (string * ColumnDef list) list)
    (joins: Join list)
    (namesPerJoin: string list list)
    : SelectStmt =
    let qualified (qualifier: string) (name: string) = QualifiedCol(qualifier, name)

    let baseQualifier, baseColumns = List.head sources

    // The ordered logical column plan: (output name, expr).
    let plan =
        (List.zip joins namesPerJoin, List.tail sources)
        ||> List.fold2
                (fun plan (join, names) rightSource ->
                    let rightQualifier, rightCols = rightSource

                    if names.IsEmpty then
                        plan @ (rightCols |> List.map (fun c -> c.Name, qualified rightQualifier c.Name))
                    else
                        let common = names |> List.map (fun n -> n.ToLowerInvariant()) |> Set.ofList
                        let isCommon ((name: string), _) = common.Contains(name.ToLowerInvariant())

                        let commons =
                            plan
                            |> List.filter isCommon
                            |> List.map (fun (name, expr) -> name, FuncCall("COALESCE", [ expr; qualified rightQualifier name ]))

                        let leftRest = plan |> List.filter (isCommon >> not)

                        let rightRest =
                            rightCols
                            |> List.filter (fun c -> not (common.Contains(c.Name.ToLowerInvariant())))
                            |> List.map (fun c -> c.Name, qualified rightQualifier c.Name)

                        match join.Kind with
                        | RightJoin
                        | NaturalRightJoin -> commons @ rightRest @ leftRest
                        | _ -> commons @ leftRest @ rightRest)
                (baseColumns |> List.map (fun c -> c.Name, qualified baseQualifier c.Name))

    // Unqualified refs to a coalesced name resolve to the COALESCE over
    // every source occurrence of that name (ponytail: a name re-added by a
    // later plain join with the same column would be silently included —
    // MySQL errors 1052 there; ORM-shaped queries never hit it).
    let coalesceMap =
        namesPerJoin
        |> List.concat
        |> List.distinctBy (fun n -> n.ToLowerInvariant())
        |> List.map (fun name ->
            let occurrences =
                sources
                |> List.filter (fun (_, cols) ->
                    cols |> List.exists (fun c -> System.String.Equals(c.Name, name, System.StringComparison.OrdinalIgnoreCase)))
                |> List.map (fun (q, _) -> qualified q name)

            name.ToLowerInvariant(), FuncCall("COALESCE", occurrences))
        |> Map.ofList

    let rewriteExpr e = rewriteCoalescedCols coalesceMap e

    let projections =
        select.Projections
        |> List.collect (fun (expr, alias) ->
            match expr with
            | Star None -> plan |> List.map (fun (name, e) -> e, Some name)
            | Star(Some _) -> [ expr, alias ]
            | _ -> [ rewriteExpr expr, alias ])

    { select with
        Projections = projections
        Where = select.Where |> Option.map rewriteExpr
        GroupBy = select.GroupBy |> List.map rewriteExpr
        Having = select.Having |> Option.map rewriteExpr
        OrderBy = select.OrderBy |> List.map (fun (e, d) -> rewriteExpr e, d) }

and private runSelectStmt
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * byte list * Value[] list =
    let matchNodes =
        (select.Projections |> List.collect (fst >> collectMatchAgainst))
        @ (select.Where |> Option.map collectMatchAgainst |> Option.defaultValue [])
        @ (select.Having |> Option.map collectMatchAgainst |> Option.defaultValue [])
        @ (select.OrderBy |> List.collect (fst >> collectMatchAgainst))
        @ (select.GroupBy |> List.collect collectMatchAgainst)
        |> List.distinct

    match select.From with
    | _ when not matchNodes.IsEmpty -> runFullTextSelect store registry dbName select matchNodes outer
    | None -> runSelect store registry dbName [] Map.empty [ [||] ] select outer
    | Some fromItem ->
        // A single real table, no `JOIN`, narrows to its PK/UNIQUE index's
        // candidates instead of a full `resolveFromItem` scan when the
        // WHERE clause allows it (see `tryPointLookup`'s doc) — pure
        // narrowing, so everything below (`applyJoin`, `runSelect`'s own
        // WHERE/ORDER BY/LIMIT/GROUP BY) runs completely unmodified over
        // whatever this produces.
        let resolved =
            match fromItem, select.Joins with
            | FromTable tref, [] ->
                tryPointLookup store dbName tref select.Where
                |> Option.map (fun (cols, rows) -> Ok(cols, rows |> List.map snd))
                |> Option.orElseWith (fun () -> tryInformationSchemaNarrow store dbName tref select.Where |> Option.map Ok)
                |> Option.defaultWith (fun () -> resolveFromItem store registry dbName fromItem)
            | _ -> resolveFromItem store registry dbName fromItem

        match resolved with
        | Error e -> e, [], []
        | Ok(baseColumns, baseRows) ->
            let baseQualifier = fromItemQualifier fromItem

            let initial : Result<((string * ColumnDef list) list * Value[] seq) * string list list, QueryResult> =
                Ok(([ baseQualifier, baseColumns ], baseRows), [])

            match
                select.Joins
                |> List.fold
                    (fun acc join ->
                        acc
                        |> Result.bind (fun ((sources, rows), namesPerJoin) ->
                            applyJoin store registry dbName outer (sources, rows) join
                            |> Result.map (fun (sources', rows', names) -> (sources', rows'), names :: namesPerJoin)))
                    initial
            with
            | Error e -> e, [], []
            | Ok((sources, rows), namesPerJoinRev) ->
                let namesPerJoin = List.rev namesPerJoinRev

                let select' =
                    if namesPerJoin |> List.forall List.isEmpty then
                        select
                    else
                        rewriteNaturalSelect select sources select.Joins namesPerJoin

                runSelect store registry dbName (sources |> List.collect snd) (qualifierRanges sources) rows select' outer

/// A WHERE expression's top-level `AND` chain flattened into its conjuncts
/// — `a AND b AND c` (any nesting/associativity) yields `[a; b; c]`; any
/// other shape (an `OR`, a single comparison, ...) is its own one-element
/// list. `tryPointLookup` uses this to find an indexed equality anywhere
/// among several ANDed conditions, not just when it's the *whole* WHERE.
and private flattenAnd (expr: Expr) : Expr list =
    match expr with
    | BinOp(And, l, r) -> flattenAnd l @ flattenAnd r
    | e -> [ e ]

/// `SELECT`'s single-table, no-`JOIN` `FROM`, narrowed to its PK/UNIQUE
/// index's candidates via `Storage.tryUniqueLookup`, when `whereExpr` ANDs
/// in an equality against an indexed column with a directly-literal value.
/// Only ever narrows (a superset of what the full WHERE could match) —
/// `runSelectStmt` still runs the complete, unmodified WHERE/ORDER
/// BY/LIMIT/GROUP BY pipeline over whatever this returns, so a missed
/// opportunity here (a composite key, a non-literal operand, a value that
/// needs real coercion, ...) just falls back to the ordinary full scan,
/// never a correctness risk.
///
/// A qualified equality (`QualifiedCol(q, n)`) only counts when `q` names
/// *this* FROM table (its alias, or its bare name with no alias) —
/// otherwise it's an outer query's column reused inside a correlated
/// subquery (`EXISTS (SELECT ... FROM posts WHERE posts.x = ... AND
/// users.id = 5)`), and matching it against this table's index would probe
/// the wrong table's key space entirely. An unqualified `Col n` doesn't
/// need this check: SQL's own scoping already resolves a bare name to the
/// innermost table that has it, and `tryUniqueLookup` only succeeds when
/// `tref.Table` actually has an indexed column called `n` — so a bare name
/// that (via that same scoping) really means an outer column simply finds
/// no matching index here and falls back, same as any other miss.
and private pointLookupEqualities (tref: TableRef) (whereExpr: Expr option) : (string * Value) list =
    match whereExpr with
    | None -> []
    | Some whereExpr ->
        let selfQualifier = tref.Alias |> Option.defaultValue tref.Table

        flattenAnd whereExpr
        |> List.choose (function
            | BinOp(Eq, Col n, Lit v)
            | BinOp(Eq, Lit v, Col n) -> Some(n, v)
            | BinOp(Eq, QualifiedCol(q, n), Lit v)
            | BinOp(Eq, Lit v, QualifiedCol(q, n)) when System.String.Equals(q, selfQualifier, System.StringComparison.OrdinalIgnoreCase) -> Some(n, v)
            | _ -> None)

and private tryPointLookup (store: Store) (dbName: string) (tref: TableRef) (whereExpr: Expr option) : (ColumnDef list * (int * Value[]) list) option =
    let tableDb = tref.Database |> Option.defaultValue dbName

    pointLookupEqualities tref whereExpr
    |> List.tryPick (fun (n, v) -> Storage.tryUniqueLookup store tableDb tref.Table n v)

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

/// The one comparator every `ORDER BY` sort site (plain
/// `SELECT`, grouped, windowed, `UNION`) shares, instead of each carrying
/// its own copy of the same fold.
and private compareByOrderKeys (dirs: Direction list) (ka: (Value * Collation.Collation option) list) (kb: (Value * Collation.Collation option) list) : int =
    List.zip3 dirs ka kb
    |> List.fold
        (fun acc (dir, (va, ca), (vb, _)) ->
            if acc <> 0 then
                acc
            else
                let c =
                    match va, vb, ca with
                    | VString sa, VString sb, Some col -> col.Compare sa sb
                    | _ -> Value.compareTotal va vb

                match dir with
                | Asc -> c
                | Desc -> -c)
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
/// there's no outer row to thread through.
///
/// MySQL's UNION type reconciliation, at wire-type granularity: all-integer
/// columns aggregate to LONGLONG; a DECIMAL promotes the numeric result;
/// FLOAT/DOUBLE promote it further; a string anywhere poisons the column to
/// VAR_STRING (verified: `UNION` of '10', '9', 2, 1.5 sorts as strings —
/// '1.5,10,2,9', not numerically); DATE + DATETIME lands on DATETIME;
/// anything else falls back to text.
and private unionAggregateType (types: byte list) : byte =
    let isInt t = t = Value.TypeTiny || t = Value.TypeShort || t = Value.TypeLong || t = Value.TypeLongLong
    let isDate t = t = Value.TypeDate
    let isDateTime t = t = Value.TypeDateTime || t = Value.TypeTimestamp

    if types |> List.exists (fun t -> t = Value.TypeVarString || t = Value.TypeString || t = Value.TypeVarchar || t = Value.TypeBlob) then
        Value.TypeVarString
    elif types |> List.exists (fun t -> t = Value.TypeDouble) then
        Value.TypeDouble
    elif types |> List.exists (fun t -> t = Value.TypeFloat) then
        Value.TypeFloat
    elif types |> List.exists (fun t -> t = Value.TypeNewDecimal) then
        Value.TypeNewDecimal
    elif types |> List.forall isInt then
        Value.TypeLongLong
    elif types |> List.forall (fun t -> isDate t || isDateTime t) then
        if types |> List.exists isDateTime then Value.TypeDateTime else Value.TypeDate
    else
        Value.TypeVarString

/// Coerces one combined-UNION value to the column's reconciled wire type,
/// so `ORDER BY` (and the wire types) see what MySQL's own reconciliation
/// produces instead of each branch's original per-branch type. Temporal and
/// unrecognized types pass through unchanged; NULL stays NULL.
and private coerceUnionValue (ty: byte) (v: Value) : Value =
    match v with
    | VNull -> VNull
    | _ ->
        match ty with
        | t when t = Value.TypeTiny || t = Value.TypeShort || t = Value.TypeLong || t = Value.TypeLongLong ->
            VInt(int64 (Value.toDouble v))
        | t when t = Value.TypeNewDecimal -> VDecimal(decimal (Value.toDouble v))
        | t when t = Value.TypeDouble || t = Value.TypeFloat -> VDouble(Value.toDouble v)
        | t when t = Value.TypeVarString || t = Value.TypeString || t = Value.TypeVarchar || t = Value.TypeBlob ->
            VString(Value.toText v |> Option.defaultValue "")
        | _ -> v

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
    // through combining so the `ORDER BY` below can compare typed values
    // instead of re-wrapping the text back into a lexicographically-
    // comparing `VString` (`SELECT n FROM t UNION SELECT n FROM t ORDER BY
    // n` sorting "10" before "2" otherwise).
    //
    // MySQL reconciles every branch's column type across the whole UNION
    // first and only *then* compares for DISTINCT (`SELECT 1.0 UNION
    // SELECT 1` is one row, `1.0` — a dedup keyed on each branch's own
    // pre-reconciliation text, `"1.0"` vs `"1"`, would keep two). So this
    // pass just concatenates every branch's raw rows plus an `(isAll,
    // cumulative length)` boundary marker per branch, and the actual dedup
    // runs after `reconciled`/`coerceColumn` below, replayed over those
    // boundaries.
    // MySQL reconciles a temporal UNION column's fsp the same way it does
    // types/collations: the max declared fsp across branches wins, and every
    // row renders exactly that many digits (a whole-second DATETIME row
    // unioned with a DATETIME(6) one shows `.000000`). Declared, not
    // value-derived — so it comes from each branch's column/CAST fsp.
    let unionFsp (a: int option) (b: int option) =
        match a, b with
        | Some x, Some y -> Some(max x y)
        | Some x, None
        | None, Some x -> Some x
        | None, None -> None

    let combine
        (acc: Result<string list * ((string option list) * Value[]) list * byte list list * Collation.Collation list * int option list * (bool * int) list, QueryResult>)
        (isAll: bool, select: SelectStmt)
        =
        acc
        |> Result.bind (fun (cols, rowsSoFar, typesSoFar, collationsSoFar, fspsSoFar, boundaries) ->
            match runBranch select with
            | Err(code, message), _, _ -> Error(Err(code, message))
            | Affected _, _, _ -> Error(Err(1064, "UNION branch did not return a resultset"))
            | ResultSet(branchCols, _), _, _ when List.length branchCols <> List.length cols ->
                Error(Err(1222, "The used SELECT statements have a different number of columns"))
            | ResultSet(branchCols, branchRows), branchTypes, branchTyped ->
                let branchPaired = List.zip branchRows branchTyped

                let branchCollations = selectColumnCollations store registry dbName select branchCols
                let collations =
                    if collationsSoFar.IsEmpty then
                        branchCollations
                    else
                        List.map2 strictestUnionCollation collationsSoFar branchCollations

                let branchFsps = selectColumnFsps store registry dbName select branchCols
                let fsps = List.map2 unionFsp fspsSoFar branchFsps

                let combined = rowsSoFar @ branchPaired
                Ok(cols, combined, branchTypes :: typesSoFar, collations, fsps, boundaries @ [ isAll, List.length combined ]))

    match runSelectStmt store registry dbName first None with
    | Err(code, message), _, _ -> Err(code, message), [], []
    | Affected _, _, _ -> Err(1064, "UNION branch did not return a resultset"), [], []
    | ResultSet(firstCols, firstRows), firstTypes, firstTyped ->
        let firstCollations = selectColumnCollations store registry dbName first firstCols
        let firstFsps = selectColumnFsps store registry dbName first firstCols
        let firstPaired = List.zip firstRows firstTyped
        let firstLen = List.length firstPaired

        match rest |> List.fold combine (Ok(firstCols, firstPaired, [ firstTypes ], firstCollations, firstFsps, [])) with
        | Error e -> e, [], []
        | Ok(cols, allPaired, typesSoFar, collations, fsps, boundaries) ->
            // MySQL's union type reconciliation across every branch, and
            // every row's values coerced to it — an `ORDER BY` over the
            // mixed-typed union then sorts exactly as MySQL does.
            let reconciled =
                cols
                |> List.mapi (fun i _ -> typesSoFar |> List.map (fun ts -> ts.[i]) |> unionAggregateType)

            // A DECIMAL-reconciled column renders at the union's scale —
            // MySQL shows `SELECT 2 UNION SELECT 1.5` as 1.5 / 2.0, not 2.
            // The scale is the widest fraction any branch's value carries.
            let decimalScale (v: Value) =
                match v with
                | VDecimal _ ->
                    match Value.toText v with
                    | Some text ->
                        match text.IndexOf '.' with
                        | -1 -> 0
                        | dot -> text.Length - dot - 1
                    | None -> 0
                | _ -> 0

            let rescale (scale: int) (v: Value) : Value =
                match v with
                | VDecimal d when scale > 0 ->
                    VDecimal(
                        System.Decimal.Parse(
                            d.ToString("F" + string scale, System.Globalization.CultureInfo.InvariantCulture),
                            System.Globalization.CultureInfo.InvariantCulture
                        )
                    )
                | _ -> v

            let scales =
                cols
                |> List.mapi (fun i _ -> allPaired |> List.map (fun (_, typed) -> decimalScale typed.[i]) |> List.max)

            let coerceColumn (i: int) (v: Value) : Value =
                let coerced = coerceUnionValue reconciled.[i] v
                if reconciled.[i] = Value.TypeNewDecimal then rescale scales.[i] coerced else coerced

            let coercedPairedRaw =
                allPaired
                |> List.map (fun (_, typed) ->
                    let coerced = typed |> Array.mapi coerceColumn
                    // Re-render the text row from the coerced values, so the
                    // wire text carries the reconciliation too (2 -> 2.0), and
                    // a DATETIME-reconciled column at the union's fsp so every
                    // branch's rows show the same digit count.
                    let text =
                        coerced
                        |> Array.mapi (fun i v ->
                            match v with
                            | VNull -> None
                            | v ->
                                match (if reconciled.[i] = Value.TypeDateTime then fsps.[i] else None) with
                                | Some fsp -> Value.toTextFsp fsp v
                                | None -> Value.toText v)
                        |> List.ofArray
                    text, coerced)

            // Replays each branch's `isAll`/boundary from `combine` above,
            // keyed on the reconciled text/collation rather than each
            // branch's own pre-reconciliation one — collation-aware, so
            // åge/age fold under the UNION's aggregated collation
            // (strictest branch wins — bin never folds).
            let dedupeKey (text: string option list) =
                List.map2 (fun (col: Collation.Collation) (cell: string option) -> cell |> Option.map col.KeyOf) collations text

            let coercedFirst = coercedPairedRaw |> List.truncate firstLen

            let _, coercedPaired =
                boundaries
                |> List.fold
                    (fun (prevUpto: int, accRows: (string option list * Value[]) list) (isAll, upto) ->
                        let newRows = coercedPairedRaw |> List.skip prevUpto |> List.take (upto - prevUpto)
                        let extended = accRows @ newRows
                        let result = if isAll then extended else extended |> List.distinctBy (fst >> dedupeKey)
                        upto, result)
                    (firstLen, coercedFirst)

            // `ORDER BY`/`LIMIT` on the combined result — same
            // alias/positional resolution as an ordinary `SELECT`, and now
            // the same typed `Value.compare` sort too, via each row's own
            // paired typed values rather than re-parsing its text.
            let projections = cols |> List.map (fun c -> Col c, None)
            let resolveOrder = resolvePositionalOrAlias projections

            // The combined result's own synthetic columns (name + reconciled
            // wire type), so an `ORDER BY` expression beyond a bare output
            // column (`ORDER BY -v`, `ORDER BY UPPER(name)`, ...) can be
            // evaluated for real against a row.
            // Only `Name` matters here — `columnIndexOf` reads nothing else,
            // and `ctxForOrder`'s empty `Qualifiers` means the `Type`-aware
            // helpers (`resolvedCollation`/`enumOrdinalFor`) never look this
            // `ColumnDef` up anyway, so the rest of the record is filler.
            let orderColumns: ColumnDef list =
                cols
                |> List.map (fun c ->
                    { Name = c
                      Type = TVarchar 255
                      Nullable = true
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false })

            let ctxForOrder = contextFactory store registry dbName (columnIndexOf orderColumns) Map.empty None

            let orderKeyOf (typedRow: Value[]) (expr: Expr) : Result<Value * Collation.Collation option, EvalError> =
                match resolveOrder expr with
                | Col name ->
                    match cols |> List.tryFindIndex (fun c -> System.String.Equals(c, name, System.StringComparison.OrdinalIgnoreCase)) with
                    | Some i when i < typedRow.Length -> Ok(typedRow.[i], None)
                    | _ -> Ok(VNull, None)
                | resolved -> evalExpr (ctxForOrder typedRow) resolved |> Result.map (fun v -> v, None)

            let sortedResult =
                if orderBy.IsEmpty then
                    Ok coercedPaired
                else
                    coercedPaired
                    |> traverse (fun (text, typed) ->
                        orderBy
                        |> traverse (fun (expr, _) -> orderKeyOf typed expr)
                        |> Result.map (fun keys -> keys, (text, typed)))
                    |> Result.map (
                        List.sortWith (fun (ka, _) (kb, _) -> compareByOrderKeys (orderBy |> List.map snd) ka kb)
                        >> List.map snd
                    )

            match sortedResult with
            | Error(code, message) -> Err(code, message), [], []
            | Ok sortedPaired ->
                let limitedPaired = sortedPaired |> applyLimitOffset limit offset
                ResultSet(cols, limitedPaired |> List.map fst), reconciled, limitedPaired |> List.map snd

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

        let evalRow (row: Value[]) : Result<(Value * Value * (Value * Collation.Collation option) list) option, EvalError> =
            let ctx = ctxFor row
            evalExpr ctx innerExpr
            |> Result.bind (function
                | VNull -> Ok None
                | v -> orderKeys |> traverse (fst >> evalOrderKey ctx) |> Result.map (fun keys -> Some(v, collationKeyOf ctx innerExpr v, keys)))

        rows
        |> traverse evalRow
        |> Result.map (fun results ->
            let present = results |> List.choose id
            let ordered =
                if orderKeys.IsEmpty then
                    present
                else
                    present |> List.sortWith (fun (_, _, ka) (_, _, kb) -> compareByOrderKeys (List.map snd orderKeys) ka kb)
            // Collation-aware dedupe: åge/age fold to one value under an
            // ai_ci column, stay distinct under bin.
            let deduped = if distinct then List.distinctBy (fun (_, key, _) -> key) ordered else ordered

            if deduped.IsEmpty then
                VNull
            else
                deduped |> List.map (fun (v, _, _) -> v |> toText |> Option.defaultValue "") |> String.concat separator |> VString)
    | [ arg ] ->
        let distinct, innerExpr = unwrapDistinct arg
        let isMin = System.String.Equals(name, "MIN", System.StringComparison.OrdinalIgnoreCase)
        let isMax = System.String.Equals(name, "MAX", System.StringComparison.OrdinalIgnoreCase)

        match Functions.lookupAggregate name registry with
        | None -> Error(unknownFunction name)
        | Some fold ->
            rows
            |> traverse (fun row ->
                evalExpr (ctxFor row) innerExpr
                |> Result.map (fun v -> v, collationKeyOf (ctxFor row) innerExpr v))
            |> Result.map (fun keyed ->
                let nonNull = keyed |> List.filter (fst >> function VNull -> false | _ -> true)
                // `DISTINCT` folds by the expression's own collation
                // (MySQL-verified: COUNT(DISTINCT name) over åge/age/ÅGE
                // is 1 under ai_ci, 3 under bin).
                let deduped = if distinct then nonNull |> List.distinctBy snd |> List.map fst else nonNull |> List.map fst

                if isCount || not deduped.IsEmpty then
                    // MIN/MAX over strings compare by the expression's own
                    // collation weights, with primary-equal values keeping
                    // the first-seen one (MySQL-verified: MAX('ÅGE','age')
                    // and MAX('age','ÅGE') each return whichever came
                    // first) — `Value.compare`'s folded server-default
                    // order would pick wrong.
                    if (isMin || isMax) && deduped |> List.forall (function VString _ -> true | _ -> false) then
                        let col = keyCollation (ctxFor (List.tryHead rows |> Option.defaultValue [||])) innerExpr

                        let text (v: Value) =
                            match v with
                            | VString s -> s
                            | _ -> ""

                        if isMax then
                            List.reduce (fun best v -> if col.ComparePrimary (text best) (text v) >= 0 then best else v) deduped
                        else
                            List.reduce (fun best v -> if col.ComparePrimary (text best) (text v) <= 0 then best else v) deduped
                    else
                        fold deduped
                else
                    VNull)
    | Distinct firstExpr :: rest when isCount ->
        // `COUNT(DISTINCT a, b)` — `distinctArg` (the call-argument parser)
        // attaches `Distinct` only to the first comma-separated argument,
        // but MySQL's `DISTINCT` here scopes over the whole tuple `(a, b)`,
        // not just `a`. Evaluate every argument per row, drop a row if
        // *any* column of it is NULL (SQL's usual "NULL drops the row from
        // an aggregate" rule, applied to the whole tuple), dedupe the
        // tuples — by each element's own collation — and count what's
        // left.
        let allArgs = firstExpr :: rest

        rows
        |> traverse (fun row ->
            let ctx = ctxFor row

            allArgs
            |> traverse (fun e -> evalExpr ctx e |> Result.map (fun v -> v, collationKeyOf ctx e v)))
        |> Result.map (fun tuples ->
            tuples
            |> List.filter (List.exists (fst >> function VNull -> true | _ -> false) >> not)
            |> List.distinctBy (List.map snd)
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
    | Placeholder _ -> Ok expr
    | MatchAgainst(cols, q, mode) -> sub q |> Result.map (fun q2 -> MatchAgainst(cols, q2, mode))
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
    | Like(e, p, cs, esc) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Like(e', p', cs, esc)))
    | Regexp(e, p) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Regexp(e', p')))
    | In(e, xs) -> sub e |> Result.bind (fun e' -> xs |> traverse sub |> Result.map (fun xs' -> In(e', xs')))
    | Between(e, lo, hi) ->
        sub e |> Result.bind (fun e' -> sub lo |> Result.bind (fun lo' -> sub hi |> Result.map (fun hi' -> Between(e', lo', hi'))))
    | Cast(e, ty) -> sub e |> Result.map (fun e' -> Cast(e', ty))
    | Collate(e, name) -> sub e |> Result.map (fun e' -> Collate(e', name))
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
    | RankOver _
    | PercentRankOver _
    | NTileOver _
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
    | Placeholder _ -> Ok expr
    | MatchAgainst(cols, q, mode) -> sub q |> Result.map (fun q2 -> MatchAgainst(cols, q2, mode))
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
    | Like(e, p, cs, esc) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Like(e', p', cs, esc)))
    | Regexp(e, p) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Regexp(e', p')))
    | In(e, xs) -> sub e |> Result.bind (fun e' -> xs |> traverse sub |> Result.map (fun xs' -> In(e', xs')))
    | Between(e, lo, hi) ->
        sub e |> Result.bind (fun e' -> sub lo |> Result.bind (fun lo' -> sub hi |> Result.map (fun hi' -> Between(e', lo', hi'))))
    | Cast(e, ty) -> sub e |> Result.map (fun e' -> Cast(e', ty))
    | Collate(e, name) -> sub e |> Result.map (fun e' -> Collate(e', name))
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
    | RankOver _
    | PercentRankOver _
    | NTileOver _
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
    : Result<Value * Collation.Collation option, EvalError> =
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
    let orderKeysOf (outputCols: (string * Value) list) (groupRows: Value[] list) : Result<(Value * Collation.Collation option) list, EvalError> =
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
                    |> traverse (fun row ->
                        groupExprs
                        |> traverse (evalExpr (ctxFor row))
                        // Collation-aware keys: åge/age/ÅGE group together
                        // under ai_ci, stay distinct under bin.
                        |> Result.map (fun key -> List.map2 (collationKeyOf (ctxFor row)) groupExprs key, row))
                    |> Result.map (fun keyed -> keyed |> List.groupBy fst |> List.map (fun (key, rows) -> key, rows |> List.map snd))

            match buildGroups () with
            | Error(code, message) -> Err(code, message), [], []
            | Ok groups ->
                let processGroup
                    (key: Value list, groupRows: Value[] list)
                    : Result<((string * Value) list * (Value * Collation.Collation option) list * Value list) option, EvalError> =
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
                                // The index order path sorts by the GROUP
                                // BY key itself — tag it with the same
                                // collation/ordinal rules the full order
                                // path uses.
                                let probeCtx = ctxFor (probeRow columns)
                                let tagged keys = List.map2 (orderValueForExpr probeCtx) groupExprs keys
                                compareByOrderKeys (groupExprs |> List.map (fun _ -> Asc)) (tagged ka) (tagged kb))
                        elif select.OrderBy.IsEmpty then
                            // Same reasoning as the plain-`SELECT` `sortRows`
                            // above: an empty `ORDER BY` makes every
                            // comparison a no-op, so skip the sort outright.
                            kept
                        else
                            kept |> List.sortWith (fun (_, ka, _) (_, kb, _) -> compareByOrderKeys (List.map snd select.OrderBy) ka kb)

                    // Declared fsp per output column, same as the plain path
                    // — a bare grouped temporal column (`SELECT dt ... GROUP
                    // BY dt`) still renders its precision; an aggregate over
                    // one (`MAX(dt)`) has no resolvable column type and falls
                    // back to `toText`.
                    let groupCtx = ctxFor (probeRow columns)
                    let groupFsps = outputColumnFsps groupCtx columns select.Projections
                    let groupWireOverrides = outputColumnWireOverrides groupCtx columns select.Projections

                    let paired =
                        sorted
                        |> List.map (fun (proj, _, _) -> renderOutputCols groupFsps proj, proj |> List.map snd |> Array.ofList)

                    let dedupedPaired = if select.Distinct then paired |> List.distinctBy fst else paired

                    let types =
                        columnTypesOf (List.length colNames) (sorted |> List.map (fun (proj, _, _) -> proj))
                        |> applyWireOverrides groupWireOverrides
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
    // MySQL allows a window function in `ORDER BY` even when no projection
    // carries one (`SELECT v FROM t ORDER BY LAG(v) OVER (...)`), so
    // collection spans both lists — each becomes a synthetic column either
    // way, and the `select'` rewrite below substitutes both lists too.
    let windowFuncs =
        (select.Projections |> List.collect (fst >> collectWindowFuncs))
        @ (select.OrderBy |> List.collect (fst >> collectWindowFuncs))
        |> List.distinct

    if windowFuncs.IsEmpty then
        Err(1064, "runWindowedSelect called without a window-function projection"), [], []
    else

    // MySQL rejects a `HAVING` referencing a windowed projection's alias
    // with a dedicated error (3594, ER_WINDOW_INVALID_WINDOW_FUNC_ALIAS_USE
    // — its errmsg really does end with a stray `.'`) rather than resolving
    // the alias.
    let windowedAliases =
        select.Projections
        |> List.choose (fun (expr, alias) ->
            match alias with
            | Some a when not (collectWindowFuncs expr |> List.isEmpty) -> Some(a.ToLowerInvariant())
            | _ -> None)
        |> Set.ofList

    let havingWindowAlias =
        select.Having
        |> Option.map collectColRefs
        |> Option.defaultValue []
        |> List.tryFind (fun name -> windowedAliases.Contains(name.ToLowerInvariant()))

    match havingWindowAlias with
    | Some alias ->
        Err(
            3594,
            sprintf "You cannot use the alias '%s' of an expression containing a window function in this context.'" alias
        ),
        [],
        []
    | None ->

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
                | RankOver(_, p, o) -> p, o
                | PercentRankOver(p, o) -> p, o
                | NTileOver(_, p, o) -> p, o
                | _ -> [], []

            let keyOf (exprs: Expr list) (row: Value[]) : Result<Value list, EvalError> =
                exprs
                |> traverse (evalExpr (ctxFor row))
                |> Result.map (fun key -> List.map2 (collationKeyOf (ctxFor row)) exprs key)

            let orderKeyOf (exprs: Expr list) (row: Value[]) : Result<(Value * Collation.Collation option) list, EvalError> =
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

                // `RANK`'s number for each row in an ORDER BY-sorted partition
                // group: the 1-based position of the first row in its tie
                // group (so ties share a rank and the next distinct value
                // skips ahead by the tie-group's size); an empty window
                // ORDER BY ties every row in the partition together, same as
                // MySQL. `PERCENT_RANK` reuses this (always non-dense, see
                // `Ast.Expr.PercentRankOver`'s doc) rather than a separate walk.
                let ranksOf (group: (int * (Value list * (Value * Collation.Collation option) list * Value[])) []) : int[] =
                    let dirs = windowOrderBy |> List.map snd

                    group
                    |> Array.mapi (fun pos (_, (_, ordKey, _)) -> pos, ordKey)
                    |> Array.mapFold
                        (fun (prevKey, lastRank) (pos, ordKey) ->
                            let tie =
                                match prevKey with
                                | Some pk -> compareByOrderKeys dirs pk ordKey = 0
                                | None -> false

                            let rank = if tie then lastRank else pos + 1
                            rank, (Some ordKey, rank))
                        (None, 0)
                    |> fst

                match windowFunc with
                | RowNumberOver _ ->
                    partitions
                    |> Array.ofList
                    |> Array.collect (Array.mapi (fun rank (origIdx, _) -> origIdx, VInt(int64 (rank + 1))))
                    |> Ok
                | RankOver(dense, _, _) ->
                    partitions
                    |> Array.ofList
                    |> Array.collect (fun group ->
                        if dense then
                            // Dense rank increments by 1 at every tie-group
                            // boundary instead of jumping to the leader's
                            // 1-based position, so it never skips a number.
                            group
                            |> Array.mapFold
                                (fun (prevKey, denseRank) (origIdx, (_, ordKey, _)) ->
                                    let newGroup =
                                        match prevKey with
                                        | Some pk -> compareByOrderKeys (windowOrderBy |> List.map snd) pk ordKey <> 0
                                        | None -> true

                                    let rank = if newGroup then denseRank + 1 else denseRank
                                    (origIdx, VInt(int64 rank)), (Some ordKey, rank))
                                (None, 0)
                            |> fst
                        else
                            Array.map2 (fun (origIdx, _) rank -> origIdx, VInt(int64 rank)) group (ranksOf group))
                    |> Ok
                | PercentRankOver _ ->
                    partitions
                    |> Array.ofList
                    |> Array.collect (fun group ->
                        let n = group.Length
                        let ranks = ranksOf group

                        Array.map2
                            (fun (origIdx, _) rank ->
                                origIdx, VDouble(if n <= 1 then 0.0 else float (rank - 1) / float (n - 1)))
                            group
                            ranks)
                    |> Ok
                | NTileOver(buckets, _, _) ->
                    if buckets <= 0L then
                        Error(1210, "Incorrect arguments to ntile")
                    else
                        let buckets = int buckets

                        partitions
                        |> Array.ofList
                        |> Array.collect (fun group ->
                            let n = group.Length
                            let baseSize = n / buckets
                            let remainder = n % buckets

                            // Earlier buckets absorb the remainder (a 10-row
                            // partition into 3 buckets is 4/3/3), matching
                            // MySQL rather than splitting evenly from the end.
                            let sizes = Array.init buckets (fun b -> baseSize + (if b < remainder then 1 else 0))
                            let bucketStarts = sizes |> Array.scan (+) 0

                            group
                            |> Array.mapi (fun pos (origIdx, _) ->
                                let bucket = bucketStarts |> Array.findIndexBack (fun start -> start <= pos)
                                origIdx, VInt(int64 (bucket + 1))))
                        |> Ok
                | LagOver(lagExpr, offset, _, _) ->
                    // `pos - offset` indexes within the same partition's
                    // ORDER BY-sorted rows — backward for `LAG` (positive
                    // offset), forward for `LEAD` (negative, see
                    // `Ast.Expr.LagOver`). Outside the partition (no such
                    // predecessor/successor) is NULL, same as real MySQL.
                    partitions
                    |> traverse (fun group ->
                        group
                        |> Array.mapi (fun pos (origIdx, _) ->
                            let srcPos = pos - int offset

                            if srcPos < 0 || srcPos >= group.Length then
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
                    syntheticColumn name (TBigInt false) (match wf with LagOver _ -> true | _ -> false))

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
                    // explicit alias labels itself like MySQL's function-call
                    // headers (`lead(v) over ()`), never the internal
                    // `__fsdb_window_N__` synthetic column name — anything
                    // wrapping one falls through to `runSelect`'s ordinary
                    // unaliased-label handling instead.
                    let alias =
                        aliasOpt
                        |> Option.orElse (
                            synthetic
                            |> List.tryFind (fun (wf, _) -> wf = expr)
                            |> Option.map (fun _ -> exprLabel expr)
                        )

                    [ substituteWindowFuncs synthetic expr, alias ]

            let select' =
                { select with
                    Projections = select.Projections |> List.collect rewriteProjection
                    OrderBy = select.OrderBy |> List.map (fun (e, dir) -> substituteWindowFuncs synthetic e, dir) }

            runSelect store registry dbName extendedColumns qualifiers extendedRows select' outer

/// The fulltext pre-pass: like `runWindowedSelect`, each distinct
/// `MATCH ... AGAINST` becomes a synthetic score column computed over the
/// whole table — but ahead of WHERE, because the score both filters rows
/// and needs corpus statistics (per-term document frequency) that only the
/// full, unfiltered table provides (MySQL's corpus is the same).
/// ponytail: single real table, no JOIN/derived source — real MySQL also
/// evaluates MATCH through joins; route those through this same pre-pass
/// keyed on the owning source if an app ever needs it.
and private runFullTextSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (matchNodes: Expr list)
    (outer: EvalContext option)
    : QueryResult * byte list * Value[] list =
    let unsupported = Err(1191, "Can't find FULLTEXT index matching the column list"), [], []

    match select.From, select.Joins with
    | Some(FromTable tref), [] ->
        let tableDb = tref.Database |> Option.defaultValue dbName

        match store.Catalog |> Map.tryFind tableDb |> Option.bind (Map.tryFind (tref.Table.ToLowerInvariant())) with
        | None ->
            // A missing table/database reports its ordinary error, not 1191.
            match Storage.scanList store tableDb tref.Table with
            | Error e -> storageErr e, [], []
            | Ok _ -> unsupported // an information_schema view has no FULLTEXT index
        | Some table ->
            let fulltextSets =
                table.Indexes
                |> List.filter (fun ix -> ix.Kind = FullTextIndex)
                |> List.map (fun ix -> ix.Columns |> List.map (fun c -> c.ToLowerInvariant()) |> Set.ofList)

            let rows = table.Rows

            // The column list must equal a FULLTEXT index's set —
            // order-insensitive, oracle-verified (a reversed list matches;
            // a subset is 1191) — and the query must already be a constant.
            let validateNode node =
                match node with
                | MatchAgainst(cols, Lit queryValue, mode) when
                    fulltextSets |> List.contains (cols |> List.map (fun c -> c.ToLowerInvariant()) |> Set.ofList)
                    ->
                    match Value.toText queryValue with
                    | Some queryText ->
                        cols
                        |> traverse (Storage.resolveColumn table.Columns)
                        |> Result.mapError Storage.toMySqlError
                        |> Result.map (fun idxs -> node, mode, queryText, idxs)
                    | None -> Error(1210, "Incorrect arguments to AGAINST")
                | MatchAgainst(cols, _, _) when
                    not (fulltextSets |> List.contains (cols |> List.map (fun c -> c.ToLowerInvariant()) |> Set.ofList))
                    ->
                    Error(1191, "Can't find FULLTEXT index matching the column list")
                | MatchAgainst _ -> Error(1210, "Incorrect arguments to AGAINST")
                | _ -> Error(1105, "fulltext pre-pass collected a non-MATCH node")

            let scoreNode (node, mode, queryText: string, idxs: int list) =
                let corpus =
                    rows
                    |> List.map (fun (row: Value[]) ->
                        idxs |> List.map (fun i -> Value.toText row.[i] |> Option.defaultValue "") |> String.concat " ")
                    |> FullText.buildCorpus

                let scores =
                    match mode with
                    | NaturalLanguage -> FullText.naturalScoresOf corpus queryText
                    | BooleanMode -> FullText.booleanScoresOf corpus queryText
                    | QueryExpansion -> FullText.expansionScoresOf corpus queryText

                node, mode, scores

            match matchNodes |> traverse validateNode |> Result.map (List.map scoreNode) with
            | Error(code, msg) -> Err(code, msg), [], []
            | Ok computed ->

            let synthetic =
                computed |> List.mapi (fun i (node, _, _) -> node, sprintf "__fsdb_match_%d__" i)

            let syntheticColumns =
                synthetic |> List.map (fun (_, name) -> syntheticColumn name TDouble false)

            let extendedColumns = table.Columns @ syntheticColumns

            let extendedRows =
                rows
                |> List.mapi (fun idx row ->
                    Array.append row (computed |> List.map (fun (_, _, scores) -> VDouble scores.[idx]) |> Array.ofList))

            let qualifier = fromItemQualifier (FromTable tref)
            let qualifiers = qualifierRanges [ qualifier, extendedColumns ]

            let sub = substituteWindowFuncs synthetic

            let rewriteProjection (expr: Expr, aliasOpt: string option) : (Expr * string option) list =
                match expr with
                | Star None -> table.Columns |> List.map (fun c -> Col c.Name, None)
                | Star(Some q) when q.ToLowerInvariant() = qualifier.ToLowerInvariant() ->
                    table.Columns |> List.map (fun c -> Col c.Name, None)
                | _ ->
                    // A bare MATCH projection labels itself like MySQL's
                    // header (`match (c) against ('q')`), never the synthetic
                    // column name.
                    let alias =
                        aliasOpt
                        |> Option.orElse (
                            synthetic
                            |> List.tryFind (fun (node, _) -> node = expr)
                            |> Option.map (fun _ -> exprLabel expr)
                        )

                    [ sub expr, alias ]

            // MySQL's implicit relevance order: a natural-language (or
            // expansion) MATCH filtering the WHERE clause, with no explicit
            // ORDER BY or grouping, returns rows relevance-descending.
            let whereNodes =
                select.Where |> Option.map collectMatchAgainst |> Option.defaultValue []

            let implicitOrder =
                if select.OrderBy.IsEmpty && select.GroupBy.IsEmpty && not select.Distinct then
                    computed
                    |> List.tryFind (fun (node, mode, _) -> mode <> BooleanMode && List.contains node whereNodes)
                    |> Option.bind (fun (node, _, _) -> synthetic |> List.tryFind (fst >> (=) node))
                    |> Option.map (fun (_, name) -> [ Col name, Desc ])
                    |> Option.defaultValue []
                else
                    []

            let select' =
                { select with
                    Projections = select.Projections |> List.collect rewriteProjection
                    Where = select.Where |> Option.map sub
                    Having = select.Having |> Option.map sub
                    GroupBy = select.GroupBy |> List.map sub
                    OrderBy =
                        if implicitOrder.IsEmpty then
                            select.OrderBy |> List.map (fun (e, dir) -> sub e, dir)
                        else
                            implicitOrder }

            runSelect store registry dbName extendedColumns qualifiers (Seq.ofList extendedRows) select' outer
    | _ -> unsupported

and private runSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (qualifiers: Map<string, ColumnDef list * int>)
    (rows: Value[] seq)
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
    elif select.Having |> Option.map collectWindowFuncs |> Option.exists (List.isEmpty >> not) then
        // MySQL rejects a window function in HAVING with a dedicated error
        // (3593, ER_WINDOW_INVALID_WINDOW_FUNC_USE) naming the function —
        // its errmsg really does end with a stray `.'`.
        let name =
            match select.Having |> Option.map collectWindowFuncs |> Option.defaultValue [] with
            | LagOver(_, offset, _, _) :: _ -> if offset < 0L then "lead" else "lag"
            | _ -> "row_number"

        Err(3593, sprintf "You cannot use the window function '%s' in this context.'" name), [], []
    elif
        projections |> List.exists (fst >> collectWindowFuncs >> List.isEmpty >> not)
        || orderBy |> List.exists (fst >> collectWindowFuncs >> List.isEmpty >> not)
    then
        // GROUP BY/window functions are honest barriers — every row must be
        // seen before either can produce anything — so `rows` (lazy when a
        // `JOIN`'s hash-probe fed it straight through) is forced here rather
        // than threading `seq` any further than the paths that can actually
        // stop early.
        runWindowedSelect store registry dbName columns qualifiers (List.ofSeq rows) select outer
    elif
        not select.GroupBy.IsEmpty
        || (select.Having |> Option.exists (containsAggregate registry))
        || projections |> List.exists (fst >> containsAggregate registry)
        || orderBy |> List.exists (fst >> containsAggregate registry)
    then
        runGroupedSelect store registry dbName columns qualifiers (List.ofSeq rows) select outer
    else

    let columnIndex = columnIndexOf columns

    let ctxFor = contextFactory store registry dbName columnIndex qualifiers outer

    // ORDER BY may name a 1-based projection position (`ORDER BY 1`) —
    // resolve that first against the projection list; `resolveOrderKey`
    // below handles the alias/output-column case (and its `*`/`t.*`
    // expansion) itself.
    let resolveOrderExpr = resolveOrderPosition projections

    // A non-aggregate, GROUP-BY-less `HAVING` (e.g. `HAVING v > 5`) filters
    // per-row exactly like `WHERE` — `runGroupedSelect` only owns the case
    // where `HAVING` actually needs a group's aggregated results (see the
    // `containsAggregate`/`GroupBy.IsEmpty` routing above) — so it's just
    // ANDed onto the same per-row `matches` check here.
    let matches (row: Value[]) : Result<bool, EvalError> =
        whereMatches ctxFor whereExpr row
        |> Result.bind (fun keep ->
            if not keep then
                Ok false
            else
                match select.Having with
                | None -> Ok true
                | Some expr -> evalExpr { ctxFor row with Clause = WhereClause } expr |> Result.map (fun v -> truthy v = Some true))

    let projectRow (row: Value[]) : Result<(string * Value) list, EvalError> =
        projections
        |> traverse (evalProjection (ctxFor row) columns)
        |> Result.map List.concat

    // `outputCols` (the row's own projection) is computed once by the
    // caller and threaded in here, rather than re-run per `ORDER BY`
    // key — re-running per key would call `projectRow` three times over
    // on the same row for `ORDER BY a, b, c`, for the same result.
    let orderKeysOf (row: Value[]) (outputCols: (string * Value) list) : Result<(Value * Collation.Collation option) list, EvalError> =
        orderBy |> traverse (fun (expr, _) -> resolveOrderKey (ctxFor row) projections outputCols (resolveOrderExpr expr))

    // The declared fsp per output column, computed once off the probe
    // context — a temporal column renders exactly its fsp digits (see
    // `renderOutputCols`), independent of row values, so it's stable across
    // every row this select emits.
    let outputFsps = outputColumnFsps (ctxFor (probeRow columns)) columns projections
    let outputWireOverrides = outputColumnWireOverrides (ctxFor (probeRow columns)) columns projections

    let pairOf (outputCols: (string * Value) list) : string option list * Value[] =
        renderOutputCols outputFsps outputCols, outputCols |> List.map snd |> Array.ofList

    let probe = probeRow columns

    match matches probe
          |> Result.bind (fun _ -> projectRow probe)
          |> Result.bind (fun outputCols -> orderKeysOf probe outputCols |> Result.map (fun _ -> outputCols)) with
    | Error(code, message) -> Err(code, message), [], []
    | Ok probeProjection when limit = Some 0 ->
        // `LIMIT 0` is the standard "column metadata, no rows" probe
        // (PDO/Doctrine's `getColumnMeta` and friends) — MySQL, and this
        // engine, both answer it by evaluating every
        // matched row's projection once rather than short-circuiting to
        // nothing, so wire types (and a row-level error) come out the same
        // as any other query would give them. No row ever needs to cross
        // the wire either way, so the scan doesn't cost the thing a
        // `LIMIT 0` caller actually asked for.
        let colNames = probeProjection |> List.map fst

        match rows |> traverseSeq (fun row -> matches row |> Result.bind (fun keep -> if keep then projectRow row |> Result.map Some else Ok None)) with
        | Error(code, message) -> Err(code, message), [], []
        | Ok allProjected ->
            ResultSet(colNames, []), applyWireOverrides outputWireOverrides (columnTypesOf (List.length colNames) allProjected), []
    | Ok probeProjection ->
        let colNames = probeProjection |> List.map fst

        // Column wire types are read off whatever rows actually cross the
        // wire (post `DISTINCT`/`LIMIT`/`OFFSET`), not the full matched set
        // — the same data-driven "first non-NULL value" approximation
        // `columnTypesOf` always used, narrowed to the rows a client can
        // observe. ponytail: a column that's NULL in every *returned* row
        // but non-NULL further down the matched set (past `LIMIT`) now
        // reports `VAR_STRING` instead of that later type; scanning the
        // full matched set just to pick a wire type would defeat the
        // `LIMIT` short-circuit below for every query. Upgrade to schema-
        // declared (not data-driven) column types if this ever bites.
        let typesOf (finalRows: Value[] list) : byte list =
            columnTypesOf (List.length colNames) (finalRows |> List.map (List.zip colNames << List.ofArray))
            |> applyWireOverrides outputWireOverrides

        // Shared by both `ORDER BY` branches below: evaluates `WHERE`, the
        // projection, and the sort keys for one row, in that order, short-
        // circuiting to `None` on a `WHERE` miss without projecting it.
        // Collation-aware DISTINCT key: string output columns fold to
        // their collation's canonical key, so åge/age dedupe under ai_ci
        // while the emitted text stays the first row's original value.
        let dedupeKeyOf (row: Value[]) (outputCols: (string * Value) list) : string option list =
            let ctx = ctxFor row

            outputCols
            |> List.map (fun (name, v) ->
                match v with
                | VString text -> Some((keyCollation ctx (Col name)).KeyOf text)
                | _ -> Value.toText v)

        let evalKeyed (row: Value[]) : Result<((Value * Collation.Collation option) list * (string option list * Value[]) * (string option list)) option, EvalError> =
            matches row
            |> Result.bind (fun keep ->
                if not keep then
                    Ok None
                else
                    projectRow row
                    |> Result.bind (fun outputCols ->
                        orderKeysOf row outputCols |> Result.map (fun keys -> Some(keys, pairOf outputCols, dedupeKeyOf row outputCols))))

        // No `ORDER BY`: `WHERE`/`DISTINCT`/`LIMIT`/`OFFSET` stream lazily
        // through `streamLimited`, which stops pulling rows the moment
        // enough have survived — verified against a real MySQL oracle
        // (`SELECT ..., CAST(bad_json AS JSON) ... LIMIT n` with the poison
        // row past position `n`): no `ORDER BY` means the error is never
        // raised, because the row is never evaluated. `ORDER BY` (below)
        // forces a full scan either way — same oracle, `ORDER BY` on an
        // unindexed column — so only that path keeps evaluating everything.
        if orderBy.IsEmpty then
            let evalPaired (row: Value[]) : Result<(string option list * (string option list * Value[])) option, EvalError> =
                matches row
                |> Result.bind (fun keep ->
                    if keep then
                        projectRow row |> Result.map (fun outputCols -> dedupeKeyOf row outputCols, pairOf outputCols) |> Result.map Some
                    else
                        Ok None)

            match rows |> streamLimited select.Distinct (Option.defaultValue 0 offset) limit evalPaired with
            | Error(code, message) -> Err(code, message), [], []
            | Ok limited -> ResultSet(colNames, limited |> List.map (snd >> fst)), typesOf (limited |> List.map (snd >> snd)), limited |> List.map (snd >> snd)

        // `ORDER BY` + `LIMIT`, no `DISTINCT`: bounded top-(limit+offset)
        // selection (`boundedTopN`) instead of a full sort — still touches
        // every matched row (see above), but keeps only `limit + offset` of
        // them at a time instead of the whole matched set. The `limit +
        // offset` addition happens in `int64` and is clamped back into
        // `int` afterward: the parser clamps a `LIMIT` up to MySQL's
        // 2^64-1 down to `Int32.MaxValue` (a real idiom — "offset with no
        // limit" pagination emits it verbatim), and adding a nonzero
        // `OFFSET` to that in unchecked 32-bit `int` wraps negative.
        elif limit.IsSome && not select.Distinct then
            let dirs = List.map snd orderBy
            let capacity = int (min (int64 (Option.get limit) + int64 (Option.defaultValue 0 offset)) (int64 System.Int32.MaxValue))

            match rows |> boundedTopN capacity (fun (ka, _, _) (kb, _, _) -> compareByOrderKeys dirs ka kb) evalKeyed with
            | Error(code, message) -> Err(code, message), [], []
            | Ok top ->
                let limited = top |> List.map (fun (_, p, _) -> p) |> applyLimitOffset limit offset
                ResultSet(colNames, limited |> List.map fst), typesOf (limited |> List.map snd), limited |> List.map snd

        // Honest barrier: `ORDER BY` with no `LIMIT` (nothing to bound a
        // top-N by) or `DISTINCT` alongside `ORDER BY` (deduping after a
        // bounded top-N could starve rows just outside the window that a
        // dedupe-first pass would have kept) — full materialize, sort,
        // dedupe.
        else
            match rows |> traverseSeq evalKeyed with
            | Error(code, message) -> Err(code, message), [], []
            | Ok keyed ->
                // No `ORDER BY` means every key list is `[]`, so the
                // comparator always returns 0 — skip the sort outright
                // rather than pay for an O(n log n) pass to discover that.
                let sorted =
                    if orderBy.IsEmpty then
                        keyed
                    else
                        keyed |> List.sortWith (fun (ka, _, _) (kb, _, _) -> compareByOrderKeys (List.map snd orderBy) ka kb)

                // Dedupes on the projected columns while still honoring
                // `ORDER BY`'s row order (first occurrence wins) — deduping
                // post-`LIMIT` would undercount, and deduping on the raw
                // pre-projection row would miss two source rows that only
                // agree on the columns actually selected.
                let paired = sorted |> List.map (fun (_, p, d) -> p, d)
                let dedupedPaired = if select.Distinct then paired |> List.distinctBy snd else paired
                let limited = dedupedPaired |> applyLimitOffset limit offset
                ResultSet(colNames, limited |> List.map (fst >> fst)), typesOf (limited |> List.map (fst >> snd)), limited |> List.map (fst >> snd)

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

/// `ON UPDATE CURRENT_TIMESTAMP` columns not named in this statement's own
/// `SET` list (`assignedIdxs`) get bumped to the current time, but only when
/// `row` (after the explicit assignments) actually differs from `original`
/// — MySQL's documented rule ("no update takes place if all columns are set
/// to their current values"); an `UPDATE t SET x = x` that touches nothing
/// real leaves the auto column alone too. An explicit assignment to the
/// auto column itself always wins, whether or not it changes the value —
/// `assignedIdxs` excludes it from this pass entirely.
let private applyOnUpdateTimestamps (columns: ColumnDef list) (assignedIdxs: Set<int>) (original: Value[]) (row: Value[]) : Value[] =
    let autoCols =
        columns
        |> List.indexed
        |> List.filter (fun (i, c) -> c.OnUpdateCurrentTimestamp && not (Set.contains i assignedIdxs))

    if autoCols.IsEmpty || row = original then
        row
    else
        let newRow = Array.copy row

        for i, c in autoCols do
            newRow.[i] <- Storage.currentTimestampForColumn c

        newRow

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
/// ponytail: MySQL's DDL-time restriction errors (3102 nondeterministic fn,
/// 3106 VIRTUAL as PK, 3107 forward reference, 3109 auto_increment ref)
/// aren't validated — misuse degrades to a stale/NULL value, not
/// corruption; add a CREATE/ALTER validation pass if a real workload hits
/// one.
let private computeGeneratedRow
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (table: string)
    (columns: ColumnDef list)
    (row: Value[])
    : Result<Value[], StorageError> =
    let generated = columns |> List.choose (fun c -> c.Generated |> Option.map (fun (e, _) -> c, e))

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
        updateRows store db table None (fun _ -> Ok true) (computeGeneratedRow store registry dbName table columns)
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
    | Placeholder _ -> expr
    | MatchAgainst(cols, q, mode) -> MatchAgainst(cols, sub q, mode)
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
    | Like(e, p, cs, esc) -> Like(sub e, sub p, cs, esc)
    | Regexp(e, p) -> Regexp(sub e, sub p)
    | In(e, xs) -> In(sub e, xs |> List.map sub)
    | Between(e, lo, hi) -> Between(sub e, sub lo, sub hi)
    | Cast(e, ty) -> Cast(sub e, ty)
    | Collate(e, name) -> Collate(sub e, name)
    | Case(subject, whens, elseBranch) ->
        Case(subject |> Option.map sub, whens |> List.map (fun (c, r) -> sub c, sub r), elseBranch |> Option.map sub)
    | Lit _
    | Col _
    | QualifiedCol _
    | Star _
    | RowNumberOver _
    | LagOver _
    | RankOver _
    | PercentRankOver _
    | NTileOver _
    // `VALUES(col)` only ever occurs directly in an `ON DUPLICATE KEY
    // UPDATE` assignment, never inside a subquery's own text — nothing to
    // substitute inside one, so it's left as-is like `Exists` always was.
    | Exists _
    | Subquery _
    | InSubquery _ -> expr

// ---------------------------------------------------------------------------
// EXPLAIN — a pure *description* of what this executor would actually do
// (join order, subquery/derived-table/union structure, current row counts),
// never a speculative index-planner: `type` is `ALL` (a full scan),
// `system` for a 0/1-row table, or `const` with `key`/`key_len`/`ref`
// filled in exactly when `execute`'s own `tryPointLookup` would resolve the
// block through a unique index — the plan reports an index only when the
// execution genuinely uses one. `rows` is the table's *actual* current row
// count (an in-memory `scan`, not an estimate) since this engine can
// afford that where real MySQL's cost-based planner can't.
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
      /// `Some(keyName, keyLen)` when this table row is a `const` unique-key
      /// lookup — rendered into `possible_keys`/`key`/`key_len` with
      /// `ref = const`, exactly when `execute`'s `tryPointLookup` would
      /// take that path.
      Key: (string * int option) option
      Rows: uint64 option
      Extra: string list }

/// Every subquery `expr` embeds, in encounter order — `EXPLAIN`'s source of
/// `SUBQUERY`/`DEPENDENT SUBQUERY` rows, one nested block per subquery form
/// found this way.
let rec private collectSubqueries (expr: Expr) : SelectStmt list =
    match expr with
    | Placeholder _ -> []
    | MatchAgainst(_, q, _) -> collectSubqueries q
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
    | Like(e, p, _, _) -> collectSubqueries e @ collectSubqueries p
    | Regexp(e, p) -> collectSubqueries e @ collectSubqueries p
    | In(e, xs) -> collectSubqueries e @ (xs |> List.collect collectSubqueries)
    | Between(e, lo, hi) -> collectSubqueries e @ collectSubqueries lo @ collectSubqueries hi
    | Cast(e, _) -> collectSubqueries e
    | Collate(e, _) -> collectSubqueries e
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
    | LagOver _
    | RankOver _
    | PercentRankOver _
    | NTileOver _ -> []

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
        | Placeholder _ -> false
        | MatchAgainst(_, q, _) -> references q
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
        | Like(e, p, _, _) -> references e || references p
        | Regexp(e, p) -> references e || references p
        | In(e, xs) -> references e || xs |> List.exists references
        | Between(e, lo, hi) -> references e || references lo || references hi
        | Cast(e, _) -> references e
        | Collate(e, _) -> references e
        | Case(subject, whens, elseBranch) ->
            (subject |> Option.map references |> Option.defaultValue false)
            || whens |> List.exists (fun (c, r) -> references c || references r)
            || (elseBranch |> Option.map references |> Option.defaultValue false)
        | FuncCall(_, args) -> args |> List.exists references
        | Lit _
        | Col _
        | Star _
        | RowNumberOver _
        | LagOver _
        | RankOver _
        | PercentRankOver _
        | NTileOver _ -> false

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
/// MySQL's `key_len` for a one-column unique key: the key part's byte
/// length as real MySQL reports it (utf8mb4 chars are 4 bytes each, `VAR*`
/// adds a 2-byte length prefix, a nullable column adds 1 for the null
/// flag); `None` for a type MySQL wouldn't report a fixed length on.
let private explainKeyLen (col: ColumnDef) : int option =
    let baseLen =
        match col.Type with
        | TTinyInt _ -> Some 1
        | TSmallInt _ -> Some 2
        | TMediumInt _ -> Some 3
        | TInt _ -> Some 4
        | TBigInt _ -> Some 8
        | TChar n -> Some(n * 4)
        | TVarchar n -> Some(n * 4 + 2)
        | TBinary n -> Some n
        | TVarBinary n -> Some(n + 2)
        | TFloat -> Some 4
        | TDouble -> Some 8
        | TDate -> Some 3
        | TEnum values -> Some(if List.length values <= 255 then 1 else 2)
        | _ -> None

    // A PRIMARY KEY column is implicitly NOT NULL in MySQL even when the
    // DDL never said so (and this engine's ColumnDef keeps `Nullable = true`
    // for it), so only a genuinely nullable unique column pays the null flag.
    baseLen |> Option.map (fun n -> if col.Nullable && not col.PrimaryKey then n + 1 else n)

let rec private explainJoinBlock
    (store: Store)
    (dbName: string)
    (nextId: unit -> int)
    (acc: ResizeArray<ExplainRow>)
    (id: int)
    (selectType: string)
    (from: FromItem option)
    (joins: Join list)
    (whereOpt: Expr option)
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
              Key = None
              Rows = rowCount
              Extra = (if idx = tableCount - 1 then extra else []) }

    /// A single-table block whose WHERE ANDs in a unique-key equality is the
    /// exact case `execute` resolves through `tryPointLookup` — report it as
    /// MySQL's `const` (key/key_len/ref filled in, rows = 1), or as the
    /// `no matching row in const table` shape when the key has no such row,
    /// instead of pretending a full scan. Shares `pointLookupEqualities`/
    /// `Storage.tryUniqueKeyProbe` with the executor so the plan can't claim
    /// an index the execution wouldn't use.
    let tryExplainConst (tref: TableRef) : bool =
        if tableCount <> 1 then
            false
        else
            let tableDb = tref.Database |> Option.defaultValue dbName

            let probed =
                pointLookupEqualities tref whereOpt
                |> List.tryPick (fun (n, v) ->
                    Storage.tryUniqueKeyProbe store tableDb tref.Table n v
                    |> Option.map (fun (table, keyName, colIdx) ->
                        let found =
                            Storage.tryUniqueLookup store tableDb tref.Table n v
                            |> Option.map (snd >> List.isEmpty >> not)
                            |> Option.defaultValue false

                        keyName, table.Columns.[colIdx], found))

            match probed with
            | None -> false
            | Some(keyName, keyCol, found) when found ->
                // MySQL shows no `Using where` on a const row — the single
                // row is read at plan time and any leftover conditions are
                // checked against it, not scanned for.
                let extra' = extra |> List.filter ((<>) "Using where")

                acc.Add
                    { Id = Some id
                      SelectType = selectType
                      Table = Some(tref.Alias |> Option.defaultValue tref.Table)
                      Type = Some "const"
                      Key = Some(keyName, explainKeyLen keyCol)
                      Rows = Some 1UL
                      Extra = extra' }

                true
            | Some _ ->
                acc.Add
                    { Id = Some id
                      SelectType = selectType
                      Table = None
                      Type = None
                      Key = None
                      Rows = None
                      Extra = [ "no matching row in const table" ] }

                true

    /// One `FromItem`'s row(s): a real table's stats, or a derived table's
    /// `<derivedN>` placeholder plus its own recursive `DERIVED` block.
    let explainFromItem (idx: int) (item: FromItem) : Result<unit, QueryResult> =
        match item with
        | FromTable tref ->
            explainTableStats store dbName tref
            |> Result.map (fun (n, ty) ->
                if not (tryExplainConst tref) then
                    emitTableRow idx (tref.Alias |> Option.defaultValue tref.Table) n ty)
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
                  Key = None
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

    explainJoinBlock store dbName nextId acc id selectType select.From select.Joins select.Where extra (selectSubqueryExprs select)

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
          r.Key |> Option.map fst
          r.Key |> Option.map fst
          r.Key |> Option.bind (snd >> Option.map string)
          r.Key |> Option.map (fun _ -> "const")
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
                    acc.Add { Id = None; SelectType = "UNION RESULT"; Table = Some label; Type = None; Key = None; Rows = None; Extra = [] })
        )
    | Update u ->
        let id = nextId ()
        let extra = [ if u.Where.IsSome then "Using where"
                      if not u.OrderBy.IsEmpty then "Using filesort" ]
        let subqueryExprs = (u.Where |> Option.toList) @ (u.Assignments |> List.map (fun a -> a.Value)) @ (u.OrderBy |> List.map fst)

        finish (
            checkMutationWhere u.From u.Joins ((u.Where |> Option.toList) @ (u.Assignments |> List.map (fun a -> a.Value)))
            |> Result.bind (fun () -> explainJoinBlock store dbName nextId acc id "UPDATE" (Some(FromTable u.From)) u.Joins u.Where extra subqueryExprs)
        )
    | Delete d ->
        let id = nextId ()
        let extra = [ if d.Where.IsSome then "Using where"
                      if not d.OrderBy.IsEmpty then "Using filesort" ]
        let subqueryExprs = d.Where |> Option.toList

        finish (
            checkMutationWhere d.From d.Joins (d.Where |> Option.toList)
            |> Result.bind (fun () -> explainJoinBlock store dbName nextId acc id "DELETE" (Some(FromTable d.From)) d.Joins d.Where extra subqueryExprs)
        )
    | Insert(table, _, rowsExprs, _, _) ->
        let id = nextId ()

        finish (
            checkTableExists table
            |> Result.map (fun () -> acc.Add { Id = Some id; SelectType = "INSERT"; Table = Some table; Type = None; Key = None; Rows = None; Extra = [] })
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
            |> Result.map (fun () -> acc.Add { Id = Some id; SelectType = "INSERT"; Table = Some table; Type = None; Key = None; Rows = None; Extra = [] })
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

/// Folds one `insertRows`/`insertRowsIgnore`/`upsertRows` result's
/// `(okPacketId, generatedId)` pair into `execute`'s own `ids` accumulator —
/// see `execute`'s doc for what each half means. `0L` from storage means
/// "this statement assigned no id at all", so the OK-packet half falls back
/// to its previous value the same way it always has; the `LAST_INSERT_ID()`
/// half only ever moves forward on an actual generated id.
let private nextIds ((okId, generatedId): int64 * int64) ((newOkId: int64), (newGenerated: int64 option)) : int64 * int64 =
    (if newOkId <> 0L then newOkId else okId), (newGenerated |> Option.defaultValue generatedId)

/// Executes one parsed statement against `store`, threading the session's
/// AUTO_INCREMENT bookkeeping through as a plain value rather than a
/// `Session` (this module knows nothing about sessions or connections —
/// `QueryHandler` is the layer that owns that). Returns the (possibly
/// updated) `ids` alongside the result.
///
/// `ids` is `(okPacketId, generatedId)`: `okPacketId` is what the OK
/// packet's `last_insert_id` reports (first generated id this statement, or
/// else the last explicitly-supplied one — see `Storage.insertCore`'s doc),
/// `generatedId` is the narrower value the SQL function `LAST_INSERT_ID()`
/// reads, which only ever reflects a *generated* id and holds its previous
/// value across a statement that generated none at all. Every non-insert
/// statement passes both straight through unchanged.
/// `foundRows` is the session's negotiated CLIENT_FOUND_ROWS capability
/// (`Protocol.ClientFoundRows`, read by `QueryHandler` off `Session.Capabilities`
/// — this module can't reference `Protocol` itself, it compiles earlier).
/// UPDATE's affected-rows count is MySQL's one behavior this flag actually
/// changes: matched rows (including no-op `SET x = x` ones) when set, changed
/// rows (the default) when not. Every other statement kind ignores it.
let execute (store: Store) (registry: Registry) (dbName: string) (ids: int64 * int64) (foundRows: bool) (stmt: Statement) : (int64 * int64) * QueryResult =
    match stmt with
    | CreateDatabase(name, ifNotExists) ->
        match Storage.createDatabase store name with
        | Ok() -> ids, Affected 0UL
        | Error(DatabaseExists _) when ifNotExists -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | DropDatabase(name, ifExists) ->
        if name.ToLowerInvariant() = "mysql" then
            ids, Err(3552, "Access to system schema 'mysql' is rejected.")
        else
            match Storage.dropDatabase store name with
            | Ok() -> ids, Affected 0UL
            | Error(NoSuchDatabase _) when ifExists -> ids, Affected 0UL
            | Error e -> ids, storageErr e

    | AlterDatabase name ->
        // The charset/collate tail is parsed and discarded (see
        // `Parser.databaseOptions`'s doc) — nothing in the catalog needs to
        // record it, so this is just an existence check.
        if Storage.databaseExists store name then
            ids, Affected 0UL
        else
            ids, storageErr (NoSuchDatabase name)

    | CreateTable(name, columns, indexes, foreignKeys, ifNotExists, tableCharset, tableCollation, autoIncrementSeed) ->
        let db, name = splitQualified dbName name

        match createTableSeeded store db name columns indexes foreignKeys tableCharset tableCollation autoIncrementSeed with
        | Ok() -> ids, Affected 0UL
        | Error(TableExists _) when ifNotExists -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | DropTable(names, ifExists) ->
        let dropOne name =
            let db, name = splitQualified dbName name

            match dropTable store db name with
            | Ok() -> Ok()
            | Error(NoSuchTable _) when ifExists -> Ok()
            | Error e -> Error e

        match names |> traverse dropOne with
        | Ok _ -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | AlterTable(table, actions) ->
        let db, table = splitQualified dbName table

        // Backfills a just-added/modified generated column over existing rows.
        match alterTable store db table actions |> withGeneratedRecomputed store registry dbName db table with
        | Ok() -> ids, Affected 0UL
        | Error e -> ids, storageErr e

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
        | Ok _ -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | CreateIndex(name, table, columns, unique, kind) ->
        let db, table = splitQualified dbName table

        match alterTable store db table [ AddIndex { Name = name; Columns = columns; Unique = unique; Kind = kind } ] with
        | Ok() -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | DropIndexStmt(name, table, _) ->
        // `ifExists` needs no executor logic (see the AST case's doc):
        // dropping a missing index is already a silent no-op, and a missing
        // table errors here even under `IF EXISTS`, matching MySQL.
        let db, table = splitQualified dbName table

        match alterTable store db table [ DropIndexAction name ] with
        | Ok() -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | Truncate table ->
        let db, table = splitQualified dbName table

        match truncate store db table with
        | Ok() -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | CreateUser(users, ifNotExists) ->
        let createOne (name, host, password) =
            match Auth.createUser store name host password with
            | Error(1396, _) when ifNotExists -> Ok()
            | r -> r

        match users |> traverse createOne with
        | Ok _ -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | DropUser(users, ifExists) ->
        let dropOne (name, host) =
            match Auth.dropUser store name host with
            | Error(1396, _) when ifExists -> Ok()
            | r -> r

        match users |> traverse dropOne with
        | Ok _ -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | AlterUser(name, host, password, ifExists) ->
        match Auth.setPassword store name host password with
        | Ok() -> ids, Affected 0UL
        | Error(1396, _) when ifExists -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | Grant(privs, level, users, withGrantOption) ->
        match Auth.grant store privs (Auth.targetOfLevel dbName level) users withGrantOption with
        | Ok() -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | Revoke(privs, level, users) ->
        match Auth.revoke store privs (Auth.targetOfLevel dbName level) users with
        | Ok() -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | Insert(table, columns, rowsExprs, onDuplicateUpdate, ignoreDuplicates) ->
        // INSERT ... VALUES expressions aren't evaluated against any row
        // (no table columns are in scope), just literals/functions — an
        // empty column index turns a stray `Col` reference into a clean
        // 1054 rather than an index-out-of-range.
        let db, table = splitQualified dbName table

        let literalCtx = contextFactory store registry dbName Map.empty Map.empty None [||]

        match rowsExprs |> traverse (traverse (evalExpr literalCtx)) with
        | Error(code, message) -> ids, Err(code, message)
        | Ok rowsValues ->
            let cols = if columns.IsEmpty then None else Some columns

            if onDuplicateUpdate.IsEmpty then
                let insert = if ignoreDuplicates then insertRowsIgnore else insertRows

                match insert store db table cols rowsValues |> withGeneratedRecomputed store registry dbName db table with
                | Ok(newLastId, newGenerated, affected) -> nextIds ids (newLastId, newGenerated), Affected(uint64 affected)
                | Error e -> ids, storageErr e
            else
                match scan store db table with
                | Error e -> ids, storageErr e
                | Ok(tableColumns, _) ->
                    let columnIndex = columnIndexOf tableColumns

                    let applyUpdate (existing: Value[]) (candidate: Value[]) : Result<Value[], StorageError> =
                        let ctx = contextFactory store registry dbName columnIndex (singleQualifier table tableColumns) None existing

                        onDuplicateUpdate
                        |> traverse (fun (name, expr) ->
                            match resolveAssignableColumn tableColumns table name with
                            | Error e -> Error e
                            | Ok idx ->
                                match evalExpr ctx (substituteValuesFunc columnIndex candidate expr) with
                                | Ok v -> Ok(idx, v)
                                | Error err -> Error(ExpressionError err))
                        |> Result.map (fun idxVals ->
                            let newRow = Array.copy existing
                            for idx, v in idxVals do
                                newRow.[idx] <- v
                            let assignedIdxs = idxVals |> List.map fst |> Set.ofList
                            applyOnUpdateTimestamps tableColumns assignedIdxs existing newRow)

                    let computeGenerated = computeGeneratedRow store registry dbName table tableColumns

                    match upsertRows store db table cols rowsValues computeGenerated applyUpdate foundRows |> withGeneratedRecomputed store registry dbName db table with
                    | Ok(newLastId, newGenerated, affected) -> nextIds ids (newLastId, newGenerated), Affected(uint64 affected)
                    | Error e -> ids, storageErr e

    | InsertSelect(table, columns, select, ignoreDuplicates) ->
        let db, table = splitQualified dbName table

        let selectResult, _, _ = runSelectStmt store registry dbName select None

        match selectResult with
        | Err(code, message) -> ids, Err(code, message)
        | Affected _ -> ids, Err(1064, "INSERT ... SELECT source did not return a resultset")
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
            | Ok(newLastId, newGenerated, affected) -> nextIds ids (newLastId, newGenerated), Affected(uint64 affected)
            | Error e -> ids, storageErr e

    | Select select ->
        let result, _, _ = runSelectStmt store registry dbName select None
        ids, result

    | Union(first, rest, orderBy, limit, offset) ->
        let result, _, _ = runUnionStmt store registry dbName first rest orderBy limit offset
        ids, result

    | Update updateStmt when updateStmt.Joins.IsEmpty ->
        let db, table = (updateStmt.From.Database |> Option.defaultValue dbName), updateStmt.From.Table
        let tableAlias = updateStmt.From.Alias |> Option.defaultValue updateStmt.From.Table

        // `WHERE <PK/UNIQUE col> = <literal>` narrows to its O(log n) index
        // candidate the same way `tryPointLookup` already does for `SELECT`
        // (see its doc) — pure narrowing to a superset of the real WHERE
        // match, so `selectMutationTargets` below still runs the complete,
        // unmodified WHERE over whatever this produces; a miss (no such
        // index, a non-literal operand, ...) falls back to the ordinary
        // full scan, never a correctness risk. `narrowed`'s positions are
        // threaded into `Storage.updateRows` below too, so a narrowed
        // `UPDATE` never walks the rest of `table.Rows` at all — not just
        // the WHERE evaluation, but the rewrite fold itself.
        let narrowed = tryPointLookup store dbName updateStmt.From updateStmt.Where

        let scanned =
            narrowed
            |> Option.map (fun (cols, rows) -> Ok(cols, rows |> List.map snd |> Seq.ofList))
            |> Option.defaultWith (fun () -> scan store db table)

        match scanned with
        | Error e -> ids, storageErr e
        | Ok(columns, rows) ->
            let columnIndex = columnIndexOf columns

            match updateStmt.Assignments |> traverse (fun a -> resolveAssignableColumn columns table a.Column |> Result.map (fun i -> i, a.Value)) with
            | Error e -> ids, storageErr e
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
                | Error(code, message) -> ids, Err(code, message)
                | Ok _ ->
                    match selectMutationTargets ctxFor (List.ofSeq rows) check updateStmt.OrderBy updateStmt.Limit with
                    | Error(code, message) -> ids, Err(code, message)
                    | Ok targetRows ->
                        let targetSet = referenceSet targetRows
                        let predicate row = Ok(targetSet.Contains row)
                        let assignedIdxs = indexedAssignments |> List.map fst |> Set.ofList

                        let updater row =
                            applyAssignments store registry dbName columnIndex qualifiers indexedAssignments row
                            |> Result.map (applyOnUpdateTimestamps columns assignedIdxs row)

                        let candidates = narrowed |> Option.map snd

                        match updateRows store db table candidates predicate updater |> withGeneratedRecomputed store registry dbName db table with
                        | Ok changed ->
                            // `targetRows` is the WHERE/ORDER BY/LIMIT match
                            // set already computed above — matched rows,
                            // unlike `changed`, needs no extra pass over the
                            // table.
                            ids, Affected(uint64 (if foundRows then targetRows.Length else changed))
                        | Error e -> ids, storageErr e

    | Update updateStmt ->
        // Multi-table `UPDATE t1 JOIN t2 ON ... SET ...` — resolves the
        // whole join, then for each matched combined row, assigns to
        // whichever source table each `SET` target names, claiming a
        // physical row (by reference) the first time a matched row touches
        // it so a row reached through more than one join match is still
        // updated at most once (see `Ast.UpdateStmt`'s doc). Runs the writes
        // against a private snapshot store, merged back via
        // `Storage.mergeCatalogInto` (see its doc) — same optimistic,
        // per-database-serialized pattern every other write uses, so this
        // statement doesn't block a concurrent write to an unrelated
        // database. ponytail: a join source qualified onto a database other
        // than the current session's isn't covered by that database's own
        // `TransactionGates` gate (only the session's current database's
        // is held before reaching `Executor`), so a target table living in
        // a *different* database can still race a concurrent writer to that
        // same table there — the same documented gap as a cross-database
        // transaction (see `Storage.Store`'s doc); upgrade to acquiring
        // every database a statement's join sources actually name if that
        // ever matters (Laravel's per-worker-database test parallelism,
        // the case this all exists for, only ever joins within one
        // database).
        (
            match runMutationJoin store registry dbName updateStmt.From updateStmt.Joins with
            | Error e -> ids, e
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

                // A generated column can't be a SET target (MySQL 3105).
                let guardAssignable ((srcIdx, colIdx, v): int * int * Expr) : Result<int * int * Expr, EvalError> =
                    let _, tableRef, cols = sources.[srcIdx]
                    let col = List.item colIdx cols

                    if col.Generated.IsSome then
                        Error(toMySqlError (GeneratedColumnAssignment(col.Name, tableRef.Table)))
                    else
                        Ok(srcIdx, colIdx, v)

                match updateStmt.Assignments |> traverse (resolveAssignment >> Result.bind guardAssignable) with
                | Error(code, message) -> ids, Err(code, message)
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
                    // as `Ast.UpdateStmt`'s "at most once" doc
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

                                // Claimed once per (srcIdx, physRow) *before*
                                // the assignment loop below, not inside it:
                                // the claim must span the whole `SET` list
                                // (`claims.Contains` flips true the moment
                                // the first column claims the row, so a
                                // per-assignment check would drop every
                                // later column for the same table, e.g. `b`
                                // in `SET t1.a = 10, t1.b = 20`).
                                let claimedThisRow =
                                    identities
                                    |> List.mapi (fun srcIdx identity ->
                                        match identity with
                                        | None -> false
                                        | Some physRow ->
                                            if claims.[srcIdx].Contains physRow then
                                                false
                                            else
                                                claims.[srcIdx].Add physRow |> ignore
                                                true)
                                    |> Array.ofList

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

                                                if claimedThisRow.[srcIdx] then
                                                    let existing = match pending.[physIdx].TryGetValue physRow with true, vs -> vs | false, _ -> []
                                                    pending.[physIdx].[physRow] <- existing @ [ colIdx, v ]

                                            go rest)

                                go resolvedAssignments)

                    match joinedRows |> traverse processRow with
                    | Error(code, message) -> ids, Err(code, message)
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
                        let baseCatalog = store.Catalog
                        let snapshot = Storage.beginTransactionSnapshot store

                        // Any source alias resolving to a given physical
                        // table shares its column list (same physical
                        // schema), so the first one found suffices to look
                        // up `ON UPDATE CURRENT_TIMESTAMP` columns for it.
                        let physicalColumns =
                            physicalGroups
                            |> Array.mapi (fun i _ ->
                                let srcIdx = sourcePhys |> Array.findIndex ((=) i)
                                let _, _, cols = sources.[srcIdx]
                                cols)

                        let assignedIdxsByPhys =
                            physicalGroups
                            |> Array.mapi (fun i _ ->
                                resolvedAssignments
                                |> List.choose (fun (srcIdx, colIdx, _) -> if sourcePhys.[srcIdx] = i then Some colIdx else None)
                                |> Set.ofList)

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

                                            Ok(applyOnUpdateTimestamps physicalColumns.[i] assignedIdxsByPhys.[i] row newRow)
                                        | false, _ -> Ok row

                                    updateRows snapshot tdb tname None predicate updater |> withGeneratedRecomputed snapshot registry dbName tdb tname)
                            |> Array.toList
                            |> traverse id

                        match apply with
                        | Ok counts ->
                            Storage.mergeCatalogInto store baseCatalog snapshot.Catalog
                            Storage.commitTransactionEvents store snapshot
                            // `pending.[i].Count` is the matched-row count per
                            // physical table (every row this JOIN claimed for
                            // it, whether or not the write actually changed
                            // anything) — `counts` from `updateRows` is the
                            // changed-row count `List.sum counts` reports by
                            // default.
                            let matched = pending |> Array.sumBy (fun d -> d.Count)
                            ids, Affected(uint64 (if foundRows then matched else List.sum counts))
                        | Error e -> ids, storageErr e)

    | Delete deleteStmt when deleteStmt.Joins.IsEmpty ->
        let db, table = (deleteStmt.From.Database |> Option.defaultValue dbName), deleteStmt.From.Table
        let tableAlias = deleteStmt.From.Alias |> Option.defaultValue deleteStmt.From.Table

        match scan store db table with
        | Error e -> ids, storageErr e
        | Ok(columns, rows) ->
            let columnIndex = columnIndexOf columns

            let ctxFor = contextFactory store registry dbName columnIndex (singleQualifier tableAlias columns) None

            let check = whereMatches ctxFor deleteStmt.Where

            match check (probeRow columns) with
            | Error(code, message) -> ids, Err(code, message)
            | Ok _ ->
                match selectMutationTargets ctxFor (List.ofSeq rows) check deleteStmt.OrderBy deleteStmt.Limit with
                | Error(code, message) -> ids, Err(code, message)
                | Ok targetRows ->
                    let targetSet = referenceSet targetRows
                    let predicate row = Ok(targetSet.Contains row)

                    match deleteRows store db table predicate with
                    | Ok affected -> ids, Affected(uint64 affected)
                    | Error e -> ids, storageErr e

    | Delete deleteStmt ->
        // Multi-table `DELETE t1[, t2] FROM t1 JOIN t2 ON ...` / `DELETE
        // FROM t1 USING t1 JOIN t2 ON ...` — resolves the whole join, marks
        // (by reference) every physical row of every named `Targets` table
        // that any matched combined row touches, then removes each target
        // table's marked rows via `Storage.deleteRows`, one call per table.
        // A physical row reached through more than one join match is still
        // only in the set once (a `HashSet`), so it's deleted at most once.
        match runMutationJoin store registry dbName deleteStmt.From deleteStmt.Joins with
        | Error e -> ids, e
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
            | Error(code, message) -> ids, Err(code, message)
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
                | Error(code, message) -> ids, Err(code, message)
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
                    | Ok counts -> ids, Affected(uint64 (List.sum counts))
                    | Error e -> ids, storageErr e

    | Explain inner ->
        ids, explainStatement store registry dbName inner
