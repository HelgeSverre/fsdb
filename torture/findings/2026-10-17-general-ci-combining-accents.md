# `utf8mb4_general_ci` decomposed accent ngrams

At ngram token size 2, several Boolean queries incorrectly matched the
document `生e\u0301日`. Its final gram is U+0301 followed by `日`.
ICU primary comparison ignored the combining accent, so fsdb allowed a
short query for `日` to address that posting. Native MySQL 8.4.11 keeps the
mark's positional weight: `日` differs from `\u0301日`, and `e\u0301`
differs from both `e` and precomposed `é` under `utf8mb4_general_ci`.

Direct `WEIGHT_STRING` checks of the 112 assigned U+0300–U+036F characters
found literal code-point weights for all except U+0345, which has weight
U+0399. U+034F was already handled by the format-control fold. Fsdb now
preserves the other 110 combining characters as escaped, positional keys in
this collation. A focused native MySQL full-text probe established that
natural, Boolean, and expansion searches for `日` or `生 日` return no row
from the decomposed-accent document, while `\u0301日` and `生e\u0301日`
find it. Expecto covers those results.

After a Release build, run the maintained two-column differential corpus:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

| Token size | Before | After |
| ---: | ---: | ---: |
| 1 | 175 | 175 |
| 2 | 29 | 13 |
| 3–10 | 0 | 0 |

Comparison by token size, search mode, query, and membership/relevance kind
shows 16 mismatch entries resolved and none introduced. The remaining
188 entries are at sizes 1 and 2, with kana-form and query-expansion cases
among the size-2 residuals.
