# Correlated top-N over nullable index prefixes

`<=>` can now bind nullable integer and matching-collation text
prefixes for scalar and lateral `ORDER BY … LIMIT` queries. A direct
`IS NULL` or `<=> NULL` predicate can supply another key part. The
lookup preserves the requested suffix order across text runs that
compare equal. The original WHERE clause filters each candidate
before applying OFFSET and LIMIT. [MySQL range optimization](https://dev.mysql.com/doc/refman/8.4/en/range-optimization.html)

The fixture matched 24 result rows against MySQL 8.4.11 in both
Fsdb storage modes. It covers nullable outer and inner keys, mixed
`=` and `<=>` predicates, a missing key, case and accent folds, both
sort directions, a residual filter, offsets, and `IGNORE INDEX`.
[SQL fixture](2026-11-27-correlated-null-safe-top-n.sql)

| Fsdb lateral query | Rows examined |
| --- | ---: |
| Baseline `eca53bd5` | 1,159 |
| Ordered prefix seek | 46 |
| Candidate with `IGNORE INDEX` | 1,659 |

Fsdb's `EXPLAIN ANALYZE` reports the ordered lookup on all five
outer rows. MySQL uses the same index prefix with a per-lookup sort
in this fixture.

A seeded sweep matched all 384 query variants across both Fsdb
storage modes and MySQL. Seed 92528 generated 1,000 events and
39 outer rows per collation. The sweep covered
`utf8mb4_0900_ai_ci`, `utf8mb4_unicode_ci`, and `utf8mb4_0900_bin`
on the inner and outer text columns, with trailing spaces, multiple bounds and
offsets, and each query repeated with `IGNORE INDEX`.

The sweep also exposed a separate schema-default gap. A database
created with `CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci`
still reports `utf8mb4_0900_ai_ci` as its default in Fsdb. An inherited
`VARCHAR` column then treats `'CAFÉ'` and `'cafe '` as unequal,
while a column explicitly declared `COLLATE utf8mb4_unicode_ci`
matches MySQL. Both `=` and `<=>` show the difference on a table scan.
The [database character defaults follow-up](2026-11-27-database-character-defaults.md)
fixes the inheritance gap.
