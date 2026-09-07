# Grouped boolean full-text candidates

The general boolean evaluator now derives candidates from the expression's
actual boolean requirements. When a node has required children, it walks the
smallest required result and probes the others. Without required children, it
unions positive children only; excluded children no longer seed rows that must
be rejected afterward.

Both measurements used the default deterministic dataset (10,000 articles),
MySQL 8.4.11, and BenchmarkDotNet `ShortRun` on the same machine. The query
requires a selective group and an everywhere-present word:

```sql
SELECT id, title
FROM articles
WHERE MATCH(title, body)
      AGAINST ('+(database concurrency) +benchmark' IN BOOLEAN MODE)
LIMIT 20;
```

| Build | Target | Mean | Median |
|---|---|---:|---:|
| `63a67b3` | fsdb | 13.930 ms | 13.471 ms |
| `63a67b3` | MySQL | 3.639 ms | 3.626 ms |
| `4f5b76e` | fsdb | 3.685 ms | 3.672 ms |
| `4f5b76e` | MySQL | 3.538 ms | 3.541 ms |

The fsdb mean improved by about 3.8× in these short runs. The post-change mean
was about 1.04× the matched MySQL mean. ShortRun uses three measured
iterations, so these values establish direction and approximate scale rather
than production guarantees.
