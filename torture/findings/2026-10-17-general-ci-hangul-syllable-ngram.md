# `utf8mb4_general_ci` Hangul syllable ngrams

Native MySQL 8.4.11 assigns literal, distinct `WEIGHT_STRING` values to all
11,172 Hangul syllables in U+AC00–U+D7A3. The [range weight oracle](../scripts/general-ci-range-weight-oracle.py)
finds no nonliteral weights or duplicate groups across the complete block.

The maintained [two-column differential oracle](../scripts/ngram-multicol-oracle.py)
tests these syllables alongside decomposed conjoining Jamo and compatibility
Jamo controls. Across ngram token sizes 1–10, all 1,551 searches per size
match MySQL in membership and relevance for natural-language, Boolean, and
query-expansion modes. This includes `가` and `각` next to `가`, `각`, and
`ㄱㅏ`; the corpus does not equate written forms merely because they can
represent related sounds.

The adjacent U+AD00–U+ADFF block and sampled middle U+C100–U+C1FF and late
U+D600–U+D6FF blocks each match all 1,536 two-column differential queries
per token size from 1–10 with zero membership or relevance mismatches. These
four search corpora sample 1,024 syllables; the weight check covers the full
syllabary. The differential result does not establish every ngram query over
all 11,172 syllables.

With a Release CLI build, reproduce the audit using:

```sh
python3 torture/scripts/general-ci-range-weight-oracle.py AC00 D7A4
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range AC00:AD00 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range AD00:AE00 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range C100:C200 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range D600:D700 --collation utf8mb4_general_ci
```

No engine change was needed. Search parity remains bounded to the sampled
corpora and expressions.
