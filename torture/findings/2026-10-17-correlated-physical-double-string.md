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
