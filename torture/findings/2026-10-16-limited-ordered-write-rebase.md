# Limited ordered writes after a contended row changes

MySQL 8.4.11 and Fsdb agree on an audited concurrent write shape. Two rows
start with `(id,n)=(1,0),(2,0)`. The first transaction changes row 1 to
`n=-1` without committing. A second transaction runs either:

```sql
UPDATE target SET n=n+1 WHERE n>=0 ORDER BY id LIMIT 1;
DELETE FROM target WHERE n>=0 ORDER BY id LIMIT 1;
```

The second writer waits on row 1. After the owner commits, row 1 no longer
matches, so the limited write selects row 2. The UPDATE leaves
`(1,-1),(2,1)`; the DELETE leaves `(1,-1)`. Both report one affected row.
This holds with only the primary key and with a secondary index on `n`.

The same re-selection occurs when row 1 still matches but moves behind row 2
in the sort order. Starting from `(1,0),(2,1)`, the owner changes row 1 to
`n=2`. A waiting `UPDATE ... SET n=n+10 WHERE n>=0 ORDER BY n,id LIMIT 1`
then changes row 2 to `n=11`; the analogous DELETE removes row 2.

The [disposable MySQL oracle](../scripts/limited-ordered-write-rebase-oracle.py)
asserts the wait, affected count, and final rows for all combinations of
write verb, predicate-versus-sort-key change, and index presence at `LIMIT 1`,
`LIMIT 2`, and `LIMIT 3`. At `LIMIT 3`, a fourth row fills the vacated result
slot after row 1 fails the predicate or moves behind the other rows in sort
order. A further twelve cases delete the first candidate in the owner
transaction; the waiting UPDATE or DELETE fills the vacated result slot at
all three limits, with and without a secondary predicate index. MySQL 8.4.11
and the matching `TransactionTests` cases agree on all 36 combinations.
No engine change was required for these row-change audits.

## Pending inserted candidates

The same oracle pins a different outcome when the owner inserts an earlier
primary-key candidate. Starting with `(1,0),(2,0)`, the owner inserts `(0,0)`
without committing. A second transaction runs
`UPDATE target SET n=n+1 WHERE n>=0 ORDER BY id LIMIT 1`. MySQL waits for
the insert, then changes row 0, leaving `(0,1),(1,0),(2,0)`. A focused fsdb
reproduction completed before the owner committed and changed row 1 instead,
leaving `(0,0),(1,1),(2,0)` after both commits. The ordered-write scheduler
now inspects active integer PRIMARY-key claims before selecting its rows. It
waits and rebases when a pending key crosses the `LIMIT` frontier, including
when fewer rows currently match than the limit. An insert after the frontier
does not delay the write. MySQL 8.4.11 and `TransactionTests` agree across 36
UPDATE/DELETE cases covering `LIMIT 1` and `LIMIT 2`, ASC and DESC, INT and
BIGINT UNSIGNED, and both wait and no-wait outcomes. The integer audit is
scoped to the audited primary-key orderings; other order expressions and key
types need separate audits.

## DATE primary keys

The same pending-insert gap affected valid single-column `DATE` primary keys.
Starting with January 2 and 3, 2026, an uncommitted January 1 insert makes
`UPDATE` or `DELETE ... ORDER BY id ASC LIMIT 1` wait; an uncommitted January 4
insert makes the descending form wait. Inserting beyond the opposite frontier
does not make either write wait. MySQL 8.4.11 confirms the wait behavior,
affected count, and final rows for both verbs in all eight combinations.

The scheduler now reads the stored day number from an active `DATE` PRIMARY
claim and compares it with visible candidate dates at the `LIMIT` frontier.
The focused transaction regression covers the eight cases. Zero dates and
other order expressions remain outside this DATE audit.

## Composite primary keys

An uncommitted insert into `PRIMARY KEY(a,b)` can cross the result frontier of
`UPDATE` or `DELETE ... WHERE n>=0 ORDER BY a,b LIMIT 1/2`. MySQL 8.4.11 waits
for that insert and reselects the first row after it commits. A pending tuple
beyond the frontier does not delay uniform ASC/ASC or DESC/DESC ordering. The
original fsdb scheduler read only single-column claims, so a crossing tuple
completed early and changed the wrong row.

The scheduler now decodes each length-prefixed PRIMARY component and compares
the complete tuple with the visible frontier. The native oracle and focused
transaction regression cover both verbs, both uniform directions, crossing
and non-crossing inserts, affected counts, and final rows. They also cover
mixed ASC/DESC and DESC/ASC directions. In those mixed-order cases MySQL waits
for a pending inserted key even when it sorts beyond the first visible row;
fsdb now claims all active primary keys for that shape before rebasing. The
24-case composite audit uses two `INT` key columns. Zero-date, wider
tuples, non-primary ordering expressions, and different predicates remain
outside it.

## Text primary keys

Pending `VARCHAR` PRIMARY inserts can also cross an ordered write's `LIMIT 1/2`
frontier. With visible keys `b` and `d`, an uncommitted `a` insert makes an
ascending UPDATE or DELETE wait and then select `a`; an uncommitted `z`
insert does the same in descending order. Inserts beyond the frontier do not
delay the write. MySQL 8.4.11 confirms the wait, affected count, and final
rows for both verbs under `utf8mb4_bin` and `utf8mb4_0900_ai_ci`.
With ascending `LIMIT 2`, a pending `c` enters the first two rows while a
pending `z` does not; descending writes follow the reversed frontier. A
pending `Ä` falls after `b,d` under the binary collation but before them
under `utf8mb4_0900_ai_ci`, reversing the corresponding wait decision.

