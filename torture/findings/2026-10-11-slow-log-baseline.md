# Slow query table baseline

The digest-pinned MySQL 8.4.11 server was probed with `log_output=TABLE` and
`slow_query_log=ON`. A separate client set `SESSION long_query_time=0` and ran
`SELECT 42 AS fsdb_slow_probe`, `DO 1`, and a failing SELECT with no default
database. After the log was disabled, `mysql.slow_log` contained all four
completed statements, including the threshold-setting statement and the
failed SELECT.

| Statement | rows_sent | rows_examined | Other observed fields |
|---|---:|---:|---|
| `SET SESSION long_query_time=0` | 0 | 0 | query time was nonzero |
| `SELECT 42 AS fsdb_slow_probe` | 1 | 1 | lock time was zero |
| `DO 1` | 0 | 1 | lock time was zero |
| `SELECT * FROM fsdb_missing_slow_probe` | 0 | 0 | error 1046, still logged |
| `SELECT id FROM fsdb_slow_scan_probe WHERE id > 1` | 2 | 3 | three-row temporary table without an index |

A follow-up mutation probe found `rows_examined=0` for a three-row literal
`INSERT`, `1` for an indexed single-row UPDATE and DELETE, and `3` for an
UPDATE of a three-row table without an index. A DELETE scanning the two rows
left after a prior deletion reported `2`. MySQL counts candidate rows inspected
by these mutations, not only rows changed.

A four-row FULLTEXT fixture with three `alpha` posting matches showed
`rows_examined=3` for `MATCH(body) AGAINST('alpha' IN BOOLEAN MODE)`, `1`
with `LIMIT 1`, and `3` when an additional `id>1` condition rejected one
candidate. A missing term examined zero rows. Fsdb now counts the consumed
full-text candidates for these single-table reads. MySQL reported six for
the same three matches with `ORDER BY id` or `ORDER BY body`, and four when
either sort uses `LIMIT 1`. Fsdb now also counts the emitted rows of that
extra sorting pass. With no explicit sort, `LIMIT` still stops consuming
posting candidates early.
Descending `ORDER BY MATCH(body) AGAINST('alpha')` is different: MySQL
uses relevance order without a separate sort, reporting three examined rows
without a limit and one with `LIMIT 1`. Fsdb now streams that exact
single-source order and retains the early limit, for both natural-language
and Boolean search.

For a forced inner join from the full-text table to a primary-key owner,
three matching postings and three owner probes produced six examined rows.
`LIMIT 1` produced two, and a missing term produced zero. Fsdb now counts
the consumed full-text base candidates once even when join planning rereads
the source; the indexed owner probes already contributed their own counts.
The same counts hold for the ordinary two-table inner and left joins when
the full-text table is written first. With the owner table written first,
MySQL still starts from the three posting matches and examines six rows;
fsdb previously examined seven by scanning four owners and three matching
documents. Fsdb now compares the posting-candidate count with the written
base-table row count and starts from the three postings, reusing those
scores during execution. Under `LIMIT 1`, MySQL examines two; fsdb's
full-text-first indexed-probe rule also examines two. More complex reversed
predicates and multi-source cost choices remain open.

An `INSERT … SELECT` that read three source rows and inserted two reported
`rows_sent=0`, `rows_examined=3`; fsdb's existing nested SELECT scope gives
the same result in a regression.

A follow-up join probe used three left rows and four right rows, with three
matching right rows. MySQL 8.4.11 logged six examined rows for an indexed
`STRAIGHT_JOIN` on the right-side key, and seven when the right-side key had no
index. Both returned three rows. The fsdb regressions now cover both paths:
they count each left source row once, right-side index candidates for the
indexed path, and right source rows for the scan/hash path. Planner re-reads
must not inflate the metric.

The probe used `a(id PRIMARY KEY, v)` with IDs 1–3 and
`b(id PRIMARY KEY, left_id)` with `left_id` values 1, 1, 2, and 4. It ran
`SELECT a.id,b.id FROM a STRAIGHT_JOIN b ON b.left_id=a.id` under
`long_query_time=0`, first with `KEY(left_id)` on `b`, then without it.
For the same data, MySQL logged six examined rows for an indexed left outer
join and seven for a scan-shaped left outer join; both returned four rows.
Right outer joins logged seven in both layouts. Adding an unmatched fourth
left row left the right-join count at seven, confirming that the preserved-
right index probe counts matching left candidates rather than the whole left
table. Fsdb now follows these observed counts. Its preserved-right strategy
is selected before evaluating unused competing plans, so those planner reads
cannot inflate the slow-log metric.

