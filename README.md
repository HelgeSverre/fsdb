# fsdb

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](global.json)
[![MySQL 8.4](https://img.shields.io/badge/MySQL-8.4%20compatible-4479A1.svg)](https://dev.mysql.com/)

A MySQL-compatible database server in idiomatic F#. It speaks the MySQL wire
protocol — point `mysql`, PDO, or any MySQL driver at it and run queries
against an in-memory engine. Readable F# is the primary goal; raw performance
is not.

## Quick start

```sh
dotnet run --project src/Fsdb        # listens on 127.0.0.1:3307
mysql --protocol=tcp -h127.0.0.1 -P3307 -e 'SELECT 1'
```

Or install it as a single binary:

```sh
just install      # publishes to ~/.local/bin/fsdb, then: fsdb --help
```

```
USAGE: fsdb [--help] [--port <port>] [--listen <address>] [--data-dir <path>]
            [--version]

OPTIONS:

    --port, -p <port>     listen port (default 3307)
    --listen <address>    bind address (default 127.0.0.1)
    --data-dir <path>     persist data here (WAL + snapshots); omit for
                          in-memory
    --version             print the fsdb version and exit
    --help                display this list of options.
```

## How it works

One pipeline, all the way down:

```
client ── MySQL wire protocol ──► Packet ──► Protocol ──► QueryHandler ──┐
                                                                        ▼
                     Parser (FParsec) ──► AST ──► Executor ──► lazy seq ──► rows
                                                                        ▲
                     Storage (catalog, immutable snapshots) ◄── Persistence
```

- **Grammar** — a parser-combinator SQL grammar over an AST of discriminated
  unions; `SELECT`s compile to a logical plan that executes lazily (`LIMIT`
  stops the scan once enough rows survive).
- **Engine** — databases and tables live in a value-swapped catalog; every
  write produces an immutable snapshot, which is what makes transactions
  (`BEGIN`/`COMMIT`/`ROLLBACK`) free: each snapshot is a consistent view.
  PRIMARY KEY/UNIQUE lookups go through a hash index; equi-joins hash-join,
  everything else is a scan.
- **Collations & charsets** — all 89 utf8mb4 collations MySQL 8.4 ships,
  honored per-column and per-`SET collation_connection`; `GROUP BY`/`DISTINCT`/
  `UNION`/joins/unique keys all fold by the column's own collation. Charsets
  `utf8mb4`/`latin1` (cp1252)/`ascii`/`binary` with MySQL's write-time
  semantics, plus `CONVERT(x USING …)` and `_charset'…'` introducers.
- **Prepared statements** — `COM_STMT_PREPARE`/`COM_STMT_EXECUTE` bind
  parameter `Value`s into the parsed AST (`?` → `Placeholder` → `Lit`), so a
  bound value keeps its real type and never round-trips through string
  escaping.
- **Persistence** — opt-in `--data-dir`: a binary WAL (`[len][crc32]` records)
  plus snapshot, fsync'd and replayed on startup; omit it and everything
  lives in memory.

## SQL surface

```sql
CREATE TABLE users (
  id BIGINT AUTO_INCREMENT PRIMARY KEY,
  name VARCHAR(100) COLLATE utf8mb4_bin,
  tags JSON,
  joined_at DATETIME DEFAULT CURRENT_TIMESTAMP
);

INSERT INTO users (name, tags) VALUES
  ('Ada',   '{"langs": ["fsharp"]}'),
  ('Grace', '{"langs": ["cobol"]}');

-- window functions
SELECT name, ROW_NUMBER() OVER (ORDER BY joined_at) FROM users;

-- JSON paths
SELECT name, JSON_EXTRACT(tags, '$.langs[0]') FROM users;

-- per-column and connection collation
SET collation_connection = utf8mb4_bin;
SELECT 'åge' = 'age';                        -- 0
SELECT 'åge' = 'age' COLLATE utf8mb4_0900_ai_ci;  -- 1

-- joins, derived tables, transactions, EXPLAIN
BEGIN;
SELECT u.name, COUNT(*) FROM users u
  JOIN (SELECT * FROM orders WHERE total > 100) o ON o.user_id = u.id
  GROUP BY u.name HAVING COUNT(*) > 1;
COMMIT;

EXPLAIN SELECT * FROM users WHERE id = 1;
```

Plus `UNION [ALL]`, `HAVING`, `NATURAL`/`USING` joins, `LAG`, `GROUP_CONCAT`,
information_schema views, `SHOW CREATE TABLE`, multi-table `UPDATE`/`DELETE`,
and `--data-dir` durability across restarts.

## Using it from your stack

It's just MySQL on port 3307 — configure any client like a local MySQL:

```php
// PDO
$pdo = new PDO('mysql:host=127.0.0.1;port=3307;dbname=app', 'root', '');
```

## Extensibility

Every function call — including built-ins like `CONCAT` and `JSON_EXTRACT` —
resolves through one registry, SQLite-style. Embed fsdb in your own program and
register a function before you start listening:

```fsharp
open Fsdb
open Fsdb.Value

let slug (s: string) =
    System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim '-'

[<EntryPoint>]
let main _ =
    Db.create ()
    |> Db.registerScalar "slugify" (function
        | [ VString s ] -> VString(slug s)
        | _ -> VNull)
    |> Db.listen System.Net.IPAddress.Loopback 3307
    |> Async.RunSynchronously

    0
```

`Db.registerAggregate` works the same way for aggregate functions — the fold
receives every non-NULL row's value and returns one:

```fsharp
let median (values: Value list) =
    let sorted = values |> List.choose (function VInt i -> Some(float i) | _ -> None) |> List.sort

    match sorted with
    | [] -> VNull
    | _ ->
        let mid = sorted.Length / 2

        if sorted.Length % 2 = 0 then
            VDouble((sorted.[mid - 1] + sorted.[mid]) / 2.0)
        else
            VDouble sorted.[mid]

let db =
    Db.create ()
    |> Db.registerAggregate "MEDIAN" median
```

A custom function can override a built-in of the same name — the registry
doesn't distinguish "shipped with fsdb" from "registered by the embedder":

```fsharp
// Deterministic timestamps for reproducible tests.
Db.create ()
|> Db.registerScalar "NOW" (fun _ -> VDateTime(System.DateTime(2026, 1, 1)))
```

Add `--data-dir` durability to the embedded server the same way — a binary WAL
and snapshot, replayed on restart:

```fsharp
Db.create () |> Db.withDataDir "./fsdb-data" |> Db.listen System.Net.IPAddress.Loopback 3307
```

`Db.withLogger` hooks a log sink in the same style. See
`tests/Fsdb.Tests/IntegrationTests.fs` for the full round-trip test
(`SLUGIFY`/`MEDIAN`) against a real client over the wire.

## Benchmarking

`benchmarks/Fsdb.Benchmarks` runs fsdb head-to-head against a native MySQL
8.4, same schema, same seeded data, same queries, via BenchmarkDotNet.

```sh
just bench         # full latency suite, ~30 min, results -> benchmarks/results/<git-sha>.md
just bench-quick   # ShortRun job for fast local iteration, no results file
just bench-durable # durability-matched: fsdb WAL vs MySQL fsync/no-fsync, results -> <git-sha>-durable.md
just bench-scale   # latency suite at 100k users / 500k orders
just bench-load    # N-writer throughput under concurrency (ops/sec)
```

Both servers run ad hoc (no brew services) and are torn down afterwards.
fsdb optimizes for readable, idiomatic F# over raw speed, so expect MySQL to
win most of these — the numbers are here to find and track the hotspots,
not to chase parity.

## License

MIT — see [LICENSE](LICENSE).