The scheduler now decodes pending text PRIMARY claims and compares the
column's collation key with visible rows at the frontier. Equal primary
weights conservatively wait, preserving full-order tie behavior. The focused
transaction regression covers all 44 audited cases, including an empty
primary-key string that precedes the first visible row. Other composite text
shapes, collations, and order expressions remain to audit.

## Composite text and integer primary keys

A `VARCHAR` plus `INT` primary key also uses the pending-insert frontier.
Starting with `(b,1)`, `(b,3)`, and `(d,1)`, MySQL 8.4.11 and fsdb agree
when a pending `(b,0)` or `(b,2)` enters an ascending `LIMIT 1/2` result,
while `(b,4)` leaves the first slot alone. Under `utf8mb4_bin`, a pending
`(Ä,0)` falls after the visible text keys; under `utf8mb4_0900_ai_ci`, it
falls before them. The corresponding ascending or descending UPDATE/DELETE
waits, affected counts, and final rows agree in all 20 audited combinations.
The [native oracle](../scripts/limited-ordered-write-rebase-oracle.py) and
focused transaction regression cover both collations.

An additional eight MySQL and fsdb cases cover mixed ASC/DESC and DESC/ASC
sorting across the text and integer columns. In both directions, MySQL waits
for an uncommitted `(b,2)` insert even when that tuple sorts beyond the first
visible candidate; after commit, both engines update or delete the same first
row. This confirms the scheduler's conservative mixed-direction wait for the
audited two-column shape. Wider tuples and other collations remain open.

## Secondary-index ordered frontiers

An ordered limited write can also need to wait for an uncommitted insert whose
ordering column is a secondary B-tree key rather than the primary key. With
visible `(id,rank_value)` rows `(1,1)` and `(2,2)`, an owner inserts `(0,0)`
and a second transaction runs `UPDATE ... ORDER BY rank_value ASC LIMIT 1`.
MySQL 8.4.11 waits, then updates row 0 after the owner commits. Before the
fix, fsdb completed early and updated row 1. An inserted `(3,3)` sorts beyond
the ascending frontier, so MySQL does not wait and updates row 1.

Fsdb now records a separate ordered observation claim for each prepared
insert into a visible, whole-column B-tree index. The limited-write scheduler
compares active index claims with its visible frontier and rebases after a
crossing insert commits. The native oracle and focused regression cover ten
UPDATE/DELETE cases: both ASC/DESC directions, crossing and non-crossing
inserts, a pending NULL ordering value, affected counts, and final rows.
Distinct primary-key inserts with
the same non-unique secondary value remain independent on both engines; the
observation claims do not serialize them.

At `LIMIT 2`, the native oracle now covers another ten UPDATE/DELETE cases
with three visible rows. Fsdb and MySQL both wait for an insert that enters
the selected pair, then update or delete the same two rows. The final rows
and affected counts also agree when the pending insert sorts beyond the
pair. For this small-table shape, MySQL waits even for those beyond-frontier
inserts. A separate 1,000-row MySQL case with an insert beyond the frontier
completes without waiting. The [short-table oracle](../scripts/secondary-limit-frontier-oracle.py)
extends this boundary across `LIMIT 1`–`4`: when visible matches are fewer than
twice the limit, the beyond-frontier insert makes MySQL wait; at or above that
count it does not wait in the audited secondary-key layout. The same three-row
`LIMIT 2` case waits without a WHERE filter, but does not wait with `id<=2` or
`rank_value<=2`: those indexed filters exclude the pending row. Fsdb now
observes pending secondary keys for the audited short-table scan shapes and
retains its narrower frontier check for indexed filters or enough visible
matches. The focused regression covers both sides of each boundary, including
the earlier three-row `LIMIT 2` mismatch. Other predicates, index plans, and
page layouts still need separate evidence.

An indexed primary-key filter can also exclude a pending insert whose
secondary ordering key falls before the selected frontier. With visible
`(id,rank_value)` rows `(1,1),(2,2),(3,3)`, a pending `(4,0)` sorts first,
but `WHERE id<=2 ORDER BY rank_value LIMIT 2` cannot select it. MySQL 8.4.11
does not wait for that insert for either UPDATE or DELETE. Fsdb previously
waited because the secondary-key claim lacked the pending row's primary key.
The claim now carries its known primary-key components; the scheduler can
discard a pending claim when an exact numeric indexed conjunct proves it
cannot match. The native oracle and focused transaction regression pin the
wait, affected count, and final rows for both verbs. Predicates that cannot
be proven false from those keys remain conservative.

An in-memory A/B/A check with 1,000 sequential inserts into a table with one
secondary index measured warm trials around 186–205 ms on the previous and
current revisions. The spread does not establish a precise cost difference;
the maintained [insert profile](../scripts/ordered-secondary-insert-profile.fsx)
allows a larger follow-up. Expressions, invisible or prefix indexes, other
order terms, and insert values unknown until later preparation remain outside
this audit.
