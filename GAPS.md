# MySQL 8.4 feature gaps

A map of where fsdb diverges from or lacks MySQL 8.4 functionality. Oracle for
every row is real MySQL 8.4 (never sqlite). Audit date: 2026-08-24, based on a
full static exploration of `src/Fsdb/` plus the documented records
(`docs/compatibility.md`, `torture/findings/`, `torture/support/known-gaps.json`,
`docs/performance-*.md`) and the adversarial parser, wire, privilege, logging,
and persistence review. Evidence anchors name files and definitions instead
of line numbers so routine refactors do not silently make them misleading.

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

The torture ledger `torture/support/known-gaps.json` is currently empty
(0 signatures); everything below is either undocumented in code, deliberately
accepted (marked `ponytail:` in source), or recorded only in
`torture/findings/`.

## Summary by area

| Area | State | Largest single gap |
|---|---|---|
| SQL statements | Broad core; large admin/programmatic tail missing | Stored procedures/functions, events |
| Query execution | Composite equality, literal range, bounded index ordering, indexed inner joins, and stable subquery materialization | Join reordering and correlated subqueries still scale poorly |
| Built-in functions | Broad scalar, aggregate, JSON, time, and common planar geometry coverage | Overlays, buffers, and geographic SRS semantics |
| Data types | Common scalar types, BIT fields, signed TIME durations, and OGC geometry | Spatial indexes and operations |
| Constraints & indexes | PK/UNIQUE/FK/CHECK plus composite equality, inner-join, literal range, grouping, and bounded index-order probes | Unique and DML ranges still scan |
| Charsets & collations | ICU-based utf8mb4 registry | Weight-table tailoring differs from MySQL's UCA tables |
| Transactions | Repeatable-read snapshots, nonlocking read-committed views, conservative serializable validation, and optimistic merge | READ UNCOMMITTED is refused; transaction commits serialize |
| Persistence | WAL + snapshot, crash-tested | Opt-in only; no group commit; tombstones never reclaimed |
| Views & triggers | Direct updatable views with all insert/replace forms; ordered BEFORE/AFTER INSERT/UPDATE/DELETE triggers and compound DML bodies | Complex views and the stored-program control language |
| Routines & events | Absent (catalogs honestly empty) | Everything |
| Full-text | Oracle-verified scoring over maintained inverted indexes | Single-table SELECT only; no CJK parser |
| Wire protocol | Handshake through COM_STMT_EXECUTE, TLS, LOCAL INFILE, and multi-result batches | No compression or cursors |
| Auth & privileges | Static privileges enforced incl. subqueries and per-host accounts | No roles/dynamic/column privileges |
| Metadata | 23 INFORMATION_SCHEMA views, 8 mysql.* tables, and core live command counters | Storage statistics are stand-ins; many SHOW forms missing |
| Server admin | KILL, SHUTDOWN, limits, config file parsing | No replication/binlog/logging files |

## 1. SQL statements and parser

Working core: full DML (INSERT/REPLACE/UPDATE/DELETE incl. INSERT/REPLACE SET
and multi-table forms,
ODKU, IGNORE), SELECT with joins (INNER/LEFT/RIGHT/CROSS/NATURAL/USING),
derived/LATERAL/JSON_TABLE sources, query-scoped CTEs (ordinary and recursive), set
operations, window functions with frames, GROUP BY WITH ROLLUP + GROUPING,
DDL for databases/tables/indexes/views/triggers/users/grants, CREATE TABLE AS
SELECT, TRUNCATE,
RENAME TABLE, EXPLAIN (TRADITIONAL). Transaction control, SET, SHOW (~25
variants), USE, KILL, DESCRIBE are text-probed before the grammar
(`QueryHandler.dispatch`).

### Statements MySQL 8.4 parses that fsdb refuses (no grammar, no probe)

| Statement family | Impact | Class |
|---|---|---|
| `CREATE/ALTER/DROP PROCEDURE|FUNCTION`, `CALL`, compound `BEGIN…END` bodies, `DECLARE`, cursors, handlers, `SIGNAL`/`GET DIAGNOSTICS` | medium | refusal |
| `CREATE/ALTER/DROP EVENT` (+ scheduler thread) | low | refusal |
| `PREPARE`/`EXECUTE`/`DEALLOCATE PREPARE` as SQL text (wire-level COM_STMT_PREPARE works) | low | refusal |
| Server-side `LOAD DATA INFILE`; `SELECT … INTO OUTFILE/DUMPFILE/@vars`; `IMPORT TABLE` | medium | refusal |
| `CHECKSUM TABLE`; specialized FLUSH forms | low | refusal |
| `LOCK TABLES…READ/WRITE`, `UNLOCK TABLES`, `HANDLER`, XA transactions | low | refusal |
| Partitioning: `PARTITION BY`, `PARTITION (p)` selection, `ADD/DROP/COALESCE/REORGANIZE PARTITION` | medium | refusal |
| Roles: `CREATE/DROP ROLE`, `SET ROLE`, `SET DEFAULT ROLE`, `GRANT role TO user`, dynamic privileges (`BACKUP_ADMIN`…), `GRANT PROXY` | medium | refusal |
| Replication/admin SQL: `CHANGE REPLICATION SOURCE TO`, `PURGE BINARY LOGS`, `RESET`, `BINLOG`, `INSTALL/UNINSTALL PLUGIN|COMPONENT`, `ALTER INSTANCE`, `CREATE SERVER`, `TABLESPACE` statements | low | refusal |
| `EXPLAIN ANALYZE`; `EXPLAIN FORMAT=JSON|TREE` | low | refusal |
| `ALTER TABLE … COMMENT=` option tails | medium | refusal |
| `CREATE USER` tails: auth plugin, `REQUIRE SSL/X509`, resource limits, `ACCOUNT LOCK`, `PASSWORD EXPIRE`; `ALTER USER` beyond password change | medium | refusal |

