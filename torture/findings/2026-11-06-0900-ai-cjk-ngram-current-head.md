# `utf8mb4_0900_ai_ci` CJK ngram current-head check

The maintained two-column corpus was rerun against disposable native MySQL
8.4.11 and the Release CLI at ngram token sizes 1 and 2. Each size runs 387
queries across natural, Boolean, and query-expansion modes, comparing both
membership and relevance within the oracle's existing score tolerance.

| Collation and size | Membership differences | Relevance differences |
| --- | ---: | ---: |
| `utf8mb4_general_ci`, 1 | 0 | 0 |
| `utf8mb4_0900_as_cs`, 1 | 0 | 0 |
| `utf8mb4_0900_ai_ci`, 1 | 24 | 122 |
| `utf8mb4_0900_ai_ci`, 2 | 50 | 52 |

The accent-insensitive differences by mode are:

| Size | Natural membership / relevance | Boolean membership / relevance | Expansion membership / relevance |
| --- | ---: | ---: | ---: |
| 1 | 16 / 24 | 1 / 14 | 7 / 84 |
| 2 | 8 / 8 | 10 / 12 | 32 / 32 |

At size 1, fifteen of the sixteen natural-language membership differences
include MySQL document 53, which contains `生\u180e日` and `蒙古分隔`, while
fsdb omits it. Queries include halfwidth and decomposed kana, variation
selectors, and ideographic variation sequences. The remaining natural
membership difference is the direct `生\u180e日` query. This concentration
points to a useful minimal probe around U+180E and the accent-insensitive
collation, but does not yet establish whether token boundaries, collation
keys, or scoring cause it. Size 2 also differs in other documents and modes.

The oracle now prints mismatch counts by mode and by membership versus
relevance before listing individual examples. These results identify a
broader accent-insensitive lane that is not covered by the matching
`utf8mb4_0900_as_cs` and `utf8mb4_general_ci` controls. No production
behavior changed in this audit.

Run `uv run --with pymysql torture/scripts/ngram-multicol-oracle.py
--collation utf8mb4_0900_ai_ci 1 2` after building the Release CLI.

## U+180E fold correction

The four-document control reduced the mismatch to two singleton ngrams:
U+180E in `生\u180e日` and the halfwidth voicing mark in `ｶﾞｯｺｳ`.
MySQL's `utf8mb4_0900_ai_ci` gives both empty primary weights, considers
them equal to one another, and matches the document containing either mark
when the query contains the other. Fsdb incorrectly applied its Unicode 5.2
U+180E weight override to the 0900 accent-insensitive collations. Removing
that override preserves the Unicode 5.2 path and corrects SQL equality and
ngram posting keys for the 0900 path.

The full native MySQL 8.4.11 oracle now has **zero differences at ngram sizes
1–4 and 6–10**, 387 searches per size. Size 5 retains two reports (Boolean
membership and relevance for `하늘`, where MySQL includes document 45 and fsdb
does not). The same two size-5 reports occur on the pre-fix `5620d681`
baseline, so they were not introduced by the U+180E correction. The focused collation and four-document ngram regressions pass, as
do the 18 filtered full-text tests. The size-5 Korean Jamo result is a
separate remaining parity case.
The 93 filtered collation tests also pass.
