# Derived-source joined writes claim a bare assignment's physical target

On native MySQL 8.4.11, a transaction holding `a(id=1)` makes a second
transaction's `UPDATE a JOIN (SELECT 1 AS id) d ON d.id=a.id SET n=n+1`
wait. After the first commits, the second increments the current value and
commits. A derived source that also exposes `n` makes the bare assignment
ambiguous (error 1052). The disposable
[`oracle probe`](../scripts/cte-joined-write-oracle.py) checks both outcomes.

FSDB previously omitted the row claim because write-target discovery required
every joined source to be a physical table or CTE. The waiting statement then
ran against its stale snapshot and its commit failed with 1205. Discovery now
uses the derived source's complete output names when deciding whether the
bare assignment has exactly one physical owner. The focused transaction
regression covers the wait, rebase, commit, final value, and ambiguity error.

This does not claim derived sources as write targets. Sources whose output
names cannot be determined remain on the conservative optimistic path.
