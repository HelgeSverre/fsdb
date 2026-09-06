# Individual allocation experiments

Date: 2026-09-06. Base: `d24accdba92a5f36185e97e9388022828d47fac0`.
Machine: Apple M2 Max, arm64, macOS 15.6.
.NET SDK 10.0.400, runtime 10.0.11, Release builds.

## Findings

- Hoisting sort directions is the strongest change. UNION sorting took 34% less
  time in both process orders; ordered GROUP_CONCAT took 22–29% less time.
  All four sort cases allocated 16–34% fewer bytes. Plain SELECT and grouped
  SELECT wall times were disrupted by large scheduling/load spikes, so their
  combined timing medians are not reliable evidence of a speedup or regression.
- Lazy outer-join padding is primarily an allocation improvement: approximately
  80–224 KB fewer bytes per 2,000-row query, or 1–2.5%. Latency differences are
  small and sometimes change sign. There is no strong throughput claim.
- The row-store loop has mixed results. Constructing 10,000 rows took 12–17%
  less time in the two process orders and allocated exactly 320,096 fewer bytes.
  Database creation allocated only 1,696 fewer bytes and took 25–40% more time,
  so this change was rejected.

The sort and join changes were retained. The row-store change was not.

## Measurements

Each fixed build contains only its named production patch, relative to the
same baseline. The measurements use a temporary `--embedded` lane in the
experiment worktree's `Fsdb.Benchmarks` project. It measures parsing, planning, execution, and result
construction through the embedding API, without network overhead. It is a
timed batch runner, not a BenchmarkDotNet confidence-interval report.

Each case runs baseline/fix/fix/baseline in separate processes with identical
fixtures and warmup history: 1.5-second warmup, 250-ms batch calibration, nine
measured batches per process. The table uses the median of 18 samples per
variant. Negative time/allocation changes mean reductions. Forward/reverse
columns compare each pair of process medians separately.

| Case | Baseline ms | Fixed ms | Time change | Bytes saved/op | Allocation change | Forward / reverse time change |
|---|---:|---:|---:|---:|---:|---:|
| join-left-matched | 5.886 | 5.638 | -4.2% | 224,141 | -2.5% | -4.1% / -1.8% |
| join-left-mixed | 4.501 | 4.628 | +2.8% | 104,238 | -1.3% | +1.1% / +3.5% |
| join-right-matched | 5.446 | 5.408 | -0.7% | 189,860 | -2.1% | -1.7% / -0.5% |
| join-right-mixed | 4.754 | 4.677 | -1.6% | 80,222 | -1.0% | +3.9% / -1.1% |
| db-create | 0.158 | 0.208 | +32.2% | 1,696 | -0.4% | +39.5% / +25.0% |
| rowstore | 7.150 | 5.899 | -17.5% | 320,096 | -2.3% | -17.4% / -11.8% |
| sort-aggregate | 1.846 | 1.420 | -23.1% | 1,603,707 | -34.0% | -21.9% / -29.4% |
| sort-group | 5.711 | 5.094 | -10.8% | 1,603,843 | -16.2% | -13.2% / +62.0% |
| sort-select | 3.587 | 4.071 | +13.5% | 1,581,491 | -20.9% | -10.3% / +228.8% |
| sort-union | 9.866 | 6.483 | -34.3% | 3,711,641 | -26.6% | -34.2% / -34.2% |

Matching result checksums for 9 data-producing cases; db-create checks successful construction only.

The SQL tables each contain 2,000 unindexed rows. UNION sorts 4,000 rows.
The direct row-store case uses a prebuilt array of 10,000 rows. Outer joins
exercise both preserved sides, with all matches or approximately half matches.
The measured bytes are current-thread managed allocations; explicit GC and
fingerprinting occur outside timed batches.

Other work was active on the machine, including sustained background CPU use.
Some plain-SELECT fixed batches had severe timing spikes; grouped sorting also
had an inconsistent reverse pair. These raw samples are retained, not silently
discarded. The allocation reductions are substantially steadier than timings.
Database-creation slowdown appeared in both process orders, but the profiles
do not establish its cause. Results are workload-specific and are not a
claim about socket-level throughput or MySQL-relative performance.

## Profiles

Separate EventPipe traces use `dotnet-sampled-thread-time,gc-verbose`, with
Speedscope exports and inclusive/exclusive stack reports. On this macOS tool,
sampling estimates managed thread time; it is not Linux kernel CPU sampling.
Whole-trace stack percentages include startup, fingerprinting, and finalizer
threads, so they should not be read as benchmark-only CPU percentages.

- Baseline plain sorting has `FSharpList<Direction>` as its largest sampled
  allocation type. It falls out of the top 30 types after hoisting. Stack
  samples also identify the full-sort comparator.
- Both join traces contain `buildCombinedRows`, confirming that the measured
  joins reach the modified fallback assembly path. Result arrays, lists, and
  grouping maps remain significant allocation sources after lazy padding.
- Baseline row-store construction samples the discarded `FSharpList<RowId>`;
  that type falls out of the fixed trace's top 30. Persistent map nodes and
  boxed row identities dominate the remaining sampled allocations.
- Database-creation stacks are dominated by system-table and index construction.
  The relative contribution of `OfSeq` is similar across the two traces.
  These traces do not explain the observed wall-clock slowdown.

Allocation-tick totals are sampling estimates across complete trace runs with
different operation counts. Use the timed bytes/op table for quantitative
before/after comparisons, not raw trace-byte totals.

## Correctness

`just check` passed with all three candidate changes applied together. The
retained sort and join changes were subsequently verified on `main`.

The nine data-producing benchmark cases have matching full-result fingerprints
across independent builds. Database creation checks successful construction.

The existing relational torture scenario against its pinned MySQL 8.4.11
oracle exits 2 on `enum_aggregates_split_numeric_and_label`. The unchanged
baseline reproduces the identical failure signature:

`3e6cdfd4df91f67b2d9fbe79608d18e952d155601b7c41ce1dab04a6e585bc83`

This is a pre-existing compatibility finding, not a passing torture run.
No known-gap entry was added and the unrelated ENUM behavior was not changed.

## Reproduction and artifacts

The [local evidence archive](/Users/helge/code/fsdb/.git/experiment-archives/d24accd-allocation-experiments.tar.gz)
contains the experiment source, raw samples, profiles, and test/torture logs.
It is local Git metadata and is not included in clones. Build outputs and frozen
variant binaries are excluded. The temporary runner is not part of the main
benchmarking harness.
