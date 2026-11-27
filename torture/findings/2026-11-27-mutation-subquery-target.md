# Updating or deleting a table read by a subquery

MySQL 8.4.11 checks the physical table assigned by an UPDATE or targeted
by a DELETE against reads in the statement's expression subqueries. With
the same base table or a second reference to the same stored view, this
returns 1093. When a different view participates, error 1443 reports the
exposed view or table aliases. An unwritten join component can supply
rows to the subquery. These checks run before row selection, so a constant
false predicate still reports a conflict.
[MySQL duplicate-table diagnostics](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_base.cc#L2117-L2162)

```sql
CREATE TABLE records (id INT PRIMARY KEY, n INT);
INSERT INTO records VALUES (1, 10);
CREATE VIEW editable AS SELECT id, n FROM records;

UPDATE editable v SET n = 20
WHERE id IN (SELECT id FROM records); -- 1443
UPDATE editable v SET n = 20
WHERE id IN (SELECT id FROM editable); -- 1093
UPDATE editable v SET n = 20
WHERE id IN (SELECT id FROM (SELECT id FROM records) AS copy);
```

The third statement reads an ordinary derived table, which MySQL
materializes in this position. A TEMPTABLE view, a structurally
nonmergeable derived table, and an audited NO_MERGE hint also avoid the
conflict. An explicit MERGE algorithm on a view takes precedence over
NO_MERGE. A MERGE hint on an eligible derived table exposes the conflict
again. A CTE shadows an unqualified table reference, while a
schema-qualified reference still reaches the physical table.
User-variable assignments can prevent a view read from merging. A
set-operation expression still checks each branch for direct reads.
[MySQL derived-table merge rules](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_resolver.cc#L179-L195)

Both fsdb storage modes were checked against MySQL for error codes,
diagnostic names, affected rows, and post-statement table contents.
The checked cases included qualified and correlated reads, join
assignments to either physical component, and UPDATE and DELETE through
layered views. Binary PREPARE and EXPLAIN also reported the conflicts.
A competing transaction held the candidate row while the rejected
statement returned without waiting, and the holder's commit remained
visible afterward. For an audited view whose definer lacked UPDATE,
both servers reported the grant denial before the subquery conflict.
Fsdb retained its existing 1142 diagnostic where MySQL returns 1356.

The guard covers expression subqueries written in the mutation.
Subqueries inside saved view definitions have separate behavior. For
example, MySQL rejects an UPDATE joined to a read-side view whose own
predicate reads the target table, and fsdb still accepts the audited
form. Same-table writes through multiple aliases also have separate
affected-row and update-order differences.
