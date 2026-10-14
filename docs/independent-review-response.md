# Response to the October 14 independent review

The review inspected `ffb9e71d`. This response checks its actionable findings
against `de493f5d` and distinguishes completed work from remaining limits. Its
scores are the reviewer's judgment, not a current measured rating.

| Review finding | Current state | Evidence and next action |
|---|---|---|
| No PR or main CI | Workflow added; full remote gate awaiting a clean run | `.github/workflows/ci.yml` runs a Release build and package verification alongside a required Coverlet job that executes the complete test suite and enforces 65% branch coverage on pushes to main and pull requests. In [the split-job run](https://github.com/HelgeSverre/fsdb/actions/runs/38565244133), coverage passed on GitHub, including all 3,534 tests; the duplicate uninstrumented test step in the other job was canceled after about 37 seconds, so package verification was skipped. A local `just coverage` at `8a2c2de2` measured 77.57% branch coverage and about 2.1 GB peak resident memory. CI now runs the suite once through Coverlet rather than twice; the release workflow still runs ordinary tests. The cancellation source remains unproven until a full remote run succeeds. The MySQL differential suite remains a separate gate. |
| Observer exceptions can turn committed writes into reported failures | Resolved after the reviewed revision | `Storage.preparePublishedEvents` catches and logs each observer exception and continues to later observers. `EmbeddingIntegrationTests` checks successful write acknowledgement and continued delivery. A synchronous observer can still delay a commit or deadlock by writing back to the store; use the queued subscription for application work. |
| Unbounded commit subscriptions | Resolved after the reviewed revision | `Db.subscribeCommits` defaults to 1024 events; `subscribeCommitsWithCapacity` accepts a positive limit. Overflow drops new events and increments `DroppedCount`, which requires the consumer to resynchronize. The queue is in-process and is not a durable change stream. |
| Hardcoded `version_compile_os` | Resolved after the reviewed revision | `Session.fs` selects Windows, macOS, or Linux at runtime. |
| Async API looks like nonblocking database I/O | Documented limitation | `Db.fs` uses `Task.Run` for embedded async calls; the README now says so and describes cancellation and host concurrency implications. A truly asynchronous executor would need separate design and workload evidence. |
| `registerTable` mutates shared configuration | Open API design issue | `registerScalar` returns a new function registry, while `registerTable` updates `Store.VirtualTables`. `Db.fs` and the README explicitly describe store-level mutation and startup registration. Derived `Db` values sharing a store therefore share virtual tables. If independent derived configurations become a supported use case, move the registry into a per-configuration/session overlay and test both embedded and wire sessions before changing this contract. |
| Large execution modules | Open maintainability issue; first narrow extraction complete | `MutationTargets.fs` now owns physical-table grouping shared by joined `UPDATE` and `DELETE`; the executor still owns source resolution, row claiming, and application. `Executor.fs` remains about 27,700 lines and `Storage.fs` about 11,700. Extract one cohesive responsibility at a time and avoid a wholesale split based only on file size. |
| Durable writes and worker scaling trail MySQL | Open performance issue | The review quotes historical benchmark artifacts. A newer, focused native-host WAL-versus-durable-MySQL insert burst measures FSDB at 5.022–5.038 ms and MySQL at 2.203–2.224 ms for sixteen sequential autocommit inserts ([evidence](../benchmarks/results/c399e5d0-durable-write-pair.md)). This is one workload, not a full replacement for `just bench-write-gate`. The broad gate previously exhausted disk with disposable MySQL binary logs; the benchmark server now disables binary logging while retaining InnoDB commit-time flush. A [separate read-path repeat check](../benchmarks/results/2026-10-14-correlated-quick-check.md) found roughly fourfold whole-query latency on bounded correlated equality queries, with a slower point-lookup control too; it cannot isolate the planner's share of the cost. Re-run the durable write gate on a quiet host with enough free disk after write-path changes. |
| Recovery and backup maturity | Partly addressed; preview limit remains | `docs/operations.md` specifies stopped-directory backup, restore checks, and a staging upgrade procedure. A regression now loads a legacy snapshot, accepts a current write, checkpoints, and reopens it. This checks one historical format path; online backup and general cross-version migration are not guaranteed. |
| Mixed-type indexed join discrepancy | Open, narrow compatibility gap | `GAPS.md` records the MySQL 8.4 discrepancy and its connection-history dependence. Keep it bounded to a reproducible application case; do not generalize it to ordinary joins. |

For joined mutations, `QueryHandler` parses and checks the request;
`Executor.runMutationJoin` resolves sources and produces joined rows;
`MutationTargets.group` maps aliases to physical tables; the `UPDATE` and
`DELETE` branches claim row identities and build pending batches; `Storage`
applies and publishes them. This extraction gives target grouping a named,
shared boundary without changing the SQL result or lock path. A larger split
should follow measured maintenance pain in source resolution or publication,
with behavior tests and a focused benchmark before moving that boundary.
The shared virtual table registry deserves a separate API design decision because changing it
silently would change what existing connections and listeners can see.

This is not a claim that the remaining architectural or performance concerns
are solved. The large-module refactor needs a narrow seam with independent
behavioral tests; changing `registerTable` to a pure builder needs an explicit
policy for already-open sessions and listeners. Those should be reviewed as API
and architecture changes, not folded into unrelated MySQL compatibility patches.

The deployment contract remains preview-grade: use disposable or restorable
data, bind safely, retain stopped-directory backups, and verify restores before
upgrades. The review's production-readiness concern is therefore still valid
even though several cited code-level hazards have since been addressed.
