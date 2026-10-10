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

The `trigonometric-functional-indexes` differential contract passed all nine
steps in pinned MySQL 8.4.11 run `20261010T145406462-91681`. The full run
retained nine identifier-case differences outside this contract.
