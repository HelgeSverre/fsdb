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

This change covers physical target references discoverable through the
existing joined-write path. It does not infer an unqualified assignment owner
through a nonphysical CTE source or claim a CTE reference as a physical target,
even when its name shadows a real table.
