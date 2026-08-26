# MySQL 8.4 feature gaps

A map of where fsdb diverges from or lacks MySQL 8.4 functionality. Oracle for
every row is real MySQL 8.4 (never sqlite). Audit date: 2026-08-25, based on a
full static exploration of `src/Fsdb/` plus the documented records
(`docs/compatibility.md`, `torture/findings/`, `torture/support/known-gaps.json`,
`benchmarks/results/`) and the adversarial parser, wire, privilege, logging,
and persistence paths. Evidence anchors name files and definitions instead
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
| SQL statements | Broad core; large admin/programmatic tail missing | Compound stored programs and replication/admin SQL |
| Query execution | Composite equality/range access, bounded index ordering, restricted join reordering, and stable/correlated index probes | General cost-based planning and broader correlated forms |
| Built-in functions | Broad scalar, aggregate, JSON, time, and common planar geometry coverage | Overlays, non-point buffers, and geographic SRS semantics |
| Data types | Common scalar types, BIT fields, signed TIME durations, and OGC geometry | Spatial indexes and operations |
| Constraints & indexes | PK/UNIQUE/FK/CHECK plus composite equality, inner-join, PK/unique/secondary range, grouping, and bounded index-order probes | Outer-join, unconstrained composite ordering, and broader grouping paths still scan |
| Charsets & collations | ICU-based utf8mb4 registry | Weight-table tailoring differs from MySQL's UCA tables |
| Transactions | Repeatable-read snapshots, nonlocking read-committed views, conservative serializable validation, and optimistic row-version merge | READ UNCOMMITTED is refused |
| Persistence | WAL + snapshot, crash-tested, with bounded group commit | Opt-in only; row tombstones are reclaimed during bounded foreground compaction rather than by a background purge worker |
| Views & triggers | Direct updatable views with all insert/replace forms; ordered BEFORE/AFTER INSERT/UPDATE/DELETE triggers and compound DML bodies | Complex views and the stored-program control language |
| Routines & events | Zero-parameter, single-statement procedures and one-time event declarations | Compound stored programs and event scheduling |
| Full-text | Oracle-verified scoring over maintained inverted indexes | Single-table SELECT only; no CJK parser |
| Wire protocol | Handshake through COM_STMT_FETCH, TLS, zlib compression, LOCAL INFILE, and multi-result batches | No session-state tracking |
| Auth & privileges | Static privileges enforced incl. subqueries, per-host accounts, account locks, and role accounts | No role activation/inheritance or dynamic/column privileges |
| Metadata | 23 INFORMATION_SCHEMA views, 10 mysql.* tables, and core live command counters | Storage statistics are stand-ins; many SHOW forms missing |
| Server admin | KILL, SHUTDOWN, limits, config file parsing | No replication/binlog/logging files |

## 1. SQL statements and parser

Working core: full DML (INSERT/REPLACE/UPDATE/DELETE incl. INSERT/REPLACE SET
and multi-table forms,
ODKU, IGNORE), SELECT with joins (INNER/LEFT/RIGHT/CROSS/NATURAL/USING),
derived/LATERAL/JSON_TABLE sources, expression subqueries over set operations,
query-scoped CTEs (ordinary and recursive,
including leading UPDATE/DELETE and branch-local WITH), set
operations, window functions with numeric and temporal interval frames, GROUP BY WITH ROLLUP + GROUPING,
DDL for databases/tables/indexes/views/triggers/users/grants, CREATE TABLE AS
SELECT, TRUNCATE,
RENAME TABLE, EXPLAIN (TRADITIONAL/JSON/ANALYZE). Transaction control, SET, SHOW (~25
variants), USE, KILL, DESCRIBE are text-probed before the grammar
(`QueryHandler.dispatch`).

### Statements MySQL 8.4 parses that fsdb refuses (no grammar, no probe)

