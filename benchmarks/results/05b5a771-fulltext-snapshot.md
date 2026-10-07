# Full-text performance snapshot

Measured 2026-10-07 at revision `05b5a771` on Apple M2 Max, macOS Darwin
24.6.0 arm64, .NET SDK 10.0.401 / runtime 10.0.12, and native MySQL 8.4.11.
fsdb uses a Release build with `DOTNET_PROCESSOR_COUNT=4` and a private
`--data-dir`. MySQL uses a private datadir, normal durable commit settings,
and a 128 MiB InnoDB buffer pool. Both run natively on the same host over
loopback TCP, one client at a time; no container or network asymmetry.

The [script](../scripts/fulltext-snapshot.fsx) seeds 1,000 and 10,000 rows in
separate word and size-2 ngram indexes. Exactly one quarter of each corpus
matches. Documents repeat a small vocabulary, so this exercises common
postings rather than a diverse production corpus. Search timings include
returning all 250 or 2,500 IDs over the wire and checking the returned count.
`ANALYZE TABLE` runs after seeding both engines.

Each workload warms for at least one second and three executions, then runs
seven batches of ten operations. The reported median, minimum, and maximum
are **batch-average milliseconds per operation**, not individual latency
percentiles. An update operation is a pair of separately committed updates
that changes one row and restores its original text; its time covers both
commits. fsdb runs before MySQL. All measured search result counts agree with
the seeded expectation.

Background BoatX processes consumed several CPU cores, with load changing
during the run. No unrelated processes were stopped. Treat these as current
approximate timings, not a controlled regression comparison. Earlier reports
use different corpora, query shapes, and runtime versions; no before/after
speedup is inferred from them.

## Results

| Workload (10,000 rows) | fsdb WAL ms | MySQL ms | fsdb / MySQL |
|---|---:|---:|---:|
| Word natural | 11.137 | 7.809 | 1.43× |
| Word natural phrase | 33.766 | 26.015 | 1.30× |
| Word phrase expansion | 62.042 | 53.905 | 1.15× |
| Word boolean | 13.343 | 10.754 | 1.24× |
| Ngram natural | 13.273 | 6.864 | 1.93× |
| Ngram natural phrase | 26.356 | 12.221 | 2.16× |
| Ngram boolean phrase | 39.126 | 23.612 | 1.66× |
| Ngram prefix | 5.857 | 7.436 | 0.79× |
| words durable update pair | 1.260 | 0.911 | 1.38× |
| grams durable update pair | 1.027 | 0.816 | 1.26× |

At 10,000 rows, fsdb's natural phrase and expansion paths remain slower than
MySQL; ngram prefix lookup is faster in this corpus. Durable update pairs are
closer, within roughly 1.3–1.4× MySQL. The report does not measure snapshot
creation, restart time, mixed historical token sizes, or nondefault startup
configuration.

The initial aggregate probe exposed an independent [COUNT(*) compatibility
issue](../../torture/findings/2026-10-07-fulltext-aggregate-order.md). The
completed measurements use `SELECT id`; aggregate failure is not included
in timings.

## Reproduction

Build `src/Fsdb` in Release and `tests/Fsdb.Tests` in Debug (the script uses
its MySqlConnector assembly). Start a private fsdb server with `--data-dir`
and a private native MySQL 8.4.11 server with durable defaults. Point
`FSDB_PERF_CONNECTION` and `FSDB_ORACLE_CONNECTION` at them, then run:

```sh
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --nologo benchmarks/scripts/fulltext-snapshot.fsx
```

The script creates and drops only its own randomly named databases. The
measured run used disposable server directories, which were removed after
both servers exited. Correctness checks before measurement passed the full
2,862-test suite, 45 MySQL contract cases with 4,978 matching steps, and the
12-check crash-recovery lane.

## Raw samples

