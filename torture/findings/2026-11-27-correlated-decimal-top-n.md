# Correlated top-N with exact decimal keys

Correlated scalar and lateral top-N queries can now use a composite index when
the equality prefix compares DECIMAL values or DECIMAL and integer values.
The ordered lookup uses the exact bound. A fractional probe against a key
with fewer decimal places can therefore seek an empty slice. DECIMAL columns
can also supply the ordered suffix. The original predicate filters candidates
before OFFSET and LIMIT. [MySQL numeric comparison rules](https://dev.mysql.com/doc/refman/8.4/en/type-conversion.html)

The [SQL fixture](2026-11-27-correlated-decimal-top-n.sql) agrees with the
digest-pinned MySQL 8.4.11 in both Fsdb storage modes. It covers unequal
DECIMAL scales above 2⁵³, a fractional miss, NULL-safe and ordinary equality,
both traversal directions, a range, a residual predicate, and an offset.
For the inner scalar query, both engines report a dependent `ref` lookup on
`recent` with 12 packed key bytes and a reference to the outer `k` column.
MySQL also reports a filesort for this fixture.

Fsdb's slow log recorded 1,310 examined rows on the fixture's lateral query
at `ed8fcf9a` and 19 with the ordered seek. A sweep of 1,024 result variants
also matched MySQL in memory and with WAL. It combined mixed DECIMAL scales,
signed and unsigned BIGINT probes, nullable keys, integer and DECIMAL sort
keys, a fixed DECIMAL prefix part, range and literal cutoffs, reversed
comparisons, offsets, and `IGNORE INDEX` controls.
