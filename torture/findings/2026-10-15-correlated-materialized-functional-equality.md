# Functional equality over stable materialized rows

Native MySQL 8.4.11 returns `(1,1)`, `(2,1)`, `(3,0)` for outer values
`10`, `999`, and `NULL` in this query against 1,000 inner rows with `k=id`:

```sql
SELECT o.id,
       (SELECT COUNT(*)
        FROM (SELECT DISTINCT id,k FROM inner_rows) d
        WHERE ABS(d.k)=o.x)
FROM outer_bounds o ORDER BY o.id;
```

FSDB returns the same rows. The initial profile found that the stable
materialized lookup covered exposed columns but not `ABS(d.k)`: with a
`TOUCH(d.id)=d.id` residual before the equality, `d.k=o.x` evaluated the
residual 6 times and `ABS(d.k)=o.x` evaluated it 4,004 times. The count
includes the engine's metadata probe. The source is materialized by
`DISTINCT`, so a physical functional index on `inner_rows` cannot directly
serve the exposed `d` rowset.

The current implementation inverts `ABS` over supported signed integer
columns to one or two statement-local equality probes, retaining the original
predicate as a residual check. Replaying the
[profile](../scripts/correlated-materialized-functional-profile.fsx) on the
preview.16 head produces the expected rows and **6 residual calls for both
`d.k=o.x` and `ABS(d.k)=o.x`**. The executor regression also covers negative
inner keys, NULL outer values, and OR branches. Other functional expressions
without a safe inverse or statement-local functional key remain a scale gap.
For example, `ABS(d.k)+0=o.x` returns the same audited rows but evaluates the
residual **4,004 times** on the same profile. The next lookup design must key
the evaluated expression with its comparison domain, exclude volatile or
outer-dependent expressions, and retain the predicate as a residual check.

## Statement-local integer expression lookup

FSDB now builds a statement-local key for exact integer-valued row-local
arithmetic (`+`, `-`, `*`, unary negation, and `ABS`) over exposed integer
columns and integer constants. It evaluates each expression once per
materialized row, keys signed and compatible unsigned integers together, and
keeps the full SQL predicate as the residual check. NULL rows contribute no
equality key. An evaluation error, a noninteger result, or a noninteger outer
bound falls back to the normal scan so numeric coercion cannot cause a false
negative. The lookup also participates in complete OR-branch unions.

The same [profile](../scripts/correlated-materialized-functional-profile.fsx)
now reports **6 residual calls** for `ABS(d.k)+0=o.x`, down from 4,004.
MySQL 8.4.11 agrees with the focused signed, NULL, reversed-equality,
arithmetic, OR, and DECIMAL-bound result corpus. This does not claim a general
functional key for text, decimal, floating, volatile, or outer-dependent
expressions.

## Statement-local text expression lookup

Stable, row-local text expressions over a materialized source now get a
statement-local equality key under the effective comparison collation. The
lookup keys evaluated string results once per materialized row and retains the
original SQL predicate as a residual check. NULL results are omitted; an
evaluation error or a non-string result falls back to the scan. The comparison
collation, including an explicit `COLLATE` on either operand, is part of the
memo key. Volatile functions, outer-dependent inner expressions, and nested
subqueries are excluded. Complete OR branches can union these candidates.

On 1,000 materialized rows, the
[text profile](../scripts/correlated-materialized-text-profile.fsx) shows
`LOWER(d.k)=o.x` reduced `TOUCH(d.id)` residual
calls from **4,004 to 6** while returning the same rows. MySQL 8.4.11 and
FSDB agree on binary and explicit case-insensitive collation, reversed
equality, `UPPER`, NULL bounds, and OR branches. Decimal and floating
expression keys remained open at this point.

## Statement-local decimal expression lookup

Exact decimal results now get a statement-local equality key when the outer
bound is decimal, signed integer, or unsigned integer. All keys use the
decimal numeric domain, including integer bounds; scale does not split equal
values. The original predicate remains the residual check. NULL results
contribute no key, and row evaluation errors or non-decimal results fall back
to a scan. DOUBLE, string, and other coerced bounds retain that scan because
their comparison domains can merge or reinterpret decimal values.

The [decimal profile](../scripts/correlated-materialized-decimal-profile.fsx)
measures `d.k+0.00=o.x` at **7 residual calls instead of 5,005** on 1,000
materialized rows. The [MySQL 8.4.11 oracle](../scripts/correlated-materialized-decimal-oracle.py)
and executor regression cover decimal and integer bounds, reversed equality,
arithmetic, NULL, a complete OR branch, and DOUBLE fallback. Floating-point
expression keys remained open at this point.

## Statement-local finite DOUBLE expression lookup

