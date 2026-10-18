# Correlated advisory-lock probe oracle

Finding 122 reports that constructing a correlated lookup can call `GET_LOCK`
even when the inner table is empty. A blanket ban on such probe evaluation
would diverge from MySQL 8.4.11. The oracle distinguishes indexed and
unindexed inner tables.

With one outer row (`users.id = 1`) and an empty table `empty_indexed(k INT,
KEY ix_k(k))`, MySQL 8.4.11 returned `0` from this count and then a non-NULL
connection ID from `IS_USED_LOCK('fsdb-probe-1')`:

```sql
SELECT (SELECT COUNT(*) FROM empty_indexed e
        WHERE e.k = GET_LOCK(CONCAT('fsdb-probe-', users.id), 0))
FROM users;
SELECT IS_USED_LOCK('fsdb-probe-1');
```

Fsdb also returned `0` and held the lock. Thus evaluating the function while
forming an indexed probe is MySQL-compatible for this case.

For an otherwise identical empty table without an index, MySQL returned `0`
and left `IS_USED_LOCK('fsdb-plain-1')` NULL. Fsdb returned `0` but held that
lock. The difference persisted with `e.k > GET_LOCK(...)` and with
`e.k + 0 = GET_LOCK(...)`, which means it is not limited to an exact indexed
equality probe. These observations call for tracing the unindexed planner and
metadata-evaluation paths before changing stability classification. Stored SQL
functions and non-lock side effects were not covered by this oracle run.
