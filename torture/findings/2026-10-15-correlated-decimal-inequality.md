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
the OR branch. `BIGINT` needs the separate index-ordering rule below because
its stored integers can exceed double's exact integer range.

## BIGINT and DOUBLE bound provenance

The native MySQL 8.4.11 oracle compares a nine-row BIGINT index spanning
`-2^63`, both sides of `±2^53`, and `2^63-1` with DOUBLE outer columns.
For `d.k > o.k`, bounds `(2^53, -2^53, 1.5)` return `(1, 6, 4)`;
`d.k < o.k` returns `(6, 1, 5)`. `IGNORE INDEX` gives the same rows, and
the focused FSDB regression agrees. The adjacent `2^53+1` row compares
equal to the DOUBLE-column value `2^53` in this context.

The same value written as a DOUBLE literal is different: MySQL returns
both `2^53+1` and `2^63-1` for `k > 9007199254740992e0`, with and
without the index. The index now orders integer keys and DOUBLE probe
values exactly. Its strict range is a candidate superset for both the
literal and outer-column forms; the original SQL predicate still applies
the appropriate comparison to those rows.

On a 1,000-row corpus with 950 keys at `2^53` and 50 keys from
`2^53+1` onward, MySQL returns 49 for the correlated DOUBLE-column bound
and 50 for the DOUBLE literal. Before the index change, the selective
correlated probe evaluated its residual 1,002 times. The regression now
keeps direct, pass-through, and materialized probes below 80 calls,
including a complete OR branch; `IGNORE INDEX` restores more than 900.
The native oracle checks both counts, the nine-row signed-limit corpus, and
an unsigned BIGINT corpus through `UINT64_MAX`. The unsigned adjacent row
likewise appears for a DOUBLE literal and is excluded for a DOUBLE outer
column.

## DOUBLE bounds against DECIMAL keys

MySQL 8.4.11 returns `(50, 0, 0)` for `DECIMAL(10,2) > DOUBLE` outer
bounds `(1.5, NULL, 51.5)` on the indexed 1,000-row corpus. The direct,
pass-through, and materialized source forms also agree for `<` and a complete
OR branch. Before the index change, the selective direct probe evaluated its
residual 1,002 times. It now keeps direct, pass-through, and materialized
probes below 80 calls; `IGNORE INDEX` restores more than 900.

The index compares stored DECIMAL values to finite DOUBLE boundaries exactly,
then expands the candidate cut by one neighboring DOUBLE value on the outside.
This retains decimal values that round to the boundary in SQL comparisons.
The original predicate still decides membership. The [native oracle](../scripts/correlated-inequality-oracle.py)
also exposes a separate high-precision compatibility gap: for five
`DECIMAL(20,2)` values around `2^53` and a DOUBLE-column bound of `2^53`,
MySQL's `>=` result is `(2,3,4,5)` with the index and `(1,2,3,4,5)` with
`IGNORE INDEX`; its `<=` result is `(1,2)` versus `(1,2,3,4)`. The pinned
MySQL 8.4.11 image reports `Range checked for each record` on the indexed
DECIMAL side and a hash join with `IGNORE INDEX`. A focused FSDB regression
now keeps those two plans distinct for an audited two-table inner join in
either written source order: each DOUBLE row probes the exact DECIMAL index
range, while the ignored-index and primary-key-filtered forms evaluate the
ordinary scan comparison. The test also covers reversed inequality syntax
and two different DOUBLE bounds.
Other join shapes still need their own plan and membership evidence.
