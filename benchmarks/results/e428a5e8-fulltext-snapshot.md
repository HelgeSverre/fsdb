# Full-text performance snapshot

Measured 2026-10-07 at revision `e428a5e8`, including deferred stopword loading,
all-seed query expansion, and short-word lookup compatibility. At 10,000 rows,
word Boolean search takes 0.91× MySQL's time, ngram prefix search is approximately
equal, and other reads take 1.09–1.61×. Ngram Boolean phrase search remains the
largest measured read gap.

## Conditions and limits

Both engines run natively over loopback on the same Apple M2 Max host, macOS
Darwin 24.6.0 arm64. Fsdb uses a Release build, .NET SDK 10.0.401 / runtime
10.0.12, `DOTNET_PROCESSOR_COUNT=4`, and a private WAL data directory. MySQL
8.4.11 uses normal durable commits, a private data directory, a 64 MiB buffer
pool, and 64 MiB redo capacity. Both disposable servers exited and their data
was removed after measurement.

The unchanged [benchmark script](../scripts/fulltext-snapshot.fsx) uses default
word and size-2 ngram indexes at 1,000 and 10,000 rows. A quarter of each corpus
matches; every search transfers all matching IDs. Each workload warms for at
least one second and three executions, then measures seven batches of ten
operations. Values are median, minimum, and maximum batch-average milliseconds
per operation, not individual latency percentiles. Update pairs change one row
and restore it, committing each UPDATE separately. Fsdb runs before MySQL.

Background applications remained active; BoatX was observed around 65% CPU
while the Release build ran. The MySQL ngram update samples are especially
variable (0.544–1.309 ms per pair), so their apparent advantage for fsdb is not
a robust write-performance conclusion. The earlier
[snapshot](0370842f-fulltext-snapshot.md) was a separate run: these numbers do
not isolate a code speedup or regression.

This small-vocabulary corpus measures common postings. It does not measure
custom stopword loading, mixed historical rules, startup, checkpoint creation,
or a diverse vocabulary where additional expansion seeds introduce new terms.

## Results

| Workload at 10,000 rows | fsdb WAL ms | MySQL ms | fsdb / MySQL |
|---|---:|---:|---:|
| Word natural | 4.527 | 3.411 | 1.33× |
| Word natural phrase | 13.977 | 9.911 | 1.41× |
| Word phrase expansion | 25.620 | 23.420 | 1.09× |
| Word boolean | 4.727 | 5.194 | 0.91× |
| Ngram natural | 4.385 | 3.555 | 1.23× |
| Ngram natural phrase | 7.559 | 5.543 | 1.36× |
| Ngram boolean phrase | 17.179 | 10.677 | 1.61× |
| Ngram prefix | 3.439 | 3.437 | 1.00× |
| words durable update pair | 0.735 | 0.366 | 2.01× |
| grams durable update pair | 0.660 | 0.836 | 0.79× |

The Release build passed with no warnings or errors. All benchmark result-count
checks passed. The preceding correctness gate at this source revision passed
2,922 tests, and the contract lane passed 49 cases / 5,111 steps without
differences; those checks were not rerun for this documentation-only snapshot.

## Reproduction

Build `src/Fsdb` in Release and `tests/Fsdb.Tests` in Debug. Start private native
servers with the conditions above, set `FSDB_PERF_CONNECTION` and
`FSDB_ORACLE_CONNECTION`, then run:

```sh
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --nologo benchmarks/scripts/fulltext-snapshot.fsx
```

## Raw samples

