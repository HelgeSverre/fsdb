# Legacy compatibility ideographs and supplementary fallback weights

The maintained multi-column ngram oracle compares native MySQL 8.4.11 with
fsdb under `utf8mb4_unicode_ci`. At token size two, U+2F00–U+2FFF,
U+3400–U+34FF, and U+F900–U+F9FF already matched. The later
U+FA00–U+FAFF and U+2F800–U+2F8FF ranges initially had 615 and 2,272
membership, relevance, or NFKC-equality differences respectively.

For the BMP range, MySQL keeps U+FA2E, U+FA2F, U+FA6B–U+FA6D, and
U+FA70–U+FAD9 distinct from their NFKC expansions. The older forms retain
their existing equivalences, including the measured equal pairs
U+FA25/U+FA67 and U+FA5D/U+FA5E. The legacy collation now gives only the
later forms collision-escaped distinct keys.

The supplementary range follows a different rule. In this legacy collation,
MySQL gives the sampled supplementary CJK compatibility forms one shared
fallback primary weight. Direct SQL also equates a sampled form with another
supplementary compatibility form, a CJK Extension-B character, and an emoji,
while keeping it distinct from its BMP NFKC base. The 32 NFKC expansions
that MySQL equates with their source in U+2F800–U+2F8FF are themselves
supplementary. Fsdb now applies one collision-escaped key to supplementary
scalars under this collation. Original text still drives ngram tokenization.
`WEIGHT_STRING` alone was not used to infer the rule: direct equality and
full-text results supplied the oracle evidence.

After the change, the U+FA00–U+FAFF lane has zero differences at startup
ngram sizes 1–10, with 1,296 full-text queries and 208 direct equality
comparisons per size. The U+2F800–U+2F8FF lane also has zero across sizes
1–10, with 1,536 queries and 265 equalities per size; its corpus includes
additional Extension-B, emoji, and BMP-base documents. Size-two adjacent
samples of U+20000–U+200FF, U+1F600–U+1F6FF, and U+2F900–U+2F9FF have
zero differences. The older U+F900–U+F9FF, kana/Bopomofo, and enclosed-CJK
size-two lanes remain at zero. The full fsdb suite passes 3,717 tests.

Direct equality checks additionally compare the shared supplementary key
with U+10000, a musical symbol, a variation selector, and U+10FFFD.

This is audited legacy-collation coverage. Exact MySQL weight bytes, other
legacy collation families, and unmeasured scalar ranges remain open.
