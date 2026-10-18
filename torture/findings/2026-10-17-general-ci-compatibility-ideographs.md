# `utf8mb4_general_ci` compatibility-ideograph ngrams

The last four differences in the maintained two-column general-collation
corpus were size-1 relevance scores for `神` and `神`. A document containing
one compatibility ideograph and two unified `神` characters scored three
occurrences for either query in fsdb. MySQL 8.4.11 counts one occurrence for
`神` and two for `神` in both natural and Boolean modes.

MySQL's `utf8mb4_general_ci` considers `神` and `神` unequal and returns
`FA19` and `795E` from `WEIGHT_STRING`. An exhaustive probe of U+F900–U+FAFF
found literal code-point weights for all 512 characters, including currently
unassigned positions. ICU compatibility normalization had merged some of
these forms with unified Han. Fsdb now escapes this range into distinct
primary keys for `utf8mb4_general_ci`; other collations retain their own
rules. Focused Expecto tests cover collation equality, index keys, and the
size-1 two-column relevance ratio.

Build the Release CLI and run the native MySQL 8.4.11 differential lanes:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range f900:fb00 --collation utf8mb4_general_ci 1
```

The broad corpus reports zero membership or relevance mismatches across 387
queries at each ngram size 1–10, down from four size-1 relevance differences.
The expanded range reports zero mismatches across 2,832 size-1 queries. These
results cover the tested documents, terms, and modes; they do not establish
general full-text or collation parity beyond them.