Finite DOUBLE results now get a statement-local key when the outer bound is
finite DOUBLE, DECIMAL, integer, or BIT. Exact numeric bounds are converted
to DOUBLE for the key, matching the runtime comparison domain. This covers
both exposed DOUBLE columns and stable,
row-local expressions such as `d.k+0e0`, while preserving the full predicate
as a residual check. NULL equality produces no candidates. A non-finite or
non-DOUBLE row result abandons the key for a scan; string and non-finite
bounds also retain the scan because their coercion is different.

The [DOUBLE profile](../scripts/correlated-materialized-double-profile.fsx)
reduces `d.k+0e0=o.x` from **5,005 to 7 residual calls** on 1,000 materialized
rows; direct `d.k=o.x` and the DECIMAL bound also use seven calls, while the
nonmatching BIGINT bounds use five. The
[MySQL 8.4.11 oracle](../scripts/correlated-materialized-double-oracle.py)
and executor regression cover direct and computed DOUBLE equality, reversed
operands, arithmetic, NULL, signed zero, a complete OR branch, DECIMAL and
BIGINT bounds, and string fallback. At 2⁵³, the rounded outer DOUBLE matches
the 2⁵³ row but excludes the distinct 2⁵³+2 row. MySQL also distinguishes
row-derived DOUBLE values from constant DOUBLE expressions when compared to
a BIGINT column: the former use approximate comparison, while the latter
keep the integer exact. FSDB's residual comparison now follows that audited
provenance rule. String and non-finite floating keys remain open.

## Statement-local finite DOUBLE expression ranges

The same stable materialized expressions now get an ordered statement-local
lookup for `<`, `<=`, `>`, `>=`, and `BETWEEN`. Finite DOUBLE row results are
sorted once, numeric outer bounds are converted to DOUBLE, and each lookup
starts at the matching interval cut. The original SQL predicate still checks
the selected rows. NULL bounds yield no candidates; string and non-finite
bounds, mixed result types, and evaluation errors retain the scan. Candidate
lists intersect under `AND` and union only for complete `OR` branches.

The [MySQL 8.4.11 oracle](../scripts/correlated-materialized-double-oracle.py)
and executor regression cover reversed and inclusive comparisons, DECIMAL and
BIGINT bounds, NULL, BETWEEN, string fallback, and OR. On 1,000 materialized
rows, a narrow `d.k+0e0 BETWEEN o.x-1e0 AND o.x+1e0` uses **11 residual
calls across four outer rows** in the
[profile](../scripts/correlated-materialized-double-profile.fsx). The oracle
also exposed a decimal-to-binary64 halfway conversion error at 2⁵³+1: the
CLR direct cast rounded to 2⁵³+2, while MySQL rounded to 2⁵³. Parsing the
exact decimal digits now gives the matching single rounding for numeric
comparisons and range bounds.

The same ordered lookup covers the complement of a wide correlated
`NOT BETWEEN` interval. Native MySQL 8.4.11 and the Fsdb regression agree on
numeric and clean-string outer bounds, including a NULL lower bound that still
permits the decisive `> upper` branch. For three outer rows with a narrow
complement, the regression evaluates fewer than 120 residual rows across a
1,004-row materialized source. This uses the existing two-range candidate
union; no new executor path was needed.

## Fully numeric string bounds

A correlated DOUBLE expression can also use its statement-local equality or
range key when the outer string is fully numeric and converts to a finite
DOUBLE. MySQL 8.4.11 returns the same rows without warnings for the audited
string equality, inequality, and `BETWEEN` cases, including a value above
2⁵³ that rounds to an existing DOUBLE key. The executor regression reduces
the narrow string equality and `BETWEEN` probes from more than 1,000 residual
calls to fewer than 80 across six outer rows. A string with trailing text
(`12.5x`) still takes the scan path: MySQL emits conversion warnings for it,
so narrowing the residual evaluation would change the warning count.

## Correlated DOUBLE expression IN candidates

The same finite DOUBLE expression key now unions candidates for a positive
`IN` list of stable outer values. Every bound must be a finite numeric value,
a fully numeric string, or NULL; otherwise the predicate keeps its scan.
Duplicate bounds share row identities, NULL contributes no WHERE-true rows,
and complete OR branches can union this candidate set with another branch.
The original predicate remains the residual check.

The [native MySQL 8.4.11 oracle](../scripts/correlated-materialized-double-oracle.py)
agrees on five audited `IN` forms over numeric, clean-string, duplicate,
and NULL bounds, including a rounded value above 2⁵³. The malformed `12.5x`
bound still scans and preserves MySQL's conversion warnings. In the focused
1,000-row regression, `d.k+0e0 IN (o.s,o.x)` initially made 7,035 residual
calls across six outer rows; the keyed path stays below 80. The maintained
[four-row profile](../scripts/correlated-materialized-double-profile.fsx)
measures seven residual calls for the same `IN` shape.
