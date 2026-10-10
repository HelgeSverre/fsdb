# Binding names in eliminated WHERE branches

MySQL 8.4.11 resolves names before simplifying a Boolean predicate. Both
`0 AND missing_column` and `1 OR missing_column` return 1054, even on an
empty table. An unknown scalar function returns 1305, and an aggregate in
`WHERE` returns 1111 under the same conditions.

Fsdb now validates column and function bindings before returning the constant
result of an eliminated predicate. The check uses a probe row and does not
execute the skipped expression, preserving its warning and side-effect
boundary. The `elided-where-bindings` contract in run
`20261010T122958464-69093` matched MySQL on all six missing-column and
aggregate-error steps. The complete run retained only the nine previously
documented identifier-case differences. A follow-up contract run
`20261010T124455861-73502` matched all eight steps, including unknown-function
code 1305 and SQLSTATE `42000` on populated and empty tables. The complete run
again had only the nine known identifier-case differences. Focused Expecto
regressions check the selected database in the error message and its SQLSTATE.

The contract covers text-protocol single-table `SELECT` predicates. Other
statement shapes, nested subqueries, and combined binding errors remain
outside this audit.
