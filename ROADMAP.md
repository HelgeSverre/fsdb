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
**Gate:** `php artisan migrate` on chatflow (91 migrations) succeeds. Status: ☐

## M5 — Chatflow test suite
Joins, aggregates, subqueries, savepoints, JSON functions, expression breadth.
**Gate:** chatflow phpunit suite green against fsdb. Status: ☐

## M6 — Extensibility polish
Public `registerScalar` / `registerAggregate` API, docs, examples.
**Gate:** README example runs as written. Status: ☐
