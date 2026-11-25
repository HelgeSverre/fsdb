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
With `s.seq > 10` on the second partner key column, MySQL
uses `range` with an eight-byte key. Fsdb's lookup still uses the equality
prefix and applies the range predicate to its candidates.
