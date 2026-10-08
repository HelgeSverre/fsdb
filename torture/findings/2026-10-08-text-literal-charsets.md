# Text literal charset identity

Native oracle: MySQL 8.4.11, disposable same-host server, 64 MiB InnoDB
buffer pool and redo capacity. The
[executable oracle](../scripts/text-literal-charset-oracle.py) checks names,
values, error codes, and SQLSTATE. Each case uses a fresh client connection;
statements within a case share their session. The runner is shared with the
NAME_CONST oracle.

## Literal identity

| Expression or context | Charset | Collation | Coercibility |
|---|---|---|---:|
| Ordinary `'a'`, default connection | utf8mb4 | utf8mb4_0900_ai_ci | 4 |
| Ordinary `'a'`, connection latin1_bin | latin1 | latin1_bin | 4 |
| `_latin1'a'` | latin1 | latin1_swedish_ci | 4 |
| `_utf8mb4'a'`, including a utf8mb4_general_ci connection | utf8mb4 | utf8mb4_0900_ai_ci | 4 |
| `N'a'` or `_utf8'a'` | utf8mb3 | utf8mb3_general_ci | 4 |
| Ordinary `'a'`, binary connection | binary | binary | 4 |
| `_binary'a'`, including a latin1 connection | binary | binary | 4 |

An introducer uses the charset's default collation, independently of the
connection's selected collation. Adjacent strings retain the first introduced
literal's identity. CONCAT of two latin1 literals and NAME_CONST with a latin1
value retain latin1_swedish_ci. CAST AS CHAR CHARACTER SET latin1 likewise
reports latin1_swedish_ci.

COLLATE rejects a different charset with 1253/42000. Examples include plain
`'a' COLLATE 'binary'` in a utf8mb4 connection, `_latin1'a' COLLATE utf8mb4_bin`,
and `N'a' COLLATE utf8mb4_bin`. Compatible latin1 and utf8mb3 controls succeed.

## Preparation and stored definitions

Preparing `SELECT CHARSET("a"),COLLATION("a")` in a utf8mb4_general_ci
connection captures utf8mb4/utf8mb4_general_ci. Executing after SET NAMES latin1
retains those results.

A view created from `'a' AS v` in a latin1_bin connection retains latin1_bin
when queried from a utf8mb4 connection. The tested mergeable literal view also
retains coercibility 4. A stored function returning COLLATION('a') similarly
retains its creation connection's latin1_bin collation.

## Encoded values and protocol metadata

Introducers accept quoted text, hexadecimal bytes, and bit strings. The
following results distinguish byte identity from decoded character count:

| Literal | HEX | LENGTH | CHAR_LENGTH |
|---|---|---:|---:|
| `_latin1'é'` from a UTF-8 client | C3A9 | 2 | 2 |
| `_latin1 X'C3A9'` | C3A9 | 2 | 2 |
| `_utf8mb4 X'C3A9'` | C3A9 | 2 | 1 |

Separate native `mysql --column-type-info -vvv` probes show that protocol
metadata describes the result encoding. With default utf8mb4 results, plain,
latin1-introduced, and national ASCII literals all expose VAR_STRING,
collation 255, and length 4. With SET NAMES binary, the plain ASCII literal
exposes VAR_STRING, collation 63, length 1, and NOT_NULL/BINARY flags. These
metadata observations are exploratory; the maintained oracle checks SQL
observable identity and values, not raw protocol descriptors.

## fsdb coverage and remaining boundary

Explicit introducers and national literals retain their charset in the AST.
Binding rejects incompatible COLLATE annotations, and byte-oriented functions
use the shared expression collation resolver. Mixed utf8mb3/utf8mb4 literals
select utf8mb4 for the tested comparison and CONCAT forms.

Quoted, hexadecimal, bit, adjacent, uppercase-introducer, and parenthesized
projection cases preserve their native labels. NAME_CONST uses the decoded
name value. SQL rendering emits introduced hexadecimal bytes so reparsing does
not reinterpret an already-decoded latin1 value as UTF-8 client text. WAL and
snapshot regressions recover a generated `HEX(_latin1'é')` expression and
produce `C3A9` after inserting into the recovered table.

Ordinary literals parsed for a session retain their connection collation in the
AST. Prepared execution keeps utf8mb4_general_ci after SET NAMES latin1.
Statement and view parser caches include that collation in their keys. Views
store their creation collation in the catalog, recover it through WAL and
snapshots, and expose it through `information_schema.VIEWS.COLLATION_CONNECTION`.
Stored functions already restore their creation context; the regression also
pins that behavior.

View client charset is independent of connection collation. The native export
case creates a view under `character_set_client=ascii` and
`collation_connection=latin1_bin`, then switches to utf8mb4. SHOW CREATE VIEW
and information_schema.VIEWS retain ascii/latin1_bin. Both values are stored
in fsdb's view catalog and survive WAL and snapshot recovery. An export/recreate
regression restores the reported session context before executing the returned
DDL and verifies latin1_bin with literal coercibility 4.

