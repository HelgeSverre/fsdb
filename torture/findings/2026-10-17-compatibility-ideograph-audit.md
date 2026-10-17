# Remaining legacy compatibility-ideograph differences

The maintained multi-column ngram oracle was run at token size two against
native MySQL 8.4.11 with the new quoted-phrase lane. Three adjacent CJK
ranges matched without further changes: U+2F00–U+2FFF (1,380 full-text
queries, 214 NFKC comparisons), U+3400–U+34FF (1,536 queries), and
U+F900–U+F9FF (1,536 queries, 256 NFKC comparisons).

Two later compatibility-ideograph ranges remain divergent:

| Range | Full-text queries | NFKC comparisons | Total differences | NFKC equality differences |
|---|---:|---:|---:|---:|
| U+FA00–U+FAFF | 1,296 | 204 | 615 | 111 |
| U+2F800–U+2F8FF | 1,536 | 256 | 2,272 | 224 |

For U+FA00–U+FAFF, MySQL distinguishes the NFKC expansions of U+FA2E,
U+FA2F, U+FA6B–U+FA6D, and U+FA70–U+FAD9 where fsdb currently folds
them together. Most, but not all, supplementary compatibility ideographs in
U+2F800–U+2F8FF also remain distinct from their NFKC expansions in MySQL.
Those equality differences propagate into extra ngram matches and incorrect
inverse-document-frequency scores. The supplementary range has 512
natural-mode membership differences, so treating the entire range as one
uniform fold or one uniformly distinct block would be unsafe.

This audit leaves those ranges open. A correction needs MySQL-backed
equivalence classes for the individual forms, including comparisons with
their base ideographs and with each other, before changing legacy collation
keys. It must retain the already-matching older U+F900–U+F9FF behavior.