| Statement family | Impact | Class |
|---|---|---|
| Zero-parameter procedures accept a direct statement or a single statement wrapped in `BEGIN…END`; parameters, functions, definer-context execution, multi-statement bodies, `DECLARE`, cursors, handlers, `SIGNAL`/`GET DIAGNOSTICS` remain absent | medium | divergence/refusal |
| Session-scoped `CREATE/DROP TEMPORARY TABLE` and temporary-table shadowing | medium | refusal |
| One-time and recurring `CREATE/DROP EVENT` declarations and metadata are supported; ALTER, status changes, definer execution, and the scheduler thread remain absent | low | divergence/refusal |
| Server-side `LOAD DATA INFILE`; `SELECT … INTO OUTFILE/DUMPFILE`; `IMPORT TABLE` | medium | refusal |
| `CHECKSUM TABLE` returns a stable fsdb row checksum rather than MySQL's storage-engine-specific value; specialized FLUSH forms remain absent | low | divergence/refusal |
| `LOCK TABLES…READ/WRITE` and `UNLOCK TABLES` are accepted without mutual exclusion or access restriction; `HANDLER` and XA transactions remain absent | low | divergence/refusal |
| `ALTER TABLE` accepts `ALGORITHM` and `LOCK` execution hints but does not enforce the requested online-DDL strategy | low | divergence |
| Partitioning: HASH declarations are accepted as logical table options; partition metadata, pruning, `PARTITION (p)` selection, and `ADD/DROP/COALESCE/REORGANIZE PARTITION` remain absent | medium | divergence/refusal |
| `CREATE/DROP ROLE` are backed by locked `mysql.user` accounts and `SET ROLE NONE` clears the empty active-role set; other `SET ROLE`, `SET DEFAULT ROLE`, role grants/inheritance, dynamic privileges (`BACKUP_ADMIN`…), and `GRANT PROXY` remain absent | medium | divergence/refusal |
| Replication/admin SQL: `CHANGE REPLICATION SOURCE TO`, `PURGE BINARY LOGS`, `RESET`, `BINLOG`, `INSTALL/UNINSTALL PLUGIN|COMPONENT`, `ALTER INSTANCE`, `CREATE SERVER`, `TABLESPACE` statements | low | refusal |
| `EXPLAIN FORMAT=JSON/TREE` report the logical access plan without MySQL's cost model; `EXPLAIN ANALYZE` reports aggregate runtime/cardinality rather than per-iterator observations | low | divergence |
| `CREATE USER … ACCOUNT LOCK/UNLOCK` is enforced; auth-plugin selection, `REQUIRE SSL/X509`, resource limits, `PASSWORD EXPIRE`, and `ALTER USER` beyond password change remain absent | medium | refusal |

### SELECT-level syntax gaps

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Locking detail | `FOR UPDATE/SHARE [OF tbl…] [NOWAIT|SKIP LOCKED]` | accepted and ignored, including `LOCK IN SHARE MODE`; no transaction-held read locks | low | divergence |
| Executable-comment edge cases | versioned comment bodies follow the server grammar at each token boundary | ordinary `/*!NNNNN … */` splicing works; empty and densely adjacent executable comments can still be accepted or rejected differently | low | divergence |
Expression coverage that does exist: full comparison/logical/arithmetic
operators incl. `<=>`, row-value comparisons and `IN`, `XOR`, three-valued logic; CASE (both forms);
CAST/CONVERT; EXISTS/IN/ANY/SOME/ALL/BETWEEN/LIKE [ESCAPE]/REGEXP; `->`/`->>` JSON
operators; charset introducers; hex literals; typed temporal literals;
`INTERVAL n unit`; MATCH…AGAINST; collation postfix; version-comment
splicing `/*!NNNNN … */`; and MySQL's single-row `FROM DUAL` source.

## 2. Query execution

