# Correlated signed-integer multiplication equality

An indexed physical table, pass-through derived table or CTE, and stable
materialized derived table or CTE can now invert `d.k * literal = o.x` and
`literal * d.k = o.x` when `d.k` is a signed TINYINT through INT column and
the nonzero integer factor fits the bounded literal profile. The inverse
uses exact decimal division. NULL bounds, non-divisible results, and keys
outside the declared column range produce empty candidate sets. The original
predicate still decides membership after lookup.

The [MySQL 8.4.11 oracle](../scripts/correlated-affine-oracle.py) checks both
operand orders, a negative factor, NULL, non-divisible bounds, and signed INT
limits across five source shapes. The fsdb residual-count regression covers
the same source families and complete OR candidate unions. Before the change,
the first physical query evaluated 9,027 residual rows; after the change,
every audited query stays below 100 residual calls while returning the
MySQL-matched counts.

Factors of zero, approximate DOUBLE factors, unsigned or BIGINT inner columns, and
broader arithmetic forms retain their scan path.

## Exact decimal factors

Finite decimal literals between `0.0000001` and `2147483647` in magnitude
now use the same inverse lookup for signed TINYINT through INT keys. Decimal
outer bounds are handled exactly, including fractional values that cannot
equal any product. A decimal factor also works with `ABS(d.k)` by probing
both signed keys. Both operand orders and negative factors are covered.
The MySQL 8.4.11 oracle checks physical, pass-through, and materialized
sources; the fsdb regression checks the same counts with fewer than 100
residual calls per query against a source with more than 1,000 rows. Zero,
approximate DOUBLE factors, and wider factor magnitudes still scan.
The lower supported factor `0.0000001` is also checked with seven-place
DECIMAL bounds, including both signed INT extremes and the two-key ABS case.

## BIGINT overflow boundary

The excluded signed `BIGINT` case needs overflow-preserving evaluation before
it can use an inverse index lookup. On MySQL 8.4.11, a table containing
`BIGINT` keys `-9223372036854775808`, `-5`, `5`, and
`9223372036854775807` raises error 1690 for both `WHERE k*2=10` and a
correlated `COUNT(*)` with `WHERE i.k*2=o.x`. The overflowing row matters even
though a key of `5` satisfies the comparison. `SELECT
9223372036854775807*2` also raises 1690; `2147483647*2` returns
`4294967294`.

Fsdb previously promoted overflowing signed multiplication to DECIMAL and
returned a count for the scanned predicates. It now raises 1690 for the
audited literal, scan, and correlated cases. The `BIGINT` inverse probe remains
disabled because narrowing to matching keys could skip an overflowing row and
suppress the required error. Extending the probe requires a proof that all
rows in its evaluation scope are safe to multiply, or equivalent overflow
evaluation before candidate narrowing.
