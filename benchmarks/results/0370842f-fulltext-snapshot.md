# Full-text performance after stopword recovery changes

Measured 2026-10-07 at revision `0370842f`, after custom stopword selection,
historical indexing rules, and recovery support. On this default-policy corpus,
fsdb's 10,000-row word Boolean search takes 0.75× MySQL's time; other reads take
1.04–1.63×. Durable update pairs take 1.95–2.32×, with fsdb medians below 0.8 ms.

## Measurement conditions

Both servers run natively on the same Apple M2 Max host over loopback TCP.
The host uses macOS Darwin 24.6.0 arm64 and .NET SDK 10.0.401 / runtime 10.0.12.
Fsdb uses a Release build, `DOTNET_PROCESSOR_COUNT=4`, and a private data directory.
MySQL 8.4.11 uses a private data directory, normal durable commit settings, a
64 MiB InnoDB buffer pool, and 64 MiB redo capacity. Disposable servers and data
were removed after both servers exited.

The unchanged [benchmark script](../scripts/fulltext-snapshot.fsx) seeds
1,000 and 10,000 rows in ordinary-word and size-2 ngram indexes. A quarter of
each corpus matches. Each search returns all 250 or 2,500 IDs over the wire;
all measured result counts agree. Documents repeat a small vocabulary, so this
measures common postings rather than a diverse production corpus.

Each workload warms for at least one second and three executions, then runs
seven batches of ten operations. Values are median, minimum, and maximum
**batch-average milliseconds per operation**, not individual latency percentiles.
An update pair changes one row and restores its text, with a separate durable
commit for each UPDATE. Fsdb runs before MySQL; `ANALYZE TABLE` runs after seeding.

Background applications remained active, including a BoatX process observed
around 57% CPU during the run. Both engines have lower absolute timings than the
[earlier snapshot](05b5a771-fulltext-snapshot.md), which had substantially more
background load and a different MySQL buffer-pool size. These runs do not establish
a code speedup or regression. Ratios describe this run and corpus only.

## Results

| Workload at 10,000 rows | fsdb WAL ms | MySQL ms | fsdb / MySQL |
|---|---:|---:|---:|
| Word natural | 4.132 | 3.269 | 1.26× |
| Word natural phrase | 13.534 | 9.547 | 1.42× |
| Word phrase expansion | 24.438 | 22.622 | 1.08× |
| Word boolean | 3.794 | 5.030 | 0.75× |
| Ngram natural | 4.097 | 3.216 | 1.27× |
| Ngram natural phrase | 6.844 | 5.294 | 1.29× |
| Ngram boolean phrase | 16.085 | 9.876 | 1.63× |
| Ngram prefix | 3.347 | 3.214 | 1.04× |
| words durable update pair | 0.762 | 0.329 | 2.32× |
| grams durable update pair | 0.625 | 0.321 | 1.95× |

The largest measured read gap is ngram Boolean phrase search. The durable
update-pair gap is larger proportionally, though its absolute difference is
under 0.5 ms. Custom stopword-list sizes, index construction, checkpoint creation,
source reload, and mixed historical policies are outside this workload.

## Reproduction and validation

Build `src/Fsdb` in Release and `tests/Fsdb.Tests` in Debug. Start private native
servers with the conditions above, set `FSDB_PERF_CONNECTION` and
`FSDB_ORACLE_CONNECTION` to their connection strings, then run:

```sh
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --nologo benchmarks/scripts/fulltext-snapshot.fsx
```

The script creates and drops only its own randomly named databases. Before
measurement, `just check` passed all 2,915 tests with no build warnings or errors.
The native custom-stopword oracle passed, the contract lane passed 47 cases and
5,007 steps without differences, and the durability lane passed 12 crash restarts,
preserving all 109 acknowledged commits and transaction boundaries.

## Raw samples

