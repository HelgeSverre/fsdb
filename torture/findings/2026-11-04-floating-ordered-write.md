# Pending FLOAT and DOUBLE inserts in limited ordered writes

The native MySQL 8.4.11 oracle repeated the keyless
[DECIMAL scheduling matrix](2026-11-04-decimal-ordered-write.md) with
`rank_value FLOAT` and `rank_value DOUBLE`, each with a secondary B-tree key.
For both types, `UPDATE` and `DELETE` waited for pending rank 0.5 or 4 when
`ORDER BY rank_value ASC|DESC LIMIT 2` could observe the insertion. After
the owner committed, the selected ranks were 0.5/1, 1/2, 4/3, or 3/2 for
the four direction and insertion combinations. That is sixteen wait and
reselection cases across the two types and verbs.

Another sixteen cases used `rank_value > 2` with pending 0.5 or
`rank_value < 2` with pending 4. MySQL completed without waiting in both
sort directions for both verbs and types, changing only the matching rank
3 or 1 respectively.

Before the fix, fsdb did not wait for the first pending FLOAT insertion.
The ordinary index encoder already stored FLOAT and DOUBLE keys with a
`D` tag, but the pending ordered-claim parser omitted those types. The
claim path now reads finite `D` keys as floating values, preserving their
binary order separately from exact DECIMAL keys. The keyed-range exclusion
uses integer boundaries only within the exactly representable binary64
integer range and finite DOUBLE boundaries; other mixed numeric bounds
retain a conservative claim rather than risk excluding a matching insert.

The maintained transaction regression now runs the wait/reselection and
excluded-range matrices for DECIMAL, FLOAT, and DOUBLE, checking both timing
and final rows. The audited corpus covers small finite FLOAT/DOUBLE values
on a keyless table. Precision-boundary comparisons, composite keys, and
other isolation histories remain to audit.
