# Indexed-key traversal reduces correlated range allocations

Direct traversal of indexed-key lists reduces median allocation by 11.6%
in the audited correlated range workload. Median throughput rises 9.0% in
this matched local comparison; timing variation makes allocation the stronger
result. This does not establish MySQL performance parity.

## Attribution and change

A twelve-second `gc-verbose` trace of baseline `8fadf98d` recorded 202,814
allocation ticks, representing approximately 21.62 GB of sampled allocation.
`Storage.compareIndexedKeys` was the nearest recorded engine frame for
approximately 2.44 GB, or 11.3%. The trace includes setup and warmup as well
as timed queries, and inlining can affect nearest-frame attribution.

The comparator constructed a zipped collation/direction list and a second
zipped value list for every comparison. The replacement walks the existing
lists directly, retaining the first nonzero value comparison while checking
all component widths. Per-value collation, NULL/numeric ordering, direction,
and row-identity tie-breaking are unchanged. The same value comparator runs
only until the first differing component, as before.

The [before allocation summary](2026-11-17-index-key-walk-allocation-before.json)
and [after summary](2026-11-17-index-key-walk-allocation-after.json) use the
existing TraceEvent 3.1.23 reader. The comparator drops out of the retained
top-40 nearest-frame ranking after the change. Whole-trace byte totals are
not directly comparable performance measurements: query counts and profiler
overhead differ. The uninstrumented allocated-byte samples below measure
the effect per query.

## Matched measurement

The [unchanged runner](../CorrelatedRangeProfile/Program.fs) seeds 100 outer
rows alternating owner IDs 1 and 2. The indexed inner table contains one row
for each owner at `k=12.5`, plus 10,000 out-of-range rows for owner 9. Every
query checks all 100 counts. The range query adds `k BETWEEN 12.5 AND 13`;
the equality control omits that predicate.

Both versions use native in-memory FSDB stores on the same Apple M2 Max
host, with 12 logical CPUs, 32 GiB memory, macOS 15.6, and .NET SDK 10.0.401.
This is a read-query comparison between FSDB revisions, not a durable-write
or MySQL speed comparison. No runtime/GC overrides are added by the harness.

Baseline and candidate Release output directories are copied separately.
Both builds and the regression suite finish before timing. Each fresh process
performs ten warmup queries and one eight-second sample. Three repetitions
alternate baseline/candidate order, reversing the order in the middle
repetition. Equality runs before range in each repetition. Samples run
sequentially without a profiler attached. Allocation is the process-wide
`GC.GetTotalAllocatedBytes` delta. The [raw samples and medians](2026-11-17-index-key-walk.json)
retain execution order, counts, elapsed times, and byte totals.

| Workload | Before queries/sec | After queries/sec | Before bytes/query | After bytes/query |
| --- | ---: | ---: | ---: | ---: |
| Equality control | 3,550 | 3,672 | 646,333 | 646,786 |
| Eligible correlated range | 615 | 670 | 3,139,419 | 2,774,639 |

All candidate range samples exceed the baseline range samples in this run:
641–695 versus 586–624 queries/sec. Equality timing varies substantially,
with overlapping ranges of 3,265–3,966 before and 3,491–3,776 after; equality
allocation differs by about 0.1%. Host activity and CPU frequency are
uncontrolled. Treat the speed increase as a local observation, and do not
compare these absolute rates directly with earlier benchmark sessions.

## Validation and follow-up

`just check` passes all 3,957 tests, including indexed/scanned ordering,
composite prefixes and directions, NULLs, collations, and correlated range
diagnostics. The zero-duration correctness probes and every timed query pass
the runner's result invariants. `just package-check` passes isolated package
consumption. Temporary traces, converted trace databases, and copied binaries
are removed after retaining the summaries.

Correlated source preparation, count evaluation, and evaluation-context
construction remain prominent in the candidate allocation profile. The
range workload still allocates about 2.77 MB per query; broader planner and
context costs remain open.
