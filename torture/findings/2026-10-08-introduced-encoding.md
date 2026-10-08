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


### Accepted bytes, warnings, and materialization

`torture/scripts/introduced-byte-preservation-oracle.py` records a separate
native matrix for accepted byte sequences. Direct HEX/LENGTH calls retain
`80`/1 for `_ascii X'80'`, `D800`/2 for `_ucs2 X'D800'`, `F09F9880`/4 for
quoted utf8mb3 and national supplementary characters, and `C3A9`/2 for the
ASCII introducer on the UTF-8 client spelling of é. The current fsdb probe
instead returns `3F`, `FFFD`, `3F`, and `3F3F`, respectively. A focused Expecto
regression reproduces the first wrong-byte result.

Native warnings are part of the contract. Each ASCII occurrence produces 1300;
quoted utf8mb3 occurrences produce deprecation warning 1287 followed by 1300.
National literals use deprecation warning 3720 before 1300. UCS-2 occurrences
produce deprecation warning 1287 but do not reject an unpaired surrogate.
The paired HEX/LENGTH fixtures repeat the literal and therefore repeat warnings.
Fsdb currently omits these warnings.

The SQL PREPARE fixtures use a string-literal SQL source. ASCII and UCS-2 bytes
survive preparation and repeated execution; warnings appear at preparation,
not repeatedly at EXECUTE. In this fixture, quoted utf8mb3/national
supplementary characters instead produce `3F` when executed. These observations
must not be generalized to binary preparation without its own evidence.

Assignment to an ASCII user variable retains `80` and emits 1300 on SET. A
derived table containing the same ASCII literal instead returns an empty
string and adds warning 1366 for its materialized column, alongside 1300.
Consequently, retaining raw source bytes and applying materialization/output
conversion are separate requirements. The successful strict Unicode rejection
work above does not close this byte-preservation boundary. The maintained
native matrix passes on disposable MySQL 8.4.11 with the same 64 MiB limits.
