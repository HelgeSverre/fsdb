# `utf8mb4_general_ci` CJK-symbol ngrams

An expanded two-column ngram corpus over U+3000–U+303F found false matches
between written symbols. For example, `〒` also found `〶`, and `〝` found
the other CJK quotation marks. MySQL 8.4.11 keeps these characters distinct.

An exhaustive native `WEIGHT_STRING` probe returned each character's literal
code point as its primary weight. U+3000 already has a distinct space key in
fsdb; the general collation now gives U+3001–U+303F distinct keys as well.
Focused tests cover collation and index keys, marker escaping, and ngram
postings.

Build the Release CLI and run:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 3000:3040 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

The expanded lane compares 378 searches at each token size. Before the
change it reported 130 differences at size 1 and 300 at size 2; afterwards
both sizes have zero. The tested corpus covers membership and relevance in
natural, Boolean, and expansion modes. The maintained broad corpus also has
zero differences across 387 searches at each token size from 1 through 10.
These results do not establish parity for every full-text shape.
