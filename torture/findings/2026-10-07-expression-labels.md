# Unaliased expression source labels

Status: partial. Parsed projections retain source names through execution,
prepared binding, metadata inference, and view rendering. Numeric `NAME_CONST`
names and introduced hexadecimal binary names still diverge.

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
whitespace is removed from string names, independently of the value.

Execution and prepared-metadata regressions cover the table above, SQL-mode
length changes, grouped window rewrites, derived sources, and explicit aliases.
The maintained oracle also checks user-variable, window, and full-text names.

Remaining native-verified differences:

| Projection | MySQL name | fsdb name |
|---|---|---|
| `NAME_CONST(1,2)` | `1` | `NAME_CONST(1,2)` |
| `_binary X'41'` | `_binary X'41'` | `A` |

The `_latin1'é'` control retains the decoded name `Ã©` on both engines.
