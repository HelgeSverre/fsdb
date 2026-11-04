# CJK Extension I two-column ngram audit

The maintained `ngram-multicol-oracle.py` compared the printable scalars in
U+2EBF0–U+2EE5F against native MySQL 8.4.11 and the current Release build.
Each scalar was inserted in a two-column `FULLTEXT` fixture and searched in
natural language, Boolean, and query-expansion modes. The oracle compared
both result membership and relevance scores.

| Collation | Token size | Searches | Mismatches |
|---|---:|---:|---:|
| `utf8mb4_0900_ai_ci` | 1–10 | 37,320 | 0 |
| `utf8mb4_unicode_ci` | 1–10 | 37,320 | 0 |
| `utf8mb4_ja_0900_as_cs_ks` | 1–10 | 37,320 | 0 |

Commands used `uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py
<sizes> --legacy-range 2EBF0:2EE60 --collation <collation>` from the repository
root. Every size and collation tested 3,732 searches. Each token size used
disposable native MySQL and fsdb servers. The
script's legacy-equality lane was not enabled for this arbitrary range, so
these results establish two-column ngram search parity for the sampled modes
and sizes, not general collation equality. Japanese sizes 3–10 were completed
in a separate current-head replay using the same fixture and oracle. MeCab
and other query shapes remain open.