| Target | Rows | Workload | Median ms | Min ms | Max ms |
|---|---:|---|---:|---:|---:|
| fsdb-wal | 1000 | Word natural | 1.3336 | 1.3164 | 1.4025 |
<!-- samples-ms: 1.3164,1.3213,1.3432,1.4025,1.3336,1.3205,1.3507 -->
| fsdb-wal | 1000 | Word natural phrase | 1.7533 | 1.7371 | 1.9515 |
<!-- samples-ms: 1.7533,1.7371,1.7456,1.7374,1.8812,1.9515,1.8467 -->
| fsdb-wal | 1000 | Word phrase expansion | 1.8102 | 1.7016 | 1.8523 |
<!-- samples-ms: 1.8102,1.7757,1.8135,1.7016,1.7875,1.8405,1.8523 -->
| fsdb-wal | 1000 | Word boolean | 0.5073 | 0.4689 | 0.5420 |
<!-- samples-ms: 0.4689,0.4863,0.5229,0.4788,0.5420,0.5073,0.5215 -->
| fsdb-wal | 1000 | Ngram natural | 0.4426 | 0.4325 | 0.4942 |
<!-- samples-ms: 0.4325,0.4411,0.4352,0.4426,0.4548,0.4878,0.4942 -->
| fsdb-wal | 1000 | Ngram natural phrase | 0.6203 | 0.5909 | 0.7316 |
<!-- samples-ms: 0.6203,0.6385,0.5909,0.6103,0.6384,0.5979,0.7316 -->
| fsdb-wal | 1000 | Ngram boolean phrase | 1.2349 | 1.1951 | 1.3796 |
<!-- samples-ms: 1.2349,1.1951,1.2989,1.3796,1.2074,1.2307,1.2572 -->
| fsdb-wal | 1000 | Ngram prefix | 0.4482 | 0.4174 | 0.4764 |
<!-- samples-ms: 0.4764,0.4666,0.4460,0.4306,0.4482,0.4492,0.4174 -->
| fsdb-wal | 1000 | words durable update pair | 0.7582 | 0.6803 | 0.7778 |
<!-- samples-ms: 0.7587,0.7596,0.7778,0.7349,0.6803,0.7232,0.7582 -->
| fsdb-wal | 1000 | grams durable update pair | 0.6058 | 0.5628 | 0.6581 |
<!-- samples-ms: 0.6581,0.6545,0.6058,0.5628,0.5745,0.5893,0.6267 -->
| fsdb-wal | 10000 | Word natural | 4.1316 | 4.0294 | 4.2553 |
<!-- samples-ms: 4.1156,4.1483,4.2553,4.1988,4.1316,4.0294,4.0584 -->
| fsdb-wal | 10000 | Word natural phrase | 13.5341 | 13.1636 | 17.2205 |
<!-- samples-ms: 13.2711,13.5898,13.7238,13.1636,13.2953,13.5341,17.2205 -->
| fsdb-wal | 10000 | Word phrase expansion | 24.4383 | 23.6902 | 25.3545 |
<!-- samples-ms: 24.1632,24.7580,25.0049,25.3545,23.9999,24.4383,23.6902 -->
| fsdb-wal | 10000 | Word boolean | 3.7944 | 3.4627 | 5.0532 |
<!-- samples-ms: 5.0532,4.4516,3.9079,3.7944,3.4627,3.5753,3.5627 -->
| fsdb-wal | 10000 | Ngram natural | 4.0973 | 4.0701 | 4.1510 |
<!-- samples-ms: 4.0727,4.1134,4.1207,4.0918,4.0701,4.1510,4.0973 -->
| fsdb-wal | 10000 | Ngram natural phrase | 6.8437 | 6.7627 | 7.0569 |
<!-- samples-ms: 6.7627,7.0569,7.0171,6.8437,6.8712,6.8033,6.7661 -->
| fsdb-wal | 10000 | Ngram boolean phrase | 16.0848 | 15.1038 | 16.5820 |
<!-- samples-ms: 16.0848,16.5611,15.1038,15.6287,16.5820,15.3380,16.3418 -->
| fsdb-wal | 10000 | Ngram prefix | 3.3471 | 3.1379 | 3.3719 |
<!-- samples-ms: 3.3471,3.1379,3.3446,3.2208,3.3655,3.3572,3.3719 -->
| fsdb-wal | 10000 | words durable update pair | 0.7623 | 0.7084 | 0.8608 |
<!-- samples-ms: 0.7487,0.8061,0.7084,0.8155,0.8608,0.7536,0.7623 -->
| fsdb-wal | 10000 | grams durable update pair | 0.6255 | 0.6144 | 0.6401 |
<!-- samples-ms: 0.6401,0.6144,0.6310,0.6200,0.6255,0.6334,0.6170 -->
| mysql | 1000 | Word natural | 0.4043 | 0.4013 | 0.4194 |
<!-- samples-ms: 0.4026,0.4127,0.4043,0.4043,0.4098,0.4194,0.4013 -->
| mysql | 1000 | Word natural phrase | 1.0180 | 1.0020 | 1.0319 |
<!-- samples-ms: 1.0319,1.0180,1.0260,1.0091,1.0189,1.0145,1.0020 -->
| mysql | 1000 | Word phrase expansion | 2.3253 | 2.2949 | 2.3283 |
<!-- samples-ms: 2.3283,2.3275,2.3253,2.3136,2.3259,2.3114,2.2949 -->
| mysql | 1000 | Word boolean | 0.5634 | 0.5574 | 0.5716 |
<!-- samples-ms: 0.5632,0.5654,0.5665,0.5623,0.5716,0.5574,0.5634 -->
| mysql | 1000 | Ngram natural | 0.4008 | 0.3915 | 0.4067 |
<!-- samples-ms: 0.4046,0.4010,0.3915,0.3959,0.4001,0.4067,0.4008 -->
| mysql | 1000 | Ngram natural phrase | 0.5981 | 0.5968 | 0.6165 |
<!-- samples-ms: 0.5968,0.6165,0.5981,0.5996,0.5975,0.5969,0.6015 -->
| mysql | 1000 | Ngram boolean phrase | 1.0559 | 1.0416 | 1.0639 |
<!-- samples-ms: 1.0562,1.0551,1.0639,1.0553,1.0565,1.0416,1.0559 -->
| mysql | 1000 | Ngram prefix | 0.3981 | 0.3938 | 0.4112 |
<!-- samples-ms: 0.3941,0.4112,0.4027,0.3981,0.3938,0.3943,0.4003 -->
| mysql | 1000 | words durable update pair | 0.3689 | 0.3451 | 0.6796 |
<!-- samples-ms: 0.3689,0.3845,0.6796,0.3451,0.3563,0.3720,0.3654 -->
| mysql | 1000 | grams durable update pair | 0.3722 | 0.3623 | 0.3879 |
<!-- samples-ms: 0.3701,0.3879,0.3859,0.3663,0.3725,0.3623,0.3722 -->
| mysql | 10000 | Word natural | 3.2692 | 3.2203 | 3.3723 |
<!-- samples-ms: 3.2462,3.2705,3.2771,3.2638,3.2203,3.2692,3.3723 -->
| mysql | 10000 | Word natural phrase | 9.5472 | 9.5274 | 9.6289 |
<!-- samples-ms: 9.6289,9.5372,9.5274,9.5472,9.5462,9.5844,9.5541 -->
| mysql | 10000 | Word phrase expansion | 22.6222 | 22.4182 | 22.6999 |
<!-- samples-ms: 22.6999,22.6162,22.4855,22.4182,22.6627,22.6427,22.6222 -->
| mysql | 10000 | Word boolean | 5.0300 | 4.9498 | 5.0651 |
<!-- samples-ms: 5.0400,4.9789,5.0651,4.9498,5.0158,5.0300,5.0639 -->
| mysql | 10000 | Ngram natural | 3.2165 | 3.1884 | 3.2332 |
<!-- samples-ms: 3.2178,3.1982,3.1884,3.2332,3.2165,3.2096,3.2306 -->
| mysql | 10000 | Ngram natural phrase | 5.2940 | 5.2614 | 5.3502 |
<!-- samples-ms: 5.3502,5.2614,5.3272,5.2940,5.3143,5.2800,5.2853 -->
| mysql | 10000 | Ngram boolean phrase | 9.8756 | 9.8484 | 9.9312 |
<!-- samples-ms: 9.8864,9.9307,9.8484,9.8615,9.8619,9.8756,9.9312 -->
| mysql | 10000 | Ngram prefix | 3.2143 | 3.1764 | 3.2452 |
<!-- samples-ms: 3.1884,3.2162,3.2445,3.2143,3.2452,3.1856,3.1764 -->
| mysql | 10000 | words durable update pair | 0.3289 | 0.3032 | 0.3387 |
<!-- samples-ms: 0.3289,0.3032,0.3314,0.3292,0.3207,0.3270,0.3387 -->
| mysql | 10000 | grams durable update pair | 0.3211 | 0.3171 | 0.3704 |
<!-- samples-ms: 0.3704,0.3174,0.3211,0.3184,0.3171,0.3306,0.3380 -->
