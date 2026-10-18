# `utf8mb4_general_ci` supplementary ngram keys

MySQL 8.4.11 gives sampled supplementary characters a shared primary weight
under `utf8mb4_general_ci`. Direct comparisons return true for `𠮷 = 🙂` and
`𠮷 = 𠀀`, but false for `𠮷 = 生`. Additional sampled code points from
U+10400 through U+10FFFF, including musical symbols, emoji, variation
selectors, and CJK extensions, also compare equal to `𠮷`. Fsdb previously
used ICU's distinct weights for the supplementary characters. This also split
full-text ngram postings that MySQL addresses with one key.

The collation now maps complete supplementary scalars to the same escaped
fallback key already used by the older `utf8mb4_unicode_ci` implementation.
The escape keeps literal private-use text distinct from the synthetic key.
Focused equality, key, hash, and ngram regressions cover the observed cases.

Build the CLI in Release and compare the two-column corpus with disposable
native MySQL and fsdb servers:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci 1 2
```

At token size 1, the corpus has 387 searches in natural, Boolean, and query
expansion modes. The total mismatch count falls from 358 to 340; membership
mismatches fall from 132 to 122 and relevance mismatches from 226 to 218.
Sixteen previously mismatching query categories now match, with none newly
mismatching. At token size 2, mismatches fall from 176 to 166 across the same
387 searches, with eight categories resolved and none newly mismatching.
Other `utf8mb4_general_ci` differences remain, including sampled
radical, format-control, Jamo, and kana behavior and their effects on query
expansion. This result is limited to the audited corpus and does not establish
general collation parity.
