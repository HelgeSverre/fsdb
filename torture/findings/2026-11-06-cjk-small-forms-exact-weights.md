# CJK Small Form Variants U+FE50–U+FE6F exact weights

The pinned MySQL 8.4.11 oracle found exact `WEIGHT_STRING` differences for
all 32 characters in U+FE50–U+FE6F under each of five legacy collations.
The two general collations use literal code points. The Unicode 4.0 and
5.2 collations have distinct primary tables, with six positions using
implicit weights in both versions.

Fsdb now emits those primaries and implicit weights. The Release CLI matches
MySQL in **635/635** in-range checks across `utf8mb4_general_ci`,
`utf8mb3_general_ci`, `utf8mb4_unicode_ci`, `utf8mb3_unicode_ci`, and
`utf8mb4_unicode_520_ci`: 32 singletons, 31 adjacent pairs, 32 pairs with
U+4E00, and 32 `AS CHAR(4)` values per collation. The previously failing
U+FE4F followed by U+FE50 boundary pair now matches as well. The focused
legacy `WEIGHT_STRING` regression passes a mixed sequence spanning both
blocks and the implicit positions.

Other CJK presentation-form blocks and broader full-text query shapes
remain open.
