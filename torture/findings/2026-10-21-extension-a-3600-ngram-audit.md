# CJK Extension A U+3600–U+36FF ngram audit

The maintained [two-column ngram oracle](../scripts/ngram-multicol-oracle.py)
compared the current Release fsdb CLI with disposable native MySQL 8.4.11
servers. The new lane indexed every printable U+3600–U+36FF character beside
`文`, with a leading `中` repeated for larger token sizes. Each mode/term pair
was checked for ordered membership and relevance in natural-language,
Boolean, and query-expansion modes, including quoted terms.

| Collation | Token sizes | Searches per size | Mismatches |
|---|---:|---:|---:|
| `utf8mb4_general_ci` | 1–10 | 1,536 | 0 |
| `utf8mb4_unicode_ci` | 1–10 | 1,536 | 0 |
| `utf8mb4_unicode_520_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_ja_0900_as_cs_ks` | 1, 2 | 1,536 | 0 |
| `utf8mb4_0900_ai_ci` | 1, 2 | 1,536 | 0 |

The new block matched on 39,936 mode/term searches. A combined U+3400–U+36FF
corpus, which places three adjacent blocks in one index and exposes
cross-block matches and shared relevance statistics, also matched MySQL:
4,608 searches per size at sizes 1 and 2 under both `utf8mb4_general_ci` and
`utf8mb4_unicode_ci` (18,432 more searches). Each token size used fresh
fsdb and MySQL servers with the same startup ngram size. No code override was
needed for these characters.

To replay a block lane or the combined corpus after building the Release CLI:

```sh
dotnet build src/Fsdb.Cli -c Release --nologo
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3600:3700 --collation utf8mb4_unicode_ci 1 2 3 4 5 6 7 8 9 10
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3400:3700 --collation utf8mb4_general_ci 1 2
```

This is an audited corpus, not a proof for all of Extension A. The combined
corpus was not run at sizes 3–10, and the block's sizes 3–10 were not run
under the three other collations. Standalone equality, exact `WEIGHT_STRING()`
bytes, other Extension A blocks, cross-form equivalences, and MeCab remain
outside this audit.
