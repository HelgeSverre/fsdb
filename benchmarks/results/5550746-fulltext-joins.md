<!--
sha: 5550746
date: 2026-08-25
dataset: 10,000 users, 50,000 orders, 10,000 articles
job: BenchmarkDotNet ShortRun, 3 measured iterations
targets: in-memory fsdb; durable MySQL 8.4
-->

# Full-text join checkpoint

| Workload | fsdb | MySQL 8.4 | Ratio |
|---|---:|---:|---:|
| Natural search | 2.681 ms | 0.406 ms | 6.6x |
| Boolean search | 1.983 ms | 1.127 ms | 1.8x |
| Accent-folded search | 1.203 ms | 0.225 ms | 5.3x |
| Boolean prefix search | 0.665 ms | 0.320 ms | 2.1x |
| Full-text result joined to users by primary key | 5.464 ms | 0.411 ms | 13.3x |

The generalized source planner preserves the existing natural-search latency
(2.681 ms here versus 2.669 ms at the preceding postings checkpoint). The
join workload includes corpus scoring, posting-candidate selection, and a
primary-key lookup into `users`; its remaining gap is execution overhead, not
a return to table-size Cartesian materialization.

These are short diagnostic runs, not publication-grade measurements.
