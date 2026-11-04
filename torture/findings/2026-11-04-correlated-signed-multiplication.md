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

Factors of zero, non-integer factors, unsigned or BIGINT inner columns, and
broader arithmetic forms retain their scan path.
