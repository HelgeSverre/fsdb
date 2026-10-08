# Optimizer hints in data-changing statements

Status: audited UPDATE, DELETE, INSERT, and REPLACE hint ownership and
diagnostics implemented. Broader CTE scope combinations, stored programs,
view expansion, and physical optimizer controls remain open.

## Native behavior

`torture/scripts/dml-hints-oracle.py` pins 43 cases on native MySQL 8.4.11.
Each case starts with an indexed table containing one row. The disposable
server uses a 64 MiB buffer pool and redo capacity.

- UPDATE and DELETE own the first query block, including joined tables and
  exposed aliases. Missing table and index targets use the same diagnostic
  rules as SELECT. Scalar and derived subqueries retain separate block numbers.
- INSERT and REPLACE with literal values own a statement block containing the
  target table. Subqueries in values, SET assignments, and ON DUPLICATE KEY
  UPDATE assignments have their own blocks.
- INSERT SELECT and REPLACE SELECT share their first SELECT block with the
  target table. SELECT hints are contextualized before statement hints, even
  though the statement hints appear earlier in the SQL text. This determines
  duplicate warnings, accepted block names, and statement-level SET_VAR values.
- Mutation CTEs are numbered after body subqueries, following references through
  CTE dependencies. Unused CTEs do not contribute semantic hint diagnostics or
  setting overrides in the audited cases.
- Nested SET_VAR assignments can determine values stored by UPDATE or INSERT.
  SQL and binary preparation emit context and resolution warnings; execution
  does not repeat those warnings.

The resolver represents a statement block explicitly and stores each block's
sources. Mutation traversal feeds the existing hint-family conflict and target
resolution functions. It does not construct synthetic SELECT statements or
introduce separate mutation conflict rules. Omitted hint offsets also prevent
unused CTE assignments from reaching the setting interpreter.

Run the native fixture from the repository root:

```sh
python3 torture/scripts/dml-hints-oracle.py
```

## Validation

The focused regression first failed because fsdb omitted UPDATE's unresolved
BKA target warning. All 43 native cases and all 3,101 root tests pass. The full
wire suite passes 73 contracts / 9,164 steps with zero differences:

`torture/artifacts/runs/20261008T115808621-13601/contracts`

The mutation wire contract resets data per case and compares affected rows,
returned rows, and warning details. Additional binary preparation checks cover
UPDATE and INSERT SELECT, their execution warning lifetimes, and updated data.
No known-gap allowlist entries were added.

An exploratory EXPLAIN UPDATE probe confirmed the hint warning but also returns
native plan estimates and rewritten-SQL notes. Full EXPLAIN fidelity remains a
separate gap; the fixture does not claim that parity.
