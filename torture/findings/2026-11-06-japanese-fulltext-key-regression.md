# Japanese full-text token keys after kana ordering

The Japanese kana ordering change made `utf8mb4_ja_0900_as_cs_ks` SQL
comparisons follow MySQL, but its katakana marker also reached the full-text
token key. On the maintained two-column CJK corpus, native MySQL 8.4.11 and
fsdb then differed in 56 searches at ngram size 1 and 49 at size 2. Halfwidth
kana queries matched decomposed hiragana documents and changed relevance.

SQL ordering and full-text postings now use separate kana transforms. SQL
comparison retains the base ICU key plus a script tie-break. Full-text tokens
use the earlier hiragana marker, which preserves MySQL's audited width and
script equivalences. A focused size-1 regression checks halfwidth `ｶﾞﾗｽ`
against decomposed hiragana, fullwidth katakana, and halfwidth katakana. It
fails when the full-text branch is removed and passes with the branch present.

After the change, the two-column corpus has zero membership or relevance
differences in 387 searches at each startup size 1–10, or 3,870 searches
total. The U+3040–U+30FF range oracle also has zero search, equality,
ordering, and indexed-range differences at sizes 1 and 2 (1,152 searches and
four equality checks per size). Exact Japanese `WEIGHT_STRING` bytes and
MeCab behavior remain outside this correction.

```sh
dotnet build src/Fsdb.Cli -c Release --nologo
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_ja_0900_as_cs_ks 1 2 3 4 5 6 7 8 9 10
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --legacy-range 3040:3100 --all-range-scalars --collation utf8mb4_ja_0900_as_cs_ks --order-checks 1 2
```
