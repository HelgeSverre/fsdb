# `utf8mb4_general_ci` vertical and small-form punctuation

An expanded two-column ngram corpus over U+FE10–U+FE6F found false matches
between written punctuation forms. Before the fix, ngram size 1 had 322
membership or relevance differences among 504 searches. For example, a search
for `︐` also matched `﹐`, and several presentation forms shared one posting.

Native MySQL 8.4.11 assigns each of the 96 code points in this range its
literal, distinct primary weight, including positions Python labels
nonprintable. Fsdb now preserves those weights in its shared
`utf8mb4_general_ci` key path. The focused regression checks all 96 distinct
keys, their order, representative equality cases, and marker escaping.

With a Release CLI build, the differential corpus is run as:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range FE10:FE70 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

After the change, all 504 searches at each of token sizes 1 and 2 match
MySQL in membership and relevance. The maintained broad corpus also has zero
differences among 387 searches at each token size from 1 through 10. Other
Unicode ranges and full-text query shapes remain outside this audit.
