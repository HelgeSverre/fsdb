# Routine-variable shadowing in index probes

MySQL 8.4.11 resolves a bare identifier to a stored-routine parameter before
a table column of the same name. A qualified table reference still names the
column. The digest-pinned oracle used a three-row table with
`KEY ix(tenant_id,secret_value)` and a procedure parameter `tenant_id`:

```sql
SELECT secret_value,COUNT(*) FROM secret
WHERE tenant_id=1 GROUP BY secret_value ORDER BY secret_value;
```

Calling the procedure with `tenant_id=1` returns all three groups; with `2`,
it returns none. Qualifying `secret.tenant_id=1` instead returns only the two
table rows with tenant 1 for either call.

Fsdb's indexed grouping path treated the bare `tenant_id` as an indexed table
column. It returned the tenant-1 slice even when the procedure parameter was
2, and omitted the tenant-2 row when the parameter was 1. The new regression
failed on that row omission before the fix. Index-column classification now
excludes bare references bound to routine variables; residual evaluation
applies the actual variable predicate.

A second MySQL procedure correlated over `(SELECT k,payload FROM
derived_secrets) d` with parameter `k`. The derived `d.k` is that parameter
for every physical row: for `k=1`, counts by outer IDs 1, 2, 3 are 3, 0, 0;
for `k=2`, they are 0, 3, 0. Fsdb instead returned 1, 1, 1 because its
pass-through projection shortcut reinterpreted `k` as the physical column.
The shortcut now declines a bare projection bound to a routine variable,
leaving ordinary derived evaluation to supply the projected values.
