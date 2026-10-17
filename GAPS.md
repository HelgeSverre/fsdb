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
include local trigger-warning lifetimes, multi-target foreign-key blocking,
selected-child identity through CASCADE and SET NULL in either target order,
and the original selected child image in DELETE triggers after those actions;
other ignored-delete error classes and combinations
require further native coverage. `SELECT` covers joins, derived and lateral
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
| Table maintenance | `CHECKSUM TABLE` uses a stable fsdb row checksum rather than MySQL's engine-specific value; its [missing-object and view diagnostics, temporary-table lookup, and numeric result metadata](torture/findings/2026-10-10-checksum-table-diagnostics.md) match in the audited modes. Supported `FLUSH` forms operate on fsdb state rather than InnoDB internals | low | divergence |
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
CTEs. Materialized derived tables and stable CTEs build statement-local composite
equality keys from their usable correlated predicates. Outer values and literals
may bind different key parts. Numeric and matching-collation text ranges over
materialized sources use statement-local ordered lookups. Numeric and
matching-collation text `IN` lists bound to direct outer columns or literals
reuse statement-local equality keys. [Multiple compatible materialized `IN`
predicates](torture/findings/2026-10-15-correlated-materialized-in-intersection.md)
intersect their candidate rows before residual evaluation.
[Correlated `ABS` equality over stable materialized sources](torture/findings/2026-10-15-correlated-materialized-abs.md)
uses the two inverse signed keys for `TINYINT` through `INT`, including complete
OR branches and exact `INT` minimum magnitude, while other operand types retain
residual evaluation.
[Indexed physical and pass-through sources](torture/findings/2026-10-15-correlated-physical-abs.md)
now probe the same signed keys through maintained equality indexes, including
complete OR branches and declared-type boundaries.
[Correlated affine equality over stable materialized sources](torture/findings/2026-10-15-correlated-materialized-affine.md)
uses one inverse equality key for bounded signed integer additions and
subtractions in either operand order, including complete OR branches; other
arithmetic forms retain residual evaluation.
[Indexed physical and pass-through sources](torture/findings/2026-10-15-correlated-physical-affine.md)
now use the same inverse key, treating keys outside the signed column's
declared range as empty candidate sets; other arithmetic forms remain open.
[Correlated NULL-safe equality](torture/findings/2026-10-15-correlated-null-safe-equality.md)
uses the same equality keys for stable non-NULL bounds and indexed NULL
buckets for NULL bounds on direct, pass-through, and stable materialized
sources, including complete OR branches. The original `<=>` predicate still
decides row membership.
Matching-collation [correlated binary `LIKE` prefixes over direct and
pass-through physical sources](torture/findings/2026-10-15-correlated-physical-like-prefix.md)
now use maintained ordered indexes, including complete OR branches and stable
NULL patterns. A leading-residual probe distinguishes candidate narrowing
from short-circuit filtering.
[Stable byte-valued correlated `LIKE` patterns](torture/findings/2026-10-15-correlated-physical-like-prefix.md#byte-valued-correlated-patterns)
also use maintained ordered ranges on physical sources and statement-local
byte-ordered ranges on materialized sources, including complete OR branches.
[Stable text patterns against byte-valued keys](torture/findings/2026-10-15-correlated-physical-like-prefix.md#text-patterns-against-byte-keys)
use the pattern expression's charset before the same correlated range lookup.
Binary-collated [correlated `LIKE` prefixes over stable materialized derived
sources and CTEs](torture/findings/2026-10-15-correlated-materialized-like-prefix.md)
now use statement-local ordered ranges for matching
`utf8mb4_bin` or `utf8mb4_0900_bin` comparisons. A NULL outer pattern in a
conjunct yields an empty candidate set, including during metadata probing;
complete OR branches union their bounded candidates and retain other matches.
The original residual
probe evaluated 2,024 rows for two selective prefixes and a NULL pattern;
the focused regression now bounds residual work while checking the same rows.
[Direct materialized `IS NULL` and `IS NOT NULL` predicates](torture/findings/2026-10-15-correlated-materialized-null.md)
reuse statement-local NULL or non-NULL buckets, including complete OR unions.
[Physical and pass-through correlated `IS NULL` and `IS NOT NULL` predicates](torture/findings/2026-10-15-correlated-physical-null.md)
use visible secondary-index NULL or non-NULL slices and intersect them with
other safe row-ID candidates.
Competing equality and range probes choose the narrower observed candidate
set, including direct physical and pass-through projected sources
([oracle and residual probe](torture/findings/2026-10-14-correlated-physical-range-choice.md)).
Direct and pass-through projected physical sources also intersect compatible
equality and range candidates by stored row identity before projection and
residual evaluation. Both source shapes can likewise intersect multiple
correlated range candidates, [mixed ordinary/functional equality candidates](torture/findings/2026-10-14-correlated-physical-range-choice.md),
and [multiple functional equality or range candidates](torture/findings/2026-10-14-correlated-functional-or-union.md).
They also intersect [correlated physical `IN` and range candidates](torture/findings/2026-10-15-correlated-physical-in-range-intersection.md)
by stored row identity before residual evaluation, including broad numeric
ranges that are useful only in combination with a membership probe.
The intersection requires physical reads to preserve stored row identity;
other projected access families still choose one slice. Complete [functional and mixed plain/functional equality and text-range OR branches](torture/findings/2026-10-14-correlated-functional-or-union.md)
over direct and pass-through physical sources intersect compatible candidates
within each branch, then union stored row identities before projection and
residual evaluation. Complete [correlated physical `IN` OR branches](torture/findings/2026-10-15-correlated-physical-in-or-union.md)
use the same row-identity union, including direct, pass-through derived, and
stable CTE sources; compatible broad numeric ranges can intersect an `IN`
probe within each branch, while an uncovered branch retains the scan path.
Correlated [numeric `NOT BETWEEN` predicates](torture/findings/2026-10-15-correlated-not-between.md)
also union the two strict indexed ranges on physical, pass-through, and stable
materialized sources while retaining the original row filter and MySQL's
NULL-bound results.
[Matching-collation text `NOT BETWEEN` predicates](torture/findings/2026-10-15-correlated-text-not-between.md)
now use the same candidate union when one bound is a stable outer value and
the other is a literal, including NULL-bound cases and statement-local
materialized lookups. The same mixed-bound form over a compatible `LOWER`
functional index now uses strict range candidates on physical and pass-through
sources.
[Correlated integer inequality predicates](torture/findings/2026-10-15-correlated-integer-inequality.md)
likewise union strict ranges for a stable integer bound, including complete OR
branches. [Exact DECIMAL inequalities](torture/findings/2026-10-15-correlated-decimal-inequality.md)
now use the same union for a stable decimal bound, including exact
integer/decimal pairs in either key direction. [Fractional DECIMAL bounds
against integer keys](torture/findings/2026-10-15-correlated-decimal-inequality.md#fractional-bounds-against-integer-keys)
now use exact ordered range probes when selective, including complete OR
branches.
Finite `DOUBLE` bounds against indexed `TINYINT`, `SMALLINT`, `MEDIUMINT`,
and `INT` keys also use their original fractional value as an ordered range
boundary; [the MySQL oracle and residual probe](torture/findings/2026-10-15-correlated-decimal-inequality.md#finite-double-bounds-against-integer-keys)
cover direct, pass-through, and materialized sources. [BIGINT/DOUBLE bounds](torture/findings/2026-10-15-correlated-decimal-inequality.md#bigint-and-double-bound-provenance)
also use exact index ordering and residual filtering so a DOUBLE outer column
and the same DOUBLE literal retain their different results above `2^53`.
[DECIMAL/DOUBLE bounds](torture/findings/2026-10-15-correlated-decimal-inequality.md#double-bounds-against-decimal-keys)
now use a conservative ordered candidate range on direct, pass-through, and
materialized sources. MySQL's plan-dependent `DECIMAL(20,2)` comparisons
near `2^53` remain a narrow row-membership divergence.
Other approximate numeric combinations, text, and functional-expression
bounds retain their prior paths.
Correlated forms preserve MySQL NULL and multi-column error semantics.

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
| Secondary-index access paths | ref/eq_ref/range/index-merge scans feed joins, DML, ORDER BY, GROUP BY | common complete-key and safe left-prefix equality, literal membership, direct and negated-comparison ranges, join, ordering, grouping, supported unary functional compositions, compatible functional-result ranges, [audited binary-collated `CHAR`, `VARCHAR`, and `TEXT` family and byte-valued `BINARY`, `VARBINARY`, and `BLOB` family `LIKE` prefixes, including Unicode, escapes, exact, safe constant expression patterns, and matching explicit binary collations](torture/findings/2026-10-12-like-prefix-range.md), fully covered OR unions, and cost-effective AND intersections use maintained indexes; arbitrary expression ordering and broader grouping still scan or sort | high (scale) | divergence |
| Optimizer | pushdown, constant folding, join reordering, cost model, statistics | physical inner joins with qualified or unambiguous bare references and source-local predicates use shape- and cardinality-driven choices; competing access families, complete OR unions, and selective AND intersections use observed cardinalities; literal numeric arithmetic, original indexed built-ins, literal `COALESCE`, `IFNULL`, `IF`, `NULLIF`, `GREATEST`, `LEAST`, and `CASE` bounds, and [audited `MOD`, `TRUNCATE`, `POW`, `POWER`, `BIT_COUNT`, `CRC32`, and nested `PI()` bounds](torture/findings/2026-10-10-numeric-constant-bounds.md) with literal comparisons and Boolean tests use maintained indexes; [pure constant numeric functional bounds](torture/findings/2026-10-10-numeric-functional-bounds.md) serve equality and `IN` through exact, rounded, and double-result keys; local STRAIGHT_JOIN constraints permit independent selective joins to move earlier; audited two-table left joins with indexed null-rejecting WHERE filters, including safe OR and integer-inequality unions, literal IN, BETWEEN, and negated range bounds, become inner joins driven by the selective side; broader expression folding, outer/lateral join reordering, ambiguous bare references, and plans needing persisted statistics retain conservative execution | medium | divergence |
| Optimizer hints | query-block, table, and index target resolution with ordered diagnostics and plan controls | shared ordered syntax parsing, audited timeout/geometry effects and nested SET_VAR precedence, SELECT and audited mutation table/query-block/index resolution, family conflicts, audited CTE instantiation, lexical scope and repeated syntax warnings, diagnostic ordering, preparation warning lifetimes, and audited bound-query join-hint elimination and [merged/materialized source warning ownership](torture/findings/2026-10-08-join-hint-merging.md) work; table `USE`/`FORCE`/`IGNORE INDEX` now controls simple JOIN-scope equality, membership, range, and index-merge access, including alternate eligible keys, single-source ORDER/GROUP index selection, and direct joined-table equality probes (and the [measured `IGNORE INDEX` case](benchmarks/results/2026-10-10-functional-bound-pipeline.md)); unknown named keys return 1176 for SELECT/EXPLAIN on physical tables, views, and metadata tables, single-table UPDATE, and joined UPDATE/DELETE; `USE INDEX` range cost decisions, multi-source ORDER/GROUP plans, broader join access, other mutation forms, optimizer_switch configuration, broader hint combinations, stored-program contexts, and other physical plan controls remain incomplete ([SELECT cases](torture/findings/2026-10-08-hint-families.md), [mutation cases](torture/findings/2026-10-08-mutation-hints.md)) | medium | divergence |
| Mixed-type join filters | numeric `IN` conditions can move between collation-equivalent text keys as join order changes | the [audited two-source equality joins](torture/findings/2026-10-07-fulltext-join-bounds.md#mixed-type-in-across-equality-joins) transfer literal multi-value `IN` when STRAIGHT_JOIN or MATCH identifies the owner; simple costed two-source plans cover the smaller unindexed base and audited singleton-source choices, including competing-index controls; a [connection-sensitive equal-cardinality counterexample](torture/findings/2026-10-07-fulltext-join-bounds.md#connection-sensitive-result-on-2026-10-12) still differs on the creating connection, and other costed shapes, broader join graphs, and physical-plan reporting remain incomplete | medium (result membership) | divergence |
| EXPLAIN fidelity | type ∈ system/const/eq_ref/ref/range/index/index_merge/ALL; FORMAT=JSON/TREE; ANALYZE; optimizer_trace | access types cover compatible direct bounds/orderings, index unions and intersections, and source-local join probes; JSON/TREE plans and aggregate ANALYZE observations work, while per-iterator timing/costs and optimizer trace rows remain absent | low | divergence |
| Subquery strategies | semi-join/materialization/early-exit transformations | stable subqueries materialize once; common correlated equality/range shapes and compatible functional probes over physical or pass-through projected sources use maintained indexes; [complete correlated equality and numeric-range OR branches](torture/findings/2026-10-14-correlated-or-union.md) on physical, pass-through projected, and stable materialized sources use deduplicated candidate unions; [functional and mixed plain/functional OR branches](torture/findings/2026-10-14-correlated-functional-or-union.md) also union equality and text-range candidates on physical and pass-through sources; [direct, physical, and pass-through NULL-test candidates](torture/findings/2026-10-15-correlated-physical-null.md) use visible, allowed single-column or composite ordered keys, including later NULL or non-NULL parts after exact equality bounds; stable materialized sources use statement-local composite equality, numeric range, matching-collation text range, [direct IS NULL/non-NULL buckets](torture/findings/2026-10-15-correlated-materialized-null.md), and [numeric](torture/findings/2026-10-14-correlated-materialized-in.md) and [matching-collation text](torture/findings/2026-10-14-correlated-materialized-text-in.md) outer-bound IN lookups, including complete OR unions; variable-bearing, nondeterministic, lateral, JSON_TABLE, and more complex correlated forms re-execute | medium (scale) | divergence |

Numeric `CAST` constants now participate in indexed equality and range
bounds, including an exact positive unsigned probe against a signed integer
key. [Warning-producing numeric CAST bounds](torture/findings/2026-10-10-numeric-constant-bounds.md#numeric-cast-bounds)
match the audited indexed SELECT result and warning count. Unindexed and
EXPLAIN warning lifetimes remain open.

Stable materialized correlated sources now [intersect usable equality and range
candidate lists](torture/findings/2026-10-14-correlated-materialized-intersection.md)
before residual evaluation, including [compatible bounds within each complete
OR branch](torture/findings/2026-10-14-correlated-materialized-or-intersection.md)
before branch union. They also intersect [multiple outer-bound `IN` candidate
groups](torture/findings/2026-10-15-correlated-materialized-in-intersection.md).
Stable arithmetic expressions over outer columns can also bind materialized
`IN` probes for derived tables and CTEs
([oracle and residual probe](torture/findings/2026-10-15-correlated-materialized-expression-in.md)).
Stable materialized sources also use [composite equality keys for correlated
row-valued `IN` tuples](torture/findings/2026-10-15-correlated-materialized-row-in.md),
including NULL-bearing outer bounds that cannot produce a WHERE-true match.
Indexed physical tables and pass-through sources also use compatible
outer-bound `IN` probes, intersecting multiple groups by stored row identity
before projection ([oracle and residual probe](torture/findings/2026-10-15-correlated-physical-in.md)).
More complex correlated expressions without a
compatible statement-local lookup still re-execute or scan. The [materialized
functional-equality profile](torture/findings/2026-10-15-correlated-materialized-functional-equality.md)
now shows six residual calls for both an exposed-column equality and its
supported signed-integer `ABS` form. [A statement-local integer-expression key](torture/findings/2026-10-15-correlated-materialized-functional-equality.md#statement-local-integer-expression-lookup)
reduces `ABS(d.k)+0=o.x` from 4,004 to six calls on the same profile.
The [matched in-memory timing](benchmarks/results/2026-10-15-materialized-integer-expression-lookup.md)
confirms a repeated-query speedup for this shape. [Text expression keys](torture/findings/2026-10-15-correlated-materialized-functional-equality.md#statement-local-text-expression-lookup)
use the effective comparison collation and reduce the audited `LOWER(d.k)=o.x`
profile from 4,004 to six residual calls. [Exact decimal expression keys](torture/findings/2026-10-15-correlated-materialized-functional-equality.md#statement-local-decimal-expression-lookup)
reduce `d.k+0.00=o.x` from 5,005 to seven calls. [Finite DOUBLE-domain keys](torture/findings/2026-10-15-correlated-materialized-functional-equality.md#statement-local-finite-double-expression-lookup)
now reduce `d.k+0e0=o.x` from 5,005 to seven calls too. DECIMAL and integer
bounds use the same approximate comparison key, while string and non-finite
floating lookups remain open. The audited BIGINT/DOUBLE expression-provenance
comparison at 2⁵³ now matches MySQL.
[Finite DOUBLE-expression ranges](torture/findings/2026-10-15-correlated-materialized-functional-equality.md#statement-local-finite-double-expression-ranges)
now use ordered statement-local cuts for numeric correlated inequalities and
BETWEEN, including complete OR branches; the narrow 1,000-row profile uses
11 residual calls across four outer rows. The audited decimal-to-DOUBLE
halfway bound at 2⁵³+1 also follows MySQL's single rounding. String and
non-finite range bounds still scan.

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
- OGC geometry construction, serialization, inspection, coordinate swapping,
  line interpolation, and member access,
  including typed text/binary constructors, line endpoints and indexing,
  polygon rings, multi-geometry members, closure checks, and planar
  `ST_Crosses`/`ST_Overlaps` with MySQL's dimension-dependent NULL results.

[`CHAR()`](torture/findings/2026-10-12-char-byte-assembly.md) now assembles
integer arguments as binary bytes instead of Unicode characters, including
audited 32-bit wrap, half rounding, NULL skipping, and downstream high-byte
TIME warnings. Audited text arguments use an integer prefix, preserve
argument-order warnings, and clamp signed/unsigned overflow. `CHAR(... USING
charset)` now interprets those bytes in the selected charset, retaining its
coercibility and audited invalid-byte diagnostics. Binary-string integer
warnings now escape high bytes and NUL, while decoded text warnings stop at
NUL. Audited 64-bit BIT, large floating and DECIMAL, and temporal arguments
now preserve their MySQL byte values. Stored DOUBLE overflow emits the audited
integer warning while a direct large literal stays quiet; audited computed
DOUBLE overflows now raise MySQL error 1690, while audited IF, CASE, and
NULLIF pass-through values stay quiet and the stored signed boundary retains
its warning. JSON argument values and diagnostics now match the audited
scalar, array, object, and overflow
expressions. Stored JSON values and warning row numbers match the audited
full, filtered, descending, bounded, and grouped projections; audited
JSON_EXTRACT, JSON_SET, JSON_MERGE_PATCH, JSON_KEYS, audited JSON mutation and
search functions, and JSON_SCHEMA_VALIDATION_REPORT origins also match. Every
registered JSON-returning builtin has at least one audited `CHAR()` source
case. Simple derived sources, JSON/NULL CTE UNIONs, and audited direct,
renamed, nested expression, single-source plain or mixed `SELECT *`, and UNION
views preserve JSON values and warning names; broader view shapes,
user-defined JSON origins, and other expression-origin overflow cases remain
open. Audited joined view references retain the JSON warning source. MySQL's
join warning row counter can reset to 1 for each probe, and projection order
changes which subsequent expressions see that reset. Fsdb still counts final
output rows; joined diagnostic row provenance remains open.
Audited mergeable pass-through and renamed derived sources over a view or
physical table retain the defining column or JSON function warning source;
limited derived sources and limited or `TEMPTABLE` views use the exposed alias.

| Missing family | Functions | Impact |
|---|---|---|
| Remaining spatial behavior | geographic point-set crossing of lines and polygons now uses member positions; [geographic line/line crossing, touching, overlap, containment, and equality](torture/findings/2026-10-11-geographic-line-topology-oracle.md) now cover audited cases; [geographic line/polygon crossing, touching, and containment](torture/findings/2026-10-11-geographic-line-polygon-topology.md) also cover audited cases; [geographic polygon/polygon topology](torture/findings/2026-10-11-geographic-polygon-topology.md) covers audited overlap, containment, touching, and equality; the [edge audit](torture/findings/2026-10-14-geographic-edge-topology-audit.md#high-latitude-point-follow-up) retains one point mismatch across 189 near-hole points, no mismatch across four near-exterior controls, and 12 line mismatches across 412 cases, including a near-polar MySQL relation/intersection inconsistency; near-tangent crossings, unsampled geodesic path deviations, other non-point topology, and reference systems beyond the audited EPSG subset remain | low |

`CONVERT_TZ` and the session `time_zone` resolve numeric offsets, `SYSTEM`,
and named zones populated in `mysql.time_zone*`. Leap-second-aware named zones
(`Use_leap_seconds = Y`) remain unsupported.
[Interval arithmetic on TIME](torture/findings/2026-10-11-interval-arithmetic-metadata.md)
retains signed, greater-than-24-hour TIME results for subday units and uses
DATETIME for day-or-larger units, with MySQL's overflow warning.
HEX preserves audited decimal rounding and [expression-sensitive DOUBLE
conversion](torture/findings/2026-10-09-hex-expression.md), including selected
conditional branches, stored-column warnings, and the declared text or binary
result of audited mixed-type scalar subqueries. Mixed UNION, INTERSECT, and
EXCEPT scalar results and UTF-16 computed-result bytes are covered. Broader
integer-conversion contexts and scalar-subquery shapes remain unaudited.
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
before arithmetic in the audited combinations. A fresh native probe confirmed
that MySQL also emits both warnings when an invalid `ADDTIME` operand and its
overflowing result have identical warning text
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
| Spatial indexes and operations | R-tree indexes and geographic SRS rules | maintained immutable MBR indexes narrow direct `MBRINTERSECTS`, `MBRWITHIN`, and `MBRCONTAINS` predicates for SRID 0; planar overlays, geometry property/member accessors, and independently configurable square/circle point, flat/round end, and miter/round join buffer strategies work; EPSG 4326 constructors and text/binary serializers honor MySQL axis options and coordinate domains, point/[multipoint distance](torture/findings/2026-10-11-geographic-multipoint-distance.md) and line length use MySQL's Andoyer strategy and linear-unit registry, and `ST_DISTANCE_SPHERE` supports point and multipoint inputs with MySQL's default, SRS-derived, or explicit radius; [geographic point-set topology](torture/findings/2026-10-11-geographic-point-set-topology.md) follows coordinate membership, and [point/polygon topology](torture/findings/2026-10-11-geographic-point-polygon-topology.md) covers containment and ring boundaries; [point/line topology](torture/findings/2026-10-11-geographic-point-line-topology.md) covers geographic segment membership and endpoint boundaries; [line interpolation](torture/findings/2026-10-10-line-interpolation.md) works across supported SRSs; [EPSG 3857 projected constructors, metadata, planar distance/length, envelope, and spherical-distance refusal](torture/findings/2026-10-10-epsg-3857.md) are covered, as are [EPSG 3395 projected metadata, planar operations, and transforms among 4326/3857/3395](torture/findings/2026-10-10-world-mercator.md); [WGS 84 UTM zones](torture/findings/2026-10-11-wgs84-utm-zones.md), [ETRS89 UTM zones 28N–37N](torture/findings/2026-10-11-etrs89-utm-zones.md), and [NAD83 UTM zones 1N–23N](torture/findings/2026-10-11-nad83-utm-zones.md) and [NAD27 UTM zones 1N–22N](torture/findings/2026-10-11-nad27-utm-zones.md) add projected transforms; [NAD27 geography](torture/findings/2026-10-11-nad27-helmert-oracle.md) adds Clarke 1866 and a seven-parameter datum shift; [EPSG 4030](torture/findings/2026-10-11-unspecified-wgs84-datum.md) supports ellipsoidal operations while refusing unavailable datum transforms; geographic point-to-line, audited line-to-line, [point-to-polygon](torture/findings/2026-10-11-geographic-point-polygon-distance.md), [line-to-polygon](torture/findings/2026-10-11-geographic-line-polygon-distance.md), and [polygon-to-polygon distance](torture/findings/2026-10-11-geographic-polygon-polygon-distance.md) are covered, with near-boundary sampling limits; audited geographic line/line crossing, touching, overlap, containment, and equality plus line/polygon crossing, touching, and containment and polygon/polygon overlap, containment, touching, and equality are covered; broader non-point geographic topology and most SRS definitions remain absent, and the internal augmented interval tree is not an R-tree | low | subset |
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
drop/re-add actions. Differing-width BIT keys compare their stored bytes;
audited cascades retain out-of-range child values and subsequent referential
actions. Stored BIT values and bit literals retain MySQL's distinct `HEX()`
formatting. [Temporal FK probes](torture/findings/2026-10-09-year-column-values.md)
now distinguish the fractional storage-byte tiers within `TIME`, `DATETIME`,
and `TIMESTAMP`, including equal displayed values that must not match across
tiers. [DECIMAL FK probes](torture/findings/2026-10-09-foreign-key-column-changes.md)
compare packed groups across different precisions and scales, including
byte-equal keys whose numeric values differ; invalid packed cascade groups
still need a row representation that retains their raw bytes.

Named CHECK constraints support enforcement state
and `ALTER` validation, and ENUM or SET values enforce membership. Adding a
unique key over colliding data returns 1062 without publishing a corrupt index.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| Remaining foreign-key column changes | preserves constraint validity through column changes | tested compatible type and nullability changes, rejected incompatible changes, disabled-checks behavior, [combined ADD/DROP FOREIGN KEY with MODIFY or CHANGE in both orders, atomic rejection, final-column renames, `BIT`/binary and differing-width `BIT` byte-key lookups and cascades, `ENUM`/`SET` storage widths and physical-key actions including out-of-domain cascade ordinals, constrained-column layout changes, `SET NULL` action nullability, and stored generated-column action limits](torture/findings/2026-10-09-foreign-key-column-changes.md), virtual generated-column FK rejection, [YEAR/unsigned-tinyint physical keys and cascades, and audited TIME/DATETIME/TIMESTAMP declaration, nonmatch, both directions of same-tier mixed-precision cascade, and SET NULL cases](torture/findings/2026-10-09-year-column-values.md) now match; fsdb deliberately keeps its index consistent where MySQL's index misses an out-of-domain ENUM cascade row, while other temporal physical-key cases and rare ALTER-action combinations remain open | low | divergence/unverified |
| Non-unique secondary indexes | physical structures serving lookups/ordering | separate immutable equality and ordered structures cover common complete-key and left-prefix probes, joins, ranges, ordering, grouping, and supported unary functional compositions; unsupported expression orderings and grouping shapes retain scan/sort fallback | high (scale) | divergence |
| Expression indexes | functional key parts participate in physical access and uniqueness | the [supported functional keys](README.md#indexes-and-joins), now including [calendar-part and date-result functional keys on `DATE`/`DATETIME`](torture/findings/2026-10-14-calendar-part-functional-index.md), charset-aware `ASCII` and `ORD`, text-result `HEX`, `MD5`, and `SHA1`/`SHA`, binary-result `UNHEX`, `ABS`, `ISNULL`, `SIGN`, [numeric, text, and binary `BIT_COUNT`](torture/findings/2026-10-10-bit-count-functional-indexes.md), [byte-aware `CRC32`](torture/findings/2026-10-10-crc32-functional-indexes.md), `FLOOR`, `CEIL`/`CEILING`, `ROUND`, [double-result `SQRT` and numeric unary compositions](torture/findings/2026-10-10-sqrt-functional-indexes.md), [double-result `EXP`](torture/findings/2026-10-10-exp-functional-indexes.md), [indexed `SIN`, `COS`, `TAN`, `ASIN`, `ACOS`, unary `ATAN`/`ATAN2`, `COT`, `DEGREES`, and `RADIANS`](torture/findings/2026-10-10-trigonometric-functional-indexes.md), and [indexed `LOG`, `LN`, `LOG2`, and `LOG10`](torture/findings/2026-10-10-logarithm-functional-indexes.md), have physical equality, uniqueness, ordering, and grouping paths; [integer `COALESCE(column, literal)`](torture/findings/2026-10-12-coalesce-functional-index.md) has audited signed, unsigned, literal-width, strict-range, equality, uniqueness, grouping, and recovery paths; [integer `IFNULL(column, literal)`](torture/findings/2026-10-12-ifnull-functional-index.md) has distinct MySQL result types, equality, and uniqueness paths; [fitting text `COALESCE` and `IFNULL` fallbacks on `VARCHAR`](torture/findings/2026-10-12-text-null-fallback-functional-index.md) use charset-resolved physical equality and unique keys, including equivalent cross-charset probes under `utf8mb4_0900_ai_ci`; [integer and matching-charset `CHAR`/`VARCHAR` text `NULLIF(column, literal)`](torture/findings/2026-10-12-nullif-functional-index.md) have nullable physical keys, collation-aware equality, and uniqueness paths; equivalent cross-charset probes on `utf8mb4_0900_ai_ci` can use the text `NULLIF` key, while broader mixed-charset forms remain open; [integer and fitting `VARCHAR` text `IF` NULL-branch forms](torture/findings/2026-10-12-if-null-branch-functional-index.md) have distinct expression identities, audited nullable key widths for BOOLEAN through signed BIGINT and text `VARCHAR`, equivalent cross-charset text-fallback probes under `utf8mb4_0900_ai_ci`, literal-width promotion, equality, uniqueness, and strict overflow paths; BIGINT UNSIGNED, unsigned fallback literals beyond signed BIGINT, and other branch results remain open; other non-unique expressions retain DDL and metadata but scan, while unsupported unique expressions are refused; calendar parts over `TIMESTAMP` remain unsupported as physical keys because MySQL's indexed result can diverge from a time-zone-adjusted projection | low | divergence/refusal |

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
| Advanced REGEXP grammar | ICU regular expressions and Unicode properties | bounded .NET regex with all fourteen POSIX class names, negated forms, positive and mixed-negated class combinations, common Unicode general-category names and `gc=`/`General_Category=` forms, `White_Space`, [ASCII and Basic Latin property aliases](torture/findings/2026-10-11-regexp-ascii-property.md), [ASCII hex digit aliases](torture/findings/2026-10-12-regexp-ascii-hex-digit.md), [Unicode hex-digit aliases](torture/findings/2026-10-12-regexp-unicode-hex-digit.md), [join-control aliases](torture/findings/2026-10-12-regexp-join-control.md), [bidi-control aliases](torture/findings/2026-10-12-regexp-bidi-control.md), [supplementary regional-indicator and emoji-modifier properties](torture/findings/2026-10-12-regexp-supplementary-properties.md), [the variation-selector property with complete scalar membership](torture/findings/2026-10-12-regexp-variation-selector.md), [Greek Script and Script_Extensions aliases with complete scalar membership](torture/findings/2026-10-12-regexp-greek-script.md), [Han Script and Script_Extensions aliases with pinned-image scalar membership](torture/findings/2026-10-12-regexp-han-script.md), [Latin Script and Script_Extensions aliases with pinned-image scalar membership](torture/findings/2026-10-12-regexp-latin-script.md), [Cyrillic Script and Script_Extensions aliases with pinned-image scalar membership](torture/findings/2026-10-12-regexp-cyrillic-script.md), [Arabic and Devanagari Script and Script_Extensions aliases with pinned-image scalar membership](torture/findings/2026-10-12-regexp-arabic-devanagari-script.md), scalar-aware dot and audited emoji matches for negated POSIX classes, `\P{ASCII}`, `\W`, `\H`, and `\V`, plus `\w`, bracket-local `\b`/`\B`, horizontal/vertical/`\R` line breaks, and `\Q...\E` literal quoting with quoted-interval diagnostics ([POSIX and Unicode class evidence](torture/findings/2026-10-09-regexp-posix-classes.md)), plus [audited malformed-pattern diagnostics](torture/findings/2026-10-11-regexp-diagnostics.md), [possessive quantifiers](torture/findings/2026-10-11-regexp-possessive-quantifiers.md), [audited letter escapes](torture/findings/2026-10-11-regexp-letter-escapes.md), and the [scalar-aware Any property](torture/findings/2026-10-11-regexp-any-property.md); other ICU script and binary properties, other Script_Extensions values, broader supplementary-scalar bracket/property matching, broader word boundaries, other grammar, and error-code distinctions can differ | low | divergence |
| Remaining charset catalog | every bundled charset and collation | eucjpms remains refused because its Microsoft EUC-JP extensions differ from the standard .NET codecs; expanded families register their default and binary collations rather than every legacy language collation | low | refusal |

## 7. Transactions and concurrency

Transactions use private snapshots and three-way optimistic merge. Disjoint
point writes have a fast path; indexed point and range updates or deletes, and
single-table full-scan writes, claim row ownership before applying changes.
Full scans conservatively claim the rows visible when the statement begins,
then refresh and recheck selected row identities after a wait
([MySQL 8.4 oracle and regressions](torture/findings/2026-10-14-full-scan-write-scheduling.md)).
They do not claim gaps or later inserted rows. A [focused unindexed-write load
probe](benchmarks/results/92b019ff-full-scan-write-load.md) records the current
worker-scaling slope. Unique-key claims, incremental index validation, and merged-result
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
| Write parallelism within a database | row-lock concurrency | indexed and single-table full-scan UPDATE/DELETE paths coordinate row stripes; full scans conservatively claim current rows but not gaps or later inserts; insert, upsert, and replacement candidates are prepared once, then claim supplied, generated, or defaulted unique keys and refresh existing duplicate rows before publication, including SELECT sources; [unique-key locks use exact keys](torture/findings/2026-10-15-exact-key-locks.md), so unrelated keys no longer wait because their old hash stripes collided; AUTO_INCREMENT identities are reserved across transaction snapshots; joined writes claim [physical targets inside nested join groups](torture/findings/2026-10-14-grouped-joined-write-rebase.md) and [bare assignments beside derived sources](torture/findings/2026-10-14-derived-joined-write-rebase.md); [secondary-index ordered `LIMIT 2` writes](torture/findings/2026-10-16-limited-ordered-write-rebase.md#secondary-index-ordered-frontiers) reselect crossing inserts, while MySQL also waits for beyond-frontier inserts in an audited small-table shape; [keyless inserts](benchmarks/results/2026-10-12-keyless-insert-load.md), CTE writes without inferable physical targets, and other multi-table writes still rely on optimistic merge; publishing a new immutable database root remains one brief per-database critical section, and durable commit events are sequenced; a [durability-matched keyless-insert run](benchmarks/results/2026-10-14-durable-keyless-scale.md) confirms the worker-scaling gap but does not isolate its cause | medium (throughput) | partial |
| Multi-database scaling | near-linear with connections | database roots and row-lock stripes are sharded; qualified foreign keys deliberately serialize catalog-wide referential actions, and recorded campaigns show CPU saturation limiting higher worker counts | medium | partial |

A [current write-path control](benchmarks/results/2026-10-14-write-path-control.md)
measures keyless inserts at one, four, and eight writers with WAL-backed and
in-memory fsdb on the same host. Durability lowers throughput, but both modes
retain a weaker worker-scaling slope than durable MySQL; the sampled trace
does not isolate a lock or group-commit cause.
A [wire scheduling control](benchmarks/results/2026-10-14-wire-write-scheduling-profile.md)
finds statement dispatch dominating measured per-connection wire time and no
throughput recovery from doubling writers beyond eight. The timing includes
waits and does not yet isolate publication or WAL completion.
A [table-lock admission profile](benchmarks/results/2026-10-15-table-lock-admission-profile.md)
measured roughly 6–9 µs of admission and release per write at eight writers;
it does not explain the hundreds of microseconds observed in the handler and
WAL path. Its temporary probe was removed.
A [post-preview.11 durability-matched checkpoint](benchmarks/results/2026-10-15-preview-11-write-checkpoint.md)
again measures about 0.43× MySQL throughput at one writer and 0.27× at eight;
the recent correctness and planning merges did not change the wire-write
scaling gap.

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

[Invalid stored-view dependencies](torture/findings/2026-10-10-view-dependency-errors.md)
now report MySQL's 1356 view diagnostic after a base table or referenced
projection/predicate column is removed, including direct views that would
otherwise merge into the outer query. Nested invalid dependencies name the
outer view requested by the client. A
[focused direct-view benchmark](benchmarks/results/2026-10-10-view-binding-check.md)
found no supported regression from the added binding check in a short run.

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
and insert through explicit target columns into one insertable component.
This includes [audited `INSERT ... SET`, `INSERT IGNORE ... SET`, and ODKU
forms](torture/findings/2026-10-14-joined-view-insert-set-check-option.md);
an implicit-column `VALUES` insert still returns MySQL error 1394.
Writable join views also cover `CROSS JOIN`, `JOIN ... USING`, and `NATURAL JOIN`;
merged `USING` and natural columns bind to the left input when a view write
references them. An [ordered join view](torture/findings/2026-10-14-ordered-join-view.md)
retains MySQL's updatable metadata and explicit-column insertion through outer
layers, while UPDATE returns 1221 because its saved `ORDER BY` cannot be used
with the joined write.
Direct inner-join views with `WITH CHECK OPTION` validate inserted and updated
rows against the join and view predicate. Updates retain their originally
matched join partners; finding a different partner after a key change does not
satisfy the check. [Multiple derived-source partners](torture/findings/2026-10-15-duplicate-derived-join-view-check.md)
must each remain joined and satisfy `CHECK OPTION`, even though an ordinary
joined UPDATE writes the physical target once. Explicit-column INSERT checks
every resulting join row, while `ON`-excluded rows do not participate. Outer
view layers inherit the restriction.
Mergeable component views and simple outer layers preserve the same behavior,
including inherited CHECK OPTION predicates, when the nested security identity
is unchanged.
Mergeable [outer layers over an inner-join view](torture/findings/2026-10-14-layered-join-view-security.md) now also retain writable metadata
and authorize UPDATE at each definer or invoker boundary, including renamed
columns and column-level grants through three layers. Explicit-column INSERT
checks the corresponding layered grants; a missing grant invalidates the outer
view with MySQL's 1356 error in the audited cases.
[Layered views now distinguish missing join-key reads from invalid view
projections](torture/findings/2026-10-15-layered-view-grant-diagnostics.md):
direct views return 1356 for revoked intermediate or inner base grants, while
audited joined views return 1143 for the first denied join column and 1356
when only a projected column remains denied; an inner INVOKER view under an
outer DEFINER layer uses the outer definer's base grants. Broader grant
ordering across multi-source layers remains open.
[Aggregate-derived read sources in a layered writable join](torture/findings/2026-10-15-layered-derived-view-grants.md)
now report 1356 for revoked source grants before a physical target join-key
denial; a revoked target read alone still reports 1143. An audited
parenthesized inner-join operand containing a derived and physical source is
also writable and preserves their distinct 1356/1143 grant diagnostics. Other
The target-first correlated `LATERAL` source under two definers also remains
writable and reports 1356 for either revoked source read. Other derived,
lateral, and grouped grant paths remain unaudited.
Joined UPDATE also reaches a mergeable component view with a distinct definer.
The outer definer must retain access to the component view, while that view's
definer must retain SELECT on its base tables and UPDATE on the written target;
revoked grants invalidate the joined write as in MySQL 8.4
([oracle and regression](torture/findings/2026-10-14-nested-join-view-security.md)).
Renamed columns retain the component view's column identity for authorization,
including column-level grants when two aliases reach the same base column.
An [audited distinct-definer component CHECK OPTION](torture/findings/2026-10-14-joined-view-insert-set-check-option.md)
also rejects invalid joined UPDATE and `INSERT ... SET` rows with error 1369,
while allowing a valid insert.
[Explicit-column INSERT and INSERT SELECT](torture/findings/2026-10-14-nested-join-view-insert.md)
also reach a distinct-definer component in a joined view. The outer definer's
component INSERT grants are checked by exposed column, and the inner definer's
base-table INSERT grant remains necessary.
An [audited third definer layer](torture/findings/2026-10-14-deep-joined-view-insert.md)
also preserves the intermediate view's column grant: `INSERT(metric)` reaches
the base table, while writing an ungranted `id` fails with 1356. ODKU through
that layer follows MySQL's distinct intermediate UPDATE-grant validity rule
and resolves assignment references against the physical row. Renamed columns
are authorized using each intermediate view's exposed names.

An inner join may also use a nonmergeable aggregate or UNION view as a
read-only row source while updating another mergeable component; INSERT still
requires every component to be mergeable.
An explicit-column derived source, or an audited `SELECT *`/qualified-star
source over one relation, can supply read-only join rows while UPDATE writes a
physical component, including derived-first and three-source inner joins.
Invoker views still require read access to the derived source's tables.
Grouped derived-star sources now preserve their logical `USING`/natural
columns and allow an outer joined view to update its physical component
([MySQL oracle and regression](torture/findings/2026-10-14-grouped-derived-star-view.md)).
[Multiple leading read-only derived sources](torture/findings/2026-10-15-multiple-derived-join-view.md)
now also precede a writable physical target in audited inner-join views;
the moved target predicate is applied after all its source aliases are bound.
[Independent CTE sources](torture/findings/2026-10-15-cte-joined-view-writes.md)
now follow that path for audited aggregate, pass-through, and limited bodies,
including two leading CTEs, explicit CTE column names, and a CTE body joining
physical read sources. Repeated references to one CTE share a statement-local
rowset, including volatile projected values, while duplicate join matches
update the physical target once and retain CHECK OPTION and INSERT rules.
[Independent CTE bodies with nested read-only derived sources](torture/findings/2026-10-15-derived-cte-joined-view-writes.md)
also preserve writable target metadata and UPDATE behavior through audited
simple, grouped, and two-level derived bodies. Explicitly named CTE columns
now also expand audited `SELECT *`, qualified-star, and mixed-star projections
over independent physical or nested-derived read sources, including ordinary,
`USING`, and `NATURAL` joins with their merged-column order and ownership.
[Recursive and dependent CTE read sources](torture/findings/2026-10-16-recursive-cte-joined-view.md)
also leave a joined physical component updatable in 38 audited layouts,
including inferred and dependent-star column names, a parenthesized operand,
aggregate and repeated recursive sources, nested derived CTE bodies, and
direct dependent `LATERAL` and `JSON_TABLE` sources, including a nested
derived source, shared recursive CTE re-reads through nested derived and
expression subqueries, independent scalar and `EXISTS` subqueries, and
local CTE chains with plain or ordered/limited UNION bodies, and a source-free
local CTE shadowing the recursive name after earlier local CTEs re-read the
outer binding, inside a lateral body; CHECK OPTION still
rejects invalid target rows. An audited
dependent-CTE UPDATE waits and rebases on its physical row under contention.
Broader recursive compositions, layered security, and
other concurrent write shapes remain open.
[Audited `STRAIGHT_JOIN` edges](torture/findings/2026-10-15-multiple-derived-straight-view.md)
between those derived sources or at the physical target retain the same
writable metadata and target-predicate filtering.
[Derived-first `USING` chains](torture/findings/2026-10-15-derived-using-view.md)
also retain writable metadata and update a physical target, including a merged
key projection; multiple leading derived sources now preserve the original
left operand of each `USING` key even when the chain merges different keys.
[Derived-first `NATURAL JOIN` views](torture/findings/2026-10-15-derived-natural-view.md)
also update a physical target; moved joins retain the original shared-column
ownership even when the target has another same-named column.
An [audited composition of aggregate derived sources and merged `NATURAL`
keys, or a leading `JSON_TABLE` joined with `USING`](torture/findings/2026-10-15-composed-join-view-writes.md)
also preserves updateability and `CHECK OPTION` rejection while refusing
joined-view INSERT and DELETE with MySQL's audited errors.
An aggregate derived source feeding a dependent `JSON_TABLE` before a physical
target also retains those write and rejection rules in the same oracle matrix,
including when the dependent read-only pair forms a parenthesized join operand.
[Target-first joins with a dependent lateral source](torture/findings/2026-10-15-lateral-join-view.md)
also update the physical component and enforce invoker read grants on the
lateral source. [Audited lateral sources before the physical target](torture/findings/2026-10-15-lateral-before-target-view.md)
also preserve writable metadata and target identity through a two-source
dependency chain. [Aggregate-derived sources followed by one or two dependent
`LATERAL` projections](torture/findings/2026-10-15-composed-join-view-writes.md#aggregate-source-followed-by-dependent-lateral-sources)
also retain writable metadata, target identity, and CHECK OPTION behavior
when the physical target is last. [JSON_TABLE joined views](torture/findings/2026-10-15-json-table-joined-view.md)
also update a physical component when the table function follows or precedes
it, including dependent table-function chains; JSON_TABLE columns remain
read-only. An [independent `LATERAL` first source](torture/findings/2026-10-15-composed-join-view-writes.md#independent-lateral-source-first)
also precedes a writable target, directly or through a dependent `JSON_TABLE`.
[Two dependent `JSON_TABLE` sources under distinct definer layers](torture/findings/2026-10-15-layered-json-chain-view-grants.md)
retain writable metadata and target identity with the physical target either
last or between the functions, including MySQL's 1143 join-key and 1356
write-grant denials. Broader join-source reorderings and layered
authorization for those chains remain open. [Native MySQL 8.4.11 reports
audited left- and right-outer-join views as non-updatable](torture/findings/2026-10-15-outer-join-view-updateability.md),
even when the projection exposes only columns from the preserved side.

Multi-component writes, outer-join view writes, and join-view DELETE/REPLACE
are refused with MySQL-compatible errors for the audited shapes.
An UPDATE that targets a single-table updatable view as its
first joined mutation source rewrites to its base table and waits on the physical
row under contention; renamed exposed columns and CHECK OPTION match the
[MySQL oracle](torture/findings/2026-10-15-joined-view-mutation-source.md).
Audited [inner joined views used as the first source of another joined
mutation](torture/findings/2026-10-15-nested-joined-view-mutation-source.md)
also update the physical target with either internal source order, retain
CHECK OPTION, and wait under contention. Uniquely owned bare view-target
assignments also work; ambiguous bare columns return 1052. Broader join,
multi-target, and security-layer combinations remain open.
`LOAD DATA` into an inner-join view is also refused with MySQL error 1288
before a local upload or server-side file read; ordinary inserts remain
available through their supported forms.

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
| Updatable-view breadth | nested write targets with distinct definer/security contexts and additional expression shapes where MySQL deems individual columns writable | joined UPDATE and explicit-column INSERT/INSERT SELECT reach a mergeable component with a distinct definer and verify its grant chain; `INSERT ... SET`, `IGNORE`, ODKU, and audited component CHECK OPTION cases work; audited three-layer outer views and renamed column grants work; single-table views, inherited or overriding `ORDER BY`, same-identity nested joins, outer view layers, and aggregate/UNION/`JSON_TABLE` read-only join components compose writable targets; audited independent and [recursive/dependent CTE](torture/findings/2026-10-16-recursive-cte-joined-view.md) read sources also precede a joined UPDATE target; broader layered joins, CTE compositions, and remaining expression shapes stay open | medium | subset |
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
| CJK | ngram and mecab parsers, WITH PARSER clause | ngram DDL, search modes, stopword and phrase boundaries, mutation, startup token sizes, and preserved postings across WAL/snapshot recovery are implemented; common metadata-only MODIFY/CHANGE column definitions also preserve historical postings ([recovery oracle](torture/findings/2026-10-07-ngram-startup-sizing.md)); [multi-column searches and relevance at startup sizes 1–10](torture/findings/2026-10-07-ngram-fulltext.md) match the audited default-collation MySQL corpus, including Unicode formatting and zero-width controls, combining-mark boundaries, decomposed and voiced kana, width and script variants, compatibility ideographs, ideographic variation selectors, CJK punctuation presentation forms, and numeric symbols, within score tolerance; [legacy `utf8mb4_unicode_ci` ideographic-comma equality and ngram membership](torture/findings/2026-10-15-legacy-ngram-punctuation.md) now match the audited MySQL cases, while exact legacy weight bytes, broader legacy-collation behavior, and MeCab remain open | medium (for CJK) | partial |

The [legacy vertical-punctuation follow-up](torture/findings/2026-10-15-legacy-ngram-punctuation.md#vertical-punctuation-block-follow-up)
also distinguishes all U+FE10–U+FE19 forms under `utf8mb4_unicode_ci` and
matches the audited period/vertical-period ngram membership. Its keys retain
their relative MySQL order, while exact weight bytes remain out of scope.
[ASCII, fullwidth, and small comma equality](torture/findings/2026-10-15-legacy-ngram-punctuation.md#ascii-fullwidth-and-small-comma-equality)
now agrees across the audited insensitive collations while ngram tokenization
retains MySQL's ASCII punctuation boundary.
The [combined legacy width-form corpus](torture/findings/2026-10-15-legacy-ngram-punctuation.md#combined-width-form-corpus)
now matches MySQL's cross-block ngram membership and relevance across all ten
token sizes and three search modes for the audited presentation ranges. An
[expanded CJK punctuation and kana corpus](torture/findings/2026-10-15-cjk-kana-quote-ngram.md)
now covers small/full-size kana equality, distinct CJK double-quote forms, and
unbalanced ngram Boolean parentheses: 936 queries per startup token size from 1 to
10 match MySQL in membership and relevance. Exact legacy weight bytes, broader
collation behavior, and MeCab remain open.

An [audited two-column ngram inner join](torture/findings/2026-10-07-fulltext-join-bounds.md#oracle-and-regressions)
also propagates equality, range, and `IN` owner bounds before full-text row
preparation while preserving MySQL's complete-corpus relevance. An [audited
ngram outer join](torture/findings/2026-10-15-ngram-outer-join-scoring.md)
now applies local `ON` bounds from the nullable side of left and right joins
before scoring and returns zero for a null-extended `MATCH` score.
[Preserved-side literal bounds across one outer-join equality](torture/findings/2026-10-15-ngram-outer-transitive-bound.md)
also narrow nullable ngram scoring and row preparation for audited left and
right joins, including `IN` bounds that retain unmatched owners. Broader outer
join graphs and MeCab remain open.

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
`COM_FIELD_LIST` filters table and view column names by its case-insensitive
SQL wildcard; [view metadata](torture/findings/2026-10-11-field-list-views.md)
comes from the session-aware descriptor without reading rows, including the
view-specific scale for string-like columns.
The [wire mutation comparison](torture/findings/2026-10-11-wire-mutation-parity.md)
passes two 166-case MySQL 8.4 corpora, including malformed `COM_FIELD_LIST`,
restricted database selection, and comment-delimiter query boundaries.

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
overloaded scalar, temporal (including
[interval arithmetic operators](torture/findings/2026-10-11-interval-arithmetic-metadata.md)),
JSON, spatial, and registered-extension result families without evaluating the
statement. Typed numeric builtin and cast
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

[Prepared statement database context](torture/findings/2026-10-10-prepared-database-context.md)
now retains the selected database at preparation for unqualified table names,
mutations, metadata binding, and `DATABASE()` during both text and binary
execution, while preserving the caller's database afterward.

| Gap | MySQL 8.4 | fsdb | Impact | Class |
|---|---|---|---|---|
| TLS certificate lifecycle | live certificate/trust-store reload and CRL validation | server and client-CA certificates are loaded when the listener starts; client chains are validated without revocation checks | low (rotation requires restart) | subset |
| Prepared user-variable typing | direct references retain their prepare-time type and refresh on statement reprepare | Parsed statements, including user/system variable `SET` assignments combined with `NAMES`, retain direct variable types and refresh them when explicit parameters force reprepare. Referenced table and view definitions also refresh captured types after DDL. Table-backed projections and indexed predicates, derived tables, CTEs, scalar subqueries, UNION branches, prepared CTAS, and audited UPDATE, DELETE, and INSERT SELECT statements retain variable types across reassignment. Common decimal arithmetic, rounding, and coalescing now propagate result scales and preserve division guard digits. Common expression precision descriptors are derived from operand shapes, and SUM/AVG distinguish untyped NULL and approximate/text inputs from exact numeric inputs; other prepared mutation shapes, expression families, and values beyond System.Decimal precision remain incomplete ([evidence](torture/findings/2026-10-06-prepared-parameter-repreparation.md#direct-user-variable-references)) | medium | divergence |
| Literal and expression charset identity | literals retain their charset, collation, coercibility, and binding context | broader stored-program binding, other quoted-string conversion contexts, non-Unicode/UCS-2 byte handling, definition rendering, and expression charset inference remain incomplete; [valid binary-literal `CONVERT ... USING` decoding](torture/findings/2026-10-11-convert-binary-literal-charset.md), introduced UTF-8/16/32 and audited SJIS/CP932/Big5/GBK/EUC/GB2312/GB18030 hex/bit validation, fixed-width padding, audited ASCII/UCS-2/UTF8MB3 byte preservation, audited encoded reversal, slicing and CONCAT, derived-prefix and audited text-storage conversions (including UCS-2 surrogate code units, length limits, and ASCII diagnostics), and preparation warning lifetimes are [native-verified](torture/findings/2026-10-08-introduced-encoding.md); [audited invalid-byte `CONVERT ... USING` warnings and outputs](torture/findings/2026-10-11-convert-binary-literal-charset.md) now follow UTF-8MB3/4, UTF-16/32, ASCII, UCS-2, audited incomplete multibyte legacy sequences, and the single-byte high-byte matrix, including audited source-to-text and source-to-binary conversions for single-byte, legacy multibyte, and UTF-16/32 values and target-byte preservation for text inputs, while other contexts remain open; [audited CAST AS CHAR CHARACTER SET](torture/findings/2026-10-11-cast-char-charset.md) retains target bytes, collation, and explicit text/binary-width diagnostics; source-expression collations and coercibility now survive the audited view, derived, join, UNION, and ROLLUP paths ([native contracts and boundaries](torture/findings/2026-10-08-text-literal-charsets.md)) | low | divergence |
| Cursor storage | materialized temporary tables spill from memory to disk | read-only, forward-only cursors retain their materialized rows in session memory until exhaustion, reset, close, or commit | low (large concurrent cursors) | divergence |
| Session state tracking | schema, system-variable, generic state, transaction, and GTID trackers | schema, configured system-variable, generic state-change, transaction-characteristic, and transaction-state blocks are encoded in final OK packets; GTID blocks remain absent because fsdb has no binlog | low | subset |
| Diagnostics coverage | warnings from conversions, truncation, deprecated syntax, and storage engines | statement errors, ignored INSERT/CHECK rows, non-strict integer/ENUM/SET/charset coercions, DECIMAL scale-loss notes, declared text/binary truncation, functional-index, numeric-aggregate, and [audited Boolean-context conversion conditions](torture/findings/2026-10-08-predicate-conversion.md), conditional DDL, ignored physical-directory options, unknown-engine substitution, GROUP_CONCAT truncation, deprecated numeric displays, `utf8` aliases and explicit `utf8mb3` declarations/conversions, plus `SQL_CALC_FOUND_ROWS`, `FOUND_ROWS()`, and ODKU `VALUES()` are captured; [trigger warning lifetimes and RESIGNAL condition order](torture/findings/2026-10-08-trigger-warnings.md) are covered; [audited integer CAST and conditional warning behavior](torture/findings/2026-10-08-integer-casts.md) is covered; [audited duplicate-key messages](torture/findings/2026-10-08-key-diagnostics.md) retain the base table and key; [audited missing-object diagnostics](torture/findings/2026-10-08-missing-table.md) preserve database/table identity and distinguish 1049/1051/1146; [single-column ALTER coercion conditions](torture/findings/2026-10-08-alter-coercion.md) preserve live row ordinals and stop at the first duplicate; [audited multi-column ALTER conditions](torture/findings/2026-10-09-alter-row-order.md) follow final-column order and preserve every error from the first failing row; [audited expression-assignment deprecation warnings](torture/findings/2026-10-09-assignment-deprecation.md) preserve syntax counts and preparation timing; [audited foreign-key failure messages](torture/findings/2026-10-09-foreign-key-validation.md) include constraint details; [audited foreign-key name/index collision diagnostics](torture/findings/2026-10-09-foreign-key-collisions.md) are covered; ALTER temporary-table identities, broader conversion contexts, and other warning producers remain divergent; [constant division-by-zero warning multiplicity](torture/findings/2026-10-10-numeric-constant-bounds.md) now follows indexed, empty-indexed, scan, projection, and audited Boolean-branch predicates; deeper conditional combinations remain open; [audited eliminated WHERE branches](torture/findings/2026-10-10-elided-where-bindings.md) bind columns and functions and reject aggregate misuse even on empty tables; audited constant indexed `IF` and `CASE` bounds emit MySQL's conversion-warning counts for both hit and miss results | low | divergence |
| System variables | hundreds live | common connector, limit, transaction, password-policy, week-format, and fixed-offset, `SYSTEM`, and catalog-backed named time-zone variables are live; most others are inert or absent. `sql_safe_updates` blocks audited UPDATE and DELETE statements without an indexed predicate or LIMIT, accepts ON/OFF session settings, and covers [single-target writes filtered through indexed inner and audited outer joins, including `USING` and `NATURAL` keys, equalities in `WHERE`, source-local `ON` filters including null-rejected RIGHT JOIN writes, three-valued constant conditions, and two-target key filters](torture/findings/2026-10-10-safe-update-joined-mutations.md), including uniquely owned bare lookup columns, source-local ranges, IN, null tests, and Boolean combinations; broader optimizer-dependent join forms remain open. [`sql_select_limit`](torture/findings/2026-10-11-sql-select-limit.md) now caps top-level SELECT/UNION and audited SELECT-shaped metadata output, respects explicit LIMIT and prepared execution, and leaves mutation sources and nested subqueries unbounded; EXPLAIN SELECT now shows the implicit zero-limit plan in traditional, JSON, and TREE formats and a nonzero limit node in TREE; traditional and TREE UNION zero-limit plans now distinguish materialized DISTINCT or globally ordered results from streaming UNION ALL; the JSON `union_result` hierarchy covers plain branches, audited materialized derived SELECTs with explicit LIMIT, and simple scalar projection subqueries under their owning SELECT or UNION branch, including audited materialized derived sources, audited dependent WHERE scalars under physical or materialized sources in SELECT and UNION branches, including mixed projection/WHERE ownership, and simple dependent HAVING/ORDER BY or wrapped projection scalars under their grouping, ordering, or DISTINCT operation, including audited ordering-over-grouping, single-scalar DISTINCT-and-ordering cases using an alias or ordinal, and independent single-table HAVING/ORDER BY scalar placement, including audited ordering over indexed grouping, a duplicate-prone DISTINCT projection ordered by an independent scalar, and audited dependent scalar combinations across projection, WHERE, HAVING, and ORDER BY, including grouped four-clause plans; mergeable/lateral/derived-set, broader independent HAVING/ORDER BY, other DISTINCT/ordering and multi-clause cases, and broader expression-subquery nesting, MySQL cost details, and exact ANALYZE presentation remain open. `div_precision_increment` controls division and AVG and is retained by prepared statements ([oracle](torture/findings/2026-10-06-prepared-parameter-repreparation.md#division-precision-increment)). `max_execution_time` settings, audited SELECT deadlines, scalar interruption, and transaction preservation work; audited timeout-hint precedence, preparation diagnostics, and routine-loading warning lifetimes work, while broader hint contexts and extreme-duration parity remain open ([native timeout contract](torture/findings/2026-10-08-select-timeout.md)). `system_time_zone` retains its static bootstrap label | medium | divergence |

The [audited typed `CONVERT(expr, type)` forms](torture/findings/2026-10-11-cast-char-charset.md)
share `CAST` behavior for character targets, including charset and width, and
for `SIGNED`. Bare and explicit-width `BINARY` casts now preserve byte width,
padding, truncation warnings, and audited wire metadata; broader cast
families and source expression shapes remain open. Audited non-`CHAR`
charset clauses are rejected with MySQL's syntax code before charset lookup.
Audited `NCHAR`/`NATIONAL CHARACTER` and `CHAR ASCII`/`CHAR UNICODE` casts
retain their target bytes and syntax-specific deprecation warnings.
Audited `CHAR BINARY` combinations now retain the target charset's `_bin`
collation and report modifier warnings in source order; invalid duplicate or
conflicting modifiers still fail with MySQL's syntax code.
Oversized explicit [CHAR cast widths](torture/findings/2026-10-11-cast-char-charset.md)
now return NULL with MySQL's packet-limit warning before source evaluation,
including national, binary-attribute, and binary-charset targets.
Zero-width CHAR/NCHAR casts retain MySQL's empty result and truncation warning;
negative widths now fail as syntax instead of reaching evaluation. Explicit
CHAR and BINARY cast widths now accept the unsigned 32-bit range, return the
packet warning above `max_allowed_packet`, and reject larger widths with 1439;
multibyte character targets reject declared byte widths over the same ceiling
with 1690 before applying the packet limit.
The [audited CAST target grammar](torture/findings/2026-10-11-cast-target-grammar.md)
now rejects column-only type spellings for both CAST and typed CONVERT, while
`CHARACTER(n)` retains its width; remaining source expression shapes and
conversion diagnostics are still open. FLOAT, TIME, and DATETIME target
precision ceilings now return MySQL's early 1426 diagnostic in both forms;
audited YEAR casts now distinguish text zero from numeric zero, return NULL
with MySQL's invalid/out-of-range warnings, and warn on trailing text;
audited date/datetime/timestamp, TIME, and bare bit/hex sources retain their
distinct year and numeric-origin behavior;
audited TIME casts now return NULL with warning 1292 for invalid text and
numeric overflow, accept a valid time prefix with a truncation warning, parse
`HH:MM`, and clamp overflowing text with warning 1292; leading and trailing
space delimiters now produce MySQL's 4096 warning on audited inputs, and
the audited one- or two-space internal delimiter cases distinguish a retained
time prefix from a NULL result; bit/hex literals and stored BIT/VARBINARY
sources now parse their bytes as text and escape nonprintable warning bytes;
audited binary-result TIME warnings now escape embedded NUL and control bytes,
while text-result warnings stop at NUL; broader delimiter grammar, binary
high-byte previews, and source expressions remain open;
audited DECIMAL precision/scale limits follow MySQL's validation order and
source-expression labels, including the zero-precision default. Negative
FLOAT, TIME, DATETIME, and DECIMAL target precisions now fail as syntax in
both CAST and typed CONVERT. Audited DECIMAL CAST values now warn on invalid
or partially consumed text and clamp rounded overflow to the target precision,
using the projection's source label or alias in the range warning; larger
numeric domains beyond .NET decimal precision remain open.
Audited finite text beyond .NET decimal precision, such as `1e29` cast to
`DECIMAL(10,2)`, now clamps with MySQL's range warning; audited exponents
past the 81-digit expansion boundary retain both conversion warnings before
the range warning. General wide DECIMAL values remain open.
Audited DOUBLE and FLOAT CAST text inputs now retain MySQL's numeric-prefix
warnings; overflowing DOUBLE text clamps to the signed maximum with warning
1292, while overflowing FLOAT text raises 1690. Other source types and numeric
conversion contexts remain open.
Audited numeric CAST text prefixes now use ASCII digits, matching MySQL for
mixed Unicode-digit and fullwidth-digit input.
Audited introduced binary strings and results of `CAST(... AS BINARY)` now
use numeric prefixes for DOUBLE and DECIMAL casts, with their distinct warning
behavior; broader binary-expression sources remain open.
Audited VARBINARY/BLOB column casts now retain their distinct DOUBLE and
DECIMAL warning behavior; other binary-derived expressions remain open.
Numeric CAST warning text from audited fixed-width BINARY padding and high
bytes now uses MySQL's `\xHH` byte escapes.
Audited computed binary strings now share the quiet DECIMAL truncation policy
seen with explicit binary casts; other expression producers remain open.
Audited binary `IF`, `CASE`, `NULLIF`, and source-free scalar-subquery DECIMAL casts now
retain regular truncation warnings, distinct from quiet `COALESCE`, `IFNULL`,
and byte-string functions; broader expression families remain open.
Derived-table and CTE columns now keep the distinct binary-column DECIMAL
warnings for audited literal and computed sources, while projected stored
BLOB values remain quiet; other source boundaries remain open.
Audited scalar subqueries with a `FROM` source now use quiet binary DECIMAL
and DOUBLE conversion, distinct from source-free scalar subqueries.
Audited NUL-containing text casts stop warning previews at the NUL; binary
casts retain the distinct `\x00` representation.
Audited numeric CAST warning arguments now stop after 128 encoded bytes,
including UTF-8 text cut mid-character; other source/client charset
combinations remain open.

The [audited missing-function diagnostics](torture/findings/2026-10-10-missing-function-dml.md)
now resolve unqualified calls against the selected database in common
expression-bearing reads, mutations, `DO`, and `SET`; absent database context
returns 1046 even for qualified-table mutations.
[View-definition and CTAS function binding](torture/findings/2026-10-10-view-function-binding.md)
now rejects missing functions before publication while preserving valid builtin
and stored-function calls. Cross-schema views validate an unqualified function
against the selected creation schema, then resolve it in the target schema at
read time; a missing target function produces MySQL's view error 1356.
[Existing stored functions without a selected database](torture/findings/2026-10-10-existing-function-no-database.md)
also follow MySQL's 1046 rule in the audited expression statements, while
qualified calls and qualified view reads remain valid.
Qualified stored routines now resolve their nested unqualified function calls
in the routine definition schema, including calls from a session with no
selected database or another selected schema.
[Prepared stored-function schema binding](torture/findings/2026-10-10-prepared-function-schema.md)
now validates names at preparation and retains the preparation schema across
later `USE` changes in the audited text and binary paths.

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
| INFORMATION_SCHEMA breadth | INNODB_*, KEYWORDS, PLUGINS, spatial-reference catalogs, and usage views | MySQL 8.4's catalog names are present. `INNODB_TRX` projects active transaction identity, lifecycle, isolation, checks, logical write weight, and held row stripes; fields that require InnoDB's lock-memory and scheduling internals remain zero or NULL. Other InnoDB dictionary views project live table, column, index, statistics, and virtual-column metadata; physical diagnostics return truthful empty rowsets where fsdb has no matching buffer-pool, tablespace, compression, or metrics subsystem. `ST_SPATIAL_REFERENCE_SYSTEMS` exposes SRID 0 and fsdb's audited EPSG subset rather than MySQL's full registry | low | divergence |
| Table statistics | estimates refreshed by ANALYZE TABLE | [Cached row and index-prefix estimates, session expiry, ANALYZE refresh, and recovery](torture/findings/2026-10-10-table-statistics.md) match the audited lifecycle; zero-expiry index reads can differ because InnoDB samples from state fsdb does not model. [Table-size metadata](torture/findings/2026-10-11-table-size-metadata.md) now shares a bounded-sample, page-rounded payload estimate across `INFORMATION_SCHEMA.TABLES` and `SHOW TABLE STATUS`, including the empty-table page and row-estimate-based average; it does not model InnoDB page splits or retained allocations, and exact row counts can differ from InnoDB samples | low | divergence |
| Index listing order | `SHOW CREATE TABLE` and `SHOW INDEX` list keys by class: `PRIMARY`, `NOT NULL` unique keys, nullable unique keys, ordinary keys, then `FULLTEXT`, each in creation order | both list keys in declaration order, so a key added before a unique key (such as a foreign-key index that Laravel-style DDL creates before a later `ADD UNIQUE`) precedes it, and a `FULLTEXT` key can precede ordinary keys; definitions and generated-index lifecycle match | low | divergence |
| Optimizer cost overrides | `mysql.server_cost` and `mysql.engine_cost` values feed plan costs after `FLUSH OPTIMIZER_COSTS` | both tables expose MySQL's bootstrap rows, generated defaults, and mutable override columns; fsdb's shape-driven planner does not consume their overrides | low | divergence |
| SHOW STATUS counters | Com_*, Innodb_*, Slow_queries, … | `Com_*` names have distinct session/global values and supported commands are live; [`Slow_queries` and fractional `long_query_time`](torture/findings/2026-10-10-slow-query-status.md) track completed text, prepared, and load statements including upload/file-read time and failed local uploads, and the global count appears in `COM_STATISTICS`, which counts itself as a question; [`Threads_running`](torture/findings/2026-10-10-running-thread-status.md) reflects active connections and the enabled scheduler; [global wire-byte totals](torture/findings/2026-10-11-global-wire-byte-status.md) include every connection while session totals remain local and `FLUSH STATUS` resets all active sessions' byte counts. [Connection attempts, aborted connection setup and established clients, the concurrent-connection high-water mark, and uptime since the last status flush](torture/findings/2026-10-11-connection-status-counters.md) now report live process values in both scopes. Wider engine/latency families remain absent ([`InformationSchema.fs`](src/Fsdb/Engine/InformationSchema.fs)) | low | divergence |
| Logging | general log, slow log, error-log file | With `log_output=TABLE`, `mysql.general_log` records text queries, SQL/binary preparation and execution, successful wire Connect/Init DB/Quit events, `Field List` requests including failed lookups, and `Statistics`/`Debug` requests independently of the issuing transaction. `mysql.slow_log` now records completed text and binary prepared statements over `long_query_time`, including failures; scalar reads, single-table scans, `INSERT … SELECT` source reads, single-table UPDATE/DELETE, audited single-table FULLTEXT posting, explicit-sort, grouped SUM/GROUP BY, and index-only COUNT(*) and non-null literal/column reads, and audited two-source full-text-to-indexed-owner joins, including Boolean first-match and selective unbounded reversed-source planning, and audited inner/outer indexed and scan-shaped joins (including simple scan-shaped left equi-joins with early LIMIT, audited indexed right-side ON filters, and indexed two-table left joins with null-rejecting WHERE filters and audited OR/IN/BETWEEN/negated-range/integer-inequality bounds) count examined candidates, while other join shapes, broader full-text joins (including complex reversed predicates and multi-source cost choices) and broader full-text aggregate expressions, and other mutations need broader accounting and lock time is currently zero. `NONE` suppresses output. FILE logging is explicitly refused when enabled; other wire commands and an error-log file remain open. Diagnostics go to credential-redacted stderr ([`Log.fs`](src/Fsdb/Foundation/Log.fs), [general-log oracle](torture/findings/2026-10-11-general-log-baseline.md), [slow-log oracle](torture/findings/2026-10-11-slow-log-baseline.md)) | low | partial/refusal |
| Replication | binlog, GTID, source/replica channels | no replication execution; REPLICATION privileges are vocabulary only and internal WAL is not a binlog; native catalog schemas and stock group-action configuration rows exist for metadata compatibility | architectural | refusal |

## 15. Differential-testing and performance tails

The [Laravel application compatibility probe](torture/findings/2026-10-16-real-app-compatibility.md)
ran migrations and selected feature tests from Reflow, Boatflow, Chatflow,
Spamcalls, and Sysflow against isolated fsdb databases. It exposed a
stack-overflowing prepared bulk-insert binder, now covered by a 3,000-marker
regression. An overlapping three-app run also produced a 50-second metadata
lock wait that still needs an isolated scheduler reproduction. The
`crescat-website` migration stops at partial-index syntax rejected by both
fsdb and MySQL 8.4.

The [null-rejected OR join snapshot](benchmarks/results/2026-10-11-null-rejected-or-join.md)
compares the indexed-union handoff with its prior revision on native servers.
The focused fsdb query fell from about 4.63 ms to 0.18 ms at 1,000 users and
5,000 orders; the same-run point-select control stayed near 0.17 ms.

The [safe-update join snapshot](benchmarks/results/224915e6-safe-update-joins.md)
checks the recent joined UPDATE work on native same-host servers. Rejected
joined updates reached about 0.20 ms on fsdb and 0.05 ms on MySQL in the
last measured block; the control and mutation workloads drifted across
blocks, so the snapshot is a profiling baseline rather than a regression
claim.

The [line-interpolation wire snapshot](benchmarks/results/2026-10-10-line-interpolation.md)
compares the recent spatial functions with a simple-query control on native
fsdb and MySQL 8.4.11. Fixed overhead dominates the short cases, but the
control's variation prevents a precise relative-performance claim.

The [spatial topology wire snapshot](benchmarks/results/2026-10-11-spatial-topology-latency.md)
compares `ST_Crosses` and `ST_Overlaps` with matching `ST_Intersects` controls
on native servers. After warmup, the new predicates are near their controls;
large early drift and no pre-change baseline prevent a regression claim.

The [BIT/binary foreign-key update snapshot](benchmarks/results/2026-10-09-foreign-key-bit-binary.md)
adds a same-type control for the recently corrected byte-key path. Its short
run is too noisy to identify a lookup penalty; a durability-matched scaling
measurement remains open.

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

The [current Release point-lookup allocation comparison](benchmarks/results/2026-10-11-current-point-lookup-allocation.md)
measured about 35 KiB per pre-parsed executor call and 95–102 KiB per warmed
text or reused prepared handler call. A sampled trace ranked execution-context
and `AsyncLocal` map objects among the largest families. Moving two synchronous
execution-limit scopes to thread-local storage reduced handler allocation by
about 3.8–4.7 KiB per call on that fixture; a latency gain and the remaining
fixed-cost gap are unproven.

A [follow-up Release comparison](benchmarks/results/2026-10-11-thread-scoped-variable-context.md)
uses thread-local variable and interval-time-zone scopes for synchronous
QueryHandler execution while retaining the public asynchronous scopes.
Warmed text and reused prepared handler allocation fell by about 6.5 and
6.2 KiB per call on the fixed lookup fixture. The short latency runs still
do not establish a speedup.

The [next Release scope comparison](benchmarks/results/2026-10-11-thread-scoped-function-database.md)
uses a thread-local function-database scope for synchronous QueryHandler
execution while preserving the public asynchronous scope. Warmed text and
reused prepared handler allocation fell by about 1.4 and 3.2 KiB per call;
short-run latency remained inconclusive.

The [empty stored-function registry comparison](benchmarks/results/2026-10-11-empty-stored-function-registry.md)
then avoided temporary session records when the stored-function catalog is
empty. Warmed text and reused prepared handler allocation fell by about
1.2 and 1.0 KiB per call on the same fixture. The populated-catalog path
still decorates and restores the session; short-run latency is inconclusive.

The [synchronous trigger-executor scope comparison](benchmarks/results/2026-10-11-thread-scoped-trigger-executor.md)
replaced an internal `AsyncLocal` callback scope with an exception-restoring
thread-local one. Warmed text and reused prepared handler allocation fell by
about 1.3 and 1.7 KiB per call, with concurrent trigger sessions covered by
a regression. Short-run latency remains inconclusive.

The [synchronous division-precision scope comparison](benchmarks/results/2026-10-11-thread-scoped-division-precision.md)
then retained the public task-flowing scope while moving prepared QueryHandler
work to a thread-local scope. Reused prepared handler allocation fell by
about 1.0 KiB per call on the fixed lookup fixture; short-run latency did
not establish a change.

The [stored-function session scope comparison](benchmarks/results/2026-10-11-thread-scoped-stored-function-session.md)
moved another internal, synchronous statement scope to thread-local storage.
Warmed text and reused prepared handler allocation fell by about 1.5 and
1.3 KiB per call. Nested and concurrent function-session regressions pass;
short-run latency remains inconclusive.

A [three-run indexed constant-bound snapshot](benchmarks/results/2026-10-10-constant-index-bounds.md)
finds fsdb's fixed primary-key lookup around 220–248 µs versus MySQL's
41–43 µs. `ROUND(PI())` and `BIT_COUNT(7)` lookups fall within the run-to-run
variation of that control, so the shared lookup/query pipeline is the next
profile target rather than a proven expression-folding regression.
An [in-process decomposition](benchmarks/results/2026-10-10-point-lookup-pipeline.md)
places parsing, execution, and session handling at about 24, 12, and 85 µs
for the fixed lookup; their allocations are about 70, 39, and 254 KB per call.
The stages are measured separately; their unexplained session-path cost
motivated the next profile.

A [prepared-lookup follow-up](benchmarks/results/2026-10-11-embedded-registry-cache.md)
identified per-statement function-registry construction as a major cost. A
session-input cache reduced the independent validator's prepared typed lookup
from about 95 to 68 µs and from about 322 to 132 KB per call while preserving
changing session function values. The remaining allocation and wire-path
overhead still need profiling; this does not close the broader point-lookup
performance gap.
A [current project-backed stage profile](benchmarks/results/2026-10-11-point-lookup-pipeline.md)
puts the fixed text-handler lookup near 50 µs and 109 KiB per call on its
same-process fixture; separate parser and pre-parsed executor actions take
about 26 and 13 µs. The warmed text handler reuses a cached AST, so its
per-call cost excludes standalone parsing. Statement handling and execution,
plus a separate prepared-result profile, are the next measured targets.
A [same-fixture reused prepared profile](benchmarks/results/2026-10-11-prepared-point-lookup.md)
puts cached text and prepared handling near 50 and 47 µs per call, with about
109 and 116 KiB allocated. The small latency difference is noisy; both paths
prompted an allocation profile before changing runtime code.
An [allocation trace and exact-integer probe follow-up](benchmarks/results/2026-10-11-indexed-probe-fastpath.md)
removed warning-suppressed coercion for already in-range signed `INT` and
`BIGINT` equality probes. It saves about 3.4 KiB per call on the shared
fixture; the separate-run latency comparison is inconclusive. The larger
statement and execution allocation gap remains open.
A [single-value global-variable lookup follow-up](benchmarks/results/2026-10-11-global-variable-lookup.md)
avoids rebuilding all live defaults for each missing GLOBAL override. It
removes about 10.4 KiB per lookup call on the same fixture while preserving
live limits, store defaults, and explicit overrides. The residual allocation
and cross-process latency gap remain open.
An [unchanged SQL-mode follow-up](benchmarks/results/2026-10-11-sql-mode-reparse.md)
avoids parsing and rebuilding the current execution settings when a prepared
statement reapplies identical mode text. It saves about 1.1 KiB per call on
the shared fixture; separate-process timings do not establish a latency gain.
An [immutable planner-set follow-up](benchmarks/results/2026-10-11-planner-foldable-set.md)
shares the foldable builtin names across constant evaluators, removing about
5.1 KiB per prepared lookup on that fixture. Residual allocations remain
substantial, and separate-process timings do not establish a latency gain.
A [view-dependency lookup follow-up](benchmarks/results/2026-10-11-view-dependency-lookup.md)
avoids rebuilding the stored-view map for ordinary physical-table reads. It
saves about 1.2 KiB per cached text lookup and 1.8 KiB per prepared lookup
on the same fixture; view lock dependencies retain their tested behavior.
A [default `sql_select_limit` hot-path comparison](benchmarks/results/2026-10-11-select-limit-hotpath.md)
found that the first helper added about 152 B per cached text lookup and
120 B per reused prepared lookup on warmed process medians. A direct,
allocation-light helper reduced the measured excess to about 36 B and
52 B respectively; the short process runs do not establish a latency change.

A [same-host indexed-conjunction snapshot](benchmarks/results/6aaef020-quick.md)
on 10,000 users measures fsdb at about 0.60 ms with either two usable indexes
or one, versus 6.56 ms for the scan-shaped expression; MySQL takes about
0.15–0.16 ms on the indexed forms and 1.57 ms on the scan. Three measured
iterations and a broad confidence interval make this a profile target, not
evidence that index intersection itself improves this workload.

The [numeric aggregate decomposition](benchmarks/results/2026-10-10-numeric-aggregate-components-baseline.md)
isolates most of the 10,000-row gap to text casts feeding SUM/AVG rather than
the integer SUM or COUNT control. In an [A/B baseline](benchmarks/results/2026-10-10-numeric-cast-ab-baseline.md)
and [fixed run](benchmarks/results/2026-10-10-numeric-cast-ab-fixed.md),
skipping repeated expression-metadata inference for a direct integer column
reduces `SUM(CAST(age AS CHAR))` from about 52 to 32 milliseconds (38%). The
[combined follow-up](benchmarks/results/2026-10-10-numeric-aggregate-components-fixed.md)
falls from about 166 to 117 milliseconds (30%); the plain `SUM(age)` control
remains about 4 milliseconds. MySQL stays near 1.15 milliseconds for the
single cast aggregate and 2.25 milliseconds for the combined query, so
text-to-number conversion and aggregate execution remain large scale gaps.
The mode-matched native MySQL 8.4.11 contract run passed 123 cases and
16,517 steps after the change; the pinned Linux mode-0 run reported only its
nine previously documented identifier-case differences.

An [original-parser](benchmarks/results/2026-10-10-numeric-parser-baseline.md)
versus [whole-number fast-path](benchmarks/results/2026-10-10-numeric-parser-fast.md)
comparison did not isolate the regex as the dominant cost: the single-cast
query measured about 31 milliseconds in both runs, while the combined query's
small difference overlaps short-run variation. The fast path was discarded;
the regex was not the main remaining cost.

A server-side runtime sample instead exposed repeated storage coercion and
diagnostic-scope setup for numeric `CAST(... AS CHAR)` values. Returning the
same text directly for those values, while retaining storage coercion for
other cast inputs, reduces the [10,000-row same-host comparison](benchmarks/results/2026-10-10-numeric-char-cast-fast.md)
from roughly 31 to 13 milliseconds for the single cast aggregate and from
roughly 108 to 55 milliseconds for the combined query. The plain integer SUM
control remains near 4 milliseconds. MySQL remains around 1.1 and 2.3
milliseconds respectively, so row preparation and aggregate evaluation still
warrant profiling. The native MySQL 8.4.11 contract lane passed all 123 cases
and 16,517 steps with zero differences after this change.

The next profile found that DISTINCT numeric aggregates opened a diagnostic
suppression scope for every converted value. A quiet conversion path preserves
expression warnings while omitting only numeric-conversion warnings. In the
[same-host baseline](benchmarks/results/2026-10-10-numeric-distinct-quiet-baseline.md)
and [changed run](benchmarks/results/2026-10-10-numeric-distinct-quiet-fixed.md),
`SUM(DISTINCT CAST(age AS CHAR))` falls from about 31 to 15 milliseconds,
and the combined text-numeric query falls from about 54 to 38 milliseconds.
The ordinary cast SUM and MySQL controls remain stable; DISTINCT input
materialization and non-DISTINCT aggregate evaluation remain scale gaps. The
native MySQL 8.4.11 contract lane again passed all 123 cases and 16,517 steps
with zero differences.

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
A [focused matched pair](benchmarks/results/c399e5d0-durable-write-pair.md)
records two native ShortRuns of sixteen sequential autocommit inserts with
WAL-backed fsdb and durable MySQL 8.4.11; the broader gate did not complete
on the disk-constrained host.

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
The [October 14 repeat check](benchmarks/results/2026-10-14-correlated-quick-check.md)
finds the bounded derived and CTE equality queries around four times MySQL's
whole-query latency in its stable quick-run pair; a point-lookup control also
lags, so that ratio is not an isolated correlated-planner cost.

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

The [composed-key equality and scan snapshot](benchmarks/results/2026-10-10-functional-index-baseline.md)
and [one-column primary-key control](benchmarks/results/2026-10-10-functional-index-id-pk-control.md)
use native fsdb and MySQL 8.4.11 on the same host. At 10,000 users, fsdb's
indexed lookup takes about 385 microseconds versus about 70 milliseconds for
its forced scan. The one-column control takes about 292 microseconds for a
primary-key lookup and 385 microseconds for the composed key; MySQL takes about
39 and 49 microseconds respectively. This points to both shared request cost
and additional work in the functional-query path. The short runs and differing
table shapes do not isolate which stage accounts for that additional work.

The [prepared-statement control](benchmarks/results/2026-10-10-prepared-functional-index-baseline.md)
reuses prepared commands for the same single-row primary-key and composed-key
lookups. MySQL returns the functional row in about 42 microseconds, while fsdb
takes about 49 milliseconds. A [repeat run](benchmarks/results/2026-10-10-prepared-functional-index-repeat.md)
with a first-row setup assertion still takes about 45 milliseconds on fsdb;
that assertion did not catch the incorrect extra rows. The cause was
prepared-parameter type inference: a boolean condition's integer result type
propagated into the equality's string parameter, converting `USER_250` to
integer zero.
The [fixed run](benchmarks/results/2026-10-10-prepared-functional-index-fixed.md)
reduces fsdb's prepared composed-key lookup to
about 278 microseconds, versus about 242 microseconds for its prepared
primary-key control; MySQL takes about 41 and 32 microseconds respectively.
A [targeted repeat](benchmarks/results/2026-10-10-prepared-functional-index-strict-row-check.md)
requires exactly one returned row and measures about 251 microseconds on fsdb.
The [MySQL 8.4.11 oracle probes](torture/findings/2026-10-10-prepared-comparison-inference.md)
with `lower_case_table_names=2` also return the same
single-row result for direct, conjunctive, and reversed prepared equalities,
and the expected two rows for a disjunction with a second ID; focused fsdb
regressions cover those four shapes.
The residual difference is part of the broader lookup overhead above.

A [native quick run](benchmarks/results/2026-10-10-mutation-quick-first.md)
and [repeat](benchmarks/results/36185ad2-quick.md) at `36185ad2` put the
six-column primary-key read at 437–461 microseconds for fsdb and 44–45
microseconds for MySQL 8.4.11. The same runs put single-row updates at
472–501 microseconds for fsdb and 132–135 for MySQL, and secondary-range
updates at 650–668 versus 127–137 microseconds. The write comparisons use
in-memory fsdb against durable MySQL, so they are directional latency
measurements rather than deployment write-parity claims. The stable read gap
keeps shared request and query setup worth profiling before another
micro-optimization.

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

A [durable concurrent-insert profile](benchmarks/results/2026-10-14-statement-admission-contention.md)
found process-wide contention in parsed-statement admission for unique literal
SQL texts. Fixed-size atomic admission slots removed that hot spot in the
audited load; full SQL and parser options still key the parsed statements.
Durable write throughput and worker scaling remain below MySQL.
A [current-head durability-matched checkpoint](benchmarks/results/2026-10-15-current-head-write-follow-up.md)
still measures about 0.27× MySQL throughput with eight wire writers; the same
WAL workload reaches about 2.65× the FSDB wire rate through the embedded API.
That comparison does not isolate a server function or scheduling lock.

An [audited correlated binary LIKE prefix](torture/findings/2026-10-15-correlated-like-prefix-audit.md)
already uses the indexed range path for actual rows. A NULL outer pattern now
supplies an empty candidate set in both metadata validation and execution.

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

2. Transaction scheduling. Indexed point/range UPDATE and DELETE statements,
   plus [ordered `LIMIT 1`, `LIMIT 2`, and `LIMIT 3` UPDATE and DELETE re-selection after a contended
   first candidate stops matching, moves behind another candidate, or is
   deleted](torture/findings/2026-10-16-limited-ordered-write-rebase.md)
   with and without a secondary predicate index, plus pending inserts across
   integer, valid `DATE`, [text](torture/findings/2026-10-16-limited-ordered-write-rebase.md#text-primary-keys),
   audited two-part integer, and [text-plus-integer](torture/findings/2026-10-16-limited-ordered-write-rebase.md#composite-text-and-integer-primary-keys) primary-key order
   frontiers, plus audited joined UPDATE and DELETE statements with named
   physical targets,
   [audited single-table updatable-view UPDATE and DELETE statements and joined-view
   UPDATE statements with one writable physical target](torture/findings/2026-10-15-view-write-rebase.md),
   and joined UPDATEs with uniquely owned bare assignments, including
   [CTE-joined physical targets](torture/findings/2026-10-14-cte-joined-write-rebase.md),
   [targets inside join groups](torture/findings/2026-10-14-grouped-joined-write-rebase.md),
   [bare assignments beside derived sources](torture/findings/2026-10-14-derived-joined-write-rebase.md),
   and [bare assignments beside `LATERAL` or `JSON_TABLE` read sources](torture/findings/2026-10-14-unqualified-joined-update-rebase.md#lateral-and-json_table-read-sources),
   wait and rebase across all target tables. Audited [view-addressed INSERT
   SELECT](torture/findings/2026-10-15-view-write-rebase.md) also waits on the
   physical primary key and reports duplicate key after the owner commits.
   The audited [joined UPDATE](torture/findings/2026-10-14-joined-update-rebase.md),
   [joined DELETE](torture/findings/2026-10-14-joined-delete-rebase.md),
   [multi-target writes](torture/findings/2026-10-14-multi-target-write-rebase.md),
   and [unqualified targets](torture/findings/2026-10-14-unqualified-joined-update-rebase.md)
   already wait and rebase. A [joined UPDATE addressed to a single-table
   updatable view](torture/findings/2026-10-15-joined-view-mutation-source.md)
   now claims and rebases its physical row; [audited inner joined view
   sources](torture/findings/2026-10-15-nested-joined-view-mutation-source.md)
   do likewise, including uniquely owned bare assignments. Literal equality
   on the writable physical target avoids waiting for an unrelated row, and
   generic joined writes now lock their matched rows at storage publication.
   [Pending PRIMARY-key inserts](torture/findings/2026-10-16-limited-ordered-write-rebase.md#pending-inserted-candidates)
   now delay ordered limited writes when their key crosses an audited uniform
   order frontier. [Mixed-direction composite ordering](torture/findings/2026-10-16-limited-ordered-write-rebase.md#composite-primary-keys)
   waits on all active primary claims as observed in the native oracle;
   [audited secondary-index orderings](torture/findings/2026-10-16-limited-ordered-write-rebase.md#secondary-index-ordered-frontiers)
   also wait for crossing pending inserts without serializing equal-key
   inserts, while unrelated inserts beyond uniform-order frontiers remain
   concurrent.
   Broader write shapes remain to audit.
   A [durability-matched dependency profile](benchmarks/results/2026-10-14-write-dependency-profile.md)
   found no throughput gain from removing a trigger-metadata lazy monitor in
   the eight-writer keyless-insert path; that experiment was reverted.
   A [completion-option check](benchmarks/results/2026-10-14-write-completion-check.md)
   likewise found no eight-writer gain from changing group-commit task
   continuations; the candidate was reverted.
   A [WAL queue and batch-stage profile](benchmarks/results/2026-10-15-wal-queue-stage-profile.md)
   found queue admission small relative to completion and measured per-batch
   file-open and flush costs. A retained-handle prototype removed the open
   cost but did not establish a stable wire-throughput gain, so it was reverted.
   A [method-resolved wire trace](benchmarks/results/2026-10-15-wire-method-trace.md)
   sampled statement execution, dependency expansion, and WAL work under
   bracketed eight-writer load. It did not isolate a causal lock or support a
   production scheduling change. [Direct timing](benchmarks/results/2026-10-15-write-dependency-timing.md)
   then measured dependency discovery and expansion at about 9–10 µs per call
   versus about 474 µs for the whole query handler; the sampled dependency
   monitor is not the dominant wire-write cost. A [one-versus-eight-writer stage
   timing](benchmarks/results/2026-10-15-wire-write-stage-timing.md) measured
   WAL completion inside the handler rising from about 53 to 273 µs per call;
   handler time outside that completion also rose from about 68 to 211 µs.
   An [async MySqlConnector control](benchmarks/results/2026-10-15-async-wire-client-control.md)
   kept FSDB near 12,000 eight-writer inserts/sec while lowering the MySQL
   control; changing the client call path did not close FSDB's wire gap.
   A [publication and queue-role profile](benchmarks/results/2026-10-15-publication-and-queue-role-profile.md)
   measured about 12 µs waiting for the database publication slot versus 261 µs
   in WAL acknowledgement at eight writers. Group-commit leaders returned about
   502 µs after their own completion signal because they continued draining
   later batches. The latter is a concrete latency cost, but no safe handoff
   implementation or repeated throughput improvement has been established.
   A [bounded leader-handoff prototype](benchmarks/results/2026-10-15-group-commit-leader-handoff-check.md)
   moved the remaining drain to one thread-pool worker. Two-trial A/B/A runs
   showed only a 1–3% eight-writer throughput difference and no material
   improvement in the FSDB/MySQL ratio; the added scheduler complexity was
   reverted.
   A [later current-head durable checkpoint](benchmarks/results/3aa41906-durable-keyless.md)
   again measured about 0.45× MySQL throughput with one writer and 0.28×
   with eight. The write-scaling gap remains open; that matched run does not
   justify changing queue ownership or WAL policy.
   The group-commit leader now drains successive batches iteratively. A
   continuously replenished queue previously grew one call frame per batch;
   the [focused regression and matched write check](benchmarks/results/2026-10-15-iterative-group-commit-drain.md)
   demonstrate bounded stack depth without changing ordering, checkpoint,
   failure, or backpressure semantics. This addresses sustained-drain safety,
   not the measured write-throughput gap.

3. Complex join-derived updatable views. Procedures, functions, and triggers
   cover nested calls with local OUT/INOUT targets, typed locals, condition
   handlers, SIGNAL/RESIGNAL, branches, labeled loops, cursors, and sequential
   data-changing statements.

4. Broader spatial behavior. EPSG 4326 construction, axis order,
   coordinate domains, ellipsoidal point distance, line length, spherical
   point/multipoint distance, and linear units are covered alongside planar
   spatial indexes and operations; EPSG 3857 and 3395 projected geometry use planar
   metric operations, with `ST_Transform` among EPSG 4326, 3857, and 3395.
   [EPSG 32633 UTM zone 33N](torture/findings/2026-10-11-utm33-north.md)
   adds metre-based geometry and transforms through WGS 84;
   The [ETRS89 UTM zones 28N–37N](torture/findings/2026-10-11-etrs89-utm-zones.md)
   use the GRS 1980 ellipsoid, while the [WGS 84 UTM zone family](torture/findings/2026-10-11-wgs84-utm-zones.md)
   covers all 120 northern and southern zone definitions and transforms.
   [NAD83 UTM zones 1N–23N](torture/findings/2026-10-11-nad83-utm-zones.md)
   also preserve the NAD83-to-WGS84 datum shift. The
   [NAD27 geography](torture/findings/2026-10-11-nad27-helmert-oracle.md)
   uses Clarke 1866 and a seven-parameter datum shift;
   [NAD27 UTM zones 1N–22N](torture/findings/2026-10-11-nad27-utm-zones.md)
   use that datum's ellipsoid and direct same-datum transforms.
   `ST_SwapXY` covers all supported geometry shapes. Line interpolation and
   point-at-distance use planar segment lengths or MySQL's Andoyer/Thomas
   geographic strategy. Geographic multipoint distance is also covered;
   geographic point-set topology is covered; [ETRS89 (4258) and NAD83 (4269)](torture/findings/2026-10-11-grs1980-geographic-systems.md)
   geographic constructors, catalog rows, and distance use their GRS 1980
   ellipsoid and transform through WGS84.
   [EPSG 4030](torture/findings/2026-10-11-unspecified-wgs84-datum.md) uses
   WGS 84 ellipsoidal operations but refuses cross-SRS transforms without a
   datum shift. [Inverse projected longitude wrapping](torture/findings/2026-10-11-projected-longitude-wrap.md)
   follows MySQL across geographic and projected targets. Geographic
   [point-to-line distance](torture/findings/2026-10-11-geographic-point-line-distance.md)
   and [line-to-line distance](torture/findings/2026-10-11-geographic-line-line-distance.md)
   are covered for audited cases. [Point-to-polygon distance](torture/findings/2026-10-11-geographic-point-polygon-distance.md)
   covers audited holes, antimeridian crossings, and polar caps. [Line-to-polygon distance](torture/findings/2026-10-11-geographic-line-polygon-distance.md)
   covers audited crossings, containment, holes, and multipart shapes.
   [Polygon-to-polygon distance](torture/findings/2026-10-11-geographic-polygon-polygon-distance.md)
   covers audited overlap, containment, holes, and multipart shapes. A line
   exactly following a [geographic polygon hole edge](torture/findings/2026-10-14-geographic-hole-edge-touch.md)
   now reports boundary contact rather than crossing. Near-boundary
   [machine-precision offsets](torture/findings/2026-10-14-geographic-edge-topology-audit.md#near-machine-precision-hole-edge-offsets)
   no longer allow crossing without an intersection, but MySQL's
   intersection and distance thresholds still differ from fsdb's.
   Broader non-point topology beyond [point/line membership](torture/findings/2026-10-11-geographic-point-line-topology.md) and [point/polygon membership](torture/findings/2026-10-11-geographic-point-polygon-topology.md),
   and other reference systems remain open.

5. Extensible authentication providers. The built-in caching-SHA2, SHA-256,
   and native password exchanges are covered; external identity providers and
   proxy-user selection are not.

6. Replication, logging, broad engine counters, and the remaining metadata
   tail. Core command counters are live; replication remains architectural.
