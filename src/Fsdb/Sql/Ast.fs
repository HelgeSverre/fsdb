/// The SQL abstract syntax tree the parser produces and the executor
/// consumes. Types only, no behavior — every case here is data.
module Fsdb.Ast

open System.Text
open Fsdb.Value

/// A comparison/logical/arithmetic binary operator, shared by every
/// `BinOp` node rather than splitting expressions into separate DU cases
/// per operator.
type Op =
    | And
    | Or
    /// `XOR` — logical exclusive or, three-valued: `NULL XOR anything` is
    /// NULL, otherwise `truthy a <> truthy b`. Binds tighter than `OR`,
    /// looser than `AND` (MySQL's own precedence level).
    | Xor
    | Eq
    | Neq
    | Lt
    | Lte
    | Gt
    | Gte
    | Add
    | Sub
    /// `-` parsed under `NO_UNSIGNED_SUBTRACTION`; exact integer operands
    /// produce a signed BIGINT result even when either side is unsigned.
    | SignedSub
    | Mul
    | Div
    /// `DIV` — MySQL's integer-division operator: always an `INT` (or
    /// `NULL`), truncated toward zero, distinct from `/`'s decimal-scaled
    /// result.
    | IntDiv
    /// `<=>` — the null-safe equals operator: like `Eq` except `NULL <=> NULL`
    /// is true (rather than `NULL`) and `NULL <=> anything-else` is false
    /// (rather than `NULL`) — it never returns SQL's three-valued unknown.
    | NullSafeEq

type ColumnType =
    | TTinyInt of unsigned: bool
    /// `BOOLEAN`/`BOOL`, and the `TINYINT(1)` they are a synonym for. Stored
    /// and range-checked exactly like `TTinyInt false`; separate only because
    /// clients decide "boolean" from the declared display width of 1, so a
    /// resultset column has to advertise its display width and
    /// `information_schema` has to render `tinyint(1)`. A plain `TINYINT` is
    /// width 4 and stays `TTinyInt`.
    | TBool
    | TSmallInt of unsigned: bool
    | TMediumInt of unsigned: bool
    | TInt of unsigned: bool
    | TBigInt of unsigned: bool
    | TBit of width: int
    | TChar of length: int
    | TVarchar of length: int
    | TTinyText
    | TText
    | TMediumText
    | TLongText
    | TBinary of length: int
    | TVarBinary of length: int
    | TTinyBlob
    | TBlob
    | TMediumBlob
    | TLongBlob
    /// The allowed value set, stored so `Storage.coerceValue` can validate an
    /// inserted string against it.
    | TEnum of values: string list
    /// Accepted like a string column; comma-set validation is not performed
    /// against `values`, add it if a migration actually needs SET semantics
    /// enforced rather than just accepted.
    | TSet of values: string list
    | TDecimal of precision: int * scale: int * unsigned: bool
    | TDouble of unsigned: bool
    | TFloat of unsigned: bool
    | TDate
    /// The `int` is the fractional-seconds precision (fsp, 0-6) a
    /// `DATETIME(N)`/`TIMESTAMP(N)`/`TIME(N)` declares — 0 for a bare
    /// `DATETIME`. It drives how many sub-second digits a value of the
    /// column renders with (a `DATETIME(6)` on an exact second still shows
    /// `.000000`), the coercion rounding of an inserted value's fraction,
    /// the binary-protocol `decimals` field, and the `datetime(N)`
    /// `information_schema`/`SHOW COLUMNS` type text.
    | TDateTime of fsp: int
    | TTimestamp of fsp: int
    | TTime of fsp: int
    | TYear
    | TJson
    | TGeometry of kind: GeometryKind
    /// MySQL 9's `VECTOR(N)` — N little-endian 4-byte floats stored/wired as
    /// a binary string. A bare `VECTOR` declares MySQL's default dimension
    /// 2048; the 16383 ceiling (and the can't-be-a-key rule) is enforced at
    /// DDL time in `Storage`, where the column name is in scope to report.
    | TVector of dim: int

/// A user-variable reference's case-folded lookup key and exact SQL token.
type UserVariableRef =
    { Name: string
      Sql: string }

type MatchColumn =
    { Qualifier: string option
      Name: string }

module UserVariableRef =
    let validationError (variable: UserVariableRef) =
        let length = variable.Name.EnumerateRunes() |> Seq.length

        if length = 0 then
            Some "User variable name is empty"
        elif length > 64 then
            Some(sprintf "User variable name '%s' is too long" variable.Name)
        else
            None

