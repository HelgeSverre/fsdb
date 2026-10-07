# Stored-function definition cache measurements

Status: measured improvement on a shared host; exact ratios are not stable
performance guarantees.

The workload evaluates a stored function once per input row inside a growing
ROWS SUM window. The function increments a user variable using SET, then
returns its value. Repeated parsing of its parameters, return type, and body
accounts for 4.80% of all sampled thread time in the initial profile.

Successful parsed definitions now use a bounded cache keyed by parser options,
parameter text, return-type text, and body text. The cache holds at most 256
entries and admits definitions with at most 16,384 combined characters.
Execution still resolves live accounts, privileges, catalog objects, and
session state for every invocation. Definition replacement selects a new key.

| Rows | Baseline ms | Cached definition ms | MySQL ms | Observed median ratio |
|---:|---:|---:|---:|---:|
| 1000 | 202.07 | 105.66 | 3.89 | 1.91x |
| 5000 | 1737.28 | 902.47 | 26.34 | 1.93x |
| 10000 | 3093.60 | 1558.94 | 47.85 | 1.98x |

The candidate is faster in all nine corresponding sample rounds at every size.
At 1,000 and 10,000 rows every candidate sample is faster than every baseline
sample. The 5,000-row ranges overlap slightly, with substantial variation in
both targets. Point controls also drift, so the exact ratios should not be
extrapolated to other workloads or hosts. MySQL remains substantially faster.

The unchanged [comparison harness](../scripts/window-comparison.fsx) uses at
least three warmups and two seconds per target/workload, nine measured samples
in rotating and reversing target order, and row-count/checksum assertions on
every query. The function counter is checked after warmup. Timings include
SET @n=0 before each function query. Point controls use seven batches of 100
queries after 200 warmups. All queries and cleanup completed successfully.

Measured 2026-10-07 UTC on Apple M2 Max, macOS 15.6, .NET SDK 10.0.401 /
runtime 10.0.12. The baseline contains the source at `079f18f7` (its binary was
built before that commit, with the RANGE patch on `319dad19`). The candidate
contains `079f18f7` plus the QueryHandler definition cache. The source diff
SHA-256 for QueryHandler is
`cda3d8cb295eab65df4f524053480c58d41ce3b31f162957a3151d049abddb64`.

Servers run natively on localhost, at normal priority, with no affinity or
processor-count override. fsdb uses in-memory storage; MySQL 8.4.11 uses normal
durability and a 64 MiB buffer pool. Setup and writes are outside query timings;
these results do not establish durable write performance. No build, test suite,
or profiler runs during the latency comparison. All owned servers stop and
the disposable MySQL data is removed afterward.

The shared host has about 30,412 MiB of swap in use at both snapshots. Unity
CPU use changes from 0.6% to 296.4%; unrelated applications remain running.
These conditions and control drift limit precision. Before/after comparisons
within this run provide stronger evidence than comparisons to earlier reports.

## Profile evidence

The [profile workload](../scripts/stored-function-profile.fsx) creates a unique
database, checks 5,000 output rows and their checksum, warms three queries,
and repeats for 40 seconds before cleanup. Run it against a dedicated Release
server using `FSDB_PERF_CONNECTION` with `Allow User Variables=true`.

`dotnet-trace` 10.0.731102 captures 30 seconds with the macOS-compatible
`dotnet-sampled-thread-time` profile and a 32 MiB buffer. These percentages
include waiting threads and are not CPU utilization percentages.

| Function | Baseline inclusive sampled time | Candidate inclusive sampled time |
|---|---:|---:|
| `parseFunctionDefinition` | 4.80% | 0.01% |
| Cached `functionDefinition` lookup | absent | 0.03% |
| `withStoredFunctions` | 0.25% | 0.65% |

The baseline completes 25 checked queries in 40.018 seconds; the candidate
completes 49 in 40.616 seconds. Profiling overhead and different host conditions
prevent using those counts as latency estimates. The untraced comparison above
provides the timing evidence. Remaining registry construction is visible, but
its increased share does not establish a regression: the removed parsing work
changes the denominator and throughput.

Local traces are `/tmp/fsdb-function-before.nettrace` and
`/tmp/fsdb-function-after.nettrace`; their inclusive reports use the same
prefixes followed by `-inclusive.txt`.

## Correctness evidence