A direct read of a literal-only view preserves literal coercibility by expanding
its single row. The native constant-row result retains coercibility 4 even with
ALGORITHM=TEMPTABLE. Expansion supports ordinary ordering and grouping. Bare ORDER BY projection
names retain their output-alias precedence; references inside ordering
expressions retain source-column precedence. Grouping dependencies are checked
against the original source before literal substitution. Expansion excludes
nested queries, HAVING, and rollup. The nested-query control retains its inner
`v` rather than substituting the view's `v`.

ONLY_FULL_GROUP_BY treats an entire grouped expression as valid, but does not
infer that its input columns are determined. For a table containing -1 and 1,
`SELECT v GROUP BY ABS(v)` and `SELECT ABS(v)+1 GROUP BY ABS(v)` both fail with
1055. `SELECT ABS(v) GROUP BY ABS(v)` succeeds, as does `SELECT v+1 GROUP BY v`.
GROUPING inspects a grouping key and remains valid with an expression argument.
The same shared validator checks these contracts before literal-view folding.

Stored schema expressions have a different native lifetime. Under
`SET NAMES latin1 COLLATE latin1_bin`, generated and default expressions using
`'a'='A'` evaluate to 1, while an explicit latin1_bin annotation evaluates to 0.
CHECK uses the same charset-default rule. A separate native SHOW CREATE probe
for `INDEX ((IF('a'='A',id,0)))` renders both literals with `_latin1` introducers.
The parser shares this normalization across generated columns, expression
defaults, checks, and functional indexes. Recovery tests pin the generated,
default, and check behavior.

Validation: `just check` passes 3,033 tests with zero build warnings or errors,
using `DOTNET_PROCESSOR_COUNT=8` and a 4 GiB `DOTNET_GCHeapHardLimit`. The
maintained native oracle passes. Differential contracts pass 49 cases and
5,117 steps with no differences; the run artifact is
`torture/artifacts/runs/20261007T233639319-67596/contracts`.

## Materialization and alias boundaries

With `grouped_literal` defined as a latin1_bin literal `'a' AS v`, the native
oracle and initial fsdb probes exposed these differences, now covered by the
source-expression metadata implementation below:

| Query shape | MySQL | fsdb |
|---|---|---|
| COERCIBILITY(v) beside a scalar subquery | 4 | 2 |
| COERCIBILITY(v) through a join | 4 | 2 |
| COERCIBILITY(v) with GROUP BY v WITH ROLLUP | 4 for both detail and total | 2 for detail, 6 for total |

The literal-view ordering alias case is covered by the [ordering alias implementation](2026-10-08-order-aliases.md).
The native cases remain in the executable oracle and now have Expecto and wire
contracts. The historical fsdb boundary probe is
`/tmp/fsdb-view-coercibility-boundary.log`. Stored-program binding combinations beyond the
tested function, invalid byte sequences, client encodings beyond the current
UTF-8 input assumption, and broader expression collation inference also remain
open. Introducer and national-literal definition rendering needs further
coverage beyond the ASCII export/recreate case.

Primary-key timing probes use the existing sequential test helper with unchanged
limits. Both a clean committed control and view-export work encountered failures
when those probes ran alongside other fixtures. The
[timing-isolation record](../../benchmarks/results/70e0be67-primary-key-timing-isolation.md)
keeps the controls and their interpretation.


### Source expression metadata and ROLLUP

A fresh MySQL 8.4.11 probe broadens the remaining boundary. The maintained
oracle now checks CHARSET, COLLATION, and COERCIBILITY together on a latin1_bin
base column, a direct-column view, a literal view, a CONCAT-of-literals view,
and a derived latin1 literal, both ordinarily and with ROLLUP.

The total row returns NULL as the grouped value but retains the detail row's
charset, collation, and coercibility. Base columns and direct-column views
retain coercibility 2; literals, constant-expression views, and derived
literals retain 4. The derived introducer uses latin1_swedish_ci, while the
other fixtures retain latin1_bin from their definitions. Thus replacing a
grouping expression with a bare NULL loses observable static metadata even
without a view. A fix must preserve source-expression metadata independently
of materialized row values and subtotal NULLs.


### Source metadata implementation and validation

Source column descriptions retain the expression's collation name and
coercibility independently of its materialized value. View and derived-source
resolution carry that descriptor through renaming and joins; UNION combines
branch identities using the shared collation rules. Ordinary physical column
constructors and snapshot decoding leave the runtime descriptor absent.
CREATE TABLE AS derives physical columns without copying expression identity.

ROLLUP retains the grouping expression and marks its subtotal value NULL in the
evaluation context. Metadata inference still sees the original expression;
aggregate inputs retain their ordinary row context. This preserves both NULL
totals and the static metadata exposed by CHARSET, COLLATION, and COERCIBILITY.

The maintained oracle additionally covers LIMIT, DISTINCT, derived numeric
aggregates, derived NULL, and UNION literals. The source-expression-collation
wire contract exercises 22 queries through direct SQL, binary preparation,
and SQL PREPARE/EXECUTE. Native MySQL 8.4.11 uses a disposable server with a
64 MiB buffer pool and redo capacity. The full gate passes 3,055 tests with no
build warnings or errors under eight logical processors and a 4 GiB heap cap.
The differential run `20261008T035011168-14167/contracts` passes 61 cases and
7,002 steps with zero differences. Broader expression inference and stored
program binding remain outside this verified matrix.
