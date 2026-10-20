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

The audit also covers `LIMIT 2` over three rows. The owner can make row 1
stop matching, move it between rows 2 and 3, or move it behind both. After
the owner commits, the waiting writer reselects two matching rows in the
current order. A further four-row case has two independent owners change
the first two candidates to negative values. The limited writer waits for
both owners to commit, then updates or deletes rows 3 and 4. MySQL and Fsdb
agree with and without a secondary index on `n`.

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

The same proof covers a literal `IN` filter. With visible primary keys 1, 2,
and 3, pending `(id,rank_value)=(4,0)` cannot enter `WHERE id IN (1,2)
ORDER BY rank_value LIMIT 2`. MySQL 8.4.11 does not wait for the insert for
UPDATE or DELETE; fsdb now skips its secondary-key claim after proving every
literal member unequal to the pending primary key. Unsupported comparison
domains retain the conservative wait. When the list also contains 4, both
engines wait and then select rows 4 and 1. The oracle and focused transaction
regression pin both verbs and their final rows.

For `WHERE id NOT IN (4,5)` with pending id 4, MySQL also completes without
waiting and selects visible rows 1 and 2. Fsdb now proves exclusion when any
literal member equals the pending key; an unsupported comparison remains
conservative. The native oracle covers both write verbs.

The same proof now covers a text primary-key filter using the column's
collation key. With visible `b,d,f` ordered by a secondary integer key, a
pending `z` at rank zero does not delay `WHERE id<='f' ORDER BY rank_value
LIMIT 2`; a pending `a` does. Under `utf8mb4_0900_ai_ci`, pending `Ä` also
enters the filtered result and makes the write wait. The maintained native
oracle and transaction regression cover UPDATE and DELETE for all six cases,
including affected counts and final rows. The check applies only to literal
text comparisons with an indexed text key; other coercion domains retain the
conservative wait.

Literal text `IN` filters use the same key with the column's collation. A
pending `Ä` is excluded by `IN ('a','b','d')` under `utf8mb4_bin`, but enters
the selected pair under `utf8mb4_0900_ai_ci`; `IN ('b','d','z')` excludes it
under the latter collation. The native oracle and transaction regression
cover both write verbs, wait decisions, and resulting rows.

An indexed `IS NOT NULL` filter can likewise rule out a pending row whose
secondary ordering key is NULL. With visible ranks 1, 2, and 3, a pending
`(id,rank_value)=(4,NULL)` does not delay an ascending `LIMIT 2` UPDATE or
DELETE filtered by `rank_value IS NOT NULL`; both select rows 1 and 2. If the
pending rank is 0, both writes wait and then select rows 4 and 1. MySQL 8.4.11,
the maintained oracle, and the focused transaction regression agree for both
verbs. The scheduler now skips a pending claim for this indexed `IS NOT NULL`
case only when the known key is NULL.

The inverse indexed `IS NULL` filter can rule out a pending row with a known
non-NULL secondary key. With visible NULL-ranked rows 1 and 2, a pending
rank-zero row sorts ahead of them under `ORDER BY rank_value DESC LIMIT 2`,
but cannot enter `WHERE rank_value IS NULL`. MySQL 8.4.11 does not wait for
that insert for either UPDATE or DELETE. Fsdb previously waited; the
scheduler now excludes the claim when its known key is non-NULL. The native
oracle and focused transaction regression cover ascending and descending
orders, affected counts, and final rows.

A literal NULL in an ordinary indexed comparison (`=`, `<>`, or an ordering
comparison) cannot make any row match a WHERE clause. MySQL 8.4.11 therefore
finishes `UPDATE` and `DELETE` with `ORDER BY rank_value LIMIT 2` without
waiting for a pending insert, even when that insert sorts ahead of visible
rows. Fsdb previously waited for the ordered-key claim. The scheduler now
excludes it for these comparisons, while leaving NULL-safe equality to its
separate semantics. The native oracle and focused transaction regression
cover `rank_value=NULL`, `rank_value<>NULL`, and `NULL<rank_value` for both
write verbs.

