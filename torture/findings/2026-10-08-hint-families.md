# SELECT optimizer hint families

Status: audited diagnostic cases fixed. Physical optimizer controls and broader
DML, stored-program, and view-expansion interactions remain open.

## Native behavior

The maintained fixture `torture/scripts/hint-family-conflicts-oracle.py` pins
107 cases against native MySQL 8.4.11, using a disposable server with a 64 MiB
buffer pool and redo capacity. Run it from the repository root with:

```sh
python3 torture/scripts/hint-family-conflicts-oracle.py
```

- Table-wide flags can reject later table-specific hints. For MRR, ICP, and
  RANGE_OPTIMIZATION, explicit indexes have individual conflict slots; repeated
  indexes within one hint are deduplicated without warnings.
- JOIN_FIXED_ORDER conflicts with preceding join-order hints. JOIN_PREFIX and
  JOIN_SUFFIX each reject repeats, while JOIN_ORDER can repeat. An unresolved
  join hint reports its first absent target, preserving explicit block spelling.
- SEMIJOIN, NO_SEMIJOIN, and SUBQUERY share a conflict slot. Diagnostic strategy
  lists use native ordering and omit repeated strategies.
- Positive INDEX_MERGE with exactly one index warns with 3614. Query-block
  lookup precedes this check, so an absent block produces 3127 instead.
- Query-block names become visible during contextualization. Nested blocks
  precede their parent; projection subqueries precede derived sources. A parent
  cannot reserve a name before its child is processed. Within one comment,
  references before a QB_NAME declaration cannot see that declaration.
- Timeout and structural context warnings follow that same order. Later target
  resolution visits source queries before projection subqueries, groups table
  and index families in native order, and reports join targets last. Generated
  block names use hexadecimal suffixes after `select#9`.

The implementation retains distinct typed join-order and query-block hints,
uses one AST traversal parameterized by the required order, and shares the
query-block lookup and conflict diagnostics. The root regression cases share a
single query-result verification helper with the target-resolution regression.

The native observations are corroborated by MySQL's
[hint-family enumeration](https://raw.githubusercontent.com/mysql/mysql-server/mysql-8.4.0/sql/opt_hints.h)
and [hint contextualization](https://raw.githubusercontent.com/mysql/mysql-server/mysql-8.4.0/sql/parse_tree_hints.cc).
These sources explain family ordering, name registration, and block lookup
before INDEX_MERGE argument validation; native 8.4.11 remains the test oracle.

## Validation

All 3,099 root tests pass. All 107 maintained native cases pass. The full wire
suite passes 71 contracts / 8,879 steps with zero differences, including existing
SQL and binary preparation checks:

`torture/artifacts/runs/20261008T113152314-1782/contracts`

No known-gap allowlist entries were added. Recognizing, resolving, and warning
about these hints does not establish that their physical strategy is applied.

## SET_VAR contextualization and execution

Status: audited `max_points_in_geometry` assignment ordering and preparation
warning lifetimes fixed. Other SET_VAR variables and broader stored-program
contexts remain outside this audit.

Native MySQL 8.4.11 processes nested assignments before the parent assignment.
For example, an outer `SET_VAR(max_points_in_geometry=9)` and a scalar-subquery
`SET_VAR(max_points_in_geometry=7)` expose 7 throughout the statement; the outer
assignment warns as a duplicate. UNION branches retain their branch order.
The first recognized assignment reserves the variable even if its value is
invalid. An invalid first assignment therefore does not allow a later valid
assignment to replace it.

Duplicate and unknown-variable warnings belong to contextualization, interleaved
with query-block and timeout diagnostics. Value validation follows that phase:
a clamped value warns with 1292, while a quoted nonnumeric value warns with 1232.
SQL and binary preparation emit both phases. Execution repeats value validation
but does not repeat duplicate or unknown-variable warnings. Statement overrides
remain visible in nested queries and do not change the session setting.

The implementation separates assignment selection from value validation and
shares the resolver's contextual ordering with diagnostic emission. This keeps
text execution and both preparation paths on the same rules.

Validation: the regression first reproduced fsdb returning 9 where native MySQL
returned 7. All 18 cases in `torture/scripts/setting-hint-context-oracle.py` pass
on the disposable native server. All 3,100 root tests pass; the expanded wire
suite passes 72 contracts / 8,928 steps with zero differences, including repeated
binary execution and session-setting restoration:

`torture/artifacts/runs/20261008T114635271-9072/contracts`
