# HASH partition names and reorganization

Status: named definitions, comments, row hints, node groups, file-per-table
declarations, engine validation, explicit-name addition, and named/no-list
reorganization are covered by regressions. Physical pruning and placement
remain open. The regression baseline `e9c0a7bd` rejected explicit
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

`HashPartitioning` retains optional ordered definitions alongside its count.
Each definition retains its name, comment, row hints, node group, and tablespace
declaration. These definitions feed
reorganization, ADD/COALESCE, selection, truncation, metadata, and rendering.
Snapshot format FSNM and schema WAL version 12 persist them; count-only
records continue to synthesize `p0`…`pN`, and name-only records receive empty
comments.

Partition-qualified sources retain the partition-aware read path. The failing
regression also exposed an indexed ordering path that returned the entire
table despite `PARTITION(...)`; ordered reads, counts, and joins now verify
the selected rows.

The root gate passes 2,951 tests. Dedicated recovery tests cover named WAL and
snapshot recovery plus count-only V7 WAL and FSNI snapshots, and captured name-only V9 WAL and FSNJ snapshots. The native oracle
passes. The compatibility lane passes 49 cases / 5,117 steps with zero
differences at `20261007T122900776-70695/contracts`. The durability lane passes
at `20261007T122918062-70821/durability-seed101-workers4-ops100-restarts8-checkpoint16`,
including acknowledged commits across 12 crash restarts.

## Remaining boundary

Rows still share one store. Physical partition pruning and tablespace placement
remain unsupported; accepted declarations do not create per-partition files.

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
and every row. Older name-only WAL and snapshot formats remain readable.


## Partition comments

The baseline `801006f2` rejected comment clauses. The native oracle verifies
CREATE and ADD comments, preservation through COALESCE, clearing an omitted
comment in named reorganization, last-clause precedence for repeated COMMENT,
and preservation of the first comment through no-list reorganization.
`NODEGROUP` reports `default` for ordinary HASH partitions and an empty string
for an unpartitioned table.

The length limit is 1,024 characters: 1,024 ASCII characters or 1,024 copies
of `é` succeed, while 1,025 fail with 1793 / HY000. Regression coverage includes
an overlong alteration leaving existing definitions unchanged, quoted-comment
rendering and parsing, and comment recovery from both WAL and snapshots.


## Partition engine validation

Status: covered by regressions. At baseline `264305d9`, fsdb rejected the engine clauses emitted by native
`SHOW CREATE TABLE` with 1064 / 42000. It also accepted
`CREATE TABLE h(id INT) ENGINE=MyISAM PARTITION BY HASH(id) PARTITIONS 2`,
which native MySQL rejects with 1178 / 42000.

The maintained [engine oracle](../scripts/hash-partition-engine-oracle.py)
runs against disposable native MySQL 8.4.11:

```sh
python3 torture/scripts/hash-partition-engine-oracle.py
```

For a two-partition CREATE definition:

| Table engine | Partition engine declarations | Native outcome |
|---|---|---|
| Omitted | Both omitted | Accept |
| Omitted | Both InnoDB | Accept |
| Omitted | One InnoDB, one omitted | 1497 / HY000 |
| InnoDB | Omitted, InnoDB, or a mixture of those two | Accept |
| InnoDB | Any MyISAM | 1497 / HY000 |
| MyISAM | Omitted or MyISAM only | 1178 / 42000 |
| MyISAM | Any InnoDB | 1497 / HY000 |
| Omitted | Both MyISAM | 1178 / 42000 |
| Any tested setting | Unknown partition engine, with NO_ENGINE_SUBSTITUTION | 1286 / 42000 |

With `sql_mode=''`, an unknown engine on one partition produces warning 1286
and uses the default engine, even when the other partition and table omit an
engine. This differs from explicitly naming InnoDB on just one partition.
Explicitness must therefore survive long enough to validate engine inference;
substitution cannot simply turn every omitted or unknown engine into an
explicit InnoDB declaration before those checks.

ADD and REORGANIZE accept InnoDB clauses against an existing InnoDB table,
including ADD lists that mix explicit InnoDB and omitted engine clauses.
A conflicting MyISAM declaration returns 1497. Structural errors precede that
conflict: ADD with a duplicate partition name and MyISAM returns 1517, while
REORGANIZE of a missing partition and MyISAM returns 1507. Unknown-engine
resolution comes earlier: ADD with both a duplicate name and an unknown engine
returns 1286 when substitution is disabled.

The oracle verifies that rejected CREATE statements publish no table, and
that rejected alterations preserve partition names and all six fixture rows.
Repeated ENGINE clauses resolve in source order: an unknown earlier request
still raises 1286 with substitution disabled, even if followed by InnoDB.
With substitution enabled, an unknown table-level engine produces warnings
1286 and 1266 and supplies an explicit InnoDB default. An unknown partition
engine instead remains implicit after warning 1286. ALTER ENGINE=MyISAM on a
partitioned table returns 1178; permissive unknown ALTER engines retain InnoDB.

