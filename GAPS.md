# MySQL 8.4 feature gaps

A map of where fsdb diverges from or lacks MySQL 8.4 functionality. Oracle for
every row is real MySQL 8.4, never sqlite. Evidence comes from source review,
compatibility and torture records, benchmark artifacts, and adversarial parser,
wire, privilege, logging, and persistence tests.

Evidence anchors name files and definitions instead of line numbers so routine
refactors do not silently make them misleading.

This is an open ledger: remove resolved rows, and narrow partially resolved
rows to only the behavior that still differs in the same commit as the fix.

## How to read this document

Impact grades measure disruption to the primary consumers: Laravel/PDO
applications, the `mysql` CLI, and `mysqldump` restore paths.

- **high** — breaks or silently corrupts common client workflows.
- **medium** — feature missing or divergent; a workaround usually exists.
- **low** — rarely exercised surface; parity nicety.

fsdb's stated policy is to refuse rather than answer wrongly. Refusals are
still gaps (the statement does not work), but they are safer than silent
divergences; rows marked *refusal* fail loudly, rows marked *divergence*
behave differently from MySQL without erroring.

The torture ledger `torture/support/known-gaps.json` is hand-reviewed. This
document also covers deliberate divergences marked `ponytail:` in source and
findings recorded under `torture/findings/`.

## Summary by area

| Area | State | Largest single gap |
|---|---|---|
| SQL statements | Broad core; large admin/programmatic tail missing | Replication and admin SQL |
| Query execution | Composite equality/range access, index ordering, restricted join reordering, and stable/correlated index probes | General cost-based planning and broader correlated forms |
| Built-in functions | Broad scalar, aggregate, JSON, time, and common planar geometry coverage | Overlays, non-point buffers, and geographic SRS semantics |
| Data types | Common scalar types, BIT fields, signed TIME durations, and OGC geometry with planar MBR indexing | Binary JSON representation |
| Constraints & indexes | PK/UNIQUE/FK/CHECK plus composite equality, inner/left/right joins, PK/unique/secondary/spatial range, grouping, and index-order probes | Arbitrary expression ordering and broader grouping paths still scan |
| Charsets & collations | ICU-based utf8mb4 registry | Weight-table tailoring differs from MySQL's UCA tables |
| Transactions | Dirty-read, read-committed, repeatable-read, and conservatively validated serializable views with optimistic row-version merge | Remaining coarse write shapes |
| Persistence | WAL + snapshot, crash-tested, with bounded group commit | Opt-in only; row tombstones are reclaimed during bounded foreground compaction rather than by a background purge worker |
| Views & triggers | Single-table, nested, and direct physical inner-join updatable views; ordered BEFORE/AFTER INSERT/UPDATE/DELETE triggers across single- and multi-table DML, with compound condition-handling bodies and procedure calls | Complex updatable views |
| Routines & events | Typed procedures with configurable recursion, trigger-invoked procedure calls, data-changing stored functions, and persisted definer-context event scheduling | No material gap recorded |
| Full-text | Oracle-verified scoring over maintained inverted indexes | CJK parsing and remaining plan combinations |
| Wire protocol | Handshake through COM_STMT_FETCH, mutual TLS, zlib/Zstandard compression, LOCAL INFILE, multi-result batches, and transaction-aware session-state tracking | No GTID state tracker or live TLS certificate reload |
| Auth & privileges | Static, dynamic, column, role, and proxy grants; per-host accounts; expiry sandboxes; resource caps; account locks; mandatory/default/session roles; inherited authorization | Auth plugins cannot select a proxied identity |
| Metadata | Broad INFORMATION_SCHEMA coverage, every MySQL 8.4 `mysql.*` table schema, fsdb catalogs, active transaction metadata, and the complete keyword and `Com_*` registries | Engine-maintained physical contents remain absent |
| Server admin | KILL, SHUTDOWN, limits, config file parsing | No replication/binlog/logging files |

## 1. SQL statements and parser

The SQL core supports full DML, including `INSERT`/`REPLACE ... SET`, ODKU,
`IGNORE`, and multi-table forms. `SELECT` covers joins, derived and lateral
sources, `JSON_TABLE`, expression subqueries, set operations, windows, rollups,
and ordinary or recursive query-scoped CTEs. CTEs can lead UPDATE or DELETE and
appear within set-operation branches.

DDL covers databases, tables, indexes, views, triggers, users, grants,
`CREATE TABLE ... AS SELECT`, temporary tables, `TRUNCATE`, and `RENAME TABLE`.
`EXPLAIN` supports traditional, JSON, and ANALYZE forms. Transaction control,
`SET`, `SHOW`, `USE`, `KILL`, and `DESCRIBE` are text-probed before the grammar
by `QueryHandler.dispatch`.

XA supports start, end, prepare, one- and two-phase commit, rollback, detached
recovery, binary transaction identifiers, and durable prepared branches.

`LOCK TABLES` enforces READ/WRITE ownership, aliases, atomic lock lists,
temporary-table exemptions, transaction boundaries, and implicit view/trigger
dependencies; `UNLOCK TABLE[S]` and disconnect release ownership.

