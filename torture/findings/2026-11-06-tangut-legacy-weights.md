# Tangut U+17000–U+187FF exact legacy weights

The prior [Tangut ngram audit](2026-10-20-tangut-ngram-audit.md) found no
membership or relevance differences in its sampled searches, but left exact
legacy `WEIGHT_STRING` bytes open. A disposable native MySQL 8.4.11 oracle
found that the fsdb byte encoder differed for every scalar in the full
6,144-position Tangut block.

MySQL's `utf8mb4_general_ci` and `utf8mb4_unicode_ci` tables give every
position the `FFFD` replacement primary. `utf8mb4_unicode_520_ci` gives
each a distinct implicit two-word primary, from `FBC2F000` at U+17000 to
`FBC387FF` at U+187FF. Fsdb now encodes that rule directly, sharing the
adjacent U+16FE0–U+16FFF rule where it is identical.

The full native comparison now matches all 6,144 singleton weights and all
6,143 internal adjacent pairs under each of the three collations: 36,861
exact-byte comparisons with no mismatch. A focused regression also covers
the U+16FFF/U+17000 mixed boundary. The ngram key path did not change; the
prior sampled Tangut ngram evidence remains applicable. Other token sizes,
query shapes, adjacent Tangut blocks, and MeCab remain open.

Reproduce after building the Release CLI:

```sh
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 17000 18800 --collation utf8mb4_unicode_520_ci --compare-fsdb
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 17000 187FF --collation utf8mb4_unicode_520_ci --compare-fsdb --pair-next
```

Repeat with `utf8mb4_unicode_ci` and `utf8mb4_general_ci` to cover the
other two legacy collations.
