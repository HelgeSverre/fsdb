# Prepared parameter type repreparation

Status: open

Oracle: MySQL 8.4.11, using the digest pinned in `torture/compose.yaml`.
The `prepared-projection-names` probe in
`artifacts/runs/20261006T114025305-62971/contracts/manifest.json` contains the
original wire comparison. MySqlConnector prepares the statement and binds three
Int32 values of -2:

```sql
SELECT ?, ABS(?), ? + 1
```

MySQL returns three BIGINT columns with values -2, 2, and -1. fsdb returns
BIGINT, DOUBLE, and BIGINT; its middle value is a floating-point 2.
The independent statement `SELECT ABS(?)` returns DOUBLE on both engines.
The bare projection parameter changes MySQL's type-repreparation decision for
the entire statement, including the argument to ABS.

MySQL's [parameter typing rules](https://dev.mysql.com/doc/refman/8.4/en/prepare.html)
distinguish initial derivation, binding conversion, and statement-wide
repreparation. Supplied NULL and string values have separate rules, as do casts,
numeric families, and temporal families. Subsequent executions also depend on
the statement's retained types.

`PreparedMetadata.bindParameters` currently infers conversion rules afresh from
the original AST for each execution. A complete fix needs a statement-local
derived-type lifecycle shared by binary and SQL prepared execution. Changing
ABS alone to always retain numeric inputs would break the standalone case.

The stable-name regressions cover parameter labels independently of this type
difference. The mixed statement remains covered for its names in
`PreparedStatementTests`, but its type mismatch is unresolved.
