# Accent-sensitive CJK wildcard prefixes

In a two-character ngram index using `utf8mb4_0900_as_ci`, MySQL
8.4.11 lets `株*` find both `株中` and `㍿中`, and `하*` find
both `하中` and `한中`. The wildcard can stop within the compatibility
symbol or syllable, between collation weights. The audited matches
persist after a full-text-only `OPTIMIZE TABLE` flush.
[MySQL UCA prefix comparison](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/strings/ctype-uca.cc#L1675-L1717)

Fsdb now builds those partial keys for CJK compatibility symbols, Kana
ligatures, and Hangul syllables. A partial Kana key can end before a
voicing mark. A key containing the mark can require different treatment
for cached and flushed postings. The audited Kana ligatures also share
their expanded spelling's full-text key.

The compatibility-symbol and Hangul sweep went from 183 to 23
membership differences across 292 wildcard searches against the pinned
MySQL image. In-memory and WAL-backed Fsdb agreed. The mixed accent,
case, width, and script sweep covered 938 searches before and after a
flush across seven collations, including a binary control. Its
membership differences fell from 309 to 293.

The [SQL fixture](2026-11-27-ngram-accent-sensitive-prefixes.sql) also
isolates two remaining stage-dependent behaviors:

- `é*` finds the precomposed `é中` in MySQL's cache. After the flush,
  it finds the decomposed `é中` instead. Fsdb finds both in either state.
- `ギ*` finds no row in the cached fixture. After the flush, MySQL finds
  `㌐中`. Fsdb returns no row in both states. The unvoiced `キ*` matches
  `㌐中` in both engines and both states.

MySQL's cache lookup searches a red-black tree using the UCA prefix
comparator and visits the matching neighbors of its first hit. The
persisted lookup selects one auxiliary index partition and applies
`LIKE` to its stored words. [MySQL cached wildcard lookup](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L844-L921) [MySQL persisted lookup](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0opt.cc#L486-L535)
