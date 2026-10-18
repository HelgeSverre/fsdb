# `utf8mb4_general_ci` letterlike, number-form, and arrow ngrams

The expanded two-column MySQL 8.4.11 oracle found false full-text matches
among U+2100–U+21FF letterlike symbols, number forms, and arrows. For example,
fsdb matched both `℁` and `⅍` for either search, and lowercased `Ⅎ` and `ⅎ`
to the same ngram key. It also merged `Ↄ` with `ↄ` and `←` with `⇐`, which
MySQL distinguishes.

A native `WEIGHT_STRING` probe checked every code point in U+2100–U+21FF:
240 have their literal code point as the primary weight under
`utf8mb4_general_ci`. The 16 lowercase Roman numerals U+2170–U+217F share
the corresponding uppercase U+2160–U+216F weights. Fsdb now gives the
block those escaped weight classes. The full-text key path passes the original
token to this older collation, because modern Unicode lowercasing merges pairs
that MySQL distinguishes. Literal
private-use marker text cannot alias a generated key.

Build the Release CLI and run:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 2100:2200 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

The initial U+2100–U+214F probe had 302 membership or relevance mismatches
in 480 size-one searches. The adjacent U+2150–U+21FF probe, before that
portion was mapped, had 92 in 1,032 searches.
Afterwards the combined range has zero mismatches in 1,512 searches at every
token size from 1 through 10 across natural, Boolean, and expansion modes.
The maintained broad corpus also has zero mismatches in 387 searches per size
across the same ten sizes. Focused regressions cover all 240 key classes,
marker escaping, and representative
ngram postings. This is evidence for the audited range and corpus, not a
claim of complete collation parity.
