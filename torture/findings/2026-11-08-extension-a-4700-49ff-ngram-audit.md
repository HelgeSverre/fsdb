# CJK Extension A U+4700–U+49FF ngram audit

The maintained two-column oracle compared a Release build of fsdb with
disposable native MySQL 8.4.11 servers. Each printable U+4700–U+49FF scalar
was indexed beside `文`; natural-language, Boolean, and query-expansion
searches compared ordered membership and relevance at ngram token sizes 1
and 2.

| Collation | Searches per size | Search mismatches | Exact singleton weights per size | Weight mismatches |
| --- | ---: | ---: | ---: | ---: |
| `utf8mb4_general_ci` | 4,608 | 0 | 768 | 0 |
| `utf8mb4_unicode_520_ci` | 4,608 | 0 | 768 | 0 |
| `utf8mb4_0900_ai_ci` | 4,608 | 0 | — | — |

Across the three collations and two token sizes, 27,648 searches matched.
The two legacy collations also matched 3,072 exact singleton
`WEIGHT_STRING` checks. The default 0900 collation's ngram search parity does
not imply byte-for-byte weight parity: an exploratory `--weight-checks` run
found all 768 singleton weights different at size 1, with MySQL starting
U+4700 at `FB80C700` and fsdb at `FC452000`. The repository already records
that 0900 `WEIGHT_STRING` uses host ICU sort-key bytes; this audit did not
change that implementation.

Commands:

```sh
dotnet build src/Fsdb.Cli -c Release --nologo -v q
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 4700:4A00 --collation utf8mb4_general_ci --weight-checks 1 2
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 4700:4A00 --collation utf8mb4_unicode_520_ci --weight-checks 1 2
uv run --with pymysql python torture/scripts/ngram-multicol-oracle.py \
  --legacy-range 4700:4A00 --collation utf8mb4_0900_ai_ci 1 2
```

Other token sizes, collations, combined-range searches, cross-form
equivalence, and MeCab on a plugin-enabled server remain open.