// `Expr` and `SelectStmt` are mutually recursive: `Exists`/a scalar subquery
// carries a nested `SelectStmt`, whose projections/`WHERE` are themselves
// built from `Expr`. Every type in between (`Projection`, `TableRef`, ...)
// rides along in the same `and` chain since `SelectStmt` needs them.
type Expr =
    | Lit of Value
    // A `?` parameter placeholder in a prepared statement, numbered by its
    // position in the SQL text. Bound to a `Lit` by `QueryHandler.bindPlaceholders`
    // before execution — the executor never sees one.
    | Placeholder of index: int
    | UserVariable of variable: UserVariableRef
    | SystemVariable of scope: string option * name: string
    | AssignUserVariable of variable: UserVariableRef * value: Expr
    | Col of name: string
    | QualifiedCol of table: string * column: string
    /// A parenthesized value list such as `(a, b)`. Row values only produce a
    /// scalar result when a row-aware predicate consumes them.
    | Row of Expr list
    | BinOp of Op * Expr * Expr
    | Not of Expr
    | IsNull of Expr
    | IsNotNull of Expr
    /// `IS TRUE` / `IS FALSE` — three-valued like the `IsNull` pair: `NULL IS
    /// TRUE` and `NULL IS FALSE` both evaluate to false (not NULL), since a
    /// truth test is always a plain boolean answer, never itself unknown.
    | IsTrue of Expr
    | IsFalse of Expr
    /// `caseSensitive` is set by `LIKE BINARY`, MySQL's shorthand for a
    /// byte-for-byte (rather than the engine's default case-insensitive)
    /// pattern match — plain `LIKE` always sets it false. `escape` is the
    /// character an `ESCAPE '<c>'` clause names for un-wildcarding `%`/`_`
    /// in the pattern; `None` means MySQL's default, backslash.
    | Like of Expr * pattern: Expr * caseSensitive: bool * escape: char option
    | Regexp of Expr * pattern: Expr
    | In of Expr * candidates: Expr list
    /// `expr IN (SELECT ...)` — the candidate set is a subquery's first
    /// column rather than a literal list; `Not(InSubquery(...))` is `NOT IN
    /// (SELECT ...)`, the same desugaring `In`'s own `NOT IN` already uses.
    | InSubquery of Expr * SelectStmt
    | Between of Expr * lo: Expr * hi: Expr
    | FuncCall of name: string * args: Expr list
    /// `MATCH (cols) AGAINST ('query' [mode])` — relevance over the
    /// FULLTEXT-indexed columns; computed as a whole-table pre-pass (the
    /// IDF half needs corpus statistics), like the window functions below.
    | MatchAgainst of columns: MatchColumn list * query: Expr * mode: MatchMode
    /// `fn(...) OVER (spec)` / `fn(...) OVER window_name` — every window
    /// function in one case (see `WindowFn`), since the executor's pre-pass
    /// treats them all the same way: partition, order, frame, then one
    /// synthetic column per distinct node. A window call can sit anywhere
    /// inside a larger expression (`value - LAG(value) OVER (...)` is a real
    /// report query), so `Executor` finds and substitutes every occurrence
    /// rather than only a bare top-level projection.
    | WindowOver of fn: WindowFn * over: OverClause
    /// Marks `DISTINCT expr` as an aggregate call's argument (`COUNT(DISTINCT
    /// x)`, `SUM(DISTINCT x)`, ...) — only meaningful as the (unwrapped) sole
    /// argument of a `FuncCall` the executor recognizes as an aggregate;
    /// anywhere else it's a parse shape that can't occur, since the parser
    /// only ever produces it inside a function call's argument list.
    | Distinct of Expr
    /// Marks one `ORDER BY` key inside `GROUP_CONCAT(expr ORDER BY key
    /// [ASC|DESC], ...)` — only meaningful as one of `GROUP_CONCAT`'s
    /// trailing arguments (see `Parser.groupConcatAtom`), the same
    /// call-site-only contract `Distinct` documents above.
    | OrderBy of Expr * Direction
    /// Minimal `CAST(expr AS type)` — reuses `ColumnType` rather than a
    /// separate cast-target vocabulary, coerced the same way a column of
    /// that type would be (see `Storage.coerceValue`).
    | Cast of Expr * ColumnType
    /// `expr COLLATE collation_name` — evaluates as its inner expression;
    /// the tag overrides the collation any *comparison* involving this
    /// expression resolves under (see `Executor.resolvedCollation`).
    /// Evaluated against the collation registry at parse time.
    | Collate of Expr * collation: string
    /// `SELECT *` (`None`) / `SELECT t.*` (`Some "t"`) — the qualifier
    /// matters once there's a `JOIN` in scope: `Executor.evalProjection`
    /// expands `t.*` to just `t`'s own columns via `EvalContext.Qualifiers`,
    /// not every joined table's columns concatenated (which is what an
    /// unqualified `*` still means, `FROM`-order, joins included).
    | Star of string option
    /// `EXISTS (SELECT ...)` — true iff the subquery returns at least one
    /// row.
    | Exists of SelectStmt
    /// `(SELECT ...)` used as a value — the subquery must yield exactly one
    /// column; zero rows evaluates to `NULL`, more than one row is MySQL
    /// error 1242, exactly one row yields that row's single column value.
    | Subquery of SelectStmt
    /// `left operator ANY (SELECT ...)` / `SOME` / `ALL`. `SOME` is parsed
    /// as `Any`, because MySQL defines the two spellings identically.
    | QuantifiedComparison of left: Expr * operator: Op * quantifier: Quantifier * SelectStmt
    /// `CASE WHEN cond THEN result ... [ELSE result] END` (the "searched"
    /// form, `subject = None`, each `whens` key is a boolean condition) or
    /// `CASE subject WHEN value THEN result ... [ELSE result] END` (the
    /// "simple" form, `subject = Some ...`, each `whens` key compares equal
    /// to `subject` instead) — one case instead of two so the executor's
    /// every other `Expr`-walking function (`containsAggregate`,
    /// `rewriteAggregates`, ...) only needs one branch to recurse through.
    | Case of subject: Expr option * whens: (Expr * Expr) list * elseBranch: Expr option

and Quantifier =
    | Any
    | All

and Direction =
    | Asc
    | Desc

/// One `ORDER BY` key: the expression to sort by and its direction.
and OrderKey = Expr * Direction

/// `ROWS` counts physical rows around the current one; `RANGE` counts
/// *values* of the window's single `ORDER BY` key (so tied rows — peers —
/// enter and leave the frame together).
and FrameUnit =
    | FrameRows
    | FrameRange

