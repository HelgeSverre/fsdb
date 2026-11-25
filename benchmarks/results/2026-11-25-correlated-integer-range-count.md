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
The same null exclusion applies to ascending and descending keys,
including an upper bound with no lower endpoint.

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

## Intersecting bounds

A correlated `COUNT(*)` can count a single ordered slice when several
comparisons or `BETWEEN` predicates constrain the same signed-integer
suffix after the equality prefix. The intersection uses the highest
lower bound and lowest upper bound. A strict bound wins when endpoints
tie. Predicates the slice cannot represent keep the ordinary row
evaluation path.

The pinned MySQL 8.4.11 wire comparison matched 640 counts across the
unhinted, forced ascending, forced descending, and ignored-index cases.
The fixture includes equal and contradictory endpoints, nullable columns
and outer bounds, out-of-domain values, mixed operand directions,
overlapping `BETWEEN` predicates, and residual filters.

The allocation regression uses 250 parents and 5,000 children. Before
the change the repeated-upper-bound count allocated 365 MB on the calling
thread, against 7.2 MB for the existing `BETWEEN` control. A warmed Debug
probe of the updated executor measured 7.5 MB for repeated upper bounds,
7.2 MB for repeated lower bounds, and 8.6 MB for a narrowed `BETWEEN`
interval. The updated `BETWEEN` control used 7.1 MB.

## Filtering narrowed candidates

In a correlated `COUNT(*)` with more predicates than one index covers,
the executor can use a signed-integer equality prefix and a range suffix
to narrow the candidates. It picks the eligible slice with the fewest
entries and evaluates the original filter against those rows. An exact
slice count still applies when the chosen index covers every conjunct.
The narrower access requires signed-integer comparisons or null tests
whose skipped evaluations cannot produce conversion warnings or side
effects. Other predicate shapes retain the existing planning path.

The pinned MySQL 8.4.11 wire comparison matched 600 counts. It covered
ascending and descending suffixes, a competing index with a longer
equality prefix, forced and ignored indexes, nullable operands, empty
intervals, and fallback filters with text or fractional operands.

On the 250-parent, 5,000-child fixture, a `bucket=1` residual allocated
293 MB per query in the original Debug regression. The updated warmed
executor probe measured 14.1 MB. Residual inequality, null, and
column-to-column comparison cases used 13.3–16.8 MB. The fully covered
control used 7.3 MB.

## Counting nullable columns

`COUNT(column)` now uses the same equality-prefix and integer-range
slice when the argument is a direct column on the physical inner table.
The executor reads the argument from each qualifying row and skips
SQL NULLs. Residual filters still run before counting, and bare names
bound to routine variables use ordinary evaluation.

The native MySQL 8.4.11 check compared 1,800 result values and two
binding errors. It covered forced and ignored indexes, ascending and
descending nullable suffixes, NULL and out-of-domain bounds, duplicate
suffix values, several counted column types, and JSON null versus SQL
NULL. Separate procedure controls covered shadowed count arguments and
range predicates.

On the existing 250-parent, 5,000-child fixture, the baseline allocated
307 MB on the calling thread for one `COUNT(c.bucket)` range query.
The regression bounds allocation for qualified and bare arguments,
one- and two-sided ranges, and residual filters relative to the
indexed `COUNT(*)` control.

The native benchmark uses 500 parents and 10,000 children. The child
index is `(parent_id, id)`. A nullable `bucket` is NULL for every
fifth row and otherwise holds `MOD(id,3)`. Each lane verifies its
result on every execution, with four warmups and 15 timed samples
after preflight. The two passes reverse the engine and workload order
on the Apple M2 Max. Both Fsdb builds use Release .NET 10.0.12 with WAL
and Server GC. MySQL 8.4.11 uses a 256 MiB InnoDB buffer pool, flush at
commit, and binary logging disabled. The Fsdb baseline is `78ec82a6`.

| Workload, p50 ms | Fsdb before | Fsdb after | MySQL |
| --- | ---: | ---: | ---: |
| `COUNT(*)` control | 7.187 | 7.770 | 1.643 |
| `COUNT(c.bucket)` | 331.003 | 7.797 | 2.394 |
| `COUNT(c.bucket)` with `c.bucket<>1` | 336.963 | 14.653 | 2.420 |

The nullable count improved 42× and the filtered count improved 23×.
Fsdb took 3.3× and 6.1× MySQL's median time on those lanes. The
`COUNT(*)` baseline varied between 3.8 and 10.6 ms across passes, so
this run gives no firm estimate of change on that control. [Per-pass p50 and p95 results](2026-11-25-correlated-nullable-count.csv).

Run the checked fixture against a disposable loopback server using
the [peer benchmark script](../scripts/native-feature-peers.fsx).

