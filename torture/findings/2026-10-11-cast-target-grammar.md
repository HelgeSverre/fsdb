# CAST and typed CONVERT targets are narrower than column declarations

MySQL 8.4.11 accepts `CHAR`/`CHARACTER`, `BINARY`, signed and unsigned
integers, `DECIMAL`, `FLOAT`, `DOUBLE`, `REAL`, `DATE`, `TIME`, `DATETIME`,
`YEAR`, and `JSON` as audited conversion targets. Its column declarations
have a different grammar. In particular, `VARCHAR`, `VARBINARY`, `TEXT`,
`BLOB`, `INT`, `BIGINT`, `BIT`, `TIMESTAMP`, and `NUMERIC` are not CAST target
spellings. A numeric `UNSIGNED` suffix, `YEAR(4)`, and the column-style
`FLOAT(M,D)` spelling also fail with syntax error 1064, SQLSTATE 42000.
The comma form of `CONVERT` uses the same target grammar.

Fsdb previously reused its column-type parser after `AS` and after the
comma in typed `CONVERT`. That admitted column-only types. An invalid typed
`CONVERT` could then backtrack into a function call and report an unrelated
missing-database or missing-function error. The cast parser now names the
accepted target forms; the comma commits `CONVERT` to that grammar.

MySQL also accepts `CHARACTER(2)` and truncates `abc` to `ab` with a warning.
Fsdb now retains bare `CHAR`/`CHARACTER` widths in the character-cast node
and evaluates them using the current connection charset. The focused test
first failed on the accepted `VARCHAR` spelling, and the differential
contract covers the audited rejections and character-width value and warning.