/// One end of a `BETWEEN ... AND ...` frame. The `Expr` of a
/// `PRECEDING`/`FOLLOWING` bound is MySQL's offset — a row count under
/// `ROWS`, a value distance under `RANGE`.
and FrameBound =
    | UnboundedPreceding
    | BoundPreceding of Expr
    | CurrentRow
    | BoundFollowing of Expr
    | UnboundedFollowing

and WindowFrame =
    { Unit: FrameUnit
      Start: FrameBound
      End: FrameBound }

/// An `OVER (...)` body. `Frame = None` means MySQL's default: `RANGE
/// BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW` when `OrderBy` is non-empty
/// (a running total), the whole partition when it's empty.
and WindowSpec =
    { Inherit: string option
      PartitionBy: Expr list
      OrderBy: OrderKey list
      Frame: WindowFrame option }

/// `OVER (...)` written inline, or `OVER w` naming a `WINDOW w AS (...)`
/// definition — resolved against `SelectStmt.Windows` at execution time
/// (the parser can't: the `WINDOW` clause is written *after* the
/// projections that reference it).
and OverClause =
    | OverSpec of WindowSpec
    | OverName of string

/// Which window function a `WindowOver` node calls. Frame-sensitive ones
/// (the aggregates, `FIRST_VALUE`/`LAST_VALUE`/`NTH_VALUE`) read only the
/// rows the frame covers; the ranking and offset families ignore the frame
/// entirely, exactly as MySQL defines them.
and WindowFn =
    | WinRowNumber
    /// `RANK()` (`dense = false`) / `DENSE_RANK()` (`dense = true`) — both
    /// give tied rows (under the window `ORDER BY`) the same number; `RANK`
    /// then skips as many following ranks as there were ties, `DENSE_RANK`
    /// never skips.
    | WinRank of dense: bool
    /// `(rank - 1) / (rows_in_partition - 1)`, using `RANK`'s tie-aware
    /// numbering; `0` for a one-row partition (MySQL never divides by zero).
    | WinPercentRank
    /// `CUME_DIST()` — rows at or before the current row's peer group over
    /// the partition size.
    | WinCumeDist
    /// `NTILE(n)` — splits each partition into `n` groups as evenly as
    /// possible, earlier groups absorbing the remainder (a 10-row partition
    /// into 3 buckets is 4/3/3, not 3/3/4).
    | WinNTile of buckets: Expr
    /// `LAG(expr[, offset[, default]])` / `LEAD(...)` (`lead = true`) — the
    /// value `offset` rows back (or forward) in the partition; `default`
    /// (NULL when omitted) when that row falls outside the partition.
    | WinLagLead of lead: bool * expr: Expr * offset: Expr option * deflt: Expr option
    | WinFirstValue of Expr
    | WinLastValue of Expr
    /// `NTH_VALUE(expr, n)` — the nth row of the frame (1-based), NULL when
    /// the frame is shorter than `n`.
    | WinNthValue of Expr * n: Expr
    /// An ordinary aggregate used as a window function (`SUM(x) OVER (...)`)
    /// — evaluated by handing the frame's rows to the same
    /// `Executor.rewriteAggregates` a `GROUP BY` uses, so every aggregate
    /// (including `GROUP_CONCAT`/`COUNT(DISTINCT ...)`) works unchanged.
    | WinAggregate of name: string * args: Expr list

/// A column's `DEFAULT`: a fixed value, `CURRENT_TIMESTAMP`, or an expression
/// evaluated for each omitted row value. The timestamp has its own case rather
/// than a marker string so storage can apply the column's declared precision.
and ColumnDefault =
    | DConst of Value
    | DCurrentTimestamp
    | DExpression of Expr

/// VIRTUAL vs STORED on a generated column. Both retain a write-time value
/// for constraint and index maintenance; query sources recompute VIRTUAL
/// values, while STORED values read the retained cell. The default is VIRTUAL.
and GeneratedKind =
    | Virtual
    | Stored

and NumericDisplay =
    { Width: int option
      Decimals: int option
      ZeroFill: bool }

and ColumnDef =
    { Name: string
      Type: ColumnType
      NumericDisplay: NumericDisplay option
      Nullable: bool
      Default: ColumnDefault option
      AutoIncrement: bool
      PrimaryKey: bool
      Unique: bool
      /// `ON UPDATE CURRENT_TIMESTAMP` — bumped by `Executor`'s `UPDATE`
      /// path to the current time (at the column's own declared fsp, same
      /// as `DCurrentTimestamp`) whenever the row actually changes and the
      /// statement didn't already assign this column itself.
      OnUpdateCurrentTimestamp: bool
      /// `[GENERATED ALWAYS] AS (expr) [VIRTUAL | STORED]` — `None` for a
      /// plain column.
      Generated: (Expr * GeneratedKind) option
      /// The column's `COMMENT` text, empty when omitted.
      Comment: string
      /// The column's `COLLATE name` (table-level `COLLATE` baked in as the
      /// default at parse time) — `None` means the server default
      /// (`Collation.defaultCollation`, utf8mb4_0900_ai_ci). Only meaningful
      /// for the string types; kept on every column so DDL round-trips the
      /// declaration as written.
      Collation: string option
      /// The column's `CHARACTER SET name` (table-level charset baked in
      /// the same way) — `None` means utf8mb4. Supported: utf8mb4, latin1,
      /// ascii. String storage is still .NET UTF-16; the charset drives
      /// write-time validation (`ascii` rejects non-ASCII with 1366 in
      /// strict mode, `latin1` lossy-maps unencodables to '?' — both
      /// MySQL-verified).
      Charset: string option }

and IndexKind =
    | BTree
    | FullTextIndex

and MatchMode =
    | NaturalLanguage
    | BooleanMode
    | QueryExpansion

