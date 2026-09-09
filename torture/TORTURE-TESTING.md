# fsdb torture-testing design

This document defines the harness's evidence and classification contracts.
For commands and day-to-day operation, start with the
[operator guide](README.md).

## Contents

- [Decision and boundaries](#decision-and-boundaries)
- [Execution pipeline](#execution-pipeline)
- [Evidence contract](#evidence-contract)
- [Failure taxonomy](#failure-taxonomy)
- [Corpus](#corpus)
- [Scaling into a torture campaign](#scaling-into-a-torture-campaign)
- [Finding lifecycle](#finding-lifecycle)
- [Automation policy](#automation-policy)
- [Acceptance checkpoints](#acceptance-checkpoints)

## Decision and boundaries

Keep the complete torture system under `torture/`. It has its own solution,
projects, corpus, scripts, support ledger, findings, and ignored artifacts. It
must not be added to the root solution, root task runner, or ordinary CI until
the harness deliberately chooses a stable subset to promote.

The harness is bespoke F# for fsdb. SQL Splitter supplies deterministic MySQL
DDL and data; it does not decide whether fsdb is correct. MySQL 8.4 is the
semantic reference, while fsdb's parser, commit stream, and in-memory catalog
provide subject-side evidence that an external black-box runner could not.

The goal is not merely to make fsdb return an error under load. A useful run
must answer:

1. Which exact generated input and toolchain produced the failure?
2. Did generation, MySQL, parsing, fsdb execution, an invariant, a semantic
   query, or the final state comparison first diverge?
3. Was the failure contained, timed out, or did it damage later state?
4. Can the same evidence signature be replayed?
5. Is it a new implementation gap, a harness/oracle defect, or an explicitly
   reviewed known gap?

## Execution pipeline

```text
model + seed + limits
        |
        v
SQL Splitter verify/generate ----> immutable SQL + hashes + diagnostics
        |
        v
byte-offset statement scanner
        |
        +--> MySQL executes first (acceptance oracle)
        |
        +--> fsdb Parser.parse + wire-protocol execution
                 |
                 +--> OnCommit summaries + direct catalog invariants
        |
        v
scenario SELECT probes on both targets
        |
        v
schema, index, FK, row-count, and typed-row snapshots
        |
        v
classification + stable signature + replay bundle
```

MySQL executes each generated statement first. If MySQL rejects it, the case
is an oracle/generator problem and is not charged to fsdb. fsdb is not asked to
execute a statement its own parser rejected; that keeps parser gaps distinct
from protocol and executor gaps.

The concurrency lane is separate from generated bulk loading. It constructs a
deterministic transfer plan, opens one physical connection per worker, prepares
an account-update command and ledger-insert command on each, and runs explicit
transactions through synchronized start/finish phases.

Because every successful transfer has a deterministic additive effect, final
balances, version counts, committed operation IDs, rollback absence, and total
conservation have one exact answer regardless of scheduling. MySQL must satisfy
that answer before fsdb is judged.

The multi-database lane applies that workload to independent databases on one
fsdb process. It compares each database with its own MySQL outcome, checks for
cross-database state bleed, and compares concurrent wall-clock time with a
separate single-database fsdb baseline.

The durability lane is also separate from the differential oracle. It runs a
WAL-backed fsdb child process and repeatedly kills it while synchronized
workers commit two-table transactions. A returned COMMIT is durable evidence;
a disconnected COMMIT is ambiguous and may be wholly present or absent.
Partial transactions, missing acknowledgements, and rows outside the attempted
set are always failures. A final graceful checkpoint and restart covers the
snapshot path with the same recovered-state oracle.

The syntax lane starts from known-valid statements spanning implemented
grammar and the open compatibility ledger. It executes each baseline on both
servers, then applies a seed-ordered, bounded set of structural mutations. A
run may chain up to three edits while deduplicating equivalent SQL before
sampling.

Fixtures may temporarily change session state to create durable input for a
baseline, then restore it before mutation begins. This lets read-only seeds
exercise mode-dependent values without making later classifications depend on
execution order.

Comment mutations replace natural token boundaries with MySQL block, hash,
dash, executable-version, or future-version comments and never recurse into an
existing comment. Block and version comments also surround parentheses and
commas outside quoted values, covering legal boundaries that contain no
whitespace in the source.

Successful DDL mutations are removed before the next case so stored objects
cannot contaminate later parser results. MySQL `1064` responses are compared
by error code and SQLSTATE rather than location text. MySQL-valid mutations
exercise fsdb acceptance; mutations that reach other semantic errors remain
visible without being mislabeled as syntax evidence.

After each successful fsdb mutation, the harness records compact commit-event
hashes and validates row arity, primary/unique keys, foreign-key references,
and auto-increment state directly against `Store.Catalog`. This catches damage
at the statement that introduced it instead of discovering it only in a final
SELECT.

## Evidence contract

Every case bundle records:

- fsdb revision, dirty flag, and assembly SHA-256;
- SQL Splitter version/path/SHA-256, MySQL version, model and resolved-model
  hashes, seed, scale, row cap, batch size, invariant cadence, deadlines,
  generated SQL size/hash, phase timings, and process peak working set;
- SQL Splitter stdout/stderr and structured diagnostics;
- every statement's UTF-8 byte range, SHA-256, bounded SQL prefix/suffix,
  parser result and AST kind, MySQL outcome, fsdb outcome, error code/SQLSTATE,
  elapsed time, commit summaries, and invariant results; `generated.sql` is
  retained as the full byte-exact source and `failure.sql` retains a causal
  failing statement in full;
- each bespoke query probe's SQL hash, parser result, target status, columns,
  declared result types, ordered canonical rows, result hash, timing, and
  first difference;
- ordered DML outcomes and affected-row counts under both found-row and
  changed-row client capability modes;
- normalized schema, indexes, ordered FK columns, NULL-aware default metadata,
  row counts, deterministically ordered typed-data hashes, 4,096-row chunk
  hashes, bounded first/last samples, and the first differing chunk;
- the classification, its unhashed diagnostic detail, the evidence-sensitive
  failure signature, and exact known-gap match state.

Passwords and the MySQL connection string are intentionally not persisted.

## Failure taxonomy

| Phase | Classifications | Meaning |
|---|---|---|
| Tool/generation | `generator_preflight`, `infrastructure` | Tool mismatch, invalid generated corpus, process or oracle setup failure |
| Oracle | `oracle_rejected`, `oracle_timeout` | MySQL did not accept or complete the supposedly valid input |
| Concurrency | `oracle_concurrency_failure`, `fsdb_concurrency_execution_gap`, `fsdb_transaction_atomicity_gap`, `multidb_scaling_gap` | The reference run failed, fsdb returned a protocol/execution error, successful replies produced the wrong committed state, or independent databases serialized beyond the configured scaling bound |
| Durability | `durability_failure`, `infrastructure` | Crash or snapshot recovery lost an acknowledgement, split a transaction, invented a row, or the child process could not be exercised |
| Parser | `fsdb_parser_gap`, `fsdb_probe_parser_gap` | MySQL accepted SQL that fsdb cannot parse |
| Syntax mutation | `matched_syntax_error`, `accepted_mutation`, `fsdb_syntax_acceptance_gap`, `fsdb_syntax_rejection_gap`, `syntax_error_contract_mismatch` | Mutated syntax matched, remained valid, or exposed an acceptance/error-contract difference |
| Compatibility contracts | `pass`, `oracle_contract_drift`, `status_mismatch`, `error_contract_mismatch`, `result_schema_mismatch`, `result_type_mismatch`, `result_mismatch`, `affected_rows_mismatch` | A declarative text, prepared-protocol, error, or named-connection contract matched or exposed a focused difference |
| Subject execution | `fsdb_execution_gap`, `fsdb_probe_execution_gap`, `contained_internal_error` | Parsed SQL failed in fsdb; error 1105 remains separately visible |
| Wire/deadline | `protocol_fault`, `fsdb_timeout` | Driver/protocol failure or subject deadline |
| Internal state | `invariant_failure` | fsdb committed a structurally invalid catalog/data state |
| Client contract | `statement_affected_rows_mismatch`, `dml_affected_rows_mismatch`, `probe_type_mismatch` | Successful mutations reported different counts, or equal values carried observably different result types |
| Semantic query | `probe_schema_mismatch`, `probe_result_mismatch` | A bespoke query returned different columns or ordered typed rows |
| Final state | `schema_mismatch`, `row_count_mismatch`, `data_mismatch`, `metadata_or_snapshot_failure` | Load succeeded but observable state diverged |

Exit status distinguishes infrastructure (`1`), new fsdb findings (`2`), and
replay drift (`3`). A known gap counts as passing only when its complete failure
signature exactly matches a manually reviewed entry.

## Corpus

The scenarios intentionally cross different layers:

- `scalar`: integer widths/signs, large exact and approximate numbers, CHAR and
  Unicode/escaping, text, bytes/blob, JSON, booleans, dates/timestamps, NULL,
  and database defaults; single-row batches localize value failures.
- `relational`: unique and composite keys, junction planning, ordinary and
  nullable FKs, enums, JSON, and decimals; probes check orphan counts and
  grouped membership results.
- `commerce`: a deeper customer/product/order/item/payment graph, nullable and
  self references, wide decimals, JSON/blob data, and multi-row batches;
  probes cover grouped totals, orphans, and calculated extended prices.
- `volume`: a narrow unkeyed table with deterministic IDs, signed and exact
  numbers, booleans, buckets, payloads, and an ordinary index; it isolates raw
  ingestion, aggregation, result typing, and bounded snapshot behavior from
  primary/unique/FK enforcement.

These are vertical diagnostics. A case stops at its first causal divergence so
later failures are not artifacts of corrupted or missing state.

## Scaling into a torture campaign

Scale dimensions independently before combining them:

1. **Seed breadth.** Run deterministic seed matrices and retain only unique
   signatures. Start with 1–20 at eight rows before larger data.
2. **Batch shape.** Exercise batches 1, 8, 64, and generator maximums. This
   separates value handling from multi-row packet and executor behavior.
3. **Cardinality.** Increase one dimension at a time while recording
   generation, statement, invariant, probe, snapshot, throughput, and memory
   evidence. Repeat established large campaigns after storage or planner
   changes to obtain stable A/B comparisons.
4. **Type boundaries.** Add dedicated models for signed and unsigned limits,
   precision and scale edges, zero and empty values, Unicode normalization,
   NUL bytes, temporal boundaries, large blobs, and deeply nested JSON.
5. **Relational depth.** Add composite foreign keys, cyclic DDL ordering,
   nullable composite references, long dependency chains, cascading actions,
   and invalid FK or unique mutations with matched rejection oracles.
6. **DML sequences.** Compose deterministic updates, deletes, insert-select,
   upserts, transactions, rollbacks, constraint violations, and state checks.
7. **Protocol stress.** Exercise prepared statements, parameter types, large
   packets, connection churn, concurrent sessions, cancellation, and partial
   disconnects.
8. **Durability.** Vary concurrent writers, crash cadence, WAL volume, and
   checkpoint boundaries. Preserve exact acknowledged and ambiguous commit
   evidence for every run.

Do not simply skip a first failing statement to expose later failures; that
creates meaningless downstream differences. To move behind a known failure
envelope, make a clearly named derived model that removes only the unsupported
surface (for example, a scalar model without binary literals), keep the
original case, and document the derivation. Once the gap is fixed, delete or
retarget the derived model so coverage does not fragment permanently.

## Finding lifecycle

For each new signature:

1. Replay the complete artifact case without changing seed or limits.
2. Decide whether the earliest divergence belongs to SQL Splitter, MySQL setup,
   the scanner/canonicalizer, or fsdb.
3. Minimize the SQL while preserving classification and error detail.
4. Add a focused root Expecto regression when the fsdb behavior is understood.
5. Fix fsdb, then replay both the minimized test and original generated case.
6. If deferring, add the exact signature to `support/known-gaps.json` only after
   review and write a dated note under `findings/`.

Known gaps are a ledger, not wildcard suppression. Never auto-enroll failures,
match on message fragments, or accept a changed signature without review.

## Automation policy

Keep the default developer smoke suite small: seed 1, eight rows/table, native
scenario batch sizes, and a ten-second per-operation deadline. It should finish
all scenarios even when one finds a subject gap.

Run broader seed/cardinality matrices manually or in a separate scheduled job.
Archive only manifests/minimized failures and aggregate timing/signature
indexes; raw SQL/data bundles can be large and may stay as short-lived ignored
artifacts. Promote stable, fast regressions to the normal fsdb test suite rather
than wiring this whole harness into root builds.

At large scale, choose invariant cadence deliberately. `--invariant-every 0`
means final-only, not disabled; a positive value validates after every Nth
statement and always once at the end. The harness reads and splits the complete
generated SQL file in memory even though result snapshots are bounded. Its
reported process working set is therefore an upper bound on the combined
harness/fsdb cost until generation hashing and statement scanning become
streaming operations or fsdb runs in a separately measured child process.

## Acceptance checkpoints

- A clean machine can bootstrap the pinned generator and verify tool versions.
- All models pass SQL Splitter verification at the requested safety cap.
- The scanner has focused tests for quoting, escaping, comments, UTF-8 byte
  offsets, directive rejection, and malformed input.
- MySQL and fsdb use the same driver and deadlines for statements and probes.
- Every subject mutation produces commit/invariant evidence.
- Every full load runs scenario probes, a normalized final snapshot, and a
  final direct-catalog invariant check regardless of periodic cadence.
- Every new finding replays to the same signature; replay drift exits `3`.
- No finding becomes known automatically and no root build wiring is added.
