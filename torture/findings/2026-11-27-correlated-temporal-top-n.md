# Correlated top-N with temporal keys

Correlated scalar and lateral queries can use an ordered composite slice
with DATE, DATETIME, or TIME keys. DATE and DATETIME can share an ordered
comparison. A fractional outer DATETIME or TIME bound keeps its original
precision, which matters when the indexed column stores fewer fractional
digits. Residual predicates apply before OFFSET and LIMIT.
An ordinary comparison against a NULL bound yields an empty
slice, while NULL-safe equality can seek NULL keys.

The [SQL fixture](2026-11-27-correlated-temporal-top-n.sql) matches the
digest-pinned MySQL 8.4.11 on the selected rows and the forced dependent
`ref` keys, lengths, and outer references. MySQL also reports filesort.
On the date-key query, the Fsdb slow log records 15 examined rows, down
from 4,105 at `3b288a8c`. `EXPLAIN ANALYZE` records ten inner index rows
across five outer invocations.
[MySQL's temporal precision storage](https://dev.mysql.com/doc/refman/8.4/en/fractional-seconds.html).

The differential sweep matched 1,440 variants in each of memory and WAL
mode. It covered scalar and lateral queries, derived projections, both
traversal directions, NULL-safe and ordinary equality, mixed DATE and
DATETIME keys, unequal fractional scales, negative TIME values, strict
and inclusive range bounds, BETWEEN, offset, and index hints.
Another 128 variants matched with DATE and DATETIME sort keys, including
mixed-type range cuts and residual OR and NOT BETWEEN predicates.
Ordered seeks for session-converted TIMESTAMP columns and coercive
predicates remain open.
