# Response to the October 14 independent review

The review inspected `ffb9e71d` on October 14. Its scores and benchmark
figures describe that revision; the reviewer could not run the project. This
response checks the actionable findings against the current implementation.

The immediate CI and commit-notification hazards have been addressed: PR/main
CI runs the normal gates, observer exceptions cannot change a committed write's
acknowledgement, and queued subscriptions have a bounded capacity and visible
drop count. The OS variable is selected at runtime. These fixes do not resolve
the review's broader maintainability, durable-write performance, or recovery
concerns, and they do not establish a new production-readiness score.
At the October 15 recheck of `11925047`, `just check` built without warnings
and passed all 3,606 regression tests. The [current-head CI run](https://github.com/HelgeSverre/fsdb/actions/runs/38682470537)
had passed its build/package and regression jobs; branch coverage was still
running at this checkpoint. The [previous completed main CI run](https://github.com/HelgeSverre/fsdb/actions/runs/38681924661)
passed all jobs. The review's scores and production-readiness judgment are not
re-scored from these gate results.

| Review finding | Current state | Evidence and next action |
|---|---|---|
| No PR or main CI | Resolved for the current workflow | `.github/workflows/ci.yml` runs a Release build, a separate ordinary regression suite, package verification, and a Coverlet job enforcing 65% branch coverage on pushes to main and pull requests. [The `f11a278c` run](https://github.com/HelgeSverre/fsdb/actions/runs/38636418469) passed all three jobs. Earlier parallel coverage runs exhausted a 16 GB runner; a resource trace showed the instrumented process exceeding 15 GB RSS. The CI test jobs now use a 4 GiB .NET GC heap limit and run sequentially. The MySQL differential suite remains a separate gate. |
| Observer exceptions can turn committed writes into reported failures | Exception handling resolved; synchronous callback limitations remain | `Storage.preparePublishedEvents` catches and logs each observer exception and continues to later observers. `EmbeddingIntegrationTests` checks successful write acknowledgement, continued delivery, and WAL recovery after a failing observer. Observers run after catalog publication but before WAL fsync; they can delay acknowledgement or deadlock by writing back to the store. Use the queued subscription for application work, and do not treat either callback delivery or queued delivery as durable acknowledgement. |
| Unbounded commit subscriptions | Resolved after the reviewed revision | `Db.subscribeCommits` defaults to 1024 events; `subscribeCommitsWithCapacity` accepts a positive limit. Overflow drops new events and increments `DroppedCount`, which requires the consumer to resynchronize. The queue is in-process and is not a durable change stream. |
| Hardcoded `version_compile_os` | Resolved after the reviewed revision | `Session.fs` selects Windows, macOS, or Linux at runtime. |
| Async API looks like nonblocking database I/O | Documented limitation | `Db.fs` uses `Task.Run` for embedded async calls; the README now says so and describes cancellation and host concurrency implications. A truly asynchronous executor would need separate design and workload evidence. |
| `registerTable` mutates shared configuration | Accepted store-wide registration contract | `registerScalar` returns a new function registry, while `registerTable` updates `Store.VirtualTables`. This is intentional for the current API: derived `Db` values sharing a store see a registration when they open new sessions; already-open connections retain their captured registry. A focused regression pins that behavior, and `Db.fs` and the README describe startup registration. An independent per-configuration table overlay would need a separate API and explicit embedded/wire session tests; silently changing `registerTable` would break its current visibility contract. |
| Large execution modules | Open maintainability issue; two narrow boundaries extracted | `MutationTargets.fs` owns physical-table grouping shared by joined `UPDATE` and `DELETE`. `ExplainRendering.fs` now owns the JSON plan shape and formatter; `Executor.fs` collects plan rows and wraps the rendered value in a result set. The executor still owns source resolution, row claiming, and application, and remains large. Extract one cohesive responsibility at a time rather than split by file size. |
| Durable writes and worker scaling trail MySQL | Open performance issue; a measured bottleneck removed | The review quotes historical benchmark artifacts. A focused native-host WAL-versus-durable-MySQL insert burst measures FSDB at 5.022–5.038 ms and MySQL at 2.203–2.224 ms for sixteen sequential autocommit inserts ([evidence](../benchmarks/results/c399e5d0-durable-write-pair.md)). `bench-write-gate` now runs both halves with WAL-backed FSDB; its earlier in-memory load run could not support a durable-write claim. A [profile and alternating before/after runs](../benchmarks/results/2026-10-14-statement-admission-contention.md) found a parsed-statement admission lock hot spot under eight writers. Replacing that admission structure improved eight-worker throughput from 8,549 to 11,719–11,896 ops/sec (37–39%) in repeated 15-second trials. A later [durability-matched dependency check](../benchmarks/results/2026-10-14-write-dependency-profile.md) measured about 11,900 FSDB versus 45,700 MySQL operations per second with eight writers. Its sampled trace suggested trigger-metadata initialization contention, but a targeted change failed to improve the FSDB/MySQL ratio and was reverted. The broad gate previously exhausted disk with disposable MySQL binary logs; the benchmark server now disables binary logging while retaining InnoDB commit-time flush. A [matched embedded-versus-wire control](../benchmarks/results/2026-10-14-embedded-wire-write-control.md) repeats the 11,900 eight-writer wire result but reaches about 31,700 through the embedded API with the same WAL-backed schema and seed. A [wire scheduling follow-up](../benchmarks/results/2026-10-14-wire-write-scheduling-profile.md) measured 11,923 FSDB versus 46,193 MySQL operations per second with eight writers. Sixteen and thirty-two writers did not improve FSDB throughput. Wire commits formed smaller WAL batches and spent longer in append-and-flush wall time than embedded commits; a one-yield scheduling experiment did not improve throughput and was reverted. These measurements narrow the problem but do not isolate a causal server function or lock. A [separate read-path repeat check](../benchmarks/results/2026-10-14-correlated-quick-check.md) found roughly fourfold whole-query latency on bounded correlated equality queries, with a slower point-lookup control too; it cannot isolate the planner's share of the cost. |
| Recovery and backup maturity | Partly addressed; preview limit remains | `docs/operations.md` specifies stopped-directory backup, restore checks, and a staging upgrade procedure. A regression now loads a legacy snapshot, accepts a current write, checkpoints, and reopens it. This checks one historical format path; online backup and general cross-version migration are not guaranteed. |
| Mixed-type indexed join discrepancy | Open, narrow compatibility gap | `GAPS.md` records the MySQL 8.4 discrepancy and its connection-history dependence. Keep it bounded to a reproducible application case; do not generalize it to ordinary joins. |

The later [WAL queue and batch-stage profile](../benchmarks/results/2026-10-15-wal-queue-stage-profile.md)
separated admission and handoff from batch work. It measured per-batch file
open and flush costs, but a retained-handle prototype did not establish a
stable wire-throughput gain and was reverted. The write-performance finding
remains open. A subsequent [method-resolved wire trace](../benchmarks/results/2026-10-15-wire-method-trace.md)
found sampled blocked stacks under dependency expansion and insert preparation,
but did not identify the contended lock or a safe scheduling fix. A
[direct timing follow-up](../benchmarks/results/2026-10-15-write-dependency-timing.md)
measured dependency discovery and expansion at about 9–10 µs per call against
about 474 µs for the whole query handler, ruling that path out as the dominant
wire-write cost. [Stage timing](../benchmarks/results/2026-10-15-wire-write-stage-timing.md)
then found that both WAL completion and the handler's remaining work grow
substantially from one to eight writers; neither the cancellation wrapper nor
response sending dominates.
The [same-workload server-stage profile](../benchmarks/results/2026-10-15-keyless-server-stage-profile.md)
measured about 95 to 458 µs per keyless wire write in the handler as writers
rose from one to eight. Nested executor time rose from about 67 to 345 µs and
WAL completion from about 49 to 274 µs. Parsing and stored-function lookup
were much smaller. These are concurrent wall times, not CPU costs or proof of
a specific lock. The temporary probes were removed and the normal Release
binary was rebuilt.
The [publication and queue-role follow-up](../benchmarks/results/2026-10-15-publication-and-queue-role-profile.md)
found only about 12 µs of publication-slot wait per eight-writer insert, while
the WAL acknowledgement action averaged about 261 µs. Group-commit leaders
spent about 502 µs continuing to drain other batches after their own completion
signal. This identifies a latency mechanism but does not establish a throughput
improvement from moving drain ownership. Temporary probes were removed.
A [leader-handoff prototype](../benchmarks/results/2026-10-15-group-commit-leader-handoff-check.md)
then returned the leader after its own completion and scheduled one worker to
drain remaining batches. The focused queue tests passed, but bracketed
durability-matched runs showed only a small eight-writer difference, with the
FSDB/MySQL ratio still about 0.27. The prototype was reverted.
A [current-head durable-write checkpoint](../benchmarks/results/2026-10-15-durable-keyless-write-check.md)
at `125afe52` measured 6,846 versus 15,639 writes/sec with one worker and
11,563 versus 41,879 with eight workers. The ratios of 0.44× and 0.28× are
close to the earlier original-scheduler ratios; trial variability prevents a
claim of material improvement or regression. The performance concern remains
open.

For joined mutations, `QueryHandler` parses and checks the request;
`Executor.runMutationJoin` resolves sources and produces joined rows;
`MutationTargets.group` maps aliases to physical tables; the `UPDATE` and
`DELETE` branches claim row identities and build pending batches; `Storage`
applies and publishes them. This extraction gives target grouping a named,
shared boundary without changing the SQL result or lock path. A larger split
should follow measured maintenance pain in source resolution or publication,
with behavior tests and a focused benchmark before moving that boundary.
For explain output, plan collection and SQL result construction remain in
`Executor`, while the JSON shape types and renderer are in `ExplainRendering`.
The existing traditional, JSON, tree, and analyze regression cases guard the
boundary without adding tests that merely mirror the move.
The shared virtual-table registry remains store-wide by design. A future
configuration-local overlay should be additive because changing this builder
silently would alter what derived configurations and new sessions can see.

Before more compatibility expansion, the maintainability work should proceed in
small, behavior-preserving slices: identify one execution responsibility with
its own inputs and outputs, move it behind a named boundary, and run the normal
gate plus a focused workload check. Do not split `Executor.fs` by line count or
introduce an abstraction that still requires callers to understand its internals.
For virtual tables, preserve the documented store-wide behavior of
`registerTable`. Specify and test any future configuration-local overlay for
both embedded and wire sessions before introducing it. For write performance, retain the
durability-matched gate and compare repeated same-host runs before accepting
another scheduling change.

This is not a claim that the remaining architectural or performance concerns
are solved. The large-module refactor needs a narrow seam with independent
behavioral tests; changing `registerTable` to a pure builder needs an explicit
policy for already-open sessions and listeners. Those should be reviewed as API
and architecture changes, not folded into unrelated MySQL compatibility patches.

The deployment contract remains preview-grade: use disposable or restorable
data, bind safely, retain stopped-directory backups, and verify restores before
upgrades. The review's production-readiness concern is therefore still valid
even though several cited code-level hazards have since been addressed.

## October 15 current-head recheck

The independent reviewer inspected `ffb9e71d`; the current main commit is
`50847710`. The intervening fixes make its two P0 recommendations stale:
`.github/workflows/ci.yml` now runs on pushes and pull requests, and commit
observer exceptions are isolated from the write result. The subscription is
bounded, but `onCommit` handlers still run synchronously inside the commit
lock. A slow handler can delay acknowledgement, and a handler that writes back
to the same store can deadlock. The documented queued subscription is the
safer application-facing choice; it is not a durable change stream.

The [CI run for `38574aa6`](https://github.com/HelgeSverre/fsdb/actions/runs/38695765967)
failed in the instrumented coverage suite when the explicit second insert in
`READ COMMITTED preserves an auto-increment identity across a refresh` hit a
50-second lock wait. The same test passed on current HEAD locally, and the
[next main CI run](https://github.com/HelgeSverre/fsdb/actions/runs/38696257661)
passed all jobs. This is evidence of an intermittent concurrency failure, not
evidence that the coverage threshold was missed: the failed run measured
77.62% branch coverage against a 65% gate. The transaction lock implementation
uses 4,096 key stripes, so unrelated keys can contend if their hashes collide;
the failed log alone does not establish that collision as the cause. Keep this
failure visible and investigate it with a deterministic reproduction before
changing lock semantics or weakening the regression.
The refresh regression now selects an explicit key on a different lock stripe
from the transaction's generated key. It still exercises a same-table
concurrent commit and checks that the private identity survives refresh;
separate lock tests retain deliberate contention coverage. This removes one
source of accidental 50-second waits from that identity test, but does not
prove that stripe aliasing caused the recorded CI failure.

The remaining review priorities are architectural and operational. The latest
[durability-matched checkpoint](../benchmarks/results/2026-10-15-durable-keyless-write-check.md)
still measures materially lower throughput than MySQL under eight writers.
The executor remains large despite the extracted mutation-target and explain
rendering boundaries. The stopped-directory backup procedure and one legacy
snapshot upgrade regression do not establish online backup or general
cross-version compatibility. None of those should be marked resolved from
the review's original numerical scores.

## Disposition before further gap work

The review's immediate regression and embedded-notification hazards have
concrete fixes and regression coverage. The remaining work is not a single
release-blocking defect that can be closed by adding another SQL feature:

1. Keep the durable-write gate in use. The embedded control and wire scheduling
   profile narrow the target, but do not identify a particular server function
   or lock. A connection-owned disconnect timer and a group-commit leader yield
   did not improve repeated load, so both were reverted. The queue-stage
   profile separated admission, handoff, batch work, and completion. The
   method-resolved trace narrowed blocked stacks; direct timing ruled out
   dependency expansion as the dominant cost. Stage timing shows both WAL
   completion and non-WAL handler time growing with concurrency; isolate the
   latter and explain the wire-versus-embedded batch-size difference before
   another scheduling change.
2. Continue extracting one cohesive executor responsibility at a time when a
   real edit exposes its boundary. Preserve the existing behavior with tests
   and avoid a file-size-driven rewrite.
3. Preserve `registerTable` as store-wide registration for new sessions. If
   independent derived configurations become necessary, add a separate API
   with explicit embedded and wire visibility tests.
4. Treat online backup, general cross-version migration, and durable change
   delivery as unsupported preview capabilities until they have explicit
   contracts and end-to-end validation.
