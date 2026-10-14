# Choosing a correlated physical range over a broad equality

The audited query has one outer user and 1,000 inner orders. The first 500
orders share `user_id=1`; only IDs 1 through 6 satisfy the correlated ID range.
Both the direct physical table and a pass-through derived source return a count
of six on native MySQL 8.4.11. After `ANALYZE TABLE`, MySQL's TREE plan still
uses the `user_id` lookup and estimates 500 rows before the range filter. This
is a result oracle and a selectivity comparison, not a claim of identical
physical plans.

Before the change, FSDB always preferred an available equality candidate over
a correlated range candidate. A residual `TOUCH(id)=id` in the regression ran
502 times for the direct physical source. The planner now compares observed
candidate counts across equality and range lookups and uses the smaller slice;
both the direct source and pass-through projection keep residual calls below
20 while returning six. Equality candidates of zero or one row skip the extra
range probes, as do statements without a syntactic range predicate.

That first change did not add a general cost model or combine physical equality
and range candidates by row identity. Correlated predicates without a
compatible lookup still use the existing execution path.

Follow-up: MySQL 8.4.11 returned two rows when a 1,000-row table had a
500-row equality bucket (`user_id=1`) and a 400-row primary-key range
(`id BETWEEN 499 AND 898`). Only IDs 499 and 500 satisfied both. The former
FSDB choice of the narrower slice still sent hundreds of rows to a residual
function. Direct physical probes now intersect their candidate row arrays by
identity before residual evaluation, reducing the focused regression to fewer
than 20 residual calls while retaining the MySQL result. This path is limited
to direct physical tables whose read rows are the stored rows.

Pass-through projected sources now intersect the physical row IDs from their
compatible equality and range lookups before projecting. The same 500-row
bucket and 400-row range returns two rows on native MySQL 8.4.11 and FSDB;
FSDB calls the residual fewer than
20 times, whereas the previous projected path called it 402 times. Other
projected access families still select one candidate family.

An ordinary indexed equality and a functional indexed equality now intersect
by physical row ID before projecting. The native MySQL 8.4.11 oracle used
1,000 rows, with `user_id=1` on IDs 1–500 and `ABS(band)=7` on IDs 499–898.
Both the direct table and `(SELECT id,user_id,band FROM probe_orders)` returned
two rows for `user_id=u.id AND ABS(band)=u.target`. Before the change, the
FSDB residual `TOUCH(id)=id` ran 402 times on the first source; the focused
regression now requires fewer than 20 calls on both sources. Functional
candidates participate only when the physical read sees the stored rows, so
projection or read overlays that change row identity keep their existing path.

Two correlated ranges over different indexed columns now share the same
physical row-ID intersection path through a pass-through projection. Native
MySQL 8.4.11 returned two rows for `id BETWEEN 1 AND 400` combined with
`band BETWEEN 399 AND 798` over 1,000 numbered rows. Before the change FSDB
sent 402 candidates to a residual function; the focused regression now sends
fewer than 20 while returning the same two rows.

Direct physical sources now use the same row-ID intersection for multiple
correlated ranges. Native MySQL 8.4.11 also returns two rows for the direct
version of that query. FSDB previously evaluated the residual over 402 rows;
the direct regression now stays below 20 calls. A zero- or one-row equality
candidate still skips the extra range probes.
