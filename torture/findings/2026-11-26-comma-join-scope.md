# Comma joins and lateral scope

In `FROM a, b LEFT JOIN c ON ...`, MySQL groups `b LEFT JOIN c` as one
operand of the comma join. The `ON` clause can refer to `b` and `c`. A direct
reference to `a` raises error 1054, including when the joined tables are
empty. The same rule applies to joined `UPDATE` and `DELETE`. An explicit
`(a, b) LEFT JOIN c` puts both tables in scope for `ON`. [MySQL JOIN rules](https://dev.mysql.com/doc/refman/8.4/en/join.html)

A lateral body has a different scope from the join condition. In
`FROM a, b RIGHT JOIN LATERAL (SELECT a.id AS x) d ON b.id=d.x`, the body
can read `a.id`. It raises error 1054 if it reads `b.id`, since `b` is the
left operand that the right join can NULL-extend. Fsdb previously flattened
the comma and explicit joins into one chain. That rejected the legal lateral
reference and could allow `UPDATE` or `DELETE` to modify rows when MySQL
would reject the statement. [MySQL lateral derived tables](https://dev.mysql.com/doc/refman/8.4/en/lateral-derived-tables.html)

The parser now keeps each explicit join chain within its comma operand.
The executor binds join conditions before matching rows, including when a
join has no candidate pairs. The [SQL fixture](2026-11-26-comma-join-scope.sql)
matched all 38 result rows and seven errors against the pinned MySQL 8.4.11
in both Fsdb storage modes. It covers direct queries, SQL `PREPARE`, empty
inputs, deeper lateral dependencies, a stored view, and final table contents
after accepted and rejected writes.
