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
documented identifier-case differences. A focused Expecto regression also
checks unknown function codes on populated and empty tables.

The contract covers text-protocol single-table `SELECT` predicates. Other
statement shapes, nested subqueries, and combined binding errors remain
outside this audit.
