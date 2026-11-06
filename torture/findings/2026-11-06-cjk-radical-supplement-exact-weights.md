# CJK Radicals Supplement U+2E80–U+2EFF exact legacy weights

The pinned MySQL 8.4.11 oracle found 128 singleton `WEIGHT_STRING`
differences in each of five legacy collations. The general collations return
the written code point. The legacy Unicode collations use a shared UCA 4.0
and 5.2 primary-weight table through U+2EF3, including some equal-weight
radical variants. Most assigned characters in this block have no Unicode
compatibility decomposition, so NFKC cannot supply those weights. The
unassigned U+2EF4–U+2EFF tail uses its own implicit weights.

Fsdb now uses the MySQL-derived compact table for U+2E80–U+2EF3, an
implicit calculation for the tail, and the written code point for general
collations. The native oracle matches all 128 singletons, 128 adjacent
pairs (including U+2EFF followed by U+2F00), 128 pairs with U+4E00, and
128 `AS CHAR(4)` padded weights in each of `utf8mb4_general_ci`,
`utf8mb4_unicode_ci`, `utf8mb4_unicode_520_ci`, `utf8mb3_general_ci`,
and `utf8mb3_unicode_ci`: **0/2,560 exact-byte mismatches**.

The focused `WEIGHT_STRING` regression covers assigned, repeated-weight,
and unassigned positions. The existing size-1 radical ngram corpus still
matches MySQL across 1,635 searches. The weight oracle's
`--emit-fsharp-words` option captures the four-byte source table from the
pinned MySQL version; the byte comparison is independent of that emission.

Run `uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py
2E80 2F00 --collation utf8mb4_unicode_ci --compare-fsdb`, then use
`--pair-next`, `--pair-with 4E00`, and `--char-width 4` for the other lanes.
Mixed-script combinations beyond the audited pair and MeCab remain open.
