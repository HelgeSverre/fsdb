# Japanese kana-sensitive ngram keys

MySQL 8.4.11 treats hiragana and katakana as different under
`utf8mb4_ja_0900_as_cs_ks`: `こ = コ`, `は = ハ`, and `ぱ = パ` are false, while
`カ = ｶ` is true. In a size-one ngram table with separate `こ` and `コ`
documents, natural and Boolean searches each return only the matching script.
Fsdb's host Japanese ICU collation had equated the scripts, so both documents
matched either search.

Fsdb now composes decomposed kana voicing and adds an escaped per-hiragana
marker to the collation input before keying and comparing. Katakana width
equivalence remains with ICU, and a literal marker cannot alias transformed
hiragana. Focused equality and indexed ngram regressions pin these cases.
The maintained two-column oracle can replay the larger corpus after a Release
CLI build:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_ja_0900_as_cs_ks 1 2
```

Across 387 searches per token size, the size-one mismatch count fell from
211 to zero and the size-two count from 57 to zero. The final four size-one
relevance differences involved `神` and `神` in natural and Boolean modes.
A native weight probe over U+F900–U+FAFF found 396 compatibility ideographs
distinct from their NFKC forms and 64 equal to them; some distinct
ideographs share a MySQL weight with each other. Fsdb now maps those audited
weight classes explicitly, including the distinction between `神` and `神`.
This closes the maintained corpus only; broader Japanese collation and parser
behavior remains open.

An expanded native 8.4.11 replay used the same two-column oracle with
`--legacy-range` and `--collation utf8mb4_ja_0900_as_cs_ks`, at token sizes
one and two. U+F900–U+F9FF produced 1,536 searches per size, U+FA00–U+FAFF
1,296, U+2F800–U+2FA1F 3,252, and U+3040–U+30FF 1,134. All 14,436
comparisons matched in both membership and relevance. Each range includes
the oracle's natural, Boolean, and query-expansion searches. This covers
individual characters in those ranges embedded in the oracle's two-column
documents, not arbitrary mixed-character queries or all Japanese collation
operations.
