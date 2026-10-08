# Aggregate-family grouping input order

Working tree based on `a975585d`, with aggregate-family input sorting and
compatible ORDER BY prefix directions. The [probe](../scripts/grouped-inputs.fsx)
uses 10,000 rows across 100 interleaved groups. The DISTINCT case combines
SUM(v) with COUNT(DISTINCT v); all input values are unique. Every result is
verified, including every timed iteration.

Embedded in-memory Debug engine, .NET SDK 10.0.401, Darwin 24.6 arm64, eight
logical processors, 4 GiB GC heap cap. Five warmups and nine alternating batches
of three executions per query. No concurrent fsdb builds, tests, profiles, or
native oracle processes. Background applications remain active.

| Query | Median ms/query | Median allocated bytes/query |
| --- | ---: | ---: |
| Scalar SUM | 7.755 | 6,755,264 |
| Grouped SUM | 15.133 | 13,238,448 |
| Grouped SUM and DISTINCT COUNT | 24.363 | 21,525,288 |
| Grouped COUNT | 9.531 | 8,635,240 |
| Grouped SUM/COUNT/MIN/MAX | 39.196 | 32,489,192 |

This is a current performance snapshot, not a before/after speed claim. Timing
varies substantially between samples and earlier controls. The DISTINCT case
includes distinct-set work as well as the input-order strategy; its difference
from grouped SUM does not isolate sorting cost.

```text
scalar-sum input=10000 median-ms=7.755 median-bytes=6755264 samples-ms=[10.8807; 8.357833333; 8.003533333; 7.3246; 6.922466667; 7.608433333;
 7.755166667; 7.4767; 13.4644]
grouped-sum input=10000 median-ms=15.133 median-bytes=13238448 samples-ms=[22.64123333; 14.36066667; 15.2033; 14.9058; 15.1326; 18.56836667; 18.6767;
 12.95906667; 12.91933333]
grouped-distinct input=10000 median-ms=24.363 median-bytes=21525288 samples-ms=[33.519; 21.43026667; 22.89886667; 24.3634; 21.2929; 35.98273333; 24.2869;
 24.8643; 25.7793]
grouped-count input=10000 median-ms=9.531 median-bytes=8635240 samples-ms=[11.83243333; 7.743966667; 8.0576; 8.843066667; 8.6076; 9.8714; 9.5313;
 15.53216667; 31.39416667]
grouped-multiple input=10000 median-ms=39.196 median-bytes=32489192 samples-ms=[45.11926667; 78.6752; 36.18686667; 35.50773333; 36.84713333; 42.74203333;
 40.4111; 39.19636667; 37.34866667]
```
