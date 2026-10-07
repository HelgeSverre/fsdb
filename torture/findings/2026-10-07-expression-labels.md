# Unaliased expression source labels

Status: fixed for the audited naming cases. Parsed projections retain source
names through execution, prepared binding, metadata inference, and view rendering.
`NAME_CONST` argument validation remains a separate compatibility gap.

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

## Remaining NAME_CONST argument validation

Native MySQL 8.4.11 rejects a NULL name with 1382/HY000 and rejects negative
names (including `-0`), arithmetic, function calls, and COLLATE expressions
with 1210/HY000. The maintained oracle checks these errors. fsdb currently
returns NULL for a NULL name and accepts the other expressions. Its scalar
implementation only checks arity and NULL; parser folding also erases the
negative syntax in `-0`. Validation must preserve the relevant argument shape
rather than infer acceptance solely from its evaluated value.
