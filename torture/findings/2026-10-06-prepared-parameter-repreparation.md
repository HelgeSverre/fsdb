# Prepared parameter type repreparation

Status: explicit-marker histories resolved; direct user-variable typing open

Oracle: MySQL 8.4.11, using the digest pinned in `torture/compose.yaml`.
The `prepared-projection-names` probe in
`artifacts/runs/20261006T114025305-62971/contracts/manifest.json` contains the
original wire comparison. MySqlConnector prepares the statement and binds three
Int32 values of -2:

```sql
SELECT ?, ABS(?), ? + 1
```

The original comparison returns three BIGINT columns from MySQL with values
-2, 2, and -1. fsdb returned BIGINT, DOUBLE, and BIGINT; its middle value was a
floating-point 2.
The independent statement `SELECT ABS(?)` returns DOUBLE on both engines.
The bare projection parameter changes MySQL's type-repreparation decision for
the entire statement, including the argument to ABS.

MySQL's [parameter typing rules](https://dev.mysql.com/doc/refman/8.4/en/prepare.html)
distinguish initial derivation, binding conversion, and statement-wide
repreparation. Supplied NULL and string values have separate rules, as do casts,
numeric families, and temporal families. Subsequent executions also depend on
the statement's retained types.

`PreparedMetadata.bindParameters` retains SQL-derived types on each binary or
SQL prepared handle, separately from the binary protocol's remembered wire
types. It converts supplied strings before checking compatibility and rederives
the whole statement when required. NULL metadata, decimal scale, inherited cast
types, LIMIT validation, column-assignment diagnostics, and numeric/temporal
widening have dedicated regressions. Declared metadata wrappers are confined to
projections so bound predicates retain literal index probes.

The `prepared-parameter-type-history` compatibility case compares repeated
binary-handle and SQL EXECUTE histories, including a failed execution followed
by typed NULLs. `PreparedStatementTests` also checks independent handles and
registered signatures overriding polymorphic builtins.

## Repeated-execution oracle

`just prepared-type-oracle` checks both binary prepared execution and SQL
`PREPARE`/`EXECUTE` against MySQL 8.4.11. Set `FSDB_ORACLE_CONNECTION` to the
connection string of an instance started from `torture/compose.yaml`. The probe
uses fresh, unpooled connections for independent histories: MySqlConnector can
reuse a prepared handle for the same SQL within one connection, even after its
command has been disposed. Reusing that connection contaminates a supposedly
fresh history with retained server types.

For `SELECT ?, ABS(?), ? + 1`, binding the same value to every marker produces
these result families in order:

| Supplied value | All three result types |
|---|---|
| signed integer -2 | BIGINT |
| string '-3' | DECIMAL |
| NULL | DECIMAL |
| decimal 1.25 | DECIMAL |
| signed integer -4 | DECIMAL |
| double 1.5 | DOUBLE |
| string 'oops' | DOUBLE; values 0, 0, 1 |
| signed integer -5 | DOUBLE |

The independent `SELECT ABS(?)` remains DOUBLE for integer, decimal, NULL, and
invalid-string executions. Binary execution and SQL EXECUTE agree on these
histories.

A whole-statement reprepare derives each non-NULL marker from its supplied
value, rather than assigning one common family to every marker. For
`SELECT ?, ABS(?)`, integer/integer produces BIGINT/BIGINT, followed by
decimal/integer producing DECIMAL/BIGINT. Later integer/integer execution
retains DECIMAL/BIGINT.

NULL requires two separate cases:

- With no reprepare, NULL keeps the retained type. A mixed statement that has
  widened to DECIMAL still reports DECIMAL when every supplied value is NULL.
- When another marker forces reprepare, a NULL marker can return to its original
  expression context. After BIGINT/BIGINT, NULL/decimal produces
  VARCHAR/DECIMAL. A subsequent integer/integer execution produces
  BIGINT/BIGINT. Conversely, starting with NULL/integer produces
  VARCHAR/DOUBLE, then integer/NULL produces BIGINT/DOUBLE.

