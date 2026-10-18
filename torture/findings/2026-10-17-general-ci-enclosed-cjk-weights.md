# `utf8mb4_general_ci` enclosed CJK and square-unit ngrams

An expanded two-column ngram corpus over U+3200–U+33FF found false matches
between symbols that ICU expands to similar ordinary text. For example,
`㎞` and `㏎` shared an fsdb key, so a query for `中㎞` could also find a
document containing `中㏎`. MySQL 8.4.11 keeps their written forms distinct.

An exhaustive native-MySQL `WEIGHT_STRING` probe returned the literal code
point for all 512 characters in U+3200–U+33FF. Fsdb now escapes this range
into distinct primary keys under `utf8mb4_general_ci`. Other collations keep
their existing rules. Focused tests cover equality, index keys, marker
escaping, and two-column ngram postings.

Build the Release CLI and run:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 3200:3400 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

The expanded lane compares 3,066 queries at each of token sizes 1 and 2.
Before the change it reported 114 and 152 membership or relevance mismatch
entries respectively; after the change it reports zero at both sizes. The
maintained broad corpus and other CJK ranges are separate checks. These
results do not establish parity for untested characters, query shapes, or
collations.
