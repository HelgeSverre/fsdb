# Benchmarks

fsdb vs a native MySQL 8.4 on identical schema, seed data, and queries,
via BenchmarkDotNet + MySqlConnector. The suite exists to find and track
hotspots, not to chase parity — fsdb optimizes for readable F# first.

## Contents

- [Running](#running)
- [Methodology](#methodology)
- [Recorded results](#recorded-results)
  - [Focused optimization reports](#focused-optimization-reports)
  - [Latency and scale](#latency-and-scale-snapshot)
  - [Durability](#durability-matched-latency)
  - [Concurrency](#concurrency-throughput)
  - [Full-text search](#full-text-search)

## Running

Choose a recipe by the behavior under investigation:

| Question | Recipe |
|---|---|
| General single-connection latency | `just bench` |
| Recently added SQL features | `just bench-features` |
| Fast directional check | `just bench-quick` |
| Commit durability cost | `just bench-durable` |
| Data-size slope | `just bench-scale` |
| Concurrent throughput | `just bench-load` or `just bench-load-scale` |
| Complete local campaign | `just bench-comprehensive` |

```sh
just bench               # full latency suite, results -> results/<git-sha>.md
just bench-features      # selected SQL features, results -> results/<git-sha>-features.md
just bench-quick         # ShortRun validation, results -> results/<git-sha>-quick.md
just bench-load          # 8-worker throughput, results -> results/<git-sha>-load.md
just bench-load-scale    # throughput at 1/2/4/8/16 workers
just bench-durable       # four-target durability-matched latency
just bench-scale         # scale-sensitive cases at 100k/500k rows
just bench-comprehensive # latency, durability, data scale, and worker scale
```

Prerequisites and isolation rules:

- Put MySQL 8.4's `mysql`, `mysqld`, and `mysqladmin` on `PATH`. The recipes
  start `mysqld` directly rather than using a Homebrew service.
- The primary MySQL process uses the disposable `benchmarks/mysql-data`
  directory. `bench-durable` also starts a no-fsync process under
  `benchmarks/mysql-data-nofsync` with
  `--skip-log-bin --innodb_flush_log_at_trx_commit=0 --sync_binlog=0`
  applied.
- The default ports are 3307 for fsdb, 3316 for MySQL, and 3317 for the
  no-fsync MySQL target. Override them with `FSDB_BENCH_PORT`,
  `FSDB_BENCH_MYSQL_PORT`, and `FSDB_BENCH_MYSQL_NOFSYNC_PORT`. The recipes
  refuse to share the selected fsdb port with another listener.
- Keep other heavy workloads off the machine. Two consecutive runs should
  agree on `PointSelectByPk` within roughly 20%; otherwise, discard the run.

### Focused runs

The public recipes select broad categories. For implementation work, narrow a
run with environment variables rather than editing benchmark attributes:

```sh
FSDB_BENCH_METHODS=JoinUsersOrders,CompositeJoinWithResidualEquality \
  just bench-quick

FSDB_BENCH_CATEGORIES=Planner just bench-quick
```

`FSDB_BENCH_METHODS` accepts comma-separated method names.
`FSDB_BENCH_CATEGORIES` accepts comma-separated BenchmarkDotNet categories.
The data-size variables `FSDB_BENCH_USERS`, `FSDB_BENCH_ORDERS`, and
`FSDB_BENCH_ARTICLES` override the seeded cardinalities.

A focused result is evidence for that shape, not a replacement for the broader
suite. Keep the generated provenance header with any tracked artifact.

## Methodology

### Process isolation

fsdb and the benchmark host build and run in Release mode. The benchmark
project disables portable PDBs so BenchmarkDotNet does not classify the run as
DEBUG, and the recipes use `dotnet exec` to avoid hot-reload environment state.

Both databases reset and reseed before each case. fsdb also restarts, which
prevents a timed-out or pathological query from affecting later measurements.
Mutation cases are therefore independent of BenchmarkDotNet's execution order.

Connection pooling is disabled so connection lifecycle behavior does not enter
per-operation measurements.

### Durability

The default suite measures in-memory fsdb without a WAL or `fsync`.
`bench-durable` adds two matched comparisons:

- WAL-backed fsdb against durable MySQL;
- in-memory fsdb against MySQL configured without commit-time `fsync`.

WAL-backed fsdb uses a plain `fsync` per commit. `Persistence.attach` avoids
.NET's `FileStream.Flush(true)`, which would issue macOS `F_FULLFSYNC`. A write
result is meaningful only when both engines pay, or both skip, the same
durability cost.

### Concurrent load

`bench-load` measures completed operations per second rather than latency. Its
disjoint and hot-row writes separate publication throughput from genuine
contention, alongside reads, inserts, upserts, `REPLACE`, explicit
transactions, and mixed traffic.

`FSDB_LOAD_WORKERS` accepts a comma-separated worker matrix, and
`FSDB_LOAD_TRIALS` controls repetition. Engine order alternates between trials.
Reports include relative standard deviation and retryable lock or deadlock
errors; throughput counts completed operations only.

The [autocommit insert publication run](results/d59c142-autocommit-insert.md)
separates sixteen serial round trips from sixteen overlapping ones. A
conservative direct path for ordinary physical-table inserts reduced fsdb's
single-insert latency by 22%, its serial burst by 27%, and its concurrent
burst by 35%. Statements that can expand into nested database writes retain
the private transaction root that provides statement atomicity.

The follow-up [direct-write run](results/3527901-direct-writes.md) applies the
same classification to physical-table `REPLACE` and simple single-table
writes. New-row `REPLACE`, existing-row `REPLACE`, and point `UPDATE` improved
by 39%, 26%, and 9% against the prior recorded implementation. Views,
subqueries, registered or stored functions, CTE and join-shaped writes, and
multi-table deletes continue to use private roots.

### Workloads and reporting

The default deterministic corpus contains 10,000 users, 50,000 orders, and
10,000 full-text articles. Override those sizes with `FSDB_BENCH_USERS`,
`FSDB_BENCH_ORDERS`, and `FSDB_BENCH_ARTICLES`. The feature matrix covers
views, triggers, constraints, generated columns, CTEs, windows, `JSON_TABLE`,
full-text modes, computed projections, transactions, and upserts.

One operation runs per invocation. BenchmarkDotNet controls warmup, outliers,
and statistics. Its allocation column measures the benchmark client process,
including MySqlConnector, but not either database server.

Sub-millisecond cases include a substantial fixed wire and MySqlConnector
cost. Use the point-query cases in the same result artifact as that run's
loopback baseline.

## Recorded results

These tables summarize immutable, linked result artifacts. They are historical
measurements, not claims about the current branch; use a fresh run for a current
comparison.

Each recipe writes one or more tracked `results/<git-sha>[-label].md` files
with a provenance header (revision, date, OS, .NET, server mode, and dataset).
Disposable MySQL data directories and BenchmarkDotNet intermediates remain
ignored. Representative historical snapshots, medians:

| Workload | f1b15ab (baseline) | a90dfae (indexed) | f4ba12a (streaming) | MySQL 8.4 |
|---|---:|---:|---:|---:|
| Point SELECT by PK | 1.32 ms | 103 µs | 111 µs | 38 µs |
| Prepared point SELECT | 22.9 ms* | 90 µs | 101 µs | 32 µs |
| Filter + sort + LIMIT scan | 21.6 ms | 15.8 ms | 6.3 ms | 1.9 ms |
| Single INSERT | 1.25 ms | 492 µs | 549 µs | 165 µs |
| Batch-100 INSERT | 142 ms | 8.6 ms | 10.1 ms | 1.2 ms |
| Single-row UPDATE | 2.41 s | 2.33 ms | 662 µs | 110 µs |
| Join users×orders | never finished | 201 ms | 10.7 ms | 268 µs |
| GROUP BY aggregate | 212 ms | 21.8 ms | 26.1 ms | 20.4 ms |
| JSON extract | 131 ms | 9.4 ms | 225 µs | 62 µs |

\* Numbers in the leftmost column after the join row were inflated by
benchmark poisoning because the timed-out join kept computing server-side.
Each benchmark case now runs against an isolated server process. See
`results/f1b15ab.md` for the original annotation.

Notable one-line context for the jump between the first two columns: an
O(n²) list-append in the UPDATE path, PK/unique hash indexes, a hash
equi-join, and `TcpClient.NoDelay` (Nagle's algorithm had been taxing every
round trip from the start).

### Focused optimization reports

The smaller reports isolate one implementation change against its immediate
baseline. They are easier to interpret than comparing unrelated full-suite
runs:

- statement and subquery work: [recursive CTE steps](results/8a32897-recursive-cte.md),
  [projected literal membership](results/2e36354-projected-literal-membership.md),
  and [projected predicate pushdown](results/ca5d377-projected-predicate-pushdown.md);
- correlated execution: [bare outer columns](results/ea70a23-correlated-bare-outer-columns.md),
  [outer expressions](results/ddafec8-correlated-outer-expressions.md), and
  [count cardinality](results/e883956-correlated-count-cardinality.md);
- writes and publication: [autocommit inserts](results/d59c142-autocommit-insert.md),
  [direct writes](results/3527901-direct-writes.md), and
  [independent publication](results/d9d9bd4-independent-publications.md);
- prepared predicates: [boolean scans](results/94a6026-prepared-boolean-scans.md),
  [scan predicates](results/8f119e1-prepared-scan-predicates.md), and
  [mutation predicates](results/584d606-prepared-mutation-predicates.md);
- join planning: [covered composite keys](results/55e7e5f-quick.md) and
  [composite join prefixes](results/5aa0131-quick.md);
- direct and correlated access:
  [single-table composite prefixes](results/582cff7-quick.md) and
  [projected correlated prefixes](results/b2b0bca-quick.md), followed by
  [compatible text-prefix cardinality](results/a50142b-quick.md) and the
  [broader correlated planner baseline](results/b9a6895-quick.md).

These reports explain a measured revision pair. They do not replace the live
compatibility and performance boundaries in [GAPS.md](../GAPS.md).

### Latency and scale snapshot

The [10k/50k latency](results/98bc883-quick.md) and
[100k/500k scale](results/98bc883-scale.md) runs separate indexed paths from
work that still scans or replans its input:

| Workload | fsdb at 10k | MySQL at 10k | fsdb at 100k | MySQL at 100k |
|---|---:|---:|---:|---:|
| Single INSERT | 299 µs | 124 µs | 404 µs | 115 µs |
| Single-row UPDATE | 292 µs | 130 µs | 369 µs | 142 µs |
| REPLACE by PK | 241 µs | 152 µs | 322 µs | 156 µs |
| Secondary range | 143 µs | 44 µs | 195 µs | 46 µs |
| Indexed join | 338 µs | 184 µs | 302 µs | 194 µs |
| Uncorrelated IN subquery | 499 µs | 162 µs | 519 µs | 163 µs |
| GROUP BY aggregate | 27.2 ms | 20.1 ms | 313 ms | 200 ms |
| Window query | 85.2 ms | 98.9 ms | 1.21 s | 1.20 s |
| Natural FULLTEXT | 1.06 ms | 429 µs | 10.3 ms | 3.89 ms |

In these two artifacts, point writes, indexed joins, uncorrelated membership,
JSON extraction, and secondary ranges have shallow slopes. Scans, grouping,
non-indexed updates, decimal membership, and window execution grow with their
input. The scale samples have high variance, but both engines finish in the
same order of magnitude. The former multi-second fsdb-only cliff is absent in
these runs; the joined FULLTEXT query is still 6.3x slower than MySQL at the
larger recorded size.

That quick matrix also exposes constant-factor work hidden by slope alone.
Point reads are 237 µs versus 40 µs, recursive CTE evaluation is 953 µs versus
54 µs, and correlated indexed counts are 1.22 ms versus 207 µs.

A sampled CPU trace of a saturated recursive-CTE workload attributes most
active managed time to per-statement query handling, binding, dynamic scope,
and `AsyncLocal` state transitions rather than the 100-row recursive body.
At that revision, shared statement setup was the next profiling seam rather
than a special-purpose CTE container.

The [low-cardinality join profile](results/1c2270d-low-cardinality-joins.md)
compares an indexed join with an otherwise identical unindexed hash-join twin.
The planner uses observed distinct-key counts to avoid repeated broad index
bucket resolution when the full join result is consumed, while preserving the
index path for early-stopping queries.

### Durability-matched latency

The [`ebc3fca` run](results/ebc3fca-durable.md) compares in-memory fsdb,
WAL-backed fsdb, durable MySQL, and MySQL without commit-time `fsync`:

| Workload | fsdb | fsdb-wal | mysql | mysql-nofsync |
|---|---:|---:|---:|---:|
| Single INSERT | 85 µs | 142 µs | 114 µs | 38 µs |
| Batch-100 INSERT | 2.07 ms | 2.23 ms | 1.22 ms | 967 µs |
| REPLACE by PK | 100 µs | 1.13 ms | 122 µs | 52 µs |
| Single-row UPDATE | 128 µs | 1.49 ms | 116 µs | 44 µs |
| Two-row transaction | 339 µs | 3.01 ms | 216 µs | 136 µs |

The WAL adds little to batched INSERT, but UPDATE, REPLACE, UPSERT, and
explicit transactions remain roughly 9–14× slower than durable MySQL. This
is a persistence/publication-path gap rather than the in-memory row-mutation
cost.

### Concurrency throughput

The broad [eight-worker baseline](results/4897506-load.md) predates optimistic
row-conflict merging:

| Workload | fsdb | mysql |
|---|---:|---:|
| update-distinct | 21,777 | 20,399 |
| insert | 5,673 | 22,703 |
| mixed read/write | 20,537 | 47,801 |

The [eight-worker `98bc883` run](results/98bc883-load.md) records completed
throughput with no retryable lock/deadlock errors:

| Workload | fsdb ops/s | MySQL ops/s | fsdb/MySQL |
|---|---:|---:|---:|
| Point read | 12,677 | 105,522 | 0.12x |
| Distinct UPDATE | 4,721 | 11,011 | 0.43x |
| Hot UPDATE | 6,026 | 15,438 | 0.39x |
| Distinct UPSERT | 4,225 | 8,085 | 0.52x |
| INSERT | 5,569 | 15,319 | 0.36x |
| Distinct REPLACE | 5,662 | 14,255 | 0.40x |
| Two-row transaction | 2,038 | 9,225 | 0.22x |
| Mixed read/write | 6,974 | 25,746 | 0.27x |

The default load run is one five-second trial and therefore directional. A
separate 64-worker prepared-transaction campaign completed 12,800 operations
with exact MySQL outcome parity and no failures: fsdb reached 781 tx/s at p99
107 ms, versus MySQL's 266 tx/s at p99 679 ms. That contrast separates the
engine's efficient hot-account transaction path from the wire and generic
statement overhead visible in the broad load matrix.

### Full-text search

The post-index [10k-article](results/8e904fd-fulltext-index.md) and
[100k-article](results/8e904fd-fulltext-index-scale.md) comparisons, followed
by the [posting-candidate comparison](results/ef4b4ab-fulltext-postings.md),
cover natural, boolean, accent-aware, and boolean-prefix queries.

Against the pre-index 10k baseline, natural search fell from 53.7 ms to 2.76
ms, accent-aware search from 51.2 ms to 1.62 ms, boolean search from 50.7 ms
to 3.19 ms, and prefix search from 49.5 ms to 1.44 ms. At the larger recorded
size, posting-driven boolean evaluation is 56.0 ms versus MySQL's 10.7 ms,
while maintained prefix postings are 33.9 ms versus 2.62 ms.

The profiles attribute the remaining cost to OR predicates, projection-only
MATCH, and the general result pipeline rather than document re-tokenization or
vocabulary scans. See [GAPS.md](../GAPS.md#11-full-text-search) for the current
boundary.
