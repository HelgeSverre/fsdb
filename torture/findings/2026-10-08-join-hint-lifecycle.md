# Join-order hint diagnostic lifecycle

Status: audited query-elimination and [source warning ownership](2026-10-08-join-hint-merging.md) cases implemented. Native MySQL 8.4.11 confirms that join-order target warnings belong
to optimization/execution, unlike table/index target warnings emitted during
preparation. The historical fsdb baseline at `75fa9901` differs in 28 of 36 cases;
all of those original cases now match.

## Evidence

`torture/scripts/join-hint-lifecycle-oracle.py` maintains exact native output.
`2026-10-08-join-hint-lifecycle-baseline.json` records the corresponding native and
fsdb outputs. Each case uses a fresh connection with a temporary `t(n INT)`
containing one row. Native checks use a disposable same-host server with 64 MiB
InnoDB buffer pool and redo capacity. The maintained native fixture passes.

## Observed contract

- Missing query-block names and duplicate/conflicting hints still warn during
  preparation, including queries without table sources.
- Missing JOIN_ORDER, JOIN_PREFIX, and JOIN_SUFFIX targets do not warn for a
  SELECT without table sources, including FROM DUAL, scalar subquery parents,
  UNION branches, and repeated CTE bodies.
- A derived-table source still causes missing join-order targets to warn.
- Table and index hints retain their unresolved-target diagnostics even when
  join-order target diagnostics are suppressed.
- SQL PREPARE emits missing BKA targets but does not emit missing JOIN_ORDER
  targets. Each EXECUTE emits the latter when optimization reaches join order.
- Constant-false/NULL WHERE, constant-false HAVING, LIMIT 0, and the audited
  contradictory equalities suppress join-order target warnings.
- Parameterized WHERE and LIMIT change suppression between executions of the
  same prepared statement as their bound values change.
- A data-dependent filter returning zero rows, or an actually empty table,
  does not suppress the warnings. Result emptiness is not the deciding rule.
- ANSI_QUOTES controls identifiers in the warnings that remain visible.

## Implemented warning phases

Join-order target diagnostics are separate from preparation-time table/index
resolution. Missing join targets warn on execution, including repeated SQL and
binary executions, and source-free blocks (including DUAL) suppress them.
Contextual conflicts and missing query-block names retain their preparation
phase. Empty tables and data-dependent filters still produce target warnings.

## Bound query planning

Pending warnings retain their query-block numbers until the executor receives
the bound statement. The shared scope walker supplies each block's SELECT and
parent. Existing safe constant-condition analysis handles false/NULL predicates;
integer-column equality conflicts and LIMIT 0 suppress the audited warnings.
Eliminated parents suppress ordinary child-block warnings; materialized-source
ownership follows the separately audited merging rules. An unchosen IF expression
still plans its subquery. SQL_CALC_FOUND_ROWS keeps planning active for LIMIT 0,
and a UNION's global LIMIT 0 does not suppress its branches' warnings.
Floating-point and DECIMAL equality conflicts retain native warning behavior.

SQL and binary prepared statements reevaluate these decisions for bound values,
including repeated transitions between zero, one, and NULL. Planning does not
execute user-defined functions or assignments to infer constants. Scalar
subqueries projecting a column retain its declared result metadata when the
outer query returns no rows, as required by the full wire comparison.

At `008a5628`, `just check` passed 3,107 tests. The native fixture passed all
62 cases; fsdb matched 59, including every case in the original 36-case baseline. The remaining
outputs are preserved in `2026-10-08-join-hint-elimination.json`.
The full wire lane passes 76 cases and 9,486 steps with zero differences:
`torture/artifacts/runs/20261008T134702569-48507/contracts`.
The [performance comparison](../../benchmarks/results/06dbbefb-join-planning.md)
records common-path and join-hint allocation costs.

## Remaining gaps

The native fixture retains these cases without enrolling them in a known-gap
allowlist:

The repeated mergeable CTE and NO_MERGE child under a statically false parent
now match; see [source warning ownership](2026-10-08-join-hint-merging.md).
The [Boolean conversion fix](2026-10-08-predicate-conversion.md) supplies the
previously missing warning 1292 for `WHERE 'x'`. The current replay matches all
62 lifecycle cases; exact outputs are retained with the conversion evidence.

Physical join-plan controls and more general predicate propagation remain
incomplete. The passing cases establish the audited behavior above, not full
optimizer equivalence.
