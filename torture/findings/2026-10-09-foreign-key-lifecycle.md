# Foreign-key index lifecycle

Native MySQL 8.4.11, disposable instance with a 64 MiB buffer pool and redo capacity, was probed in 14 isolated cases. Each case passed a subsequent liveness query. The native and fsdb JSON files retain SQL, output, and diagnostics.

Required child and parent indexes cannot be dropped, including with foreign_key_checks=0: error 1553 / HY000. Validation uses the final ALTER definition, allowing a replacement index or removal of the foreign key in either action order. Renaming an index preserves its protection. Failed changes leave the schema intact.

All original cases are now retained in the expanded foreign-key-index-lifecycle wire contract. The original three differences (add-redundant, drop-after-replacement, and child-alternate) are resolved by [persisted index provenance](2026-10-09-foreign-key-index-origin.md). The original JSON retains the pre-fix comparison; the expanded matrix records current parity.

Validation: root `just check` passed 3,183 tests with no build warnings. The full native wire suite passed 100 contracts / 15,076 steps with zero differences (`torture/artifacts/runs/20261009T073525814-99294/contracts`). The three documented lifecycle differences are not enrolled as accepted gaps.

A direct embedding-API check also verified column-flag primary keys: a redundant explicit parent index can be dropped, while dropping the required implicit primary key returns 1553.
