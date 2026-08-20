# Compatibility

How fsdb's MySQL compatibility is validated, and the current evidence.

## Validation method

fsdb is validated by migrating and running the test suites of real Laravel
applications against it, unmodified. Where a suite diverges from its sqlite
baseline, the dispute is settled by running the same tests against a real
MySQL 8.4 — fsdb must match MySQL, not sqlite.

`torture/` adds a differential fuzz harness: generated SQL runs against both
fsdb and a MySQL 8.4 oracle, and the first divergence is classified and
replayable (`torture/scripts/run.sh suite`; exit 0 = pass/known gaps, 2 = new
fsdb findings).

The ordered DML battery covers `REPLACE` values, `REPLACE ... SELECT`, and
`REPLACE ... SET` in both client affected-row modes. It includes unchanged
replacements, conflicts spanning separate unique keys, same-statement key
reuse, defaults, and source-row ordering.

## Gauntlet

| Application | Laravel | Migrations | Result |
|---|---|---|---|
| App A | 11 | 94 | full parity, 0 failures |
| App B | 11 | 205 | parity; 5 residual failures reproduce identically on real MySQL (app-side factory/collation bugs) |
| App C | 10 | 160 | behavioral equivalence with real MySQL (identical failure set from an app-side factory bug) |
| App D | 13 | 43 | parity; 1 residual failure is a sqlite-only PRAGMA introspection test that fails identically on real MySQL |
| App E | 13 | 487 | full 10,972-test suite: 10,913 passed, all 36 failures individually verified as app-side bugs or real-MySQL-identical; one documented order divergence on an unordered query |

The applications are private codebases, identified here only by framework
version and size.

## Milestones

All ten original milestones are done — wire protocol, PDO/mysql-CLI
compatibility, the SQL engine core, Laravel migrations, test-suite parity,
the embedding API, opt-in persistence, EXPLAIN + multi-table DML,
performance-without-ugliness, and the streaming pipeline. Each shipped
against a runnable acceptance gate (a real external client, the reference
app's suite, or a benchmark threshold); the per-milestone evidence lives in
git history.

## GUI clients and introspection

The introspection surface was built from what real clients actually send:
TablePlus 26.9.6's queries extracted verbatim from its binary, and
phpMyAdmin 5.2.x's query builders read from source. All 22
`information_schema` tables have column sets diffed byte-for-byte against a
live MySQL 8.4.11 (`SHOW COLUMNS` per table, both sides), and a ~70-query
replay fixture covering both clients' connect/browse/structure flows runs
with a single divergence: `SHOW SLAVE STATUS`, which real 8.4 also rejects
with 1064. Object catalogs fsdb has no objects for (views, routines,
triggers, events) are genuinely empty rather than stubbed; PROCESSLIST,
`Threads_connected`, and `KILL` operate on the real connection registry.

## Server settings

The tunables an operator would plausibly change live in `Fsdb.Limits` and can
be set from a my.cnf-style file's `[mysqld]` section via `--defaults-file`
(`max_allowed_packet`, `max_connections`, `wait_timeout`,
`innodb_lock_wait_timeout`, `cte_max_recursion_depth`, plus fsdb's own WAL
rotation thresholds). Size suffixes (`64M`, `1G`) work; other sections are
ignored, but an unrecognised line inside `[mysqld]` is a startup error rather
than a silent no-op. No config path is auto-discovered.

Two deliberate divergences from stock MySQL:

- **`wait_timeout` and `interactive_timeout` report 300, not 28800.** fsdb
  reaps a connection idle between commands after five minutes, because a
  half-open peer otherwise pins a socket and a task for eight hours. The
  number reported is the number enforced — a pool that sizes its idle-recycle
  from `wait_timeout` gets the truth instead of a connection the server closed
  hours earlier. Set `wait_timeout` in a defaults file to restore MySQL's
  value. `interactive_timeout` mirrors it because fsdb ignores
  `CLIENT_INTERACTIVE` at handshake.
- **Settings are startup-scoped.** `SET GLOBAL max_connections = N` is
  accepted and shows up in `SHOW GLOBAL VARIABLES`, but the running server
  keeps the value it started with; likewise `max_allowed_packet` is
  process-wide, not per session.

## Users, authentication, and privileges

fsdb has a real account system backed by a stored `mysql` schema (`user`,
`db`, `tables_priv`, `columns_priv`, `global_grants` — MySQL 8.4 column
shapes, oracle-verified). `CREATE USER` / `DROP USER` / `ALTER USER` /
`SET PASSWORD` / `GRANT` / `REVOKE` persist through the ordinary WAL/snapshot
path; passwords are mysql_native_password hashes verified at the handshake
(with an AuthSwitchRequest for clients that answer with caching_sha2 first);
statements are privilege-checked at global, database, and table scope with
MySQL's 1045/1142/1044/1227 error shapes. `SHOW GRANTS [FOR user]`,
`SHOW PRIVILEGES` (8.4's 73 rows), `information_schema.USER_PRIVILEGES`, and
`FLUSH PRIVILEGES` (no-op OK) are served; `DROP DATABASE mysql` is 3552.

Deliberate divergences (each marked `ponytail:` at its code site):

- One host per account, matched by name only; the connecting host always
  renders as `localhost`, accounts default to `'%'`.
- Enforcement covers parsed statements' top-level table references;
  SHOW/SET text probes and subqueries/derived tables are unchecked.
- No roles, dynamic privileges, column-level privileges, proxy users, or
  password expiry — the columns exist in the table shapes but stay at their
  defaults. 5 of MySQL's 38 `mysql.*` tables exist.
- `SHOW GRANTS` omits root's dynamic-privilege and PROXY lines.
