# Derived-source view updateability metadata

MySQL 8.4.11 reports `IS_UPDATABLE=YES` for an outer view that directly
projects columns from a derived table or CTE, even when that source cannot
accept a write. Examples checked against the digest-pinned MySQL image:

```sql
CREATE VIEW derived_constant AS SELECT d.k FROM (SELECT 1 k) d;
CREATE VIEW cte_constant AS WITH c(k) AS (SELECT 1) SELECT k FROM c;
CREATE VIEW recursive_only AS
  WITH RECURSIVE seq(k) AS
    (SELECT 1 UNION ALL SELECT k+1 FROM seq WHERE k<3)
  SELECT k FROM seq;
```

All three return `YES` from `information_schema.views`; `UPDATE ... SET k=4`
returns 1288. MySQL also reports `YES` for outer projections over grouped,
`DISTINCT`, limited, and UNION-derived sources, and for direct, starred,
filtered, or ordered projections from the recursive CTE. An expression-only
outer projection or an outer `DISTINCT`, `GROUP BY`, or `LIMIT` returns `NO`.
The source query's own restrictions therefore do not determine this metadata
flag; the outer view shape does. A left-outer-join view remains `NO`.

Fsdb previously returned `NO` for the read-only-source examples. Its metadata
check now admits a direct derived source or a named CTE as an apparent source
of writable columns while retaining outer-query disqualifiers. The executor's
separate write-target check remains strict, so these views still reject UPDATE
with 1288. The focused regression covers the metadata and rejection together.
