# Flat boolean full-text postings

Flat boolean expressions made only of exact words or prefixes now score from
their maintained postings. Required terms begin with the smallest posting.
Without a required term, execution either probes a smaller upstream candidate
set or accumulates only rows touched by positive postings, then removes rows
matched by excluded terms. Phrase, proximity, and grouped expressions retain
the general evaluator.

Both measurements used the default deterministic dataset (10,000 articles),
MySQL 8.4.11, and BenchmarkDotNet `ShortRun` on the same machine. The newly
added optional-term query was:

```sql
SELECT id, title
FROM articles
WHERE MATCH(title, body) AGAINST ('database concurrency' IN BOOLEAN MODE)
LIMIT 20;
```

| Build | Target | Mean | Median |
|---|---|---:|---:|
| `e24279c` | fsdb | 2.153 ms | 2.148 ms |
| `e24279c` | MySQL | 172.0 µs | 172.1 µs |
| `1390114` | fsdb | 864.4 µs | 862.7 µs |
| `1390114` | MySQL | 175.1 µs | 175.7 µs |

The fsdb mean improved by about 2.5× in these short runs. It remains about
4.9× the matched MySQL mean, leaving a visible constant-factor gap outside
the posting scorer.

The same post-change run measured the required exact query at 1.037 ms for
fsdb versus 1.172 ms for MySQL, and the required prefix query at 560.7 µs
versus 332.0 µs. ShortRun uses three measured iterations, so these values
establish direction and approximate scale rather than production guarantees.
