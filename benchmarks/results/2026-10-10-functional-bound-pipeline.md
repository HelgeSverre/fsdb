# Functional-bound pipeline profile

At revision `b5f9a5b1`, a native Release build on .NET 10.0.401 measured the newly supported constant bound on a `SQRT(v)` functional key. Two identically seeded 10,000-row tables carry `id INT PRIMARY KEY, v INT`; only the indexed table has `KEY ix_sqrt ((SQRT(v)))`. Each query returns ID 9. `EXPLAIN` is asserted before timing: both indexed predicates use `ref` on `ix_sqrt`, while the scan twin uses `ALL`.

```sh
dotnet build src/Fsdb/Fsdb.fsproj -c Release
DOTNET_TieredCompilation=0 dotnet fsi --nologo \
  benchmarks/scripts/functional-bound-pipeline.fsx
```

The script warms each action 500 times, then records five trials of 500 calls. The table shows median per-call elapsed time and allocation from trials 2–5; [the CSV](2026-10-10-functional-bound-pipeline.csv) retains every trial.

| Predicate | Pre-parsed executor | Text handler |
|---|---:|---:|
| Indexed `SQRT(v)=3e0` | 22.95 µs / 45.0 KB | 142.17 µs / 260.8 KB |
| Indexed `SQRT(v)=SQRT(9)` | 26.03 µs / 52.0 KB | 148.06 µs / 272.1 KB |
| Scan twin `SQRT(v)=SQRT(9)` | 19.84 ms / 30.0 MB | 40.61 ms / 56.2 MB |

The constant function adds about 3 µs to the pre-parsed indexed call and 6 µs to the text-handler call, while the index avoids a much larger row-scan cost. These are in-process fsdb stages, not wire timings or a MySQL comparison. The two scan and indexed tables have the same rows but different index metadata. The measurement is useful for deciding whether this planner path deserves optimization; it does not establish deployment latency.

An attempted `IGNORE INDEX (ix_sqrt)` control exposed a separate gap. Fsdb accepts that syntax yet `EXPLAIN` still selects `ix_sqrt`; MySQL 8.4.11 reports `ALL` for the same hint. The final benchmark uses the unindexed twin instead. Index-hint execution remains open under the optimizer-hint gap.
