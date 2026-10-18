# Han extension and Hangul Jamo ngram audit

The maintained [two-column ngram oracle](../scripts/ngram-multicol-oracle.py)
compared fsdb with native MySQL 8.4.11 under `utf8mb4_general_ci` for four
printable-character ranges. Each query checks row membership and relevance
in natural-language, Boolean, and query-expansion modes with the character
embedded in CJK text.

| Range | Block | Searches per token size | Mismatches |
|---|---|---:|---:|
| U+3400–U+34FF | CJK Unified Ideographs Extension A sample | 1,536 | 0 |
| U+20000–U+200FF | CJK Unified Ideographs Extension B sample | 1,536 | 0 |
| U+A960–U+A97F | Hangul Jamo Extended-A | 174 | 0 |
| U+D7B0–U+D7FF | Hangul Jamo Extended-B | 432 | 0 |

The 3,678 searches at each of ngram sizes 1 and 2 total 7,356 matching
comparisons. Reproduce with a Release CLI build and:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 3400:3500 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 20000:20100 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range A960:A980 --collation utf8mb4_general_ci 1 2
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range D7B0:D800 --collation utf8mb4_general_ci 1 2
```

This bounds the sampled parity claim to individual characters in these
two-column documents at token sizes 1 and 2. It does not establish MeCab,
arbitrary mixed-character searches, or exact primary-weight bytes.