String conversion precedes the compatibility decision. A valid negative
integer string supplied to a signed integer marker becomes DECIMAL in this
oracle version; an invalid string such as 'abc' retains the integer context and
converts to zero. A decimal string can widen only its marker while the other
marker stays BIGINT. The source-level behavior is in
[`Item_param::convert_value`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/item.cc)
and [`Prepared_statement::check_parameter_types`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_prepare.cc).
The manual's string exception alone is insufficient to implement these cases.

## Direct user-variable references

This case has no explicit parameter marker:

```sql
SET @v=-2;
PREPARE p FROM 'SELECT @v, ABS(@v)';
EXECUTE p;
SET @v=1.25;
EXECUTE p;
SET @v=1.5e0;
EXECUTE p;
SET @v='hello';
EXECUTE p;
SET @v=NULL;
EXECUTE p;
```

MySQL 8.4.11 returns BIGINT/BIGINT throughout, with rows (-2, 2), (1, 1),
(1, 1), (0, 0), and (NULL, NULL). fsdb now retains these types on the
parsed variable references while reading their current values. Decimal-to-integer
reads round away from zero at midpoints; double and string reads truncate.
Assignments earlier in the same statement remain visible without changing the
stored variable's actual type.

The differential contract covers signed integer, unsigned integer, decimal,
double, text, and initially NULL/binary types. Decimal direct reads preserve
their value scale; ABS reports the declared decimal scale. An explicit parameter
that triggers statement reprepare also refreshes the direct references' types.
Compatible parameter changes and NULL values retain the captured types.

These histories use SQL PREPARE/EXECUTE because MySqlConnector 2.6.2 rejects
direct user variables during binary prepared execution with "Parameter '@v'
must be defined", even with AllowUserVariables enabled. A raw-wire integration
regression separately checks COM_STMT_PREPARE metadata and COM_STMT_EXECUTE
integer conversion after a decimal assignment.

Remaining boundaries:

- Decimal result-scale propagation and division rounding remain incomplete;
  the checked expression-family oracle below records their required behavior.
- MySQL's direct decimal variable read after assigning 'abc' produces decimal
  wire text that MySqlConnector cannot parse; fsdb returns error 1292. This
  malformed-result edge is not included in the successful-history contract.

## Prepared user-variable SET

MySQL 8.4.11 retains BIGINT for `SET @x=@v` prepared while @v is -2:
assigning 1.75 to @v and executing stores BIGINT 2 in @x. An invalid numeric
string becomes BIGINT 0, and NULL stays NULL. fsdb now retains a typed
assignment AST and uses the same parameter binding and reprepare rules as
other parsed statements.

For `SET @x=?,@y=@v`, a NULL parameter retains the initial integer type of
@v. Changing the parameter to an integer triggers reprepare and captures a
current decimal @v; changing it to decimal captures a current double @v.
Both SQL and binary prepared handles follow these rules, without result columns.

Outer assignments are delayed: `SET @v=1.75,@x=@v` reads the old @v for @x.
Nested assignment expressions take effect during evaluation. With @side=9,
@x=8 and @y=7, the following returns error 1242/21000 and leaves @side=1,
@x=8 and @y=7, through both ordinary and prepared execution:

```sql
SET @x=(@side:=1),@y=(SELECT 1 UNION ALL SELECT 2);
```

The shared user-variable SET evaluator preserves those nested effects while
publishing outer assignments only after all expressions succeed. Ordinary
user-variable SET also now uses the SQL parser's string unescaping: an escaped
quote is stored as a quote rather than retaining its escape backslash, matching
the oracle.

## Prepared system-variable and mixed SET oracle

`just prepared-type-oracle` includes independent histories for
`SET @x=@v, SESSION sql_select_limit=100`,
`SET SESSION sql_select_limit=@v`, and
`SET SESSION sql_select_limit=@v, @x=@v`, prepared with integer @v=2.
Changing @v to decimal 1.75, invalid numeric text, NULL, and integer 3 retains
the captured integer interpretation throughout. The resulting user-variable
values are 2, 0, NULL, and 3, all with BIGINT metadata.

For the system-variable target, the retained integer NULL becomes 0 rather
than remaining NULL. A fresh ordinary assignment of NULL or decimal 1.75 to
`sql_select_limit` instead returns 1232/42000. The inspection query explicitly
uses `LIMIT 1` so a zero session limit does not hide its result.

