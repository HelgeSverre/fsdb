# Native MySQL comparison after the geographic and ngram fixes

Fsdb ran from the working tree based on `f7c47dd7`, with the
document-order and nested ngram changes applied. MySQL was 8.4.11.
Both ran on the same Apple M2 Max over IPv4 loopback with disposable
data directories, using the durable settings from the
[four-engine run](cc2cb5f6-peer-comparison.md). Fsdb ran in Release
with WAL and server GC. The 32 GiB host had about 12 GiB of swap in use.

The same fixtures and result checks ran twice per engine. The second
pass reversed the engine order and workload order within each suite.
Values below average the two per-pass p50s in milliseconds. The correlated queries use
500 parents and 10,000 children, the planar index uses 10,000 points,
and the CJK queries use 10,000 articles.
[Per-pass measurements](2026-11-26-spatial-cjk-refresh.csv).

| Workload | Fsdb p50 (ms) | MySQL p50 (ms) | Fsdb latency / MySQL |
| --- | ---: | ---: | ---: |
| Correlated equality count | 4.712 | 1.419 | 3.3× |
| Correlated range count | 8.745 | 1.676 | 5.2× |
| Joined-view update | 2.351 | 0.136 | 17.3× |
| Indexed planar `MBRContains` | 0.303 | 0.120 | 2.5× |
| Geographic antimeridian distance | 0.184 | 0.075 | 2.4× |
| Geographic mixed turns, forward | 2.540 | 0.162 | 15.6× |
| Geographic mixed turns, reverse | 1.890 | 0.141 | 13.4× |
| Geographic near-hole touch | 2.427 | 0.132 | 18.4× |
| Geographic near-hole crossing | 2.431 | 0.152 | 16.0× |
| Japanese ngram phrase | 1.034 | 0.444 | 2.3× |
| Chinese ngram phrase | 1.112 | 0.454 | 2.5× |
| Korean ngram phrase | 0.930 | 0.421 | 2.2× |
| Japanese ngram wildcard | 0.495 | 0.333 | 1.5× |
| Japanese ngram exclusion | 1.016 | 0.461 | 2.2× |

The near-hole crossing passed in both Fsdb trials. It failed the
result check in the earlier run. The other three geographic edge
queries also passed, and each query checks four spatial predicates.
Both engines used range access on the planar spatial index.
Their current timings varied across passes. Correlated equality also
varied from 3.50 to 5.93 ms in Fsdb. These runs show current relative
latency on the shared host. Attributing a change in latency to a
commit would require alternating the old and new Fsdb builds under
the same conditions.

Concurrent write and point-read throughput were measured in the
[four-engine run](cc2cb5f6-peer-comparison.md#point-reads-and-durable-updates).
That report also records MariaDB and Dolt results for the applicable SQL
lanes. Both rejected the tested ngram parser DDL.

Reproduce the refresh with `native-feature-peers.fsx` using the
default suite followed by `edge-controls` for each engine and pass.
