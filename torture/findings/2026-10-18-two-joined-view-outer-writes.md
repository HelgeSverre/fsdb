# Two writable joined views under outer UPDATE joins

The [native MySQL 8.4.11 differential oracle](../scripts/two-joined-view-outer-oracle.py)
compares thirty-three UPDATE statements against fsdb. Two independently
writable inner-join views share their internal physical/read aliases. The
matrix uses both LEFT and RIGHT outer joins, with a fully matched row and
rows missing from either view. A second pair of views joins grouped read
sources and dependent `LATERAL` projections before the outer UPDATE join.

All thirty-three statements match MySQL's affected-row count and the resulting rows
in both physical target tables. A `CASCADED CHECK OPTION` on the grouped
lateral view also rejects an invalid two-target update with error 1369 and
leaves both tables unchanged. This extends the existing inner two-view and
single-view outer-join audits to the combination of two writable views under
an outer mutation join.

The first contention extension found a real gap. With `va RIGHT JOIN vb` and
only the `vb` row present, another transaction held that physical row while
the two-view UPDATE ran. MySQL waited and committed after the holder; fsdb
returned one affected row from UPDATE but failed its later COMMIT with error
1205. The pre-snapshot write-target planner claimed only the first writable
view in the joined statement. It now claims each assigned joined view's
physical target as well, using a literal equality bound when one is
available. The same oracle now matches MySQL in three contended cases:
both views matched, only the left view matched, and only the right view
matched. Each waits for the held row, rebases, commits, and produces the same
physical target values. A focused F# transaction regression pins the
right-only commit path.

The ninth case exposed a separate source-order gap. When the first source is
an unassigned joined view and the second view owns a uniquely named bare
assignment (`SET bonus=bonus+1`), MySQL updates the second view but fsdb
previously returned error 1146 for that view name as though it were a base
table. The writable-view source reorder now also moves a later assigned view
ahead of an unassigned first view. The oracle matches both the ordinary
update and a fourth contended rebase on that second view; an F# regression
pins the original 1146 case.

A read-only aggregate view can also precede that writable second view. MySQL
updates `bonus` through the second view, whereas fsdb previously returned
1146 for the view name. The source reorder now recognizes a stored first
view even when it is non-updatable.
Both the ordinary and contended aggregate-first cases match MySQL.

A three-source form exposed another 1146. An aggregate read-only view can
precede two writable joined views, with the second joined by `LEFT JOIN`.
MySQL updates both targets, but fsdb expanded the first writable view and
stopped its second source-order pass at the intervening aggregate view. The
rewrite now crosses an independent read-only inner-joined view to bring the
second assigned view first. It likewise crosses an unassigned writable
inner-joined view when only the later view is assigned. The ordinary cases
and three additional contended rebases now match MySQL. A focused F#
regression checks both assignment patterns and restores the physical rows.

A CTE-first UPDATE exposed the same 1146 error on the opposite side of the
rewrite: `WITH c AS (SELECT id FROM link) UPDATE c JOIN va ... SET va.n=...`
updates the later joined view in MySQL, but fsdb tried to open `c` as a base
table. The mutation wrapper had removed the CTE declarations before choosing
the writable first source. It now moves the assigned view while the CTE names
are still available, then retains the CTE because the moved source joins it.
The ordinary statement and a ninth contended rebase match MySQL. The focused
F# regression checks the UPDATE and restores the target row.

The adjacent CTE-first, two-view UPDATE exposed a second 1146 for `vb` after
the first view was expanded. At that stage the declarations had been cleared,
but the CTE binding was still active. Later source-order passes now recognize
that binding and cross it to reach `vb`. The ordinary two-target UPDATE and
two additional contended runs, with either physical target held, match MySQL.
The F# regression checks both affected rows and restores their values.

The same CTE-first source order also matches MySQL under LEFT and RIGHT joins:
the matrix includes matched rows, a missing optional view row, a right-only
view row, and an assignment to only the second view. Two extra contention
checks hold the first target with no optional row and hold the second target
when it is the sole assignment. Both wait, rebase, and commit as MySQL does.

A dependent source between the two writable views exposed a separate 1054:
`WITH c ... UPDATE c JOIN LATERAL (...) q JOIN va JOIN LATERAL
(SELECT va.link_id ...) r JOIN vb ...` lost `va.link_id` when the first view
was expanded. The same issue affected `JSON_TABLE(JSON_ARRAY(va.link_id),...)`.
The view rewrite now updates qualified outer references inside dependent
source bodies as well as join conditions. It respects a local `va` alias
declared inside a lateral body, which must shadow the outer writable view.
The three ordinary cases and two contended rebases holding `vb` match MySQL;
the focused F# regression covers both dependent source forms and shadowing.