```sh
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3391 1 nullable-count-controls
```

## Counting expressions and DISTINCT values

Plain correlated `COUNT` subqueries can feed the composite slice into
the ordinary aggregate evaluator. This covers direct `DISTINCT` columns
and audited integer expressions, including `CASE`, `NULLIF`, and
arithmetic on signed 32-bit operands. The evaluator still applies the
original predicate and handles expression results, SQL NULLs, and
distinct-value collation. Expressions with possible warnings, errors,
or side effects use the existing access path.

The native MySQL 8.4.11 comparison matched all 4,680 count values and
two binding errors. It tested ascending and descending indexes, forced
and ignored indexes, NULLs, duplicate values and collations, and both
covered and residual filters. Fsdb retained its previous warning
and user-variable behavior on the separate fallback controls.

The conditional count allocated 311 MB on the 250-parent, 5,000-child
regression fixture before the change. The regression bounds allocations
relative to the indexed `COUNT(*)` control for conditional expressions,
arithmetic, and direct and conditional `DISTINCT` counts.

The benchmark uses the same 500-parent, 10,000-child fixture and
durability settings as the nullable-column run. The baseline is
`a9fc8a01`. Two native passes reverse engine and workload order.
Each workload verifies its result before timing, then takes 15
measurements after four warmups. The table averages the two medians.

| Workload, p50 ms | Fsdb before | Fsdb after | MySQL 8.4.11 |
| --- | ---: | ---: | ---: |
| `COUNT(*)` control | 6.138 | 5.851 | 1.636 |
| `COUNT(CASE WHEN c.bucket<>1 THEN c.bucket END)` | 324.187 | 32.970 | 2.396 |
| `COUNT(DISTINCT c.bucket)` | 323.476 | 29.431 | 2.415 |
| `COUNT(DISTINCT CASE WHEN c.bucket<>1 THEN c.bucket END)` | 324.605 | 29.722 | 2.426 |

The expression counts improved 9.8–11.0× and took 12–14× MySQL's
median time. The conditional count varied from 19.2 to 46.7 ms
between candidate passes, and the indexed control also varied on
the shared host. [Per-pass p50 and p95 results](2026-11-25-correlated-expression-count.csv).

```sh
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3391 1 expression-count-controls
```

## Exact numeric aggregates

Plain correlated `SUM`, `AVG`, `MIN`, and `MAX` can use the composite
slice for direct integer columns and audited integer expressions.
Direct fixed-scale decimal values can use it for extrema. Decimal
`SUM` and `AVG` use it through precision 19, which fits the exact
accumulator across an int-sized row set. The unchanged aggregate
evaluator handles NULLs, DISTINCT, and result types. Approximate values
and expressions with possible warnings or errors use the existing path.

The native MySQL 8.4.11 check covered 974 queries and binding errors
with forced, ignored, ascending, and descending indexes. Fsdb preserved
its baseline values, wire type codes, and diagnostics throughout.
SUM and the extrema matched MySQL numerically. The 490 AVG values
that differed matched after rounding to MySQL's reported scale.
Some MIN and MAX result columns still advertise BIGINT on Fsdb
where MySQL advertises INT. Separate fallback cases with approximate
values, wider sums, conversion warnings, and overflow preserved
their baseline results and diagnostics.

On the 250-parent, 5,000-child allocation fixture, the original SUM
allocated 307 MB per query on the calling thread. The regression bounds
allocation for SUM, AVG, MIN, MAX, and exact numeric DISTINCT relative
to the indexed count control.

The benchmark uses the same 500-parent, 10,000-child fixture and native
durability settings. The baseline is `b6620a41`. The two passes reverse
engine and workload order. Each lane has one preflight, four warmups,
and 15 measured calls. The table averages the two per-pass medians.

| Workload, p50 ms | Fsdb before | Fsdb after | MySQL 8.4.11 |
| --- | ---: | ---: | ---: |
| `COUNT(*)` control | 6.193 | 5.787 | 1.638 |
| `SUM(CASE WHEN c.bucket<>1 THEN c.bucket END)` | 325.311 | 29.955 | 2.409 |
| `AVG(c.bucket)` | 329.110 | 29.847 | 2.388 |
| `MIN(c.bucket+1)` | 320.746 | 28.775 | 2.388 |
| `MAX(DISTINCT c.bucket)` | 321.832 | 29.039 | 2.400 |

The four aggregate workloads each improved about 11×. Fsdb took
12–13× MySQL's median time on this fixture. The Fsdb candidate medians
varied from 17.5 to 40.5 ms across workloads and passes.
[Per-pass p50 and p95 results](2026-11-25-correlated-exact-aggregates.csv).

```sh
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3391 1 exact-aggregate-controls
```
