# Checked inner-join view writes

MySQL 8.4.11 marks a direct inner-join view with `WITH CASCADED CHECK OPTION`
updatable. It accepts writes that leave the row visible and returns error 1369
when a write breaks the join or the view predicate. FSDB previously rejected
the view at creation with error 1368 because its join-view classifier required
`CHECK OPTION` to be absent.

The focused native MySQL probe used `a(id PRIMARY KEY, n)` and
`b(id PRIMARY KEY, v)` with matching rows for IDs 1 and 2 and an additional
`b` row for ID 3. The checked view joined `b.id = a.id` and required `n > 0`
and `v > 0`. MySQL accepted `UPDATE ... SET n = 11 WHERE id = 1` and an insert
of `(id, n) = (3, 30)`. It returned 1369 for `n = 0`, `v = 0`, an insert with
no matching `b` row, and changing `a.id` from 1 to 3. The last update matters:
the new key matches `b.id = 3`, but MySQL checks the originally selected join
partner. A plain outer view inherited the checked join restriction, and a
duplicate-key update through the checked join also returned 1369.

`ViewTests` now exercises these cases. The write check reuses mutation join
evaluation for the candidate row and, for updates, compares its join partners
with those selected by the old row. The normal suite checks that existing view
behavior remains intact. This does not claim support for every join-derived
updatable view shape; the broader limitation remains in `GAPS.md`.
