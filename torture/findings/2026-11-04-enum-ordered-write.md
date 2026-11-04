# Pending ENUM inserts in limited ordered writes

MySQL orders an `ENUM` by declaration ordinal. With
`ENUM('z','a','m')`, visible rows `'a'` and `'m'`, and an uncommitted `'z'`
insert, `UPDATE` or `DELETE ... ORDER BY rank_value ASC LIMIT 1` waits for
the insert and then changes `'z'`. With visible `'z'` and `'a'`, a pending
`'m'` insert has the same effect under descending order. The [disposable
MySQL 8.4.11 oracle](../scripts/enum-set-ordered-write-oracle.py) pins
wait behavior, one affected row, and final rows for the primary or secondary
key, single or two-part key, write verb, and direction combinations. A tied
ENUM value with a pending lower integer suffix is also covered.

Fsdb previously finished the ascending UPDATE before commit because pending
ordered claims excluded `ENUM`. The secondary index's equality key encodes
the label's collation key, which is not its declaration ordinal. The insert
observation claim now separately stores the ordinal with an `R` tag, while
the ordinary index key remains unchanged. The ordered-write scheduler reads
that ordinal and compares it with visible ENUM rows in declaration order.
The transaction regression covers all of those key layouts.

For an ENUM primary key, the ordinary unique-key claim still encodes the
label for equality. A separate primary observation claim encodes its ordinal
so the ordered writer can see the pending row. This covers ENUM-led single
and two-part primary and secondary keys with `LIMIT 1`, including an audited
tie on the integer suffix. SET is covered in the
[companion finding](2026-11-04-set-ordered-write.md). Composite indexes, range
predicates, and broader isolation histories remain to audit.

```sh
uv run --with pymysql python torture/scripts/enum-set-ordered-write-oracle.py
```
