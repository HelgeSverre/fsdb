# Trigonometric functional keys

Native MySQL 8.4.11 accepts separate `SIN(n)`, `COS(n)`, and `TAN(n)` functional
indexes on a `DOUBLE` column. Over `0`, `1`, `-1`, and `NULL`, equality on
`SIN(n)=0e0` and `TAN(n)=0e0` selects their respective keys with `ref` access;
`COS(n)>0e0` selects its key with `range` access. The non-null results are
double values, while all three functions return `NULL` for a null input.

The focused Expecto regression checks equality, a selective range, grouping,
uniqueness, update maintenance, and key choice. The persistence regression
checks lookup behavior after WAL replay and snapshot recovery. These keys use
the existing physical expression path, so further trigonometric functions and
general expressions remain outside this coverage.

MySQL also accepts separate `ASIN(n)`, `ACOS(n)`, and unary `ATAN(n)` keys.
`ASIN` and `ACOS` yield `NULL` for out-of-domain values such as `2`, which
coexists with a source `NULL` in an indexed key. The `0` and `1` equality
probes select the corresponding keys with `ref` access. fsdb's focused tests
cover these values, selective range and grouping plans, uniqueness, updates,
and WAL/snapshot recovery. Two-argument `ATAN` remains outside the unary-key
grammar.

The differential run also exposed a general unique-key bug: source `NULL`
values were ignored even when an expression such as `ISNULL(n)` produced a
non-null key, while out-of-domain `ASIN(n)` results were compared as raw source
values. Unique-key checks now use the projected result's nullness. MySQL 8.4.11
rejects a second `ISNULL(NULL)` key with error 1062; the focused regression and
`functional-unique-projected-nullness` contract cover that rule.
Duplicate-key diagnostics also render the projected key value (`1` for
`ISNULL(NULL)`) rather than the source `NULL`.

MySQL's grouped `ATAN(2)` projection differs from its ordinary scalar display
in the final decimal digit. The differential contract compares grouping counts
and the focused plan check verifies key selection; exact grouped floating
display remains outside this change.

The `trigonometric-functional-indexes` differential contract passed all nine
steps in pinned MySQL 8.4.11 run `20261010T145406462-91681`. The full run
retained nine identifier-case differences outside this contract.
The `inverse-trigonometric-functional-indexes` contract passed all nine steps
in pinned MySQL 8.4.11 run `20261010T150348157-93348`, with the same nine
unrelated identifier-case differences in the full run.
The follow-up `20261010T150621125-93504` run passed that contract and all
three `functional-unique-projected-nullness` steps, again retaining only the
nine existing identifier-case differences.
