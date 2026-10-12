# CHAR() byte assembly

MySQL 8.4.11 returns a binary string from `CHAR(n1, ...)`. Each non-NULL
integer argument contributes its shortest big-endian byte sequence, with at
least one byte for zero. Audited examples are `CHAR(128)` → `80`,
`CHAR(256)` → `0100`, `CHAR(4660)` → `1234`, and `CHAR(-1)` → `FFFFFFFF`.
`CHAR(4294967296)` wraps to `00`; a half value such as `CHAR(1.5)` rounds to
`02`. NULL arguments contribute no bytes.

Fsdb previously made Unicode characters and encoded them as UTF-8, so
`HEX(CHAR(128))` returned `C280`. It now constructs binary bytes directly.
The focused regression covers values and a downstream TIME warning; the
MySQL differential contract compares byte assembly, direct returned values,
and escaped high-byte diagnostics through binary `CONCAT`.

MySQL parses text arguments as integer prefixes, distinct from numeric
arguments: `CHAR('1.5')` contributes `01` and warning 1292, while numeric
`CHAR(1.5)` contributes `02` without a warning. Invalid text contributes
`00` with warning 1292; trailing text warns, but surrounding spaces do not.
Warnings follow argument order. Audited positive text overflow clamps to
unsigned 64-bit maximum, negative overflow to signed 64-bit minimum, each
with one warning before 32-bit byte assembly. Fsdb now matches those values
and conditions in focused and differential tests.

`CHAR(... USING charset)` interprets the assembled bytes in that charset.
For valid UTF-8, `CHAR(0xC3,0xA9 USING utf8mb4)` retains bytes `C3A9`,
reports `utf8mb4_0900_ai_ci`, has one character, and has coercibility 4.
Invalid ASCII byte `80` survives with warning 1300; incomplete UTF-8 byte
`C3` yields NULL with warning 1300. The focused regression and native
differential contract cover those cases, quoted charset names, multiple
arguments, and NULL. The implementation shares `CONVERT`'s byte decoding but
retains CHAR's different coercibility.

Computed byte strings use the same integer-prefix values but keep binary
warning previews: `CHAR(UNHEX('8031'))` contributes `00` and warns with
`'\\x801'`, while `CHAR(UNHEX('31FF'))` contributes `01` and warns with
`'1\\xFF'`. A binary NUL appears as `\\x00` in the warning; decoded text
stops its preview at NUL. The focused regression and native differential
contract cover the values and warning order alongside a decoded `latin1`
source. Numeric CAST and CHAR share the binary-preview renderer.

A 64-bit BIT argument must not pass through `double`: its low 32 bits supply
the word even when all 64 bits are set. MySQL also clamps positive floating
values beyond the unsigned 64-bit range to `FFFFFFFF`; exact DECIMAL values
outside unsigned or signed 64-bit bounds clamp to `FFFFFFFF` or `00` with
warning 1292. Focused and differential cases cover those boundaries and
ordinary DATE, TIME, and DATETIME numeric arguments.

Stored DOUBLE overflow also has a source-dependent warning: a `1e30`
literal yields `FFFFFFFF` quietly, while `CHAR(amount)` over a DOUBLE column
with that value yields the same bytes and warning 1292. A negative stored
overflow yields `00` and the same warning kind. Focused and differential
tests cover both sources and warning order.

JSON arguments have a separate integer cast path. Audited JSON numbers round
ties to even, JSON strings truncate an integer prefix and warn 3156 for a
fraction or trailing junk, booleans become one or zero, and JSON null,
arrays, and objects contribute zero with warning 3156. The audited
64-bit-boundary JSON numbers clamp with warning 3155. The warning names the
originating `cast_as_json`, `json_array`, or `json_object` expression; focused and native
differential cases cover values, codes, names, and order.

Stored JSON values and warning row numbers now match the audited seven-row
table with all rows, a `WHERE` filter, descending ordering with and without
`LIMIT`, and grouping by the row ID. The projection row number follows the
selected result order: a filtered or descending warning refers to its output
position rather than the physical table position. Other JSON expression
origins, more complex ordering and grouping shapes, and floating overflow in
other expression forms remain outside this audit.
