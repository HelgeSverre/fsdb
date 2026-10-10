# Logarithm functional keys

Pinned MySQL 8.4.11 accepts functional indexes on unary `LOG`, `LN`, `LOG2`,
and `LOG10` over numeric and text columns. The first two compute natural
logarithms, but their index expressions are distinct: an index on `LOG(n)`
does not serve a predicate on `LN(n)`. Each expression produces a double key.
The audited equality and selective range predicates use their matching keys,
as does grouping by `LOG(n)`. Indexed zero and negative inputs fail with error
3020 / SQLSTATE 2201E; `NULL` inputs remain nullable keys.

Expecto covers key identity and selection, uniqueness, updates, and WAL and
snapshot recovery. The differential contract compares MySQL and fsdb results,
numeric result types, errors, and subsequent table state.

The `logarithm-functional-indexes` contract passed all 14 steps in the pinned
MySQL 8.4.11 run `20261010T144027930-90388`. The full contracts run retained
the same nine identifier-case differences outside this contract.
