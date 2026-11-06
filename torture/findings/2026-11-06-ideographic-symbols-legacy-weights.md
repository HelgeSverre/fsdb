# Supplementary ideographic symbols and marks U+16FE0–U+16FFF

A disposable native MySQL 8.4.11 oracle found exact `WEIGHT_STRING`
differences for all 32 scalars in U+16FE0–U+16FFF under three legacy
`utf8mb4` collations. `utf8mb4_general_ci` and `utf8mb4_unicode_ci` give
each the `FFFD` replacement primary. `utf8mb4_unicode_520_ci` gives each
a distinct two-word implicit primary, from `FBC2EFE0` to `FBC2EFFF`.

Fsdb now encodes those weights directly. All 32 singleton and mixed-pair
checks match MySQL in each collation. The Unicode 5.2 ngram key also keeps
these scalars distinct: before the change, a search for U+16FE4 matched
rows containing U+16FF0 and U+16FF1. After the change, the two-column
ngram oracle reports zero membership or relevance differences across 42
queries at each startup token size 1–10 in each of the three collations.
Focused regressions cover the exact block endpoints and distinct Unicode
5.2 postings.

Reproduce after building the Release CLI:

```sh
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 16FE0 17000 --collation utf8mb4_unicode_520_ci --compare-fsdb --pair-with 16FE0
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_unicode_520_ci --legacy-range 16FE0:17000
```

Repeat with `utf8mb4_unicode_ci` and `utf8mb4_general_ci` for the other
two audited lanes. Adjacent supplementary blocks and MeCab remain open.
