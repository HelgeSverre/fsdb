# InnoDB query expansion uses every first-pass match

Status: fixed. Fsdb expands from all matching seed documents. It does not apply
MyISAM's `ft_query_expansion_limit` to its InnoDB-style search engine. Startup
configuration and variable reporting also match the verified native cases.

## Native evidence

The [oracle](../scripts/fulltext-expansion-seeds-oracle.py) runs native MySQL
8.4.11 with `ft_query_expansion_limit` set to 0, 1, 2, 20, and 1000, rebuilding
both InnoDB and MyISAM tables for each value. It uses disposable servers and data,
a 64 MiB buffer pool, and 64 MiB redo capacity:

```sh
python3 torture/scripts/fulltext-expansion-seeds-oracle.py
```

The corpus contains 25 seed rows with `orchard` and a distinct companion word,
25 rows containing only their respective companion word, and one unrelated row.
Searching for `orchard` with query expansion produces:

| Reported expansion limit | InnoDB matches | MyISAM matches |
|---|---:|---:|
| 0 | 50 | 25 |
| 1 | 50 | 26 |
| 2 | 50 | 27 |
| 20 | 50 | 45 |
| 1000 | 50 | 50 |

Every InnoDB seed contributes its companion word, regardless of the reported
limit. MyISAM selects only the configured number of seeds; its tied seed order
is not part of this assertion.

The official [MySQL 8.4.11 InnoDB implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/fts/fts0que.cc#L3655-L3684)
supports the observation: `fts_expand_query` iterates all first-pass document
IDs and collects their terms, with no ranking-based seed cutoff.

## Implementation

The former 20-document cutoff returned only 45 of the 50 expected matches at
the default setting. Removing the cutoff and seed-ranking sort allows every
visible first-pass match to contribute searchable terms. Predicate candidates
still restrict final results without narrowing the seed pass. Final relevance
scoring and transaction visibility retain their existing paths.

The Expecto regression covers all 50 matches and a candidate restricted to a
companion row beyond the former cutoff. The `fulltext-expansion-seeds` contract
compares text and prepared queries, ordinary and restricted results, and rounded
relevance scores against MySQL.

Larger seed sets require reading more documents than the old cutoff allowed.
The implementation keeps the existing straightforward token collection; no
performance claim is inferred from removing the sort.

The CLI, option files, and `Db.withFullTextQueryExpansionLimit` configure the
reported value, defaulting to 20 and clamping to 0–1000. Unsigned startup
parsing accepts MySQL's size suffixes and clamps negative spellings to zero;
unrecognized suffixes and overflow fail. Command-line assignments follow
option-file assignments, and the last assignment wins.

`@@ft_query_expansion_limit`, its GLOBAL form, and both SHOW scopes report the
configured value. The SESSION expression fails with error 1238. Both SESSION
and GLOBAL SET assignments also fail with error 1238, SQLSTATE HY000: read-only
validation precedes assignment-scope validation for this variable.

The value is isolated per database instance, survives either order of the
configuration and data-directory builders, and resets at restart unless
configured again. It does not enter WAL records or snapshots and does not
change query expansion. MyISAM storage-engine behavior remains outside fsdb's
InnoDB-shaped store. Ordinary-word settings are covered by the
[word-length oracle](2026-10-07-fulltext-word-lengths.md).

## Verification

`just check` passes all 2,933 tests with no build warnings or errors. The native
seed-limit oracle verifies startup clamping, read-only scope errors, and the
distinct InnoDB/MyISAM expansion behavior. Executable checks cover accepted and
rejected options, both CLI assignment forms, option-file precedence, repeated
flags, default reset, help/version output, and all 50 InnoDB matches even at
limit zero.

All 49 compatibility contracts pass 5,117 steps without differences, including
variable reporting and scope errors alongside text/prepared membership and
relevance checks:
`torture/artifacts/runs/20261007T094131462-51480/contracts`.
