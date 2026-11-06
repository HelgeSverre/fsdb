# Fullwidth and halfwidth general-collation weights

The pinned MySQL 8.4.11 range oracle compared singleton `WEIGHT_STRING`
bytes for every code point in U+FF00–U+FFEF (240 values). Before this
change, fsdb differed on all 240 values in both `utf8mb4_general_ci` and
`utf8mb3_general_ci`. MySQL uses the written code point as the weight,
except U+FF41–U+FF5A fold to U+FF21–U+FF3A. Fsdb now follows that rule.

The Release CLI has **0/240 mismatches** against MySQL in each general
collation. The focused legacy `WEIGHT_STRING` test and three general Unicode
ngram tests pass.

The follow-up added MySQL's UCA 4.0 and 5.2 primary weights for U+FF00–U+FFFF
under `utf8mb4_unicode_ci`, `utf8mb3_unicode_ci`, and
`utf8mb4_unicode_520_ci`. Each has **0/256 singleton mismatches**. The UCA 4.0
oracle also has no mismatches for 255 adjacent pairs wholly within the block;
UCA 5.2 has none for 256 `AS CHAR(4)` values. U+FFFF followed by U+10000 is
outside the measured block and still differs.

The two-column U+FF00–U+FF9F ngram corpus matches all 954 searches and 159
NFKC equalities at each token size 1 and 2 under both Unicode collations.
UCA 5.2 initially had 92 equality differences per size even after exact
weights matched; its comparison path now folds U+FF01–U+FF5E to ASCII while
the ngram key path keeps its existing measured behavior. Broader full-text
search shapes and other Unicode blocks remain open. A mixed ASCII/Han
UCA 5.2 run at token size 1 also matches 954 searches, 795 equalities, and
795 exact weights.
