# SELECT maximum execution time

Status: open. Native MySQL 8.4.11 is the oracle. The maintained
`select-timeout-oracle.py` uses a disposable native server with 64 MiB buffer
pool and redo capacity.

At `dcd30a06`, fsdb rejects reads and assignments of `max_execution_time`
with 1193/HY000. A two-row SELECT containing SLEEP completes normally;
there is no session SELECT deadline.

## Native contract

- Session and global values initially read as zero, disabling the timeout.
- Nonnegative integer assignments are accepted, including UINT64_MAX.
  Negative integers clamp to zero with warning 1292. Decimal, string, and
  NULL assignments fail with 1232/42000.
- With a one-millisecond setting, a two-row table SELECT containing
  SLEEP(0.1) fails with 3024/HY000: `Query execution was interrupted,
  maximum statement execution time exceeded`. A joined COUNT with SLEEP
  in its ON condition has the same error.
- SQL PREPARE does not freeze the setting: changing it before EXECUTE
  affects execution.
- UPDATE containing SLEEP remains successful under the same setting.
- Source-free SLEEP returns 1 on interruption, including when another
  constant is projected. BENCHMARK returns 0. A MAX_EXECUTION_TIME(1)
  optimizer hint interrupts the maintained BENCHMARK case with that same
  result; this result alone does not establish its timing.

The fixture asserts results and errors, not exact elapsed times. Expensive
function inputs make the table-backed cancellation cases deterministic without
requiring a tight wall-clock performance threshold.

## Implementation constraints

The existing query cancellation token serves socket disconnect and KILL.
Its exception path can abort a transaction. A SELECT deadline must retain its
own reason and statement scope rather than reuse that error path blindly.
Existing BENCHMARK work ceilings are separate resource limits and must not
be presented as MySQL's 3024 timeout.

Before closing this gap, extend native controls to transaction survival,
GLOBAL inheritance and DEFAULT, stored functions and routines, hint precedence,
and prepared protocol execution. Preserve the special scalar interruption
results without allowing a table-backed query to return partial success.
