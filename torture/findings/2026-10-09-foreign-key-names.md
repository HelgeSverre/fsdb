# Foreign-key constraint and index names

Status: resolved for the fourteen-script naming matrix. All scripts match,
including CREATE and ALTER numbering, suffix reuse, rename behavior, generated
collisions, and the backing-index metadata covered by these scripts. The
[expanded supporting-index audit](2026-10-09-foreign-key-indexes.md) and
[collision audit](2026-10-09-foreign-key-collisions.md) add independent coverage.
Counts describe scripts, not independent defects.

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

Unnamed ALTER declarations reach storage without a fabricated constraint name.
Storage derives their starting counter from the original table definition,
before applying any action, and generates only the unnamed constraints in
statement order. The WAL records the resolved actions. REFERENCES privileges
and algorithm selection recognize both named and unnamed declarations.

The [collision and error-precedence audit](2026-10-09-foreign-key-collisions.md)
confirms schema-wide case-insensitive names, pre-statement ALTER name
reservation, and the interaction with backing-index errors.

Backing indexes and schema-wide name checks follow the audited native rules.
Broader combinations with a rename in the same ALTER, rename collisions,
generated-index replacement across later statements, and extreme numeric
suffix limits remain unaudited. Do not infer whether a name was explicit by
recognizing an old generated string pattern.

## Validation

The root gate passes 3,181 tests without build warnings or errors. WAL and
snapshot recovery retain the resolved constraint and backing-index names.
The full native wire run passes 99 contracts and 14,972 steps with zero
differences at `torture/artifacts/runs/20261009T071530527-97190/contracts`.
All fourteen naming scripts, fourteen backing-index scripts, and eleven
collision scripts match their native evidence.