Two further statements start the UPDATE with an independent grouped derived
table, then inner-join the first writable view. A dependent grouped or scalar
`LATERAL` source precedes the second writable view under INNER or LEFT JOIN.
MySQL updates both views; fsdb previously rejected the leading `(` as syntax.
The parser now swaps the first two inner-join operands, preserving their ON
predicate and the order of all later dependent joins. Both statements match
MySQL's affected-row count and final physical rows; an F# regression pins
both forms.

Five more statements put the grouped derived source on the preserved side of
LEFT JOIN or the optional side of RIGHT JOIN. They cover matched and missing
first-view rows, plus a second writable view under a later LEFT JOIN. The
source swap reverses the first outer join; the view rewrite retains the
independent derived source when moving the later assigned view. MySQL and
fsdb agree on row counts and both base tables after each statement. Two
additional cases hold either physical target while a two-view update waits;
both rebase and commit with MySQL's result, bringing the contention matrix
to seventeen cases.

Three further UPDATEs use a six-source LEFT JOIN chain with two writable
joined views and four physical read aliases. The chain preserves a matched
row, a row with only the first view present, and a row with only the second
view present. MySQL and fsdb agree on affected rows and both physical target
tables after each statement. These extend the audited outer-join width from
five to six sources for this shape. Four contention checks hold either
physical target on the matched row, the first target on its one-sided row,
or the second target on its one-sided row. Both engines wait, rebase, and
commit the same row values. A further case holds both physical targets in
one transaction before the six-source UPDATE starts. It also waits for the
holder and rebases both assignments to MySQL's result. A separate-owner
case then holds the first and second physical targets in distinct
transactions. Both engines keep the joined UPDATE blocked after the first
holder commits, resume after the second commits, and produce the same
affected count and physical rows. The contention matrix now has twenty-three
cases. Other join trees and lock-order combinations remain open.

## Parenthesized outer-join source

MySQL 8.4.11 accepts a parenthesized first operand in a multi-table UPDATE:
`(link LEFT JOIN va) RIGHT JOIN vb` updates both writable joined views on a
matched row, only `vb` when `va` is absent, and `(va RIGHT JOIN link) LEFT
JOIN vb` updates only `va` when `vb` is absent. The affected counts are 2,
1, and 1, respectively. Fsdb previously returned syntax error 1064 for all
three. The UPDATE parser now flattens a grouped first operand into the same
left-deep join sequence before selecting its physical write anchor. The
focused regression pins all three affected counts and the final base rows.
Other grouped join trees still need their own association and write-scope
audits.

## Grouped right operand with a writable view

A follow-up added three passing grouped-first-source forms to the maintained
oracle, including an explicitly parenthesized whole join tree. MySQL 8.4.11
and fsdb match affected counts and both base tables after each statement.

A grouped **right** operand exposed a separate parser-to-write gap. On the
same fixture, MySQL updates both base rows (affected count 2) for either form:

```sql
UPDATE va LEFT JOIN (link l JOIN vb ON vb.link_id=l.id)
ON va.link_id=l.id
SET va.n=va.n+1,vb.n=vb.n+1 WHERE va.id=1;

UPDATE va RIGHT JOIN (link l LEFT JOIN vb ON vb.link_id=l.id)
ON va.link_id=l.id
SET va.n=va.n+1,vb.n=vb.n+1 WHERE l.id=1;
```

Fsdb previously returned 1146, `Table 'probe.vb' doesn't exist`, before
changing either base table. The first joined view expanded, but the second
writable view stayed inside a right-side `FromJoinGroup`, beyond the existing
joined-view rewrite. The UPDATE parser now reverses the first LEFT/RIGHT join
and moves its grouped right operand to the first position. For example,
`va LEFT JOIN (link JOIN vb)` becomes `(link JOIN vb) RIGHT JOIN va`; the
inner group is then a left-deep source chain and both writable views reach
the existing rewrite. This preserves the outer-join association. The rewrite
requires ordinary table operands inside the group: `LATERAL` and `JSON_TABLE`
sources can depend on the original left table, so moving those groups first
would lose their scope. Later grouped operands and broader nested trees
remain to audit.