and IndexTransform =
    | Lowercase
    | Uppercase
    | Expression of Expr

and IndexColumn =
    { Name: string
      PrefixLength: int option
      Transform: IndexTransform option
      Direction: Direction }

and IndexDef =
    { Name: string
      KeyColumns: IndexColumn list
      Unique: bool
      Visible: bool
      /// `FULLTEXT KEY` vs an ordinary index — drives `MATCH ... AGAINST`
      /// eligibility and the `Index_type` introspection column. SPATIAL
      /// collapses to `BTree` until fsdb has a spatial-index implementation.
      Kind: IndexKind }

    member this.Columns =
        this.KeyColumns
        |> List.choose (fun column -> if column.Name = "" then None else Some column.Name)

and ForeignKeyDef =
    { Name: string
      Columns: string list
      RefDatabase: string option
      RefTable: string
      RefColumns: string list
      OnDelete: string option
      OnUpdate: string option }

/// A table- or column-level `[CONSTRAINT name] CHECK (expr) [[NOT]
/// ENFORCED]`. `Name = None` is resolved to MySQL's `table_chk_N` form once
/// the target schema/table and its existing constraint names are known.
/// `Column = Some name` preserves the stricter rule that an inline column
/// check may only reference the column it is declared on.
and CheckConstraintDef =
    { Name: string option
      Expression: Expr
      Enforced: bool
      Column: string option }

/// A `SELECT` projection: the expression and its optional `AS alias`.
and Projection = Expr * string option

/// `FROM [db.]table [[AS] alias]`, preserving qualification and aliasing.
and TableRef =
    { Database: string option
      Table: string
      Alias: string option
      Partitions: string list }

/// A derived table's body: a plain `SELECT`, or `(SELECT ...) UNION
/// (SELECT ...) ...` — MySQL allows a `UNION` directly inside `FROM (...)
/// AS alias` (Laravel's `unionAll(...)->paginate()` compiles to exactly
/// this: `SELECT COUNT(*) FROM ((SELECT ...) UNION (SELECT ...)) AS
/// alias`), so `FromSubquery` needs to carry either shape rather than just
/// a bare `SelectStmt`.
and SelectOrUnion =
    | PlainSelect of SelectStmt
    | UnionSelect of first: SelectStmt * rest: (SetOp * SelectStmt) list * orderBy: OrderKey list * limit: Expr option * offset: Expr option

/// One `WITH` binding: `name [(col, ...)] AS (body)`. `Recursive` is the
/// whole `WITH RECURSIVE` clause's flag (MySQL marks the clause, not the
/// individual CTE), which only changes how the body is evaluated — a
/// recursive body's `UNION [ALL]` splits into an anchor half and a half
/// that re-reads `name` until it stops producing rows.
/// (Field names are `Cte`-prefixed so record-label inference elsewhere in
/// the AST — `ForeignKeyDef`, `IndexDef`, both `Name`/`Columns` records —
/// keeps resolving to what it always did.)
and CommonTableExpr =
    { CteName: string
      CteColumns: string list
      Recursive: bool
      Body: SelectOrUnion }

/// One `UNION`/`INTERSECT`/`EXCEPT` between two branches. `all` keeps
/// duplicates (multiset semantics); without it the operator's result is
/// distinct, which is MySQL's default for all three.
///
/// `INTERSECT` binds *tighter* than `UNION`/`EXCEPT` (oracle-verified:
/// `SELECT 1 UNION SELECT 2 INTERSECT SELECT 3` is one row, `1`), so a flat
/// list of these is not a left-to-right fold — `Executor.runTopLevelUnion`
/// collapses the `INTERSECT` runs first. `UNION` and `EXCEPT` share one
/// precedence level and associate left to right.
and SetOp =
    | OpUnion of all: bool
    | OpIntersect of all: bool
    | OpExcept of all: bool

/// A `SELECT`'s `FROM` target: a real (or `information_schema` virtual)
/// table, or a derived table — `FROM (SELECT ...) AS alias` — whose alias is
/// mandatory (MySQL requires one) and qualifies `t.col` references like a
/// real table alias.
and FromItem =
    | FromTable of TableRef
    | FromSubquery of SelectOrUnion * alias: string
    /// `LATERAL (SELECT ...) AS alias` — a derived table that may reference
    /// the columns of the tables to its left in the same `FROM`, so it is
    /// re-evaluated once per left row (like `FromJsonTable`'s correlated
    /// form) instead of resolved once. Only ever reachable as a join
    /// target: a leading `FROM LATERAL (...)` has nothing to correlate to,
    /// and MySQL rejects it too.
    | FromLateral of SelectOrUnion * alias: string
    /// `JSON_TABLE(expr, 'path' COLUMNS (...)) alias` — the table function
    /// that explodes a JSON document into rows, one per node the row path
    /// matches. `source` is an arbitrary expression so the correlated form
    /// (`FROM t, JSON_TABLE(t.doc, ...)`) carries the left table's column
    /// reference; the alias is mandatory (MySQL's 3667 "Every table function
    /// must have an alias"), enforced by the grammar like a derived table's.
    | FromJsonTable of source: Expr * path: string * columns: JsonTableColumn list * alias: string

and JsonTableAction =
    | JsonNull
    | JsonDefault of Value
    | JsonError

