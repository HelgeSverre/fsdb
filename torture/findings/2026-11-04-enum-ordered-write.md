# Pending ENUM inserts in limited ordered writes

MySQL orders an `ENUM` by declaration ordinal. With
`ENUM('z','a','m')`, visible rows `'a'` and `'m'`, and an uncommitted `'z'`
insert, `UPDATE` or `DELETE ... ORDER BY rank_value ASC LIMIT 1` waits for
the insert and then changes `'z'`. With visible `'z'` and `'a'`, a pending
`'m'` insert has the same effect under descending order. The [disposable
MySQL 8.4.11 oracle](../scripts/keyless-enum-ordered-write-oracle.py) pins
wait behavior, one affected row, and final rows for all eight combinations
of primary or secondary key, write verb, and direction.

Fsdb previously finished the ascending UPDATE before commit because pending
ordered claims excluded `ENUM`. The secondary index's equality key encodes
the label's collation key, which is not its declaration ordinal. The insert
observation claim now separately stores the ordinal with an `R` tag, while
the ordinary index key remains unchanged. The ordered-write scheduler reads
that ordinal and compares it with visible ENUM rows in declaration order.
The eight-case transaction regression covers both key layouts.

For an ENUM primary key, the ordinary unique-key claim still encodes the
label for equality. A separate primary observation claim encodes its ordinal
so the ordered writer can see the pending row. This covers single-column ENUM
primary and secondary keys with `LIMIT 1`. SET, composite indexes, range
predicates, and broader isolation histories remain to audit.

```sh
uv run --with pymysql python torture/scripts/keyless-enum-ordered-write-oracle.py
```
