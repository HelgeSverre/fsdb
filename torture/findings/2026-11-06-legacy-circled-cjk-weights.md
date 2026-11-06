# Legacy circled CJK weights, U+3280–U+32FF

The pinned MySQL 8.4.11 `WEIGHT_STRING` oracle found exact-byte differences
for all 128 characters in U+3280–U+32FF under `utf8mb4_unicode_ci`,
`utf8mb4_unicode_520_ci`, and `utf8mb3_unicode_ci` before this change.
The range contains circled ideographs, numbers, month symbols, and kana.

The first 36 ideographs reuse the primary table for parenthesized forms,
with ten circled-specific primaries. Thirteen additional ideographs, the
version-specific numeric sequences, and the contiguous kana weights complete
the range. The focused `WEIGHT_STRING` regression includes representatives
from each group.

After rebuilding the Release CLI, all 128 singleton weights matched in each
collation. All 127 adjacent pairs wholly inside the range matched too:
**0/765 exact-byte mismatches**. A `--pair-next` run including U+32FF still
finds the pair U+32FF/U+3300 different, because U+3300 starts the next
unaudited block. That boundary is not included in the 765-match claim.

This is an exact-weight audit. The existing U+3200–U+33FF
`utf8mb4_unicode_ci` equality and ngram corpus remains the behavioral
comparison control. U+3300–U+33FF exact weights remain open.
