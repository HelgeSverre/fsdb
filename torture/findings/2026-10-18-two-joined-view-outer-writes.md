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

The oracle uses disposable native MySQL and fsdb servers. Run it after a
Release CLI build:

```sh
dotnet build src/Fsdb.Cli -c Release
python3 torture/scripts/two-joined-view-outer-oracle.py
```

Wider outer-join trees, nested security layers, and concurrent writes through
both outer-joined views remain untested here.
