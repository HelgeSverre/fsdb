# Unaliased expression source labels

Status: fixed for the audited naming cases. Parsed projections retain source
names through execution, prepared binding, metadata inference, and view rendering.
Literal COLLATE charset validation remains a separate compatibility gap.

The maintained [expression-label oracle](../scripts/expression-label-oracle.py)
checks column headers and values against a disposable native MySQL 8.4.11
server with a 64 MiB buffer pool and redo capacity. The client uses `--comments`
to prevent client-side removal of the syntax under test.

| Projection | MySQL column name |
|---|---|
| `concat('a','b')` | `concat('a','b')` |
| `1  +  2` | `1  +  2` |
| `(1 + 2)` | `(1 + 2)` |
| `1 /* middle */ + 2 /* tail */` | `1 /* middle */ + 2` |
| `/* leading */ 1 + 2` | `1 + 2` |
| `((1))` | `1` |
| `'a' 'b'` | `a` (the value is `ab`) |
| `CAST(1 AS CHAR)` | `CAST(1 AS CHAR)` |
| `CASE WHEN 1 THEN 'a' ELSE 'b' END` | `CASE WHEN 1 THEN 'a' ELSE 'b' END` |

SQL modes preserve their original expression spelling: `PIPES_AS_CONCAT`
retains `'a'||'b'`, `ANSI_QUOTES` retains `"x" + 1`, and
`HIGH_NOT_PRECEDENCE` retains lowercase `not` in `not 1 = 0`.
An executable version comment in `1 /*!80000 + 2 */` produces `1  + 2`.

Projection records retain names independently of aliases and expression rewrites.
Source positions account for length changes made by SQL-mode preprocessing.
The SQL source scanner excludes trailing trivia while retaining interior
comments. Literal names preserve numeric, boolean, and binary spelling;
redundant parentheses around positive literals are removed. Adjacent quoted
strings concatenate as values and retain the first string's name. Leading
ASCII whitespace and controls (U+0000–U+0020 and U+007F) are removed from
string names, independently of the value; nonbreaking spaces remain.

Execution and prepared-metadata regressions cover the table above, SQL-mode
length changes, grouped window rewrites, derived sources, and explicit aliases.
The maintained oracle also checks user-variable, window, and full-text names.

`NAME_CONST` uses its first literal's value as the name: `001` becomes `1`,
`1.00` retains its scale, `1e0` becomes `1`, and binary `X'41'` becomes `A`.
Binary name bytes decode as UTF-8. Introduced binary literals retain the full
source expression, including parentheses and interior comments; unintroduced
`0b01` retains its token spelling. Explicit aliases override inferred names.

The `_latin1'é'` control retains the decoded name `Ã©` on both engines.

## NAME_CONST argument validation

The [argument oracle](../scripts/name-const-oracle.py) checks acceptance,
column names, values, error codes, and SQLSTATE on native MySQL 8.4.11.

The name accepts a literal, including Boolean and temporal literals, with
parentheses and unary plus. NULL names produce 1382/HY000. Negation, arithmetic,
function calls, variables, parameters, and COLLATE names produce 1210/HY000.
The value accepts ordinary literals or a single negation or COLLATE wrapper;
Boolean and typed temporal literals are rejected. Wrappers cannot nest.
Invalid argument shapes take precedence over the NULL-name error.

fsdb validates argument source syntax before folding can erase `-0` or
Boolean literal spelling. Execution and PREPARE regressions also check unused
branches, empty-result queries, views, and stored definitions. Wrong arity
produces 1582/42000. Parsing and definition validation share the diagnostic
mapping.

Repeated COLLATE clauses retain their nesting and the outer annotation governs
comparison and metadata. NAME_CONST rejects a repeated wrapper with 1210/HY000,
while a single quoted collation name is accepted. SET preserves the expression
parser's diagnostic for both direct execution and PREPARE, including 1210/HY000
for invalid NAME_CONST argument shapes and 1382/HY000 for a NULL name.

## Remaining literal COLLATE charset validation

`SELECT NAME_CONST(1,NULL COLLATE utf8mb4_bin)` still returns NULL rather than
1253/42000. This is a general expression-validation gap: native MySQL also
rejects `NULL COLLATE utf8mb4_bin`, binary literals with that collation, and
`'a' COLLATE 'binary'` in a utf8mb4 connection. The argument oracle retains
these native controls.

Validation order matters. An invalid NAME_CONST shape, such as
`NAME_CONST(1+1,NULL COLLATE utf8mb4_bin)`, produces 1210/HY000; with a literal
NULL name and the same value, the charset error takes precedence over the
1382 NULL-name error. A shared expression-validation step must preserve those
boundaries, including unused branches and PREPARE.
