# `utf8mb4_general_ci` variation-selector weights

The two-column ngram differential found false matches across U+FE00–U+FE0E.
At token size 1, 150 row or relevance comparisons differed among 96 searches:
each selector's search found documents containing other selectors. Native
MySQL 8.4.11 `WEIGHT_STRING` returns the written `FE00` through `FE0F`
primary weights in codepoint order. Fsdb previously generated only two
distinct keys across the 16 selectors.

The shared collation-key path now maps the whole block to separate escaped
synthetic weights. U+FE0F moves from its earlier one-off format-control key
to the same ordered block; it remains distinct from the other controls.
Focused equality tests cover all 16 keys, their order, and a literal marker.
An ngram regression checks representative postings at both ends of the block.

The native differential command is:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range FE00:FE10 --collation utf8mb4_general_ci
```

All 96 two-column searches agree with MySQL on matching rows and relevance
at each token size from 1 through 10. Other variation-selector ranges and
full-text query shapes remain outside this finding.
