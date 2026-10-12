# Integer NULLIF functional keys

The digest-pinned MySQL 8.4.11 oracle accepts
`NULLIF(integer_column, integer_literal)` as a functional index, including a
unique key. Rows whose source equals the comparison literal project NULL, as
do source NULLs. A unique key admits multiple such rows but rejects duplicate
non-NULL results with 1062/23000. MySQL uses the maintained key for equality
and updates it after mutations.

The hidden result is always nullable. `TINYINT`, `SMALLINT`, `MEDIUMINT`, and
`INT` sources promote to an `INT` result, preserving signedness, with a
five-byte key. `BIGINT` sources retain their width and signedness, with a
nine-byte key. A `TINYINT NOT NULL` source still gets the NULL byte because
`NULLIF(v,5)` can produce NULL. These result types were checked with MySQL
`CREATE TABLE AS SELECT`; access and key width were checked with `EXPLAIN`.
The `BIGINT UNSIGNED` maximum remains exact. MySQL also indexes a composed
`ABS(NULLIF(v,0))` expression.

Fsdb now records a literal NULL comparison in the physical-expression model.
Key projection, uniqueness, equality probes, function-override guards, and
key-length metadata use that same expression identity. The focused Expecto
regression covers nullable uniqueness, narrow-type promotion, and the exact
unsigned maximum. The differential case covers rows, duplicate diagnostics,
a nonzero comparison literal, composition with `ABS`, and key maintenance
through an update. All 3,492 Fsdb tests pass. All 11
`nullif-functional-index` steps match MySQL in
`20261012T104107235-2663/contracts`; the full run has 143 cases and
19,019 steps, with the same ten previously documented differences as before.
Non-integer or non-literal comparisons and broader compositions remain open.