### SELECT-level syntax gaps

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Locking detail | `FOR UPDATE/SHARE [OF tbl…] [NOWAIT|SKIP LOCKED]` | `FOR UPDATE`/`FOR SHARE`/`LOCK IN SHARE MODE` accepted and ignored; no OF/NOWAIT/SKIP LOCKED | low | divergence |
| Set-operation expression subqueries | scalar/EXISTS/IN/ANY/SOME/ALL subqueries may be a UNION/INTERSECT/EXCEPT query expression | expression-subquery AST carries one SELECT; set operations work at top level and in derived/CTE bodies | medium | refusal |

Expression coverage that does exist: full comparison/logical/arithmetic
operators incl. `<=>`, row-value comparisons and `IN`, `XOR`, three-valued logic; CASE (both forms);
CAST/CONVERT; EXISTS/IN/ANY/SOME/ALL/BETWEEN/LIKE [ESCAPE]/REGEXP; `->`/`->>` JSON
operators; charset introducers; hex literals; typed temporal literals;
`INTERVAL n unit`; MATCH…AGAINST; collation postfix; version-comment
splicing `/*!NNNNN … */`; and MySQL's single-row `FROM DUAL` source.

## 2. Query execution

Working: hash joins for equi-joins with collation-folded keys, lazy nested
loops otherwise, statement-stable scalar/EXISTS/IN/ANY/SOME/ALL subqueries
materialized once per statement, correlated scalar/EXISTS/IN/ANY/SOME/ALL
subqueries with correct NULL
semantics, bounded top-N sort for ORDER BY+LIMIT, GROUP_CONCAT byte cap,
WITH ROLLUP expansion, window frames (ROWS/RANGE, numeric offsets),
COUNT(DISTINCT a,b) tuples, statement-atomic multi-table DML, exact ODKU
affected-rows semantics (changed=2/no-op=0 under default flags), row-value
equality/order/null-safe comparisons and literal/subquery `IN`, MySQL's 1241
error for multi-column scalar/IN/ANY/SOME/ALL subqueries, and the empty-group
identities for bit aggregates.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Secondary-index access paths | ref/eq_ref/range scans feed joins, DML, ORDER BY, GROUP BY | fully-bound composite equality probes and matching physical inner joins use B-tree buckets; direct literal ranges feed single-table SELECT/UPDATE/DELETE through primary, unique, and secondary indexes; bounded `ORDER BY`/`GROUP BY` can stream a matching composite index when preceding keys are fixed; outer joins, unconstrained multi-key ordering, and broader grouping still scan/sort | high (scale) | divergence |
| Optimizer | pushdown, constant folding, join reordering, cost model, statistics | qualified physical inner-join stars choose ready indexed sources by cardinality, while `STRAIGHT_JOIN` preserves written order; outer/lateral/derived joins and statements with name-resolution-sensitive unqualified references retain source order; pushdown, statistics, and a general cost model remain absent | medium | divergence |
| EXPLAIN fidelity | type ∈ system/const/eq_ref/ref/range/index/ALL; FORMAT=JSON/TREE; ANALYZE; optimizer_trace | `type` ∈ {system, const, eq_ref, ref, range, index, ALL}; `range` and `index` cover compatible direct composite bounds/orderings; FORMAT=JSON/TREE, ANALYZE, and optimizer_trace absent; extra flags limited to Using where/filesort/temporary | low | divergence |
| Subquery strategies | semi-join/materialization/early-exit transformations | statement-stable scalar/IN/ANY/SOME/ALL/EXISTS subqueries materialize once, simple EXISTS stops at one row, and direct correlated equalities probe inner primary/unique/secondary indexes; other correlated, variable-bearing, nondeterministic, CTE, derived, lateral, and JSON_TABLE forms re-execute | medium (scale) | divergence |
| Join size ceiling | unbounded (memory-bound) | `Executor.maxJoinCandidateRows` caps candidate rows at 1,000,000 → error 1105 | medium | divergence |
| Multi-table UPDATE/DELETE sources | derived tables allowed as join sources | `Executor.applyMutationJoin` accepts real base tables only → 1064 | low | refusal |
| MATCH…AGAINST placement | evaluates in UPDATE/DELETE WHERE, joins, subqueries | physical SELECT/JOIN sources and single-table UPDATE/DELETE are supported; multi-table UPDATE/DELETE with MATCH remains unsupported | low | refusal |
| RANGE window frames | `RANGE BETWEEN INTERVAL n DAY PRECEDING…` | `Executor.validateFrame` refuses temporal offsets with 1235; numeric offsets only | low | refusal |
| sql_mode | ~20 mode bits with semantic effect | strictness plus NO_ZERO_DATE/NO_ZERO_IN_DATE have effect; ONLY_FULL_GROUP_BY remains absent (a bare column picks the first row of its group), and IGNORE_SPACE does not relax whitespace-sensitive function calls | medium | divergence |

