# Accent-sensitive CJK wildcard prefixes

In a two-character ngram index using `utf8mb4_0900_as_ci`, MySQL
8.4.11 lets `株*` find both `株中` and `㍿中`, and `하*` find
both `하中` and `한中`. The wildcard can stop within the compatibility
symbol or syllable, between collation weights. The audited matches
persist after a full-text-only `OPTIMIZE TABLE` flush.

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

## Cached prefix search

For cached postings, Fsdb starts at the first matching
cache tree entry and walks its neighbors in each direction. A
nonmatching entry ends that side of the search. The flat Boolean
scorer uses the same posting lookup as grouped and weighted
expressions.
[MySQL cached wildcard traversal](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L915-L994)

The 143-query cached bigram sweep went from 131 to 143 matches in
membership and score. The mixed-script, two-column bigram matrix
included deletions and required, excluded, weighted, and repeated
terms. Accent-sensitive membership improved from 270 to 286 of
290 queries, and scores within tolerance improved from 218 to 282.
The two comparison collations matched all 580 queries. The same
870-query matrix matched MySQL at token size 3 across the three
collations. Both fsdb storage modes agreed on the bigram results.
All 870 cached bigram results reproduced after an interrupted server
replayed its WAL. An orderly shutdown flushes the cache and exercises
the separate flushed lookup.

## Flushed postings

The [SQL fixture](2026-11-27-ngram-accent-sensitive-prefixes.sql)
isolates two changes in membership at the flush boundary. Fsdb
matches both states.

- `é*` finds the precomposed `é中` in MySQL's cache. After the flush,
  it finds the decomposed `é中`.
- `ギ*` finds no row in the cached fixture. After the flush, MySQL finds
  `㌐中`. The unvoiced `キ*` matches `㌐中` in both states.

The persisted cursor seeks to the query's full key in the selected
auxiliary partition. It scans forward while the query's primary and
secondary weights both match prefixes of the stored word's levels.
For `é*`, the exact decomposed gram sorts first. The plain `e中`
gram then ends the scan before it reaches `é中`. Fsdb merges the
separate cache and flushed results and keeps the first term
frequency per row. It derives the ordered terms from the stored
postings on recovery. [MySQL persisted lookup](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0opt.cc#L486-L535) [MySQL UCA level comparison](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/strings/ctype-uca.cc#L1789-L1844)

| Bigram probes | Queries | Row matches before → after | Row and score matches before → after |
| --- | ---: | ---: | ---: |
| Flushed wildcard sweep | 143 | 128 → 143 | 127 → 142 |
| Mixed wildcard sweep | 143 | 128 → 143 | 126 → 139 |
| Flushed two-column matrix | 348 | 291 → 348 | 234 → 348 |
| Mixed two-column matrix | 348 | 291 → 344 | 234 → 338 |

The two-column matrix includes deletes and solo, required, excluded,
weighted, repeated, and phrase queries. It ran `ANALYZE TABLE` at
each phase so MySQL's row-count estimates reflected the small test
tables. Without that refresh, MySQL scores also differed in the
accent-insensitive and binary controls. At that stage, the mixed
accent-sensitive differences involved cache lookups for `花*`
and `é*`. For `花*`, the cache and auxiliary table both contain
grams with leading combining marks whose primary weights match
`花` and whose secondary weights differ. Those grams sort before
`花中` and `花花`. Replaying the oracle's cached grams in document
order with their exact collation keys reproduced MySQL's cache hits
for both remaining prefixes. All 2,088 two-column trigram queries
matched in rows and score across the three collations after
refreshing statistics.
Both Fsdb storage modes agreed on the bigram matrix, and all 1,044
mixed queries reproduced after WAL replay. An orderly shutdown
and snapshot reload reproduced the explicit-flush results for
`é*` and `ギ*` in a separate mixed fixture with no deletes.

## Exact cache term order

Fsdb now uses the pinned collation keys when it groups and inserts
grams. The red-black tree uses the same weight comparison as the
flushed prefix cursor. The `cache_order` fixture returns no rows
for `花*` in either engine. InnoDB inserts a document's grams in
collation order, so different weights can change the tree path even
when two cursors agree about individual spelling equivalences.
[MySQL cache insertion](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0fts.cc#L1189-L1217)

The cached accent-sensitive matrix now matches on rows and scores
for all 290 searches, up from 282. The mixed accent-sensitive
matrix matches on both for all 348 searches, up from 338. All 348
flushed searches still match. An additional 860 natural-language
and Boolean searches with the ngram and default parsers agreed
with MySQL on rows and scores after `ANALYZE TABLE`.

A checkpoint created before the collation change returned a
false hit for `花*`. On reload, the newer version rebuilt the
cache from document order and retained deletes. Its results agreed
with a freshly built index and a checkpoint written by the newer
version. All 1,914 cached and mixed bigram results reproduced after
WAL replay.

These commands regenerate the embedded weight table using an isolated,
running MySQL container from `torture/compose.yaml`.

```sh
ruby torture/scripts/generate-zh-0900-weights.rb --accent-sensitive queries |
  docker compose -p fsdb-accent-weights -f torture/compose.yaml exec -T mysql \
  sh -c 'MYSQL_PWD="$MYSQL_ROOT_PASSWORD" mysql -uroot -NB' > /tmp/fsdb-accent-weights.tsv
ruby torture/scripts/generate-zh-0900-weights.rb --accent-sensitive pack \
  /tmp/fsdb-accent-weights.tsv src/Fsdb/Sql/AccentSensitive0900Weights.bin.gz
```
