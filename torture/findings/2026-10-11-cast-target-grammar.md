# CAST and typed CONVERT targets are narrower than column declarations

MySQL 8.4.11 accepts `CHAR`/`CHARACTER`, `BINARY`, signed and unsigned
integers, `DECIMAL`, `FLOAT`, `DOUBLE`, `REAL`, `DATE`, `TIME`, `DATETIME`,
`YEAR`, and `JSON` as audited conversion targets. Its column declarations
have a different grammar. In particular, `VARCHAR`, `VARBINARY`, `TEXT`,
`BLOB`, `INT`, `BIGINT`, `BIT`, `TIMESTAMP`, and `NUMERIC` are not CAST target
spellings. A numeric `UNSIGNED` suffix, `YEAR(4)`, and the column-style
`FLOAT(M,D)` spelling also fail with syntax error 1064, SQLSTATE 42000.
The comma form of `CONVERT` uses the same target grammar.

Fsdb previously reused its column-type parser after `AS` and after the
comma in typed `CONVERT`. That admitted column-only types. An invalid typed
`CONVERT` could then backtrack into a function call and report an unrelated
missing-database or missing-function error. The cast parser now names the
accepted target forms; the comma commits `CONVERT` to that grammar.

MySQL also accepts `CHARACTER(2)` and truncates `abc` to `ab` with a warning.
Fsdb now retains bare `CHAR`/`CHARACTER` widths in the character-cast node
and evaluates them using the current connection charset. The focused test
first failed on the accepted `VARCHAR` spelling, and the differential
contract covers the audited rejections and character-width value and warning.

Target precision is a separate diagnostic boundary. MySQL rejects `FLOAT(54)`
with error 1426 naming a maximum of 53; `TIME(7)` and `DATETIME(7)` use the
same code with a maximum of 6. The message names `CAST` even in typed
`CONVERT`, and the error is raised before an unchosen `IF` branch can skip
the expression. Fsdb now checks these target precisions during parsing and
retains the exact audited messages.

Decimal CAST targets follow a different validation order. MySQL first rejects
precision above 65 with 1426, then a scale larger than the declared precision
with 1427, then scale above 30 with 1425. Precision and scale errors name the
source expression as written; the 1427 message uses an empty column name.
Column declarations have a different order: `DECIMAL(10,10000)` on a column
reports scale error 1425 for that column.
The same order applies to typed `CONVERT` and to an unchosen expression branch.
`DECIMAL(0)` is accepted and takes the default precision of 10. Fsdb now
captures the operand's source text during parsing for these diagnostics and
normalizes zero precision after validation. The focused regression checks
exact codes and messages. The differential contract also exposed that Fsdb's
1425 and 1427 errors carried generic SQLSTATE `HY000`; both now use MySQL's
`42000`. Other conversion shapes remain open.

Negative target precision is syntax, not a valid small precision. MySQL 8.4.11
rejects `FLOAT(-1)`, `TIME(-1)`, `DATETIME(-1)`, `DECIMAL(-1)`, and
`DECIMAL(5,-1)` with error 1064 and SQLSTATE 42000 in both CAST and typed
CONVERT. Fsdb previously accepted negative FLOAT precision, treating it as
single-precision FLOAT; the shared CAST precision check now rejects it and
the temporal cases. DECIMAL already rejected negative precision and scale.
The focused regression and differential contract cover both conversion forms.

DECIMAL CAST values use their declared precision as a result bound. MySQL
8.4.11 rounds `CAST('1.239' AS DECIMAL(5,2))` to `1.24` without a condition,
but clamps `CAST(999.995 AS DECIMAL(5,2))` to `999.99` with warning 1264.
Negative overflow clamps symmetrically. Invalid or partially consumed text
returns the parsed value (zero for `abc`) with warning 1292 naming the
original text. Overflow warnings name the projection's source expression or
its alias. Fsdb previously suppressed the conversion warning and let storage
coercion return a DECIMAL wider than the CAST target. The focused regression
and differential contract now cover these audited values and diagnostics.
Finite DECIMAL text that exceeds .NET's `decimal` range but remains within the
audited MySQL conversion range, such as `1e29`, now clamps to a smaller target
with warning 1264; partial text also retains its 1292 warning. MySQL's
81-digit expansion boundary is now covered for bounded targets: `1e80` has
only the range warning, while `1e81` and `1e309` first warn about the mantissa
and original text, then about the target range. The mantissa warning removes
leading zeroes and a leading plus sign. A zero mantissa remains zero
without warnings. General DECIMAL values beyond .NET's `decimal` precision,
including wide targets, remain open.

