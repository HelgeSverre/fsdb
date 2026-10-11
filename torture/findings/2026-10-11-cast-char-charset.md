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
the audited value, metadata, and warning cases.

The `binary` charset uses byte lengths. MySQL keeps raw `0xE9` as `E9`,
encodes UTF-8 text `é` as `C3A9`, and truncates `CAST('éx' AS CHAR(1)
CHARACTER SET binary)` to byte `C3`. That truncation emits warning 1292
for `BINARY(1)` with the original source bytes in the message. Its result
metadata is variable-string with the binary flag and audited widths of one,
four, and one byte for those three forms. Fsdb now uses the same cast node
for binary targets and follows these value, warning, and metadata cases.

MySQL rejects an unknown target charset with error 1115 and SQLSTATE 42000
before evaluating either `CAST` or `CONVERT ... USING`. The error occurs for
NULL input and an unchosen `IF` branch too. Fsdb now validates the literal
charset while parsing these forms; the focused regression and differential
contract cover the early failure.

MySQL also accepts `CONVERT(expr, type)` as a typed conversion. For the
audited `CHAR` targets, an optional width and `CHARACTER SET` clause have the
same values as `CAST(expr AS CHAR ... CHARACTER SET ...)`; `SIGNED` likewise
uses the existing numeric cast. Fsdb now parses both forms through the same
target grammar and cast expression. Text and prepared differential checks
cover character limits, charset identity, binary-byte limits through `CHAR(1)
CHARACTER SET binary`, and unknown-charset rejection. Explicit `BINARY(N)`
casts still need a separate width audit.
Other CAST charset contexts remain open.
