# Full-text performance snapshot

Measured 2026-10-07 at `884fece3`, after configurable word-length bounds and
expansion-limit reporting. Default-setting reads remain in the same broad range
as the [previous snapshot](e428a5e8-fulltext-snapshot.md). Separate runs with
background activity do not isolate a code speedup or regression.

## Conditions

Both servers run natively over loopback on the same Apple M2 Max host, macOS
Darwin 24.6.0 arm64. Fsdb uses Release, .NET SDK 10.0.401 / runtime 10.0.12,
`DOTNET_PROCESSOR_COUNT=4`, and a private WAL directory. MySQL 8.4.11 uses normal
durable commits, a private directory, 64 MiB buffer pool and 64 MiB redo capacity.
The disposable servers and data were cleaned up after measurement.

The unchanged [benchmark](../scripts/fulltext-snapshot.fsx) measures default word
and size-2 ngram indexes at 1,000 and 10,000 rows. A quarter of the corpus matches;
reads transfer all matching IDs. Each workload warms for at least one second and
three executions, then measures seven batches of ten operations. Times are median
batch averages, not latency percentiles. Update pairs change and restore one row
with separate commits. Fsdb runs before MySQL.

Background applications remained active (BoatX was observed around 47% CPU).
MySQL ngram Boolean phrase and update timings vary substantially. Small timing
differences and write ratios should not be treated as stable advantages.
This corpus does not cover nondefault word bounds, historical indexing rules,
startup, checkpoints, or diverse-vocabulary expansion.

## Results at 10,000 rows

| Workload | fsdb WAL ms | MySQL ms | fsdb / MySQL |
|---|---:|---:|---:|
| Word natural | 4.225 | 3.330 | 1.27× |
| Word natural phrase | 13.727 | 9.798 | 1.40× |
| Word phrase expansion | 24.887 | 23.227 | 1.07× |
| Word boolean | 4.027 | 5.173 | 0.78× |
| Ngram natural | 4.329 | 3.386 | 1.28× |
| Ngram natural phrase | 7.130 | 5.717 | 1.25× |
| Ngram boolean phrase | 15.401 | 12.456 | 1.24× |
| Ngram prefix | 3.273 | 3.407 | 0.96× |
| words durable update pair | 0.781 | 0.676 | 1.16× |
| grams durable update pair | 0.642 | 0.473 | 1.36× |

Release build passed without warnings or errors. All benchmark result-count
checks passed. No engine changes were made for this measurement.

## Equality-join diagnostic

A separate embedded Debug, in-memory microprofile compares the following query
against one with the redundant `AND d.owner_id=42` predicate. The corpus contains
10,000 documents, `owner_id=id%100`, an ordinary index on `owner_id`, and a full-text
index on `body`; 80% contain `needle`, and the owners table contains ID 42.

```sql
SELECT d.id FROM docs d JOIN owners o ON o.id=d.owner_id
WHERE o.id=42 AND MATCH(d.body) AGAINST('needle') ORDER BY d.id;
```

Five warmups precede seven batches of ten queries, with `DOTNET_PROCESSOR_COUNT=4`.
Both forms return 100 rows. This diagnostic measures candidate-selection overhead;
it is not a durable deployment benchmark or a comparison with MySQL.

```text
cross-source rows=100 median-ms=6.917 samples=[6.66193; 7.13226; 6.91668; 7.58428; 5.91798; 7.49955; 4.24673]
explicit-bound rows=100 median-ms=0.811 samples=[0.90222; 0.82837; 0.83747; 0.81111; 0.79973; 0.80494; 0.78993]
```

The explicit bound exposes a useful remaining planner optimization: propagate
compatible equality bounds across joins before full-text scoring, while retaining
the complete relevance corpus and original join semantics.

## Reproduction

Build `src/Fsdb` in Release and `tests/Fsdb.Tests` in Debug. Start private native
servers with the settings above and set `FSDB_PERF_CONNECTION` and
`FSDB_ORACLE_CONNECTION`, then run:

```sh
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --nologo benchmarks/scripts/fulltext-snapshot.fsx
```

## Raw samples