`HANDLER` supports session-local table aliases, natural and named-index
navigation, prefix comparisons, WHERE/LIMIT filtering, temporary tables,
live row roots, declared result metadata, and DDL invalidation; MySQL likewise
refuses it through the prepared-statement protocol.
`CREATE SERVER`, `ALTER SERVER`, and `DROP SERVER` maintain the persisted
`mysql.servers` catalog with MySQL's patch and privilege semantics.

### Statement-level gaps

| Statement family | Impact | Class |
|---|---|---|
| Server-side `LOAD DATA INFILE`; `SELECT … INTO OUTFILE/DUMPFILE`; `IMPORT TABLE` | medium | refusal |
| `CHECKSUM TABLE` returns a stable fsdb row checksum rather than MySQL's storage-engine-specific value; `FLUSH PRIVILEGES`/`USER_RESOURCES`/`STATUS`, all log-channel forms, plain and named `TABLES` (including `LOCAL`/`NO_WRITE_TO_BINLOG` and named `WITH READ LOCK`/`FOR EXPORT`), and `OPTIMIZER_COSTS` work, while the global `FLUSH TABLES WITH READ LOCK` remains absent | low | divergence/refusal |
| `ALTER TABLE` retains last-wins `ALGORITHM`/`LOCK` options, accepts known engine names, follows `NO_ENGINE_SUBSTITUTION` for unknown engines, and rejects unsupported operation, generated-column, foreign-key, and lock combinations with MySQL errors; engine changes and InnoDB's COPY/INPLACE/INSTANT lock duration still collapse to one atomic immutable-root publication | low | divergence |
| `CREATE TABLE` rejects unknown engines under `NO_ENGINE_SUBSTITUTION` and otherwise reports MySQL's substitution warnings; known non-InnoDB engine names still use and report fsdb's shared InnoDB-shaped row store | low | divergence |
| HASH and LINEAR HASH partition definitions, `pN` selection, INFORMATION_SCHEMA/SHOW metadata, `ADD`/`COALESCE`/`TRUNCATE PARTITION`, and logical `ANALYZE`/`CHECK`/`OPTIMIZE`/`REPAIR PARTITION` operate over the shared row store; `DROP PARTITION` returns MySQL's HASH-specific refusal, while physical pruning and partition renaming through `REORGANIZE PARTITION` remain absent | low | divergence/refusal |
| Replication/admin SQL: `CHANGE REPLICATION SOURCE TO`, `PURGE BINARY LOGS`, `RESET`, `BINLOG`, `INSTALL/UNINSTALL PLUGIN|COMPONENT`, `ALTER INSTANCE`, and `TABLESPACE` statements | low | refusal |
| `EXPLAIN FORMAT=JSON/TREE` report the logical access plan without MySQL's cost model; `EXPLAIN ANALYZE` reports aggregate runtime/cardinality rather than per-iterator observations | low | divergence |
| `CREATE/ALTER USER` enforce account locks, `REQUIRE SSL`/`X509`, per-account query/update/connection limits, explicit and global-default password lifetimes, mergeable JSON attributes/comments, and the expired-password reset sandbox. Auth-plugin selection, issuer/subject/cipher requirements, and password history/reuse/current policy remain absent | medium | refusal |

### SELECT-level syntax gaps

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Locking-read granularity | row and next-key locks over the selected access path | `FOR UPDATE`, `FOR SHARE`, `LOCK IN SHARE MODE`, `OF`, `NOWAIT`, and `SKIP LOCKED` hold shared or exclusive row-stripe ownership until transaction end; direct indexed single-table predicates narrow their targets, while joins and scan-shaped reads conservatively lock every row in each named physical source; no next-key/gap locks | low | divergence |
Expression coverage that does exist: full comparison/logical/arithmetic
operators incl. `<=>`, row-value comparisons and `IN`, `XOR`, three-valued logic; CASE (both forms);
CAST/CONVERT; EXISTS/IN/ANY/SOME/ALL/BETWEEN/LIKE [ESCAPE]/REGEXP; `->`/`->>` JSON
operators; charset introducers; hex literals; typed temporal literals;
`INTERVAL n unit`; MATCH…AGAINST; collation postfix; version-comment
splicing `/*!NNNNN … */`; and MySQL's single-row `FROM DUAL` source.

## 2. Query execution

Equi-joins use collation-folded hash keys; other joins use lazy nested loops.
Direct physical inner tables can build statement-local equality buckets.
`ORDER BY ... LIMIT` uses a bounded top-N sort.

Statement-stable scalar, `EXISTS`, `IN`, `ANY`, `SOME`, and `ALL` subqueries
materialize once per statement. Compatible scalar and row-value membership
tests reuse typed sets and can narrow a directly indexed outer table. Correlated
forms preserve MySQL NULL and multi-column error semantics.

