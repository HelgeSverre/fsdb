# Two writable joined views under outer UPDATE joins

The [native MySQL 8.4.11 differential oracle](../scripts/two-joined-view-outer-oracle.py)
compares fourteen UPDATE statements against fsdb. Two independently
writable inner-join views share their internal physical/read aliases. The
matrix uses both LEFT and RIGHT outer joins, with a fully matched row and
rows missing from either view. A second pair of views joins grouped read
sources and dependent `LATERAL` projections before the outer UPDATE join.

All fourteen statements match MySQL's affected-row count and the resulting rows
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

The oracle uses disposable native MySQL and fsdb servers. Run it after a
Release CLI build:

```sh
dotnet build src/Fsdb.Cli -c Release
python3 torture/scripts/two-joined-view-outer-oracle.py
```

Wider outer-join trees, nested security layers, and contention involving two
simultaneously held targets remain untested here.
