# Word stopwords and surviving postings

Status: implemented. Word indexing filters stopwords before adding postings;
ordinary word lookup can still find an indexed collation equivalent or a
historical posting. Phrase anchors retain their separate stopword rules.

The [native oracle](../scripts/fulltext-word-stopword-postings-oracle.py) uses
MySQL 8.4.11 with this corpus:

| id | body |
|---|---|
| 1 | the |
| 2 | thé |
| 3 | THE |
| 4 | tHe |
| 5 | zzzz |

Under `utf8mb4_0900_ai_ci`, natural, Boolean, and query-expansion searches for
`the`, `thé`, and `THE` return only row 2. Literal stopped spellings in rows
1, 3, and 4 never contribute postings. Under `utf8mb4_0900_as_cs` and
`utf8mb4_bin`, `thé` returns row 2 while `the` and `THE` return no rows.

The Boolean prefix `th*` returns only row 2 in all three collations. The phrase
`"the zzzz"` returns row 5: its leading stopword is omitted as a phrase anchor,
even when ordinary lookup of `the` can find row 2. Repeated word queries and
query expansion follow the same posting boundary. After ANALYZE, the natural
score for `thé` is approximately 0.48855907, using one matching document among
five rather than counting the filtered spellings.

Fsdb previously kept all word tokens in exact postings and applied stopword
filtering during lookup. That both rejected legitimate `the` searches and made
`thé` searches include filtered rows through their shared collation key.

The implementation uses one historical-document token filter for exact postings,
prefix postings, removal, and expansion seeds. Ordinary word lookup checks token
length and then reads those postings; it does not apply the active stopword list
again. Both optimized dictionary paths follow this rule. Original tokens remain
available for phrase positions.

This also preserves the word lookup behavior observed after a custom source
reload: an old posting remains searchable even if newer documents filter that
word. Core and snapshot regressions cover a previously unfiltered document beside
newer filtered documents, including subsequent insertion and removal. Custom
source resolution and policy encoding remain separate open work in the
[custom-stopword finding](2026-10-07-fulltext-custom-stopwords.md).

## Verification

Both native MySQL and fsdb pass the maintained SQL oracle, including its relevance
checks. `just check` passes all 2,900 tests with no build warnings or errors.
The existing stopword configuration matrix also passes on fsdb.

All 47 MySQL contracts (5,007 steps) pass without differences:
`torture/artifacts/runs/20261007T062147481-85786/contracts`.

The durability lane passes 12 crash restarts and recovers all 67 acknowledged
commits, including checkpoint, WAL-tail, snapshot, schema, and torn-tail checks:
`torture/artifacts/runs/20261007T062202882-85798/durability-seed101-workers4-ops100-restarts8-checkpoint16`.

No persistence format changes or known-gap suppressions are included.
