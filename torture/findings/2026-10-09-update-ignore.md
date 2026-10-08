# UPDATE IGNORE constraint failures

Native MySQL 8.4.11 with a disposable 64 MiB buffer pool and redo capacity is
the oracle. The [native results](2026-10-09-update-ignore-native.json) and
[fsdb replay](2026-10-09-update-ignore-current.json) match across all seven
scripts.

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

## Verification

The mixed-row Expecto regression failed with error 1452 before the fix. The
root gate passes 3,169 tests without build warnings or errors. The full native
wire run passes 96 contracts and 14,339 steps with zero differences:
`torture/artifacts/runs/20261008T233824254-87173/contracts`.

The broader foreign-key replay now matches 34 of 35 cases. Generated names
for unnamed constraints remain different. This audit does not establish all
multi-target joined-update orderings, every SQL-mode coercion, or every possible
trigger interaction.
