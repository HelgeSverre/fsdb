# Integer COALESCE functional keys

The digest-pinned MySQL 8.4.11 oracle indexes `COALESCE(v,0)` for equality,
ordering, grouping, and uniqueness. On a signed `INT` source, NULL and zero
produce the same key, so a unique expression index rejects the second row with
1062/23000. A fallback need not fit the source column: `COALESCE(v,500)` on
`TINYINT` can index a NULL row. MySQL's hidden result type, however, controls
whether the fallback and source values fit the key.

The fresh type matrix changes the earlier conclusion about unsigned columns:

| Source and fallback | Hidden key | Width | Oracle behavior |
| --- | --- | ---: | --- |
| `TINYINT` through `INT`, signed or unsigned, with `0` | signed `INT` | 4 bytes | An `INT UNSIGNED` source above 2147483647 overflows the key. |
| `BIGINT` signed with `0` | signed `BIGINT` | 8 bytes | Exact signed range. |
| `BIGINT UNSIGNED` with `0` | `DECIMAL(20,0)` | 9 bytes | Preserves 18446744073709551615 and enforces uniqueness. |
| `TINYINT` with `2147483648` | signed `INT` | 4 bytes | A NULL fallback overflows the key. |
| `TINYINT` with `9223372036854775807` | signed `BIGINT` | 8 bytes | The NULL fallback fits. |

In strict mode, an overflowing hidden key rejects the row with 3752/22003.
In permissive mode, MySQL stores the original `INT UNSIGNED` source and warns
3752, but clips the indexed result to signed `INT` maximum. Thus the scalar
`SELECT COALESCE(v,0)` can show 2147483648 while an indexed equality probe for
2147483648 finds no row; probing 2147483647 can find it. This surprising
behavior is MySQL compatibility, not a data conversion of the stored column.

Fsdb now models the result type once for DDL support, physical key projection,
probe normalization, and key-length metadata. It maintains the integer keys,
including unsigned sources and wide fallbacks, and applies the strict or
permissive out-of-range diagnostic at index projection. The expression's
fallback remains part of its canonical identity. Mutation, uniqueness,
recovery, and scalar-function override paths retain their existing guards.

The focused Expecto regressions cover unsigned uniqueness, the `INT UNSIGNED`
strict overflow, exact `BIGINT UNSIGNED` maximum, literal-width boundaries,
and the permissive clipped-key probes. All 3,490 Fsdb tests pass. All 20
steps of `coalesce-functional-index` match MySQL in
`20261012T101608884-98574/contracts`, including error code and SQLSTATE,
warning text, stored value, and both clipped-key equality results. The full
differential run has 141 cases and 18,998 steps, with the same ten previously
documented differences as the preceding 18,985-step run. Other `COALESCE`
shapes, non-integer result types, and general expression indexes remain open.
