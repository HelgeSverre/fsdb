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

## Storage conversion of invalid source bytes

The `encoded-storage-oracle.py` matrix checks ASCII `418042` and UTF8MB3
`a😀b` inserted into same-charset and UTF8MB4 VARCHAR columns. Non-strict
same-charset insertion retains the valid prefix (`41` or `61`); conversion
to UTF8MB4 substitutes each invalid source byte (`413F42` or `613F3F3F3F62`).
Every conversion emits warning 1366 after the literal diagnostics. Strict
mode rejects all four shapes with 1366 and leaves the table empty.

Storage now validates encoded source bytes before decoding, sharing the
Unicode scalar scan with literal validation and the byte-preview formatter
with derived-row conversion. Ordinary decoded text retains its existing
target-charset conversion checks. This does not establish complete parity
for every source/target charset or for malformed UCS2 surrogate output.

The maintained wire contract compares rows, warnings, strict errors, and
post-error table state. Run `20261008T050419021-25285/contracts` passed
62 cases and 7,502 steps without differences. The root gate passed 3,068
tests with no build warnings or errors under the usual 4 GiB heap cap.

### UCS2 code-unit storage semantics

Native MySQL accepts `0041D8000042` in UCS2 columns in both strict and
non-strict modes, retaining all six bytes. Conversion to UTF8MB4 retains the
surrogate encoding as `41EDA08042`, without a conversion warning. ASCII
conversion produces `413F42` and warning 1366 in non-strict mode; strict mode
rejects it, with the diagnostic preview based on the original UCS2 bytes.

UCS2 surrogate pairs are two independent code units. `D83DDE00` converts to
UTF8MB4 `EDA0BDEDB880`, not the four-byte scalar encoding. VARCHAR limits
apply during conversion by source code unit: VARCHAR(1) retains the first
surrogate, and VARCHAR(2) retains both. `CHAR_LENGTH` then reports two for
the UCS2 value but six for the invalid UTF8MB4 byte sequence. Likewise,
VARCHAR(2) converts `0041D8000042` to `41EDA080`, whose UTF8MB4 character
length is four. The maintained storage oracle contains the size-one-through-
three controls for both source strings and both targets.

Text-column coercion now preserves encoded UCS2 values through length
enforcement and conversion, and character counting uses UCS2 code units.
ASCII conversion replaces each surrogate independently: `0041D83DDE0000420043`
becomes `413F3F4243`. Its warning previews the original six-byte suffix,
`\xD8\x3D\xDE\x00\x00\x42...`; strict mode rejects the same input.

Focused Expecto regressions cover preservation, character counting, and
strict/non-strict ASCII conversion. The native wire contracts cover these
cases and the length matrix: 62 cases and 7,623 steps pass without differences.
This closes the audited UCS2 storage boundary; other charset conversions and
encoded-value expression operations still need separate native evidence.

### Encoded string operations

The maintained `encoded-expression-oracle.py` probes REVERSE, LEFT, RIGHT,
SUBSTRING, and same-charset CONCAT over UCS2 surrogate pairs, invalid ASCII,
and supplementary UTF8MB3 input. All fifteen native results differ from fsdb
at `82890893`, although warning order and text match.

MySQL reverses UCS2 code units independently: `0041D83DDE000042` becomes
`0042DE00D83D0041`. LEFT with length two retains `0041D83D`, and SUBSTRING
at position two with length one retains `D83D`. Invalid ASCII bytes survive
these operations unchanged. Each invalid UTF8MB3 byte counts separately:
LEFT of `61F09F988062` with length two yields `61F0`, while REVERSE yields
`6280989FF061`. Same-charset CONCAT retains every original byte.

Reversal and slicing now share encoded character offsets and retain source
bytes. CONCAT uses the resolved result charset for the audited same-charset,
binary, and UCS2-to-UTF8 paths. Binary values retain byte-oriented behavior.

The expanded CONCAT matrix also covers binary inputs, same-charset slices,
and cross-charset inputs. UCS2-to-UTF8MB4 conversion retains independent
surrogate units and emits warning 1300 for the converted invalid UTF8 bytes.
Invalid ASCII and supplementary UTF8MB3 inputs concatenated with ordinary
UTF8MB4 text fail with 1267/HY000, including the operand collations in the
message. The maintained oracle pins all 24 cases, now included in the wire
contracts. The full wire run passed 62 cases and 7,693 steps without differences;
the root gate passed 3,076 tests with no build warnings or errors.

These checks close the audited expression cases. Other legacy multibyte
charsets, wider mixed-charset combinations, and additional string functions
still need native probes before claiming general encoded-expression parity.

### Shift-JIS structural character boundaries

MySQL accepts Shift-JIS byte pairs even when the platform codec cannot map
them to a Unicode character. For `_sjis X'4182A08242'`, REVERSE returns
`824282A041`, LEFT with length two returns `4182A0`, RIGHT with length two
returns `82A08242`, and SUBSTRING at position two returns `82A0`. Decoding
or treating the unmapped pair as independent bytes loses these boundaries.

