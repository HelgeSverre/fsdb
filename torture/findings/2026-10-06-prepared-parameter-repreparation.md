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

Common expression precision metadata now includes addition's precision 66 and
ROUND/TRUNCATE's differing descriptors, as detailed below. The decimal gap
remains open for broader prepared query shapes and scale
propagation through other expression families. Runtime values
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
gap or all remaining expression precision descriptors.

`artifacts/runs/20261006T143455022-11863/contracts/manifest.json` records
matching division, ordinary/DISTINCT/window AVG, retained SQL and binary
handles, schema and parameter-type refresh, and assignment diagnostics, with
both targets restored after the run.


## Decimal precision descriptors

Expression metadata derives precision and scale together from its operands.
Addition reserves a carry digit, multiplication and division bound their
precision, coalescing combines integer and fractional widths, and SUM/AVG
apply their distinct precision increments. ROUND reserves a carry digit when
reducing the scale; TRUNCATE does not. DISTINCT preserves the argument shape.

MySQL can report precision 66 for decimal addition while capping that result's
precision at 65 when it becomes an operand. Thus both `@v+1` and `(@v+@v)+1`
report a wire length of 68 for DECIMAL user variables. Combining
DECIMAL(65,0) with DECIMAL(65,30) reports precision 96 for addition, whereas
COALESCE caps its combined precision at 65 and MOD takes the larger operand
precision. Stored decimal columns
retain the 65-digit ceiling. Binary PREPARE reads expression descriptors
separately from stored-column definitions, preserving the wider wire result.

The shared shape calculation also replaces duplicate arithmetic and aggregate
rules used to describe stored query results. The checked oracle covers literal
and user-variable expressions, nested arithmetic, negative rounding scales,
and ordinary/DISTINCT aggregates. Binary oracle probes use explicit
DECIMAL(65,30) casts for the user-variable input shape because MySqlConnector
treats named variables as bindings in binary prepared commands.

Unary negation now has its own AST node, shared traversal support, and a
persistent expression tag. `-@v` retains precision 65 while `0-@v` reserves the
extra digit. WAL and snapshot regressions exercise generated expressions after
recovery. Integer negation distinguishes constant promotion from runtime
BIGINT checks. Negating an unsigned column or cast parameter accepts 2^63 as
the signed minimum, but rejects larger values with 1690/22003. A signed
minimum column also overflows under negation. Constant BIGINT casts instead
promote to DECIMAL: unsigned 2^63 and UInt64.MaxValue report precision 21,
and negated signed minimum reports precision 20. A bare negated parameter
has a DOUBLE context. Bound expressions retain their runtime origin after
parameter substitution so execution cannot accidentally enable promotion.

The `unsigned-negation-boundaries` contract covers column boundaries,
constant casts, a retained binary handle through overflow and recovery, and
a bare unsigned parameter. The manifest at
`artifacts/runs/20261006T151525810-19239/contracts/manifest.json` records no
differences in values, type families, or error codes. It does not verify wire
precision. Constant inference covers literals, BIGINT casts, basic arithmetic,
and the original ABS builtin; broader constant functions and exact integer
arithmetic descriptor widths remain open.

Remaining descriptor limitations include source spellings lost during parsing.
The direct expression-metadata path for binary PREPARE currently handles
SELECT without CTEs; other prepared query shapes retain the stored-column
fallback. System.Decimal's value precision remains a separate limit.


The metadata oracle passes in both text and binary modes, and the Expecto
regressions assert exact wire lengths and scales. The differential manifest at
`artifacts/runs/20261006T145627015-16934/contracts/manifest.json` records
matching values and type families after these changes; that lane does not
compare precision descriptors.

### Approximate aggregate descriptors

MySQL 8.4.11 reports DOUBLE for SUM/AVG over untyped NULL, text, and approximate
numeric inputs. Typed exact numeric NULL remains DECIMAL. At division precision
increments 0, 4, and 10, SUM(NULL) retains wire length/scale 17/0; AVG(NULL)
uses length 17 plus the increment and scale equal to the increment. DISTINCT
NULL and NULL+NULL follow the same rule. Text and DOUBLE arguments report
length/scale 23/31, where 31 means unspecified fractional precision.

`runApproximateAggregateDescriptors` in the maintained oracle verifies values,
type families, lengths, and scales in text and binary modes. Each precision
setting uses a distinct SQL comment to prevent connector statement caching
from retaining the descriptor captured under a previous setting. The Expecto
regression asserts execution and PREPARE descriptors, including typed DECIMAL
NULL. Both paths retain the derived approximate numeric descriptors instead of
replacing them with generic stored-column metadata.

