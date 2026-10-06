# User-variable SET in stored programs

MySQL 8.4.11 accepts standalone user-variable SET in stored functions. The
[oracle script](../scripts/stored-function-set-oracle.fsx) checks local-variable
lookup, assignment visibility, handler continuation, and loop bodies.

For `@n=10`, the statement `SET @n=@n+2,@label=CONCAT('count,',@n)` leaves
`@n=12` and `@label='count,10'` at top level. Inside a stored program it leaves
`@label='count,12'`: routine assignments execute individually. If the middle
assignment raises SQLSTATE 45000 and a CONTINUE handler handles it, execution
resumes with the following assignment. Earlier user-variable assignments remain
visible after an unhandled function error as well.

The routine AST retains parsed user-variable targets and expressions. Expression
traversal includes their RHS values for privilege and restricted-function
validation. A shared execution cursor exposes one SET assignment at a time to
both routine and trigger executors, preserving handler continuation without
changing top-level SET publication. Typed trigger assignments use the same
session assignment executor as routine and top-level commands.

The wire batch splitter also needs to retain complete loop bodies. Counting
BEGIN and END alone incorrectly splits a function at END WHILE. WHILE, LOOP,
and REPEAT nesting, labeled loops, and DO boundaries retain the full definition;
scalar IF calls in standalone DO statements do not add compound nesting.

The `stored-function-set` contract covers text and prepared function execution,
locals, nested calls, failed calls, handlers, loops, window inputs, single-body
procedures, quoted targets, and trigger row values. The existing
`volatile-window-inputs` contract retains RETURN assignment-expression coverage.

Validation against native MySQL 8.4.11 passes all 3,951 steps in 43 contract
cases, with zero differences. The new contract's 37 steps include the distinct
top-level evaluation boundary and preserved state after SQLSTATE 45000. The
[manifest](../artifacts/runs/20261006T224952040-16003/contracts/manifest.json)
records the dirty source revision and assembly hash. The standalone oracle and
prepared-type oracle also pass. Disposable native server data is removed after
the run.

The final `just check` build has no warnings or errors and all 2,843 tests pass.
Compilation uses four processors; the final up-to-date gate uses eight.
