# Window performance after RANGE compatibility fixes

Status: exploratory; not a regression or speedup verdict.

Measured 2026-10-06 UTC / 2026-10-07 Europe/Oslo on Apple M2 Max,
macOS 15.6 (Darwin 24.6.0), .NET SDK 10.0.401 / runtime 10.0.12.
The baseline is Release `2df67bf6`; current is Release `10d2a1d8`.
Native MySQL 8.4.11 is the comparison target. The RANGE binary-search
optimization is not applied in either fsdb build.

| Workload, 5,000 rows | Baseline ms | Current ms | MySQL ms |
|---|---:|---:|---:|
| Grouped text SUM | 9.73 | 12.48 | 1.15 |
| Prefix ROWS SUM | 16.78 | 16.52 | 2.82 |
| Sliding ROWS SUM | 27.14 | 23.20 | 4.10 |
| Offset RANGE SUM | 777.23 | 725.93 | 6.34 |
| Stored-function prefix SUM | 1284.15 | 1332.11 | 21.05 |

At 10,000 rows, current offset RANGE takes 3,020.43 ms versus MySQL's
12.38 ms; stored-function inputs take 2,488.24 ms versus 42.42 ms.
These workloads remain the clearest profiling priorities. Other measurements
and all raw samples appear below. The observed baseline/current differences
are not sufficient to attribute a regression or improvement to the change.

All servers run natively on localhost with normal priority and no processor
count override or CPU affinity. fsdb is in memory; MySQL uses normal durability
and a 64 MiB buffer pool. Schema creation and inserts are outside the timings;
these results do not measure durable write performance. No builds, correctness
tests, or profilers run alongside measurement. Disposable servers stop and
MySQL data is removed after the successful run.

Host load is not controlled: Unity uses about 99.5% CPU before and 0.4% after;
the BoatX simulator uses about 316.8% before and 325.9% after. Neither is stopped
or changed. Point-lookup controls drift substantially, so the timings describe
this run rather than establish repeatable performance changes.

| Rows | Target | Before ms | After ms | Change |
|---:|---|---:|---:|---:|
| 1000 | baseline | 0.8423 | 0.2875 | -65.9% |
| 1000 | candidate | 0.7586 | 0.2862 | -62.3% |
| 1000 | mysql | 0.1219 | 0.0833 | -31.7% |
| 5000 | baseline | 0.2828 | 0.3780 | +33.7% |
| 5000 | candidate | 0.2756 | 0.4056 | +47.2% |
| 5000 | mysql | 0.0678 | 0.1115 | +64.5% |
| 10000 | baseline | 0.4740 | 0.2864 | -39.6% |
| 10000 | candidate | 0.4367 | 0.2862 | -34.5% |
| 10000 | mysql | 0.1107 | 0.0862 | -22.1% |

The [harness](../scripts/window-comparison.fsx) uses 1,000, 5,000, and 10,000
rows, at least three warmup queries and two seconds of warmup per target and
workload, and nine measured queries with rotating target order. Point controls
use seven batches of 100 queries after 200 warmup queries. Every measured
workload verifies row count and aggregate checksum. Stored-function warmup
also checks that the function ran once per input row.

The function uses standalone SET followed by RETURN:

```sql
CREATE FUNCTION tick() RETURNS INT NOT DETERMINISTIC NO SQL
BEGIN
  SET @n=COALESCE(@n,0)+1;
  RETURN @n;
END;
```

The timed stored-function operation includes `SET @n=0` and the SELECT.
This differs from the RETURN-assignment function used in the earlier
[window snapshot](01e61930-windows.md), so those timings are not a direct
before/after comparison.

To reproduce, start the two specified Release builds and native MySQL 8.4.11
on separate local ports, then set `FSDB_BASELINE_CONNECTION`,
`FSDB_CANDIDATE_CONNECTION`, and `FSDB_MYSQL_CONNECTION` to their connection
strings with `Allow User Variables=true;Pooling=false`. The harness creates
and drops its own uniquely named database on each target. With the test
project built to provide MySqlConnector, run:

```sh
dotnet fsi --nologo benchmarks/scripts/window-comparison.fsx
```