| Target | Rows | Workload | Median ms | Min ms | Max ms |
|---|---:|---|---:|---:|---:|
| fsdb-wal | 1000 | Word natural | 1.9454 | 1.8720 | 2.0180 |
<!-- samples-ms: 1.8720,1.9454,1.9975,1.9231,1.8787,2.0180,1.9571 -->
| fsdb-wal | 1000 | Word natural phrase | 4.0196 | 3.7670 | 4.2026 |
<!-- samples-ms: 3.9108,4.0757,3.7670,3.7771,4.2026,4.0841,4.0196 -->
| fsdb-wal | 1000 | Word phrase expansion | 7.6615 | 6.9178 | 8.0251 |
<!-- samples-ms: 8.0251,7.6615,7.0394,6.9178,7.6287,7.7672,7.9164 -->
| fsdb-wal | 1000 | Word boolean | 1.0653 | 0.9832 | 1.1743 |
<!-- samples-ms: 1.0346,1.0185,1.1546,0.9832,1.0653,1.1743,1.1688 -->
| fsdb-wal | 1000 | Ngram natural | 1.0943 | 1.0006 | 1.2088 |
<!-- samples-ms: 1.1256,1.2088,1.0943,1.1117,1.0853,1.0803,1.0006 -->
| fsdb-wal | 1000 | Ngram natural phrase | 1.3599 | 1.2406 | 1.4732 |
<!-- samples-ms: 1.3406,1.4380,1.4508,1.2406,1.3599,1.4732,1.3095 -->
| fsdb-wal | 1000 | Ngram boolean phrase | 3.0369 | 2.7997 | 3.1269 |
<!-- samples-ms: 3.0369,3.1269,3.0666,3.0064,3.1205,2.7997,2.8180 -->
| fsdb-wal | 1000 | Ngram prefix | 1.0442 | 0.8605 | 1.0745 |
<!-- samples-ms: 0.8605,0.9951,1.0505,1.0558,1.0253,1.0442,1.0745 -->
| fsdb-wal | 1000 | words durable update pair | 1.8112 | 1.7378 | 2.1107 |
<!-- samples-ms: 1.7751,1.9484,1.8008,1.7378,1.8112,1.9358,2.1107 -->
| fsdb-wal | 1000 | grams durable update pair | 1.5643 | 1.4159 | 1.6373 |
<!-- samples-ms: 1.6301,1.6373,1.5808,1.5118,1.5643,1.4782,1.4159 -->
| fsdb-wal | 10000 | Word natural | 11.1371 | 9.9140 | 23.0695 |
<!-- samples-ms: 11.1371,10.7838,9.9140,10.5697,13.8435,14.1755,23.0695 -->
| fsdb-wal | 10000 | Word natural phrase | 33.7661 | 32.0749 | 35.4118 |
<!-- samples-ms: 33.7661,32.0749,34.0530,32.6827,35.4118,32.6123,34.6413 -->
| fsdb-wal | 10000 | Word phrase expansion | 62.0421 | 59.5737 | 81.0095 |
<!-- samples-ms: 63.3796,59.5737,61.6943,62.5100,59.8006,62.0421,81.0095 -->
| fsdb-wal | 10000 | Word boolean | 13.3434 | 11.2996 | 16.2653 |
<!-- samples-ms: 11.2996,11.7951,16.2653,13.5034,14.8804,13.3434,11.3739 -->
| fsdb-wal | 10000 | Ngram natural | 13.2727 | 12.7597 | 14.6405 |
<!-- samples-ms: 12.7597,13.0057,14.6405,13.3670,13.1353,13.4126,13.2727 -->
| fsdb-wal | 10000 | Ngram natural phrase | 26.3559 | 21.5302 | 29.6203 |
<!-- samples-ms: 25.3114,27.6334,27.4255,29.6203,26.3559,21.5302,23.4078 -->
| fsdb-wal | 10000 | Ngram boolean phrase | 39.1255 | 34.4139 | 45.6902 |
<!-- samples-ms: 45.0459,36.8445,34.4139,37.4201,39.1255,42.9885,45.6902 -->
| fsdb-wal | 10000 | Ngram prefix | 5.8575 | 5.5153 | 10.0555 |
<!-- samples-ms: 10.0555,8.5726,6.3512,5.6590,5.8575,5.5153,5.5900 -->
| fsdb-wal | 10000 | words durable update pair | 1.2600 | 1.2314 | 1.3890 |
<!-- samples-ms: 1.2600,1.3890,1.3192,1.2511,1.3683,1.2314,1.2482 -->
| fsdb-wal | 10000 | grams durable update pair | 1.0267 | 0.8950 | 1.1761 |
<!-- samples-ms: 0.9937,1.0267,1.1148,0.8950,1.0487,0.9492,1.1761 -->
| mysql | 1000 | Word natural | 0.8553 | 0.6697 | 0.9183 |
<!-- samples-ms: 0.8696,0.6697,0.8553,0.7496,0.9183,0.7274,0.8831 -->
| mysql | 1000 | Word natural phrase | 3.5065 | 3.4162 | 3.7483 |
<!-- samples-ms: 3.4162,3.5065,3.5524,3.4840,3.5811,3.4950,3.7483 -->
| mysql | 1000 | Word phrase expansion | 7.9385 | 7.7501 | 8.0905 |
<!-- samples-ms: 8.0905,7.9544,7.9385,7.9374,7.9111,7.7501,7.9504 -->
| mysql | 1000 | Word boolean | 0.9455 | 0.8643 | 1.0389 |
<!-- samples-ms: 0.8888,1.0225,0.9045,1.0013,0.9455,1.0389,0.8643 -->
| mysql | 1000 | Ngram natural | 0.6186 | 0.5787 | 0.7347 |
<!-- samples-ms: 0.7234,0.5906,0.5946,0.6813,0.6186,0.7347,0.5787 -->
| mysql | 1000 | Ngram natural phrase | 0.9611 | 0.8561 | 0.9829 |
<!-- samples-ms: 0.9828,0.8561,0.9611,0.9530,0.9656,0.9323,0.9829 -->
| mysql | 1000 | Ngram boolean phrase | 2.0304 | 2.0221 | 2.2127 |
<!-- samples-ms: 2.0291,2.0267,2.0304,2.0402,2.2127,2.0221,2.1288 -->
| mysql | 1000 | Ngram prefix | 0.8213 | 0.7510 | 0.9570 |
<!-- samples-ms: 0.8213,0.9288,0.7875,0.7982,0.9237,0.7510,0.9570 -->
| mysql | 1000 | words durable update pair | 0.9626 | 0.8066 | 1.0030 |
<!-- samples-ms: 1.0030,0.8066,0.9823,0.8399,0.9626,0.8288,0.9685 -->
| mysql | 1000 | grams durable update pair | 1.0270 | 0.7854 | 1.2824 |
<!-- samples-ms: 1.0270,0.9478,0.7854,1.0729,1.0117,1.2824,1.1414 -->
| mysql | 10000 | Word natural | 7.8090 | 7.5941 | 8.5566 |
<!-- samples-ms: 8.1585,8.5566,7.8090,7.8387,7.6465,7.7662,7.5941 -->
| mysql | 10000 | Word natural phrase | 26.0155 | 23.5782 | 41.3968 |
<!-- samples-ms: 26.5246,23.8647,24.1826,23.5782,41.3968,27.5269,26.0155 -->
| mysql | 10000 | Word phrase expansion | 53.9051 | 53.8869 | 55.9242 |
<!-- samples-ms: 55.9242,55.3733,53.9051,54.0491,53.8939,53.8946,53.8869 -->
| mysql | 10000 | Word boolean | 10.7543 | 10.5366 | 11.4238 |
<!-- samples-ms: 10.7543,10.5366,11.3986,11.4238,10.5719,10.5618,10.9903 -->
| mysql | 10000 | Ngram natural | 6.8644 | 6.6750 | 7.3717 |
<!-- samples-ms: 6.8644,6.7765,6.7047,6.9371,7.3717,7.0644,6.6750 -->
| mysql | 10000 | Ngram natural phrase | 12.2210 | 11.3006 | 12.3580 |
<!-- samples-ms: 11.3006,12.2238,12.2210,11.9367,11.5801,12.3580,12.2279 -->
| mysql | 10000 | Ngram boolean phrase | 23.6123 | 22.6800 | 24.8512 |
<!-- samples-ms: 23.7799,23.1208,24.6160,22.6800,24.8512,23.0324,23.6123 -->
| mysql | 10000 | Ngram prefix | 7.4365 | 6.9491 | 8.1760 |
<!-- samples-ms: 6.9491,7.5693,7.3527,7.4085,7.5595,8.1760,7.4365 -->
| mysql | 10000 | words durable update pair | 0.9111 | 0.8328 | 1.0857 |
<!-- samples-ms: 0.9111,0.8328,0.9823,0.8776,1.0857,1.0487,0.8841 -->
| mysql | 10000 | grams durable update pair | 0.8161 | 0.7369 | 0.9660 |
<!-- samples-ms: 0.7369,0.9660,0.8071,0.9182,0.8161,0.9072,0.8144 -->