An `IS TRUE` filter does not have the same wait behavior as those impossible
NULL comparisons. With visible ranks 1, 2, and 3 and a pending rank-zero
insert ordered before them, MySQL 8.4.11 waits for both `UPDATE` and `DELETE`
under `WHERE rank_value IS TRUE ORDER BY rank_value LIMIT 2`, then selects
visible rows 1 and 2. Fsdb already agrees. The native oracle and focused
transaction regression pin this boundary, so pending-key exclusion remains
limited to the predicate forms proved above.

## Equal secondary-key frontiers

A pending row can enter a limited write even when its secondary ordering key
equals the visible frontier. With visible `(id,rank_value)` rows `(1,1)`,
`(2,2)`, and `(3,3)`, a pending `(0,2)` enters `ORDER BY rank_value ASC LIMIT 2`
ahead of `(2,2)`; pending `(4,2)` does not. Descending order reverses that
tie: `(4,2)` enters the selected pair and `(0,2)` does not. The same rule
applies to a nullable secondary key: among visible `(1,NULL)` and `(2,NULL)`,
a pending `(0,NULL)` enters an `IS NULL` filtered ascending pair, while
`(4,NULL)` does not. MySQL 8.4.11 confirms waits, affected counts, and final
rows for both UPDATE and DELETE across all 12 combinations.

The scheduler now compares the complete primary key when a pending secondary
key ties the frontier. After a required wait, the mutation selector also uses
that primary-key order to choose rows; waiting alone had still selected the
wrong visible pair after the inserted row appeared. The tie rule applies when
the ORDER BY columns cover a complete secondary index or a prefix of the
primary index. An incomplete secondary-index prefix conservatively waits on
equal keys because another index column may decide their order.

An in-memory A/B/A check with 1,000 sequential inserts into a table with one
secondary index measured warm trials around 186–205 ms on the previous and
current revisions. The spread does not establish a precise cost difference;
the maintained [insert profile](../scripts/ordered-secondary-insert-profile.fsx)
allows a larger follow-up. Expressions, invisible or prefix indexes, other
order terms, and insert values unknown until later preparation remain outside
this audit.

## Filesort expression order

MySQL 8.4.11 waits for an uncommitted primary-key insert when a limited write
orders by `id+0`, even when the inserted key sorts beyond the visible result
frontier. With `LIMIT 1`, `2`, or `3`, an insert of either 0 or 100 blocks both
UPDATE and DELETE. After commit, the write reselects the first `LIMIT` rows
in expression order. The native oracle and focused transaction regression
cover all 12 wait, affected-row, and final-row outcomes.

Fsdb previously recognized only direct indexed ORDER BY columns and completed
the key-zero case before the insert committed. Unsupported filesort expressions
now conservatively wait for active primary-key insert claims before choosing
limited rows. This reproduces the measured behavior for `id+0`; more complex
expressions and other non-primary insert shapes still need separate audits.

## Keyless filesort writes

The native MySQL 8.4.11 oracle also tests a table with no primary key and an
ordinary `rank_value` index. A pending insert at rank 0 or 100 blocks an
`UPDATE` or `DELETE` with `WHERE n>=0 ORDER BY rank_value+0 LIMIT 2`, even
though rank 100 lies beyond the selected pair. After the wait, rank 0 enters
the first pair and rank 100 does not. A strict indexed lower bound,
`WHERE rank_value>0`, skips a pending rank 0 without waiting but still waits
for rank 100. `rank_value>=0` waits for rank 0; `rank_value<50` also waits
for rank 100 at the upper range's next-key boundary. On `UPDATE`,
`IGNORE INDEX(ix_rank)` restores a scan, so even `rank_value>0` waits for
pending rank 0.

A pending `NULL` rank also blocks an unfiltered `UPDATE`. An indexed numeric
range excludes that `NULL` key without waiting, while `IGNORE INDEX(ix_rank)`
restores the wait. These nullable cases are covered by the native oracle and
transaction regression for `UPDATE`.

