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
211 to 4 and the size-two count from 57 to zero. The four remaining cases
are relevance scores for `神` and `神` in natural and Boolean modes. MySQL
distinguishes those characters under this collation, while fsdb still shares
their host ICU key. A native weight probe over U+F900–U+FAFF found 396
compatibility ideographs distinct from their NFKC forms and 64 equal to them;
some distinct ideographs share a MySQL weight with each other. That remaining
mapping needs an explicit collation audit rather than folding the whole block
or adding one exception for `神`.
