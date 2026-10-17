# U+2D00–U+2DFF Boolean ngram query boundary

The expanded two-column `utf8mb4_general_ci` corpus matches MySQL 8.4.11
at ngram size 1 (1,260 searches). At size 2, 152 membership or relevance
differences occur, all in Boolean mode: 38 query characters, each with plain
and quoted query forms and both membership and relevance checks. Natural
language mode and query expansion match throughout the size-2 corpus.

For example, `中ⴧ` and `"中ⴧ"` return no rows in MySQL but return document
99 in fsdb. Other affected characters include U+2D2D, U+2D66, U+2D7F,
and U+2DE0–U+2DFF. The affected set includes letters and combining marks
added after the Unicode 5.0 ctype table used by MySQL's Boolean parser.
An isolated six-document MySQL probe also returns no rows for `中` by itself
at ngram size 2, while `中ⴀ` matches its exact document. The historical
Boolean word table splits the affected queries after `中`; fsdb permits that
short surviving term to look up a size-2 posting whose collation key has
collapsed to the same key. Its short-term lookup is also used for postings
created under an earlier ngram size, so a fix must preserve those cases.

Native `WEIGHT_STRING` confirms all 256 characters in the block have
distinct literal primary weights, so this is not a collation-key alias.

Reproduce after building the Release CLI:

```sh
python3 torture/scripts/general-ci-range-weight-oracle.py 2D00 2E00
python3 torture/scripts/ngram-multicol-oracle.py --legacy-range 2D00:2E00 --collation utf8mb4_general_ci 1 2
```
