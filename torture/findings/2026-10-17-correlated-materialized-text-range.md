# Correlated text-expression ranges over materialized rows

Native MySQL 8.4.11 is the result oracle for a five-bound, 1,004-row
correlated fixture in
[`correlated-materialized-text-range-oracle.py`](../scripts/correlated-materialized-text-range-oracle.py).
The inner `SELECT DISTINCT id,k` is stable and materialized; `LOWER(d.k)` is
a row-local expression over its exposed text column. The oracle covers
strict and inclusive inequalities, reversed operands, `BETWEEN`, an explicit
case-insensitive comparison collation, NULL bounds, and a complete OR branch.
It also repeats the selective `BETWEEN` case through a stable CTE source and
checks a selective `NOT BETWEEN` case. The latter returns five outside rows
for `word001`–`word998` and exercises the complete union of two strict
text-expression ranges.

Fsdb already returned MySQL's rows, but it scanned the materialized source
for every outer bound. The focused `BETWEEN` regression evaluated a
`TOUCH(d.id)` residual **6,030 times** before the planner change. A
statement-local ordered lookup now evaluates the stable expression once per
materialized row, sorts text results under the effective comparison
collation, and selects candidate intervals with binary searches. NULL rows
contribute no key; NULL bounds return no candidates. The original SQL
predicate remains the final filter, and candidates return in materialized
source order. A different explicit comparison collation gets a separate
lookup. Complete OR branches can union these candidate sets.

The focused regression now keeps each selective range below 80 residual
calls while preserving seven derived-source, one CTE, and one `NOT BETWEEN`
MySQL result vectors.
Unsupported result
types, non-text bounds, unstable or outer-dependent expressions, and
collation-resolution errors retain the scan path. Broader expression and
coercion shapes remain open.
