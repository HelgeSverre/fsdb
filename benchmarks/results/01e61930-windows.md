# Aggregate and window performance snapshot

Status: exploratory; excluded from regression and speedup conclusions.

Measured 2026-10-06 UTC (2026-10-07 Europe/Oslo) on an Apple M2 Max,
macOS 15.6, .NET SDK 10.0.401 / runtime 10.0.12, native MySQL 8.4.11.
fsdb used a Release build of `01e61930` plus the pending behavior-preserving
SET parser extraction. Its source diff SHA-256 is
`68b1bf4fae3056722e1a543414275ebccacd1ea746d4736d64be259cbb3f5542`.
The standalone stored-function SET feature is not implemented in this snapshot.

Both servers ran natively on localhost with normal priority, no CPU affinity or
processor-count override. fsdb was in memory; MySQL used normal durability and
a 64 MiB buffer pool. Only query execution was timed, so this is not evidence
about write durability. Build concurrency was four; compilation finished before
measurement. No correctness suite ran alongside measurement. Unity and a
simulator were each observed consuming about 270% CPU during the run; they were
not stopped or changed.

At 5,000 rows, observed median times were:

| Workload | fsdb ms | MySQL ms |
|---|---:|---:|
| Grouped text SUM | 10.63 | 1.62 |
| Prefix ROWS SUM | 21.71 | 5.09 |
| Sliding ROWS SUM, 10 preceding | 28.34 | 7.42 |
| Offset RANGE SUM, 10 preceding | 886.66 | 12.18 |
| Stored-function prefix SUM | 599.74 | 15.75 |

Offset RANGE boundary lookup and stored-function invocation are candidates for
profiling. The RANGE implementation checks partition rows to find the bounds
for each output row; the growing ROWS SUM accumulates each input once. The
snapshot does not isolate invocation from variable publication, parsing, or
other work inside the stored-function path, and includes no historical window
baseline. No before/after improvement or regression is established.

The control fails the repository's 20% repeatability criterion at smaller
sizes: fsdb at 1,000 rows changes from 0.2191 to 0.3169 ms (+44.6%); MySQL at
100 rows changes from 0.0851 to 0.1616 ms (+89.9%). At 5,000 rows the controls
change by -1.1% for fsdb and -13.0% for MySQL. These steadier controls do not
remove the host-load and sequential-target limitations of the overall run.
An earlier three-call-warmup pilot also drifted and was discarded.

## Reproduction and validation

