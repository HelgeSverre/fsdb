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
| [Built-in functions](#3-built-in-functions) | Broad scalar, aggregate, JSON, temporal, planar geometry, and geographic distance/length coverage | Non-point geographic distance/topology and broader SRS definitions |
| [Data types](#4-data-types-and-values) | Common scalar, temporal, JSON, and OGC geometry values | Binary JSON representation |
| [Constraints and indexes](#5-constraints-and-indexes) | Constraints and common equality, range, ordering, grouping, join, and spatial probes | Arbitrary expression ordering and broader grouping paths |
| [Charsets and collations](#6-charsets-and-collations) | ICU-backed charset and collation registry | Exact MySQL UCA weight tables |
| [Transactions](#7-transactions-and-concurrency) | Supported isolation levels, row ownership, optimistic merge, and XA | Remaining coarse write shapes |
| [Persistence](#8-persistence-and-durability) | Opt-in WAL, snapshots, recovery, rotation, and group commit | Foreground rather than background row reclamation |
| [Views and triggers](#9-views-and-triggers) | Single-table, nested, and restricted join views; ordered compound triggers | Complex updatable views |
| [Routines and events](#10-stored-routines-events-schedulers) | Procedures, functions, and scheduled events are persisted and executable | No confirmed gap currently recorded |
| [Full-text](#11-full-text-search) | Maintained inverted indexes and MySQL-shaped scoring | CJK parsing and remaining plan combinations |
| [Wire protocol](#12-wire-protocol-and-prepared-statements) | Prepared statements, TLS, compression, LOCAL INFILE, and multi-results | GTID state tracking and live TLS certificate reload |
| [Authentication](#13-authentication-and-privileges) | Host accounts, caching-SHA2/SHA-256/native credentials, grants, roles, proxy grants, and account policy | Pluggable identity and proxy-user selection |
| [Metadata and administration](#14-metadata-server-administration-logging-replication) | Broad metadata catalogs and live command/session state | Engine-owned contents, logging, and replication |

## 1. SQL statements and parser

Quoted table targets preserve literal dots and escaped backticks through the
[audited DDL, DML, view-write, and cross-database rename paths](torture/findings/2026-10-09-quoted-table-names.md).
The same audit covers CREATE DATABASE affected counts and empty SHOW INDEX
result types, including text collations paired with binary metadata flags.

The application-facing DML surface includes `INSERT`/`REPLACE ... SET`, ODKU,
`IGNORE`, and multi-table forms. `DELETE IGNORE` covers the
[audited conversion-warning policy](torture/findings/2026-10-08-mutation-conversion.md)
and foreign-key row skipping with ordered LIMIT, single-target joined deletion,
and trigger rollback. The [audited delete cases](torture/findings/2026-10-08-delete-ignore.md)
include local trigger-warning lifetimes; other ignored-delete error classes and
multi-target combinations require further native coverage. `SELECT` covers joins, derived and lateral
sources, `JSON_TABLE`, expression subqueries, set operations, windows, rollups,
and ordinary or recursive query-scoped CTEs. CTEs can lead UPDATE or DELETE and
appear within set-operation branches.

The SELECT modifier pins the complete join order; table-level `STRAIGHT_JOIN`
preserves its left-prefix dependencies while allowing independent inner joins
to move earlier. The table form accepts ON, USING, or no condition and works in reads,
joined mutations, and updatable views. Parentheses around the left FROM chain
preserve its association and allow rendered join definitions to parse back
([oracle and regressions](torture/findings/2026-10-07-straight-joins.md)).

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
| Identifier case policy | fsdb implements and advertises MySQL's `lower_case_table_names=2` behavior for table lookup, hint aliases, generated foreign-key prefixes, and audited diagnostics; the [mode-matched full wire run](torture/findings/2026-10-09-identifier-case-policy.md) passes. MySQL fixes this setting at initialization; fsdb cannot select modes 0 or 1, so the pinned Linux server's default mode 0 differs. | low | subset |
| Server-side files | `IMPORT TABLE` is unsupported | low | refusal |
| Table maintenance | `CHECKSUM TABLE` uses a stable fsdb row checksum rather than MySQL's engine-specific value; supported `FLUSH` forms operate on fsdb state rather than InnoDB internals | low | divergence |
| ALTER execution | Accepted changes publish one immutable root; [audited algorithm selection and COPY affected-row counts](torture/findings/2026-10-08-alter-copy-counts.md) match, including populated foreign-key additions, charset conversions, and length-prefix changes; InnoDB physical algorithms and lock durations do not exist, while [audited expression-default binlog conditions](torture/findings/2026-10-08-alter-defaults.md) match | low | divergence |
| Storage engines | Known engine names still use fsdb's shared InnoDB-shaped row store; physical DATA/INDEX DIRECTORY placement is rejected unless `NO_DIR_IN_CREATE` discards it | low | divergence |
| HASH partitions | Named HASH/LINEAR HASH definitions, comments, and reorganization use the shared row store; InnoDB engine clauses and default inference are validated, and row hints, node groups, and file-per-table declarations are retained; physical partition pruning and placement remain absent ([oracle](torture/findings/2026-10-07-hash-partition-reorganization.md)) | low | divergence/refusal |
| Administration and replication | Replication source, binlog purge/reset, plugin/component installation, instance, and tablespace statements are unsupported | low | refusal |
| EXPLAIN | JSON/TREE expose the logical plan without MySQL's cost model; ANALYZE reports aggregate rather than per-iterator observations | low | divergence |

### SELECT-level syntax gaps

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Grouped join operands | nested table references preserve join association and inner ON scope | association, merged-column ownership, scoped binding, mutations, locks, and source metadata are covered; grouped LATERAL/JSON_TABLE sources receive preceding rows, and direct and grouped dependent mutations retain target identities; nested derived sources retain enclosing query context; broader correlated-source/metadata combinations remain ([oracle](torture/findings/2026-10-07-grouped-joins.md)) | medium | partial |
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
access paths. An indexed join whose runtime probe cannot be represented by the
index scans and checks its full `ON` condition before returning or padding rows,
including `USING` and `NATURAL` joins. Full-result plans compare the prefix's
observed candidate count with one hash build, while remaining equality
conditions stay residual
predicates. `ORDER BY ... LIMIT` uses a bounded top-N sort.

Direct single-table equality predicates likewise use complete keys or safe
left prefixes for reads and mutations. Literal probes and conservative
row-independent numeric expressions share that path across equality, scalar
or row-value membership, direct numeric ranges, and `BETWEEN`. Their observed
candidate count chooses between the index slice and a row-store scan. When
several supported access families apply, the narrowest observed candidate set
is used consistently by reads, mutations, locking reads, and `EXPLAIN`.
Fully covered `OR` branches union their equality, membership, range, or spatial
candidates. One uncovered branch keeps the whole disjunction on the scan path.
Compatible `AND` branches intersect physical candidates for exact mutation and
locking scopes. Reads build the intersection only for broad inputs with a
strong observed reduction; otherwise the narrowest single index remains
cheaper in the in-memory row store.
Compatible `ORDER BY` suffixes continue streaming the same composite slice
rather than sorting the narrowed rows again.

Statement-stable scalar, `EXISTS`, `IN`, `ANY`, `SOME`, and `ALL` subqueries
materialize once per statement. Compatible scalar and row-value membership
tests reuse typed sets and can narrow a directly indexed outer table.
Compatible direct-column scalar lists use the same statement-scoped membership
representation.

Correlated equality probes use single keys, complete composite keys, or safe
ordered left prefixes through direct tables and pass-through derived tables or
CTEs. Outer values and literals may bind different key parts. Correlated forms
preserve MySQL NULL and multi-column error semantics.

Execution also covers `WITH ROLLUP`, numeric and temporal window frames,
multi-column `COUNT(DISTINCT ...)`, the `GROUP_CONCAT` byte ceiling,
statement-atomic multi-table DML, and exact ODKU affected-row counts. Row
comparisons retain null-safe behavior and MySQL's 1241 error for invalid
multi-column subqueries, including empty row-IN results; empty-group bit
aggregates retain their identities.

Row-local recursive CTE members prepare their invariant predicate, projection,
and type coercions once; members needing joins, grouping, windows, full-text,
or locking retain the general SELECT pipeline.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Secondary-index access paths | ref/eq_ref/range/index-merge scans feed joins, DML, ORDER BY, GROUP BY | common complete-key and safe left-prefix equality, literal membership, range, join, ordering, grouping, supported unary functional compositions, compatible functional-result ranges, fully covered OR unions, and cost-effective AND intersections use maintained indexes; arbitrary expression ordering and broader grouping still scan or sort | high (scale) | divergence |
| Optimizer | pushdown, constant folding, join reordering, cost model, statistics | physical inner joins with qualified or unambiguous bare references and source-local predicates use shape- and cardinality-driven choices; competing access families, complete OR unions, and selective AND intersections use observed cardinalities; local STRAIGHT_JOIN constraints permit independent selective joins to move earlier; general expression folding, outer/lateral join reordering, ambiguous bare references, and plans needing persisted statistics retain conservative execution | medium | divergence |
| Optimizer hints | query-block, table, and index target resolution with ordered diagnostics and plan controls | shared ordered syntax parsing, audited timeout/geometry effects and nested SET_VAR precedence, SELECT and audited mutation table/query-block/index resolution, family conflicts, audited CTE instantiation, lexical scope and repeated syntax warnings, diagnostic ordering, preparation warning lifetimes, and audited bound-query join-hint elimination and [merged/materialized source warning ownership](torture/findings/2026-10-08-join-hint-merging.md) work; optimizer_switch configuration, broader hint combinations, stored-program contexts, and physical plan controls remain incomplete ([SELECT cases](torture/findings/2026-10-08-hint-families.md), [mutation cases](torture/findings/2026-10-08-mutation-hints.md)) | medium | divergence |
| Mixed-type join filters | numeric `IN` conditions can move between collation-equivalent text keys as join order changes | keeps the original comparison domain; the [controlled join-order case](torture/findings/2026-10-07-fulltext-join-bounds.md#mixed-type-in-remains-open) differs when MySQL transfers the filter to the other key, with or without MATCH | medium (result membership) | divergence |
| EXPLAIN fidelity | type ∈ system/const/eq_ref/ref/range/index/index_merge/ALL; FORMAT=JSON/TREE; ANALYZE; optimizer_trace | access types cover compatible direct bounds/orderings, index unions and intersections, and source-local join probes; JSON/TREE plans and aggregate ANALYZE observations work, while per-iterator timing/costs and optimizer trace rows remain absent | low | divergence |
| Subquery strategies | semi-join/materialization/early-exit transformations | stable subqueries materialize once; common correlated equality/range shapes and compatible functional probes over physical or pass-through projected sources use maintained indexes; variable-bearing, nondeterministic, lateral, JSON_TABLE, and more complex correlated forms re-execute | medium (scale) | divergence |

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
- NULL-selection, comparison, and session identity functions;
- OGC geometry construction, serialization, inspection, and member access,
  including typed text/binary constructors, line endpoints and indexing,
  polygon rings, multi-geometry members, and closure checks.

| Missing family | Functions | Impact |
|---|---|---|
| Remaining geographic spatial behavior | non-point distance and topology beyond line length, plus reference systems beyond EPSG 4326 | low |

`CONVERT_TZ` and the session `time_zone` resolve numeric offsets, `SYSTEM`,
and named zones populated in `mysql.time_zone*`. Leap-second-aware named zones
(`Use_leap_seconds = Y`) remain unsupported.
HEX preserves audited decimal rounding and [expression-sensitive DOUBLE
conversion](torture/findings/2026-10-09-hex-expression.md), including selected
conditional branches and stored-column warnings. Broader integer-conversion
contexts and materialized scalar-subquery shapes remain unaudited.
`WEIGHT_STRING()` returns host-ICU sort-key bytes for textual collations, not
MySQL's UCA weight-table bytes.
SUM and AVG convert text before accumulation, including single-row windows;
their DISTINCT forms compare converted numbers while COUNT retains text
collation equality. Text and binary-string conversions emit numeric-prefix
warnings; DISTINCT suppresses those conversion warnings. Window aggregates
materialize each argument once per input, including volatile expressions.
Growing ROWS and RANGE frames accumulate SUM/AVG incrementally; bounded sliding
frames retain repeated conversion warnings. Offset RANGE frames preserve NULL
peers at unbounded edges.

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
calendar validation. Temporal scalar functions apply the session's six-digit
rounding or truncation policy to string inputs, including TIME hour carry,
range clamping, and one warning per clamped argument; trigger and stored-routine
scalar parsing uses the object's captured SQL mode
([native oracle](torture/findings/2026-10-09-time-part-fractional.md)).
`SEC_TO_TIME` and `MAKETIME` overflow warnings retain the source value and
seconds precision ([native oracle](torture/findings/2026-10-09-time-constructor-warnings.md)).
`ADDTIME`, `SUBTIME`, and `TIMEDIFF` report overflowing TIME results with
their function-specific warning precision and clamp invalid TIME operands
before arithmetic in the audited combinations; an invalid second `ADDTIME`
operand can still produce one extra warning when its overflow text repeats
([native oracle](torture/findings/2026-10-09-time-arithmetic-warnings.md)).

Numeric offsets appended to DATETIME and TIMESTAMP inputs are converted into
the session `time_zone`. TIMESTAMP values then retain the UTC instant and
follow later session-zone changes; DATETIME values retain the converted
wall-clock fields. TIMESTAMP range validation happens after conversion,
including MySQL's reserved epoch zero and 2038 upper boundary.

JSON, functional defaults, virtual generated columns, normalized comments,
and OGC WKB geometry values also persist through the regular value and wire
metadata paths. Virtual generated values are recomputed when queried.

Integer literals and Boolean expressions retain MySQL's BIGINT result family
and expression widths. Arithmetic, DIV, and integer MOD derive widths from
operand precision and signedness; prepared results retain those descriptors.
Bare binary literals contribute byte-capacity precision but do not make an
arithmetic result unsigned. Integer user variables reserve their declared width independently
of the current value ([oracle](torture/findings/2026-10-06-prepared-parameter-repreparation.md#integer-expression-descriptors)).


Approximate arithmetic and conditional results derive width and scale from their
operands, independently of stored FLOAT/DOUBLE column widths. Unary numeric
functions, rounding, and floating-point casts retain their expression descriptors
through PREPARE. Fixed-scale numeric text preserves shortest meaningful digits
and pads to the declared scale; binary SELECT rows retain computed doubles,
while UNION materialization rounds to its combined scale
([oracle](torture/findings/2026-10-06-prepared-parameter-repreparation.md#approximate-expression-descriptors)).

FLOOR/CEILING choose BIGINT or scale-zero DECIMAL from the declared input
precision and preserve large DOUBLE values. ROUND/TRUNCATE use shared bounded
digit-count coercion, infer constant precision expressions, and retain exact
BIGINT values. Runtime precision keeps its declared metadata boundary
([oracle](torture/findings/2026-10-06-prepared-parameter-repreparation.md#rounding-precision-and-exact-values)).

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Spatial indexes and operations | R-tree indexes and geographic SRS rules | maintained immutable MBR indexes narrow direct `MBRINTERSECTS`, `MBRWITHIN`, and `MBRCONTAINS` predicates for SRID 0; planar overlays, geometry property/member accessors, and independently configurable square/circle point, flat/round end, and miter/round join buffer strategies work; EPSG 4326 constructors and text/binary serializers honor MySQL axis options and coordinate domains, point distance and line length use MySQL's Andoyer strategy and linear-unit registry, and `ST_DISTANCE_SPHERE` supports point and multipoint inputs with MySQL's default, SRS-derived, or explicit radius; broader non-point geographic distance/topology and other SRS definitions remain absent, and the internal augmented interval tree is not an R-tree | low | subset |
| Constant unary negation inference | constant integer expressions can promote to DECIMAL; runtime BIGINT operands retain overflow checks | casts, arithmetic, CASE, and audited original builtin constants support promotion through the shared evaluator; unaudited constant functions remain incomplete. Mixed signed/unsigned BIGINT conditional results use DECIMAL throughout arithmetic; column and parameter expressions whose result remains BIGINT retain runtime overflow checks ([oracle](torture/findings/2026-10-06-prepared-parameter-repreparation.md#constant-negation-and-mixed-integer-results)) | low | divergence/refusal |
| Binary-literal expression boundaries | context-dependent numeric interpretation and expression descriptors | bare bit/hex literals retain numeric origin through arithmetic, casts, aggregates, IF/CASE, and source-free scalar subqueries with absent or supported constant-true conditions; variables, derived columns, and non-reduced scalar subqueries erase it. Conditional reduction shares execution semantics for literal operators and audited original builtins, and preserves runtime variable/parameter boundaries. NOT and IF condition simplification follow MySQL’s three-valued logic boundaries; ordinary comparisons, casts, and COALESCE retain runtime operands. Broader constant-function reduction remains incomplete ([oracle](torture/findings/2026-10-06-prepared-parameter-repreparation.md#binary-literal-context-boundaries)) | low | divergence |
| Numeric expression descriptors | widths and scales depend on expression family and literal spelling | common exact and approximate arithmetic, conditionals, numeric rounding, casts, and aggregates retain operand-derived descriptors; scientific literal spelling survives projections, views, and CTAS. Broader scalar/function descriptors remain incomplete ([oracle](torture/findings/2026-10-06-prepared-parameter-repreparation.md#rounding-precision-and-exact-values)) | low | divergence |
| JSON representation | binary DOM, member-of/path ops on it | `Value.VJson` stores raw text, re-parsed per operation | low (perf) | divergence |

## 5. Constraints and indexes

Composite primary and unique keys use collation-aware encodings and MySQL NULL
semantics. Index maintenance is incremental, supports mixed ascending and
descending keys, and keeps invisible constraint indexes out of ordinary plans.

Foreign keys use unique parent probes, cycle-safe cascade, set-null, and
restrict actions, qualified cross-database targets, and the session
`foreign_key_checks` gate. [Audited candidate validation and diagnostics](torture/findings/2026-10-09-foreign-key-validation.md)
cover self-references, missing parents, and changes to supporting index records.
Native audits cover [ignored updates](torture/findings/2026-10-09-update-ignore.md),
[constraint numbering](torture/findings/2026-10-09-foreign-key-names.md),
[supporting indexes](torture/findings/2026-10-09-foreign-key-indexes.md), and
[collision diagnostics](torture/findings/2026-10-09-foreign-key-collisions.md).
[Required-index protection](torture/findings/2026-10-09-foreign-key-lifecycle.md),
[generated-index lifecycle](torture/findings/2026-10-09-foreign-key-index-origin.md),
[rename collisions](torture/findings/2026-10-09-foreign-key-rename.md), and
[combined definition changes](torture/findings/2026-10-09-foreign-key-alter.md)
also match the tested cases. [Column-change probes](torture/findings/2026-10-09-foreign-key-column-changes.md)
cover compatible type families, incompatible type and collation changes, and
drop/re-add actions.

Named CHECK constraints support enforcement state
and `ALTER` validation, and ENUM or SET values enforce membership. Adding a
unique key over colliding data returns 1062 without publishing a corrupt index.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Remaining foreign-key column changes | preserves constraint validity through column changes | tested compatible type and nullability changes, rejected incompatible changes, disabled-checks behavior, [combined ADD/DROP FOREIGN KEY with MODIFY or CHANGE in both orders, atomic rejection, final-column renames, `BIT`/binary compatibility, `ENUM`/`SET` storage widths, constrained-column layout changes, and stored generated-column action limits](torture/findings/2026-10-09-foreign-key-column-changes.md), and virtual generated-column FK rejection now match; broader rare type and ALTER-action combinations remain unverified | low | unverified |
| Non-unique secondary indexes | physical structures serving lookups/ordering | separate immutable equality and ordered structures cover common complete-key and left-prefix probes, joins, ranges, ordering, grouping, and supported unary functional compositions; unsupported expression orderings and grouping shapes retain scan/sort fallback | high (scale) | divergence |
| Expression indexes | functional key parts participate in physical access and uniqueness | the [supported functional keys](README.md#indexes-and-joins), including compatible unary compositions, have physical equality, uniqueness, ordering, and grouping paths; other non-unique expressions retain DDL and metadata but scan, while unsupported unique expressions are refused | low | divergence/refusal |

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
| Advanced REGEXP grammar | ICU regular expressions and Unicode properties | bounded .NET regex with all fourteen POSIX class names, negated forms, positive and mixed-negated class combinations, [audited Unicode categories, `\w`/`\W`, bracket-local `\b`/`\B`, horizontal/vertical/`\R` line breaks, and `\Q...\E` literal quoting with quoted-interval diagnostics](torture/findings/2026-10-09-regexp-posix-classes.md), plus mapped malformed patterns; ICU script/binary properties, supplementary-scalar matching, broader word boundaries, other grammar, and error-code distinctions can differ | low | divergence |
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
New DDL WAL records retain the originating `ALLOW_INVALID_DATES` setting so
replay preserves both accepted invalid calendar dates and intentional
non-strict coercion ([recovery regression](torture/findings/2026-10-09-invalid-date-ddl-replay.md)).

Group commit, ordered checkpoint barriers, lock-step rotation, shutdown
rotation, decode-depth limits, generated-expression codecs, and durable XA
records share the same persistence path. XA records retain logical lock claims
so startup can rebuild their row/key ownership. Snapshots include prepared
branches' recovery bases, events, and lock claims, so checkpoint rotation can
truncate the WAL while branches remain prepared.

The durability campaign forces repeated automatic rotations, appends a
WAL-only commit, crashes the server, and verifies the recovered transaction
sets before and after a graceful snapshot restart.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Durability default | durable unless configured otherwise | in-memory unless `--data-dir` passed; process death loses everything | medium (deployment) | divergence |
| Legacy invalid-date DDL WAL | DDL recovery retains accepted temporal values | older WAL records without the originating `ALLOW_INVALID_DATES` flag remain readable but can replay ambiguous invalid-date defaults or ALTER coercions differently; a snapshot taken before upgrading avoids this ambiguity | low (legacy recovery) | subset |
| Prepared XA ngram recovery | MySQL 8.4.11 can commit a recovered row without restoring its pending ngram posting | fsdb preserves recorded document tokenizers and postings through live commit, WAL replay, and snapshots ([oracle and regression](torture/findings/2026-10-07-ngram-xa-recovery.md)) | low (edge-case parity) | divergence |
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

The implemented routine and event boundary is summarized in the
[compatibility guide](docs/compatibility.md#stored-routines-and-events).
In stored programs, user-variable SET supports local expressions, sequential
assignments, and CONTINUE handler resumption; assignments remain visible to callers after
nested calls and errors ([oracle](torture/findings/2026-10-07-stored-function-set.md)).
ALTER PROCEDURE/FUNCTION retain comments, security and data-access characteristics,
alteration metadata, and persisted definitions. Current routine diagnostics and
connection-local timeout-hint loading warnings follow the audited native lifecycle
([oracle](torture/findings/2026-10-08-select-timeout.md#audited-routine-lifecycle-and-alter-support)).

[Audited stored-function writes during UPDATE](torture/findings/2026-10-09-routine-update-writes.md)
share the invoking statement's snapshot. Assignment, WHERE, and JOIN calls
retain their writes on success and roll them back on statement failure.
COMMIT, ROLLBACK, isolation-level visibility, and durable recovery are covered.
No additional confirmed gaps are currently recorded for this area.

## 11. Full-text search

Natural-language, boolean, and query-expansion modes use MySQL 8.4's
oracle-verified TF × IDF² scoring with its epsilon floor and built-in stopword
list. Word stopwords are filtered when indexing; ordinary queries can still
match surviving collation-equivalent or historical postings
([oracle evidence](torture/findings/2026-10-07-fulltext-word-stopword-postings.md)). Boolean syntax covers
`+ - > < ~ word* "phrases" @N proximity ()` with depth cap; blind
relevance-feedback expansion and bare WHERE-MATCH relevance ordering are also
supported.

FULLTEXT DDL, introspection, column validation, and indexed-column collation
semantics share immutable term-frequency and position postings. Nonbinary
full-text tokens fold case, including under case-sensitive collations, while
retaining the indexed collation's accent sensitivity
([oracle](torture/findings/2026-10-07-fulltext-case-folding.md)). DML maintains
the postings incrementally, while recovery rebuilds them once. Direct
WHERE-MATCH candidates stream by stable row identity.

Flat boolean word expressions score directly from exact or prefix postings.
Required terms begin with the smallest posting; optional, excluded, raised,
lowered, and soft terms probe a smaller upstream candidate set or accumulate
only touched rows. Phrase and proximity conditions intersect their word
postings, retain per-word relevance, and check candidate documents with
ordered matching or a linear sliding window.

Natural-language word queries preserve quoted phrases within each indexed
column, including internal short words and stopwords. Mixed terms and phrases
are alternatives; repeated query words retain MySQL's relevance calculation.
Query expansion preserves phrase conditions while adding seed terms
([oracle evidence](torture/findings/2026-10-07-fulltext-natural-phrases.md)). Binary exact
phrases retain case-sensitive candidate postings and verify lowercased
document text at original anchor positions; single-token and proximity
queries retain their distinct lookup rules
([oracle](torture/findings/2026-10-07-fulltext-case-folding.md#binary-phrase-verification)).

The grouped evaluator begins with the smallest required child and probes the
other required children; without one, only positive children seed candidates.
Bounded AND/OR predicate trees intersect or union MATCH candidates before
residual evaluation.

Single-table reads and writes intersect compatible equality, indexed `IN`,
range, and spatial candidates before scoring. Inner joins also propagate compatible
bounds through equality chains without shrinking the relevance corpus. Multi-table
UPDATE/DELETE score each physical source before evaluating joins, predicates,
and assignments.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| MATCH planning | optimizer can combine FULLTEXT access with every other access path | bounded AND/OR MATCH predicates stream posting candidates; compatible source-local equality, indexed `IN`, range, and spatial candidates restrict scoring for predicates and projections in single-table and all-inner-join queries; implicit relevance ordering can drive either side of a qualified two-table inner join when the other side is an exact unique probe; [equality chains](torture/findings/2026-10-07-fulltext-join-bounds.md) propagate compatible equality, range, and literal `IN` bounds across inner/cross joins without shrinking the relevance corpus; mixed comparison domains and broader join-shaped combinations still score each owning corpus before joining | medium (scale) | divergence |
| Transaction relevance | MySQL uses an approximate global table-row estimate for ranking | visibility cases agree; fsdb uses the committed corpus count rather than estimates that remain changed after rollback until ANALYZE ([controlled oracle](torture/findings/2026-10-07-fulltext-transaction-visibility.md#relevance-and-implementation-boundary)); follows the deliberate statistics policy in section 16 | high (relevance correctness) | deliberate divergence |
| CJK | ngram and mecab parsers, WITH PARSER clause | ngram DDL, search modes, stopword and phrase boundaries, mutation, startup token sizes, and preserved postings across WAL/snapshot recovery are implemented; common metadata-only MODIFY/CHANGE column definitions also preserve historical postings ([recovery oracle](torture/findings/2026-10-07-ngram-startup-sizing.md)); MeCab remains open ([oracle and regressions](torture/findings/2026-10-07-ngram-fulltext.md)) | medium (for CJK) | partial |

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

Physical result columns report primary, unique, composite, and non-unique key
membership consistently across queries, prepared statements, `COM_FIELD_LIST`,
and `HANDLER`. Prepared descriptors derive schema, operator, aggregate,
overloaded scalar, temporal, JSON, spatial, and registered-extension result
families without evaluating the statement. Typed numeric builtin and cast
arguments are coerced before expression evaluation for both protocol and SQL
prepared statements. `LIMIT` and `OFFSET` retain MySQL's distinct binary-
protocol and SQL user-variable validation rules.
Unaliased projection names retain source spelling through SQL modes, prepared
binding, metadata inference, and view rendering. Literal names and `NAME_CONST`
follow their value and source-specific rules
([oracle](torture/findings/2026-10-07-expression-labels.md)).

Each prepared handle retains its derived parameter types independently of the
binary protocol's cached encodings. Incompatible supplied types rederive the
whole statement, while compatible executions retain their numeric and temporal
families. NULL projections retain declared metadata; a reprepare restores a
NULL marker's original expression context.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| TLS certificate lifecycle | live certificate/trust-store reload and CRL validation | server and client-CA certificates are loaded when the listener starts; client chains are validated without revocation checks | low (rotation requires restart) | subset |
| Prepared user-variable typing | direct references retain their prepare-time type and refresh on statement reprepare | Parsed statements, including user/system variable `SET` assignments combined with `NAMES`, retain direct variable types and refresh them when explicit parameters force reprepare. Referenced table and view definitions also refresh captured types after DDL. Common decimal arithmetic, rounding, and coalescing now propagate result scales and preserve division guard digits. Common expression precision descriptors are derived from operand shapes, and SUM/AVG distinguish untyped NULL and approximate/text inputs from exact numeric inputs; broader prepared query shapes and expression families, and values beyond System.Decimal precision remain incomplete ([evidence](torture/findings/2026-10-06-prepared-parameter-repreparation.md#direct-user-variable-references)) | medium | divergence |
| Literal and expression charset identity | literals retain their charset, collation, coercibility, and binding context | broader stored-program binding, quoted-string conversion, non-Unicode/UCS-2 byte handling, definition rendering, and expression charset inference remain incomplete; introduced UTF-8/16/32 and audited SJIS/CP932/Big5/GBK/EUC/GB2312/GB18030 hex/bit validation, fixed-width padding, audited ASCII/UCS-2/UTF8MB3 byte preservation, audited encoded reversal, slicing and CONCAT, derived-prefix and audited text-storage conversions (including UCS-2 surrogate code units, length limits, and ASCII diagnostics), and preparation warning lifetimes are [native-verified](torture/findings/2026-10-08-introduced-encoding.md); source-expression collations and coercibility now survive the audited view, derived, join, UNION, and ROLLUP paths ([native contracts and boundaries](torture/findings/2026-10-08-text-literal-charsets.md)) | low | divergence |
| Cursor storage | materialized temporary tables spill from memory to disk | read-only, forward-only cursors retain their materialized rows in session memory until exhaustion, reset, close, or commit | low (large concurrent cursors) | divergence |
| Session state tracking | schema, system-variable, generic state, transaction, and GTID trackers | schema, configured system-variable, generic state-change, transaction-characteristic, and transaction-state blocks are encoded in final OK packets; GTID blocks remain absent because fsdb has no binlog | low | subset |
| Diagnostics coverage | warnings from conversions, truncation, deprecated syntax, and storage engines | statement errors, ignored INSERT/CHECK rows, non-strict integer/ENUM/SET/charset coercions, DECIMAL scale-loss notes, declared text/binary truncation, functional-index, numeric-aggregate, and [audited Boolean-context conversion conditions](torture/findings/2026-10-08-predicate-conversion.md), conditional DDL, ignored physical-directory options, unknown-engine substitution, GROUP_CONCAT truncation, deprecated numeric displays, `utf8` aliases and explicit `utf8mb3` declarations/conversions, plus `SQL_CALC_FOUND_ROWS`, `FOUND_ROWS()`, and ODKU `VALUES()` are captured; [trigger warning lifetimes and RESIGNAL condition order](torture/findings/2026-10-08-trigger-warnings.md) are covered; [audited integer CAST and conditional warning behavior](torture/findings/2026-10-08-integer-casts.md) is covered; [audited duplicate-key messages](torture/findings/2026-10-08-key-diagnostics.md) retain the base table and key; [audited missing-object diagnostics](torture/findings/2026-10-08-missing-table.md) preserve database/table identity and distinguish 1049/1051/1146; [single-column ALTER coercion conditions](torture/findings/2026-10-08-alter-coercion.md) preserve live row ordinals and stop at the first duplicate; [audited multi-column ALTER conditions](torture/findings/2026-10-09-alter-row-order.md) follow final-column order and preserve every error from the first failing row; [audited expression-assignment deprecation warnings](torture/findings/2026-10-09-assignment-deprecation.md) preserve syntax counts and preparation timing; [audited foreign-key failure messages](torture/findings/2026-10-09-foreign-key-validation.md) include constraint details; [audited foreign-key name/index collision diagnostics](torture/findings/2026-10-09-foreign-key-collisions.md) are covered; ALTER temporary-table identities, broader conversion contexts, and other warning producers remain divergent | low | divergence |
| System variables | hundreds live | common connector, limit, transaction, password-policy, week-format, and fixed-offset, `SYSTEM`, and catalog-backed named time-zone variables are live; most others are inert or absent, `div_precision_increment` controls division and AVG and is retained by prepared statements ([oracle](torture/findings/2026-10-06-prepared-parameter-repreparation.md#division-precision-increment)), `max_execution_time` settings, audited SELECT deadlines, scalar interruption, and transaction preservation work; audited timeout-hint precedence, preparation diagnostics, and routine-loading warning lifetimes work, while broader hint contexts and extreme-duration parity remain open ([native timeout contract](torture/findings/2026-10-08-select-timeout.md)), and `system_time_zone` retains its static bootstrap label | medium | divergence |

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
| INFORMATION_SCHEMA breadth | INNODB_*, KEYWORDS, PLUGINS, spatial-reference catalogs, and usage views | MySQL 8.4's catalog names are present. `INNODB_TRX` projects active transaction identity, lifecycle, isolation, checks, logical write weight, and held row stripes; fields that require InnoDB's lock-memory and scheduling internals remain zero or NULL. Other InnoDB dictionary views project live table, column, index, statistics, and virtual-column metadata; physical diagnostics return truthful empty rowsets where fsdb has no matching buffer-pool, tablespace, compression, or metrics subsystem. `ST_SPATIAL_REFERENCE_SYSTEMS` exposes fsdb's supported SRID 0 and EPSG 4326 rows instead of MySQL's full EPSG registry | low | divergence |
| Table statistics | estimates refreshed by ANALYZE TABLE | `InformationSchema.tablesRows` reports InnoDB, a 16384 DATA_LENGTH stand-in, CARDINALITY 0, and live row counts where MySQL keeps stale page estimates until ANALYZE | low | divergence |
| Optimizer cost overrides | `mysql.server_cost` and `mysql.engine_cost` values feed plan costs after `FLUSH OPTIMIZER_COSTS` | both tables expose MySQL's bootstrap rows, generated defaults, and mutable override columns; fsdb's shape-driven planner does not consume their overrides | low | divergence |
| SHOW STATUS counters | Com_*, Innodb_*, Slow_queries, … | `Com_*` names have distinct session/global values and supported commands are live; unsupported commands remain truthfully zero, while engine/latency families remain absent ([`InformationSchema.fs`](src/Fsdb/Engine/InformationSchema.fs)) | low | divergence |
| Logging | general log, slow log, error-log file | `mysql.general_log` and `mysql.slow_log` expose their catalog schemas but remain empty; diagnostics go to credential-redacted stderr ([`Log.fs`](src/Fsdb/Foundation/Log.fs)) | low | divergence |
| Replication | binlog, GTID, source/replica channels | no replication execution; REPLICATION privileges are vocabulary only and internal WAL is not a binlog; native catalog schemas and stock group-action configuration rows exist for metadata compatibility | architectural | refusal |

## 15. Differential-testing and performance tails

The [UPDATE IGNORE batching comparison](benchmarks/results/4ebf5f82-update-ignore-batching.md)
removes the measured per-row publication regression for updates without
triggers, incoming foreign keys, or custom-function calls. The valid 5,000-row
workloads take 73–74% less time and allocate 56–57% fewer bytes, with stable
plain-UPDATE controls. The excluded execution paths retain per-row publication
and are not claimed improved.

The remaining campaigns include planner and numeric-expression overhead.
Indexed joins, equality/`IN`, and secondary ranges retain measurable fixed
overhead compared with MySQL. The [numeric-expression snapshot](benchmarks/results/c29d188d-numeric.md)
also records higher scalar-subquery/prepared-expression latency and larger
text-to-number aggregate scan costs. Its repeatability check passes, but
between-run variation and the absence of a pre-change baseline limit it to
profiling guidance rather than a regression claim.

The [window snapshot](benchmarks/results/01e61930-windows.md) identifies offset
RANGE boundary lookup and stored-function window inputs as profiling candidates.
Smaller-size controls exceed the repeatability threshold under competing host
load, so its raw timings do not establish a regression or speedup.

The [post-compatibility comparison](benchmarks/results/10d2a1d8-windows.md)
provides a saved fsdb baseline with alternating target order. The subsequent
[RANGE boundary search measurements](benchmarks/results/range-boundary-search.md)
show a large reduction from replacing partition scans with binary searches
for compatible key domains. Mixed domains retain the scan fallback.
[Stored-function definition caching](benchmarks/results/stored-function-definition-cache.md)
removes repeated syntax parsing and roughly halves latency in the measured
window workload with function inputs. Function execution and aggregate/expression overhead
remain substantially above MySQL; shared-host load and control drift limit
precise timing claims.

Unless an artifact header says otherwise, the linked planner profiles compare
native in-memory fsdb with native durable MySQL. They expose query-shape and
scaling differences, not deployment-equivalent write cost. Durable write claims
require the matched WAL/durable pair produced by `just bench-durable`.

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

The [correlated functional-key profile](benchmarks/results/2bb13632-quick.md)
extends that coverage to compatible unary and composed expression indexes.
Direct physical sources and pass-through projections now probe the maintained
bucket; collation-changing comparisons and embedding overrides retain ordinary
row evaluation.

The [numeric functional-range profile](benchmarks/results/04686720-quick.md)
covers direct and correlated `ABS`/length-result bounds. The maintained ordered
slice removes fsdb's repeated-scan cliff on that corpus. MySQL's functional-key
spelling was slower than its forced-scan control in this short run, so the raw
artifact is retained without treating it as a general engine ranking.

The [text functional-range profile](benchmarks/results/ff58fd4c-quick.md)
covers direct and correlated composed string bounds. On the recorded corpus,
fsdb's maintained slice is about nine times faster than its direct scan and
about forty-four times faster than its correlated scan. MySQL strongly favors
the direct functional key but does not profit from the correlated spelling in
this short run.

The constant-expression lookup pair records the
[scan baseline](benchmarks/results/baa1b51-quick.md) and the
[indexed implementation](benchmarks/results/ede0c8e-quick.md). Safe numeric
arithmetic and untouched [functional-key built-ins](README.md#indexes-and-joins)
now use the same physical key path as a literal probe; extension overrides and
coercive text expressions retain row evaluation.

The follow-up membership-and-range pair records the
[scan baseline](benchmarks/results/36b0fa8-quick.md) and the
[indexed implementation](benchmarks/results/e1c2bbe-quick.md). It applies the
same conservative evaluator to scalar and row-value `IN`, numeric range bounds,
and compatible ordered composite suffixes.

The `BETWEEN` pair records the
[scan baseline](benchmarks/results/c54f2f0-quick.md) and the
[indexed implementation](benchmarks/results/89b9846-quick.md). Inclusive
literal and safe numeric-expression bounds now use the same range path in
reads, mutations, correlated probes, and compatible ordered composite suffixes.

The [competing-index baseline](benchmarks/results/cef5118-quick.md) and
[cardinality-arbitrated follow-up](benchmarks/results/0cc8aee-quick.md) exercise
an equality bucket and a single-row range that both satisfy one predicate. The
planner selects the narrower physical family consistently; the recorded short
run is effectively flat because parsing and wire overhead dominate this small
candidate difference.

The [indexed-disjunction profile](benchmarks/results/ba86713-quick.md) pairs a
fully covered equality-or-range union with an expression-forced scan. On the
recorded corpus, fsdb's union is an order of magnitude faster than its scan and
within the same latency order as MySQL's indexed path. This removes the
table-size cliff while leaving planner setup as the next constant-factor
target.

The [indexed-conjunction profile](benchmarks/results/9d6c895-quick.md) compares
the chosen `AND` plan with a forced single-index control and a full scan. A
naive intersection was slower than resolving the narrowest bucket and applying
the other condition as a residual. The cost gate now keeps that common query on
the single index: the two fsdb index forms are effectively level in the recorded
short run, while the forced scan remains an order of magnitude slower. Broad,
strongly reducing intersections remain available where their candidate savings
can repay the merge.

The engine already avoids several earlier cliffs:

- low-cardinality joins can push safe source-local predicates below fan-out and
  stream plain `COUNT(*)`;
- flat boolean full-text searches score from postings, while required groups
  narrow their candidate set;
- mutation scans retain matched targets only, and unordered limits stop early;
- eligible direct-column predicates bind comparison metadata once per
  statement, including literals and leaves inside `AND` and `OR` trees.
- already-materialized string operands skip output-format inference during
  text predicate evaluation ([same-host measurement](benchmarks/results/2026-10-09-regexp-wire-latency.md)).
- REGEXP expressions reuse a compiled pattern within a statement while the
  pattern, match type, and collation stay the same ([scan measurement](benchmarks/results/2026-10-09-regexp-wire-latency.md)).

Shared statement setup, computed scan predicates, phrase/proximity matching,
and broader join-shaped full-text plans remain input-sensitive. Immutable
[benchmark artifacts](benchmarks/results/) hold the measurements; this ledger
records the open shape rather than copying numbers that go stale.

## 16. Deliberate divergences (accepted, not targeted for parity)

Documented decisions that differ from MySQL intentionally:

- the additive `VECTOR` type and function family forward-ported from MySQL 9;
- live statistics rather than `ANALYZE`-stale estimates, including full-text
  ranking from the committed corpus rather than rollback-stale InnoDB row
  estimates ([evidence](torture/findings/2026-10-07-fulltext-transaction-visibility.md#relevance-and-implementation-boundary));
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

4. Broader geographic SRS behavior. EPSG 4326 construction, axis order,
   coordinate domains, ellipsoidal point distance, line length, spherical
   point/multipoint distance, and linear units are covered alongside planar
   spatial indexes and operations; broader non-point geographic distance and
   topology, plus other reference systems, remain absent.

5. Extensible authentication providers. The built-in caching-SHA2, SHA-256,
   and native password exchanges are covered; external identity providers and
   proxy-user selection are not.

6. Replication, logging, broad engine counters, and the remaining metadata
   tail. Core command counters are live; replication remains architectural.