The `prepared-mixed-variable-assignments` differential contract verifies the
same histories using the supported `max_sp_recursion_depth` variable.
`sql_select_limit` remains absent from fsdb's system-variable catalog.

User/system assignment lists retain expressions in `SetVariables`; shared AST
traversal applies parameter binding, captured user-variable types, and schema
repreparation. Assignment targets and DEFAULT remain outside expression
evaluation. `systemSetAction` supplies the ordinary system-variable validation
and default resolution. A prepared DEFAULT observes the global value at
execution, including changes made after preparation.

Parameter markers retain their separate argument-validation rules: decimal,
NULL, and text bindings for `SET max_sp_recursion_depth=?` return 1232/42000,
unlike a retained integer user-variable read. Binary prepared regressions also
cover mixed assignment metadata and type refresh after parameter repreparation.

The contract manifest at
`artifacts/runs/20261006T134501838-3301/contracts/manifest.json` records parity
for these histories, literal NULL, and parameter error codes and SQLSTATEs.

## Prepared charset clauses

MySQL 8.4.11 retains the captured integer type in both
`SET NAMES utf8mb4 COLLATE utf8mb4_bin,@x=@v` and
`SET @x=@v,NAMES utf8mb4 COLLATE utf8mb4_bin`. Preparing with @v=2 and then
assigning decimal 1.75 stores BIGINT 2 in @x while applying the charset clause.
The differential history also covers invalid numeric text, NULL, and a later
integer assignment, with charset and collation inspected after execution.

Charset clauses are explicit `SetClause` cases alongside variable assignments.
Their validation is shared with ordinary SET NAMES, while expression traversal
retains parameter binding, user-variable typing, and schema dependencies.
Binary coverage combines NAMES with a parameter, DEFAULT, and a captured direct
reference. A compatible NULL parameter preserves the old type; an integer
parameter that forces repreparation refreshes it to the current decimal type,
as independently verified through SQL PREPARE on MySQL.

`artifacts/runs/20261006T135131109-4349/contracts/manifest.json` records the
expanded charset-clause differential history with no differences.

## Schema-triggered repreparation

MySQL 8.4.11 refreshes captured user-variable types after DDL on a referenced
table, even when the statement contains no parameter markers. The differential
history prepares `SELECT @v FROM schema_source` while @v is an integer, then
changes @v before each operation:

- UPDATE of existing rows and ALTER of an unrelated table retain the old type.
- Adding a column or index, changing a table comment, repeating the same
  comment, and TRUNCATE refresh the captured type.
- A no-op `ALTER TABLE ... ENGINE=InnoDB` refreshes types; ANALYZE does not.
- Base-table DDL beneath nested views and an altered view definition both
  refresh the captured type.
- ALTER of a temporary table also refreshes the type.

fsdb retains dependency definitions per prepared handle. Base tables carry an
in-memory DDL revision, including no-op alterations; row writes preserve it.
The revision is deliberately absent from snapshots because prepared handles
cannot survive restart. Creation times distinguish replacement tables, while
view catalog definitions identify view changes. Dependency stamps contain no
row roots.

A separate oracle probe showed that a temporary table may shadow a permanent
view. fsdb currently refuses the CREATE TEMPORARY TABLE with error 1050. This
is recorded under views in GAPS.md; the dependency traversal nevertheless stops
at a temporary-table entry rather than expanding a hidden view.

## Decimal expression descriptors and values

`just prepared-type-oracle` checks decimal expression metadata and exact wire
text with `GetMySqlDecimal`, avoiding System.Decimal normalization. It covers
an ordinary query and a retained prepared handle after decimal, integer, NULL,
and a different-scale decimal assignment.

With @v initially 1.25, direct reads advertise precision 65 and scale 30 while
returning `1.25`. Arithmetic, ABS, unary negation, and COALESCE also advertise
scale 30, but pad their results to that scale. CASE returns the selected value's
current scale instead. ROUND and TRUNCATE with a requested scale of 2 advertise
scale 2 and report precisions 38 and 37 respectively. Addition and subtraction
report precision 66 in the connector's result metadata; this is result metadata,
not a permitted DECIMAL column declaration.

The differences are not purely presentational. For the retained decimal @v
holding 1.25, MySQL returns:

