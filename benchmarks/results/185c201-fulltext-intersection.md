# Full-text candidate intersection

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11,
BenchmarkDotNet 0.14.0, MySQL 8.4.11. The previous fsdb server is
`024ade5`; the current server is `185c201`. Both used the same benchmark
host, schema, SQL, 10,000-row article corpus, three warmups, and three
measured iterations.

`FullTextPointIntersection` searches for the deliberately broad term
`application` together with `id = 5000`.

| Engine | Previous | Current | Change |
|---|---:|---:|---:|
| fsdb | 7.200 ms | 242.5 us | -96.6% |
| MySQL | 2.234 ms | 2.144 ms | -4.0% |

The fsdb change moves equality, literal-IN, range, and spatial candidates
ahead of full-text scoring on single-table reads and writes. Document
frequency and query-expansion seeds still come from the complete corpus, so
the narrower execution preserves the original relevance scores.

Command:

```sh
FSDB_BENCH_METHODS=FullTextPointIntersection just _bench-run --quick
```
