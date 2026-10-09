# TIME arithmetic overflow warnings

MySQL 8.4.11 clamps out-of-range TIME arithmetic to `±838:59:59` and emits
warning 1292 with the overflowing result. `ADDTIME` and `SUBTIME` render the
warning value at whole-second precision: adding one microsecond to
`838:59:59` warns with `'838:59:59'`, while adding a whole second warns with
`'839:00:00'`. `TIMEDIFF` retains fractional argument precision, so its
one-microsecond overflow warns with `'838:59:59.000001'`.

fsdb already clamped these results but omitted the warnings. A shared
unbounded TIME formatter now supplies their warning values without changing
the clamped `TimeValue` representation. The focused Expecto regression failed
before the change and passes after it. `just check` passed all 3,227 tests.
The four new `function-family-contracts` wire steps compare result values and
`SHOW WARNINGS` against MySQL and pass. The full contract run retains the same
nine identifier-case differences as the preceding run.

## Invalid operands

MySQL also clamps a TIME operand before applying arithmetic. For example,
`SUBTIME('839:00:00','00:00:01')` and
`TIMEDIFF('839:00:00','00:00:01')` both return `838:59:58`, with one warning
for the invalid left operand. fsdb previously carried a one-microsecond
out-of-range sentinel into `SUBTIME`, returning `838:59:58.000001` without a
warning. TIME-consuming scalar functions now use the same rounded, clamped,
warning-producing parser as `TIME()`. The focused regression failed before
this change and passes after it. Six new wire steps cover invalid left and
right operands, result values, and warning order; all pass against MySQL 8.4.11.
The final `just check` run passed all 3,228 tests, and the nine known
identifier-case contract differences are unchanged.

A fresh disposable MySQL 8.4.11 probe confirmed that
`ADDTIME('00:00:01','839:00:00')` emits two identical 1292 warnings: one for
the invalid interval operand and one for the overflowing result. fsdb's two
warnings match. The same probe confirmed two warnings when the invalid operand
is on the left, three when both operands are invalid and their sum overflows,
and distinct operand/result warnings for the corresponding SUBTIME and
TIMEDIFF cases. The earlier claim of one warning was incorrect; there is no
known warning-count gap in these cases.
