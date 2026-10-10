# CHECKSUM TABLE missing-object diagnostics

A disposable MySQL 8.4.11 server returned one result row per named table and
recorded a condition for each missing object. `CHECKSUM TABLE absent` returned
a NULL checksum and Error 1146 (`Table 'probe.absent' doesn't exist`);
`CHECKSUM TABLE no_db.any` returned a NULL checksum and Error 1049
(`Unknown database 'no_db'`). The same behavior held for `QUICK` and for a
mixed list of existing and missing tables. MySQL describes the `Checksum`
result column as BIGINT even when all values are NULL.

fsdb previously returned the NULL rows without conditions. `QUICK` also
skipped the lookup entirely. Both modes now check each table, record the
ordered MySQL-shaped condition, and retain the result rows. The wire result
reports a numeric checksum column. The engine-specific non-QUICK checksum
value remains an intentional documented difference.

## Views and temporary tables

The same MySQL 8.4.11 oracle returned a NULL checksum and Error 1347
(`'probe.vv' is not BASE TABLE`) for a view in both ordinary and QUICK mode.
It checks subsequent tables in the same list and records their conditions in
order. A session temporary table is eligible: ordinary mode returns a
checksum and QUICK mode returns NULL without a condition.

fsdb's temporary-table overlay already resolved the latter correctly. Its
view lookup used to report 1146 as though the view were missing. The checksum
path now recognizes a saved view and reports 1347 while retaining its NULL
result row.

Focused regressions cover missing objects, views, and temporary tables. The
`missing-table-diagnostics` wire contract covers both checksum modes,
`SHOW WARNINGS`, a mixed view/missing-table list, and temporary QUICK results.
All 11 checksum steps passed at
`torture/artifacts/runs/20261010T000038959-31990/contracts`; the full run
retained the same nine identifier-case differences. `just check` passed all
3,253 tests with no build warnings.
