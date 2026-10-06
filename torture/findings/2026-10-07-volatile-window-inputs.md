# Materialized arguments in window aggregates

MySQL 8.4.11 evaluates each window aggregate argument once per input row,
including rows that occur in no output frame. Frame reads reuse those values.
SUM/AVG numeric conversion remains a separate step: a sliding frame can warn
again when it converts the same materialized string.

A stored counter function returning successive integers over three inputs
produces a whole-partition SUM of 6 for every row. A one-preceding/current-row
SUM produces 1, 3, 5, with three function calls. A one-following/two-following
frame produces 5, 3, NULL, still with three calls. Turning each counter value
into a malformed numeric string produces five conversion warnings for the
sliding frame, while the argument itself is evaluated only three times.

The shared aggregate fold accepts an argument evaluator. Grouped aggregates
read expressions through their row contexts; window aggregates supply cached
values by row and argument position. Positions distinguish repeated expressions
in multi-argument aggregates. Existing enum conversion and collation rules
continue using the original expression and row context. Growing SUM/AVG frames
reuse the numeric accumulator over the materialized inputs.

Stored function invocation also transfers user-variable values between the
caller and routine session. Each invocation sees preceding assignments, and
routine changes remain visible after return or an error. Raised conditions retain
their SQLSTATE across the expression boundary. A separate parser
limitation remains: standalone SET of a user variable is rejected in stored
function bodies; the counter probe uses an assignment expression in RETURN.

Multiple volatile aggregates can interleave their calls differently between
engines. MySQL explicitly leaves [user-variable expression evaluation order
undefined](https://dev.mysql.com/doc/refman/8.4/en/user-variables.html), so no
particular interleaving is treated as a guaranteed compatibility contract.

`torture/scripts/volatile-window-oracle.fsx` checks values, warning rows, and
call counts against native MySQL 8.4.11 through text and prepared protocols.
The parser accepts the two-argument JSON_OBJECTAGG window form.
The `volatile-window-inputs` differential contract also covers COUNT, MIN/MAX,
JSON, bitwise and statistical aggregates, nested calls, error state, and
statement atomicity after failed UPDATE and INSERT operations.

Validation: `DOTNET_PROCESSOR_COUNT=8 just check` builds without warnings and
passes all 2,838 tests. A four-worker full run hit the recurring event-scheduler
timing assertion; that test passed in isolation, and the eight-worker full run
passed without changing timeouts, assertions, or exclusions.

Native MySQL 8.4.11 passes the volatile-window and prepared-type oracle scripts.
The final differential run passes 3,914 steps across 42 cases with no differences;
its manifest is
`torture/artifacts/runs/20261006T221927180-11365/contracts/manifest.json`.
The disposable native server is shut down after validation.
