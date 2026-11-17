# Correlated range estimate performance checkpoint

The planner change in `d77873f4` regresses this skewed correlated range
workload relative to parent `bf57ee65`. Median throughput falls 37.8% and
allocated bytes per query rise 60.4%. An equality-only control is effectively
unchanged. All timed queries returned the expected rows and counts.

| Workload | Parent queries/sec | Current queries/sec | Current / parent | Parent bytes/query | Current bytes/query |
| --- | ---: | ---: | ---: | ---: | ---: |
| Equality control | 3,782 | 3,806 | 1.007 | 661,308 | 660,608 |
| Two-index correlated range | 1,183 | 736 | 0.622 | 2,019,889 | 3,239,872 |

These are medians of three samples. Range throughput spans 1,139–1,197
queries/sec on the parent and 731–768 on current. Equality throughput spans
3,765–3,944 on the parent and 3,767–3,986 on current. The range samples do
not overlap; this is a reproducible regression in this experiment, rather
than evidence of a general FSDB throughput change.

## Workload and method

- Host: Apple M2 Max, 12 CPUs, 32 GiB memory, macOS 15.6 (24G84).
- SDK: .NET 10.0.401; Release builds; default runtime and GC settings.
- Both revisions run natively on the same host with in-memory FSDB stores.
  This is neither a durable-write benchmark nor a comparison with MySQL speed.
- Each fresh process seeds 100 outer rows alternating owner IDs 1 and 2.
  The inner table contains one row for each of those owners at `k=12.5`,
  plus 10,000 rows for owner 9 outside the range. It has indexes on owner
  and `k`.
- The range query counts matching owners with `k BETWEEN 12.5 AND 13`.
  The equality control omits that range predicate. Bounds are numeric, so
  the measured difference does not require emitting conversion warnings.
- Each process warms up with ten queries, then runs for eight seconds.
  Every query checks all 100 result counts. Timing includes parsing,
  execution, result construction, and that check; setup is excluded.
- Three repetitions per revision and workload run sequentially. Revision
  order reverses in the middle repetition. Builds finish before measurements.
  Processes are not CPU-pinned, and background host activity is uncontrolled.
- Allocation uses the process-wide `GC.GetTotalAllocatedBytes` delta across
  the timed loop. Raw query counts, elapsed times, allocations, and ordering
  are in the [sample JSON](2026-11-17-correlated-range-ref-estimate.json).

The identical [benchmark source](../CorrelatedRangeProfile/Program.fs) was
copied into separate temporary runner projects. Each project referenced its
revision's library using the `FsdbProject` property; each had separate output
directories. The parent used a detached worktree. Runner projects and the
worktree were removed after measurement.

To run a single current-source sample:

```sh
dotnet run --project benchmarks/CorrelatedRangeProfile -c Release -- range 8
dotnet run --project benchmarks/CorrelatedRangeProfile -c Release -- equality 8
```

For revision comparisons, use separate copies of the runner project and
build each with `-p:FsdbProject=/absolute/path/to/revision/src/Fsdb/Fsdb.fsproj`,
then execute their built DLLs sequentially. Do not include build or setup
wall time as query throughput.

## Follow-up

The new planner compares estimated ref work with a range and retains initial
and recheck analysis phases. This workload now takes a range candidate path
where the parent accepted actual one-row owner buckets. The experiment
isolates the commit's aggregate effect; it does not isolate allocation or
time by function. Profile repeated planning and candidate preparation before
choosing an optimization. Preserve the MySQL diagnostic regressions and the
existing broad-candidate intersection behavior when reducing this cost.

A subsequent [allocation profile and numeric-probe experiment](2026-11-17-correlated-range-numeric-probe.md)
reduced range allocations by 12.6% by avoiding warning scopes around numeric
conversions that cannot warn. This recovers part of the regression; it does
not establish full recovery to the parent revision measured here.
