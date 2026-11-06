# Plane-two unassigned-tail ngram audit

The plane-two legacy-weight change left the U+2FF00–U+2FFFF ngram tail
unaudited. The pinned MySQL 8.4.11 oracle now compares every scalar in that
256-position range, including positions absent from Python's Unicode database,
against fsdb. Each document contains the scalar between a fixed prefix and
`文`; searches use both unquoted and quoted terms in three full-text modes.

For each of `utf8mb4_general_ci`, `utf8mb4_unicode_ci`, and
`utf8mb4_unicode_520_ci`, all 1,536 searches at each startup token size from
1 through 10 match MySQL in membership and relevance. That is 46,080 search
comparisons with zero differences. The audit changes no production code.
It covers this range and corpus, not MeCab or arbitrary multi-character CJK
queries.

Reproduce after building `src/Fsdb.Cli` in Release:

```sh
uv run --with pymysql torture/scripts/ngram-multicol-oracle.py --legacy-range 2FF00:30000 --all-range-scalars --collation utf8mb4_general_ci 1 2 3 4 5 6 7 8 9 10
uv run --with pymysql torture/scripts/ngram-multicol-oracle.py --legacy-range 2FF00:30000 --all-range-scalars --collation utf8mb4_unicode_ci 1 2 3 4 5 6 7 8 9 10
uv run --with pymysql torture/scripts/ngram-multicol-oracle.py --legacy-range 2FF00:30000 --all-range-scalars --collation utf8mb4_unicode_520_ci 1 2 3 4 5 6 7 8 9 10
```