```text
@v + 1 = 2.250000000000000000000000000000
@v / 3 = 0.416666666000000000000000000000
```

fsdb at `4f45e955` returned `2.25` and `0.416667`. Padding that quotient
would preserve the wrong value. Scalar division now retains intermediate
fractional precision in nine-digit groups, using the operand scales and MySQL's
default precision increment. This follows the grouping in MySQL 8.4.11's
[`do_div_mod`](https://raw.githubusercontent.com/mysql/mysql-server/mysql-8.4.11/mysys/decimal.cc).

Shared expression formatting applies declared scales to arithmetic, ABS, MOD,
ROUND, TRUNCATE, COALESCE, and IFNULL. Direct variable reads and CASE retain the
selected value's scale. Text conversion uses the same formatting, while numeric
nesting keeps guard digits: `CONCAT(1/3)` returns `0.3333`, `(1/3)*3` returns
`1.0000`, and `CAST(10.00/3 AS CHAR)` returns `3.333333`.

Exact-text Expecto regressions cover division, result descriptors, integer and
NULL reassignment, and nested conversions. The differential manifest at
`artifacts/runs/20261006T141436113-7989/contracts/manifest.json` records parity
for the expanded decimal history, including negative values and a nine-digit
fraction. Differential numeric normalization does not prove trailing-zero
formatting; the exact-text regressions cover that separately.

The decimal gap remains open for exact expression precision metadata, including
addition's precision 66 and ROUND/TRUNCATE's differing precision descriptors,
and for scale propagation through other expression families. Runtime values
still use System.Decimal: intermediate division is capped at scale 28 and its
96-bit coefficient cannot retain all MySQL DECIMAL values or guard digits.
The full oracle corpus therefore remains a specification, not a claim of
complete fsdb decimal parity.

## Division precision increment

`just prepared-type-oracle` also checks `div_precision_increment` against MySQL
8.4.11. The session setting changes both decimal division and AVG. The corpus
checks exact text and precision/scale descriptors at increments 0, 1, 4, 9,
and 30, including nested multiplication. For example:

| Increment | `1/3` | `(1/3)*3` | AVG of integer values 1, 2, 2 |
|---|---|---|---|
| 0 | `0` | `0` | `1` |
| 1 | `0.3` | `1.0` | `1.7` |
| 4 | `0.3333` | `1.0000` | `1.6667` |
| 9 | `0.333333333` | `0.999999999` | `1.666666666` |

Both SQL PREPARE and binary preparation retain the increment from preparation.
Changing the session value does not change the prepared descriptor or result.
An ALTER on a referenced table forces repreparation and captures the new
increment: preparing `n/3` at four and executing after a change to one returns
`0.3333` before ALTER and `0.3` afterward.

The valid assignment range is 0 through 30. MySQL clamps -1 and 31 to its
endpoints and emits warning 1292. Decimal, quoted integer, and NULL assignments
fail with 1232/42000. The oracle checks these diagnostics separately from
query evaluation. Its ordinary and binary prepared probes use distinct SQL
texts because MySqlConnector caches preparation by SQL text on a connection.

fsdb now exposes the GLOBAL and SESSION setting, including inherited defaults,
integer-only assignments, and clamping diagnostics. Division and ordinary,
DISTINCT, and windowed AVG use it. Prepared handles retain the increment
independently of the live system-variable read; both schema and parameter-type
repreparation refresh it. Binary preparation and execution report the retained
scale.

MySQL materializes a windowed AVG at its declared scale before surrounding
arithmetic. At increment one, `AVG(n)*3` over 1, 2, 2 returns `5.0`, whereas
`(AVG(n) OVER ())*3` returns `5.1` on each row. fsdb preserves guard digits for
the ordinary aggregate and rounds the window result at materialization. Both
forms have exact-text regressions and differential coverage.

The scale-30 oracle cases still exceed the current System.Decimal
representation. Configuring the increment does not close that numeric precision
gap or the remaining exact expression precision descriptors.

`artifacts/runs/20261006T143455022-11863/contracts/manifest.json` records
matching division, ordinary/DISTINCT/window AVG, retained SQL and binary
handles, schema and parameter-type refresh, and assignment diagnostics, with
both targets restored after the run.
