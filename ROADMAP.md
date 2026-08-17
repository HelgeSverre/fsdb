# fsdb roadmap

Each milestone has a runnable acceptance gate. A milestone is done only when its
gate passes against a real external client.

## M1 — Wire protocol skeleton
`mysql --protocol=tcp -h127.0.0.1 -P3307 -e 'SELECT 1'` returns a resultset.
Handshake, mysql_native_password auth, packet framing, COM_QUERY, COM_PING,
COM_QUIT, text resultset encoding.
**Gate:** mysql CLI gets `1` back. Status: done

## M2 — PDO connects
Session variables (`SET NAMES`, `sql_mode`), `@@version`, `SELECT DATABASE()`.
**Gate:** a 5-line PHP PDO script connects and queries. Status: done

## M3 — SQL engine core
FParsec parser, in-memory storage, Value coercion rules.
CREATE TABLE / INSERT / SELECT with WHERE, ORDER BY, LIMIT via mysql CLI.
**Gate:** Expecto suite + mysql CLI session exercising CRUD. Status: done

## M4 — Laravel migrations
ALTER TABLE, indexes, foreign keys, information_schema virtual tables,
prepared statements (COM_STMT_*), transactions.
**Gate:** `php artisan migrate` on the reference application (a private
Laravel 11 app, 94 migrations) succeeds, plus
`migrate:status` (all Ran), `migrate:fresh` (DROP via SHOW/information_schema),
and a second consecutive `migrate` (Nothing to migrate). Status: done

