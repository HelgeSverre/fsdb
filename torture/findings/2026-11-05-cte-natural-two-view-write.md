# CTE-first NATURAL JOIN write through two joined views

MySQL 8.4.11 and fsdb agree on two `UPDATE` shapes that start with an independent CTE and then naturally join two writable views, one nested in parentheses. The maintained [two-view oracle](../scripts/two-joined-view-outer-oracle.py) checks affected rows and both physical base tables after each statement, alongside its existing join cases. It also independently holds one row in each base table while a CTE-first natural joined write waits, commits the owners one at a time, and confirms that the writer waits for both and rebases both increments. The oracle passed 51 UPDATE cases and 31 contention cases on this run.

The focused in-process regression, `two joined views with bare targets rebase after independent row holders`, now includes the CTE-first natural shape and passed. This extends the audited intersection of CTE name binding, merged natural columns, multi-view write targets, and two-row contention. It does not establish behavior for dependent or recursive CTEs in this exact outer layout.

## Recursive CTE follow-up

The same oracle now includes a recursive CTE that emits IDs 1 through 3
before the two writable views. Its `WHERE c.id=1` joined UPDATE matches MySQL
8.4.11's affected rows and both base tables. With independent transactions
holding the `a` and `b` rows, the writer waits for both, rebases both
increments, and commits in both engines. The expanded oracle passes 57 UPDATE
cases and 33 contention cases. The focused in-process two-view regression
includes the recursive shape. This covers an independent recursive read
source in the CTE-first NATURAL JOIN layout; dependent recursion and other
source orders remain unverified.