The differential manifest at
`artifacts/runs/20261006T152124423-20024/contracts/manifest.json` verifies
matching aggregate values and type families in text and binary execution.

### Numeric aggregate conversion

MySQL 8.4.11 returns DOUBLE values 1.25, 1.25, 0, and 12 for
`SUM('001.25'), AVG('001.25'), SUM('foo'), SUM('12abc')`. SUM converts even a
single input before accumulation. The same rule holds for a one-row window.
For text rows '1', '01', and '2', SUM(DISTINCT) is 3 and AVG(DISTINCT) is 1.5,
while COUNT(DISTINCT) remains 3. Numeric conversion precedes numeric aggregate
deduplication; it does not change COUNT's text equality.

The shared aggregate conversion preserves exact integers and decimals,
converts BIT column values to unsigned integers, and converts other inputs to
DOUBLE. Streaming accumulation, ordinary builtin folds, and numeric DISTINCT
use it; registered aggregate replacements retain their existing input contract.
The contract manifest at
`artifacts/runs/20261006T152601781-20910/contracts/manifest.json` verifies text
and binary execution for single-row, DISTINCT, and window cases.

Bit literals require a distinct numeric interpretation:
`SUM(b'01'), AVG(b'01')` return DECIMAL 1 and 1.0000 in MySQL. The binary
literal context rules below preserve that interpretation until materialization.
A BIT(4) column containing 1 and 2 returns DECIMAL 3 and 1.5000 in MySQL;
column values retain their numeric representation. Numeric conversion warning
coverage remains a separate diagnostics limitation.

### Binary literal context boundaries

`runBinaryLiteralContexts` in `torture/scripts/prepared-type-oracle.fsx`
checks the following contracts on pinned MySQL 8.4.11 in text and binary
execution. The literal's binary value and numeric interpretation are distinct:

| Expression | Result family | Value |
|---|---|---|
| `b'01'`, `0b01` | binary BLOB | byte 01 |
| `b'000000001'` | binary BLOB | bytes 00 01 |
| `b'01'+0`, `b'01'*2` | BIGINT | 1, 2 |
| `-b'01'`, `ABS(b'01')` | DOUBLE | -1, 1 |
| `CAST(b'01' AS UNSIGNED)` | BIGINT | 1 |
| `CAST(b'01' AS DECIMAL)` | DECIMAL | 1 |
| `SUM(b'01')`, `AVG(b'01')` | DECIMAL | 1, 1.0000 |
| `b'01'=1`, `b'01'='1'` | BIGINT | 1, 0 |
| `X'01'+0`, `SUM(X'01')` | BIGINT, DECIMAL | 1, 1 |
| `_binary X'01'+0`, `SUM(_binary X'01')` | DOUBLE | 0, 0 |

IF and CASE preserve the selected literal's numeric interpretation even though
the surrounding arithmetic advertises DOUBLE. COALESCE, IFNULL, and CONCAT
produce ordinary binary strings: applying `+0` to their byte-01 result yields
DOUBLE zero. SUM(IF(...)) is DOUBLE 1, whereas SUM(COALESCE(...)) is DOUBLE 0.
A derived-column reference also loses the literal interpretation, including a
single-row direct projection and a UNION-derived source.

Assigning `b'01'` to a user variable yields an ordinary binary value.
`@literal_bytes+0` and `SUM(@literal_bytes)` return DOUBLE zero. Binding that
variable through SQL PREPARE to `SELECT ?+0,SUM(?)` returns BIGINT zero and
DOUBLE zero respectively. A literal implementation must preserve these
materialization boundaries rather than changing all binary-byte coercions.

For 64 one bits, SUM and CAST AS UNSIGNED expose 18446744073709551615, while
CAST AS SIGNED and the client-visible `literal+0` result expose -1. A 65-bit
literal consisting of one followed by 64 zeros yields zero under both `+0`
and SUM. The oracle checks observed values and type families in both wire
modes; it does not assert diagnostic warnings for these boundaries.

The parser preserves bare bit/hex literals as `VBinaryLiteral`, distinct from
ordinary `VBytes` and stored `VBit` column values. Arithmetic reads their
numeric interpretation, while string consumers retain their bytes. COALESCE,
IFNULL, variable assignment, stored-column coercion, and derived-column
materialization remove literal origin. Scalar conditional selection preserves
it. Ordinary byte values render with an explicit `_binary` introducer when
substituted into SQL so they cannot accidentally regain literal semantics.

