# Pending ENUM inserts in limited ordered writes

MySQL orders an `ENUM` by declaration ordinal. With
`ENUM('z','a','m')`, visible rows `'a'` and `'m'`, and an uncommitted `'z'`
insert, `UPDATE` or `DELETE ... ORDER BY rank_value ASC LIMIT 1` waits for
the insert and then changes `'z'`. With visible `'z'` and `'a'`, a pending
`'m'` insert has the same effect under descending order. The [disposable
MySQL 8.4.11 oracle](../scripts/keyless-enum-ordered-write-oracle.py) pins
wait behavior, one affected row, and final rows for all four combinations.

Fsdb previously finished the ascending UPDATE before commit because pending
ordered claims excluded `ENUM`. The secondary index's equality key encodes
the label's collation key, which is not its declaration ordinal. The insert
observation claim now separately stores the ordinal with an `R` tag, while
the ordinary index key remains unchanged. The ordered-write scheduler reads
that ordinal and compares it with visible ENUM rows in declaration order.
The four-case transaction regression failed before the fix and passes after.

This covers a single ENUM secondary index on a keyless table with `LIMIT 1`.
ENUM primary keys, SET, composite indexes, range predicates, and broader
isolation histories remain to audit.

```sh
uv run --with pymysql python torture/scripts/keyless-enum-ordered-write-oracle.py
```
