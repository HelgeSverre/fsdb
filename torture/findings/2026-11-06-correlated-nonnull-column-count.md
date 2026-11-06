# Correlated counts of non-NULL physical columns

The joined correlated count path now treats `COUNT(t.column)` as a row count
when the qualified source is a physical table, the declared column is
non-NULL or primary-key, and the join chain cannot pad that source with NULL.
The proof tracks left and right outer joins separately. It applies to plain
scalar counts and count-based correlated `EXISTS ... HAVING` membership.
Unknown, derived, nullable, and outer-padded columns retain ordinary
aggregate evaluation.

Pinned MySQL 8.4.11 and fsdb agree that `COUNT(sys.id)` and
`COUNT(cat_sys.id)` equal `COUNT(*)` over the audited inner join. A 204-row
outer control with `COUNT(cat_sys.id)` remains below 400 examined rows, so
the joined source is grouped once rather than scanned for each outer row.

MySQL and fsdb also agree on the controls: a primary key on the nullable
side of either `LEFT JOIN` or `RIGHT JOIN` counts zero for an unmatched
outer row; a primary key on the preserved side counts one; and a declared
nullable column containing NULL counts zero. Grouped and ungrouped
`HAVING COUNT(i.id)` preserve the distinct empty-group behavior and use the
indexed membership path in the focused regression.

This removes a common correlated-query planning omission. General
cost-based selection and expression or derived-column nullability proofs
remain open.
