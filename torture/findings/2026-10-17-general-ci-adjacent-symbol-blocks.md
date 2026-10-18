# `utf8mb4_general_ci` adjacent symbol-block ngram audit

Following the punctuation and script-number fix, the maintained two-column
native MySQL 8.4.11 comparison was expanded across these printable Unicode
blocks at ngram token sizes 1 and 2:

| Range | Searches per token size | Differences |
| --- | ---: | ---: |
| U+2300–U+23FF | 1,536 | 0 |
| U+2400–U+24FF | 1,278 | 0 |
| U+2500–U+25FF | 1,536 | 0 |
| U+2600–U+26FF | 1,536 | 0 |
| U+2700–U+27FF | 1,536 | 0 |
| U+2800–U+28FF | 1,536 | 0 |
| U+2900–U+29FF | 1,536 | 0 |
| U+2A00–U+2AFF | 1,536 | 0 |

All 12,030 searches per size matched MySQL in result membership and
relevance. A separate MySQL 8.4.11 `WEIGHT_STRING` scan found 256 distinct
literal weights in U+2800–U+28FF. Its ngram corpus also matched across all
ten token sizes, with 1,536 searches per size. Reproduce each row with a
Release CLI build and:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 2300:2400 --collation utf8mb4_general_ci 1 2
```

Replace the range with each table row's endpoints, using an exclusive upper
endpoint (for example, `2400:2500`). The printable-character filter explains
the smaller U+2400 block count. This audit does not establish parity for
other blocks, collations, or query shapes; sizes 3–10 were run only for
U+2800–U+28FF.
