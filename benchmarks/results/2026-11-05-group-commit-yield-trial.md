# Group-commit leader yield trial

On current head `3102df6d`, a temporary change made a newly elected
group-commit leader call `Thread.Yield()` once before draining the WAL queue.
The intent was to let concurrent followers join the first batch. No other
queue or durability behavior was changed. The candidate was reverted after
measurement.

The load harness ran keyless inserts against WAL-backed fsdb and durable
native MySQL 8.4.11, with 1,000 seeded users, orders, and articles. Each
measurement used one second of warmup and two five-second trials. Baseline
and candidate builds alternated in A/B/A/B order. Each row is the reported
mean of its two trials; RSD is the harness's relative standard deviation.

| Run | Leader behavior | Writers | fsdb inserts/s | fsdb RSD | MySQL inserts/s | MySQL RSD |
|---:|---|---:|---:|---:|---:|---:|
| 1 | Current | 1 | 4,242 | 6.3% | 6,730 | 20.9% |
| 1 | Current | 8 | 6,847 | 16.4% | 29,620 | 1.9% |
| 2 | Yield | 1 | 5,023 | 12.5% | 10,425 | 5.0% |
| 2 | Yield | 8 | 10,208 | 3.9% | 35,965 | 5.9% |
| 3 | Current | 1 | 3,773 | 14.2% | 10,602 | 10.1% |
| 3 | Current | 8 | 9,087 | 9.4% | 28,231 | 1.7% |
| 4 | Yield | 1 | 3,598 | 1.1% | 5,813 | 19.5% |
| 4 | Yield | 8 | 6,500 | 24.4% | 20,362 | 19.3% |

The first candidate run improved, but the second fell below both baseline
runs at eight writers. MySQL throughput also varied widely, so the matched
control cannot isolate a yield effect. During the trial, unrelated host
processes consumed substantial CPU: OrbStack Helper was near 197%, a Claude
renderer near 53%, WindowServer near 43%, and an nginx worker near 31% in a
post-run process snapshot. The data volume had only about 5–6 GiB free.
These are observed confounders, not a proven explanation of each run's rate.

This trial provides no reliable evidence for adopting the yield. The source
change was reverted; no production scheduling policy changed. The next
throughput experiment needs a less contended host and a control that stays
stable across bracketed runs, or a more direct queue-stage measurement.
