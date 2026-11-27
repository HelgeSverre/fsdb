# Subqueries that read a saved view's base table

MySQL 8.4.11 can report `IS_UPDATABLE=NO` for a single-source view
whose saved query also opens the source table in a subquery. It rejects
`WITH CHECK OPTION` with 1368 for that definition and for a direct
outer layer over it. Catalog updateability also accounts for a
materialized read and an uncorrelated projection subquery.
[MySQL view registration](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_view.cc#L705-L759)

```sql
CREATE TABLE items (id INT PRIMARY KEY, n INT);
INSERT INTO items VALUES (1, 10);
CREATE VIEW filtered AS SELECT id, n FROM items
WHERE EXISTS (SELECT 1 FROM items r WHERE r.id = items.id);
CREATE VIEW outer_filter AS SELECT id, n FROM filtered;

UPDATE filtered SET n = 11 WHERE id = 1;  -- 1288
INSERT INTO filtered VALUES (2, 20);
UPDATE outer_filter SET n = 11 WHERE id = 1;
DELETE FROM outer_filter WHERE id = 1;   -- 1395
```

For an update, the physical assignment target determines whether a
saved predicate or ordering subquery conflicts. Updating the other
component of an inner-join view can still succeed. A materialized
derived table or view supplies a separate read, and an uncorrelated
projection can remain usable for updating a directly exposed column.

An outer view over a predicate that keeps a second source can return
1395 for DELETE or REPLACE with a column list. An implicit column list
returns 1394 for INSERT and REPLACE, while explicit-column INSERT can
succeed. A source-free predicate or an unrelated
subquery eliminated by a constant-false condition can still allow the
outer delete. A projection that reads the target table can instead
produce 1288 for DELETE without preventing UPDATE.

The 137-observation matrix improved from 58 exact MySQL matches to 112
in each fsdb storage mode. It covers metadata, UPDATE, INSERT, DELETE,
and CHECK OPTION across 26 saved definitions plus seven joined writes.
Eight statements matched the expected errors and completed without
waiting while another session held the base row. Their binary PREPARE
diagnostics also matched, including after a durable restart.

Remaining differences include some INSERTs through views with indirect
self-reads and dependent outer predicates.

## Unused top-level CTEs

An unused WITH declaration leaves a saved view over a physical table writable.
View write analysis now follows the CTEs reachable from the query. It keeps the
dependencies of live declarations and respects nested shadowing and UNION
scope. The catalog preserves the saved SQL. SHOW CREATE VIEW and the
information schema omit unused top-level bindings in their canonical
definitions, as MySQL does. Unused declarations can include recursive
queries and references to missing tables or columns.

The initial MySQL 8.4.11 comparison matched 74 of 75 observations across
15 definitions in memory, with WAL, and after a checkpoint restart. It
checked IS_UPDATABLE, UPDATE, INSERT, DELETE, and WITH CHECK OPTION.
The check option still rejected an update that would remove a row from the
view. The live CTE case led to the follow-up below.

## CHECK OPTION over read-only sources

MySQL accepts LOCAL and CASCADED CHECK OPTION when the outer view exposes
columns from a derived table or CTE. This includes recursive and aggregate
CTEs, nested saved views, and inner joins whose sources are all read-only.
The outer query's DISTINCT, LIMIT, and window functions still make the view
ineligible. A window used only in ORDER BY counts at the outer level as well.
The metadata and DDL classification are separate from write target resolution.

The pinned MySQL 8.4.11 comparison matched all 42 tested CREATE, ALTER,
and CREATE OR REPLACE decisions in memory and with WAL. The returned
view metadata and rows in the restart fixture also matched before and
after reopening the checkpoint. Rejected writes preserved the base rows.
Fsdb also quotes the view name in the corresponding 1471 INSERT diagnostic.

## DELETE from merged join views

MySQL checks whether the merged view has an updatable underlying table before
checking whether it spans several tables. A physical table beside a CTE or
derived table is enough for error 1395, even when the projection contains no
column from the physical table. A view composed entirely of read-only query
sources still reports 1288. The rule applies through nested merged views
and names the outer view. Source binding can report an invalid view before
either eligibility error.
[MySQL DELETE preparation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_delete.cc#L672-L700)

The pinned MySQL 8.4.11 comparison now matches the SQLSTATE, code, and
message in all 32 tested errors across the original, extended, and
invalid-view fixtures. Both storage modes agree. A checkpoint restart
preserved the extended fixture's results, including PREPARE and EXPLAIN.
No base rows changed.

## Nested TEMPTABLE views

Each nested definition now uses its own view algorithm during mutation
planning. A stored view that cannot be merged supplies no physical write
target to its outer view. The regression covers UPDATE, DELETE, and INSERT
against an outer projection over TEMPTABLE and verifies the base rows.
The pinned MySQL comparison confirmed matching execution error codes for
those three statements in memory and with WAL. The checkpoint retained the
original rows.

### Target errors before execution

Execution, EXPLAIN, and PREPARE now reject the same audited targets. An
UPDATE or DELETE through a materialized layer reports 1288 with the
statement's target alias. INSERT and REPLACE both report the 1471
insertability diagnostic. Joined UPDATE binds its join and WHERE before
the assignment target. A single-table UPDATE of a materialized view
rejects the target before checking its outer expressions.
[MySQL UPDATE preparation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_update.cc#L1415-L1486).

A mixed view can expose both materialized and writable columns.
Assignments to a direct materialized column return 1288, and assignments
to a computed projection return 1348. The physical component can still
be updated using that same view.

The replay against pinned MySQL improved from 29 to 120 matches across
execution, EXPLAIN, and text preparation in both Fsdb storage modes.
It covered nested aliases, both join directions, bare and qualified
assignments, CTEs, INSERT SELECT, and binding errors. Another 15 checks
matched after a view dependency was dropped. Six binary PREPARE
diagnostics and the contended UPDATE matched, including after reopening
the WAL-backed store. The rejected UPDATE returned before the held
base-row lock timed out, and both base tables retained their rows.

## DELETE through computed projections

DELETE through a merged single-table view resolves the physical row
independently of the output columns. The view can expose only computed
or constant expressions. Each layer still has to merge, and TEMPTABLE,
aggregate, and window views refuse the delete.

The pinned MySQL 8.4.11 comparison matched row counts and final rows for
standalone, ordered and limited, joined, and prepared DELETE in memory
and with WAL. The base rows persisted after reopening a checkpoint.
A joined DELETE through equal computed values in a keyless table
deleted both distinct source rows and fired the trigger once for each
row. A self-read of the physical target still returned 1443. A two-session
WAL test also matched MySQL's row-lock timeout and deleted the row after the
holding transaction rolled back.

For fully computed projections, UPDATE and INSERT refuse the write with
different diagnostics from MySQL.

## EXPLAIN DELETE through merged views

Standalone DELETE and its EXPLAIN share the view rewrite and predicate.
The plan names the permanent base table or its saved alias and uses the
mutation's index candidate selection. Caller expressions still bind to the
view's exposed columns. A materialized view reports 1288 before checking
the caller's predicate. Direct single-table DELETE likewise reports
primary-key equality as a range, including a folded arithmetic bound.

The pinned MySQL 8.4.11 comparison matched five traditional primary-key
plans and eleven audited privilege decisions in memory and with WAL.
The plan needed SHOW VIEW on both saved-view layers, SELECT on an
underlying column, and SELECT access to the extra table read by a
subquery. Active role grants satisfy these checks, and inactive grants
do not. A temporary table with the same name did not shadow the base
table in the view plan. A checkpoint restart preserved the plans and
base rows.

JSON omits MySQL's DELETE marker and some index and condition fields.
The saved-subquery plan uses different block numbering and access labels,
and unknown-column diagnostics still name a different clause. View
expansion stays on the original path when rewriting a subquery would
change the statement's temporary, CTE, or cross-schema namespace.

## Direct joined updates

MySQL checks subqueries owned by a directly merged read-side view
against the physical assignment targets of an UPDATE. The check
includes predicates, ordering, and scalar projections, even when the
outer statement ignores the projection or its WHERE is false.
[MySQL duplicate-target check](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_update.cc#L1723-L1743)

```sql
CREATE TABLE items (id INT PRIMARY KEY, n INT);
CREATE TABLE labels (id INT PRIMARY KEY, tag INT);
CREATE VIEW matching_labels AS
  SELECT l.id, l.tag FROM labels l
  WHERE EXISTS (SELECT 1 FROM items p WHERE p.id = l.id);

UPDATE items i JOIN matching_labels r ON i.id = r.id
SET i.n = 11 WHERE 0;
```

Both engines return 1443 with `r` as the read-side view and `i` as
the leading leaf. When the leading leaf owns the conflicting read,
MySQL returns 1288 instead. A RIGHT JOIN can make its right operand
the leading leaf. A materialized derived operand uses its exposed
alias, while a merged derived operand can report its first base
table. [MySQL duplicate-target diagnostics](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_base.cc#L2305-L2338)

A materialized read-side view, a nested view with no subquery of its
own, and assignments to an unrelated physical table remain allowed.
The audited dependent and volatile projections materialize by
default, and explicit merge directives can change whether their
reads enter the check. [MySQL merge rules](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_resolver.cc#L3367-L3386)

The 30-case UPDATE matrix improved from 9 to 30 exact matches on
acceptance or error code and message in both Fsdb storage modes.
Additional right-join, optimizer-hint, and projection probes agreed
with MySQL. Binary PREPARE and prepared EXPLAIN returned the same
diagnostics. A conflicting UPDATE was rejected while another
session held the target row locked, and the stored value stayed
unchanged. The prepared and locked-row probes also agreed after
loading a checkpoint containing the view definitions.

## Joined deletes through merged read-side views

A view can read the DELETE target in a subquery that survives semijoin
resolution. Fsdb now rejects the audited retained reads, including
scalar projections and ordering, nullable NOT IN, EXISTS with HAVING,
and EXISTS under a disjunction. The diagnostic names the view alias
and the written alias, including when the view leads the join. A target
view that owns the conflicting read returns 1288.
[MySQL duplicate-target check](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_delete.cc#L784-L802)

```sql
CREATE TABLE items (id INT PRIMARY KEY, n INT);
CREATE TABLE labels (id INT PRIMARY KEY);
CREATE VIEW scored AS
  SELECT l.id, (SELECT MAX(n) FROM items) AS score FROM labels l;

DELETE i FROM scored r JOIN items i ON i.id = r.id;
-- 1443: The definition of table 'r' prevents operation DELETE on table 'i'.
```

When a saved predicate becomes a semijoin in the mutation's join block,
its ordinary row sources no longer count as subquery reads. A scalar
subquery inside one of those sources can still conflict. A semijoin
inside a mutation subquery stays below the mutation's join and still
counts as a read. Materialization provides a separate read in the
audited cases.
[MySQL semijoin requirements](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/item_subselect.cc#L1397-L1491)

Both engines allow the direct correlated EXISTS control with default
settings. MySQL rejects the same control with 1443 when
`optimizer_switch='semijoin=off'`, which Fsdb does not support.

The main DELETE matrix improved from 16 to 39 matches across affected
rows and exact error diagnostics in both storage modes. Additional
alias and semijoin probes also matched. Binary PREPARE and prepared
EXPLAIN returned the same diagnostics after a durable restart.
A conflicting delete was rejected while a second session held the
target row locked, and the target rows stayed intact.

## Query bounds on saved EXISTS predicates

MySQL omits the outer `LIMIT` and `OFFSET` of an `EXISTS` query when
[serializing a view definition](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_lex.cc#L2768-L2788).
Fsdb applies the same rule when reopening a saved view. The definition
shown by `SHOW CREATE VIEW` and `information_schema.VIEWS` uses the
reopened query. Ad hoc `EXISTS` keeps its bounds. A CTE, derived table,
or individual set-operation branch inside the `EXISTS` query keeps its
own bounds.

For a saved filter containing `EXISTS(SELECT 1 FROM items i WHERE i.id =
l.id LIMIT 1 OFFSET 2)`, the two matching rows remain visible through
the view. The audited joined DELETE now affects both rows, as it does in
MySQL 8.4.11. Before the fix, the offset hid both rows from Fsdb.
The prepared DELETE and the metadata checks also matched after a
checkpoint restart. The differential matrix compared direct and saved
results, creation status, and the reported bounds in both storage modes.
The regression also recreates the view using `SHOW CREATE VIEW` and checks
that the branch-local limits still affect its results.

## Aliases in saved joined views

When a saved joined view inlines another view, a correlated reference
in the inner predicate keeps its original relation. Fsdb now rewrites
the physical qualifier inside the subquery and assigns a fresh physical
alias if the requested name is already used locally. Within known
physical column scopes, it also preserves an unqualified correlation
before exposing it to the extra columns of the enclosing join.
[MySQL correlated name resolution](https://dev.mysql.com/doc/refman/8.4/en/correlated-subqueries.html)

```sql
CREATE TABLE items (id INT PRIMARY KEY, n INT);
CREATE TABLE labels (id INT PRIMARY KEY, tag INT);
CREATE TABLE allowed (id INT PRIMARY KEY);
INSERT INTO items VALUES (1, 10), (2, 20);
INSERT INTO labels VALUES (1, 100), (2, 200);
INSERT INTO allowed VALUES (1);
CREATE VIEW filtered AS SELECT l.id, l.tag FROM labels l
  WHERE EXISTS (SELECT 1 FROM allowed r WHERE r.id = l.id);
CREATE VIEW joined AS
  SELECT i.id, i.n, r.tag FROM items i JOIN filtered r ON r.id = i.id;

UPDATE joined SET n = n + 1;
SELECT id, n FROM items ORDER BY id;
```

Both engines update only row 1. Binding `l.id` to the inner `r` would
make the predicate true for every outer row.

The differential matrix improved from 22 to 46 matching outcomes
and final row sets in both storage modes. It includes a second level
of correlation, a qualified CTE, generated-name collisions,
`NATURAL JOIN`, `USING`, and cascading check options. Additional
probes confirmed that an inner column retains its owner and that a
check option still rejects an update that would remove the row from
the view. Binary prepared writes to the qualified and bare variants
matched after checkpoint restart.

## Bare correlations through CTEs

The view rewriter now carries the visible CTE output columns through
each query block. A declaration sees earlier definitions, and a local
CTE shadows an enclosing definition in the blocks where it is
visible. A schema-qualified reference still resolves to its physical
table. [MySQL CTE name resolution](https://dev.mysql.com/doc/refman/8.4/en/with.html)

For example, in a filter over an outer labels table with a `tag`
column, the `tag` in this predicate belongs to the outer row. Inlining
the filter into a join with another `tag` column used to make the
reference ambiguous.

```sql
EXISTS (
  WITH ids AS (SELECT id FROM allowed), copied AS (SELECT * FROM ids)
  SELECT 1 FROM copied WHERE id = tag - 99
)
```

The 32 UPDATE probes for definitions accepted by both engines improved
from 10 to 32 matches on affected rows and row contents. They include
declared names, wildcard projections, inherited and shadowed CTEs,
recursive CTEs, derived sources, UNION branches, and correlations in
CTE definitions. Both Fsdb storage modes agreed. The earlier 46 alias
probes still match. One nested shadowing definition returned 1064 at
creation in MySQL and was accepted in Fsdb. Bare names whose local
column sets cannot be inferred still need broader name binding.
Binary prepared updates through a nested CTE matched after checkpoint
restart.
