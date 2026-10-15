# Legacy Unicode ideographic-comma ngrams

Native MySQL 8.4.11 gives the vertical ideographic comma `︑` (U+FE11) a
distinct primary weight under `utf8mb4_unicode_ci`. The ordinary `、`
(U+3001) and small `﹑` (U+FE51) forms compare equal. The newer
`utf8mb4_unicode_520_ci` and `utf8mb4_0900_ai_ci` collations equate the
ordinary and vertical forms. The original host ICU fold equated all three
under the legacy collation, so an indexed ngram search returned an extra row.

The native oracle used a disposable MySQL database and this table at the
default ngram token size of two:

```sql
CREATE TABLE docs (
  id INT PRIMARY KEY,
  body TEXT COLLATE utf8mb4_unicode_ci,
  FULLTEXT KEY ft(body) WITH PARSER ngram
);
INSERT INTO docs VALUES (1,'中、文'), (2,'中︑文'), (3,'中﹑文');
SELECT id FROM docs
WHERE MATCH(body) AGAINST('中、' IN NATURAL LANGUAGE MODE) ORDER BY id;
SELECT id FROM docs
WHERE MATCH(body) AGAINST('中︑' IN NATURAL LANGUAGE MODE) ORDER BY id;
```

MySQL returns IDs `1, 3` for the first search and `2` for the second.
`WEIGHT_STRING` returns `0237` for `、` and `﹑`, and `FBC1FE11` for `︑`.
The focused fsdb regression initially returned `1, 2, 3` for the first
search. Collation folding now keeps U+FE11 separate for the legacy collation,
while escaping the synthetic marker so literal private-use text cannot
collide with it. Equality, canonical keys, hashes, and ngram postings share
that distinction. The focused equality and SQL ngram regressions pass.

This establishes the audited equality and membership behavior, not exact
MySQL weight bytes, all legacy UCA 4.0 comparisons, or MeCab support.

## Vertical punctuation block follow-up

A new disposable native MySQL 8.4.11 probe inspected every character from
U+FE10 through U+FE19 under `utf8mb4_unicode_ci`. MySQL assigns distinct,
ascending implicit weights `FBC1FE10` through `FBC1FE19`. In particular,
U+FE12 (`︒`) does not equal the ordinary ideographic period U+3002, whereas
the host ICU collation previously equated them. Indexed ngram searches over
`中。文` and `中︒文` returned only the matching row for `中。` and `中︒`,
respectively. The same probe separately confirmed the existing comma split.

The legacy fold now escapes all ten vertical forms into distinct private-use
markers before asking ICU for a key. It preserves U+FE11's earlier marker,
escapes literal marker text, and orders the markers in MySQL's audited
vertical-block order. Focused equality, key, ordering, and indexed ngram
regressions cover the change. The audited behavior is limited to this block;
it does not claim exact `WEIGHT_STRING` bytes or comprehensive UCA 4.0 parity.

## ASCII, fullwidth, and small comma equality

A further native MySQL 8.4.11 probe found that `,` (U+002C), `，` (U+FF0C),
and `﹐` (U+FE50) compare equal under
`utf8mb4_unicode_ci`, `utf8mb4_unicode_520_ci`, `utf8mb4_0900_ai_ci`, and
`utf8mb4_0900_as_ci`. Under `utf8mb4_0900_as_cs`, ASCII comma stays distinct
from the other two. Host ICU previously separated ASCII from fullwidth comma;
FSDB's small-comma fold inherited that split. The affected insensitive
collations now normalize all three to one key, while the sensitive collation
keeps its existing distinction.

This equality does not erase parser boundaries. In a native MySQL ngram table
with `TEXT COLLATE utf8mb4_unicode_ci`, rows `中,文`, `中，文`, and `中﹐文`
respond differently: searching `中,` finds no row, while `中，` or `中﹐`
finds the latter two rows. The focused FSDB regression pins this distinction
as well as the collation equality, canonical keys, and hashes. Snapshot
recovery rebuilds full-text postings from saved documents under the current
collation rules; the preview format does not persist canonical posting keys.

## Combined width-form corpus

Isolated presentation-form blocks masked cross-block ngram equivalences. The
maintained multi-column oracle now has a `--legacy-punctuation` lane that puts
printable U+FE30–U+FE6F and U+FF01–U+FF65 forms, plus ASCII punctuation, in one
`utf8mb4_unicode_ci` table and searches every two-character `中` prefix at
ngram size two. On native MySQL 8.4.11, `中︵`, `中﹙`, and `中（` find the
same three rows; `中﹡` and `中＊` find the same two. Before the change, fsdb
separated these keys and the combined corpus had 108 membership or relevance
differences among 159 searches. Earlier isolated sweeps of each block had
found none because they could not expose matches across blocks.

The legacy collation now applies compatibility width folding to these
presentation ranges when forming comparison and posting keys. It leaves the
original characters for ngram token boundaries, so the ASCII comma behavior
above is unchanged. The combined oracle passed 573 searches per token size for
those ranges. The [expanded CJK follow-up](2026-10-15-cjk-kana-quote-ngram.md)
extends the same lane to 936 searches per size, still with zero differences
across natural, Boolean, and query-expansion modes at startup sizes 1 through
10. Its representative parenthesis
search asserts that cross-block matches actually occur at every size. The
existing default-collation 387-query size-two corpus also remains at zero.
Focused equality/key/hash and indexed ngram regressions pin representative
cross-block matches. This does not establish exact legacy weight bytes or
MeCab behavior.
