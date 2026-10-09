# Foreign-key name collisions and error precedence

Status: fixed for the eleven audited scripts. All now match native MySQL
8.4.11; the baseline at `43d23f00` differed in ten. Counts describe scripts,
not independent defects. The
[native evidence](2026-10-09-foreign-key-collisions-native.json) and
[fsdb replay](2026-10-09-foreign-key-collisions-current.json) retain complete
SQL, result rows, errors, and SQLSTATE.

## Established rules

- Constraint names are unique across tables in one schema, case-insensitively.
  The error retains the spelling of the newly requested name: 1826 / HY000,
  `Duplicate foreign key constraint name 'name'`.
- `FOREIGN_KEY_CHECKS=0` does not disable name uniqueness.
- A generated name colliding with an explicit name fails with 1826.
- Repeated explicit constraint names also fail with 1826 when an existing
  supporting index avoids a duplicate index declaration.
- ALTER rejects a name already present in the original table, including both
  DROP-then-ADD and ADD-then-DROP of that name within one statement.
- ALTER adding two constraints with the same name but different child columns
  reports 1061 / 42000, `Duplicate key name 'shared'`, before constraint-name
  validation. Backing-index construction affects diagnostic precedence.
- A missing referenced table reports 1824 before a name collision with another
  table. This is the one script that already matches fsdb.
- Rejected CREATE and ALTER leave the existing constraint metadata unchanged.

## Implementation implications

Name checking follows definition/index validation. ALTER name availability
consults the pre-statement catalog even when an earlier action drops the same
constraint. CREATE and ALTER share the backing-index rules in the
[supporting-index audit](2026-10-09-foreign-key-indexes.md).

## Reproduction and evidence quality

Run `python3 torture/scripts/foreign-key-collision-oracle.py` from the repository.
It uses a disposable native server with a 64 MiB buffer pool and redo capacity,
checks server liveness after each script, and writes
`/tmp/fsdb-fk-collision-native.json`. No running application database is used.

An earlier exploratory run disconnected after a rename-collision probe. Its
trailing outputs were discarded. Rename collisions are excluded from this
retained matrix and require an isolated audit with preserved server logs before
being used as an implementation oracle. The retained eleven-case rerun completed
with every liveness check passing.

The root gate passes 3,181 tests. Native wire validation passes 99 contracts
and 14,972 steps with zero differences, including a permanent index/collision
contract. No known-gap allowlist entries are added.