/// One column of a `JSON_TABLE(...) COLUMNS (...)` clause.
and JsonTableColumn =
    /// `name FOR ORDINALITY` — 1-based row counter, restarting per source row.
    | ForOrdinality of name: string
    /// `name TYPE PATH 'path' [DEFAULT lit ON EMPTY] [DEFAULT lit ON ERROR]`
    /// — extracted, unquoted, coerced. `onEmpty`/`onError` carry NULL,
    /// decoded JSON defaults, or the request to raise an error.
    | PathColumn of name: string * ColumnType * path: string * onEmpty: JsonTableAction * onError: JsonTableAction
    /// `name TYPE EXISTS PATH 'path'` — 1 when the path matches at least one
    /// node in the row, 0 otherwise; never NULL, never an error.
    | ExistsColumn of name: string * ColumnType * path: string
    /// `NESTED PATH 'path' COLUMNS (...)` — expands each node the nested
    /// path matches *within* the parent node into its own row, repeating
    /// the parent's columns; a parent whose nested path matches nothing
    /// still yields one row with the nested columns NULL (MySQL's outer
    /// semantics), and sibling NESTED PATHs never cross-join — each
    /// sibling's rows carry NULL for the others' columns.
    | NestedColumns of path: string * columns: JsonTableColumn list

/// `INNER`/`CROSS JOIN` require a matching row on `On` (`CROSS JOIN` has no
/// `ON` at all — the parser gives it the always-true `Lit (VInt 1L)` so it
/// shares `INNER JOIN`'s matching logic and produces every combination, the
/// Cartesian product); `LEFT JOIN` keeps every left-hand row even without a
/// match, padding the right-hand columns with `NULL`; `RIGHT JOIN` is the
/// mirror, keeping every right-hand row and padding the left-hand columns
/// (SQL's standard outer-join semantics either way).
and JoinKind =
    | InnerJoin
    | LeftJoin
    | RightJoin
    | CrossJoin
    /// `NATURAL [INNER] JOIN` — the equi-join over every column the two
    /// sides have in common (matched case-insensitively by name, resolved
    /// at execution time since the parser only sees names, not schemas);
    /// no common columns degenerates to a full Cartesian product. The
    /// parser rejects an `ON` after `NATURAL`, matching MySQL's 1064.
    | NaturalJoin
    /// `NATURAL LEFT [OUTER] JOIN` / `NATURAL RIGHT [OUTER] JOIN` — the
    /// outer-join variants of `NaturalJoin`.
    | NaturalLeftJoin
    | NaturalRightJoin

/// One `[INNER | LEFT [OUTER] | RIGHT [OUTER]] JOIN table {ON expr |
/// USING (cols)}` clause (plus the `NATURAL`/`CROSS` variants in
/// `JoinKind`), applied against whatever's already in scope to its left
/// (the `FROM` table, or the result of an earlier `Join` in the same list —
/// this engine only ever nests joins left-to-right, matching how they're
/// written). `Table` is a `FromItem`, not a bare `TableRef`, so
/// `JOIN (SELECT ...) AS alias ON ...` (Eloquent's `joinSub`/`leftJoinSub`)
/// parses the same derived-table shape the leading `FROM` already does; a
/// multi-table `UPDATE`/`DELETE ... JOIN` still only accepts `FromTable`
/// (see `Executor.applyMutationJoin`).
///
/// `Using` is the `JOIN ... USING (col, ...)` column list — like `ON`, it
/// cannot be combined with an explicit `ON` (MySQL rejects that too), so
/// `On` is always the always-true literal for the `Using`/`NATURAL` kinds
/// and the equi-keys come from the column names at execution time instead.
and Join =
    { Kind: JoinKind
      Table: FromItem
      On: Expr
      Using: string list }

and LockingReadStrength =
    | UpdateLock
    | ShareLock

and LockingReadWait =
    | WaitForLocks
    | NoWait
    | SkipLocked

and LockingRead =
    { Strength: LockingReadStrength
      Tables: string list
      Wait: LockingReadWait }

/// A `SELECT` statement's clauses as a record rather than a positional
/// tuple: every clause after `SELECT ... FROM` is optional and grows
/// independently, so a record avoids a breaking
/// edit — and an 8-argument re-spelling at every call site — each time one
/// does.
and SelectStmt =
    { Projections: Projection list
      IntoVariables: UserVariableRef list
      Distinct: bool
      CalculateFoundRows: bool
      StraightJoin: bool
      From: FromItem option
      Joins: Join list
      Where: Expr option
      /// `GROUP BY` key expressions — a positional integer (`GROUP BY 2`) or
      /// a bare column that names a `SELECT ... AS alias` resolve against
      /// `Projections` the same way `OrderBy` already does, both at
      /// execution time (`Executor.resolveGroupOrOrderExpr`) rather than
      /// here, since resolving an alias needs the sibling projection list in
      /// scope.
      GroupBy: Expr list
      /// `GROUP BY ... WITH ROLLUP` — adds one super-aggregate row per
      /// dropped `GroupBy` suffix (plus the grand total), each with the
      /// dropped keys reported as NULL and `GROUPING(key)` returning 1.
      Rollup: bool
      /// `WINDOW w AS (...), w2 AS (...)` definitions an `OVER w` in this
      /// same SELECT resolves against (see `OverClause`).
      Windows: (string * WindowSpec) list
      /// `WITH [RECURSIVE] name AS (...)` bindings in scope for this
      /// statement — visible to its own FROM, its joins, and every
      /// subquery nested inside it (see `Executor.cteScope`).
      Ctes: CommonTableExpr list
      /// `HAVING` filters *grouped* rows (after `GroupBy` collapses the
      /// `WHERE`-filtered set, or the whole result as one group when
      /// `GroupBy` is empty) — unlike `Where`, its expression may contain
      /// aggregate calls that aren't in `Projections` at all (`HAVING
      /// COUNT(*) > 1`).
      Having: Expr option
      OrderBy: OrderKey list
      Limit: Expr option
      Offset: Expr option
      /// Locking clauses apply to this query block only. An empty `Tables`
      /// list targets every physical source not named by another clause.
      Locking: LockingRead list }