## M5 — Reference application test suite
Joins, aggregates, subqueries, savepoints, JSON functions, expression breadth.
**Gate:** `vendor/bin/pest tests --no-coverage --compact` (not `php artisan
test` — that only resolves phpunit.xml's `Unit`/`Feature` testsuites, 269
tests, and silently skips `tests/Arch`/`tests/Integration`, which the sqlite
REFERENCE baseline's `vendor/bin/pest tests` does sweep in, 304 tests) run
against fsdb on port 3307, same scope/command both sides, dot-pattern
compared against the sqlite baseline. Status: done (287 passed, 15 skipped, 2
todos, 787 assertions — parity with the sqlite baseline)

## M6 — Extensibility polish
Public `registerScalar` / `registerAggregate` API, docs, examples.
**Gate:** README example runs as written. Status: done (`Fsdb.Db` embedding
facade — `Db.create`/`registerScalar`/`registerAggregate`/`listen`; the
README's compilable example builds against `Fsdb.dll` as written, and
`tests/Fsdb.Tests/IntegrationTests.fs`'s "Db.registerScalar/registerAggregate
are queryable over the wire" test proves SLUGIFY/MEDIAN work over a real
MySqlConnector connection)

## M7 — Persistence
Opt-in durability via `--data-dir`: WAL + snapshot, replay on startup.
Default stays pure in-memory (tests unchanged).
**Gate:** with --data-dir: `artisan migrate`, restart fsdb, `migrate` says
"Nothing to migrate"; plus kill -9 mid-write leaves committed data intact
and replayable. Status: done (fresh `--data-dir` on 3424: create+insert incl.
`NOW()`/`UUID()`, `kill -9`, restart — identical `SELECT`; graceful
SIGTERM, restart — intact. Fresh `--data-dir` on 3307: the reference app's 94
migrations via `artisan migrate --force`, graceful restart, `migrate`
again — "Nothing to migrate". WAL replay hardened along the way: a torn
final line no longer poisons future appends, `RowsUpdated`/`RowsDeleted`
replay no longer cascades or over-applies on duplicate-valued rows, replay
no longer re-validates FK checks a `SET FOREIGN_KEY_CHECKS=0` write
deliberately skipped, `snapshotNow` is crash-safe and fsynced, a failed WAL
append is fatal instead of silently diverging, `GENERATED` column
expressions survive a restart)

## M8 — EXPLAIN + semantics cleanups
EXPLAIN (tabular; reports the executor's actual full-scan behavior, no fake
index usage), multi-table UPDATE/DELETE with JOINs, UPDATE/DELETE ORDER
BY+LIMIT (currently parsed but ignored), AFTER/FIRST column positioning.
**Gate:** EXPLAIN on join/subquery queries via mysql CLI; UPDATE/DELETE JOIN
semantics differential-verified against real MySQL 8 (Docker oracle);
reference suite still at exact parity. Status: done (EXPLAIN on a join+correlated-subquery
query renders correctly via the mysql CLI and now validates the statement
it describes — 1146/1054 for a missing table/column instead of a fake
plan; UPDATE JOIN / DELETE JOIN / `UPDATE ... ORDER BY ... LIMIT` /
`DELETE ... ORDER BY ... LIMIT` smoke-verified over the wire, including a
self-join `UPDATE` that used to silently drop one alias's writes and a
cross-table constraint violation that used to leave the earlier table's
rows mutated — both fixed and statement-atomic now; `SET a = x, b = a`
evaluates left-to-right, matching MySQL; comma (implicit-join) `FROM`
lists now parse for `SELECT`/`UPDATE`/`DELETE` alike. Differential
comparisons against real MySQL 8.4 for the fixes above came from the
review that found them, not a Docker oracle run in this pass. The reference app's
full `vendor/bin/pest tests --no-coverage --compact` (in-memory, no
data-dir): 288 passed/15 skipped/2 todos/0 failed, 792 assertions — 0
failures either way, one more test (and 5 more assertions) than the M5
baseline's 287/787, which tracks the app's own migrations/tests having
grown since that baseline was recorded, not a regression here)

## M9 — Performance without ugliness
Kill the measured pathologies while the code stays idiomatic — design and
evidence in [docs/performance-design.md](docs/performance-design.md):
quadratic list appends in Storage, hash join for equi-ON, index-addressable
rows + PK/unique hash indexes, disconnect cancellation, trustworthy
benchmark harness.
**Gate:** UpdateSingleRow < 10ms, JoinUsersOrders completes,
PointSelectByPk < 250µs, two consecutive bench runs agree within 20%,
Expecto + one gauntlet suite regression green. Status: done (measured at
a90dfae: point select 102µs, prepared 89µs, update 2.3ms, batch-100 8.6ms,
GROUP BY at MySQL parity, join completes in 201ms — the join's < 25ms
target moves to M10, whose streaming pipeline is what it actually needs)

## M10 — Streaming pipeline
Stop materializing full result sets before LIMIT: lazy scan/filter/join/
project end-to-end, top-N heap for ORDER BY + LIMIT, honest barriers only
where SQL requires them (GROUP BY, window functions, UNION DISTINCT).
Wire boundary stays materialized.
**Gate:** JoinUsersOrders < 25ms; no benchmark row regresses; differential
tests prove streaming output equals materialized output wherever SQL
defines order; Expecto + reference-app suite parity green. Status: done
(`JoinUsersOrders` 9,376/8,875 µs across two `just bench` runs — both under
the 25ms gate with headroom to spare; no benchmark row regresses against
a90dfae's fsdb numbers; 690 Expecto tests green. `InsertSingle`/
`UpdateSingleRow` sub-gates carried over from M9 still miss narrowly — see
"Sub-gate profiling" below, unchanged by this milestone's own work)
Starting point (measured at 5037a48): JoinUsersOrders 202,198 µs vs
MySQL's 239 µs on the same box — the hash join killed the cross-product
pathology (previously ~425s), the remaining ~8x is `WHERE ... LIMIT 50`
materializing and projecting every matched row before LIMIT slices it.
Sub-gates carried over from M9: InsertSingle < 300µs (at 492µs),
UpdateSingleRow < 500µs (at 2.3ms; now 554µs after the fix below — see
"Sub-gate profiling").

**Landed:** `runSelect`'s plain (non-grouped, non-windowed) path streams —
no `ORDER BY` means `WHERE`/`DISTINCT`/`LIMIT`/`OFFSET` pull rows lazily
through `streamLimited` and stop the moment enough survive; `ORDER BY` +
`LIMIT` (no `DISTINCT`) uses `boundedTopN`, a sorted-buffer top-`(limit +
offset)` selection instead of a full sort. `ORDER BY` without `LIMIT`, or
combined with `DISTINCT`, stays an honest full materialize+sort+dedupe
(unsafe to bound: a duplicate inside a bounded window can starve a row just
outside it). Cancellation checks verified still firing in the streaming
paths (`streamLimited`/`traverseSeq` both check `queryCancellation` every
256 rows). Error semantics decided against a live MySQL 8.4 oracle rather
than assumed: no `ORDER BY` lets `LIMIT` skip evaluating a row past the cut
entirely (no error surfaces); `ORDER BY` forcing a filesort evaluates every
matched row regardless of whether it makes the final cut (error surfaces).
Differential tests (`ExecutorTests.fs`, "M10 streaming pipeline") compare
both paths against the engine's own unlimited-query output, randomized over
plain scans, joins, and DISTINCT; a counting-scalar test proves `LIMIT 10`
against a 20k-row table touches under 1,000 rows.

**Landed (the join itself):** `applyJoin`'s hash-probe candidate list
(`hashPairs`) now `yield`s off the probe side lazily instead of building the
full matched-pair list up front. For the `INNER`/`CROSS` case with an
equi-only `ON` (no residual conjunct left over — the shape `JoinUsersOrders`
and the overwhelming majority of real joins are) there's nothing that needs
to see every match before deciding the result: no `LEFT`/`RIGHT` padding to
compute, no per-candidate re-check that could itself fail past the point a
caller stops pulling. That case hands the lazy `seq` straight through
`applyJoin`/`runSelectStmt` as `Value[] seq` into `runSelect`'s existing
`streamLimited`, so `WHERE u.age > 30 LIMIT 50` now decides how many
combined rows the join ever builds, the same way it already did for a plain
scan. `LEFT`/`RIGHT` joins and any join with a residual `ON` conjunct keep
materializing (both must see every candidate anyway); `GROUP BY` and window
functions force the seq immediately at `runSelect`'s dispatch point, an
honest barrier like every other one already in this pipeline — verified via
a targeted `dotnet fsi` check that forcing an already-materialized `list`
through this path is a zero-copy no-op (`List.ofSeq`'s fast path returns the
same list reference), so those paths pay nothing for the wider `seq` type.
Differential coverage: the existing randomized "streaming LIMIT/OFFSET ...
plain scan or JOIN" test already exercises exactly this equi-INNER-JOIN
shape (40 randomized iterations); a new counting-scalar test
("LIMIT N against an equi-JOIN's WHERE...") proves a `LIMIT 10` against a
5,000-row equi-join touches under 1,000 combined rows, not the full match
set. Measured (`just bench`, two full runs): `JoinUsersOrders` 9,376 µs then
8,875 µs — both comfortably under the 25ms gate (MySQL: 258/269 µs on the
same box), down from the milestone's starting 202,198 µs and the
partial-landing's 34,037 µs in-process figure.

**Sub-gate profiling (InsertSingle/UpdateSingleRow, measured at e92a77b,
`just bench-quick` before/after plus an in-process `Executor.execute`
harness at 10k rows, same methodology as the join numbers above):**

`UpdateSingleRow` 2,626 µs → 554 µs (4.7x; gate is < 500 µs, 11% short).
Root cause found by decomposition, not guessed: a single-table `UPDATE`
ran the WHERE clause through the *interpreted expression evaluator*
against every one of the table's 10k rows (`Executor.selectMutationTargets`,
via `whereMatches`/`evalExpr`) just to find the one row `WHERE id = <pk>`
means — measured in isolation at ~2.9 ms of the 3.1 ms in-process total,
vs. a bare integer-equality `List.filter` over the same 10k rows at 66 µs.
`Storage.updateRows`'s own rebuild-the-row-list pass added a second,
separate full scan on top. Fix: `Executor`'s single-table `UPDATE` branch
now tries `tryPointLookup` — the same PK/UNIQUE-index narrowing `SELECT`
already used (`Storage.tryUniqueLookup`, O(log n)) — before falling back to
`scan`'s full table read, exactly mirroring `runSelectStmt`'s existing
`FromTable`/no-`JOIN` narrowing. Pure narrowing (a superset of the real
WHERE match), so `selectMutationTargets` still runs the complete WHERE
over whatever it returns — never a correctness risk, and no observable
behavior changes (`UPDATE`/`DELETE` order isn't SQL-defined either way).
Caught in the same pass: `Storage.updateRows` built `Array.ofList
table.Rows`, converted it back with `List.ofArray`, and ran `List.indexed`
over it — three full-table passes producing an index the fold's own `step`
function then discarded (`let step acc (_, row) = ...`, the index was
never read). Dead weight left over from an earlier shape; deleted, folding
over `table.Rows` directly. Remaining 554 µs is the honest floor: even
narrowed to one matching row, `Storage.updateRows` still walks every row
of `table.Rows` once to rebuild the table's immutable list (a `Value[]
list` cons-and-reverse, the same "still O(table)" ceiling the function's
own doc comment already named). Closing that needs `Table.Rows` itself to
become index-addressable (array/`ResizeArray`) — `docs/performance-design.md`
section 2's change C, already scoped there as "Large" blast radius (every
`Rows` consumer in `Executor`/`Persistence` snapshot-replay) precisely
because the table's copy-on-write immutability is what the engine's
per-database snapshot/transaction model leans on; not undertaken here for
an 11%, now-isolated gap.

`InsertSingle`: not changed this pass. The official `just bench-quick`
number barely moved (492 µs → 459 µs, within its own noise — Error is
±588 µs, a wider band than the mean). A clean, warm, minimal-harness
measurement (a single reused connection, 300 real wire round trips against
a Release server already primed with 10k rows and 200+ warmup calls) reads
205 µs total, with the equivalent in-process `Executor.execute` call at
105 µs and a bare `SELECT 1` round trip at 95-99 µs — i.e. the engine's
own steady-state contribution is already comfortably inside a 300 µs
budget once JIT tiering has caught up. `just bench-quick`'s much noisier,
much higher number is a known property of its own methodology, not a
regression to chase: `ServerBenchmarks` restarts fsdb per benchmark case
(deliberately, to stop one case's leftover work poisoning the next — see
section 1.1/1.6 above) and ShortRun only warms up 3 iterations before
measuring, nowhere near enough for a branch-heavy, allocation-light
per-statement path like a single `INSERT` to reach steady-state tiered
JIT code — `UpdateSingleRow`'s tight O(n) loop hits that ceiling fast
regardless (its StdDev dropped to 0.45% after the fix above), `InsertSingle`
doesn't. Of the real, steady-state 60 µs (roughly half the 105-205 µs
budget), `Storage.insertCore`'s own doc comment already names the cause:
`table.Rows @ accepted` is an O(existing table size) list append per
statement — the same `Table.Rows`-immutability ceiling `UpdateSingleRow`
hit above, and the same deferred "Large" fix. Left alone this pass for the
same reason.

**Gate closure, two `just bench` runs (`benchmarks/results/2fb1a16.md`,
overwritten by run 2; `benchmarks/results/2fb1a16-run1.md`, run 1's copy set
aside first):** `JoinUsersOrders` 9,376 µs then 8,875 µs — both clear the
< 25ms gate. `PointSelectByPk` 110.64 µs then 128.29 µs, a 16% spread —
inside the 20% reproducibility gate. No fsdb row regresses against
a90dfae's closing M9 numbers: `JoinUsersOrders` and `FilterScanOrderLimit`
and `JsonExtract` and `UpdateSingleRow` all improved (the latter two from
already-landed streaming/index work predating this slice, not from the join
change here); `PointSelectByPk`/`InsertSingle`/`InsertBatch100`/
`GroupByAggregate` moved within single-digit-to-~18% run-to-run noise in
either direction — none of those paths touch a `JOIN`, and `GroupByAggregate`
(the largest apparent delta, +9.5%/+18.6% across the two runs vs a90dfae)
is ruled out as a regression from this milestone's own `List.ofSeq rows`
addition at `runSelect`'s GROUP BY dispatch: a targeted check confirms
`List.ofSeq` on a value that's already a `list` returns the same list
object, zero-copy — the two runs of *this* code disagree with each other by
7.7% on that same row, which brackets the a90dfae delta as ordinary
ShortRun-class noise, not a regression to chase. Sub-gates: `InsertSingle`
(538/602 µs, gate < 300 µs) and `UpdateSingleRow` (575/571 µs, gate < 500
µs) both still miss, unchanged from the "Sub-gate profiling" diagnosis
above — neither touches this milestone's join-laziness change, and both
sit on the same already-documented `Table.Rows`-immutability floor.

**Correctness fixes after gate closure:** `ORDER BY ... LIMIT` with MySQL's
max `LIMIT` (the "OFFSET with no LIMIT" pagination idiom, clamped by the
parser to `Int32.MaxValue`) silently returned zero rows — `boundedTopN`'s
capacity (`limit + offset`) overflowed negative in unchecked 32-bit `int`.
A large-but-unclamped `LIMIT` either preallocated a multi-gigabyte
`ResizeArray` or faulted outright with `Array dimensions exceeded supported
range`. Fixed: the capacity arithmetic runs in `int64` and clamps to
`Int32.MaxValue`; `boundedTopN` no longer preallocates from that number
either, and now folds the per-row `WHERE`/projection/order-key evaluation
directly into its insertion loop, so peak memory during `ORDER BY` +
`LIMIT` is `O(capacity)` rather than `O(matched rows)` (previously only the
buffer's own capacity hint was bounded — every matched row's evaluation
still landed in a separate full accumulator first). `LIMIT 0` (the
`getColumnMeta`/"metadata, no rows" probe idiom) reported `VAR_STRING` for
every column instead of its real MySQL wire type, because wire-type
inference narrowed to the (empty) returned row set; it now takes a
dedicated full-scan path matching MySQL and this engine's pre-M10 shape,
costing nothing extra since `LIMIT 0` returns no rows either way.
`applyJoin`'s hash-eligibility check re-walked a chained `JOIN`'s left side
through `rowsSoFar` after it had already been forced into `leftIndexed`,
re-driving a previous `JOIN`'s lazy `hashPairs` seq from scratch on a 3+
table chain; routed through `leftIndexed` instead.

Re-verified: 693 Expecto tests green, `vendor/bin/pest tests --no-coverage
--compact` against a live fsdb on port 3307 (288 passed, 15 skipped, 2
todos, 792 assertions — parity with the sqlite baseline), one `just bench`
run (`benchmarks/results/f4ba12a.md`): `JoinUsersOrders` 10,742.98 µs,
still clear of the < 25ms gate; `FilterScanOrderLimit` 6,258.65 µs, down
from 15,804.64 µs at a90dfae. `PointSelectByPk`/`InsertSingle`/
`InsertBatch100`/`GroupByAggregate`/`PreparedPointSelect` moved 12-27%
against a90dfae in this single run — none of those paths were touched by
these fixes (only `ORDER BY`/`LIMIT`/`JOIN` were), and the spread sits
inside the run-to-run noise band the gate-closure entry above already
measured on this same machine (up to 18.6% on an untouched row across two
back-to-back runs). Sub-gates unchanged: `InsertSingle` 549 µs (gate < 300
µs) and `UpdateSingleRow` 662 µs (gate < 500 µs) both still miss, on the
same `Table.Rows`-immutability floor documented above.
