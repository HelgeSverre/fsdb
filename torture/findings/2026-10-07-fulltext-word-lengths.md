# Full-text word lengths distinguish indexing from lookup

Status: implemented for ordinary-word startup bounds, historical postings,
short-word lookup, and repeated unweighted Boolean-word scoring.

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

The CLI, option files, and `Db.withFullTextWordLengths` select current query/write
bounds independently of each document's historical indexing bounds. Minimum
values clamp to 0–16, maximum values to 10–84; defaults remain 3 and 84. Ngram
indexes are independent. Variables remain GLOBAL and read-only.

Unsigned startup parsing follows the native option parser: negative spellings
clamp to zero, K/M/G/T/P/E suffixes scale by powers of 1024, and only the first
suffix character matters (`64MB` and `1e2` are accepted). Bare suffixes such as
`K` mean zero. Overflow and unrecognized suffixes fail. Option-file assignments
are applied in order, followed by command-line assignments.

Metadata ALTER and unrelated-column updates preserve historical postings.
Changed indexed text, physical ALTER, and separate DROP/ADD FULLTEXT statements
use current bounds. The oracle verifies these distinctions through restarts.

Query expansion extracts seed words using current length bounds. A seed row
containing `orchard xy abcdefghijk` expands through `xy` and `abcdefghijk` under
1–84, but not under 3–10. Loosening the bounds also allows formerly unindexed
words in old seed text to reach postings added under the new settings. Historical
postings themselves remain unchanged.

Snapshot format 18 (`FSNI`) adds a bounded-word tokenizer tag to per-document
rule tables. Readers retain support for older formats; a real format-17 fixture
verifies default historical bounds. WAL tag `0x1E` captures the current word
bounds around writes and DDL, including transaction and prepared-XA events.
Startup reapplies current bounds without changing historical document rules or
forcing deferred stopword loading. Older binaries cannot read `FSNI` snapshots
or replay the new WAL tag.

## Verification

`just check` passes all 2,931 tests with no build warnings or errors. Regressions
cover independent startup bounds, parser forms, per-store reporting, ngram
independence, query expansion, both builder orders, WAL/checkpoint history,
metadata versus physical mutations, prepared-XA publication, and an actual
format-17 snapshot fixture.

The maintained native word-length oracle passes against MySQL 8.4.11. Executable
wire checks pass the same restart/ALTER/expansion matrix, startup values,
option-file precedence, repeated options, malformed values, and help output.
The existing stopword and natural-phrase oracles also pass.

All 49 compatibility contracts pass 5,111 steps without differences:
`torture/artifacts/runs/20261007T093010281-46658/contracts`.
The durability lane preserves all 101 acknowledged commits across 12 crash
restarts, including automatic checkpoints, WAL tails, snapshots, schema, and
torn-tail repair:
`torture/artifacts/runs/20261007T093020860-46809/durability-seed101-workers4-ops100-restarts8-checkpoint16`.
