# Letterlike symbols under the legacy Unicode collation

The maintained `ngram-multicol-oracle.py --legacy-range 2100:2200` lane compares
all printable U+2100–U+21FF forms in a multi-column
`utf8mb4_unicode_ci` full-text index against native MySQL 8.4.11. It checks
natural, Boolean, and query-expansion membership and relevance, plus equality
between each form and its distinct Unicode NFKC expansion.

Before this change, the size-two lane found 79 differences in 756 full-text
queries and 105 NFKC comparisons. They clustered in letterlike symbols,
fractions, and reversed letters. ICU merged pairs that MySQL's legacy UCA
keeps distinct, such as `ℼ`/`π`, `ℼ`/`ℿ`, `℁`/`⅍`, `Ⅎ`/`ⅎ`, and `Ↄ`/`ↄ`.
Conversely, MySQL equates `ℏ` with `ħ` but distinguishes it from the other
script-H forms. The NFKC expansions of `⅐`, `⅑`, `⅒`, and `↉` are distinct
under MySQL's legacy collation. The extra posting matches also changed inverse
document frequency and therefore relevance.

The legacy collation now gives those distinct forms collision-escaped keys and
keeps the measured equal pairs together. Full-text tokens retain the original
characters for key generation under this collation. In particular, Boolean
ngram parsing no longer lowercases a word before splitting it into ngrams;
modern Unicode lowercasing would merge `Ⅎ` with `ⅎ` and `Ↄ` with `ↄ`
before the legacy collation could distinguish them.

The lane now reports zero differences at every startup token size from 1 to 10:
1,512 full-text queries, including quoted phrases, and 105 NFKC comparisons
at each size. The earlier U+3200–U+33FF enclosed-CJK lane also remains at
zero across sizes 1–10 (3,066 queries including phrases and 502 NFKC
comparisons per size). The full fsdb suite passes 3,712 tests. This is an
audited range, not a claim of complete UCA 4.0 weight-byte parity.
