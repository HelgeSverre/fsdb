# Correlated top-N through derived projections

A scalar or lateral top-N query can now stream the physical index through
an eligible derived projection. Binding uses the projected names and the
outer scope before translating the seek and sort keys to physical columns.
This preserves a bare outer reference even when the physical table has a
same-named column hidden by a projection alias. Derived filters and residual
predicates apply before OFFSET and LIMIT. An independently ordered or limited
derived table keeps its separate query block.

The [SQL fixture](2026-11-27-correlated-derived-top-n.sql) matches pinned
MySQL 8.4.11 on the result rows and the dependent inner `ref` access on
`recent`, including its key length and outer reference. MySQL merges the
projection and sorts its inner matches. Fsdb streams three index rows per
outer row in this fixture. Fsdb examined 40 rows after the change, down
from 1,610 at `b0cbc686`.

An additional comparison matched 576 result variants and 192 forced-index
plans in memory and with WAL. It combined single and nested projections,
a derived LIMIT control, scalar and lateral subqueries, both traversal
directions, integer, DECIMAL and collated text keys, NULL-safe equality,
qualified and bare outer keys, an optional range, and ignored-index controls.
The nested cases included a bounded computed projection alongside direct
seek and order columns.
