# Required-prefix boolean full-text intersection

Required prefix terms now use the same direct posting intersection as required
exact words. Prefix frequencies still come from the maintained prefix postings,
so score and corpus-wide document frequency are unchanged; the execution path
avoids the general evaluator's intermediate persistent maps and candidate set.

Both measurements used the default deterministic dataset (10,000 articles),
MySQL 8.4.11, and BenchmarkDotNet `ShortRun` on the same machine. The query was:

```sql
SELECT id, title
FROM articles
WHERE MATCH(title, body) AGAINST ('+resu*' IN BOOLEAN MODE)
LIMIT 20;
```

| Build | Target | Mean | Median |
|---|---|---:|---:|
| `43e3cdd` | fsdb | 997.4 µs | 965.3 µs |
| `43e3cdd` | MySQL | 318.2 µs | 318.6 µs |
| `4897645` | fsdb | 467.9 µs | 467.9 µs |
| `4897645` | MySQL | 319.5 µs | 319.0 µs |

The fsdb mean improved by about 2.1× in these short runs. The post-change fsdb
mean was about 1.5× the matched MySQL mean. ShortRun uses three measured
iterations, so these values establish the direction and approximate size of
the improvement rather than a production throughput guarantee.

At this build, optional, excluded, raised/lowered, soft, phrase, proximity, and
grouped boolean expressions remained on the general evaluator.
