# Ideographic Description Characters U+2FF0–U+2FFF

The pinned MySQL 8.4.11 exact-weight oracle found a mismatch at every
position in U+2FF0–U+2FFF. The legacy general collations use the written
code point. `utf8mb4_unicode_ci` and `utf8mb3_unicode_ci` use consecutive
primaries 0x0DAF–0x0DBA for U+2FF0–U+2FFB, while
`utf8mb4_unicode_520_ci` uses 0x10D4–0x10DF. The final four positions
have implicit FBC0 weights in both Unicode collations.

Fsdb now follows those mappings. The native oracle matches all 16
singletons, 16 adjacent pairs (including U+2FFF followed by U+3000),
16 pairs with U+4E00, and 16 `AS CHAR(4)` padded weights under each of
five legacy collations: **0/320 exact-byte mismatches**. The focused
`WEIGHT_STRING` regression pins the first and last assigned primaries,
the implicit tail, and general-collation values.

The maintained two-column ngram oracle also matches MySQL membership and
relevance for 96 searches at each startup size 1–10 under
`utf8mb4_unicode_ci`, `utf8mb4_unicode_520_ci`, and
`utf8mb4_general_ci`: **0/2,880 search mismatches**. MeCab and broader
mixed-script terms remain open.

Run the exact-weight oracle with
`uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py 2FF0 3000 --collation utf8mb4_unicode_ci --compare-fsdb`.
Repeat with `--pair-next`, `--pair-with 4E00`, and `--char-width 4`
for each of the five collations. Run the ngram oracle with
`uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py --legacy-range 2FF0:3000 --collation utf8mb4_unicode_ci`
and repeat under the other two `utf8mb4` collations.