## 3. Built-in functions

Registered surface (`Functions.builtins`): string (CONCAT family,
SUBSTRING_INDEX, ELT/FIELD/FIND_IN_SET/EXPORT_SET, QUOTE, STRCMP,
WEIGHT_STRING with `AS CHAR(N)`/`AS BINARY(N)`, REGEXP_LIKE family with
match_type, SOUNDEX, MAKE_SET, base64 conversion), math (ROUND with exact/approximate split, CONV,
CRC32, BIT_COUNT, logarithms, exponentials, and trigonometry), date/time
(DATE_ADD/SUB, TIMESTAMPADD/DIFF, DATE_FORMAT,
STR_TO_DATE, EXTRACT, LAST_DAY, MAKEDATE, UNIX_TIMESTAMP, FROM_UNIXTIME,
WEEK(mode)/YEARWEEK(mode)), JSON (EXTRACT/UNQUOTE/CONTAINS/SET/INSERT/
REPLACE/REMOVE/ARRAY/OBJECT/LENGTH/DEPTH/VALID/SCHEMA_VALID/
SCHEMA_VALIDATION_REPORT/TYPE/KEYS/SEARCH,
ARRAYAGG/OBJECTAGG, JSON_TABLE), AES (`AES_ENCRYPT`/`AES_DECRYPT` with every
MySQL `block_encryption_mode`, HKDF, and PBKDF2-HMAC), hashing (MD5/SHA1/SHA2), UUID family,
IPv4/IPv6 conversion and predicates, COALESCE/IFNULL/IF/NULLIF/GREATEST/
LEAST, plus session functions DATABASE/LAST_INSERT_ID/VERSION/CONNECTION_ID/
CURRENT_USER/USER/SESSION_USER.

| Missing family | Functions | Impact |
|---|---|---|
| JSON Schema recursive regular-expression references | Local reference cycles traversing `pattern` or `patternProperties` return 1235 | low |
| Geometry topology and relations | overlays, buffers, and geographic SRS semantics; planar `ST_Contains`, `ST_Within`, `ST_Touches`, `ST_Equals`, and `ST_ConvexHull` work | low |

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

Working: TINYINT–BIGINT signed/unsigned, DECIMAL(p,s) with fixed-point
round-trip, FLOAT/DOUBLE with MySQL exponent rendering, CHAR/VARCHAR,
TINYTEXT–LONGTEXT, BINARY/VARBINARY, TINYBLOB–LONGBLOB, ENUM/SET with
canonicalization, DATE/DATETIME(fsp)/TIMESTAMP(fsp)/TIME(fsp) with half-up
fsp rounding and carry cases, all-zero and partial-zero dates with sql_mode
enforcement, YEAR, JSON, per-column charset/collation,
wire-faithful column metadata (`ColumnWire.metadataOfType`), `BIT(1)`–`BIT(64)`
fields with binary literals and defaults, and OGC WKB geometry values
(`GEOMETRY`, concrete spatial types, WKT/WKB construction and common
accessors).

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Spatial indexes and operations | R-tree indexes, overlays, buffers, geographic SRS axis rules | geometry values, common WKT/WKB accessors, planar `ST_Distance`, `ST_Envelope`, `ST_IsValid`, `ST_Contains`, `ST_Within`, `ST_Touches`, `ST_Equals`, `ST_ConvexHull`, `ST_Intersects`, `ST_Disjoint`, and MBR predicates work; spatial indexes still collapse to BTree | low | refusal |
| Generated columns | VIRTUAL recomputed on read, STORED materialized | `Executor.recomputeGeneratedColumns` materializes both at write time; no read-path recompute | low | divergence |
| Functional defaults | `DEFAULT (expr)` | literal constants and CURRENT_TIMESTAMP only | low | refusal |
| Column-comment character sets | converted through the table/column charset; utf8mb3 stores non-BMP text as `?` | raw .NET text, without charset conversion | low | divergence |
| ZEROFILL/display width | zero-fill formatting, width in metadata | not tracked beyond static wire lengths; `ColumnWire.metadataOfType` never sets ZEROFILL | low | divergence |
| JSON representation | binary DOM, member-of/path ops on it | `Value.VJson` stores raw text, re-parsed per operation | low (perf) | divergence |

