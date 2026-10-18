# Unicode 5.2 Bopomofo ngram weights

The maintained [MySQL 8.4.11 differential oracle](../scripts/ngram-multicol-oracle.py)
found 42 membership or relevance mismatches at ngram size one in the combined
U+3100–U+31FF corpus under `utf8mb4_unicode_520_ci`. MySQL keeps `ㄮ`
distinct from `ㄜ`, and `ㆻ` distinct from `ㄍ` and `ㆣ`. Fsdb had merged
those pairs through ICU primary weights, so indexed ngram searches returned
extra rows and changed relevance. MySQL still equates the older velar forms
`ㄍ` and `ㆣ`.

The Unicode 5.2 collation now protects the two distinct forms with the same
collision-escaped keys already used for `utf8mb4_unicode_ci`. Equality,
canonical keys, and full-text postings share the corrected weights. Focused
regressions cover the distinct pairs, the older equal pair, and natural and
Boolean ngram searches.

After the change, the native oracle reports **zero membership or relevance
mismatches across 1,440 queries at each token size from 1 through 10** in
natural, Boolean, and query-expansion modes. The audit covers this corpus and
does not establish exact MySQL weight bytes or comprehensive Unicode 5.2
collation parity.

Reproduce the differential sweep after building the Release CLI:

```sh
python3 torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3100:3200 --collation utf8mb4_unicode_520_ci
```
