# Full-text scoring with equality-join bounds

Status: bounded inference implemented; broader MATCH planning remains in GAPS.md.

For an inner join on `o.id=d.owner_id`, `WHERE o.id=42` restricts the possible
values of `d.owner_id`. Fsdb uses this bound to select indexed scoring candidates
before preparing full-text source rows. Equality chains can span multiple
physical sources, with equality, ordered comparisons, BETWEEN, and literal IN bounds in
WHERE or ON. The original predicates
and joins still execute, and relevance uses the complete indexed corpus.

Inference requires uniquely resolved columns, identical column types and compatible
text collations. Each ON clause resolves names against its visible join prefix;
WHERE resolves against all sources. A later source with the same column name
does not make an earlier ON reference ambiguous. Numeric columns accept numeric literal bounds; text columns
accept string literal bounds. NULL list members retain SQL three-valued semantics.
Outer joins, USING joins, disjunctions, mixed
comparison domains, and invalid forward ON references do not supply inferred
bounds. Existing source-local access remains available.

The restriction on literal domains matters: under `utf8mb4_0900_ai_ci`, `'①'`
and `'1'` compare equal, but `d.k=1` rejects `'①'`. Propagating a numeric literal
across a text equality would incorrectly drop a matching row.

## Oracle and regressions

The [native oracle](../scripts/fulltext-join-bounds-oracle.py) checks MySQL 8.4.11
on a disposable server:

```sh
python3 torture/scripts/fulltext-join-bounds-oracle.py
```

It verifies IDs and rounded relevance for direct, transitive, ON-literal, and
cross joins, plus text coercion, outer joins, and disjunctions. Unqualified
keys and bounds include a later-source name collision. Ambiguous WHERE/ON
references return error 1052; forward ON references return error 1054. The matching
Expecto regressions compare results with the explicit-bound query and count
virtual-column evaluations to verify candidate preparation. Before inference,
the 1,000-row fixture prepares 800 rows despite returning ten; the regression
requires fewer than 30 evaluations after inference. Range and IN cases return
20 rows and require fewer than 50 evaluations, including reversed comparisons
and an IN list containing NULL.

## Targeted performance evidence

These timings measure the qualified-bound implementation at `7b4da9ff`.

The embedded Debug, in-memory profile described in the
[baseline snapshot](../../benchmarks/results/884fece3-fulltext-snapshot.md)
uses 10,000 documents and returns 100 rows. Five warmups precede seven batches
of ten queries, with `DOTNET_PROCESSOR_COUNT=4` on the same host.

| Query | Before median ms | After median ms |
|---|---:|---:|
| Bound supplied through join | 6.917 | 1.149 |
| Explicit redundant source bound | 0.811 | 1.217 |

After-change batch averages in milliseconds:

- Join bound: 1.11221, 1.23457, 1.10175, 1.25141, 1.17055, 1.13346, 1.14867.
- Explicit bound: 1.20385, 1.25312, 1.21730, 1.26226, 1.20239, 1.30870, 1.12162.

The formerly unbounded query is about 6 times faster in this profile. The
explicit-bound control is slower in this run; graph analysis adds work, and background activity remains uncontrolled.
This profile does not isolate the cause of the control difference. These figures
are diagnostic engine timings, not durable deployment or MySQL comparisons.

## Validation

`just check` passes 2,939 tests without build warnings or errors. The native
join-bound oracle and natural-phrase oracle pass. The contract lane passes
49 cases / 5,117 steps without differences at
`20261007T103918427-74525/contracts`.

## Mixed-type IN across equality joins

The same native oracle records a join-order-dependent result for a numeric
IN filter on collation-equivalent text keys. MATCH can change the chosen plan,
but the behavior also occurs without full-text search. It is separate from
compatible-domain bound propagation.

```sql
CREATE TABLE names(id INT PRIMARY KEY,
  k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci, body TEXT, KEY(k), FULLTEXT(body));
CREATE TABLE labels(k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci);
INSERT INTO names VALUES(1,'①','needle'),(2,'1','needle'),(3,'other','ordinary');
INSERT INTO labels VALUES('1');
SELECT d.id FROM names d JOIN labels o ON o.k=d.k
WHERE o.k IN (1,NULL) AND MATCH(d.body) AGAINST('needle') ORDER BY d.id;
```

MySQL 8.4.11 returns ID 2; fsdb previously returned IDs 1 and 2. Removing
MATCH returns IDs 1 and 2 on both engines. Replacing IN with `o.k=1` or
`o.k BETWEEN 1 AND 1` also retains both rows on MySQL. Replacing the numeric
literal with the string `'1'` retains both rows with IN and MATCH.

