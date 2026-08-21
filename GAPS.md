# MySQL 8.4 feature gaps

A map of where fsdb diverges from or lacks MySQL 8.4 functionality. Oracle for
every row is real MySQL 8.4 (never sqlite). Audit date: 2026-08-21, based on a
full static exploration of `src/Fsdb/` plus the documented records
(`docs/compatibility.md`, `torture/findings/`, `torture/support/known-gaps.json`,
`docs/performance-*.md`). Line references are evidence anchors and drift as the
code moves.

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
| Query execution | Correct but planless | No index-based join access; subqueries re-run per outer row |
| Built-in functions | ~170 implemented | AES crypto, time arithmetic, half the JSON surface |
| Data types | All common types | Signed-int range unenforced; no TIME value domain, BIT, or geometry |
| Constraints & indexes | PK/UNIQUE/FK/CHECK enforced | Non-unique secondary indexes are metadata only |
| Charsets & collations | ICU-based utf8mb4 registry | Weight-table tailoring differs from MySQL's UCA tables |
| Transactions | Snapshot + optimistic merge | SERIALIZABLE refused; no intra-database write parallelism |
| Persistence | WAL + snapshot, crash-tested | Opt-in only; no group commit; tombstones never reclaimed |
| Views & triggers | Read-only views; AFTER INSERT triggers | No DML through views; no BEFORE/UPDATE/DELETE triggers, no OLD.* |
| Routines & events | Absent (catalogs honestly empty) | Everything |
| Full-text | Oracle-verified scoring | No inverted index; single-table SELECT only; no CJK parser |
| Wire protocol | Handshake through COM_STMT_EXECUTE solid | No TLS, compression, cursors, LOAD DATA, multi-statement |
| Auth & privileges | Static privileges enforced incl. subqueries | Name-only host matching; no roles/dynamic/column privileges |
| Metadata | 23 INFORMATION_SCHEMA views, 8 mysql.* tables | Storage statistics are stand-ins; many SHOW forms missing |
| Server admin | KILL, limits, config file parsing | No replication/binlog/logging files; no SHUTDOWN statement |

## 1. SQL statements and parser

Working core: full DML (INSERT/REPLACE/UPDATE/DELETE incl. INSERT/REPLACE SET
and multi-table forms,
ODKU, IGNORE), SELECT with joins (INNER/LEFT/RIGHT/CROSS/NATURAL/USING),
derived/LATERAL/JSON_TABLE sources, CTEs (top-level, recursive), set
operations, window functions with frames, GROUP BY WITH ROLLUP + GROUPING,
DDL for databases/tables/indexes/views/triggers/users/grants, TRUNCATE,
RENAME TABLE, EXPLAIN (TRADITIONAL). Transaction control, SET, SHOW (~25
variants), USE, KILL, DESCRIBE are text-probed before the grammar
(`QueryHandler.fs:1248–1334`).

### Statements MySQL 8.4 parses that fsdb refuses (no grammar, no probe)

| Statement family | Impact | Class |
|---|---|---|
| `CREATE/ALTER/DROP PROCEDURE|FUNCTION`, `CALL`, compound `BEGIN…END` bodies, `DECLARE`, cursors, handlers, `SIGNAL`/`GET DIAGNOSTICS` | medium | refusal |
| `CREATE/ALTER/DROP EVENT` (+ scheduler thread) | low | refusal |
| `PREPARE`/`EXECUTE`/`DEALLOCATE PREPARE` as SQL text (wire-level COM_STMT_PREPARE works) | low | refusal |
| `LOAD DATA [LOCAL] INFILE`; `SELECT … INTO OUTFILE/DUMPFILE/@vars`; `IMPORT TABLE` | medium | refusal |
| `ANALYZE/OPTIMIZE/CHECK/CHECKSUM/REPAIR TABLE`; any `FLUSH` beyond PRIVILEGES | low | refusal |
| `LOCK TABLES…READ/WRITE`, `UNLOCK TABLES`, `DO`, `HANDLER`, XA transactions | low | refusal |
| Partitioning: `PARTITION BY`, `PARTITION (p)` selection, `ADD/DROP/COALESCE/REORGANIZE PARTITION` | medium | refusal |
| Roles: `CREATE/DROP ROLE`, `SET ROLE`, `SET DEFAULT ROLE`, `GRANT role TO user`, dynamic privileges (`BACKUP_ADMIN`…), `GRANT PROXY` | medium | refusal |
| Replication/admin SQL: `CHANGE REPLICATION SOURCE TO`, `PURGE BINARY LOGS`, `RESET`, `SHOW MASTER/REPLICA STATUS`, `SHOW BINARY LOGS`, `BINLOG`, `INSTALL/UNINSTALL PLUGIN|COMPONENT`, `ALTER INSTANCE`, `CREATE SERVER`, `TABLESPACE` statements | low | refusal |
| `EXPLAIN ANALYZE`; `EXPLAIN FORMAT=JSON|TREE`; `DESCRIBE <statement>` | low | refusal |
| `CREATE TABLE … AS SELECT` and `CREATE TABLE … LIKE other` | medium | refusal |
| `ALTER TABLE … ALTER COLUMN c SET/DROP DEFAULT`; `RENAME INDEX a TO b`; `CONVERT TO CHARACTER SET`; `ENGINE=`/`COMMENT=` option tails (only `AUTO_INCREMENT=n` works) | medium | refusal |
| `RENAME USER`; `CREATE USER` tails: auth plugin, `REQUIRE SSL/X509`, resource limits, `ACCOUNT LOCK`, `PASSWORD EXPIRE`; `ALTER USER` beyond password change | medium | refusal |
| Multi-statement strings (`stmt1; stmt2`) — exactly one statement per round trip | medium | refusal |