Numeric storage has a separate overflow boundary: a literal wider than eight
bytes raises 1264/22003 in strict mode, including a nine-byte literal with
leading zeros and numeric value one. With an empty SQL mode, MySQL saturates
at the signed or unsigned BIGINT limit before applying the destination column's
range. BIT(64) and BIGINT UNSIGNED receive 18446744073709551615; DECIMAL(30)
receives 9223372036854775807; DOUBLE receives its floating-point conversion;
TINYINT receives 127. Each column emits one 1264 warning. The differential
contract checks strict rejection, permissive values, and warning rows.

WAL and snapshot encoding preserve literal origin inside generated expressions.
Regression tests recover a generated `source+b'01'` expression through both
paths and execute it after reopening the store. Binary index probes compare
materialized byte keys, and binary function inputs retain their existing
behavior. CONCAT exposes a binary result descriptor for binary arguments.

Aggregate precision derives from byte capacity, capped at 64 bits. The checked
MySQL lengths/scales are 24/0 for SUM(b''), 28/0 for SUM(b'100000001'), 11/4
for AVG(b'100000001'), and 31/0 for SUM(X'010001'). Empty literals contribute
one precision digit. The maintained decimal-descriptor oracle verifies these
shapes in text and binary execution.

Unfiltered source-free scalar subqueries retain their projection descriptors:
`(SELECT b'01')+0` is BIGINT 1, SUM of that subquery is DECIMAL 1, and AVG
is DECIMAL 1.0000. Nested scalar projections retain the same interpretation.
A scalar subquery with a row source or HAVING materializes its bytes instead:
`(SELECT b'01' FROM t)+0` and `(SELECT b'01' HAVING 1)+0` return DOUBLE zero.
A scalar comparison likewise sees zero, while IN and ANY compare against the
literal's numeric interpretation and return true for numeric one. Scalar
materialization therefore belongs outside the shared membership result cache.

Remaining descriptors are observable: `b'01'+0` has MySQL wire length 5 and
fsdb length 20; `b'01'/2` has MySQL length 9 and fsdb length 7. Conditional
source-free scalar reduction distinguishes supported constant-true WHERE
conditions from runtime predicates. Broader constant-function coverage and
expression descriptors remain in GAPS.md.

A reducible source-free scalar query discards LIMIT and OFFSET, even LIMIT 0,
and may include ordinary GROUP BY. ROLLUP, aggregates, windows, HAVING, and
row sources prevent that reduction and retain their limits. This matches
`Item_singlerow_subselect::fix_fields` in the
[MySQL 8.4.11 source](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/item_subselect.cc)
and the pinned text/binary oracle. The executor keeps ordinary query validation
and grouping, but removes the redundant scalar limit before execution.
Multi-column row subqueries retain their limits and materialize literal bytes,
including source-free rows: `ROW(1,1)=(SELECT b'01',b'01')` returns zero;
adding LIMIT 0 makes that comparison NULL. Membership queries remain distinct
from row-value subquery comparisons.

Explicit `_binary` accepts quoted bit/hex literals and the lowercase `0b`/`0x`
prefixes. The parser shares their existing byte decoders and removes numeric
origin: `_binary b'01'+0` and `SUM(_binary b'01')` both return DOUBLE zero.
Whitespace or a comment separates the introducer from a letter/digit prefix;
`_binaryX'00ff'` instead names column `_binaryX` with alias `00ff` and raises
1054/42S22 when that column is absent. Empty and leading-zero byte sequences
remain intact.

The contract manifest at
`artifacts/runs/20261006T164543504-37991/contracts/manifest.json` verifies the
literal contexts, scalar/row subqueries and their limits, and explicit binary
introducers in text and binary execution, variable materialization, and numeric
storage boundaries without differences.

Conditional scalar predicates are pinned separately by `just prepared-type-oracle`
on MySQL 8.4.11. For `(SELECT b'01' WHERE predicate)+0`, text and binary
execution expose these result types and values:

| Predicate | Result type | Value |
| --- | --- | --- |
| `1`, `1=1`, `ABS(-1)=1`, `'a'='A'`, `COALESCE(NULL,1)` | BIGINT | 1 |
| `0`, `NULL` | DOUBLE | NULL |
| `RAND()>=0`, `EXISTS(SELECT 1)` | DOUBLE | 0 |

