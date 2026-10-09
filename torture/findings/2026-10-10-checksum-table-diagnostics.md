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

The focused `CHECKSUM TABLE reports each missing object` regression covers
both modes, ordered 1146/1049 conditions, and preserved result rows. The
`missing-table-diagnostics` differential contract also covers both modes and
their `SHOW WARNINGS` results. Its four new steps passed against pinned MySQL
8.4.11 at `torture/artifacts/runs/20261009T235520898-27509/contracts`;
the full 112-case run retained the same nine unrelated identifier-case
differences. `just check` passed all 3,252 tests with no build warnings.
