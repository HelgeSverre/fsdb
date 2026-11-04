# CJK Extension A U+3800–U+38FF ngram audit

The maintained [two-column ngram oracle](../scripts/ngram-multicol-oracle.py)
compared the Release fsdb CLI with disposable native MySQL 8.4.11 servers.
Every printable character in U+3800–U+38FF was indexed beside `文`, with a
leading `中` repeated for larger token sizes. The oracle compared ordered
membership and relevance for natural-language, Boolean, and query-expansion
searches, including quoted terms.

| Collation | Token sizes | Searches per size | Mismatches |
|---|---:|---:|---:|
| `utf8mb4_general_ci` | 1–10 | 1,536 | 0 |
| `utf8mb4_unicode_ci` | 1–10 | 1,536 | 0 |
| `utf8mb4_unicode_520_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_0900_ai_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_ja_0900_as_cs_ks` | 1, 2 | 1,536 | 0 |

The standalone block matched 39,936 searches. A combined U+3400–U+38FF
corpus put five adjacent blocks in one index, exposing cross-block matches
and shared relevance statistics. It matched another 7,680 searches at each
of sizes 1 and 2 under `utf8mb4_unicode_ci` (15,360 searches). Each token
size used fresh fsdb and MySQL servers with the same startup ngram size. No
code change was needed for these characters.

To replay after building the Release CLI:

```sh
dotnet build src/Fsdb.Cli -c Release --nologo
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3800:3900 --collation utf8mb4_unicode_ci 1 2 3 4 5 6 7 8 9 10
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3400:3900 --collation utf8mb4_unicode_ci 1 2
```

This sampled corpus does not prove parity across Extension A. The combined
index was not checked under other collations or at sizes 3–10. Standalone
equality, exact `WEIGHT_STRING()` bytes, cross-form equivalences, later
Extension A blocks, and MeCab remain unaudited.
