# Updatable derived-first views with NATURAL JOIN

Native MySQL 8.4.11 reports `IS_UPDATABLE=YES` and updates the physical table
when a read-only derived source precedes it through `NATURAL JOIN`. The same
holds with two leading derived sources when either their join or the final
target join is natural.

The focused oracle uses `a(id,n)`, `b(id,x)`, and `c(x)`. It verifies a single
`(SELECT id,x FROM b) d NATURAL JOIN a`, a leading `d1 NATURAL JOIN d2`
followed by `JOIN a USING(id)`, and `d1 JOIN d2 USING(x)` followed by
`NATURAL JOIN a`. Each updates the matched `a` row.

An additional target `a_extra(id,x,n)` deliberately stores `x=999` while
the derived pair matches at `x=7`. MySQL still updates it when the original
query joins `d1 NATURAL JOIN d2` before `JOIN a_extra USING(id)`. Moving
`a_extra` to the front and retaining `NATURAL JOIN` would incorrectly make
its `x` participate in the earlier match. FSDB now resolves each natural
join's shared columns against the original logical left input, turns those
matches into qualified equalities, and changes the moved join to an ordinary
inner join. The existing single-derived swap keeps its symmetric natural
match.

The regression checks metadata, successful writes, unmatched rows, and the
extra target column that must not enter the original derived join. This does
not establish lateral, outer, or multi-component view writes.
