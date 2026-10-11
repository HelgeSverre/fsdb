# REGEXP malformed-pattern diagnostics

The pinned MySQL 8.4.11 server was probed with `REGEXP_LIKE('a', pattern,
'c')`. Patterns were sent as UTF-8 hex strings through `CONVERT(... USING
utf8mb4)` so the SQL client could not consume a backslash before the regex
engine saw it.

| Pattern | MySQL error | Message |
|---|---:|---|
| `*a`, `+a`, `?a` | 3688 | Syntax error in regular expression on line 1, character 1. |
| `a|*b`, `😀|*b` | 3688 | Syntax error in regular expression on line 1, character 3. |
| `a{,2}` | 3692 | Incorrect description of a {min,max} interval. |
| `a{99999999999}` | 4007 | Decimal number in regular expression is too large. |
| `(?<99999999999>a)` | 3688 | Syntax error in regular expression on line 1, character 4. |
| `\99999999999` | 3694 | Invalid back-reference in regular expression. |
| `\k` | 3887 | A capture group has an invalid name. |
| `\x` | 3689 | Unrecognized escape sequence in regular expression. |
| `\p{}`, `\p{NoSuch}` | 3685 | Illegal argument to a regular expression. |

The .NET parser uses one out-of-range error for the interval, capture name,
and back-reference rows, so Fsdb distinguishes their pattern shapes before
mapping the MySQL diagnostic. Fsdb previously returned generic 3691 for most
of these and treated `a{,2}` as a literal. The shared regex compiler now
maps the corresponding .NET parse errors and rejects the missing interval
minimum. Quoted
`\Q{,2}\E` remains a valid literal, as MySQL confirmed. Root regressions
and the `regexp-posix-classes` differential contract cover the cases.

The same probe found that MySQL accepts possessive `a++`, while .NET rejects
it as nested quantifiers. The audited possessive forms are now covered
separately in [REGEXP possessive quantifiers](2026-10-11-regexp-possessive-quantifiers.md).
Other unprobed error-code distinctions remain open.
