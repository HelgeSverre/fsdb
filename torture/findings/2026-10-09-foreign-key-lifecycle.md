# Foreign-key index lifecycle

Native MySQL 8.4.11, disposable instance with a 64 MiB buffer pool and redo capacity, was probed in 14 isolated cases. Each case passed a subsequent liveness query. The native and fsdb JSON files retain SQL, output, and diagnostics.

Required child and parent indexes cannot be dropped, including with foreign_key_checks=0: error 1553 / HY000. Validation uses the final ALTER definition, allowing a replacement index or removal of the foreign key in either action order. Renaming an index preserves its protection. Failed changes leave the schema intact.

Eleven cases match and are retained in the foreign-key-index-protection wire contract. Three confirmed differences remain: add-redundant, drop-after-replacement, and child-alternate. MySQL removes a redundant automatically generated index after a replacement is added, but retains explicit indexes. fsdb currently retains both. Safely matching that behavior requires persisted generated-index provenance; index names alone cannot distinguish explicit and generated indexes.

Validation: root `just check` passed 3,183 tests with no build warnings. The full native wire suite passed 100 contracts / 15,076 steps with zero differences (`torture/artifacts/runs/20261009T073525814-99294/contracts`). The three documented lifecycle differences are not enrolled as accepted gaps.

A direct embedding-API check also verified column-flag primary keys: a redundant explicit parent index can be dropped, while dropping the required implicit primary key returns 1553.
