# Correlated binary LIKE prefixes over physical rows

Native MySQL 8.4.11 returned the same correlated `LIKE` results from a physical
table and a pass-through derived source. With `utf8mb4_bin` columns, patterns
`a01%`, `a99%`, and `NULL` matched `(2, 2, 0)` rows in a five-row corpus.
Adding `OR c.id = 1` returned `(2, 3, 1)`. The fixture and expected results
were checked on a disposable native MySQL server.

The existing FSDB regression placed its `TOUCH(c.id)` counter after `LIKE`.
Short-circuit evaluation made a scan look selective: it counted only rows that
already matched the predicate. Moving `TOUCH` before `LIKE` in a 1,000-row
indexed corpus exposed 2,004 calls for two selective prefixes and a NULL
pattern. A matching-collation binary prefix now supplies an ordered secondary
index range for direct and pass-through physical sources; the original `LIKE`
still filters candidates. Complete OR branches union the physical row IDs,
and a stable NULL pattern contributes an empty branch rather than forcing a
scan. The shared prefix-bound rule also serves stable materialized sources.

The focused regression checks the same results with a leading residual and
bounds its calls below 60 for a conjunct and below 100 for complete OR
branches. Other collations, patterns without a fixed prefix, and uncovered OR
branches retain the general evaluation path.
