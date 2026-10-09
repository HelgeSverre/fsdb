# DELETE IGNORE foreign-key and trigger behavior

Status: all maintained cases match, including foreign-key row skipping, detailed
1451 diagnostics, fatal trigger handling, and local warning lifetimes.

## Evidence

`torture/scripts/delete-ignore-oracle.py` passes 14 scripts on MySQL 8.4.11.
Each script starts with parent rows 1, 2, and 3 and a child referencing parent 2.
The disposable native server uses a 64 MiB InnoDB buffer pool and redo capacity.
The client continues after errors so diagnostics and final table state remain
observable; the fixture separately checks error code and SQLSTATE.

The current fsdb replay matches all 14 scripts. Exact outputs are in
`2026-10-08-delete-ignore-warning-lifetimes.json`. The row-skipping implementation's
13 matching scripts remain in `2026-10-08-delete-ignore-row-skipping.json`.
Historical baselines remain in
`2026-10-08-delete-ignore-baseline.json` and
`2026-10-08-delete-ignore-trigger-current.json`; the latter records nine
differences before row skipping was implemented.

## Native contract

- DELETE IGNORE skips a foreign-key-blocked parent with warning 1451, continues
  deleting eligible rows, and leaves the referencing child intact.
- LIMIT counts selected rows, including an ignored blocker. Descending LIMIT 2
  selects parents 3 and 2, deletes 3, warns for 2, and leaves parent 1 untouched.
- Named-target and USING joined deletes apply the same row-skipping behavior.
- Multi-target joined deletes keep a referenced parent blocked against the
  statement's original child rows even if another target deletes those children
  first. Both `p,c` and `c,p` target orders leave parent 2, delete selected
  children and unreferenced parents, and report warning 1451. The one-child
  case reports three affected rows; two children in the reverse target order
  likewise leave parent 2.
- An explicit transaction can roll back successful deletions after the warning.
- ON DELETE CASCADE still deletes the child.
- A trigger's explicit SIGNAL SQLSTATE 45000 remains error 1644; IGNORE does
  not turn it into a successful deletion.
- The 1451 warning includes the child database/table, constraint name, child
  column, and referenced parent table/column.

## Execution and rollback

Single-table and single-target joined ignored deletes share an ordered row
executor. Stable row identities are resolved against the statement snapshot;
a row removed by an earlier cascade is skipped. Each selected row runs BEFORE,
attempts deletion, and runs AFTER only when deletion succeeds. Ignoring a storage
foreign-key restriction retains its BEFORE effects and continues to the next row.
A multi-target ignored delete also checks the original catalog for restrictions
before attempting each deletion against the working snapshot. This preserves
MySQL's parent blocker when a targeted child was removed earlier in the same
statement; the working-snapshot check still catches new blockers.
A fatal error prevents publication of the statement snapshot. Explicit transaction
rollback restores both successful deletions and trigger writes.

The native late-failure probe deletes parent 1, skips parent 2, and reaches a
fatal AFTER trigger for parent 3. All parents survive and the audit table is
empty afterward. Diagnostics retain warning 1451 followed by error 1644 with
SQLSTATE 45000. A child-insert foreign-key error inside a BEFORE trigger remains
fatal rather than becoming an ignored outer deletion. Extra native outputs are
in `2026-10-08-delete-ignore-errors.json`.

Bare SIGNAL and RESIGNAL use the shared stored-program statement parser.
Fatal conditions retain their original SQLSTATE through single and joined
DELETE execution; inactive RESIGNAL retains error 1645 / SQLSTATE 0K000.
Foreign-key restrictions carry the child identity and definition for the 1451
message, including the referenced columns and declared actions.

## Remaining boundaries

Successful triggers keep warning-class SIGNAL conditions local, preserving the
outer statement's warnings. Local GET DIAGNOSTICS remains able to inspect the
warning. [Expanded trigger diagnostics probes](2026-10-08-trigger-warnings.md)
cover failing statements, handlers, RESIGNAL, and INSERT/UPDATE controls.
Other ignored-error classes and broader multi-target joined combinations need
further native coverage; these results do not establish complete IGNORE parity.

The wire fixture also exposed an independent
[ALTER ADD FOREIGN KEY affected-row count](2026-10-08-alter-foreign-key-count.md)
difference. Its deletion cases create the intended constraints directly.
No failure is enrolled in the known-gap allowlist.

## Validation

- `just check`: 3,124 tests passed, no build warnings or errors.
- The maintained native oracle passes all 14 scripts.
- Full wire suite: 82 cases, 11,095 steps, zero differences. The ignored-delete
  contract covers affected counts, exact warning text, LIMIT, both joined forms,
  cascades, trigger ordering, explicit rollback, and a late fatal AFTER trigger.
  Artifact: `torture/artifacts/runs/20261008T195250417-70218/contracts`.
- The multi-target follow-up passed all 204 `delete-ignore-foreign-keys` contract
  steps against the pinned MySQL 8.4.11 oracle. The full 111-case run had nine
  previously recorded identifier-case differences elsewhere; none was in this
  contract. Artifact: `torture/artifacts/runs/20261009T220505258-43853/contracts`.
