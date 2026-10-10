# BIT_COUNT functional indexes

MySQL 8.4.11 creates and uses a physical `BIT_COUNT(column)` index for integer and text columns. An integer table with values 7, 8, and 3 returns row 1 for `BIT_COUNT(v)=3`; `EXPLAIN` reports `ref` access through the functional key. A text column behaves the same way. In strict mode, inserting a text value such as `12x` fails with error 3751; in permissive mode it stores a projected count of 2 and reports `Data truncated for functional index ...`.

Fsdb previously retained the index declaration but scanned these predicates. The indexed transform now shares the scalar function's bit-count calculation: binary strings count every byte, exact integers preserve their 64-bit pattern, numeric decimals and doubles round by their respective MySQL rules, and text truncates on its integer-cast path. Physical lookup, range, mutation, and conversion behavior are covered by the `bit-count-functional-index` differential contract and a focused Expecto regression.

This closes one supported expression family, not arbitrary functional expressions. The broader expression-index gap remains open.
