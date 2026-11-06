# Legacy enclosed CJK weights, U+3220–U+325F

The pinned MySQL 8.4.11 oracle found 64 exact `WEIGHT_STRING` byte
differences in each of `utf8mb4_unicode_ci`, `utf8mb4_unicode_520_ci`, and
`utf8mb3_unicode_ci` for U+3220–U+325F. The first 36 characters are enclosed
ideographs, followed by implicit or numeric forms. The two Unicode versions
share the ideograph primaries but use different wrappers; the later numeric
forms have version-specific sequences.

The legacy weight path now uses a compact ideograph-primary table and
version-specific rules for the remaining forms. The Release CLI matched all
64 singleton weights and all 64 adjacent-pair weights in each of the three
collations: **0/384 exact-byte mismatches**. The focused primary-weight
regression test passed representative values from each group.

This audit addresses exact weight bytes. The existing U+3200–U+33FF
`utf8mb4_unicode_ci` equality and ngram corpus remains the behavioral
comparison control. Exact weights after U+327F in this block remain open.
