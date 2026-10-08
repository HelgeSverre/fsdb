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

Before closing this gap, verify deadline execution through the prepared
protocol and cover timer overflow and data-changing stored functions. Preserve the special scalar interruption
results without allowing a table-backed query to return partial success.

## Settings and lifetime coverage

Session/global reads, assignments, unsigned range, clamp warnings, DEFAULT,
and new-session inheritance are implemented through the shared bounded-integer
setting rules. The settings regression passes with the full 3,086-test suite.
Wire validation passes 64 cases and 8,365 steps with zero differences:
`torture/artifacts/runs/20261008T080034689-59636/contracts`.
Execution deadlines and MAX_EXECUTION_TIME hints remain unimplemented.

The expanded native fixture verifies transaction and savepoint survival after
3024, procedure-body SELECT exemption, and timeout of an outer table-backed
SELECT calling a stored function. A positive hint overrides the session value;
a zero hint falls back to it. A source-free UNION still raises 3024, while a
constant derived source containing SLEEP returns its interrupted scalar value.
These distinctions remain requirements for deadline execution.

UINT64_MAX assignment is accepted, but a repeat native run interrupted its
readback with 3024. Settings-only readbacks use a 10,000 ms hint to separate
unsigned assignment validation from native timer overflow behavior. The latter
needs dedicated controls before choosing how to schedule very large deadlines.
