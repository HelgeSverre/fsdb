# Ideographic space in legacy collation weights

MySQL 8.4.11 assigns U+3000 an exact `WEIGHT_STRING` primary weight of
`3000` under `utf8mb4_general_ci` and `utf8mb3_general_ci`, `0209` under
`utf8mb4_unicode_ci` and `utf8mb3_unicode_ci`, and `020A` under
`utf8mb4_unicode_520_ci`. Fsdb previously fell back to ICU bytes (`0400`
for the singleton under `unicode_ci`), so composed CJK strings also differed.

The legacy encoder now emits the MySQL weight when U+3000 appears alongside
the already supported ideographs and ASCII. The [native differential
oracle](../scripts/general-ci-range-weight-oracle.py) compared every
U+3B00–U+3BFF Extension A scalar followed by U+3000 under all five collations:
1,280 exact-byte comparisons, zero mismatches. Another 48 `AS CHAR(4)`
comparisons across the three `utf8mb4` collations matched, including padding.
The focused regression pins the composed bytes for each collation.

The full `just check` run passed 3,884 of 3,885 tests. Its independent
primary-key lookup latency ratio test exceeded its timing threshold once and
passed when rerun alone without rebuilding. The Release build and focused
legacy-weight regression passed.

This closes the audited ideographic-space composition. Other CJK punctuation
and mixed-script exact-weight families remain open; the change does not alter
the ngram posting collation key.
