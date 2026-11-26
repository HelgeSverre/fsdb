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

## Result nullability and parameter definitions

Fsdb now clears `NOT_NULL` for direct columns from the optional side using
their joined column positions, including derived columns without a physical
origin. Both storage modes matched MySQL's rows, column names, and
`NOT_NULL` flags for 13 direct and prepared query shapes. The cases include
empty results, wildcard and named projections, merged keys, a literal
derived table, and a `COALESCE` control that stays non-null. Two binary
executions also matched MySQL's rows, NULL bitmaps, and result nullability.

Parameter inference now follows the same lateral scope as the join binder.
The binary definitions matched MySQL for signed and unsigned BIGINT markers
across grouped and consecutive joins, including an alias that shadows an
enclosing source. A VARCHAR comparison matched with `utf8mb4_general_ci`
and still reports collation 45 where MySQL reports 255 for
`utf8mb4_0900_ai_ci`. Other result flag bits and source lineage still differ
in the probe. [MySQL PREPARE rules](https://dev.mysql.com/doc/refman/8.4/en/prepare.html)
