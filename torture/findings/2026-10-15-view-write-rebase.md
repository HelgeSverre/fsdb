# Updatable-view writes claim their physical transaction target

MySQL 8.4.11 was probed with two connections in explicit transactions and a
third connection reading committed state. The second write was started while
the first transaction held the row and was still waiting after 200 ms.

| View and statement | Second statement after first commit | Final base row |
|---|---:|---|
| `SELECT id,n FROM t`; `UPDATE v SET n=n+1 WHERE id=1` | 1 affected | `n=2` |
| `SELECT t.id,t.n FROM t JOIN lookup l ON l.id=t.id`; same `UPDATE` | 1 affected | `n=2` |
| `SELECT id,n FROM t`; `DELETE FROM v WHERE id=1` | 0 affected | absent |

Before the change, `transactionWriteTargets` returned no physical row claim
for a view. The second FSDB update could run against its private snapshot, but
its later `COMMIT` returned 1205 instead of waiting and rebasing. Target
discovery now resolves the view's writable base target and maps its predicate
to physical candidates. The transaction lock path waits for the first writer,
rebases, and executes the statement against the latest committed row.

`TransactionTests` covers the three cases above, including the wait and final
state. A separate MySQL 8.4.11 probe found that literal INSERTs through both
a simple and a joined view wait on the base table's primary key; after the
first transaction commits, the waiter returns 1062. FSDB already matched
these two forms because physical insert execution claims the key, so they now
have a regression without an engine change. This does not establish scheduling
parity for every view: writes through derived or dependent sources remain
separate cases to audit.

A native MySQL 8.4.11 two-session probe also inserted a row through an
inner-join view using `INSERT INTO v(id,n) SELECT id,n FROM source WHERE id=1`.
The second transaction waited while the first held the physical primary key;
after the first committed, it returned duplicate-key error 1062, and the base
table retained the first row. FSDB already follows that path when executing
the selected insert rows. A focused transaction regression now pins the wait,
error, and final base row. This verifies the audited view-addressed INSERT
SELECT case, not every source query or duplicate-key update form.

MySQL 8.4.11 also waits and rebases when the target follows a read-only
`JSON_TABLE` source in the view definition. The second `UPDATE v SET n=n+1`
affects one row after the first commits, leaving the physical value at 2.
`TransactionTests` confirms the same wait, acknowledgement, and final value in
FSDB; this shape was already covered by the physical-target resolution above.
