# Grouped-query performance after window binding validation

Revision `ceed2fc9`, clean worktree. The [maintained probe](../scripts/grouped-inputs.fsx)
checks every result over 10,000 rows and 100 interleaved groups. Embedded,
in-memory Debug engine on Darwin arm64, .NET SDK 10.0.401, eight logical
processors and a 4 GiB GC heap cap. Five warmups, nine alternating batches of
three executions. No concurrent fsdb build, test, native oracle, or profile.
Background applications remain active.

| Query | Median ms/query | Median allocated bytes/query |
|---|---:|---:|
| scalar-sum | 5.878 | 6756272 |
| grouped-sum | 12.434 | 13290120 |
| grouped-rollup | 20.744 | 25416960 |
| grouped-distinct | 17.761 | 21554544 |
| grouped-count | 6.972 | 8686912 |
| grouped-multiple | 28.408 | 32536424 |

This is a current snapshot, not an isolated before/after comparison. Relative
to the [earlier ROLLUP snapshot](05727fa2-rollup-inputs.md), medians are about
0.5–7.4% higher and allocations about 0.006–0.42% higher. Several correctness
changes separate the revisions, and timing noise is visible in the samples;
these observations do not establish a regression caused by window validation.
The fixture does not measure volatile assignment replay or state retained for
multiple subtotal levels. No MySQL speed comparison or durable-write claim is
made.

```text
scalar-sum input=10000 median-ms=5.878 median-bytes=6756272 samples-ms=[7.1275; 5.629866667; 5.679666667; 5.779; 5.5433; 6.131733333; 14.22653333;
 5.888566667; 5.878333333]
grouped-sum input=10000 median-ms=12.434 median-bytes=13290120 samples-ms=[15.88923333; 11.69853333; 11.98326667; 12.2304; 12.30343333; 12.55; 12.7501;
 12.70993333; 12.43426667]
grouped-rollup input=10000 median-ms=20.744 median-bytes=25416960 samples-ms=[26.7139; 19.27656667; 19.76146667; 22.66933333; 20.06143333; 20.7439;
 20.86883333; 22.38953333; 20.50553333]
grouped-distinct input=10000 median-ms=17.761 median-bytes=21554544 samples-ms=[38.23423333; 17.19016667; 17.19206667; 17.4346; 16.80896667; 18.03046667;
 18.30453333; 18.5484; 17.76066667]
grouped-count input=10000 median-ms=6.972 median-bytes=8686912 samples-ms=[7.3613; 6.972366667; 6.851366667; 6.896233333; 6.888033333; 6.868933333;
 7.136533333; 7.364066667; 6.998866667]
grouped-multiple input=10000 median-ms=28.408 median-bytes=32536424 samples-ms=[28.3672; 28.40833333; 29.50216667; 27.60816667; 27.28973333; 27.27826667;
 29.88266667; 29.49763333; 31.83346667]
```