### SELECT-level syntax gaps

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| `SQL_CALC_FOUND_ROWS` | accepted and feeds `FOUND_ROWS()` | absent alongside `FOUND_ROWS()` | low | refusal |
| Locking detail | `FOR UPDATE/SHARE [OF tbl…] [NOWAIT|SKIP LOCKED]` | `FOR UPDATE`/`FOR SHARE`/`LOCK IN SHARE MODE` accepted and ignored; no OF/NOWAIT/SKIP LOCKED | low | divergence |
| CTE placement | `WITH` in subqueries, derived tables, `INSERT…WITH` | top-level SELECT/UNION only (`Parser.fs:2458–2464`) | medium | refusal |
| Quantified comparison | `= ANY/SOME/ALL (subquery)` | absent | medium | refusal |
| Row constructors | `(a,b) = (1,2)`, `(a,b) IN ((1,2),(3,4))` | unparseable | medium | refusal |
| User/system variables in expressions | `@x`, `@@x` anywhere an expression fits; `@x := …` | only bare `SELECT @x, @@y AS a` lists via post-parse regex fallback (`QueryHandler.fs:1188–1193`); inside larger queries → 1064 | medium | refusal |
| Named-window inheritance | `OVER (w ORDER BY x)` extends a named window | absent (`Ast.fs:220–221`) | low | refusal |

Expression coverage that does exist: full comparison/logical/arithmetic
operators incl. `<=>`, `XOR`, three-valued logic; CASE (both forms);
CAST/CONVERT; EXISTS/IN/BETWEEN/LIKE [ESCAPE]/REGEXP; `->`/`->>` JSON
operators; charset introducers; hex literals; typed temporal literals;
`INTERVAL n unit`; MATCH…AGAINST; collation postfix; version-comment
splicing `/*!NNNNN … */`; and MySQL's single-row `FROM DUAL` source.

## 2. Query execution

Working: hash joins for equi-joins with collation-folded keys, lazy nested
loops otherwise, correlated scalar/EXISTS/IN subqueries with correct NULL
semantics, bounded top-N sort for ORDER BY+LIMIT, GROUP_CONCAT byte cap,
WITH ROLLUP expansion, window frames (ROWS/RANGE, numeric offsets),
COUNT(DISTINCT a,b) tuples, statement-atomic multi-table DML, exact ODKU
affected-rows semantics (changed=2/no-op=0 under default flags), MySQL's
1241 error for multi-column scalar/IN subqueries, and the empty-group
identities for bit aggregates.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Secondary-index access paths | ref/eq_ref/range scans feed joins, ORDER BY, GROUP BY | only single-table PK/UNIQUE equality point lookups (`Storage.fs:1581–1634`); join inner sides and all ORDER BYs are full scans/sorts | high (scale) | divergence |
| Optimizer | pushdown, constant folding, join reordering, cost model, statistics | none; joins fold left-to-right as written; derived tables materialize once per statement (`Functions.fs:44`, `Executor.fs:36–43`) | medium | divergence |
| EXPLAIN fidelity | type ∈ system/const/eq_ref/ref/range/index/ALL; FORMAT=JSON/TREE; ANALYZE; optimizer_trace | `type` ∈ {system, const, ALL} only; extra flags limited to Using where/filesort/temporary (`Executor.fs:6697–6708, 6896–6903`) | low | divergence |
| Subquery strategies | semi-join/materialization/early-exit transformations | IN-subqueries re-execute per outer row; EXISTS materializes fully without LIMIT-1 short-circuit (`Executor.fs:2011, 2216`) | medium (scale) | divergence |
| Join size ceiling | unbounded (memory-bound) | hard cap 1,000,000 candidate rows → error 1105 (`Executor.fs:1586, 3287–3290`) | medium | divergence |
| Multi-table UPDATE/DELETE sources | derived tables allowed as join sources | real base tables only → 1064 (`Executor.fs:3334–3339`) | low | refusal |
| MATCH…AGAINST placement | evaluates in UPDATE/DELETE WHERE, joins, subqueries | single-table SELECT pre-pass only, else 1191 (`Executor.fs:1828–1831, 5828–5830`) | medium | refusal |
| GROUP_CONCAT truncation | emits warning, increments warning count | truncates silently (`Executor.fs:4424–4438`) | low | divergence |
| RANGE window frames | `RANGE BETWEEN INTERVAL n DAY PRECEDING…` | temporal offsets refused with 1235; numeric offsets only (`Executor.fs:5438–5444`) | low | refusal |
| sql_mode | ~20 mode bits with semantic effect | only strictness (STRICT_TRANS_TABLES/STRICT_ALL_TABLES) has effect; ONLY_FULL_GROUP_BY absent (bare column picks first row of group, `Executor.fs:4768`); `@@sql_mode` echoes a constant string regardless of SET (`Session.fs:22`) | medium | divergence |
| FOUND_ROWS()/ROW_COUNT() | session functions | not registered (`QueryHandler.fs:833–868` has the session registry) | low | refusal |
| Result column types | declared/schema-driven | inferred from returned row values; an all-NULL column under LIMIT reports VAR_STRING (`Executor.fs:6110`) | medium | divergence — see §15 open finding |

