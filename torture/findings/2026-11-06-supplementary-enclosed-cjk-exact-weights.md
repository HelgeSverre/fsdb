# Supplementary enclosed CJK exact legacy weights

The prior U+1F200–U+1F2FF audit covered SQL equality and two-column ngram
searches, but not exact `WEIGHT_STRING` bytes. A native MySQL 8.4.11 probe
found 256 singleton mismatches under each of `utf8mb4_unicode_520_ci`,
`utf8mb4_unicode_ci`, and `utf8mb4_general_ci`.

MySQL's UCA 5.2 weights split this range into assigned enclosed kana and
ideographs, nine parenthesized ideographs with opening and closing weights,
and regular implicit two-weight scalars. The older Unicode and general
collations emit replacement weight `FFFD` for the range. Fsdb now follows
those exact rules in `WEIGHT_STRING`; its SQL comparison and full-text folds
remain separate.

The exact-byte oracle now reports zero differences across all three
collations:

| Shape | Comparisons |
|---|---:|
| 256 singletons | 768 |
| 255 adjacent pairs within the range | 765 |
| 256 strings paired with U+3B00 | 768 |
| 256 such strings as `CHAR(4)` | 768 |
| **Total** | **3,069** |

The focused regression pins the kana expansion, an assigned ideograph,
an unassigned position, a parenthesized ideograph, and the older-collation
replacement weight. The maintained two-column ngram oracle at token size 1
also remains at zero mismatches under `utf8mb4_unicode_520_ci`.

Run `uv run --with pymysql python
torture/scripts/general-ci-range-weight-oracle.py 1f200 1f300
--collation utf8mb4_unicode_520_ci --compare-fsdb` after building the
Release CLI. Other supplementary ranges and mixed-script compositions
remain open.