Execution also covers `WITH ROLLUP`, numeric and temporal window frames,
multi-column `COUNT(DISTINCT ...)`, the `GROUP_CONCAT` byte ceiling,
statement-atomic multi-table DML, and exact ODKU affected-row counts. Row
comparisons retain null-safe behavior and MySQL's 1241 error for invalid
multi-column subqueries; empty-group bit aggregates retain their identities.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Secondary-index access paths | ref/eq_ref/range scans feed joins, DML, ORDER BY, GROUP BY | fully-bound composite equality probes, scalar/composite literal `IN` lists, and matching physical inner/left/right joins use B-tree buckets; full-result inner/left joins reject broad repeated probes in favor of one compatible hash build, while early-stopping joins retain streaming probes; a simple physical `RIGHT JOIN` can reverse-probe an exact left index and stream the preserved right side; direct literal ranges feed single-table SELECT/UPDATE/DELETE and locking reads through primary, unique, and secondary indexes; `ORDER BY` and `GROUP BY` can stream matching stored-column and `LOWER(column)`/`UPPER(column)` key prefixes, including suffixes after literal-equality keys; arbitrary expression ordering and broader grouping still scan/sort | high (scale) | divergence |
| Optimizer | pushdown, constant folding, join reordering, cost model, statistics | qualified physical inner-join stars choose ready indexed sources by cardinality and push qualified base-table ranges into the initial scan, while `STRAIGHT_JOIN` preserves written order; direct physical range and equality probes compare immutable index cardinality with table cardinality before resolving rows, literal `IN` lists use distinct-key distribution plus a bounded bucket count to avoid broad unions, and full-result equi-joins compare repeated index row resolution with one hash build; outer/lateral/derived joins and statements with name-resolution-sensitive unqualified references retain source order; broader pushdown, persisted statistics, and a general cost model remain absent | medium | divergence |
| EXPLAIN fidelity | type ∈ system/const/eq_ref/ref/range/index/ALL; FORMAT=JSON/TREE; ANALYZE; optimizer_trace | access types cover compatible direct bounds/orderings; JSON/TREE plans and aggregate ANALYZE observations work, while per-iterator timing/costs and optimizer trace rows remain absent | low | divergence |
| Subquery strategies | semi-join/materialization/early-exit transformations | statement-stable scalar/IN/ANY/SOME/ALL/EXISTS subqueries materialize once; compatible integer/string/decimal scalar `IN`, scalar equality quantifiers, and row-value `IN` reuse typed equality sets, while `IN`/`= ANY` also narrow direct indexed physical outer tables with residual, prefix-key, and row-NULL checks; simple EXISTS stops at one row; direct correlated equalities use persistent indexes or a statement-local canonical-key lookup over a physical inner table; other correlated, variable-bearing, nondeterministic, CTE, derived, lateral, and JSON_TABLE forms re-execute | medium (scale) | divergence |
| Join size ceiling | unbounded (memory-bound) | `Executor.maxJoinCandidateRows` caps candidate rows at 1,000,000 → error 1105 | medium | divergence |
| sql_mode | MySQL 8.4 modes affect parsing and execution | strictness, zero-date modes, ERROR_FOR_DIVISION_BY_ZERO diagnostics, ONLY_FULL_GROUP_BY, NO_ENGINE_SUBSTITUTION, ANSI_QUOTES, IGNORE_SPACE, PIPES_AS_CONCAT, REAL_AS_FLOAT (including ANSI implications), HIGH_NOT_PRECEDENCE, NO_AUTO_VALUE_ON_ZERO, NO_UNSIGNED_SUBTRACTION, NO_BACKSLASH_ESCAPES, TIME_TRUNCATE_FRACTIONAL, and PAD_CHAR_TO_FULL_LENGTH have effect; most other mode bits remain inert | medium | divergence |

## 3. Built-in functions

`Functions.builtins` covers these broad families:

- string search, transformation, weighting, regular expressions, phonetics,
  quoting, base64 conversion, and MySQL aliases;
- exact and approximate rounding, base conversion, CRC32, bit counting,
  logarithms, exponentials, and trigonometry;
- date and time arithmetic, formatting, parsing, extraction, week modes,
  day-number conversion, and Unix timestamps;
- JSON extraction, mutation, search, schema validation, aggregation, and
  `JSON_TABLE`;
- AES encryption and decryption across MySQL block modes, HKDF, PBKDF2-HMAC,
  hashing, and UUIDs;
- IPv4 and IPv6 conversion and predicates;
- NULL-selection, comparison, and session identity functions.

| Missing family | Functions | Impact |
|---|---|---|
| Geometry topology and relations | overlays, non-point buffers, buffer strategies, and geographic SRS semantics; planar point `ST_Buffer` and common predicates work | low |

`CONVERT_TZ` resolves numeric offsets and `SYSTEM`, but named zones return NULL
without loaded time-zone tables;
`WEIGHT_STRING()` returns host-ICU sort-key bytes for textual collations, not
MySQL's UCA weight-table bytes.

MySQL Enterprise Encryption is not a Community Server compatibility gap. Its
asymmetric key-management functions belong to the separately installed
`component_enterprise_encryption` component. A disposable MySQL Community
8.4.11 oracle returned `1305` for `asymmetric_decrypt`, `asymmetric_derive`,
`asymmetric_encrypt`, `asymmetric_sign`, `asymmetric_verify`,
`create_asymmetric_priv_key`, `create_asymmetric_pub_key`,
`create_dh_parameters`, and `create_digest`; fsdb returns the same
unknown-function error without that component.

## 4. Data types and values

Numeric values cover signed and unsigned integers, fixed-point decimals,
floating-point exponent rendering, numeric display widths, `ZEROFILL`, and
`BIT(1)` through `BIT(64)`. Wire metadata preserves the declared shapes.

Text and binary values cover the CHAR, VARCHAR, TEXT, BINARY, VARBINARY, BLOB,
ENUM, and SET families with per-column charset and collation metadata.

