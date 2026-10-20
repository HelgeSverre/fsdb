# Tangut two-column ngram audit

The maintained [`ngram-multicol-oracle.py`](../scripts/ngram-multicol-oracle.py)
compared native MySQL 8.4.11 with fsdb on Tangut ideographs, Tangut
Components, and the Tangut Supplement. Each selected scalar appears inside
CJK text in a two-column FULLTEXT corpus. The harness compares matching row
IDs and relevance scores in natural-language, Boolean, and query-expansion
modes. Runs used disposable servers and the configured ngram token size.

| Range | Collation | Token sizes | Searches | Differences |
|---|---|---:|---:|---:|
| U+17000–U+187FF, in 24 pages of 256 scalars | `utf8mb4_general_ci` | 1–2 | 73,632 | 0 |
| U+18800–U+18AFF, in three pages | `utf8mb4_general_ci` | 1–2 | 9,216 | 0 |
| U+18D00–U+18D8F | `utf8mb4_general_ci` | 1–2 | 108 | 0 |
| U+17000–U+170FF | `utf8mb4_unicode_520_ci`, `utf8mb4_0900_ai_ci`, `utf8mb4_ja_0900_as_cs_ks` | 1–2 | 9,216 | 0 |
| U+18800–U+188FF | the same three collations | 1–2 | 9,216 | 0 |
| U+17000–U+170FF | `utf8mb4_general_ci` | 3 | 1,536 | 0 |
| U+17000–U+170FF | `utf8mb4_unicode_ci` | 1–2 | 3,072 | 0 |

The broad general-collation lane totals 82,956 searches. The smaller
Supplement count reflects the harness's printable-scalar selection in that
range. The full-size-3 and other-collation results are representative pages,
not block-wide claims. The harness did not run standalone equality or exact
`WEIGHT_STRING()` comparisons in this campaign.

To reproduce the broad lane after building `Fsdb.Cli` in Release mode, run
the maintained harness once per 256-scalar page from `17000:17100` through
`18A00:18B00`. For example:

```sh
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 17000:17100 --collation utf8mb4_general_ci 1 2
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 18A00:18B00 --collation utf8mb4_general_ci 1 2
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 18D00:18D90 --collation utf8mb4_general_ci 1 2
```

This closes the sampled Tangut two-column ngram parity question for the
reported ranges, collations, token sizes, and search modes. MeCab, other
token sizes, full-block coverage under the other collations, exact legacy
weight bytes, and other CJK query shapes remain open.
