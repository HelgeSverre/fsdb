# Correlated integer equalities inside inner-join `ON`

The statement-local joined `COUNT(*)`/`EXISTS` key path previously extracted
outer-to-inner equalities only from `WHERE`. A query with both equalities in
an inner-join `ON` clause returned the right rows but examined 41 rows on a
five-row outer fixture. The key-tuple control with the same predicates in
`WHERE` stayed below 25.

The pinned MySQL 8.4.11 container returned counts `2, 1, 1, 0, 0` for outer
pairs `(1,1)`, `(1,2)`, `(2,1)`, `(2,2)`, and `(NULL,1)`. `EXISTS` selected
the first three pairs. The new regression matches those rows and checks that
both `ON` equalities, or one each in `ON` and `WHERE`, use one statement-local
grouping pass. A 205-row outer control stays below 250 examined rows.

The rewrite removes only qualified outer-to-inner equality conjuncts from
`INNER` and `STRAIGHT` joins before grouping the stable inner source. It
leaves outer-join `ON` clauses in place: MySQL's `LEFT JOIN` control counts
all five preserved inner rows for every outer pair, even when none of the
`ON` equalities match. Extraction also checks the aliases available at that
join position. The pinned MySQL oracle rejects an earlier `ON future.a=o.a`
when `future` is joined later with error 1054; the regression caught and
prevented the shortcut from hiding that error. Other correlated expressions
and unstable joins still take ordinary evaluation.
