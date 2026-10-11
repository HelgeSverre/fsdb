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

Byte-string high-byte diagnostics and the remaining unusual numeric source
domains are outside this audit.
