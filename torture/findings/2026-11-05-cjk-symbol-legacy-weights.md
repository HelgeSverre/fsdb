# CJK Symbols and Punctuation legacy weights

The earlier [ideographic-space fix](2026-11-05-ideographic-space-legacy-weight.md)
covered U+3000, but the native MySQL 8.4.11 oracle found exact-byte
`WEIGHT_STRING` differences for the other 63 characters in U+3000–U+303F.
The `*_general_ci` collations give each character its literal code-point
weight. The legacy Unicode 4.0 and 5.2 collations use separate primary
weights: U+3006 and U+303C expand to two weights, U+302A–U+302F have no
primary weight, and several iteration marks share weights.

Fsdb's legacy encoder now covers the complete block for `utf8mb4_general_ci`,
`utf8mb4_unicode_ci`, `utf8mb4_unicode_520_ci`, `utf8mb3_general_ci`, and
`utf8mb3_unicode_ci`. The [differential oracle](../scripts/general-ci-range-weight-oracle.py)
compared exact bytes with MySQL for:

| Shape | Comparisons | Mismatches |
| --- | ---: | ---: |
| 64 single characters in five collations | 320 | 0 |
| 64 symbols followed by U+3B00 in five collations | 320 | 0 |
| 63 adjacent symbol pairs in five collations | 315 | 0 |
| 64 symbol-plus-ideograph strings as `CHAR(4)` in three collations | 192 | 0 |

The focused regression pins ordinary punctuation, expansions, ignorable
marks, compatibility weights, and the block's upper boundary. The change is
limited to exact legacy weight output; the ngram posting collation key is
unchanged. Other Unicode blocks and mixed-script weight families remain open.
