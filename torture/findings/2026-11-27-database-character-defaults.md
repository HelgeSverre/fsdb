# Database character defaults

Creating or altering a database now records its character set and
collation. A new table inherits the database default when the table
has no character attributes. A table or column declaration takes
precedence at its own level. Changing the database default affects
new tables, and existing columns continue to use their recorded
collations. [MySQL database character defaults](https://dev.mysql.com/doc/refman/8.4/en/charset-database.html)

The original failure changed query results. Two inherited
`utf8mb4_unicode_ci` columns containing `'CAFÉ'` and `'cafe '`
now compare equal, including in a correlated query. The same
values in a column explicitly declared `utf8mb4_0900_bin` remain
distinct. A subsequent switch to `latin1` affects newly created
columns and their stored bytes. The fixture matched 21 output rows
and seven errors against MySQL 8.4.11 in both Fsdb storage modes.
[SQL fixture](2026-11-27-database-character-defaults.sql)

The [correlated top-N sweep](2026-11-27-correlated-null-safe-top-n.md)
now matches all 384 variants when the text columns inherit one of
the three tested database collations. Earlier runs needed explicit
column collations.

The catalog records nondefault collations in the existing snapshot
and WAL formats. Tests recover the database default and its existing
columns from WAL and from a checkpoint, then create a table using the
recovered setting. Dropping and recreating a database clears its
previous default.

Each engine passed 12 races between six clients creating the same
database with different collations. Exactly one create succeeded,
and its collation was visible in the catalog. Another 12 races
between ALTER and DROP per engine preserved the expected outcome
and cleared the old default on recreation.

`SHOW CREATE DATABASE` and `INFORMATION_SCHEMA.SCHEMATA` report the
stored default. `USE` loads the selected database's settings into the
session variables. An `ALTER DATABASE` in another session leaves that
selection's variables as they were, while subsequent table DDL reads
the stored catalog default. [MySQL ALTER DATABASE](https://dev.mysql.com/doc/refman/8.4/en/alter-database.html)

Runtime changes to server-level character defaults and conflicting
repeated charset clauses remain gaps.