A session-variable predicate stays DOUBLE when its binding changes through
1, 0, 1: its values are 0, NULL, 0. Direct statements and a retained SQL
PREPARE handle agree. A retained binary prepared statement with a bound
parameter likewise stays DOUBLE through bindings 0, 1, 0, 1, returning NULL,
0, NULL, 0. Binding a true value therefore does not make a predicate equivalent
to a literal true condition. Conversely, `1 OR @scalar_condition` remains
BIGINT 1 across those session-variable changes, including SQL PREPARE reuse.
An eliminated runtime branch does not prevent reduction.

MySQL's `Query_block::setup_conds` and `simplify_const_condition` in
[sql_resolver.cc](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_resolver.cc)
remove eligible constant-true conditions before scalar reduction checks whether
WHERE is absent. This makes condition simplification part of the compatibility
boundary. fsdb uses its scalar evaluator for literal operators and audited original
builtins, sharing SQL null and collation rules with execution. AND/OR combine
constant truth values without evaluating runtime operands during inference.
Bound parameters retain runtime origin inside scalar WHERE conditions, and
non-reduced scalar results materialize their bytes. The original query still
passes through ordinary validation and execution, preserving error reporting.

The audited function set includes numeric arithmetic and trigonometry, extrema,
NULLIF, ABS, COALESCE, IF/IFNULL, case conversion, string lengths, concatenation,
HEX, CRC32, REVERSE, and TRIM. Overridden functions,
other functions, variables, and subqueries cannot execute during inference.
Broader constant functions remain open. The conditional cases now participate in text/binary differential
contracts, including retained SQL PREPARE bindings. The maintained MySQL oracle
also checks a retained binary prepared handle across changing bindings.

The conditional contract manifest is
`artifacts/runs/20261006T170623905-43982/contracts/manifest.json`; it also checks
constant versus runtime LIMIT 0, binary collation, and NULL logical operands.

Nested conditions retain context-specific reduction boundaries. MySQL 8.4.11
returns BIGINT 1 for `(SELECT b'01' WHERE predicate)+0` with
`NOT (0 AND @flag)`, `NOT NOT (1 OR @flag)`, or
`IF(1 OR @flag,1,0)`. `IF(NULL AND @flag,0,1)` also reduces: its condition
cannot be true, although its exact truth value can be false or NULL. In
contrast, `NOT (NULL AND @flag)` remains runtime-dependent. The executor
tracks possible SQL truth values using its existing logical operations;
it does not collapse false and NULL before applying NOT.

Reduction does not propagate through every parent expression. The same
`1 OR @flag` inside `=1`, `IS TRUE`, CAST, COALESCE, CASE, or arithmetic
returns DOUBLE zero. `IF(1,1,@flag)` also retains its runtime boundary even
though its selected branch is constant. Both IF result branches must qualify
as literal expressions before its simplified condition can enable reduction.
The maintained oracle checks these distinctions with changing session variables
and a reused binary prepared parameter. Differential contracts also cover
LIMIT 0 and the NULL-sensitive NOT case.

The nested-condition and numeric-function contract manifest is
`artifacts/runs/20261006T171738627-45712/contracts/manifest.json`.

### Division operand descriptors

`just prepared-type-oracle` pins division operand categories on MySQL 8.4.11
with `div_precision_increment=4`, in text and binary execution. It checks the
result family, display width, precision, scale, and value. The following
comparisons record the original mismatches at fsdb revision `d5388eb5`.
The division operand regressions now match the MySQL descriptors and values.

| Expression | MySQL family / width / scale | fsdb family / width / scale |
| --- | --- | --- |
| `b'01'/2` | DECIMAL / 9 / 4 | DECIMAL / 7 / 4 |
| `b'01'/2.00` | DECIMAL / 11 / 4 | DECIMAL / 9 / 4 |
| `'1'/2` | DOUBLE / 23 / 31 | DECIMAL / 7 / 4 |
| `NULL/2` | DOUBLE / 4 / 4 | DECIMAL / 6 / 4 |
| `NULL/NULL` | DOUBLE / 4 / 4 | VAR_STRING / 0 / 0 |
| `(SELECT b'01' FROM (SELECT 1)t)/2` | DOUBLE / 5 / 4 | DECIMAL / 6 / 4 |
| `CAST('1' AS JSON)/2` | DOUBLE / 23 / 31 | DECIMAL / 7 / 4 |

