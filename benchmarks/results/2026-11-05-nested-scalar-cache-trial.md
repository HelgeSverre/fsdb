# Nested source-free scalar metadata cache trial

Tested on 2026-11-05 against commit `69f39a72` on an Apple M2 Max with .NET 10.0.12 and MySQL 8.4.11. Both runs used `FSDB_BENCH_METHODS=CorrelatedNestedScalarNoSourceFiltered,CorrelatedScalarNoSourceFiltered just bench-quick` with BenchmarkDotNet `ShortRun` (three warmups and three measured iterations). The trial cached successful projection metadata and binding validation by the inner and immediate outer scope identities in the statement memo. The focused SELECT-boundary test passed.

Raw results: [baseline](2026-11-05-nested-scalar-cache-baseline.md) and [trial](2026-11-05-nested-scalar-cache-trial-raw.md).

| Query | Baseline fsdb | Trial fsdb | Baseline MySQL | Trial MySQL |
| --- | ---: | ---: | ---: | ---: |
| Filtered nested scalar | 6.959 ms | 7.075 ms | 0.325 ms | 0.377 ms |
| Filtered top-level scalar (control) | 0.690 ms | 0.682 ms | 0.093 ms | 0.091 ms |

The nested fsdb result moved 1.7% slower, while the MySQL result moved 16% slower. With this short run and environmental variation, the data does not support a speedup. The trial added 77 lines to the executor and was reverted. A future attempt should profile the full nested execution path before adding another cache.

The benchmark's nested scalar projection references the immediate `orders` scope; it does not exercise a reference to the second outer `users` scope. The existing focused correctness test exercises both scopes. A new benchmark is needed before making a performance claim about two-scope bindings.
