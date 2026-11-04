# Indexed correlated EXISTS fast path

The 100-row correlated equality profile exposed a different path for `EXISTS`
than for scalar `COUNT(*)`. Both queries probe `orders.user_id = users.id` on a
secondary index. Before this change, fsdb's plain `EXISTS(SELECT 1 ...)`
attempted statement-local decorrelation first and grouped the full 50,000-row
inner table. The COUNT query used a complete stored equality key per outer row.

The matched [before](2026-11-04-correlated-exists-before.md) and
[after](2026-11-04-correlated-exists-after.md) ShortRuns used 10,000 users,
50,000 orders, 100 selected outer users, the same host and MySQL 8.4.11, and
three measured iterations after three warmups per method. The raw artifact
headers share a commit SHA because the implementation was measured in the
working tree before committing; the named files preserve both versions.

| Query | fsdb before | fsdb after | MySQL before | MySQL after |
|---|---:|---:|---:|---:|
| Outer only | 337 µs | 378 µs | 74 µs | 74 µs |
| Source-free correlated scalar | 1,131 µs | 1,269 µs | 77 µs | 79 µs |
| Indexed correlated EXISTS | 8,926 µs | 600 µs | 170 µs | 171 µs |
| Indexed correlated COUNT | 645 µs | 705 µs | 210 µs | 207 µs |
| Derived correlated COUNT | 636 µs | 593 µs | 214 µs | 210 µs |

The `EXISTS` mean fell about 14.9×. Its increment over the outer-only control
fell from about 8,589 to 222 µs. This is a strong same-workload improvement,
not a universal speedup claim: the after run's fsdb controls drifted upward,
and each ShortRun has only three measured iterations. The source-free scalar
subquery still adds roughly 0.9 ms over its outer-only control, so subquery
dispatch and projection remain separate targets for profiling.

The fast path accepts only a simple, ungrouped, unlimited `EXISTS` with a
plain `SELECT 1` or `SELECT *` projection and a physical or pass-through
source whose correlated equality is represented by a complete stored key.
The existing execution and decorrelation paths handle other shapes. A focused
regression covers matched and missing keys, `LIMIT 0`, and aggregate `EXISTS`
semantics.

```sh
FSDB_BENCH_METHODS=CorrelatedEqualityOuterOnly,CorrelatedScalarNoSource,CorrelatedPhysicalExists,CorrelatedPhysicalEquality,CorrelatedDerivedEquality just bench-quick
```
