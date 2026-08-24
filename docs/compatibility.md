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

The separate syntax lane starts from valid statements for recent features,
applies up to three deterministic bounded mutations, and compares MySQL and
fsdb error codes and SQLSTATEs
(`torture/scripts/run.sh syntax --syntax-cases 2000 --syntax-depth 3`). Its
comment operators cover block, hash, dash, executable-version, and
future-version comments while avoiding unsupported nested comments.

The ordered DML battery covers `REPLACE` values, `REPLACE ... SELECT`, and
`REPLACE ... SET` in both client affected-row modes. It includes unchanged
replacements, conflicts spanning separate unique keys, same-statement key
reuse, defaults, source-row ordering, composite-index updates, checked-view
inserts, and ordered compound-trigger side effects.

For `INSERT ... SELECT ... ON DUPLICATE KEY UPDATE`, assignments may read
qualified columns from direct, derived, and joined SELECT sources, including
source columns omitted from the inserted projection and qualified correlations
inside assignment subqueries. `VALUES(column)` remains available for the
candidate value.

The scalar-expression battery pins logarithmic, exponential, trigonometric,
IPv4, and IPv6 functions to MySQL 8.4 values and domain behavior. It also
covers phonetic, base64, ordinal, bit-selection, and common alias functions;
signed and fractional time arithmetic, period and day-number conversion, and
seeded randomness; `FROM DUAL`; row-value comparison and `IN` semantics;
multi-column subquery operand errors; and empty-group bit aggregate identities.

The parser accepts MySQL's `INSERT ... SET`, singular `VALUE` and optional
`ROW` constructors, substring-based `TRIM` modes, `ALL`/`DISTINCTROW`, and
the optimizer-only SELECT modifiers without changing query results.

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
phpMyAdmin 5.2.x's query builders read from source. All 23
`information_schema` tables have column sets diffed byte-for-byte against a
live MySQL 8.4.11 (`SHOW COLUMNS` per table, both sides), and a ~70-query
replay fixture covering both clients' connect/browse/structure flows runs
with a single divergence: `SHOW SLAVE STATUS`, which real 8.4 also rejects
with 1064. Stored views and triggers populate their real object catalogs;
routines and events remain genuinely empty rather than stubbed. PROCESSLIST,
`Threads_connected`, and `KILL` operate on the real connection registry.

## TLS transport

Supplying `--ssl-cert` and `--ssl-key` enables TLS 1.2 and 1.3 on the MySQL
listener. The same `ssl-cert`, `ssl-key`, and `require-secure-transport`
options work in the server sections of an option file.
`--require-secure-transport` rejects plaintext handshakes with 3159.
Embedding hosts supply an already-loaded `X509Certificate2` through
`Db.withTlsCertificate`; `Db.requireSecureTransport` enables the same
plaintext restriction. Client certificates and account-level `REQUIRE SSL` or
`REQUIRE X509` remain unsupported.

## Bulk wire commands

`CLIENT_MULTI_STATEMENTS` and `CLIENT_MULTI_RESULTS` permit semicolon-separated
COM_QUERY batches. Results retain one packet sequence and mark every successful
nonfinal result with `SERVER_MORE_RESULTS_EXISTS`; an error stops the remaining
statements.

`LOAD DATA LOCAL INFILE` is disabled by default. An operator enables it with
`local_infile=ON` in configuration or `SET GLOBAL local_infile = ON`; the
client must also negotiate `CLIENT_LOCAL_FILES`. fsdb requests the named file
from the client and never resolves or opens that path on the server. Uploads
are capped by `max_load_data_bytes` (64 MiB by default), drained to their empty
packet terminator, and then rejected with 1153 when over the cap.

The supported load subset is UTF-8/utf8mb4 input with one-character
field/line delimiters, optional enclosure and escape characters, `REPLACE` or
`IGNORE`, header-line skipping, and target column lists. Server-side
`LOAD DATA INFILE`, multibyte delimiters, `SET` assignments, and user-variable
targets remain unsupported.

## Views and triggers