With `LIMIT 1`, MySQL logs two examined rows for each indexed inner, left,
and right join, four for the unindexed inner join, five for the unindexed left
join, and two for the unindexed right join on this fixture. Fsdb now matches
the indexed cases and both unindexed inner/right cases. The hash-side size
probe is excluded from the slow-log counter; only rows consumed by the chosen
hash plan count. The unindexed left outer join now matches: fsdb builds
right-side hash buckets, then streams left rows until the limit is satisfied,
logging five examined rows. Follow-up MySQL 8.4.11 probes also reported five
for `LIMIT 2`, `LIMIT 1 OFFSET 1`, and `LIMIT 1` when the leading left row is
unmatched; the regression covers all four shapes. A residual predicate
`ON b.left_id=a.id AND b.id<2 LIMIT 1` reported two examined rows in MySQL;
fsdb now matches by applying the indexed right-only condition before joining.
Without `LIMIT`, the same condition returned three left rows (two null-padded)
and examined four rows in MySQL 8.4.11: three left candidates plus the one
right candidate selected by the primary-key range. Fsdb matches that case too.

A null-rejecting `WHERE b.id<2` on the same two-table left join returned only
`(1,1)` and examined two rows in MySQL 8.4.11, both with and without `LIMIT 1`.
Fsdb formerly examined seven rows because it retained the outer join. The
audited indexed case now becomes an inner join driven from `b`, then probes
`a`; `SELECT *` keeps the written table-column order and `WHERE b.id IS NULL`
still retains the outer join.

With `WHERE b.id=1 OR b.id=2`, MySQL 8.4.11 returned the two matching rows
and examined four candidates, or two with `LIMIT 1`. Fsdb previously examined
seven for the full query. Both OR branches reject a null-padded `b`, so the
same inner-join conversion now carries the two-row indexed union into the
join plan. `WHERE b.id=1 OR b.id IS NULL` remains an outer join and returns
the unmatched left row, as MySQL does.

The equivalent `WHERE b.id IN (1,2)` and `WHERE b.id BETWEEN 1 AND 2`
predicates have the same MySQL 8.4.11 counts: four examined rows without a
limit and two with `LIMIT 1`. Both are strict on a null-padded `b.id`, and
fsdb now selects their indexed candidates for the same join conversion.

MySQL 8.4.11 also folds strict negated range comparisons before choosing the
join order. On the same fixture, `WHERE NOT (b.id>2)` returns `(1,1)` and
`(1,2)` with four rows examined; `WHERE NOT (b.id<3)` returns `(2,3)` with
three examined. Fsdb formerly examined seven for both. It now uses the
equivalent inclusive range to select indexed right-side candidates while
evaluating the original `WHERE` condition for the result. In contrast,
`WHERE (b.id=1 OR b.id=2) IS TRUE` examined seven rows in MySQL, despite
rejecting a null-padded right row, so that form is not included in this
indexed join conversion.

For a single table with 1,000 primary-key rows, MySQL 8.4.11 chose a range
access for `SELECT payload FROM t WHERE NOT (id>2)` and examined only the two
matching rows. With four rows, it chose an index scan and examined all four.
Fsdb previously scanned all 1,000 rows in the larger case. The shared range
classifier now exposes the equivalent bound to its costed index planner, so
the larger query examines two rows while the original predicate still decides
the result. On that 1,000-row fixture,
`UPDATE t SET payload=99 WHERE NOT (id>2)` and
`DELETE FROM t WHERE NOT (id>2)` each affected and examined two
rows in MySQL 8.4.11; the fsdb regression confirms the same counts and final
row count for both mutations.

On the indexed two-table left-join fixture, both `WHERE NOT (b.id=3)` and
`WHERE b.id<>3` returned `(1,1)` and `(1,2)` while MySQL 8.4.11 examined five
candidates. Fsdb previously examined seven. For exact integer keys, the two
strict ranges on either side of the excluded value can be merged as indexed
candidates; fsdb now uses that union for the join while evaluating the
original condition on the resulting rows.

The symmetric null-rejected `RIGHT JOIN ... WHERE a.id<2` already matches
MySQL's observed counts: with an index on `b.left_id`, two result rows examine
three candidates, or two with `LIMIT 1`; without that index, the full query
examines five and `LIMIT 1` examines two. These cases are retained as
regressions for the preserved-right probe and scan paths.

A prepared `SELECT ? AS fsdb_slow_prepared_probe` executed with 43 produced a
slow-log row for `SELECT 43 AS fsdb_slow_prepared_probe` with one row sent and
one examined. The fsdb regression also covers its binary prepared execution
path.

All rows had an empty `db`, zero `last_insert_id`/`insert_id`, and server ID
1 for this connection. `query_time` and `lock_time` have microsecond `TIME`
values.

Fsdb now publishes table-backed slow-log rows from completed text and binary
prepared statements. An execution scope counts source candidates before
filtering for scalar reads, single-table scans, `INSERT … SELECT` source reads,
single-table UPDATE/DELETE, and audited inner and outer indexed/scan joins, rather
than guessing from result-row counts.
`SET SESSION long_query_time=0`
now counts itself, matching the oracle above. Other join shapes, full-text, and
other mutation accounting remain
incomplete, and `lock_time` currently reports zero. FILE output is refused
while enabled until there is a compatible sink.
