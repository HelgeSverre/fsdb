# ASCII hex digit regular-expression property

MySQL 8.4.11 accepts both `\p{ASCII_Hex_Digit}` and its `\p{AHex}` alias.
They match ASCII `0`–`9`, `A`–`F`, and `a`–`f`, but not `é` or a fullwidth
Latin letter. Negated `\P{...}` matches those nonmembers and consumes a
supplementary emoji as one Unicode scalar. Positive and negative forms also
work inside bracket expressions.

Fsdb previously passed the unknown ICU property spelling to .NET, which
raised MySQL error 3685. It now maps the two audited spellings to the ASCII
range. Regex normalization keeps separate atom and bracket-member spellings,
so the same property works correctly inside and outside brackets. A focused
regression failed before the change; the native differential contract checks
values and supplementary-scalar consumption. Other ICU binary and script
properties remain open.
