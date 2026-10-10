# Numeric constant bounds on integer keys

Pinned MySQL 8.4.11 folds `MOD`, `TRUNCATE`, `POW`, and `POWER` over literal
arguments into integer-key equality and range access. `EXPLAIN` reports
`PRIMARY`/`const` for `id=MOD(5,3)`, `id=TRUNCATE(2.9,0)`, and
`id=POW(2,2)` in the audited table. An integral decimal probe such as `2.0`
also uses the integer key. A fractional decimal probe such as `2.5` matches no
integer row.

Fsdb now admits those original builtins to its planner constant evaluator and
normalizes a decimal probe to an integer key only when it has no fractional
part and the coerced integer has exactly the same value. An overridden builtin
still cannot claim the stored key. The focused Expecto regression checks
signed `MOD`, negative `TRUNCATE` precision, both power names, integral and
fractional decimals, and an ordered range bound.
The native and Expecto probes also cover an exact `BIGINT` decimal bound above
the double-precision integer limit (`9007199254740993.0`).

MySQL 8.4.11 also chooses `PRIMARY`/`const` for `id=BIT_COUNT(7)` and
`id=CRC32('abc')`. Fsdb now folds these unmodified deterministic builtins into
numeric index bounds. Focused tests cover matching rows, plan keys, and
UPDATE/DELETE effects; the differential contract checks the same final data.
The text-coercion result and both conversion warnings for indexed
`BIT_COUNT('12x')` now match MySQL.
Empty and whitespace-only text arguments also return zero with MySQL's
truncated-INTEGER warning.
For `BIT_COUNT`, MySQL interprets `_binary` strings and `CAST(... AS BINARY)`
as byte sequences, counting bits across their full length without a numeric
conversion warning. Numeric bit/hex literals retain their numeric origin;
ones wider than 64 bits yield zero with a truncated-BINARY warning. Fsdb now
keeps these two paths distinct.

The expanded `numeric-constant-index-bounds` contract in run
`20261010T160547990-6651` matched MySQL 8.4.11 on all 50 steps.
The complete run retained the same nine documented identifier-case
differences elsewhere.

## Division warning lifetime

For `id=MOD(5,0)`, MySQL returns no rows and emits two division-by-zero
warnings when `id` is indexed, even when the table is empty. Without an index,
it emits one warning regardless of row count; projecting `MOD(5,0)` emits one
warning per projected row. Fsdb now follows those counts for `MOD(5,0)`,
`1/0`, and nested `TRUNCATE(1/0,0)` bounds. The index planner evaluates the
division bound once when a B-tree lookup is available; the prepared comparison
evaluates it once more, including for an empty indexed table. The audited
unindexed table evaluates the predicate only once.

The audited Boolean branches now also match. A diagnostic-free literal `0`
in `AND` or `1` in `OR` eliminates the audited column-versus-closed-division
comparison without evaluating its bound. A missed indexed key before `AND`
retains one planner warning; an indexed `OR` lookup retains two, while its
scan counterpart emits one. Closed warning-producing operands before the
decisive literal still run:
`'x' AND 0` emits its conversion warning. Deeper conditional and mixed
expression shapes remain outside this audit.
