# Prepared statement database context

The digest-pinned MySQL 8.4.11 SQL `PREPARE`/`EXECUTE` oracle binds an
unqualified table name to the database selected at `PREPARE` time. After `USE`
changes the session database, prepared reads and updates still use the
preparation database.
`DATABASE()` inside the prepared query reports that database, while a later
ordinary query reports the caller's newly selected database. A statement
prepared without a selected database continues to report `NULL` for
`DATABASE()` after the caller selects one.

Fsdb retains the preparation database on each prepared handle and executes it
through a scoped session for binding, authorization, metadata refresh, and
statement evaluation. The scope restores the caller's selected database in
the returned session. Focused Expecto regressions cover text and binary reads,
updates, both `DATABASE()` contexts, and preparation without a selected
database.
