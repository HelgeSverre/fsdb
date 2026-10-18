# Permanent writes sourced from temporary tables under a global read lock

The digest-pinned MySQL 8.4.11 oracle distinguishes a temporary-only write
from a statement that reads a temporary table but changes a permanent one.
With `staging(n)` temporary in one session and `permanent(n)` shared:

```sql
-- Another session holds FLUSH TABLES WITH READ LOCK.
INSERT INTO staging VALUES (8);                  -- succeeds
INSERT INTO permanent SELECT n FROM staging;    -- waits for UNLOCK TABLES
```

The mixed insert completes after unlock and publishes its rows. If the lock
owner runs the mixed insert using its own temporary table, MySQL returns 1223.

Fsdb classified any statement mentioning a temporary table as a temporary
statement. Its statement-lock path removed temporary accesses correctly but
then ignored the global read lock for every remaining access, including a
permanent write. A two-session regression first showed that the mixed write
completed while the lock was held.

The temporary-statement lock path excludes a newly created temporary target
from shared-catalog accesses. It ignores the global lock only when the
remaining accesses are read-only; any remaining write obeys it. Temporary-only
writes therefore remain available. The regression covers the lock owner, the
waiting writer, the temporary-only write, and the rows published after unlock.
