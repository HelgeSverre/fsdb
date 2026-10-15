# Composed read-only sources in updatable join views

Native MySQL 8.4.11 and fsdb agreed on eleven inner-join view arrangements with a
single physical write target. The read-only sources were aggregate derived
tables, `JSON_TABLE`, or a dependent `LATERAL` subquery, placed before, after,
and around the target. The matrix included grouped join operands and merged
`USING` and `NATURAL` keys. Each view reported `IS_UPDATABLE=YES`; updating a
matched target row affected one row and left unmatched rows unchanged. The
[native oracle script](../scripts/composed-join-view-oracle.py) reproduces the
matrix with a disposable server.

The added arrangement puts an aggregate derived source first, passes its
`id` to a dependent `JSON_TABLE`, then joins the physical target last. MySQL
and fsdb both report the view as updatable, update the one matched target row,
reject an invalid checked update with 1369, and reject joined-view INSERT and
DELETE with 1471 and 1395. The unmatched target row remains unchanged. This
tests source dependency and target identity across three different source
kinds in one view.

Two representative merged-key forms were:

```sql
CREATE VIEW merged_aggregate AS
  SELECT a.id,a.n,d1.flag,d2.aux
  FROM (SELECT id,SUM(flag) flag FROM merged_left GROUP BY id) d1
  NATURAL JOIN merged_target a
  NATURAL JOIN (SELECT id,SUM(aux) aux FROM merged_right GROUP BY id) d2
  WHERE a.n >= 0 WITH CASCADED CHECK OPTION;

CREATE VIEW merged_json AS
  SELECT a.id,a.n,j.id AS jid
  FROM JSON_TABLE('[1]','$[*]' COLUMNS(id INT PATH '$')) j
  JOIN merged_target a USING(id)
  WHERE a.n >= 0 WITH CASCADED CHECK OPTION;
```

For both, an update setting `n=-1` returned 1369 and left the base row
unchanged. Explicit-column `INSERT` returned 1471 and `DELETE` returned 1395.
The focused `ViewTests` regression pins the metadata, successful and rejected
updates, error codes, and final rows. No engine change was needed.

A further native 8.4.11 case groups an aggregate-derived source with its
dependent `JSON_TABLE` before joining the physical target:

```sql
FROM ((SELECT id,SUM(flag) flag FROM b GROUP BY id) d
      JOIN JSON_TABLE(JSON_ARRAY(d.id),'$[*]' COLUMNS(x INT PATH '$')) j
        ON j.x=d.id)
JOIN a ON a.id=j.x
```

The view remains updatable and changes only the matched target row. A checked
variant rejects an invalid update with 1369; joined-view INSERT and DELETE
still return 1471 and 1395. The focused FSDB regression pins those outcomes
and the unchanged target after rejected writes. Arbitrary grouped dependency
graphs and layered authorization through this grouped source remain open.

## Aggregate source followed by dependent lateral sources

Native MySQL 8.4.11 marks a join view as updatable when an aggregate-derived
source feeds one or two dependent `LATERAL` projections before the physical
write target. Both arrangements update the one matched base row once; an
unmatched target remains unchanged. The checked variant rejects an invalid
update with 1369 and leaves the base row intact. Joined-view INSERT and
DELETE return 1471 and 1395. The maintained
[oracle](../scripts/composed-join-view-oracle.py) now passes 15 composed
shapes, including these two.

FSDB previously marked the first arrangement `IS_UPDATABLE=NO` and returned
1288 for UPDATE. Its shared source-order classifier counted derived and
`JSON_TABLE` sources before a writable target but omitted `LATERAL` sources.
The classifier now includes a leading lateral source; write rewriting retains
the original dependency order after moving the physical target to the write
anchor. The focused regression covers both chain lengths, metadata, successful
and unmatched updates, CHECK OPTION rejection, INSERT/DELETE errors, and final
base rows. This establishes the audited inner-join chains, not arbitrary
lateral reordering or distinct definer layers.

This audit covers the stated inner-join shapes, not arbitrary join graphs,
distinct security identities, or authorization through layered views.
