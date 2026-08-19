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
    /// MySQL 9's `VECTOR(N)` — N little-endian 4-byte floats stored/wired as
    /// a binary string. A bare `VECTOR` declares MySQL's default dimension
    /// 2048; the 16383 ceiling (and the can't-be-a-key rule) is enforced at
    /// DDL time in `Storage`, where the column name is in scope to report.
    | TVector of dim: int

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
    /// `ROW_NUMBER() OVER (PARTITION BY expr, ... ORDER BY expr, ...)` —
    /// the one window-function shape Laravel's constrained eager loading
    /// compiles a relation query's `->limit()` into (e.g. `->with(['messages'
    /// => fn ($q) => $q->orderBy('created_at', 'desc')->limit(1)])`).
    /// `MATCH (cols) AGAINST ('query' [mode])` — relevance over the
    /// FULLTEXT-indexed columns; computed as a whole-table pre-pass (the
    /// IDF half needs corpus statistics), like the window functions below.
    | MatchAgainst of columns: string list * query: Expr * mode: MatchMode
    | RowNumberOver of partitionBy: Expr list * orderBy: OrderKey list
    /// `LAG(expr[, offset]) OVER (PARTITION BY expr, ... ORDER BY expr, ...)`
    /// — the value of `expr` `offset` rows back (default 1) within the same
    /// partition, ordered by the window's own `ORDER BY`; `NULL` for a row
    /// with no such predecessor. `LEAD` is the same node with the offset
    /// negated (rows *forward*), so one case covers both directions.
    /// Unlike `RowNumberOver`, this can sit
    /// anywhere inside a larger expression (`value - LAG(value) OVER (...)`
    /// is a real report query), so `Executor` finds and substitutes every
    /// occurrence rather than only a bare top-level projection.
    /// ponytail: no `ROWS BETWEEN`/frame clauses — every window here is the
    /// implicit whole-partition frame; add frame support if a migration's
    /// query needs one.
    | LagOver of expr: Expr * offset: int64 * partitionBy: Expr list * orderBy: OrderKey list
    /// `RANK() OVER (...)` (`dense = false`) / `DENSE_RANK() OVER (...)`
    /// (`dense = true`) — one case for both, same pairing `LagOver` uses for
    /// `LAG`/`LEAD`. Both assign every row in a partition the same rank as
    /// any row it ties with under the window's `ORDER BY` (all rows tie when
    /// there's no `ORDER BY`); `RANK` then skips as many following ranks as
    /// there were tied rows, `DENSE_RANK` never skips.
    | RankOver of dense: bool * partitionBy: Expr list * orderBy: OrderKey list
    /// `PERCENT_RANK() OVER (...)` — `(rank - 1) / (rows_in_partition - 1)`
    /// using the same tie-aware `RANK` numbering as `RankOver(false, ...)`;
    /// `0` for a one-row partition (MySQL never divides by zero here).
    | PercentRankOver of partitionBy: Expr list * orderBy: OrderKey list
    /// `NTILE(buckets) OVER (...)` — splits each partition into `buckets`
    /// groups as evenly as possible, earlier groups absorbing the remainder
    /// (a 10-row partition into 3 buckets is 4/3/3, not 3/3/4).
    | NTileOver of buckets: int64 * partitionBy: Expr list * orderBy: OrderKey list
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

/// VIRTUAL vs STORED on a generated column. Both are materialized at write
/// time in this engine (no recompute-on-read path), so the kind only drives
/// metadata (SHOW CREATE TABLE, information_schema `EXTRA`). MySQL's default
/// when the keyword is omitted is VIRTUAL.
and GeneratedKind =
    | Virtual
    | Stored

