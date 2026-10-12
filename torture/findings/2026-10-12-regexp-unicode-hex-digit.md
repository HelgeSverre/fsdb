# Unicode hex-digit regular-expression property

MySQL 8.4.11 accepts `\p{Hex_Digit}`, `\p{Hex}`, and `\p{XDigit}` as
aliases. Unlike `ASCII_Hex_Digit`, they also match fullwidth `０`–`９`,
`Ａ`–`Ｆ`, and `ａ`–`ｆ`. Audited nonmembers include `é`, Arabic-Indic
`١`, circled `①`, and mathematical `𝟡`.

Fsdb previously passed these ICU property names to .NET, which returned
error 3685. The bounded regex normalizer now maps the aliases to the
audited Unicode hex ranges. The same mapping works in bracket expressions;
negated forms consume a supplementary character as one scalar. The focused
regression failed with 3685 before the change and passes after it. The
`regexp-posix-classes` differential contract compares the values with the
pinned MySQL 8.4.11 server. The run at
`20261012T041746326-5234/contracts` passed both new steps and retained the
same eleven previously known difference signatures across 139 cases and
18,874 steps. `just check` passed 3,475 tests with no build warnings or
errors. Other ICU binary and script properties remain open.
