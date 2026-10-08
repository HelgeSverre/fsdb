# Duplicate ordering alias binding cost

Revision `26716423` plus the duplicate-alias implementation was measured with
[order-aliases.fsx](../scripts/order-aliases.fsx): 10,000 input rows, 100 output
rows, five warmups, and nine alternating batches of five verified executions.

| Ordering expression | Median batch-average ms/query | Median allocated bytes/query |
|---|---:|---:|
| `ABS(v)` | 51.000 | 59,994,459 |
| `ABS(a)` with `v AS a` | 50.798 | 59,994,546 |

Embedded in-memory Debug engine, .NET SDK 10.0.401, Darwin 24.6.0 arm64,
`DOTNET_PROCESSOR_COUNT=8`, `DOTNET_GCHeapHardLimit=0x100000000` (4 GiB).
Background applications remained active. Allocation includes result verification.

Source samples in ms: 72.08898, 53.44852, 53.60022, 51.06032, 50.99984,
49.15248, 49.45630, 49.57044, 49.69356.
Alias samples in ms: 56.99948, 52.14202, 50.79766, 57.58984, 51.17410,
49.93170, 49.40410, 49.64184, 49.80510.

The two spellings remain comparable in this sample. Compared with the
[earlier measurement](6451673a-order-aliases.md), allocation is about 1.1% higher;
timing remains within the observed spread. These separate runs do not establish
a speedup or a statistically controlled before/after result. This is neither a
MySQL performance comparison nor a durable-write measurement.

The full gate passes 3,038 tests. Native alias contracts pass; wire contracts
pass 51 cases / 5,261 steps with zero differences at
`20261008T003426298-49934/contracts`.
