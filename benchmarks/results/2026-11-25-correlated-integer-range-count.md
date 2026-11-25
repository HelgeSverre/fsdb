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

## One-sided bounds

An upper or lower bound can also count the corresponding suffix slice.
A pair of comparisons still requires one bound in each direction on the
same suffix. Repeated same-direction predicates and other residual filters
retain row evaluation. The same null exclusion applies to ascending and
descending keys, including an upper bound with no lower endpoint.

The pinned MySQL 8.4.11 wire check agreed on all 900 result rows across
ascending and descending nullable SMALLINT suffixes, composite equality
prefixes, reversed comparison operands, NULLs, out-of-domain values, and
forced or ignored indexes. The executor regression compares allocations
for equivalent upper-only and two-sided counts. Before the change, the
upper-only query allocated 361 MB on the calling thread, about 50× the
two-sided query's 7.2 MB. Both one-sided cases now pass the threefold
allocation bound against the two-sided query.

The native benchmark uses the same fixture and settings described above.
The baseline is `46b804ee`. Pass 2 reverses both the engine and workload
order. The upper-only count returns 1,500 matches and the lower-only count
returns 10,000. The two-sided count is included as a control. The values
below average the two pass medians.

| Predicate, p50 ms | Fsdb before | Fsdb after | MySQL | Fsdb speedup |
| --- | ---: | ---: | ---: | ---: |
| `c.id <= p.id + 1000` | 413.359 | 9.711 | 1.697 | 42.6× |
| `p.id <= c.id` | 40.584 | 9.012 | 1.679 | 4.5× |
| `BETWEEN` control | 7.358 | 10.134 | 1.730 | 0.7× |

The two-sided control had a lower baseline median in pass 2. A separate
two-sided-only control used 60 warmups and 60 samples per process, four
passes, and alternating engine order. Its mean p50 was 7.87 ms for the
baseline (6.99–8.66 ms across passes) and 8.62 ms for the candidate
(8.30–9.02 ms). The candidate was slower in three of four pairs. The
baseline varied enough across processes that the exact two-sided
performance effect remains uncertain.

[Per-pass p50 and p95 results](2026-11-25-correlated-one-sided-range-count.csv).
