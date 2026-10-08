# Ordering alias binding cost

The build based on `6451673a` with the ordering-alias changes sorts 10,000 integer
rows and returns the smallest 100. Source-expression and alias-expression
spellings have comparable measured cost; the timing spread does not establish
a speedup.

| ORDER BY expression | Median batch-average ms/query | Median allocated bytes/query |
|---|---:|---:|
| `ABS(v)` | 52.545 | 59,354,099 |
| `ABS(a)` where `v AS a` | 50.748 | 59,352,770 |

## Conditions and reproduction

The [script](../scripts/order-aliases.fsx) uses the embedded in-memory engine,
a Debug build, .NET SDK 10.0.401, and Darwin 24.6.0 arm64. It warms each query
five times, then measures nine batches of five executions, alternating which
query runs first. Every execution verifies the returned rows. Allocation uses
`GC.GetTotalAllocatedBytes`; the batch includes query execution and result
verification. Background applications remain active.

```sh
DOTNET_PROCESSOR_COUNT=8 DOTNET_GCHeapHardLimit=0x100000000 \
  dotnet fsi --nologo benchmarks/scripts/order-aliases.fsx
```

The heap cap is 4 GiB. This is a current-build comparison of two query spellings,
not a before/after engine comparison, MySQL comparison, or durable-write result.

## Batch-average milliseconds per query

- Source: 67.58220, 51.47272, 50.94908, 52.54504, 55.04762, 50.17576,
  58.54436, 50.21078, 59.03700.
- Alias: 50.74768, 49.50244, 49.93734, 56.11928, 50.72974, 50.44252,
  56.29560, 53.82958, 52.46866.

The complete gate passes 3,037 tests under the same heap cap. Native MySQL 8.4.11
alias contracts pass, and the differential suite passes 50 cases / 5,153 steps
with zero differences at `20261008T000306350-85680/contracts`.
