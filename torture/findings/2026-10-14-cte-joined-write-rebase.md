# CTE-joined writes claim physical targets

On native MySQL 8.4.11, a transaction updating a row makes a second
transaction's CTE-joined `UPDATE` or `DELETE` of that row wait. After the first
commits, the waiting write succeeds and commits. The disposable
[`oracle probe`](../scripts/cte-joined-write-oracle.py) checks both forms.

FSDB already claimed physical targets for ordinary joined writes, but
`transactionWriteTargets` skipped every statement with a CTE. In the focused
`UPDATE` regression, the waiting statement ran without a claim and its later
`COMMIT` failed with 1205. Discovery now claims the same physical target even
when the statement has a CTE. The focused `UPDATE` and `DELETE` regressions
both wait, rebase, and commit successfully.

The follow-up MySQL 8.4.11 probe uses `SET n=n+1` with a CTE source exposing
only `id`. MySQL waits and commits. Fsdb previously ran that statement without
a row claim, then failed its later `COMMIT` with 1205. Target discovery now
uses the CTE's declared or inferable output names while resolving bare
assignments; it claims the physical table only when exactly one source owns
the assigned column. A CTE that also exposes `n` leaves the assignment
ambiguous and returns MySQL error 1052. The focused regression covers the
wait, rebase, final value, and ambiguity guard.

The claim still requires a physical target discoverable through the joined
write path. CTE sources themselves are not claimed as physical tables, even
when a CTE name shadows a real table. Complex CTE output metadata that cannot
be inferred conservatively remains on the optimistic path.
