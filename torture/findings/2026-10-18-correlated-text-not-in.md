# Correlated text NOT IN over indexed and materialized sources

An indexed, binary-collated physical text column still scanned for a
correlated `NOT IN` bound. On 1,001 rows with 1,000 copies of `a010` and one
`a999`, `d.code NOT IN(u.lo)` evaluated the leading residual 2,004 times
across the outer probe, even though the matching `a010` rows can be excluded
with two strict index ranges.

The planner now admits text exclusions when the bound is a string whose
effective comparison collation matches the stored key collation. It reuses
the existing complete-OR candidate union: each list member supplies the
rows below or above its key, the smallest union is chosen, and the original
`NOT IN` predicate decides final membership. Nullable bounds may produce an
empty candidate range. Mixed-collation or otherwise unsafe bounds still
decline this narrowing path.

The [native MySQL 8.4.11 oracle](../scripts/correlated-text-range-or-oracle.py)
confirms one row for one- and two-member lists, including the selective
second-member order; a NULL member returns zero rows, and a complete OR with
`d.id=1` returns two. Direct physical, pass-through, and stable materialized
sources agree. The focused regression checks the same counts and fewer than
80 residual calls per shape. The original physical baseline exceeded 2,000
calls for the one-member direct source. An explicit, different comparison
collation keeps the correct row and scans rather than applying the stored
key's order.

## Functional text key follow-up

`LOWER(d.code) NOT IN(LOWER(u.lo))` still scanned all 1,001 rows despite a
maintained `LOWER(code)` index; the leading residual ran 2,004 times on the
two-row outer probe. The correlated physical planner now recognizes a stored
text-result functional key when the comparison uses its collation. It splits
`NOT IN` into two strict functional ranges, chooses the smallest candidate
union across list members, and keeps the original predicate as the final
filter. Direct and pass-through sources use the same stored row identities.

The native MySQL 8.4.11 oracle now covers one and two members, reversed
selectivity, a NULL member, a complete OR, and an explicit mismatched
collation for both source shapes. The focused regression checks identical
counts and fewer than 80 residual calls for compatible cases; the mismatched
collation still scans. Other functional text expressions and stable
materialized expression keys remain to audit.
