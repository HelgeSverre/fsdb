# `utf8mb4_general_ci` Jamo weights and ngram keys

Native MySQL 8.4.11 keeps conjoining Hangul Jamo distinct from composed
syllables under `utf8mb4_general_ci`. All 256 U+1100–U+11FF characters in
the sampled weight probe had distinct keys. None of the 146 compatibility or
halfwidth Jamo whose NFKD decomposition is entirely conjoining Jamo compared
equal to that decomposition. For example, MySQL reports `하 != 하` and
`하 != ㅎㅏ`. The older `utf8mb4_unicode_ci` and
`utf8mb4_unicode_520_ci` collations also distinguish `하` from `하`, but
equate the compatibility Jamo sequence with `하`.

Fsdb's ICU-backed `general_ci` previously composed the conjoining sequence
into the syllable. A regression asserting MySQL's distinction failed before
the change. The shared Jamo-folding routine now escapes and keys the
conjoining characters while allowing each collation to choose its own
compatibility-Jamo rule. A size-5 Boolean ngram no longer lets the short
composed term `하늘` match a five-Jamo posting.

Build the CLI in Release and run the two-column comparison against disposable
native MySQL and fsdb servers:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

The corpus has 387 searches per token size. Mismatches count membership and
relevance separately; the counts before and after this change are:

| Token size | Before | After |
| ---: | ---: | ---: |
| 1 | 340 | 313 |
| 2 | 166 | 143 |
| 3 | 96 | 86 |
| 4 | 23 | 23 |
| 5 | 20 | 18 |
| 6 | 3 | 3 |
| 7 | 0 | 0 |
| 8 | 3 | 3 |
| 9 | 0 | 0 |
| 10 | 0 | 0 |

At size 5, Boolean `하늘` membership and relevance change from mismatch to
match. No newly mismatching query category appears at any token size. Other
`utf8mb4_general_ci` collation and full-text differences remain.
