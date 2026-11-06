# Supplementary kana U+1B000–U+1B16F legacy weights

The existing supplementary-kana audit covered equality and ngram postings,
but exact legacy `WEIGHT_STRING` bytes were still wrong across this block.
Before the fix, all 368 singletons differed from native MySQL 8.4.11 in each
of `utf8mb4_general_ci`, `utf8mb4_unicode_ci`, and
`utf8mb4_unicode_520_ci`.

MySQL gives every position the replacement primary `FFFD` in the first two
collations. Unicode 5.2 gives each position a distinct implicit primary from
`FBC3B000` through `FBC3B16F`. The existing Nüshu rule starts with
`FBC3B170`, so the implementation now handles the contiguous
U+1B000–U+1B2FF range with one rule. A focused regression pins both kana
endpoints, their boundary with Nüshu, and the three collations. It failed
before the change and passes after it.

The native oracle now finds zero mismatches in all 368 singleton and 368
adjacent or boundary pairs per collation: 2,208 exact-byte comparisons.
The two-column ngram corpus matches MySQL in 1,800 searches at each size
1–3 for each of the three collations: 16,200 searches with zero membership
or relevance differences. The full-text key path did not change.

Reproduce after building the Release CLI:

```sh
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 1B000 1B170 --collation utf8mb4_unicode_520_ci --compare-fsdb
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 1B000 1B170 --collation utf8mb4_unicode_520_ci --compare-fsdb --pair-next
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --legacy-range 1B000:1B170 --collation utf8mb4_unicode_520_ci 1 2 3
```

Repeat for `utf8mb4_general_ci` and `utf8mb4_unicode_ci` to cover all
three exact-byte and ngram lanes. Other token sizes, MeCab, and mixed-script
search shapes remain open.