MySQL uses byte capacity to obtain the numeric precision of a bare binary
literal, but retains its original byte width when another operand makes the
result approximate: `b'01'/NULL` is DOUBLE with width 5 and scale 4.
An introduced byte string instead has unspecified numeric scale:
`_binary X'31'/2` is DOUBLE with width 23 and scale 31. A materialized scalar
binary literal retains width 1 and scale 0 for the division descriptor even
though its numeric interpretation becomes approximate. Numeric result kind
and original display properties therefore cannot be represented by replacing
all operand metadata with an integer descriptor.

`Item_num_op::set_numeric_type`, `Item_func_div::result_precision`, and
`Item_func_div::resolve_type` in the
[MySQL 8.4.11 source](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/item_func.cc)
separate numeric-context classification from operand precision and display
properties. Exact division adds the divisor's scale and the session increment
to dividend precision. Approximate division derives scale from both operands
and width from the original dividend descriptor; it does not always use the
usual DOUBLE width of 23.

Temporal operands at revision `d5388eb5` also expose a value mismatch:
`CAST('2020-01-01' AS DATE)/2` returns DECIMAL 10100050.5000 with width 14
on MySQL, versus 1010 with width 15 on that revision. The shared value layer
now converts DATE, DATETIME, TIME, TIMESTAMP, and stored zero-component values
from their complete numeric fields. Fractional seconds use decimal arithmetic
before exact division; DATETIME(6) therefore retains microseconds instead of
rounding them through DOUBLE. Temporal operand precision derives from numeric
digits and declared fractional precision rather than punctuation in the display
width. Temporal division values and descriptors have dedicated regressions.

The temporal differential contract covers direct casts, stored columns,
zero-component stored values, and TIMESTAMP division under UTC and a session
UTC+02:00 zone. The division operand contract additionally covers bare and
introduced binary literals, reduced and materialized scalar subqueries, text,
JSON, untyped NULL, and stored columns. It compares text and binary execution
with `div_precision_increment` set to 0, 4, 10, and 30. The maintained oracle
and Expecto regressions pin display widths and scales, including the transition
to unspecified scale 31 for a fractional temporal operand divided with NULL.

Numeric-context classification remains separate from display metadata. Exact
division uses the binary literal's numeric precision and retains unsignedness
only when both operands are unsigned. Approximate division uses the original
dividend width and both operand scales. Scalar string columns retain unspecified
numeric scale even when their declared string metadata has zero decimals.

The pinned-container division operand contract manifest is
`artifacts/runs/20261006T185156369-63268/contracts/manifest.json`.
Distinct SQL text for each increment avoids connector prepared-statement cache
reuse. That matrix and the extended maintained oracle also pass against native
MySQL 8.4.11; its contract manifest is
`artifacts/runs/20261006T190046570-65205/contracts/manifest.json`.
Native validation followed an unresponsive OrbStack API during container cleanup.

The temporal numeric-conversion contract manifest is
`artifacts/runs/20261006T173345886-48124/contracts/manifest.json`.

### Calendar CAST modes

DATE and DATETIME casts preserve allowed zero-component values. `NO_ZERO_DATE`
rejects the all-zero date; `NO_ZERO_IN_DATE` rejects a zero month or day in a
nonzero date. Year zero alone is valid, including with both flags enabled.
Strict mode does not turn an invalid CAST into a statement error: rejection
returns NULL and warning 1292, `Incorrect datetime value: '<input>'`.
`ALLOW_INVALID_DATES` permits February 31 but does not permit month 13.

The maintained MySQL 8.4.11 oracle checks each flag separately, their combination,
strict mode alone, and `ALLOW_INVALID_DATES`, in text and binary execution.
Numeric zero converts to the all-zero date when allowed, while the string `'0'`
is invalid. Numeric `20200101` converts to January 1, 2020. This does not establish
support for every compact numeric date/time spelling.

Shared storage and literal validation also allow year zero when month and day
are nonzero. Expecto regressions cover mode-dependent CAST results and warning
text, numeric inputs, year-zero literals, and stored columns. The differential
contract compares calendar values through division by one because the driver's
.NET DateTime reader cannot represent zero-component dates.

Text and binary casts and text-function arguments preserve declared temporal
fractional precision through the shared output formatter, including trailing
zeroes and zero-component DATETIME values. SHA2 hashes the declared precision:
`SHA2(CAST('2024-01-01 00:00:00.5' AS DATETIME(3)),256)` hashes the
text ending in `.500`, rather than `.500000`. Stored TIMESTAMP text retains
fractional precision after conversion to the session time zone.

Temporal arithmetic retains declared fractional precision even when the current
value has zero microseconds; its numeric-context rules are described below.

