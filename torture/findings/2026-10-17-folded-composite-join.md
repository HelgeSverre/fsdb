# Folded text equality in composite left-prefix lookups

The imported finding “Composite-prefix joins miss collation-equal rows” is valid. A join can bind the leading `name` part of an index on `(name, sequence_no)`. The old `tryEqualityLookupForMatch` searched secondary-order entries with full collation order, while SQL equality uses primary collation order. Under `utf8mb4_0900_ai_ci`, `foo` and `FOO` compare equal but full-order tie breaks can place them on opposite sides of an exact binary-search boundary.

The pinned MySQL 8.4.11 image (`mysql:8.4.11@sha256:b3b90af2a6552ae30c266fdb7d5dd55f3afb72404bb78d37fe8a23eb857fd3fb`) returned one row for `SELECT COUNT(*) FROM l STRAIGHT_JOIN r FORCE INDEX(ix_name_seq) ON r.name=l.name`, with `l.name='foo'`, `r.name='FOO'`, and `KEY ix_name_seq(name,seq)`. EXPLAIN reported `ref` on that key. A LEFT JOIN likewise returned the matching row rather than a NULL-padded one.

Fsdb now seeks the primary-equivalent class of the first folded text key within any exact leading-key slice. It checks all bound key parts before returning candidates, because later key parts need not be contiguous when the earlier text part has full-order tie variants. Complete and order-equivalent key lookups retain their existing direct path.

The focused regression failed against `origin/main` (no INNER JOIN rows) and passed after the change. A second case checks two bound parts under a descending three-part index, excluding a row with a different integer suffix; the MySQL control returned ids `1,3` through `ref` on that descending index. These fixtures establish the audited join shape; broader cross-collation joins and other index families remain separate work.