`just check` passes 2,850 tests with no build warnings or errors. The new
regression covers function replacement, parameter names, return types, and
creation SQL mode. Native MySQL oracle scripts for stored-function SET,
volatile windows, and RANGE warnings pass. Differential contracts pass 4,551
steps across 43 cases with zero differences, recorded under
`torture/artifacts/runs/20261007T002319109-30132/contracts/manifest.json`.

## Assembly identity

```json
{
  "started_utc": "2026-10-07T00:24:51Z",
  "assemblies": {
    "baseline": {
      "path": "/var/folders/xr/z75qc4354_v3lm18m65xkz580000gn/T/fsdb-function-baseline-fnuz52yf/Fsdb.dll",
      "sha256": "04ad44b8dba81bd0541e93cf44961f6df4aa43cad3fda20da17c6bc714488815"
    },
    "candidate": {
      "path": "/Users/helge/code/fsdb/src/Fsdb/bin/Release/net10.0/Fsdb.dll",
      "sha256": "b5707c8098793343e4be217c7d01f4ebc5c8cdb90193e335cc60840fa860a9ad"
    }
  },
  "processor_count_override": null,
  "ports": {
    "baseline": 65131,
    "candidate": 65136,
    "mysql": 65139
  }
}
```

## Raw samples

<!-- baseline server: 8.4.0-fsdb -->
<!-- candidate server: 8.4.0-fsdb -->
<!-- mysql server: 8.4.11 -->
| Rows | Target | Workload | Median ms | Min ms | Max ms |
|---:|---|---|---:|---:|---:|
| 1000 | baseline | Point lookup before | 0.6810 | 0.5637 | 0.7448 |
<!-- samples-ms: 0.6866,0.6810,0.7448,0.6967,0.5979,0.5864,0.5637 -->
| 1000 | candidate | Point lookup before | 0.5601 | 0.4681 | 0.5764 |
<!-- samples-ms: 0.5601,0.5601,0.5764,0.5299,0.5220,0.5751,0.4681 -->
| 1000 | mysql | Point lookup before | 0.0818 | 0.0720 | 0.0855 |
<!-- samples-ms: 0.0720,0.0741,0.0844,0.0845,0.0818,0.0855,0.0813 -->
| 1000 | baseline | Grouped text SUM | 3.4433 | 2.6110 | 8.7104 |
<!-- samples-ms: 4.2304,8.7104,3.4433,2.7527,3.5442,2.6110,3.4956,2.7927,3.2981 -->
| 1000 | candidate | Grouped text SUM | 3.2205 | 2.2241 | 3.5196 |
<!-- samples-ms: 2.5824,3.4504,2.3817,3.3342,3.2205,2.2241,3.5196,2.5233,3.3355 -->
| 1000 | mysql | Grouped text SUM | 0.3421 | 0.2397 | 0.4071 |
<!-- samples-ms: 0.3105,0.2397,0.2422,0.4071,0.3739,0.3995,0.3468,0.3421,0.3326 -->
| 1000 | baseline | Prefix ROWS SUM | 2.3632 | 1.7127 | 12.5061 |
<!-- samples-ms: 3.6051,1.7127,12.5061,2.0155,3.3430,2.1085,2.2352,3.0353,2.3632 -->
| 1000 | candidate | Prefix ROWS SUM | 2.4304 | 1.9867 | 3.9464 |
<!-- samples-ms: 3.0619,2.2415,3.9464,1.9867,2.1974,3.2714,2.1314,2.4304,2.9913 -->
| 1000 | mysql | Prefix ROWS SUM | 0.8383 | 0.6992 | 0.9896 |
<!-- samples-ms: 0.6992,0.7344,0.7801,0.8970,0.8383,0.9896,0.9414,0.6995,0.8752 -->
| 1000 | baseline | Sliding ROWS SUM | 4.3664 | 3.7770 | 5.8112 |
<!-- samples-ms: 5.8112,3.7770,5.8080,4.1897,5.7322,4.3664,5.5250,4.3495,3.9259 -->
| 1000 | candidate | Sliding ROWS SUM | 4.9012 | 3.7646 | 5.8512 |
<!-- samples-ms: 3.8668,5.7752,3.7717,5.1691,3.7646,4.9012,5.7739,4.1385,5.8512 -->
| 1000 | mysql | Sliding ROWS SUM | 1.4135 | 1.1625 | 1.5182 |
<!-- samples-ms: 1.3939,1.4179,1.1625,1.4634,1.5182,1.4135,1.4791,1.3368,1.2943 -->
| 1000 | baseline | Offset RANGE SUM | 4.8902 | 4.1295 | 5.9785 |
<!-- samples-ms: 5.8258,4.4371,5.9416,4.5410,4.8902,5.8791,4.3900,4.1295,5.9785 -->
| 1000 | candidate | Offset RANGE SUM | 5.1945 | 4.1197 | 6.4750 |
<!-- samples-ms: 6.4750,4.4602,5.1945,5.8336,4.1217,5.0217,5.5948,4.1197,5.6070 -->
| 1000 | mysql | Offset RANGE SUM | 1.6839 | 1.4337 | 1.9004 |
<!-- samples-ms: 1.6666,1.8697,1.4337,1.7425,1.8352,1.9004,1.6839,1.6749,1.6294 -->
| 1000 | baseline | Stored-function prefix SUM | 202.0745 | 196.2478 | 210.7005 |
<!-- samples-ms: 202.2080,203.6425,201.7798,202.0745,204.7041,200.0141,196.2478,210.7005,200.0625 -->
| 1000 | candidate | Stored-function prefix SUM | 105.6560 | 100.8810 | 109.1369 |
<!-- samples-ms: 100.8810,105.5083,106.8970,102.7285,107.7857,104.5038,106.3637,109.1369,105.6560 -->
| 1000 | mysql | Stored-function prefix SUM | 3.8877 | 3.5792 | 4.0928 |
<!-- samples-ms: 4.0297,3.8133,3.5792,4.0928,3.8382,3.8594,3.8877,4.0483,3.9071 -->
| 1000 | baseline | Point lookup after | 0.2947 | 0.2578 | 0.3567 |
<!-- samples-ms: 0.3567,0.3098,0.2975,0.2947,0.2825,0.2578,0.2648 -->
| 1000 | candidate | Point lookup after | 0.2884 | 0.2614 | 0.3475 |
<!-- samples-ms: 0.3475,0.3186,0.3169,0.2884,0.2650,0.2615,0.2614 -->
| 1000 | mysql | Point lookup after | 0.0787 | 0.0764 | 0.0985 |
<!-- samples-ms: 0.0985,0.0798,0.0768,0.0764,0.0774,0.0790,0.0787 -->
| 5000 | baseline | Point lookup before | 0.2762 | 0.2580 | 0.3591 |
<!-- samples-ms: 0.3290,0.3273,0.3591,0.2738,0.2580,0.2596,0.2762 -->
| 5000 | candidate | Point lookup before | 0.1383 | 0.1341 | 0.1412 |
<!-- samples-ms: 0.1412,0.1383,0.1389,0.1412,0.1372,0.1373,0.1341 -->
| 5000 | mysql | Point lookup before | 0.0827 | 0.0304 | 2.2308 |
<!-- samples-ms: 0.0304,2.2308,0.1142,0.0916,0.0795,0.0827,0.0773 -->
| 5000 | baseline | Grouped text SUM | 13.5087 | 12.5774 | 15.0194 |
<!-- samples-ms: 15.0194,12.7268,14.1580,12.7166,14.1146,12.5774,13.5087,13.1170,14.3474 -->
| 5000 | candidate | Grouped text SUM | 13.0408 | 12.5433 | 13.8012 |
<!-- samples-ms: 13.0408,13.8012,12.5927,13.0851,12.5433,13.4517,12.6333,13.8012,12.6039 -->
| 5000 | mysql | Grouped text SUM | 1.1897 | 0.9981 | 1.2879 |
<!-- samples-ms: 1.2718,1.1542,1.0421,1.2765,1.1897,1.2879,1.2447,0.9981,1.1076 -->
| 5000 | baseline | Prefix ROWS SUM | 23.0328 | 15.9074 | 27.5649 |
<!-- samples-ms: 18.2521,23.0328,23.9666,15.9074,19.5265,27.5649,25.2341,18.5200,24.3165 -->
| 5000 | candidate | Prefix ROWS SUM | 23.4814 | 15.8951 | 25.3020 |
<!-- samples-ms: 16.2101,25.1205,25.3020,23.4814,17.4285,23.7907,23.7518,15.8951,18.1316 -->
| 5000 | mysql | Prefix ROWS SUM | 3.9322 | 3.6257 | 4.2267 |
<!-- samples-ms: 4.1491,4.2267,4.0319,3.8368,3.6257,3.9322,3.6465,4.0997,3.7004 -->
| 5000 | baseline | Sliding ROWS SUM | 46.8920 | 35.5471 | 60.4582 |
<!-- samples-ms: 47.8397,46.8920,39.8080,59.4901,45.4247,35.5471,44.1599,50.5312,60.4582 -->
| 5000 | candidate | Sliding ROWS SUM | 47.6612 | 40.9527 | 50.9873 |
<!-- samples-ms: 47.6612,49.3189,40.9527,47.0451,48.9266,50.9873,46.3071,45.5869,50.5717 -->
| 5000 | mysql | Sliding ROWS SUM | 7.0848 | 6.5144 | 7.7201 |
<!-- samples-ms: 6.7521,7.5501,6.5144,7.4766,7.3730,7.7201,6.7898,6.6311,7.0848 -->
| 5000 | baseline | Offset RANGE SUM | 50.9730 | 45.6480 | 59.1220 |
<!-- samples-ms: 48.5321,50.9730,48.8088,50.8104,55.3152,59.1220,51.1596,51.0065,45.6480 -->
| 5000 | candidate | Offset RANGE SUM | 48.5860 | 45.6586 | 63.8292 |
<!-- samples-ms: 51.8021,61.4666,47.2653,45.6586,46.7441,63.8292,57.8358,47.3845,48.5860 -->
| 5000 | mysql | Offset RANGE SUM | 10.9483 | 9.8929 | 11.3996 |
<!-- samples-ms: 10.4893,11.3996,10.9483,10.8043,10.5684,11.2570,11.1498,11.3840,9.8929 -->
| 5000 | baseline | Stored-function prefix SUM | 1737.2842 | 1063.4707 | 2036.0347 |
<!-- samples-ms: 1737.2842,1764.8374,1837.1686,1824.0757,1631.1373,1309.0590,1063.4707,1369.3003,2036.0347 -->
| 5000 | candidate | Stored-function prefix SUM | 902.4736 | 558.5784 | 1090.8710 |
<!-- samples-ms: 921.3290,902.4736,943.1807,875.9295,858.9540,911.6865,575.9867,1090.8710,558.5784 -->
| 5000 | mysql | Stored-function prefix SUM | 26.3445 | 16.2966 | 34.2824 |
<!-- samples-ms: 26.0831,26.3445,27.1565,26.7566,34.2824,16.2966,20.4593,22.8510,31.4510 -->
| 5000 | baseline | Point lookup after | 0.3504 | 0.3382 | 0.4068 |
<!-- samples-ms: 0.4068,0.3439,0.3382,0.3526,0.3574,0.3504,0.3439 -->
| 5000 | candidate | Point lookup after | 0.3416 | 0.3167 | 0.4116 |
<!-- samples-ms: 0.4116,0.3456,0.3167,0.3449,0.3361,0.3267,0.3416 -->
| 5000 | mysql | Point lookup after | 0.0903 | 0.0827 | 0.1045 |
<!-- samples-ms: 0.1045,0.0971,0.0878,0.0934,0.0886,0.0903,0.0827 -->
| 10000 | baseline | Point lookup before | 0.3777 | 0.3557 | 0.5457 |
<!-- samples-ms: 0.5457,0.5239,0.3665,0.3844,0.3557,0.3666,0.3777 -->
| 10000 | candidate | Point lookup before | 0.4574 | 0.4244 | 0.5289 |
<!-- samples-ms: 0.5289,0.4993,0.4574,0.4387,0.4373,0.4666,0.4244 -->
| 10000 | mysql | Point lookup before | 0.1115 | 0.0973 | 0.1185 |
<!-- samples-ms: 0.1057,0.0973,0.1185,0.1085,0.1149,0.1144,0.1115 -->
| 10000 | baseline | Grouped text SUM | 46.3270 | 43.5006 | 51.8801 |
<!-- samples-ms: 44.2589,51.8801,48.8365,45.9853,46.7908,46.3270,45.2805,43.5006,46.4544 -->
| 10000 | candidate | Grouped text SUM | 44.6254 | 39.8033 | 46.4447 |
<!-- samples-ms: 43.7558,44.9375,44.6254,46.4447,43.9987,44.0893,39.8033,44.7119,44.6720 -->
| 10000 | mysql | Grouped text SUM | 3.5919 | 2.4747 | 4.1111 |
<!-- samples-ms: 3.4954,3.7534,4.1111,3.5919,3.3565,3.6797,2.4747,3.6448,3.3095 -->
| 10000 | baseline | Prefix ROWS SUM | 62.8797 | 51.2702 | 75.7243 |
<!-- samples-ms: 62.8797,54.1240,56.6525,71.4611,51.2702,75.7243,55.3472,71.6467,63.1936 -->
| 10000 | candidate | Prefix ROWS SUM | 63.2543 | 53.9062 | 78.9732 |
<!-- samples-ms: 65.8125,53.9062,54.2724,78.4125,54.8189,78.9732,54.4435,67.7085,63.2543 -->
| 10000 | mysql | Prefix ROWS SUM | 8.1734 | 7.7270 | 8.8153 |
<!-- samples-ms: 7.9772,8.2684,8.8153,8.0467,8.1734,8.5778,7.8335,7.7270,8.5605 -->
| 10000 | baseline | Sliding ROWS SUM | 98.4947 | 84.1744 | 108.1066 |
<!-- samples-ms: 99.1711,108.1066,84.1744,98.4947,103.2257,87.2399,97.8037,104.3760,87.2132 -->
| 10000 | candidate | Sliding ROWS SUM | 96.3022 | 77.5601 | 104.1466 |
<!-- samples-ms: 102.6342,85.3590,96.3123,104.1466,77.5601,96.3022,103.7520,83.5746,94.3185 -->
| 10000 | mysql | Sliding ROWS SUM | 12.3595 | 11.8093 | 13.3600 |
<!-- samples-ms: 12.3595,13.3600,12.3998,12.3132,12.2503,11.8093,12.2622,12.9414,12.7624 -->
| 10000 | baseline | Offset RANGE SUM | 119.1505 | 98.4512 | 130.5817 |
<!-- samples-ms: 119.1505,119.4412,130.5817,117.3655,106.5950,107.6200,119.6649,98.4512,123.2027 -->
| 10000 | candidate | Offset RANGE SUM | 113.0319 | 100.6835 | 146.6212 |
<!-- samples-ms: 111.2350,113.2120,110.3441,109.2309,100.6835,146.6212,118.7770,113.0319,116.0376 -->
| 10000 | mysql | Offset RANGE SUM | 20.4972 | 19.3128 | 22.0504 |
<!-- samples-ms: 20.4972,19.7276,19.8703,19.7019,20.6508,22.0504,20.7441,19.3128,20.9397 -->
| 10000 | baseline | Stored-function prefix SUM | 3093.6003 | 2575.2076 | 3277.2389 |
<!-- samples-ms: 2787.7662,3093.6003,2960.0391,3096.0455,3082.8970,3107.1812,3277.2389,3181.9259,2575.2076 -->
| 10000 | candidate | Stored-function prefix SUM | 1558.9394 | 1095.2946 | 1633.9738 |
<!-- samples-ms: 1633.9738,1498.4711,1558.9394,1537.7586,1599.8520,1586.3187,1617.6376,1489.7896,1095.2946 -->
| 10000 | mysql | Stored-function prefix SUM | 47.8504 | 43.5367 | 49.1022 |
<!-- samples-ms: 49.1022,46.5758,46.5427,46.3621,48.1569,48.6325,47.8504,43.5367,48.3847 -->
| 10000 | baseline | Point lookup after | 0.4050 | 0.3919 | 0.4903 |
<!-- samples-ms: 0.4903,0.4040,0.3919,0.3969,0.4090,0.4050,0.4163 -->
| 10000 | candidate | Point lookup after | 0.4182 | 0.3863 | 0.5406 |
<!-- samples-ms: 0.5406,0.4017,0.4234,0.4182,0.3889,0.3863,0.4201 -->
| 10000 | mysql | Point lookup after | 0.1081 | 0.1002 | 0.1260 |
<!-- samples-ms: 0.1260,0.1124,0.1027,0.1074,0.1002,0.1100,0.1081 -->
