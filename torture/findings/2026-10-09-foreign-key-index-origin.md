# Generated foreign-key index lifecycle

The native MySQL 8.4.11 matrix covers 25 cases on a disposable server with a 64 MiB buffer pool and redo capacity. Every case passed a subsequent liveness query. The native/current JSON files contain SQL, output, and error-code/SQLSTATE comparisons; all cases match.

A covering explicit, primary, or longer generated index removes a redundant generated index. This remains true after dropping the foreign key or copying the table with CREATE TABLE LIKE. An explicit rename clears generated status, so later covering indexes retain the renamed index. Explicit indexes are retained. A covering explicit index may replace a generated index under the same name.

Index provenance is persisted in snapshot format 23 (FSNN) and V13 schema WAL records. Format 22 and older files remain readable. Older files contain no trustworthy provenance, so recovered indexes default to explicit retention; names are never used to infer origin. This prevents removing an explicit index during recovery or later ALTER operations. New-format files require a reader that supports the new format.

ALTER resolves generated removals before publication and journals the resolved actions. Same-name replacements remove the old definition before adding its replacement. Regression tests verify generated, explicit, renamed, copied, and same-name indexes across WAL and snapshot recovery, plus conservative decoding of captured format-22 snapshots and WAL records.

SHOW INDEX now derives ordinary-column nullability from the same column metadata used by INFORMATION_SCHEMA.STATISTICS. The primary-key replacement case verifies the nonnullable result.

Reproduce the native matrix with `python3 torture/scripts/foreign-key-index-origin-oracle.py`. The wire contract `foreign-key-index-lifecycle` covers every case. Root `just check` passes 3,188 tests with no build warnings.

The full native wire gate passes 100 contracts / 15,213 steps with zero differences (`torture/artifacts/runs/20261009T075929145-3021/contracts`). The duplicate-primary case also verifies error 1068 / 42000; dropping an already replaced generated index verifies 1091 / 42000.
