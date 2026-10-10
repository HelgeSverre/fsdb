# Prepared stored-function schema

The digest-pinned MySQL 8.4.11 oracle rejects `PREPARE p FROM 'SELECT f()'`
with 1046 (`3D000`) when no database is selected and a function `f` exists in
another schema. Preparing `SELECT schema.f()` succeeds. A prepared unqualified
call also retains its preparation schema: after `USE` switches to a schema
containing a different `f`, `EXECUTE p` still calls the original function.

Fsdb validates function names during preparation against a registry that
includes stored functions and their result metadata. Text and binary prepared
statements retain the preparation database for function lookup at execution.
The same [database context](2026-10-10-prepared-database-context.md) now applies
to unqualified table names and `DATABASE()` during prepared execution.
The focused regression covers both protocols, qualified calls without a
selected database, and schema switches with same-named zero-argument and
parameterized functions. Other prepared statement binding rules are outside
this finding.
