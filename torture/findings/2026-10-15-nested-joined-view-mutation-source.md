# Joined UPDATE through a joined updatable view

MySQL 8.4.11 accepts an UPDATE whose first source is an updatable view of an
inner join and whose statement adds another join:

```sql
CREATE TABLE t(id INT PRIMARY KEY, n INT);
CREATE TABLE l(id INT PRIMARY KEY);
CREATE TABLE x(id INT PRIMARY KEY);
CREATE VIEW v AS SELECT t.id, t.n FROM t JOIN l ON l.id=t.id;
UPDATE v JOIN x ON x.id=v.id SET v.n=v.n+1 WHERE v.id=1;
```

The [disposable-server oracle](../scripts/joined-view-nested-mutation-oracle.py)
runs this with the writable `t` before and after `l` inside the view. In both
orders, an owner transaction first increments `t.n`; the view-addressed joined
UPDATE waits for its row, then increments the committed value. The final
value is 2 and the joined UPDATE affects one row. Both a qualified `v.n` and
a uniquely owned bare `n` assignment work; adding `n` to the outer source
produces MySQL's 1052 ambiguity error. A `CASCADED CHECK OPTION`
view over the same join rejects a joined UPDATE that would remove the row
from its view with error 1369, leaving the base row unchanged.

Before this change, Fsdb returned 1288 because the external joined-source
rewrite rejected views containing their own joins. It now appends the view's
join to the physical source, rewrites exposed columns in the external join,
and claims the physical assignment target before execution. Tests cover both
internal source orders, rebasing, the CHECK OPTION, and caller permission on
the external read source. A literal equality on an exposed column mapped to
the writable physical table narrows the pre-execution row claim; other shapes
conservatively claim all its rows. The generic joined writer now passes its
matched physical rows to storage, avoiding a later full-table lock. With an
owner holding `t.id=2`, a joined view update restricted to `v.id=1` completes
before the owner commits, matching the oracle's disjoint-row result.

The implementation handles view assignments in the first, second, or third
inner-join position, qualified or uniquely owned bare.

## Two writable views with shared internal aliases

MySQL 8.4.11 updates both targets in `UPDATE va JOIN vb ON va.id=vb.id SET
va.n=va.n+1,vb.n=vb.n+1` when `va` and `vb` are distinct updatable join views.
Their definitions may each call an internal link-table alias `l`; those names
belong to separate view scopes. The maintained native oracle pins both view
orders, the two-row update, and both resulting physical values. Fsdb previously
returned 1066 (`Not unique table/alias: 'l'`) because view expansion placed
both internal joins in one source scope. It now gives a colliding read-side
table alias a fresh name when flattening the first view. The mapping follows
exposed expressions, join conditions, visibility predicates, and CHECK OPTION
checks; authorization still uses the stored view's original names. The focused
regression covers both view orders and an invalid CHECK OPTION update that
leaves both physical targets unchanged. The first view also projects `l.id`
and filters on `l.id=1`, exercising the renamed alias outside the join
condition. Collisions involving writable target
aliases are now handled too: MySQL and fsdb both update two views whose
physical target tables are separately named `a` inside their definitions,
in either view order. Runtime target and CHECK OPTION references use the fresh
alias, while authorization checks use the stored target alias. The oracle and
focused regression also verify a CHECK OPTION rejection leaves both targets
unchanged after this rewrite. Two views with independent derived read sources
may also reuse the same alias `r`: the rewrite now renames that outer derived
alias and can promote the second writable view past the first view's derived
source. The oracle and regression cover both view orders and an exposed
`r.id` join key. The same promotion now passes a dependent `LATERAL` or
`JSON_TABLE` read source while preserving its preceding writable table. Its
view-local alias is freshened when both views use `r`; the dependent
`JSON_TABLE` expression follows any renamed outer alias. MySQL 8.4.11 and
the focused regression update both physical targets in both view orders for
each source type. A further `LATERAL` case gives both writable tables the
same internal alias `a` and still updates both targets correctly. A checked
dependent view rejects an invalid two-target assignment with error 1369 and
leaves both physical rows unchanged. A lateral body that declares its own
local `a` alias also stays scoped in both view orders. A stronger native
control gives that local alias the value 2 while the writable row's
`a.id` is 1. It exposed a false zero-row UPDATE when the locally declared
alias was rewritten as the view's outer `a`; the SELECT rewriter now carries
the shadowed-alias set into expression and FROM subqueries. The joined UPDATE
matches MySQL's two affected rows with `WHERE vb.link_id=2`. Grouped non-table
sources and broader dependency compositions remain to audit.

Two writable views can also each join a grouped derived read source under
the same view-local alias `r`. MySQL 8.4.11 and fsdb update both physical
targets in either view order. The same result holds when each grouped source
feeds a dependent `LATERAL` source under a second shared alias `q`. A
`CASCADED CHECK OPTION` on one such view rejects an invalid two-target
assignment with error 1369 and leaves both rows unchanged. When another
transaction holds one physical target, the two-view UPDATE waits, then
rebases and changes both rows after the owner commits. The maintained native
oracle and focused transaction regression pin all of these outcomes;
other grouped dependency trees and layered grants remain unaudited.

## A physical target beside the writable view

MySQL also accepts `UPDATE x JOIN v ON x.id=v.id SET x.n=x.n+1,v.n=v.n+1`
and the reversed join order, reporting two affected rows. A three-source
inner join with the view last has the same behavior. Fsdb previously returned
1288 because the view rewrite assumed every assignment targeted that view.
It now retains the outer physical assignment while mapping only the view's
assignment to its physical component. A joined view can also supply read rows
for an UPDATE targeting only the other physical table.

