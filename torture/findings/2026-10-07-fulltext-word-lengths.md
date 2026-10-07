# Full-text word lengths distinguish indexing from lookup

Status: short-word lookup and repeated unweighted Boolean-word scoring are
fixed. Configurable startup word lengths remain open.

The [native oracle](../scripts/fulltext-word-length-oracle.py) verifies MySQL
8.4.11 using disposable native servers and data directories:

```sh
python3 torture/scripts/fulltext-word-length-oracle.py
```

## Short query spellings

Under `utf8mb4_0900_ai_ci`, the one-character ligature `ﬃ` compares equal to
`ffi`. A default full-text index contains rows 1=`ffi`, 2=`orchard`, 3=`zzzz`,
and 4=`ﬃ`. The minimum indexed length is three, so row 4 has no indexed word.

Natural, Boolean, and query-expansion searches agree:

| Query | Matches |
|---|---|
| `ﬃ` | 1 |
| `ﬃ ﬃ` | 1 |
| `+ﬃ` | 1 |
| `"ﬃ"` | none |
| `"ﬃ orchard"` | 2 |

Plain lookup can address an indexed collation equivalent without applying the
indexing minimum. Phrase anchors still skip short words. Fsdb separates the
lookup maximum from the minimum/maximum checks used for indexing and phrases;
both general and optimized lookup paths use the same rule.

## Repeated Boolean words

For flat ordinary-word queries using only optional or required terms, repeated
collation-equivalent words increase document frequency rather than adding the
same posting contribution repeatedly. In the four-row corpus, `ffi ffi`,
`ﬃ ﬃ`, `ffi ﬃ`, and `+ffi +ﬃ` each score row 1 as `0.09062`, rounded to five
places. Three repetitions score `0.01561`.

Fsdb reuses its occurrence-aware word scoring for this query shape and applies
required-word membership before scoring. Both the general evaluator and flat
optimized path share it. Weighted, excluded, prefix, phrase, and nested terms
retain their existing evaluation; the new recurrence assertions do not cover
those combinations.

## Startup changes and historical postings

The restart oracle disables stopwords to isolate length filtering. It builds
rows 1=`xy`, 2=`abcdefghijk`, 3=`orchard`, and 4=`zzzz` under minimum 1 and maximum
84. It then restarts with minimum 3 and maximum 10, inserts row 5=`xy abcdefghijk
orchard`, and finally restarts with the original bounds.

| Stage | Exact `xy` | Exact `abcdefghijk` | Exact `orchard` | Boolean `abc*` |
|---|---|---|---|---|
| Initial 1–84 | 1 | 2 | 3 | 2 |
| Restricted 3–10, after insert | 1 | none | 3,5 | 2 |
| Restored 1–84 | 1 | 2 | 3,5 | 2 |

Both natural and Boolean exact lookups retain access to the older short word.
The current maximum rejects an older long word, but a Boolean prefix can still
reach its posting. Restoring the old bounds reveals that posting again. New
writes under the restricted bounds do not index either out-of-range word.

Numeric startup configuration therefore needs separate current query/write
bounds and each document's historical indexing bounds. Recovery must retain
old postings without re-tokenizing them under the current values. The existing
indexing-rule model retains historical ngram and stopword settings; configurable
ordinary-word bounds still need to be threaded through it and persistence.

## Verification

`just check` passes all 2,922 tests with no build warnings or errors. The native
word-length and natural-phrase oracles pass. All 49 compatibility contracts pass
5,111 steps without differences, including text/prepared short-word membership,
phrase anchors, repeated terms, and relevance scores:
`torture/artifacts/runs/20261007T090047794-22656/contracts`.
