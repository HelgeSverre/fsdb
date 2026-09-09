# Compatibility

How fsdb validates MySQL compatibility and where the implemented behavior has
important operational detail. This is not a blanket compatibility claim:
[GAPS.md](../GAPS.md) remains the authoritative ledger of open differences.

## Contents

- [Validation method](#validation-method)
- [Application gauntlet](#application-gauntlet)
- [Implemented surface](#implemented-surface)
- [GUI clients and introspection](#gui-clients-and-introspection)
- [TLS transport](#tls-transport)
- [Bulk wire commands](#bulk-wire-commands)
- [Temporal values, zones, and offsets](#temporal-values-zones-and-offsets)
- [Schema moves](#schema-moves)
- [Views and triggers](#views-and-triggers)
  - [Writable views](#writable-views)
  - [Triggers](#triggers)
- [Stored routines and events](#stored-routines-and-events)
- [ALTER execution options](#alter-execution-options)
- [HASH partitioning](#hash-partitioning)
- [Check constraints](#check-constraints)
- [Server settings](#server-settings)
- [Users, authentication, and privileges](#users-authentication-and-privileges)
  - [Catalogs and accounts](#catalogs-and-accounts)
  - [Authentication](#authentication)
  - [Grants, proxy grants, and roles](#grants-proxy-grants-and-roles)
  - [Host matching](#host-matching)
  - [Text-probed statements](#text-probed-statements)
  - [Deliberate limits](#deliberate-limits)

## Validation method

Compatibility evidence comes from focused MySQL-oracle regressions, the
differential and failure-injection harness, private Laravel application suites,
and pinned upstream applications. When another backend or an application's own
assumption differs, MySQL 8.4 decides the expected behavior.

`torture/` supplies the differential and failure-injection layers. Each lane
has a distinct contract:

| Lane | What it establishes |
|---|---|
| Differential suite | Generated SQL produces the same typed results, affected rows, and final state on fsdb and MySQL 8.4. |
| Syntax mutation | Valid feature statements and bounded token/comment mutations agree on acceptance, error code, and SQLSTATE. |
| Transaction concurrency | Prepared transactions preserve balances, ledger identity, rollback, and lock reuse under contention and disconnects. |
| Multi-database concurrency | Independent databases do not leak state and publish without an unnecessary catalog-wide bottleneck. |
| Durability | Acknowledged commits survive forced crashes; transactions remain atomic across WAL tails and snapshot rotation. |

Every divergence produces a replayable artifact. Exit code 0 means parity or
only hand-reviewed known gaps; 2 means a new fsdb finding. Exact commands and
artifact formats live in the [torture harness guide](../torture/README.md).

The ordered DML battery covers every supported `REPLACE` source form in both
client affected-row modes. It includes unchanged replacements, conflicts
across unique keys, same-statement key reuse, defaults, source ordering,
composite indexes, checked views, and ordered trigger side effects.

For `INSERT ... SELECT ... ON DUPLICATE KEY UPDATE`, assignments may read
qualified columns from direct, derived, and joined SELECT sources, including
source columns omitted from the inserted projection and qualified correlations
inside assignment subqueries. `VALUES(column)` remains available for the
candidate value.

The scalar-expression battery covers numeric, string, network, temporal, JSON,
row-value, subquery, and aggregate edge behavior. Spatial comparisons use
topology rather than WKT vertex order and exercise relations, overlays, signed
default buffers, and independently configured point, join, and end strategies.

Planner regressions pair each accelerated predicate with a scan-only twin and
check the selected access through `EXPLAIN`. Literal values and the supported
row-independent numeric expressions are exercised across equality, `IN`,
range, ordering, and mutation paths. Overridden host functions must remain on
the ordinary execution path and are never invoked while a plan is selected.

The parser accepts MySQL's `INSERT ... SET`, singular `VALUE` and optional
`ROW` constructors, substring-based `TRIM` modes, `ALL`/`DISTINCTROW`, and
the optimizer-only SELECT modifiers without changing query results.

## Application gauntlet

Private Laravel suites exercise migrations and application behavior without
database-specific patches:

| Application | Laravel major | Oracle result |
|---|---|---|
| App A | 11 | full parity |
| App B | 11 | residual failures reproduce identically on real MySQL (app-side factory/collation bugs) |
| App C | 10 | behavioral equivalence with real MySQL (identical failure set from an app-side factory bug) |
| App D | 13 | residual failure is a sqlite-only PRAGMA introspection test that fails identically on real MySQL |
| App E | 13 | full backend-facing suite; residual failures are app-side or real-MySQL-identical, with one documented order divergence on an unordered query |

The applications are private codebases, identified here only by framework
version.

The public [application smoke suite](../smoke/README.md) adds pinned Gitea,
MediaWiki, Drupal, Nextcloud, Shopware, Ghost, Moodle, WordPress, Rails, and
Magento probes across Go, PHP, Node.js, and Ruby client stacks. Failures are
classified against the same pinned project on MySQL 8.4 before they become fsdb
compatibility findings.

## Implemented surface

The implemented surface includes:

- the MySQL wire protocol, forward-only prepared cursors, and policy-controlled
  zlib or Zstandard compression;
- mysql CLI, PDO/mysqli, MySqlConnector, Doctrine DBAL, mysql2, and Active
  Record compatibility through focused or application-level probes;
- the SQL engine, Laravel migrations, multi-table DML, and `EXPLAIN`;
- the embedding API, opt-in persistence, and lazy result streaming.

Evidence comes from external clients, reference application suites, focused
MySQL-oracle regressions, the differential harness, and performance profiles.
No single lane is treated as proof of complete MySQL compatibility.

## GUI clients and introspection

The introspection surface follows queries sent by real clients. Fixtures cover
TablePlus connect, browse, and structure flows and phpMyAdmin query builders.
Supported `information_schema` descriptors are pinned against MySQL 8.4.

The fixture also includes `SHOW SLAVE STATUS`, which MySQL 8.4 rejects with
1064; accepting a familiar but removed statement would be a compatibility bug.

Stored views, triggers, procedures, functions, parameters, and events populate
their corresponding catalogs. `PROCESSLIST`, `Threads_connected`, and `KILL`
operate on the live connection registry.

`information_schema.INNODB_TRX` exposes active seeded transactions, including
their isolation mode, logical write weight, and held row stripes. InnoDB-only
lock-memory and scheduling fields remain zero or NULL.

## TLS transport

Supplying `--ssl-cert` and `--ssl-key` enables TLS 1.2 and 1.3 on the MySQL
listener. The same `ssl-cert`, `ssl-key`, `ssl-ca`, and
`require-secure-transport` options work in the server sections of an option
file. `ssl-ca` requests client certificates and validates them against every
CA certificate in the PEM file.

Plaintext full authentication uses a process-local RSA key unless matching
`caching-sha2-password-private-key-path` and
`caching-sha2-password-public-key-path` settings are supplied. The equivalent
`sha256-password-*` pair configures the deprecated SHA-256 plugin. Embedding
hosts can provide either private key through `Db.withAuthenticationRsaKey`;
fsdb derives and serves its public half. `SHOW STATUS` exposes the active keys
as `Caching_sha2_password_rsa_public_key` and `Rsa_public_key`.

`--require-secure-transport` rejects plaintext handshakes with 3159. Embedding
hosts supply already-loaded `X509Certificate2` values through
`Db.withTlsCertificate` and `Db.withClientCertificateAuthority`, while
`Db.requireSecureTransport` enables the same plaintext restriction.

Accounts created with `REQUIRE SSL` reject plaintext authentication. Accounts
created with `REQUIRE X509` also require a client certificate that chains to a
configured client CA. When extended key usage is present, it must permit client
authentication.

Account-specific transport attributes use the same `CREATE USER` and `ALTER
USER` grammar as MySQL:

```sql
CREATE USER 'service'@'10.%'
  REQUIRE SUBJECT '/CN=service-client'
          ISSUER '/CN=application-ca'
          CIPHER 'TLS_AES_256_GCM_SHA384';
```

`SUBJECT`, `ISSUER`, and `CIPHER` can appear in any order, with an optional
`AND` between attributes. Repeating an attribute is rejected. Subject and
issuer policies require a client certificate that first passes CA validation;
their slash-form names and the negotiated cipher are then compared exactly.
The same values persist in `mysql.user` and appear in `SHOW CREATE USER`.
`COM_CHANGE_USER` retains the established connection's TLS identity when it
authenticates the replacement account.

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

The supported load subset accepts utf8mb4, utf8mb3/utf8, latin1 (cp1252),
and ASCII input with string field/line delimiters, optional single-character
enclosure and escape markers, `REPLACE` or `IGNORE`, header-line skipping,
target columns or user variables, and ordered `SET` transformations.
Server-side `LOAD DATA INFILE` remains unsupported.

## Temporal values, zones, and offsets

The session `time_zone` accepts MySQL's numeric offsets and `SYSTEM`. It drives
current-time functions, Unix timestamp conversion, TIMESTAMP storage, and
result rendering. Named zones depend on MySQL's optional time-zone tables and
are not loaded by fsdb.

DATETIME and TIMESTAMP strings may carry a numeric offset such as
`2024-01-01 10:10:10.123456+05:30`. Both are converted into the active session
zone on input. DATETIME retains that converted wall time; TIMESTAMP retains the
corresponding UTC instant and follows later session-zone changes. The same
conversion path handles typed temporal literals, casts, scalar functions,
ordinary writes, and prepared string parameters.

Offsets use MySQL's `-13:59` through `+14:00` range. The `-00:00` spelling,
named suffixes, malformed widths, and out-of-range offsets are rejected or
coerced according to the active SQL mode. Zero date parts remain invalid when
an explicit offset is present.

`ALLOW_INVALID_DATES` relaxes full calendar checking for DATE and DATETIME.
Values such as `2023-02-31` retain their written fields while month and day
must still be within 1–12 and 1–31. The rule also applies to defaults, casts,
typed temporal literals, ordinary writes, and prepared string parameters.
`NO_ZERO_DATE` and `NO_ZERO_IN_DATE` remain separate policies, and TIMESTAMP
always requires a valid calendar date.

## Schema moves

`RENAME TABLE` evaluates its pairs from left to right and publishes the whole
statement atomically. This permits swaps through an intermediate name and
ensures a later missing source or occupied target leaves every earlier object
unchanged. `ALTER TABLE ... RENAME` uses the same move path and may combine the
rename with other supported alterations.

Base tables may move between databases. Rows, AUTO_INCREMENT state, generated
check and foreign-key names, outgoing references, and incoming references move
with the table. The source requires `ALTER` and `DROP`; the destination requires
`CREATE` and `INSERT`. Grants remain attached to their original object names,
matching MySQL.

Views may be renamed within their database. MySQL rejects a cross-database view
move, or a cross-database move of a table that has triggers; fsdb returns the
same errors rather than detaching those stored objects from their schema.
See MySQL 8.4's [`RENAME TABLE` reference](https://dev.mysql.com/doc/refman/8.4/en/rename-table.html)
for the corresponding object, privilege, and metadata-lock rules.

## Views and triggers

MySQL views are stored queries, not persisted materialized results. `MERGE`
rewrites a referencing statement against the underlying tables, while
`TEMPTABLE` builds a temporary result for that statement; MySQL has no
materialized-view object. See the MySQL 8.4 documentation for
[view creation](https://dev.mysql.com/doc/refman/8.4/en/create-view.html) and
[view processing algorithms](https://dev.mysql.com/doc/refman/8.4/en/view-algorithms.html).

fsdb supports stored queries broadly and a shape-checked writable surface:

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

### Writable views

Single-table views and nested views over that shape accept `UPDATE` and
`DELETE`, including predicates over computed projections. A view's `ORDER BY`
guides limited updates and deletes, survives direct nesting, and yields to an
explicit outer `ORDER BY`.

Direct physical inner-join views accept `UPDATE` against one component table
and `INSERT` with an explicit column list against one insertable component.
Outer joins, join-view `DELETE`/`REPLACE`, and one statement that writes
multiple component tables are refused with MySQL's corresponding errors.

Only direct column projections are assignable; computed columns return 1348.
A component is insertable only when each selected projection for that
component is direct, no base column is repeated, and every required base column
is exposed.

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
INVOKER` use their respective privilege identities.

Explicit definers require the selected account or `SUPER` authority. Missing
definers are retained with a 1449 note and fail closed when executed. `ALTER`
preserves omitted declaration options.

`TEMPTABLE` views are non-updatable. `MERGE` and `UNDEFINED` remain planner
hints over fsdb's shape-driven execution; incompatible `MERGE` definitions
become `UNDEFINED` with warning 1354. Creation validates the saved SQL grammar
but defers missing dependency and output-shape errors until the first read.
`SELECT *` follows the base table's current columns instead of freezing them at
creation.

### Triggers

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
handlers, `SIGNAL`/`RESIGNAL`, and `GET CURRENT/STACKED DIAGNOSTICS`.

The full MySQL surface is documented under
[CREATE TRIGGER](https://dev.mysql.com/doc/refman/8.4/en/create-trigger.html).

## Stored routines and events

Stored procedures support typed `IN`, `OUT`, and `INOUT` parameters; local
variables; nested branches and loops; scoped condition declarations;
`CONTINUE` and `EXIT` handlers; `SIGNAL`, `RESIGNAL`, and diagnostics; cursors;
routine variables in expressions and `LIMIT`; and multiple result sets.

Procedure recursion follows the GLOBAL and SESSION
`max_sp_recursion_depth` setting, including MySQL's per-routine counting for
mutual recursion.

Stored functions support typed parameters and return coercion, compound
control flow, handlers, cursors, subqueries, typed local `SELECT ... INTO`,
nested routine calls, prepared execution, and metadata. MySQL's recursion
refusal and creation-time SQL mode, charset, and collation behavior are
retained.

Data-changing routine bodies share the invoking statement's transaction.
Failed statements discard their effects, and error 1442 protects tables read
or written by the invoking statement. Metadata probes do not invoke function
bodies, while function writes during `CREATE TABLE ... AS SELECT` return 1746.

One-time and recurring events retain schedules, status, body, name, definer,
metadata, persistence, and definer-context execution. Routine, execute, and
event privileges guard the corresponding operations.

## ALTER execution options

`ALTER TABLE` retains repeated `ALGORITHM` and `LOCK` clauses with MySQL's
last-option-wins rule. Unsupported algorithms and incompatible lock requests
fail before any schema change. The compatibility matrix covers ordinary and
generated columns, indexes, checks, foreign keys, primary-key replacement,
table options, and character-set conversion.

The in-memory engine publishes every accepted schema change as one immutable
database-root replacement, so InnoDB's physical COPY/INPLACE/INSTANT lock
duration does not apply.

## HASH partitioning

`PARTITION BY HASH` and `PARTITION BY LINEAR HASH` retain their expression and
partition count. Tables expose MySQL-style `p0`…`pN` names through
`information_schema.PARTITIONS`, `SHOW CREATE TABLE`, and `PARTITION (...)`
selection. `ALTER TABLE ... ADD PARTITION PARTITIONS n` and `COALESCE
PARTITION n` update the logical map and redistribute subsequent selections.

All rows still share one immutable row store. Partition selection evaluates
the hash expression while scanning; it does not provide MySQL's physical
partition pruning or separate storage. `ANALYZE`, `CHECK`, `OPTIMIZE`, and
`REPAIR PARTITION` validate partition names and report MySQL-compatible status
rows. `TRUNCATE PARTITION` removes rows from named partitions without firing
DELETE triggers and preserves the table's AUTO_INCREMENT counter.

`DROP PARTITION` returns MySQL's HASH-specific 1512 refusal. Partition renaming
through `REORGANIZE PARTITION` remains unsupported.

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
schema.

Dropping a column removes its column-owned check, while a table check that
depends on the column blocks drop or rename with error 3959. The MySQL
restriction between checked columns and foreign-key `SET NULL`/`ON UPDATE`
referential actions is enforced at DDL time.

Definitions persist through the ordinary WAL and snapshot paths and appear in
`SHOW CREATE TABLE`, `information_schema.CHECK_CONSTRAINTS`, and
`information_schema.TABLE_CONSTRAINTS`, including enforcement state. The
expression validator rejects subqueries, aggregates, window functions,
nondeterministic functions, cross-table references, auto-increment references,
and DirectOnly or nondeterministic host extensions.

Skipped `INSERT IGNORE` rows and ignored CHECK violations appear in the
session diagnostics area and through `SHOW WARNINGS`; the OK/EOF warning count
reports the same conditions.

## Server settings

The process-wide numeric and boolean knobs live in `Fsdb.Limits`. Standard
server option files and an explicit `--defaults-file` share the same parser and
validation path:

- connection and protocol limits: `max_allowed_packet`, `max_connections`,
  `max_prepared_stmt_count`, `local_infile`, and `max_load_data_bytes`;
- timeouts: `connect_timeout`, `wait_timeout`, `interactive_timeout`,
  `net_read_timeout`, `net_write_timeout`, and
  `innodb_lock_wait_timeout`;
- execution and account defaults: `cte_max_recursion_depth`,
  `default_password_lifetime`, `password_history`,
  `password_reuse_interval`, `password_require_current`,
  `default_week_format`, and `max_points_in_geometry`;
- durability controls: `wal_rotate_bytes`, `wal_rotate_entries`, and
  `wal_group_commit_queue_capacity`.

`max_load_data_bytes` and the `wal_*` controls are fsdb-only configuration;
they do not appear as invented MySQL system variables.

The option-file parser follows MySQL's format rather than a generic INI
dialect. It accepts:

- `[mysqld]`, `[mysqld-8.4]`, and `[server]` groups;
- `name = value` and bare-name booleans;
- mid-line `#` and `;` comments;
- single- or double-quoted values with `\n`, `\t`, `\r`, `\b`, `\s`, and `\\`
  escapes;
- interchangeable `-` and `_` in names, plus size suffixes such as `64M`;
- `loose-`, `!include`, and `!includedir`.

Reading the real format lets `skip-name-resolve` be reported as an option fsdb
lacks instead of as a syntax error.

Groups other than `[mysqld]`, `[mysqld-8.4]`, and `[server]` are skipped, so a
shared `my.cnf` is safe to use. Within those groups, an unrecognised option is a
startup error naming the file and line. `loose-` is the escape hatch for options
that fsdb does not implement. Every bad line is reported, not only the first.

`wait_timeout` and `interactive_timeout` default to MySQL's 28800 seconds and
remain independently configurable. A client that negotiates
`CLIENT_INTERACTIVE` inherits `interactive_timeout`; other clients inherit
`wait_timeout`.

`connect_timeout` bounds greeting, TLS, and authentication exchanges before an
account exists. `net_read_timeout` governs a stalled packet after its first
byte, while `net_write_timeout` bounds a client that stops reading server
output.

`SET GLOBAL` updates the live limits used by later accepts, packet reads,
transaction conflict waits, and recursive CTEs. Session-scoped
`wait_timeout`, `net_read_timeout`, `innodb_lock_wait_timeout`, and
`cte_max_recursion_depth` are honoured. Process-wide `max_connections` and
`max_allowed_packet` reject a session-scoped `SET`.

`max_points_in_geometry` is GLOBAL and SESSION scoped. It bounds newly created
buffer strategies without invalidating values created under an earlier limit.
The `/*+ SET_VAR(max_points_in_geometry=...) */` hint applies the same bound to
one statement, including prepared execution, without changing the session
value.

An idle connection uses `wait_timeout`. Once the first packet byte arrives,
every pause in ordinary, TLS, compressed, and LOCAL INFILE traffic uses
`net_read_timeout`.

`COMMIT` and `ROLLBACK` honor `AND [NO] CHAIN`, `[NO] RELEASE`, and the
session or global `completion_type` default. `RELEASE` sends the command reply
before closing the connection.

## Users, authentication, and privileges

### Catalogs and accounts

fsdb has a real account system backed by the native MySQL 8.4 catalog schemas
plus fsdb's stored-object catalogs. Native tables use their MySQL column order,
types, nullability, key membership, defaults, and generated columns.

The optimizer cost tables include MySQL's bootstrap rows and remain writable,
although fsdb's planner does not consume their overrides. Group-action
configuration rows are present without replication execution. Engine-maintained
help, log, statistics, GTID, NDB, and replication-channel data still differ or
remain empty.

`CREATE USER`, `DROP USER`, `ALTER USER`, `SET PASSWORD`, `GRANT`, and `REVOKE`
persist through the WAL and snapshot path. Account rows retain their plugin,
credential hash, policy, grants, roles, and transport requirements.

### Authentication

New accounts default to `caching_sha2_password`. Its fast-auth cache is scoped
to the server process; a cache miss uses the full TLS or RSA-protected password
exchange and repopulates the cache after successful verification.

`sha256_password` always uses the full exchange. On plaintext connections,
clients request the matching public key and send an RSA-OAEP-SHA1 encrypted
password. The server can load persistent PEM pairs for either SHA-2 plugin, or
derive a process-local public key from a generated private key. `SHOW STATUS`
reports the active public keys.

`mysql_native_password` remains available for older clients. It does not share
the SHA-2 full-auth or key configuration paths.

Account locks, TLS requirements, password lifetimes, JSON attributes and
comments, the expired-password reset sandbox, and resource limits are
enforced.

Password history, day-based reuse intervals, and current-password rules use
the native nullable `mysql.user` policy fields. `DEFAULT` values inherit the
live `password_history`, `password_reuse_interval`, and
`password_require_current` globals. Retained hashes live in
`mysql.password_history` and follow account rename, drop, WAL, and snapshot
lifecycle.

Self-service `ALTER USER` and `SET PASSWORD` accept MySQL's `REPLACE` clause.
Missing and incorrect current passwords report 3892 and 3891; administrators
changing another account are exempt and may not supply `REPLACE`.

Statements are checked at global, database, table, and column scope with
MySQL's 1045, 1142, 1044, and 1227 error shapes.

`SHOW GRANTS [FOR user]`, `SHOW PRIVILEGES`,
`information_schema.USER_PRIVILEGES`, and the no-op `FLUSH PRIVILEGES` are
served. `DROP DATABASE mysql` returns 3552.

### Grants, proxy grants, and roles

MySQL 8.4's registered dynamic global privileges are stored in
`mysql.global_grants`, retain their individual grant options, appear in both
metadata surfaces, and participate in authorization. Static `ALL PRIVILEGES`
does not imply them.

`GRANT/REVOKE PROXY` persist relationships in `mysql.proxies_priv`, enforce
target-specific delegation, follow grantee rename/drop lifecycle, and appear
in `SHOW GRANTS`. The built-in authentication plugins do not select an
alternate proxied identity.

`CREATE SERVER`, `ALTER SERVER`, and `DROP SERVER` persist foreign-server
definitions in `mysql.servers` and require `SUPER`, matching MySQL 8.4.

Roles use `mysql.role_edges` and `mysql.default_roles`; grants, admin option,
transitive inheritance, default activation during authentication, session
`SET ROLE`, global mandatory roles, `activate_all_roles_on_login`, role-aware
metadata visibility, and `SHOW GRANTS ... USING` are enforced through the same
authorization path as direct account privileges. Mandatory roles are applicable
to every account but, as in MySQL, remain inactive after `SET ROLE NONE`.

### Host matching

Accounts select an exact peer address before CIDR/netmask, `localhost`
loopback, and `%`/`_` patterns; `CURRENT_USER()` reports the selected
account while `USER()` reports the handshake name and peer host. Accounts
without a host still default to `'%'`. Hostname accounts are not resolved:
the server accepts numeric peer addresses and the loopback `localhost`
alias, avoiding unauthenticated reverse-DNS identity claims.

### Text-probed statements

Text-probed forms carry their own privilege checks because they do not pass
through the parsed-statement authorization gate. This includes global and
scoped SET, USE and protocol database selection, SHOW metadata and server
status, table maintenance, FLUSH, KILL, and explicit table locks.

### Deliberate limits

The complete ledger lives in [GAPS.md](../GAPS.md). Deliberate authentication
and catalog limits include:

- Pluggable authentication and proxy identity selection are absent.
- Every MySQL `mysql.*` table schema is exposed, but engine-owned help, log,
  GTID, statistics, NDB, and replication-channel rows remain empty unless
  ordinary fsdb DML populates them.