The oracle and regression cover two-target row counts, a contended view row
that waits and rebases both targets, a CHECK OPTION rejection that leaves
both tables unchanged, and a caller missing `UPDATE` on the outer target
receiving MySQL error 1143. A definer with no rights on that outer table can
still expose the view target; the caller's outer-table grant is checked
separately. The same authorization split works through an additional view
layer with a different definer. `CURRENT_USER()` in the caller's outer-table
assignment stays the caller identity rather than changing to the view definer.
An unqualified assignment to a column exposed by exactly one outer physical
source resolves to that source, even when the view's hidden base table has a
column with the same name. A column exposed by both the view and an outer
source remains ambiguous with error 1052. Further multi-target source kinds,
other bare-assignment shapes, and wider outer joins remain to audit. MySQL
also updates a writable view on either side of a two-source LEFT or RIGHT
join. Fsdb matches the audited one- and two-target assignments; a missing
nullable view row updates only the preserving table when it is assigned. The
right-side LEFT join is normalized to its equivalent view-first RIGHT join
before the view's physical sources are expanded. A view predicate stays in
that outer join's ON condition rather than filtering preserving rows in the
global WHERE. A matching filtered view updates both targets; a filtered-out
view row leaves its physical target unchanged. CHECK OPTION still rejects an
invalid view assignment without changing the preserving table. The audited
three-source chains `x LEFT JOIN y LEFT JOIN v` and `x LEFT JOIN v LEFT JOIN y`
also update both matching targets. When `y` is absent in the first chain,
only `x` changes. The rewrite keeps the preceding `x LEFT JOIN y` operand
grouped when moving `v` to a view-first RIGHT join, so null-extension and ON
predicate scope survive. A three-source CHECK OPTION failure leaves both
targets unchanged. MySQL also accepts `x LEFT JOIN y RIGHT JOIN v`: with all
rows present, both `x` and the view's physical target update; without `y`,
the preserving `v` row still updates while `x` does not. Fsdb previously
reported 1146 for the later view. The symmetric rewrite now moves `v` to
the first source with a LEFT join to grouped `x LEFT JOIN y`, preserving the
right-hand row and the original ON scope.

The native oracle also extends that three-source chain with a final physical
table `z`. With `LEFT JOIN z`, the writable view remains the preserved target
across all four combinations of present or missing `y` and `z` rows. MySQL
updates one to three physical rows accordingly: the view's target always,
`x` only when `y` matches, and `z` only when it matches. With `RIGHT JOIN z`
and no matching view row, MySQL updates only `z`. The focused transaction
regression checks the same affected counts and stored rows. Fsdb already
matched these cases; no executor change was needed.

The chain can also end with a fifth, right-preserving physical table `w`:
`x LEFT JOIN y RIGHT JOIN v LEFT JOIN z RIGHT JOIN w`. MySQL 8.4.11 and fsdb
agree for all four presence combinations of `y` and `z`: `w` and the view's
physical target update, `x` updates only when `y` matches, and `z` updates
only when its optional row exists. With only an unmatched `w` row, both
engines update that row alone. The native oracle and focused transaction
regression check affected counts and all final rows. Wider join trees and
additional view-security layers remain unaudited.

The five-source chain also keeps `CASCADED CHECK OPTION` atomic across its
four writable physical targets. When the view assignment would move its row
outside the view, MySQL 8.4.11 and fsdb return 1369 and leave the view target
and all present outer targets unchanged. The native oracle and focused
transaction regression cover the fully matched case and all three cases where
either or both optional outer targets are absent.

## Contended multi-target outer writes

The four-source `x LEFT JOIN y RIGHT JOIN v LEFT JOIN z` UPDATE also writes
correctly after a concurrent owner holds `v`'s physical target `t` or either
outer physical target `x` or `z`. MySQL 8.4.11 waits in all three cases, then reports
three affected rows and increments the owner's committed value from 10 to 11
while changing the other two targets from 0 to 1.

Fsdb initially handled the view-target contention but returned 1205 when the
owner held `x`: pre-write target discovery claimed only the view's physical
table even though the statement assigned `x` and `z` too. It now combines the
view target with every assigned outer physical target in table order before
rebasing the transaction snapshot. The focused regression checks both owner
positions, wait behavior, affected counts, commits, and final rows. Other
multi-target outer-join shapes remain to audit.

A bare assignment to `m`, which is exposed by `x` but not by `v`, also
claims `x` despite the view's hidden base table having its own `m` column.
With an owner holding `x.m=10`, MySQL and Fsdb both wait, then set `x.m=11`,
`t.n=1`, and `z.n=1` while leaving `x.n=0`. The focused regression covers
this uniquely owned bare target as well as the qualified assignments above.

The same four-source right/left chain preserves statement atomicity when its
joined view has `CASCADED CHECK OPTION`: assigning an `id` that removes the
view row yields MySQL error 1369, and neither outer physical target changes.
Fsdb matches the error and all three unchanged rows in the focused regression.

In the five-source chain, an owner holding `w.n=10` makes the four-target
joined UPDATE wait. After the owner commits, MySQL and fsdb each report four
affected rows: `w.n` becomes 11, while `x.n`, the view target's `n`, and
`z.n` become 1. This confirms that the additional right-preserving target
participates in the same pre-write claim and rebase path.
