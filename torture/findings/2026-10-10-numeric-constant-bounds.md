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

The `numeric-constant-index-bounds` contract in run
`20261010T114104362-62206` matched MySQL 8.4.11 on all ten query steps.
The complete run retained the same nine documented
identifier-case differences elsewhere.

## Remaining diagnostic difference

For `id=MOD(5,0)`, MySQL returns no rows and emits two division-by-zero
warnings when `id` is indexed, even when the table is empty. Without an index,
it emits one warning regardless of row count; projecting `MOD(5,0)` emits one
warning per projected row. Fsdb returns the same empty result, but its indexed
constant probe currently emits one warning. This requires a broader treatment
of constant-predicate warning lifetime than the index-bound change.
