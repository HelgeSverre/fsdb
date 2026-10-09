# Foreign-key supporting indexes

Status: fixed for the audited CREATE and ALTER declarations. All fourteen
[native scripts](2026-10-09-foreign-key-indexes-native.json) match the
[fsdb replay](2026-10-09-foreign-key-indexes-current.json), including SHOW INDEX
rows, constraint metadata, command errors, and SQLSTATE. The oracle is native
MySQL 8.4.11 with a disposable 64 MiB buffer pool and redo capacity.

## Native rules

- A constraint name takes precedence over an explicit FOREIGN KEY index label.
  Without a constraint name, an explicit index label names the backing index.
- With neither name supplied, the index uses the first child column. An occupied
  name produces a numeric suffix starting at `_2`.
- An existing B-tree index with the full child columns as its leading columns
  supports the reference. Visibility does not disqualify it.
- Equivalent generated indexes retain the last declaration's index name.
- Duplicate explicit index names on different columns produce 1061 / 42000.
  When a shared supporting index removes that collision, repeated constraint
  names instead produce 1826 / HY000.

## Implementation

`ForeignKeyIndexes` owns supporting-prefix recognition and index completion.
CREATE supplies parsed constraint/index labels; ALTER supplies its original and
explicitly altered indexes plus the new references. The same prefix predicate
is used when deciding which child-index changes require reference validation.

Generated ALTER indexes become concrete AddIndex actions in the persisted
schema event. Recovery therefore retains the original resolved names instead
of reinterpreting an unnamed declaration against a different table state.

Schema-wide foreign-key name checks remain active when FOREIGN_KEY_CHECKS is
zero. ALTER reserves original constraint names until the statement completes,
even when it also drops one. Index-name validation precedes constraint-name
validation, while missing-parent definition errors retain their native order.
The [collision audit](2026-10-09-foreign-key-collisions.md) covers those rules.

## Validation and limits

The root gate passes 3,181 tests without build warnings or errors. Regressions
cover generated names, supporting-index reuse, diagnostic precedence, and WAL
and snapshot recovery. Native wire validation passes 99 contracts and 14,972
steps with zero differences at
`torture/artifacts/runs/20261009T071530527-97190/contracts`.

The combined wire contract retains the index and collision matrices. The
original fourteen-case constraint-naming matrix also matches completely.

Rename collisions, removal/replacement of existing generated indexes across
later statements, complex combined ALTER actions, and extreme name/suffix limits
remain outside this audit. No claim of complete foreign-key DDL parity is made.
