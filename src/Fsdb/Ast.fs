/// The SQL abstract syntax tree the parser produces and the executor
/// consumes. Types only, no behavior — every case here is data.
module Fsdb.Ast

open Fsdb.Value

/// A comparison/logical/arithmetic binary operator, shared by every
/// `BinOp` node rather than splitting expressions into separate DU cases
/// per operator.
type Op =
    | And
    | Or
    | Eq
    | Neq
    | Lt
    | Lte
    | Gt
    | Gte
    | Add
    | Sub
    | Mul
    | Div
    /// `<=>` — the null-safe equals operator: like `Eq` except `NULL <=> NULL`
    /// is true (rather than `NULL`) and `NULL <=> anything-else` is false
    /// (rather than `NULL`) — it never returns SQL's three-valued unknown.
    | NullSafeEq

type ColumnType =
    | TTinyInt of unsigned: bool
    | TSmallInt of unsigned: bool
    | TMediumInt of unsigned: bool
    | TInt of unsigned: bool
    | TBigInt of unsigned: bool
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
    /// Accepted like a string column — ponytail: no comma-set validation
    /// against `values`, add it if a migration actually needs SET semantics
    /// enforced rather than just accepted.
    | TSet of values: string list
    | TDecimal of precision: int * scale: int
    | TDouble
    | TFloat
    | TDate
    | TDateTime
    | TTimestamp
    | TTime
    | TYear
    | TJson

// `Expr` and `SelectStmt` are mutually recursive: `Exists`/a scalar subquery
// carries a nested `SelectStmt`, whose projections/`WHERE` are themselves
// built from `Expr`. Every type in between (`Projection`, `TableRef`, ...)
// rides along in the same `and` chain since `SelectStmt` needs them.
type Expr =
    | Lit of Value
    | Col of name: string
    | QualifiedCol of table: string * column: string
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
    /// pattern match — plain `LIKE` always sets it false.
    | Like of Expr * pattern: Expr * caseSensitive: bool
    | Regexp of Expr * pattern: Expr
    | In of Expr * candidates: Expr list
    /// `expr IN (SELECT ...)` — the candidate set is a subquery's first
    /// column rather than a literal list; `Not(InSubquery(...))` is `NOT IN
    /// (SELECT ...)`, the same desugaring `In`'s own `NOT IN` already uses.
    | InSubquery of Expr * SelectStmt
    | Between of Expr * lo: Expr * hi: Expr
    | FuncCall of name: string * args: Expr list
    /// Marks `DISTINCT expr` as an aggregate call's argument (`COUNT(DISTINCT
    /// x)`, `SUM(DISTINCT x)`, ...) — only meaningful as the (unwrapped) sole
    /// argument of a `FuncCall` the executor recognizes as an aggregate;
    /// anywhere else it's a parse shape that can't occur, since the parser
    /// only ever produces it inside a function call's argument list.
    | Distinct of Expr
    /// Minimal `CAST(expr AS type)` — reuses `ColumnType` rather than a
    /// separate cast-target vocabulary, coerced the same way a column of
    /// that type would be (see `Storage.coerceValue`).
    | Cast of Expr * ColumnType
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
    /// `CASE WHEN cond THEN result ... [ELSE result] END` (the "searched"
    /// form, `subject = None`, each `whens` key is a boolean condition) or
    /// `CASE subject WHEN value THEN result ... [ELSE result] END` (the
    /// "simple" form, `subject = Some ...`, each `whens` key compares equal
    /// to `subject` instead) — one case instead of two so the executor's
    /// every other `Expr`-walking function (`containsAggregate`,
    /// `rewriteAggregates`, ...) only needs one branch to recurse through.
    | Case of subject: Expr option * whens: (Expr * Expr) list * elseBranch: Expr option

and Direction =
    | Asc
    | Desc

/// One `ORDER BY` key: the expression to sort by and its direction.
and OrderKey = Expr * Direction

/// A column's `DEFAULT`: either a fixed value, or `CURRENT_TIMESTAMP`, which
/// evaluates fresh at insert time rather than once at parse time — kept as
/// its own case instead of a `VString "CURRENT_TIMESTAMP"` sentinel value so
/// storage evaluates it explicitly rather than trying (and failing) to
/// coerce the marker text itself into the column's type.
and ColumnDefault =
    | DConst of Value
    | DCurrentTimestamp

and ColumnDef =
    { Name: string
      Type: ColumnType
      Nullable: bool
      Default: ColumnDefault option
      AutoIncrement: bool
      PrimaryKey: bool
      Unique: bool
      /// `[GENERATED ALWAYS] AS (expr) [VIRTUAL | STORED]` — `None` for a
      /// plain column. Both VIRTUAL and STORED are persisted the same way
      /// here (this engine has no separate "recompute on every read" path),
      /// so only the expression itself is kept.
      Generated: Expr option }

/// A named `[UNIQUE] KEY|INDEX (cols)` — from a `CREATE TABLE` trailing item,
/// `ALTER TABLE ADD INDEX`, `CREATE INDEX`, or a column-level `UNIQUE`
/// modifier (which synthesizes one of these named after the column).
and IndexDef = { Name: string; Columns: string list; Unique: bool }