and ColumnDef =
    { Name: string
      Type: ColumnType
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

/// A named `[UNIQUE] KEY|INDEX (cols)` — from a `CREATE TABLE` trailing item,
/// `ALTER TABLE ADD INDEX`, `CREATE INDEX`, or a column-level `UNIQUE`
/// modifier (which synthesizes one of these named after the column).
and IndexKind =
    | BTree
    | FullTextIndex

and MatchMode =
    | NaturalLanguage
    | BooleanMode
    | QueryExpansion

and IndexDef =
    { Name: string
      Columns: string list
      Unique: bool
      /// `FULLTEXT KEY` vs an ordinary index — drives `MATCH ... AGAINST`
      /// eligibility and the `Index_type` introspection column. SPATIAL
      /// still collapses to `BTree` (no geometry types exist to index).
      Kind: IndexKind }

/// A `CONSTRAINT name FOREIGN KEY (cols) REFERENCES tbl (cols) [ON DELETE
/// ...] [ON UPDATE ...]` — enforced (insert/update-time parent check,
/// `CASCADE`/`SET NULL`/`RESTRICT` on both `ON DELETE` and `ON UPDATE`) by
/// `Storage`'s `checkFkParents`/`cascadeDeleteVisited`/`cascadeUpdateVisited`.
/// ponytail: DDL itself isn't validated as strictly as MySQL — a `SET NULL`
/// action over a `NOT NULL` FK column, or `RefColumns` that aren't actually a
/// unique key on the parent, are accepted here and only surface as a runtime
/// error (1048, or a `RESTRICT` that never finds a "still referenced" row)
/// instead of MySQL's own DDL-time rejection (1215/1822); `RefTable` also
/// carries no database qualifier, so a cross-database FK is invisible to the
/// referencing-side checks. Tighten once a migration's test suite depends on
/// catching one of these at `CREATE`/`ALTER TABLE` time rather than later.
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
/// to live — needed for schema introspection and, later, joins —
/// without another breaking edit to every `Select` call site.
and TableRef =
    { Database: string option
      Table: string
      Alias: string option }

/// A derived table's body: a plain `SELECT`, or `(SELECT ...) UNION
/// (SELECT ...) ...` — MySQL allows a `UNION` directly inside `FROM (...)
/// AS alias` (Laravel's `unionAll(...)->paginate()` compiles to exactly
/// this: `SELECT COUNT(*) FROM ((SELECT ...) UNION (SELECT ...)) AS
/// alias`), so `FromSubquery` needs to carry either shape rather than just
/// a bare `SelectStmt`.
and SelectOrUnion =
    | PlainSelect of SelectStmt
    | UnionSelect of first: SelectStmt * rest: (bool * SelectStmt) list * orderBy: OrderKey list * limit: int option * offset: int option

/// A `SELECT`'s `FROM` target: a real (or `information_schema` virtual)
/// table, or a derived table — `FROM (SELECT ...) AS alias` — whose alias is
/// mandatory (MySQL requires one) and doubles as the qualifier later
/// `t.col` references resolve against, the same way a real table's alias
/// does.
and FromItem =
    | FromTable of TableRef
    | FromSubquery of SelectOrUnion * alias: string

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

/// A `SELECT` statement's clauses as a record rather than a positional
/// tuple: every clause after `SELECT ... FROM` is optional and grows
/// independently, so a record avoids a breaking
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
    | AddForeignKey of ForeignKeyDef
    | DropForeignKey of name: string
    | AddPrimaryKey of columns: string list
    /// `ALTER TABLE t AUTO_INCREMENT = n` — moves the counter forward
    /// (never below what existing rows already require, like InnoDB).
    | SetAutoIncrement of value: int64

type Statement =
    | CreateDatabase of name: string * ifNotExists: bool
    | DropDatabase of name: string * ifExists: bool
    /// `ALTER DATABASE name [CHARACTER SET x] [COLLATE y]` — parsed and
    /// discarded, same treatment as `CREATE DATABASE`'s own charset/collate
    /// tail (see `Parser.databaseOptions`'s doc); only errors if `name`
    /// itself doesn't exist.
    | AlterDatabase of name: string
    | CreateTable of
        name: string *
        columns: ColumnDef list *
        indexes: IndexDef list *
        foreignKeys: ForeignKeyDef list *
        ifNotExists: bool *
        /// The table's own declared `[DEFAULT] CHARSET`/`COLLATE` options
        /// (`None` = server default) — kept separate from the per-column
        /// values they default to, so `SHOW CREATE TABLE` renders the
        /// table-level declaration MySQL reports rather than the baked-in
        /// column defaults.
        tableCharset: string option *
        tableCollation: string option *
        /// `AUTO_INCREMENT = n` from the table-options tail — dump files
        /// carry it on every table so restored inserts continue at the
        /// dumped counter even before any row lands.
        autoIncrementSeed: int64 option
    | DropTable of names: string list * ifExists: bool
    | AlterTable of table: string * actions: AlterAction list
    | RenameTable of pairs: (string * string) list
    | CreateIndex of name: string * table: string * columns: string list * unique: bool * kind: IndexKind
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
    /// `INSERT INTO t (cols) SELECT ... [ON DUPLICATE KEY UPDATE ...]` — a
    /// separate case rather than folding a `SelectStmt` into `Insert`'s
    /// `rows` shape, since the row sources don't otherwise share anything.
    /// `onDuplicateUpdate` mirrors `Insert`'s: `VALUES(col)` resolves
    /// against the select-derived candidate row, bare column refs against
    /// the stored row (ponytail: MySQL also allows `alias.col` refs into
    /// the SELECT's own columns here — deferred, see
    /// torture/findings/2026-08-19-insert-select-odku-gap.md; add if a
    /// migration needs them).
    | InsertSelect of
        table: string *
        columns: string list *
        select: SelectStmt *
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
    | Update of UpdateStmt
    | Delete of DeleteStmt
    | Truncate of table: string
    /// `CREATE USER [IF NOT EXISTS] 'name'@'host' [IDENTIFIED BY 'pw'], ...`
    /// — each account as `(name, host, password)`; host defaults to `'%'`
    /// when omitted. Executed against `mysql.user` (see `Auth.createUser`);
    /// no `REQUIRE`/`WITH`/`ACCOUNT LOCK`/role tail — ponytail, add clauses
    /// when a client actually sends them.
    | CreateUser of users: (string * string * string option) list * ifNotExists: bool
    /// `DROP USER [IF EXISTS] 'name'@'host', ...`
    | DropUser of users: (string * string) list * ifExists: bool
    /// `ALTER USER [IF EXISTS] 'name'@'host' IDENTIFIED BY 'pw'` — the one
    /// supported alteration (password change).
    | AlterUser of name: string * host: string * password: string * ifExists: bool
    /// `GRANT privs ON level TO users [WITH GRANT OPTION]` — `privs` are the
    /// SQL privilege names (`"ALL"` for ALL PRIVILEGES, `"USAGE"` grants
    /// nothing); `level` is `(db, table)`: `(None, None)` = `*.*`,
    /// `(Some db, None)` = `db.*`, `(Some db, Some t)` = `db.t`, and
    /// `(None, Some t)` = bare `t`, resolved against the session database at
    /// execution time.
    | Grant of privs: string list * level: (string option * string option) * users: (string * string) list * withGrantOption: bool
    /// `REVOKE privs ON level FROM users` — same shapes as `Grant`;
    /// `"GRANT OPTION"` may appear in `privs`.
    | Revoke of privs: string list * level: (string option * string option) * users: (string * string) list
    /// `EXPLAIN [FORMAT=TRADITIONAL] stmt` — MySQL's classic tabular
    /// `EXPLAIN` accepts `SELECT`/`UPDATE`/`DELETE`/`INSERT`, all handled by
    /// describing what `Executor` would actually do rather than running it.
    | Explain of Statement

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
    { From: TableRef
      Joins: Join list
      Assignments: Assignment list
      Where: Expr option
      OrderBy: OrderKey list
      Limit: int option }

/// `DELETE FROM t1 [WHERE ...] [ORDER BY ...] [LIMIT n]` (single-table —
/// `Targets = [t1's alias-or-name]`), `DELETE t1[, t2] FROM t1 JOIN t2 ON
/// ... [WHERE ...]`, or `DELETE FROM t1 USING t1 JOIN t2 ON ... [WHERE ...]`
/// (multi-table forms) — `Targets` holds the alias (or bare table name,
/// unaliased) exactly as written before `FROM`/`USING`, resolved against
/// `From`/`Joins` at execution time; `OrderBy`/`Limit` are only legal
/// (single-table) the same way `UpdateStmt`'s are.
and DeleteStmt =
    { Targets: string list
      From: TableRef
      Joins: Join list
      Where: Expr option
      OrderBy: OrderKey list
      Limit: int option }