type ExplicitTableLockMode =
    | ReadTableLock
    | WriteTableLock

type ExplicitTableLock =
    { Name: string
      Alias: string option
      Mode: ExplicitTableLockMode }

type HandlerComparison =
    | HandlerEqual
    | HandlerLessOrEqual
    | HandlerGreaterOrEqual
    | HandlerLess
    | HandlerGreater

type HandlerPosition =
    | HandlerFirst
    | HandlerNext
    | HandlerPrevious
    | HandlerLast

type HandlerReadMode =
    | HandlerNatural of HandlerPosition
    | HandlerIndexPosition of index: string * position: HandlerPosition
    | HandlerIndexComparison of index: string * comparison: HandlerComparison * values: Expr list

type HandlerCommand =
    | HandlerOpen of table: string * alias: string option
    | HandlerRead of name: string * mode: HandlerReadMode * where: Expr option * limit: Expr option * offset: Expr option
    | HandlerClose of name: string

let indexColumns names =
    names
    |> List.map (fun name ->
        { Name = name
          PrefixLength = None
          Transform = None
          Direction = Asc })

/// Where `ADD`/`MODIFY`/`CHANGE COLUMN` places a column: `PositionDefault`
/// means no `AFTER`/`FIRST` was written (a plain `ADD` appends at the end;
/// a plain `MODIFY`/`CHANGE` leaves the column where it already was —
/// `Storage.applyAlterAction` picks the right fallback index per action
/// since the two mean different things), `PositionFirst` is `FIRST`,
/// `PositionAfter col` is `AFTER col`.
type ColumnPosition =
    | PositionDefault
    | PositionFirst
    | PositionAfter of column: string

type AlterAlgorithm =
    | AlgorithmDefault
    | AlgorithmInstant
    | AlgorithmInplace
    | AlgorithmCopy

type AlterLock =
    | LockDefault
    | LockNone
    | LockShared
    | LockExclusive

/// One `ALTER TABLE` action; a statement carries a list of these since
/// MySQL (and Laravel) commonly comma-separates several in one `ALTER
/// TABLE`.
type AlterAction =
    | AddColumn of ColumnDef * position: ColumnPosition
    | DropColumn of column: string
    | ModifyColumn of ColumnDef * position: ColumnPosition
    | ChangeColumn of oldName: string * ColumnDef * position: ColumnPosition
    | RenameTo of newName: string
    | RenameColumnTo of oldName: string * newName: string
    | AddIndex of IndexDef
    | DropIndexAction of name: string
    | RenameIndex of oldName: string * newName: string
    | SetIndexVisibility of name: string * visible: bool
    | AddForeignKey of ForeignKeyDef
    | DropForeignKey of name: string
    | AddCheck of CheckConstraintDef
    | DropCheck of name: string
    | SetCheckEnforced of name: string * enforced: bool
    | AddPrimaryKey of columns: IndexColumn list
    | DropPrimaryKey
    | SetDefault of column: string * value: ColumnDefault option
    | SetEngine of name: string
    | SetTableComment of comment: string
    | ConvertCharset of charset: string * collation: string option
    | SetAlterAlgorithm of AlterAlgorithm
    | SetAlterLock of AlterLock
    | SetRowFormat of name: string
    /// `ALTER TABLE t AUTO_INCREMENT = n` — moves the counter forward
    /// (never below what existing rows already require, like InnoDB).
    | SetAutoIncrement of value: int64
    | AddHashPartitions of count: uint32
    | CoalesceHashPartitions of count: uint32
    | DropPartitions of names: string list
    | TruncatePartitions of names: string list option

type HashPartitioning =
    { Expression: Expr
      Count: uint32
      Linear: bool }

type TriggerTiming =
    | Before
    | After

type TriggerEvent =
    | TriggerInsert
    | TriggerUpdate
    | TriggerDelete

type TriggerOrder =
    | Follows of trigger: string
    | Precedes of trigger: string

type ViewSecurity =
    | ViewDefiner
    | ViewInvoker

type ViewAlgorithm =
    | ViewAlgorithmUndefined
    | ViewAlgorithmMerge
    | ViewAlgorithmTemptable

type ViewDefiner =
    | CurrentViewDefiner
    | ExplicitViewDefiner of user: string * host: string

type ViewDdlAction =
    | CreateViewDdl of orReplace: bool
    | AlterViewDdl

type ViewSpec =
    { Action: ViewDdlAction
      Algorithm: ViewAlgorithm option
      Definer: ViewDefiner option
      Security: ViewSecurity option
      Name: string
      Columns: string list
      Definition: string }

type AccountTlsRequirement =
    | RequireNone
    | RequireSsl
    | RequireX509

type AccountResourceLimits =
    { MaxQueriesPerHour: uint32 option
      MaxUpdatesPerHour: uint32 option
      MaxConnectionsPerHour: uint32 option
      MaxUserConnections: uint32 option }

type PasswordExpiration =
    | ExpirePassword
    | ExpirePasswordByDefault
    | NeverExpirePassword
    | ExpirePasswordAfterDays of uint16

type AccountAttribute =
    | AccountComment of string
    | AccountAttributeJson of string

type AccountOptions =
    { TlsRequirement: AccountTlsRequirement option
      ResourceLimits: AccountResourceLimits
      PasswordExpiration: PasswordExpiration option
      Locked: bool option
      Attribute: AccountAttribute option }

