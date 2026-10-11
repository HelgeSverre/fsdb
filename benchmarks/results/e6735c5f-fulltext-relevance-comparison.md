# Full-text search after single-table relevance streaming

The slow-log accounting work exposed an existing execution cost: a
single-table natural-language `MATCH` query with `LIMIT` materialized and
sorted every posting candidate before returning its first rows. Commit
`e6735c5f` lets that query consume rows in descending relevance order. This
comparison checks the affected read path against its immediate predecessor,
`d6fc0e15`.

Both revisions ran sequentially on the same Apple M2 Max host with .NET
10.0.12 and native MySQL 8.4.11. `FSDB_BENCH_METHODS` selected only
`FullTextNaturalSearch` and `FullTextBooleanSearch` for `just bench-quick`.
Each run seeded 10,000 articles, 10,000 users, and 50,000 orders. BenchmarkDotNet
ShortRun used one launch, three warmups, and three measured iterations.
Fsdb was in-memory; MySQL was durable. These are read-only queries, so the
storage difference does not explain write latency. The raw reports are
[before](d6fc0e15-quick.md) and [after](e6735c5f-quick.md).

| Query | Before fsdb | After fsdb | MySQL before | MySQL after |
|---|---:|---:|---:|---:|
| Natural `MATCH(title,body) AGAINST('database') LIMIT 20` | 898.8 µs | 463.8 µs | 416.4 µs | 418.6 µs |
| Boolean `MATCH(title,body) AGAINST('+database +concurrency' IN BOOLEAN MODE) LIMIT 20` | 981.3 µs | 864.8 µs | 1,087.1 µs | 1,143.8 µs |

The natural-language query fell by 48% (about 1.94× faster). Its MySQL
control changed by less than 1% between runs, supporting a real improvement
in the affected fsdb path. The Boolean query's 12% lower reading is
directional only: the change does not target its explicit Boolean order, and
ShortRun's three iterations are too few to separate small effects from run
variance. This snapshot does not establish broad full-text or durable-write
performance; it measures two warmed, single-connection reads at one data size.
