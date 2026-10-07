# Unaliased expression source labels

Status: partial. Common nested literal and qualifier labels match, but source
spelling is not retained in the projection AST.

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

Capturing the entire text consumed by the expression parser is insufficient:
trailing comments must be excluded, literals have separate naming rules, and
SQL-mode preprocessing can replace the original spelling. Projection naming
must preserve these distinctions through expression rewrites, prepared binding,
and metadata inference. The native-only boundary cases above describe the
remaining contract; they do not establish fsdb parity.
