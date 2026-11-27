# Correlated top-N index seeks

For a query such as `WHERE e.tenant=o.tenant AND e.state=1 ORDER BY
e.stamp DESC,e.id LIMIT 2`, Fsdb now binds the equality prefix from the
current outer row and walks the index in the requested suffix order. A
compatible bound on the first ordered column narrows the slice before the
walk. The original predicate applies before `LIMIT` and `OFFSET`, so rows
that fail a residual filter never consume either count. The same path
works for an eligible scalar subquery or a lateral derived table. [MySQL index ordering](https://dev.mysql.com/doc/refman/8.4/en/order-by-optimization.html) [MySQL LIMIT optimization](https://dev.mysql.com/doc/refman/8.4/en/limit-optimization.html)

The [fixture](2026-11-26-correlated-index-top-n.sql) matched all 52 result
rows against pinned MySQL 8.4.11 in both Fsdb storage modes. It covers a
two-part equality prefix, forward and reverse traversal with mixed sort
directions, duplicate sort keys with an ID tie-breaker, residual filtering,
offsets, repeated outer keys, NULLs, `IGNORE INDEX`, and a routine variable
that shadows an index column in `ORDER BY`. Further probes
matched signed and unsigned boundary cases, fractional `DATETIME(6)` order,
ordering by a projected alias and an ordinal, and a prepared limit.

| Fixture query | Fsdb rows examined before | Fsdb rows examined after |
| --- | ---: | ---: |
| Lateral prefix | 1,617 | 23 |
| Lateral cutoff, residual filter, and offset | 1,617 | 43 |
| Lateral cutoff with reverse traversal | 1,617 | 23 |
| Lateral cutoff with `IGNORE INDEX` | 1,619 | 1,619 |
| Scalar cutoff | 1,610 | 16 |

The larger benchmark used 24,576 events and 96 outer rows. Two native
in-memory Debug builds served the same loopback client, with `3fa8e313` as
the baseline. The client alternated builds over seven measured executions
after two warm-ups and checked every row against MySQL.

| Query | Before median | After median | Speedup |
| --- | ---: | ---: | ---: |
| Lateral prefix | 373.68 ms | 35.63 ms | 10.5× |
| Lateral cutoff with offset | 514.15 ms | 46.13 ms | 11.1× |
| Scalar cutoff | 504.65 ms | 33.40 ms | 15.1× |

MySQL used the `recent` index and still sorted each inner result in this
benchmark. Fsdb's [ANALYZE output](2026-11-27-explain-analyze-correlated-top-n.md)
now reports the ordered index access when execution uses it.
The [text-prefix follow-up](2026-11-27-correlated-text-index-top-n.md) adds
direct text equalities that use the index collation. The ordered suffix
still requires a supported physical index order. Coercive filters,
warning-producing expressions, and incompatible index hints use the
existing execution path.

### Constant text prefixes

An index on `(tenant,state,stamp DESC,id)` now supplies the ordered
suffix when the outer row fixes `tenant` and an equality literal
fixes the text state. For example, `e.tenant=o.tenant AND
e.state='ACTIVE'` opens the same merged lookup used for correlated
text columns. The engine checks that the comparison uses the stored
collation and applies the original predicate before OFFSET and LIMIT.
[MySQL index ordering](https://dev.mysql.com/doc/refman/8.4/en/order-by-optimization.html)

The fixture includes `Active`, `ÁCTIVE`, and `active` in the same
tenant under `utf8mb4_0900_ai_ci`. The lateral query with a cutoff,
filter, and offset examined 608 rows before the change and 22 after.
`EXPLAIN ANALYZE` reports the ordered lookup for the lateral and scalar
shapes. An explicitly different collation and `IGNORE INDEX` retain
the scan path in the fixture.

Seven wire variants and eight prepared executions matched MySQL 8.4.11.
A separate comparison covered 192 combinations of `CHAR` and
`VARCHAR`, three collations, trailing spaces, introduced literals,
ordinary and NULL-safe equality, both index directions, and index
hints. Those 192 combinations matched in both Fsdb storage modes.

On 40,000 events and 100 outer rows, the native in-memory Debug builds
were timed on loopback after the test suite finished. The client
alternated baseline `99f00146` and the updated build, taking the median
of six measured runs after one warm-up per build. Every timed result
matched MySQL.

| Query | Before | After | Speedup |
| --- | ---: | ---: | ---: |
| Lateral top-N | 3,314 ms | 41 ms | 81× |
| Scalar top-N | 3,275 ms | 46 ms | 71× |

### Collation-equal sort keys

A separate text-suffix probe uses `ORDER BY state,id LIMIT 3` with
`state` under `utf8mb4_0900_ai_ci`. With IDs 1–4 holding `active`,
`ACTIVE`, `áctive`, and `Active` respectively, MySQL's lateral query
returns IDs 1, 2, and 3. Fsdb previously returned 1, 4, and 2. A scalar
lookup at `LIMIT 1 OFFSET 1` returned ID 4 in Fsdb and ID 2 in MySQL.

The SQL sort now moves to the next key when the current collation
compares equal. The same comparison gives equivalent values the
same window rank. Physical index order retains its spelling
refinement for range and locking operations. Ordered SQL access
uses the index when that refinement cannot override a later sort
key, including binary-collated text in a correlated order suffix.

All 512 generated queries match MySQL 8.4.11, up by 32 with no
regressions. The comparison covered scans, index hints, correlated
scalar and lateral queries, window rankings, NULLs, `CHAR` and
`VARCHAR`, and four collations. The same ordered-query results matched
with durable Fsdb storage.

The same index-order check applies to `GROUP BY state,stamp`. Previously,
physical spelling order could split the `active` group for stamp 1 into
two groups with a count of 1. MySQL, the Fsdb scan, and the corrected
indexed query return one group with a count of 2. All 128 generated
grouping queries now match MySQL in both storage modes, up from 112.
They cover direct and computed keys, index hints, filters, and `HAVING`.

The binary-collated suffix switched from a scan to an ordered index
lookup on 40,000 events and 100 outer rows. Native in-memory Debug builds
returned the same rows as MySQL. The loopback client alternated baseline
`cfe8fe06` and the updated build, taking the median of six measured runs
after one warm-up per build. Validation was running on the same host.

| Query | Before | After | Index rows per loop |
| --- | ---: | ---: | ---: |
| Lateral top-N | 1,673 ms | 25 ms | 9 |
| Scalar top-N | 1,723 ms | 35 ms | 6 |

Changing only the suffix to `utf8mb4_0900_ai_ci` keeps both queries on
a scan. The same fixture matched MySQL and took about 1.7 seconds per
query in the native in-memory Debug build. Streaming these folded suffixes
would require preserving the later key order across equivalent spellings.
