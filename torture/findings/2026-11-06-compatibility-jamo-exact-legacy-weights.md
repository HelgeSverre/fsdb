# Hangul Compatibility Jamo exact legacy weights

The preceding Bopomofo audit covered U+3100–U+312F. U+3130–U+318F is the
adjacent Hangul Compatibility Jamo block. Fsdb's legacy collation equality
already distinguished its written characters, but `WEIGHT_STRING` used ICU
sort-key bytes. A native MySQL 8.4.11 comparison found all 96 singletons
differed under `utf8mb4_unicode_ci` before this change.

Fsdb now emits MySQL's measured UCA 4.0 and 5.2 primary weights for the
94 assigned positions U+3131–U+318E. The unassigned endpoints U+3130 and
U+318F use legacy implicit two-weight values. General collations emit each
written code point.

The [exact-byte oracle](../scripts/general-ci-range-weight-oracle.py) used
disposable native MySQL 8.4.11 and Release fsdb instances. Every comparison
matched in `utf8mb4_general_ci`, `utf8mb4_unicode_ci`,
`utf8mb4_unicode_520_ci`, `utf8mb3_general_ci`, and `utf8mb3_unicode_ci`:

| Shape | Comparisons |
|---|---:|
| 96 singletons | 480 |
| 95 adjacent pairs within the block | 475 |
| 96 Jamo plus U+3B00 strings | 480 |
| 96 Jamo plus U+3B00 strings as `CHAR(4)` | 480 |
| **Total** | **1,915** |

The focused regression pins implicit endpoints, order exceptions, a mixed
ideograph, and both Unicode generations. Other CJK blocks and more complex
mixed-script compositions remain open.