Component DATETIME coercion quantizes fractional fields before storage and CAST.
It rounds half-up, or truncates under `TIME_TRUNCATE_FRACTIONAL`. A carry into
the next second revalidates the calendar: zero month/day and bounded invalid
calendar dates reject that carry even with `ALLOW_INVALID_DATES`. CAST returns
NULL without a warning. INSERT reports error 1292 in strict mode, or warning
1264 and an all-zero DATETIME in permissive mode.

Year-zero dates with a nonzero month/day follow MySQL's normalization when a
fraction carries: a carry within year zero produces the all-zero date while
retaining the adjusted clock; a midnight carry from December 31 reaches
`0001-01-01`. Validation of zero-date SQL modes precedes this rounding.

TIMESTAMP permits the all-zero sentinel only when every date, time, and
fractional field is zero. Nonzero microseconds are rejected before quantization,
even when the declared precision would round them to zero. Strict insertion
returns 1292; permissive insertion warns with 1264 and stores the sentinel.

Year zero follows MySQL's non-leap calendar. February 29 and other bounded
invalid days require `ALLOW_INVALID_DATES`, independently of zero-date flags.
Without that mode, casts return NULL and warning 1292, and typed DATE literals
reject with 1525. With it, storage preserves the written fields and DATETIME
fractions round normally until a carry requires a valid calendar. That carry
returns NULL without warnings in CAST, including for year-zero February 29.
The component representation distinguishes zero fields from invalid calendar
days while retaining both in its serialization format.

The calendar CAST and temporal text-precision contract manifest is
`artifacts/runs/20261006T175534381-51819/contracts/manifest.json`.

The component fractional-rounding and zero-TIMESTAMP contract manifest is
`artifacts/runs/20261006T180810304-53230/contracts/manifest.json`. It compares
text and prepared casts at precision 0 through 6, rounding and truncation,
calendar carry rejection, year-zero normalization, and strict/permissive
storage outcomes with their warnings.

The year-zero calendar-validation contract manifest is
`artifacts/runs/20261006T181404831-55015/contracts/manifest.json`. It adds
February 29 and 31 across zero-date modes, literal validation, fractional
rounding/carry, and strict/permissive storage.

### Temporal arithmetic contexts

Addition, subtraction, multiplication, and MOD classify temporal operands by
their declared fractional precision. DATE and whole-second DATETIME operands
use integer arithmetic; fractional DATETIME/TIME/TIMESTAMP operands use DECIMAL.
The original temporal descriptors supply numeric digit counts, so an exact-second
DATETIME(6) plus zero still has DECIMAL precision 21 and scale 6 (display width
23), while DATE plus zero has BIGINT width 10.

Unary negation and ABS use DOUBLE for temporal operands, with width 17 plus
declared fractional precision and that same scale. This approximate boundary
is observable: negating DATETIME(6) `2020-01-01 03:04:05.123456` produces the
DOUBLE value `-20200101030405.125`, while adding zero retains the exact decimal
fraction `.123456`.

TIMESTAMP literals preserve their written precision through a typed expression,
as TIME literals do. Binary temporal parameters retain their derived temporal
type during binding; DATETIME parameters therefore keep six fractional digits,
including when the supplied value has no microseconds. Stored-column and reused
prepared-parameter contracts cover the same arithmetic paths. A temporal wrapper
is retained only when the converted value belongs to the expected temporal
family, so inherited DATETIME context does not reinterpret a TIME argument to
ADDTIME/SUBTIME. Approximate temporal conversion parses the exact numeric fields
to avoid the double rounding exposed by a `.123` fraction on a fourteen-digit
DATETIME number. ADDTIME/SUBTIME share fractional-precision inference for typed
temporal results; their string results retain string formatting and omit a
zero fractional part.

The temporal arithmetic descriptor contract manifest is
`artifacts/runs/20261006T184034246-60870/contracts/manifest.json`. It covers
integer and fractional temporal operands, binary arithmetic, negation, ABS,
MOD, written literal precision, stored columns, and a reused DATETIME parameter.

### Constant negation and mixed integer results

MySQL 8.4.11 promotes closed integer functions during unary negation when the
result exceeds signed BIGINT. `-LEAST(18446744073709551615,18446744073709551615)`
and `-NULLIF(18446744073709551615,0)` return DECIMAL with width 21 and scale 0.
Negated ROUND and TRUNCATE of the same unsigned literal have width 22: their
integer operands reserve width 21 before negation adds its sign.