## 5. Constraints and indexes

Working: composite PK/UNIQUE with collation-aware key encoding and MySQL
NULL-uniqueness semantics, incremental index maintenance, FK enforcement with
MATCH SIMPLE parent probes through unique indexes, ON DELETE CASCADE/SET
NULL/RESTRICT with cycle-safe recursion, ON UPDATE cascade on update/upsert
paths, session foreign_key_checks gate, named CHECK constraints with
ENFORCED/NOT ENFORCED and ALTER ADD validation, ENUM/SET membership,
ADD UNIQUE over colliding data fails 1062 rather than corrupting.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Non-unique secondary indexes | physical structures serving lookups/ordering | separate immutable equality buckets and ordered entries serve fully-bound composite equality, matching physical inner-join keys, direct literal `SELECT` ranges, compatible grouping, and bounded composite index ordering; duplicate structures deliberately trade memory and write work for point probes plus bounded seeks; unique/PK ranges, DML ranges, outer joins, and unconstrained ordering remain scans | high (scale) | divergence |
| Prefix indexes | `INDEX (col(N))` with SUB_PART metadata | `Parser.indexColumn` discards the prefix length; INFORMATION_SCHEMA.STATISTICS reports SUB_PART as NULL | low | divergence |
| Expression indexes | `INDEX ((expr))` | absent | low | refusal |
| Descending/invisible indexes | `DESC`, `INVISIBLE` | absent | low | refusal |
| Cross-database FKs | supported | `Ast.ForeignKeyDef.RefTable` carries no database qualifier; cross-database references are invisible/unenforceable | low | divergence |
| AUTO_INCREMENT | counter persists across restart via redo | burned ids survive rollback (InnoDB-like), but the counter rebuild after crash depends on replayed row events; ALTER can only move it forward | low | divergence |

## 6. Charsets and collations

Working: ICU-backed registry covering the utf8mb4 0900 attribute matrix,
legacy unicode/general collations, ~21 language collations, ja_0900_as_cs_ks,
utf8mb3/latin1(cp1252)/ascii/binary; real MySQL collation ids and SORTLENs
for SHOW COLLATION/I_S; PAD SPACE semantics; connection collation for
literal-vs-literal comparison; symmetric MySQL coercibility precedence for
scalar, row, `IN`, subquery, quantified, `CASE`, `BETWEEN`, `LIKE`, and join
comparisons; default utf8mb4_0900_ai_ci.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Weight tables | UCA 9.0/5.2/4.0 weight tables per collation | `Collation` uses ICU CLDR tailoring; tie-break order among primary-equal strings and `WEIGHT_STRING()` textual bytes can differ (equality never does) | low | divergence |
| Compound-expression collation | string functions derive result collation and coercibility from every argument | comparison operands follow MySQL precedence, but the reported coercibility and result collation of compound strings such as mixed-collation `CONCAT()` remain approximate | low | divergence |
| Advanced REGEXP grammar | ICU regular expressions and Unicode properties | bounded .NET regex with common POSIX character classes and mapped malformed patterns; remaining ICU-only grammar and error-code distinctions can differ | low | divergence |
| Usable charsets | 40+ charsets with transcoding | `Collation.Charset` supports utf8mb4/utf8mb3/latin1/ascii/binary only; CONVERT(expr USING x) has the same ceiling | low | refusal |
| Identifier casing | lower_case_table_names semantics | variable reported; identifiers ordinal-case-folded internally | low | divergence |

## 7. Transactions and concurrency

Working: private-snapshot transactions with three-way optimistic merge and a
point-update fast path for disjoint writes, retryable 1205 on same-row
conflicts, merged-result revalidation of unique keys and FKs, savepoints
with MySQL establishment-order semantics, autocommit implicit transactions,
read-only transactions never blocking writers, per-database sharding so
cross-database writers never contend, 4096-stripe row locks for indexed
updates, InnoDB-style burned AUTO_INCREMENT on rollback.
`SERIALIZABLE` uses conservative whole-catalog validation for writing
transactions, preventing write skew while keeping read-only transactions
lock-free.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| SERIALIZABLE locking behavior | predicate/gap locks and blocking reads | conservative snapshot validation rejects any intervening catalog change with 1205 when the transaction writes; read-only transactions retain snapshot semantics | low | divergence |
| READ COMMITTED | a fresh nonlocking read view per statement | a fresh committed view plus the transaction's own successful writes per parsed statement; locking reads remain unsupported | medium | partial |
| READ UNCOMMITTED | dirty reads | refused with 1235 | medium | refusal |
| Deadlock errors | 1213 deadlock detection with victim selection | write-write conflicts surface as lock-wait timeout 1205; no deadlock classification | low | divergence |
| Write parallelism within a database | row-lock concurrency | indexed autocommit updates use row stripes; transactions and full scans serialize at publication | high (throughput) | divergence |
| Multi-database scaling | near-linear with connections | the 2026-08-17 campaign predates sharded `Store.Databases` and row-striped updates; rerun it before classifying current scaling | medium | unverified |
| Cross-database snapshots | linearizable catalog reads | the `Store.Catalog` projection is explicitly not atomic across databases mid-commit | low | divergence |

