# Response to the October 14 independent review

The review inspected `ffb9e71d`, so its scores and historical benchmarks do not
describe current main. This is a disposition of its actionable findings, not a
new production-readiness score.

| Finding | Current disposition | Evidence and remaining limit |
|---|---|---|
| Missing PR/main validation | Addressed | [CI](../.github/workflows/ci.yml) builds, runs the regression suite, verifies a package consumer, and enforces branch coverage on pushes and pull requests. The MySQL differential suite remains a separate gate. |
| Observer failure changes the result of a committed write | Addressed | `Storage.preparePublishedEvents` isolates each observer exception and continues delivery. [Embedding regressions](../tests/Fsdb.Tests/EmbeddingIntegrationTests.fs) check acknowledgement, subsequent observers, and WAL recovery. |
| Unbounded commit subscriptions | Addressed | `Db.subscribeCommits` uses a bounded queue, exposes `DroppedCount`, and unregisters on disposal. Overflow requires consumer resynchronization; the queue is neither a durable stream nor an acknowledgement protocol. |
| Hardcoded `version_compile_os` | Addressed | Session variables select the host OS at runtime. |
| Async API described as native asynchronous execution | Documented limit | [README](../README.md) says the embedded async methods use `Task.Run` offloading. |
| `registerTable` mutates a shared store | Documented API contract | Registrations are store-wide for newly opened sessions. Existing sessions retain their captured registry. A configuration-local overlay would need a separate API and visibility tests; silently changing this method would break its current contract. |
| Synchronous `onCommit` callback can delay or re-enter a commit | Open | Callbacks still execute inside `CommitLock` to preserve delivery order. The [README](../README.md) recommends the bounded subscription for application work. A callback that writes back into the store can deadlock; moving callbacks outside the lock without a delivery design would lose ordering. |
| Large executor and storage modules | Open | Narrow responsibilities have moved to `MutationTargets.fs` and `ExplainRendering.fs`, but `Executor.fs` remains large. Further extraction should follow a real edit boundary and preserve behavior, rather than split by size. |
| Durable write throughput and worker scaling | Open | The [current matched checkpoint](../benchmarks/results/2026-10-15-current-head-write-follow-up.md) measured 12,346 WAL-backed wire inserts/sec against 45,640 durable MySQL inserts/sec with eight writers (0.27×). A WAL-backed embedded control reached 32,672 inserts/sec. Prior admission-lock improvement was retained; unsuccessful scheduling and WAL-handle prototypes were reverted. The stage profiles narrow the cost but do not establish another causal optimization. |
| Recovery, backup, and upgrades | Partly addressed | The [operations guide](operations.md) covers stopped-directory backup and staging restore. A legacy-snapshot upgrade regression covers one historical format path. Online backup and general cross-version migration remain unsupported. |
| Mixed-type indexed join discrepancy | Open, narrow | [GAPS](../GAPS.md) records a connection-sensitive MySQL 8.4 result. It does not imply ordinary joins are broken; a change needs an application case and a stable oracle reproduction. |

The historical security issues cited in the review were closed before this
recheck. None of the remaining concerns is evidence that FSDB is a dependable
drop-in production MySQL replacement. Continue compatibility work with the
MySQL oracle, but keep durable-write performance, notification semantics,
module boundaries, and recovery guarantees visible as separate workstreams.
