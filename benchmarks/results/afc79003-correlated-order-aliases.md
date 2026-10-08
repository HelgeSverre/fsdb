# Correlated ordering alias cost

Revision `afc79003` plus the correlated-alias implementation uses the embedded
in-memory Debug engine, .NET SDK 10.0.401, Darwin 24.6.0 arm64, eight logical
processors, and a 4 GiB GC heap cap. Background applications remain active.

The [script](../scripts/order-aliases.fsx) now includes correlated alias ordering.
Each query sorts 10,000 rows and returns 100, with five warmups and nine batches
of five verified executions. Batch order reverses on alternate samples.
Allocation includes result verification.

| Ordering expression | Median batch-average ms/query | Median allocated bytes/query |
|---|---:|---:|
| `ABS(v)` | 53.446 | 60,234,614 |
| `ABS(a)` with `v AS a` | 53.403 | 60,234,750 |
| `ABS((SELECT a))` | 182.752 | 251,523,040 |

The correlated form takes about 3.4 times the time and 4.2 times the allocation
of direct ordering in this sample. It still executes a scalar subquery for each
row; the result identifies an optimization opportunity, not a MySQL comparison.

Source samples in ms: 58.15806, 54.71550, 53.75988, 53.71414, 53.35176,
52.19358, 53.44596, 52.08864, 52.20280.
Alias samples: 56.20970, 53.58252, 54.28510, 52.80386, 53.40324, 65.32318,
51.92878, 52.89864, 52.18636.
Correlated samples: 184.18004, 185.38298, 181.39500, 180.78964, 181.54906,
180.91864, 184.37778, 183.21028, 182.75156.

Two preceding two-query runs had source/alias medians of 53.027/64.474 ms and
50.180/48.996 ms. The alias slowdown did not repeat. Both allocated about
60,234,500 bytes per query, roughly 0.4% above the preceding duplicate-alias
measurement. These runs do not establish a timing regression or speedup.

```sh
DOTNET_PROCESSOR_COUNT=8 DOTNET_GCHeapHardLimit=0x100000000 \
  dotnet fsi --nologo benchmarks/scripts/order-aliases.fsx
```

Validation: 3,039 tests pass; native MySQL 8.4.11 alias contracts pass;
52 differential cases / 5,313 steps report zero differences at
`20261008T005024900-62019/contracts`. This is neither a durable-write result
nor a claim of overall engine performance.