Working: hash joins for equi-joins with collation-folded keys, lazy nested
loops otherwise, statement-stable scalar/EXISTS/IN/ANY/SOME/ALL subqueries
materialized once per statement, exact-integer `IN` materializations with a
reusable membership set and direct indexed outer-table narrowing, correlated
scalar/EXISTS/IN/ANY/SOME/ALL
subqueries with correct NULL
semantics, statement-local equality buckets for direct physical inner tables,
bounded top-N sort for ORDER BY+LIMIT, GROUP_CONCAT byte cap,
WITH ROLLUP expansion, window frames (ROWS/RANGE, numeric and temporal interval offsets),
COUNT(DISTINCT a,b) tuples, statement-atomic multi-table DML, exact ODKU
affected-rows semantics (changed=2/no-op=0 under default flags), row-value
equality/order/null-safe comparisons and literal/subquery `IN`, MySQL's 1241
error for multi-column scalar/IN/ANY/SOME/ALL subqueries, and the empty-group
identities for bit aggregates.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Secondary-index access paths | ref/eq_ref/range scans feed joins, DML, ORDER BY, GROUP BY | fully-bound composite equality probes and matching physical inner joins use B-tree buckets; direct literal ranges feed single-table SELECT/UPDATE/DELETE through primary, unique, and secondary indexes; bounded `ORDER BY`/`GROUP BY` can stream a matching composite index when preceding keys are fixed; outer joins, unconstrained multi-key ordering, and broader grouping still scan/sort | high (scale) | divergence |
| Optimizer | pushdown, constant folding, join reordering, cost model, statistics | qualified physical inner-join stars choose ready indexed sources by cardinality and push qualified base-table ranges into the initial scan, while `STRAIGHT_JOIN` preserves written order; outer/lateral/derived joins and statements with name-resolution-sensitive unqualified references retain source order; broader pushdown, statistics, and a general cost model remain absent | medium | divergence |
| EXPLAIN fidelity | type ∈ system/const/eq_ref/ref/range/index/ALL; FORMAT=JSON/TREE; ANALYZE; optimizer_trace | access types cover compatible direct bounds/orderings; JSON/TREE plans and aggregate ANALYZE observations work, while per-iterator timing/costs and optimizer_trace remain absent | low | divergence |
| Subquery strategies | semi-join/materialization/early-exit transformations | statement-stable scalar/IN/ANY/SOME/ALL/EXISTS subqueries materialize once; exact-integer `IN` reuses an ordered membership set and narrows a direct indexed physical outer table; simple EXISTS stops at one row; direct correlated equalities use persistent indexes or a statement-local canonical-key lookup over a physical inner table; string/decimal and compound semi-joins remain scans, while other correlated, variable-bearing, nondeterministic, CTE, derived, lateral, and JSON_TABLE forms re-execute | medium (scale) | divergence |
| Join size ceiling | unbounded (memory-bound) | `Executor.maxJoinCandidateRows` caps candidate rows at 1,000,000 → error 1105 | medium | divergence |
| MATCH…AGAINST placement | evaluates in UPDATE/DELETE WHERE, joins, subqueries | physical SELECT/JOIN sources and single-table UPDATE/DELETE are supported; multi-table UPDATE/DELETE with MATCH remains unsupported | low | refusal |
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