## 3. Built-in functions

Registered surface (`Functions.fs:2871–3048`): string (CONCAT family,
SUBSTRING_INDEX, ELT/FIELD/FIND_IN_SET/EXPORT_SET, QUOTE, STRCMP, REGEXP_LIKE
family with match_type, SOUNDEX, MAKE_SET, base64 conversion), math (ROUND with exact/approximate split, CONV,
CRC32, BIT_COUNT, logarithms, exponentials, and trigonometry), date/time
(DATE_ADD/SUB, TIMESTAMPADD/DIFF, DATE_FORMAT,
STR_TO_DATE, EXTRACT, LAST_DAY, MAKEDATE, UNIX_TIMESTAMP, FROM_UNIXTIME,
WEEK(mode)/YEARWEEK(mode)), JSON (EXTRACT/UNQUOTE/CONTAINS/SET/INSERT/
REPLACE/REMOVE/ARRAY/OBJECT/LENGTH/DEPTH/VALID/TYPE/KEYS/SEARCH,
ARRAYAGG/OBJECTAGG, JSON_TABLE), hashing (MD5/SHA1/SHA2), UUID family,
IPv4/IPv6 conversion and predicates, COALESCE/IFNULL/IF/NULLIF/GREATEST/
LEAST, plus session functions DATABASE/LAST_INSERT_ID/VERSION/CONNECTION_ID/
CURRENT_USER/USER/SESSION_USER.

| Missing family | Functions | Impact |
|---|---|---|
| Crypto | `AES_ENCRYPT AES_DECRYPT ENCODE DECODE`, entire asymmetric family | medium |
| Compression/weight | `WEIGHT_STRING` | low |
| JSON second half | `JSON_SCHEMA_VALID JSON_SCHEMA_VALIDATION_REPORT JSON_VALUE` | medium |
| Misc | `BENCHMARK SLEEP COERCIBILITY DEFAULT()` outside REPLACE-SET | low |
| Geometry | all `ST_*`/`GeometryCollection` functions and types | low |

Divergences in existing functions: `CURTIME()`/`TIME()` return strings (no
TIME value domain, `Functions.fs:1296–1305`); `CONVERT_TZ` resolves numeric
offsets only — named zones and `SYSTEM` return NULL (`Functions.fs:1564–1571`);
`TIMESTAMP()` is 1-arg only; `JSON_SEARCH` lacks escape_char/path arguments
(`Functions.fs:941`); JSON_TABLE lacks `ERROR ON EMPTY|ERROR` raise-form
(`Ast.fs:432–434`) and refuses `JOIN…USING`/LEFT-JOIN-ON-TRUE against it
(`Executor.fs:3069–3077, 3108–3113`).

## 4. Data types and values

