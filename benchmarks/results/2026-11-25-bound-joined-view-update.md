# Primary-key probes for joined-view updates

The [native peer benchmark](2026-11-25-native-peer-refresh.md#feature-latency)
identified a point update through a joined view whose executor materialized
both input tables before applying the `WHERE` clause. Fsdb can now pass
the literal bound through same-type signed-integer join equalities and
probe both primary keys on the statement snapshot. The ordinary mutation
path still evaluates the join, visibility predicate, and `CHECK OPTION`
against the selected rows. The execution path requires all parts of each
key, physical inner or straight join inputs, and simple integer predicates.

The composite-key regression binds one component through the partner
alias. It checks a hit, a miss, a rejected `CHECK OPTION` update, and the
committed rows. The pinned MySQL 8.4.11 oracle used `PRIMARY` with `const`
access on both inputs and agreed on the checked rows and error. At the
benchmarked revision, Fsdb's `EXPLAIN UPDATE` displayed a view scan.
Separate wire controls for a partial composite key, a disjunctive
filter, and a left join matched MySQL's affected counts and committed rows.
The allocation regression verifies the execution path. Four point
updates allocated 21.1 MB at 500 rows and 181.2 MB at 5,000 rows before the
change. The candidate passes the regression's less-than-threefold growth
bound across those inputs.

## Native benchmark

The rerun selected the joined-view lane from the
[native feature harness](../scripts/native-feature-peers.fsx). It uses
500 parent rows, 500 status rows, and one checked autocommit update per
observation. The client verifies each affected-row count and the final
committed value. Each pass took 60 samples after one preflight and four
warmups. The second pass reversed server order.

The servers ran one at a time on the same Apple M2 Max Mac with macOS 15.6
over loopback TCP. Fsdb used Release .NET 10.0.12, Server GC, and disposable
WAL stores. MySQL 8.4.11 used a 256 MiB InnoDB buffer pool, commit flushing
set to 1, and binary logging disabled. The baseline is `68b3271d`. The
host was shared and the runs use warm data.

| Engine | Pass 1 p50 / p95 ms | Pass 2 p50 / p95 ms | Mean p50 ms |
| --- | ---: | ---: | ---: |
| Fsdb baseline | 5.386 / 6.025 | 4.642 / 4.864 | 5.014 |
| Fsdb candidate | 3.500 / 3.654 | 3.074 / 3.263 | 3.287 |
| MySQL | 0.141 / 0.154 | 0.142 / 0.164 | 0.141 |

Fsdb's median fell by 34%, a 1.53× speedup. The candidate remains about
23× MySQL's median on this fixture. The timing covers the complete wire
round trip, including view handling, transaction work, and durability.

## Write-lock control

One connection held an uncommitted update on parent ID 2. Another
updated parent ID 1 through the joined view using `WHERE partner_id=1`.
MySQL completed the second update while ID 2 remained locked. Fsdb's
second update remained pending through the two-second observation and
completed after the holder rolled back. Both engines committed the same
value for ID 1. The bound used to execute the join has yet to reach the
write-lock selection for this partner-qualified filter in the benchmarked
revision.

The follow-up shares the same bound keys between execution and write-lock
selection for eligible joined updates. A complete key can narrow the
write claim even when a read-only join partner has a partial key. A
missing target key retains the conservative row claims so a concurrent
primary-key move is visible when the waiting statement resumes.

The MySQL 8.4.11 and Fsdb wire probes used a two-column primary key.
Both joined-view and physical updates completed while a different row
was held. Both waited on the matching row, and both waited when another
transaction moved a row into the initially absent key. Every statement
affected one row and the final committed values matched. A repeat with
two matching partner rows and an unbound third partner key column gave
the same results.

## Explaining bound joined-view updates

Traditional `EXPLAIN UPDATE` now uses the same bound-join proof as
execution. It displays each physical source, the written source, and
the complete `PRIMARY` access. It distinguishes an absent const row
from a row rejected by the view's visibility predicate. Planning leaves
the rows intact even if the proposed assignment would fail `CHECK OPTION`.

The pinned MySQL 8.4.11 wire comparison matched 20 plans across two- and
three-table views, including reversed input order, each writable source,
covering partner reads, absent keys, and a hidden row. The two-table lane
matched under both definer and invoker security. The committed data was
identical before and after `EXPLAIN`.

The expanded plan requires `SHOW VIEW` and underlying read privileges,
including the nested view. A delegated caller without the nested-view
grant returns 1345, matching the oracle. [MySQL privilege reference](https://dev.mysql.com/doc/refman/8.4/en/privileges-provided.html).

Broader view plans and exact JSON and TREE rendering remain open.

## Executing partial-key joins

Two-table inner updates can also narrow an individual source by an exact
index prefix. A complete target key can be probed while the read partner
returns several rows from a composite key. The executor keeps those rows
in row-store scan order because the first matching partner can supply the
assignment. The ordinary join and view checks still run on the candidates.

The allocation regression holds the two matching partner rows constant
while increasing the partner table from roughly 500 to 5,000 rows. Four updates
allocated 3.49 MB and 13.71 MB before the change. The same measurements
with the candidate allocated 2.40 MB at both sizes and preserved the
original partner choice when index order differs from insertion order.
MySQL and Fsdb agree on affected rows, committed values, and `CHECK OPTION` for the
composite-prefix wire fixture with equal-valued matching partners.

## Explaining partial-key joins

Traditional `EXPLAIN UPDATE` shares index selection with execution. With
a complete target primary key and two partner matches on the first part
of a composite key, it reports `const` for the target and `ref` with a
four-byte key and two candidate rows for the partner. The plan reports a
residual predicate or a covering lookup where applicable.

The pinned MySQL 8.4.11 comparison also covers an absent target, an empty
partner prefix, a target hidden by the view predicate, and a covering prefix.
Both engines return error 1345 without the required underlying grants for
either an existing or an absent target. Fsdb checks those grants before
selecting an index prefix. Both engines leave the rows intact.
MySQL estimates `filtered` at 33.33%
for the residual partner predicate. Fsdb currently renders 100.00%.

## Applying a range after the equality prefix

Two-table inner updates can now apply signed-integer comparisons or
`BETWEEN` to the key column after the equality prefix. Execution restores
row-store order for the selected rows so the same first matching partner
supplies the assignment. Equal `BETWEEN` endpoints can complete an exact key.

MySQL 8.4.11 and Fsdb matched on all plan columns, affected counts, and
committed rows for the primary-key fixture. It covers open endpoints,
an empty range, and equal `BETWEEN` endpoints. A descending secondary key
with a nullable range column also matched on key access, affected rows,
and committed data. MySQL reports `Using index condition` on the secondary
case. Fsdb reports `Using where`.

The embedded `Db.Connection.Query` allocation probe used in-memory debug
builds on .NET 10.0.12. Four measured autocommit updates followed one
warmup. The matching two partner rows remain fixed while additional rows
share their leading key and fall outside the suffix bounds. Both builds
preserved the same first partner and committed values.

| Out-of-range rows | `b347b3a5` allocated | With the range probe allocated |
| ---: | ---: | ---: |
| 500 | 21.22 MB | 9.65 MB |
| 5,000 | 151.76 MB | 32.84 MB |

The measured predicate was `id=1 AND seq >= 20 AND seq <= 21`. The
executor regression also checks bounded allocation growth for an equivalent
`BETWEEN` range. The table measures thread allocations through the embedded
API.

## Preparing read locks from the same range

An EventPipe allocation trace of 60 warmed, embedded updates against a
5,001-row partner at `d3e48cbd` found that transaction preparation still
enumerated the equality prefix and acquired read locks across it. The
largest sampled allocation stacks passed through partner candidate
materialization and acquisition of transaction read targets.

Read-lock preparation now uses the selected index range for a standalone
joined-view update. It probes published changes to the partner table from
active transactions, so a row with an existing identity can be claimed
when a pending update moves its key into the range. The statement then
rebases and reads the committed value after waiting.

In the default repeatable-read wire fixture, Fsdb and MySQL 8.4.11 let
the update pass held rows outside the range and wait on a matching row
or a move into or out of the range. A move into the range from another
prefix also waits. The transaction regression verifies that the writer
reads the moved row after commit.

The embedded allocation probe used a single qualifying partner and four
updates after one warmup, with `id=1 AND seq>=20 AND seq<=21`. The extra
rows share the leading key and fall outside the range. In-memory debug
builds on .NET 10.0.12 produced the same committed value.

| Out-of-range rows | `d3e48cbd` allocated | With range-based read claims |
| ---: | ---: | ---: |
| 500 | 9.61 MB | 6.83 MB |
| 5,000 | 32.80 MB | 6.74 MB |

## Waiting for pending partner inserts

The transaction planner probes published partner indexes for inserts in
the selected range under `REPEATABLE READ` and `SERIALIZABLE`. It waits
on each matching insertion's primary-key stripe, refreshes the snapshot,
and then claims any committed rows using their stable row identities.
The temporary key wait uses the existing deadlock and timeout handling.

The pinned MySQL 8.4.11 wire comparison covers primary and descending
secondary ranges, autocommit and explicit transactions, initially empty
partners, commit, rollback, and out-of-range inserts across three isolation
levels. Both engines wait for matching inserts under `REPEATABLE READ`
and `SERIALIZABLE` and recheck the data after resolution. Both proceed
immediately in the `READ COMMITTED` insert controls. The transaction
regression also checks that a later writer waits for the newly committed
partner's read lock to be released. A separate Fsdb probe returned 1205
while the insert remained uncommitted. A retry in the same transaction
succeeded after the inserter committed.

## Partner reads at lower isolation levels

Joined-view UPDATE now uses the statement read view for read-only
partners at `READ COMMITTED` and `READ UNCOMMITTED`. It continues to
claim and wait for the writable target. The higher isolation levels
continue to claim partner rows and wait for qualifying pending inserts.

The pinned MySQL 8.4.11 wire comparison covers payload changes, key
moves in both directions, inserts, deletes, exact keys, derived partners,
and a held writable target at all four isolation levels. All 64 cases
matched on affected rows, target values, and lock timeouts across
autocommit and explicit transactions.
`READ COMMITTED` reads the last committed partner values while an owner
holds changes. `READ UNCOMMITTED` sees the owner's published changes.
Both wait if the owner holds the target. The transaction regression
also checks that the next statement sees the newly committed partner
value and retains the transaction's earlier target write.

## Partner ranges inside a larger joined update

A writable view inside `UPDATE ... JOIN` now uses its internal range to
prepare partner claims. The range planner considers the view-local
filter and any same-type signed-integer equality inferred through the
outer inner joins. Both parts of a composite prefix can come from the
outer relation, including a pass-through CTE with explicitly renamed
columns.

At `REPEATABLE READ` and `SERIALIZABLE`, an out-of-range existing row
remains free to progress. Held or deleted matching rows, key moves, and
pending inserts inside the range still wait and read the owner's resolved
state. The regression also checks that a second tenant's data remains
unchanged.

The pinned MySQL 8.4.11 wire probes matched all 216 cases at these two
isolation levels. They used single- and two-part target keys, both outer
source positions, view-local and inferred bounds, CTE operands,
autocommit and explicit transactions, and owner commit or rollback.
The baseline had eight differences in the initial 25-case fixture
because the larger updates waited for unrelated existing rows and
missed qualifying pending inserts.

The lower-isolation matrix exposed a separate gap. For the audited
`UPDATE ... JOIN`, MySQL waits on the matching partner and retains a
shared record lock on it at `READ COMMITTED` and `READ UNCOMMITTED`.
It also waits if the first existing record beyond the range is held,
but `performance_schema.data_locks` showed no retained lock on that
record after the statement. The equivalent standalone view update
used a nonlocking read of the partner. Before the follow-up, Fsdb used
that path for the larger statement too. In the initial matrix, 120 of
the 168 lower-isolation cases differed on waits or resolved data.
[InnoDB statement locks](https://dev.mysql.com/doc/refman/8.4/en/innodb-locks-set.html).

## Lower-isolation range scans in larger joined updates

At `READ COMMITTED` and `READ UNCOMMITTED`, the larger joined update
now waits on matching partner rows and on the first record after its
range in index traversal order. The boundary can fall in the next key
prefix. The initial scan also checks published partner inserts and key
moves, then refreshes the committed snapshot after waiting. The
transaction retains shared locks on rows passing the partner-local
predicate. It releases temporary claims on the boundary and on rows
rejected by that predicate.

The pinned wire comparison matched 172 wait, affected-row, and committed
value cases across ascending primary and descending secondary indexes,
both isolation levels, explicit transactions and autocommit, direct and
CTE-bound larger updates, and standalone controls. Two additional cases
confirmed the wait at the next key prefix. Six `FOR UPDATE NOWAIT`
controls matched which partner rows remained locked after updates with
no residual filter, a selective filter, and a filter rejecting every row.
The transaction regression also verifies an independent write to the
released boundary and to a row excluded by the partner filter.

The scan audit used one concurrent owner at a time. Join shapes without
a proven partner range and pending inserts into keyless partners remain
open.

## Outer join operand locks

The view `active` joins `parent(id,n)` to `partner(id,seq,delta)`. The
outer statement also reads `gate(id,delta)`, where `id` is its primary key.

```sql
UPDATE active a JOIN gate g ON g.id=a.id
SET a.n=a.n+a.delta+g.delta
WHERE a.id=1 AND a.seq BETWEEN 20 AND 22 AND a.delta>=3;
```

With `parent.n=10`, `partner.delta=3`, and `gate.delta=7`, an owner
holds a change to the outer row's delta from 7 to 19. Native MySQL
8.4.11 waits and commits `n=32` when the owner commits, or `n=20`
when the owner rolls back. A committed delete affects no rows. A
pending insert for the initially absent key also causes a wait and,
on commit, supplies the outer value. Before the fix, Fsdb completed the
statement before the owner resolved in each of these cases.

All 24 matching-key wait controls diverged over four isolation levels
and both owner outcomes. Eight unrelated-key controls matched. A
separate lock-retention probe used `FOR UPDATE NOWAIT` after the
statement and before commit. MySQL returned error 3572 on the outer
key in all twelve cases, including when `g.delta>=10` or
`a.delta>=10` eliminated the update. Fsdb allowed the contender to
lock that key. The unrelated key remained free in both engines.

The read planner now propagates signed-integer bounds through the audited
inner joins and single left join, then probes fully bound primary keys on
the outer physical tables. Fsdb waits for a pending insert by key and
keeps a shared lock on each visible row through transaction end. The
statement rechecks the row identity after refreshing its snapshot so a
concurrent delete and insert at the same key cannot leave the replacement
row unlocked.

The native MySQL 8.4.11 wire comparison matched all 128 wait and result
checks across the original join order, the reversed join order, an
outer composite primary key, and autocommit. Each variant covered
matching-key updates, inserts, and deletes plus unrelated-key controls
across four isolation levels and both owner outcomes. The unrelated
controls let the joined writer commit before releasing the owner so
the check measures row scheduling. Four more replacement checks
verified that the writer used the new delta and held a shared lock on
the replacement row. For visible keys, a second shared reader succeeded
and `FOR UPDATE NOWAIT` returned 3572 until the writer ended. That also
held when a predicate rejected every candidate.

The missing-key case at REPEATABLE READ and SERIALIZABLE still differs.
With no outer row and no pending writer, MySQL prevents another
transaction from inserting that key. Fsdb allows the insert. Fsdb can
also reject a SERIALIZABLE commit after an unrelated writer commits
first, matching its pre-existing whole-catalog validation rule.

### Locks through projected outer sources

An unfiltered projection can expose the outer table's primary key under
a different name. Joined-update lock planning resolves the exposed
column to the physical key through nested derived tables and simple
CTEs, including explicit CTE column lists. When a pending insert or
replacement publishes, the replan resolves the physical row from the
refreshed snapshot.

The native MySQL 8.4.11 probe matched 48 schedules across four isolation
levels. It compared the direct table, an aliased projection, nested
projections, and a CTE with explicit column names. A pending insert or
delete-and-reinsert of key `3` blocked the joined writer, which read the
published value after commit. An unrelated writer at key `5` proceeded
independently. In each case a second reader could take a shared lock on
key `3`, and an exclusive `NOWAIT` read returned error 3572 while the
joined writer was open. An exclusive lock on key `5` remained available
after its owner ended.

Materialized reads remain a separate gap. With `gate` containing keys
`1`, `3`, and `5`, MySQL held shared row locks on all three keys for a
derived `LIMIT 50` or `DISTINCT` at READ COMMITTED and REPEATABLE READ.
With `ORDER BY id LIMIT 1`, MySQL retained a lock only on `1` despite
the outer update affecting zero rows. Fsdb allowed exclusive locks on
all three keys in these materialized cases.

### Scope of the missing-key gap

A native MySQL 8.4.11 follow-up moved the parent and partner to key `3`
and seeded `gate` with keys `1` and `5`. The joined update above used
`a.id=3` and affected zero rows. A separate transaction attempted each
insert with `innodb_lock_wait_timeout=1` while the joined writer stayed
open. Each successful insert was rolled back before the next attempt.

| Isolation | MySQL timed out | MySQL inserted | Fsdb inserted |
| --- | --- | --- | --- |
| READ COMMITTED | — | 0, 2, 3, 4, 6 | 0, 2, 3, 4, 6 |
| REPEATABLE READ | 2, 3, 4 | 0, 6 | 0, 2, 3, 4, 6 |
| SERIALIZABLE | 2, 3, 4 | 0, 6 | 0, 2, 3, 4, 6 |

MySQL returned error 1205 for all six blocked inserts. The observed
lock covers the open interval between the neighboring primary keys.

At REPEATABLE READ, an insert at `2` and a primary-key update from `5`
to `4` also timed out while the joined writer remained open. An update
of `delta` at the existing endpoint `5` succeeded. After `ROLLBACK TO
SAVEPOINT` preceding the lookup, all three writes succeeded in this
fixture. A prepared XA branch with a separate write to `parent` retained
the gap until `XA ROLLBACK`. A prepared branch whose only operation was
the no-op joined update allowed all three writes. Fsdb allowed both forms
of key creation during the active gap. [InnoDB locking](https://dev.mysql.com/doc/refman/8.4/en/innodb-locking.html).
