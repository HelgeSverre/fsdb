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