## 8. Persistence and durability

Working: opt-in `--data-dir` mode with CRC-framed WAL ([len][crc32] records
over CommitEvent payloads, torn-tail truncation), self-delimiting CRC'd
snapshots, libc fsync-before-ack with FailFast on failure, directory fsync
after rename, `.new` snapshot verification before preference, replay that
bypasses checked write paths with ordered change application, deferred
unique-index rebuild, rotation via a lock-step replica store, signal-driven
final rotation, decode-depth caps, codecs for every column type including
generated-column expressions.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Durability default | durable unless configured otherwise | in-memory unless `--data-dir` passed; process death loses everything | medium (deployment) | divergence |
| Group commit | binlog/redo group flush amortization | `Persistence.attach` performs one open+fsync per committed statement | medium (throughput) | divergence |
| Space reclamation | purge threads reclaim deleted rows | `RowStore` leaves deleted slots as tombstones; long-lived delete-heavy tables grow memory without bound | medium | divergence |
| Platform | portable | durable mode macOS/Linux only (libc fsync design) | low | divergence |

## 9. Views and triggers

Views working: CREATE [OR REPLACE] VIEW with explicit column lists, views
over views, recursive-reference detection (1462), definer-privilege checking
at read time so revokes take effect, persistence through WAL restarts,
SHOW CREATE VIEW and I_S.VIEWS with correct shapes. Direct projections over
one filtered or unfiltered base table accept INSERT, INSERT ... SELECT,
REPLACE VALUES/SET/SELECT, ODKU, UPDATE, and DELETE
with exposed-column enforcement and definer privilege checks.
`LOCAL` and `CASCADED` CHECK OPTION values are persisted, exposed through
metadata, and enforced on the direct writable subset.
View projections appear in I_S.COLUMNS, DESCRIBE, SHOW COLUMNS, and SHOW TABLE
STATUS. Their metadata is derived from the saved query without evaluating it.

Triggers working: ordered multiple BEFORE/AFTER INSERT/UPDATE/DELETE FOR EACH
ROW triggers with OLD/NEW row images, BEFORE SET NEW assignments,
FOLLOWS/PRECEDES, and `BEGIN ... END` sequences of DML and SET NEW statements;
statement atomicity, 1442 cycle/self-write detection, a depth cap,
definer-based privilege checks per fire, lifecycle maintenance, and SHOW
TRIGGERS/I_S.TRIGGERS metadata. Generated row-image columns and illegal
OLD/NEW images are rejected when the trigger is created.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Updatable-view breadth | joins, expressions, and nested views where MySQL deems the view writable | direct single-table projections with a simple base-table WHERE predicate; insert, ODKU, replace, update, and delete forms map through it | medium | refusal |
| ALGORITHM / SQL SECURITY INVOKER / ALTER VIEW | supported | absent; `InformationSchema.viewsRows` reports SECURITY_TYPE as DEFINER | low | refusal |
| VIEW_DEFINITION rendering | fully-qualified expanded form; SHOW CREATE VIEW wrapped in `/*!50001 */` | `InformationSchema.showCreateView` returns raw user text without the wrapper | low | divergence |
| Trigger DML breadth | triggers fire for every applicable MySQL DML form | single-table DML is covered; REPLACE refuses when DELETE triggers exist, and multi-table UPDATE/DELETE firing remains unsupported | medium | refusal |
| Compound trigger language | BEGIN…END with variables, conditions, handlers, and control flow | ordered DML and SET NEW statement sequences; DECLARE, handlers, IF/CASE/loops, SIGNAL, and dynamic SQL remain absent | medium | refusal |
| Trigger recursion cap | cycle detection at runtime | `Executor.fireTriggers` uses a hardcoded depth of 8 | low | divergence |
| Per-trigger sql_mode/charset capture | stored and applied | `InformationSchema.triggerSqlMode` and the charset fields are server constants | low | divergence |

## 10. Stored routines, events, schedulers

