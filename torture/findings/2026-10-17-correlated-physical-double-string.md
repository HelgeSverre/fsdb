# Clean string bounds on indexed physical DOUBLE columns

A correlated equality or narrow range against an indexed physical `DOUBLE`
column previously scanned the inner table when its outer bound was a string,
even if that string parsed completely as a finite number. On 1,001 inner
rows, `d.k=o.s` with `o.s='12.5'` returned the correct count but evaluated
`TOUCH(d.id)` 1,002 times. The same happened through a pass-through derived
source.

The [native MySQL 8.4.11 oracle](../scripts/correlated-materialized-double-oracle.py)
now compares physical and pass-through `DOUBLE` sources against string
bounds, alongside the existing materialized cases. It checks equality,
`BETWEEN`, whitespace-padded text, underflow (`1e-500`), malformed text
(`12.5x`), and overflow (`1e309`). The audited row results agree.

For a fully numeric finite bound, the physical equality or range probe now
uses the parsed `DOUBLE` key. The original SQL predicate remains the residual
filter. The focused fsdb regression keeps the narrow probes below 80 residual
calls on both source shapes. A malformed or overflowing string still declines
the probe and scans, preserving the existing conversion path. This audit
covers row membership and candidate work; plan-dependent warning counts for
non-finite text bounds remain to verify separately.

## Correlated positive IN bounds

The physical `IN` planner also declined `DOUBLE` keys, so even
`d.k IN(o.s,o.s)` scanned 1,001 inner rows for each outer probe. It now
converts finite numeric values and fully numeric strings to the DOUBLE key
before unioning indexed row IDs. Duplicate bounds remain deduplicated by row
ID, and the original `IN` predicate remains the residual check. Malformed or
overflowing text still declines the index and scans. In the focused regression,
the clean-string probe falls from 2,004 to fewer than 80 residual calls across
direct and pass-through physical sources. The native MySQL 8.4.11 oracle
matches the audited row counts for each string form.

## Selective inequality

The physical `<>` planner can split a clean finite string bound against a
`DOUBLE` key into two strict indexed ranges. A NULL outer bound also splits
to two empty ranges, avoiding a full scan during the scalar subquery's
metadata probe. On 1,001 inner rows with 1,000 copies of `12.5`, a correlated
`d.k<>o.s` probe for `o.s='12.5'` now checks fewer than 80 rows through direct
and pass-through sources, versus 1,004 residual calls before the change.
The original predicate still decides row membership. The MySQL 8.4.11 oracle
also checks the NULL-bound zero result.

The same repeated-key corpus confirms `d.k NOT BETWEEN o.s AND o.s` and
`d.k NOT BETWEEN o.s AND o.hi` use the two strict ranges. With 1,000 copies
of `12.5`, one `14.5`, and bounds `12.5`/`13.5`, both physical source forms
check fewer than 80 rows. MySQL 8.4.11 agrees on the one unmatched row, the
NULL-bound zero result, and zero warnings for the clean bounds. The original
predicate still decides membership after the indexed candidate union.

`NOT IN` can also use strict-range candidates for clean numeric bounds: any
matching row must differ from every list member. The planner compares each
member's two-range candidate union and uses the smallest, leaving the
original `NOT IN` predicate to decide membership. The repeated-key direct
and pass-through probes check fewer than 80 rows for one- and two-member
lists, including the order where the selective bound is second. MySQL 8.4.11
returns one unmatched row for bounds `12.5`/`13.5` and zero for NULL bounds,
with no warnings. Non-numeric or diagnostic-producing bounds retain the
existing scan path.

The same choice now works inside a complete OR. On the skewed corpus,
`d.k NOT IN(o.hi,o.s) OR d.id=1` returns two rows for the clean bounds and
one for NULL bounds. Direct and pass-through physical sources each perform
fewer than 80 residual `TOUCH` calls rather than scanning the 1,001 rows.
The native MySQL 8.4.11 oracle agrees on both row counts. Every OR arm must
still have a safe lookup; the original predicate filters the union.
