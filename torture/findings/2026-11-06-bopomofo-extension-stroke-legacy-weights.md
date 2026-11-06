# Bopomofo extensions and CJK strokes: legacy exact weights

A native MySQL 8.4.11 comparison of U+31A0–U+31FF found that all 87
printable scalars in the two-column ngram corpus already matched at token
size 1, while all 87 exact `WEIGHT_STRING` values differed under
`utf8mb4_unicode_520_ci`. A separate exact-weight probe covered all 96 code
points, including unassigned positions, under both legacy Unicode versions
and the general collation.

Fsdb now uses MySQL's UCA 4.0 and 5.2 primary weights for the 24 Bopomofo
extension letters and 16 Katakana phonetic extensions. UCA 5.2 assigns the
first 36 CJK strokes sequential weights; UCA 4.0 and the remaining positions
use the legacy implicit two-weight form. General collations use the written
code point. The SQL comparison and full-text folds are unchanged.

The exact-byte oracle reports zero differences for each of five collations:
`utf8mb4_general_ci`, `utf8mb4_unicode_ci`,
`utf8mb4_unicode_520_ci`, `utf8mb3_general_ci`, and
`utf8mb3_unicode_ci`.

| Shape | Comparisons across five collations |
|---|---:|
| 96 singletons | 480 |
| 96 adjacent pairs | 480 |
| 96 mixed strings with U+3B00 | 480 |
| 96 mixed strings as `CHAR(4)` | 480 |
| **Total** | **1,920** |

The focused regression pins duplicate extension weights, assigned and
implicit stroke boundaries, phonetic extensions, and general-collation bytes.
Run `uv run --with pymysql python
torture/scripts/general-ci-range-weight-oracle.py 31a0 3200 --collation
utf8mb4_unicode_520_ci --compare-fsdb` after building the Release CLI.
Other CJK blocks and mixed-script compositions remain open.

## Adjacent Kanbun block

The adjacent U+3190–U+319F block had 16 exact-weight mismatches under each
legacy Unicode collation. MySQL gives its first two marks different UCA 4.0
and 5.2 weights, and the remaining 14 annotated ideographs share their
measured implicit weights across those versions. Fsdb now emits those
weights while general collations retain the written code points. The same
five-collation oracle compares all 16 singletons, adjacent pairs,
ideograph-mixed strings, and `CHAR(4)` strings: 320 further exact-byte checks.
