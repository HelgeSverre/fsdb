# DELETE IGNORE foreign-key and trigger behavior

Status: bare SIGNAL/RESIGNAL trigger parsing implemented; row-skipping semantics,
diagnostic detail, and warning-class trigger lifetimes remain incomplete.

## Evidence

`torture/scripts/delete-ignore-oracle.py` passes 14 scripts on MySQL 8.4.11.
Each script starts with parent rows 1, 2, and 3 and a child referencing parent 2.
The disposable native server uses a 64 MiB InnoDB buffer pool and redo capacity.
The client continues after errors so final diagnostics and table state remain
observable; the fixture separately checks error code and SQLSTATE.

At fsdb `1538b449`, seven scripts differ. The successful LIMIT-1 deletion and
ON DELETE CASCADE control match. Native output and fsdb comparison are preserved
in `2026-10-08-delete-ignore-baseline.json`. No failure is enrolled in the
known-gap allowlist.

## Native contract

- DELETE IGNORE skips a foreign-key-blocked parent with warning 1451, continues
  deleting eligible rows, and leaves the referencing child intact.
- LIMIT counts selected rows, including an ignored blocker. Descending LIMIT 2
  selects parents 3 and 2, deletes 3, warns for 2, and leaves parent 1 untouched.
- Named-target and USING joined deletes apply the same row-skipping behavior.
- An explicit transaction can roll back successful deletions after the warning.
- ON DELETE CASCADE still deletes the child.
- A trigger's explicit SIGNAL SQLSTATE 45000 remains error 1644; IGNORE does
  not turn it into a successful deletion.
- The 1451 warning includes the child database/table, constraint name, child
  column, and referenced parent table/column.

## Current differences and implementation constraints

fsdb's storage deletion applies cascade validation to the whole selected set.
A blocker therefore aborts the statement instead of allowing eligible rows to
be deleted. Its 1451 text identifies only the constraint name. Correcting this
requires preserving candidate order and LIMIT while reporting successful rows
separately from rejected rows. Trigger execution, cascades, catalog publication,
and rollback must remain consistent with the actual deletion result.

Bare SIGNAL and RESIGNAL bodies now use the same simple-statement parser as
compound bodies. RETURN and local-assignment parsing also share that parser,
removing duplicate expression handling. Fatal SIGNAL preserves error 1644 and
its supplied SQLSTATE. Inactive RESIGNAL preserves error 1645 with SQLSTATE
0K000. DELETE IGNORE does not swallow either error, and the rows remain intact.

The expanded trigger probes establish that BEFORE effects survive an ignored
foreign-key blocker, while AFTER fires only for successful deletions. Events
interleave per selected row, including descending LIMIT order. These are
constraints on the pending ignored-row deletion implementation.

The current 14-case replay has nine differences: foreign-key skipping and text,
the corresponding trigger effects, and warning-class SIGNAL diagnostics that
remain visible after the trigger returns in fsdb but are cleared in MySQL.
Exact current outputs are in `2026-10-08-delete-ignore-trigger-current.json`.

## Bare trigger validation

- `just check`: 3,115 tests passed, no build warnings or errors.
- The expanded maintained native oracle passes all 14 scripts.
- Full wire suite: 80 cases, 10,430 steps, zero differences. The bare-trigger
  contract checks exact SIGNAL/RESIGNAL codes, SQLSTATEs, diagnostics, and
  unchanged rows after failed DELETE IGNORE.
  Artifact: `torture/artifacts/runs/20261008T191048169-66375/contracts`.

Passing trigger controls do not imply the remaining ignored-row deletion or
warning-lifetime differences are fixed. No failure is allowlisted.
