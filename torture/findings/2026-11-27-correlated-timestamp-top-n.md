# Correlated TIMESTAMP equality with an ordered limit

During the fall-back transition, two stored UTC instants can have the same
session-local TIMESTAMP. MySQL's correlated ref lookup converts the outer
wall time back to one UTC key. A scan can match both instants. Fsdb now
preserves that distinction for eligible scalar and lateral queries, including
simple derived sources. The execution path keeps the original predicate,
checks residual conditions before applying OFFSET, and filesorts the chosen
slice when its index order cannot serve ORDER BY.
[MySQL indexed TIMESTAMP lookups](https://dev.mysql.com/doc/refman/8.4/en/timestamp-lookups.html)

The [SQL fixture](2026-11-27-correlated-timestamp-top-n.sql) uses a
disposable server and installs a synthetic named time zone. It distinguishes
the two UTC instants, an absent earlier occurrence, NULL equality, and a
fractional DATETIME bound. It also checks a prepared statement across a
session time-zone change.

Against the digest-pinned MySQL 8.4.11 image, Fsdb matched 708 of 720
comparisons after adding the UTC index lookup, up from 528 at `ecfb443e`.
The sweep varied fixed and named time zones, direct and wildcard derived
projections, index hints, equality operators, bound types, sort directions,
and offsets.

With 1,000 additional nonmatching rows, Fsdb examined 16 rows for the
forced scalar query, down from 5,064. MySQL examined 19 rows and returned
the same four results as the revised Fsdb query.

## Sorting the two occurrences

The final 12 cases used `IGNORE INDEX` with the TIMESTAMP column in
`ORDER BY`. The scan filter compares session-local times, so both
occurrences can pass. MySQL sorts the qualifying rows by the stored UTC
instant for direct columns, including projection aliases, ordinal
references, merged derived tables, and merged views. An explicit DATETIME
cast or a materialized projection sorts by the local value.

Fsdb now carries the stored instant through merged projections and uses
it for direct sort keys. The projected value still displays in the session
time zone. All 720 comparisons match the pinned oracle in memory and WAL
mode.

## Seeking correlated TIMESTAMP ranges

An ordered lookup on a composite index can now seek a TIMESTAMP suffix
after a constant or correlated equality prefix. Fixed offsets produce
exact UTC endpoints. Named and system time zones use a covering UTC
interval based on their offset extremes. The executor still applies the
session-local predicate before OFFSET and LIMIT, so it retains both
qualifying occurrences in a repeated hour and filters out the intervening
UTC instants that fall outside the local range.

The digest-pinned MySQL 8.4.11 replay matched all 2,016 queries in memory
and WAL mode. It covered direct and projected sources, ascending and
descending traversal, open and closed bounds, fractional precision, NULL
bounds, both DST transitions, and index hints. A seeded half-hour transition
fixture matched all 4,000 outer-row choices. The regression fixture drops
from 343 examined rows to fewer than 20.

### Leading TIMESTAMP keys

The ordered range also works with TIMESTAMP as the first index column.
When both layouts are available, Fsdb prefers the eligible equality-prefix
lookup. Collation-equal spellings in later sort keys share one sort group
before applying the next key, `OFFSET`, and `LIMIT`. The derived-table
fallback builds its filtering keys in session-local time while keeping
the stored UTC values for ordering.

An expanded replay matched 3,024 queries against the pinned oracle in
memory and WAL mode. On the forced leading-index query, the analyzed plans
reported 4 rows per invocation in Fsdb and 89 in MySQL 8.4.11. Fsdb's
regression drops from 173 examined rows to fewer than 20. Tighter disjoint
UTC slices through a clock transition remain open.

### CTE reference materialization

Two scalar subqueries can refer to the same CTE using the same alias while
only one uses `NO_MERGE`. The merged reference sorts direct TIMESTAMP
columns in UTC. The materialized reference sorts its copied local values.
The distinguishing fall-back fixture returns the earlier occurrence for
the merged reference and the higher-ranked local-time tie for the
materialized reference.

A separate joined query still differs. When a sort follows a join between
merged and materialized references, MySQL's audited plan streams the joined
rows into a sort and compares the merged reference's session-local values.
Fsdb compares that reference's stored UTC instants.
