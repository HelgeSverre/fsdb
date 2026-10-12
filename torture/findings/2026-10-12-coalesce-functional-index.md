# Literal COALESCE functional keys

The digest-pinned MySQL 8.4.11 image uses an index on
`COALESCE(v,0)` for equality. With `v INT`, rows `(1,NULL)`, `(2,0)`, and
`(3,5)`, `COALESCE(v,0)=0` returns IDs 1 and 2; `EXPLAIN` reports `ref`
access through `ix_fallback`. A unique key on the same expression rejects
`(2,0)` after `(1,NULL)` with 1062/23000. MySQL also uses
`COALESCE(v,500)` on a `TINYINT` column to find a NULL row: the fallback
is an expression value even though 500 is outside the column's range. On
the three-row `INT` table, grouping by `COALESCE(v,0)` combines NULL and
zero into a count of two; MySQL's plan streams the functional index. The
hidden result uses an `INT` key (four-byte `key_len`) for `TINYINT` and
`INT` sources, and a `BIGINT` key (eight bytes) for a `BIGINT` source.

Fsdb previously accepted the non-unique declaration but scanned the
predicate. It now maintains and probes physical keys for a single signed
integer column with an integer literal NULL fallback, including a
fallback outside the source column's range. The fallback is part of the
canonical index expression, so different fallback values cannot alias the
same key. Its projected result drives equality, uniqueness, and updates;
WAL recovery rebuilds the key. A scalar-function override prevents the
planner from claiming the stored built-in result. The focused regression
failed on the initial `ALL` plan before the change.

The `coalesce-functional-index` differential case compares rows,
affected counts, and the duplicate error with MySQL. The focused
Expecto tests additionally check `EXPLAIN` access and key length,
grouping, the out-of-range fallback, expression identity, override
safety, and WAL recovery. All seven differential steps pass in
`20261012T094917775-84075/contracts`. The full run has 141 cases and
18,985 steps, with the same ten documented differences as before. The
3,487-test Fsdb suite passes. Other `COALESCE` shapes, result types, and
general expression indexes remain open. MySQL accepts the same expression
on an unsigned `INT` source; fsdb does not yet maintain that mixed-signedness
key and refuses its `UNIQUE` form rather than silently omitting uniqueness.
