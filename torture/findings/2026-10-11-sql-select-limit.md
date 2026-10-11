# Session and global `sql_select_limit`

A disposable native MySQL 8.4.11 server reports `sql_select_limit` as an
unsigned session/global variable defaulting to 18446744073709551615.
`SET SESSION sql_select_limit=2` caps a top-level three-row SELECT or UNION
at two rows. An explicit `LIMIT 3` overrides it. A scalar subquery still sees all
three source rows, as do `INSERT … SELECT` and `CREATE TABLE … AS SELECT`;
the implicit cap belongs to the outer result-producing query. A prepared
statement observes the value at execution, not preparation: the same
prepared UNION returned two rows at setting 2 and three at setting 3.
`SQL_CALC_FOUND_ROWS` returned two rows while `FOUND_ROWS()` reported three.

At zero, MySQL returns no rows for ordinary SELECT and the audited
SELECT-shaped metadata commands (`SHOW VARIABLES`, `SHOW STATUS`,
`SHOW DATABASES`, `SHOW TABLES`, `SHOW TABLE STATUS`, `DESCRIBE`,
`SHOW COLUMNS`, `SHOW ENGINES`, `SHOW PLUGINS`, `SHOW CHARACTER SET`,
`SHOW COLLATION`, `SHOW OPEN TABLES`, `SHOW TRIGGERS`, `SHOW EVENTS`,
`SHOW PROCEDURE STATUS`, and `SHOW INDEX`). `SHOW WARNINGS`, `SHOW GRANTS`,
`SHOW PRIVILEGES`, `SHOW PROCESSLIST`, `SHOW BINARY LOGS`, and
`SHOW CREATE TABLE` retain their rows. This distinction drives the probe whitelist in fsdb.

MySQL rejects string, NULL, fractional, and out-of-range assignments with
1232. It clips `-1` to zero with warning 1292. `SET SESSION ...=DEFAULT`
reads the current global value, while a GLOBAL assignment seeds later
sessions without changing the current one. Fsdb uses the existing bounded
unsigned-variable normalization and applies the implicit cap only where the
native oracle observed it. The focused QueryHandler regression covers the
query forms, metadata commands, assignment diagnostics, and scope reset.
MySQL also shows an implicit zero limit in `EXPLAIN` and `EXPLAIN FORMAT=TREE`;
fsdb's EXPLAIN planner does not yet include session limits.
