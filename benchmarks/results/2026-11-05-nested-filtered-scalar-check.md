# Nested filtered source-free scalar check

The `CorrelatedNestedScalarNoSourceFiltered` control counts orders for 100 users. Its nested source-free scalar projection binds both the immediate order row and the ancestor user row; its predicate binds the order row. This covers a scope that the earlier top-level validated-plan cache intentionally excludes. BenchmarkDotNet ShortRun used the 10,000-user/50,000-order fixture on the M2 Max host, .NET 10.0.12, and MySQL 8.4.11. Raw [before](2026-11-05-nested-filtered-scalar-before.md) and [after](2026-11-05-nested-filtered-scalar-after.md) reports include the matched top-level control.

| Query | fsdb before | fsdb after | MySQL before | MySQL after |
|---|---:|---:|---:|---:|
| Top-level filtered scalar | 0.597 ms | 0.726 ms | 0.092 ms | 0.089 ms |
| Nested filtered scalar | 10.188 ms | 5.344 ms | 0.312 ms | 0.320 ms |

The nested shortcut validates the projection and predicate for each row, then evaluates them in the current context. It does not reuse metadata across changing ancestor scopes. The nested control improved by about 47.5%; the top-level difference is within the small ShortRun samples and is not attributed to this change. The nested route is still much slower than MySQL, so binding and dispatch work remain open.
