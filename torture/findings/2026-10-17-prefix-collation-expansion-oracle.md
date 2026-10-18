# Prefix-index collation expansion: MySQL 8.4.11 oracle

The imported security finding “Unicode expansions can evade prefix-index equality probes” claims that fsdb's prefix-key truncation can omit rows whose full values compare equal. That internal mismatch is real, but the pinned MySQL 8.4.11 oracle has the same indexed-versus-scan behavior. The finding therefore is **not an fsdb/MySQL compatibility gap**. Do not change the indexed path to include those rows without revisiting the compatibility target.

Oracle: `mysql:8.4.11@sha256:b3b90af2a6552ae30c266fdb7d5dd55f3afb72404bb78d37fe8a23eb857fd3fb`, isolated database, default `utf8mb4_0900_ai_ci`.

```sql
CREATE TABLE indexed(id INT PRIMARY KEY, label VARCHAR(30), KEY ix_label(label(1)));
CREATE TABLE scanned(id INT PRIMARY KEY, label VARCHAR(30));
INSERT INTO indexed VALUES(1,'æra'),(2,'aera'),(3,'beta');
INSERT INTO scanned VALUES(1,'æra'),(2,'aera'),(3,'beta');

SELECT id FROM indexed WHERE label='aera' ORDER BY id; -- 2
SELECT id FROM indexed IGNORE INDEX(ix_label) WHERE label='aera' ORDER BY id; -- 1, 2
SELECT id FROM scanned WHERE label='aera' ORDER BY id; -- 1, 2
EXPLAIN SELECT id FROM indexed WHERE label='aera'; -- ref on ix_label, estimated 1 row
```

The collation itself equates the strings: `SELECT _utf8mb4'æra' COLLATE utf8mb4_0900_ai_ci = _utf8mb4'aera' COLLATE utf8mb4_0900_ai_ci` returns `1`, and `WEIGHT_STRING` returns the same bytes for both. But the one-character prefix keys differ. MySQL uses the prefix key, then applies the full predicate only to its candidate bucket. On the same fixture, `UPDATE indexed SET id=id+10 WHERE label='aera'` changes only id `2` to `12`; a subsequent `DELETE ... WHERE label='aera'` deletes only id `12`, leaving the `æra` row untouched.

This is an optimizer-dependent inconsistency in the reference server, not evidence that fsdb's compatibility implementation should scan every row. It also means an indexed-versus-unindexed differential test is the wrong oracle for this specific case. Applications that need every collation-equivalent row should avoid such prefix indexes or use an explicit scan in MySQL as well.
