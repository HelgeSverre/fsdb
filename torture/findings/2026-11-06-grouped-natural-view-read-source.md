# Grouped derived sources in writable natural-join views

Pinned MySQL 8.4.11 and current fsdb agree on the updatability boundary for
views with a grouped derived read source and a physical target. A derived
source using `GROUP BY id HAVING SUM(flag)>0` or `GROUP BY id WITH ROLLUP`
can precede the target in an inner `NATURAL JOIN`; both views report
`IS_UPDATABLE=YES` and update the matched physical row once. In the audited
three-row fixture, the `HAVING` view exposes ids 1 and 2, while the `ROLLUP`
view exposes ids 1, 2, and 3. The rollup total row has a NULL key and does
not create an extra target match.

Moving the same grouped `HAVING` source to the nullable side of an explicit
or natural `RIGHT JOIN` makes the view read-only on both engines.
`IS_UPDATABLE=NO`, and `UPDATE` returns 1288 without changing the target.
The focused `ViewTests` regression records the rows, metadata, affected
counts, and rejection. Ordered or limited `UPDATE` through the inner joined
view also returns MySQL error 1221 on both engines.

This closes the audited view-shape question; broader derived-source and
contention combinations remain open.
