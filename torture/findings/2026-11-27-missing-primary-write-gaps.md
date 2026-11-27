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

## Decimal and binary primary keys

Missing exact writes also protect gaps in complete primary keys made of
integer, `DECIMAL`, `BINARY`, and `VARBINARY` columns. Fsdb decodes the
values in the lock key and uses the stored index order. Decimal values
retain their fractional digits above `2^53`, and binary values retain
their bytes, including an empty key.

The comparison ran six key fixtures at each isolation level, in memory
and with WAL enabled. Both engines let UPDATE and DELETE through a
mergeable view share the gap. Inserts and key moves inside it waited
under REPEATABLE READ and SERIALIZABLE. Inserts outside the gap could
proceed. Lower isolation levels let the neighboring insert proceed.
Two further schedules checked that a pending insert could roll back,
after which the waiting UPDATE missed and protected the refreshed gap.

For a decimal reproduction, substitute `id DECIMAL(20,4) PRIMARY KEY`
in the setup above and use `1.2500` and `9.7500` as boundary keys.
Seek `4.1250` and try to insert `5.1250`. For binary, use a
`VARBINARY(4)` primary key and substitute `X'10'`, `X'90'`,
`X'40'`, and `X'50'`, respectively.

The fixtures reset the index to isolate the boundary records. Text and
other primary-key types, secondary-key gaps, broader write ranges,
and boundaries influenced by delete-marked records remain open.
For the text case, MySQL orders equivalent spellings by the following
key part. With `PRIMARY KEY(k ASC, seq DESC)` and an accent-insensitive
`k`, a miss at `('E',30)` between `('é',50)` and `('e',10)`
blocks an insert at `('É',20)` while allowing `('ê',60)`. Fsdb
currently allows both inserts.
