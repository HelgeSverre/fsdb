# REGEXP wire latency snapshot

Measured on 2026-10-09 with native loopback servers on macOS 15.6, Apple M2 Max
(`Darwin 24.6.0 arm64`). The fsdb working tree was dirty (48 porcelain entries)
at `cab70b90d1cd2d006af1eb747b303e8042ea9607`, built in Release with
.NET SDK 10.0.401. The oracle was native MySQL 8.4.11. Fsdb used its in-memory
store; MySQL used its normal durable configuration. These are read-only,
source-free queries, so write durability was not exercised.

One Python 3.14.7 process using PyMySQL 2.2.8 reused one TCP connection to each server.
Each case had 100 warm-up queries and three timed runs of 1,000 identical
queries per target. Parameter values were encoded by PyMySQL into SQL text;
the measured time includes client encoding, loopback, server parsing, regex
matching, and result transfer. Values below are milliseconds per query. There
was no table data.

| Query | fsdb runs | MySQL runs | Median fsdb / MySQL |
|---|---:|---:|---:|
| `REGEXP_LIKE` with join-control word boundary | 0.714, 0.696, 0.973 | 0.141, 0.153, 0.219 | 0.714 / 0.153 (4.7×) |
| `REGEXP_LIKE` with `\Q{3,2}\E` | 0.705, 0.495, 0.328 | 0.076, 0.071, 0.168 | 0.495 / 0.076 (6.5×) |
| `REGEXP_SUBSTR` with `\R` on CRLF | 0.420, 0.454, 0.446 | 0.091, 0.138, 0.105 | 0.446 / 0.105 (4.2×) |
| `REGEXP_LIKE` with `[[:word:]]` | 0.388, 0.253, 0.199 | 0.082, 0.083, 0.086 | 0.253 / 0.083 (3.0×) |

The variation, particularly for the first two cases, makes this a directional
latency snapshot. It does not isolate regex compilation from the rest of the
wire and SQL paths. Profile that path before attributing the gap to the regex
translator or changing it for speed.

A separate in-process F# check using the same Release fsdb assembly compiled
each pattern 10,000 times and matched with one reused compiled regex 10,000
times, with 1,000 warm-ups and three runs. The median compilation times were
1.74 µs (`a\B‍b`), 1.71 µs (`\Q{3,2}\E`), 6.69 µs (`\R`), and 7.16 µs
(`[[:word:]]`). Reusing the regex took median matching times of 0.115, 0.103,
0.132, and 0.192 µs respectively. Compilation is real work, but it explains
only a small part of the source-free wire latencies above. Repeated evaluation
over table rows needs its own profile before introducing a cache.

An additional scan used 2,000 rows with a `VARCHAR(32)` payload alternating
`alpha` and `a‍b`. Setup writes were excluded; each read had five warm-ups
and ten timed executions over the same connections. Median milliseconds per
query were:

| Query | fsdb | MySQL |
|---|---:|---:|
| `COUNT(*)` | 0.528 | 0.226 |
| `COUNT(*) WHERE payload IS NOT NULL` | 1.319 | 0.286 |
| `COUNT(*) WHERE payload LIKE '%a%'` | 19.039 | 0.339 |
| `COUNT(*) WHERE REGEXP_LIKE(payload, '[[:word:]]', 'c')` | 28.324 | 0.538 |

The regex scan is roughly 53× MySQL at this small size, but `LIKE` is also
slow on the same fsdb expression path. Regex compilation on every row is a
plausible contributor; it is not sufficient to explain the whole gap. A
profile of expression evaluation and a larger dataset should precede a cache
or execution-plan specialization.

The 10,000-row follow-up exposed repeated output-format metadata inference
while evaluating text predicates. A small `displayValueForText` fast path now
returns an already-materialized `VString` directly; numeric and temporal values
retain the existing formatter. With the same 10,000-row fixture, the pre-change
`LIKE` runs ranged from 42 to 130 ms (median 79 ms), while the new runs ranged
from 20 to 27 ms (median 23 ms). REGEXP remained near 41 ms. The ranges are
more informative than the medians here because the pre-change run was noisy.
`just check` passed 3,220 tests, including a numeric `ZEROFILL`/ordinary-text
display regression. The Docker contract run at
`torture/artifacts/runs/20261009T170207794-98286/contracts` passed the new
display case and retained exactly the same nine identifier-case differences
as before.

A bounded global compiled-regex cache was tried during profiling. Isolated
compilation became cheaper, but the 10,000-row SQL scan changed by only a few
milliseconds amid large run-to-run variation. That cache was removed; the
remaining REGEXP scan gap needs a better-localized improvement.

For a 10,000-row REGEXP scan, a ten-second .NET sampled-thread trace covered
195 repeated queries. Within sampled `evalExpr` stacks, about 47% included
`metadataOfExpr` and about 32% included `Regexp.compile` (inclusive times
overlap, and thread sampling is not an exact CPU attribution). The string
fast path reduced metadata work for text values; it did not materially improve
the REGEXP scan. The follow-up adds a statement-local compiled-pattern memo
per REGEXP expression. It reuses the result while pattern, match type, and
collation stay the same, and replaces it when any of them changes. Its size is
bounded by the number of REGEXP expressions in the statement, including when
patterns vary by row. Both successful compilations and errors are cached.

On the same 10,000-row fixture, with five warm-ups and twelve timed reads on a
reused connection, the REGEXP scan median fell from 41.6 ms before this change
to 28.4 ms after it (about 32%). The after-change `LIKE` median was 26.0 ms.
These are separate short same-host runs with visible timing variation, so the
comparison is directional. The memo removes repeated compilation for a stable
row filter; per-row expression evaluation and other scan costs remain. The
row-varying pattern and match-type regression passed, `just check` passed
3,221 tests, and the full Docker contract run at
`torture/artifacts/runs/20261009T171004941-99821/contracts` passed that new
case while retaining exactly the same nine identifier-case differences.
