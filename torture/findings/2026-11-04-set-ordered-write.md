# Pending SET inserts in limited ordered writes

MySQL orders a `SET` column by its member bitmask. For `SET('z','a','m')`,
the values `'z'`, `'a'`, `'z,a'`, and `'m'` sort in that order, regardless
of their text. An uncommitted insert that belongs at the `LIMIT 1` frontier
causes an ordered UPDATE or DELETE to wait, then affect that inserted row.
The [MySQL 8.4.11 oracle](../scripts/enum-set-ordered-write-oracle.py)
checks primary and secondary indexes, both write verbs and directions, and
a multi-member value.

Fsdb previously sorted displayed SET labels as text and did not observe
pending SET inserts. It now uses one bitmask conversion for visible ORDER BY
keys, pending ordered claims, and foreign-key storage values. A SET primary
key retains its ordinary equality claim and adds a numeric observation claim;
secondary indexes add the same numeric observation value. The transaction
regression checks the twelve oracle cases.

This covers single-column SET primary and secondary keys with `LIMIT 1`.
Composite indexes, range predicates, and broader isolation histories still
need investigation.

```sh
uv run --with pymysql python torture/scripts/enum-set-ordered-write-oracle.py
```