| Target | Rows | Workload | Median ms | Min ms | Max ms |
|---|---:|---|---:|---:|---:|
| fsdb-wal | 1000 | Word natural | 1.2400 | 1.2070 | 1.2611 |
<!-- samples-ms: 1.2365,1.2070,1.2611,1.2609,1.2546,1.2400,1.2199 -->
| fsdb-wal | 1000 | Word natural phrase | 2.1609 | 1.8555 | 2.2782 |
<!-- samples-ms: 2.0118,1.8555,1.9654,2.2782,2.2599,2.1609,2.1750 -->
| fsdb-wal | 1000 | Word phrase expansion | 1.9449 | 1.8984 | 2.0134 |
<!-- samples-ms: 1.9258,1.9449,2.0134,1.8984,1.9520,1.9572,1.9410 -->
| fsdb-wal | 1000 | Word boolean | 0.5374 | 0.4843 | 0.6683 |
<!-- samples-ms: 0.6683,0.5374,0.5182,0.6058,0.5351,0.4843,0.5866 -->
| fsdb-wal | 1000 | Ngram natural | 0.4808 | 0.4583 | 0.5740 |
<!-- samples-ms: 0.4808,0.4656,0.4764,0.4583,0.4910,0.5015,0.5740 -->
| fsdb-wal | 1000 | Ngram natural phrase | 0.6737 | 0.6105 | 0.7669 |
<!-- samples-ms: 0.6192,0.7669,0.6737,0.6105,0.7251,0.6468,0.6972 -->
| fsdb-wal | 1000 | Ngram boolean phrase | 1.2517 | 1.2123 | 1.3053 |
<!-- samples-ms: 1.2457,1.2470,1.2517,1.2725,1.2123,1.3053,1.2947 -->
| fsdb-wal | 1000 | Ngram prefix | 0.4224 | 0.4081 | 0.4343 |
<!-- samples-ms: 0.4095,0.4219,0.4224,0.4243,0.4081,0.4343,0.4275 -->
| fsdb-wal | 1000 | words durable update pair | 0.7356 | 0.6909 | 0.8241 |
<!-- samples-ms: 0.7654,0.7356,0.6909,0.7423,0.7298,0.8241,0.7108 -->
| fsdb-wal | 1000 | grams durable update pair | 0.6624 | 0.6048 | 0.6729 |
<!-- samples-ms: 0.6729,0.6250,0.6671,0.6561,0.6048,0.6670,0.6624 -->
| fsdb-wal | 10000 | Word natural | 4.5275 | 4.3329 | 4.9666 |
<!-- samples-ms: 4.3673,4.5275,4.9666,4.4615,4.6507,4.3329,4.5880 -->
| fsdb-wal | 10000 | Word natural phrase | 13.9766 | 13.5973 | 14.1474 |
<!-- samples-ms: 13.6921,13.5973,13.9963,13.9307,13.9890,13.9766,14.1474 -->
| fsdb-wal | 10000 | Word phrase expansion | 25.6198 | 25.0411 | 26.6494 |
<!-- samples-ms: 25.7200,25.3854,25.4910,25.0411,25.8605,25.6198,26.6494 -->
| fsdb-wal | 10000 | Word boolean | 4.7269 | 3.9261 | 5.9190 |
<!-- samples-ms: 5.2573,5.9190,5.4840,4.7269,4.1024,3.9261,3.9266 -->
| fsdb-wal | 10000 | Ngram natural | 4.3850 | 4.3180 | 4.8769 |
<!-- samples-ms: 4.3850,4.6266,4.8769,4.7250,4.3554,4.3332,4.3180 -->
| fsdb-wal | 10000 | Ngram natural phrase | 7.5585 | 6.9718 | 7.8686 |
<!-- samples-ms: 7.5585,6.9718,7.8227,7.0859,7.4830,7.8686,7.7022 -->
| fsdb-wal | 10000 | Ngram boolean phrase | 17.1787 | 15.6654 | 23.1538 |
<!-- samples-ms: 15.9989,15.6654,16.2086,20.2709,23.1538,17.2035,17.1787 -->
| fsdb-wal | 10000 | Ngram prefix | 3.4390 | 3.3398 | 3.5131 |
<!-- samples-ms: 3.5131,3.3398,3.3983,3.4558,3.4390,3.4411,3.3769 -->
| fsdb-wal | 10000 | words durable update pair | 0.7345 | 0.7025 | 0.8083 |
<!-- samples-ms: 0.7025,0.7345,0.7739,0.7497,0.7284,0.8083,0.7165 -->
| fsdb-wal | 10000 | grams durable update pair | 0.6596 | 0.6300 | 0.7090 |
<!-- samples-ms: 0.6325,0.6855,0.6410,0.7001,0.6596,0.6300,0.7090 -->
| mysql | 1000 | Word natural | 0.4416 | 0.4010 | 0.4592 |
<!-- samples-ms: 0.4416,0.4266,0.4156,0.4513,0.4488,0.4010,0.4592 -->
| mysql | 1000 | Word natural phrase | 1.0867 | 1.0526 | 1.1177 |
<!-- samples-ms: 1.0975,1.0867,1.0527,1.1103,1.0592,1.0526,1.1177 -->
| mysql | 1000 | Word phrase expansion | 2.3677 | 2.3528 | 2.4122 |
<!-- samples-ms: 2.3621,2.4061,2.3571,2.3970,2.3677,2.3528,2.4122 -->
| mysql | 1000 | Word boolean | 0.6319 | 0.5925 | 0.6407 |
<!-- samples-ms: 0.5945,0.6397,0.6407,0.5925,0.6229,0.6319,0.6330 -->
| mysql | 1000 | Ngram natural | 0.4207 | 0.4131 | 0.4299 |
<!-- samples-ms: 0.4184,0.4207,0.4158,0.4238,0.4215,0.4299,0.4131 -->
| mysql | 1000 | Ngram natural phrase | 0.6289 | 0.6104 | 0.6479 |
<!-- samples-ms: 0.6479,0.6264,0.6114,0.6289,0.6104,0.6425,0.6345 -->
| mysql | 1000 | Ngram boolean phrase | 1.1253 | 1.0808 | 1.1598 |
<!-- samples-ms: 1.1375,1.1252,1.1339,1.1598,1.0808,1.1253,1.0893 -->
| mysql | 1000 | Ngram prefix | 0.4108 | 0.3992 | 0.4454 |
<!-- samples-ms: 0.4049,0.4360,0.4108,0.3992,0.4093,0.4388,0.4454 -->
| mysql | 1000 | words durable update pair | 0.3781 | 0.3712 | 0.4485 |
<!-- samples-ms: 0.3773,0.4420,0.3727,0.3712,0.3906,0.4485,0.3781 -->
| mysql | 1000 | grams durable update pair | 0.3401 | 0.3209 | 0.4007 |
<!-- samples-ms: 0.3437,0.4007,0.3209,0.3401,0.3224,0.3368,0.3627 -->
| mysql | 10000 | Word natural | 3.4112 | 3.3612 | 3.5309 |
<!-- samples-ms: 3.5093,3.5309,3.5112,3.4112,3.3789,3.3876,3.3612 -->
| mysql | 10000 | Word natural phrase | 9.9114 | 9.8260 | 10.0575 |
<!-- samples-ms: 9.9791,10.0575,9.8750,9.9433,9.8260,9.9114,9.9048 -->
| mysql | 10000 | Word phrase expansion | 23.4201 | 23.0267 | 23.4794 |
<!-- samples-ms: 23.4201,23.4753,23.2643,23.0267,23.4794,23.2507,23.4736 -->
| mysql | 10000 | Word boolean | 5.1936 | 5.1508 | 5.2282 |
<!-- samples-ms: 5.1936,5.1508,5.2009,5.2282,5.1577,5.1612,5.2233 -->
| mysql | 10000 | Ngram natural | 3.5547 | 3.3751 | 5.2016 |
<!-- samples-ms: 5.2016,3.3751,3.5268,3.4779,3.5618,3.6340,3.5547 -->
| mysql | 10000 | Ngram natural phrase | 5.5430 | 5.4978 | 5.7051 |
<!-- samples-ms: 5.7051,5.6200,5.5430,5.5959,5.4978,5.5095,5.5400 -->
| mysql | 10000 | Ngram boolean phrase | 10.6771 | 10.5021 | 10.8515 |
<!-- samples-ms: 10.5021,10.6771,10.6204,10.7854,10.8515,10.6703,10.7087 -->
| mysql | 10000 | Ngram prefix | 3.4365 | 3.4119 | 3.7360 |
<!-- samples-ms: 3.7360,3.4341,3.4365,3.4119,3.5369,3.4678,3.4282 -->
| mysql | 10000 | words durable update pair | 0.3661 | 0.3327 | 0.4275 |
<!-- samples-ms: 0.3327,0.4131,0.3661,0.3343,0.3966,0.3358,0.4275 -->
| mysql | 10000 | grams durable update pair | 0.8356 | 0.5442 | 1.3093 |
<!-- samples-ms: 0.5442,0.6152,0.8356,0.9100,1.3093,0.8105,0.8565 -->
