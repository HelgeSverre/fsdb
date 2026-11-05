# CJK Extension A U+4400–U+44FF ngram audit

The maintained [two-column ngram oracle](../scripts/ngram-multicol-oracle.py)
compared the Release fsdb CLI with disposable native MySQL 8.4.11 servers.
Each printable U+4400–U+44FF character was indexed beside `文`, with a leading
`中` for larger token sizes. Natural-language, Boolean, and query-expansion
searches, including quoted terms, compared ordered membership and relevance
scores.

| Collation | Token sizes | Searches per size | Mismatches |
| --- | --- | ---: | ---: |
| `utf8mb4_general_ci` | 1–10 | 1,536 | 0 |
| `utf8mb4_unicode_ci` | 1–10 | 1,536 | 0 |
| `utf8mb4_unicode_520_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_0900_ai_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_ja_0900_as_cs_ks` | 1, 2 | 1,536 | 0 |

The standalone block matched 39,936 searches. A combined U+3400–U+44FF
index also matched 1,536 targeted searches at each of sizes 1 and 2 under
`utf8mb4_unicode_ci`, searching the new block against accumulated documents.
Across both runs, 43,008 searches matched MySQL. No engine change was needed.

The commands used the repository's uv workflow, for example:

```sh
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 4400:4500 --collation utf8mb4_unicode_ci 1 2 3 4 5 6 7 8 9 10
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3400:4500 --legacy-term-range 4400:4500 \
  --collation utf8mb4_unicode_ci 1 2
```

This does not establish parity for all searches across the combined range,
later Extension A blocks, cross-form equivalences, other query shapes, or
MeCab on plugin-enabled servers.
