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

Invalid byte sequences are a separate boundary. MySQL returns NULL with a
1300 warning for invalid UTF-8 input, but can preserve invalid ASCII bytes
with a warning. Those cases need their own conversion and warning audit.
