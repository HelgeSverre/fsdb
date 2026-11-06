# CJK Compatibility Forms U+FE30–U+FE4F exact weights

The pinned MySQL 8.4.11 oracle found 32/32 singleton `WEIGHT_STRING`
differences under each of five legacy collations. The Unicode 4.0 and 5.2
collations assign different primary weights to these forms; U+FE30 expands
to two identical primaries. The two general collations use the literal code
points. Fsdb's previous fallback returned ICU-derived bytes.

Fsdb now emits the MySQL primaries for this range. The Release CLI matches
MySQL in **635/635** checks across `utf8mb4_general_ci`,
`utf8mb3_general_ci`, `utf8mb4_unicode_ci`, `utf8mb3_unicode_ci`, and
`utf8mb4_unicode_520_ci`: 32 singletons, 31 adjacent pairs entirely within
the range, 32 pairs with U+4E00, and 32 `AS CHAR(4)` values per collation.
The adjacent-pair run also included U+FE4F followed by U+FE50; that
out-of-range pair still differs and is excluded from the 635 count.

The focused legacy `WEIGHT_STRING` regression passes representative forms
and their mixed sequence. Exact weights beyond this range and wider CJK
full-text search shapes remain open.
