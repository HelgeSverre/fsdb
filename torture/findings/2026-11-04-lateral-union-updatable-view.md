# Writable joined view after a dependent LATERAL UNION

MySQL 8.4.11 keeps a physical target writable when a preceding read source
is a dependent `LATERAL` subquery containing `UNION DISTINCT`. The maintained
composed-view oracle uses two outer source rows and a duplicate UNION branch.
The lateral UNION eliminates the duplicate within each invocation; the outer
join still yields flags 7, 8, 8, and 9. MySQL reports `IS_UPDATABLE=YES`,
updates the physical target once, and rejects a write that violates the
cascaded CHECK OPTION. INSERT and DELETE retain the joined-view refusal.

Fsdb's focused regression matches the read rows, metadata, matched and
unmatched UPDATE counts, and atomic CHECK OPTION rejection. No production
change was needed for this shape. Dependent security layers, outer joins,
and broader set-operation compositions remain open.

```sh
uv run --with pymysql python torture/scripts/composed-join-view-oracle.py
just test --filter-test-case 'dependent lateral UNION source keeps a joined target writable'
```
