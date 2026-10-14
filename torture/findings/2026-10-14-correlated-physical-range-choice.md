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

This does not add a general cost model or combine physical equality and range
candidates by row identity. Correlated predicates without a compatible lookup
still use the existing execution path.