Working: TINYINT–BIGINT signed/unsigned, DECIMAL(p,s) with fixed-point
round-trip, FLOAT/DOUBLE with MySQL exponent rendering, CHAR/VARCHAR,
TINYTEXT–LONGTEXT, BINARY/VARBINARY, TINYBLOB–LONGBLOB, ENUM/SET with
canonicalization, DATE/DATETIME(fsp)/TIMESTAMP(fsp)/TIME(fsp) with half-up
fsp rounding and carry cases, YEAR, JSON, per-column charset/collation,
wire-faithful column metadata (`ColumnWire.fs:17–84`).

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Zero dates | `'0000-00-00'` representable; NO_ZERO_DATE mode-gated | no zero-date sentinel; rejection unconditional, non-strict NOT NULL temporal writes hard-fail (`Storage.fs:63–67, 707–711`) | low | divergence |
| TIME value domain | typed TIME comparisons/arithmetic | stored and compared as pre-formatted strings; no `VTime` case (`Value.fs:13–30`, `Storage.fs:911–935`) | medium | divergence |
| BIT type | `BIT(M)` with bit-literal I/O | absent | low | refusal |
| Spatial types | GEOMETRY/POINT/LINESTRING/POLYGON… | absent; SPATIAL INDEX parses and collapses to BTree (`Ast.fs:326–328`) | low | refusal |
| Generated columns | VIRTUAL recomputed on read, STORED materialized | both materialize at write time; no read-path recompute (`Storage.fs:3705–3713`) | low | divergence |
| Functional defaults | `DEFAULT (expr)` | literal constants and CURRENT_TIMESTAMP only | low | refusal |
| Column comments | tracked, shown in SHOW CREATE TABLE/I_S | accepted then dropped (`Parser.fs:1371`) | low | divergence |
| ZEROFILL/display width | zero-fill formatting, width in metadata | not tracked beyond static wire lengths; ZEROFILL flag never set (`ColumnWire.fs:58–84`) | low | divergence |
| JSON representation | binary DOM, member-of/path ops on it | raw text value, re-parsed per operation (`Value.fs:28–29`) | low (perf) | divergence |

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
| Non-unique secondary indexes | physical structures serving lookups/ordering | catalog metadata only; every non-point-lookup WHERE is a scan (`Storage.fs:110–117`) | high (scale) | divergence |
| Prefix indexes | `INDEX (col(N))` with SUB_PART metadata | parsed prefix length discarded; SUB_PART always NULL (`InformationSchema.fs:491–495`) | low | divergence |
| Expression indexes | `INDEX ((expr))` | absent | low | refusal |
| Descending/invisible indexes | `DESC`, `INVISIBLE` | absent | low | refusal |
| Cross-database FKs | supported | referenced key carries no database qualifier; invisible/unenforceable across databases (`Ast.fs:334–341`) | low | divergence |
| FK DDL validation | rejects SET NULL → NOT NULL child, non-unique referenced keys at CREATE | accepted at DDL, surfaces as runtime errors (`Storage.fs:3071–3080`) | low | divergence |
| CHECK DDL-time restrictions | errors 3818/3102 for nondeterministic/foreign refs at CREATE | relaxed; caught at first write (`Executor.fs:6349`) | low | divergence |
| AUTO_INCREMENT | counter persists across restart via redo | burned ids survive rollback (InnoDB-like) but counter rebuild after crash replays WAL events; forward-only ALTER SET (`Storage.fs:2179–2182`) | low | divergence |

## 6. Charsets and collations

Working: ICU-backed registry covering the utf8mb4 0900 attribute matrix,
legacy unicode/general collations, ~21 language collations, ja_0900_as_cs_ks,
utf8mb3/latin1(cp1252)/ascii/binary; real MySQL collation ids and SORTLENs
for SHOW COLLATION/I_S; PAD SPACE semantics; connection collation for
literal-vs-literal comparison; default utf8mb4_0900_ai_ci.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Weight tables | UCA 9.0/5.2/4.0 weight tables per collation | ICU CLDR tailoring for everything; tie-break order among primary-equal strings can differ (equality never does) (`Collation.fs:13–26`) | low | divergence |
| `utf8mb4_general_ci` ß | compares equal to 's', not 'ss' | expands ß→ss (`Collation.fs:13–26`) | low | divergence |
| LIKE expansions | 'æ' LIKE 'ae' true under accent-insensitive collations | per-character folding without expansions; false while 'æ' = 'ae' holds (`Collation.fs:13–26`) | low | divergence |
| REGEXP collation | follows collation accent/case rules | always case-sensitive per pattern flags, accent-sensitive | low | divergence |
| Usable charsets | 40+ charsets with transcoding | utf8mb4/utf8mb3/latin1/ascii/binary only; CONVERT(expr USING x) limited to the same set (`Functions.fs:966`) | low | refusal |
| Identifier casing | lower_case_table_names semantics | variable reported; identifiers ordinal-case-folded internally | low | divergence |

## 7. Transactions and concurrency

