# `utf8mb4_general_ci` enclosed alphanumeric ngrams

An expanded two-column ngram corpus over U+2460–U+24FF found false matches
between enclosed number forms. For example, a search for `①` also found a
document containing `⓵`. MySQL 8.4.11 keeps those forms distinct.

An exhaustive native `WEIGHT_STRING` probe found literal code-point weights
throughout this block, except that U+24D0–U+24E9 share the corresponding
U+24B6–U+24CF uppercase circled-letter weights. The general-collation key
now preserves those classes rather than accepting ICU's compatibility folds.
Focused tests cover equality, index keys, marker escaping, and ngram postings.

Build the Release CLI and run:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 2460:2500 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

The expanded lane compares 960 searches at each of token sizes 1 and 2.
Before this change, size 1 had 420 membership or relevance differences;
afterwards both sizes have zero. The native server reported version 8.4.11.
The maintained broad corpus also has zero differences across 387 searches
at each token size from 1 through 10. These results cover the tested
documents, terms, and modes, not every collation or full-text shape.