Run [window-snapshot.fsx](../scripts/window-snapshot.fsx) with isolated server
connections as described in the [benchmark README](../README.md#focused-window-snapshot).
The measured table has one integer primary key populated with 1 through N.
The client keeps one connection open, creates a command per query, consumes
all rows, and converts the first column to decimal for a checksum. It warms
each workload for at least two seconds and three batches, then records seven
samples. Point controls use 100 queries per sample; aggregate samples use one.
Function counter resets and all seeding occur outside timing. The targets run
sequentially, fsdb then MySQL. This is a Stopwatch snapshot, not BenchmarkDotNet.

All result counts and numeric totals matched the expected formulas on both
engines. The stored-function counter matched the input row count at every
size. The Release build passed with no warnings. Both disposable servers
stopped and their temporary data was removed after successful completion.

## Raw measurements

All times are milliseconds. Comments retain the individual samples in order.

| Target | Rows | Workload | Median ms | Min ms | Max ms |
|---|---:|---|---:|---:|---:|
| fsdb | 100 | Point lookup before | 0.6835 | 0.6217 | 0.7280 |
<!-- samples-ms: 0.6415,0.6217,0.6725,0.7280,0.6835,0.7274,0.7136 -->
| fsdb | 100 | Grouped text SUM | 1.0310 | 0.8252 | 2.3022 |
<!-- samples-ms: 1.0320,1.0310,0.8828,2.3022,1.0479,0.8252,0.9266 -->
| fsdb | 100 | Prefix ROWS SUM | 0.7674 | 0.7131 | 0.9271 |
<!-- samples-ms: 0.8076,0.7131,0.9271,0.7670,0.8336,0.7265,0.7674 -->
| fsdb | 100 | Sliding ROWS SUM | 1.0455 | 0.8355 | 1.4467 |
<!-- samples-ms: 0.8355,1.4467,1.0455,0.9710,1.4400,1.2592,0.9185 -->
| fsdb | 100 | Offset RANGE SUM | 2.6911 | 2.0445 | 4.2461 |
<!-- samples-ms: 2.9005,2.6911,2.7495,2.0445,2.2667,2.6347,4.2461 -->
| fsdb | 100 | Stored-function prefix SUM | 33.5879 | 30.2511 | 38.6601 |
<!-- samples-ms: 30.3106,31.7362,30.2511,38.6601,34.5760,36.1776,33.5879 -->
| fsdb | 100 | Point lookup after | 0.5820 | 0.5453 | 0.6109 |
<!-- samples-ms: 0.5613,0.5820,0.5809,0.5931,0.6109,0.6080,0.5453 -->
| fsdb | 1000 | Point lookup before | 0.2191 | 0.2045 | 0.3205 |
<!-- samples-ms: 0.3205,0.2485,0.2222,0.2191,0.2140,0.2066,0.2045 -->
| fsdb | 1000 | Grouped text SUM | 3.5655 | 2.6922 | 6.1147 |
<!-- samples-ms: 2.6922,6.1147,3.0189,3.5655,5.9739,3.5537,6.0635 -->
| fsdb | 1000 | Prefix ROWS SUM | 4.5106 | 2.8190 | 7.7759 |
<!-- samples-ms: 7.5860,5.2951,3.7534,7.7759,3.7763,4.5106,2.8190 -->
| fsdb | 1000 | Sliding ROWS SUM | 4.8697 | 3.9938 | 6.1721 |
<!-- samples-ms: 3.9938,6.1721,4.8697,4.6486,5.6388,4.2374,5.9967 -->
| fsdb | 1000 | Offset RANGE SUM | 104.7045 | 88.2610 | 119.5968 |
<!-- samples-ms: 119.5968,112.1465,106.5142,103.9884,104.7045,95.6357,88.2610 -->
| fsdb | 1000 | Stored-function prefix SUM | 138.3339 | 132.7065 | 139.1224 |
<!-- samples-ms: 138.4194,138.3339,138.0248,139.1224,138.9647,136.7717,132.7065 -->
| fsdb | 1000 | Point lookup after | 0.3169 | 0.3058 | 0.3247 |
<!-- samples-ms: 0.3058,0.3143,0.3113,0.3213,0.3237,0.3169,0.3247 -->
| fsdb | 5000 | Point lookup before | 0.3196 | 0.3133 | 0.3452 |
<!-- samples-ms: 0.3191,0.3196,0.3352,0.3162,0.3199,0.3133,0.3452 -->
| fsdb | 5000 | Grouped text SUM | 10.6312 | 9.8834 | 11.7012 |
<!-- samples-ms: 10.6312,11.2381,10.4354,11.7012,11.5264,9.8834,10.5052 -->
| fsdb | 5000 | Prefix ROWS SUM | 21.7137 | 14.8962 | 27.7294 |
<!-- samples-ms: 27.4076,22.3776,14.8962,15.6286,20.7543,27.7294,21.7137 -->
| fsdb | 5000 | Sliding ROWS SUM | 28.3360 | 23.9052 | 35.0736 |
<!-- samples-ms: 23.9052,35.0736,30.9924,28.3360,26.5835,32.3670,28.0970 -->
| fsdb | 5000 | Offset RANGE SUM | 886.6620 | 867.3134 | 954.7447 |
<!-- samples-ms: 891.4685,871.7185,954.7447,886.6620,872.4130,867.3134,905.8055 -->
| fsdb | 5000 | Stored-function prefix SUM | 599.7442 | 592.3244 | 616.2265 |
<!-- samples-ms: 616.2265,607.4785,599.7442,596.7697,595.8584,611.9250,592.3244 -->
| fsdb | 5000 | Point lookup after | 0.3161 | 0.3083 | 0.3244 |
<!-- samples-ms: 0.3133,0.3161,0.3088,0.3244,0.3177,0.3083,0.3213 -->
| mysql | 100 | Point lookup before | 0.0851 | 0.0828 | 0.0899 |
<!-- samples-ms: 0.0840,0.0851,0.0828,0.0866,0.0895,0.0828,0.0899 -->
| mysql | 100 | Grouped text SUM | 0.0576 | 0.0491 | 0.0843 |
<!-- samples-ms: 0.0801,0.0750,0.0843,0.0537,0.0555,0.0491,0.0576 -->
| mysql | 100 | Prefix ROWS SUM | 0.1392 | 0.0979 | 0.2298 |
<!-- samples-ms: 0.1365,0.1239,0.1695,0.2298,0.1392,0.1682,0.0979 -->
| mysql | 100 | Sliding ROWS SUM | 0.2616 | 0.1615 | 0.4737 |
<!-- samples-ms: 0.3140,0.4737,0.1798,0.2078,0.1615,0.2720,0.2616 -->
| mysql | 100 | Offset RANGE SUM | 0.2299 | 0.1682 | 0.5601 |
<!-- samples-ms: 0.1941,0.1682,0.2299,0.5601,0.1705,0.2583,0.2711 -->
| mysql | 100 | Stored-function prefix SUM | 0.9062 | 0.4516 | 1.1486 |
<!-- samples-ms: 0.4516,0.8531,0.9535,0.7903,1.0976,1.1486,0.9062 -->
| mysql | 100 | Point lookup after | 0.1616 | 0.1455 | 0.1792 |
<!-- samples-ms: 0.1610,0.1455,0.1634,0.1598,0.1792,0.1777,0.1616 -->
| mysql | 1000 | Point lookup before | 0.0888 | 0.0826 | 0.0916 |
<!-- samples-ms: 0.0888,0.0863,0.0892,0.0899,0.0856,0.0916,0.0826 -->
| mysql | 1000 | Grouped text SUM | 0.2791 | 0.1988 | 0.4738 |
<!-- samples-ms: 0.4738,0.2791,0.3841,0.3307,0.2311,0.1988,0.1995 -->
| mysql | 1000 | Prefix ROWS SUM | 1.2760 | 1.0945 | 1.9282 |
<!-- samples-ms: 1.9282,1.2100,1.3196,1.7847,1.2642,1.2760,1.0945 -->
| mysql | 1000 | Sliding ROWS SUM | 2.1006 | 1.6169 | 2.8205 |
<!-- samples-ms: 1.8242,2.3415,1.8276,2.5187,2.1006,1.6169,2.8205 -->
| mysql | 1000 | Offset RANGE SUM | 2.6843 | 2.0995 | 3.2130 |
<!-- samples-ms: 2.6374,2.3465,2.9730,2.0995,3.1532,2.6843,3.2130 -->
| mysql | 1000 | Stored-function prefix SUM | 3.1284 | 2.6465 | 4.3282 |
<!-- samples-ms: 3.1284,3.9407,2.9790,4.3282,2.9072,2.6465,3.4498 -->
| mysql | 1000 | Point lookup after | 0.1323 | 0.1214 | 0.1450 |
<!-- samples-ms: 0.1297,0.1272,0.1450,0.1382,0.1402,0.1323,0.1214 -->
| mysql | 5000 | Point lookup before | 0.1606 | 0.1475 | 0.1678 |
<!-- samples-ms: 0.1678,0.1606,0.1663,0.1475,0.1581,0.1675,0.1543 -->
| mysql | 5000 | Grouped text SUM | 1.6245 | 1.3482 | 2.0593 |
<!-- samples-ms: 1.7377,1.3482,2.0593,1.6245,1.4328,1.9730,1.4744 -->
| mysql | 5000 | Prefix ROWS SUM | 5.0907 | 4.7312 | 5.7656 |
<!-- samples-ms: 5.0858,5.1259,4.8649,5.0907,5.7656,4.7312,5.1804 -->
| mysql | 5000 | Sliding ROWS SUM | 7.4184 | 7.0095 | 8.6872 |
<!-- samples-ms: 8.2046,7.0095,7.2405,7.4184,8.6872,7.6480,7.1975 -->
| mysql | 5000 | Offset RANGE SUM | 12.1764 | 11.5234 | 12.6333 |
<!-- samples-ms: 11.9424,12.6333,12.2128,12.6066,11.8382,12.1764,11.5234 -->
| mysql | 5000 | Stored-function prefix SUM | 15.7456 | 14.4773 | 15.9271 |
<!-- samples-ms: 15.8315,15.1819,15.4349,14.4773,15.7456,15.9271,15.8669 -->
| mysql | 5000 | Point lookup after | 0.1397 | 0.1280 | 0.1467 |
<!-- samples-ms: 0.1386,0.1454,0.1384,0.1467,0.1397,0.1421,0.1280 -->
