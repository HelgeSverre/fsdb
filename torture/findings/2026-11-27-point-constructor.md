# Numeric POINT constructor

`POINT(x,y)` creates a Cartesian point with SRID 0. MySQL 8.4.11
reads both coordinates as doubles before checking for SQL `NULL`.
Geometry arguments are rejected even if a geometry column is NULL.
Text and JSON conversions retain their respective diagnostics.
[MySQL geometry constructors](https://dev.mysql.com/doc/refman/8.4/en/gis-mysql-specific-functions.html)
[MySQL POINT implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/item_geofunc.cc#L4224-L4258)

The fixture matched 22 output rows and six error codes and SQLSTATEs
in both in-memory and durable Fsdb. It covers strict and ignored writes
to a spatially indexed column, signed zero, numeric strings, JSON,
and SQL preparation. The ordinary error messages also matched. The
syntax errors for the wrong number of arguments retain Fsdb's parser
wording. [SQL fixture](2026-11-27-point-constructor.sql)

Another 1,000 generated coordinate pairs matched byte for byte in both
Fsdb modes. The generator used seed 85120 and alternated between signed
and unsigned integer ranges and floating-point coordinates spanning
exponents from −300 to 300. Binary preparation reported two
`DOUBLE` inputs and a binary `GEOMETRY` result with the same lengths,
flags, scale, and collation as MySQL.

`EXPLAIN` uses the spatial key for the audited `MBRIntersects`
query over numeric literal points. The indexed and forced-scan results
match. The planner leaves potentially warning-producing constructor
arguments for execution.

`CREATE TABLE AS SELECT POINT(1,2)` infers `GEOMETRY` in Fsdb and
`POINT` in MySQL. Fsdb also accepts a backtick-quoted `point` call as
the built-in, while MySQL resolves it as a routine name. `ST_AsText`
can format extreme doubles with a different exponent spelling.
