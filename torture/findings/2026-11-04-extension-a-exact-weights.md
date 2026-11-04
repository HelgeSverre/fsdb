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

## Implicit ideograph encoder follow-up

The [MySQL 8.4.11 UCA implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/strings/ctype-uca.cc)
computes two primary weights for ideographs: a lead determined by character
group and the high code-point bits, followed by the low fifteen bits with
bit 15 set. Native probes found the legacy 4.0 and 5.2 boundaries at
U+4DB5/U+4DB6 and U+9FA5/U+9FA6. Both Unicode collations use the unassigned
lead after those boundaries; `utf8mb4_general_ci` uses the literal two-byte
code point throughout the audited BMP ranges.

Fsdb now encodes strings composed entirely of U+3400–U+4DBF Extension A and
U+4E00–U+9FFF unified ideographs with those rules. The differential oracle
compared every adjacent pair inside both ranges under all three collations:
82,746 pair results, zero exact-byte mismatches. The focused regression pins
the assigned/unassigned boundaries, the U+8000 lead transition, and a pair
mixing the two ideograph ranges. The oracle's `--pair-with` mode independently
confirmed `U+3B00 U+4E00` against MySQL under all three collations with zero
mismatches. This changes `WEIGHT_STRING` output; ngram
membership and relevance continue to use the existing collation keys.

The encoder also handles printable ASCII mixed with a BMP ideograph. Under
`*_general_ci`, MySQL's ASCII weight is its uppercase code point. The legacy
Unicode collations use distinct UCA 4.0 and 5.2 primary weights, with
lowercase letters sharing uppercase weights. All 95 ASCII-plus-U+3B00 pairs
match exact bytes under each of the five collations; 16 U+3B00–U+3B0F pairs
with a trailing ASCII `A` also match under `utf8mb4_unicode_520_ci`. Other
mixed text, supplementary ideographs, and other legacy-collation weight
families remain open.

The same ASCII weights now apply to printable ASCII-only strings. Across all
five collations, 95 singleton weights, 95 ASCII-plus-`A` pairs, and 95
`AS CHAR(1)` truncations of ASCII-plus-U+3B00 match MySQL exactly. Another
475 `AS CHAR(4)` padded ASCII-plus-U+3B00 cases match. These probes cover
space padding without using ICU weights; other mixed text remains open.

The equivalent `utf8mb3_general_ci` and `utf8mb3_unicode_ci` collations use
the same BMP weights. The oracle now selects the matching charset introducer
and compared all 55,164 adjacent pairs across the two ranges and both
`utf8mb3` collations, again with zero mismatches. A cross-range pair also
matched under both. Across all five collations, the audited adjacent-pair
total is 137,910 with zero exact-byte differences.
