# Custom stopword loading after restart

Status: open. MySQL 8.4.11 loads remembered custom stopword sources on first
full-text use. Fsdb resolves them during recovery. The difference affects both
new index contents and future writes when the source changes after restart.

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
| UPDATE assigning identical text | 2 |
| UPDATE changing only the primary key | 2 |
| DELETE, whether or not a row is removed | 2 |
| Rename the full-text index | 2 |
| Add an ordinary index | 2 |
| Change the table comment | 2 |

Only the tested full-text queries and insert load the source. The other
operations leave it unloaded. This does not establish the behavior of every
UPDATE shape or ALTER algorithm.

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

## Fsdb divergence

The same wire probes on fsdb at `b936f85e` reproduce both differences:

- A cold added index excludes `cobalt`; MySQL includes row 2.
- After the post-restart source edit, `cold` matches only row 2 for `cobalt`;
  MySQL matches rows 2 and 5.

`Storage.reloadFullTextStopwords` resolves source tables while loading the
catalog. Recovery therefore fixes the active policy too early. Changing only
the added index's build policy would leave the ordinary-write difference open.

A compatible loading state must preserve historical document rules, capture
source contents at first full-text use, and survive metadata operations without
being forced. Snapshot serialization and WAL context capture must not themselves
load a cold table. WAL replay must still reproduce the policy used for each
historical mutation, independently of which reads preceded the original write.
No known-gap suppression is added.