Working: private-snapshot transactions with three-way optimistic merge and a
point-update fast path for disjoint writes, retryable 1205 on same-row
conflicts, merged-result revalidation of unique keys and FKs, savepoints
with MySQL establishment-order semantics, autocommit implicit transactions,
read-only transactions never blocking writers, per-database sharding so
cross-database writers never contend, 4096-stripe row locks for indexed
updates, InnoDB-style burned AUTO_INCREMENT on rollback.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| SERIALIZABLE | implemented via shared locks / auto-conversion | refused with 1235 (`QueryHandler.fs:1357–1367`) | medium | refusal |
| READ COMMITTED / READ UNCOMMITTED | distinct semantics | accepted, recorded, but execute snapshot (repeatable-read) semantics like every other level | medium | divergence |
| Deadlock errors | 1213 deadlock detection with victim selection | write-write conflicts surface as lock-wait timeout 1205; no deadlock classification | low | divergence |
| Write parallelism within a database | row-lock concurrency | per-database publication gate serializes commits; measured 45 tx/s, p99 64.5 s at 128 workers on one hot database (`torture/findings/2026-08-16-concurrency-campaign.md`) | high (throughput) | divergence |
| Multi-database scaling | near-linear with connections | super-serial slowdowns (ratio up to 10.98×) demonstrated; store-wide connection ceiling produces honest 1205s (`torture/findings/2026-08-17-multidb-concurrency-campaign.md`, status open) | medium | divergence |
| Cross-database snapshots | linearizable catalog reads | catalog view explicitly not atomic across databases mid-commit (`Storage.fs:308–319`) | low | divergence |

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
| Group commit | binlog/redo group flush amortization | one open+fsync per committed statement (`Persistence.fs:1130`) | medium (throughput) | divergence |
| Space reclamation | purge threads reclaim deleted rows | tombstoned slots never reclaimed; long-lived delete-heavy tables grow memory without bound (`RowStore.fs:61–63`) | medium | divergence |
| Data-directory trust | tablespace pages authenticated by InnoDB checks | CRC detects but does not authenticate; anyone writing `wal.bin`/`snapshot.fsdb` rewrites `mysql.user` (documented in README) | low (threat-model) | divergence |
| CREATE_TIME stability | stable across recovery | re-stamped on WAL-only replay (`Storage.fs:134–137`) | low | divergence |
| Platform | portable | durable mode macOS/Linux only (libc fsync design) | low | divergence |

## 9. Views and triggers

Views working: CREATE [OR REPLACE] VIEW with explicit column lists, views
over views, recursive-reference detection (1462), definer-privilege checking
at read time so revokes take effect, persistence through WAL restarts,
SHOW CREATE VIEW and I_S.VIEWS with correct shapes.

Triggers working: AFTER INSERT FOR EACH ROW with NEW.* substitution
(including generated-column values and inside ODKU bodies), bodies limited
to one INSERT/REPLACE/UPDATE/DELETE, statement atomicity (failing body rolls
back originating rows), 1442 cycle/self-write detection with MySQL's exact
message, depth cap, definer-based privilege checks per fire, DROP TABLE /
RENAME TABLE lifecycle maintenance, SHOW TRIGGERS and I_S.TRIGGERS fidelity.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Updatable views | INSERT/UPDATE/DELETE through simple views | absent; a write targeting a view behaves as table-not-found rather than 1288 (`Ast.fs:693–695`) | medium | refusal |
| WITH CHECK OPTION | enforced on updatable views | unparseable | low | refusal |
| ALGORITHM / SQL SECURITY INVOKER / ALTER VIEW | supported | absent; SECURITY_TYPE constant DEFINER (`InformationSchema.fs:922–923`) | low | refusal |
| View metadata projection | views appear in I_S.COLUMNS, DESCRIBE, SHOW TABLE STATUS | absent (documented in compatibility.md) | medium | divergence |
| VIEW_DEFINITION rendering | fully-qualified expanded form; SHOW CREATE VIEW wrapped in `/*!50001 */` | raw user text, no wrapper (`InformationSchema.fs:1749–1764`) | low | divergence |
| Trigger timings/events | BEFORE/AFTER × INSERT/UPDATE/DELETE | AFTER INSERT only; engine has no pre-write row-image hook (`Ast.fs:681–684`) | high (for trigger users) | refusal |
| OLD.* row access | available in UPDATE/DELETE triggers | absent everywhere | medium | refusal |
| Compound trigger bodies | BEGIN…END with variables/handlers | single statement only (`Executor.fs:8326–8333`) | medium | refusal |
| Multiple triggers per slot | yes, with FOLLOWS/PRECEDES ordering | one per (table,timing,event); second CREATE → 1359 (`Executor.fs:8343–8357`) | low | divergence |
| Trigger recursion cap | cycle detection at runtime | hardcoded depth 8 (`Executor.fs:7608–7616`) | low | divergence |
| Per-trigger sql_mode/charset capture | stored and applied | server constants in I_S output (`InformationSchema.fs:1019–1023`) | low | divergence |

## 10. Stored routines, events, schedulers

