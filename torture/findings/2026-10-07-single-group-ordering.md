# Ordering a single aggregate group

Status: fixed.

Native MySQL 8.4.11, with `ONLY_FULL_GROUP_BY` enabled, accepts a plain row
column in ORDER BY when an aggregate query has no GROUP BY:

```sql
CREATE TABLE docs(id INT PRIMARY KEY, body TEXT, FULLTEXT ft(body));
INSERT INTO docs VALUES (1,'database concurrency'), (2,'storage transactions');
SELECT COUNT(*) AS n FROM docs ORDER BY body;
```

The result is 2. An empty input still returns the aggregate row with count 0.
The same shape with a natural full-text predicate is accepted. fsdb previously
rejected the row column with error 1055.

MySQL resolves the ORDER BY names but does not evaluate the unused sort
expression. After `SET @n=0`, this query returns 2 and leaves `@n` at 0:

```sql
SELECT COUNT(*) AS n FROM docs ORDER BY (@n:=@n+1);
```

An assignment in the projection does execute: `COUNT(*)+(@n:=@n+1)` returns 3
and leaves the variable at 1. Aggregate metadata probing must not perform an
extra assignment before execution.

## Validation boundaries

Name resolution and real grouping constraints remain active:

| Query suffix or projection | MySQL error |
|---|---|
| `ORDER BY missing` | 1054 / 42S22 |
| `GROUP BY body ORDER BY id` | 1055 / 42000 |
| `COUNT(*), ROW_NUMBER() OVER(ORDER BY body)` without GROUP BY | 1140 / 42000 |

The executor skips grouped-column checks for the final ORDER BY of a single
aggregate group, retains the metadata/name probe, and omits runtime sort-key
evaluation. Aggregate metadata probing suppresses variable assignments, as the
ordinary SELECT probe does. Error codes 1055 and 1140 use SQLSTATE 42000.

The focused Expecto regression first reproduced error 1055. Its side-effect
assertion then exposed a metadata-probe assignment. Native differential tests
also exposed HY000 for grouped-column errors; both text and prepared protocols
now match the oracle's error codes and SQLSTATEs.

## Verification

On 2026-10-07, `just check` passes 2,864 tests with no build warnings or errors.
All 47 compatibility contract cases and 5,007 differential steps pass. The
`single-group-ordering` case covers successful queries, empty input, full-text
filtering, the three rejection boundaries, and assignment state. Manifest:
`torture/artifacts/runs/20261007T024159214-51948/contracts/manifest.json`.
