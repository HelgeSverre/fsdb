# Full-text join candidate intersection

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11,
BenchmarkDotNet 0.14.0, MySQL 8.4.11. The previous fsdb server is
`c65b021`; the current server is `c599f76`. Both used the same benchmark
host, schema, SQL, 10,000-row article and user tables, three warmups, and
three measured iterations.

`FullTextJoinedPointIntersection` searches for the broad term `application`,
joins the article to its user row, and restricts the article source to
`a.id = 5000`.

| Engine | Previous | Current | Change |
|---|---:|---:|---:|
| fsdb | 20.391 ms | 302.5 us | -98.5% |
| MySQL | 1.937 ms | 2.215 ms | +14.4% |

The full-text path now shares the ordinary all-inner-join predicate planner.
Compatible source-local equality, literal-IN, range, and spatial candidates
restrict scoring before the join while residual evaluation still uses the
prepared source rows. Corpus statistics remain global, preserving relevance
scores.

Command:

```sh
FSDB_BENCH_METHODS=FullTextJoinedPointIntersection just _bench-run --quick
```