Working: TINYINT–BIGINT signed/unsigned, DECIMAL(p,s) with fixed-point
round-trip, FLOAT/DOUBLE with MySQL exponent rendering, CHAR/VARCHAR,
TINYTEXT–LONGTEXT, BINARY/VARBINARY, TINYBLOB–LONGBLOB, ENUM/SET with
canonicalization, DATE/DATETIME(fsp)/TIMESTAMP(fsp)/TIME(fsp) with half-up
fsp rounding and carry cases, all-zero and partial-zero dates with sql_mode
enforcement, YEAR, JSON, per-column charset/collation,
wire-faithful column metadata (`ColumnWire.metadataOfType`), `BIT(1)`–`BIT(64)`
fields with binary literals and defaults, per-row functional defaults with
column references, and OGC WKB geometry values
(`GEOMETRY`, concrete spatial types, WKT/WKB construction and common
accessors).

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Spatial indexes and operations | R-tree indexes, overlays, general buffers, geographic SRS axis rules | geometry values, common WKT/WKB accessors, planar point `ST_Buffer`, `ST_Distance`, `ST_Envelope`, topology predicates, and MBR predicates work; spatial indexes still collapse to BTree | low | refusal |
| Generated columns | VIRTUAL recomputed on read, STORED materialized | `Executor.recomputeGeneratedColumns` materializes both at write time; no read-path recompute | low | divergence |
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
| Non-unique secondary indexes | physical structures serving lookups/ordering | separate immutable equality buckets and ordered entries serve fully-bound composite equality, matching physical inner-join keys, direct literal SELECT/UPDATE/DELETE ranges, compatible grouping, and bounded composite index ordering; duplicate structures deliberately trade memory and write work for point probes plus bounded seeks; outer joins and unconstrained ordering remain scans | high (scale) | divergence |
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
point-update fast path for disjoint writes, wait-and-rebase coordination for
indexed point/range UPDATE and DELETE statements, incremental unique-index
validation and merged-result FK revalidation, savepoints with MySQL establishment-order semantics,
autocommit implicit transactions, read-only transactions never blocking
writers, per-database sharding so cross-database writers never contend,
4,096-stripe row ownership, and InnoDB-style burned AUTO_INCREMENT on rollback.
`SERIALIZABLE` uses conservative whole-catalog validation for writing
transactions, preventing write skew while keeping read-only transactions
lock-free.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| SERIALIZABLE locking behavior | predicate/gap locks and blocking reads | conservative snapshot validation rejects any intervening catalog change with 1205 when the transaction writes; read-only transactions retain snapshot semantics | low | divergence |
| READ COMMITTED | a fresh nonlocking read view per statement | a fresh committed view plus the transaction's own successful writes per parsed statement; locking reads remain unsupported | medium | partial |
| READ UNCOMMITTED | dirty reads | refused with 1235 | medium | refusal |
| Deadlock errors | 1213 deadlock detection with victim selection | waits honor `innodb_lock_wait_timeout` and return 1205; cycles are not detected or assigned a 1213 victim | low | divergence |
| Write parallelism within a database | row-lock concurrency | indexed autocommit and transaction UPDATE/DELETE paths coordinate row stripes; full-scan, CTE, multi-table, and insert/upsert transaction writes still rely on optimistic merge; publishing a new immutable database root remains one brief per-database critical section, and durable commit events are sequenced | medium (throughput) | partial |
| Multi-database scaling | near-linear with connections | database roots and row-lock stripes are sharded; a 4-database/8-worker campaign completed in 0.49x its serial projection, while an 8-database/16-worker CPU-saturated campaign reached 1.06x | medium | partial |
| Cross-database snapshots | linearizable catalog reads | the `Store.Catalog` projection is explicitly not atomic across databases mid-commit | low | divergence |

## 8. Persistence and durability

Working: opt-in `--data-dir` mode with CRC-framed WAL ([len][crc32] records
over CommitEvent payloads, torn-tail truncation), self-delimiting CRC'd
snapshots, libc fsync-before-ack with FailFast on failure, directory fsync
after rename, `.new` snapshot verification before preference, replay that
bypasses checked write paths with ordered change application and incremental
derived-index maintenance, bounded group commit, ordered checkpoint barriers,
rotation via a lock-step replica store, signal-driven final rotation,
decode-depth caps, codecs for every column type including generated-column
expressions.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Durability default | durable unless configured otherwise | in-memory unless `--data-dir` passed; process death loses everything | medium (deployment) | divergence |
| Keyless WAL row lookup | redo addresses physical records directly | replay resolves rows through unique indexes when possible; events on tables without a usable unique key use one ordered table pass because the WAL stores row images rather than row ids | low (recovery and durable keyless-write throughput) | divergence |
| Space reclamation | purge threads reclaim deleted rows | Delete-heavy tables compact immutable row roots after at least 256 tombstones occupy one quarter of physical slots; reclamation is foreground and occasionally scans one table root | low | divergence |
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
Direct single-table projections with a static predicate merge into the outer
SELECT so physical equality, range, and ordered-limit paths remain available;
other view shapes materialize once per statement.

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

