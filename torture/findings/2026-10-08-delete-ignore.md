# DELETE IGNORE foreign-key and trigger behavior

Status: native contract pinned; row-skipping semantics and diagnostic detail
remain incomplete.

## Evidence

`torture/scripts/delete-ignore-oracle.py` passes nine scripts on MySQL 8.4.11.
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

The trigger control has a separate failure: CREATE TRIGGER with a bare SIGNAL
body is rejected before DELETE runs. Its apparent deletion difference is not
evidence that a successfully created trigger's error was swallowed. Trigger-body
parsing needs its own regression before exercising this IGNORE error boundary.
