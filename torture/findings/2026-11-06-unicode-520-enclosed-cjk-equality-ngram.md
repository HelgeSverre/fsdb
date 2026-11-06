# Unicode 5.2 enclosed CJK equality and ngram keys

The expanded U+3200–U+33FF oracle accidentally used `utf8mb4_unicode_ci`
for its SQL equality checks even when the selected collation was
`utf8mb4_unicode_520_ci`. Correcting that query exposed 198 mismatches among
502 equality checks at each tested ngram size. The 3,066 two-column search
queries at each size already matched MySQL 8.4.11.

MySQL 5.2 equates several enclosed CJK characters with their expansions,
including U+3244–U+3247, where the older Unicode collation keeps distinct
weights. The SQL equality fold now follows that version-specific behavior.
The ngram token key retains its existing equivalence classes, since applying
the equality fold to full-text tokens introduced search regressions. The
shared fold pipeline takes an explicit choice for this one difference.

After the change, the corrected oracle reports **zero search and zero
equality mismatches** at every ngram size 1–10: 30,660 search queries and
5,020 equality checks in total. The focused regression test verifies that
equality, index keys, and hash keys agree for representative enclosed forms.

Command: `uv run --with pymysql torture/scripts/ngram-multicol-oracle.py
--legacy-range 3200:3400 --collation utf8mb4_unicode_520_ci` after building
`src/Fsdb.Cli` in Release. This covers the audited two-column corpus, not
all possible full-text documents or Unicode characters.