Total absence, honestly surfaced: no CREATE/ALTER/DROP PROCEDURE or FUNCTION,
no CALL, no compound-statement language, no event DDL, no scheduler thread.
`information_schema.ROUTINES/PARAMETERS/EVENTS` and `SHOW PROCEDURE|FUNCTION
STATUS`/`SHOW EVENTS` return correctly-shaped empty results
(`InformationSchema.fs:902–905, 1969–1984`). Execute_priv/Event_priv/
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
column-set validation.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Inverted index | persistent inverted index, sublinear queries | none; every MATCH re-tokenizes and scores the whole table per statement (`FullText.fs:2–5`) | high (scale) | divergence |
| MATCH scope | any SELECT/UPDATE/DELETE context, joins included | single-table SELECT pre-pass only; elsewhere 1191 | medium | refusal |
| Tunables | innodb_ft_min_token_size, ft_query_expansion_limit, stopword tables, enable/disable | fixed at 3 / 20 / built-in list (`FullText.fs:17–24`) | low | divergence |
| CJK | ngram and mecab parsers, WITH PARSER clause | absent; no CJK tokenization | medium (for CJK) | refusal |
| Accent folding | ai_collation-aware matching | none; é ≠ e in search (`FullText.fs:45–47`) | low | divergence |
| Proximity/prefix details | manual leaves distance semantics open; phrase-prefix via `"word*"`-adjacent forms | @N interpreted as N-token window; prefix wildcard attaches to single words only (`FullText.fs:138–140, 227–231`) | low | divergence |

## 12. Wire protocol and prepared statements

Working: HandshakeV10 with capability negotiation, CLIENT_DEPRECATE_EOF both
directions, auth-switch to mysql_native_password for caching_sha2 clients,
constant-time credential verification, COM_QUERY/INIT_DB/PING/FIELD_LIST/
QUIT/RESET_CONNECTION, full COM_STMT_PREPARE/EXECUTE/CLOSE/SEND_LONG_DATA/
RESET with type reuse and 1153-on-overflow long-data accounting, text and
binary row encodings including µs-precision temporals and 16 MiB multi-packet
framing, CLIENT_FOUND_ROWS honored, max_allowed_packet/max_connections/
max_prepared_stmt_count enforced with honest advertising, mid-query
disconnect detection cancelling evaluation (`Server.fs:363–406`).

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| TLS | full SSL/TLS negotiation | SSLRequest answered ERR 1045 "SSL is not supported" (`Server.fs:998–1006`); have_ssl DISABLED | high (networked deployments) | refusal |
| Compression | CLIENT_COMPRESS/ZSTD | never offered | low | refusal |
| Cursors | COM_STMT_EXECUTE CURSOR_TYPE_READ_ONLY + COM_STMT_FETCH | cursor flags ignored; COM_STMT_FETCH unsupported → 1047 (`Server.fs:772`) | medium (large-result readers) | refusal |
| LOAD DATA LOCAL INFILE | supported | absent entirely | medium | refusal |
| Multi-statement | CLIENT_MULTI_STATEMENTS batching | not advertised; one statement per packet (CLIENT_MULTI_RESULTS advertised but only one resultset ever sent, `Protocol.fs:21,36`) | medium | refusal |
| Session state tracking | CLIENT_SESSION_TRACK info in OK packets | absent | low | refusal |
| Diagnostics area | warning count in OK/EOF, SHOW WARNINGS populated | warning count hardwired 0 (`Protocol.fs:157`); SHOW WARNINGS/ERRORS always empty | medium | divergence |
| Unimplemented COM_* | STATISTICS, PROCESS_INFO, PROCESS_KILL, DEBUG, SET_OPTION, CHANGE_USER | all → ERR 1047; `mysqladmin status` fails (`Server.fs:70, 973–981`) | low | refusal |
| Auth plugins | caching_sha2_password fast/full auth, sha256_password, RSA exchange | mysql_native_password only; caching_sha2 clients downgraded via auth-switch (`Server.fs:469–479`) | low (works, weaker) | divergence |
| Column definition fidelity | schema/table/org_table names, requested charsetnr | empty strings; charset forced to 45 (utf8mb4_general_ci) or 63 binary regardless of request (`Protocol.fs:110, 253–260`) | low | divergence |
| Column flags | MULTIPLE_KEY, ZEROFILL, NO_DEFAULT_VALUE, ON_UPDATE_NOW, NUM, PART_KEY | not composed (`Value.fs:58–66`) | low | divergence |
| Parameter metadata | STMT_PREPARE_OK carries result columns and typed param defs | column count always 0; params generic "?" VAR_STRING (`Protocol.fs:475–483`) | low | divergence |
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
(`Auth.fs:667–682`), persistence through ordinary row operations.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Host matching | per-host accounts, `%`/CIDR patterns | name-only; every account `'name'@'%'`, connecting host renders localhost (`Session.fs:169–171`) | medium | divergence |
| Text-probe privilege bypass | all statements checked | SET/SHOW/KILL/USE bypass the gate (SET PASSWORD and KILL carry their own checks) — documented divergence (`docs/compatibility.md`) | low | divergence |
| Roles | CREATE ROLE, SET ROLE, role grants, mandatory roles | absent | medium | refusal |
| Dynamic privileges | BACKUP_ADMIN, CONNECTION_ADMIN, … | vocabulary absent from GRANT parsing | low | refusal |
| Column-level privileges | mysql.columns_priv enforced | table exists, never consulted | low | divergence |
| Account lock/expiry/resource limits | enforced | columns present in mysql.user, zero readers (`Auth.fs:69–71, 221–223`) | low | divergence |
| Proxy users | supported | absent | low | refusal |
| SHOW GRANTS completeness | includes dynamic-privilege and PROXY lines | omits them (`Auth.fs:891–892`) | low | divergence |
| System-table coverage | ~38 mysql.* tables | 8 (user, db, tables_priv, columns_priv, global_grants, triggers, views, check_constraints) (`Storage.fs:1444–1453`) | low | divergence |