Total absence, honestly surfaced: no CREATE/ALTER/DROP PROCEDURE or FUNCTION,
no CALL, no compound-statement language, no event DDL, no scheduler thread.
`information_schema.ROUTINES/PARAMETERS/EVENTS` and `SHOW PROCEDURE|FUNCTION
STATUS`/`SHOW EVENTS` return correctly-shaped empty results
(`InformationSchema.virtualTableDefs` and the SHOW-status handlers). Execute_priv/Event_priv/
Create_routine_priv columns exist in grant tables but guard nothing.
Impact: medium for applications that install logic server-side (common in
legacy schemas and some migration toolchains); irrelevant to pure ORM clients.

## 11. Full-text search

Working: natural-language, boolean, and query-expansion modes; InnoDB's
exact scoring (TF × IDF² with epsilon floor, oracle-verified against
8.4.11); InnoDB's 36-word default stopword list; boolean operators
`+ - > < ~ word* "phrases" @N proximity ()` with depth cap; blind
relevance-feedback expansion (top 20 docs); implicit relevance ordering for
bare WHERE-MATCH queries; FULLTEXT index DDL, introspection, and
column-set validation; indexed-column collation sensitivity for case,
accents, and binary text; immutable term-frequency postings and row-local
token positions maintained with DML and rebuilt once after snapshot/WAL
recovery; direct WHERE-MATCH candidate streaming by stable row identity.
Boolean evaluation unions only touched postings, prefix terms have maintained
prefix postings, and bounded AND/OR predicate trees intersect or union MATCH
candidates before residual evaluation. Projection-only MATCH retains ordinary
point lookup plans; physical joins score each owning corpus before joining;
single-table UPDATE/DELETE consume the same candidate algebra.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| MATCH planning | optimizer can combine FULLTEXT access with every other access path | bounded AND/OR MATCH predicates stream posting candidates and projection-only MATCH preserves point probes; other projection-only shapes scan their owning corpus | medium (scale) | divergence |
| MATCH scope | any SELECT/UPDATE/DELETE context, joins included | physical SELECT/JOIN sources and single-table UPDATE/DELETE are supported; multi-table UPDATE/DELETE with MATCH remains unsupported | low | refusal |
| Tunables | innodb_ft_min_token_size, innodb_ft_max_token_size, ft_query_expansion_limit, stopword tables, enable/disable | constants in `FullText` fix these at 3 / 84 / 20 / the built-in list | low | divergence |
| CJK | ngram and mecab parsers, WITH PARSER clause | absent; no CJK tokenization | medium (for CJK) | refusal |
| Proximity/prefix details | manual leaves distance semantics open; phrase-prefix via `"word*"`-adjacent forms | `FullText` interprets @N as an N-token window; prefix wildcard attaches to single words only | low | divergence |

## 12. Wire protocol and prepared statements

Working: HandshakeV10 with capability negotiation, CLIENT_DEPRECATE_EOF both
directions, auth-switch to mysql_native_password for caching_sha2 clients,
constant-time credential verification, COM_QUERY/INIT_DB/PING/FIELD_LIST/
QUIT/RESET_CONNECTION, full COM_STMT_PREPARE/EXECUTE/CLOSE/SEND_LONG_DATA/
RESET with type reuse and 1153-on-overflow long-data accounting, text and
binary row encodings including µs-precision temporals and 16 MiB multi-packet
framing, TLS 1.2/1.3 with an optional PEM server certificate and
require_secure_transport, CLIENT_FOUND_ROWS honored, max_allowed_packet/max_connections/
max_prepared_stmt_count enforced with honest advertising, mid-query
disconnect detection cancelling evaluation (`Server.watchForDisconnect`).

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| TLS client authentication | account `REQUIRE SSL`/`REQUIRE X509`, client certificates, certificate reload | server certificate authentication only; no account-level TLS requirement | medium (mutual TLS deployments) | refusal |
| Compression | CLIENT_COMPRESS/ZSTD | never offered | low | refusal |
| Cursors | COM_STMT_EXECUTE CURSOR_TYPE_READ_ONLY + COM_STMT_FETCH | cursor flags are ignored; `Server.handleConnection` returns 1047 for COM_STMT_FETCH | medium (large-result readers) | refusal |
| LOAD DATA LOCAL INFILE | client-streamed file loading | opt-in `local_infile`; UTF-8/utf8mb4, one-character field/line separators, `REPLACE`/`IGNORE`, column lists, and header skipping; no server-file loading, `SET`, user variables, or multibyte separators | low | subset |
| Multi-statement | CLIENT_MULTI_STATEMENTS batching | negotiated COM_QUERY batches and multi-result status flags; COM_SET_OPTION remains unsupported | low | subset |
| Session state tracking | CLIENT_SESSION_TRACK info in OK packets | absent | low | refusal |
| Diagnostics coverage | warnings from conversions, truncation, deprecated syntax, and storage engines | statement errors, ignored INSERT/CHECK rows, non-strict integer/ENUM/SET/charset coercions, DECIMAL scale-loss notes, declared text/binary truncation, and GROUP_CONCAT truncation are captured; other warning producers remain silent | low | divergence |
| Unimplemented COM_* | SET_OPTION, CHANGE_USER | both → ERR 1047 (`Server.fs`) | low | refusal |
| Auth plugins | caching_sha2_password fast/full auth, sha256_password, RSA exchange | mysql_native_password only; `Server.authenticateHandshake` downgrades caching_sha2 clients via auth-switch | low (works, weaker) | divergence |
| Column definition fidelity | schema/table/org_table names, requested charsetnr | `Protocol.columnDefPayload` leaves source names empty and reports collation 45 for text or 63 for binary regardless of the declared collation | low | divergence |
| Column flags | MULTIPLE_KEY, ZEROFILL, NO_DEFAULT_VALUE, ON_UPDATE_NOW, NUM, PART_KEY | `ColumnWire.metadataOfColumn` does not compose them | low | divergence |
| Parameter metadata | STMT_PREPARE_OK carries result columns and typed param defs | `Protocol.stmtPrepareOkPayload` reports zero result columns and generic VAR_STRING `?` parameters | low | divergence |
| Reprepare | automatic reprepare on metadata change | frozen ASTs; stale-metadata edge cases possible | low | divergence |
| System variables | hundreds live | ~30 known; most others inert or absent; time_zone static strings with no conversion | medium | divergence |

