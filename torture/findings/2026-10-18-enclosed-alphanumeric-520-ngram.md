# Unicode 5.2 enclosed alphanumeric ngram keys

The maintained [two-column ngram oracle](../scripts/ngram-multicol-oracle.py)
compared printable U+1F100–U+1F1FF characters with native MySQL 8.4.11.
At token size two under `utf8mb4_unicode_520_ci`, fsdb initially differed on
656 membership or relevance results among 1,200 natural, Boolean, and
query-expansion searches. For example, searching for `中🄋` also returned the
document containing `中🄌`; MySQL returned only the matching document.

A native `WEIGHT_STRING` sweep of the whole block found 193 scalars with
distinct UCA 5.2 implicit weights. These are `FBC3` followed by the low
16 bits of the scalar; the other 63 scalars have explicit weights. MySQL
therefore distinguishes `🄋` from `🄌` and `🄰` from `🅰` under Unicode 5.2,
while its Unicode 9.0 collation equates the latter pair. Fsdb now uses a
collision-escaped synthetic key for just the 193 implicit-weight scalars.
The shared supplementary-key encoder also serves the earlier kana fix, so
literal private-use marker text cannot alias the generated keys.

The focused equality, index-key, and full-text regressions cover these
representative pairs. The full range oracle reports zero differences in
1,200 membership and score searches at each ngram startup size from 1
through 10. This establishes the audited search behavior,
not exact `WEIGHT_STRING` bytes or general Unicode 5.2 collation parity.
