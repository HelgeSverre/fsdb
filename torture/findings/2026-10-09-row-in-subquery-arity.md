# Empty row-IN subquery arity

MySQL 8.4.11 returns error 1241 (`21000`, "Operand should contain 2 column(s)")
for `SELECT (1,2) IN (SELECT 1 WHERE 0)` and for the equivalent `NOT IN`.
`SELECT (1,2) IN (SELECT 1,2 WHERE 0)` returns `0`. The mismatch is an
expression-shape error, independent of whether the subquery yields a row.

fsdb previously returned `0` for the one-column empty subquery because row
width was checked only when comparing result rows. Row-valued `IN` now checks
the subquery result column count before using its membership cache or walking
rows. The focused Expecto test failed before the change and passes after it.
The `semantic-error-contracts` wire case covers both the error and a matching
empty result against native MySQL. Both pass in
`torture/artifacts/runs/20261009T171604571-1627/contracts`. The complete run
retains nine previously known identifier-case differences. `just check` passes
3,221 tests.
