# Introduced binary-literal encoding

Native MySQL 8.4.11 rejects malformed UTF-8, UTF-16, and UTF-32 hex or bit
literals with 1300 / HY000 before evaluating the query. The same errors occur
in SQL PREPARE and binary preparation, in unused IF branches, and with a
false WHERE predicate. Invalid encoding also precedes an incompatible COLLATE
annotation. `torture/scripts/introduced-encoding-oracle.py` retains the native
rejection cases and valid controls.

The error quotes up to three bytes beginning at the first invalid scalar,
rendered as uppercase hexadecimal. `_utf8mb3 X'F09F9880'` reports `F09F98`;
`_utf8mb4 X'41FF42434445'` reports `FF4243`. UTF-16 reports the high surrogate's
starting byte when its following code unit is not a low surrogate. Validation
therefore scans code units rather than relying on .NET decoder exception
positions. Supplementary scalars remain valid in utf8mb4, UTF-16, and UTF-32,
but not utf8mb3; the utf8 alias follows utf8mb3.

Before decoding, fixed-width introducers left-pad hex/bit bytes with zeroes to
a complete code unit: two bytes for UCS-2 and either UTF-16 byte order, four for
UTF-32. This is left-padding even for utf16le. `_utf16 X'41'` has bytes `0041`,
and `_utf32 X'0041FF'` has bytes `000041FF`. Validation and error fragments use
the padded bytes. Empty literals remain empty. Valid-literal HEX controls pin
supplementary characters, byte order, padding, and latin1/binary preservation.

## Implementation and validation

Charset owns byte padding and the first-invalid-scalar scan. The parser applies
these rules to introduced hex/bit values before creating their AST literal,
and preserves semantic error 1300 through parser backtracking. Quoted strings
remain on their existing conversion path.

The native oracle passes 19 rejection cases and 24 valid controls, directly and
through SQL PREPARE/EXECUTE. The wire contract also covers binary preparation.
All 57 recorded rejection messages match exactly. The full gate passes 3,059
tests with no build warnings or errors under eight logical processors and a
4 GiB heap cap. Native MySQL uses a disposable server with a 64 MiB buffer pool
and redo capacity. Differential run `20261008T041050449-17081/contracts` passes
62 cases and 7,299 steps without differences.

## Remaining encoding boundaries

These rules do not establish complete character-set parity. Quoted-string
conversion and warnings, non-Unicode byte preservation, UCS-2 surrogate
handling, and client encodings beyond the current UTF-8 SQL-input assumption
need further work. Native probes accept `_ascii X'80'` and `_ucs2 X'D800'`;
applying .NET strict decoding indiscriminately would reject native syntax.