Temporal values cover DATE, YEAR, and microsecond-precision DATETIME,
TIMESTAMP, and signed TIME durations. Fractional values round half-up unless
`TIME_TRUNCATE_FRACTIONAL` applies, and SQL modes control zero-date acceptance.

JSON, functional defaults, virtual generated columns, normalized comments,
and OGC WKB geometry values also persist through the regular value and wire
metadata paths. Virtual generated values are recomputed when queried.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Spatial indexes and operations | R-tree indexes, overlays, general buffers, geographic SRS axis rules | maintained immutable MBR indexes narrow direct `MBRINTERSECTS`, `MBRWITHIN`, and `MBRCONTAINS` predicates for SRID 0; the internal augmented interval tree is not an R-tree, and the remaining operations stay unsupported | low | subset |
| JSON representation | binary DOM, member-of/path ops on it | `Value.VJson` stores raw text, re-parsed per operation | low (perf) | divergence |

## 5. Constraints and indexes

Composite primary and unique keys use collation-aware encodings and MySQL NULL
semantics. Index maintenance is incremental, supports mixed ascending and
descending keys, and keeps invisible constraint indexes out of ordinary plans.

Foreign keys use unique parent probes, cycle-safe cascade, set-null, and
restrict actions, qualified cross-database targets, and the session
`foreign_key_checks` gate. Named CHECK constraints support enforcement state
and `ALTER` validation, and ENUM or SET values enforce membership. Adding a
unique key over colliding data returns 1062 without publishing a corrupt index.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Non-unique secondary indexes | physical structures serving lookups/ordering | separate immutable equality buckets and ordered entries serve fully-bound composite equality, scalar and composite-row literal `IN` lists, prefix-key candidates with full residual checks, matching physical inner/left/right-join keys, direct literal SELECT/UPDATE/DELETE ranges, and matching stored-column or `LOWER(column)`/`UPPER(column)` ordering and grouping prefixes after optional literal-equality keys; broad full-result inner/left join probes yield to a compatible hash build; duplicate structures deliberately trade memory and write work for point probes plus bounded seeks; other expression ordering and grouping still sort | high (scale) | divergence |
| Expression indexes | functional key parts participate in physical access and uniqueness | `LOWER(column)` and `UPPER(column)` key parts maintain physical equality buckets for matching equality and literal-`IN` SELECT/DML predicates, maintain ordered entries for matching `ORDER BY` and `GROUP BY` prefixes and fixed-prefix suffixes, and enforce uniqueness; other non-unique expressions retain DDL, persistence, and metadata but use scan fallback, while other unique expressions are refused | low | divergence/refusal |

## 6. Charsets and collations

The ICU-backed registry covers the utf8mb4 0900 attribute matrix, legacy and
language-tailored Unicode collations, and common Windows, DOS, CJK, ISO Latin,
KOI8, and Mac codecs. Catalog metadata uses MySQL collation IDs and sort lengths.

DDL, write coercion, introducers, `LOAD DATA`, `CONVERT`, binary keys, and byte
functions are charset-aware. Comparisons apply PAD SPACE semantics, connection
collations, and symmetric MySQL coercibility precedence across scalar, row,
subquery, quantified, conditional, pattern, and join expressions.

String-result policies live beside their scalar implementations. The executor
composes them through aggregates, subqueries, windows, fixed-binary values, and
JSON without maintaining a second builtin-name list.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Weight tables | UCA 9.0/5.2/4.0 weight tables per collation | `Collation` uses ICU CLDR tailoring; tie-break order among primary-equal strings and `WEIGHT_STRING()` textual bytes can differ (equality never does) | low | divergence |
| Advanced REGEXP grammar | ICU regular expressions and Unicode properties | bounded .NET regex with common POSIX character classes and mapped malformed patterns; remaining ICU-only grammar and error-code distinctions can differ | low | divergence |
| Remaining charset catalog | every bundled charset and collation | armscii8, dec8, eucjpms, gb2312, geostd8, hp8, keybcs2, sjis, swe7, and tis620 remain refused; expanded families register their default and binary collations rather than every legacy language collation | low | refusal |

## 7. Transactions and concurrency

Transactions use private snapshots and three-way optimistic merge. Disjoint
point writes have a fast path; indexed point and range updates or deletes wait
and rebase. Unique-key claims, incremental index validation, and merged-result
foreign-key validation protect publication.

Savepoints follow MySQL establishment order. Autocommit uses implicit
transactions, read-only transactions do not block writers, and unrelated
databases use independent roots. Row-stripe ownership coordinates finer-grained
writes. Redo-backed AUTO_INCREMENT reservations survive rollback and restart.

`READ UNCOMMITTED` composes the immutable deltas of active transactions into a
fresh statement view without publishing them; rolled-back deltas disappear on
the next statement, while stronger isolation levels never consult that view.

`SERIALIZABLE` uses conservative whole-catalog validation for writing
transactions, preventing write skew while keeping read-only transactions
lock-free.

Queued table writers close the reader gate until acquisition and release,
preventing later readers from starving them.

Row- and key-stripe waits participate in a shared wait-for graph; the request
with the least held ownership aborts with MySQL error 1213/SQLSTATE 40001;
equal-cost cycles choose the newest participant.

