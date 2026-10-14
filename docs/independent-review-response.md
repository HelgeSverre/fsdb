# Response to the October 14 independent review

The review inspected `ffb9e71d`. This response checks its actionable findings
against the October 14 main branch through `92fe4a11` and distinguishes completed work from remaining limits. Its
scores are the reviewer's judgment, not a current measured rating.

| Review finding | Current state | Evidence and next action |
|---|---|---|
| No PR or main CI | Resolved after the reviewed revision | `.github/workflows/ci.yml` builds, runs the normal tests, verifies the package consumer, and enforces a branch-coverage threshold on pushes to main and pull requests. The MySQL differential suite remains a separate gate. |
| Observer exceptions can turn committed writes into reported failures | Resolved after the reviewed revision | `Storage.preparePublishedEvents` catches and logs each observer exception and continues to later observers. `EmbeddingIntegrationTests` checks successful write acknowledgement and continued delivery. A synchronous observer can still delay a commit or deadlock by writing back to the store; use the queued subscription for application work. |
| Unbounded commit subscriptions | Resolved after the reviewed revision | `Db.subscribeCommits` defaults to 1024 events; `subscribeCommitsWithCapacity` accepts a positive limit. Overflow drops new events and increments `DroppedCount`, which requires the consumer to resynchronize. The queue is in-process and is not a durable change stream. |
| Hardcoded `version_compile_os` | Resolved after the reviewed revision | `Session.fs` selects Windows, macOS, or Linux at runtime. |
| Async API looks like nonblocking database I/O | Documented limitation | `Db.fs` uses `Task.Run` for embedded async calls; the README now says so and describes cancellation and host concurrency implications. A truly asynchronous executor would need separate design and workload evidence. |
| `registerTable` mutates shared configuration | Open API design issue | `registerScalar` returns a new function registry, while `registerTable` updates `Store.VirtualTables`. `Db.fs` and the README explicitly describe store-level mutation and startup registration. Derived `Db` values sharing a store therefore share virtual tables. If independent derived configurations become a supported use case, move the registry into a per-configuration/session overlay and test both embedded and wire sessions before changing this contract. |
| Large execution modules | Open maintainability issue | `Executor.fs` is about 27,700 lines and `Storage.fs` about 11,700. Extract one cohesive query or mutation responsibility at a time, with behavior-preserving tests and a separate commit from semantic changes. Avoid a wholesale split based only on file size. |
| Durable writes and worker scaling trail MySQL | Open performance issue | The review quotes historical benchmark artifacts. A newer, focused native-host WAL-versus-durable-MySQL insert burst measures FSDB at 5.022–5.038 ms and MySQL at 2.203–2.224 ms for sixteen sequential autocommit inserts ([evidence](../benchmarks/results/c399e5d0-durable-write-pair.md)). This is one workload, not a full replacement for `just bench-write-gate`. The broad gate previously exhausted disk with disposable MySQL binary logs; the benchmark server now disables binary logging while retaining InnoDB commit-time flush. Re-run on a quiet host with enough free disk after write-path changes. |
| Recovery and backup maturity | Partly addressed; preview limit remains | `docs/operations.md` specifies stopped-directory backup, restore checks, and a staging upgrade procedure. Online backup and cross-version migration are not guaranteed. Recovery tests exercise WAL replay and snapshot rotation, but do not establish production field history. |
| Mixed-type indexed join discrepancy | Open, narrow compatibility gap | `GAPS.md` records the MySQL 8.4 discrepancy and its connection-history dependence. Keep it bounded to a reproducible application case; do not generalize it to ordinary joins. |

The next architectural round should start with a map of the mutation path across
`QueryHandler`, `Executor`, and `Storage`, then extract a single stable boundary
around target selection or publication. Success means a smaller dependency
surface for that responsibility, unchanged MySQL-visible behavior, the normal
gate passing, and no regression in a focused benchmark. The shared virtual
table registry deserves a separate API design decision because changing it
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
