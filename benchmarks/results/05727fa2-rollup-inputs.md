# Deferred ROLLUP aggregate inputs

Working tree based on `05727fa2`, with level-specific ROLLUP input materialization
and unordered LIMIT demand. The [probe](../scripts/grouped-inputs.fsx) inserts
10,000 rows across 100 interleaved groups and verifies every result, including
all timed iterations. The ROLLUP case uses SUM(v) with immutable source inputs
and verifies the grand total as well as detail totals.

Embedded in-memory Debug engine, .NET SDK 10.0.401, Darwin 24.6 arm64, eight
logical processors, 4 GiB GC heap cap. Five warmups and nine alternating batches
of three executions per query. No concurrent fsdb build, test, native oracle,
or profile process. Background applications remain active.

| Query | Median ms/query | Median allocated bytes/query |
| --- | ---: | ---: |
| Scalar SUM | 5.473 | 6,755,840 |
| Grouped SUM | 11.958 | 13,254,080 |
| Grouped SUM with ROLLUP | 19.960 | 25,401,944 |
| Grouped SUM and DISTINCT COUNT | 17.085 | 21,540,896 |
| Grouped COUNT | 6.937 | 8,650,864 |
| Grouped SUM/COUNT/MIN/MAX | 27.930 | 32,504,720 |

This is a current snapshot, not a before/after speed claim. ROLLUP adds sorting,
subtotal evaluation, and deferred row state; comparison with ordinary grouped
SUM does not isolate one of those costs. Stateful expressions can require
additional retained values for each subtotal level, which this immutable-input
fixture does not measure.

```text
scalar-sum input=10000 median-ms=5.473 median-bytes=6755840 samples-ms=[7.205533333; 5.5704; 5.520466667; 5.541666667; 5.445366667; 5.4228; 5.473066667;
 5.404066667; 5.471833333]
grouped-sum input=10000 median-ms=11.958 median-bytes=13254080 samples-ms=[15.73856667; 12.20773333; 11.8157; 11.98026667; 11.51846667; 11.73446667;
 20.2584; 11.95806667; 11.81573333]
grouped-rollup input=10000 median-ms=19.960 median-bytes=25401944 samples-ms=[25.36396667; 19.3616; 19.26283333; 24.11043333; 19.9939; 19.24823333; 19.3445;
 19.9602; 20.05653333]
grouped-distinct input=10000 median-ms=17.085 median-bytes=21540896 samples-ms=[37.4515; 17.59476667; 16.9242; 17.35826667; 17.08453333; 16.80473333; 16.8921;
 16.77316667; 17.24453333]
grouped-count input=10000 median-ms=6.937 median-bytes=8650864 samples-ms=[7.445566667; 6.913566667; 6.827333333; 6.9195; 6.9678; 6.898866667; 6.9371;
 6.995; 6.948433333]
grouped-multiple input=10000 median-ms=27.930 median-bytes=32504720 samples-ms=[31.1165; 27.93; 27.87206667; 27.84466667; 27.9756; 27.5678; 28.1449; 27.8946;
 30.28123333]
```