XA branches use the same private snapshots and conflict validation. Prepared
branches detach from their sessions, survive WAL recovery, remain invisible
until completion, and defer snapshot truncation until every prepared branch
has resolved.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Failed cross-database commit atomicity | `COMMIT` publishes every database change or none | `Storage.commitCatalogIntoWith` holds the affected roots but merges them sequentially; if a later root raises an optimistic 1205 conflict, earlier roots have already been published even though `COMMIT` reports failure | high (data integrity) | divergence |
| SERIALIZABLE locking behavior | predicate/gap locks and blocking reads | conservative snapshot validation rejects any intervening catalog change with 1205 when the transaction writes; read-only transactions retain snapshot semantics | low | divergence |
| Write parallelism within a database | row-lock concurrency | indexed UPDATE/DELETE paths coordinate row stripes, literal VALUES inserts/upserts claim supplied unique keys and existing duplicate rows, and AUTO_INCREMENT identities are reserved across transaction snapshots; generated/default non-identity keys, INSERT SELECT, full-scan, CTE, and multi-table writes still rely on optimistic merge; publishing a new immutable database root remains one brief per-database critical section, and durable commit events are sequenced | medium (throughput) | partial |
| Multi-database scaling | near-linear with connections | database roots and row-lock stripes are sharded; qualified foreign keys deliberately serialize catalog-wide referential actions, and recorded campaigns show CPU saturation limiting higher worker counts | medium | partial |
| Cross-database snapshots | linearizable catalog reads | the `Store.Catalog` projection is explicitly not atomic across databases mid-commit | low | divergence |
| XA recovery details | recovered branches retain InnoDB locks and `XA RECOVER` requires `XA_RECOVER_ADMIN` | live prepared branches retain row/key ownership and `XA_RECOVER_ADMIN` is enforced through `mysql.global_grants`; after restart, overlapping completion returns 1205 through optimistic validation instead of waiting on reconstructed locks; use `CONVERT XID` for byte-exact non-ASCII identifiers because the unconverted result still crosses the string result carrier | low | divergence |

## 8. Persistence and durability

Persistence is opt-in through `--data-dir`. The WAL uses length- and CRC-framed
commit records with torn-tail truncation; snapshots are self-delimiting and
CRC-protected. A durable flush precedes acknowledgement, and fatal flush failure
terminates the process rather than claiming a commit.

Snapshot replacement verifies the `.new` file before preferring it and syncs
the containing directory after rename on Unix. Replay applies ordered changes
without re-entering checked write paths and maintains derived indexes
incrementally.

Group commit, ordered checkpoint barriers, lock-step rotation, shutdown
rotation, decode-depth limits, generated-expression codecs, and durable XA
records share the same persistence path. Checkpoint rotation waits for prepared
XA branches so their recovery base remains in the WAL.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Durability default | durable unless configured otherwise | in-memory unless `--data-dir` passed; process death loses everything | medium (deployment) | divergence |
| Keyless WAL row lookup | redo addresses physical records directly | replay resolves rows through unique indexes when possible; events on tables without a usable unique key use one ordered table pass because the WAL stores row images rather than row ids | low (recovery and durable keyless-write throughput) | divergence |
| Space reclamation | purge threads reclaim deleted rows | Delete-heavy tables compact immutable row roots after at least 256 tombstones occupy one quarter of physical slots; reclamation is foreground and occasionally scans one table root | low | divergence |

## 9. Views and triggers

**Views.** Creation and alteration preserve explicit column lists, algorithms,
host-qualified definers, and SQL security. Definitions may reference other
views; recursive references return 1462. Definer privileges are checked when a
view is read, so later revocation takes effect. Definitions persist through the
WAL and snapshots and appear in `SHOW CREATE VIEW` and `I_S.VIEWS`.

Single-table and nested views accept updates and deletes through direct columns,
including predicates over computed projections. Insertable views also accept
the supported INSERT, REPLACE, and ODKU forms with required, repeated, exposed,
and privilege checks. `LOCAL` and `CASCADED CHECK OPTION` predicates persist and
compose through nested views.

Uncorrelated scalar projection subqueries preserve updateability but not
insertability, dependent projection subqueries refuse writes, and subqueries
inside view predicates lower with the base-table write.

Direct physical inner-join views can update one component table per statement
and insert through an explicit column list into one insertable component.
Mergeable component views and simple outer layers preserve the same behavior,
including inherited CHECK OPTION predicates, when the nested security identity
is unchanged.

An inner join may also use a nonmergeable aggregate or UNION view as a
read-only row source while updating another mergeable component; INSERT still
requires every component to be mergeable.

Multi-component writes, outer-join writes, and join-view DELETE/REPLACE are
refused with MySQL-compatible errors.

View projections appear in I_S.COLUMNS, DESCRIBE, SHOW COLUMNS, and SHOW TABLE
STATUS. SHOW CREATE VIEW reports the algorithm, host-qualified definer,
security mode, explicit column list, and check option. Metadata is derived
from the saved query without evaluating it.

Direct single-table projections with a static predicate merge into the outer
SELECT so physical equality, range, and ordered-limit paths remain available;
other view shapes materialize once per statement.

**Triggers.** Multiple `BEFORE` and `AFTER` triggers run in declared
`FOLLOWS`/`PRECEDES` order for INSERT, UPDATE, and DELETE. Bodies can use OLD and
NEW row images, `SET NEW`, DML, local declarations, branches, labeled loops,
condition handling, and nested procedure calls with typed output targets.

