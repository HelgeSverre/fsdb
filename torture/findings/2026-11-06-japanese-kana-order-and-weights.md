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
voicing before script. Fsdb now compares the ICU base key first and appends
the kana script as a tie-break. It normalizes halfwidth kana before either
the sort or full-text key. The expanded oracle reports zero ordering,
comparison, indexed-range, equality, search, and relevance differences at
all ten sizes. Focused regressions cover the script and iteration-mark order
plus an indexed range.

`WEIGHT_STRING` remains a separate gap: all 192 singleton byte strings in
this range still differ from MySQL because fsdb emits ICU bytes rather than
MySQL's Japanese UCA 9.0 weight format. The correction is to query ordering
and indexed ranges, not exact serialized weights or MeCab behavior.

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