Assembly SHA-256 values:

- Baseline: `741f2f5df55d15a6653200436c816c688a8698cab8abc118c246ac85541ae5c8`.
- Current: `8b5376ae8f3cba3ad5abb604ef64f780df37acb9972c892211e83245c5dd002f`.

Raw measurements follow; each HTML comment retains the samples in execution
order. All times are milliseconds.

<!-- baseline server: 8.4.0-fsdb -->
<!-- candidate server: 8.4.0-fsdb -->
<!-- mysql server: 8.4.11 -->
| Rows | Target | Workload | Median ms | Min ms | Max ms |
|---:|---|---|---:|---:|---:|
| 1000 | baseline | Point lookup before | 0.8423 | 0.7492 | 0.8885 |
<!-- samples-ms: 0.8885,0.8423,0.8703,0.8692,0.8233,0.7492,0.7496 -->
| 1000 | candidate | Point lookup before | 0.7586 | 0.7293 | 0.9558 |
<!-- samples-ms: 0.8327,0.8887,0.9558,0.7430,0.7293,0.7325,0.7586 -->
| 1000 | mysql | Point lookup before | 0.1219 | 0.1134 | 0.1361 |
<!-- samples-ms: 0.1361,0.1219,0.1221,0.1225,0.1177,0.1213,0.1134 -->
| 1000 | baseline | Grouped text SUM | 5.7931 | 5.0115 | 9.2178 |
<!-- samples-ms: 8.1190,5.2270,7.3295,5.0115,5.1413,7.0753,5.1619,9.2178,5.7931 -->
| 1000 | candidate | Grouped text SUM | 7.9715 | 5.9082 | 9.6286 |
<!-- samples-ms: 8.8123,6.3270,9.6286,5.9082,8.9688,6.3652,9.2537,7.3329,7.9715 -->
| 1000 | mysql | Grouped text SUM | 0.8432 | 0.6784 | 1.3262 |
<!-- samples-ms: 0.7944,0.6784,1.3262,0.9562,0.7727,0.9095,0.8432,0.9861,0.7195 -->
| 1000 | baseline | Prefix ROWS SUM | 3.5283 | 2.6928 | 5.0750 |
<!-- samples-ms: 4.3984,3.1784,4.3063,3.5283,2.6928,5.0750,3.2715,3.0291,4.0391 -->
| 1000 | candidate | Prefix ROWS SUM | 3.0088 | 2.4191 | 4.9260 |
<!-- samples-ms: 3.6777,4.4477,2.6494,2.6340,4.9260,3.0088,2.7472,4.6767,2.4191 -->
| 1000 | mysql | Prefix ROWS SUM | 1.1036 | 1.0198 | 1.6727 |
<!-- samples-ms: 1.2263,1.0269,1.0595,1.6727,1.1036,1.1763,1.0198,1.1575,1.0749 -->
| 1000 | baseline | Sliding ROWS SUM | 4.6270 | 3.5382 | 5.7235 |
<!-- samples-ms: 4.4264,5.7235,4.4139,4.6270,5.5918,3.5382,5.5177,4.2815,5.2025 -->
| 1000 | candidate | Sliding ROWS SUM | 5.1669 | 3.7131 | 5.9230 |
<!-- samples-ms: 4.3136,5.3840,3.7131,5.1669,5.9230,4.1076,5.5850,3.8870,5.5549 -->
| 1000 | mysql | Sliding ROWS SUM | 1.3370 | 1.2230 | 1.8472 |
<!-- samples-ms: 1.2230,1.7464,1.3180,1.7903,1.3370,1.8472,1.2948,1.4182,1.2352 -->
| 1000 | baseline | Offset RANGE SUM | 45.1498 | 41.5691 | 50.2308 |
<!-- samples-ms: 44.8273,45.1498,45.0942,47.0379,47.9746,48.4214,50.2308,41.5691,43.5207 -->
| 1000 | candidate | Offset RANGE SUM | 42.7506 | 36.1485 | 45.6546 |
<!-- samples-ms: 42.7506,41.9533,43.6124,42.0598,43.3675,42.6582,45.6546,43.6734,36.1485 -->
| 1000 | mysql | Offset RANGE SUM | 2.0458 | 1.8247 | 2.2252 |
<!-- samples-ms: 2.1166,1.8820,1.9613,2.0458,2.1146,2.2252,2.1668,1.9433,1.8247 -->
| 1000 | baseline | Stored-function prefix SUM | 196.3835 | 183.7731 | 200.6564 |
<!-- samples-ms: 183.7731,196.3835,186.2416,192.7583,195.2302,200.6564,196.5355,199.0055,196.7625 -->
| 1000 | candidate | Stored-function prefix SUM | 201.1985 | 182.3660 | 211.2372 |
<!-- samples-ms: 182.3660,199.7602,195.4683,196.0253,210.5919,201.2928,201.1985,211.2372,203.9615 -->
| 1000 | mysql | Stored-function prefix SUM | 4.0043 | 3.3415 | 4.6587 |
<!-- samples-ms: 4.3006,4.6587,3.3415,3.9412,4.0043,4.0202,3.7195,4.0125,3.4437 -->
| 1000 | baseline | Point lookup after | 0.2875 | 0.2837 | 0.3682 |
<!-- samples-ms: 0.3682,0.3288,0.2850,0.2870,0.2906,0.2875,0.2837 -->
| 1000 | candidate | Point lookup after | 0.2862 | 0.2734 | 0.3451 |
<!-- samples-ms: 0.3451,0.3042,0.3130,0.2862,0.2767,0.2762,0.2734 -->
| 1000 | mysql | Point lookup after | 0.0833 | 0.0794 | 0.0918 |
<!-- samples-ms: 0.0918,0.0802,0.0820,0.0905,0.0833,0.0794,0.0853 -->
| 5000 | baseline | Point lookup before | 0.2828 | 0.2603 | 0.3015 |
<!-- samples-ms: 0.3015,0.2828,0.2853,0.2828,0.2616,0.2603,0.2630 -->
| 5000 | candidate | Point lookup before | 0.2756 | 0.2546 | 0.3042 |
<!-- samples-ms: 0.2816,0.2852,0.3042,0.2756,0.2546,0.2586,0.2681 -->
| 5000 | mysql | Point lookup before | 0.0678 | 0.0627 | 0.0779 |
<!-- samples-ms: 0.0678,0.0652,0.0779,0.0672,0.0627,0.0679,0.0748 -->
| 5000 | baseline | Grouped text SUM | 9.7310 | 7.8086 | 10.2726 |
<!-- samples-ms: 9.6409,10.2726,9.5468,9.8869,9.8329,9.7310,7.8086,9.9212,9.5980 -->
| 5000 | candidate | Grouped text SUM | 12.4828 | 11.3378 | 13.9128 |
<!-- samples-ms: 12.6876,11.8255,13.9128,11.8422,12.8283,12.0828,12.7406,11.3378,12.4828 -->
| 5000 | mysql | Grouped text SUM | 1.1498 | 0.9492 | 1.2173 |
<!-- samples-ms: 0.9492,1.0136,1.2032,1.2173,1.1498,1.1797,1.1708,1.0596,0.9807 -->
| 5000 | baseline | Prefix ROWS SUM | 16.7762 | 13.0485 | 22.3465 |
<!-- samples-ms: 21.3550,16.7762,14.6574,22.3465,16.7750,18.3568,16.6249,17.4388,13.0485 -->
| 5000 | candidate | Prefix ROWS SUM | 16.5224 | 12.9481 | 17.3239 |
<!-- samples-ms: 16.6583,17.3239,13.1887,16.5224,16.2885,13.5613,12.9481,17.0802,16.6517 -->
| 5000 | mysql | Prefix ROWS SUM | 2.8155 | 2.5730 | 2.9677 |
<!-- samples-ms: 2.6382,2.8165,2.5730,2.8155,2.9677,2.9516,2.8683,2.7493,2.6835 -->
| 5000 | baseline | Sliding ROWS SUM | 27.1402 | 23.7256 | 29.3741 |
<!-- samples-ms: 24.7266,27.7233,29.3741,23.9968,25.5839,27.1402,23.7256,28.9774,27.3407 -->
| 5000 | candidate | Sliding ROWS SUM | 23.2034 | 20.5890 | 33.7616 |
<!-- samples-ms: 33.7616,21.4749,25.3868,22.7269,20.5890,23.2034,25.5876,25.4845,22.4906 -->
| 5000 | mysql | Sliding ROWS SUM | 4.1049 | 3.5819 | 4.4974 |
<!-- samples-ms: 4.2613,4.4974,3.9787,4.1049,3.9379,4.3289,4.0207,3.5819,4.2530 -->
| 5000 | baseline | Offset RANGE SUM | 777.2326 | 755.2054 | 1509.4448 |
<!-- samples-ms: 819.6543,774.9965,776.4683,755.2054,784.4022,850.5894,758.0195,777.2326,1509.4448 -->
| 5000 | candidate | Offset RANGE SUM | 725.9259 | 701.3274 | 1364.6930 |
<!-- samples-ms: 701.3274,725.9259,723.9862,897.5322,865.6382,748.5561,721.7185,723.8851,1364.6930 -->
| 5000 | mysql | Offset RANGE SUM | 6.3388 | 6.1921 | 9.9620 |
<!-- samples-ms: 6.1921,6.3360,9.9620,6.2991,6.3146,6.3388,6.3986,6.6783,6.5827 -->
| 5000 | baseline | Stored-function prefix SUM | 1284.1502 | 1186.0330 | 1347.0897 |
<!-- samples-ms: 1285.3097,1186.0330,1192.9912,1199.8521,1251.1246,1284.1502,1305.5942,1347.0897,1345.6195 -->
| 5000 | candidate | Stored-function prefix SUM | 1332.1147 | 1195.2982 | 1418.7526 |
<!-- samples-ms: 1275.8364,1195.2982,1295.4553,1332.1147,1320.4696,1332.1276,1354.2252,1418.7526,1417.1337 -->
| 5000 | mysql | Stored-function prefix SUM | 21.0536 | 19.8850 | 23.4527 |
<!-- samples-ms: 19.8850,21.0536,21.1426,20.4494,20.9917,20.2090,23.4527,23.3266,23.4220 -->
| 5000 | baseline | Point lookup after | 0.3780 | 0.3726 | 0.5482 |
<!-- samples-ms: 0.5482,0.3891,0.3929,0.3777,0.3780,0.3726,0.3746 -->
| 5000 | candidate | Point lookup after | 0.4056 | 0.3705 | 0.4662 |
<!-- samples-ms: 0.4662,0.4056,0.3705,0.4275,0.4262,0.3885,0.3799 -->
| 5000 | mysql | Point lookup after | 0.1115 | 0.1087 | 0.1346 |
<!-- samples-ms: 0.1346,0.1128,0.1104,0.1115,0.1104,0.1128,0.1087 -->
| 10000 | baseline | Point lookup before | 0.4740 | 0.4485 | 0.4905 |
<!-- samples-ms: 0.4768,0.4740,0.4905,0.4608,0.4793,0.4553,0.4485 -->
| 10000 | candidate | Point lookup before | 0.4367 | 0.3943 | 0.4924 |
<!-- samples-ms: 0.4082,0.4796,0.4924,0.3943,0.4138,0.4466,0.4367 -->
| 10000 | mysql | Point lookup before | 0.1107 | 0.0981 | 0.1152 |
<!-- samples-ms: 0.0981,0.1107,0.1113,0.1141,0.1152,0.1105,0.1097 -->
| 10000 | baseline | Grouped text SUM | 25.8402 | 18.1157 | 148.9227 |
<!-- samples-ms: 148.9227,48.3632,37.3348,38.3202,21.3099,18.1157,25.8402,19.4327,20.7865 -->
| 10000 | candidate | Grouped text SUM | 45.8698 | 25.1305 | 118.6103 |
<!-- samples-ms: 118.6103,52.0076,55.7922,55.7430,45.8698,25.3542,25.1305,27.5238,31.0055 -->
| 10000 | mysql | Grouped text SUM | 2.3020 | 1.8296 | 6.7480 |
<!-- samples-ms: 6.7480,4.4140,3.8088,4.1667,2.3020,1.8296,2.0283,2.1440,2.0605 -->
| 10000 | baseline | Prefix ROWS SUM | 48.9359 | 36.0553 | 82.1670 |
<!-- samples-ms: 79.1065,82.0262,72.3218,82.1670,36.0553,45.5494,36.2523,48.9359,47.4539 -->
| 10000 | candidate | Prefix ROWS SUM | 43.6979 | 33.5157 | 91.1902 |
<!-- samples-ms: 64.7334,87.5954,64.8836,91.1902,40.9202,43.6979,38.4812,35.7474,33.5157 -->
| 10000 | mysql | Prefix ROWS SUM | 5.3300 | 4.7871 | 10.5784 |
<!-- samples-ms: 9.9645,10.2802,10.5784,10.2818,4.9235,5.3067,4.7871,5.3300,5.0700 -->
| 10000 | baseline | Sliding ROWS SUM | 80.6647 | 72.2529 | 91.9932 |
<!-- samples-ms: 80.0072,72.8416,85.9590,87.7087,75.9303,72.2529,91.9932,83.1652,80.6647 -->
| 10000 | candidate | Sliding ROWS SUM | 83.8840 | 70.3752 | 90.3864 |
<!-- samples-ms: 90.3864,86.8672,82.9871,75.7152,80.7975,87.1584,70.3752,83.8840,85.1842 -->
| 10000 | mysql | Sliding ROWS SUM | 10.5536 | 10.1019 | 11.4577 |
<!-- samples-ms: 11.1472,11.4577,10.3810,11.1064,10.6655,10.5226,10.1560,10.5536,10.1019 -->
| 10000 | baseline | Offset RANGE SUM | 3642.7916 | 3133.3117 | 5785.5331 |
<!-- samples-ms: 5785.5331,4321.8934,3138.9814,3642.7916,3645.5971,3557.9093,3133.3117,3157.0173,4673.9231 -->
| 10000 | candidate | Offset RANGE SUM | 3020.4274 | 2796.9958 | 3357.1295 |
<!-- samples-ms: 3357.1295,2796.9958,2811.1220,3020.4274,3286.5610,3233.6022,3025.9675,2828.0010,2851.4265 -->
| 10000 | mysql | Offset RANGE SUM | 12.3761 | 11.8198 | 24.4949 |
<!-- samples-ms: 24.4949,12.5298,12.0989,12.5498,12.9565,11.8198,12.2765,12.2283,12.3761 -->
| 10000 | baseline | Stored-function prefix SUM | 2366.6817 | 1741.8889 | 5023.8273 |
<!-- samples-ms: 2366.6817,2872.2349,5023.8273,2132.1824,1741.8889,1980.6853,2269.8687,2428.7485,3474.8335 -->
| 10000 | candidate | Stored-function prefix SUM | 2488.2369 | 1761.0281 | 3950.5975 |
<!-- samples-ms: 2488.2369,3099.6496,3633.6062,1773.3012,3025.7618,1761.0281,2334.6006,3950.5975,2168.4921 -->
| 10000 | mysql | Stored-function prefix SUM | 42.4174 | 29.3020 | 67.0768 |
<!-- samples-ms: 46.7079,50.9999,54.1777,29.7523,29.3020,36.8470,38.2745,42.4174,67.0768 -->
| 10000 | baseline | Point lookup after | 0.2864 | 0.2776 | 0.3805 |
<!-- samples-ms: 0.3805,0.2868,0.2780,0.2801,0.2776,0.2893,0.2864 -->
| 10000 | candidate | Point lookup after | 0.2862 | 0.2740 | 0.4436 |
<!-- samples-ms: 0.4436,0.2862,0.2849,0.2905,0.2815,0.2740,0.2912 -->
| 10000 | mysql | Point lookup after | 0.0862 | 0.0816 | 0.1063 |
<!-- samples-ms: 0.1063,0.0853,0.0894,0.0855,0.0816,0.0866,0.0862 -->
