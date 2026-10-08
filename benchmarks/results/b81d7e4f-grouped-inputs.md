# Grouped aggregate input materialization

Implementation based on `b81d7e4f`. Aggregate arguments that can depend on
execution state are retained in source-row order. Source columns and literals
reuse their immutable values; this avoids storing a redundant value per argument.

The [probe](../scripts/grouped-inputs.fsx) inserts 10,000 rows across 100
interleaved groups. Every timed result is verified. Each query has five warmups
and nine alternating batches of three executions. Embedded in-memory Debug
engine, .NET SDK 10.0.401, Darwin 24.6 arm64, eight logical processors, and a
4 GiB GC heap cap. No concurrent fsdb build, test, or profile runs. Background
applications remain active.

The committed baseline was extracted with git archive and compiled independently
using the same SDK and heap cap. The same probe script targets its assembly.
Its temporary checkout was removed after measurement.

| Query | Baseline bytes/query | Initial bytes/query | Final bytes/query | Final change |
|---|---:|---:|---:|---:|
| scalar-sum | 6,274,016 | 12,034,952 | 6,675,008 | 6.4% |
| grouped-sum | 12,098,232 | 18,437,144 | 13,077,200 | 8.1% |
| grouped-count | 7,495,216 | 9,353,960 | 8,473,992 | 13.1% |
| grouped-multiple | 31,332,872 | 52,428,448 | 32,327,752 | 3.2% |

Initial materialization retained every argument and repeatedly constructed row
contexts. The cleanup removes most of that extra allocation. The remaining
row records and setup add 3.2–13.1% for these ordinary workloads. Both baseline
and cleaned implementation reproduce their allocation counts in a second run.
State-dependent expressions still require retained input values; these ordinary
queries measure the cost imposed on unaffected query shapes.

Timing does not support a precise speed claim. Baseline scalar SUM moves from
5.680 to 22.691 ms/query, and baseline multiple aggregates from 29.448 to
71.872 ms/query. The cleanup runs also vary. The later controls demonstrate
substantial host/run variation; they do not prove that execution time is flat.
This is an fsdb allocation probe, not a MySQL latency or durable-write comparison.

```sh
DOTNET_PROCESSOR_COUNT=8 DOTNET_GCHeapHardLimit=0x100000000 \
  dotnet fsi --nologo benchmarks/scripts/grouped-inputs.fsx
```

## Recorded samples

### Committed baseline

```text
scalar-sum input=10000 median-ms=5.680 median-bytes=6274016 samples-ms=[6.838233333; 7.442566667; 23.7297; 5.633633333; 5.600166667; 5.3475; 5.1905;
 5.691; 5.679866667]
grouped-sum input=10000 median-ms=11.906 median-bytes=12098232 samples-ms=[14.31003333; 13.39416667; 15.0385; 11.55913333; 11.28223333; 11.3974;
 11.16023333; 11.9065; 12.6112]
grouped-count input=10000 median-ms=6.836 median-bytes=7495216 samples-ms=[8.323666667; 7.655666667; 7.435133333; 6.6136; 6.806066667; 6.835533333;
 6.745766667; 6.742366667; 7.375966667]
grouped-multiple input=10000 median-ms=29.448 median-bytes=31332872 samples-ms=[32.9997; 33.13806667; 41.57176667; 28.80143333; 29.58986667; 29.44806667;
 28.45756667; 27.87863333; 29.39186667]
```

### Initial materialization

```text
scalar-sum input=10000 median-ms=8.827 median-bytes=12034952 samples-ms=[10.4973; 8.240733333; 8.565933333; 9.181866667; 8.8271; 8.326533333;
 8.666833333; 17.72736667; 9.132133333]
grouped-sum input=10000 median-ms=15.040 median-bytes=18437144 samples-ms=[18.9145; 14.8357; 15.11633333; 27.52346667; 15.921; 14.95366667; 14.80986667;
 15.0397; 14.85663333]
grouped-count input=10000 median-ms=7.652 median-bytes=9353960 samples-ms=[10.88056667; 7.794366667; 7.5398; 7.652433333; 8.220566667; 7.7062; 7.5613;
 7.613733333; 7.575866667]
grouped-multiple input=10000 median-ms=38.305 median-bytes=52428448 samples-ms=[73.53686667; 41.13056667; 37.3372; 38.83486667; 45.2128; 38.305; 38.1669;
 37.7478; 37.78153333]
```

### Immutable-input cleanup

```text
scalar-sum input=10000 median-ms=18.338 median-bytes=6675008 samples-ms=[26.46383333; 18.33783333; 61.5808; 48.36346667; 23.4931; 12.45853333;
 9.424966667; 13.11733333; 12.09343333]
grouped-sum input=10000 median-ms=32.731 median-bytes=13077200 samples-ms=[33.6414; 37.7219; 57.86696667; 25.55596667; 32.73133333; 32.06016667;
 28.09273333; 25.19616667; 37.4142]
grouped-count input=10000 median-ms=13.987 median-bytes=8473992 samples-ms=[18.72256667; 22.678; 11.58826667; 13.98696667; 13.33416667; 14.38663333;
 17.03373333; 10.37873333; 12.61133333]
grouped-multiple input=10000 median-ms=66.084 median-bytes=32327752 samples-ms=[68.42256667; 84.43486667; 72.12576667; 52.40946667; 66.08393333; 58.7374;
 57.86803333; 54.7652; 66.28933333]
```

### Repeated committed baseline

```text
scalar-sum input=10000 median-ms=22.691 median-bytes=6274016 samples-ms=[37.88146667; 107.2860667; 26.3066; 8.669333333; 8.297033333; 8.166733333;
 22.69123333; 18.3673; 26.15266667]
grouped-sum input=10000 median-ms=37.509 median-bytes=12098232 samples-ms=[60.79736667; 62.555; 58.23506667; 17.64276667; 18.32823333; 37.50876667;
 25.12253333; 32.77756667; 39.19713333]
grouped-count input=10000 median-ms=18.074 median-bytes=7495216 samples-ms=[43.6247; 18.0744; 47.15646667; 9.963; 11.62686667; 26.3633; 12.30973333;
 17.13956667; 24.24423333]
grouped-multiple input=10000 median-ms=71.872 median-bytes=31332872 samples-ms=[161.4038667; 88.31683333; 138.0711; 37.9638; 45.925; 52.90303333; 79.04393333;
 59.6615; 71.872]
```

### Repeated cleanup

```text
scalar-sum input=10000 median-ms=11.312 median-bytes=6675008 samples-ms=[13.24343333; 17.7436; 79.66993333; 9.613966667; 9.961566667; 9.5331; 8.782;
 11.3119; 16.19476667]
grouped-sum input=10000 median-ms=22.107 median-bytes=13077200 samples-ms=[21.97956667; 34.0579; 57.0395; 20.7459; 26.317; 20.09436667; 22.107;
 21.55776667; 31.61806667]
grouped-count input=10000 median-ms=12.844 median-bytes=8473992 samples-ms=[14.63896667; 16.0089; 12.84443333; 11.2474; 14.34663333; 11.24813333;
 13.18843333; 12.3496; 10.86596667]
grouped-multiple input=10000 median-ms=51.210 median-bytes=32327752 samples-ms=[129.644; 61.69796667; 52.84806667; 50.69716667; 52.7865; 51.20976667;
 51.17263333; 47.4675; 48.15116667]
```
