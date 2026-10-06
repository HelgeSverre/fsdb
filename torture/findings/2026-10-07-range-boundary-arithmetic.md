# RANGE boundary arithmetic and diagnostic ordering

Status: fixed.

The [native oracle](../scripts/range-overflow-oracle.fsx) checks MySQL 8.4.11
through text and prepared execution, consuming every result row so a later
error cannot escape detection. Integer offsets retain signed or unsigned
arithmetic and raise 1690 / 22003 at their limits. Decimal `1.0` and approximate
`1e0` offsets promote arithmetic instead. `NO_UNSIGNED_SUBTRACTION` permits
zero minus one but rejects results above the signed BIGINT maximum. Distinct
SQL text prevents prepared-statement cache reuse across SQL modes.

A complete non-NULL unique-key equality lookup makes the source constant.
MySQL then removes stable ordering expressions, even for offset frames. A
predicate that merely returns one row, a partial composite key, or a NULL key
does not have that effect. fsdb uses its equality access plan to identify
constant sources and retains the original ordering for frame validation.
The oracle covers qualified columns, arithmetic expressions, and casts.

Boundary errors can precede numeric conversion warnings in SHOW WARNINGS.
MySQL's [numeric comparators](https://raw.githubusercontent.com/mysql/mysql-server/mysql-8.4.11/sql/item_cmpfunc.cc)
return equality when operand evaluation fails. The
[window iterator](https://raw.githubusercontent.com/mysql/mysql-server/mysql-8.4.11/sql/iterators/window_iterators.cc)
can reach the candidate's aggregate conversion before propagating that error;
a further finite-bound check can stop it first. Growing frames also check
the retained first row before adding new inputs.

Frame failures retain eligible candidate positions. The aggregate executor
uses its prior frame and consumed input positions to determine whether a
candidate conversion is reached. Statement diagnostic capture keeps those
conditions separate until completion inserts the terminal error before them.
Stored-program handlers receive the same ordered snapshot. Window argument
materialization and frame evaluation use the shared error-stopping array
traversal, preventing warnings from later output rows after a failure.

The contract harness creates the same disposable database name on both targets,
so qualified diagnostic messages can be compared directly. The
[contract manifest](../artifacts/runs/20261006T234559326-24083/contracts/manifest.json)
records 4,551 steps in 43 cases with zero differences. The matrix includes NULL
and non-NULL keys, both sort directions, bounded and unbounded frames, and
unsigned, decimal, double, date, datetime, and time ordering. Standalone RANGE,
warning-order, and volatile-window oracles pass. Nine focused RANGE tests and
eleven diagnostics tests pass, including capture isolation and suppression.
The final `just check` run after cleanup passes all 2,849 tests with no build
warnings or errors. No finding is enrolled in the known-gaps ledger.
