# Readers behind a waiting writer under a global read lock

The statement-access manager prioritizes a waiting table writer over new
readers. A cross-database control exposed an exception: while `FLUSH TABLES
WITH READ LOCK` was held, writers waiting in two independent databases also
kept later readers waiting in both databases. The global lock itself forbids
those writes until release, so that writer priority cannot make progress.

A disposable native MySQL 8.4.11 control held the global read lock, started
an UPDATE that remained blocked, then started a SELECT on the same table.
The SELECT completed before the lock was released and returned the old row;
the UPDATE completed only after release. Fsdb's SELECT previously waited.

The reader gate now lets readers bypass waiting and prioritized writers
while a global read lock is active. Explicit table writers remain excluded
from global-lock acquisition, and ordinary writer priority resumes after
the global lock is released. The cross-database regression verifies that an
explicit lock in one database does not block an independent write, then
checks two blocked global-lock writers and concurrent reads in both
databases. The focused test and 95 lock-related tests pass.

This corrects the read scheduling contract. It does not remove the shared
statement-access monitor or establish a multi-database throughput gain.
