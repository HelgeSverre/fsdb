# Correlated top-N with text equality prefixes

A query that retrieves recent rows for the current tenant can now use
`(tenant, state, stamp DESC, id)` when the correlated tenant key is text.
The comparison must use the indexed column's collation. Under a folding
collation, several spellings can compare equal while occupying separate
ordered runs. Fsdb seeks the timestamp bounds within each run and merges
the runs in the requested suffix order. The original predicate still
filters rows before OFFSET and LIMIT.
[MySQL collation coercibility](https://dev.mysql.com/doc/refman/8.4/en/charset-collation-coercibility.html)
[MySQL index ordering](https://dev.mysql.com/doc/refman/8.4/en/order-by-optimization.html)

The [SQL fixture](2026-11-27-correlated-text-index-top-n.sql) matched all
31 result rows with pinned MySQL 8.4.11 in both Fsdb storage modes. It
covers case and accent folds, reverse traversal, a numeric offset, a
residual filter, NULL and missing keys, `IGNORE INDEX`, and an explicit
binary collation that changes membership. A seeded sweep (27860) also
matched 144 queries per mode across `utf8mb4_0900_ai_ci`,
`utf8mb4_unicode_ci`, and `utf8mb4_0900_bin`. The sweep used two text
prefix columns, case and accent folds, trailing spaces, multiple sort
directions, bounds, and offsets.

| Fixture query | Fsdb examined rows before | Fsdb examined rows after |
| --- | ---: | ---: |
| Lateral with cutoff, residual filter, and offset | 714 | 36 |
| Scalar with reverse ordering and offset | 709 | 22 |

The larger timing run used 12,000 events and 50 outer rows. Both Fsdb
builds were native, in-memory Debug servers on the same host, with
`93786f11` as the baseline. The loopback client rotated server order
over seven measured executions per query after two warm-ups. It checked
each result against MySQL.

| Query | Before median | After median |
| --- | ---: | ---: |
| Lateral with cutoff, residual filter, and offset | 1,980.71 ms | 18.92 ms |
| Scalar with reverse ordering and offset | 1,939.19 ms | 26.70 ms |

The ordered path still requires a supported physical index suffix.
Other text expressions and comparisons that use a different collation
fall back to the ordinary execution path. EXPLAIN still labels these
correlated accesses as scans.
