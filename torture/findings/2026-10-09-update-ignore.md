# UPDATE IGNORE constraint failures

Native MySQL 8.4.11 with a disposable 64 MiB buffer pool and redo capacity is
the oracle. At the batching baseline, the [native results](2026-10-09-update-ignore-native.json) and
[fsdb replay](2026-10-09-update-ignore-current.json) match across ten of eleven
scripts. That routine-write case also fails on the
[pre-batching baseline](2026-10-09-update-ignore-before-batching.json).

## Behavior

UPDATE IGNORE converts duplicate-key, missing-parent, and restricted-parent
storage failures to warnings and skips only the rejected row. Accepted earlier
and later rows remain. Ordered LIMIT counts selected rows, including rejected
ones. Single-table and joined UPDATE use the same execution helper.

BEFORE-trigger effects survive an ignored row failure. AFTER triggers run
immediately after each accepted row, including accepted no-ops. Errors raised
by a trigger remain errors even when their numeric code is normally ignorable.
The statement snapshot prevents a fatal error from publishing earlier writes.

Only the storage update is inside the ignorable-error match. Assignment,
BEFORE-trigger, and AFTER-trigger failures are outside it. Existing CHECK and
view CHECK OPTION handling retains its separate validation rules.

## Batched writes

Updates without triggers, incoming foreign keys, or custom-function calls can
skip rejected rows inside one private storage fold and publish accepted rows
once. Assignment errors remain outside the ignorable-constraint match. A
counter assignment confirms that accepted and rejected candidates are each
evaluated once. Triggered and referential-action paths retain per-row execution.
[The performance comparison](../../benchmarks/results/4ebf5f82-update-ignore-batching.md)
records the measured improvement and its limits.

## Routine writes during UPDATE

Status: fixed by the [routine UPDATE snapshot correction](2026-10-09-routine-update-writes.md).
In `function-parent-write`, a stored function changes an unreferenced parent
key from 2 to 3 and returns 3 to the outer UPDATE IGNORE. Both engines retain
the new parent key and accept all three child updates. The baseline evidence
above retains the original failure, which predates batching. The expanded
routine audit covers statement rollback, JOIN predicates, and transaction
completion as well as this original case.

## Batching baseline verification

The root gate passes 3,173 tests without build warnings or errors. The full
native wire run passes 97 contracts and 14,454 steps with zero differences:
`torture/artifacts/runs/20261009T001705713-89878/contracts`.

The ten matching scripts cover ignored constraints, warning order, trigger
order, single evaluation, and retained rows. The original routine-write failure
remains in the baseline evidence. Its correction has a separate permanent
wire contract and is not enrolled into a known-gap allowlist. Broader multi-target joined-update
orderings, SQL-mode coercions, and trigger interactions remain unaudited.