## 13. Authentication and privileges

Working: mysql.user with MySQL 8.4's exact 51-column order, root bootstrap,
SHA1-double password hashing with constant-time compare, CREATE/DROP/ALTER
USER and SET PASSWORD, GRANT/REVOKE across global/db/table scopes with
level-shaped denials (1045/1044/1142), GRANT OPTION checked at target level,
fail-closed unknown privileges, DROP USER cleanup across grant tables,
privilege collection recursing through subqueries/derived tables/CTEs,
SHOW DATABASES/TABLES visibility filtering, PROCESS-scoped PROCESSLIST/KILL,
DROP TRIGGER resolved to its subject table for TRIGGER privilege
(`Auth.requiredPrivileges`), persistence through ordinary row operations.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Hostname accounts | forward-confirmed reverse DNS matching | numeric peer addresses plus the loopback `localhost` alias; DNS names are not trusted | low | divergence |
| Text-probe privilege bypass | all statements checked | SET/USE and server-wide SHOW probes bypass the general AST gate; account, process, database, and table metadata probes carry scoped checks | low | divergence |
| Roles | CREATE ROLE, SET ROLE, role grants, mandatory roles | absent | medium | refusal |
| Dynamic privileges | BACKUP_ADMIN, CONNECTION_ADMIN, … | vocabulary absent from GRANT parsing | low | refusal |
| Column-level privileges | mysql.columns_priv enforced | table exists, never consulted | low | divergence |
| Account lock/expiry/resource limits | enforced | columns are present in mysql.user but `Auth` does not consult them | low | divergence |
| Proxy users | supported | absent | low | refusal |
| SHOW GRANTS completeness | includes dynamic-privilege and PROXY lines | `Auth.renderGrantsForAccount` omits them | low | divergence |
| System-table coverage | ~38 mysql.* tables | `Storage.mysqlSystemDatabase` provides 8: user, db, tables_priv, columns_priv, global_grants, triggers, views, check_constraints | low | divergence |

## 14. Metadata, server administration, logging, replication

Working: 23 INFORMATION_SCHEMA views with viewer scoping (SCHEMATA, TABLES,
COLUMNS (including column comments), STATISTICS, TABLE_CONSTRAINTS, KEY_COLUMN_USAGE,
REFERENTIAL_CONSTRAINTS, CHECK_CONSTRAINTS, VIEWS, TRIGGERS, PROCESSLIST,
ENGINES, COLLATIONS, CHARACTER_SETS, privilege views, …), direct
SELECT-ability of the 8 mysql.* tables, SHOW TABLES/COLUMNS/INDEX/CREATE
TABLE/CREATE VIEW/TABLE STATUS (real byte accounting)/ENGINES/CHARACTER SET/
COLLATION/PRIVILEGES (73 oracle-verified rows)/PROCESSLIST/VARIABLES/STATUS/
GRANTS/TRIGGERS/WARNINGS/ERRORS with statement condition counts, DESCRIBE,
ALTER TABLE DISABLE/ENABLE KEYS
no-op for mysqldump, my.cnf parsing ([mysqld]/[server], loose- prefix,
!include with depth cap), KILL QUERY/CONNECTION with PROCESS/SUPER checks,
live Limits reporting.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| INFORMATION_SCHEMA breadth | ~60+ views incl. INNODB_*, COLUMN_STATISTICS, RESOURCE_GROUPS, ENABLED_ROLES | 23 views; EVENTS/ROUTINES/PARAMETERS/COLUMN_PRIVILEGES genuinely empty | low | divergence |
| Table statistics | estimates refreshed by ANALYZE TABLE | `InformationSchema.tablesRows` reports InnoDB, a 16384 DATA_LENGTH stand-in, CARDINALITY 0, and live row counts where MySQL keeps stale page estimates until ANALYZE | low | divergence |
| SHOW STATUS counters | Com_*, Innodb_*, Slow_queries, … | live Questions, TLS, connection, uptime, and Com_select/insert/update/delete/replace counters; engine and latency families remain absent (`InformationSchema.fs`) | low | divergence |
| wait_timeout | 28800 default | 300 (deliberate DoS posture, honestly advertised) | low | divergence |
| Logging | general log, slow log, error-log file | stderr diagnostics with credential redaction only (`Log.fs`) | low | divergence |
| Replication | binlog, GTID, source/replica channels | nothing; REPLICATION privileges are vocabulary only; internal WAL is not a binlog | architectural | refusal |
| net_read_timeout | configurable | does not exist | low | divergence |

