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
