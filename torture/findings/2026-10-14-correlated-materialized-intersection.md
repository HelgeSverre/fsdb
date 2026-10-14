# Intersecting correlated materialized candidates

MySQL 8.4.11 returned two rows for the audited correlated count over a
1,000-row derived source. The inner predicate bound `user_id` to the outer
`users.id` and limited `id` to the next 50 values. MySQL's TREE plan scanned
the materialized derived table for this query, so this case is an execution
result oracle rather than a physical-plan parity target.

Fsdb builds statement-local equality and range lookups over stable materialized
sources. Previously it selected the shortest candidate list when multiple
lookups applied. That was correct but left rows rejected by the other indexed
conditions for residual evaluation. In the focused fixture, the equality list
contained 20 rows, the ID range contained 51, and only two overlapped. A
residual function placed first in the predicate ran 22 times, including
projection/type setup, before the change.

Fsdb now intersects compatible candidate lists by materialized row identity,
then evaluates residual predicates. The first list remains in source order, and
duplicate rows remain distinct. Both equality-plus-range and two independent
range conditions return the same two rows while the focused regression keeps
residual calls below five. The lookup still falls back to general execution
when a predicate cannot be represented by a statement-local key.
