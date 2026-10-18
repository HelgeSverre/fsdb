# `utf8mb4_general_ci` CJK radical weights

The remaining size-3 `日本語` relevance differences came from a two-column
document whose body contains `⽇本語` and title contains `日本語`. MySQL
`utf8mb4_general_ci` gives the Kangxi radical `⽇` a weight distinct from
the ideograph `日`; ICU folded them together, making fsdb count two postings
for the ideograph query. A focused four-document MySQL 8.4.11 probe gives
both the radical/title document and an ordinary `日本語` document a natural
and Boolean relevance of 0.0906190574 for that query. The Expecto regression
checks membership and relevance.

Native MySQL 8.4.11 `WEIGHT_STRING` returns each assigned character's literal
code point weight across CJK Radicals Supplement U+2E80–U+2EF3 and Kangxi
Radicals U+2F00–U+2FD5 (329 assigned characters, excluding the gaps). For
the 216 characters whose NFKC form differs, none compare equal to that form
under `utf8mb4_general_ci`. Fsdb now gives those assigned radicals escaped,
distinct primary keys in this collation. The legacy Unicode collations retain
their own behavior.

Build the Release CLI and run these native-MySQL comparisons:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-radicals --collation utf8mb4_general_ci 1 3
```

The expanded radical lane has zero mismatches across 1,635 searches at each
of token sizes 1 and 3. The general two-column corpus changes as follows:

| Token size | Before | After |
| ---: | ---: | ---: |
| 1 | 248 | 175 |
| 2 | 68 | 29 |
| 3 | 15 | 0 |
| 4–10 | 0 | 0 |

Comparison by token size, search mode, query, and membership/relevance kind
finds 127 previous mismatch entries resolved and none introduced.
The remaining 204 entries at sizes 1 and 2 cover other full-text and
collation cases. At size 2, the 29 entries come from 13 query spellings,
chiefly kana iteration/ligature forms and Boolean boundaries around spaces
or format characters; size 1 has broader query-expansion and short-token
differences. This audit does not establish broader `general_ci` parity.
