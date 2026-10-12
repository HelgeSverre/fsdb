# IF NULL-branch functional keys

Native MySQL 8.4.11 accepts both `IF(value IS NULL, 7, value)` and
`IF(value IS NOT NULL, value, 7)` as functional index expressions. The two
forms have distinct expression identities: an index on the first does not
serve a query using the second, even though they return the same values.
With a nullable `INT` source, each has a five-byte nullable key, unlike the
four-byte `COALESCE(value, 7)` key and eight-byte `IFNULL(value, 7)` key.

In a unique-key fixture with `(1, NULL)` and `(2, 9)`, inserting `(3, 7)`
fails with duplicate-key error 1062. The `IF` equality query returns only
ID 1 and `EXPLAIN` names the key. Both branch orders behave this way.

The nullable hidden key is five bytes for `BOOLEAN`, `TINYINT`, `SMALLINT`,
`MEDIUMINT`, and `INT` sources, including audited unsigned variants. Signed
`BIGINT` uses nine bytes and retains `9223372036854775807`. An `INT UNSIGNED`
source value of `4294967295` overflows the four-byte signed result and fails
with error 3752 in strict mode. These width and overflow probes ran on the
disposable native MySQL 8.4.11 benchmark server; their databases were dropped.

Literal width also controls the hidden type. For a `TINYINT` or `INT` source,
fallback literals `2147483648` and `9999999999` leave a five-byte key and
fail with 3752 when a NULL row selects them. At `10000000000`, the result
promotes to a nine-byte nullable `BIGINT` key and the NULL row is stored.
The negative boundary mirrors this: `-9999999999` overflows the narrow key,
while `-10000000000` uses nine bytes. Both IF branch orders share these
result widths while retaining distinct expression identities.

Fsdb now maintains physical equality and unique keys for both branch forms
with fitting signed integer literals and the audited source types. The
Expecto regressions first failed with unsupported unique-expression error
1235 or an unindexed insert that should have overflowed; they now cover
result lookup, unique collisions, distinct identities, key widths, and
overflow. Other `IF` conditions, `BIGINT UNSIGNED`, unsigned fallback
literals beyond signed `BIGINT`, and other branch expressions remain outside
the supported physical-key grammar.

`just check` passed all 3,497 tests with no build warnings. All 23 steps of
`if-null-branch-functional-index` passed in the native MySQL 8.4.11 contract
run at `torture/artifacts/runs/20261012T160736591-54070/contracts`. The
145-case, 19,052-step run retained two unrelated result differences:
`regexp-posix-classes/han-script-negation` and the previously recorded
`mixed-type-join-in/costed-d-three-labels-unindexed`. The native server uses
`lower_case_table_names=2` on macOS. This is supplementary evidence; the
digest-pinned container lane has not been rerun for this change.