The controlled STRAIGHT_JOIN pairs expose the deciding transformation. In every
query the written predicate is `o.k IN (1,NULL)`:

| Join form | MATCH | MySQL filter owner | MySQL IDs | fsdb IDs |
|---|---|---|---|---|
| `names d JOIN labels o` | absent | o | 1, 2 | 1, 2 |
| `names d JOIN labels o` | present | d | 2 | 2 |
| `names d STRAIGHT_JOIN labels o` | absent | d | 2 | 2 |
| `names d STRAIGHT_JOIN labels o` | present | d | 2 | 2 |
| `labels o STRAIGHT_JOIN names d` | absent | o | 1, 2 | 1, 2 |
| `labels o STRAIGHT_JOIN names d` | present | o | 1, 2 | 1, 2 |

`EXPLAIN FORMAT=TREE` shows `(d.k in (1,NULL))` on the names source for the
three differing plans. With labels first it retains `(o.k in (1,NULL))` and
looks up the names key using `o.k`. The maintained oracle asserts the result
and predicate owner for every pair, without pinning estimated costs.

General numeric-bound propagation across text equalities is invalid: the
collation equates `'①'` and `'1'`, but their numeric conversions differ. An
unconditional rewrite would disagree with MySQL's labels-first plan. The
audited two-source equality joins now transfer literal multi-value numeric
`IN` when `STRAIGHT_JOIN` fixes the first source or a MATCH predicate identifies
the driven source. A single-value `IN` retains its original comparison behavior.
The `mixed-type-join-in` contract covers both explicit join orders, the MATCH
case, and a string/numeric mixed list. MySQL also makes the same placement when
the equality is one conjunct of a compound `ON` condition: adding
`AND d.id>0` gives ID 2 with names first and IDs 1 and 2 with labels first.
fsdb now recognizes that audited shape as well.

Costed joins without MATCH remain open. MySQL may choose the filtered table
even when its raw row count is larger; simply selecting the smaller table
introduced a wrong result in a counterprobe with additional label rows. fsdb
therefore retains the written predicate for these plans until its source-choice
model can identify the owner reliably. In particular, when `d.k IN (1,NULL)`
is written on an ordinary `names d JOIN labels o` and MySQL drives from `o`,
fsdb still returns only ID 2 where MySQL returns IDs 1 and 2. Broader join
graphs are likewise open.

A repeatable native 8.4.11 plan probe with the indexed `names` table above
adds a second constraint on the next implementation. With one `labels` row,
both written `d.k IN (1,NULL)` and `o.k IN (1,NULL)` return IDs 1 and 2;
`EXPLAIN FORMAT=TREE` drives from `labels`, filters `o.k`, and looks up `names`
through its key. After adding `labels('other')`, the written `d.k` predicate
uses a hash join with a `d.k` filter in the plan, yet still returns IDs 1 and
2. The written `o.k` predicate continues to drive from `labels` and returns
IDs 1 and 2. Thus plan-text filter ownership alone does not predict the
membership difference observed in the controlled straight-join cases. The
oracle script now asserts both plan placement and result rows, and compares
full-text scores with a same-instance baseline because rounded relevance can
vary between fresh servers.

The indexed-table probe also evaluates the keys directly: `names.k IN
(1,NULL)` is NULL for `①` and true for `1`. Yet after the second label row is
added, an ordinary costed join with a written `d.k IN (1,NULL)` returns both
IDs, while `names d STRAIGHT_JOIN labels o` with the same written predicate
returns only ID 2. Both TREE plans show a hash join with a filter on `d.k`.
The visible plan shape therefore cannot stand in for the optimizer's internal
predicate conversion. The oracle pins the direct expression, the two result
sets, and the displayed filter placement separately.

A second MySQL 8.4.11 probe isolates why this cannot be decided from the
written predicate alone. With `names` containing `(1,'①'), (2,'1'),
(3,'other')` and `labels` initially containing just `'1'`, the ordinary
`names d JOIN labels o ON o.k=d.k` returns IDs 1 and 2 for either
`d.k IN (1,NULL)` or `o.k IN (1,NULL)`. Adding `'other'` to `labels` changes
the `d.k` predicate result to ID 2, while the `o.k` predicate still returns
IDs 1 and 2. These are row-membership differences, not only EXPLAIN or cost
differences. The next fix must couple the chosen driving source, its filter
ownership, and physical execution; a row-count-based WHERE rewrite is not
sufficient.

### Recheck on 2026-10-12

