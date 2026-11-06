# Bopomofo exact legacy weights

Fsdb already keeps the older Bopomofo U+3100–U+318F code points distinct for
legacy collation equality and ngram postings. Its `WEIGHT_STRING` result for
U+3100–U+312F still came from ICU. A native MySQL 8.4.11 comparison found
all 48 singletons differed under `utf8mb4_unicode_ci` before this change.

Fsdb now emits MySQL's measured UCA 4.0 and 5.2 primary weights for
U+3100–U+312F. Unassigned positions use the legacy two-weight implicit
form; U+312D has an assigned weight only in the 5.2 table. The general
collations emit the written code point, as MySQL does.

The [exact-byte oracle](../scripts/general-ci-range-weight-oracle.py) used
disposable native MySQL 8.4.11 and Release fsdb instances. It found zero
mismatches in each of `utf8mb4_general_ci`, `utf8mb4_unicode_ci`,
`utf8mb4_unicode_520_ci`, `utf8mb3_general_ci`, and `utf8mb3_unicode_ci`:

| Shape | Comparisons |
|---|---:|
| 48 singletons | 240 |
| 47 adjacent pairs within the block | 235 |
| 48 Bopomofo plus U+3B00 strings | 240 |
| 48 Bopomofo plus U+3B00 strings as `CHAR(4)` | 240 |
| **Total** | **955** |

The focused regression pins implicit positions, assigned positions in both
Unicode generations, order exceptions near U+312A, a mixed ideograph, and
general-collation bytes. U+3130–U+318F and other mixed CJK blocks still need
an exact-byte audit.
