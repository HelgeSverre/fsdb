# CONVERT binary literals using a charset

The pinned MySQL 8.4.11 oracle interprets a binary literal's bytes in the
requested character set. `CONVERT(0xF09F9880 USING utf8mb4)` is one emoji
whose HEX value remains `F09F9880`; `CONVERT(0xE9 USING latin1)` is one
character with byte `E9`; `CONVERT(0xF09F9880 USING binary)` retains four
bytes. The same values are checked through text and binary preparation.

Fsdb previously passed binary literals through `toText`, which decoded each
byte as Latin-1 before transcoding it. The first example consequently became
four characters and produced `C3B0C29FC298C280` when hex-encoded. The
`CONVERT` scalar now decodes raw binary-literal and binary-value bytes with
the target codec, or retains them unchanged for `binary`. Text input keeps
its existing transcode path. A focused executor regression and the
`introduced-literal-encoding` differential contract cover valid byte cases.

An additional MySQL matrix distinguishes invalid byte handling. Invalid
UTF-8MB3/4, UTF-16, and UTF-32 sequences return NULL with warning 1300;
the message quotes at most three bytes starting at the invalid sequence.
Invalid ASCII bytes remain available with their original byte values and
warning 1300. UCS-2 retains an isolated surrogate code unit and emits only
its deprecation warning. Fsdb now follows these audited outcomes; focused
regressions pin invalid UTF-8, ASCII, and UCS-2 behavior, while the
differential contract checks the broader value-and-warning matrix. Other
legacy codecs and invalid-input contexts remain open.
