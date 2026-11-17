# Numeric DOUBLE range eligibility: corrected benchmark coverage

Exact numeric DOUBLE bounds now form a storage range. This exposes a higher
cost on the correlated benchmark: median range throughput drops from 972 to
581 queries/sec and allocations rise from 2.61 MB to 4.11 MB per query.
The equality-control median is unchanged. This is a remaining performance
regression, not a speed improvement.

## Why the interpretation changed

An execution trace showed that the preceding numeric-literal benchmark
never reached the selected-range branch. Its bounds were DECIMAL 12.5 and
integer 13; generic exact-probe coercion rejected the different DOUBLE Value
case even though both values are exactly representable. A failing storage
regression reproduced the missing range directly.

The original performance checkpoint measured unsuccessful range planning
followed by equality fallback. The rejected selection-reuse experiment also
did not execute its modified recheck branch. Those reports now state this
coverage limitation. In particular, that experiment cannot rule out selection
reuse as an optimization for a genuinely eligible range.

The [oracle investigation](../../torture/findings/2026-11-17-numeric-double-range-bounds.md)
confirmed eligibility on MySQL 8.4.11. FORCE INDEX(ix_k) uses a two-row range;
the ordinary choice switches from owner ref to range after ANALYZE TABLE.
All result counts match and warnings remain zero. This also shows why a
static claim about MySQL's ordinary plan requires recording statistics state.

## Matched measurement

Before: `320d58af`. After: exact numeric DOUBLE range-bound normalization.
The same host, Release builds, data, warmup, alternating revision order,
and three eight-second samples per workload/revision from the
[benchmark protocol](2026-11-17-correlated-range-ref-estimate.md) were used.
All result invariants passed. Raw samples are in the
[measurement JSON](2026-11-17-correlated-range-numeric-bounds.json).

| Workload | Before median queries/sec | After median queries/sec | Before bytes/query | After bytes/query |
| --- | ---: | ---: | ---: | ---: |
| Equality control | 4,155 | 4,160 | 644,839 | 644,366 |
| Range | 972 | 581 | 2,608,871 | 4,111,715 |

Range throughput samples span 917–979 before and 566–591 after. All after
samples are slower. Median throughput falls 40.2% and allocation rises 57.6%.
These results compare failed planning/fallback with an eligible range path;
they do not isolate time or bytes to a single planning function. Candidate
preparation, repeated binding, and the now-reachable recheck are next targets.
The failed selection-reuse experiment should be repeated only with branch
coverage confirmed on this eligible workload.

The baseline was built in a detached worktree through its canonical
`/private/tmp` path after a fresh restore; both runner processes were verified
before timed samples. The temporary worktree and probe files were cleaned up.

Validation: the full correctness gate passed all 3,954 tests. An expanded
focused storage test also passed exact signed/unsigned/DECIMAL boundaries
and conservative fallback for 2^53+1 and DECIMAL 0.1. No release-readiness
claim follows from this checkpoint; the performance cost remains open.

A subsequent [covered selection-reuse experiment](2026-11-17-eligible-range-reuse.md)
reduced eligible-range allocations by 23.7% and improved median throughput
33.8% against this corrected source revision. This recovers part of the
successful-range execution cost, with both diagnostic phases retained.
