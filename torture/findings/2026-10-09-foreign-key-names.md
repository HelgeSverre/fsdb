# Foreign-key constraint and index names

Status: partially resolved. CREATE constraint naming matches the audited
unnamed, explicit-sequence, and mixed-case scripts. Eleven of fourteen scripts
still differ. The baseline at `83d96a0a` differed in all fourteen; these counts
describe scripts, not independent defects.

The [native evidence](2026-10-09-foreign-key-names-native.json) retains executable
statement sequences, metadata, errors, and SQLSTATE. The
[fsdb replay](2026-10-09-foreign-key-names-current.json) retains matching and differing
results. The oracle uses a disposable server with a 64 MiB buffer pool and redo
capacity.

## Native rules established by the matrix

- An unnamed constraint receives `<lowercase table>_ibfk_N`.
- `FOREIGN KEY key_label (...)` names the backing index. It does not name the
  constraint; the constraint still receives an automatically generated name.
- CREATE starts generated numbering at 1. An explicit `child_ibfk_7` in the
  same CREATE does not advance that counter. A collision with an explicit
  `child_ibfk_1` fails with 1826 rather than skipping the occupied name.
- ALTER starts above the highest existing canonical numbered name. An existing
  `child_ibfk_7` leads to `child_ibfk_8`; `child_ibfk_07` does not advance it.
  Explicit names introduced by the same ALTER do not advance the starting
  counter. DROP-and-ADD in the same ALTER uses the pre-drop numbering state.
- Dropping the highest name in a separate statement makes that suffix reusable.
  Dropping all constraints allows the next statement to start again at 1.
- Foreign-key constraint names collide across tables in the same schema.
  A generated collision fails with 1826.
- Duplicate explicit names in the audited CREATE fail with duplicate-index
  error 1061; backing-index validation affects which error is reported first.
- RENAME changes the generated table prefix, and subsequent ALTER continues
  from the renamed suffixes.
- Mixed-case table spelling remains visible in constraint metadata while its
  generated constraint prefix is lowercase on this native server.

## Implementation boundary

The parsed foreign-key clause retains separate optional constraint and index
names. CREATE resolves a table-based constraint name with an independent
counter. The generic reference record shares column and target fields between
parsed names and resolved string names; catalog and persistence records still
carry resolved names.

ALTER still uses the prior name resolution. Backing-index metadata and collision
validation remain open, including schema-wide generated-name collisions and
error precedence for duplicate explicit names. Do not infer whether a name was
explicit by recognizing the old generated string pattern.

## Validation

The CREATE naming regression failed before the fix. The root gate passes
3,170 tests without build warnings or errors. The full native wire run passes
97 contracts and 14,360 steps with zero differences at
`torture/artifacts/runs/20261008T235044805-88372/contracts`. The original
35-script foreign-key diagnostics/validation matrix now matches completely;
this broader naming matrix retains the remaining limitations.