/// A `CONSTRAINT name FOREIGN KEY (cols) REFERENCES tbl (cols) [ON DELETE
/// ...] [ON UPDATE ...]` — metadata only, stored so `information_schema` can
/// see it; ponytail: no referential-action enforcement yet (no cascading
/// delete/update, no insert-time reference check), add it once a migration's
/// test suite actually depends on the enforcement rather than just the
/// metadata existing.
and ForeignKeyDef =
    { Name: string
      Columns: string list
      RefTable: string
      RefColumns: string list
      OnDelete: string option
      OnUpdate: string option }

/// A `SELECT` projection: the expression and its optional `AS alias`.
and Projection = Expr * string option

/// `FROM [db.]table [[AS] alias]`. A record (not a bare string) so a
/// qualified name (`information_schema.tables`) and an alias have somewhere
/// to live — needed for M4's schema introspection and, later, joins —
/// without another breaking edit to every `Select` call site.
and TableRef =
    { Database: string option
      Table: string
      Alias: string option }

/// A `SELECT`'s `FROM` target: a real (or `information_schema` virtual)
/// table, or a derived table — `FROM (SELECT ...) AS alias` — whose alias is
/// mandatory (MySQL requires one) and doubles as the qualifier later
/// `t.col` references resolve against, the same way a real table's alias
/// does.
and FromItem =
    | FromTable of TableRef
    | FromSubquery of SelectStmt * alias: string

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

/// One `[INNER | LEFT [OUTER]] JOIN table ON expr` clause, applied against
/// whatever's already in scope to its left (the `FROM` table, or the result
/// of an earlier `Join` in the same list — this engine only ever nests
/// joins left-to-right, matching how they're written).
and Join =
    { Kind: JoinKind
      Table: TableRef
      On: Expr }

/// A `SELECT` statement's clauses as a record rather than a positional
/// tuple: every clause after `SELECT ... FROM` is optional and grows
/// independently (M5 adds `GroupBy`/`Having`), so a record avoids a breaking
/// edit — and an 8-argument re-spelling at every call site — each time one
/// does.
and SelectStmt =
    { Projections: Projection list
      Distinct: bool
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
      /// `HAVING` filters *grouped* rows (after `GroupBy` collapses the
      /// `WHERE`-filtered set, or the whole result as one group when
      /// `GroupBy` is empty) — unlike `Where`, its expression may contain
      /// aggregate calls that aren't in `Projections` at all (`HAVING
      /// COUNT(*) > 1`).
      Having: Expr option
      OrderBy: OrderKey list
      Limit: int option
      Offset: int option
      /// `FOR UPDATE` / `LOCK IN SHARE MODE` — accepted and ignored: this
      /// engine has no row-level locking to apply it to (no concurrent
      /// writers within one in-memory `Store`), so the clause only needs to
      /// parse rather than change execution.
      Locking: bool }

/// One `ALTER TABLE` action; a statement carries a list of these since
/// MySQL (and Laravel) commonly comma-separates several in one `ALTER
/// TABLE`. `AFTER col` / `FIRST` are accepted by the parser but not carried
/// here — ponytail: column ordering isn't tracked yet, add a position field
/// if a migration's assertion ever depends on physical column order rather
/// than just the column existing.
type AlterAction =
    | AddColumn of ColumnDef
    | DropColumn of column: string
    | ModifyColumn of ColumnDef
    | ChangeColumn of oldName: string * ColumnDef
    | RenameTo of newName: string
    | RenameColumnTo of oldName: string * newName: string
    | AddIndex of IndexDef
    | DropIndexAction of name: string
    | AddForeignKey of ForeignKeyDef
    | DropForeignKey of name: string
    | AddPrimaryKey of columns: string list

type Statement =
    | CreateDatabase of name: string * ifNotExists: bool
    | DropDatabase of name: string * ifExists: bool
    | CreateTable of
        name: string *
        columns: ColumnDef list *
        indexes: IndexDef list *
        foreignKeys: ForeignKeyDef list *
        ifNotExists: bool
    | DropTable of names: string list * ifExists: bool
    | AlterTable of table: string * actions: AlterAction list
    | RenameTable of pairs: (string * string) list
    | CreateIndex of name: string * table: string * columns: string list * unique: bool
    | DropIndexStmt of name: string * table: string
    | Insert of
        table: string *
        columns: string list *
        rows: Expr list list *
        onDuplicateUpdate: (string * Expr) list *
        ignoreDuplicates: bool
    | Select of SelectStmt
    /// `select1 UNION [ALL|DISTINCT] select2 [UNION [ALL|DISTINCT] select3 ...]
    /// [ORDER BY ...] [LIMIT ...]` — `First`/`Rest` are the branches with
    /// each `Rest` member's own `bool` recording whether *that* `UNION` was
    /// `ALL` (duplicates kept) or plain/`DISTINCT` (deduped against
    /// everything combined so far); the trailing `ORDER BY`/`LIMIT` apply to
    /// the whole combined result, so they live here rather than on any one
    /// branch's own (unused) `SelectStmt.OrderBy`/`Limit`.
    | Union of first: SelectStmt * rest: (bool * SelectStmt) list * orderBy: OrderKey list * limit: int option * offset: int option
    | Update of table: string * assignments: (string * Expr) list * where: Expr option
    | Delete of table: string * where: Expr option
    | Truncate of table: string
