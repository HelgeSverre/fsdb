# Writable join view beside a UNION ALL derived source

MySQL 8.4.11 permits an UPDATE through a joined view when a read-only
`UNION ALL` derived source precedes the writable physical component. The
derived source can produce several rows for one physical target; UPDATE
changes that target once. A cascaded check depending on a derived column
must hold for every joined row. A passing row cannot conceal a failing one,
and the rejected write leaves the physical row unchanged.

The [composed-view oracle](../scripts/composed-join-view-oracle.py) now tests
this shape against MySQL, including both insertion orders of duplicate
derived rows. The corresponding fsdb regression in
[`ViewTests.fs`](../../tests/Fsdb.Tests/ViewTests.fs) verifies writable
metadata, single physical update, and atomic CHECK OPTION rejection.

The native oracle and focused fsdb test passed. This closes the audited
`UNION ALL` derived-source order. The [UNION CTE follow-up](2026-11-04-union-cte-updatable-view.md)
covers an independent CTE body; other compositions and security contexts
remain open.
