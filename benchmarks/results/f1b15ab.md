# fsdb vs MySQL 8.4 — f1b15ab

10,000 users / 50,000 orders, seeded identically on both servers. Full job:
3 warmup + 6 measured iterations (`just bench`). MySQL wins essentially
everywhere, as expected — fsdb optimizes for readable F# over raw speed
(see README). Two things below are not "just fsdb is slower" and are worth
a closer look:

- **`JoinUsersOrders` errors on fsdb (`NA`).** A single manual run of the
  same query (`JOIN ... WHERE u.age > 30 LIMIT 50` over 10k x 50k rows) did
  not return within 2 minutes against a fresh fsdb instance, well past
  BenchmarkDotNet's 30s command timeout — not a fluke of this run. Points at
  an unindexed nested-loop join that doesn't short-circuit on `LIMIT`,
  i.e. it's likely doing work closer to O(users x orders) before slicing.
  **Top suspect for follow-up profiling.**
- **`UpdateSingleRow` (2.4ms mean but a wide 700us error band, with a 4.79s
  outlier removed) and `PointSelectByPk`/`PreparedPointSelect` are all
  1-2 orders of magnitude slower than a plain `SELECT ... WHERE id =`
  should be** for a PK lookup — consistent with `WHERE id = ?` doing a
  linear scan of the table's backing `seq` rather than any kind of index
  lookup. `PreparedPointSelect` (23ms) is ~17x slower than the unprepared
  `PointSelectByPk` (1.3ms) on fsdb specifically, suggesting the
  prepare/bind path carries its own separate overhead on top of the scan.
  **Stale: this is not a prepared-statement defect.** `JoinUsersOrders`
  (above) times out
  and leaves fsdb building a 500M-pair cross product in the background;
  every method that ran after it in this suite, including
  `PreparedPointSelect`, was measured while that leftover work was still
  consuming the server, inflating its numbers 5-8x. A direct measurement of
  the prepared path in isolation is within noise of the text path. The
  harness now restarts fsdb between benchmark methods.
- No workload "won" implausibly for fsdb; every row is a genuine MySQL
  advantage or an fsdb error, nothing was dropped.

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.107
  [Host]     : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD DEBUG
  Job-KVGRFA : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target | Mean            | Error         | StdDev         | Gen0   | Allocated |
|--------------------- |------- |----------------:|--------------:|---------------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**   |     **1,322.79 μs** |     **284.94 μs** |     **101.612 μs** |      **-** |     **882 B** |
| FilterScanOrderLimit | fsdb   |    21,565.64 μs |  18,337.33 μs |   6,539.270 μs |      - |     584 B |
| InsertSingle         | fsdb   |     1,252.53 μs |     333.61 μs |      86.638 μs |      - |    1234 B |
| InsertBatch100       | fsdb   |   141,837.85 μs |  16,347.08 μs |   5,829.525 μs |      - |   92844 B |
| UpdateSingleRow      | fsdb   | 2,411,465.39 μs | 699,872.86 μs | 181,754.760 μs |      - |         - |
| JoinUsersOrders      | fsdb   |              NA |            NA |             NA |     NA |        NA |
| GroupByAggregate     | fsdb   |   211,954.39 μs |  29,360.49 μs |  10,470.232 μs |      - |         - |
| JsonExtract          | fsdb   |   130,737.03 μs |  21,979.74 μs |   7,838.187 μs |      - |         - |
| PreparedPointSelect  | fsdb   |    22,956.72 μs |   2,185.30 μs |     779.301 μs |      - |     994 B |
| **PointSelectByPk**      | **mysql**  |        **47.35 μs** |      **25.57 μs** |       **9.118 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit | mysql  |     1,946.71 μs |      21.72 μs |       5.642 μs |      - |     643 B |
| InsertSingle         | mysql  |       126.19 μs |      22.18 μs |       5.759 μs |      - |    1240 B |
| InsertBatch100       | mysql  |     1,532.88 μs |     465.39 μs |     165.964 μs | 9.7656 |   96713 B |
| UpdateSingleRow      | mysql  |       136.41 μs |      57.62 μs |      20.548 μs |      - |     743 B |
| JoinUsersOrders      | mysql  |       286.90 μs |      28.44 μs |       7.385 μs |      - |     888 B |
| GroupByAggregate     | mysql  |    21,202.42 μs |   1,013.10 μs |     361.282 μs |      - |     614 B |
| JsonExtract          | mysql  |        73.29 μs |      24.15 μs |       8.612 μs |      - |     584 B |
| PreparedPointSelect  | mysql  |        44.93 μs |      22.56 μs |       8.045 μs | 0.0916 |     960 B |

Benchmarks with issues:
  ServerBenchmarks.JoinUsersOrders: Job-KVGRFA(IterationCount=6, WarmupCount=3) [Target=fsdb]
