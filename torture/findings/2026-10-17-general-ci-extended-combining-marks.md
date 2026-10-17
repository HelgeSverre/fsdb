# `utf8mb4_general_ci` extended combining-mark weights

The two-column ngram differential corpus found false matches in three later
combining-mark blocks under `utf8mb4_general_ci`. At token size 1, the
U+1AB0–U+1AFF corpus had 156 mismatched row or relevance comparisons among
186 searches; U+1DC0–U+1DFF had 292 among 384, and U+20D0–U+20FF had 326
among 198. A search for `᪰` also found a document containing `᪱` and many
other marks.

Native MySQL 8.4.11 gives all 192 code points across the three blocks
distinct, literal primary weights in codepoint order. Fsdb's ICU-based key
produced only 55 keys for the first 80 points and 126 keys across all three
blocks. The shared collation-key path now escapes one synthetic marker and
preserves each written weight in non-overlapping ranges, so equality and
ngram postings use the same distinction. Focused tests cover all keys, their
order, representative postings from each block, and literal marker escaping.
The adjacent U+FE20–U+FE2F block already agrees with MySQL and needs no
override.

The native differential command is:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1AB0:1B00 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1DC0:1E00 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 20D0:2100 --collation utf8mb4_general_ci
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range FE20:FE30 --collation utf8mb4_general_ci
```

All four corpora agree on matching rows and relevance at each token size from
1 through 10: 186 searches for U+1AB0–U+1AFF, 384 for U+1DC0–U+1DFF, 198
for U+20D0–U+20FF, and 96 for the U+FE20–U+FE2F control. Other
combining-mark blocks and full-text query shapes remain outside this finding.
