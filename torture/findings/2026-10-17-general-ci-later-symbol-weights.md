# `utf8mb4_general_ci` dingbat and supplemental-operator ngrams

The two-column MySQL 8.4.11 oracle found false full-text matches in
U+2700–U+27FF and U+2A00–U+2AFF. Fsdb matched the three decorated forms
`❶`, `➀`, and `➊` for each search, while MySQL kept them distinct. It also
merged `⫝̸` with `⫝`.

A native `WEIGHT_STRING` probe checked every code point in U+2300–U+2BFF.
All characters in the two affected blocks have their literal code points as
distinct primary weights under `utf8mb4_general_ci`. The already-audited
U+2400–U+24FF block retains its own case fold, and separate size-one ngram
probes found no mismatch within U+2300, U+2500–U+26FF, U+2800–U+29FF, or
U+2B00. Fsdb now gives the two divergent blocks escaped synthetic keys.

Build the Release CLI and run:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 2700:2800 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 2A00:2B00 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

Before the fix, the dingbat size-one lane had 300 membership or relevance
mismatches across 1,536 searches; the supplemental-operator lane had 12.
Afterwards each expanded lane has zero mismatches at each token size from 1
through 10 across natural, Boolean, and expansion modes. The maintained broad
corpus also has zero mismatches in 387 searches per size across the same ten
sizes. Focused regressions cover all 512 key classes, literal marker escaping,
and representative ngram postings. Other Unicode blocks, collations, and
search shapes remain outside this audit.
