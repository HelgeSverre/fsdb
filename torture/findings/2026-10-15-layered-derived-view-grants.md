# Derived-source grants in layered writable views

Native MySQL 8.4.11 reports error 1356 when the definer of an inner join view
loses `SELECT` on a table inside an aggregate-derived source, even though the
outer view can still identify its writable physical target. FSDB previously
reported the underlying column denial (1143). The audited inner view joins two
aggregate-derived sources before a physical target, and a second definer view
renames the target's writable column.

Revoking `SELECT` on either derived source returns 1356 in both engines after
the fix. Revoking only the physical target's `SELECT` returns 1143 for its join
key. When both a derived source and the target lack `SELECT`, MySQL reports
1356 first; FSDB now follows that order. The view remains marked updatable,
the initial update affects one row, and denied updates leave target rows
unchanged.

The [native oracle](../scripts/layered-derived-view-oracle.py) starts disposable
MySQL and FSDB servers, checks the MySQL version, and compares metadata, error
codes, affected rows, and final data. A focused `ViewTests` regression pins the
grant sequence. This covers two aggregate-derived sources and one physical
target under two definers; other derived, lateral, and grouped grant paths
remain to be audited.
