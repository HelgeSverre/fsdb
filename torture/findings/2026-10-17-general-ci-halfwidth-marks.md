# `utf8mb4_general_ci` halfwidth voicing-mark ngrams

Native MySQL 8.4.11 keeps halfwidth voicing marks in their written positions
under `utf8mb4_general_ci`. It distinguishes `ﾟﾋ` from `ﾋﾟ`, `ﾊﾟ` from
`ﾊ`, and `ﾊﾞ` from `ﾊﾟ`. Fsdb's ICU primary comparison previously equated
each pair. At ngram sizes 6 and 8, overlapping grams in the query
`ﾊﾟﾋﾟﾌﾟﾍﾟﾎﾟ` consequently collapsed to fewer keys than MySQL uses.
Membership matched, but relevance was too low in natural, Boolean, and query
expansion modes.

The collation now preserves U+FF9E and U+FF9F with separate escaped primary
keys. A direct comparison regression failed before this change. A focused
four-document SQL oracle at token size 6 gives relevance 1.812381148 for
the matching document in all three search modes; the corresponding Expecto
regression matches within score tolerance.

Build the CLI in Release and run the two-column corpus against disposable
native MySQL and fsdb servers:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

The corpus runs 387 searches at each token size. Mismatches count membership
and relevance separately:

| Token size | Before | After |
| ---: | ---: | ---: |
| 1 | 313 | 292 |
| 2 | 143 | 134 |
| 3 | 86 | 86 |
| 4 | 23 | 20 |
| 5 | 18 | 18 |
| 6 | 3 | 0 |
| 7 | 0 | 0 |
| 8 | 3 | 0 |
| 9 | 0 | 0 |
| 10 | 0 | 0 |

Thirty-nine mismatch categories resolve, with none newly mismatching. The
remaining differences at sizes 1–5 involve other `utf8mb4_general_ci`
collation and full-text behavior; this audit does not establish broader
collation parity.
