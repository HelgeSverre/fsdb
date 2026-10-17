# `utf8mb4_general_ci` Hangul syllable ngrams

Native MySQL 8.4.11 assigns literal, distinct `WEIGHT_STRING` values to all
256 Hangul syllables in U+AC00–U+ACFF. The [range weight oracle](../scripts/general-ci-range-weight-oracle.py)
finds no nonliteral weights or duplicate groups in this block.

The maintained [two-column differential oracle](../scripts/ngram-multicol-oracle.py)
tests these syllables alongside decomposed conjoining Jamo and compatibility
Jamo controls. Across ngram token sizes 1–10, all 1,551 searches per size
match MySQL in membership and relevance for natural-language, Boolean, and
query-expansion modes. This includes `가` and `각` next to `가`, `각`, and
`ㄱㅏ`; the corpus does not equate written forms merely because they can
represent related sounds.

With a Release CLI build, reproduce the audit using:

```sh
python3 torture/scripts/general-ci-range-weight-oracle.py AC00 AD00
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range AC00:AD00 --collation utf8mb4_general_ci
```

No engine change was needed. This is evidence for the sampled block and
search shapes, not all Hangul syllables or every full-text expression.
