# Foreign-key names during table renames

Ten cases match native MySQL 8.4.11. Every case ran in a fresh disposable server with a 64 MiB buffer pool and redo capacity, followed by a successful liveness query. Individual server logs are retained in `2026-10-09-foreign-key-rename-logs/`; the native/current JSON files preserve SQL, result rows, and error-code/SQLSTATE comparisons. No disconnect occurred in these isolated probes.

RENAME TABLE and ALTER TABLE RENAME reject schema-wide constraint-name collisions with error 1826 / HY000. This includes generated names within a schema and explicit or generated names crossing schemas. A preceding rename may clear the collision by moving its owner out of the destination schema. If a later pair fails, the entire rename batch leaves original table and constraint names intact.

Both rename paths update matching table-prefixed constraint names, including an explicitly supplied suffix with leading zeroes. Other explicit constraint names remain unchanged. The shared name transformation and destination-name validator operate before publication.

Expecto regressions cover rejected ALTER and RENAME statements, multi-pair rollback, WAL recovery, and snapshot recovery. The `foreign-key-rename-collisions` wire contract covers the full matrix. Root `just check` passes 3,190 tests with no build warnings. Broader combined ALTER actions remain outside this matrix.

The first shared wire run crashed native MySQL during the next case's DROP TABLE reset. `shared-wire-drop-crash.log` retains the SIGSEGV and foreign-key cache-removal stack. That run is infrastructure failure, not parity evidence. Three fresh-server teardown probes remained healthy: removing constraints first, dropping the collision owner first, and dropping the database. The wire contract records results before explicitly removing the remaining constraints, then drops tables during reset. The standalone probe retains fresh-server isolation.

With constraint-first teardown, the full native wire gate passes 101 contracts / 15,325 steps with zero differences (`torture/artifacts/runs/20261009T081156908-6024/contracts`).