Mixed signed/unsigned BIGINT choices already have a DECIMAL result before
negation. COALESCE, IFNULL, IF, CASE, GREATEST, and LEAST combine the declared
ranges of their branches. This type applies to the chosen value as well as its
wire descriptor, so `COALESCE(18446744073709551615,0)+1` remains exact instead of
raising unsigned overflow. NULLIF preserves its first operand's type.

Closed-expression negation and descriptor inference share the audited constant
predicate and normal expression evaluator. Descriptor evaluation suppresses
warnings and rejects host overrides and runtime bindings. Prepared parameters
remain runtime expressions: an all-unsigned COALESCE result still raises 1690
when negated beyond signed BIGINT, while a mixed signed/unsigned result uses
DECIMAL. The maintained oracle checks constant values and descriptors through
text and binary execution; the negation differential contract also covers
arithmetic and prepared runtime boundaries.

The native MySQL 8.4.11 contract manifest is
`artifacts/runs/20261006T191407359-66965/contracts/manifest.json`.
The native oracle was used because the OrbStack API did not answer its health
probe. The disposable server was stopped and its data directory removed.

### Integer expression descriptors

MySQL 8.4.11 reports integer literals as BIGINT regardless of the smallest
storage type that could hold them. Display widths follow expression precision:

| Expression | Family | Width | Unsigned |
| --- | --- | --- | --- |
| `1` | BIGINT | 2 | no |
| `1+1` | BIGINT | 3 | no |
| `12*34` | BIGINT | 5 | no |
| `1=1` | BIGINT | 1 | no |
| `-(1=1)` | BIGINT | 2 | no |
| `b'01'+1` | BIGINT | 5 | no |
| `CAST(1 AS UNSIGNED)+2` | BIGINT | 22 | yes |
| `MOD(CAST(1 AS UNSIGNED),2)` | BIGINT | 22 | yes |
| `1 DIV 2e0` | BIGINT | 22 | no |
| `b'01'/b'01'` | DECIMAL | 9 | no |

CAST to SIGNED or UNSIGNED reserves width 21, even for a one-digit value.
Stored-column widths and literal widths therefore cannot substitute for CAST
widths. Integer user variables likewise reserve width 21; their arithmetic
precision does not shrink to the current value.

Bare binary literals contribute numeric precision from byte capacity but retain
a signed numeric descriptor. Integer arithmetic inherits unsignedness from
either operand; exact decimal arithmetic requires both operands to be unsigned.
MOD determines its width before inheriting the dividend's unsigned flag.
`NO_UNSIGNED_SUBTRACTION` clears the result flag after width inference.

DIV derives precision from the dividend's whole digits and divisor's scale,
capped at 21. Unspecified scale is zero for the dividend and the operand's
precision for the divisor. Boolean negation reserves a sign even though the
predicate itself has width 1.

The maintained oracle checks text and binary descriptors, values, and integer
signedness. Expecto also checks PREPARE descriptors, which retain integer
expression metadata independently of generic column definitions. The differential
contract covers literals, stored columns, user variables, and unsigned subtraction.

The native MySQL 8.4.11 contract manifest is
`artifacts/runs/20261006T193956330-70045/contracts/manifest.json`.
The root gate passes with `DOTNET_PROCESSOR_COUNT=4`; a scheduler timing test
that exceeded its five-second admission window with two workers also passes
in isolation with four. Native MySQL supplied the oracle while OrbStack's API
was unresponsive, and the disposable server and data directory were cleaned up.


## Approximate expression descriptors

MySQL 8.4.11 distinguishes stored floating-point columns from numeric expressions.
For a row with `d DOUBLE`, `f FLOAT`, and `df DOUBLE(10,2)`, all containing 1.25:

| Expression | Family | Width | Scale |
|---|---|---:|---:|
| `d` | DOUBLE | 22 | 31 |
| `f` | FLOAT | 12 | 31 |
| `df` | DOUBLE | 10 | 2 |
| `d+0`, `f+0`, `SQRT(4)` | DOUBLE | 23 | 31 |
| `df+df`, `df*df`, `COALESCE(df,0)` | DOUBLE | 10 | 2 |
| `df+1.2345`, `df*1.2345` | DOUBLE | 12 | 4 |
| `-df`, `ABS(df)` | DOUBLE | 19 | 2 |
| `ROUND(df,1)`, `TRUNCATE(df,1)`, `FLOOR(df)` | DOUBLE | 23 | 31 |
| `COALESCE(f,0)` | FLOAT | 23 | 31 |
| `COALESCE(f,1.25)` | DOUBLE | 23 | 31 |
| `CAST(1 AS FLOAT)` | FLOAT | 23 | 31 |
| `-b'01'`, `ABS(b'01')` | DOUBLE | 17 | 0 |

