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

String-to-integer argument warnings, `CHAR(... USING charset)`, and the
remaining large or unusual numeric source domains are outside this audit.
