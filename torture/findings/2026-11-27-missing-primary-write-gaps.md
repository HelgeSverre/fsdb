# Gap locks after missing primary-key writes

For an exact seek on a complete integer primary key, a missing UPDATE
or DELETE in MySQL 8.4.11 protects the gap between neighboring records
under REPEATABLE READ and SERIALIZABLE. Two missing seeks in the same
gap can both return zero affected rows. An insert or a move into that
gap waits for both holders. READ COMMITTED and READ UNCOMMITTED allow
the neighboring insert in the audited cases.
[MySQL InnoDB gap locks](https://dev.mysql.com/doc/refman/8.4/en/innodb-locking.html#innodb-gap-locks)

To reproduce, start with `t(id INT PRIMARY KEY, n INT)` containing keys
1 and 9. In session A, start a repeatable-read transaction and run
`UPDATE t SET n=n+1 WHERE id=4`. In session B, repeat the UPDATE in a
transaction. Both report zero. In session C, set
`innodb_lock_wait_timeout=1` and try an INSERT at key 5. It times out
until both transactions have ended. An insert at key 10 can proceed.

Fsdb reuses its integer primary-key read gap logic for the refreshed
write view. The gap is installed before releasing the exact key probe.
Write searches can share a gap. Inserts and primary-key moves still
check for conflicting gap holders. Rolling back to a savepoint set after
the first table access keeps the write's gap in the audited case. Rolling
back to a savepoint set before the first table access releases it.

Twenty-four differential schedules matched in memory and on disk. They
covered a descending composite key, UPDATE, DELETE through a mergeable
view, all four isolation levels, and both savepoint states. Two further
schedules confirmed that a pending insert or move rolling back leaves the
waiting writer's gap protected. The destination-move, point-hit, and
`LIMIT 0` controls also matched.

The fixture resets the index before each schedule to isolate the boundary
records. Secondary-key gaps, noninteger primary keys, broader write ranges,
and boundaries influenced by delete-marked records remain outside this audit.
