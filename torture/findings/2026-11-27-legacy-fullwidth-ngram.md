# Fullwidth ASCII in legacy Unicode ngram indexes

MySQL 8.4.11 uses the same posting key for `0中` and `０中` under
`utf8mb4_unicode_520_ci` and `utf8mb3_unicode_ci`. With both forms in a
two-column ngram index, a natural-language search for `0中` returns both
rows. Fsdb previously returned only the ASCII row.

Fsdb now folds fullwidth ASCII when it constructs the legacy Unicode
posting key. The ngram parser and stopword filter still see the original
characters. In the fixture with default stopwords, `a中` in Boolean mode
has no matches, while `Ａ中` finds the fullwidth and accented rows. A
natural-language search can find `＠中` because the fullwidth at sign is
part of the indexed gram. The ASCII `@` remains a delimiter.

The [SQL fixture](2026-11-27-legacy-fullwidth-ngram.sql) compares 19 results
with ngram size 2 and default stopwords. In-memory and durable Fsdb matched
MySQL. A second audit compared postings for all 94 fullwidth ASCII pairs
with stopwords disabled. Each affected collation now matches MySQL on all
63 reachable width aliases, up from zero. The case-sensitive, binary, and
general collation controls still match MySQL's separate posting keys.
Across ngram sizes 1–10, another 1,560 searches matched MySQL membership
and ranking in natural-language, Boolean, and query-expansion modes.
The same posting-key code also matched MySQL in 756 word-parser searches.

A durable database created by `e5f87aac` returned only the ASCII row before
the upgrade. After reopening it with the fix, the 19 fixture results matched
MySQL, including after a fullwidth row was inserted and deleted before
the upgrade. An existing `utf8mb3_unicode_ci` unique index containing both
widths continued to find each row by its original key.

Ordinary `utf8mb3_unicode_ci` equality
still differs for 93 of the 94 pairs. Changing SQL keys can make existing
unique indexes ambiguous when both widths were accepted as distinct.
The [collation prefix follow-up](2026-11-27-ngram-collation-prefixes.md)
covers searches such as `a*` against `æ中` under `utf8mb4_unicode_520_ci`
and `utf8mb4_0900_ai_ci`.

See the [MySQL ngram parser](https://dev.mysql.com/doc/refman/8.4/en/fulltext-search-ngram.html)
and [InnoDB stopword handling](https://dev.mysql.com/doc/refman/8.4/en/fulltext-stopwords.html).