Single- and multi-table writes fire row triggers atomically. Error 1442 protects
every target and joined table in the invoking statement. Each fire uses the
definer's privileges and restores the creation-time SQL mode, client charset,
and connection collation. Definitions follow their table lifecycle and appear
in `SHOW TRIGGERS` and `I_S.TRIGGERS`.

Generated row-image columns and illegal OLD/NEW images are rejected when the
trigger is created.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Updatable-view breadth | nested write targets with distinct definer/security contexts and additional expression shapes where MySQL deems individual columns writable | single-table, same-identity nested joins, outer view layers, and aggregate/UNION read-only join components compose writable targets; one mergeable component updates or inserts at a time | medium | refusal |
| View algorithm strategy | MERGE and TEMPTABLE select distinct execution strategies | declarations and ALTER retain the effective algorithm, incompatible MERGE shapes become UNDEFINED with warning 1354, and TEMPTABLE views are non-updatable; MERGE and UNDEFINED still share fsdb's shape-driven planner | low | divergence |
| VIEW_DEFINITION rendering | fully-qualified canonical expression text | SHOW CREATE VIEW renders the stored declaration envelope, but its SELECT body and I_S.VIEWS.VIEW_DEFINITION retain the user's original text | low | divergence |

## 10. Stored routines, events, schedulers

**Procedures.** Typed `IN`, `OUT`, and `INOUT` parameters work with
`DECLARE`/`SET`, nested `IF`/`ELSEIF`/`ELSE` and `CASE`, labeled blocks,
`WHILE`, `REPEAT`, and `LOOP` with `LEAVE`/`ITERATE`, scoped condition
declarations, `CONTINUE`/`EXIT` handlers, `SIGNAL`/`RESIGNAL`, sequential SQL statements,
routine variables in expressions and `LIMIT`, and multiple resultsets with
the protocol's final OK result.

Creation, removal, calls, SHOW output, and routine metadata are persisted.
Definer and invoker bodies use the corresponding account, routine schema, and
captured session semantics. `INFORMATION_SCHEMA.PARAMETERS` describes procedure
arguments and function return or argument rows.

Procedure recursion follows the GLOBAL and SESSION
`max_sp_recursion_depth` setting, including MySQL's 0–255 bounds and
per-routine counting for mutual recursion.

**Functions.** Stored functions support typed parameters, return coercion,
`RETURN`, compound control flow, handlers, cursors, subqueries, typed local
`SELECT … INTO`, nested function and procedure calls, definer or invoker
execution, native-function precedence, prepared execution, SHOW metadata, and
catalog persistence. Stored functions retain MySQL's 1424 recursion refusal
and restore their creation-time SQL mode, client charset, and connection
collation.

Data-changing bodies write through the invoking statement's transaction, so a
failed statement discards their effects. Error 1442 protects every table the
invoking statement reads or writes. Metadata-only probes and synthetic
validation rows do not invoke the body, while function writes during
`CREATE TABLE … AS SELECT` return 1746.

**Events.** One-time and recurring declarations support creation, removal,
schedule, status, body and name alteration, SHOW output, persistence, and
definer-context execution. Routine, execute, and event privileges guard their
corresponding paths.

## 11. Full-text search

Natural-language, boolean, and query-expansion modes use MySQL 8.4's
oracle-verified TF × IDF² scoring with its epsilon floor and built-in stopword
list. Boolean syntax covers
`+ - > < ~ word* "phrases" @N proximity ()` with depth cap; blind
relevance-feedback expansion and bare WHERE-MATCH relevance ordering are also
supported.

FULLTEXT DDL, introspection, column validation, and indexed-column collation
semantics share immutable term-frequency and position postings. DML maintains
the postings incrementally, while recovery rebuilds them once. Direct
WHERE-MATCH candidates stream by stable row identity.

Boolean evaluation unions only touched postings, prefix terms have maintained
prefix postings, and bounded AND/OR predicate trees intersect or union MATCH
candidates before residual evaluation. Projection-only MATCH retains ordinary
point lookup plans; physical joins score each owning corpus before joining;
single- and multi-table UPDATE/DELETE score each physical source before
evaluating join conditions, predicates, and assignments.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| MATCH planning | optimizer can combine FULLTEXT access with every other access path | bounded AND/OR MATCH predicates stream posting candidates and projection-only MATCH preserves point probes; other projection-only shapes scan their owning corpus | medium (scale) | divergence |
| Tunables | innodb_ft_min_token_size, innodb_ft_max_token_size, ft_query_expansion_limit, stopword tables, enable/disable | the three numeric defaults are exposed with MySQL's GLOBAL/read-only scope and drive `FullText` at 3 / 84 / 20; `INNODB_FT_DEFAULT_STOPWORD` exposes the exact duplicate-preserving built-in list, while custom stopword tables and enable/disable behavior remain absent | low | divergence/refusal |
| CJK | ngram and mecab parsers, WITH PARSER clause | absent; no CJK tokenization | medium (for CJK) | refusal |

## 12. Wire protocol and prepared statements

HandshakeV10 negotiates capabilities, deprecated EOF behavior, authentication,
compression, TLS, packet limits, and affected-row mode. Authentication can
switch caching-SHA2 clients to `mysql_native_password` and verifies credentials
in constant time.

