# Missing-function database resolution in expression statements

MySQL 8.4.11 resolves an unqualified stored-function call against the selected
database before executing `INSERT`, `REPLACE`, `UPDATE`, `DELETE`, `DO`, or
`SET`. With a selected database, an absent function returns 1305 (`42000`)
and names `database.function`. Without one, the same call returns 1046
(`3D000`), even when a mutation names its target table with a database prefix.

Fsdb now normalizes missing-function errors at the query boundary for these
expression-bearing statements. `SET` uses its text handler, so its recognized
syntax has a narrow fallback when the SQL parser does not produce an AST.
Focused Expecto regressions cover selected and absent database contexts. The
`missing-function-mutations` contract in run `20261010T125244543-74837`
matched MySQL on all eight selected-database statements. The complete run
retained only the nine known identifier-case differences.

This audit covers these text-protocol forms. Other expression-bearing DDL and
stored-program contexts remain outside its scope.
