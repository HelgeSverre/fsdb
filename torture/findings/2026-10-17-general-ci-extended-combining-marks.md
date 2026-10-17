# `utf8mb4_general_ci` extended combining-mark weights

The two-column ngram differential corpus over U+1AB0–U+1AFF found false
matches under `utf8mb4_general_ci`. At token size 1, 156 membership or
relevance comparisons differed among 186 searches. A search for `᪰` also
found a document containing `᪱` and many other marks.

Native MySQL 8.4.11 gives all 80 code points distinct, literal primary
weights in codepoint order. Fsdb's ICU-based key produced only 55 distinct
keys. The shared collation-key path now escapes a synthetic marker and
preserves each written weight, so equality and ngram postings use the same
distinction. Focused tests cover all keys, their order, representative
postings, and a literal marker that must not alias a generated key.

The native differential command is:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 1AB0:1B00 --collation utf8mb4_general_ci
```

All 186 two-column searches agree on matching rows and relevance at each
token size from 1 through 10. Other combining-mark blocks and full-text
query shapes remain outside this finding.