module AccountOptions =
    let empty =
        { TlsRequirement = None
          ResourceLimits =
            { MaxQueriesPerHour = None
              MaxUpdatesPerHour = None
              MaxConnectionsPerHour = None
              MaxUserConnections = None }
          PasswordExpiration = None
          Locked = None
          Attribute = None }

type ForeignServerOptions =
    { Host: string option
      Database: string option
      User: string option
      Password: string option
      Port: uint64 option
      Socket: string option
      Owner: string option }

[<RequireQualifiedAccess>]
module ForeignServerOptions =
    let empty =
        { Host = None
          Database = None
          User = None
          Password = None
          Port = None
          Socket = None
          Owner = None }

type ExplainFormat =
    | ExplainTraditional
    | ExplainJson
    | ExplainTree
    | ExplainAnalyze

/// Parse-time evidence for diagnostics that semantic normalization erases.
/// Replay deliberately reconstructs an empty list: warnings belong to the
/// client statement, not to durable schema recovery.
type SyntaxDeprecation =
    | Utf8CharsetAlias

type CreateTableSpec =
    { Name: string
      Columns: ColumnDef list
      Indexes: IndexDef list
      ForeignKeys: ForeignKeyDef list
      Checks: CheckConstraintDef list
      IfNotExists: bool
      /// The table's own declared `[DEFAULT] CHARSET`/`COLLATE` options;
      /// `None` means the server default.
      Charset: string option
      Collation: string option
      /// The table-option seed restored before any row is inserted.
      AutoIncrementSeed: int64 option
      Comment: string option
      Partitioning: HashPartitioning option
      Deprecations: SyntaxDeprecation list }

type RoleSelection =
    | NoRoles
    | DefaultRoles
    | AllRoles
    | AllRolesExcept of (string * string) list
    | NamedRoles of (string * string) list

type PrivilegeSpec =
    { Name: string
      Columns: string list }

[<RequireQualifiedAccess>]
module PrivilegeSpec =
    let named name = { Name = name; Columns = [] }

type LoadDataField =
    | LoadColumn of name: string
    | LoadUserVariable of variable: UserVariableRef

type LoadDataCommand =
    { Table: string
      Fields: LoadDataField list
      Rows: Value list list
      Assignments: (string * Expr) list
      Replace: bool
      Ignore: bool }

