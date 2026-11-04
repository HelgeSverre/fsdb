# CJK Extension A U+3B00–U+3BFF ngram audit

The maintained [two-column ngram oracle](../scripts/ngram-multicol-oracle.py)
compared the Release fsdb CLI with disposable MySQL 8.4.11 servers. Each
printable character in U+3B00–U+3BFF was indexed beside `文`, with a leading
`中` repeated for larger token sizes. Natural-language, Boolean, and query
expansion searches, including quoted terms, compared ordered membership and
relevance scores.

| Collation | Token sizes | Searches per size | Mismatches |
|---|---:|---:|---:|
| `utf8mb4_general_ci` | 1–10 | 1,536 | 0 |
| `utf8mb4_unicode_ci` | 1–10 | 1,536 | 0 |
| `utf8mb4_unicode_520_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_0900_ai_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_ja_0900_as_cs_ks` | 1, 2 | 1,536 | 0 |

The standalone block matched 39,936 searches. A combined U+3400–U+3BFF
index under `utf8mb4_unicode_ci` matched another 12,288 searches at each
of token sizes 1 and 2, exercising cross-block postings and relevance
statistics. Each size used fresh MySQL and fsdb servers with the same startup
ngram size. No production change was needed for this block.

```sh
dotnet build src/Fsdb.Cli -c Release --nologo
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3B00:3C00 --collation utf8mb4_unicode_ci 1 2 3 4 5 6 7 8 9 10
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3400:3C00 --collation utf8mb4_unicode_ci 1 2
```

The other standalone collations used the same first command with their
respective collation names and audited sizes. This corpus does not prove
parity for later Extension A blocks, exact legacy weight bytes, or MeCab
on plugin-enabled MySQL servers.
