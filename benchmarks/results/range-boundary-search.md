# Binary-search RANGE boundary measurements

Status: measured RANGE improvement on a shared host; exact factors are not
stable performance guarantees.

The offset RANGE workload selects SUM(id) over ten preceding values through
the current row. Before this change, each output row scans the whole sorted
partition to find its bounds. Comparable numeric and temporal partitions now
use up to two binary searches after one linear eligibility check. Mixed exact and
approximate values, or otherwise incompatible comparison domains, retain the
scan fallback. Aggregate folding cost remains dependent on frame contents.

| Rows | Baseline ms | Binary search ms | MySQL ms | Observed median ratio |
|---:|---:|---:|---:|---:|
| 1000 | 69.69 | 7.22 | 2.42 | 9.6x |
| 5000 | 741.56 | 29.00 | 6.51 | 25.6x |
| 10000 | 4377.98 | 109.30 | 17.68 | 40.1x |

All candidate RANGE samples are faster than all baseline RANGE samples at
each tested size. Competing host load and control drift limit the precision
of the factors above, but the reduction is much larger than the observed
within-run timing variation. Stored-function inputs remain a separate cost:
at 5,000 rows their medians are 1,123.74 ms baseline and 1,108.75 ms candidate,
versus MySQL's 26.06 ms. The overlapping ranges for those measurements do not
establish a change in stored-function performance.

Measured 2026-10-07 UTC on Apple M2 Max, macOS 15.6, .NET SDK 10.0.401 /
runtime 10.0.12. The baseline is Release `10d2a1d8`; the candidate is
`319dad19` with the Executor binary-search patch. The source diff SHA-256 is
`fd21d15fe4967cb364bd660ad750af4cbeb2843db28fa5c0f6d0130f3035f266`.

The [comparison harness](../scripts/window-comparison.fsx) and query shapes
are unchanged from the [preceding snapshot](10d2a1d8-windows.md). Targets run
sequentially in rotating order with at least three queries and two seconds of
warmup per workload, nine measured samples, and checked row counts/checksums.
The stored-function workload includes its counter reset and uses standalone
SET followed by RETURN. Point controls use seven batches of 100 queries.

Servers run natively on localhost with no affinity or processor-count override.
fsdb is in memory; MySQL 8.4.11 has normal durability and a 64 MiB buffer pool.
Schema creation and inserts are outside the timings. These are read-path
measurements, not durable write comparisons. No build, test suite, or profiler
runs during the comparison. All benchmark servers stop and disposable MySQL
data is removed after the successful run.

Unity's main process moves from 0.6% CPU before to 302.1% after; BoatX moves
from 326.1% to 190.9%, using macOS ps percentages. Neither application is
changed. Control medians show the instability directly:

| Rows | Target | Before ms | After ms | Change |
|---:|---|---:|---:|---:|
| 1000 | baseline | 1.2525 | 0.2573 | -79.5% |
| 1000 | candidate | 1.1222 | 0.2876 | -74.4% |
| 1000 | mysql | 0.1663 | 0.0765 | -54.0% |
| 5000 | baseline | 0.2861 | 0.2538 | -11.3% |
| 5000 | candidate | 0.5360 | 0.2583 | -51.8% |
| 5000 | mysql | 0.0652 | 0.0750 | +15.0% |
| 10000 | baseline | 0.2832 | 0.3430 | +21.1% |
| 10000 | candidate | 0.2689 | 0.3429 | +27.5% |
| 10000 | mysql | 0.0823 | 0.0904 | +9.8% |

## Profile evidence

The earlier Release `2df67bf6` profile identified frame-bound scans as the
hotspot. The candidate profile uses the same 5,000-row query through the
[maintained workload](../scripts/range-profile.fsx), which completes 1,195
checksum-verified queries in 40.014 seconds. Profiling is separate from the
untraced timings above. The earlier profile predates the correctness fixes;
the timing comparison uses `10d2a1d8` to isolate the binary-search change.

Both traces use dotnet-trace 10.0.731102, a 30-second collection with
`dotnet-sampled-thread-time`, and a 32 MiB buffer. Percentages include idle
thread time and are **not CPU-utilization percentages**:

| Function | Earlier inclusive sample share | Candidate inclusive sample share |
|---|---:|---:|
| Window computeColumn | 8.90% | 5.14% |
| frameRange | 8.62% | 1.30% |
| Value.compare | 4.90% | 0.22% |
| compareRangeValues | 0.97% | 0.08% |

