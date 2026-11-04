# Nested correlated EXISTS projection check

The new `CorrelatedNestedExistsOuterExpression` benchmark selects 100 users,
counts their orders, and evaluates an indexed `EXISTS` over a second orders
alias for each matching order. Its unused projection, `c.id+0`, binds to the
intermediate order scope. Unlike a top-level outer scope, that context can
have a changing ancestor binding chain.

On the M2 Max host with .NET 10.0.12 and MySQL 8.4.11, three-iteration
BenchmarkDotNet ShortRuns measured:

| Run | fsdb | MySQL |
|---|---:|---:|
| Initial baseline | 5.879 ms | 1.002 ms |
| Full-chain structural-binding cache trial | [5.345 ms](2026-11-04-nested-exists-structural-cache-trial.md) | 0.737 ms |
| Baseline replay after reverting the trial | [7.426 ms](2026-11-04-nested-exists-baseline.md) | 0.699 ms |

The baseline replay varied by more than the apparent cache improvement, so
these measurements do not support retaining the cache. A reference-identity
key trial measured 5.588 ms on fsdb and 0.704 ms on MySQL, also within the
baseline variation. Both cache variants were reverted. The benchmark remains
as a reproducible nested-scope control for a future planner change. A cache
for this path must account for the full outer binding chain and changing CTE
source bindings; a faster result alone would not establish correct reuse.