Working: zero-parameter procedures with one parsed statement body, optionally
wrapped in `BEGIN…END`, support
CREATE/DROP/CALL, SHOW CREATE PROCEDURE, SHOW PROCEDURE STATUS, and persisted
ROUTINES metadata. One-time and recurring event declarations support CREATE/DROP, SHOW
CREATE EVENT, SHOW EVENTS, and persisted EVENTS metadata. CREATE ROUTINE,
ALTER ROUTINE, EXECUTE, and EVENT privileges guard their corresponding paths.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Routine language | parameters, functions, compound bodies, local variables, handlers, cursors, control flow, dynamic SQL | zero-parameter procedures with one statement body, directly or inside `BEGIN…END` | medium | refusal |
| Routine execution context | SQL SECURITY DEFINER/INVOKER and stored sql_mode/charset | procedure body executes with the caller's session context | medium | divergence |
| Event scheduler | recurring and one-time schedules execute in a scheduler thread | declarations and schedule metadata persist, but no event is scheduled or executed | medium | refusal |
| Event alteration | ALTER EVENT schedule/status/body/rename | absent | low | refusal |

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
QUIT/RESET_CONNECTION, full COM_STMT_PREPARE/EXECUTE/FETCH/CLOSE/SEND_LONG_DATA/
RESET with read-only cursors, type reuse, and 1153-on-overflow long-data accounting, text and
binary row encodings including µs-precision temporals and 16 MiB multi-packet
framing, zlib CLIENT_COMPRESS transport, TLS 1.2/1.3 with an optional PEM server certificate and
require_secure_transport, CLIENT_FOUND_ROWS honored, max_allowed_packet/max_connections/
max_prepared_stmt_count enforced with honest advertising, mid-query
disconnect detection cancelling evaluation (`Server.watchForDisconnect`).

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| TLS client authentication | account `REQUIRE SSL`/`REQUIRE X509`, client certificates, certificate reload | server certificate authentication only; no account-level TLS requirement | medium (mutual TLS deployments) | refusal |
| Compression | CLIENT_COMPRESS/ZSTD | CLIENT_COMPRESS zlib framing is negotiated; Zstandard is not offered | low | subset |
| Cursor storage | materialized temporary tables spill from memory to disk | read-only, forward-only cursors retain their materialized rows in session memory until exhaustion, reset, close, or commit | low (large concurrent cursors) | divergence |
| LOAD DATA LOCAL INFILE | client-streamed file loading | opt-in `local_infile`; UTF-8/utf8mb4, one-character field/line separators, `REPLACE`/`IGNORE`, column lists, and header skipping; no server-file loading, `SET`, user variables, or multibyte separators | low | subset |
| Multi-statement | CLIENT_MULTI_STATEMENTS batching | negotiated COM_QUERY batches, multi-result status flags, and COM_SET_OPTION toggling | low | covered |
| Session state tracking | CLIENT_SESSION_TRACK info in OK packets | absent | low | refusal |
| Diagnostics coverage | warnings from conversions, truncation, deprecated syntax, and storage engines | statement errors, ignored INSERT/CHECK rows, non-strict integer/ENUM/SET/charset coercions, DECIMAL scale-loss notes, declared text/binary truncation, and GROUP_CONCAT truncation are captured; other warning producers remain silent | low | divergence |
| Unimplemented COM_* | CHANGE_USER | returns ERR 1047 (`Server.fs`) | low | refusal |
| Auth plugins | caching_sha2_password fast/full auth, sha256_password, RSA exchange | mysql_native_password only; `Server.authenticateHandshake` downgrades caching_sha2 clients via auth-switch | low (works, weaker) | divergence |
| Column definition fidelity | schema/table/org_table names, requested charsetnr | direct physical COM_QUERY/COM_STMT_PREPARE columns and COM_FIELD_LIST report source names; declared expressions and text-probed resultsets report their effective MySQL collation ids; view, derived, and UNION source names remain empty | low | partial |
| Column flags | MULTIPLE_KEY, ZEROFILL, NO_DEFAULT_VALUE, ON_UPDATE_NOW, NUM, PART_KEY | numeric, temporal, required-default, primary/unique key-part, and implicit YEAR ZEROFILL flags are reported; non-unique secondary membership and an explicit ZEROFILL declaration are not represented in `ColumnDef` | low | divergence |
| Prepared metadata | STMT_PREPARE_OK carries result columns and typed parameter definitions | result columns, schema/operator/DML contexts, and common numeric, temporal, JSON, and spatial built-in arguments are derived statically without evaluating the statement; less-common overloaded built-ins and registered extensions without declared signatures remain generic VAR_STRING | low | divergence |
| Reprepare | automatic reprepare on metadata change | prepared ASTs resolve tables, columns, views, and result metadata from the live schema on each execution, yielding the same observable schema-change behavior without recompiling SQL text | low | aligned for supported syntax |
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
| Roles | CREATE ROLE, SET ROLE, role grants, mandatory roles | CREATE/DROP ROLE persist locked accounts and `SET ROLE NONE` succeeds; activation of named roles, grants, inheritance, and mandatory roles are absent | medium | divergence/refusal |
| Dynamic privileges | BACKUP_ADMIN, CONNECTION_ADMIN, … | vocabulary absent from GRANT parsing | low | refusal |
| Column-level privileges | mysql.columns_priv enforced | table exists, never consulted | low | divergence |
| Account lock/expiry/resource limits | enforced | account locks are enforced; expiry and resource limits are not | low | divergence |
| Proxy users | supported | absent | low | refusal |
| SHOW GRANTS completeness | includes dynamic-privilege and PROXY lines | `Auth.renderGrantsForAccount` omits them | low | divergence |
| System-table coverage | ~38 mysql.* tables | `Storage.mysqlSystemDatabase` provides the account/grant, trigger/view/constraint, routine, and event catalogs used by supported features | low | divergence |

