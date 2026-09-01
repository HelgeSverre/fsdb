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

The fsdb-only durability lane kills a WAL-backed child server during
concurrent two-table commits and verifies acknowledged, ambiguous, atomic, and
snapshot-restart outcomes (`torture/scripts/run.sh durability --workers 16
--operations 500 --restarts 20`).

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

## Implemented surface

The implemented surface covers the wire protocol, including forward-only
prepared cursors and zlib compression, PDO/mysql-CLI compatibility, the SQL
engine core, Laravel
migrations, test-suite parity,
the embedding API, opt-in persistence, EXPLAIN, multi-table DML, and the
streaming pipeline. Each area has a runnable acceptance gate: a real external
client, a reference application suite, or a benchmark threshold.

## GUI clients and introspection

The introspection surface was built from what real clients actually send:
TablePlus 26.9.6's queries extracted verbatim from its binary, and
phpMyAdmin 5.2.x's query builders read from source. All 25
`information_schema` tables have column sets diffed byte-for-byte against a
live MySQL 8.4.11 (`SHOW COLUMNS` per table, both sides), and a ~70-query
replay fixture covering both clients' connect/browse/structure flows runs
with a single divergence: `SHOW SLAVE STATUS`, which real 8.4 also rejects
with 1064. Stored views, triggers, procedures, functions, their parameter
metadata, and events populate their object catalogs. PROCESSLIST,
`Threads_connected`, and `KILL` operate on the real connection registry.

## TLS transport

Supplying `--ssl-cert` and `--ssl-key` enables TLS 1.2 and 1.3 on the MySQL
listener. The same `ssl-cert`, `ssl-key`, `ssl-ca`, and
`require-secure-transport` options work in the server sections of an option
file. `ssl-ca` requests client certificates and validates them against every
CA certificate in the PEM file.
`--require-secure-transport` rejects plaintext handshakes with 3159.
Embedding hosts supply already-loaded `X509Certificate2` values through
`Db.withTlsCertificate` and `Db.withClientCertificateAuthority`;
`Db.requireSecureTransport` enables the same plaintext restriction. Accounts
created with `REQUIRE SSL` reject plaintext authentication. Accounts created
with `REQUIRE X509` additionally require a client certificate that chains to
a configured client CA and, when extended key usage is present, permits client
authentication.

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

The supported load subset is UTF-8/utf8mb4 input with string field/line
delimiters, optional single-character enclosure and escape markers, `REPLACE`
or `IGNORE`, header-line skipping, target columns or user variables, and
ordered `SET` transformations. Server-side `LOAD DATA INFILE` remains
unsupported.

## Views and triggers

