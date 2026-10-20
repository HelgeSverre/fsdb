# CJK Extension A U+3500–U+35FF ngram audit

The maintained [multi-column ngram oracle](../scripts/ngram-multicol-oracle.py)
compared fsdb with disposable native MySQL 8.4.11 servers for every printable
character in U+3500–U+35FF. Each document put an Extension A character before
`文`, with a leading `中` at token size two. The oracle searched the two-column FULLTEXT
index in natural-language, Boolean, and query-expansion modes, including
quoted terms, and compared ordered membership and relevance scores.

| Collation | Token sizes | Searches per size | Mismatches |
|---|---:|---:|---:|
| `utf8mb4_general_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_unicode_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_unicode_520_ci` | 1, 2 | 1,536 | 0 |
| `utf8mb4_ja_0900_as_cs_ks` | 1, 2 | 1,536 | 0 |
| `utf8mb4_0900_ai_ci` | 1, 2 | 1,536 | 0 |

That is 15,360 matched searches. Each token size used fresh fsdb and MySQL
servers with the same startup ngram size and the current Release CLI. To
replay one lane:

```sh
dotnet build src/Fsdb.Cli -c Release --nologo
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 3500:3600 --collation utf8mb4_unicode_ci 1 2
```

This narrows the sampled Extension A gap beyond the earlier U+3400–U+34FF
lane. The oracle did not compare standalone equality or exact
`WEIGHT_STRING()` bytes for this range. Other Extension A blocks, cross-form
equivalences, larger ngram sizes, and MeCab remain open.
