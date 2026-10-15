# Grant diagnostics through layered writable views

Native MySQL 8.4.11 allows an outer definer view to update a direct inner
definer view when the outer owner can update the inner view and the inner
owner can read and update its base table. Revoking the outer owner's `UPDATE`
on the inner view, the inner owner's `SELECT` on the base table, or the inner
owner's base `UPDATE` each makes `UPDATE outer_view` return 1356. The base row
remains unchanged. FSDB previously exposed its underlying 1142 or 1143 grant
error for those direct-view paths; it now reports the invalid outer view as
MySQL does. A focused regression covers all three revocations and a
column-level intermediate grant that still permits the update. A separate
owner-created-view and non-root writer probe returned the same 1356 for a
revoked inner base `UPDATE`, correcting an older FSDB test that expected 1142.

The distinguishing definitions are:

```sql
CREATE DEFINER='inner_owner'@'%' SQL SECURITY DEFINER VIEW inner_view
  AS SELECT id,n FROM target;
CREATE DEFINER='outer_owner'@'%' SQL SECURITY DEFINER VIEW outer_view
  AS SELECT id,n AS metric FROM inner_view;
UPDATE outer_view SET metric=metric+1 WHERE id=1;
```

A related two-definer view whose inner query joins the base table to
`JSON_TABLE` behaves differently in MySQL. After the inner owner loses base
`SELECT`, MySQL returns 1143 for the referenced base `id` column while FSDB
returns 1356. FSDB's prerequisite check reports a table-level 1142 before
view error translation. Mapping that error to 1143 would not identify which
column was denied and could mis-handle partial column grants. This remains an
open diagnostic and authorization-order gap; the denied write changes no row
in either engine.

The inner query in this variant is `SELECT a.id,a.n,j.x FROM target a JOIN
JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id`; the same
diagnostic difference occurred when `JSON_TABLE` used `JSON_ARRAY(a.id)`.

The [native MySQL oracle](../scripts/layered-view-grant-oracle.py) reproduces
the grant sequence and error codes in a disposable server. These results cover
the audited two-definer fixtures, not all view algorithms, join sources, or
grant combinations.
