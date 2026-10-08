# Ordering alias scope setup

The implementation based on `bbb713cc` prepares correlated ordering alias
identities once per statement and supplies projected values per row. Scalar and
grouped ordering share the setup helper. Subqueries still execute through the
normal evaluator; this does not remove their per-row cost.

The [benchmark](../scripts/order-aliases.fsx) uses 10,000 input rows and 100
output rows, five warmups per query, and nine alternating batches of five verified
executions. Embedded in-memory Debug engine, .NET SDK 10.0.401, Darwin 24.6.0
arm64, eight logical processors, 4 GiB GC heap cap. Background applications
remain active. No builds, tests, or other fsdb profiles run concurrently.

| Run | Source ms/query | Alias ms/query | Correlated alias ms/query |
|---|---:|---:|---:|
| Committed baseline | 52.279 | 52.355 | 182.359 |
| Prepared binding, first run | 63.234 | 60.527 | 211.893 |
| Prepared binding, repeat | 62.702 | 63.096 | 212.260 |
| Rebuilt committed baseline | 54.956 | 56.361 | 185.016 |
| Prepared binding, following control | 51.353 | 54.706 | 167.268 |

Correlated-query allocation falls from 251,523,040 to 240,883,344 bytes/query:
10,639,696 bytes, or 4.2%. Every run reproduces that allocation difference.
Source and ordinary alias allocation remain about 60.23 MB/query, with 104
additional bytes of statement setup in the new implementation.

Timing varies substantially, including the controls. The slower initial runs do
not reproduce consistently; the final comparison favors prepared binding. These
samples support the allocation reduction, but not a precise timing improvement.
The rebuilt baseline was extracted from `bbb713cc` into a temporary directory,
compiled independently, and used the same installed SDK and benchmark script.
The temporary checkout was removed after measurement.

```sh
DOTNET_PROCESSOR_COUNT=8 DOTNET_GCHeapHardLimit=0x100000000 \
  dotnet fsi --nologo benchmarks/scripts/order-aliases.fsx
```

The full gate passes 3,039 tests with the same heap cap. Native MySQL 8.4.11
wire contracts pass 52 cases / 5,313 steps with zero differences at
`20261008T005647259-83422/contracts`; the disposable server uses 64 MiB buffer
and redo limits. These measurements are not MySQL latency or durable-write
comparisons.

## Recorded samples

### Committed baseline

```text
source input=10000 output=100 median-ms=52.279 median-bytes=60234614 samples-ms=[56.3984; 55.7368; 54.1102; 51.3941; 52.27916; 52.05698; 51.87018; 52.92968;
 51.13588]
alias input=10000 output=100 median-ms=52.355 median-bytes=60234750 samples-ms=[54.24814; 54.10422; 54.38066; 52.35532; 51.89326; 50.65904; 51.91956; 52.40254;
 51.04264]
correlated-alias input=10000 output=100 median-ms=182.359 median-bytes=251523040 samples-ms=[203.31918; 191.18834; 191.67636; 181.6447; 181.8529; 182.97098; 182.35944;
 178.74286; 180.0999]
```

### Prepared binding, first run

```text
source input=10000 output=100 median-ms=63.234 median-bytes=60234718 samples-ms=[71.90978; 63.26176; 64.66414; 57.12798; 74.1262; 63.17966; 60.17918; 63.23434;
 59.86994]
alias input=10000 output=100 median-ms=60.527 median-bytes=60234854 samples-ms=[52.58282; 67.5918; 60.17394; 60.52724; 59.4079; 61.17688; 63.65466; 60.50002;
 61.0616]
correlated-alias input=10000 output=100 median-ms=211.893 median-bytes=240883344 samples-ms=[211.89322; 234.23362; 224.92728; 243.1473; 201.71882; 205.77556; 208.67984;
 202.13338; 214.63166]
```

### Prepared binding, repeat

```text
source input=10000 output=100 median-ms=62.702 median-bytes=60234718 samples-ms=[61.4976; 65.1725; 64.33984; 62.7023; 69.46758; 63.25832; 58.89384; 57.58166;
 62.69734]
alias input=10000 output=100 median-ms=63.096 median-bytes=60234854 samples-ms=[61.74044; 63.09566; 64.71172; 67.94918; 64.82504; 72.58182; 60.01198; 60.6264;
 57.96956]
correlated-alias input=10000 output=100 median-ms=212.260 median-bytes=240883344 samples-ms=[213.61782; 208.50784; 212.2597; 212.314; 220.34282; 217.4869; 199.4587;
 194.36804; 196.2293]
```

### Rebuilt committed baseline

```text
source input=10000 output=100 median-ms=54.956 median-bytes=60234614 samples-ms=[51.18834; 52.42554; 52.63078; 54.06848; 54.95604; 56.29776; 60.9565; 56.5656;
 56.97874]
alias input=10000 output=100 median-ms=56.361 median-bytes=60234750 samples-ms=[51.51542; 52.35014; 51.76108; 52.4334; 59.6048; 56.36052; 67.3327; 57.3822;
 62.03734]
correlated-alias input=10000 output=100 median-ms=185.016 median-bytes=251523040 samples-ms=[174.84498; 171.25064; 174.96096; 179.6248; 190.1742; 188.16802; 185.01622;
 200.3516; 205.46832]
```

### Prepared binding, following control

```text
source input=10000 output=100 median-ms=51.353 median-bytes=60234718 samples-ms=[48.19422; 48.58676; 48.04816; 51.16372; 51.69084; 55.14936; 51.3534; 54.62182;
 57.13596]
alias input=10000 output=100 median-ms=54.706 median-bytes=60234854 samples-ms=[47.9817; 48.53368; 48.72372; 64.90728; 51.26362; 54.70648; 54.7817; 61.42248;
 57.61412]
correlated-alias input=10000 output=100 median-ms=167.268 median-bytes=240883344 samples-ms=[161.52514; 159.56604; 160.55192; 161.49266; 167.26846; 224.0639; 192.29952;
 191.00256; 185.8888]
```
