# Natural full-text search with an aggregate

Status: open.

With default SQL modes including `ONLY_FULL_GROUP_BY`, MySQL 8.4.11 accepts:

```sql
CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT ft(body));
INSERT INTO docs VALUES (1,'database concurrency'), (2,'storage transactions');
ANALYZE TABLE docs;
SELECT COUNT(*) FROM docs WHERE MATCH(body) AGAINST ('database');
```

The result is 1. fsdb at `05b5a771` rejects the corresponding natural-language
aggregate with error 1055. Its error names an ORDER BY expression containing
`__fsdb_match_0__`, although the query has no explicit ORDER BY. This suggests
implicit relevance ordering reaches aggregate validation; the exact cause
still needs a focused regression and source investigation.

The performance probe encountered this using a 1,000-row corpus. The native
MySQL 8.4.11 reproduction used the two-row example above and confirmed the
server's active SQL modes. The performance harness uses `SELECT id` and
checks the number of returned rows, so its timing results do not cover this
aggregate shape.
