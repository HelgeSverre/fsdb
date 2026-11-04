# Memoizing unsuccessful physical projection discovery

A correlated query over a computed derived source cannot use a direct physical
projection. The planner previously rediscovered that fact for each outer row;
its statement-local memo retained successful projections only. The memo now
stores `None` as well, so the same source node is classified once per
statement.

The maintained benchmark case selects 100 outer users and counts matching
rows from `(SELECT user_id+0 AS user_id FROM orders WHERE id <= 1000)`. On the
same M2 Max host with .NET 10.0.12 and MySQL 8.4.11, a three-iteration
BenchmarkDotNet ShortRun measured:

| Engine | Before | After |
|---|---:|---:|
| fsdb | 4.680 ms | 3.706 ms |
| MySQL control | 13.115 ms | 12.613 ms |

The [before](2ebf0d0a-before-projection-miss-cache.md) and
[after](2ebf0d0a-after-projection-miss-cache.md) results were run against
the same commit with only the uncommitted memo change between them. This is a
single matched ShortRun; its small sample does not establish a stable
percentage improvement. The result supports avoiding repeated unsuccessful
classification for this shape. `just check` passed 3,870 tests, including
computed-derived and CTE query cases, with no build warnings.

The memo remains statement-local and uses the source AST node as its key.
Broader query setup and correlated row costs remain open.
