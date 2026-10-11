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
differential contract checks the broader value-and-warning matrix.

The next oracle matrix covered an incomplete lead byte in each supported
multibyte legacy family: SJIS, CP932, Big5, GBK, GB2312, GB18030, EUC-KR,
and UJIS. MySQL returned NULL and one warning 1300 naming the charset and
the invalid lead byte. Fsdb now reuses its existing binary-literal byte
validator at conversion time and matches those value-and-warning outcomes.
The single-byte oracle sweep covered every high byte (`80`–`FF`) in the 25
supported single-byte charsets. MySQL preserves these raw bytes even when a
charset marks them invalid. The warning sets vary by charset: CP1251 warns for
`98`, while TIS-620 warns for none; ASCII and SWE7 warn for every high byte.
Fsdb now preserves the raw bytes and emits warning 1300 for the audited holes.
The same sweep showed that `dec8`, `hp8`, `macce`, and `macroman` also emit a
preceding deprecation warning 1287, which the conversion path now preserves.

A follow-up used the result as the source of another conversion. MySQL changes
invalid CP1251 byte `98` to `?` when transcoding to UTF-8, but retains `98`
when converting to binary or back to CP1251. TIS-620 control byte `80`
becomes UTF-8 `C280` and Latin1 `E9` stays `E9` in binary. Fsdb now
distinguishes text transcodes from binary byte preservation for these encoded
sources.

Valid encoded source values also retain their bytes. MySQL keeps SJIS `82A0`,
GBK `C4E3`, Big5 `A440`, UTF-16 `0041`, and UTF-32 `00000041` when a second
`CONVERT ... USING binary` is applied, while a second UTF-8 conversion emits
their transcoded bytes. Fsdb now retains the validated source encoding instead
of immediately discarding it into a plain Unicode string. Other conversion
contexts remain open.
