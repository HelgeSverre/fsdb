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

Computed large DOUBLE arguments take a different path. MySQL raises error
1690 before byte assembly for audited arithmetic, negation beyond the signed
boundary, `ABS`, `COALESCE`, `IFNULL`, `GREATEST`, `LEAST`, `POW`, `EXP`,
`CEIL`, `FLOOR`, `ROUND`, `TRUNCATE`, and `CAST(... AS DOUBLE)` origins.
The `CEIL` diagnostic uses MySQL's `ceiling(...)` spelling, and a one-argument
`ROUND` diagnostic shows the default scale `0`. Audited `IF`, `CASE`, and `NULLIF` results pass
large values through quietly. The direct negative value exactly at the signed
boundary is also quiet, while the same value stored in a DOUBLE column warns
1292. Large DECIMAL casts and DECIMAL arithmetic retain their truncation
warnings rather than taking the DOUBLE error path. A failing Fsdb regression
exposed the initially broad computed-value check; the source-specific cases
now cover both error and pass-through paths. Other expression origins remain
open.

JSON arguments have a separate integer cast path. Audited JSON numbers round
ties to even, JSON strings truncate an integer prefix and warn 3156 for a
fraction or trailing junk, booleans become one or zero, and JSON null,
arrays, and objects contribute zero with warning 3156. The audited
64-bit-boundary JSON numbers clamp with warning 3155. The warning names the
originating `cast_as_json`, `json_array`, or `json_object` expression. A later
oracle sweep also confirmed `json_extract`, `json_set`, `json_merge_patch`, and
`json_keys` source names, bytes, codes, and warning order. Audited
`json_merge_preserve`, `json_array_append`, `json_array_insert`, `json_insert`,
`json_replace`, `json_remove`, and `json_search` origins match as well. The
`json_schema_validation_report` source also matches. Together these cover at
least one warning-producing `CHAR()` case for every registered JSON-returning
builtin. Focused and native differential cases pin down those observed forms.

Simple derived JSON columns retain their column names in conversion warnings.
An all-JSON `UNION ALL` inside a CTE previously reconciled to text, turning a
JSON string into a zero-valued `CHAR()` argument; it now retains JSON. A bare
`NULL` projection on either side of the audited UNION keeps the JSON result
type too.
Focused and native cases cover the values, warnings, and the direct UNION wire
type.

Stored views retain a distinct warning source rule. A direct view column and
an audited UNION view warn under the exposed column name `payload`, while a
view expression `JSON_EXTRACT(payload, '$') AS extracted` warns under
`json_extract`. A second view renaming `extracted` retains `json_extract`;
renaming the direct `payload` column retains `payload`. Fsdb previously used
the outer alias for these warning sources. Descending view queries also match
the warning row numbers. A nested view of `CAST(... AS JSON) AS payload` likewise
retains `payload` as the warning source after renaming it.

Stored JSON values and warning row numbers now match the audited seven-row
table with all rows, a `WHERE` filter, descending ordering with and without
`LIMIT`, and grouping by the row ID. The projection row number follows the
selected result order: a filtered or descending warning refers to its output
position rather than the physical table position. More deeply nested,
other view shapes or user-defined JSON origins, more complex ordering and grouping
shapes, and floating overflow in other expression forms remain outside this
audit.
