# Correlated binary LIKE prefixes over physical rows

Native MySQL 8.4.11 returned the same correlated `LIKE` results from a physical
table and a pass-through derived source. With `utf8mb4_bin` columns, patterns
`a01%`, `a99%`, and `NULL` matched `(2, 2, 0)` rows in a five-row corpus.
Adding `OR c.id = 1` returned `(2, 3, 1)`. The fixture and expected results
were checked on a disposable native MySQL server.

The existing FSDB regression placed its `TOUCH(c.id)` counter after `LIKE`.
Short-circuit evaluation made a scan look selective: it counted only rows that
already matched the predicate. Moving `TOUCH` before `LIKE` in a 1,000-row
indexed corpus exposed 2,004 calls for two selective prefixes and a NULL
pattern. A matching-collation binary prefix now supplies an ordered secondary
index range for direct and pass-through physical sources; the original `LIKE`
still filters candidates. Complete OR branches union the physical row IDs,
and a stable NULL pattern contributes an empty branch rather than forcing a
scan. The shared prefix-bound rule also serves stable materialized sources.

The focused regression checks the same results with a leading residual and
bounds its calls below 60 for a conjunct and below 100 for complete OR
branches. Other collations, patterns without a fixed prefix, and uncovered OR
branches retain the general evaluation path.

## Byte-valued correlated patterns

Native MySQL 8.4.11 also returned `(2, 2, 0)` for outer `VARBINARY` patterns
`0x61303125`, `0x61393925`, and `NULL` against a five-row `VARBINARY` code
column. Adding `OR c.id=1` returned `(2, 3, 1)`. Direct, pass-through derived,
and `DISTINCT` materialized sources agreed. A 1,000-row FSDB regression with
`TOUCH` before `LIKE` initially evaluated 2,004 rows despite an index; result
membership was already correct.

Stable raw-byte patterns now use the same escaped fixed-prefix interval as
direct byte `LIKE`. Physical sources probe their maintained ordered index;
stable materialized sources build a statement-local byte-ordered lookup.
Complete OR branches union candidate rows, and the original `LIKE` remains the
residual filter. The focused regression bounds leading-residual calls for all
three source shapes, and the existing direct VARBINARY and correlated binary
text-prefix tests still pass. Patterns without a fixed byte prefix or with an
explicit collation retain general evaluation.
