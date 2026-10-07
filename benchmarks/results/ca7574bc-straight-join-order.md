# Partial straight-join order profile

The baseline is `ca7574bc`; the comparison adds predecessor-constrained planning
to that source. At 500 base rows, 25,000 indexed fan-out rows, and one selective
row, the local-constraint query falls from 76.671 ms to 2.841 ms, about 27 times
faster. The globally pinned control stays near 76–77 ms. Both return 50 rows.

| Query | Before median ms | After median ms |
|---|---:|---:|
| Local table constraint | 76.671 | 2.841 |
| Global SELECT constraint | 77.122 | 76.078 |

## Conditions and reproduction

The [script](../scripts/straight-join-order.fsx) runs an embedded Debug build in
memory on the same Apple M2 Max host, macOS Darwin 24.6.0 arm64, .NET SDK
10.0.401/runtime 10.0.12, with `DOTNET_PROCESSOR_COUNT=4`. Each query warms five
times, then measures seven batches of ten executions. Values are batch-average
milliseconds, not individual latency percentiles. The local query runs first
in both measurements. Background applications remain active.

```sh
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --nologo benchmarks/scripts/straight-join-order.fsx
```

The query with `bases b STRAIGHT_JOIN many_rows l JOIN few_rows s` may run the
selective source before the fan-out while preserving b before l. Its global
`SELECT STRAIGHT_JOIN` control must keep b, l, s. The native MySQL oracle
verifies this distinction separately; these timings compare fsdb implementations,
not MySQL or durable deployment performance.

## Raw batch averages

Before:

- Local: 77.08276, 76.67115, 76.95225, 77.10853, 76.37260, 76.19748, 76.65727.
- Global: 77.30755, 77.12175, 76.85523, 76.91508, 77.60214, 77.44277, 77.06748.

After:

- Local: 2.84195, 2.76115, 6.41207, 2.84134, 2.80148, 2.82065, 2.93827.
- Global: 75.09228, 75.69330, 76.07772, 75.82267, 76.44734, 76.10295, 76.29907.

A confirmation run of the maintained script reports 3.081 ms locally and
81.431 ms globally, with the same result rows:

- Local: 3.08073, 2.98332, 7.04342, 3.03591, 2.97793, 3.16651, 3.08679.
- Global: 81.43148, 80.73276, 80.67165, 81.49067, 84.48403, 82.57005, 80.59725.

The complete gate passes 2,939 tests. The native order oracle passes, and the
contract lane passes 49 cases / 5,117 steps with zero differences at
`20261007T105948551-81369/contracts`.
