# Prepared parameter type repreparation

Status: explicit-marker histories resolved; direct user-variable typing open

Oracle: MySQL 8.4.11, using the digest pinned in `torture/compose.yaml`.
The `prepared-projection-names` probe in
`artifacts/runs/20261006T114025305-62971/contracts/manifest.json` contains the
original wire comparison. MySqlConnector prepares the statement and binds three
Int32 values of -2:

```sql
SELECT ?, ABS(?), ? + 1
```

The original comparison returns three BIGINT columns from MySQL with values
-2, 2, and -1. fsdb returned BIGINT, DOUBLE, and BIGINT; its middle value was a
floating-point 2.
The independent statement `SELECT ABS(?)` returns DOUBLE on both engines.
The bare projection parameter changes MySQL's type-repreparation decision for
the entire statement, including the argument to ABS.

MySQL's [parameter typing rules](https://dev.mysql.com/doc/refman/8.4/en/prepare.html)
distinguish initial derivation, binding conversion, and statement-wide
repreparation. Supplied NULL and string values have separate rules, as do casts,
numeric families, and temporal families. Subsequent executions also depend on
the statement's retained types.

`PreparedMetadata.bindParameters` retains SQL-derived types on each binary or
SQL prepared handle, separately from the binary protocol's remembered wire
types. It converts supplied strings before checking compatibility and rederives
the whole statement when required. NULL metadata, decimal scale, inherited cast
types, LIMIT validation, column-assignment diagnostics, and numeric/temporal
widening have dedicated regressions. Declared metadata wrappers are confined to
projections so bound predicates retain literal index probes.

The `prepared-parameter-type-history` compatibility case compares repeated
binary-handle and SQL EXECUTE histories, including a failed execution followed
by typed NULLs. `PreparedStatementTests` also checks independent handles and
registered signatures overriding polymorphic builtins.

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

## Direct user-variable references

This remaining case has no explicit parameter marker:

```sql
SET @v=-2;
PREPARE p FROM 'SELECT @v, ABS(@v)';
EXECUTE p;
SET @v=1.25;
EXECUTE p;
SET @v=1.5e0;
EXECUTE p;
SET @v='hello';
EXECUTE p;
SET @v=NULL;
EXECUTE p;
```

MySQL 8.4.11 returns BIGINT/BIGINT throughout, with rows (-2, 2), (1, 1),
(1, 1), (0, 0), and (NULL, NULL). fsdb follows each current variable value:
TINYINT/TINYINT, DECIMAL/DECIMAL, DOUBLE/DOUBLE, VARCHAR/DOUBLE, and
VARCHAR/DOUBLE. The decimal and double rows retain 1.25 and 1.5, respectively;
the string row retains 'hello' in its first column.

Direct variable references require a separate prepare-time variable-type
snapshot and conversion policy. They must remain distinguishable from the
explicit markers in `EXECUTE ... USING`, whose value changes follow the
repreparation rules above.