MySQL views are stored queries, not persisted materialized results. `MERGE`
rewrites a referencing statement against the underlying tables, while
`TEMPTABLE` builds a temporary result for that statement; MySQL has no
materialized-view object. See the MySQL 8.4 documentation for
[view creation](https://dev.mysql.com/doc/refman/8.4/en/create-view.html) and
[view processing algorithms](https://dev.mysql.com/doc/refman/8.4/en/view-algorithms.html).

fsdb supports stored queries broadly and a narrow writable subset:

- `CREATE [OR REPLACE] [ALGORITHM=...] [DEFINER=...] [SQL SECURITY ...]
  VIEW`, `ALTER VIEW`, and `DROP VIEW [IF EXISTS]`.
- Nested views, joins, unions, CTEs, grouping, windows, and the rest of the
  supported SELECT grammar inside a definition.
- Direct single-table projections with a static predicate use MERGE-like
  evaluation, allowing the base table's indexes and streaming `LIMIT` path to
  serve reads. Other shapes use one typed TEMPTABLE-like result per statement.
  A later statement always reevaluates the definition against current base
  rows.
- Definitions execute under the recorded creator's privileges; revoking the
  definer's access to an underlying table makes later reads fail.
- Persistence through the WAL and snapshots, plus `SHOW [FULL] TABLES`,
  `SHOW CREATE VIEW`, `information_schema.TABLES`, and
  `information_schema.VIEWS` metadata.
- Projection metadata in `information_schema.COLUMNS`, `DESCRIBE`, `SHOW
  COLUMNS`, and `SHOW TABLE STATUS`. This path reads the stored definition
  without running it, so empty and nondeterministic views have the same
  metadata shape as populated views.
- Numeric display widths and `ZEROFILL` survive direct view projections;
  computed expressions and unions discard them as MySQL does.

Single-table views and nested views over that shape accept `UPDATE` and
`DELETE`, including predicates over computed projections. Direct physical
inner-join views accept `UPDATE` against one component table and `INSERT` with
an explicit column list against one insertable component. Outer joins,
join-view `DELETE`/`REPLACE`, and one statement that writes multiple component
tables are refused with MySQL's corresponding errors. Only direct column
projections are assignable; computed columns return 1348. A component is
insertable only when every selected projection for that component is direct,
no base column is repeated, and every required base column is exposed.
Uncorrelated scalar subqueries in the projection keep direct columns
updatable but make the view noninsertable; a dependent projection is refused
for writes. Scalar and correlated subqueries in a view predicate remain part
of the lowered base-table write.
Mergeable component views and a simple outer view layer retain the join's
writable targets, predicates, and per-component checks when their stored
security identity is unchanged. A nested component with a different definer
or security mode remains a read-only source in the outer view.
For UPDATE, an aggregate or UNION component may remain materialized and
read-only while another component supplies the writable columns. INSERT
through a join still requires every component to be mergeable.
Single-table insertable views additionally accept `INSERT`, `INSERT ...
SELECT`, `REPLACE` in each supported source form, and `ON DUPLICATE KEY
UPDATE`. Every written or referenced column must be exposed, and the privilege
identity is checked at every nested view boundary. `LOCAL` and `CASCADED`
check predicates compose through nested views and are reported by `SHOW CREATE
VIEW` and `information_schema.VIEWS`. `SQL SECURITY DEFINER` and `SQL SECURITY
INVOKER` use their respective privilege identities. Explicit definers require
the selected account or SUPER authority; missing definers are retained with a
1449 note and fail closed when executed. ALTER preserves omitted declaration
options. `TEMPTABLE` views are non-updatable; `MERGE` and `UNDEFINED` remain
planner hints over fsdb's shape-driven execution, and incompatible `MERGE`
definitions become `UNDEFINED` with warning 1354. Creation validates
the saved SQL grammar but defers missing dependency and output-shape errors
until the first read; `SELECT *` follows the base table's current columns
instead of freezing them at creation.

Trigger execution has stronger behavioral coverage than its syntax breadth:

- Ordered `BEFORE` and `AFTER` triggers run for `INSERT`, `UPDATE`, and
  `DELETE`, including every physical target of multi-table UPDATE/DELETE.
  `FOLLOWS` and `PRECEDES` determine order within a timing/event slot.
- Bodies accept one statement or a `BEGIN ... END` sequence of `INSERT`,
  `REPLACE`, `UPDATE`, `DELETE`, local `DECLARE`/`SET`, nested
  `IF`/`ELSEIF`/`ELSE`, `CALL`, and `SET NEW` statements. Local assignments
  may read a scalar subquery. Procedure calls support nested calls and typed
  local or user-variable `OUT`/`INOUT` targets.
- `OLD.column` and `NEW.column` bind the applicable row images. A `BEFORE`
  trigger may assign `NEW.column`; generated columns cannot be referenced.
- Multi-row statements fire once per affected row. Ignored candidates do not
  fire, and the update branch of `ON DUPLICATE KEY UPDATE` uses update
  triggers.
- The source write and every trigger effect are one atomic statement. A body
  error rolls all of them back, and effects participate normally in explicit
  transaction commit or rollback.
- Trigger and called-procedure writes may fire another table's trigger. Their
  dependencies enter the invoking statement's lock plan. Cycles and writes to
  any target or joined table of the invoking statement return error 1442;
  long acyclic chains continue normally.
- Procedures cannot return result sets from a trigger, and dynamic SQL in a
  trigger call chain is rejected with the corresponding MySQL errors. Either
  failure rolls back the source row and all preceding body effects.
- Bodies run after a definer-privilege check, reject DirectOnly extension
  functions, persist through the ordinary WAL/snapshot path, and appear in
  `SHOW TRIGGERS` and `information_schema.TRIGGERS`.
- A trigger follows its subject through `RENAME TABLE`; dropping the subject
  table or its database removes the stored trigger definition.

`REPLACE` fires BEFORE INSERT, each conflicting row's DELETE pair, and AFTER
INSERT in row order; every phase rolls back together on failure. Compound
bodies support CASE and labeled loops, scoped conditions, read-only cursors,
handlers,
SIGNAL/RESIGNAL, and GET CURRENT/STACKED DIAGNOSTICS. The full MySQL surface is
documented under
[CREATE TRIGGER](https://dev.mysql.com/doc/refman/8.4/en/create-trigger.html).

## HASH partitioning

`PARTITION BY HASH` and `PARTITION BY LINEAR HASH` retain their expression and
partition count. Tables expose MySQL-style `p0`…`pN` names through
`information_schema.PARTITIONS`, `SHOW CREATE TABLE`, and `PARTITION (...)`
selection. `ALTER TABLE ... ADD PARTITION PARTITIONS n` and `COALESCE
PARTITION n` update the logical map and redistribute subsequent selections.

All rows still share one immutable row store. Partition selection evaluates
the hash expression while scanning; it does not provide MySQL's physical
partition pruning or separate storage. `DROP` and `REORGANIZE PARTITION`
remain unsupported.

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
`max_allowed_packet`, `max_connections`, `wait_timeout`, `net_read_timeout`,
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
`wait_timeout`, `net_read_timeout`, `innodb_lock_wait_timeout`, and
`cte_max_recursion_depth` are honoured; process-wide `max_connections` and
`max_allowed_packet` reject a session-scoped `SET`. An idle command wait uses
`wait_timeout`; after the first packet byte arrives, every pause in ordinary,
TLS, compressed, and LOCAL INFILE traffic uses `net_read_timeout`.

## Users, authentication, and privileges

fsdb has a real account system backed by a stored `mysql` schema (`user`,
`db`, `tables_priv`, `columns_priv`, `global_grants` — MySQL 8.4 column
shapes, oracle-verified). `CREATE USER` / `DROP USER` / `ALTER USER` /
`SET PASSWORD` / `GRANT` / `REVOKE` persist through the ordinary WAL/snapshot
path; passwords are mysql_native_password hashes verified at the handshake
(with an AuthSwitchRequest for clients that answer with caching_sha2 first);
account locks, TLS requirements, explicit and global-default password lifetimes, the
expired-password reset sandbox, and per-hour/per-connection resource limits
are enforced;
statements are privilege-checked at global, database, table, and column scope with
MySQL's 1045/1142/1044/1227 error shapes. `SHOW GRANTS [FOR user]`,
`SHOW PRIVILEGES` (8.4's 73 rows), `information_schema.USER_PRIVILEGES`, and
`FLUSH PRIVILEGES` (no-op OK) are served; `DROP DATABASE mysql` is 3552.
MySQL 8.4's registered dynamic global privileges are stored in
`mysql.global_grants`, retain their individual grant options, appear in both
metadata surfaces, and participate in authorization. Static `ALL PRIVILEGES`
does not imply them.
Roles use `mysql.role_edges` and `mysql.default_roles`; grants, admin option,
transitive inheritance, default activation during authentication, session
`SET ROLE`, global mandatory roles, `activate_all_roles_on_login`, role-aware
metadata visibility, and `SHOW GRANTS ... USING` are enforced through the same
authorization path as direct account privileges. Mandatory roles are applicable
to every account but, as in MySQL, remain inactive after `SET ROLE NONE`.

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
- Proxy users, auth-plugin selection, password history/reuse/current policy,
  and a mutable global
  default password lifetime are absent. Thirteen of MySQL's roughly 38
  `mysql.*` tables exist, including fsdb's row-backed role/default-role,
  constraint, trigger, view, routine, function, and event catalogs.
- `SHOW GRANTS` omits PROXY lines.
