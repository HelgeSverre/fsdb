# Japanese kana-sensitive order and exact weights

The pinned MySQL 8.4.11 oracle tested all U+3040–U+30FF scalars under
`utf8mb4_ja_0900_as_cs_ks`, including positions absent from Python's
Unicode database. Each document contains a scalar between a fixed prefix
and `文`. At each startup ngram size from 1 through 10, 1,152 searches in
natural, Boolean, and expansion modes match fsdb in membership and relevance;
four equality checks per size also match.

Before the correction, `ORDER BY` displaced 180 documents, the first
mixed-script comparison was reversed, and an indexed range omitted a row.
The collation transform marked hiragana as an extra ICU primary. That put
script before voicing, whereas MySQL's kana-sensitive weights distinguish
voicing before script. The first correction compared the ICU base key
before the kana script and normalized halfwidth kana before building keys.
The expanded oracle reported zero ordering,
comparison, indexed-range, equality, search, and relevance differences at
all ten sizes. Focused regressions cover the script and iteration-mark order
plus an indexed range.

All 192 singleton weight strings still differed at that stage. The
[SQL comparison follow-up](#sql-comparison-and-index-order) now covers
the exact serialized weights for both Japanese collations.

Reproduce after building `src/Fsdb.Cli` in Release:

```sh
uv run --with pymysql torture/scripts/ngram-multicol-oracle.py --legacy-range 3040:3100 --all-range-scalars --collation utf8mb4_ja_0900_as_cs_ks --order-checks 1 2 3 4 5 6 7 8 9 10
uv run --with pymysql torture/scripts/ngram-multicol-oracle.py --legacy-range 3040:3100 --all-range-scalars --collation utf8mb4_ja_0900_as_cs_ks --weight-checks --order-checks 1
```

## Kana-sensitive full-text cursors

With `ア駅`, `あ駅`, and `背景` indexed as bigrams, `ア*`
matches the Katakana row in cache. After full-text-only optimize,
the disk cursor stops at the Hiragana posting and the prefix returns
no rows. The exact search `ア駅` still matches, and removing the
Hiragana row restores the wildcard result. Fsdb now uses MySQL's
four-level Japanese keys for postings and both prefix cursors.
[MySQL Japanese collation scanner](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/strings/ctype-uca.cc#L1390-L1558)

The expanded Japanese bigram corpus went from 6,499 differences to
zero across 26,356 searches in fresh, flushed, mixed, and mixed
postings after deletion. The maintained corpus matched 3,870 queries
at sizes 1–10. The all-Kana corpus matched another 11,520 searches
and all audited order and equality checks at those sizes. A checkpoint
written before the change reopened with the corrected prefix result.

The four-level weights reduced the same corpus with the default word parser
from 1,306 differences to 288. Keeping modified punctuation-split words in
their own Boolean list removed another 114 differences. Retaining deleted
word postings until full-text optimize removed the final 174 score
differences. All 26,356 searches match across cached, flushed, mixed, and
mixed postings after deletion.
[Default UTF-8 word parser](2026-10-16-ngram-boolean-unicode-ctype.md#default-utf-8-word-parser).
The same corpus also matches under `utf8mb4_bin` and `utf8mb4_0900_ai_ci`.

An isolated MySQL 8.4.11 fixture confirms that the word parser counts
retained postings in modified and compound Boolean queries. With seven
live rows and one deleted `東京駅` posting, `東京駅` weighs three live
postings, while `+東京駅` and `東京駅 never` weigh four. The same fixture
has four live `東*` matches and two retained postings, one cached and
one flushed. `東*` weighs four postings and `+東*` weighs six.
[MySQL single-term ranking](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L3119-L3169).

The smaller fixture matched in Boolean, natural-language, and query-expansion
modes across two deletion stages and full-text optimize. A durable regression
checks that checkpoint loading and WAL replay preserve the same retained
frequency through automatic cache sync.

The embedded weight table comes from pinned MySQL 8.4.11. Its
generator captures every Unicode scalar and 1,160 Kana context pairs
for iteration and length marks. It stores the 586 pairs whose weights
differ from singleton composition, and repacking the same dump
reproduces the asset byte for byte.

```sh
ruby torture/scripts/generate-zh-0900-weights.rb --japanese queries | mysql -NBr > japanese-weights.tsv
ruby torture/scripts/generate-zh-0900-weights.rb --japanese pack japanese-weights.tsv src/Fsdb/Sql/Japanese0900Weights.bin.gz
```

## Three-level Japanese wildcards

The three-level `utf8mb4_ja_0900_as_cs` collation had a separate bigram
prefix difference against pinned MySQL 8.4.11. In the following fixture,
`か*`, `カ*`, and `ｶ*` each return IDs 1 through 9 and 12. Fsdb omitted
IDs 2 and 8, which start with precomposed voiced Kana. With `が*` or
`ガ*`, MySQL returns IDs 2 and 8, while Fsdb also returned the decomposed
form in row 3. The exact `かな` query returns IDs 1, 4, 5, and 6 on both
engines.

```sql
CREATE TABLE kana_prefix (
  id INT PRIMARY KEY,
  body TEXT COLLATE utf8mb4_ja_0900_as_cs,
  FULLTEXT KEY ft(body) WITH PARSER ngram
);
INSERT INTO kana_prefix VALUES
  (1,'かな'), (2,'がな'), (3,CONVERT(0xE3818BE38299E381AA USING utf8mb4)),
  (4,'ｶﾅ'), (5,'カナ'), (6,'かナ'), (7,'ｶﾞﾅ'), (8,'ガナ'),
  (9,'かた'), (10,'漢字'), (11,'東京'), (12,'カル');
SELECT id FROM kana_prefix
WHERE MATCH(body) AGAINST ('か*' IN BOOLEAN MODE) ORDER BY id;
SELECT id FROM kana_prefix
WHERE MATCH(body) AGAINST ('が*' IN BOOLEAN MODE) ORDER BY id;
```

In a separate weight comparison, the first three levels of
`utf8mb4_ja_0900_as_cs_ks` matched `utf8mb4_ja_0900_as_cs` byte for byte
on 734 single characters, 1,624 Kana-plus-mark pairs, and 300 seeded
mixed strings. Fsdb now builds full-text keys from those three levels
and uses them in the ordered cache and disk prefix cursors.

The 12-row fixture and its mutations match MySQL membership across all
70 Boolean queries in five phases. With `が*`, the result changes from
IDs 2 and 8 in cache to ID 3 after flush, then to IDs 3 and 13 after new
inserts. After the deletion and final optimize, IDs 3 and 15 match.
Checkpoints written by the previous implementation reopen with corrected
results for cached postings, flushed postings, and mixed postings after
deletion. The seeded multi-column corpus matched MySQL membership and
scores across 212 bigram searches and 266 default-parser searches,
including edits and deletes.

On the first 70-query replay, scores agreed through deletion. The last
optimize left 13 live rows, and MySQL's exact `かな` query scored as if
there were 12. Repeating with a statistics read after the first match
showed MySQL's estimates diverging from `COUNT(*)` after optimize. Fsdb
uses the live count, so score differences from estimated document counts
remain open. [MySQL document-count lookup](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L3668-L3672)

An expanded corpus combined archaic, voiced, halfwidth, and enclosed Kana
with iteration marks and long vowels. Four Unicode 9.0 collations were
compared in fresh, flushed, mixed, and post-deletion posting states. After
`ANALYZE TABLE` aligned the live counts and estimates, all 3,584 bigram
and 3,584 trigram comparisons agreed on membership. The largest score
difference was below `1e-6`. A separate 2,688-query expansion comparison
also matched membership, with score differences below `2e-6`.

## SQL comparison and index order

Both Japanese UCA 9.0 collations now use the pinned weights for SQL
equality, grouping, ordering, index keys, and `WEIGHT_STRING`. For
example, `神` and `神` compare differently in MySQL. Fsdb previously
grouped them together under `utf8mb4_ja_0900_as_cs`. The leading mark in
`ゝが` receives context from the final Kana on later weight levels because
MySQL's scanner retains the previous character between levels. [MySQL Japanese weight scanner](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/strings/ctype-uca.cc#L1480-L1542)

The pinned MySQL replay compared 294 statements across two corpora.
Both collations matched all 855 weight strings in the wider corpus,
including the 192 scalars from U+3040 through U+30FF. Equality joins,
grouping, plain and forced-index ordering, bounded ranges, and correlated
`ORDER BY ... LIMIT` queries matched. Checkpoint and WAL data written
by the previous build reopened with the corrected range and equality
results, and the new primary-key insert distinguished `神` from `神`.
The native MySQL 8.4.11 replay also matched all 11,520 Kana ngram
searches, equality and ordering checks, and exact singleton weights
across token sizes 1–10.

One `LIKE` difference depended on MySQL's plan. Under
`utf8mb4_ja_0900_as_cs_ks`, a scalar comparison and a table scan both
match bare `ぁ` against `ァ%`. A covering range scan starts at `ァ`
and omits that row. Fsdb's result agrees with the table scan.