The maintained `mixed-type-join-in` contract now compares costed joins with
the numeric `IN` written on each key after resetting `labels` to one row,
then adding a second, nonmatching label. On fsdb revision `62adbc22`,
MySQL 8.4.11 returns IDs 1 and 2 for both predicate owners at both
cardinalities. Fsdb agrees when the predicate is written on `o.k`, but
returns only ID 2 when it is written on `d.k`. The run at
`20261012T040412071-2626/contracts` has 139 cases / 18,872 steps and 11
differences: the previous nine signatures are unchanged, and these two
costed-join probes account for the increase. Recent index and write-path
changes have not closed this result-membership gap.

The native oracle now tests an index-only counterprobe on the same two-label
fixture. Adding `KEY(k)` to `labels` changes the costed query with written
`d.k IN (1,NULL)` from IDs 1 and 2 to ID 2; dropping that key restores IDs
1 and 2. The rows and SQL are otherwise unchanged. The expanded
`fulltext-join-bounds-oracle.py` passes against MySQL 8.4.11. A fix therefore
cannot transfer the predicate solely because `names.k` is indexed or the
labels table has fewer rows; it must account for competing access paths.
These probes require a UTF-8 connection: the native oracle explicitly uses
`utf8mb4` so the circled-digit key reaches MySQL intact.

An optimizer-trace probe of a fresh `d`/`o` fixture with the same rows adds
chosen access paths to the result evidence. With only `d.k` indexed, MySQL
chooses `o` first, probes `d.k`, attaches `o.k IN (1,NULL)`, and returns
IDs 1 and 2. Adding `KEY(k)` to `o` makes the `d`-first plan cheaper;
the trace attaches `d.k IN (1,NULL)` and the result becomes ID 2. This was
observed on the pinned 8.4.11 image with a `utf8mb4` client connection.
It explains this controlled counterexample, but it does not turn the displayed
TREE plan or a row-count heuristic into a general predicate-ownership rule.
The costed fix needs an explicit source-choice decision that accounts for
competing equality access paths and retains the original `IN` semantics
when that choice is not established.

### Audited costed-source fix on 2026-10-12

Fsdb now chooses the smaller unindexed source first for a simple two-table
inner equality join when its partner has an equality index and the query has
the audited literal multi-value numeric `IN` on a compatible text join key.
The same source choice drives physical execution and the numeric filter
transfer. A competing equality index leaves the written base in place. The
scope is deliberately limited to simple qualified projections and ordering;
other costed joins still need a fuller access-path model.

The focused regression failed on the one-label costed query before this fix.
It now covers one and two labels and the competing-index counterprobe. After
sharing text-key validation between the source choice and transfer paths,
`just check` passed 3,478 tests with no build warnings or errors. The pinned
MySQL 8.4.11 run at `20261012T045938983-12044/contracts` passed every
`mixed-type-join-in` step, including the new indexed counterprobe. Across
139 cases and 18,882 steps, the two costed result mismatches disappeared;
the remaining nine signatures are exactly the previous identifier-case
differences, with no new mismatch.

### A wider costed-join matrix

Fresh MySQL 8.4.11 tables show why the small costed fix is not a general
row-count rule. With three `names` rows and three unindexed `labels` rows,
the written `d.k IN (1,NULL)` query returns IDs 1 and 2, as it does with
101 labels. With only the two collation-equivalent `names` rows and two
unindexed labels, it returns ID 2. Adding a key to `labels` makes the
three-name, one-label query return IDs 1 and 2, but the three-name,
two-label query returns only ID 2. The maintained native
[`fulltext-join-bounds-oracle.py`](../scripts/fulltext-join-bounds-oracle.py)
asserts those fresh-table outcomes.

The focused regression and differential contract now cover the indexed
single-label choice and an unindexed one-name/one-label choice, with the
two-name/two-label and two-index/two-label outcomes as controls. Those two
singleton cases failed before this follow-up and pass afterward. The
equal-cardinality three-name/three-label unindexed query still returns only
ID 2 in Fsdb, so it remains an observed result-membership gap. MySQL's
displayed TREE filter on `d.k` does not by itself explain why that query
returns both IDs; moving the predicate based solely on row counts would
break the two-name/two-label control.

`just check` passed 3,478 tests with no build warnings or errors. In the
pinned `20261012T051636753-13861/contracts` run, the two singleton
differences disappeared and no new mismatch appeared. Of 139 cases and
18,895 steps, ten signatures remain: the nine identifier-case differences
and this equal-cardinality costed-join result mismatch. A fuller
access-path and predicate-placement model is needed before expanding this
rule to the remaining costed shapes.
