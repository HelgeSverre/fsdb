# `utf8mb4_general_ci` format-control and space ngrams

The two-column CJK corpus still had size-4 and size-5 false-positive matches
after the halfwidth-mark fix. At token size 5, a query containing U+200D
(zero-width joiner) also matched a document containing U+FE0F (variation
selector); a U+00A0 (nonbreaking space) query also matched a U+3000
(ideographic space) document.

On native MySQL 8.4.11, direct `utf8mb4_general_ci` equality rejects both
pairs. `WEIGHT_STRING` reports distinct, positional weights for U+034F,
U+00A0, U+180E, U+200B, U+200C, U+200D, U+2060, U+3000, and U+FE0F.
ICU primary comparison ignored or equated some of these characters. Fsdb now
gives the audited characters escaped, distinct keys in this collation.

A focused native MySQL size-5 full-text probe with eight documents also
established the search-mode boundary: natural and query-expansion searches for
the U+200D, U+FE0F, and U+00A0 strings each find only their own document;
Boolean search finds the U+FE0F document but returns no rows for U+200D or
U+00A0. The Expecto regression checks those results.

Build the Release CLI and rerun the 387-search two-column corpus at each
token size:

```sh
python3 torture/scripts/ngram-multicol-oracle.py --collation utf8mb4_general_ci
```

| Token size | Before | After |
| ---: | ---: | ---: |
| 1 | 292 | 260 |
| 2 | 134 | 68 |
| 3 | 86 | 17 |
| 4 | 20 | 4 |
| 5 | 18 | 0 |
| 6–10 | 0 | 0 |

The parent and changed runs share the same mismatch identity definition
(size, search mode, query, and membership versus relevance). Of 550 previous
mismatch entries, 201 resolved and none were introduced. The remaining 349
entries concentrate at sizes 1–3. Size 4 retains only repeated Boolean
`ガラス` mismatches: fsdb's short Boolean spelling addresses the size-4
posting for decomposed `カ\u3099ラス` in document 55, while MySQL returns no
row. The term occurs twice in the corpus, producing four membership/relevance
entries. This does not prove exact general-collation parity beyond
the audited characters and corpus.
