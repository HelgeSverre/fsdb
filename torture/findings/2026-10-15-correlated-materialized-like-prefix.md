# Correlated binary LIKE prefixes over materialized rows

MySQL 8.4.11, run as a disposable native server, returned `(2, 2, 0)` for
outer patterns `a01%`, `a99%`, and `NULL` against a `DISTINCT` derived source
containing `a010`, `a011`, `a990`, `a991`, and `zzz`. Replacing that source with
a stable CTE returned the same counts. For the NULL pattern, adding an `OR`
branch matching `id = 1` returned one row. The columns used
`utf8mb4_0900_bin`; the local regression also covers `utf8mb4_bin`.
An additional `utf8mb4_bin` oracle with an `OR id = 5` branch outside both
prefix matches returned `(3, 3, 1)` for the derived and CTE forms.

```sql
CREATE TABLE patterns(id INT PRIMARY KEY, p VARCHAR(8) COLLATE utf8mb4_0900_bin);
CREATE TABLE codes(id INT PRIMARY KEY, code VARCHAR(8) COLLATE utf8mb4_0900_bin);
INSERT INTO patterns VALUES (1,'a01%'), (2,'a99%'), (3,NULL);
INSERT INTO codes VALUES (1,'a010'), (2,'a011'), (3,'a990'), (4,'a991'), (5,'zzz');

SELECT p.id,
       (SELECT COUNT(*) FROM (SELECT DISTINCT id, code FROM codes) c
        WHERE c.code LIKE p.p)
FROM patterns p ORDER BY p.id;

WITH cte AS (SELECT DISTINCT id, code FROM codes)
SELECT p.id,
       (SELECT COUNT(*) FROM cte c WHERE c.code LIKE p.p)
FROM patterns p ORDER BY p.id;
```

FSDB already returned the right rows. Its first 1,000-row residual probe
evaluated `TOUCH(c.id)` 2,024 times: the metadata probe and NULL outer row
each scanned the materialized source. A binary prefix now uses a cached,
ordered statement-local range, and a NULL pattern in a conjunct yields an
empty candidate set. The original filter still checks every candidate. The
focused derived and CTE regressions keep residual calls below 60 while
checking result rows, the NULL/OR branch, and invalid-column errors. Other
collations and patterns without a fixed prefix retain general evaluation.
Complete `OR` branches containing a prefix or a NULL pattern now feed the
same materialized candidate union before an outer residual; the focused
regression keeps those calls below 100 for both source forms.
