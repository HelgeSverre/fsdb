# Correlated range diagnostic boundaries

Oracle: digest-pinned MySQL 8.4.11 in `torture/compose.yaml`.
FSDB baseline: `104ee2a4`. All result rows matched in these controls.

The [SQL replay](2026-11-17-correlated-range-boundaries.sql) creates a disposable
database with three outer rows and 102 inner rows, two in the truncated DOUBLE
range. Controls run in order: the owner index remains present for the empty
inner and empty outer cases. Warning counts are read immediately after each
query. Use the replay only with a disposable oracle or an empty FSDB store.

| Control | MySQL warnings | Baseline FSDB warnings |
| --- | ---: | ---: |
| Indexed range, no owner index | 14 | 14 |
| Outer LIMIT 0 | 0 | 12 |
| Scalar subquery LIMIT 0 | 0 | 0 |
| No outer rows match filter | 2 | 2 |
| Add owner index, ordinary plan | 16 | 6 |
| Both indexes, FORCE INDEX(ix_k) | 14 | 6 |
| Both indexes, FORCE INDEX(ix_owner) | 6 | 6 |
| Empty inner, FORCE INDEX(ix_k) | 2 | 0 |
| Empty inner, ordinary plan | 4 | 0 |
| Empty both tables, ordinary plan | 4 | 0 |

The outer LIMIT 0 regression reproduced twelve warnings instead of zero.
Its row-dependent metadata traversal used `withMetadataProbe` without
diagnostic suppression. It now uses `withSourceMetadataProbe`, preserving
metadata inspection while suppressing diagnostics and variable assignments.

The remaining owner-index and empty-inner mismatches are open. In the
nonempty two-index fixture, MySQL EXPLAIN chooses a range on `ix_k` both for
the ordinary plan and for FORCE INDEX(ix_k), with `Using index condition;
Using where`. Their warning counts still differ (sixteen versus fourteen),
so the selected execution index alone does not describe all setup conversion
diagnostics. FSDB currently prioritizes the stored owner equality lookup.
The forced-range result showed that index hints did not constrain that
correlated equality choice. The next fix must distinguish optimizer setup
diagnostics from execution-candidate diagnostics rather than infer both from
the final row set.

## Forced-index correction

Correlated stored equality probes now filter index matches using the table's
JOIN hint policy. Their single-column fallback also chooses only allowed
indexes, and USE/FORCE hints prevent substituting a transient equality lookup.
With both indexes present, FORCE INDEX(ix_k) now returns the same rows and
fourteen warnings as MySQL; FORCE INDEX(ix_owner) preserves the same rows and
six warnings. Both are covered by the diagnostics regression. The ordinary
two-index plan and ordinary empty-inner cases remain open; the baseline table above
records observations before this correction.

## Broad and empty forced ranges

The forced empty-inner control already matches MySQL after the equality-hint
correction: three zero counts and two setup warnings. It is now a regression.

A broader pinned-oracle control forces `ix_k` with bounds `'0x'` and `'1000x'`
before adding the owner index. All 102 inner rows are range candidates.
MySQL EXPLAIN selects `range` on `ix_k`, and the statement reports 614 warnings:
two during setup and two per candidate for each of three outer rows.
FSDB at `bfc7a80b` returned the same counts but twelve warnings.

The projected range selector now honors FORCE INDEX independently of its
cost threshold, while retaining hint filtering and storage validation. This
matches the existing forced-membership policy. The broad forced range is in
the SQL replay and regression; the ordinary two-index optimizer setup counts
remain a separate open issue.

## Ordinary two-index optimizer trace

Fresh pinned-oracle controls distinguish setup from execution:

| Available indexes | Three outer rows | One outer row | Zero outer rows |
| --- | ---: | ---: | ---: |
| ix_k | 14 | 6 | 2 |
| ix_k and ix_owner | 16 | 8 | 4 |

The [two-index trace](2026-11-17-correlated-range-optimizer-trace.json)
records an owner `ref` estimate of 34 rows with cost 4.15, compared with a
range estimate of two rows and final access cost 1.16. It selects `ix_k`.
The owner estimate agrees with the uniform average of 102 rows over three
owner keys. FSDB's correlated shortcut instead probes the actual bound owner
and immediately accepts a bucket with one or two rows. Thus EXPLAIN-like
static range costing and per-row execution choice can disagree.

The two-index trace contains two range analyses: one under `rows_estimation`
and one under `attaching_conditions_to_tables` →
`attached_conditions_computation` → `rechecking_index_usage`. Both choose
the same `12.5 <= k <= 13` range. The
[single-index trace](2026-11-17-correlated-single-range-optimizer-trace.json)
has only the first analysis. Combined with the zero-outer-row counts, this
supports the inference that the second analysis accounts for the extra pair
of setup conversion conditions; the traces do not directly label warnings.

The next implementation needs to compare statement-level estimated ref and
range work, preserve the chosen access into execution, and represent the
range recheck as a distinct setup phase. Adding two warnings merely because
another index exists would not model this evidence.

To collect these traces, enable `optimizer_trace`, execute the scalar query
from the replay immediately after the relevant index setup, then read
`TRACE` from `information_schema.optimizer_trace` immediately afterward.
Do not insert `SHOW COUNT(*) WARNINGS`: that is expanded to a SELECT and
replaces the trace with the warning-count query's trace. Warning cardinality
controls were collected separately with tracing disabled.

## Estimated ref versus range correction

The correlated chooser now compares a full-key stored equality estimate,
using the existing uniform-cardinality estimator, with the eligible physical
range count before accepting an actual one- or two-row owner bucket. When
the range wins, it rechecks the range and retains both analyses as explicit
`EstimateRange` and `RecheckRange` phases. Deferred setup preserves both
phases when no outer rows execute. Range rows are materialized only after
the estimated comparison selects the range.
When the existing planner marks both equality and range candidate sets broad
enough to probe their intersection, that path retains priority. A full-suite
regression caught the initial range-only choice evaluating 402 residuals
instead of keeping the intersection's residual work below twenty.

The original regression reproduced six warnings instead of sixteen. Its
focused rerun passed the 16/8/4 ordinary two-index matrix, the forced-index
controls, and the four setup warnings with an empty inner table. The full
gate also covers empty outer and inner tables together. These observations
close the recorded fixture's ordinary-index mismatch; partial composite
equality keys, specialized intersections, and broader optimizer cost parity
remain outside this verified scope.
