# Grouped join performance snapshot

## Latest source confirmation

Measured 2026-10-07 after the grouped-column ownership and source-qualification
changes, using the same method below. Engine source diff SHA-256
(`git diff -- src/Fsdb | shasum -a 256`):
`81e9c603189c4ee2eaf929038ed68c0d9b866b5e9d70dfc02f6f5708c1ff1fe5`.
The preceding full gate passed 2,979 tests with no ignored tests or errors.

| Query | Median batch-average ms |
|---|---:|
| Flat inner join | 2.993 |
| Grouped inner join with ON | 187.317 |
| Grouped inner join with USING | 63.648 |
| Existing local STRAIGHT_JOIN constraint | 3.902 |
| Existing global STRAIGHT_JOIN constraint | 77.122 |

Grouped ON remains about 63 times the flat-query latency. Grouped USING is
about 21 times the flat-query latency and measured about 65% lower than the
earlier snapshot. These runs do not isolate which change caused the difference.
Local STRAIGHT_JOIN samples are noisy; their median does not establish a
regression. Every grouped benchmark execution verified all 50 expected rows.

```text
flat inner rows=50 median-ms=2.993 samples=[3.0805; 2.99324; 6.96787; 2.95103; 2.93094; 2.94071; 3.02457]
grouped inner rows=50 median-ms=187.317 samples=[192.53861; 192.14359; 191.30118; 187.31716; 186.12769; 182.79498; 180.5556]
grouped USING rows=50 median-ms=63.648 samples=[63.00174; 63.28937; 63.69371; 63.64786; 65.27104; 63.03855; 63.71257]
table constraint rows=50 median-ms=3.902 samples=[2.93292; 2.87298; 7.05885; 3.90165; 5.58336; 3.21566; 6.13932]
global constraint rows=50 median-ms=77.122 samples=[77.69971; 76.58669; 76.64132; 77.16616; 77.12452; 76.75227; 77.12243]
```

## Earlier snapshot

Measured 2026-10-07 on main at `869a4b6c` with uncommitted grouped-join changes.
Tracked diff SHA-256 before adding this report:
`b3c37c96bf77275ba2e2d2cca3b4d38f068dfc1310c147991973ece7065d0b2d`.

| Query | Median batch-average ms |
|---|---:|
| Flat inner join | 2.822 |
| Grouped inner join with ON | 184.436 |
| Grouped inner join with USING | 182.345 |
| Existing local STRAIGHT_JOIN constraint | 2.976 |
| Existing global STRAIGHT_JOIN constraint | 80.895 |

The grouped shapes are approximately 65 times slower than the equivalent flat
inner join on this fixture. This identifies a profiling target; it does not
establish the cause. Grouped-join compatibility remains incomplete in GAPS.md.
The existing straight-join measurements are close to the previous confirmation
run (3.081 ms local and 81.431 ms global); these samples do not establish a
statistically significant change.

## Method

Embedded Debug build, in-memory, Apple M2 Max, Darwin 24.6.0 arm64,
.NET SDK 10.0.401, DOTNET_PROCESSOR_COUNT=4. Background applications active.
Fixture: 500 base rows, 25,000 indexed fan-out rows, one selective row.
Five warmups, seven batches of ten executions per query; cases run in listed
script order. Grouped benchmark checks all 50 expected result rows on every
execution. These are batch averages, not individual latency percentiles.
No MySQL timing comparison or durable write claim is made.

```sh
DOTNET_PROCESSOR_COUNT=8 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 just check
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --nologo benchmarks/scripts/straight-join-order.fsx
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --nologo benchmarks/scripts/grouped-joins.fsx
```

Full gate: 2,961 passed, zero failures or ignored tests.

## Raw batch averages (ms)

```text
table constraint rows=50 median-ms=2.976 samples=[2.97537; 2.9733; 7.17862; 2.9992; 2.97584; 2.9667; 3.07882]
global constraint rows=50 median-ms=80.895 samples=[80.8949; 81.55841; 80.99989; 80.37938; 80.77373; 80.90742; 80.63468]
flat inner rows=50 median-ms=2.822 samples=[2.81377; 2.77709; 7.26443; 2.80869; 2.82232; 2.84617; 2.90627]
grouped inner rows=50 median-ms=184.436 samples=[183.03869; 186.12015; 190.16421; 186.39988; 184.43624; 182.89222; 182.52866]
grouped USING rows=50 median-ms=182.345 samples=[183.23275; 182.34462; 182.62771; 182.0531; 182.00929; 185.69055; 181.81767]
```
