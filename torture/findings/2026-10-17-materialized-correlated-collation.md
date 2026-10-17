# Materialized correlated equality across collations

A materialized correlated equality previously keyed both operands using the
inner column's collation. This could omit a row when SQL selected a different
collation for the comparison. MySQL 8.4.11 returns one match for this query:

```sql
CREATE TABLE outer_labels(
  id INT PRIMARY KEY,
  label VARCHAR(10) CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci
);
CREATE TABLE inner_labels(
  label VARCHAR(10) CHARACTER SET latin1 COLLATE latin1_bin
);
INSERT INTO outer_labels VALUES (1,'A');
INSERT INTO inner_labels VALUES ('a');
SELECT o.id,
       (SELECT COUNT(*) FROM (SELECT label FROM inner_labels) d
        WHERE d.label=o.label) AS matches
FROM outer_labels o;
```

MySQL returns `(1,1)`; fsdb returned `(1,0)` because its materialized lookup
used `latin1_bin` keys for both `a` and `A`. The SQL comparison instead uses
the Unicode outer column's accent- and case-insensitive collation.

The equality extractor now retains the bound expression. It uses a materialized
text key only when the effective SQL comparison collation matches that key's
collation, as the existing materialized `IN` and range paths already require.
Other stable equalities continue to narrow candidates; an incompatible text
comparison evaluates the complete predicate against the materialized rows.
An Expecto regression covers derived and stable-CTE sources, a matching and a
nonmatching outer row. The existing 1,000-row same-collation probe still makes
only five residual calls for direct text equality, confirming that the
compatible lookup remains active. A fresh Release wire replay returned
`((1,1),(2,0))` for both source shapes on both native MySQL 8.4.11 and fsdb.

This audit addresses the tested materialized equality path. It does not prove
parity for every correlated expression, collation combination, or query plan.
