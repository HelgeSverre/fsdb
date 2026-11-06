# Nüshu U+1B170–U+1B2FF exact legacy weights

The [Nüshu ngram audit](2026-10-18-nushu-ngram-audit.md) left exact
legacy `WEIGHT_STRING` bytes open. A disposable native MySQL 8.4.11 oracle
found differences for every one of the 400 positions in the
[Nüshu block](https://www.unicode.org/versions/Unicode18.0.0/core-spec/chapter-18/).

MySQL gives each position the `FFFD` replacement primary under
`utf8mb4_general_ci` and `utf8mb4_unicode_ci`. Under
`utf8mb4_unicode_520_ci`, the positions have distinct implicit two-word
primaries from `FBC3B170` to `FBC3B2FF`. Fsdb now uses the same encoder
rule already established for the later ideographic and Tangut blocks.

All 400 singleton and 399 internal adjacent-pair weights now match MySQL
in each of the three collations: 2,397 exact-byte comparisons. A focused
regression covers the block endpoints and a mixed ASCII suffix.

The full-text key path did not change. The earlier general-collation
ngram corpus matched at token sizes 1–3 and the Unicode 5.2 corpus matched
at size 1. This follow-up additionally found zero membership or relevance
differences across 2,376 searches per size at Unicode 5.2 sizes 2–3 and
Unicode 4.0 sizes 1–3. MeCab, larger token sizes, standalone equality,
and other query shapes remain open.

Reproduce after building the Release CLI:

```sh
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 1B170 1B300 --collation utf8mb4_unicode_520_ci --compare-fsdb
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 1B170 1B2FF --collation utf8mb4_unicode_520_ci --compare-fsdb --pair-next
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --legacy-range 1B170:1B300 --collation utf8mb4_unicode_520_ci 2 3
```

Repeat the weight commands under `utf8mb4_general_ci` and
`utf8mb4_unicode_ci` for the other exact-byte lanes.
