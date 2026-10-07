# Natural full-text search with an aggregate

Status: fixed.

With default SQL modes including `ONLY_FULL_GROUP_BY`, MySQL 8.4.11 accepts:

```sql
CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT ft(body));
INSERT INTO docs VALUES (1,'database concurrency'), (2,'storage transactions');
ANALYZE TABLE docs;
SELECT COUNT(*) FROM docs WHERE MATCH(body) AGAINST ('database');
```

The result is 1. fsdb at `05b5a771` rejected the corresponding natural-language
aggregate with error 1055 because its implicit relevance sort referenced
`__fsdb_match_0__` as an ungrouped row column.

Implicit relevance ordering now applies only when the query does not group
rows. Aggregate projections, aggregates nested inside window functions, and
HAVING-only aggregates suppress that implicit sort. Ordinary full-text queries
retain their relevance ordering. Explicit ORDER BY behavior is unchanged.

The performance probe encountered this using a 1,000-row corpus. The native
MySQL 8.4.11 reproduction used the two-row example above and confirmed the
server's active SQL modes. The performance harness uses `SELECT id` and
checks the number of returned rows, so its timing results do not cover this
aggregate shape.

The Expecto regression failed with error 1055 before the fix. It now covers
COUNT, nested aggregate expressions, HAVING-only grouping, empty matches,
LIMIT, and a grouped aggregate feeding a window function. The
`natural-fulltext-aggregates` contract checks the same forms through text and
prepared statements against native MySQL 8.4.11.

Validation on 2026-10-07: `just check` passes 2,863 tests with no build warnings
or errors. All 46 contract cases and 4,990 differential steps pass. Manifest:
`torture/artifacts/runs/20261007T023019576-49159/contracts/manifest.json`.
