# Grouped-query checkpoint during encoded-text work

Baseline: committed `0382c3e8`, built from `git archive HEAD` in a temporary directory.
Candidate: the same revision with uncommitted encoded-string, derived-row conversion,
and literal-warning changes. Source patch SHA-256: `202e7fc7f717b84056fb9d5a782fd247d1a471981cdac8555fa60f0fdab3b606`.

The maintained `benchmarks/scripts/grouped-inputs.fsx` verifies every result over
10,000 rows and 100 interleaved groups. Both runs use embedded, in-memory Debug
builds, .NET SDK 10.0.401, Darwin arm64, eight logical processors and a 4 GiB GC
heap cap. Five warmups; nine alternating batches of three queries. Candidate ran
first, then the isolated baseline was built and run. No concurrent fsdb build,
test, native oracle, or profile ran during either measurement. Background
applications remained active.

| Query | Baseline ms | Candidate ms | Baseline bytes | Candidate bytes | Added bytes |
|---|---:|---:|---:|---:|---:|
| scalar-sum | 6.084 | 5.878 | 7716920 | 7717840 | 920 |
| grouped-sum | 12.597 | 12.371 | 15188352 | 15189592 | 1240 |
| grouped-rollup | 20.449 | 20.547 | 28274568 | 28275696 | 1128 |
| grouped-distinct | 18.592 | 18.102 | 24433000 | 24434448 | 1448 |
| grouped-count | 7.318 | 7.251 | 9625144 | 9626384 | 1240 |
| grouped-multiple | 30.887 | 30.602 | 37376880 | 37378720 | 1840 |

Candidate allocations increase by 920–1,840 bytes/query (less than 0.02%).
Timing medians range from approximately 3.4% lower to 0.5% higher; the samples
do not establish a speed improvement or a material timing regression.

Both revisions allocate substantially more than the earlier
[`ceed2fc9` snapshot](ceed2fc9-grouped-inputs.md), by roughly 11–15% depending
on the query. That increase predates the uncommitted patch. This comparison
does not isolate which intervening committed change caused it. The source
collation and metadata changes are candidates for profiling, not established causes.

These are numeric grouping controls, not measurements of malformed-text conversion
or warning-heavy queries. No MySQL speed comparison or durable-write claim is made.

## Committed baseline

```text
scalar-sum input=10000 median-ms=6.084 median-bytes=7716920 samples-ms=[7.635833333; 6.4886; 6.084266667; 5.961533333; 6.0641; 6.047233333; 6.048666667;
 6.1755; 6.194566667]
grouped-sum input=10000 median-ms=12.597 median-bytes=15188352 samples-ms=[16.7063; 12.81826667; 12.37606667; 12.66093333; 12.4137; 12.5967; 19.0597;
 12.59036667; 12.46206667]
grouped-rollup input=10000 median-ms=20.449 median-bytes=28274568 samples-ms=[39.63736667; 20.62883333; 20.40893333; 20.2488; 20.33786667; 20.16916667;
 47.0863; 20.44883333; 23.33366667]
grouped-distinct input=10000 median-ms=18.592 median-bytes=24433000 samples-ms=[20.0845; 18.6021; 18.05253333; 18.0072; 17.7882; 17.43416667; 19.65916667;
 18.70616667; 18.59243333]
grouped-count input=10000 median-ms=7.318 median-bytes=9625144 samples-ms=[7.839366667; 7.4746; 7.3554; 7.343866667; 7.318366667; 7.259533333; 7.294966667;
 7.266266667; 7.2675]
grouped-multiple input=10000 median-ms=30.887 median-bytes=37376880 samples-ms=[31.53206667; 30.95006667; 30.8873; 31.6995; 29.33153333; 36.94523333;
 30.27086667; 29.6795; 29.96263333]
```

## Candidate

```text
scalar-sum input=10000 median-ms=5.878 median-bytes=7717840 samples-ms=[7.483; 5.920733333; 5.8898; 5.787833333; 5.706366667; 5.680433333; 5.684066667;
 5.891466667; 5.877766667]
grouped-sum input=10000 median-ms=12.371 median-bytes=15189592 samples-ms=[15.50163333; 12.37133333; 12.09696667; 12.8286; 12.33863333; 12.28146667;
 12.2685; 12.82903333; 12.64126667]
grouped-rollup input=10000 median-ms=20.547 median-bytes=28275696 samples-ms=[45.2638; 20.4222; 29.31306667; 20.54666667; 19.87073333; 20.33926667; 19.914;
 21.4429; 20.69116667]
grouped-distinct input=10000 median-ms=18.102 median-bytes=24434448 samples-ms=[19.25006667; 17.45923333; 18.062; 19.6805; 17.8454; 17.83423333; 18.10166667;
 18.4234; 19.7062]
grouped-count input=10000 median-ms=7.251 median-bytes=9626384 samples-ms=[7.1522; 7.136166667; 7.393233333; 16.37323333; 7.1982; 7.3095; 8.3316;
 7.229333333; 7.2514]
grouped-multiple input=10000 median-ms=30.602 median-bytes=37378720 samples-ms=[50.76886667; 36.8429; 30.60223333; 50.30026667; 29.66433333; 38.21223333;
 29.9027; 29.87016667; 30.59256667]
```
