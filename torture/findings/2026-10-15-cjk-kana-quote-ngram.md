# CJK small kana, quote forms, and Boolean ngram queries

The maintained [MySQL 8.4.11 differential oracle](../scripts/ngram-multicol-oracle.py)
previously combined printable U+FE30–U+FE6F and U+FF01–U+FF65 presentation
forms with ASCII punctuation. Extending the same legacy-collation table with
U+3000–U+303F and U+FF66–U+FF9F exposed 128 membership or relevance
differences among 936 size-two searches. The differences clustered in small
versus regular kana, the three CJK double-quote forms, and two unbalanced
Boolean-parenthesis queries.

MySQL equates each of 24 audited small/full-size hiragana and katakana pairs
under `utf8mb4_unicode_ci`, `utf8mb4_unicode_520_ci`, and the audited
`utf8mb4_0900_ai_ci` and `utf8mb4_0900_as_ci` comparisons. It gives `〝`,
`〞`, and `〟` distinct, ascending primary
weights, separate from the ordinary and fullwidth double quote. FSDB now
folds small kana after width and script normalization and protects those
three quote forms with collision-escaped keys. Original text still determines
ngram token boundaries. MySQL's ngram parser also returns no Boolean matches
for unbalanced `(` or `)`; FSDB now rejects the whole malformed ngram query
while leaving balanced groups to the existing parser. The separate
[ordinary word-parser audit](2026-10-17-word-boolean-group-syntax.md) now raises
MySQL error 1064 for the same unbalanced groups.

The enlarged oracle reports zero membership or relevance differences across
936 queries at **each** configured ngram size from 1 through 10, covering
natural, Boolean, and query-expansion modes. The original default-collation
size-two corpus remains at zero differences across 387 queries. Focused
collation-key/hash, indexed ngram, and malformed-Boolean regressions pin the
new behavior. This does not establish exact MySQL weight bytes, comprehensive
legacy UCA behavior, or MeCab parsing.
