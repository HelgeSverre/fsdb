# Extension A exact legacy weight bytes

The [range weight oracle](../scripts/general-ci-range-weight-oracle.py) now
has `--compare-fsdb`, which runs the Release fsdb CLI and disposable native
MySQL 8.4.11 servers against the same `HEX(WEIGHT_STRING(...))` queries. It
compared every single character in U+3B00–U+3BFF under three collations:

| Collation | Characters | Exact-byte mismatches | MySQL U+3B00 | fsdb U+3B00 |
|---|---:|---:|---|---|
| `utf8mb4_general_ci` | 256 | 256 | `3B00` | `FAEE9200` |
| `utf8mb4_unicode_ci` | 256 | 256 | `FB80BB00` | `FAEE9200` |
| `utf8mb4_unicode_520_ci` | 256 | 256 | `FB80BB00` | `FAEE9200` |

The `--pair-next` follow-up compared all 256 adjacent pairs from
U+3B00–U+3C00 under each collation. All 768 pair results also differ. At
U+3B00 followed by U+3B01, MySQL returns `3B003B01` under `general_ci`
and `FB80BB00FB80BB01` under both Unicode collations; fsdb returns
`FAEE92FAEF9C00` in all three cases. This confirms the divergence for
composed strings as well as isolated characters.

MySQL's general-collation weights in this block are the literal two-byte
code points. Both tested Unicode collations use a four-byte implicit weight
beginning `FB80BB00` at U+3B00. Fsdb's `WeightOf` currently returns ICU sort
key bytes, which encode a different representation. This is an
exact `WEIGHT_STRING` byte divergence, not a new ngram membership or relevance
failure: the [U+3B00 ngram audit](2026-11-04-extension-a-3b00-ngram-audit.md)
matched MySQL across its 64,512 searches.

Changing only this block's single-character output would leave mixed strings,
expansions, and the rest of each legacy collation inconsistent. A general
byte-compatible legacy weight encoder needs separate validation for composed
strings and other Unicode ranges before replacing the current ICU output.

```sh
dotnet build src/Fsdb.Cli -c Release --nologo
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py \
  --compare-fsdb --collation utf8mb4_unicode_ci 3B00 3C00
uv run --with pymysql python torture/scripts/general-ci-range-weight-oracle.py \
  --compare-fsdb --pair-next --collation utf8mb4_unicode_ci 3B00 3C00
```
