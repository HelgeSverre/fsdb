# fsdb roadmap

Each milestone has a runnable acceptance gate. A milestone is done only when its
gate passes against a real external client.

## M1 — Wire protocol skeleton
`mysql --protocol=tcp -h127.0.0.1 -P3307 -e 'SELECT 1'` returns a resultset.
Handshake, mysql_native_password auth, packet framing, COM_QUERY, COM_PING,
COM_QUIT, text resultset encoding.
**Gate:** mysql CLI gets `1` back. Status: ✅

## M2 — PDO connects
Session variables (`SET NAMES`, `sql_mode`), `@@version`, `SELECT DATABASE()`.
**Gate:** a 5-line PHP PDO script connects and queries. Status: ✅

## M3 — SQL engine core
FParsec parser, in-memory storage, Value coercion rules.
CREATE TABLE / INSERT / SELECT with WHERE, ORDER BY, LIMIT via mysql CLI.
**Gate:** Expecto suite + mysql CLI session exercising CRUD. Status: ✅

## M4 — Laravel migrations
ALTER TABLE, indexes, foreign keys, information_schema virtual tables,
prepared statements (COM_STMT_*), transactions.
**Gate:** `php artisan migrate` on chatflow (94 migrations) succeeds, plus
`migrate:status` (all Ran), `migrate:fresh` (DROP via SHOW/information_schema),
and a second consecutive `migrate` (Nothing to migrate). Status: ✅

## M5 — Chatflow test suite
Joins, aggregates, subqueries, savepoints, JSON functions, expression breadth.
**Gate:** `vendor/bin/pest tests --no-coverage --compact` (not `php artisan
test` — that only resolves phpunit.xml's `Unit`/`Feature` testsuites, 269
tests, and silently skips `tests/Arch`/`tests/Integration`, which the sqlite
REFERENCE baseline's `vendor/bin/pest tests` does sweep in, 304 tests) run
against fsdb on port 3307, same scope/command both sides, dot-pattern
compared against the sqlite baseline. Status: ✅ (287 passed, 15 skipped, 2
todos, 787 assertions — parity with the sqlite baseline)

## M6 — Extensibility polish
Public `registerScalar` / `registerAggregate` API, docs, examples.
**Gate:** README example runs as written. Status: ✅ (`Fsdb.Db` embedding
facade — `Db.create`/`registerScalar`/`registerAggregate`/`listen`; the
README's compilable example builds against `Fsdb.dll` as written, and
`tests/Fsdb.Tests/IntegrationTests.fs`'s "Db.registerScalar/registerAggregate
are queryable over the wire" test proves SLUGIFY/MEDIAN work over a real
MySqlConnector connection)

## M7 — Persistence
Opt-in durability via `--data-dir`: WAL + snapshot, replay on startup.
Default stays pure in-memory (tests unchanged).
**Gate:** with --data-dir: `artisan migrate`, restart fsdb, `migrate` says
"Nothing to migrate"; plus kill -9 mid-write leaves committed data intact
and replayable. Status: ☐

## M8 — EXPLAIN + semantics cleanups
EXPLAIN (tabular; reports the executor's actual full-scan behavior, no fake
index usage), multi-table UPDATE/DELETE with JOINs, UPDATE/DELETE ORDER
BY+LIMIT (currently parsed but ignored), AFTER/FIRST column positioning.
**Gate:** EXPLAIN on join/subquery queries via mysql CLI; UPDATE/DELETE JOIN
semantics differential-verified against real MySQL 8 (Docker oracle);
chatflow suite still at exact parity. Status: ☐
