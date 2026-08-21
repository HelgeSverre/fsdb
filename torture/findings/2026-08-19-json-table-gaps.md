# 2026-08-19 — JSON_TABLE subset: unsupported clauses and pinned error wording

Status: resolved

Phase 3 ships the scoped subset `JSON_TABLE(expr, 'path' COLUMNS (col TYPE
PATH 'path' | col FOR ORDINALITY)) alias`. Read-only oracle probes (MySQL
8.4.11, port 3462) pinned the divergences below; the supported subset is
exercised by the `json_table_lateral` / `json_table_inner_drop` scalar-scenario
probes (Harness.fs `ScenarioProbes`) and the Expecto `JSON_TABLE` list.

## Resolved clauses and joins

`ERROR ON EMPTY` and `ERROR ON ERROR` now raise MySQL's 3665 and 3156,
respectively.

- **NESTED PATH / EXISTS PATH / DEFAULT ... ON EMPTY|ERROR** are supported,
  including nested outer semantics and JSON-decoded defaults.
- **LEFT JOIN JSON_TABLE(...) ON expr** now keeps unmatched left rows and
  null-pads the JSON_TABLE columns, including empty and NULL documents.
- **JOIN JSON_TABLE(...) USING (col)** now applies the collation-aware
  equi-join and coalesces the named column in `SELECT *`.

## Pinned error wording (now matched)

```sql
SELECT jt.x FROM JSON_TABLE(t.j, '$[*]' COLUMNS (x INT PATH '$')) jt, t;
-- ERROR 1109 (42S02): Unknown table 't' in a table function argument
SELECT jt.x FROM JSON_TABLE(j, '$[*]' COLUMNS (x INT PATH '$')) jt;
-- ERROR 1054 (42S22): Unknown column 'j' in 'a table function argument'
```

A forward (or plain unknown) table reference in an uncorrelated JSON_TABLE
source is code **1109**, not a 1054 unknown-column — clients matching on the
code see MySQL's behavior. Both wordings are asserted by Expecto cases.

The correlated site with an unknown qualifier (`FROM t,
JSON_TABLE(nope.j, ...)`) now checks the current and outer qualifier scopes
before reporting MySQL's 1109.

Per TORTURE-TESTING.md §"If deferring", `support/known-gaps.json` gains a
signature only after a real torture run reproduces a divergence and it is
reviewed and minimized. No such signature exists yet — the ledger stays empty;
the remaining error-shape divergence has no enrolled signature.