The command surface covers query, database selection, ping, field listing,
quit, connection reset, and the complete prepared-statement lifecycle. Prepared
statements support read-only cursors, type reuse, bounded long data, and text or
binary rows with microsecond temporal precision. Packet framing handles values
larger than one protocol packet.

Transport supports zlib, Zstandard, TLS 1.2 and 1.3, optional server and client
CA certificates, and secure-transport enforcement. Packet, connection, and
prepared-statement limits are enforced and advertised honestly. Mid-query
disconnects cancel evaluation through `Server.watchForDisconnect`.
`COM_SET_OPTION` toggles multi-statement handling for negotiated clients.

`CLIENT_SESSION_TRACK` reports default-schema changes and assignments to the
configured system-variable set, including same-value assignments, plus the
generic state-change tracker and transaction state/characteristics when enabled.

Physical result columns report
primary, unique, composite, and non-unique key membership consistently across
queries, prepared statements, `COM_FIELD_LIST`, and `HANDLER`. Prepared
descriptors derive schema, operator, aggregate, overloaded scalar, temporal,
JSON, spatial, and registered-extension result families without evaluating
the statement.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| TLS certificate lifecycle | live certificate/trust-store reload and CRL validation | server and client-CA certificates are loaded when the listener starts; client chains are validated without revocation checks | low (rotation requires restart) | subset |
| Compression policy | `protocol_compression_algorithms` changes the algorithms offered to new connections | zlib and Zstandard are always offered; the global variable reports that static policy and refuses assignment | low | subset |
| Cursor storage | materialized temporary tables spill from memory to disk | read-only, forward-only cursors retain their materialized rows in session memory until exhaustion, reset, close, or commit | low (large concurrent cursors) | divergence |
| Session state tracking | schema, system-variable, generic state, transaction, and GTID trackers | schema, configured system-variable, generic state-change, transaction-characteristic, and transaction-state blocks are encoded in final OK packets; GTID blocks remain absent because fsdb has no binlog | low | subset |
| Diagnostics coverage | warnings from conversions, truncation, deprecated syntax, and storage engines | statement errors, ignored INSERT/CHECK rows, non-strict integer/ENUM/SET/charset coercions, DECIMAL scale-loss notes, declared text/binary truncation, conditional DDL and unknown-engine substitution, GROUP_CONCAT truncation, deprecated numeric displays, `utf8` aliases and explicit `utf8mb3` declarations/conversions, plus `SQL_CALC_FOUND_ROWS`, `FOUND_ROWS()`, and ODKU `VALUES()` are captured; other warning producers remain silent | low | divergence |
| Auth plugins | caching_sha2_password fast/full auth, sha256_password, RSA exchange | mysql_native_password only; `Server.authenticateAccount` downgrades caching_sha2 clients via auth-switch | low (works, weaker) | divergence |
| System variables | hundreds live | common connector, limit, transaction, password-lifetime, and week-format variables are live; most others are inert or absent, and time_zone remains a static string without conversion | medium | divergence |

## 13. Authentication and privileges

The account catalog follows MySQL 8.4's `mysql.user` column order and includes a
root bootstrap account. Passwords use double-SHA1 hashes with constant-time
comparison. Account DDL covers locks, TLS requirements, password expiry,
resource limits, and mergeable JSON attributes or comments.

Static and dynamic grants apply at global, database, table, and column scope.
Grant option is checked at the target level, unknown privileges fail closed,
and denials retain MySQL's level-specific error shapes.

Roles support admin option, transitive inheritance, default and session
activation, mandatory roles, login activation, metadata visibility, and catalog
cleanup. Proxy grants are target-specific and persist in
`mysql.proxies_priv`.

Privilege discovery recurses through subqueries, derived tables, and CTEs.
Metadata visibility, view security, PROCESS-scoped process control, and trigger
privileges use the same authorization model. Grant data persists through
ordinary row operations.

`SHOW DATABASES` and `SHOW TABLES` filter by visibility. `DROP TRIGGER` resolves
its subject table before checking the `TRIGGER` privilege.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Hostname accounts | forward-confirmed reverse DNS matching | numeric peer addresses plus the loopback `localhost` alias; DNS names are not trusted | low | divergence |
| Advanced account policy | auth-plugin selection and password history/reuse/current policy | explicit/default expiry lifetimes, resource limits, and account attributes/comments are enforced; advanced policy clauses remain absent | low | refusal |
| Proxy identity selection | authentication plugins can map a login to an authorized proxied account | proxy declarations, target-specific grant-option delegation, lifecycle cleanup, persistence, and `SHOW GRANTS` lines work; mysql_native_password never returns an alternate identity and fsdb has no pluggable authentication provider | low | refusal |
| System-table coverage | mysql.* tables with engine-maintained contents | MySQL 8.4 table schemas preserve column order, types, nullability, key membership, defaults, and generated columns alongside fsdb's stored-object catalogs; stock optimizer-cost and group-replication configuration/action rows are present, but native catalog collations and engine-maintained help, log, GTID, InnoDB-statistics, procedure-grant, NDB, and replication-channel rows still differ or remain empty unless ordinary fsdb DML populates them | low | divergence |

## 14. Metadata, server administration, logging, replication

