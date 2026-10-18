# Legacy-collation variation selectors and CJK punctuation

The maintained two-column ngram differential compared native MySQL 8.4.11
with fsdb at `ngram_token_size` 1 through 10. Each corpus ran 96 Boolean,
natural-language, and query-expansion queries per size, comparing both row
membership and relevance scores.

| Collation | Corpus | Queries | Mismatches |
|---|---|---:|---:|
| `utf8mb4_unicode_ci` | U+FE00–U+FE0F variation selectors | 960 | 0 |
| `utf8mb4_unicode_520_ci` | U+FE00–U+FE0F variation selectors | 960 | 0 |
| `utf8mb4_unicode_ci` | U+E0100–U+E010F supplementary variation selectors | 960 | 0 |
| `utf8mb4_unicode_520_ci` | U+E0100–U+E010F supplementary variation selectors | 960 | 0 |
| `utf8mb4_unicode_ci` | U+3030–U+303F CJK symbols and punctuation | 960 | 0 |

These are parity observations, not a claim about the full supplementary
variation-selector or CJK punctuation ranges. The oracle is
[`ngram-multicol-oracle.py`](../scripts/ngram-multicol-oracle.py); for example:

```sh
python3 torture/scripts/ngram-multicol-oracle.py \
  --legacy-range E0100:E0110 --collation utf8mb4_unicode_520_ci
```

The remaining legacy-collation and MeCab gaps are unchanged. No engine
change was needed for these five sampled corpora.
