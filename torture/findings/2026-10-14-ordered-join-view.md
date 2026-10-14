# Ordered join views retain insertion but reject UPDATE

On native MySQL 8.4.11, an inner-join view with `ORDER BY a.id DESC` reports
`IS_UPDATABLE=YES` and returns rows in the saved order. `INSERT INTO view
(id,n) VALUES (3,30)` succeeds when the corresponding join partner exists.
An outer view selecting from it also reports YES and accepts an insert into the
same component. MySQL rejects UPDATE through either layer with error 1221,
`Incorrect usage of UPDATE and ORDER BY`, including an UPDATE without its own
ORDER BY or LIMIT. A checked ordered join view is accepted at creation.

FSDB previously classified the ordered join as non-updatable and reported NO,
blocking the valid inserts. The join-view classifier now retains the saved
order without excluding the view. The UPDATE path detects an inherited saved
order and returns 1221 before rewriting the joined mutation. A regression
checks metadata, read order, direct and layered inserts, and the three UPDATE
forms audited against MySQL. Broader nested security contexts and other
updatable-view expression shapes remain open.