The encoded-character helper now recognizes SJIS/CP932 lead and trail byte
ranges directly. A native matrix spans lead/trail range endpoints and the
excluded 7F trail byte for both charsets. All 58 accepted cases match reversal
and character length; they also pass in the native wire suite. The full run
passed 62 cases and 7,751 steps without differences, and the root gate passed
3,077 tests without build warnings or errors.

`legacy-expression-oracle.py` also pins rejected inputs. Native MySQL rejects
malformed Big5 `41A440FF42` and GBK `41D6D0FF42` introduced hex literals with
1300/HY000; fsdb currently accepts both. Strict structural validation of
legacy introduced literals remains open. The SJIS/CP932 matrix includes
82 native rejection cases for that follow-up, alongside the accepted cases.

### Shift-JIS literal validation

SJIS and CP932 introduced hex/bit literals now use the same structural
character-width rule as encoded string operations. ASCII and half-width kana
are single bytes; valid lead/trail pairs remain accepted even without a
platform Unicode mapping. Malformed sequences raise 1300/HY000 at the first
invalid byte, with the native three-byte diagnostic preview.

The shared invalid-byte scanner also serves Unicode validation. All 140
native SJIS/CP932 boundary cases match, including 82 rejections. The full wire
run passed 62 cases and 7,833 steps without differences; the root gate passed
3,078 tests without build warnings or errors. Big5 and GBK malformed-literal
validation remains open.

### Big5 and GBK structural validation

Big5 and GBK introduced hex/bit literals now reject malformed byte sequences
with 1300/HY000 and the native diagnostic preview. Character boundaries use
the same byte rules for reversal and slicing. Big5 accepts leads A1–F9 and
trails 40–7E or A1–FE; GBK accepts leads 81–FE and trails 40–FE except 7F.
ASCII remains single-byte in both families. These checks do not depend on
whether the platform codec has a Unicode mapping for an accepted pair.

The native boundary matrix covers 144 accepted and rejected inputs, all
matching fsdb rows or exact errors. The maintained legacy oracle and full
wire suite pass; the latter covers 62 cases and 7,977 steps without differences.
The root gate passed 3,079 tests without build warnings or errors. This closes
the audited Big5/GBK binary-introducer gap. Quoted-string validation, other
legacy charset families, conversion diagnostics, and charset mappings remain
separate compatibility boundaries.

### EUC and GB2312 boundaries

`euc-expression-oracle.py` pins a native matrix for UJIS, EUC-KR, and GB2312.
At `de26bab6`, 229 of its 274 probes differ in acceptance, reversal bytes,
character count, or the exact rejection message.

The native endpoints distinguish these families. EUC-KR accepts two-byte
sequences with both bytes in 81–FE. GB2312 accepts leads A1–F7 and trails
A1–FE. UJIS accepts ordinary A1–FE pairs and three-byte sequences beginning
8F followed by two A1–FE bytes. Its 8E prefix has an important distinction:
`8E A0` is accepted in an introduced hex literal but reversed as `A0 8E`
and counted as two characters, whereas `8E A1` and `8E DF` are single
characters. Literal acceptance therefore needs a separate rule from character
boundaries for that UJIS edge; the implementation must not erase the native
distinction by forcing both operations through an identical predicate.

The audited EUC/GB2312 boundary cases now match. Binary literal validation
uses the pinned structural rules, and reversal, slicing, and encoded
character counting share character offsets. The UJIS `8E A0` acceptance
exception remains confined to literal validation. Encoded values remain
encoded when a lossless platform byte round-trip would change their native
character count, as with EUC-KR `81 81`.

All 274 native probes match rows and exact errors. The full wire suite passed
62 cases and 8,251 steps without differences; the root gate passed 3,080 tests
without build warnings or errors. Broader conversion, output mapping, and
mixed-sequence behavior remain outside this audited boundary matrix.

### GB18030 validation and supplementary slicing remain open

Six additional mixed UJIS sequences combine `8E A0`, three-byte characters,
and ordinary pairs in both orders. All match native reversal, character
count, LEFT, and SUBSTRING at `4b613a00`; they are retained in the EUC oracle.

The GB18030 oracle exposes ten mismatches across sixteen cases at that
revision. Malformed two/four-byte sequences must fail with 1300/HY000.
Native MySQL accepts `FE39FE39` structurally even though the platform codec
cannot map it; reversal and slicing must retain it as one character.
`90308130` decodes to a supplementary Unicode scalar: reversal and character
count already match, but LEFT and SUBSTRING cut the UTF-16 surrogate pair,
producing `3F` instead of its four original bytes. This latter issue belongs
to decoded text slicing as well as encoded GB18030 handling.
