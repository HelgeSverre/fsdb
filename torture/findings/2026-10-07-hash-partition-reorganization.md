# HASH partition names and reorganization

Status: named definitions, explicit-name addition, and named/no-list
reorganization are covered by regressions. Partition-option clauses and
physical pruning remain open. The regression baseline `e9c0a7bd` rejected explicit
names and both reorganization forms with 1064 / 42000.

## Oracle

Run the disposable native oracle:

```sh
python3 torture/scripts/hash-partition-reorganize-oracle.py
```

It checks both HASH and LINEAR HASH on an integer-primary-key table with three
partitions and rows 0 through 5. Every operation preserves all six rows,
including rejected alterations.

| Reorganization suffix | Result |
|---|---|
| `p0 INTO (PARTITION q0)` | Names become `q0,p1,p2` |
| `p0,p1 INTO (PARTITION q0,PARTITION q1)` | Names become `q0,q1,p2` |
| `p1,p0 INTO (PARTITION q0,PARTITION q1)` | Names become `q0,q1,p2`; input order does not reverse the slots |
| `p0 INTO (PARTITION q0,PARTITION q1)` | 1510 / HY000; partition count cannot change through this form |
| `p0,p1 INTO (PARTITION q0)` | 1510 / HY000 |
| `p0 INTO (PARTITION p1)` | 1517 / HY000; duplicate name |
| `p4 INTO (PARTITION q0)` | 1507 / HY000; unknown source partition |
| `p0,p2 INTO (PARTITION a,PARTITION b)` | 1519 / HY000; nonconsecutive selection |
| `p0,p0 INTO (PARTITION a,PARTITION b)` | 1507 / HY000; repeated source name |
| `p0,p1 INTO (PARTITION a,PARTITION A)` | 1517 / HY000; case-insensitive duplicate |
| No suffix | Names become `p0`; the fixture collapses to one partition |

An explicit definition
`PARTITION BY HASH(id) (PARTITION First,PARTITION Second)` retains the names'
case in `INFORMATION_SCHEMA.PARTITIONS`. `ADD PARTITION PARTITIONS 1` appends
`p2`; `COALESCE PARTITION 1` removes it and retains the existing names.
Renaming `First` to `Renamed` leaves rows 0, 2, and 4 in that slot, selected
case-insensitively by `PARTITION(renamed)`.

The no-list observation is scoped to this native configuration and fixture;
it is not evidence that every engine or server configuration chooses one
partition.

## Implementation and validation

`HashPartitioning` retains optional ordered names alongside its count. The
same names feed reorganization, ADD/COALESCE, selection, truncation, metadata,
and rendering. Snapshot format FSNJ and schema WAL version 9 persist them;
count-only snapshots and WAL records continue to synthesize `p0`…`pN`.

Partition-qualified sources retain the partition-aware read path. The failing
regression also exposed an indexed ordering path that returned the entire
table despite `PARTITION(...)`; ordered reads, counts, and joins now verify
the selected rows.

The root gate passes 2,944 tests. Dedicated recovery tests cover named WAL and
snapshot recovery plus count-only V7 WAL and FSNI snapshots. The native oracle
passes. The compatibility lane passes 49 cases / 5,117 steps with zero
differences at `20261007T113119060-18198/contracts`. The durability lane passes
at `20261007T113138610-18520/durability-seed101-workers4-ops100-restarts8-checkpoint16`,
including acknowledged commits across 12 crash restarts.

## Remaining boundary

Native MySQL accepts partition options such as `COMMENT 'x'`; fsdb still
refuses these clauses. Physical pruning remains a separate performance gap.

## Explicit-name additions

The baseline `0e0cb8b6` refused `ADD PARTITION (PARTITION Third)`. The regression
covers one or several added names, case-insensitive collisions with existing
or newly added names, preserved metadata after rejection, and durable names.
The native oracle also verifies that mixing `PARTITIONS n` with a name list
fails with 1064 / 42000, and that successful additions report zero affected
rows.

For rows 0 through 5, expanding two partitions to three puts rows 2 and 5 in
`Third` under HASH, but only row 2 under LINEAR HASH. Both methods put row 2
there after expanding to four partitions. The maintained oracle checks these
row mappings and verifies that no-list reorganization retains the first name
and every row. Older WAL versions remain readable; snapshots retain format
FSNJ because the stored partition representation is unchanged.