Engine requests are transient syntax. Validation clears them before schema
publication, and accepted partitions use InnoDB. WAL and snapshot recovery
retain names and comments without changing the persistence format.

## Row hints and node groups

Status: covered by regressions. Baseline `fd771308` rejected each of `MAX_ROWS=100`,
`MIN_ROWS=10`, and `NODEGROUP=7` in a partition definition with 1064 / 42000.
The maintained [hints oracle](../scripts/hash-partition-hints-oracle.py) passes
on disposable native MySQL 8.4.11:

```sh
python3 torture/scripts/hash-partition-hints-oracle.py
```

`MAX_ROWS` and `MIN_ROWS` survive `SHOW CREATE TABLE` independently. Zero is
omitted, repeated clauses use the last value, and `MIN_ROWS > MAX_ROWS` is
accepted. Both accept 9223372036854775807; 9223372036854775808 and negative
values fail with 1064 / 42000 without publishing a table. Equals signs are
optional.

`NODEGROUP` accepts unsigned 64-bit input and retains its low 16 bits.
65535 means `default` in metadata and is omitted from rendered definitions;
65536 becomes explicit zero. Zero differs from the default. Values above
18446744073709551615 and negative values fail with 1064 / 42000. Repeated
clauses use the last value.

Rendering orders NODEGROUP before MAX_ROWS before MIN_ROWS before ENGINE.
ADD retains the new values, COALESCE preserves surviving definitions, and
named REORGANIZE clears omitted values in replacement definitions. No-list
REORGANIZE retains the first partition's options. The oracle checks all six
fixture rows after each alteration. These are retained schema properties;
accepting their syntax without storing them would still diverge from MySQL.

Snapshot FSNL and schema WAL version 11 persist row hints and node groups.
Captured FSNK and V10 WAL fixtures verify that older comments remain readable
with zero hints and a default node group. Recovery regressions cover options
from both CREATE and ADD, alongside names, comments, and partition row selection.

## Partition tablespaces

Status: covered by regressions. Baseline `b1153190` rejected a partition declaration of
`TABLESPACE=innodb_file_per_table` with 1064 / 42000. The maintained
[tablespace oracle](../scripts/hash-partition-tablespace-oracle.py) passes on
native MySQL 8.4.11 with the disposable server's default file-per-table setup.

```sh
python3 torture/scripts/hash-partition-tablespace-oracle.py
```

The identifier `innodb_file_per_table` is accepted with optional equals and
backticks. String literals fail with 1064. Names are case-sensitive:
`INNODB_FILE_PER_TABLE` and an absent name return 3510 / HY000. Both
`innodb_system` and `innodb_temporary` return 1478 / HY000 because a partitioned
table cannot occupy a shared tablespace. Repeated clauses use the last name;
an earlier absent name does not cause failure when overwritten.

A declaration belongs to the partition that received it, but SHOW CREATE
renders `TABLESPACE = innodb_file_per_table` on every partition while any
explicit declaration survives. Reorganizing the declaring partition without
a tablespace clears that declaration; reorganizing another partition does
not. No-list reorganization retains the first partition's declaration.
ADD without a declaration preserves existing state, and ADD with one causes
all partitions to render it. `INFORMATION_SCHEMA.PARTITIONS.TABLESPACE_NAME`
remains NULL throughout these cases.

A duplicate ADD name precedes a missing tablespace (1517), and an unknown
REORGANIZE source precedes it (1507). Otherwise a missing tablespace in ADD
returns 3510. Rejected CREATE publishes no table; rejected ALTER preserves
all fixture rows and existing rendering. General tablespace administration
and physical placement remain distinct unsupported capabilities.

Snapshot FSNM and schema WAL version 12 preserve the declaring partition.
Captured FSNL and V11 WAL fixtures load existing hints and comments without
inventing tablespace declarations. Recovery tests retain declarations introduced
by CREATE and ADD independently of the table-wide rendered representation.

## Engine aliases and refusal families

At baseline `cfa19330`, equivalent HEAP/MEMORY and MERGE/MRG_MYISAM
requests incorrectly produced mixed-engine error 1497. The native engine
oracle now covers both alias pairs with omitted and explicit table engines.
Canonical identity is checked before deciding whether handlers conflict.

MySQL 8.4.11 returns 1178 / 42000 for MEMORY/HEAP, MyISAM, ARCHIVE, and
BLACKHOLE partitioning. CSV, MERGE/MRG_MYISAM, and performance_schema instead
return 1572 / HY000. A conflicting InnoDB declaration still takes precedence
with 1497. CREATE with a performance_schema table engine and partitioning
reaches these checks; ordinary CREATE retains its 1683 refusal. ALTER to
performance_schema retains its distinct 1031 refusal, while ALTER to CSV or
MERGE returns 1572. Regressions cover these distinctions without changing the
shared row-store policy for ordinary tables.

Alias validation passes the 2,951-test root gate, the expanded native engine
oracle, and 49 compatibility cases / 5,117 steps with zero differences at
`20261007T123400576-73711/contracts`.
