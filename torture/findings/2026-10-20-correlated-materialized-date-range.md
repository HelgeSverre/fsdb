# Correlated temporal ranges over stable materialized sources

A stable materialized source with a DATE column re-scanned its rows for a
correlated range. On 365 distinct 2026 dates and two outer bounds,
`d.d >= o.min_d` returned the correct counts but evaluated the leading
residual function 1,098 times. The same corpus under native MySQL 8.4.11
returns 12 matches for `2026-12-20` and zero for a NULL bound.

The statement-local range lookup now accepts DATE source rows, DATE-valued
outer bounds, and valid ISO `yyyy-MM-dd` string bounds. It still rejects
zero/invalid DATE source rows and other bound spellings, leaving those cases
on the existing scan. Source-row and bound compatibility are checked
separately: a valid date string can bound a DATE lookup without becoming a
valid source-row key.

The [native MySQL oracle](../scripts/correlated-materialized-date-range-oracle.py)
checks five predicates over a grouped CTE: strict and inclusive lower ranges,
BETWEEN with two outer DATE columns or an ISO literal upper bound, and a
complete OR with an ID equality. The focused regression checks the same
counts, including NULL outer bounds, and fewer than 30 residual calls for
each shape. Before this change, the inclusive range and ISO-literal BETWEEN
each made 1,098 calls in that regression.

The same grouped-source corpus now covers `DATETIME(6)` and `TIMESTAMP(6)`
under MySQL 8.4.11 with six predicates each, including a date-only literal
bound against timestamped rows. The statement-local lookup
accepts temporal source rows and DATE, DATETIME, TIMESTAMP, or valid ISO
date/time bounds; strings with other spellings retain the scan path. The
focused regression checks both result counts and fewer than 30 residual
calls per predicate. Its inclusive lower bound made 1,098 calls before
the extension.

Zero/invalid temporal source values, non-ISO string bounds, and other
correlated temporal expressions remain outside this audit.
