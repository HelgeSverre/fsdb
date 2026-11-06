# Hiragana and Katakana exact legacy weights

The maintained ngram audits already cover kana search membership and relevance,
but `WEIGHT_STRING` still returned ICU sort-key bytes for U+3040–U+30FF. A
native MySQL 8.4.11 comparison found all 96 Hiragana singletons differed
under `utf8mb4_unicode_ci` and `utf8mb4_unicode_520_ci`. The ordinary
Katakana letters U+30A1–U+30F6 share the corresponding Hiragana primary
weights; the remaining Katakana punctuation and forms have distinct values.

Fsdb now emits MySQL's exact primary weights for the full 192-code-point
U+3040–U+30FF range under `utf8mb4_general_ci`, `utf8mb4_unicode_ci`,
`utf8mb4_unicode_520_ci`, `utf8mb3_general_ci`, and `utf8mb3_unicode_ci`.
The two Unicode generations have separate tables; the general collations
emit each code point. The tables include unassigned implicit weights,
ignorable combining marks, and two-primary-weight expansions.

The [exact-byte oracle](../scripts/general-ci-range-weight-oracle.py) used a
disposable native MySQL instance and Release fsdb. It found zero mismatches
in each of the five collations for:

| Shape | Comparisons |
| --- | ---: |
| 192 single characters | 960 |
| 191 adjacent pairs within the block | 955 |
| 192 kana-plus-U+3B00 strings | 960 |
| 192 kana-plus-U+3B00 strings as `CHAR(4)` | 960 |
| **Total** | **3,835** |

The focused regression pins a Hiragana pair, script equivalence, an
ignorable mark, the U+309F/U+30A0 boundary, special Katakana values, and
mixed ideograph output. This is exact-weight coverage for the audited block;
other mixed CJK text and MeCab on plugin-enabled servers remain open.
