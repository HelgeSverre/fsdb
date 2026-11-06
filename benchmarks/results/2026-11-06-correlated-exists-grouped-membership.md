# Indexed correlated EXISTS with inner-column grouping

The correlated `EXISTS` shortcut previously rejected all `GROUP BY` queries.
On the 50-outer-row, 500-inner-row regression fixture, grouping by the
correlated inner key returned the correct membership values but examined
25,550 rows. The indexed membership path examines fewer than 200 rows on
that fixture after this change.

The initial shortcut accepted grouping when every projection was a literal,
plain inner column, or inner-source star, there was no `HAVING`, and each
grouping key resolves to a unique column of
the single inner source or a null check of one. A bare key shadowed by a projection alias stays on
the ordinary path. Other limits, offsets, source shapes, and expressions
retain their existing eligibility rules. These restrictions preserve
binding errors and avoid skipping evaluation of grouping expressions.

The focused regression covers qualified and bare keys, `LIMIT 1`, plain
column and star projections, matched and unmatched outer rows, an unknown
grouping column, and the examined-row budget. The native MySQL 8.4.11 oracle
confirms the same membership and
unknown-column error, and prepares the valid queries. This is an examined-row
improvement; no latency measurement is claimed.

Run `dotnet run --project tests/Fsdb.Tests -c Release -- --filter-test-case
'correlated' --no-spinner` and `uv run --with pymysql python
torture/scripts/correlated-order-alias-oracle.py`.

## Inner-column null-check grouping

The indexed shortcut also accepts `GROUP BY (i.outer_id IS NULL)` and
`GROUP BY (outer_id IS NOT NULL)` when the operand resolves uniquely to an
inner column and does not bind to a projection alias. These checks cannot
raise conversion errors or change whether a nonempty filtered source has a
group. Both forms stay below 200 examined rows on the 50-by-500 fixture.
The native MySQL 8.4.11 oracle confirms their results and prepared forms;
all 90 correlated tests pass. Other computed group keys remain on the
ordinary path.

## Boolean combinations of null checks

The indexed membership path also accepts `NOT`, `AND`, and `OR` combinations
whose leaves are inner-column `IS NULL` or `IS NOT NULL` checks. These
operations cannot raise a numeric conversion warning and do not change
whether a nonempty filtered source has a group. Three grouped expressions
match the ungrouped 50-row result with fewer than 200 examined rows each.
The native MySQL 8.4.11 oracle agrees on ordinary and prepared results, and
all 90 correlated tests pass. `GROUP BY NOT i.outer_id` remains on the
ordinary path because Boolean coercion of a raw column can warn; its
examined-row control verifies that it does not enter this shortcut. Other
computed keys remain open.

## Null-safe equality to NULL

The same indexed membership path now accepts `GROUP BY (i.outer_id <=> NULL)`
and its reversed-operand form, including Boolean combinations with the
existing null checks. The grouping expression reads a uniquely bound inner
column and cannot raise a conversion warning. The 50-by-500 regression
matches ungrouped membership while examining fewer than 200 rows for each
form. Native MySQL 8.4.11 agrees on the results and prepared statements.
Other computed grouping expressions remain on the ordinary path.

## Null-check projections over grouped EXISTS

Grouped `EXISTS` now uses the same validated inner-column null-check grammar
for its projection as for its grouping key: `IS NULL`, `IS NOT NULL`,
`<=> NULL`, and Boolean `NOT`/`AND`/`OR` combinations. These projections
cannot raise a conversion warning, and `EXISTS` needs only the group’s
presence. The 50-by-500 indexed fixture stays below 200 examined rows for
each audited form, including matched and unmatched outer keys. A coercing
`NOT inner_column` projection stays above 500 examined rows on the ordinary
path; an unknown projected column retains error 1054.

Native MySQL 8.4.11 agrees on the audited results and prepared forms. All
90 correlated tests pass. No latency claim is made from the examined-row
counter alone.

## Projection aliases as grouping keys

`GROUP BY group_key` now uses indexed membership when exactly one projection
defines `group_key`, no inner column has that name, and the projection
expression is an inner column or one of the validated null-check forms above.
The 50-by-500 fixture examines fewer than 200 rows for both
`i.outer_id AS group_key` and `i.outer_id IS NULL AS group_key`. A coercing
`NOT i.outer_id AS group_key` remains on the ordinary path and examines more
than 500 rows. Duplicate aliases, names that collide with an inner column,
and other expressions retain ordinary binding and evaluation. Native MySQL
8.4.11 agrees on matched and unmatched results and prepared statements; all
90 correlated tests pass. This is an examined-row result, not a latency claim.

## Combined COUNT bounds

The indexed count path now accepts `AND` and `OR` combinations when every
leaf compares the built-in `COUNT(*)` with an integer literal. On the
50-outer-row, 500-inner-row fixture, `COUNT(*) > 19 AND COUNT(*) < 21`
previously examined 550 rows; the indexed path stays below 200 while
returning the same membership values. A four-key fixture checks both
connectives for zero through three matches, with and without grouping. The
grouped shortcut remains conservative: each leaf must independently have a
constant truth value for positive group sizes. The pinned MySQL 8.4.11 oracle
confirms ordinary and prepared results.

The same guarded path now accepts `NOT` around eligible comparisons and
`AND`/`OR` combinations. On the 50-by-500 fixture,
`NOT (COUNT(*) <= 19 OR COUNT(*) >= 21)` previously examined 550 rows;
the indexed count examines fewer than 200, with or without grouping by the
correlated integer key. A zero-to-three-row fixture and the MySQL 8.4.11
oracle check the negated truth table, including empty grouped sources. This
is an examined-row result, not a latency measurement.

## Positive COUNT HAVING membership

An `EXISTS` projection of literals with no `ORDER BY` and
`HAVING COUNT(*) > 0` now uses the
indexed membership count when the source and grouping keys meet the existing
eligibility rules. The equivalent `0 < COUNT(*)`, `COUNT(*) >= 1`, and
`1 <= COUNT(*)` forms follow the same path. On the 50-by-500 fixture, each
returns the same matched and unmatched values as plain `EXISTS` while
 examining fewer than 200 rows. Other HAVING predicates
and nonliteral projections retain ordinary evaluation so their binding and
aggregate behavior remain visible. Native MySQL 8.4.11 agrees on the
audited results and prepared forms; all 90 correlated tests pass.

`COUNT(*) <> 0` and the reversed `0 != COUNT(*)` are also positive-count
membership tests: `COUNT(*)` cannot be negative. Both now take the indexed
path on the 50-by-500 fixture and examine fewer than 200 rows, with matched
and unmatched outer keys. The native MySQL 8.4.11 oracle agrees on the
results and prepared forms; all 90 correlated tests pass.

## Zero COUNT HAVING membership

The same guarded path now handles `COUNT(*) = 0` and `0 = COUNT(*)`.
Because counts are nonnegative integers, it also handles `COUNT(*) < 1`,
`1 > COUNT(*)`, `COUNT(*) <= 0`, and `0 >= COUNT(*)`.
With no `GROUP BY`, an empty filtered source still produces one aggregate
row, so `EXISTS` is true exactly when the indexed match count is zero.
With safe grouping keys, an empty source produces no group, while every
nonempty group has a positive count; `EXISTS` is always false. The
50-by-500 fixture checks both shapes, matched and unmatched outer rows,
and fewer than 200 examined rows. Native MySQL 8.4.11 agrees on the
results and prepared statements; all 90 focused correlated tests pass.
