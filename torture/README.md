# fsdb torture harness

This directory contains an isolated, developer-only differential test harness
for fsdb. It uses SQL Splitter to generate deterministic MySQL dumps, executes
the same statements through MySqlConnector against MySQL and fsdb, and records
enough evidence to classify and replay the first divergence.

This file is the operator guide: choose a lane, run it, and classify its
artifact. The evidence model, failure taxonomy, and scale-up strategy live in
[`TORTURE-TESTING.md`](TORTURE-TESTING.md). Reviewed discovery reports live
under [`findings/`](findings/); raw run bundles remain ignored under
[`artifacts/`](artifacts/).

Nothing here is part of the root solution or its normal test/benchmark gates.
Focused bugs found here should be promoted into the normal Expecto suite after
they are understood and minimized.

## Contents

- [Design](#design)
- [Quick start](#quick-start)
- [Transaction concurrency](#transaction-concurrency)
- [Cross-database concurrency](#cross-database-concurrency)
- [Crash recovery](#crash-recovery)
- [Syntax mutation](#syntax-mutation)
- [Scale and toolchain](#scale-and-toolchain)
- [Scenarios](#scenarios)
- [Artifacts and classification](#artifacts-and-classification)
- [Development checks](#development-checks)

## Design

| Concern | Design |
|---|---|
| Subject | Native F# starts fsdb on an OS-assigned port; all SQL still crosses the real MySQL wire protocol. Internal catalog, parser, and commit data enrich failure artifacts without adding production tracing APIs. |
| Oracle | A digest-pinned MySQL 8.4.11 container decides semantics. SQL Splitter generates and validates deterministic corpora but is never the database oracle. |
| Differential checks | Scenario probes compare column names, declared result types, ordered typed results, affected rows, and final schema/data state. |
| Syntax checks | Deterministic mutations of known-valid statements compare acceptance, error code, and SQLSTATE. |
| Concurrency checks | Prepared transactions exercise contention, cancellation, savepoints, disconnects, and independent databases. |
| Durability checks | A child fsdb process is killed during commits and checkpoint rotation, then verified through WAL-tail and snapshot recovery. |
| Evidence | Replayable artifacts stay under the ignored `artifacts/` directory. Known gaps are added only by manual review. |

## Quick start

The command selects one independent lane:

| Lane | Purpose | Oracle |
|---|---|---|
| `suite` / `run` | Generated schema, data, queries, and final state | MySQL 8.4 |
| `syntax` | Valid baselines plus bounded syntax and comment mutations | MySQL 8.4 |
| `concurrency` | Contended prepared transactions and fault schedules | MySQL 8.4 plus deterministic invariants |
| `multidb` | Isolation and scaling across independent databases | MySQL 8.4 plus a single-database fsdb baseline |
| `durability` | Crash, WAL-tail, and snapshot recovery | Acknowledged/ambiguous commit sets |

Run `./scripts/run.sh --help` for every option and its default.

### Differential suite

```bash
cd torture
./scripts/run.sh suite
```

The suite continues through all scenarios. Its exit codes are:

- `0`: every scenario matched, or only exact registered gaps were reproduced;
- `1`: generator, oracle, tool, or harness infrastructure failure;
- `2`: one or more new fsdb findings;
- `3`: replay did not reproduce its recorded failure signature.

Run one scenario or replay a prior artifact bundle:

```bash
./scripts/run.sh run --scenario scalar --seed 1 --max-rows 8 --batch-size 1
./scripts/run.sh run --scenario volume --seed 61 --scale 1000 \
  --max-rows 1000000 --batch-size 10000 --invariant-every 0 \
  --timeout-seconds 900
./scripts/run.sh replay --case artifacts/runs/<run>/<case>
```

## Transaction concurrency

Run the independent prepared-transaction concurrency lane:

```bash
./scripts/run.sh concurrency --seed 101 --workers 64 --operations 100 \
  --accounts 128 --hot-accounts 16 --rollback-every 11 \
  --timeout-seconds 180
```

Every worker owns a distinct unpooled connection and two server-side prepared
commands. Transactions begin together, contend on a deterministic account
hotset, insert a committed-operation ledger row, and deterministically commit
or roll back.

The oracle checks exact balances, version counts, committed operation IDs,
rollback absence, total-money conservation, client errors, prepared-command
counts, throughput, and p50/p95/p99 latency. Its reusable phase barrier is
asynchronous so the harness does not manufacture thread-pool starvation at
high connection counts.

The same run applies matched fault schedules to MySQL and fsdb. It cancels a
statement queued for a row lock, rolls a contended write back to a savepoint,
and mixes hot-row commits with disconnects that leave transactions open.

Further schedules contend on the same row under each transaction isolation
level and on a generated unique key reached through `INSERT ... SELECT` and
`REPLACE ... SELECT`. The cases verify atomicity, retained pre-savepoint work,
rollback on disconnect, statement-time duplicate detection, rebased
replacement, and subsequent lock reuse.

Database creation and deletion also run alongside live catalog reads and
transactions on an anchor table. The final committed value and absence of
worker errors protect catalog publication from corrupting unrelated traffic.

## Cross-database concurrency

Run the same prepared-transaction workload across independent databases on one
fsdb process:

```bash
./scripts/run.sh multidb --seed 101 --databases 4 --workers 8 \
  --operations 100 --accounts 32 --hot-accounts 4
```

Each database is compared with its own MySQL run and must conserve balances and
transaction ledgers independently. A separate single-database fsdb run supplies
the scaling baseline; `--scaling-factor` sets the largest accepted fraction of
the serially projected runtime.

## Crash recovery

Run the crash/restart durability lane without Docker or a MySQL oracle:

```bash
./scripts/run.sh durability --seed 101 --workers 16 --operations 500 \
  --restarts 20 --checkpoint-entries 16 --timeout-seconds 15
```

Each operation inserts the same identity into two tables inside one explicit
transaction. The harness kills the server during every work phase, restarts it
against the same data directory, and distinguishes acknowledged commits from
commits whose reply was lost. Recovery must retain every acknowledgement,
never expose one side of a transaction, and never invent an operation.

The lane then crosses automatic snapshot rotation, appends a commit to the new
WAL, and crashes again. Recovery must include that WAL tail. The last restart
follows a graceful checkpoint and must preserve the same recovered sets.

## Syntax mutation

Run the bounded syntax-mutation lane:

```bash
./scripts/run.sh syntax --seed 101 --syntax-cases 2000 --syntax-depth 3

# Execute only the MySQL-accepted feature baselines.
./scripts/run.sh syntax --seed 101 --syntax-cases 0
```

Every feature seed first executes unchanged on both servers. The harness then
applies deterministic token deletion, truncation, duplication, delimiter,
parenthesis, whitespace, comment, punctuation-boundary comment, and case
mutations.

Collation seeds reverse operands and cross scalar, row, quantified, CTE,
`CASE`, `BETWEEN`, `LIKE`, and join comparison paths. Mutation depth one tests
isolated edits; depths two and three sample unique chained edits. A run executes
at most 10,000 mutations.

MySQL error `1064` is matched by numeric code and SQLSTATE. Message text remains
in the evidence but is excluded from parity because error-location prose is not
a stable interface. MySQL-valid mutations must remain valid on fsdb. A mutation
that reaches another MySQL semantic error is classified separately.

The baseline corpus covers implemented features such as HASH partitioning,
compound stored programs, data-changing stored functions, scheduled events,
account options, transaction isolation, temporal SQL modes, administration
statements, and planar spatial operations.

A baseline-only run is an executable feature inventory. Any disagreement still
becomes a finding; a deliberate refusal counts as expected only when its exact
signature is present in the hand-reviewed known-gap ledger.

## Scale and toolchain

`--scale` multiplies the declared model row counts before `--max-rows` applies.
`--invariant-every 0` runs catalog invariants once after the load; use it for
million-row campaigns where checking the entire catalog after every statement
would measure the harness more than the engine. The final invariant check is
never skipped.

If SQL Splitter 1.21.0 is not installed, install an isolated copy:

```bash
./scripts/bootstrap.sh
```

The harness resolves SQL Splitter from `--sql-splitter`, then
`SQL_SPLITTER_BIN`, then `.tools/bin/sql-splitter`, then `PATH`. A version
mismatch is a hard error.

## Scenarios

- `scalar`: MySQL scalar types, NULL/default values, Unicode, escaping, JSON,
  temporal values, and binary literals; one-row INSERT batches.
- `relational`: unique and composite keys plus ordinary and nullable foreign
  keys over tenants, users, memberships, projects, and tasks.
- `commerce`: a deeper customer/product/order/item/payment graph with decimals,
  enums, JSON, binary data, self-reference, and multi-row INSERT batches.
- `volume`: one narrow, deliberately unkeyed table for isolating parser,
  protocol, coercion, storage-growth, aggregate, and snapshot costs from
  relational constraint costs; defaults to 10,000-row INSERT batches.

Defaults are seed `1`, eight rows per table, a 10-second statement deadline,
and the batch size declared by each scenario. Models deliberately use larger
declared row counts; `--max-rows` is the safety cap used by the harness.
The harness deliberately does not pass SQL Splitter's `--strict`: the expected
cap diagnostic is a warning, and strict mode would turn that safety control
into a false generator failure. `--verify` and all structured diagnostics are
still retained.

## Artifacts and classification

### Differential cases

Each case directory contains a versioned `manifest.json` plus the exact inputs
and evidence needed to replay its first divergence. This includes model and SQL
hashes, generator output, statement byte ranges, parser and target outcomes,
commit summaries, catalog invariants, semantic probes, final snapshots,
comparisons, and timings.

Large statements use bounded prefix and suffix previews in JSON;
`generated.sql` remains the byte-exact source. `failure.sql` preserves a local
failing statement or probe in full. Large post-load comparisons use 4,096-row
typed-data chunk hashes and bounded samples rather than embedding entire tables
in JSON.
Probe-type and affected-row mismatches have distinct signatures.

### Syntax cases

Syntax runs write their complete bounded corpus and mutation chains to
`mutations.sql` and a schema-versioned `manifest.json` containing the parser
result, both server outcomes, classification, and failure signature for every
case.

Outcomes distinguish generator rejection, MySQL rejection, fsdb parser and
execution gaps, contained internal errors, protocol faults, timeouts, schema or
data mismatches, invariant failures, and infrastructure failures.

The harness never adds a finding to `support/known-gaps.json`; entries are
reviewed and added manually by exact failure signature.

### Concurrency and durability

Concurrency cases use their own schema-versioned manifest and classifications:
`oracle_concurrency_failure`, `fsdb_concurrency_execution_gap`, and
`fsdb_transaction_atomicity_gap`. A successful COMMIT is not accepted as
evidence by itself—the final ledger and account oracle must prove that every
committed transaction survived.

Durability cases record attempted, acknowledged, ambiguous, and recovered
operations plus missing acknowledgements, partial transactions, impossible
rows, restart count, automatic-checkpoint and snapshot verification, process
logs, and the retained data directory. A durability mismatch exits `2`;
child-process or harness failure exits `1`.

Syntax classifications distinguish matched errors, accepted mutations,
fsdb over-acceptance, fsdb rejection of MySQL-valid syntax, error-contract
mismatches, semantic oracle rejection, and infrastructure failures.

## Development checks

```bash
dotnet build Fsdb.Torture.slnx
dotnet run --project tests/Fsdb.Torture.Tests
./scripts/run.sh check-tools
```

The SQL scanner is intentionally limited to ordinary MySQL dump statements. It
handles quoted strings, backtick identifiers, escapes, and comments, but
rejects `DELIMITER` scripts explicitly.
