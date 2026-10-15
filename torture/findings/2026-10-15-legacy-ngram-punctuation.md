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
