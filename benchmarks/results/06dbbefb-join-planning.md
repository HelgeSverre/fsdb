# Join-hint planning comparison

Measured on 2026-10-08, arm64 macOS, .NET SDK 10.0.401, Debug assemblies.
Before is `06dbbefb`; after is the bound-query join-hint planning implementation
based on that revision, including the empty-scope fast path and scalar-column
metadata correction. The baseline is built from a disposable archive of the
commit. Both processes use `DOTNET_PROCESSOR_COUNT=8` and
`DOTNET_GCHeapHardLimit=0x100000000`.

Command: `dotnet fsi --nologo benchmarks/scripts/hint-handling.fsx`.
The current harness runs against both assemblies, serially in before/after/
after/before order after builds and validation have completed. Each invocation
warms each query 200 times, then measures five samples of 2,000 calls. The store
contains a single INT row. Result rows are checked. Network and durability are
excluded. Medians below combine the two invocations for each revision.

| Query | Before µs/call | After µs/call | Before bytes/call | After bytes/call | Added bytes/call |
|---|---:|---:|---:|---:|---:|
| plain | 106.64 | 110.74 | 212905.07 | 212937.15 | 32.08 |
| timeout | 110.43 | 111.06 | 219896.49 | 219928.49 | 32.00 |
| combined | 116.54 | 116.20 | 223936.86 | 223968.86 | 32.00 |
| join-warning | 191.06 | 190.47 | 290241.11 | 299657.11 | 9416.00 |
| join-eliminated | 197.88 | 208.17 | 318561.11 | 328233.23 | 9672.12 |

The common paths add approximately 32 bytes per call, below 0.02% of their
existing allocation. An initial measurement without the empty-scope fast path
added roughly 1.7–2.5 KB per common-path call; the final code avoids installing
that scope when neither the current statement nor its caller has pending
join diagnostics.

Queries with join-order hints allocate approximately 9.4–9.7 KB more per call
for scope traversal and query-elimination analysis. The eliminated-query
baseline emitted a warning that MySQL suppresses, so that pair does not perform
identical diagnostic work. Elapsed medians vary across workloads, and the host
is not isolated; these measurements describe this local snapshot and do not
establish a general throughput change. Raw samples are in the adjacent
`06dbbefb-join-planning-*.csv` files.

Correctness: 3,107 root tests pass; 76 native MySQL wire contracts / 9,486 steps
pass with zero differences. The original join-hint baseline's 36 cases all
match; the expanded 62-case oracle retains three explicitly documented gaps.
