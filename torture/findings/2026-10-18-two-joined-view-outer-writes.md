# Two writable joined views under outer UPDATE joins

The [native MySQL 8.4.11 differential oracle](../scripts/two-joined-view-outer-oracle.py)
compares eight multi-target UPDATE statements against fsdb. Two independently
writable inner-join views share their internal physical/read aliases. The
matrix uses both LEFT and RIGHT outer joins, with a fully matched row and
rows missing from either view. A second pair of views joins grouped read
sources and dependent `LATERAL` projections before the outer UPDATE join.

All eight statements match MySQL's affected-row count and the resulting rows
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
right-only commit path; the full suite passed 3,721 tests.

The oracle uses disposable native MySQL and fsdb servers. Run it after a
Release CLI build:

```sh
dotnet build src/Fsdb.Cli -c Release
python3 torture/scripts/two-joined-view-outer-oracle.py
```

Wider outer-join trees, nested security layers, and contention involving two
simultaneously held targets remain untested here.