The [text-index oracle](../scripts/keyless-text-filesort-oracle.py) adds 34
`UPDATE` and `DELETE` cases with `ORDER BY CONCAT(rank_value,'')` and an
ordinary `VARCHAR` index under both `utf8mb4_bin` and
`utf8mb4_0900_ai_ci`. MySQL skips a pending `a000` below the strict
`rank_value>'a000'` bound, including the folded-equal `>'A000'` comparison;
it still waits for a pending `z999` beyond the visible pair, for an inclusive
lower bound, and at an upper next-key boundary. Pending `NULL` is excluded
by the indexed text range but blocks the unfiltered filesort. An `UPDATE`
with `IGNORE INDEX(ix_rank)` waits for the lower pending key. The shared
transaction regression checks the same waits, affected counts, and final
rows. Fsdb compares text claims with the index column's collation key before
excluding them from the indexed lower range.

The [DATE-index oracle](../scripts/keyless-date-filesort-oracle.py) covers
thirteen `UPDATE` and `DELETE` cases on a keyless table ordered by
`DAYOFYEAR(rank_date)`. MySQL skips a pending `2026-01-01` below a strict
indexed lower bound and a pending `NULL` under that bound. It waits for the
same date at an inclusive lower bound, a pending `2026-12-31` beyond the
visible pair or at an upper next-key boundary, and an unfiltered pending
`NULL`. `IGNORE INDEX(ix_rank)` restores the wait for an excluded lower
date. Fsdb previously waited even on the two excluded claims because its
ordered DATE key was encoded as a day number while the SQL bound remained a
string. The claim comparison now accepts valid ISO DATE literals and leaves
other date spellings conservative. The shared transaction regression checks
the same waits, affected counts, and final rows for both write verbs.

The same oracle now covers seventeen `TIME` cases on a keyless table ordered
by `TIME_TO_SEC(rank_value)`, including negative values and values above 24
hours. A pending `350:00:00` is skipped below a strict indexed lower bound,
but blocks at an inclusive bound or when the index is ignored. A pending
`800:00:00` beyond the visible pair and an unfiltered pending NULL also block. The scheduler
decodes the stored TIME tick key and compares parseable TIME bounds before
excluding a claim; unrecognized bounds remain conservative. The shared
transaction regression checks waits, affected counts, and final rows for
both UPDATE and DELETE.

The temporal oracle also covers thirteen cases each for `DATETIME(6)` and
`TIMESTAMP(6)` at UTC, plus thirteen `TIMESTAMP(6)` cases at `+02:00`, ordered
by `DATE_ADD(rank_value, INTERVAL 0 SECOND)`. These check strict and inclusive
indexed bounds, a pending key beyond the visible limit, an upper next-key
boundary, pending `NULL`, and `IGNORE INDEX`. Fsdb now decodes pending
DATETIME and TIMESTAMP keys and compares valid ISO literal bounds against
their stored ticks. TIMESTAMP bounds use the waiting session's time zone to
compare with stored UTC ticks. The shared transaction regression checks waits,
affected counts, and final rows for both write verbs. Other temporal spellings
and time-zone edge cases remain outside this audit.

A follow-up runs eight `TIMESTAMP(6)` writes with an owner at `+02:00` and a
waiter at UTC, and eight in the reverse direction. MySQL 8.4.11 and the fsdb
transaction regression agree on whether a pending key falls strictly below,
exactly on, or beyond the waiter's local bound, as well as affected counts and
final rows. This checks the UTC claim conversion across two session time zones;
named zones and daylight-saving transitions remain unaudited.

Fsdb now observes the pending ordered claim for a keyless filesort when its
first indexed column appears in the ORDER BY expression. It rules out a
pending key proven below an indexed lower bound only when that index is
available to the read; it retains the upper-boundary and ignored-index waits.
The transaction regressions and oracles check wait behavior,
affected counts, and final rows for both write verbs. Keyless tables without
a usable ordered claim, wider index expressions, and other predicate shapes
remain outside this audit.
