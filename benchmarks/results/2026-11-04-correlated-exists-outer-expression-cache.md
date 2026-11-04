# Outer-bound projection validation in indexed correlated EXISTS

The indexed correlated `EXISTS` path ignores the projected value but validates
its bindings. For an unused expression such as `u.id+0`, the binding shape
is stable across rows of one top-level outer scope. The statement memo now
stores successful validation under the identity of that scope's column and
qualifier maps. Nested outer scopes and failed validation retain per-row
validation. The existing inner-only cache remains the first lookup.

The [before](2026-11-04-correlated-exists-outer-expression-before.md) and
[after](2026-11-04-correlated-exists-outer-expression-after.md) ShortRuns used
the same M2 Max host, 10,000 users, 50,000 orders, and native MySQL 8.4.11.
The after run measures the final cache ordering; the artifacts' SHA headers
identify the committed base plus the benchmark and engine edits.

| Query | fsdb before | fsdb after | MySQL before | MySQL after |
|---|---:|---:|---:|---:|
| Inner expression `c.id+0` | 627 µs | 583 µs | 179 µs | 182 µs |
| Outer expression `u.id+0` | 1,130 µs | 641 µs | 173 µs | 171 µs |

The outer-expression query is about 1.8× faster in these short runs. The
three-iteration intervals are wide, and the remaining gap against MySQL is
about 3.7×. The focused regression covers matching and nonmatching outer
rows, inner and outer expression projections, indexed probes, and invalid
inner-column diagnostics. The full suite passed all 3,875 tests.

```sh
FSDB_BENCH_METHODS=CorrelatedPhysicalExistsExpression,CorrelatedPhysicalExistsOuterExpression just bench-quick
```
