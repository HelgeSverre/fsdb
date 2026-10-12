# Integer IFNULL functional keys

The digest-pinned MySQL 8.4.11 oracle accepts `IFNULL(column, integer_literal)`
as a functional index, including a unique key. A NULL source and a source
value equal to the fallback collide under that key. MySQL uses the key for
literal equality and maintains it through updates.

`IFNULL` must retain its own expression identity. Although `IFNULL(v,0)` and
`COALESCE(v,0)` yield the same scalar values for integer `v`, MySQL gives them
different hidden result types. For `TINYINT` through `BIGINT` with a signed
integer fallback, `IFNULL` has a signed `BIGINT` result and eight-byte key;
`COALESCE` can use a four-byte signed `INT` result. Thus a `TINYINT` source
with fallback `2147483648` fits an `IFNULL` key but overflows a `COALESCE`
key. An `INT UNSIGNED` source can index its full `4294967295` maximum under
`IFNULL(v,0)`. A `BIGINT UNSIGNED` source with signed fallback produces an exact
`DECIMAL(20,0)` result and nine-byte key, preserving
`18446744073709551615`. The result types were checked with MySQL
`CREATE TABLE AS SELECT`; key widths and access were checked with `EXPLAIN`.

Fsdb's physical-expression model now records the fallback function as well as
its value. The shared integer-key projection, probe, uniqueness, key-length,
and function-override paths therefore use the correct `IFNULL` type without
aliasing a `COALESCE` key. The focused regression covers uniqueness, the wide
fallback, the unsigned maximum, and key-length metadata. The differential
case checks row results, duplicate diagnostics, and key maintenance after an
update. All 3,491 Fsdb tests pass. All ten `ifnull-functional-index` steps
match MySQL in `20261012T102955296-99835/contracts`; the full campaign has
142 cases and 19,008 steps, with the same ten previously documented
differences as before. Non-integer and more complex `IFNULL` expression
indexes remain open.
