# 2026-08-19 — JSON_TABLE subset: unsupported clauses and pinned error wording

Phase 3 ships the scoped subset `JSON_TABLE(expr, 'path' COLUMNS (col TYPE
PATH 'path' | col FOR ORDINALITY)) alias`. Read-only oracle probes (MySQL
8.4.11, port 3462) pinned the divergences below; the supported subset is
exercised by the `json_table_lateral` / `json_table_inner_drop` scalar-scenario
probes (Harness.fs `ScenarioProbes`) and the Expecto `JSON_TABLE` list.

## Deliberate gaps (refusals, not silent divergence)

- **NESTED PATH / EXISTS PATH / DEFAULT ... ON EMPTY|ERROR** don't parse —
  the subset is fixed NULL-on-empty/error (MySQL's probed default). The
  `Parser.fs` ponytail comment names the skipped clauses.
- **LEFT JOIN JSON_TABLE(...) ON TRUE** (the keep-the-left-row outer lateral
  form) → error 1064. MySQL null-pads the JSON_TABLE columns; this subset is
  inner-only and refuses rather than silently running inner semantics.
- **JOIN JSON_TABLE(...) USING (col)** → error 1064. MySQL runs the real
  equi-join (probed: `... JSON_TABLE(t.j, '$[*]' COLUMNS (x INT PATH '$')) jt
  USING (x)` filters to matching x and coalesces the name); the lateral
  branch has no coalesce/equi wiring, and before this note's commit it
  silently ignored USING and returned the cross product. Rejected until wired
  through the coalesce-names/equi-key machinery.

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

Remaining known divergence: the *correlated* site with an unknown qualifier
(`FROM t, JSON_TABLE(nope.j, ...)`) still surfaces as the evaluator's 1054
rather than MySQL's 1109 — the qualifier may legitimately resolve to an outer
subquery scope there, so a prepare-time check needs scope plumbing the subset
skips.

Per TORTURE-TESTING.md §"If deferring", `support/known-gaps.json` gains a
signature only after a real torture run reproduces a divergence and it is
reviewed and minimized. No such signature exists yet — the ledger stays empty;
the refusals above error identically on every run, so they classify as
`oracle_rejected`-vs-`fsdb` probe gaps only if someone adds a probe using the
unsupported syntax (deliberately not done: refusal is the contract).
