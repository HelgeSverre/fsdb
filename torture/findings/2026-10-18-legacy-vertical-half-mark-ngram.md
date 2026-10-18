# Legacy Unicode vertical and half-mark ngram weights

A native MySQL 8.4.11 differential over U+FE10–U+FE6F exposed false
full-text matches under `utf8mb4_unicode_ci`. Before the fix, fsdb differed
on 112 of 504 two-column search cases at `ngram_token_size=1` and 464 of
504 at size 2. The cases compare both matching row IDs and relevance for
natural-language, Boolean, and query-expansion searches.

MySQL `WEIGHT_STRING` gives U+FE10–U+FE1F and U+FE24–U+FE2F distinct implicit
primary weights (`FBC1FE10` through `FBC1FE2F` for those assigned positions).
U+FE20–U+FE23 instead have empty primary weights. Fsdb already protected
U+FE10–U+FE19; the legacy collation fold now also preserves the remaining
implicit-weight forms and escapes its private-use marker in literal input.
The focused collation regression checks all 28 distinct keys, their order,
and marker isolation.

After the change, the maintained
[`ngram-multicol-oracle.py`](../scripts/ngram-multicol-oracle.py) reports zero
membership or relevance mismatches among 504 cases at each token size 1–10:

```sh
python3 torture/scripts/ngram-multicol-oracle.py \
  --legacy-range FE10:FE70 --collation utf8mb4_unicode_ci
```

The same corpus under `utf8mb4_unicode_520_ci` already matched at sizes 1
and 2 without this fold. Other legacy weight families remain outside this
audit.
