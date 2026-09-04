# FSDB torture harness

This directory contains an isolated, developer-only differential test harness
for FSDB. It uses SQL Splitter to generate deterministic MySQL dumps, executes
the same statements through MySqlConnector against MySQL and FSDB, and records
enough evidence to classify and replay the first divergence.

The durable strategy and scale-up guidance live in
[`TORTURE-TESTING.md`](TORTURE-TESTING.md). Reviewed discovery reports
live under `findings/`; raw run bundles remain ignored under `artifacts/`.

Nothing here is part of the root solution or its normal test/benchmark gates.
Focused bugs found here should be promoted into the normal Expecto suite after
they are understood and minimized.

## Design

- The harness is native F# and references FSDB directly.
- FSDB runs in-process on an OS-assigned port, while all SQL still travels over
  its real MySQL wire protocol.
- `Store.OnCommit`, `Store.Catalog`, and `Parser.parse` provide subject-side
  diagnostics without adding production tracing APIs.
- MySQL 8.4.11 is the semantic oracle. The Compose file pins its image digest.
- SQL Splitter 1.21.0 is the deterministic corpus generator and preflight
  verifier. It is not treated as the database oracle.
- Scenario-specific SELECT probes compare column names, declared result types,
  and ordered typed results before the final schema/data snapshot.
- An ordered DML battery compares affected-row counts in both found-row and
  changed-row client modes, including composite-index, checked-view, nested
  join-view, and ordered compound-trigger writes.
- A deterministic syntax lane mutates known-valid feature statements and
  compares MySQL and FSDB error codes and SQLSTATEs.
- A durability lane runs fsdb as a child process, kills it during concurrent
  commits, and verifies WAL and snapshot recovery independently of MySQL.
- Generated artifacts stay under `artifacts/`, which is ignored.

## Quick start

### Differential suite

```bash
cd torture
./scripts/run.sh suite
```

The suite continues through all scenarios. Its exit codes are:

- `0`: every scenario matched, or only exact registered gaps were reproduced;
- `1`: generator, oracle, tool, or harness infrastructure failure;
- `2`: one or more new FSDB findings;
- `3`: replay did not reproduce its recorded failure signature.

Run one scenario or replay a prior artifact bundle:

```bash
./scripts/run.sh run --scenario scalar --seed 1 --max-rows 8 --batch-size 1
./scripts/run.sh run --scenario volume --seed 61 --scale 1000 \
  --max-rows 1000000 --batch-size 10000 --invariant-every 0 \
  --timeout-seconds 900
./scripts/run.sh replay --case artifacts/runs/<run>/<case>
```

### Transaction concurrency

Run the independent prepared-transaction concurrency lane:

```bash
./scripts/run.sh concurrency --seed 101 --workers 64 --operations 100 \
  --accounts 128 --hot-accounts 16 --rollback-every 11 \
  --timeout-seconds 180
```

Every worker owns a distinct unpooled connection and two server-side prepared
commands. Transactions begin together, contend on a deterministic account
hotset, insert a committed-operation ledger row, and deterministically commit
or roll back. The oracle checks exact balances, version counts, committed
operation IDs, rollback absence, total-money conservation, client errors,
prepared-command counts, throughput, and p50/p95/p99 latency. Its reusable
phase barrier is asynchronous so the harness does not manufacture thread-pool
starvation at high connection counts.

### Crash recovery

Run the crash/restart durability lane without Docker or a MySQL oracle:

```bash
./scripts/run.sh durability --seed 101 --workers 16 --operations 500 \
  --restarts 20 --timeout-seconds 15
```

Each operation inserts the same identity into two tables inside one explicit
transaction. The harness kills the server during every work phase, restarts it
against the same data directory, and distinguishes acknowledged commits from
commits whose reply was lost. Recovery must retain every acknowledgement,
never expose one side of a transaction, and never invent an operation. The
last restart follows a graceful snapshot checkpoint and must preserve the
same recovered sets.

### Syntax mutation

Run the bounded syntax-mutation lane:

```bash
./scripts/run.sh syntax --seed 101 --syntax-cases 2000 --syntax-depth 3

# Execute only the MySQL-accepted feature and gap baselines.
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
a stable interface. MySQL-valid mutations must remain valid on FSDB. A mutation
that reaches another MySQL semantic error is classified separately.

The baseline corpus covers:

- implemented features such as HASH partitioning, compound stored programs,
  data-changing stored functions, account options, transaction isolation, and
  administration statements;
- declared gaps in administration, event scheduling, and spatial operations.

A baseline-only run is an executable gap inventory. Declared refusals remain
findings until they are implemented or added to the hand-reviewed known-gap
ledger.

### Corpus scale and tools

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

Each case directory contains the exact model and SQL hashes, generator output,
the generated SQL, split-statement byte ranges and hashes, parser and target
outcomes, commit summaries, catalog invariants, semantic probes, snapshots,
comparison, timings, and a versioned `manifest.json`.

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

Outcomes distinguish generator rejection, MySQL rejection, FSDB parser and
execution gaps, contained internal errors, protocol faults, timeouts, schema or
data mismatches, invariant failures, and infrastructure failures. The harness
never adds a finding to `support/known-gaps.json`; entries are reviewed and
added manually by exact failure signature.

### Concurrency and durability

Concurrency cases use their own schema-versioned manifest and classifications:
`oracle_concurrency_failure`, `fsdb_concurrency_execution_gap`, and
`fsdb_transaction_atomicity_gap`. A successful COMMIT is not accepted as
evidence by itself—the final ledger and account oracle must prove that every
committed transaction survived.

Durability cases record attempted, acknowledged, ambiguous, and recovered
operations plus missing acknowledgements, partial transactions, impossible
rows, restart count, snapshot verification, process logs, and the retained
data directory. A durability mismatch exits `2`; child-process or harness
failure exits `1`.

Syntax classifications distinguish matched errors, accepted mutations,
FSDB over-acceptance, FSDB rejection of MySQL-valid syntax, error-contract
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