Frame-range work represents roughly 97% of sampled window-computation time
in the earlier trace and 25% in the candidate trace. Collection uses:

```sh
dotnet-trace collect --process-id <fsdb-pid> --profile dotnet-sampled-thread-time --buffersize 32 --duration 00:00:30 --output range.nettrace
dotnet-trace report range.nettrace topN --number 100 --inclusive
```

Set `FSDB_PERF_CONNECTION` to an isolated server and run
`dotnet fsi --nologo benchmarks/scripts/range-profile.fsx` for the profile
workload. Its uniquely named database is removed in a finally block.

## Correctness and build evidence

`just check` passes all 2,849 tests with zero warnings or errors. The
[differential manifest](../../torture/artifacts/runs/20261007T000112630-26310/contracts/manifest.json)
records 4,551 steps in 43 cases with zero differences, including numeric and
temporal boundaries, NULL peers, both directions, integer overflow, prepared
execution, and warning ordering. Both Debug and Release builds succeed.

Assembly SHA-256 values:

- Baseline: `8b5376ae8f3cba3ad5abb604ef64f780df37acb9972c892211e83245c5dd002f`.
- Candidate: `04ad44b8dba81bd0541e93cf44961f6df4aa43cad3fda20da17c6bc714488815`.

## Raw timings

Times are milliseconds. HTML comments preserve samples in execution order.

