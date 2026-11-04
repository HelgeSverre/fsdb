# Writable joined view beside a UNION DISTINCT read source

Native MySQL 8.4.11 allows a read-only `UNION DISTINCT` derived table or CTE
to precede a writable physical table in an inner joined view. The maintained
[composed-view oracle](../scripts/composed-join-view-oracle.py) now includes
both shapes in its write contract: writable metadata, one physical UPDATE,
atomic `CHECK OPTION` rejection, and the same INSERT and DELETE refusal as
the audited `UNION ALL` view shapes.

An `ORDER BY flag DESC LIMIT 2` inside either UNION source also keeps the
outer view writable. On the 7, 8, 9 flag fixture, both MySQL and fsdb expose
only 8 and 9, update the physical target once, and reject `n=8` through the
cascaded check. The maintained native oracle and focused fsdb regression cover
both ordered derived and ordered CTE forms.

A separate duplicate-row case inserts `(1,7),(1,7),(1,8)` in the left source
and `(1,9)` in the right source. Both engines expose flags 7, 8, and 9 once,
update the physical target once, and reject `n=7` because a joined row fails
the cascaded check. The focused fsdb regression covers both derived and CTE
forms. No production change was needed for this composition.

```sh
uv run --with pymysql python torture/scripts/composed-join-view-oracle.py
just test --filter-test-case 'UNION DISTINCT read sources keep a joined physical target writable'
```

Dependent branches, broader security layers, and further set-operation
compositions remain open.
