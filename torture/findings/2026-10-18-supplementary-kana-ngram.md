# Supplementary kana ngram keys across Unicode collations

MySQL 8.4.11 and fsdb differed for the Hiragana and Katakana archaic WU
characters under `utf8mb4_unicode_520_ci`. A two-column ngram index containing
`中𛄟文` (U+1B11F) and `中𛄢文` (U+1B122) returned the matching document alone
in MySQL. Fsdb gave both characters one ICU primary key, so natural-language
and query-expansion searches returned both documents and changed relevance.
The maintained [`ngram-multicol-oracle.py`](../scripts/ngram-multicol-oracle.py)
found 16 membership or relevance differences among 1,746 size-two queries
over printable U+1B000–U+1B12F characters.

A native weight probe found that MySQL's Unicode 5.2 collation gives all 291
printable characters in this span distinct implicit primary weights. Its
`utf8mb4_0900_ai_ci` collation gives the first two explicit weights and the
other 289 distinct implicit weights. In both collations, the archaic WU pair
has weights `FBC3B11F` and `FBC3B122` and compares unequal. By contrast,
`utf8mb4_unicode_ci` and `utf8mb4_general_ci` equate the pair at their shared
supplementary fallback weight `FFFD`.

Fsdb now preserves the implicit-weight characters as distinct canonical keys
for Unicode 5.2 and 9.0. It leaves the first two 9.0 characters to ICU's
explicit weights and escapes the synthetic marker so literal private-use text
cannot alias a generated key. Equality, indexed ngram membership, and score
calculation inherit the same distinction. Focused collation and SQL full-text
regressions pin the archaic WU pair, including `utf8mb4_0900_as_ci`, whose
distinct native weights were checked separately.

The maintained oracle now passes 5,238 queries per collation at token sizes
1, 2, and 3 for `utf8mb4_unicode_ci`, `utf8mb4_unicode_520_ci`, and
`utf8mb4_0900_ai_ci`, with zero membership or relevance differences. A
positive-control assertion confirms that the supplementary-kana corpus
produces actual matches. This is an audited ngram and equality result for
this Unicode span; exact `WEIGHT_STRING` bytes and other untested collations
remain outside the claim.
