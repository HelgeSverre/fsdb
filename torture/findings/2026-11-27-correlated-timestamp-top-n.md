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
it for direct sort keys. The projected value still
displays in the session time zone. All 720 comparisons now match the pinned
oracle in memory and WAL mode. Broader correlated TIMESTAMP range seeks
remain open.
