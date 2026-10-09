# Foreign-key ALTER definitions

Twenty cases match native MySQL 8.4.11. Each native case ran in a fresh disposable server with a 64 MiB buffer pool and redo capacity and passed a subsequent liveness query. The native/current JSON files retain SQL, result rows, and error-code/SQLSTATE comparisons.

Foreign-key drops resolve against the original definition: a missing name, repeated drop, or attempted drop of a constraint added in the same statement returns 1091 / 42000. Surviving child and parent constraints prevent dropping required columns with 1828 or 1829, including when foreign_key_checks=0. Explicitly dropping the constraint permits its child column to be removed in either statement order.

New constraints bind to the final column definition. ADD COLUMN and RENAME COLUMN can provide that definition after the ADD FOREIGN KEY clause in SQL text. Missing final columns return 1072 / 42000. Table renames give newly added constraints the same prefix transformation as existing constraints, including explicit table-prefixed names.

Column renames update child column references and incoming parent references across schemas. Regression tests verify that invalid child inserts and referenced parent updates remain rejected after these renames, WAL recovery, and snapshot recovery. Rejected ALTER statements preserve the original schema.

Root `just check` passes 3,193 tests with no build warnings. The `foreign-key-alter-definitions` wire contract covers the matrix. Type/nullability changes and dropping/re-adding a column under the same name remain outside this audit.

The full native wire gate passes 102 contracts / 15,533 steps with zero differences (`torture/artifacts/runs/20261009T082830681-19336/contracts`).