MySQL views are stored queries, not persisted materialized results. `MERGE`
rewrites a referencing statement against the underlying tables, while
`TEMPTABLE` builds a temporary result for that statement; MySQL has no
materialized-view object. See the MySQL 8.4 documentation for
[view creation](https://dev.mysql.com/doc/refman/8.4/en/create-view.html) and
[view processing algorithms](https://dev.mysql.com/doc/refman/8.4/en/view-algorithms.html).

fsdb supports stored queries broadly and a narrow writable subset:

- `CREATE [OR REPLACE] VIEW name [(columns)] AS SELECT ...` and
  `DROP VIEW [IF EXISTS] name [, ...]`.
- Nested views, joins, unions, CTEs, grouping, windows, and the rest of the
  supported SELECT grammar inside a definition.
- TEMPTABLE-like evaluation: one typed result is materialized and reused for
  the referencing statement. A later statement reevaluates the definition
  against current base rows.
- Definitions execute under the recorded creator's privileges; revoking the
  definer's access to an underlying table makes later reads fail.
- Persistence through the WAL and snapshots, plus `SHOW [FULL] TABLES`,
  `SHOW CREATE VIEW`, `information_schema.TABLES`, and
  `information_schema.VIEWS` metadata.
- Projection metadata in `information_schema.COLUMNS`, `DESCRIBE`, `SHOW
  COLUMNS`, and `SHOW TABLE STATUS`. This path reads the stored definition
  without running it, so empty and nondeterministic views have the same
  metadata shape as populated views.

Direct projections over one base table, with an optional simple predicate but
without grouping, joins, or computed columns, accept `INSERT`, `INSERT ...
SELECT`, `REPLACE` in each supported source form, `ON DUPLICATE KEY UPDATE`,
`UPDATE`, and `DELETE`. Every written or referenced column must be exposed by
the view, and base-table writes run under the view definer's privileges. `WITH
CHECK OPTION` is enforced on this direct subset and reported by `SHOW CREATE
VIEW` and `information_schema.VIEWS`. `ALTER VIEW`, `ALGORITHM`, explicit
`DEFINER`, and `SQL SECURITY` remain unsupported. Creation validates
the saved SQL grammar but defers missing dependency and output-shape errors
until the first read; `SELECT *` follows the base table's current columns
instead of freezing them at creation.

Trigger execution has stronger behavioral coverage than its syntax breadth:

- Ordered `BEFORE` and `AFTER` triggers run for single-table `INSERT`,
  `UPDATE`, and `DELETE`. `FOLLOWS` and `PRECEDES` determine order within a
  timing/event slot.
- Bodies accept one statement or a `BEGIN ... END` sequence of `INSERT`,
  `REPLACE`, `UPDATE`, `DELETE`, and `SET NEW` statements.
- `OLD.column` and `NEW.column` bind the applicable row images. A `BEFORE`
  trigger may assign `NEW.column`; generated columns cannot be referenced.
- Multi-row statements fire once per affected row. Ignored candidates do not
  fire, and the update branch of `ON DUPLICATE KEY UPDATE` uses update
  triggers.
- The source write and every trigger effect are one atomic statement. A body
  error rolls all of them back, and effects participate normally in explicit
  transaction commit or rollback.
- Trigger writes may fire another table's trigger. Cycles and self-writes
  return error 1442, and the current chain-depth ceiling is eight.
- Bodies run after a definer-privilege check, reject DirectOnly extension
  functions, persist through the ordinary WAL/snapshot path, and appear in
  `SHOW TRIGGERS` and `information_schema.TRIGGERS`.
- A trigger follows its subject through `RENAME TABLE`; dropping the subject
  table or its database removes the stored trigger definition.

Remaining trigger gaps are local variables, conditions, handlers, control
flow, multi-table DML firing, and complete `REPLACE` delete-event behavior.
`REPLACE` refuses when DELETE triggers exist instead of silently skipping
them. The full MySQL surface is documented under
[CREATE TRIGGER](https://dev.mysql.com/doc/refman/8.4/en/create-trigger.html).

## Check constraints

`CHECK` constraints follow MySQL 8.4's row semantics: an expression that is
true or unknown passes, while false returns error 3819. They are evaluated
after generated columns and before a row becomes visible for `INSERT`,
`INSERT ... SELECT`, `UPDATE`, `REPLACE`, and both branches of
`INSERT ... ON DUPLICATE KEY UPDATE`. A failing multi-row statement is atomic;
`INSERT IGNORE` skips only the violating candidates, and `UPDATE IGNORE`
leaves candidates that would violate a check unchanged.

Both column and table forms support explicit names, generated
`table_chk_N` names, and `[NOT] ENFORCED`. `ALTER TABLE` supports `ADD CHECK`,
`DROP CHECK`, and `ALTER CHECK ... [NOT] ENFORCED`; enabling a constraint
validates existing rows before changing its state. Names are unique within a
schema. Dropping a column removes its column-owned check, while a table check
that depends on the column blocks drop or rename with error 3959. The MySQL
restriction between checked columns and foreign-key `SET NULL`/`ON UPDATE`
referential actions is enforced at DDL time.

Definitions persist through the ordinary WAL and snapshot paths and appear in
`SHOW CREATE TABLE`, `information_schema.CHECK_CONSTRAINTS`, and
`information_schema.TABLE_CONSTRAINTS`, including enforcement state. The
expression validator rejects subqueries, aggregates, window functions,
nondeterministic functions, cross-table references, auto-increment references,
and DirectOnly or nondeterministic host extensions. Skipped `INSERT IGNORE`
rows and ignored CHECK violations appear in the session diagnostics area and
through `SHOW WARNINGS`; the OK/EOF warning count reports the same conditions.

## Server settings

The tunables an operator would plausibly change live in `Fsdb.Limits` and are
set from the standard server option files or an explicit `--defaults-file`:
`max_allowed_packet`, `max_connections`, `wait_timeout`,
`innodb_lock_wait_timeout`, `cte_max_recursion_depth`, plus fsdb's own WAL
rotation thresholds.

The option-file parser follows MySQL's format rather than a generic ini
dialect: `[mysqld]` and `[server]` groups, `name = value` and the bare-name
boolean form, `#`/`;` comments that may start mid-line, single- or
double-quoted values with `\n`/`\t`/`\r`/`\b`/`\s`/`\\` escapes, `-` and `_`
interchangeable in names, size suffixes (`64M`, `1G`), `loose-` to tolerate an
option fsdb doesn't have, and `!include`/`!includedir`. Reading the real format
is what lets `skip-name-resolve` be reported as an option fsdb lacks instead of
as a syntax error.

Groups other than `[mysqld]`/`[mysqld-8.4]`/`[server]` are skipped, so a shared
my.cnf is safe to point at. Within those groups an unrecognised option is a startup
error naming the file and line, matching mysqld, which also refuses to start on
an unknown option; `loose-` is the escape hatch for both. Every bad line is
reported, not just the first.

Two deliberate divergences from stock MySQL:

- **`wait_timeout` and `interactive_timeout` report 300, not 28800.** fsdb
  reaps a connection idle between commands after five minutes, because a
  half-open peer otherwise pins a socket and a task for eight hours. The
  number reported is the number enforced — a pool that sizes its idle-recycle
  from `wait_timeout` gets the truth instead of a connection the server closed
  hours earlier. Set `wait_timeout` in a defaults file to restore MySQL's
  value. `interactive_timeout` mirrors it because fsdb ignores
  `CLIENT_INTERACTIVE` at handshake.

`SET GLOBAL` updates the live limits used by later accepts, packet reads,
idle waits, transaction conflict waits, and recursive CTEs. Session-scoped
`wait_timeout`, `innodb_lock_wait_timeout`, and `cte_max_recursion_depth` are
honoured; process-wide `max_connections` and `max_allowed_packet` reject a
session-scoped `SET`.

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

Accounts select an exact peer address before CIDR/netmask, `localhost`
loopback, and `%`/`_` patterns; `CURRENT_USER()` reports the selected
account while `USER()` reports the handshake name and peer host. Accounts
without a host still default to `'%'`. Hostname accounts are not resolved:
the server accepts numeric peer addresses and the loopback `localhost`
alias, avoiding unauthenticated reverse-DNS identity claims.

Deliberate divergences (each marked `ponytail:` at its code site):
- Enforcement follows parsed statements through subqueries, derived tables,
  and CTEs. Text-probed account, process, database, and table metadata forms
  carry scoped checks; SET, USE, and server-wide SHOW forms remain outside
  the common privilege gate.
- No roles, dynamic privileges, column-level privileges, proxy users, or
  password expiry — the columns exist in the table shapes but stay at their
  defaults. Eight of MySQL's 38 `mysql.*` tables exist, including fsdb's
  row-backed `check_constraints`, `triggers`, and `views` catalogs.
- `SHOW GRANTS` omits root's dynamic-privilege and PROXY lines.
