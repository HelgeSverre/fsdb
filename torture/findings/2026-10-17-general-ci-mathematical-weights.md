# `utf8mb4_general_ci` mathematical-operator ngrams

The two-column MySQL 8.4.11 oracle found false full-text matches among
U+2200–U+22FF mathematical operators. For example, searching for `∃` also
returned the `∄` document in fsdb; MySQL kept the postings separate.

A native `WEIGHT_STRING` probe checked every code point in the block under
`utf8mb4_general_ci`. All 256 have distinct primary weights equal to their
written code points. Fsdb now gives them separate escaped weight keys, so a
literal private-use marker cannot alias a generated key.

Build the Release CLI and run:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 2200:2300 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

Before the fix, the expanded size-one lane had 420 membership or relevance
mismatches across 1,536 searches. Afterwards it has zero mismatches at each
token size from 1 through 10 across natural, Boolean, and expansion modes.
The maintained broad corpus also has zero mismatches in 387 searches per size
across the same ten sizes. Focused regressions cover all 256 key classes,
marker escaping, and representative ngram postings. Other Unicode blocks,
collations, and search shapes remain outside this audit.
