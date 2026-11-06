# CJK presentation-form ngram and equality audit

The pinned MySQL 8.4.11 oracle indexed U+FE30–U+FE6F in a two-column ngram
table under `utf8mb4_unicode_ci` and `utf8mb4_unicode_520_ci`. At startup
sizes 1 and 2, the initial UCA 4.0 run matched all 348 natural, Boolean,
and query-expansion searches per size but differed on four of 56 NFKC
equality comparisons: U+FE49–U+FE4C incorrectly equaled their NFKC
expansion, SPACE plus COMBINING OVERLINE.

MySQL's exact primary weight for these four forms is the weight of U+203E
OVERLINE. Fsdb now folds them to that character in its legacy UCA 4.0
comparison and full-text key path. The original characters remain available
to the ngram tokenizer.

After the change, **6,960/6,960 searches** and **1,120/1,120 equality
comparisons** match MySQL across both collations and startup sizes 1–10.
The focused collation regression checks equality, index keys, and rejection
of the NFKC expansion. Other presentation-form ranges and mixed-script
full-text query shapes remain open.
