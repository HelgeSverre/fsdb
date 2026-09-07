# Required-word boolean full-text intersection

The common boolean shape made entirely of required exact words now walks its
smallest posting list and probes the remaining postings directly. It produces
the same per-row TF × IDF² score as the general boolean evaluator, including
duplicate query terms and candidate-scoped execution, without constructing an
intermediate persistent score map and candidate union for every term.

Both measurements used the default deterministic dataset (10,000 articles),
MySQL 8.4.11, and BenchmarkDotNet `ShortRun` on the same machine. The query was:

```sql
SELECT id, title
FROM articles
WHERE MATCH(title, body)
      AGAINST ('+database +concurrency' IN BOOLEAN MODE)
LIMIT 20;
```

| Build | Target | Mean | Median |
|---|---|---:|---:|
| `8295cfd` | fsdb | 3.801 ms | 3.654 ms |
| `8295cfd` | MySQL | 2.429 ms | 2.362 ms |
| `31e863e` | fsdb | 1.298 ms | 1.111 ms |
| `31e863e` | MySQL | 1.179 ms | 1.181 ms |

The fsdb mean improved by about 2.9× in these short runs. The post-change fsdb
mean was about 1.1× the matched MySQL mean. ShortRun uses three measured
iterations and the confidence intervals are wide, so the result establishes a
clear regression-sized improvement rather than a precise production ratio.

Optional, excluded, raised/lowered, soft, prefix, phrase, proximity, and grouped
boolean expressions deliberately remain on the general evaluator.
