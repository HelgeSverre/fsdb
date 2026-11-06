# Supplementary enclosed alphanumeric exact legacy weights

The existing U+1F100–U+1F1FF ngram audit did not cover exact
`WEIGHT_STRING` bytes. Native MySQL 8.4.11 initially differed from fsdb
on all 256 singletons under each of `utf8mb4_unicode_520_ci`,
`utf8mb4_unicode_ci`, and `utf8mb4_general_ci`.

UCA 5.2 expands assigned enclosed digits and letters using its ASCII
primary weights, with a handful of historical abbreviation spellings and
enclosure marks. Other positions use regular implicit weights. The older
Unicode and general collations emit replacement weight `FFFD` throughout
this supplementary range. Fsdb now applies these exact-weight rules
without changing its SQL comparison or full-text folds.

The exact-byte oracle now reports zero differences across the three
collations:

| Shape | Comparisons |
|---|---:|
| 256 singletons | 768 |
| 255 adjacent pairs within the range | 765 |
| 256 strings paired with U+3B00 | 768 |
| 256 such strings as `CHAR(4)` | 768 |
| **Total** | **3,069** |

The focused regression pins digit punctuation, the first and last enclosed
letters, a historical abbreviation, a regional indicator, and the older
collations' replacement weight. The maintained ngram oracle at token size 1
also retains zero search and equality mismatches under
`utf8mb4_unicode_520_ci`.

Run `uv run --with pymysql python
torture/scripts/general-ci-range-weight-oracle.py 1f100 1f200
--collation utf8mb4_unicode_520_ci --compare-fsdb` after building the
Release CLI. Other supplementary ranges and mixed-script compositions
remain open.