Viewer-scoped `INFORMATION_SCHEMA` surfaces cover schemas, tables, columns,
indexes, constraints, views, triggers, processes, engines, charsets, collations,
extensions, geometry, privileges, roles, dependencies, keywords, plugins, user
attributes, and the supported optimizer and physical-engine metadata shapes.

MySQL-native `mysql.*` schemas and fsdb catalogs are directly queryable. SHOW
and DESCRIBE cover object definitions, status, privileges, process state,
variables, diagnostics, and live byte accounting. Mysqldump's key toggles are
accepted as no-ops.

Server administration covers MySQL-format option files, scoped KILL checks,
and live limit reporting. `SHOW STATUS` exposes the `Com_*` registry and updates
each implemented command family, including prepared statements, XA, HANDLER,
routines, events, and administrative probes.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| INFORMATION_SCHEMA breadth | INNODB_*, KEYWORDS, PLUGINS, spatial-reference catalogs, and usage views | Every MySQL 8.4 view is present. `INNODB_TRX` projects active transaction identity, lifecycle, isolation, checks, logical write weight, and held row stripes; fields that require InnoDB's lock-memory and scheduling internals remain zero or NULL. Other InnoDB dictionary views project live table, column, index, statistics, and virtual-column metadata; physical diagnostics return truthful empty rowsets where fsdb has no matching buffer-pool, tablespace, compression, or metrics subsystem. ST_SPATIAL_REFERENCE_SYSTEMS exposes fsdb's supported SRID 0 instead of MySQL's full EPSG registry | low | divergence |
| Table statistics | estimates refreshed by ANALYZE TABLE | `InformationSchema.tablesRows` reports InnoDB, a 16384 DATA_LENGTH stand-in, CARDINALITY 0, and live row counts where MySQL keeps stale page estimates until ANALYZE | low | divergence |
| Optimizer cost overrides | `mysql.server_cost` and `mysql.engine_cost` values feed plan costs after `FLUSH OPTIMIZER_COSTS` | both tables expose MySQL's eight bootstrap rows, generated defaults, and mutable override columns; fsdb's shape-driven planner does not consume their overrides | low | divergence |
| SHOW STATUS counters | Com_*, Innodb_*, Slow_queries, … | `Com_*` names have distinct session/global values and supported commands are live; unsupported commands remain truthfully zero, while engine/latency families remain absent (`InformationSchema.fs`) | low | divergence |
| Logging | general log, slow log, error-log file | `mysql.general_log` and `mysql.slow_log` expose their catalog schemas but remain empty; diagnostics go to credential-redacted stderr (`Log.fs`) | low | divergence |
| Replication | binlog, GTID, source/replica channels | no replication execution; REPLICATION privileges are vocabulary only and internal WAL is not a binlog; native catalog schemas plus the three stock group-action configuration rows exist for metadata compatibility | architectural | refusal |

## 15. Differential-testing and performance tails

| Open campaign | Current gap |
|---|---|
| Planner constant factors | Indexed joins, equality/`IN`, and secondary ranges retain a constant-factor gap. Low-cardinality joins avoid repeated broad index probes, while grouping, scan-shaped updates, decimal membership, and full-text joins remain input-sensitive. Benchmark result artifacts carry the measurements; shared statement setup and scan-shaped plans remain the principal measured seams. |
| Transaction fault scheduling | The torture harness lacks matched connection churn during transactions, cancellation while queued, savepoints under contention, and concurrent campaigns across every isolation level. |
| Catalog churn | Concurrent `CREATE/DROP DATABASE` under query and transaction traffic lacks a differential campaign. |
| Snapshot rotation volume | Crash/restart campaigns cover acknowledged-commit and atomicity invariants; longer high-volume checkpoint-rotation campaigns remain useful stress coverage. |

## 16. Deliberate divergences (accepted, not targeted for parity)

Documented or ponytail-marked decisions that differ from MySQL intentionally:

- a one-million-row join candidate ceiling;
- the additive `VECTOR` type and function family forward-ported from MySQL 9;
- live statistics rather than `ANALYZE`-stale estimates;
- ICU CLDR collation tailoring;
- `SUPER` for a foreign `KILL`;
- honest limit advertising, leaving fsdb-only WAL rotation knobs unreported;
- a trusted data directory whose CRCs detect corruption but do not authenticate
  a hostile local writer.

## 17. Relative severity view

Ranked by expected disruption to the primary consumers, independent of
implementation effort:

1. General cost-based planning beyond qualified physical inner joins, plus
   correlated forms that cannot use a direct physical-table equality lookup. Correctness holds,
   but scale still diverges from MySQL past small data.

2. Transaction scheduling. Indexed point/range UPDATE and DELETE statements
   wait and rebase, while the remaining transaction write shapes still rely on
   optimistic catalog merge.

3. Complex join-derived updatable views. Procedures, functions, and triggers
   cover nested calls with local OUT/INOUT targets, typed locals, condition
   handlers, SIGNAL/RESIGNAL, branches, labeled loops, cursors, and sequential
   data-changing statements.

4. Overlay/buffer operations and geographic SRS behavior. Planar spatial
   indexes, the common topology family, equality, and convex hull are covered.

5. Replication, logging, broad engine counters, and the remaining metadata
   tail. Core command counters are live; replication remains architectural.