Scale 31 denotes unspecified fractional precision. Finite-scale arithmetic and
conditional expressions combine the largest whole-part display width with the
largest scale. Unary approximate operations use their own width rule; rounding
functions return unspecified scale. FLOAT combined with DECIMAL promotes to
DOUBLE, while FLOAT combined with integer branches can remain FLOAT.

Text output and binary values intentionally differ for finite-scale doubles.
`df*df` renders as `1.56` in text but yields 1.5625 through the binary protocol.
Binary prepared execution takes approximate values from retained typed SELECT
and UNION rows before wire serialization. UNION materialization first rounds
values to its combined scale: `df*df UNION ALL df*df` yields 1.56 through both
protocols. Midpoint values round to even (`df*0.1` materializes as 0.12).
SQL EXECUTE and subsequent text queries retain text display formatting.
Character casts and text-function arguments use the same display rule. A selected
integer fallback in `COALESCE(NULLIF(df,df),2)` becomes an approximate value and
renders as `2.00`; its arithmetic retains the declared approximate result type.

MySQL's fixed-point formatter preserves the shortest meaningful digits before
padding, avoiding extra binary approximation digits. For example, negating
`TIMESTAMP '2020-01-01 00:00:00.123'` renders `-20200101000000.120`, and negating
the binary literal for 2^63 renders `-9223372036854776000`.
The implementation uses a shared formatter for stored columns, expressions,
character casts, and text-function arguments. Its rules are grounded in the
native oracle and MySQL's
[`aggregate_float_properties`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/item.cc)
and [`my_fcvt_internal`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/strings/dtoa.cc)
implementations.

The maintained oracle checks text and binary values, families, widths, and scales.
Expecto checks text output and both execution and PREPARE descriptors. The
`approximate-expression-descriptors` contract also covers text conversion and
user variables.

Scientific-notation literal widths are covered below. Broader scalar/function
descriptor families remain outside these matrices; these results do not
establish complete numeric descriptor parity.

Validation: `DOTNET_PROCESSOR_COUNT=4 just check` passes all 2,824 tests.
The maintained native MySQL 8.4.11 oracle passes. The compatibility contract
run passes 3,299 steps across 37 cases with no differences; its manifest is
`artifacts/runs/20261006T202152662-79151/contracts/manifest.json`.
The disposable native oracle server and data directory are cleaned up.


## Scientific literal descriptors

Native MySQL 8.4.11 preserves scientific literal spelling for names and DOUBLE
widths: `1e0` has width 3, `1E+00` width 5, `0001e000` width 8, and `.1e1`
width 4. Their scale is 31. Parentheses and unary plus preserve this width;
unary minus, arithmetic, ABS, and COALESCE use the normal expression width 23.
Scalar projections, derived columns, views, and CREATE TABLE AS SELECT preserve
the literal width. Assigning the value to a user variable erases that spelling.

The AST retains the spelling alongside the double value. Shared literal-value
matching keeps index probes, predicate preparation, grouping, and other value
consumers independent of presentation. SQL rendering, WAL, and snapshot recovery
retain the spelling; an explicitly declared DOUBLE column keeps its own width.

DOUBLE's numeric character capacity for DIV is 22 regardless of display width,
following MySQL's [`Item::max_char_length`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/item.h).
For a DOUBLE(10,2) column `d=1.25`, `d DIV 1` has width 21, `d DIV 0.1`
width 22, and `2 DIV d` width 4. FLOAT retains its own operand width.
The maintained oracle and scientific-literal contract cover both protocols,
literal names, projection boundaries, parameter inference, and numeric values.

Validation: the final native MySQL 8.4.11 oracle passes, and contracts pass
3,353 steps across 38 cases with no differences (manifest
`artifacts/runs/20261006T204951041-88299/contracts/manifest.json`).
The final `DOTNET_PROCESSOR_COUNT=4 just check` builds without warnings and
passes 2,827 of 2,828 tests. Its only failure is the existing lookup timing
ratio test (3.26 versus the 2.5 ceiling); that test passes when run alone with
`just test --filter-test-case 'point SELECT by PRIMARY KEY latency'`.
An earlier full gate passed before the additional optimizer regressions.
The final functional tests include spelling, persistence, prepared descriptors,
and indexed DOUBLE lookup coverage. No timing threshold was changed.