type Statement =
    | CreateDatabase of name: string * ifNotExists: bool * deprecations: SyntaxDeprecation list
    | DropDatabase of name: string * ifExists: bool
    /// `ALTER DATABASE [name] [CHARACTER SET x] [COLLATE y]`; an omitted
    /// name targets the current database.
    | AlterDatabase of name: string option * deprecations: SyntaxDeprecation list
    | CreateTable of CreateTableSpec
    | CreateTableLike of name: string * source: string * ifNotExists: bool
    | CreateTableAs of name: string * query: Statement * ifNotExists: bool
    | DropTable of names: string list * ifExists: bool
    | AlterTable of table: string * actions: AlterAction list
    | RenameTable of pairs: (string * string) list
    | CreateIndex of name: string * table: string * columns: IndexColumn list * unique: bool * kind: IndexKind * visible: bool
    /// `DROP INDEX [IF EXISTS] name ON table` — `IF EXISTS` is accepted for
    /// MySQL parity. The executor doesn't need the flag: a missing *index*
    /// already drops to a silent no-op (the one thing `IF EXISTS` suppresses
    /// in MySQL), and a missing *table* still errors under `IF EXISTS` in
    /// MySQL too, which the executor's own `NoSuchTable` path already does.
    /// Kept on the AST (and in the WAL encoding) so `EXPLAIN`/replay see the
    /// statement exactly as written.
    | DropIndexStmt of name: string * table: string * ifExists: bool
    | Insert of
        table: string *
        columns: string list *
        rows: Expr list list *
        onDuplicateUpdate: (string * Expr) list *
        ignoreDuplicates: bool
    /// `INSERT INTO t (cols) SELECT ... [ON DUPLICATE KEY UPDATE ...]` keeps
    /// the SELECT source separate from literal `VALUES` rows so its source
    /// columns remain available while duplicate assignments are evaluated.
    | InsertSelect of
        table: string *
        columns: string list *
        select: SelectStmt *
        onDuplicateUpdate: (string * Expr) list *
        ignoreDuplicates: bool
    | Replace of table: string * columns: string list * rows: Expr list list
    | ReplaceSelect of table: string * columns: string list * select: SelectStmt
    | ReplaceSet of table: string * assignments: (string * Expr) list
    /// Parsed separately from ordinary statements because the row bytes
    /// arrive in packets after the command has been accepted.
    | LoadData of LoadDataCommand
    | Select of SelectStmt
    | Do of expressions: Expr list
    /// A set operation over two or more `SELECT` branches — `UNION`,
    /// `INTERSECT` or `EXCEPT`, each `[ALL|DISTINCT]`, in any mix. `first`
    /// plus each `rest` member's own `SetOp` records which operator joined
    /// *that* branch to what precedes it (see `SetOp` for the precedence
    /// rule, which is not a plain left fold); the trailing `ORDER BY`/`LIMIT`
    /// apply to the whole combined result, so they live here rather than on
    /// any one branch's own (unused) `SelectStmt.OrderBy`/`Limit`.
    | Union of first: SelectStmt * rest: (SetOp * SelectStmt) list * orderBy: OrderKey list * limit: Expr option * offset: Expr option
    | Update of UpdateStmt
    | Delete of DeleteStmt
    | Truncate of table: string
    /// `CREATE USER [IF NOT EXISTS] 'name'@'host' [IDENTIFIED BY 'pw'], ...`
    /// with account requirements shared by every account in the statement.
    | CreateUser of
        users: (string * string * string option) list *
        ifNotExists: bool *
        options: AccountOptions
    /// `DROP USER [IF EXISTS] 'name'@'host', ...`
    | DropUser of users: (string * string) list * ifExists: bool
    | RenameUser of users: ((string * string) * (string * string)) list
    | AlterUser of name: string * host: string * password: string option * ifExists: bool * options: AccountOptions
    | CreateServer of name: string * wrapper: string * options: ForeignServerOptions
    | AlterServer of name: string * options: ForeignServerOptions
    | DropServer of name: string * ifExists: bool
    | CreateRole of users: (string * string) list * ifNotExists: bool
    | DropRole of users: (string * string) list * ifExists: bool
    | GrantRoles of roles: (string * string) list * users: (string * string) list * withAdminOption: bool
    | RevokeRoles of roles: (string * string) list * users: (string * string) list
    | SetRole of RoleSelection
    | SetDefaultRole of roles: RoleSelection * users: (string * string) list
    /// `GRANT privs ON level TO users [WITH GRANT OPTION]`; each privilege
    /// carries its optional column list. `"ALL"` means ALL PRIVILEGES and
    /// `"USAGE"` grants nothing. `level` is `(db, table)`: `(None, None)` = `*.*`,
    /// `(Some db, None)` = `db.*`, `(Some db, Some t)` = `db.t`, and
    /// `(None, Some t)` = bare `t`, resolved against the session database at
    /// execution time.
    | Grant of privs: PrivilegeSpec list * level: (string option * string option) * users: (string * string) list * withGrantOption: bool
    /// `REVOKE privs ON level FROM users` — same shapes as `Grant`;
    /// `"GRANT OPTION"` may appear as a privilege name.
    | Revoke of privs: PrivilegeSpec list * level: (string option * string option) * users: (string * string) list
    /// `CREATE TRIGGER name timing event ON table FOR EACH ROW body` —
    /// `body` is the single statement after `FOR EACH ROW`, carried as the
    /// raw SQL text exactly as written: the executor validates it by
    /// parsing at CREATE time and re-parses at fire time, so there's one
    /// source of truth rather than a parsed-Statement-plus-text double
    /// carry (statement parsing is cheap). `order` places the trigger within
    /// its timing/event slot. BEGIN...END bodies retain the same raw form and
    /// are split into their supported statements at validation and fire time.
    | CreateTrigger of
        name: string *
        timing: TriggerTiming *
        event: TriggerEvent *
        table: string *
        order: TriggerOrder option *
        body: string
    /// `SET NEW.column = expression` is valid only as a BEFORE INSERT or
    /// BEFORE UPDATE trigger body.
    | SetTriggerNew of column: string * value: Expr
    /// `DROP TRIGGER [IF EXISTS] name` — resolved against the session
    /// database's triggers (error 1360 when missing, unless `ifExists`).
    | DropTrigger of name: string * ifExists: bool
    /// A CREATE/ALTER VIEW declaration. The definition remains SQL text so
    /// the row-backed `mysql.views` catalog can persist it through ordinary
    /// row events; it is parsed when the view is read.
    | CreateView of ViewSpec
    /// `DROP VIEW [IF EXISTS] view [, ...]`.
    | DropView of names: string list * ifExists: bool
    | ChecksumTables of tables: string list * quick: bool
    /// `EXPLAIN [FORMAT=TRADITIONAL|JSON] stmt` describes an access plan;
    /// `EXPLAIN ANALYZE SELECT` also evaluates the query.
    | Explain of format: ExplainFormat * statement: Statement

/// A `SET` target: `col` or `table.col` — the table qualifier only matters
/// once there's more than one table in scope (a multi-table `UPDATE ...
/// JOIN`); a single-table `UPDATE` still parses (and discards, same as
/// always) a `table.` qualifier even so (Laravel's `touch()` writes
/// `updated_at = ...` qualified even in a single-table `UPDATE`).
and Assignment = { Table: string option; Column: string; Value: Expr }

/// `UPDATE t1 [[AS] a] [JOIN ...] SET assignments [WHERE ...] [ORDER BY ...]
/// [LIMIT ...]` — `OrderBy`/`Limit` are only legal (and only ever parsed) when
/// `Joins` is empty, matching MySQL's own grammar restriction against a
/// multi-table `UPDATE`. Multi-table semantics: a physical row reached
/// through more than one join match is still updated at most once (see
/// `Executor`'s multi-table `UPDATE` handling).
and UpdateStmt =
    { Ctes: CommonTableExpr list
      Ignore: bool
      From: TableRef
      Joins: Join list
      Assignments: Assignment list
      Where: Expr option
      OrderBy: OrderKey list
      Limit: Expr option }

/// `DELETE FROM t1 [WHERE ...] [ORDER BY ...] [LIMIT n]` (single-table —
/// `Targets = [t1's alias-or-name]`), `DELETE t1[, t2] FROM t1 JOIN t2 ON
/// ... [WHERE ...]`, or `DELETE FROM t1 USING t1 JOIN t2 ON ... [WHERE ...]`
/// (multi-table forms) — `Targets` holds the alias (or bare table name,
/// unaliased) exactly as written before `FROM`/`USING`, resolved against
/// `From`/`Joins` at execution time; `OrderBy`/`Limit` are only legal
/// (single-table) the same way `UpdateStmt`'s are.
and DeleteStmt =
    { Ctes: CommonTableExpr list
      Targets: string list
      From: TableRef
      Joins: Join list
      Where: Expr option
      OrderBy: OrderKey list
      Limit: Expr option }
