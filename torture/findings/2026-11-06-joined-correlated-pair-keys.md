# Joined correlated integer pair keys

The joined correlated `COUNT(*)` precomputation previously accepted exactly
one outer-to-inner equality. A second equality sent every outer row through
the ordinary joined subquery. The small five-row control examined 41 rows.

The pinned MySQL 8.4.11 image from `torture/config/toolchain.json` returned
the following counts for outer pairs `(1,1)`, `(1,2)`, `(2,1)`, `(2,2)`, and
`(NULL,1)` against an inner table containing two `(1,1)` rows, one each for
`(1,2)` and `(2,1)`, and a NULL-key row:

| Outer pair | COUNT(*) |
| --- | ---: |
| `(1,1)` | 2 |
| `(1,2)` | 1 |
| `(2,1)` | 1 |
| `(2,2)` | 0 |
| `(NULL,1)` | 0 |

The subquery also joined a one-row marker table. The regression checks these
rows, `EXISTS` membership, and the examined-row budget. The new path groups
the stable inner source by all exact integer correlation keys once per
statement. NULL inner keys cannot satisfy equality; noninteger keys fall back
to the ordinary query path to preserve coercion. The 205-row outer control
remains under 250 examined rows, whereas a per-row scan would exceed that
budget. The full `just check` passed 3,916 tests after the implementation;
the focused test passed again after adding the larger control.

This is a bounded joined-subquery improvement, not general decorrelation for
expressions, unstable sources, or arbitrary join predicates.
