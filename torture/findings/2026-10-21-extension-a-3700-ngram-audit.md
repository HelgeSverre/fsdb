# CJK Extension A U+3700–U+37FF ngram audit

The maintained [two-column ngram oracle](../scripts/ngram-multicol-oracle.py)
compared the current Release fsdb CLI with disposable native MySQL 8.4.11
servers. Every printable character in U+3700–U+37FF was indexed beside `文`,
with a leading `中` repeated for larger token sizes. For each term and mode,
the oracle compared ordered membership and relevance for natural-language,
Boolean, and query-expansion searches, including quoted terms.

| Collation | Token sizes | Searches per size | Mismatches |
|---|---:|---:|---:|
| `utf8mb4_general_ci` | 1–10 | 1,536 | 0 |
| `utf8mb4_unicode_ci` | 1–10 | 1,536 | 0 |
| `utf8mb4_unicode_520_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_0900_ai_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_ja_0900_as_cs_ks` | 1, 2 | 1,536 | 0 |

The standalone block matched 39,936 searches. A combined U+3400–U+37FF
corpus put four adjacent blocks in one index, exposing cross-block matches
and shared relevance statistics. It matched another 6,144 searches at each
of sizes 1 and 2 under both `utf8mb4_general_ci` and
`utf8mb4_unicode_ci` (24,576 searches). Each token size used fresh fsdb and
MySQL servers with the same startup ngram size. No code override was needed
for these characters.

To replay the standalone and combined lanes after building the Release CLI:

```sh
dotnet build src/Fsdb.Cli -c Release --nologo
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3700:3800 --collation utf8mb4_unicode_ci 1 2 3 4 5 6 7 8 9 10
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3400:3800 --collation utf8mb4_general_ci 1 2
```

This sampled corpus does not prove parity across Extension A. The combined
index was not checked at sizes 3–10; the standalone block's sizes 3–10 were
not checked under the other three collations. Standalone equality, exact
`WEIGHT_STRING()` bytes, cross-form equivalences, later Extension A blocks,
and MeCab remain unaudited.
