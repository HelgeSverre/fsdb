# Correlated COUNT(*) must cover the full equality predicate

The imported findings “Correlated text COUNT trusts truncated index candidates,” “Correlated COUNT ignores equalities outside an index prefix,” and “Correlated COUNT overcounts binary prefix-index collisions” describe one unsafe shortcut: returning an index candidate count as the final `COUNT(*)` without checking whether those candidates are exactly the rows satisfying the subquery's WHERE clause.

The pinned MySQL 8.4.11 image (`mysql:8.4.11@sha256:b3b90af2a6552ae30c266fdb7d5dd55f3afb72404bb78d37fe8a23eb857fd3fb`) returned `1` for each of these two controls:

```sql
CREATE TABLE outer_t(v VARBINARY(8), tenant INT, bucket INT);
CREATE TABLE inner_t(v VARBINARY(8), tenant INT, bucket INT,
  KEY ix_v(v(2)), KEY ix_tenant(tenant, v));
INSERT INTO outer_t VALUES(X'010203',1,1);
INSERT INTO inner_t VALUES(X'010203',1,1),(X'010204',1,2);

SELECT (SELECT COUNT(*) FROM inner_t i WHERE i.v=o.v) FROM outer_t o; -- 1
SELECT (SELECT COUNT(*) FROM inner_t i
        WHERE i.tenant=o.tenant AND i.bucket=o.bucket) FROM outer_t o; -- 1
```

The first lookup's two-byte prefix bucket contains both rows, but only one full byte value matches. The second lookup can use `ix_tenant` to narrow candidates, but `bucket` remains a residual equality and rejects one row. The `COUNT(*)` shortcut now requires every equality to correspond to a distinct key part and every used key part to store the whole value. Otherwise execution retains its ordinary candidate-and-WHERE path. Fully covered keys still use direct cardinality without resolving rows.

Validation: the focused regression and existing zero-row-resolution cardinality test passed; `just check` built with zero warnings and passed 3,707 tests. This closes the three stated shortcut shapes, not every correlated-query planning case.
