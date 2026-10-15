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
expression keys remain open.
