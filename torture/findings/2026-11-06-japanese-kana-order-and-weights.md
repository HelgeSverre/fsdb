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
