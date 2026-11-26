# Boolean relevance for a shared exact and wildcard ngram key

Under `utf8mb4_0900_ai_ci`, the bigram U+034F followed by `快` has the
same collation key as the quoted term `"快"` and the prefix in `快*`.
MySQL 8.4.11 accumulates document frequency under that shared key and
uses the first posting read for each document as its term frequency.
The exact and prefix lookups still determine query membership using
their respective posting sets. Fsdb previously scored the two lookups
independently.
[MySQL wildcard lookup](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L853-L895)
and [per-document frequency](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L2808-L2820).

In the [SQL fixture](2026-11-26-ngram-shared-relevance.sql), the first row
contains one exact occurrence and three occurrences of the first gram
visited by the wildcard. Its score is 0.004482 for `"快" +快*` and
0.013446 for `快* "快"`, rounded to six decimals. The two servers agree
on both result rows, including the independent exact and prefix scores
and the optional prefix that follows a phrase.

The [8,787-query Boolean bigram sweep](2026-11-26-fulltext-nul-query.md)
now agrees on every result against the [pinned MySQL 8.4.11 image](../config/toolchain.json).
This closes the 42 relevance differences from the previous commit across
`utf8mb4_0900_ai_ci` and `utf8mb4_unicode_520_ci`. All binary control
queries still agree. A further 228 comparisons of overlapping terms
across the same three collations matched with cached postings, after a
full-text-only optimize, after inserting rows into the cache again, and
after deleting rows with postings from both sources.

The [maintained multicolumn comparison](../scripts/ngram-multicol-oracle.py)
also matched all 3,870 searches across startup ngram sizes 1–10 with
native MySQL 8.4.11. The ngram compatibility contract passed all 283 steps
against the pinned image.
