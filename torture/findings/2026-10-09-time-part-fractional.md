# TIME component functions and fractional precision

Native MySQL 8.4.11 rounds `-34:20:30.1234567` to microseconds before
evaluating HOUR, MINUTE, SECOND, and MICROSECOND. Its results are 34, 20, 30,
and 123457. Rounding `34:59:59.9999995` carries to hour 35, with zero minutes,
seconds, and microseconds. At the TIME limit, `838:59:59.9999995` clamps to
`838:59:59` and MySQL emits warning 1292.

fsdb's TIME function already rounded parsed ticks to six digits and clamped
out-of-range durations. The component functions instead passed unrounded ticks
directly to `tryTimeValue`, so a nonzero seventh digit made an otherwise valid
duration become NULL. Both paths now use one rounded-and-clamped parser. The
focused Expecto case failed before the change and passes after it. The
`function-family-contracts` wire case covers all three examples and passes
against MySQL 8.4.11.

The shared parser now also emits MySQL's warning 1292 with the original input
for each rounded value that must be clamped. Rounding into the legal range
does not warn. The focused diagnostics test failed before this change and
passes after it. The wire contract compares both the clamped result values
and the rows returned by `SHOW WARNINGS`; all three new steps pass. The full
contracts lane has the same nine identifier-case differences as the prior run.
The warning-fix `just check` run passed all 3,224 tests. An earlier run had one
load-sensitive UPDATE timing-canary failure; it passed in isolation and in the
final full run.

## Session precision mode

With `TIME_TRUNCATE_FRACTIONAL`, MySQL 8.4.11 truncates the seventh digit in
TIME(), component functions, TIME_FORMAT(), EXTRACT(), and implicit datetime
parsing used by DATE(). Without the mode it rounds, including a carry from
`2024-01-01 23:59:59.9999995` to the next date. fsdb previously rounded the
TIME paths regardless of mode and truncated implicit datetime text regardless
of mode. Statement-scoped precision now supplies one policy to those parsers.
The focused regression failed before the change and passes after it; the final
`just check` run passed all 3,225 tests. The new truncate-mode wire step passes
against MySQL 8.4.11, and the full contract run retains the same nine
identifier-case differences.

## Captured SQL mode in stored objects

MySQL evaluates temporal scalars in triggers and stored functions under the
SQL mode captured when each object was created. A trigger and function created
with `TIME_TRUNCATE_FRACTIONAL` both return `123456` from
`MICROSECOND('12:34:56.1234567')` after the caller resets its mode. fsdb's
first statement-scoped implementation used the caller's mode inside triggers,
returning `123457`. The precision scope now lives in `Temporal` and follows
temporary `Storage.withExecutionSettings` changes, which also cover stored
routines and events. The trigger regression failed before the refactor and
passes after it. The stored-function regression and the new wire result step
pass against native MySQL 8.4.11. `just check` passed all 3,230 tests, and the
full wire run retains the same nine identifier-case differences.
