# Fullwidth and halfwidth general-collation weights

The pinned MySQL 8.4.11 range oracle compared singleton `WEIGHT_STRING`
bytes for every code point in U+FF00–U+FFEF (240 values). Before this
change, fsdb differed on all 240 values in both `utf8mb4_general_ci` and
`utf8mb3_general_ci`. MySQL uses the written code point as the weight,
except U+FF41–U+FF5A fold to U+FF21–U+FF3A. Fsdb now follows that rule.

The Release CLI has **0/240 mismatches** against MySQL in each general
collation. The focused legacy `WEIGHT_STRING` test and three general Unicode
ngram tests pass. This exact-weight result does not establish all full-text
search shapes. `utf8mb4_unicode_ci` still had 240/240 singleton differences
in the initial range probe and needs its own legacy UCA weight work.
