# SQRT functional keys

Pinned MySQL 8.4.11 accepts functional indexes on `SQRT` of `DECIMAL`,
`DOUBLE`, and `VARCHAR` columns. In the audited five-row table, zero, four,
and nine produce double keys zero, two, and three; negative and `NULL` inputs
produce `NULL`. A `2e0` equality probe uses the matching functional key for
each column type. A `2e0` lower range bound and grouping on `SQRT` can use the
ordered key. An exact integer `2` probe does not claim the double key in the
audited MySQL plan.

The `rounded-functional-indexes` differential contract in run
`20261010T111248690-60710` passed all 32 steps, including the ten new `SQRT`
steps for results, equality, range, grouping, uniqueness, and update
maintenance. The complete contracts run had nine pre-existing identifier-case
differences outside this contract. Expecto also covers key selection, an
overridden builtin, and WAL/snapshot recovery.
