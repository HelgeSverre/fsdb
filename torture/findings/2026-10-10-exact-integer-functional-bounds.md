# Constant bounds for exact-integer functional keys

MySQL 8.4.11 uses physical functional keys for predicates such as `BIT_COUNT(v)=BIT_COUNT(7)`, `SIGN(v)=SIGN(-2)`, `CRC32(txt)=CRC32('abc')`, and `CHAR_LENGTH(txt)=CHAR_LENGTH('abc')`. Each `EXPLAIN` probe reported `ref` access through its corresponding key. Fsdb previously scanned these predicates except for the checksum case.

The planner now recognizes a pure constant expression on the other side of an exact-integer functional key. The same rule applies to equality and indexed `IN` probes and refuses a stored projection if the relevant built-in has been overridden in the function registry. Key result classification is shared with fixed integer key metadata rather than listing CRC32 separately in the planner.

The `exact-integer-functional-bounds` differential contract checks rows and a mutation using these bounds; the focused Expecto regression checks physical access and override safety. Broader expression folding, including non-integer functional results, remains open.

One diagnostic edge remains open: MySQL emits warning 1292 and three functional-index warnings 3751 for `BIT_COUNT(v)=BIT_COUNT('12x')` when it uses the functional key. With `IGNORE INDEX`, only 1292 remains. Fsdb currently emits 1292 but not the three index-path warnings. The count stayed three after changing the table from three to four rows, so it is not a row-by-row warning rule.
