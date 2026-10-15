# Preserved-side bounds for nullable ngram sources

In an outer join, a literal filter on the preserved owner's key can constrain
the nullable document key through an exact `ON` equality without filtering out
the preserved row. For example:

```sql
SELECT o.id,d.id,MATCH(d.body,d.title) AGAINST('生日' IN BOOLEAN MODE)
FROM owners o LEFT JOIN cjk_docs d ON d.owner_id=o.id
WHERE o.id IN (42,999);
```

FSDB already used a literal bound written directly on `d.owner_id` to restrict
the ngram scoring pre-pass. It missed the equivalent owner-side bound. The
focused fixture prepared 1,600 virtual document values for an equality case
that joins ten documents. With projected scoring and an `IN` bound, it
prepared 2,000 values because it also materialized every document row for the
join.

The planner now reuses its type-compatible inner-join bound inference with
only predicates local to the preserved side. It retains only bounds inferred
for the nullable side, and applies those to both score candidates and the
nullable input rows. The original outer join, `ON`, and `WHERE` expressions
still determine row membership; relevance still uses the complete indexed
corpus. The path handles one outer join among otherwise eligible physical
inner joins. It does not infer bounds across ambiguous names, incompatible
column types, or an incomplete equality chain.

The [native MySQL 8.4.11 oracle](../scripts/fulltext-join-bounds-oracle.py)
confirms equivalent rows and scores for left and right joins, an owner literal
in `ON`, and an unmatched owner under `IN`. The focused regression requires
fewer than 30 virtual preparations in each bounded case and verifies the
unmatched owner's score is `0`.
