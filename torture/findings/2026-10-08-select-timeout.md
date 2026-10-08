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

Before closing this gap, cover hint validation and precedence, native timer
overflow, and broader scalar and routine timeout boundaries. Preserve the special scalar interruption
results without allowing a table-backed query to return partial success.

## Settings and lifetime coverage

Session/global reads, assignments, unsigned range, clamp warnings, DEFAULT,
and new-session inheritance are implemented through the shared bounded-integer
setting rules. The settings regression passes with the full 3,086-test suite.
Wire validation passes 64 cases and 8,365 steps with zero differences:
`torture/artifacts/runs/20261008T080034689-59636/contracts`.
The settings-only checkpoint precedes execution deadline support described below.

The expanded native fixture verifies transaction and savepoint survival after
3024, procedure-body SELECT exemption, and timeout of an outer table-backed
SELECT calling a stored function. A positive hint overrides the session value;
a zero hint falls back to it. A source-free UNION still raises 3024, while a
constant derived source containing SLEEP returns its interrupted scalar value.
The audited session-deadline distinctions are covered by the implementation
and regressions below; hint precedence remains open.

UINT64_MAX assignment is accepted, but a repeat native run interrupted its
readback with 3024. Settings-only readbacks use a 10,000 ms hint to separate
unsigned assignment validation from native timer overflow behavior. The latter
needs dedicated controls before choosing how to schedule very large deadlines.

## Session deadline execution

Session deadlines use a monotonic clock and a distinct interruption condition.
Engine row pipelines and stored-program loops share deadline and cancellation
checks. Deadline errors are converted to 3024 without taking the disconnect or
KILL transaction-abort path. SLEEP measures actual elapsed time instead of
assuming sub-millisecond waits consumed their requested duration.

Native controls and regressions cover physical and temporary table reads,
scalar SLEEP/BENCHMARK, UNION, transaction/savepoint preservation, procedure-body
exemption, and timer disabling when a stored function writes data. The latter
emits note 3025. Binary prepared execution observes the current session setting.

Validation: all 3,088 tests pass. The wire suite passes 65 cases and 8,381 steps
with zero differences against MySQL 8.4.11, including prepared timeouts and
transaction preservation:
`torture/artifacts/runs/20261008T081631631-99012/contracts`.

Remaining: MAX_EXECUTION_TIME hint parsing, validation and precedence; native
overflow behavior for extreme unsigned durations; broader scalar-only and
stored-routine combinations. Deadline checks are cooperative, so operations
without an internal polling point are observed at the next engine check.

## Timeout hint diagnostics

`select-timeout-hints-oracle.py` pins native warning behavior. Signed, decimal,
quoted, NULL, empty, and multiple-argument spellings warn with 1064 and retain
successful query execution. Syntax diagnostics identify the offending source
suffix and line. The first accepted hint wins; later duplicates warn with 3126,
including when the first value is zero. Nested SELECT and UPDATE hints warn
with 3125, while misplaced hint comments are ignored. Prepared statements emit
these warnings at preparation and do not repeat them on EXECUTE.

The native fixture accepts 4,294,967,295 and warns for 4,294,967,296. An exploratory
UINT64_MAX hint interrupted its own scalar readback; that unstable overflow case
is excluded from the deterministic diagnostic fixture and remains open.

The existing comment scanner exposes hint body offsets, owning and statement
keywords, and parenthesis depth. Its body-only interface retains existing
behavior. The scanner regression covers nesting, quoted parentheses, misplaced
comments, and statement batches. All 3,089 tests pass, and the existing wire
suite remains at 65 cases / 8,381 steps / zero differences:
`torture/artifacts/runs/20261008T082458417-8827/contracts`.
Timeout-hint interpretation and preparation-lifetime diagnostics remain open.
