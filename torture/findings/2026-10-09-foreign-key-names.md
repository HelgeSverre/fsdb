# Foreign-key constraint and index names

Status: open. Native MySQL 8.4.11 and fsdb at `83d96a0a` differ in all 14
focused scripts. This count describes scripts, not independent defects.

The [native evidence](2026-10-09-foreign-key-names-native.json) retains executable
statement sequences, metadata, errors, and SQLSTATE. The
[fsdb baseline](2026-10-09-foreign-key-names-current.json) retains the differing
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

The parser currently collapses the optional constraint name and optional index
name into one string, or invents a parent/column-based name. That loses the
information required by these rules. Preserve both optional names through
parsing, then resolve constraint names with the owning table and existing
schema available. Do not infer whether a name was explicit by recognizing the
old generated string pattern.

Constraint diagnostics, SHOW CREATE, information_schema, index metadata,
ALTER, rename, and persisted catalog recovery must agree on the resolved name.
The matrix establishes a baseline; no runtime fix is claimed here.