The maintained native oracle now matches MySQL 8.4.11 in 40 two-view UPDATE
cases, including matched and one-sided rows for both grouped-right forms.
Two additional cases hold the `a` and `b` physical rows in separate
transactions; fsdb and MySQL both keep the grouped-right UPDATE waiting
until both owners commit, then rebase and change both rows. The oracle's 26
contended rebases match. A focused F# regression checks matched and one-sided
affected counts and final physical rows.

An independent `(SELECT id FROM link)` source inserted between `link` and
`vb` in the right-hand group exposed the same former 1146 error. Allowing
non-lateral derived operands through the reorientation closes that case too:
the oracle now matches 41 UPDATE cases and still matches all 26 contention
rebases. A focused regression checks both physical target rows. The guard
continues to exclude dependent `LATERAL` and `JSON_TABLE` operands.

The group-local `JSON_TABLE(JSON_ARRAY(l.id))` form then exposed the same
1146 error: its input depends on `link l` inside the group, not on the
original left-side `va`. The parser now checks each group's sources in
order and permits a `JSON_TABLE` operand only when its column references
are qualified by already available group sources. Bare columns, nested
subqueries, and out-of-group qualifiers keep the original association.
The native oracle matches 42 UPDATE cases and 26 contended rebases; the
focused F# regression checks both target rows. Lateral groups still require
their own dependency check.

The analogous `LATERAL (SELECT l.id AS next_id)` group also returned 1146
before the rewrite. A source-free lateral SELECT can now move with the group
when every expression references only preceding group aliases. This includes
its projection, filter, grouping, ordering, and limit expressions; local
FROM/JOIN, CTE, window, locking, and output destinations remain on the
original path. The maintained MySQL oracle matches 43 UPDATE cases and 26
contended rebases. The focused F# regression pins both physical target rows,
and the existing grouped mutation tests retain left-dependent lateral scope.

A lateral SELECT with its own `FROM link r WHERE r.id=l.id` then exposed
the same former 1146 error. The dependency check now adds that one local
read-table alias to the known group aliases before validating the lateral
expressions. Unknown or bare references, local joins, and nested query
blocks remain excluded. The native oracle matches 44 UPDATE cases and 26
contended rebases, and the focused F# regression checks both physical rows.

## Grouped `USING` joins

`UPDATE va LEFT JOIN (link l JOIN vb USING(id)) USING(id)` initially failed
with 1052 because the right group exposed two raw `id` columns to the outer
key resolver. Reorienting the independent group revealed a second failure:
the joined-view source rewrite stopped at the inner `USING` edge and treated
`vb` as a missing physical table (1146). The parser now permits this group
reorientation, and the joined-view rewrite resolves each `USING` key against
the views' exposed columns before expanding their physical joins. The
resolved keys become explicit join predicates, retaining LEFT/RIGHT
association and bare merged-key binding. An invalid `USING` key remains an
error instead of becoming an empty predicate.

The native MySQL 8.4.11 oracle now matches 56 two-view UPDATE cases and 32
contended rebases. The added cases cover matched and one-sided rows, qualified
and bare merged `id` predicates, an unknown-key error, and a grouped `USING` update waiting on two
physical targets held by separate transactions. A focused F# regression
checks affected counts, final rows, and an unknown grouped key. Other grouped
join trees and dependency combinations remain open.

The oracle uses disposable native MySQL and fsdb servers. Run it after a
Release CLI build:

```sh
dotnet build src/Fsdb.Cli -c Release
uv run --with pymysql python torture/scripts/two-joined-view-outer-oracle.py
```

## Third writable view after a grouped source

A CTE-first UPDATE with a read-side derived join and three assigned joined
views reached the physical mutation path with `vc_unique` still unresolved.
MySQL changed three base rows; fsdb returned 1146 for the third view. After
the first two view expansions, the rewrite had grouped the preceding sources
under a RIGHT join. The source-order walk stopped at that group before
reaching the third view. It now carries an already grouped ordinary join
operand into the prefix, preserving its association while moving the next
writable view to the front.

The oracle covers matched and one-sided rows under `NATURAL LEFT` and
`NATURAL RIGHT` joins. All 76 UPDATE cases match MySQL 8.4.11 in affected
counts and physical rows. Five added contention cases hold each of the
three physical targets separately, all three in one transaction, and all
three under independent owners. In each case the joined UPDATE waits for
the owners, then commits the same three rebased rows as MySQL. The maintained
contention matrix now has 45 cases. More three-view join associations and
lock-order combinations remain open.

Wider outer-join trees and nested security layers remain untested here.
