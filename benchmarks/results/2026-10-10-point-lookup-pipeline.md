# Point-lookup pipeline profile

The [same-host wire snapshot](2026-10-10-constant-index-bounds.md) found a
roughly 5–6× fixed primary-key latency gap versus MySQL. This in-process
profile separates three fsdb stages for the same one-row lookup: parse a SQL
string, execute a pre-parsed statement, and handle a text query through the
session API. It uses a Release build at `5a9009ad`, a 10,000-row integer-key
table, 1,000 warmups per action, and five trials of 2,000 calls. The table
shows the median of trials 2–5; [the CSV](2026-10-10-point-lookup-pipeline.csv)
retains every trial.

```sh
dotnet build src/Fsdb/Fsdb.fsproj -c Release
dotnet fsi --nologo benchmarks/scripts/point-lookup-pipeline.fsx
```

| Predicate | Parse | Pre-parsed executor | Text handler |
|---|---:|---:|---:|
| `id=3` | 24.37 µs / 70.0 KB | 11.75 µs / 39.1 KB | 84.67 µs / 254.2 KB |
| `id=ROUND(PI())` | 34.72 µs / 104.3 KB | 16.89 µs / 49.5 KB | 95.42 µs / 273.5 KB |
| `id=BIT_COUNT(7)` | 31.16 µs / 97.7 KB | 15.34 µs / 46.1 KB | 94.33 µs / 266.0 KB |

Each action returns the same row. These are separate calls, not additive
components of one instrumented request: the text handler also performs
session, registry, diagnostics, and metadata work. The parser and pre-parsed
executor explain part of its latency and allocation, while the remainder is
large enough to justify profiling the session path next. The benchmark does
not include sockets, so its absolute latency cannot be compared directly with
the wire snapshot.

An exploratory SELECT dispatch shortcut and a CREATE/DROP regex prefix guard
saved fewer than 200 bytes per text query. Their timing changes were smaller
than run-to-run variation, so neither was retained. The next optimization
should measure the session registry and statement-handling work more directly
before changing their contracts.
