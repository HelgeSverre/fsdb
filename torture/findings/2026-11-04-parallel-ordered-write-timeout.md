# Parallel-suite timeout in an ordered text DELETE

On the local macOS runner, the default parallel Expecto run twice timed out
in `keyless filesort write waits for pending secondary-key inserts` after the
owner transaction committed. Increasing the waiter deadline from five to
fifteen seconds did not resolve the failure. The case-specific diagnostic
identified this statement:

```sql
DELETE FROM tx_keyless_filesort
WHERE rank_value>'A000'
ORDER BY CONCAT(rank_value,'') LIMIT 2
```

The pending insert has rank `'a000'` under the folded
`utf8mb4_0900_ai_ci` collation. The owner commits before the waiter timeout.
A sample of the still-running parallel test process showed about 19 GB of
physical memory use. The same focused test passed, and the full sequenced
suite passed 3,869/3,869 tests, including this case. The tagged NuGet
workflow also runs the suite sequenced.

The evidence does not yet distinguish a lock scheduling defect from resource
starvation in the parallel test process. The test now reports the exact SQL,
pending value, and time zones on failure. `just check` uses the same
sequenced mode as CI so the documented release gate remains reproducible.
A controlled parallel-load reproduction and managed stack capture would be
needed before changing production lock behavior.