## 14. Metadata, server administration, logging, replication

Working: 23 INFORMATION_SCHEMA views with viewer scoping (SCHEMATA, TABLES,
COLUMNS, STATISTICS, TABLE_CONSTRAINTS, KEY_COLUMN_USAGE,
REFERENTIAL_CONSTRAINTS, CHECK_CONSTRAINTS, VIEWS, TRIGGERS, PROCESSLIST,
ENGINES, COLLATIONS, CHARACTER_SETS, privilege views, …), direct
SELECT-ability of the 8 mysql.* tables, SHOW TABLES/COLUMNS/INDEX/CREATE
TABLE/CREATE VIEW/TABLE STATUS (real byte accounting)/ENGINES/CHARACTER SET/
COLLATION/PRIVILEGES (73 oracle-verified rows)/PROCESSLIST/VARIABLES/STATUS/
GRANTS/TRIGGERS/WARNINGS shells, DESCRIBE, ALTER TABLE DISABLE/ENABLE KEYS
no-op for mysqldump, my.cnf parsing ([mysqld]/[server], loose- prefix,
!include with depth cap), KILL QUERY/CONNECTION with PROCESS/SUPER checks,
live Limits reporting.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| INFORMATION_SCHEMA breadth | ~60+ views incl. INNODB_*, COLUMN_STATISTICS, RESOURCE_GROUPS, ENABLED_ROLES | 23 views; EVENTS/ROUTINES/PARAMETERS/COLUMN_PRIVILEGES genuinely empty | low | divergence |
| Table statistics | estimates refreshed by ANALYZE TABLE | ENGINE always InnoDB, DATA_LENGTH stand-in 16384, CARDINALITY 0, live row counts where MySQL keeps stale page estimates until ANALYZE (`InformationSchema.fs:267–288`); no ANALYZE statement exists | low | divergence |
| COLUMN_COMMENT | user text | always "" (`InformationSchema.fs:323–326`) | low | divergence |
| Missing SHOW forms | CREATE USER/EVENT/PROCEDURE/FUNCTION, MASTER/REPLICA STATUS, BINARY LOGS | fall through to 1064 | low | refusal |
| SHOW STATUS counters | Questions, Com_*, Innodb_*, Slow_queries, … | four variables only: Ssl_cipher, Ssl_version, Threads_connected, Uptime (`InformationSchema.fs:1929–1938`) | low | divergence |
| wait_timeout | 28800 default | 300 (deliberate DoS posture, honestly advertised) | low | divergence |
| Option-file discovery | /etc/my.cnf, ~/.my.cnf, $MYSQL_HOME auto-read; `[mysqld-8.4]` groups | only `--defaults-file`; version-suffixed groups skipped (documented) | low | divergence |
| Logging | general log, slow log, error-log file | stderr diagnostics with credential redaction only (`Log.fs`) | low | divergence |
| Replication | binlog, GTID, source/replica channels | nothing; REPLICATION privileges are vocabulary only; internal WAL is not a binlog | architectural | refusal |
| SHUTDOWN statement | supported | absent; shutdown via embedding API or signals | low | refusal |
| net_read_timeout | configurable | does not exist | low | divergence |

## 15. Open differential-testing findings (torture harness)

Recorded in `torture/findings/`, not yet fixed, not enrolled in
`support/known-gaps.json`:

