# CTE-first CROSS JOIN update through a writable view

MySQL 8.4.11 waits on the physical row for a joined UPDATE whose first source
is a CTE and whose writable view follows a `CROSS JOIN`. After the owner
commits, the view-addressed writer increments the committed value. The same
behavior holds when a physical read source sits between the CTE and the view.

The [native oracle](../scripts/cte-joined-write-oracle.py) pins both cases
under contention. Before this change, fsdb's CTE-first view promotion handled
`INNER JOIN` but stopped at `CROSS JOIN`. The writer therefore ran without a
pre-execution physical row claim. The new transaction regression failed its
wait assertion. The promotion now recognizes both join kinds while retaining
the original `CROSS JOIN` in the rewritten source tree. Both contended cases
wait, rebase, and commit with the expected final physical row values. The
full `just check` run passed all 3,883 tests, and the focused regression
passed again after the test was deduplicated.

```sh
uv run --with pymysql python torture/scripts/cte-joined-write-oracle.py
dotnet run --project tests/Fsdb.Tests -c Release -- --sequenced --no-spinner \
  --filter-test-case 'CTE-first cross joined view update waits for its physical target'
```

This covers the audited CTE-first order and one intervening physical source.
Other dependent source and outer-join reorderings remain open.
