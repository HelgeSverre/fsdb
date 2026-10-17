# `utf8mb4_general_ci` Glagolitic and Coptic ngram weights

An expanded two-column ngram corpus over printable U+2C00–U+2CFF found
false matches under `utf8mb4_general_ci`. Before the fix, ngram size 1 had
2,118 membership or relevance differences across 1,506 searches; size 2
had 2,704. ICU case-folded many Glagolitic uppercase/lowercase pairs that
native MySQL 8.4.11 keeps distinct. For example, a search for `Ⰰ` also
matched the `ⰰ` document in fsdb, but only the `Ⰰ` document in MySQL.

The [native weight oracle](../scripts/general-ci-range-weight-oracle.py)
checked all 256 code points, including unassigned positions. Every MySQL
primary weight equals its code point, with no duplicate weights. Fsdb now
gives the block escaped synthetic keys in its shared general-collation path;
the focused regression checks all 256 key classes, their ordering, literal
marker escaping, and representative ngram postings.

Run the weight and posting comparisons after a Release CLI build:

```sh
python3 torture/scripts/general-ci-range-weight-oracle.py 2C00 2D00
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 2C00:2D00 --collation utf8mb4_general_ci 1 2
```

After the fix, all 1,506 searches at each ngram size match MySQL in result
membership and relevance. Other Unicode blocks, token sizes, and full-text
query shapes remain outside this audit.
