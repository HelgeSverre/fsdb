# Materialized correlated integer-expression lookup

The [reproducible harness](../../torture/scripts/correlated-materialized-integer-benchmark.fsx)
runs `ABS(d.k)+0=o.x` against a `DISTINCT`-materialized 1,000-row inner
source for 100 outer rows. It checks every returned count and uses a
`TOUCH(d.id)=d.id` residual to count rows reaching the predicate. Each trial
executes a fresh SQL statement on the same in-memory connection, so the
statement-local key is rebuilt each time.

On the same arm64 macOS host with .NET SDK 10.0.401, the baseline commit
`5fffd493` and the candidate were each built in Debug and run three times:

| Build | Trial 1 | Trial 2 | Trial 3 | Residual calls per trial |
| --- | ---: | ---: | ---: | ---: |
| Baseline | 1,214 ms | 959 ms | 813 ms | 101,101 |
| Integer-expression lookup | 187 ms | 19 ms | 19 ms | 201 |

The first trial includes runtime warm-up and should not be read as an isolated
query-planner cost. The later trials show an approximately 43–50× reduction
in elapsed time for this in-memory shape. This is a local microbenchmark, not
a wire-throughput or general optimizer claim. A native MySQL 8.4.11 result
probe agrees on the focused signed, NULL, reversed-equality, arithmetic, OR,
and DECIMAL-bound cases; the decimal comparison retains the scan path because
its coercion semantics are not covered by the integer key.
