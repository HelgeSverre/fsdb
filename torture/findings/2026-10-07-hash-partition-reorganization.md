# HASH partition names and reorganization

Status: open. Native MySQL 8.4.11 accepts explicitly named HASH partitions and
`ALTER TABLE ... REORGANIZE PARTITION`; fsdb at `e9c0a7bd` rejects both with
1064 / 42000. The same refusal applies to the no-list reorganization form.

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

## Implementation boundary

`HashPartitioning` stores an expression, count, and linear flag. Partition
names are synthesized from ordinal positions by `Storage.hashPartitionNames`.
`Persistence.encodePartitioning` stores the same three fields. Consequently,
a parser-only rename would not survive metadata rendering or a restart.

The implementation needs ordered partition identities shared by construction,
reorganization, ADD/COALESCE, selection, truncation, metadata, and rendering.
The persisted representation must continue reading existing count-only
records. Regression coverage must include renamed-row routing and recovery,
alongside the native error codes and atomic rejection demonstrated here.

Noncontiguous selections, duplicate source names, explicit-count/name-list
mismatches, and partition-option clauses still need native probes before
claiming complete named-partition support. Physical pruning remains a separate
performance gap.
