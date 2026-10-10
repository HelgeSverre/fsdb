# Trigonometric functional keys

Pinned MySQL 8.4.11 accepts separate `SIN(n)`, `COS(n)`, and `TAN(n)` functional
indexes on `DOUBLE` columns. Equality uses the matching key with `ref` access,
and selective ranges use `range` access. These functions return double values
for finite inputs and `NULL` for source `NULL`.

`ASIN(n)`, `ACOS(n)`, and unary `ATAN(n)` have the same physical access paths.
`ASIN` and `ACOS` return `NULL` for out-of-domain inputs such as `2`; a unique
functional key allows multiple such rows and source `NULL`s. Unary `ATAN2(n)`
is an alias of `ATAN(n)` in MySQL's stored expression: an index declared as
`ATAN2(n)` serves an `ATAN(n)` predicate, and `SHOW CREATE TABLE` renders it
as `atan(n)`. Two-argument `ATAN` and `ATAN2` remain outside the unary-key
grammar. MySQL's grouped `ATAN(2)` projection differs from its ordinary scalar
display in the final decimal digit. The differential contract compares group
counts, while the focused test verifies key selection.

MySQL also accepts `COT`, `DEGREES`, and `RADIANS` functional keys. A `COT`
unique key allows multiple source `NULL`s, rejects a duplicate finite value,
and reports 1690/22003 when zero would produce an infinite key. MySQL returns
a finite `RADIANS(1e308)` value; fsdb now divides by 180 before multiplying by
π in both scalar and indexed execution. Focused tests cover equality, range,
grouping, uniqueness, updates, and WAL/snapshot recovery across these keys.

The inverse-trigonometric differential run also exposed a general unique-key
bug: fsdb used source nullness rather than projected-key nullness. MySQL rejects
a second `ISNULL(NULL)` key with 1062 and displays the projected value `1` in
the duplicate message. fsdb now uses the projected values for both checks and
diagnostics.

The `trigonometric-functional-indexes` contract passed all nine steps in run
`20261010T145406462-91681`; `inverse-trigonometric-functional-indexes` passed
all 11 steps in `20261010T152623452-98304`;
`functional-unique-projected-nullness` passed all three steps in
`20261010T150621125-93504`; and `angle-functional-indexes` passed all nine
steps in `20261010T151654993-95961`. Each full run retained the same nine
identifier-case differences outside these contracts.
