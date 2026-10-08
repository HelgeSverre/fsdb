# Join-order hint diagnostic lifecycle

Status: partially implemented. Native MySQL 8.4.11 confirms that join-order target warnings belong
to optimization/execution, unlike table/index target warnings emitted during
preparation. The fsdb baseline at `75fa9901` differs in 28 of 36 cases.

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

`just check` passes 3,106 tests. The native fixture passes all 36 cases; fsdb
replay now differs in 13 query-elimination cases, down from the original 28.
The full wire lane passes 76 cases and 9,460 steps with zero differences:
`torture/artifacts/runs/20261008T131329085-40960/contracts`.

## Remaining query-elimination behavior

Attach pending execution diagnostics to the relevant query-block execution/optimization path after bound
parameters and query-elimination decisions are available. Preserve contextual
conflicts and missing query-block diagnostics in their existing preparation
phase. Bound SQL and binary parameter changes need matching regression coverage for
query elimination.
A guard on an empty source list alone does not satisfy this contract.

The baseline is evidence of an open gap, not an allowlist. Physical join-plan
controls remain a separate incomplete feature.
