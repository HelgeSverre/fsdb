# Join-hint ownership across source merging

Status: audited derived/CTE warning ownership implemented. Physical optimizer
strategies and optimizer_switch configuration remain incomplete.

## Native contract

The maintained `torture/scripts/join-hint-merge-oracle.py` pins MySQL 8.4.11
output for 71 scripts. Each script has a fresh connection and an ordinary
`merge_base(n INT)` table containing one row. Checks use a disposable native
server with 64 MiB InnoDB buffer pool and redo capacity.

Merged derived and CTE bodies drop their join-order target warnings. Materialized
bodies retain them even under a statically false parent, unless their own query
eliminates planning. DISTINCT, grouping/HAVING, LIMIT, aggregates, windows,
UNION, and recursive CTEs prevent merging in the audited cases. ORDER BY and a
reducible scalar `(SELECT 1)` projection alone do not. Table-hint preparation
warnings survive merging.

Accepted MERGE/NO_MERGE hints follow existing conflict and query-block resolution.
A source-specific directive takes precedence over a later block-wide default;
an earlier block-wide directive makes a later source directive conflicting.
MERGE cannot override intrinsic materialization. Materialized-source warnings
precede their owner's warnings; scalar-subquery warnings follow their owner.

A false WHERE evaluated only after parameter binding skips materialized-child
planning. Static `WHERE 0`, `WHERE 0 AND ?`, and bound HAVING/LIMIT retain child
warnings. Repeated prepared executions reevaluate the condition. Original
parameterized WHERE expressions travel with execution diagnostics, preserving
this distinction without altering executable expressions or result metadata.

## Implementation and validation

The shared scope walker records source ownership and parent blocks. A shared
parent-assignment helper covers SELECT and mutation roots. Accepted source
strategies are typed values, and merge eligibility reuses scalar-subquery
materialization analysis. Planning distinguishes retained, locally eliminated,
and recursively skipped blocks. Empty diagnostic scopes retain their fast path.

- `just check`: 3,109 tests passed, no build warnings or errors.
- Maintained native oracle: all 71 scripts passed.
- fsdb replay: 70 of 71 merging scripts match; 61 of 62 lifecycle scripts match.
  Exact outputs are in `2026-10-08-join-hint-merging.json`.
- Full wire contracts: 77 cases, 9,597 steps, zero differences, including typed
  result metadata and binary prepared executions with false/true parents.
  Artifact: `torture/artifacts/runs/20261008T182828557-62482/contracts`.

The binary prepared case first failed with an extra child warning. The expanded
SQL regression reproduced the same mismatch before the parameter-origin fix.

## Remaining gaps

`SET optimizer_switch='derived_merge=off'` is unsupported (1193); the native
script remains in the oracle and replay, outside the passing regression/wire
cases. The [Boolean conversion fix](2026-10-08-predicate-conversion.md) closes the
lifecycle replay's missing `WHERE 'x'` warning. The remaining configuration
difference is not enrolled in a known-gap allowlist.

This implementation governs observable warning ownership. It does not implement
physical MERGE/NO_MERGE strategy controls or establish full optimizer equivalence.
Broader materialization interactions and predicate propagation need further
native evidence. No new performance claim is made by this change.
