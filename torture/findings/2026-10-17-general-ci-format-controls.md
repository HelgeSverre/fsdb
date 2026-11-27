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

## Pinned general-collation weights

A follow-up against the digest-pinned MySQL 8.4.11 found additional collisions
in CJK bigrams. Natural search distinguished the invisible operators
U+2061–U+2064, U+FEFF, and several bidi controls and spaces. ICU collapsed
those spellings and folded U+0345 into the same class as the invisible
controls. MySQL groups U+0345 with upper- and lowercase Greek iota, including
in Boolean exact and wildcard searches.

Fsdb now builds full-text keys for `utf8mb3_general_ci` and
`utf8mb4_general_ci` from MySQL's lowercase mappings and one weight per
Unicode scalar. Regenerating the resource checks all 63,488 BMP scalar
weights against both collations and stores the 1,108 nonidentity weights.
Supplementary scalars share the `FFFD` weight in `utf8mb4_general_ci`.

The original 234-search matrix had 36 membership differences before the
change and zero afterward. It exercised natural, Boolean, and Boolean
wildcard search with the index present before inserts and with a bulk-built
index. Two additional 234-search matrices introduced the Greek iota
spellings and agreed on both legacy general collations. The broader
bulk-index oracle also matched in membership and relevance across its
22,320 searches. The maintained two-column CJK corpus and the complete
U+2000–U+206F range matched native MySQL 8.4.11 at every token size from
1 through 10, for another 10,590 searches.

Checkpoints written with the earlier keys can hold one cache tail for several
terms that now differ. On recovery, Fsdb rebuilds postings from the stored
rows and spellings, syncs the affected legacy cache, and retains recorded
deletes. The snapshot regression checks both that upgrade and a subsequent
checkpoint with the new keys.

To regenerate the weight resource, run the query and pack stages with the
MySQL image pinned in `torture/config/toolchain.json`.

```sh
ruby torture/scripts/generate-general-fulltext-weights.rb queries |
  mysql --batch --skip-column-names [connection-options] > weights.tsv
ruby torture/scripts/generate-general-fulltext-weights.rb pack weights.tsv > src/Fsdb/Sql/GeneralFullTextWeights.bin
```
