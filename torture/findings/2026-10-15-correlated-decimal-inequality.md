# Correlated decimal inequality candidates

Native MySQL 8.4.11 returns `(50, 0, 999)` for outer decimal bounds
`(1.25, NULL, 51.25)` against an indexed inner table with 950 rows at
`k=1.25` and 50 rows at `k=2.25..51.25`. Both `d.k <> o.k` and
`NOT (d.k = o.k)` have those counts; adding `OR d.id=500` returns
`(51, 1, 999)`. The [native oracle](../scripts/correlated-inequality-oracle.py)
checks direct, pass-through derived, and stable materialized derived sources.

FSDB returned the same rows before the planner change, but a selective
`TOUCH(d.id)=d.id` residual ran 2,002 times for the direct source. The
planner now unions strict `<` and `>` indexed ranges for a direct DECIMAL
inner column and a stable DECIMAL or NULL bound. The original inequality
still filters candidate rows. A focused regression keeps residual calls
below 80 for direct, pass-through, and materialized sources, including a
complete OR branch and NULL bound; `IGNORE INDEX` restores over 900 calls.

The integer path remains intact.

## Exact integer/decimal bounds

The native oracle also pairs a DECIMAL key containing integral values with an
INT outer bound, and an INT key with an integral DECIMAL outer bound. Both
directions retain the same `(50, 0, 999)` counts and `(51, 1, 999)` OR counts
for direct, pass-through, and stable materialized sources. Before the follow-up,
the selective DECIMAL-key/INT-bound query evaluated its residual 1,002 times.

The candidate split now accepts these exact numeric pairs. A range lookup
normalizes an integer bound to the DECIMAL key only when coercion preserves
the bound's numeric value; rounded or out-of-range conversions fall back to
the prior scan path. The focused regression keeps residual calls below 80
for both directions, including OR and NULL bounds, while `IGNORE INDEX`
restores over 900 calls. Approximate numeric values, text, and functional
index expressions need separate range evidence.

## Fractional bounds against integer keys

MySQL 8.4.11 returns `(50, 0, 0)` for `d.k > o.k` with outer bounds
`(1.50, NULL, 51.50)` against the indexed integer corpus. The `<`, `<=`,
`>=`, and selective `> OR d.id=500` results also agree with the
[native oracle](../scripts/correlated-inequality-oracle.py) for direct,
pass-through, and stable materialized sources.

FSDB already returned those rows, but the selective `>` query evaluated
its residual 1,002 times. An ordered integer index can compare a DECIMAL
boundary exactly, including its fractional part, without converting it to
an integer stored key. Range lookup now retains that boundary for integer
columns. The focused regression keeps residual calls below 80 for direct,
pass-through, and materialized sources, including a complete OR branch.
The original predicate remains the final filter. Text and functional index
expressions remain outside this audited rule.

## Finite DOUBLE bounds against integer keys

MySQL 8.4.11 returns the same `(50, 0, 0)` counts for `INT > DOUBLE`
outer bounds `(1.5, NULL, 51.5)` as for the DECIMAL variant. The native
oracle also checks `<`, `<=`, `>=`, and an OR branch on direct, pass-through,
and stable materialized sources. Negative and near-`INT`-limit fractional
bounds agree on a separate six-row corpus.

Before this change, the selective `>` probe evaluated its residual 1,002
times. The range lookup now retains a finite DOUBLE bound against
`TINYINT`, `SMALLINT`, `MEDIUMINT`, or `INT` keys: every stored key in those
domains is exactly representable as a double, so ordered comparison needs
no integer rounding. The focused regression keeps residual calls below 80
for the selective direct, pass-through, and materialized probes, including
the OR branch. `BIGINT` can exceed double's exact integer range and is not
included in this access-path change.
