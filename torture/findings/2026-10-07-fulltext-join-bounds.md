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
