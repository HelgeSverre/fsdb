# Correlated integer COALESCE and IFNULL over materialized rows

The pinned MySQL 8.4.11 oracle returns `(1,2)`, `(2,1)`, `(3,0)` for outer
integer bounds `0`, `10`, and `NULL` against 1,000 signed inner keys plus one
NULL key in each of these correlated subquery predicates:

```sql
COALESCE(d.k,0)=o.x
IFNULL(d.k,0)=o.x
COALESCE(d.k,NULL,0)=o.x
COALESCE(NULL,d.k,0)=o.x
```

Here `d` is a stable materialized `SELECT DISTINCT id,k` source. The NULL
inner key contributes to the `0` match, alongside the physical zero key.
Fsdb already returned the same rows, but its expression-key eligibility
excluded both functions. The focused `TOUCH(d.id)=d.id` residual ran 4,012
times for the two-argument `COALESCE` case before the change.

Exact integer-valued `COALESCE` arguments and the two `IFNULL` arguments now
use the existing statement-local integer expression key. Integer and NULL
literals are eligible; a noninteger row result, incompatible bound, or
evaluation error still falls back to the scan. The original SQL predicate
continues to decide final membership. The focused regression checks derived
and CTE sources, all four expressions, NULL fallback, and bounded residual
work. A separate `BIGINT UNSIGNED` matrix includes zero, `2^63`, the unsigned
maximum, and NULL on both sides; its ordered counts match MySQL for
two-argument `COALESCE` and `IFNULL`. The maintained
[profile](../scripts/correlated-materialized-functional-profile.fsx)
reports six residual calls for two-argument `COALESCE`, `IFNULL`, and the
three-argument `COALESCE` form on its 1,000-row corpus. Other expression
domains and unstable correlated sources are outside this lookup.