| Finding | Detail | Status |
|---|---|---|
| Declared result-type mismatches | 23 probes: ENUM→VARCHAR (8), BIGINT→VARCHAR (8), BOOL→BIGINT (4), YEAR→BIGINT (2), TIME→VARCHAR (1); four stable case signatures recorded (`2026-08-20-client-contract-campaign.md`) | open |
| Multi-database scaling | super-serial slowdowns; store-wide connection ceiling; classified `multidb_scaling_gap` (`2026-08-17-multidb-concurrency-campaign.md`) | open, reporting-only |
| INSERT…SELECT…ODKU alias refs | bare select-column references in the UPDATE clause error where MySQL reads select-derived values; only `VALUES(col)` works (`2026-08-19-insert-select-odku-gap.md`, `Ast.fs:630–633`) | deferred by design |
| JSON_TABLE refusals | LEFT JOIN JSON_TABLE(…) ON TRUE → 1064; JOIN…USING → 1064; ERROR ON EMPTY/ERROR raise-form unparsed; correlated unknown qualifier yields 1054 vs MySQL 1109 (`2026-08-19-json-table-gaps.md`) | partially stale — see §17 |
| Signed-int clamping ceilings | TINYINT–INT clamp instead of 1264; CAST(double AS UNSIGNED) clamps at unsigned ceiling where MySQL uses signed max; 1690 message lacks expression text (`2026-08-19-probe-corpus-triage.md`) | ponytail ceilings |
| Temporal/error-shape ceilings | `DATE 'bad'` → 1064 vs MySQL 1525; CONVERT_TZ(…,'SYSTEM') → NULL; parenthesized set-op groups `(A UNION B) INTERSECT C` refused | ponytail ceilings |

Uncovered torture lanes (harness scope, not product gaps): durability/restart
during concurrent commits, matched negative-oracle campaigns, connection
churn mid-transaction, cancellation while queued, savepoints under
concurrency, isolation levels other than REPEATABLE READ, concurrent
CREATE/DROP DATABASE under traffic.

## 16. Deliberate divergences (accepted, not targeted for parity)

Documented or ponytail-marked design decisions that differ from MySQL on
purpose: wait_timeout 300; no option-file auto-discovery; join candidate cap
of 1M rows; unconditional zero-date rejection; text-probe privilege bypass;
VECTOR type and function family (a MySQL 9 forward-port, absent from 8.4 —
purely additive); live statistics values instead of ANALYZE-stale estimates;
ICU CLDR collation tailoring; SUPER required for foreign KILL; honest
advertising of enforced limits (wal_rotate knobs unreported rather than
fabricated); empty routine/event catalogs rather than stubs.

## 17. Documentation drift found during the audit

Where the docs and the code disagree, the code is authoritative:

- `docs/compatibility.md` states DROP TRIGGER lacks its subject-table
  privilege check; commit 1002e9e added it (`Auth.fs:667–682`). Doc is stale.
- `docs/performance-design.md` §1.7 states COM_RESET_CONNECTION answers
  "Unknown command"; it replies OK and clears session state
  (`Server.fs:943–972`, pinned by IntegrationTests). Doc is stale.
- `docs/performance-design.md` lists query cancellation on client disconnect
  as rejected; the disconnect watcher cancels row evaluation
  (`Server.fs:280–406`, tested). Doc is stale.
- `torture/findings/2026-08-19-json-table-gaps.md` says NESTED PATH, EXISTS
  PATH, and DEFAULT clauses do not parse; waves W3/W4 shipped them. The
  remaining true items are ERROR ON EMPTY/ERROR, LEFT JOIN … ON TRUE, and
  JOIN…USING refusals.
- `docs/compatibility.md` claims subqueries are unchecked by privileges;
  `Auth.exprReadTables` walks them (`Auth.fs:438–546`). Doc is stale.
- The open client-contract campaign holds four result-type signatures while
  `support/known-gaps.json` is empty — consistent with the manual-enrollment
  policy, but unreconciled.

## 18. Relative severity view

Ranked by expected disruption to the primary consumers, independent of
implementation effort:

1. No TLS — blocks any non-loopback deployment with security requirements.
2. Non-unique secondary indexes as metadata plus planless joins/subqueries —
   correctness holds, but scale diverges sharply from MySQL past small data.
3. Diagnostics area (warnings count, SHOW WARNINGS) — clients that check
   warnings after IGNORE/bulk loads see nothing.
4. Declared result-type mismatches (open campaign) — typed/native clients
   misread columns.
5. Trigger coverage (BEFORE/UPDATE/DELETE, OLD.*, compound bodies) and
   updatable views — the two largest deliberate-subset cliffs.
6. Missing function families (AES, time arithmetic, JSON second half) —
   each individually small, collectively frequent in
   report-style queries.
7. LOAD DATA LOCAL INFILE and multi-statement packets — bulk-loading and
   migration-tool paths.
8. LIMIT ? placeholders and user variables in expressions — ORM patterns
   that bind limits or pass session state.
9. SERIALIZABLE/READ COMMITTED semantics and intra-database write
   parallelism — transactional throughput shape.
10. Signed-int range enforcement and zero-date handling — strict-mode
    correctness edges.
11. Everything in the admin/replication/metadata tail — matters only once a
    specific tool needs it (mysqladmin, monitoring agents, replica setups).
