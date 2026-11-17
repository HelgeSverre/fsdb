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
The forced-range result also shows that index hints need to constrain that
correlated equality choice. The next fix must distinguish optimizer setup
diagnostics from execution-candidate diagnostics rather than infer both from
the final row set.
