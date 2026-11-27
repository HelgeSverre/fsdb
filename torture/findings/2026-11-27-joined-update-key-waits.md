# Unique-key waits in wrapped autocommit UPDATE

Fsdb runs joined autocommit updates inside a statement-sized
transaction. They now refresh the catalog after waiting for a unique
key, using the same mechanism as an explicit transaction. A joined
update hands the refreshed snapshot back to the surrounding transaction
for a single publication.

Create two permanent tables before starting either session.

```sql
CREATE TABLE target(id INT PRIMARY KEY,k INT UNIQUE,qty INT);
CREATE TABLE selected(id INT PRIMARY KEY);
INSERT INTO target VALUES(1,1,5),(3,5,5);
INSERT INTO selected VALUES(1),(3);
```

Run these commands in session A and leave its transaction open.

```sql
START TRANSACTION;
INSERT INTO target VALUES(9,2,7);
```

Start the update in session B before committing or rolling back A.

```sql
UPDATE IGNORE target a JOIN selected s ON s.id=a.id
SET a.k=CASE a.id WHEN 1 THEN 2 ELSE 6 END,a.qty=a.qty+1;
SHOW WARNINGS;
```

If A commits, both engines warn about duplicate key 2 and change
only row 3. Ordinary UPDATE returns 1062 and leaves both updates
unapplied. If A rolls back, both updates succeed in either form.

The audit matched 28 schedules against MySQL 8.4.11 in memory and
on disk. It covered autocommit, explicit transactions, more than
one writable target, a cross-database foreign key, a CTE source,
and a correlated EXISTS filter.

## Session-local join sources

For the same race, create and populate `selected` as a temporary
table in session B. Before the fix, the competing insert could lead to
1205 in Fsdb. The temporary overlay now shares the outer transaction's
catalog refresh for permanent writes. It publishes permanent changes
back to that transaction and retains temporary changes in session B.

Twenty additional schedules matched MySQL 8.4.11 in memory and on disk.
They covered ordinary UPDATE, UPDATE IGNORE, a cross-database foreign
key, and a statement that updated both a permanent and a temporary
target. The temporary source shadowed a permanent table with different
rows, and the permanent table's contents stayed unchanged. A waiter
rollback and a rollback to a savepoint both preserved the competing
transaction's commit.
