# Left-hand lateral sources in right joins

In `LATERAL (SELECT a.id AS x) d RIGHT JOIN a ON d.x=a.id`, MySQL lets `d`
read the preserved table `a`. It also permits `JSON_TABLE` in that position.
If the lateral source yields no match, the result retains the row from `a`
and returns NULL for the columns from `d`. The same reference is rejected
for the plain `INNER JOIN`, `CROSS JOIN`, comma join, and `LEFT JOIN`
controls in the fixture. [MySQL lateral derived tables](https://dev.mysql.com/doc/refman/8.4/en/lateral-derived-tables.html)

Fsdb now binds the preserved operand before the lateral body and evaluates
the dependent join in that order. It restores the written source order for
rows and writable identities. `USING` and `NATURAL RIGHT JOIN` still expose
their merged keys in MySQL's order. For consecutive right joins, binding
starts with the outermost preserved operand so references to both operands
are visible in the lateral body.

The [SQL fixture](2026-11-26-left-lateral-right-join.sql) matched 95 lines of
tabular output, including headers, and six errors against pinned MySQL
8.4.11 in both Fsdb storage modes. It covers grouped operands, empty inputs,
alias shadowing, a CJK ngram full-text filter, SQL prepared execution, a
read-only view, and final table contents after joined UPDATE and DELETE.
A binary-protocol probe verified the prepared result names, order, and
integer types. Two executions with different integer bindings matched
MySQL's rows, including the NULL bitmap. Result flags still differ. Fsdb
sets the non-NULL flag on the null-extended projection at execution, and
the inferred parameter type for `a.id<>?` inside the lateral body differs.
