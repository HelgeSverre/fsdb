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
document also covers deliberate implementation ceilings and findings recorded
under `torture/findings/`.

## Contents

- [Summary by area](#summary-by-area)
- [1. SQL statements and parser](#1-sql-statements-and-parser)
- [2. Query execution](#2-query-execution)
- [3. Built-in functions](#3-built-in-functions)
- [4. Data types and values](#4-data-types-and-values)
- [5. Constraints and indexes](#5-constraints-and-indexes)
- [6. Charsets and collations](#6-charsets-and-collations)
- [7. Transactions and concurrency](#7-transactions-and-concurrency)
- [8. Persistence and durability](#8-persistence-and-durability)
- [9. Views and triggers](#9-views-and-triggers)
- [10. Stored routines, events, schedulers](#10-stored-routines-events-schedulers)
- [11. Full-text search](#11-full-text-search)
- [12. Wire protocol and prepared statements](#12-wire-protocol-and-prepared-statements)
- [13. Authentication and privileges](#13-authentication-and-privileges)
- [14. Metadata, administration, logging, replication](#14-metadata-server-administration-logging-replication)
- [15. Differential-testing and performance tails](#15-differential-testing-and-performance-tails)
- [16. Deliberate divergences](#16-deliberate-divergences-accepted-not-targeted-for-parity)
- [17. Relative severity view](#17-relative-severity-view)

## Summary by area

| Area | Current boundary | Largest remaining gap |
|---|---|---|
| [SQL statements](#1-sql-statements-and-parser) | Application-facing DML and DDL are broad | Replication and administrative SQL |
| [Query execution](#2-query-execution) | Common index, join, subquery, ordering, and grouping paths have dedicated plans | General cost-based planning and broader correlated forms |
| [Built-in functions](#3-built-in-functions) | Broad scalar, aggregate, JSON, temporal, and planar geometry coverage | Geographic SRS semantics |
| [Data types](#4-data-types-and-values) | Common scalar, temporal, JSON, and OGC geometry values | Binary JSON representation |
| [Constraints and indexes](#5-constraints-and-indexes) | Constraints and common equality, range, ordering, grouping, join, and spatial probes | Arbitrary expression ordering and broader grouping paths |
| [Charsets and collations](#6-charsets-and-collations) | ICU-backed charset and collation registry | Exact MySQL UCA weight tables |
| [Transactions](#7-transactions-and-concurrency) | Supported isolation levels, row ownership, optimistic merge, and XA | Remaining coarse write shapes |
| [Persistence](#8-persistence-and-durability) | Opt-in WAL, snapshots, recovery, rotation, and group commit | Foreground rather than background row reclamation |
| [Views and triggers](#9-views-and-triggers) | Single-table, nested, and restricted join views; ordered compound triggers | Complex updatable views |
| [Routines and events](#10-stored-routines-events-schedulers) | Procedures, functions, and scheduled events are persisted and executable | No open gap recorded |
| [Full-text](#11-full-text-search) | Maintained inverted indexes and MySQL-shaped scoring | CJK parsing and remaining plan combinations |
| [Wire protocol](#12-wire-protocol-and-prepared-statements) | Prepared statements, TLS, compression, LOCAL INFILE, and multi-results | GTID state tracking and live TLS certificate reload |
| [Authentication](#13-authentication-and-privileges) | Host accounts, caching-SHA2/SHA-256/native credentials, grants, roles, proxy grants, and account policy | Pluggable identity and proxy-user selection |
| [Metadata and administration](#14-metadata-server-administration-logging-replication) | Broad metadata catalogs and live command/session state | Engine-owned contents, logging, and replication |

## 1. SQL statements and parser

The SQL core supports full DML, including `INSERT`/`REPLACE ... SET`, ODKU,
`IGNORE`, and multi-table forms. `SELECT` covers joins, derived and lateral
sources, `JSON_TABLE`, expression subqueries, set operations, windows, rollups,
and ordinary or recursive query-scoped CTEs. CTEs can lead UPDATE or DELETE and
appear within set-operation branches.

DDL covers databases, tables, indexes, views, triggers, users, grants,
`CREATE TABLE ... AS SELECT`, temporary tables, `TRUNCATE`, and `RENAME TABLE`.
Multi-pair renames resolve from left to right and publish atomically. Base
tables retain their data, generated constraint names, and foreign-key
relationships across supported moves. As in MySQL, a triggered table and a
view cannot cross a database boundary.

`EXPLAIN` supports traditional, JSON, and ANALYZE forms. Transaction control,
`SET`, `SHOW`, `USE`, `KILL`, and `DESCRIBE` are text-probed before the grammar
by `QueryHandler.dispatch`.

XA supports start, end, prepare, one- and two-phase commit, rollback, detached
recovery, byte-exact raw and converted transaction identifiers, and durable
prepared branches.

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

| Area | Remaining difference | Impact | Class |
|---|---|---|---|
| Server-side files | `LOAD DATA INFILE`, `SELECT … INTO OUTFILE/DUMPFILE`, and `IMPORT TABLE` are unsupported | medium | refusal |
| Table maintenance | `CHECKSUM TABLE` uses a stable fsdb row checksum rather than MySQL's engine-specific value; supported `FLUSH` forms operate on fsdb state rather than InnoDB internals | low | divergence |
| ALTER execution | Accepted changes publish one immutable root; MySQL's COPY/INPLACE/INSTANT algorithms and lock durations do not exist | low | divergence |
| Storage engines | Known engine names still use fsdb's shared InnoDB-shaped row store | low | divergence |
| HASH partitions | Definitions and logical maintenance use the shared row store; physical pruning and `REORGANIZE PARTITION` renames remain absent | low | divergence/refusal |
| Administration and replication | Replication source, binlog purge/reset, plugin/component installation, instance, and tablespace statements are unsupported | low | refusal |
| EXPLAIN | JSON/TREE expose the logical plan without MySQL's cost model; ANALYZE reports aggregate rather than per-iterator observations | low | divergence |

### SELECT-level syntax gaps

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Locking-read granularity | row and next-key locks over the selected access path | `FOR UPDATE`, `FOR SHARE`, `LOCK IN SHARE MODE`, `OF`, `NOWAIT`, and `SKIP LOCKED` hold shared or exclusive row-stripe ownership until transaction end; direct indexed single-table predicates narrow their targets, while joins and scan-shaped reads conservatively lock every row in each named physical source; no next-key/gap locks | low | divergence |
The expression grammar includes:

- comparison, logical, and arithmetic operators, including `<=>`, `XOR`, and
  three-valued logic;
- scalar and nested row comparisons, row `IN`, both `CASE` forms, and
  `CAST`/`CONVERT`;
- `EXISTS`, `IN`, `ANY`/`SOME`, `ALL`, `BETWEEN`, `LIKE ... ESCAPE`, and
  regular expressions;
- JSON `->`/`->>` operators, charset introducers, hex and typed temporal
  literals, intervals, `MATCH ... AGAINST`, and postfix collations;
- MySQL version-comment splicing (`/*!NNNNN ... */`) and the single-row
  `FROM DUAL` source.

## 2. Query execution

Equi-joins use collation-folded hash keys; other joins use lazy nested loops.
Physical join targets can use single-column keys, complete composite keys, or
ordered composite-key prefixes. Qualified inner-join ordering recognizes those
access paths. Full-result plans compare the prefix's observed candidate count
with one hash build, while remaining equality conditions stay residual
predicates. `ORDER BY ... LIMIT` uses a bounded top-N sort.

Direct single-table equality predicates likewise use complete keys or safe
left prefixes for reads and mutations. Their observed candidate count chooses
between the index slice and a row-store scan. Compatible `ORDER BY` suffixes
continue streaming the same composite slice rather than sorting the narrowed
rows again.

Statement-stable scalar, `EXISTS`, `IN`, `ANY`, `SOME`, and `ALL` subqueries
materialize once per statement. Compatible scalar and row-value membership
tests reuse typed sets and can narrow a directly indexed outer table.
Compatible direct-column scalar literal lists use the same statement-scoped
membership representation.

Correlated equality probes use single keys, complete composite keys, or safe
ordered left prefixes through direct tables and pass-through derived tables or
CTEs. Outer values and literals may bind different key parts. Correlated forms
preserve MySQL NULL and multi-column error semantics.

Execution also covers `WITH ROLLUP`, numeric and temporal window frames,
multi-column `COUNT(DISTINCT ...)`, the `GROUP_CONCAT` byte ceiling,
statement-atomic multi-table DML, and exact ODKU affected-row counts. Row
comparisons retain null-safe behavior and MySQL's 1241 error for invalid
multi-column subqueries; empty-group bit aggregates retain their identities.

Row-local recursive CTE members prepare their invariant predicate, projection,
and type coercions once; members needing joins, grouping, windows, full-text,
or locking retain the general SELECT pipeline.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Secondary-index access paths | ref/eq_ref/range scans feed joins, DML, ORDER BY, GROUP BY | common complete-key and safe left-prefix equality, literal membership, range, join, ordering, and grouping shapes use maintained indexes; arbitrary expression ordering and broader grouping still scan or sort | high (scale) | divergence |
| Optimizer | pushdown, constant folding, join reordering, cost model, statistics | physical inner joins with qualified or unambiguous bare references and source-local predicates use shape- and cardinality-driven choices; outer/lateral joins, ambiguous bare references, and plans needing persisted statistics retain source order or conservative execution | medium | divergence |
| EXPLAIN fidelity | type ∈ system/const/eq_ref/ref/range/index/ALL; FORMAT=JSON/TREE; ANALYZE; optimizer_trace | access types cover compatible direct bounds/orderings and source-local join probes; JSON/TREE plans and aggregate ANALYZE observations work, while per-iterator timing/costs and optimizer trace rows remain absent | low | divergence |
| Subquery strategies | semi-join/materialization/early-exit transformations | stable subqueries materialize once and common correlated equality/range shapes probe indexes; variable-bearing, nondeterministic, lateral, JSON_TABLE, and more complex correlated forms re-execute | medium (scale) | divergence |
| Join size ceiling | unbounded (memory-bound) | `Executor.maxJoinCandidateRows` caps candidate rows at 1,000,000 → error 1105 | medium | divergence |
| sql_mode | MySQL 8.4 modes affect parsing and execution | recognized modes are validated, deduplicated, expanded, and reported in MySQL order; every accepted mode has its relevant parser or execution effect except `NO_DIR_IN_CREATE`, which is reporting-only because DATA/INDEX DIRECTORY table options are unsupported | low | divergence |

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
| Geographic spatial behavior | geographic SRS axis, ordering, and distance semantics | low |

`CONVERT_TZ` and the session `time_zone` resolve numeric offsets and `SYSTEM`,
but named zones remain unavailable without MySQL's optional time-zone tables.
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
`TIME_TRUNCATE_FRACTIONAL` applies. SQL modes control zero-date acceptance and
bounded invalid day-of-month combinations; TIMESTAMP always retains full
calendar validation.

Numeric offsets appended to DATETIME and TIMESTAMP inputs are converted into
the session `time_zone`. TIMESTAMP values then retain the UTC instant and
follow later session-zone changes; DATETIME values retain the converted
wall-clock fields. TIMESTAMP range validation happens after conversion,
including MySQL's reserved epoch zero and 2038 upper boundary.

JSON, functional defaults, virtual generated columns, normalized comments,
and OGC WKB geometry values also persist through the regular value and wire
metadata paths. Virtual generated values are recomputed when queried.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Spatial indexes and operations | R-tree indexes and geographic SRS axis rules | maintained immutable MBR indexes narrow direct `MBRINTERSECTS`, `MBRWITHIN`, and `MBRCONTAINS` predicates for SRID 0; planar overlays and independently configurable square/circle point, flat/round end, and miter/round join buffer strategies work, but the internal augmented interval tree is not an R-tree | low | subset |
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
| Non-unique secondary indexes | physical structures serving lookups/ordering | separate immutable equality and ordered structures cover common complete-key and left-prefix probes, joins, ranges, ordering, and grouping; unsupported expression orderings and grouping shapes retain scan/sort fallback | high (scale) | divergence |
| Expression indexes | functional key parts participate in physical access and uniqueness | the [supported functional keys](README.md#indexes-and-joins) have physical equality and ordering paths; other non-unique expressions retain DDL and metadata but scan, while unsupported unique expressions are refused | low | divergence/refusal |

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
| Weight tables | UCA 9.0/5.2/4.0 weight tables per collation | `Collation` uses ICU CLDR tailoring; tie-break order among primary-equal strings, uncommon substring expansions, and `WEIGHT_STRING()` textual bytes can differ | low | divergence |
| Advanced REGEXP grammar | ICU regular expressions and Unicode properties | bounded .NET regex with common POSIX character classes and mapped malformed patterns; remaining ICU-only grammar and error-code distinctions can differ | low | divergence |
| Remaining charset catalog | every bundled charset and collation | eucjpms remains refused because its Microsoft EUC-JP extensions differ from the standard .NET codecs; expanded families register their default and binary collations rather than every legacy language collation | low | refusal |

## 7. Transactions and concurrency

Transactions use private snapshots and three-way optimistic merge. Disjoint
point writes have a fast path; indexed point and range updates or deletes wait
and rebase. Unique-key claims, incremental index validation, and merged-result
foreign-key validation protect publication.

Savepoints follow MySQL establishment order. Autocommit uses implicit
transactions, read-only transactions do not block writers, and unrelated
databases use independent roots. Whole-catalog consumers sample those roots
under the brief publication boundary, so a multi-database commit appears as
one coherent catalog. Row-stripe ownership coordinates finer-grained writes.
Redo-backed AUTO_INCREMENT reservations survive rollback and restart.

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
until completion, and retain their shared row, exclusive row, and unique-key
claims after restart. Snapshot truncation waits until every prepared branch has
resolved.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| SERIALIZABLE locking behavior | predicate/gap locks and blocking reads | conservative snapshot validation rejects any intervening catalog change with 1205 when the transaction writes; read-only transactions retain snapshot semantics | low | divergence |
| Write parallelism within a database | row-lock concurrency | indexed UPDATE/DELETE paths coordinate row stripes; insert, upsert, and replacement candidates are prepared once, then claim supplied, generated, or defaulted unique keys and refresh existing duplicate rows before publication, including SELECT sources; AUTO_INCREMENT identities are reserved across transaction snapshots; keyless inserts, full-scan, CTE, and multi-table writes still rely on optimistic merge; publishing a new immutable database root remains one brief per-database critical section, and durable commit events are sequenced | medium (throughput) | partial |
| Multi-database scaling | near-linear with connections | database roots and row-lock stripes are sharded; qualified foreign keys deliberately serialize catalog-wide referential actions, and recorded campaigns show CPU saturation limiting higher worker counts | medium | partial |

## 8. Persistence and durability

Persistence is opt-in through `--data-dir`. The WAL uses length- and CRC-framed
commit records with torn-tail truncation; snapshots are self-delimiting and
CRC-protected. A durable flush precedes acknowledgement, and fatal flush failure
terminates the process rather than claiming a commit.

Snapshot replacement verifies the `.new` file before preferring it and syncs
the containing directory after rename on Unix. Snapshots retain stable row
identities.

Current update and delete WAL records address those identities and verify their
before-images, falling back to the image only when a concurrent transaction
rebase has reassigned a private row identity. Replay applies the ordered
changes without re-entering checked write paths and maintains derived indexes
incrementally. Older image-only WAL and snapshot formats remain readable.

Group commit, ordered checkpoint barriers, lock-step rotation, shutdown
rotation, decode-depth limits, generated-expression codecs, and durable XA
records share the same persistence path. XA records retain logical lock claims
so startup can rebuild their row/key ownership. Checkpoint rotation waits for
prepared XA branches so their recovery base remains in the WAL.

The durability campaign forces repeated automatic rotations, appends a
WAL-only commit, crashes the server, and verifies the recovered transaction
sets before and after a graceful snapshot restart.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Durability default | durable unless configured otherwise | in-memory unless `--data-dir` passed; process death loses everything | medium (deployment) | divergence |
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
and privilege checks. A saved `ORDER BY` retains updateability, inherits through
direct nesting, and supplies the order for limited updates and deletes unless
the outer statement overrides it. `LOCAL` and `CASCADED CHECK OPTION` predicates
persist and compose through nested views.

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
| Updatable-view breadth | nested write targets with distinct definer/security contexts and additional expression shapes where MySQL deems individual columns writable | single-table views, inherited or overriding `ORDER BY`, same-identity nested joins, outer view layers, and aggregate/UNION read-only join components compose writable targets; one mergeable component updates or inserts at a time | medium | refusal |
| View algorithm strategy | MERGE and TEMPTABLE select distinct execution strategies | declarations and ALTER retain the effective algorithm, incompatible MERGE shapes become UNDEFINED with warning 1354, and TEMPTABLE views are non-updatable; MERGE and UNDEFINED still share fsdb's shape-driven planner | low | divergence |

## 10. Stored routines, events, schedulers

No open routine or event difference is recorded. The implemented boundary is
summarized in the [compatibility guide](docs/compatibility.md#stored-routines-and-events).

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

Flat boolean word expressions score directly from exact or prefix postings.
Required terms begin with the smallest posting; optional, excluded, raised,
lowered, and soft terms probe a smaller upstream candidate set or accumulate
only touched rows. Phrase and proximity conditions intersect their word
postings, retain per-word relevance, and check candidate documents with
ordered matching or a linear sliding window.

The grouped evaluator begins with the smallest required child and probes the
other required children; without one, only positive children seed candidates.
Bounded AND/OR predicate trees intersect or union MATCH candidates before
residual evaluation.

Single-table reads and writes intersect compatible equality, literal-IN,
range, and spatial candidates before scoring. Physical joins score each owning
corpus before joining, and multi-table UPDATE/DELETE score each physical source
before evaluating joins, predicates, and assignments.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| MATCH planning | optimizer can combine FULLTEXT access with every other access path | bounded AND/OR MATCH predicates stream posting candidates; compatible source-local equality, literal-IN, range, and spatial candidates restrict scoring for predicates and projections in single-table and all-inner-join queries; implicit relevance ordering can drive either side of a qualified two-table inner join when the other side is an exact unique probe; cross-source inference and broader join-shaped combinations still score each owning corpus before joining | medium (scale) | divergence |
| Tunables | innodb_ft_min_token_size, innodb_ft_max_token_size, ft_query_expansion_limit, stopword tables, enable/disable | the three numeric defaults are exposed with MySQL's GLOBAL/read-only scope and drive `FullText` at 3 / 84 / 20; `INNODB_FT_DEFAULT_STOPWORD` exposes the exact duplicate-preserving built-in list, while custom stopword tables and enable/disable behavior remain absent | low | divergence/refusal |
| CJK | ngram and mecab parsers, WITH PARSER clause | absent; no CJK tokenization | medium (for CJK) | refusal |

## 12. Wire protocol and prepared statements

HandshakeV10 negotiates capabilities, deprecated EOF behavior, authentication,
compression, TLS, packet limits, and affected-row mode. Authentication defaults
to `caching_sha2_password`, supports cached and full exchanges over TLS or an
RSA public-key request, and can switch clients to explicit `sha256_password`
or `mysql_native_password` accounts. Both SHA-2 plugins accept configured PEM
key pairs and otherwise use a process-local key.

The command surface covers query, database selection, ping, field listing,
quit, connection reset, and the complete prepared-statement lifecycle. Prepared
statements support read-only cursors, type reuse, bounded long data, and text or
binary rows with microsecond temporal precision. Packet framing handles values
larger than one protocol packet.

Transport supports zlib, Zstandard, TLS 1.2 and 1.3, optional server and client
CA certificates, secure-transport enforcement, and per-account SSL, X509,
subject, issuer, or cipher requirements. Packet, connection, and
prepared-statement limits are enforced and advertised honestly. Mid-query
disconnects cancel evaluation through `Server.watchForDisconnect`.

`COM_SET_OPTION` toggles multi-statement handling for negotiated clients.
The dynamic GLOBAL `protocol_compression_algorithms` policy controls zlib,
Zstandard, and uncompressed negotiation for new connections.

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
| Cursor storage | materialized temporary tables spill from memory to disk | read-only, forward-only cursors retain their materialized rows in session memory until exhaustion, reset, close, or commit | low (large concurrent cursors) | divergence |
| Session state tracking | schema, system-variable, generic state, transaction, and GTID trackers | schema, configured system-variable, generic state-change, transaction-characteristic, and transaction-state blocks are encoded in final OK packets; GTID blocks remain absent because fsdb has no binlog | low | subset |
| Diagnostics coverage | warnings from conversions, truncation, deprecated syntax, and storage engines | statement errors, ignored INSERT/CHECK rows, non-strict integer/ENUM/SET/charset coercions, DECIMAL scale-loss notes, declared text/binary truncation, functional-index conversion conditions, conditional DDL and unknown-engine substitution, GROUP_CONCAT truncation, deprecated numeric displays, `utf8` aliases and explicit `utf8mb3` declarations/conversions, plus `SQL_CALC_FOUND_ROWS`, `FOUND_ROWS()`, and ODKU `VALUES()` are captured; other warning producers remain silent | low | divergence |
| System variables | hundreds live | common connector, limit, transaction, password-policy, week-format, and fixed-offset or `SYSTEM` time-zone variables are live; most others are inert or absent, named time zones are unavailable, and `system_time_zone` retains its static bootstrap label | medium | divergence |

## 13. Authentication and privileges

The account catalog follows MySQL 8.4's `mysql.user` column order and includes
a root bootstrap account. New credentials use salted
`caching_sha2_password` hashes; explicit accounts can retain the deprecated
`sha256_password` or `mysql_native_password` transforms.

Account DDL covers locks, expiry, history, reuse intervals, current-password
rules, resource limits, mergeable JSON attributes or comments, and transport
policy from `REQUIRE SSL` through exact X509 subject, issuer, and cipher
attributes. Default password policies inherit live global variables, while
retained hashes use `mysql.password_history` and follow account persistence,
rename, and drop lifecycle.

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
| External authentication providers | component-provided LDAP, Kerberos, WebAuthn, socket, and service-specific identity plugins | the built-in `caching_sha2_password`, `sha256_password`, and `mysql_native_password` plugins are available; external provider loading is absent | low (specialized accounts) | refusal |
| Hostname accounts | forward-confirmed reverse DNS matching | numeric peer addresses plus the loopback `localhost` alias; DNS names are not trusted | low | divergence |
| Proxy identity selection | authentication plugins can map a login to an authorized proxied account | proxy declarations, target-specific grant-option delegation, lifecycle cleanup, persistence, and `SHOW GRANTS` lines work; fsdb's built-in plugins never return an alternate identity and there is no pluggable authentication provider | low | refusal |
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
| INFORMATION_SCHEMA breadth | INNODB_*, KEYWORDS, PLUGINS, spatial-reference catalogs, and usage views | MySQL 8.4's catalog names are present. `INNODB_TRX` projects active transaction identity, lifecycle, isolation, checks, logical write weight, and held row stripes; fields that require InnoDB's lock-memory and scheduling internals remain zero or NULL. Other InnoDB dictionary views project live table, column, index, statistics, and virtual-column metadata; physical diagnostics return truthful empty rowsets where fsdb has no matching buffer-pool, tablespace, compression, or metrics subsystem. ST_SPATIAL_REFERENCE_SYSTEMS exposes fsdb's supported SRID 0 instead of MySQL's full EPSG registry | low | divergence |
| Table statistics | estimates refreshed by ANALYZE TABLE | `InformationSchema.tablesRows` reports InnoDB, a 16384 DATA_LENGTH stand-in, CARDINALITY 0, and live row counts where MySQL keeps stale page estimates until ANALYZE | low | divergence |
| Optimizer cost overrides | `mysql.server_cost` and `mysql.engine_cost` values feed plan costs after `FLUSH OPTIMIZER_COSTS` | both tables expose MySQL's bootstrap rows, generated defaults, and mutable override columns; fsdb's shape-driven planner does not consume their overrides | low | divergence |
| SHOW STATUS counters | Com_*, Innodb_*, Slow_queries, … | `Com_*` names have distinct session/global values and supported commands are live; unsupported commands remain truthfully zero, while engine/latency families remain absent ([`InformationSchema.fs`](src/Fsdb/Engine/InformationSchema.fs)) | low | divergence |
| Logging | general log, slow log, error-log file | `mysql.general_log` and `mysql.slow_log` expose their catalog schemas but remain empty; diagnostics go to credential-redacted stderr ([`Log.fs`](src/Fsdb/Foundation/Log.fs)) | low | divergence |
| Replication | binlog, GTID, source/replica channels | no replication execution; REPLICATION privileges are vocabulary only and internal WAL is not a binlog; native catalog schemas and stock group-action configuration rows exist for metadata compatibility | architectural | refusal |

## 15. Differential-testing and performance tails

The remaining campaign is planner constant factors. Indexed joins,
equality/`IN`, and secondary ranges retain measurable fixed overhead compared
with MySQL.

The [composite-prefix profile](benchmarks/results/582cff7-quick.md) compares a
maintained left-prefix lookup with an expression-forced scan on the same data.
It confirms that the bounded access path removes the scan cliff while also
showing the remaining per-candidate gap to MySQL.

The [correlated composite-prefix profile](benchmarks/results/b2b0bca-quick.md)
measures the same access shape through a pass-through derived table and includes
an executor control. The follow-up
[text-prefix cardinality profile](benchmarks/results/a50142b-quick.md) shows the
fully covered `COUNT(*)` path reading a compatible index bucket directly.
Collation-changing and coercive comparisons deliberately retain row evaluation.

The [correlated planner baseline](benchmarks/results/b9a6895-quick.md) covers
nested derived tables, chained CTEs, source filters, and range predicates.
Equality probes retain a small fixed setup cost; the filtered and range shapes
already avoid the scan cliff on the recorded corpus.

The engine already avoids several earlier cliffs:

- low-cardinality joins can push safe source-local predicates below fan-out and
  stream plain `COUNT(*)`;
- flat boolean full-text searches score from postings, while required groups
  narrow their candidate set;
- mutation scans retain matched targets only, and unordered limits stop early;
- eligible direct column/literal predicates bind comparison metadata once per
  statement, including leaves inside `AND` and `OR` trees.

Shared statement setup, computed scan predicates, phrase/proximity matching,
and broader join-shaped full-text plans remain input-sensitive. Immutable
[benchmark artifacts](benchmarks/results/) hold the measurements; this ledger
records the open shape rather than copying numbers that go stale.

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
   correlated forms that cannot use a direct equality lookup over a physical
   or already-materialized source. Correctness holds, but scale still diverges
   from MySQL past small data.

2. Transaction scheduling. Indexed point/range UPDATE and DELETE statements
   wait and rebase, while the remaining transaction write shapes still rely on
   optimistic catalog merge.

3. Complex join-derived updatable views. Procedures, functions, and triggers
   cover nested calls with local OUT/INOUT targets, typed locals, condition
   handlers, SIGNAL/RESIGNAL, branches, labeled loops, cursors, and sequential
   data-changing statements.

4. Geographic SRS behavior. Planar spatial indexes, overlays, independently
   configurable buffers, topology predicates, equality, and convex hull are
   covered.

5. Extensible authentication providers. The built-in caching-SHA2, SHA-256,
   and native password exchanges are covered; external identity providers and
   proxy-user selection are not.

6. Replication, logging, broad engine counters, and the remaining metadata
   tail. Core command counters are live; replication remains architectural.