| Target | Rows | Workload | Median ms | Min ms | Max ms |
|---|---:|---|---:|---:|---:|
| fsdb-wal | 1000 | Word natural | 1.2081 | 1.1820 | 1.3003 |
<!-- samples-ms: 1.3003,1.2281,1.1902,1.2081,1.1820,1.1852,1.2156 -->
| fsdb-wal | 1000 | Word natural phrase | 1.7059 | 1.6935 | 1.7781 |
<!-- samples-ms: 1.7012,1.6987,1.7148,1.7781,1.7059,1.7357,1.6935 -->
| fsdb-wal | 1000 | Word phrase expansion | 2.0259 | 1.8036 | 3.5577 |
<!-- samples-ms: 1.9557,1.8036,1.9318,2.0473,2.0259,2.9945,3.5577 -->
| fsdb-wal | 1000 | Word boolean | 0.4682 | 0.4485 | 0.5453 |
<!-- samples-ms: 0.4562,0.5016,0.4527,0.5453,0.4838,0.4485,0.4682 -->
| fsdb-wal | 1000 | Ngram natural | 0.4823 | 0.4336 | 0.5237 |
<!-- samples-ms: 0.4823,0.5237,0.4544,0.4831,0.4925,0.4485,0.4336 -->
| fsdb-wal | 1000 | Ngram natural phrase | 0.6363 | 0.5961 | 0.6969 |
<!-- samples-ms: 0.6363,0.5961,0.6969,0.6110,0.6383,0.6597,0.6142 -->
| fsdb-wal | 1000 | Ngram boolean phrase | 1.2117 | 1.1906 | 1.2560 |
<!-- samples-ms: 1.1906,1.2348,1.2484,1.1937,1.2560,1.2117,1.1923 -->
| fsdb-wal | 1000 | Ngram prefix | 0.4160 | 0.4084 | 0.4492 |
<!-- samples-ms: 0.4160,0.4492,0.4109,0.4146,0.4326,0.4394,0.4084 -->
| fsdb-wal | 1000 | words durable update pair | 0.6908 | 0.6403 | 0.7455 |
<!-- samples-ms: 0.6699,0.7185,0.6668,0.7220,0.7455,0.6908,0.6403 -->
| fsdb-wal | 1000 | grams durable update pair | 0.6165 | 0.6073 | 0.6562 |
<!-- samples-ms: 0.6165,0.6468,0.6073,0.6165,0.6234,0.6091,0.6562 -->
| fsdb-wal | 10000 | Word natural | 4.2253 | 4.2027 | 4.4464 |
<!-- samples-ms: 4.2737,4.2027,4.2253,4.2187,4.2944,4.4464,4.2033 -->
| fsdb-wal | 10000 | Word natural phrase | 13.7266 | 13.2682 | 14.0538 |
<!-- samples-ms: 13.4464,13.7266,13.9005,13.6500,13.7838,14.0538,13.2682 -->
| fsdb-wal | 10000 | Word phrase expansion | 24.8873 | 24.6391 | 25.6824 |
<!-- samples-ms: 25.3484,25.6824,24.7840,24.9045,24.7804,24.6391,24.8873 -->
| fsdb-wal | 10000 | Word boolean | 4.0274 | 3.4524 | 5.3850 |
<!-- samples-ms: 5.3850,5.3025,4.4407,4.0274,3.8492,3.6139,3.4524 -->
| fsdb-wal | 10000 | Ngram natural | 4.3292 | 4.2484 | 4.4910 |
<!-- samples-ms: 4.2625,4.4465,4.4910,4.3087,4.3365,4.3292,4.2484 -->
| fsdb-wal | 10000 | Ngram natural phrase | 7.1296 | 6.8144 | 7.2399 |
<!-- samples-ms: 6.8237,7.2087,7.2170,6.8144,7.1296,7.2399,6.9444 -->
| fsdb-wal | 10000 | Ngram boolean phrase | 15.4014 | 15.1800 | 15.8367 |
<!-- samples-ms: 15.3841,15.6576,15.8367,15.7839,15.4014,15.1800,15.2938 -->
| fsdb-wal | 10000 | Ngram prefix | 3.2728 | 3.2010 | 3.5136 |
<!-- samples-ms: 3.2402,3.2395,3.2956,3.5136,3.4465,3.2010,3.2728 -->
| fsdb-wal | 10000 | words durable update pair | 0.7813 | 0.7003 | 0.8485 |
<!-- samples-ms: 0.8485,0.7003,0.7570,0.7838,0.7813,0.7200,0.7903 -->
| fsdb-wal | 10000 | grams durable update pair | 0.6420 | 0.6105 | 0.7018 |
<!-- samples-ms: 0.6461,0.6316,0.7018,0.6420,0.6669,0.6366,0.6105 -->
| mysql | 1000 | Word natural | 0.4133 | 0.3844 | 0.4397 |
<!-- samples-ms: 0.4133,0.3844,0.4231,0.4127,0.4397,0.4121,0.4157 -->
| mysql | 1000 | Word natural phrase | 1.0506 | 1.0340 | 1.0887 |
<!-- samples-ms: 1.0887,1.0340,1.0533,1.0343,1.0386,1.0516,1.0506 -->
| mysql | 1000 | Word phrase expansion | 2.3761 | 2.3520 | 2.3903 |
<!-- samples-ms: 2.3761,2.3778,2.3520,2.3885,2.3903,2.3718,2.3672 -->
| mysql | 1000 | Word boolean | 0.5928 | 0.5761 | 0.6250 |
<!-- samples-ms: 0.5964,0.5761,0.6011,0.5928,0.5793,0.6250,0.5785 -->
| mysql | 1000 | Ngram natural | 0.4181 | 0.4064 | 0.4310 |
<!-- samples-ms: 0.4152,0.4064,0.4181,0.4231,0.4234,0.4151,0.4310 -->
| mysql | 1000 | Ngram natural phrase | 0.6222 | 0.6053 | 0.6500 |
<!-- samples-ms: 0.6222,0.6382,0.6171,0.6069,0.6500,0.6053,0.6359 -->
| mysql | 1000 | Ngram boolean phrase | 1.0692 | 1.0598 | 1.0893 |
<!-- samples-ms: 1.0684,1.0893,1.0598,1.0692,1.0831,1.0720,1.0676 -->
| mysql | 1000 | Ngram prefix | 0.4240 | 0.4035 | 0.4282 |
<!-- samples-ms: 0.4035,0.4257,0.4260,0.4103,0.4057,0.4282,0.4240 -->
| mysql | 1000 | words durable update pair | 0.3326 | 0.3209 | 0.3568 |
<!-- samples-ms: 0.3568,0.3209,0.3283,0.3431,0.3519,0.3302,0.3326 -->
| mysql | 1000 | grams durable update pair | 0.3637 | 0.3418 | 0.4020 |
<!-- samples-ms: 0.4020,0.3637,0.3570,0.3418,0.3874,0.3648,0.3459 -->
| mysql | 10000 | Word natural | 3.3300 | 3.2948 | 3.3471 |
<!-- samples-ms: 3.3300,3.3376,3.3379,3.2948,3.3471,3.3268,3.2949 -->
| mysql | 10000 | Word natural phrase | 9.7979 | 9.7321 | 9.8769 |
<!-- samples-ms: 9.8769,9.8652,9.7608,9.7979,9.7321,9.7927,9.8757 -->
| mysql | 10000 | Word phrase expansion | 23.2268 | 22.9070 | 23.9012 |
<!-- samples-ms: 23.1820,22.9318,22.9070,23.2268,23.5932,23.9012,23.7659 -->
| mysql | 10000 | Word boolean | 5.1733 | 5.1269 | 5.2325 |
<!-- samples-ms: 5.2044,5.2243,5.1733,5.2325,5.1269,5.1670,5.1694 -->
| mysql | 10000 | Ngram natural | 3.3864 | 3.3569 | 3.6802 |
<!-- samples-ms: 3.4554,3.6802,3.4112,3.3569,3.3704,3.3758,3.3864 -->
| mysql | 10000 | Ngram natural phrase | 5.7166 | 5.6763 | 5.9949 |
<!-- samples-ms: 5.9949,5.7813,5.6992,5.7154,5.8683,5.6763,5.7166 -->
| mysql | 10000 | Ngram boolean phrase | 12.4556 | 10.3630 | 21.9787 |
<!-- samples-ms: 21.9787,10.3630,16.5393,12.4556,10.4790,18.9841,10.5265 -->
| mysql | 10000 | Ngram prefix | 3.4074 | 3.3454 | 3.7573 |
<!-- samples-ms: 3.4018,3.4074,3.3454,3.4182,3.7573,3.4127,3.3642 -->
| mysql | 10000 | words durable update pair | 0.6762 | 0.4500 | 0.8646 |
<!-- samples-ms: 0.8646,0.7190,0.6860,0.6762,0.5066,0.5019,0.4500 -->
| mysql | 10000 | grams durable update pair | 0.4729 | 0.4338 | 0.7402 |
<!-- samples-ms: 0.5690,0.4729,0.6610,0.4550,0.4597,0.7402,0.4338 -->