DOUBLE and FLOAT CAST text conversion now follows the same audited numeric-prefix
warning rule. MySQL returns zero with warning 1292 for `abc` and `nan`, and the
parsed prefix with that warning for `12x`. For a string beyond DOUBLE's range,
such as `1e309`, it returns signed `Double.MaxValue` with warning 1292. A
value such as `1e309x` is both truncated and overflowing, but still produces
only one warning 1292. A single-precision `FLOAT` overflow such as `1e39`
instead raises error 1690,
SQLSTATE 22003. Fsdb previously omitted the text warnings and did not apply
these distinct overflow outcomes. Focused tests and differential contract
steps cover the values and diagnostics. Other source types and numeric
conversion contexts remain open.

Numeric text prefixes use ASCII digits. MySQL reads `1٢3` as `1` for DOUBLE
and DECIMAL casts with warning 1292, and a fullwidth leading digit produces
zero with that warning. Fsdb's shared floating-prefix regex previously used
Unicode `\d`, consumed the entire text, and returned zero without the
proper prefix warning. The audited cases now share the integer cast's ASCII
digit rule.

Binary string sources now use the numeric text prefix for DOUBLE and DECIMAL
CASTs. An introduced `_binary'12x'` value returns 12 and retains MySQL's
truncation warning for both targets. A value produced by `CAST('12x' AS
BINARY)` also returns 12, but MySQL suppresses the DECIMAL truncation warning;
an entirely invalid binary result instead emits error-class warning 1366.
The bounded range and high-exponent paths also accept audited binary text.
Bare hex/bit literals retain their separate numeric interpretation.
Stored VARBINARY and BLOB columns both parse numeric prefixes, but BLOB
suppresses DOUBLE truncation warnings. Invalid DECIMAL text in either column
produces warning 1366; VARBINARY also retains warning 1292 for the original
text. These column distinctions are covered by focused and differential tests.
Fixed-width BINARY padding and high bytes in numeric CAST diagnostics now use
MySQL's uppercase `\xHH` escapes for bytes outside printable ASCII, while
the numeric prefix still parses the underlying bytes.
Audited computed binary results from `CONCAT`, `CONCAT_WS`, `SUBSTRING`, and
`REPLACE` follow the same quiet DECIMAL truncation policy as an explicit
binary CAST, including warning 1366 for an entirely invalid value. The
classification uses the binary value's origin rather than a function-name
exception; other expression producers remain to be audited.
Binary values returned by `IF`, `CASE`, `NULLIF`, and a source-free scalar
subquery use regular DECIMAL truncation warning 1292, including wholly invalid text.
`COALESCE`, `IFNULL`, and audited byte-string functions instead keep quiet
truncation and report 1366 for wholly invalid text. The numeric CAST source
classification now distinguishes these conditional and scalar-subquery
shapes; further expression families remain open.
When a binary literal or expression crosses a derived-table or CTE column,
MySQL treats it like a binary column for invalid DECIMAL text: 1366 precedes
1292. A valid numeric prefix still warns 1292. A projected stored BLOB stays
quiet, including when a byte-string function uses it. Fsdb now uses the
retained expression coercibility to distinguish synthetic binary columns
from the stored-column case.
A scalar subquery with a `FROM` source instead suppresses DOUBLE truncation
warnings and uses quiet DECIMAL conversion: no warning for a valid numeric
prefix, or 1366 alone for wholly invalid text. A source-free scalar subquery
keeps the regular 1292 rule even with a constant `WHERE` or `LIMIT`.
Text-string numeric CAST warning previews stop at the first NUL byte, as in
MySQL, while binary-source warnings keep the escaped `\x00` representation.
The result still uses the numeric prefix preceding the NUL.
Long ASCII warning arguments are capped at 128 rendered bytes: a `12x` prefix
followed by 300 `a` characters retains only 125 `a` characters in MySQL's
DOUBLE warning. With a UTF-8 client, the cap also counts encoded bytes:
`12x` plus repeated `é` ends with one `?` for the cut byte, while repeated
`€` ends with two. Supplementary characters become `?` in this diagnostic
before clipping. Fsdb now matches these audited text-source boundaries;
other source charsets and client-charset conversions remain open.
