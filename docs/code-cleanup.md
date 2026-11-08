# Correlated planner cleanup

Status: locally verified; CI on the changed head is pending. This sweep covers
the correlated `COUNT` and `EXISTS` planner after the nullable `HAVING` changes.

## Scope and baseline

- Repository: `fsdb`; target: the .NET 10 `Fsdb` library and its SQL executor.
- Baseline: `ec42b62756dc5c5b10429d8b416ce3148213ee4e`, clean worktree before
  this document was added. The release-only commit `eef1ef37` changed versions
  and documentation; the diagnostics tests and this file are task-owned edits.
- Included: `src/Fsdb/Engine/Executor.fs` statement memo, correlated membership,
  grouped key count planning, `src/Fsdb/Foundation/Diagnostics.fs` suppression,
  and first-party tests in `tests/Fsdb.Tests/QueryHandlerDiagnosticsTests.fs`.
- Excluded: wire authentication and transaction scheduling, spatial and
  full-text algorithms, storage internals except interfaces called by this
  planner, and public library API compatibility. The touched planner helpers
  are private and existed before this cleanup.

## Validation matrix

| Gate | Source | Snapshot and artifact | Platform/mode | Command | Current outcome |
| --- | --- | --- | --- | --- | --- |
| Local build and regression | `justfile` | baseline `ec42b627`, Debug `Fsdb.Tests.dll` SHA-256 `6df64458bb6cce9a93d9f930850f4b51ef35dda4d525370c0bb9e9075095106b` | macOS, .NET SDK 10.0.401, Debug | `just check` | passed: 3,932 tests, zero warnings/errors |
| Package consumer | `justfile`, CI | baseline `ec42b627`, Release `Fsdb.dll` SHA-256 `bfbfa7172e830f4b1c12d9e03d5df73001a31281391abad17abbea0202c4d830` | macOS, Release | `just package-check` | passed: `Fsdb.0.1.2` package and symbols; consumer returned 42 |
| CI package build and consumer | `.github/workflows/ci.yml` | baseline `ec42b627` | Ubuntu, .NET 10, Release | `dotnet build Fsdb.slnx -c Release`; `scripts/verify-package.sh` | passed; [run 42497999050](https://github.com/HelgeSverre/fsdb/actions/runs/42497999050) |
| CI regression and branch coverage | `.github/workflows/ci.yml` | release-only `eef1ef37` (same production source) | Ubuntu, .NET 10, Release regression and Debug coverage | workflow jobs | all jobs passed; [run 42499510017](https://github.com/HelgeSverre/fsdb/actions/runs/42499510017) |
| Changed-head local regression | `justfile` | uncommitted source on `eef1ef37`; Debug `Fsdb.Tests.dll` SHA-256 `a8080d35576c5c3d1d948848b3465d0ac06ddd5e6a5efe0cec1c01d8d8dd26db` | macOS, .NET SDK 10.0.401, Debug | `just check` | passed: 3,946 tests, zero warnings/errors |
| Changed-head package consumer | `justfile` | changed source on `eef1ef37`; Release `Fsdb.dll` SHA-256 `9c6574f09dff52170b8887929085c24dc07965df4ddf77265a9059a62044e9b7` | macOS, .NET SDK 10.0.401, Release | `just package-check` | passed: `Fsdb.0.1.3` package and symbols; consumer returned 42 |

The shipped package is the `Fsdb` library; no CLI executable behavior changes
are in scope. A green baseline gate does not validate later source edits.

## Sweep A: state-machine correctness

| Machine | Owner and representation | Entry points and linked machines | Current disposition |
| --- | --- | --- | --- |
| A1 statement memo and warning ledger | `Executor.StatementMemo` in `AsyncLocal`; reference-keyed dictionaries, including count shapes and grouped results, plus one `Pending`/`Reported` warning state | statement reset/current memo; correlated evaluation; A2 and A3 | changed and locally verified |
| A2 count shape | `tryCorrelatedExists` classifies nullable/always-present counts, grouped safety, and literal projection binding | `evalExpr Exists`; A1, A3 | inspected in discovery |
| A3 grouped key result | `tryDecorrelatedKeyShape`, `decorrelatedKeyCounts`, and `tryDecorrelatedMatchCount` cache exact numeric key counts or no plan | scalar subquery and `EXISTS`; A1, A2 | changed and locally verified |

Invariant A1: a cached plan or count belongs to the current statement's store,
catalog, scope and SQL expression. Invariant A2: a shortcut preserves binding
errors, NULL counting, empty aggregate groups and all grouped `HAVING` results.
Invariant A3: only exact compatible correlation keys may use the precomputed
dictionary; other values follow ordinary evaluation.

| From | Event and guard | Owner/effect | To | Failure or fallback | Evidence |
| --- | --- | --- | --- | --- | --- |
| no statement memo | statement starts | install fresh `StatementMemo` | empty caches | no reuse across statements | `resetStatementMemo`, `currentStatementMemo` |
| count shapes absent | eligible nullable `HAVING` seen | derive nullable and matched `SELECT`s and fixed-group proof | shapes cached by select identity | ineligible shape follows ordinary executor | `tryCorrelatedExists` |
| grouped result absent | stable shape and actual exact numeric outer key, outside metadata/suppression | execute grouped inner query once, collect counts | `Some` dictionary cached | non-exact/NULL key or probe defers without caching; grouped failure caches `None` and falls back | `tryDecorrelatedMatchCount`, `decorrelatedKeyCounts` |
| grouped result cached | outer row arrives | evaluate outer key and look up count | result for that row | coercive key or evaluation error follows fallback/error path | `tryDecorrelatedMatchCount` |
| numeric conversion absent | malformed finite DOUBLE string bound reached in correlated execution | normalize bound; defer during metadata/suppression or emit once in runtime | `Pending(text)` or `Reported` | source-free/inner `LIMIT 0` discard scope; nested derived child can report before outer limit | `recordCorrelatedNumericWarning`, `runSelectStmt` |
| numeric conversion pending | SELECT completes outside metadata/suppression | emit once and retain text | `Reported` | zero-limit rolls back only new pending entries | `flushCorrelatedNumericWarnings`, `rollbackCorrelatedNumericWarnings` |

### Discovery findings

| ID | Priority / impact | Trace and invariant | Current status / next check |
| --- | --- | --- | --- |
| A-01 | confirmed, warning count | With numeric outer keys `(1),(1),(2)` and two inner rows, correlated `COUNT(i.payload)` with residual `i.k='12.5x'` returns three correct counts. MySQL 8.4.11 emits one 1292 warning; baseline fsdb's grouped path emits zero. A trial that skips decorrelation during metadata probing emitted two, so probe caching explains loss but is not a complete fix. | Fixed and locally verified in changed batch: usable outer keys precede materialization; metadata/suppression cannot populate grouped counts; the correlated constant conversion reports once. Exact `=`, `IN`, nullable `HAVING`, NULL-safe, grouped, empty-outer, nested-derived, NULL-then-numeric and zero-limit controls passed in the 3,946-test suite. CI pending. |
| A-03 | confirmed, warning count | The same query with scalar subquery `LIMIT 1` follows ordinary correlated evaluation: MySQL 8.4.11 emits one 1292 warning, baseline fsdb three. | Fixed in same batch by the statement-local conversion owner; focused and full-suite tests passed. CI pending. |
| A-04 | confirmed, warning count | For indexed `i.k BETWEEN '12.5x' AND '13x'` with the same repeated outer rows and two inner rows, pinned MySQL emits 14 warnings for both ordinary `LIMIT 1` and decorrelated query; fsdb emits 6 and 4 respectively. Without the index, MySQL emits 6 in both correlated forms and fsdb emits 6 ordinary / 4 grouped. See the linked GAPS finding. | Unresolved: `BETWEEN` has access-path-sensitive warning cardinality beyond the constant-bound rule. Investigate predicate order and candidate scans before adding a committed exact regression. |
| A-02 | coverage gap | Unsupported unknown, distinct, or second nullable `COUNT` argument must fall back to ordinary binding/evaluation. | No bug confirmed; add focused fallback controls if oracle confirms expected diagnostics. |

The first candidate for A-01 predicted duplicate warnings when an outer key was
textual and rejected by the planner. A pinned MySQL 8.4.11 check and the new
focused test both return one warning, so that prediction was falsified. The
numeric-key and ordinary-path cases above are separate confirmed findings.
Further oracle probes show that a direct unhinted `DOUBLE = '12.5x'` query
warns once with or without a matching row, whereas a direct `IGNORE INDEX`
scan warns once per evaluated row. The correlated `IGNORE INDEX` query still
warns once. Warning cardinality is access-path sensitive, so a global
deduplication rule is not justified by these observations.
The new scoped conversion owner also matches MySQL for `IN('12.5x')`: one
warning for the repeated-key fixture. A correlated inner `LIMIT 0` reports
none; a nonzero correlated query whose outer filter returns no rows reports
one. These controls are in `QueryHandlerDiagnosticsTests.fs`.

The lifecycle and SQL-consumer discovery lenses found no confirmed stale memo
or binding bug at the baseline. Nested trigger DML can reset the async-local
memo, but no semantic failure was established; it remains a performance
hypothesis outside this planner cluster.

## Sweep B: module depth

| Cluster and callers | Current caller knowledge | Deletion test and owner decision | Disposition |
| --- | --- | --- | --- |
| `tryCorrelatedExists` and `evalExpr Exists` | dispatch order, count classification, grouped safety and projection binding | retain membership owner and existing nullable-HAVING fixed-group proof; sharing a cross-call plan requires CTE-scope identity | retained with reason |
| `tryDecorrelatedKeyShape` / `decorrelatedKeyCounts` / `tryDecorrelatedMatchCount` | first actual key previously triggered shape derivation twice | pass the checked shape directly to materialization; retain cache recheck for reentrancy | changed and verified; no cross-call plan cache added because CTE bindings can change |
| `StatementMemo` count and warning caches | SELECT wrapper previously coordinated two warning collections | one private `Pending`/`Reported` ledger owns warning transitions; SELECT takes a checkpoint only when a LIMIT exists | changed and verified; `Diagnostics.isSuppressed` is internal |

## Round log

- Sweep A Discover: three read-only reviewers assigned lifecycle, concurrency,
  and SQL consumer/coverage lenses at baseline `ec42b627`. Pinned MySQL and
  focused fsdb tests confirmed A-01 and A-03, and falsified the initial
  duplicate-warning prediction. A-02 remains a coverage gap.
- Sweep A Challenge: three reviewers independently traced suppressed metadata
  cache contamination and the separate ordinary-path conversion count. They
  rejected a global warning deduplication rule.
- Sweep A Verify on an intermediate patch found a grouped SELECT finalization
  hole and prompted finalization at the shared `runSelectStmt` boundary. A
  later full suite passed 3,944 tests.
- Sweep B Discover: three reviewers inventoried the warning state, SELECT
  finalization, and grouped count plan. They proposed one warning state owner,
  a lazy zero-limit checkpoint, and an explicit fallback/planned/materialized
  decorrelation plan.
- Sweep B Challenge rejected a cross-call plan cache without CTE-scope identity
  and an early `rowCount` check that could change error order. The coordinator
  combined warning states and passed the same shape through one actual call.
- Sweep B Verify was repeated on the later source snapshots. Three reviewers
  found no concrete regression in the warning ledger, SELECT finalization, or
  shape carry-through. Their mixed NULL-then-numeric control and nested derived
  `LIMIT 0` control passed against pinned MySQL and in the final 3,946-test
  local suite. The Release package consumer also passed.

## Residual work

CI on `4d6b49d2` passed all three jobs. The focused [performance
comparison](../benchmarks/results/4d6b49d2-correlated-warning-comparison.md)
found no material regression in the repeat run, although machine drift in the
first run limits the claim. A-04 is an open compatibility gap; A-02 is a
fallback-coverage gap. No broad concurrency safety claim is inferred from the
bounded suite.
