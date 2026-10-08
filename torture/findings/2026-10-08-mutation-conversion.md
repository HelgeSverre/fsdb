# Non-strict and IGNORE mutation conversion

Status: audited predicate warning counts and table outcomes match native MySQL.

## Contract and evidence

`torture/scripts/mutation-conversion-oracle.py` passes 24 scripts on MySQL
8.4.11. Every case starts with `predicate_base(n INT,s VARCHAR(20))` containing
`(1,'x'), (2,'1x'), (3,'0x')`. Native checks use a disposable same-host server
with 64 MiB InnoDB buffer pool and redo capacity.

Non-strict UPDATE/DELETE and UPDATE/DELETE IGNORE warn once for a constant
truncated predicate, including LIMIT 0 and LIMIT 1. Column predicates warn for
each evaluated row; short-circuited operands do not warn. The fixture checks the
resulting table as well as warning text and multiplicity.

At `057b1b55`, 18 scripts differed: metadata validation duplicated constant
predicate warnings, and DELETE IGNORE failed to parse. The current replay
matches all 24 scripts. Exact outputs are in `2026-10-08-mutation-conversion.json`.
No failure is enrolled in the known-gap allowlist.

## Implementation

Mutation metadata checks use the existing source-metadata probe scope, which
suppresses diagnostics and variable assignments from speculative evaluation.
Reference-validation errors still propagate through the ordinary result path.
Actual predicate preparation and row evaluation retain their conditions.

The DELETE AST retains an Ignore field. Parsing and statement diagnostic policy
use it for the audited conversions, consistently with UPDATE IGNORE. View/CTE
rewrites preserve the field through record updates.

## Validation

- `just check`: 3,113 tests passed, no build warnings or errors.
- Maintained native oracle and fsdb replay: all 24 scripts match.
- Full wire suite: 79 cases, 10,421 steps, zero differences. The mutation
  contract resets SQL mode and data for every script, then compares affected
  rows, warnings, metadata, and final table contents.
  Artifact: `torture/artifacts/runs/20261008T185732147-65583/contracts`.

## Remaining coverage

DELETE IGNORE is verified here for numeric predicate conversion. [Foreign-key and trigger probes](2026-10-08-delete-ignore.md) establish remaining
row-skipping, diagnostic-detail, and trigger-warning lifetime differences; bare
SIGNAL/RESIGNAL parsing is implemented. Other
ignored error classes require their own native evidence;
parsing the modifier does not establish full IGNORE behavior. Joined mutations,
additional SQL modes, and additional conversion families need broader coverage.
