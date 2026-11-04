# Correlated scaled ABS equality over signed integer keys

Correlated `ABS(d.k) * literal = o.x` and `literal * ABS(d.k) = o.x`
can now use two inverse signed keys when `d.k` is an indexed signed
TINYINT through INT column and the nonzero integer factor fits the bounded
literal profile. Exact decimal division rejects non-integral magnitudes.
Negative results, NULL bounds, magnitudes above the INT minimum absolute
value, and keys outside the declared column range produce empty candidate
sets. The original predicate remains the final membership check.

The [MySQL 8.4.11 oracle](../scripts/correlated-abs-oracle.py) checks both
operand orders and a negative factor across physical, pass-through, and
stable materialized sources. It covers NULL, non-divisible results, INT
minimum, a BIGINT lower-bound extreme, and a complete OR branch. The fsdb
regression checks those counts and candidate narrowing. Before the change,
the first physical query made 8,016 residual calls; after the change, every
audited query stays below 80.

Zero and non-integer factors, other inner column types, and broader nested
arithmetic forms retain their existing paths.
