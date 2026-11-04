# Correlated equality planner: November current-head check

On `e2d42407`, the maintained BenchmarkDotNet ShortRun measured the point-key
control and two correlated equality queries against 10,000 users and 50,000
orders. The host was an M2 Max running .NET 10.0.12 and MySQL 8.4.11. Fsdb
used its in-memory server; MySQL used the benchmark's durable server. Each
method had three warmups and three measured iterations. The [raw results](e2d42407-quick.md)
preserve the run details.

| Query | fsdb mean | MySQL mean | fsdb / MySQL |
|---|---:|---:|---:|
| PointSelectByPk | 359 µs | 43 µs | 8.3× |
| CorrelatedDerivedEquality | 961 µs | 234 µs | 4.1× |
| CorrelatedCteEquality | 980 µs | 239 µs | 4.1× |

The derived and CTE results are close to each other, and the fsdb derived
result remains near the 987–1,004 µs range in the [October control](2026-10-21-correlated-planner-current-check.md).
The point query still has a larger relative latency gap than either correlated
query. This run therefore does not isolate a candidate-row scan or a planner
regression. It does show a persistent absolute latency gap of roughly 0.7 ms
for both correlated shapes. Next, profile per-statement setup and projection
work with a candidate-row counter before changing access-path selection.

Run command:

```sh
FSDB_BENCH_METHODS=PointSelectByPk,CorrelatedDerivedEquality,CorrelatedCteEquality just bench-quick
```
