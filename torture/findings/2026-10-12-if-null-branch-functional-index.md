# IF NULL-branch functional keys

On native MySQL 8.4.11, `IF(value IS NULL, 7, value)` over a nullable signed
`INT` column is accepted as a functional index expression. Its hidden result
is nullable: an equality probe reports `key_len=5`, unlike the four-byte
`COALESCE(value, 7)` key and eight-byte `IFNULL(value, 7)` key on the same
column. MySQL keeps these three expressions as distinct key definitions.

The oracle fixture used `CREATE TABLE conditional_key (id INT PRIMARY KEY,
value INT, UNIQUE INDEX ux ((IF(value IS NULL, 7, value))))`. After inserting
`(1, NULL)` and `(2, 9)`, inserting `(3, 7)` produced duplicate-key error
1062. `SELECT id FROM conditional_key WHERE IF(value IS NULL, 7, value) = 7`
returned only ID 1, and `EXPLAIN` named `ux` with `key_len=5`. The fixture
ran against the disposable native MySQL 8.4.11 benchmark server and was
removed afterward.

Fsdb now recognizes this exact NULL-branch shape for signed `INT` sources
with a fitting integer literal. The focused Expecto regression first failed
with unsupported unique-expression error 1235, then passed with physical
equality and uniqueness. Other `IF` conditions, result types, and branches
remain outside the supported physical-key grammar.

`just check` passed all 3,494 tests with no build warnings. The six steps of
`if-null-branch-functional-index` passed against native MySQL 8.4.11 in the
full contract run at `torture/artifacts/runs/20261012T154134289-48423/contracts`.
That run had two unrelated result differences among 145 cases and 19,035
steps: `regexp-posix-classes/han-script-negation` and the previously recorded
`mixed-type-join-in/costed-d-three-labels-unindexed`. The native server uses
`lower_case_table_names=2` on macOS; this is supplementary evidence rather
than the pinned-container oracle lane.
