# Layered inner-join view writes

MySQL 8.4.11 permits an `ALGORITHM=MERGE` outer view over a mergeable inner-join
view to remain updatable when the view layers use different `SQL SECURITY`
modes or definers. An audited outer `SQL SECURITY INVOKER` view reported
`information_schema.views.IS_UPDATABLE = YES` and updated columns from both
joined tables. A left- or right-outer-join view was read-only and returned 1288
for `UPDATE`; this change does not make those views writable.

The privilege boundary is each view's exposed interface. With distinct
definers on the joined view and its outer view, MySQL allowed updates while
the outer definer held `SELECT, UPDATE` on the inner view and the inner definer
held the required base-table grants. Revoking the outer definer's `UPDATE` on
the inner view caused error 1356 for both joined components. When the inner
view exposed `a.n AS metric` and the outer view exposed `metric AS n`, granting
only `UPDATE(metric)` on the inner view allowed the outer `n` update while an
update of the other component still returned 1356. A third definer layer with
`UPDATE(n)` on the middle view preserved the same result.

For explicit-column `INSERT`, MySQL also returned 1356 when either the outer
definer lacked `INSERT` on the inner view or the inner definer lacked the needed
base-table `INSERT` grant. With both grants, the insert succeeded when a join
partner already existed.

FSDB now retains the mergeable outer view's writable metadata and validates
the update at each layer using that layer's exposed column name and security
context. The focused regression is in `tests/Fsdb.Tests/ViewTests.fs` under
"joined view outer layers preserve invoker and definer grants". Other joined
insert shapes and ambiguous duplicate exposed columns remain outside this
audit.
