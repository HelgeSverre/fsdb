# Pending DECIMAL inserts in limited ordered writes

A native MySQL 8.4.11 oracle used a keyless table with
`rank_value DECIMAL(10,2)` and `KEY ix_rank(rank_value)`. One transaction
inserted an uncommitted row while another ran `UPDATE` or `DELETE` with
`ORDER BY rank_value ASC|DESC LIMIT 2`. The base rows had ranks 1, 2, and 3.

| Pending rank | Direction | MySQL wait | Selected ranks after owner commit |
|---:|---|---|---|
| 0.50 | ASC | yes | 0.50, 1.00 |
| 4.00 | ASC | yes | 1.00, 2.00 |
| 4.00 | DESC | yes | 4.00, 3.00 |
| 0.50 | DESC | yes | 3.00, 2.00 |

Both verbs matched these four cases. Before the fix, fsdb's keyless `UPDATE`
at the first pending rank completed without waiting. Its pending-insert
claim parser recognized integer, temporal, and text keys, but excluded the
`M`-tagged DECIMAL keys that the ordinary index
encoder already produces.

The fix includes DECIMAL in the ordered insert groups, parses its invariant
decimal key, and compares stored decimal values with pending keys at the
visible LIMIT frontier. A second eight-case MySQL matrix established the
non-wait boundary: when `rank_value > 2` excludes a pending 0.50 or
`rank_value < 2` excludes a pending 4.00, both UPDATE and DELETE complete
without waiting in either sort direction. Focused fsdb regressions cover
the same wait/reselection and excluded-range cases, including final rows.

This verifies a DECIMAL(10,2) secondary index on a keyless table. Other
numeric types, composite decimal keys, and broader isolation-level histories
remain to audit.
