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
retain accepted source bytes without applying hex-literal rejection rules.

The native oracle passes 19 rejection cases and 24 valid controls, directly and
through SQL PREPARE/EXECUTE. The wire contract also covers binary preparation.
All 57 recorded rejection messages match exactly. The full gate passes 3,059
tests with no build warnings or errors under eight logical processors and a
4 GiB heap cap. Native MySQL uses a disposable server with a 64 MiB buffer pool
and redo capacity. Differential run `20261008T041050449-17081/contracts` passes
62 cases and 7,299 steps without differences.

## Remaining encoding boundaries

These rules do not establish complete character-set parity. General text-function and storage conversion, UCS-2 surrogate output encoding,
and client encodings beyond the current UTF-8 SQL-input assumption need
further work. The accepted-literal cases below are now covered. Native probes accept `_ascii X'80'` and `_ucs2 X'D800'`;
applying .NET strict decoding indiscriminately would reject native syntax.


### Accepted bytes, warnings, and materialization

`torture/scripts/introduced-byte-preservation-oracle.py` records a separate
native matrix for accepted byte sequences. Direct HEX/LENGTH calls retain
`80`/1 for `_ascii X'80'`, `D800`/2 for `_ucs2 X'D800'`, `F09F9880`/4 for
quoted utf8mb3 and national supplementary characters, and `C3A9`/2 for the
ASCII introducer on the UTF-8 client spelling of é. The initial fsdb probe
returned `3F`, `FFFD`, `3F`, and `3F3F`, respectively. Focused Expecto regressions now cover the corrected bytes.

Native warnings are part of the contract. Each ASCII occurrence produces 1300;
quoted utf8mb3 occurrences produce deprecation warning 1287 followed by 1300.
National literals use deprecation warning 3720 before 1300. UCS-2 occurrences
produce deprecation warning 1287 but do not reject an unpaired surrogate.
The paired HEX/LENGTH fixtures repeat the literal and therefore repeat warnings.
Fsdb now reports these warnings at submission or preparation, including cached
statements; prepared execution does not repeat them.

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

### Derived-row conversion of accepted bytes

A further native MySQL 8.4.11 probe selects `HEX(v), LENGTH(v)` from a
derived literal source. ASCII `80` and `C3A9` become empty strings; ASCII
`418042` retains only `41`. UTF8MB3 supplementary text likewise retains
only the valid prefix (`a😀b` becomes `61`). These conversions emit warning
1366 after the literal warnings. UCS2 `D800` retains both bytes and emits
only its charset deprecation warning. The maintained byte-preservation
oracle records the exact rows and warning messages. A blanket decode and
re-encode or truncation of every encoded value would therefore be incorrect.

### Quoted and variable SQL preparation sources

Native MySQL 8.4.11 distinguishes quoted `PREPARE ... FROM` text from a
user-variable source. With `SET NAMES utf8mb4`, quoted SQL containing a
supplementary character prepares `?` instead, including when the nested
literal is ordinary, introduced UTF8MB3, or national text. A user variable
retains the original UTF-8 bytes, and preparation from that variable retains
them in all three forms. With `SET NAMES utf8mb3`, both source forms retain
the bytes. Invalid-string warnings occur at preparation when the bytes
survive, and do not repeat on execution. The maintained oracle pins all
twelve combinations and the variable source bytes. This rules out a blanket
conversion of introduced literals during preparation.

## Accepted-literal implementation

Encoded text retains its charset and original bytes when decoding and encoding
would lose information. Byte-oriented functions consume those bytes directly.
User variables preserve them, while derived rows truncate invalid ASCII and
Unicode sequences at the first invalid character and emit warning 1366. UCS2
code units retain their distinct acceptance rules. Both value codecs preserve
the encoded representation.

Literal syntax retains named versus national introducers for deprecation
warnings. This diagnostic spelling is not persisted. Binary preparation starts
a new diagnostics area and includes its warning count in the protocol header.
Quoted SQL preparation sources convert separately from variable sources.
Rendering uses quoted syntax where Unicode hex syntax would reject the bytes.

The accepted-byte matrix has 30 native fixtures, including source lifetimes
and derived conversions; all match fsdb rows and warning messages. These cases
also run in the maintained wire contract. The full root gate passes 3,067 tests
with no build warnings or errors.