<!-- baseline server: 8.4.0-fsdb -->
<!-- candidate server: 8.4.0-fsdb -->
<!-- mysql server: 8.4.11 -->
| Rows | Target | Workload | Median ms | Min ms | Max ms |
|---:|---|---|---:|---:|---:|
| 1000 | baseline | Point lookup before | 1.2525 | 0.9570 | 1.6501 |
<!-- samples-ms: 1.2929,1.2525,1.6501,1.3326,1.1176,0.9570,0.9700 -->
| 1000 | candidate | Point lookup before | 1.1222 | 0.8042 | 1.1482 |
<!-- samples-ms: 1.1482,1.0067,1.1480,1.1222,1.1363,0.9119,0.8042 -->
| 1000 | mysql | Point lookup before | 0.1663 | 0.1392 | 0.1852 |
<!-- samples-ms: 0.1663,0.1600,0.1574,0.1392,0.1704,0.1852,0.1790 -->
| 1000 | baseline | Grouped text SUM | 6.2861 | 5.2048 | 7.2683 |
<!-- samples-ms: 6.2861,7.0655,5.4480,7.2366,5.4502,7.2683,5.2048,7.2364,5.5265 -->
| 1000 | candidate | Grouped text SUM | 6.7440 | 4.7131 | 8.1496 |
<!-- samples-ms: 8.1496,5.2187,6.9488,4.7131,7.1277,5.2109,7.1332,4.9196,6.7440 -->
| 1000 | mysql | Grouped text SUM | 0.6806 | 0.5822 | 0.7670 |
<!-- samples-ms: 0.6140,0.5947,0.6806,0.5822,0.7117,0.7282,0.6201,0.6897,0.7670 -->
| 1000 | baseline | Prefix ROWS SUM | 3.7514 | 3.4145 | 5.6836 |
<!-- samples-ms: 5.0586,5.4080,3.7514,3.7468,5.6836,3.4145,5.1400,3.5091,3.5613 -->
| 1000 | candidate | Prefix ROWS SUM | 3.7650 | 3.4477 | 5.7384 |
<!-- samples-ms: 4.3022,5.4845,3.7095,5.2634,3.6126,3.4477,5.7384,3.5997,3.7650 -->
| 1000 | mysql | Prefix ROWS SUM | 1.4057 | 1.2613 | 1.5183 |
<!-- samples-ms: 1.2613,1.3780,1.2832,1.5183,1.4117,1.4057,1.4997,1.3343,1.4480 -->
| 1000 | baseline | Sliding ROWS SUM | 8.8567 | 7.0565 | 10.2912 |
<!-- samples-ms: 7.7428,10.2912,7.3274,9.6808,7.1313,9.5866,7.0565,8.8567,10.0726 -->
| 1000 | candidate | Sliding ROWS SUM | 9.2417 | 7.1646 | 10.1422 |
<!-- samples-ms: 9.2417,10.1422,7.1646,9.8097,7.1740,10.1177,7.1698,9.7825,8.8924 -->
| 1000 | mysql | Sliding ROWS SUM | 2.4008 | 2.2750 | 2.5355 |
<!-- samples-ms: 2.3256,2.4402,2.2896,2.2879,2.2750,2.4462,2.4362,2.5355,2.4008 -->
| 1000 | baseline | Offset RANGE SUM | 69.6888 | 45.5450 | 286.4341 |
<!-- samples-ms: 63.4342,48.8790,45.5450,286.4341,80.4787,65.1557,76.6523,102.1901,69.6888 -->
| 1000 | candidate | Offset RANGE SUM | 7.2218 | 4.4271 | 15.1467 |
<!-- samples-ms: 6.6064,5.7647,6.3150,4.4271,7.2218,11.6148,15.1467,10.0830,9.9008 -->
| 1000 | mysql | Offset RANGE SUM | 2.4160 | 1.8606 | 4.1283 |
<!-- samples-ms: 2.2693,2.2275,2.1173,1.8606,2.4160,2.7607,4.1283,4.0402,3.3874 -->
| 1000 | baseline | Stored-function prefix SUM | 193.5515 | 182.4315 | 462.9530 |
<!-- samples-ms: 246.2740,462.9530,371.5155,371.3470,193.5515,182.9083,182.4315,184.6171,183.2294 -->
| 1000 | candidate | Stored-function prefix SUM | 353.4401 | 186.5680 | 408.2545 |
<!-- samples-ms: 353.4401,408.2545,378.7105,378.0527,378.5690,188.1093,186.5680,189.8403,189.3187 -->
| 1000 | mysql | Stored-function prefix SUM | 7.0840 | 3.2637 | 9.6462 |
<!-- samples-ms: 9.6462,8.1922,7.0987,7.1520,7.0840,3.6247,3.2637,3.3761,3.5423 -->
| 1000 | baseline | Point lookup after | 0.2573 | 0.2546 | 0.3170 |
<!-- samples-ms: 0.3170,0.2566,0.2609,0.2546,0.2547,0.2573,0.2585 -->
| 1000 | candidate | Point lookup after | 0.2876 | 0.2482 | 0.3115 |
<!-- samples-ms: 0.3115,0.2992,0.3023,0.2876,0.2649,0.2482,0.2529 -->
| 1000 | mysql | Point lookup after | 0.0765 | 0.0753 | 0.0911 |
<!-- samples-ms: 0.0911,0.0796,0.0761,0.0753,0.0775,0.0762,0.0765 -->
| 5000 | baseline | Point lookup before | 0.2861 | 0.2563 | 0.2987 |
<!-- samples-ms: 0.2865,0.2929,0.2861,0.2698,0.2563,0.2768,0.2987 -->
| 5000 | candidate | Point lookup before | 0.5360 | 0.4755 | 1.3876 |
<!-- samples-ms: 1.3876,1.0875,0.4922,0.5360,0.5612,0.4755,0.4932 -->
| 5000 | mysql | Point lookup before | 0.0652 | 0.0589 | 0.0715 |
<!-- samples-ms: 0.0654,0.0658,0.0589,0.0652,0.0715,0.0624,0.0646 -->
| 5000 | baseline | Grouped text SUM | 13.0875 | 11.7250 | 17.2758 |
<!-- samples-ms: 17.2758,12.2776,12.1047,13.3285,12.7654,13.3252,11.7250,13.0875,13.4132 -->
| 5000 | candidate | Grouped text SUM | 12.8807 | 12.3534 | 13.5430 |
<!-- samples-ms: 12.7524,13.2444,12.5832,13.0880,12.3534,13.3257,12.8807,12.6652,13.5430 -->
| 5000 | mysql | Grouped text SUM | 1.1297 | 1.0286 | 1.2429 |
<!-- samples-ms: 1.0720,1.0737,1.0286,1.2429,1.1720,1.1596,1.1513,1.1266,1.1297 -->
| 5000 | baseline | Prefix ROWS SUM | 15.4974 | 11.1315 | 19.7286 |
<!-- samples-ms: 12.2095,18.1282,16.9777,19.7286,13.0516,16.9239,15.4974,11.1315,12.7087 -->
| 5000 | candidate | Prefix ROWS SUM | 15.6732 | 12.4218 | 18.2868 |
<!-- samples-ms: 17.3711,14.7240,14.9877,16.9528,18.2868,12.4218,13.9022,15.6732,17.0858 -->
| 5000 | mysql | Prefix ROWS SUM | 2.7030 | 2.5584 | 3.4545 |
<!-- samples-ms: 2.6372,3.3624,2.6283,3.1239,3.4545,2.8556,2.5584,2.7030,2.6836 -->
| 5000 | baseline | Sliding ROWS SUM | 24.9597 | 21.6370 | 33.8657 |
<!-- samples-ms: 29.0391,21.6370,26.9906,33.8657,23.9589,23.0082,28.9092,24.9597,22.2294 -->
| 5000 | candidate | Sliding ROWS SUM | 25.8071 | 21.5910 | 31.4697 |
<!-- samples-ms: 25.7198,24.5098,23.2742,27.2072,28.6296,31.4697,25.8071,26.3147,21.5910 -->
| 5000 | mysql | Sliding ROWS SUM | 4.1810 | 4.0383 | 4.2984 |
<!-- samples-ms: 4.1792,4.0974,4.1810,4.2577,4.1207,4.2049,4.2644,4.0383,4.2984 -->
| 5000 | baseline | Offset RANGE SUM | 741.5626 | 679.7668 | 862.1223 |
<!-- samples-ms: 800.4205,699.4912,679.7668,752.6932,744.6962,727.6196,741.5626,712.3170,862.1223 -->
| 5000 | candidate | Offset RANGE SUM | 29.0012 | 27.4392 | 38.6763 |
<!-- samples-ms: 38.6763,28.5998,29.0012,27.6085,31.1287,37.2091,28.6652,27.4392,32.8550 -->
| 5000 | mysql | Offset RANGE SUM | 6.5120 | 6.1494 | 7.2257 |
<!-- samples-ms: 6.5007,6.6790,6.2902,6.5110,6.1494,6.5120,7.2257,6.6141,6.5858 -->
| 5000 | baseline | Stored-function prefix SUM | 1123.7390 | 846.1994 | 1994.2624 |
<!-- samples-ms: 860.1077,1908.6163,901.5970,846.1994,1043.0855,1123.7390,1232.9317,1994.2624,1430.2459 -->
| 5000 | candidate | Stored-function prefix SUM | 1108.7453 | 858.8816 | 2146.5832 |
<!-- samples-ms: 1206.5084,1129.2469,858.8816,915.8085,932.7096,1108.7453,1838.3153,2146.5832,1050.8501 -->
| 5000 | mysql | Stored-function prefix SUM | 26.0614 | 14.6816 | 33.9842 |
<!-- samples-ms: 29.9638,30.4820,14.6992,14.6816,16.3931,19.0350,33.9842,28.0498,26.0614 -->
| 5000 | baseline | Point lookup after | 0.2538 | 0.2336 | 0.2918 |
<!-- samples-ms: 0.2918,0.2407,0.2336,0.2606,0.2467,0.2538,0.2550 -->
| 5000 | candidate | Point lookup after | 0.2583 | 0.2487 | 0.3091 |
<!-- samples-ms: 0.3091,0.2583,0.2487,0.2626,0.2565,0.2649,0.2549 -->
| 5000 | mysql | Point lookup after | 0.0750 | 0.0738 | 0.0942 |
<!-- samples-ms: 0.0942,0.0739,0.0757,0.0750,0.0751,0.0738,0.0738 -->
| 10000 | baseline | Point lookup before | 0.2832 | 0.2795 | 0.3140 |
<!-- samples-ms: 0.2944,0.3111,0.3140,0.2832,0.2828,0.2795,0.2797 -->
| 10000 | candidate | Point lookup before | 0.2689 | 0.2512 | 0.2984 |
<!-- samples-ms: 0.2984,0.2799,0.2845,0.2689,0.2575,0.2551,0.2512 -->
| 10000 | mysql | Point lookup before | 0.0823 | 0.0790 | 0.0905 |
<!-- samples-ms: 0.0844,0.0800,0.0823,0.0800,0.0905,0.0790,0.0854 -->
| 10000 | baseline | Grouped text SUM | 30.5619 | 28.8129 | 31.5897 |
<!-- samples-ms: 30.5619,31.1639,29.6464,31.5520,30.6222,30.3659,30.2620,31.5897,28.8129 -->
| 10000 | candidate | Grouped text SUM | 30.8280 | 27.5871 | 40.5094 |
<!-- samples-ms: 31.1183,40.5094,27.5871,30.4235,31.3459,30.8280,31.4474,29.9295,30.1400 -->
| 10000 | mysql | Grouped text SUM | 2.3430 | 2.2572 | 2.4926 |
<!-- samples-ms: 2.2572,2.2868,2.3140,2.3756,2.3389,2.4926,2.3430,2.4870,2.4862 -->
| 10000 | baseline | Prefix ROWS SUM | 48.2247 | 40.8970 | 57.6397 |
<!-- samples-ms: 40.8970,41.6870,50.2041,41.8906,48.2247,57.6397,41.7537,56.0182,48.3967 -->
| 10000 | candidate | Prefix ROWS SUM | 48.1108 | 38.9965 | 55.2773 |
<!-- samples-ms: 48.5045,40.0296,48.1108,41.1539,38.9965,53.8190,42.4497,55.2773,48.4972 -->
| 10000 | mysql | Prefix ROWS SUM | 6.4089 | 5.8206 | 6.5216 |
<!-- samples-ms: 6.5216,6.4737,6.4850,6.4089,6.4040,6.4493,6.2302,5.8206,6.3391 -->
| 10000 | baseline | Sliding ROWS SUM | 61.5568 | 48.7081 | 85.0921 |
<!-- samples-ms: 77.3893,81.1161,85.0921,48.7081,61.8827,61.5568,51.4837,58.3061,60.3010 -->
| 10000 | candidate | Sliding ROWS SUM | 68.2909 | 49.5014 | 87.1682 |
<!-- samples-ms: 83.6889,87.1682,68.3601,69.0342,68.2909,49.5654,58.1914,62.6608,49.5014 -->
| 10000 | mysql | Sliding ROWS SUM | 8.2084 | 7.1963 | 10.5517 |
<!-- samples-ms: 10.5517,9.9077,10.3300,10.5469,8.2084,7.3908,7.1963,7.2066,7.3721 -->
| 10000 | baseline | Offset RANGE SUM | 4377.9815 | 3257.6925 | 5201.9586 |
<!-- samples-ms: 4137.7951,4082.2080,4230.0645,4759.5613,5201.9586,4884.5042,4597.1682,3257.6925,4377.9815 -->
| 10000 | candidate | Offset RANGE SUM | 109.2990 | 70.3126 | 136.1631 |
<!-- samples-ms: 88.4455,80.0525,104.5197,109.2990,127.7177,136.1631,112.0080,70.3126,126.5804 -->
| 10000 | mysql | Offset RANGE SUM | 17.6810 | 12.4198 | 22.5211 |
<!-- samples-ms: 16.5480,17.1423,17.6810,19.1490,22.5211,20.3202,22.4665,12.9320,12.4198 -->
| 10000 | baseline | Stored-function prefix SUM | 2352.5584 | 1765.9813 | 4226.9955 |
<!-- samples-ms: 2790.9561,4226.9955,2532.9614,2523.9410,2352.5584,1765.9813,1846.2421,2191.9315,2042.4960 -->
| 10000 | candidate | Stored-function prefix SUM | 2282.7130 | 1817.8444 | 4350.2256 |
<!-- samples-ms: 2627.4231,4350.2256,2282.7130,2429.7660,1817.8444,2819.8604,1961.9063,2086.9435,2089.8840 -->
| 10000 | mysql | Stored-function prefix SUM | 32.8563 | 30.0341 | 80.1746 |
<!-- samples-ms: 43.5245,80.1746,54.3763,38.7719,30.0341,30.1609,32.8563,31.0945,32.6710 -->
| 10000 | baseline | Point lookup after | 0.3430 | 0.3315 | 0.4162 |
<!-- samples-ms: 0.4162,0.3315,0.3394,0.3430,0.3456,0.3573,0.3399 -->
| 10000 | candidate | Point lookup after | 0.3429 | 0.3327 | 0.4463 |
<!-- samples-ms: 0.4463,0.3422,0.3495,0.3327,0.3429,0.3427,0.3632 -->
| 10000 | mysql | Point lookup after | 0.0904 | 0.0876 | 0.1060 |
<!-- samples-ms: 0.1060,0.0898,0.0876,0.0913,0.0935,0.0892,0.0904 -->
