# ALTER ADD FOREIGN KEY affected-row count

Status: open.

MySQL 8.4.11 reports one affected row when adding a foreign key with checks
enabled to a table containing one row. fsdb reports zero. Both operations
succeed and enforce the constraint afterward. The mismatch occurs with either
ON DELETE CASCADE or ON UPDATE CASCADE; exact SQL and protocol results are in
`2026-10-08-alter-foreign-key-count.json`.

Reproduce by creating `parent(id INT PRIMARY KEY)` and
`child(id INT PRIMARY KEY,pid INT)`, inserting parent `(2)` and child `(1,2)`,
then executing:

```sql
ALTER TABLE child ADD CONSTRAINT fk_parent
  FOREIGN KEY(pid) REFERENCES parent(id) ON DELETE CASCADE;
```

`Executor.validateAlterExecutionOptions` already requires COPY for adding a
foreign key with checks enabled, but the successful ALTER path always returns
zero affected rows. Algorithm selection and result counts need native coverage
for explicit/default algorithms and disabled foreign-key checks before changing
the general ALTER result policy.

The native comparison used a disposable server with 64 MiB buffer pool and redo.
Artifact: `torture/artifacts/runs/20261008T192947703-68753/contracts`.
The DELETE IGNORE contract creates each constraint with its intended action
in CREATE TABLE so its setup does not depend on ALTER's result-count behavior.
No mismatch is enrolled in the known-gap allowlist.
