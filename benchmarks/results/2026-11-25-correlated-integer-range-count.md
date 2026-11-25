# Counting correlated integer ranges from a composite index

The [native peer comparison](2026-11-25-native-feature-peers.md#correlated-range-controls)
identified a scalar count that spent much of its time probing a broad
primary-key range. The child table also had an index on `(parent_id, id)`.
The executor now counts a single slice of that index when exact
signed-integer equality predicates cover the leading key parts and an
exact two-sided range covers the next key part. The slice count supplies
the aggregate when those predicates cover the entire inner filter.
Index hints, unsupported bound types, residual predicates, nonphysical
sources, and locking-read row restrictions still govern eligibility.

The pinned MySQL 8.4.11 oracle verified inclusive and exclusive boundaries,
reversed intervals, empty matches, and NULL handling. A descending suffix
with duplicate values returned counts 3, 1, and 0 for three parents.
The Fsdb regression also checks that a broad competing primary-key range
does not inflate allocations. Before the change the unhinted query
allocated 287.6 MB and the forced child-index query allocated 56.4 MB on
the test thread, failing the 3× ratio guard.

## Native benchmark

The same [range-controls fixture](../scripts/native-feature-peers.fsx) uses
500 parent rows and 10,000 child rows. Every execution verifies a total of
1,500 matches. Two passes reverse server order on the Apple M2 Max host.
Each pass starts a disposable native server, runs one client per server,
and samples 15 timed operations following one preflight and four warmups.
Both Fsdb binaries use Release .NET 10.0.12, Server GC, and WAL. The
baseline is `a98b726b`. MySQL 8.4.11 uses a 256 MiB InnoDB buffer pool,
normal commit flush, and binary logging disabled. The host was shared
and under memory pressure. The table averages the two pass medians.

| Predicate or hint, p50 ms | Fsdb before | Fsdb after | MySQL |
| --- | ---: | ---: | ---: |
| Unhinted `BETWEEN` | 398.854 | 12.071 | 1.784 |
| `FORCE INDEX (ix_child)` | 27.525 | 12.186 | 1.747 |
| `IGNORE INDEX (PRIMARY)` | 30.473 | 12.651 | 1.776 |
| Unhinted `>=` and `<=` | 347.444 | 11.070 | 1.983 |

The unhinted query improved 33× and was 6.8× slower than MySQL in this
run. The hinted controls improved as well because the ordered slice
counts only the matching interval inside the equality prefix.
[Per-pass p50 and p95 results](2026-11-25-correlated-integer-range-count.csv).
