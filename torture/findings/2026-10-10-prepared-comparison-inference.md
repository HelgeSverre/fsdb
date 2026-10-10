# Prepared comparison parameter inference

Native MySQL 8.4.11 with `lower_case_table_names=2` was the oracle. The
following setup and SQL `PREPARE`/`EXECUTE` probes used the same text value in
each case:

```sql
CREATE TABLE names (
  id INT PRIMARY KEY,
  name VARCHAR(30) COLLATE utf8mb4_bin,
  INDEX ix_normalized ((UPPER(TRIM(name))))
);
INSERT INTO names VALUES
  (1, ' user_1 '), (2, ' user_2 '), (3, ' user_3 ');
SET @key = 'USER_2';
```

| Prepared predicate | Returned IDs |
|---|---|
| `UPPER(TRIM(name)) = ?` | `2` |
| `id > 0 AND UPPER(TRIM(name)) = ?` | `2` |
| `UPPER(TRIM(name)) = ? OR id = 3` | `2, 3` |
| `? = UPPER(TRIM(name))` | `2` |

Before the fix, fsdb propagated the boolean WHERE result type into the
comparison's string parameter and converted `USER_2` to integer zero.
Nonnumeric string keys then compared equal to zero, returning extra rows and
making the prepared functional lookup appear about 45–49 milliseconds slow at
10,000 rows. A first-row-only benchmark assertion missed the extra rows.

Comparison operands now infer from each other without inheriting the boolean
result type. The focused prepared-statement regression checks all four shapes,
the exact result set, and bounded residual evaluation for the indexed case.
The benchmark setup now requires exactly one row; the corrected lookup takes
about 251–278 microseconds in the recorded short runs.
