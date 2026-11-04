# Indexed correlated EXISTS with a qualified column projection

The existing indexed correlated `EXISTS` shortcut accepted `SELECT 1` and
`SELECT *`, but `EXISTS(SELECT c.id FROM orders c WHERE c.user_id=u.id)`
fell through to broader subquery processing even though EXISTS does not
consume the projected value. The shortcut now accepts a qualified source
column after confirming that the qualifier and column exist in the inner
source. Invalid projections retain the ordinary path and error 1054. MySQL
8.4.11 confirmed matching true/false values and the invalid-column error;
the focused fsdb regression pins both cases.

The [before](2026-11-04-correlated-exists-column-before.md) and
[after](2026-11-04-correlated-exists-column-after.md) artifacts are three-
iteration ShortRuns on the same M2 Max host and 10,000-user, 50,000-order
dataset. The before artifact uses committed `127ecd62` plus the benchmark
method; the after artifact also includes the uncommitted engine change.
Their SHA headers identify the committed base, not the candidate diff.

| Query | fsdb before | fsdb after | MySQL before | MySQL after |
|---|---:|---:|---:|---:|
| Outer-only control | 361 µs | 386 µs | 74 µs | 77 µs |
| `EXISTS(SELECT 1 ...)` | 675 µs | 646 µs | 171 µs | 171 µs |
| `EXISTS(SELECT c.id ...)` | 4,000 µs | 720 µs | 178 µs | 176 µs |

The qualified-column query fell about 5.6× and is now close to the existing
literal-projection path. The remaining whole-query latency gap versus MySQL
includes fixed query handling and other row work; this result does not
establish general correlated-query parity.

```sh
FSDB_BENCH_METHODS=CorrelatedEqualityOuterOnly,CorrelatedPhysicalExists,CorrelatedPhysicalExistsColumn just bench-quick
```
