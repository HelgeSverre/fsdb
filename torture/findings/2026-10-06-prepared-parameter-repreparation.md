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

## Repeated-execution oracle

`just prepared-type-oracle` checks both binary prepared execution and SQL
`PREPARE`/`EXECUTE` against MySQL 8.4.11. Set `FSDB_ORACLE_CONNECTION` to the
connection string of an instance started from `torture/compose.yaml`. The probe
uses fresh, unpooled connections for independent histories: MySqlConnector can
reuse a prepared handle for the same SQL within one connection, even after its
command has been disposed. Reusing that connection contaminates a supposedly
fresh history with retained server types.

For `SELECT ?, ABS(?), ? + 1`, binding the same value to every marker produces
these result families in order:

| Supplied value | All three result types |
|---|---|
| signed integer -2 | BIGINT |
| string '-3' | DECIMAL |
| NULL | DECIMAL |
| decimal 1.25 | DECIMAL |
| signed integer -4 | DECIMAL |
| double 1.5 | DOUBLE |
| string 'oops' | DOUBLE; values 0, 0, 1 |
| signed integer -5 | DOUBLE |

The independent `SELECT ABS(?)` remains DOUBLE for integer, decimal, NULL, and
invalid-string executions. Binary execution and SQL EXECUTE agree on these
histories.

A whole-statement reprepare derives each non-NULL marker from its supplied
value, rather than assigning one common family to every marker. For
`SELECT ?, ABS(?)`, integer/integer produces BIGINT/BIGINT, followed by
decimal/integer producing DECIMAL/BIGINT. Later integer/integer execution
retains DECIMAL/BIGINT.

NULL requires two separate cases:

- With no reprepare, NULL keeps the retained type. A mixed statement that has
  widened to DECIMAL still reports DECIMAL when every supplied value is NULL.
- When another marker forces reprepare, a NULL marker can return to its original
  expression context. After BIGINT/BIGINT, NULL/decimal produces
  VARCHAR/DECIMAL. A subsequent integer/integer execution produces
  BIGINT/BIGINT. Conversely, starting with NULL/integer produces
  VARCHAR/DOUBLE, then integer/NULL produces BIGINT/DOUBLE.

String conversion precedes the compatibility decision. A valid negative
integer string supplied to a signed integer marker becomes DECIMAL in this
oracle version; an invalid string such as 'abc' retains the integer context and
converts to zero. A decimal string can widen only its marker while the other
marker stays BIGINT. The source-level behavior is in
[`Item_param::convert_value`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/item.cc)
and [`Prepared_statement::check_parameter_types`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_prepare.cc).
The manual's string exception alone is insufficient to implement these cases.

The fix therefore needs retained types owned by each prepared handle,
conversion before compatibility checking, whole-statement rederivation when
required, and typed NULL result metadata. Binary wire type reuse is separate
from this state. SQL and binary execution must share the same lifecycle without
sharing state between distinct handles.