## 14. Metadata, server administration, logging, replication

Working: 23 INFORMATION_SCHEMA views with viewer scoping (SCHEMATA, TABLES,
COLUMNS (including column comments), STATISTICS, TABLE_CONSTRAINTS, KEY_COLUMN_USAGE,
REFERENTIAL_CONSTRAINTS, CHECK_CONSTRAINTS, VIEWS, TRIGGERS, PROCESSLIST,
ENGINES, COLLATIONS, CHARACTER_SETS, privilege views, …), direct
SELECT-ability of the 10 mysql.* tables, SHOW TABLES/COLUMNS/INDEX/CREATE
TABLE/CREATE VIEW/TABLE STATUS (real byte accounting)/ENGINES/CHARACTER SET/
COLLATION/PRIVILEGES (73 oracle-verified rows)/PROCESSLIST/VARIABLES/STATUS/
GRANTS/TRIGGERS/WARNINGS/ERRORS with statement condition counts, DESCRIBE,
ALTER TABLE DISABLE/ENABLE KEYS
no-op for mysqldump, my.cnf parsing ([mysqld]/[server], loose- prefix,
!include with depth cap), KILL QUERY/CONNECTION with PROCESS/SUPER checks,
live Limits reporting.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| INFORMATION_SCHEMA breadth | ~60+ views incl. INNODB_*, COLUMN_STATISTICS, RESOURCE_GROUPS, ENABLED_ROLES | 23 views; ROUTINES and EVENTS expose supported declarations, while PARAMETERS and COLUMN_PRIVILEGES remain empty | low | divergence |
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
| Planner/CTE syntax | two deterministic depth-three campaigns (2,000 and 10,000 mutations) exposed unconditional INNER JOIN, eager unused-CTE, and incomplete MATCH grammar differences; fixed campaigns now pass with zero differences | resolved 2026-08-25 |
| Executable gap baselines | The corpus grew from 62 to 70 MySQL-accepted baselines. A native MySQL 8.4.11 rerun passed 64 and reproduced exactly the six intentional findings: procedure parameters/bodies, account requirements, READ UNCOMMITTED, and partition selection/maintenance. `--syntax-cases 0` runs this inventory without mutations | oracle-verified 2026-08-25 |
| Depth-three syntax stress | 10,000 deterministic mutations plus 70 baselines produced no crash, timeout, protocol fault, or invariant failure. The 133 differences cluster in the six declared feature gaps and executable-comment/error-contract edges; account cleanup covers both default and empty-host identities | compatibility differences retained; harness cleanup fixed 2026-08-25 |
| Same-row transaction contention | the original 32-worker/16-hot-account campaign produced 2,541 fsdb 1205 conflicts. Row-delta publication removed whole-table copy/reindex work: a 32x100 rerun completed all 3,200 prepared transactions with exact parity and zero failures at 1,116 fsdb tx/s versus 2,805 MySQL tx/s, with p99 85 ms versus 38 ms. Deferring the private catalog until the first database statement raised the eight-worker campaign from 1,643 to 1,967 fsdb tx/s; MySQL reached 5,618 | correctness resolved; constant-factor and high-contention performance open |
| Multi-database scaling | single-capture snapshots, deferred transaction catalogs, and per-database lock namespaces prevent cross-database conflicts. After row-delta publication, an 8x16 campaign fell from 38.8s to 8.6s and preserved all 12,800 outcomes, but remained 0.95x its serial projection instead of the harness target ≤0.75; allocation, GC, and 128-session scheduling now dominate rather than a shared database gate | correctness resolved; high-fan-out performance open |
| Crash/restart durability | concurrent two-table transactions were interrupted by 60 forced process crashes across three 16-worker campaigns. Recovery retained every acknowledged commit, exposed no partial transaction, invented no row, and preserved identical state through graceful snapshot restarts; the runs recovered 2,298, 2,064, and 2,008 complete operations | resolved 2026-08-25; broader snapshot-rotation volume remains useful stress coverage |
| Planner slope rerun | a focused 10k/50k ShortRun measured the indexed join at 371 µs versus MySQL's 178 µs and uncorrelated `IN` at 534 µs versus 155 µs, replacing the historical 4.49 ms and 103 ms fsdb results. Streaming common aggregate folds reduced `GROUP BY` from 98.2 ms to 77.1 ms versus MySQL's 20.6 ms; allocation-free order comparison reduced the representative 50k-row window from 354 ms to 315 ms versus 50.1 ms. Window peer bounds are now linear rather than rescanned per row | join/subquery cliffs and quadratic peer scans resolved; aggregate/window constants open |
| Set-operation syntax ceiling | parenthesized set-op groups `(A UNION B) INTERSECT C` are refused | ponytail ceiling |

Uncovered torture lanes (harness scope, not product gaps): matched negative
semantic-oracle campaigns, connection
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
fabricated); declaration-only events rather than a scheduler; and an explicitly
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

1. General cost-based planning beyond qualified physical inner joins, plus
   correlated forms that cannot use a direct physical-table equality lookup. Correctness holds,
   but scale still diverges from MySQL past small data.
2. Transaction scheduling and READ UNCOMMITTED. Indexed point/range UPDATE and
   DELETE statements wait and rebase, but deadlock victim selection, dirty
   reads, and the remaining transaction write shapes are not implemented.
3. Complex/nested updatable views and the stored-program control language
   inside trigger bodies. Ordered multi-trigger slots and sequential compound
   DML bodies are covered.
4. Spatial indexes, overlay/buffer operations, and geographic SRS behavior.
   The common planar topology family includes equality and convex hull.
5. Replication, logging, broad engine counters, and the remaining metadata
   tail. Core command counters are live; replication remains architectural.
