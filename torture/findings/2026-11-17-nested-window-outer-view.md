# Nested checked view with an outer window CTE join

Status: audited parity; no engine change required.

Oracle: MySQL 8.4.11, using the digest pinned in `torture/compose.yaml`.

The inner view joins two physical tables, exposes their `id`, `n`, and `aux`
columns, and has `WHERE n>=0 WITH CASCADED CHECK OPTION`. The outer view
LEFT JOINs a CTE containing `ROW_NUMBER() OVER(PARTITION BY id ORDER BY flag)`
through `USING(id)`, with an outer `WHERE n<10` predicate.

Both MySQL and FSDB:

- Reject the outer view's `WITH CASCADED CHECK OPTION` with error 1368.
- Accept its definition without CHECK OPTION and report `IS_UPDATABLE=NO`.
- Return duplicate matching window rows and retain the unmatched target row
  with a NULL window result.
- Reject UPDATE with error 1288 and preserve all physical target values.

The Expecto regression `outer window CTE joins make nested checked views read
only` captures the complete setup and these observations in `ViewTests.fs`.
This contrasts with the existing writable inner/NATURAL window join cases:
the outer join is a meaningful boundary for the audited shape. It does not
establish parity for every complex view or every outer join.
