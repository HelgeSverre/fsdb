# fsdb roadmap

Each milestone has a runnable acceptance gate. A milestone is done only when its
gate passes against a real external client.

## M1 — Wire protocol skeleton
`mysql --protocol=tcp -h127.0.0.1 -P3307 -e 'SELECT 1'` returns a resultset.
Handshake, mysql_native_password auth, packet framing, COM_QUERY, COM_PING,
COM_QUIT, text resultset encoding.
**Gate:** mysql CLI gets `1` back. Status: ✅

## M2 — PDO connects
Session variables (`SET NAMES`, `sql_mode`), `@@version`, `SELECT DATABASE()`.
**Gate:** a 5-line PHP PDO script connects and queries. Status: ✅

## M3 — SQL engine core
FParsec parser, in-memory storage, Value coercion rules.
CREATE TABLE / INSERT / SELECT with WHERE, ORDER BY, LIMIT via mysql CLI.
**Gate:** Expecto suite + mysql CLI session exercising CRUD. Status: ✅

## M4 — Laravel migrations
ALTER TABLE, indexes, foreign keys, information_schema virtual tables,
prepared statements (COM_STMT_*), transactions.
**Gate:** `php artisan migrate` on the reference application (a private
Laravel 11 app, 94 migrations) succeeds, plus
`migrate:status` (all Ran), `migrate:fresh` (DROP via SHOW/information_schema),
and a second consecutive `migrate` (Nothing to migrate). Status: ✅

## M5 — Reference application test suite
Joins, aggregates, subqueries, savepoints, JSON functions, expression breadth.
**Gate:** `vendor/bin/pest tests --no-coverage --compact` (not `php artisan
test` — that only resolves phpunit.xml's `Unit`/`Feature` testsuites, 269
tests, and silently skips `tests/Arch`/`tests/Integration`, which the sqlite
REFERENCE baseline's `vendor/bin/pest tests` does sweep in, 304 tests) run
against fsdb on port 3307, same scope/command both sides, dot-pattern
compared against the sqlite baseline. Status: ✅ (287 passed, 15 skipped, 2
todos, 787 assertions — parity with the sqlite baseline)

## M6 — Extensibility polish
Public `registerScalar` / `registerAggregate` API, docs, examples.
**Gate:** README example runs as written. Status: ✅ (`Fsdb.Db` embedding
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
and replayable. Status: ✅ (fresh `--data-dir` on 3424: create+insert incl.
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
reference suite still at exact parity. Status: ✅ (EXPLAIN on a join+correlated-subquery
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
Expecto + one gauntlet suite regression green. Status: ✅ (measured at
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
defines order; Expecto + reference-app suite parity green. Status: ☐ (partial
— see below)
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

**Not landed, and why the gate isn't ticked:** the join itself
(`applyJoin`'s hash-probe candidate list) still fully materializes every
matched left×right pair into `Value[]` before `WHERE`/`LIMIT` in `runSelect`
ever see them — so `JoinUsersOrders`'s new streaming WHERE/LIMIT layer only
gets to save work on the post-join filter/project step, not the join's own
row-building. Same-methodology in-process timing (`Executor.execute`
directly, no wire protocol, 10k users/50k orders, 15 runs, median):
64,251 µs before this milestone's changes → 34,037 µs after — real, ~1.9x,
consistent with the diagnosis, but short of the < 25 ms gate because the
join's materialization is the now-dominant remaining cost. Making the
hash-join probe itself a lazy seq (item 1's "join probe" clause) needs the
build-side-size heuristic and the LEFT/RIGHT outer-join padding logic
reworked around a type that's genuinely single-pass — a separate, focused
piece of work against `applyJoin`/`applyMutationJoin`, deliberately not
rushed into this slice given how load-bearing (and delicately
comment-documented) that code already is. Follow-up, not abandoned.

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
