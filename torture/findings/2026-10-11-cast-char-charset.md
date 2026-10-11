# CAST AS CHAR CHARACTER SET retains its target

The pinned MySQL 8.4.11 oracle treats `CAST(expr AS CHAR CHARACTER SET cs)`
as a charset conversion. Under a UTF-8 connection, `CAST('é' AS CHAR
CHARACTER SET latin1)` has byte `E9`, reports charset `latin1` and collation
`latin1_swedish_ci`, and still has byte `E9` after conversion to `binary`.
`CAST(0xE9 AS CHAR CHARACTER SET latin1)` also keeps `E9`; an invalid UTF-8
source byte returns NULL with warning 1300. An explicit `CHAR(1)` truncates
`'éx'` to `E9` and emits warning 1292 naming the original value.
The wire result uses variable-text metadata: in the audited UTF-8 connection,
an unbounded six-character source advertises 24 bytes, while `CHAR(1)`
advertises four bytes.

Fsdb previously parsed and discarded nonbinary charset clauses, giving these
expressions the connection charset. A dedicated cast expression now retains
the target charset and optional character limit across evaluation, text and
prepared-result metadata, SQL rendering, and persistence. The focused
regressions and `introduced-literal-encoding` differential contract cover
the audited value, metadata, and warning cases. Other CAST charset contexts
remain open.
