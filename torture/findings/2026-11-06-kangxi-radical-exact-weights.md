# Kangxi Radicals U+2F00–U+2FEF exact legacy weights

The pinned MySQL 8.4.11 oracle found 224 singleton `WEIGHT_STRING`
differences through U+2FDF in each of five legacy collations. Fsdb had
fallen back to ICU sort-key bytes for the general collations and to the
written code point's implicit weight for the Unicode collations.

MySQL gives the assigned Kangxi radicals U+2F00–U+2FD5 the implicit weight
of their NFKC ideograph. The unassigned U+2FD6–U+2FEF tail retains its own
implicit weight under the Unicode collations. The general collations use the
written code point throughout. Fsdb's exact-weight path now follows those
rules without changing its equality or ngram key paths.

The native oracle matches fsdb for all 240 singletons, 239 within-block
adjacent pairs, 240 pairs with U+4E00, and 240 `AS CHAR(4)` padded weights
in each of `utf8mb4_general_ci`, `utf8mb4_unicode_ci`,
`utf8mb4_unicode_520_ci`, `utf8mb3_general_ci`, and
`utf8mb3_unicode_ci`: **0/4,795 exact-byte mismatches**. The focused
`WEIGHT_STRING` regression passes assigned and unassigned boundaries. The
existing size-1 radical ngram corpus still matches MySQL across 1,635
queries.

Use `uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py
2F00 2FF0 --collation utf8mb4_unicode_ci --compare-fsdb` for the singleton
control; `--pair-next` with stop `2FEF`, `--pair-with 4E00`, and
`--char-width 4` cover the other byte lanes. Broader mixed-script sequences
and other CJK blocks remain open.
