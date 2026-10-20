# Khitan Small Script two-column ngram audit

The maintained [`ngram-multicol-oracle.py`](../scripts/ngram-multicol-oracle.py)
compared fsdb with native MySQL 8.4.11 over the [Khitan Small Script block
U+18B00–U+18CFF](https://www.unicode.org/charts/nameslist/c_18B00.html).
The harness selects printable scalars, places each one in CJK text in a
two-column FULLTEXT corpus, and compares matching row IDs and relevance
scores in natural-language, Boolean, and query-expansion searches. Each
token size uses disposable MySQL and fsdb servers.

| Range | Collation | Token sizes | Searches | Differences |
|---|---|---:|---:|---:|
| U+18B00–U+18CFF, two 256-scalar pages | `utf8mb4_general_ci` | 1–2 | 5,652 | 0 |
| U+18B00–U+18CFF, two 256-scalar pages | `utf8mb4_unicode_ci` | 1–2 | 5,652 | 0 |
| U+18B00–U+18BFF | `utf8mb4_0900_ai_ci` | 1–3 | 4,608 | 0 |
| U+18B00–U+18BFF | `utf8mb4_unicode_520_ci` | 1–2 | 3,072 | 0 |
| U+18B00–U+18BFF | `utf8mb4_ja_0900_as_cs_ks` | 1–2 | 3,072 | 0 |

The block-wide lanes total 11,304 searches. The second page has fewer
searches because the harness excludes non-printable scalars. The other
collations and token size 3 were sampled on the first page only. The
harness did not check standalone equality or exact `WEIGHT_STRING()` bytes.

After building `Fsdb.Cli` in Release mode, reproduce each block-wide lane
with both pages and both collations:

```sh
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 18B00:18C00 --collation utf8mb4_general_ci 1 2
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 18C00:18D00 --collation utf8mb4_general_ci 1 2
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 18B00:18C00 --collation utf8mb4_unicode_ci 1 2
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 18C00:18D00 --collation utf8mb4_unicode_ci 1 2
```

This audit narrows the CJK ngram question for the reported block, collations,
token sizes, and search modes. MeCab, other token sizes, full-block coverage
under the other collations, exact legacy weight bytes, and other query shapes
remain open.