## 15. Differential-testing findings and reruns (torture harness)

Recorded in `torture/findings/` and not enrolled in
`support/known-gaps.json`; status distinguishes current ceilings from evidence
that predates the implementation it measured:

| Finding | Detail | Status |
|---|---|---|
| Multi-database scaling | the historical campaign found super-serial slowdowns and 1205s before the storage-concurrency rewrite (`2026-08-17-multidb-concurrency-campaign.md`) | stale evidence; rerun required |
| Numeric error shape | 1690 message lacks the offending expression text (`2026-08-19-probe-corpus-triage.md`) | ponytail ceiling |
| Temporal/error-shape ceilings | `DATE 'bad'` → 1064 vs MySQL 1525; parenthesized set-op groups `(A UNION B) INTERSECT C` refused | ponytail ceilings |

Uncovered torture lanes (harness scope, not product gaps): durability/restart
during concurrent commits, matched negative semantic-oracle campaigns, connection
churn mid-transaction, cancellation while queued, savepoints under
concurrency, isolation levels other than REPEATABLE READ, concurrent
CREATE/DROP DATABASE under traffic.

## 16. Deliberate divergences (accepted, not targeted for parity)

Documented or ponytail-marked design decisions that differ from MySQL on
purpose: wait_timeout 300; no option-file auto-discovery; join candidate cap
of 1M rows; residual SET/USE/server-wide SHOW text-probe privilege bypass;
VECTOR type and function family (a MySQL 9 forward-port, absent from 8.4 —
purely additive); live statistics values instead of ANALYZE-stale estimates;
ICU CLDR collation tailoring; SUPER required for foreign KILL; honest
advertising of enforced limits (wal_rotate knobs unreported rather than
fabricated); empty routine/event catalogs rather than stubs; and an explicitly
trusted data directory whose CRCs detect corruption rather than authenticate a
hostile local writer.

## 17. Historical records with resolved entries

These dated findings remain useful as campaign records, but include behavior
that later work changed:

- `torture/findings/2026-08-19-json-table-gaps.md` says NESTED PATH, EXISTS
  PATH, and DEFAULT clauses do not parse; waves W3/W4 shipped them. The
  remaining true items are ERROR ON EMPTY/ERROR, LEFT JOIN … ON TRUE, and
  JOIN…USING refusals.
- The client-contract campaign's four result-type signatures were resolved;
  the 2026-08-21 differential rerun passed every scenario.
- The 2026-08-17 multi-database campaign predates the sharded database cells,
  paged row store, and row-striped point updates. Its correctness evidence is
  retained, but its scaling classification needs a fresh run.
- The 2026-08-24 adversarial security report was retired after parser-depth,
  oversized-packet, metadata-privilege, bounded-logging, and idempotent-GRANT
  fixes landed. Data-directory write access remains the explicit trust boundary
  recorded in README and section 16.
- The constraints audit found PRIMARY KEY columns were rendered as implicitly
  NOT NULL but remained nullable in storage. New schemas normalize the flag,
  old persisted schemas are guarded at coercion, and ADD PRIMARY KEY validates
  both NULL and duplicate existing values.

## 18. Relative severity view

Ranked by expected disruption to the primary consumers, independent of
implementation effort:

1. Join reordering, correlated-subquery planning, and access paths outside the
   fully-bound composite cases — correctness holds, but scale still diverges
   from MySQL past small data.
2. READ UNCOMMITTED and intra-database transaction publication. Serializable
   correctness is covered conservatively, without InnoDB's locking behavior.
3. Complex/nested updatable views and the stored-program control language
   inside trigger bodies. Ordered multi-trigger slots and sequential compound
   DML bodies are covered.
4. Spatial indexes, overlay/buffer operations, and geographic SRS behavior.
   The common planar topology family includes equality and convex hull.
5. Replication, logging, broad engine counters, and the remaining metadata
   tail. Core command counters are live; replication remains architectural.
