# Custom stopword loading after restart

Status: implemented for the tested custom-source loading paths. MySQL 8.4.11
and fsdb load remembered custom stopword sources on first full-text use,
including source edits after restart and cold index creation.

Run the [native oracle](../scripts/fulltext-stopword-loading-oracle.py):

```sh
python3 torture/scripts/fulltext-stopword-loading-oracle.py
```

The oracle uses disposable native servers, one data directory across each
restart, a 64 MiB buffer pool, and 64 MiB redo capacity. It passes on MySQL 8.4.11.

## Operations that load the source

Each table initially has a full-text index on `body`, another text column
`other`, and rows 1=`orchard`, 2=`cobalt`, 3=`the` in both columns. Its custom
source contains `orchard` when built, then changes to `cobalt` before restart.
After restarting, the oracle performs one operation and adds a full-text index
on `other`. Searching the new index for `cobalt` exposes whether the source was
loaded before construction.

| First operation after restart | New index matches `cobalt` |
|---|---|
| None | 2 |
| Ordinary SELECT or COUNT | 2 |
| Full-text query, whether or not anything matches | none |
| Insert an actual row | none |
| UPDATE with no matching row | 2 |
| UPDATE changing indexed text | none |
| UPDATE assigning identical text | 2 |
| UPDATE changing only the primary key | 2 |
| DELETE, whether or not a row is removed | 2 |
| Rename the full-text index | 2 |
| Add an ordinary index | 2 |
| Change the table comment | 2 |

The tested full-text queries, insert, and indexed-text update load the source.
The other operations leave it unloaded. This does not establish the behavior
of every UPDATE shape or ALTER algorithm.

## Source edits after restart

Three tables use the same initial corpus and source. After restarting with
source contents `cobalt`:

1. Leave `cold` untouched.
2. Run a full-text query with no matches on `searched`.
3. Add a second full-text index on `altered` without querying either index.
4. Change the source contents to `the`.
5. Insert row 5=`orchard cobalt the` into all three tables.

Searches on the original `body` index produce:

| Table | `orchard` | `cobalt` | `the` |
|---|---|---|---|
| `cold` | 5 | 2,5 | 3 |
| `searched` | 5 | 2 | 3,5 |
| `altered` | 5 | 2,5 | 3 |

The first insert into `cold` loads the newer `the` list. The full-text query on
`searched` already captured `cobalt`, even though it returned no rows. Adding an
index on `altered` did not load the list; its first insert also captures `the`.
Historical postings remain unchanged in every case.

## Implementation and recovery

Each recovered custom source has a deferred load shared by its table's indexes.
Queries and writes that index text resolve the source against the current catalog
once. Ordinary reads, deletes, unchanged indexed text, and the tested metadata
operations retain the unloaded state. A newly added index on a cold table builds
without stopword filtering while sharing the same deferred load for future use.
Historical document rules remain independent of the current policy.

Snapshot serialization inspects cached rules without loading the source. It
retains source names and historical rules in the existing format. Metadata
reconstruction preserves the shared load; selecting a new physical rebuild
policy replaces it. Recovery creates a fresh deferred load after replay.

WAL row events capture the policy used for indexed writes without forcing a cold
policy for updates to other columns. ALTER captures its construction policy,
independently of a concurrent query loading the source afterward. Replay therefore
reconstructs the original postings without replaying the original query history.

The Expecto regression covers post-restart edits, warmed and cold tables, added
indexes, a checkpoint taken before first use, and another WAL recovery. The native
and fsdb wire oracles agree for the operation matrix and source-edit sequence.
No known-gap suppression is added.

## Verification

`just check` passes all 2,919 tests without build warnings or errors. The native
and fsdb loading matrices agree, including indexed-text updates. The existing
stopword configuration matrix passes. All 47 compatibility contracts pass
5,007 steps without differences:
`torture/artifacts/runs/20261007T083642060-8739/contracts`.

The durability lane passes 12 crash restarts and preserves all 97 acknowledged
commits, including automatic checkpoints, WAL-tail repair, snapshots, schema
changes, and torn-tail recovery:
`torture/artifacts/runs/20261007T083652254-8788/durability-seed101-workers4-ops100-restarts8-checkpoint16`.
