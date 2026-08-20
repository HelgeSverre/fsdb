# Client-contract differential campaign — 2026-08-20

Status: open

## Method

The campaign extends the existing MySQL 8.4.11 differential oracle with the
client-visible checks used by the ElyraSQL compatibility work:

- ordered query values, server errors, column names, and declared result types;
- integer-width and text-family folding, with type checks omitted for columns
  containing only SQL `NULL`;
- affected-row counts for generated `INSERT`, `INSERT ... SELECT`, `UPDATE`,
  and `DELETE` statements;
- a fixed 17-case DML battery under both MySqlConnector found-row
  (`UseAffectedRows=false`) and changed-row (`UseAffectedRows=true`) modes.

Unit-level negative controls prove that numeric-family differences, result-type
cardinality differences, and mutation-count differences produce distinct
classifications. Manifest schema 3 retains the new evidence; replay accepts
schemas 1 through 3.

Reference methodology:
[ElyraSQL pull request 98](https://github.com/kwhorne/ElyraSQL/pull/98) and
[MySqlConnector connection options](https://mysqlconnector.net/connection-options/).

Command, repeated three times:

```sh
./scripts/run.sh suite --seed 1 --max-rows 8 --invariant-every 1 --timeout-seconds 10
```

Evidence bundles:

- `artifacts/runs/20260820T112535844-56215`
- `artifacts/runs/20260820T112649265-56415`
- `artifacts/runs/20260820T112706083-56538`

The complete set of failed probe records, failed DML records, and four case
signatures is identical in all three runs.

## Open result-type gaps

All 23 affected probes returned the same ordered canonical values as MySQL.
Only the declared result metadata differed.

| MySQL type | FSDB type | Probes | Surfaces |
|---|---|---:|---|
| `ENUM` | `VARCHAR` | 8 | `membership_roles`, `enum_numeric_context`, `enum_group_by_sorts_by_ordinal`, `order_by_ordinal_and_alias`, `order_totals`, `payments_self_join_parent_child`, `distinct_multi_column_projection`, `order_by_ordinal_descending_group` |
| `BIGINT` | `VARCHAR` | 8 | `correlated_exists_negative_budget`, `group_by_having_task_counts`, `left_join_null_extended_assignee`, `limit_offset_tail_boundary`, `project_metadata_json_table_expansion`, `group_by_having_on_alias`, `limit_offset_last_page`, `intersect_set_operation` |
| `BOOL` | `BIGINT` | 4 | `boolean_is_tinyint`, `partitioned_rank_family`, `volume_edges`, `volume_groups` |
| `YEAR` | `BIGINT` | 2 | `date_part_extraction`, `timestamp_vs_datetime_columns` |
| `TIME` | `VARCHAR` | 1 | `datetime_component_and_truncation` |

The four stable case signatures are:

| Scenario | Signature |
|---|---|
| scalar | `0ae41c3b7baa9c68428427d6bb480dae275197a1c2f5e83258a1d547cf509e3c` |
| relational | `0c22b9bc28a28d4ee2b4fe45b65943b7c2d69ca064c674644dc98216916982d6` |
| commerce | `8cc7ecbf6fdee0fd70022648f914e6a68955e3b513ca66538088fd75c00ec6fc` |
| volume | `3e40a99c697e586455ac3ec71f1cedd0bae9507be2f783731e7a8bd6dd1692d5` |

## Resolved DML syntax gap

The original campaign found that FSDB rejected all three `REPLACE INTO`
cases with error 1064 while MySQL executed them:

- insertion of a new key;
- replacement with a changed value;
- replacement with the same value.

`REPLACE` values, select, and set forms run through a 23-case battery in
both affected-row modes. The 46 cases in
`artifacts/runs/20260820T115250504-61978/scalar-seed1-scale1-rows8-batch1`
have zero DML differences from MySQL 8.4.11. Coverage includes conflicts that
delete two rows through separate unique keys, same-statement key reuse,
unchanged replacements, default expressions, and source-row ordering.

## Affected-row results

No affected-row mismatch was found among supported statements:

- 54 generated mutation statements matched MySQL in each suite run;
- 28 of 34 focused DML cases matched MySQL in each suite run;
- changed versus unchanged `UPDATE` behavior matched in both client modes;
- `INSERT ... ON DUPLICATE KEY UPDATE`, including mixed and same-statement
  duplicates, matched in both client modes;
- multi-row insert, `INSERT IGNORE`, delete, and no-match cases matched.

No known-gap entry is required for `REPLACE`.
